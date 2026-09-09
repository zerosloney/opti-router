# OptiRouter 架构审查与首批工程改进

## 1. 范围、结论与验证边界

审查基线：`ae69560`。本次检查了应用组合根、代理/路由/客户端、配置与状态存储、管理 UI、后台任务、MCP、合规和测试/CI。使用 Linux、.NET SDK 8.0.425，未启动生产服务、读取生产数据库或调用真实模型上游。HTTP 测试使用隔离配置、测试客户端或本地 WireMock。

**结论：应保留单体部署，先建立内部边界与跨执行模式的不变量，不宜先拆微服务。** 当前功能模块划分清楚，也有大量回归测试；风险主要集中在多条成功/失败路径重复实现安全约束、计费与生命周期收尾，以及内存状态和持久化状态缺少统一提交边界。

本次交付分开两类变更：

- **行为保持型重构**：将入口鉴权、路径分类、身份解析移出 `Program`；消除登录防爆破与代理限流的重复 IP 解析；减少 XFF 解析分配。
- **明确改变错误行为的安全修复**：Cascade 的评审模型与升级模型不得重新引入初始路由已经硬排除的模型。不能把这一修复称为“所有行为未改变”。

以下问题除明确标为“测试复现”外，均为实现与调用方交叉验证的静态发现；不代表已确认生产事故。没有做生产流量压测、长时浏览器会话实验或真实 MariaDB/Postgres/Redis 故障注入。

## 2. 架构与主要组件

应用是一个 .NET 8 ASP.NET Core Web 项目，加一个 xUnit 测试项目。模块目前主要通过目录而非程序集隔离。管理面与代理面共用进程、DI 容器和状态服务，因此管理查询、后台任务和代理请求会竞争相同资源。

| 边界 | 主要组件 | 职责与依赖 |
| --- | --- | --- |
| 组合根 | [Program](../src/OptiRouter/Program.cs) | 注册服务/存储/策略与后台任务，加载和迁移配置，排列中间件，映射端点 |
| 入口安全 | [Security](../src/OptiRouter/Security/RequestAuthenticationMiddleware.cs)、`ClientKeyService`、`AdminKeyStore` | 管理 Cookie/Bearer 与租户 key 分离；代理准入、QPS/日预算、IP 分区 |
| 协议端点 | `ChatCompletionsEndpoint`、`AnthropicMessagesEndpoint`、`GeminiGenerateContentEndpoint` | 校验 HTTP/JSON，原生协议与统一 `ChatRequest`/OpenAI 风格响应互转，输出 JSON/SSE |
| 请求执行 | [ProxyOrchestrator](../src/OptiRouter/Endpoints/ProxyOrchestrator.cs)、Race、Fusion、Cascade、StreamingHedge | 缓存、预处理、预算预留、候选尝试、流式生命周期与替代执行模式 |
| 路由决策 | [RouterEngine](../src/OptiRouter/Routing/RouterEngine.cs)、`IRouterPolicy`、`RouterDecision` | 按 Filter → Classify → Order → Constraint 执行策略；区分硬排除与 tier/pin 等软偏好 |
| 上游适配 | `ModelClientProvider`、`ModelClientFactory`、三种协议客户端和 Translators | 根据模型配置构造/缓存客户端，处理 HTTP、重试、超时、SSE、usage；热更新后旧客户端延迟释放 |
| 结果与状态 | [OutcomeRecorder](../src/OptiRouter/Endpoints/OutcomeRecorder.cs)、CostLedger、Audit、Health、学习状态 | 全局/会话/租户计费，审计、配额、健康、亲和性、Bandit/Thompson 反馈 |
| 配置与管理面 | `AppConfigDbStore`、`ModelsConfigService`、`DbAppConfigProvider`、Dashboard/Models handlers、Blazor、`ApiService` | 配置入库并 Reload；管理页面通过服务端 HTTP 调回管理 API，而非直接调用领域服务 |
| 后台与可选功能 | Health、Metrics、Mesh、MCP、Compliance、Compression | 探活/清理/指标、Redis 状态广播、工具调用、内容审核、PII 与压缩 |

