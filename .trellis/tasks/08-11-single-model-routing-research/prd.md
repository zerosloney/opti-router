# 单模型智能选择路由深层算法研究

## Goal

对 OptiRouter 的单模型智能选择路由做两层研究（与 `08-10-fusion-router-algo-research` 同模式，但研究对象是**选一个模型**而非**融合多个模型**）：

1. **算法综述 + 改进提案**：对照单模型选择路由（predictive routing / model selection）领域的最新算法 —— RouterBench predictive 家族、LLM-as-router、cascading、多臂老虎机（Thompson Sampling / UCB / EXP3 / LinUCB）、embedding/semantic 语义路由、compact input routing、成本-质量 Pareto 前沿等 —— 找出现有策略链与 SOTA 的差距，产出**可落地、可验证**的算法改进提案。
2. **实证分析现有实现**：用审计数据（真实或合成）量化现有策略链 + Thompson MAB + 多维能力评分的实际收益与成本：策略分类信号准不准？Thompson 探索-利用是否真的在优化成本-质量？多维评分是否比纯 rule 更优？并补上分析工具链缺口。

产出物是一份**研究报告**（markdown），含综述、差距分析、改进提案（每个提案带验收标准）、实证数据与结论。研究结论用于指导后续实现，但**本任务不做生产代码实现**——提案被采纳后另立实现任务。

## Confirmed Facts（代码勘察结论）

### 现有实现（单模型选择路由）
- **策略链**（`Program.cs` 注册，`RouterEngine.Decide` 串行线性执行）：`CapabilityFilter → RuleClassifier → SessionAffinity → SemanticRouter → LongInput → LatencyAware → PromptCacheAffinity → BudgetGuard → QuotaAware → Failover → LoadBalance`（11 个 `IRouterPolicy`）。
- **决策耦合方式**：`RouterEngine.Decide` 逐个 `policy.Apply(context, decision)`，每个策略通过 `decision.Candidates` 隐式耦合（前一个的排序/过滤直接影响后一个），`Reason` 是单字符串，无结构化、无并行。
- **RuleClassifierPolicy**：tier 分类（Strong/Medium/Cheap/Unknown）+ 代码意图细分（`code-complex`→Strong、`code-simple`→Medium、`code-detected` 裸块→保守 Strong）；权重画像按分类（code/math/translation/simple-qa/complex-instruction/default）路由到多维评分。
- **多维能力评分**（`GetEffectiveCapability`）：utility dot product `Σ weight_i × capability_i`，tolerance 0.15，分差 ≤ tolerance 时价格择廉；维度化 tier 回退（语言扁平 0.80/0.78/0.76，推理/代码陡 0.90/0.50/0.20 等）。
- **Thompson Sampling MAB**（`ThompsonSampler`/`ThompsonStateStore`）：Beta(α,β) 采样重排候选；连续奖励——快成功(<`ThompsonLatencyTargetMs`)→1.0、慢成功→0.3、硬失败→0.0、竞速失败→`ThompsonRaceCancelledReward`(0.5)；discount 0.95；`RecordThompsonRaceCancelled` 区分竞速取消与真失败。
- **延迟感知**（`LatencyAwarePolicy`）：`score = 1/(avg + 0.5×p95 + 50)`，p95 压制 tail 差模型。
- **SemanticRouterPolicy**：cosine similarity 到语义 route phrases 覆盖 tier；**PromptCacheAffinityPolicy**：privacy-safe 稳定前缀 SHA-256 软提升命中模型。
- **能力标签**：`KnownTags` 仅 3 个（vision/tool-use/json-mode）；`HasAllTags` 对空 Tags 返回 false（扩展时改为「未标注=不限制」）。
- **成本/审计**：全部请求按真实/预估成本入账；`routing_reason` 单字符串记录路由原因；`routed_tier` 记录落点；审计列含 cascade/parallel/fusion/ttft/cache 等 25 列。

### 分析工具链现状
- `scripts/generate_audit_data.py`：已有 25 列 schema、7 类分类信号（code-complex/code-simple/math/translation/simple-qa/complex-instruction/default）、`--misclassify`（误判注入）、`--cascade-rate`、`--parallel-rate`、`--fusion-rate`、`--fusion-analyst-fail-rate`。**无单模型选择专用维度**：无 Thompson 奖励/探索轮次、无成本-质量凸包构造、无分类信号准确率对照（真实应路由 vs 实际路由）。
- `scripts/analyze_audit.py`：已有 `build_summary`/`build_by_model`/`build_by_tier`/`build_cascade`/`build_fusion`/`build_by_reason`（规则误判率代理）/`build_daily_trend`。**缺单模型选择专用报告段**：分类信号 ↔ 实际 tier 的混淆/准确率、Thompson 奖励分布与 regret 代理、成本-质量 Pareto（AIQ）、探索-利用平衡观测。

