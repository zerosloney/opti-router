## OptiRouter 项目分析报告

### 项目定位

OptiRouter 是一个基于 .NET 10 / ASP.NET Core 的多模型智能路由 HTTP 代理。它对外暴露 OpenAI 兼容的 `/v1/chat/completions` 接口，对内接入多个 OpenAI 兼容的上游模型，由路由引擎自动决定每个请求发给哪个模型，目标是「省 token、降成本、保可用」。客户端只需按标准 OpenAI 格式发请求（`model` 字段会被忽略），无需关心后端模型编排细节。

### 技术栈

运行时为 .NET 10（`net10.0`），主项目为 Minimal API 风格的 Web 项目，开启了 `Nullable`、`TreatWarningsAsErrors` 与文档生成。第三方依赖非常克制，仅两个：`Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3`（成本账本持久化）。测试项目使用 xUnit、`Microsoft.AspNetCore.Mvc.Testing`（集成测试宿主）和 `WireMock.Net`（真实 HTTP mock 上游），另配 coverlet 做覆盖率采集。整体是「零框架、少依赖」的风格，核心逻辑全部手写。

### 架构与请求流转

一次请求的完整链路是：客户端 POST `/v1/chat/completions` → API Key 中间件校验（SHA256 + 固定时间比较，防时序攻击）→ 按来源 IP 的固定窗口限流（默认每分钟 60 次，仅作用于 `/v1/*`）→ `ChatCompletionsEndpoint` 做入参校验 → `ProxyOrchestrator` 编排 → `RouterEngine.Decide` 产出候选模型链 → 按链顺序调用上游 → 成功即透明透传原始响应（非流式直接回原始 JSON，流式逐行转发 SSE）。

路由引擎采用策略管道（Pipeline）模式：初始候选为所有启用的模型（按 Tier 升序、上下文长度降序），随后依次经过四个 `IRouterPolicy`，每个策略用 record `with` 表达式修正决策并追加可读的 Reason 字符串，便于排查。

四个策略各有分工。`RuleClassifierPolicy` 按请求特征推断能力分档：检测到代码标记（```、def、class 等）→ Strong；多轮对话带超长 system prompt → Strong；单条短消息 → Cheap；其余走 `DefaultTier`。`TokenEstimator` + `LongInputPolicy` 用 rune 分桶加权粗估 token（CJK 按 1.5 字符/token、ASCII 按 4、其他按 2.5，每条消息加 3 固定开销），超长输入时过滤上下文装不下的模型。`BudgetGuardPolicy` 检查日/会话预算，耗尽时按配置要么拒绝（返回 429），要么降级到最便宜的可行模型链。`FailoverPolicy` 排除本次请求内已失败的模型和处于跨请求熔断冷却中的模型，候选全空时构建跨 Tier 的兜底降级链。

`ProxyOrchestrator` 负责执行候选链：非流式场景逐模型尝试，仅对可重试状态码（408/429/5xx）和网络错误降级，成功即记账并返回；流式场景分两阶段——首 chunk 之前失败可切换候选，一旦开始 yield 就无法再换模型，异常会转为 SSE 错误帧。`ModelHealthTracker` 提供简单熔断：连续失败达阈值进入冷却，到期自动放行（无 HalfOpen 探测）。`CostLedger` 线程安全地记录日花费、全局累计与按 `X-Session-Id` 隔离的会话花费，底层存储可选 SQLite（生产，跨重启保留）或内存（测试），会话账户按空闲时长懒淘汰防泄漏。

### 安全设计

安全方面有几点值得注意。入站 `ProxyApiKey` 为空时直接拒绝所有 `/v1/*` 请求，避免「忘配密钥即裸奔」。密钥比较用 SHA256 哈希 + `CryptographicOperations.FixedTimeEquals`，规避时序侧信道。生产环境缺 HTTPS 会打警告日志。限流仅对 `/v1/*` 生效，`/health` 公开且不限流，方便监控探活。上游 ApiKey 通过 `Authorization: Bearer` 传递，README 明确要求生产走 TLS。

### 测试体系

测试覆盖相当完整。单元测试按模块组织：路由引擎、四个策略、Token 估算器、成本账本（含两种 store）、熔断器、配置绑定与校验器、客户端。冒烟测试用 `WebApplicationFactory` 起真实应用、`WireMock.Net` 起真实 HTTP 上游，验证非流式全链路（含记账断言）、流式 SSE 全链路（含 `[DONE]` 与末块 usage 记账）、以及主模型 500 自动切换到备用模型的 failover 场景。测试项目同样开启 `TreatWarningsAsErrors`，工程质量意识较强。

### 设计亮点与已知权衡

亮点方面：策略管道 + record 不可变决策的组合式设计清晰易扩展；透明透传原始响应避免了 re-serialize 的性能与兼容性损耗；共享 `SocketsHttpHandler` 防 socket 耗尽；配置校验启动即失败（ValidateOnStart）；README 对已知限制非常坦诚。

权衡与局限（项目自己也标注了）：Token 估算是分桶粗估而非真实 BPE，误差约 ±15%；熔断是简单冷却，无半开探测；Models 端点配置按模型名缓存在 `ModelClientProvider`，变更需重启（Routing 开关可热更新）；流式请求一旦开始输出就无法 failover；`RuleClassifierPolicy` 的代码检测基于关键词，误报/漏报都可能。

### 潜在改进方向

可以考虑的方向包括：接入真实 tokenizer（如 tiktoken）提升估算精度；为熔断引入 HalfOpen 探测或完整断路器；支持 Models 端点配置热更新（监听 `IOptionsMonitor.OnChange` 重建客户端）；扩展更多端点能力（如 embeddings、多 key 轮换）；补充 `/v1/models` 列表接口方便客户端发现可用模型；以及为 SQLite 账本增加定期归档/清理策略。

### 总体评价

这是一个定位清晰、工程素养较高的轻量级 LLM 路由代理。代码组织按职责分层（Configuration / Routing / Clients / Endpoints / Health），接口抽象适度（`IRouterPolicy`、`ICostLedgerStore`、`IModelClientProvider`），测试覆盖从单元到真实 HTTP 端到端，注释和 README 对设计取舍的解释非常充分。适合作为多模型成本优化场景的网关层使用；在规模化生产前，建议优先补齐熔断半开探测、配置热更新与更精确的 token 估算。