### 存储不是统一的一套后端

- 配置库：SQLite 或 MariaDB。`appsettings` 承担部署设置和首次播种，运行时配置由配置库提供，经 `IConfigurationRoot.Reload()` 更新 Options。
- 成本账本：InMemory / SQLite / MariaDB / Postgres / Redis。
- 审计：InMemory / SQLite / MariaDB / Postgres，不应因账本支持 Redis 就推断审计也支持。
- 租户密钥：文件或 MariaDB，进程内另有缓存、QPS 状态、预算预留与待提交增量。
- 响应缓存、健康和部分学习状态在内存；可选持久化和 Mesh 只覆盖各自实现支持的部分。不能据此宣称全部状态跨节点强一致。

## 3. 关键数据流

### 3.1 代理请求

```text
OpenAI /v1、Anthropic /v1/messages、Gemini /v1beta
  → 请求 ID / trace
  → RequestAuthenticationMiddleware
      → RequestIdentity 提取凭据
      → ClientKeyService 原子准入，身份写入 HttpContext.Items
  → 并发闸 + 固定窗口限流
  → 协议校验 / 翻译为 ChatRequest
  → ProxyOrchestrator
      → 安全分区、缓存键、PII/压缩/输入审核、可选缓存命中
      → RouterEngine → RouterDecision（候选 + 硬排除）
      → 预算预留、候选准入
      → 串行 / Race / Fusion / Cascade / StreamingHedge
      → ModelClientProvider → 协议客户端 → 上游
      → 结果处理、计费、审计、健康与学习反馈
  → 回译为调用方协议的 JSON 或 SSE
```

精确/语义缓存使用授权 key 与会话形成安全分区，已有租户隔离测试。下文的多模态缓存问题是**同一安全分区内错答**，不是已证实的跨租户泄露。

流式路径先取得首行，再向下游输出；开始输出后不能像非流式一样任意换模型。首行、正常结束、上游失败、外部取消、消费者提前 Dispose 必须分别定义。当前计费与健康反馈在这些终态上的覆盖不一致。

### 3.2 配置更新与管理台

```text
Blazor circuit → ApiService（转发当前/捕获的 Cookie）→ 管理 HTTP API
  → 校验/版本检查 → AppConfigDbStore 或 ModelsConfigService
  → 持久化 → IConfigurationRoot.Reload()
  → OptionsMonitor / 客户端缓存 / 路由状态消费者更新
```

浏览器另有 session ping 续期 Cookie、断线恢复与 BFCache 刷新。不能删除这些现有保活机制；但它们不能自动刷新服务端 circuit 已捕获的旧 Cookie。

## 4. 优先级问题清单

P1：安全约束、计费/可用性或持久化正确性，应优先处理。P2：可靠性、性能与维护风险，需要明确后续验收。下列行号如未注明，指审查基线。

### P1-1：Cascade 绕过已有硬排除，已修复并测试复现