### 已知 P2 待办（memory 记录，供差距分析引用）
- 策略链并行化（4 组 A/B/C/D + `ParallelGroup` 接口 + 结构化 `Reason`）。
- 能力标签扩展（audio/video/long-context/structured-output 等）。

### 现有 spec 文档
- `.trellis/spec/backend/routing.md` 已含策略链、Thompson 奖励、多维评分、代码意图细分的契约与决策记录——研究需保持一致，改进提案不得破坏既有契约。

## Requirements

### R1 算法综述
- 覆盖单模型选择路由的代表性范式：RouterBench predictive（LLM 路由/分类器）、cascading（成本升序查询）、多臂老虎机（Thompson / UCB / EXP3 / LinUCB 上下文老虎机）、embedding/semantic 语义路由、compact input routing（RouterBench 的 RI / RL 类）、LLM-as-judge 路由、成本-质量 Pareto 前沿（AIQ）。
- 每个范式给出：核心机制、成本-质量特征、适用场景、与现有策略链的关键差异点。
- 综述要有出处（论文/官方文档链接），标注哪些已落在现有实现、哪些是 SOTA 差距。

### R2 差距分析 + 改进提案
- 逐条对比现有策略链（11 策略 + Thompson + 多维评分 + 语义路由）与 SOTA，识别可落地差距。
- 每条改进提案必须包含：**问题陈述、方案、预期收益、成本增量、验收标准（可测试）、风险/回滚**。
- 提案必须尊重现有契约（`routing.md` 的策略链顺序、Thompson 奖励语义、多维 tolerance、audit 列），不得破坏向后兼容。
- 已知 P2 待办（策略链并行化、能力标签扩展）应作为候选提案纳入差距分析，给出证据与优先级。

### R3 实证分析
- 扩展 `scripts/generate_audit_data.py`：支持生成单模型选择路由维度样本——分类信号（含真实应路由 tier vs 实际路由）、Thompson 奖励（快/慢/失败/竞速 + 探索轮次）、成本-质量（模型成本 vs 质量代理分数）、多维评分 vs 纯 rule 的对照。
- 扩展 `scripts/analyze_audit.py`：新增单模型选择报告段——分类信号↔实际 tier 混淆矩阵/准确率、Thompson 奖励分布与 regret 代理、成本-质量 Pareto 前沿（AIQ 比较）、探索-利用平衡观测。
- 用合成数据跑通闭环，产出实证报告，回答：策略分类准不准？Thompson 是否在优化成本-质量？多维评分是否优于纯 rule？

### R4 研究报告
- 综合综述 + 实证，形成一份结构化研究报告 `research/single-model-routing-algo-research.md`（或类似路径），含结论、优先级排序的改进提案清单、实证数据表。

## Acceptance Criteria

- [ ] **AC1**：`research/` 下存在完整综述章节，覆盖 R1 列出的 ≥6 个代表性范式，每个都有出处与"与现有实现的差异"。
- [ ] **AC2**：改进提案清单 ≥6 条，每条含问题/方案/收益/成本/验收/风险，且与 `routing.md` 现有契约一致；已知 P2 待办（策略链并行化、能力标签扩展）已被评估并纳入/排除。
- [ ] **AC3**：`generate_audit_data.py` 能生成含单模型选择维度的合成数据（分类信号准确率、Thompson 奖励分布、成本-质量），`analyze_audit.py` 报告新增单模型选择段（混淆矩阵/准确率、Thompson 奖励、成本-质量 AIQ）。
- [ ] **AC4**：用合成数据跑通 `generate → analyze` 闭环，报告能回答"分类信号准确率、Thompson 效益、多维评分 vs 纯 rule"（合成数据前提下）。
- [ ] **AC5**：研究报告结论明确：哪些改进提案值得进入实现阶段（附优先级），哪些是收益不明的探索项。
- [ ] **AC6**：`analyze_audit.py` 对无单模型选择列的 DB 仍可正常运行（向后兼容，不崩）。

## Out of Scope

- 不实现任何单模型路由算法改进（本任务只研究 + 提案）。
- 不改动 `RouterEngine.cs` / 策略链 / `RoutingOptions` / `ThompsonSampler` 的生产代码。
- 不接入真实付费上游跑真实流量（实证基于合成数据）。
- 不重写 `routing.md` spec 的既有路由决策记录（只新增研究成果，不破坏既有内容）。

## Open Questions

- 无（研究范围已由用户确认：综述 + 提案 + 实证工具扩展，与 fusion 研究同模式）。