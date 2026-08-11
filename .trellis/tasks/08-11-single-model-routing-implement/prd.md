# 单模型路由实现 P1+P2+P3

## Goal

落地 `08-11-single-model-routing-research` 研究报告中的**立即集**提案（用户已确认范围）：

- **P1**：分类准确率可观测——生产端确保 `routing_reason` 含可解析的 `target=Tier(signal)` 标记 + 结构化分类信号，让已交付的 `analyze_audit.py` 单模型段在生产可观测分类准确率。
- **P2**：策略链并行化 + 结构化 `Reason`——引入 `ParallelGroup` 接口与策略分组（A 过滤 / B 分类 / C 排序 / D 约束），组内可并行、组间保持依赖序；`RouterDecision.Reason` 从拼接字符串改为结构化键值对，支撑归因与可测。
- **P3**：上下文老虎机（LinUCB）——新增 `EnableContextualBandit`（默认关），用现有 7 类分类信号 + tier 作上下文特征，每模型维护线性 θ + 协方差，替代/补充非上下文 Thompson；修实证发现的「非上下文 Thompson 系统性低估 Strong」缺陷。

生产代码实现，全景区块（RouterEngine / 策略链 / RoutingOptions / LatencyAwarePolicy / 新 ContextualBanditState）。所有新特性默认关、向后兼容。

## Confirmed Facts（代码勘察结论）

### P1 现状
- `RuleClassifierPolicy` 已输出 `rule-classifier: target=Tier(signal)` 到 `Reason`（`target=Strong(code-complex)` 等），`analyze_audit.py` 已能解析（本次研究交付）。
- `RouterDecision` 已有结构化 `RequestComplexity` 字段（`RequestComplexity = Unknown/Simple/Standard/Complex`）。
- **缺口**：分类信号只有字符串 reason，无结构化字段；`analyze_audit.py` 的准确率解析依赖 `routing_reason` 字符串格式，生产端稳定性未锁定（无测试）。

### P2 现状
- `RouterEngine.Decide`：`foreach (var policy in _policies) decision = policy.Apply(context, decision)`——**串行**线性。
- `IRouterPolicy.Apply(RouterContext, RouterDecision)`——策略间通过 `decision.Candidates` 隐式耦合。
- `RouterDecision.Reason` 是 `required string`，各策略 `previous with { Reason = $"{previous.Reason}; {reason}" }` 拼接——**单字符串、无结构化**。
- 策略链（`Program.cs` 注册）：CapabilityFilter → RuleClassifier → SessionAffinity → SemanticRouter → LongInput → LatencyAware → PromptCacheAffinity → BudgetGuard → QuotaAware → Failover → LoadBalance（11 个）。
- 依赖关系（决定分组）：过滤型（CapabilityFilter/LongInput/Failover/QuotaAware）可并行；分类型（RuleClassifier/SemanticRouter）可并行；排序型（LatencyAware/PromptCacheAffinity）依赖分类结果；约束型（BudgetGuard/SessionAffinity/LoadBalance）依赖过滤+排序。

### P3 现状
- `LatencyAwarePolicy`：段内 `ReorderByThompsonSampling`（每模型单 Beta 采样）或 `ReorderByLatencyScore`；`EnableThompsonSampling` Gate。
- `ThompsonStateStore`：每模型 `ModelStats(Alpha, Beta)`，`RecordOutcome` 连续奖励（快 1.0/慢 0.3/失败 0/竞速 0.5）、discount 0.95。
- `RuleClassifierPolicy`：7 类分类信号（code-complex/code-simple/math-detected/translation/simple-qa/complex-instruction/default）→ 目标 tier + 权重 profile。
- **实证发现**（研究报告 §5）：合成下 gpt-4o 因延迟高 reward 低（regret 0.447），非上下文 Thompson 系统性低估 Strong——只优化延迟不优化质量。
- `RoutingOptions` 新增配置需过 `RouterOptionsValidator`（启动校验）。