- **证据**：[CascadeUpgradeHandler](../src/OptiRouter/Endpoints/CascadeUpgradeHandler.cs) 基线 86–111、146–160 从全量启用模型选择 verifier/Strong，没有检查 `RouterDecision.HardExcludedModels`；[DataSovereigntyPolicy](../src/OptiRouter/Routing/DataSovereigntyPolicy.cs#L14-L39) 明确记录这些排除。
- **触发/影响**：数据不出域开启，本地 Cheap 给出低置信答案，公共云 Strong 或配置的云 verifier 仍收到请求。
- **修复**：两个选择点均检查硬排除，保留原 Strong 排序；不符合约束的 verifier 回退自评。Handler 内使用同一次 Options 读取。修复范围是尊重已有决策的硬排除，不等于解决整个请求跨热更新的快照一致性。
- **证据测试**：[CascadeHardConstraintTests](../tests/OptiRouter.Tests/Endpoints/CascadeHardConstraintTests.cs)：修复前 2 失败/2 通过；修复后 4 通过。验证云模型调用次数为零，同时验证未开启约束时原响应、调用次数和成本不变。

### P1-2：替代成功出口绕过输出审核，待修复

- **证据**：[ProxyOrchestrator](../src/OptiRouter/Endpoints/ProxyOrchestrator.cs#L339-L464) 的 Fusion/Race 在 361/392 行提前返回；输出审核在串行分支 443–464 行。508–522 行 Cascade 在 Cheap 已审核后产生新答案并返回；`ProcessResponse` 只做 PII 还原。
- **触发/影响**：同时开启输出 Block 审核与对应执行模式，最终答案可能未经输出审核。
- **方案/验收**：收敛最终响应审核出口，保留每次真实调用计费；用所有执行模式 × 违规/安全输出的测试，断言违规文本不会送到下游。

### P1-3：租户密钥先改缓存后落盘，失败操作仍可能生效，待修复

- **证据**：[ClientKeyService](../src/OptiRouter/Configuration/ClientKeyService.cs#L539-L594) 的 Create/Update/Delete 直接修改缓存列表/对象后才持久化；148–181 行在文件 mtime 未变时复用缓存。
- **触发/影响**：启用禁用密钥时写文件失败，API 报错，但后续鉴权仍可能使用已启用的内存对象。删除/配额修改也可能与磁盘不一致。
- **方案/验收**：独立快照上修改，持久化成功后发布；故障注入覆盖写临时文件、替换、DB 写入失败，断言失败前后鉴权与持久状态一致。

### P1-4：MariaDB 配置版本检查不是跨实例原子 CAS，待修复

- **证据**：[MariaDbAppConfigStore](../src/OptiRouter/Configuration/MariaDbAppConfigStore.cs#L238-L297) 事务内普通 SELECT 检查版本，随后无版本条件 UPSERT；对象级 `_gate` 不能协调不同实例。
- **触发/影响**：两个连接持相同 expectedVersion 并发写，都可通过检查，后写覆盖先写，UI 乐观锁失效。
- **方案/验收**：使用库端稳定版本行的锁或条件更新，连同首次无文档场景设计；真实 MariaDB 双连接屏障测试必须恰好一个写成功。不要只在现有 SELECT 后机械加锁而漏掉空库与写入入口。

### P1-5：MariaDB 密钥缓存刷新绕过故障降级，待修复

- **证据**：[ClientKeyService](../src/OptiRouter/Configuration/ClientKeyService.cs#L148-L166) 过 30 秒先 Flush/Load；异常未捕获。AuthorizeRequest 241 行先进入此路径，而降级 catch 位于后面的 `AuthorizeViaDbNoLock`。
- **触发/影响**：DB 故障跨越缓存 TTL 后，刷新异常先传播，原本的进程内准入降级无法执行。
- **方案/验收**：统一定义刷新失败的安全策略，不应未经决定直接无限使用旧密钥快照；注入时钟与存储故障，验证 TTL 前后、密钥撤销及 DB 恢复行为。

### P1-6：流式外部取消可能污染上游健康且丢弃已有 usage，待修复

- **证据**：[ProxyOrchestrator](../src/OptiRouter/Endpoints/ProxyOrchestrator.cs#L1170-L1278)：MoveNext 的所有异常都置 `streamFaulted`，finally 记录失败/负反馈；结算仅在正常结束处，失败审计写 null usage、0 成本。
- **触发/影响**：已有输出后用户取消，读取抛取消异常，可能触发共享模型熔断；已取得的 usage 不进入该路径的结算。供应商是否收费及收费多少需要独立核对。
- **方案/验收**：显式区分客户端取消、上游错误、超时、正常结束和提前 Dispose；保留可确认 usage，一次性结算并释放 lease。测试断言取消不增加上游失败计数、不重复计费、不丢已知消费。

### P2-1：原生流协议与超时边界不一致，待修复

- **证据**：[AnthropicModelClient](../src/OptiRouter/Clients/AnthropicModelClient.cs#L155-L187) 在未收到终止事件时合成 `[DONE]`；[AnthropicTranslators](../src/OptiRouter/Clients/Protocols/AnthropicTranslators.cs#L239-L293) 未把流内 error 转成异常。另见三客户端的错误状态处理：OpenAI 386–410、Anthropic 242–256、Gemini 240–254。
- **影响**：HTTP 200 中的 error/异常 EOF 可能变成正常结束；非成功响应头后的错误正文读取只有大小限制，已离开模型超时包装。未设置其他全局超时时，上游悬挂正文可长期占用请求。
- **方案/验收**：统一传输生命周期但保留协议事件差异；参数化测试错误事件、缺终止、错误头后正文永不完成，以及取消和释放。

### P2-2：语义缓存忽略最后 user 消息中的图片差异，待修复

- **证据**：[ProxyOrchestrator](../src/OptiRouter/Endpoints/ProxyOrchestrator.cs#L1544-L1605) 允许多模态进语义缓存，构建语义分区时把最后 user 的整个 Content 置空；`ChatMessage.GetText()` 只提取文字。
- **影响**：同分区、同文字、不同图片可得到上一图片的缓存答案。
- **方案/验收**：先保守禁用多模态语义缓存，或只移除用于相似度的文本而保留非文本块参与分区。覆盖不同 image_url/inline image、相同文字的上游调用数和响应。

### P2-3：日消费增量丢失发生日期，待修复

- **证据**：[ClientKeyService](../src/OptiRouter/Configuration/ClientKeyService.cs#L395-L414) 按 key 聚合待提交金额，482–498 行 flush 时重新取当天；[MariaDbClientKeyStore](../src/OptiRouter/Configuration/MariaDbClientKeyStore.cs#L227-L242) 按传入日期累计/重置。
- **影响**：午夜前消费、午夜后 flush，会计入新一天预算，DB 故障重试跨日会扩大影响。
- **方案/验收**：增量携带业务发生日期，设计迟到旧日消费规则；使用可控时钟测试记账→跨日→flush，不要仅测试 flush 后跨日。

### P2-4：服务端管理 API 可能继续使用旧 Cookie，待长时验证与修复

- **证据**：[ApiService](../src/OptiRouter/Components/Services/ApiService.cs#L19-L66) readonly 捕获 Cookie，HttpContext 不可用时一直复用；浏览器 ping 只更新浏览器票据。
- **影响**：旧票据过期后服务端调用可能 401，即使浏览器刚续期仍被送往登录页。本次未做 8 小时真实浏览器实验。
- **方案/验收**：浏览器携带当前 Cookie 调管理 API，或建立每 circuit 的安全更新机制；使用可控票据时钟与浏览器续期测试，不得退回共享 Cookie handler。

### P2-5：并发注册表返回裸 Semaphore，淘汰/替换存在竞态，待修复

- **证据**：[ConcurrencyRegistry](../src/OptiRouter/Concurrency/ConcurrencyRegistry.cs#L46-L108)：返回信号量与调用方 Wait 分离；扫描只观察 CurrentCount。配置替换竞争失败后仍返回此前的 existing 引用，而非重新读取当前 entry。
- **影响**：同一分区可同时持有已脱离注册表的旧 gate 和新 gate，实际并发超过单 gate 上限；静态状态还跨测试 host 共用。未做确定性竞态复现。
- **方案/验收**：实例级 registry 直接返回 acquisition lease，将引用、获取和可淘汰判定放到同一生命周期；屏障测试覆盖获取/淘汰、配置替换、取消与释放交错。

### P2-6：测试通过数不能代表所有外部后端已验证

- [MariaDbStoresIntegrationTests](../tests/OptiRouter.Tests/Routing/MariaDbStoresIntegrationTests.cs#L8-L24) 缺连接串直接 return，9 项计为 Passed 而不是 Skipped。本次没有执行这些真实 DB 断言。
- 多个 WebApplicationFactory 仅替换部分配置；默认 Production 配置、共用文件和静态 registry 增加环境耦合。此次用显式 Development、空 DB 连接及临时 ConfigDbPath 隔离。
- 原基线 `McpToolExecutorTests.ExecuteToolAsync_Timeout_ReturnsFailure` 已失败，单跑也失败；最终仍是同一失败。已确认 ErrorMessage 为空而不是预期的 `timed out`，尚未判定是 TestServer 取消传播差异还是实现错误，不能靠放宽断言或跳过测试变绿。
- 建议 CI 提供专用临时 DB job、显式报告未执行集成用例，收敛公共 TestHost 构建器，并为 MCP 超时建立确定性 transport 与真实本地 HTTP 双层测试。

## 5. 重复代码、结构与性能

### 已处理

- `Program` 从 **1,406 行减至 1,145 行**。这只是移出责任，不代表整体复杂度已经消失。新的三个 Security 文件各自负责身份、路径及准入流程。
- `LoginRateLimiter` 与代理分区共用 IP 解析；保留 unknown/anonymous 的不同回退、Bearer 优先级、原生 key 适用路径、大小写与路径段匹配语义。
- XFF 只取首段，不再 Split 整个链。129 地址构造输入、1,000 次调用的线程分配量测试：**7,288 B/op → 40 B/op**。这是极长代理链的局部测量，不是生产吞吐或端到端延迟提升承诺。

### 后续应拆分而非直接复制的责任

- `ProxyOrchestrator` 1,744 行、`DashboardHandler` 1,537 行、`ApiService` 1,215 行；RouterStudio 2,169 行、Models 页面 1,800 行（基线）。协议传输、响应终态、UI 配置编辑分别已有过大的修改范围。
- SQLite/MariaDB 配置存储重复 `ComputeDocumentsVersion` 和 JSON 契约。可以先共享纯计算/序列化，**不要把不同后端锁与事务语义隐藏在一个泛型仓储中**。
- 三协议客户端重复发送/错误正文/重试/释放；端点又重复 SSE 预读与错误映射。适合共享资源所有权和时间预算，不适合用一个大模板抹平协议差异。
- ApiService 重复 GET/反序列化/错误回退，页面重复轮询与 loading 状态；先固定失败返回 null 还是抛异常，再抽共同逻辑。

### 热路径风险，不冒充压测结论

[ClientKeyService.AuthorizeRequest](../src/OptiRouter/Configuration/ClientKeyService.cs#L232-L278) 在全局锁内逐项解码/常量时间比较所有密钥，再同步调用 MariaDB 准入。一次慢 DB 往返可阻塞本进程其他租户准入；无效 key 也承担 O(key 数) 扫描。入口限流在鉴权之后，不能给这些失败请求提供同一层的限流保护。

应先测不同 key 数、并发数及 DB 延迟下的锁等待、分配量、p95/p99，再选择预解码不可变快照、每租户同步和异步存储。常量时间扫描有现有安全意图，不能只为速度换成提前退出查找而不审查时序侧信道。网络边界的独立失败请求限流也应纳入设计。

## 6. 分阶段重构计划与验收

| 阶段 | 具体工作 | 合入条件 |
| --- | --- | --- |
| 本次 | 入口 Security 拆分、IP 去重、XFF 分配优化；单独修复 Cascade 硬排除 | 新增契约前后对照；安全问题先红后绿；原测试结果逐项对比 |
| 下一批：安全与一致性 | 统一最终输出审核；密钥修改先持久后发布；DB 原子配置版本；明确缓存刷新失败策略 | 所有执行模式不绕过审核；故障注入不发布失败配置；真实 MariaDB 并发写仅一方成功 |
| 请求生命周期 | 提取请求快照、单次 AttemptResult 与结算组件；区分取消/错误/EOF；整合错误正文时间预算 | 终态矩阵、部分 usage、一次结算、探测/预算/concurrency lease 无泄漏；禁止无关调度策略变更 |
| 内部模块化 | 按安全、路由决策、执行、配置存储、管理查询拆注册/端点扩展；UI 配置编辑拆组件 | 组合根只保留配置与组装；应用服务不引用 Blazor；协议实现不自行选择业务策略 |
| 性能与质量门禁 | 专用 DB 测试、明确跳过状态、公共隔离 TestHost；按后端与并发建立性能基线 | 无新增失败；关键分支覆盖率/测试有效性可见；用实际测量设定性能回归阈值 |

每批独立提交，纯重构与语义修复分开。不要同时改策略排序、缓存分区、成本语义和后台调度。部署前先解决现有失败测试及剩余 P1；本次未修改 DB schema、生产配置或部署步骤。

## 7. 测试与行为证据

| 验证 | 结果 |
| --- | --- |
| 原始全量基线 | 1,389 项：1,388 Passed、1 Failed |
| 新增 HTTP 契约在原鉴权实现上 | 29/29 Passed |
| 重构后针对性回归 | 90/90 Passed，覆盖入口、原生协议、登录限流、并发限流和指标鉴权 |
| 新增安全复现，修复前 | 4 项：2 Failed、2 Passed |
| 最终全量 | 1,443 项：1,442 Passed、1 Failed |
| 对比原有 1,389 项的结果 | 无变化，包括同一个 MCP 超时失败 |
| 新增用例 | 54/54 Passed：29 HTTP、21 路径/身份、4 Cascade |
| Release `-warnaserror` 构建 | 0 warning、0 error |
| 直接/传递依赖漏洞扫描 | 本次 NuGet 数据源未报告已知漏洞；不等于不存在漏洞 |

[RequestIdentityTests](../tests/OptiRouter.Tests/Security/RequestIdentityTests.cs) 用冻结的基线算法对照 **840 组** header/trust/remote/Bearer 组合，覆盖空/空白/多值、IPv6、Unicode 空白和回退；另有分配量回归测试。[RequestPathPolicyTests](../tests/OptiRouter.Tests/Security/RequestPathPolicyTests.cs) 固定大小写、路径段与近似前缀边界。[HTTP 契约测试](../tests/OptiRouter.Tests/Endpoints/RequestAuthenticationContractTests.cs) 验证凭据优先级、401/302、错误协议信封、请求 ID 和安全响应头。

这些是针对已覆盖输入的行为等价证据，不是所有并发调度、所有协议输入或真实后端的数学证明。原有后端集成用例未执行的限制见 P2-6。全量测试目前**不是全绿**，不应把已知失败过滤后称为成功。

### 安全复现命令（Bash）

需要 .NET 8 SDK。测试不应继承生产数据库连接；以下使用临时配置库，不读取 launchSettings：

```bash
work=$(mktemp -d)
unset OPTIROUTER_MARIADB_TEST
export DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development
export OptiRouter__ConfigDbConnectionString=''
export OptiRouter__EnableSingleInstanceGuard=false
export OptiRouter__ConfigDbPath="$work/config.db"

dotnet restore OptiRouter.sln
dotnet build OptiRouter.sln -c Release --no-restore -warnaserror
dotnet test OptiRouter.sln -c Release --no-build \
  --logger 'trx;LogFileName=tests.trx' --results-directory "$work/results"
dotnet list OptiRouter.sln package --include-transitive --vulnerable --format json
```

运行环境还应保持干净，不能注入其他生产 Budget/Redis/上游配置。连接真实后端的并发与故障测试，应另建完全独立的临时服务，不复用生产库。
