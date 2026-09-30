# ROADMAP

规划与取舍的单一事实来源。已交付能力见 [README](README.md)，当前架构见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。
本文只列**尚未实现**的规划项与**明确不做**的事，避免「核心特性里混入规划项」的预期错位。

## 规划中（按优先级）

### 提示词版本管理（PromptTemplateManager）

Analyst / Outer 系统提示词模板的版本控制与变量动态插值。动机：Fusion 编排的内置提示词目前是代码常量，调优提示词需要发版；版本化后可在管理台迭代提示词并回滚。落地形态候选：配置库新 scope 文档 + 版本号字段 + 热生效（复用现有配置流）。

### 端云混合投机解码（token 级，HybridSpeculativeOrchestrator）

本地 1B/3B 端侧模型生成 Draft 草稿，云端强模型（Verifier）二次校验修补。**注意与现状的区别**：现存的「渐进式投机流」（Fusion 流式 Anchor 推流 + 后台 Panel 补丁）是**文档级编排**，不是 token 级投机解码；token 级需要自研 draft/verify 协议与 SSE 帧级重组，工程量大，依赖本地小模型端点普及后再启动。

### 管理 SSO / OIDC

最小 RBAC（admin/operator/viewer，见 [docs/CONFIGURATION.md](docs/CONFIGURATION.md)「管理台角色」）已落地；SSO 是其自然延伸——企业部署中管理台账号接入组织 IdP，角色映射到 IdP group。前置条件：目标客群出现明确需求（当前单管理员自部署场景收益低）。

### 学习信号升级

- Bandit 语义特征从词袋哈希升级为可插拔稠密向量（ONNX 引擎已存在，缺与学习特征的接线）。
- 策略间公开横评的常态化：`scripts/strategy-bakeoff.ps1`（见 [docs/strategy-bakeoff.md](docs/strategy-bakeoff.md)）已可复现，后续在模型池变更时重跑并存档结论。

### 亲和机制整合评估

SessionAffinity / PromptCacheAffinity / KvCacheLocality 三套亲和机制并存，各自开关。规划：写清选型矩阵（何时用哪个、互斥性、组合语义），评估是否合并为统一亲和层。在此之前三者的行为不变。

## 明确不做

- **微服务拆分**：架构评审结论（[docs/architecture-review.md](docs/architecture-review.md) §1）——保留单体，先建立内部边界与跨执行模式不变量。存储层已具备多实例共享能力（Postgres/Redis 后端），水平扩展不依赖进程拆分。
- **预防性性能优化**：热路径改造由 [bench/hotpath-bench/RESULTS.md](bench/hotpath-bench/RESULTS.md) 的重构触发阈值驱动（如 DB p95 > 5ms、租户数 > 2000），不凭感觉提前优化。
- **成本/合规类功能默认开启**：Fusion（N+2 成本）、judge 抽评（原文外发）、PII 脱敏、数据主权等一律保持显式 opt-in，默认行为永远是最便宜、最少副作用的。