### 测试现状
- `tests/OptiRouter.Tests/`：`RouterEngineTests.cs`、`MultiDimensionalAndBanditTests.cs`、`LatencyAwarePolicyTests.cs`、`RuleClassifierPolicyTests.cs`、`RoutingFoundationTests.cs` 等。414 测试全绿（研究任务前）。

## Requirements

### R1 P1 分类准确率可观测
- 生产端把分类信号从「仅字符串 reason」提升为**结构化字段**（`RouterDecision` 或 `RouterContext` 新增分类信号属性），同时保持 `routing_reason` 的 `target=Tier(signal)` 格式不变（向后兼容 analyze_audit）。
- 用测试锁定 `target=` 标记格式，防止回归。

### R2 P2 策略链分组契约 + 结构化 Reason
- 新增 `PolicyGroup` 枚举，`IRouterPolicy` 声明所属分组（Filter/Classify/Order/Constraint）。
- `RouterEngine.Decide` 按分组依赖序执行（Filter→Classify→Order→Constraint），组内保留串行（叠加过滤/fallback/重排语义）。
- `RouterDecision.ReasonEvents` 结构化事件列表（`ReasonEvent(Policy, Detail)`），`Reason` 字符串保持原样（向后兼容日志/审计与测试断言）。
- **勘察结论**：策略链本质上串行（Failover 有 fallback 副作用、QuotaAware 有重排副作用），genuine 并行需重构独立子链，超出安全范围——P2 交付分组契约 + 结构化 Reason，不强制并行。

### R3 P3 上下文老虎机（LinUCB）
- 新增 `RoutingOptions.EnableContextualBandit`（默认 false）+ 相关参数（特征维度、UCB 探索系数 α、折扣）。
- 新增 `ContextualBanditState`（每模型 θ + 协方差矩阵 A、b），线程安全。
- 用现有分类信号（one-hot）+ tier 构造上下文特征向量。
- `LatencyAwarePolicy` 段内：`EnableContextualBandit=true` 时用 LinUCB 打分替代 Thompson（或共存）；`false` 时行为与现有一致（向后兼容）。
- 单测验证：上下文特征影响选型（同模型不同分类信号选型不同）；`false` 时与现有一致。

## Acceptance Criteria

- [ ] **AC1**：分类信号有结构化字段，`routing_reason` 的 `target=` 格式保持不变；`analyze_audit.py` 对生产数据仍能解析准确率（P1）。
- [ ] **AC2**：策略链支持分组契约（`PolicyGroup` + `IRouterPolicy.Group`），`Decide` 按组依赖序执行且结果与现有一致；`ReasonEvents` 结构化且 `Reason` 字符串保持原样（P2）。
- [ ] **AC3**：`EnableContextualBandit` 配置项存在且过校验；默认 false 时 `LatencyAwarePolicy` 行为与现有一致（P3 向后兼容）。
- [ ] **AC4**：`EnableContextualBandit=true` 时 LinUCB 打分生效，且上下文特征（分类信号）影响选型——单测验证同模型不同分类信号选型不同（P3）。
- [ ] **AC5**：新增配置/接口/LinUCB 有单测；全量测试套件通过（现 414 + 新增全绿）。
- [ ] **AC6**：`routing.md` spec 更新（新增 ParallelGroup 契约、EnableContextualBandit 配置、结构化 Reason 决策记录）。

## Out of Scope

- 不做 P4（能力标签扩展）、P5/P6/P7/P8（中期/探索提案）。
- 不接入真实 LLM-as-judge 质量反馈（P7 探索项）。
- 不重写 `analyze_audit.py` 既有段（只确保兼容）。
- 不进 `generate_audit_data.py` 新逻辑（研究任务已交付）。

## Open Questions

- 无（实现范围已由用户确认：P1+P2+P3 立即集）。