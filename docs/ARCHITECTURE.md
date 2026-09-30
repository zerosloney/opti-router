# OptiRouter 架构

本文是**活文档**：描述当前代码的组件地图、数据流与不变量，供新成员与 agent 快速建立全局认知。
历史评审结论（问题清单、修复验收）见 [architecture-review.md](architecture-review.md)；部署与发版操作见仓库根 `AGENTS.md`。

单进程 .NET 8 双职责：`/v1`、`/v1/messages`、`/v1beta` 三协议 LLM 路由代理 + Blazor Server 管理台（`/dashboard` 等页面）。构建版本由 MinVer 从 `v*` git tag 自动派生。

## 组件地图

| 边界 | 主要组件 | 职责与依赖 |
| --- | --- | --- |
| 组合根 | [Program.cs](../src/OptiRouter/Program.cs)（531 行）+ [Composition/](../src/OptiRouter/Composition/) 六模块 | 注册路由/存储/安全/上游/可观测/管理台服务；组合根只做配置与组装，不含业务逻辑 |
| 入口安全 | [Security/](../src/OptiRouter/Security/)、[ClientKeyService](../src/OptiRouter/Configuration/ClientKeyService.cs)、AdminKeyStore | 管理 Cookie/Bearer 与租户 ClientKey 分离；代理准入、QPS/日预算、IP 分区；管理面最小 RBAC（admin/operator/viewer，执法单点 `AdminOperations.Can`，附加身份存配置库 admin-identities scope）；管理端路径前缀集中在 `RequestPathPolicy.AdminPathPrefixes` |
| 协议端点 | `ChatCompletionsEndpoint`、`AnthropicMessagesEndpoint`、`GeminiGenerateContentEndpoint` | 各协议 HTTP/JSON 校验与 `ChatRequest` 双向翻译；三入口共用同一路由、预算、熔断、审计 |
| 请求执行 | [ProxyOrchestrator](../src/OptiRouter/Endpoints/ProxyOrchestrator.cs) + [Settlement partial](../src/OptiRouter/Endpoints/ProxyOrchestrator.Settlement.cs)、`RaceOrchestrator`、`FusionRouter`、`CascadeUpgradeHandler`、`StreamingHedgeOrchestrator` | 缓存、预处理、预算预留、候选尝试、五类执行模式的流式/非流式生命周期 |
| 路由决策 | [RouterEngine](../src/OptiRouter/Routing/RouterEngine.cs) + `IRouterPolicy` 策略族（Routing/ 90+ 文件） | Filter→Classify→Order→Constraint 四阶段管道产出候选链与硬排除 |
| 上游适配 | [ModelClientProvider](../src/OptiRouter/Endpoints/ModelClientProvider.cs)、三协议 Client + [Protocols/](../src/OptiRouter/Clients/Protocols/) Translators | 客户端构造/缓存、HTTP/SSE、超时与重试、usage 提取；热更新后旧客户端延迟释放 |
| 结果与状态 | [OutcomeRecorder](../src/OptiRouter/Endpoints/OutcomeRecorder.cs)、`CostLedger`、审计存储、`ModelHealthTracker`、学习状态存储 | 全局/会话/租户计费、审计、配额、健康、Bandit/Thompson 反馈 |
| 配置与管理面 | `AppConfigDbStore`、`ModelsConfigService`、Dashboard handlers、Blazor 页面 + [ApiService](../src/OptiRouter/Components/Services/ApiService.cs) | 配置入库并 `IConfigurationRoot.Reload()` 热生效；管理页面经服务端 HTTP 调管理 API（不直调领域服务） |
| 后台与可选功能 | Health 探活、Metrics、Mesh（Redis 状态广播）、MCP 工具编排、Compliance（PII/审核/压缩）、Compression | HostedService 与旁路组件，全部有开关默认关闭或默认无害 |

## 代理请求生命周期

非流式（`SendAsync`）与流式（`StreamAsync`）共用同一前置序列，均以**每轮路由决策的不可变快照**（`RequestSnapshot`）贯穿结算：

```text
会话派生/界定 → PII 脱敏前算缓存与 regenerate 键 → PII/Persona/压缩改写 → 输入审核
→ 精确/语义缓存查询（命中短路，消费 regenerate 信号）
→ RouterEngine.Decide → RequestSnapshot → 预算预留（BudgetReservationScope）
→ 执行模式分派（Fusion → Fusion-lite/Race → 串行候选链）
→ 结算（Settlement partial 单点）→ PII 还原 / 合规 flush → 响应
```

**终态矩阵与结算单点**：所有候选级终态的记账集中在 `ProxyOrchestrator.Settlement.cs`——
`SettleCandidateSuccess`（非流式成功）、`SettleStreamSuccess`（流式成功）、`SettleCandidateFailure`（五类失败，串行与流式首行前共用，`streamed` 参数区分审计口径）、`SettleStreamAbnormalEnd`（流式中途故障/客户端取消/断开）、`ModerateFinalOutputAsync`（所有非流式成功出口的统一输出审核）。控制流（换候选/透传状态码/全局超时终止）留在调用方。
改动记账语义只动这一个文件；新增失败类别 = 加一个 `CandidateFailureKind` 分支。

关键终态语义（回归测试锁定于 `StreamingLifecycleTests`/`ProbeSlotAccountingTests`）：
- 客户端取消/断开**不是**上游失败：不进熔断、不记负反馈，已知 usage 一次性结算（审计记 `client-cancelled`）。
- 429 纯配额：不进熔断、不记 Thompson/regenerate 负反馈，仅配额记录与探槽释放。
- 400 类请求语义拒绝：不进熔断，但进审计与 bandit；无候选可降级时透传原始状态码。
- 探测槽位（半开熔断）要么被结算上报、要么被释放，任何离开路径不得泄漏。

## 执行模式

| 模式 | 开关 | 语义 | 结算边界 |
| --- | --- | --- | --- |
| 串行 Failover | `EnableFailover` | 候选链顺序尝试，异常换下一候选 | 全部走 Settlement partial |
| Fusion-lite（Race） | `EnableFusionMode` | 非流式首轮并行前 N 候选取最快成功，取消其余 | 每尝试独立探槽；取消方 `ReleaseProbe` 不计失败（RaceOrchestrator 内聚） |
| Fusion（质量路由） | `EnableFusionRouter` | panel 并行 → analyst 结构化分析 → outer 终答；流式为 Anchor 推流 + 后台补丁 | FusionRouter 内聚（N+2 成本分拆经 `DagCostAttributor`） |
| 级联升级 | `EnableCascadeUpgrade` | Cheap 低置信采样升级 Strong 重答；升级出口同过输出审核 | CascadeUpgradeHandler，尊重硬排除 |
| 流式 Hedge | `StreamHedgeDelayMs` | 主候选首行竞速延迟启动下一候选，先出首行者服务 | 竞速指标 `optirouter_stream_hedge_races_total`；落败方按慢首行记失败 |

## 路由决策管道

`RouterEngine.Decide`：候选按预编译的 `PolicyGroup` 分区依次应用——
**Filter**（能力/上下文/预算/数据主权等硬过滤）→ **Classify**（规则分类、复杂度）→ **Order**（Tier 序、Thompson/延迟/负载重排）→ **Constraint**（预算守卫、失败排除）。

不变量：**资格池**（`eligibleModels`）单调收缩，后续策略只能从池内选候选，防止降级策略重建大集合 undo 硬过滤；唯一例外是"全灭逃生门"（池被清空时接受补链保活命，注释标明了该窗口）。学习类策略（Thompson/Contextual Bandit/LatencyAware）只重排**不扩池**。

## 存储后端矩阵

各状态能力不同，**不能**因某后端支持 A 就推断支持 B：

| 状态 | 后端 | 多实例共享 |
| --- | --- | --- |
| 配置库（路由/预算/模型/租户 Key/学习状态） | SQLite / MariaDB（`ConfigDbConnectionString` 一处连接，`StoreProvider=Auto` 自动切换）；保存走跨实例原子 CAS | ✅ |
| 成本账本 | InMemory / SQLite / MariaDB / Postgres / Redis | 服务器型 ✅ |
| 请求审计 | InMemory / SQLite / MariaDB / Postgres（异步批量落库） | 服务器型 ✅ |
| 租户密钥 | `data/client-keys.json` / MariaDB | MariaDB ✅ |
| 响应缓存/健康/亲和/部分学习状态 | 进程内存 | ❌（Mesh 可选广播，非强一致） |

## 配置流

`appsettings*.json` 仅承担部署设置与**首次播种**；运行时权威配置在配置库（Dashboard 编辑 → 落库 → `IConfigurationRoot.Reload()` → `OptionsMonitor` / 客户端缓存 / 路由状态消费者热更新，无需重启）。管理端鉴权密钥 SHA256 哈希存配置库，appsettings/环境变量中的 `AdminApiKey` 仅作首启种子。

## 关键不变量与运维指针

- **Blazor 会话保活三防线**（移除任一即复发）：session ping 续期、断线终态自动整页刷新、BFCache 恢复刷新——详见根 `AGENTS.md`。
- **单实例守卫**：Production 开启 `Local\` 互斥锁，publish 目录只允许一个实例；开发实例（Development）不受影响。
- **dev/生产库隔离**：launchSettings 注入的连接串指向 `optirouter_dev`；HostedService（探活/审计淘汰/预算清扫）不得触碰生产数据。
- **发版**：`scripts/release.ps1` 固化"配置预检 → 停服 → publish → 启服 → /health 验证"；版本 = git tag（MinVer）。
- 性能结论先测量再动：[bench/hotpath-bench/RESULTS.md](../bench/hotpath-bench/RESULTS.md) 记录了准入热路径的量化天花板与重构触发阈值。
