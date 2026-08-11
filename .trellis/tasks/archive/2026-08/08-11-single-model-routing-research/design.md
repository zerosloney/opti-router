# 单模型智能选择路由 — 研究设计

## 1. 研究边界

- **研究对象**：单模型选择路由 = 对一个请求**选一个模型**发出去。对照的是现有**策略链**（11 个 `IRouterPolicy`）+ Thompson MAB + 多维能力评分 + 语义路由。
- **与 fusion 研究的区别**：fusion 研究的是「组合多个模型」（panel→analyst→outer，Overgenerate & Rerank 变体）；本研究是「选优」（predictive / cascading / bandit 家族）。两者互补，共享审计工具链。
- **交付**：研究报告（`research/single-model-routing-algo-research.md`）+ `generate_audit_data.py` / `analyze_audit.py` 扩展。不动生产代码。

## 2. 综述结构（R1）

按「选优路由」范式家族组织，每范式一段，含：核心机制 / 成本-质量特征 / 适用场景 / 与现有策略链差异。计划覆盖：

| # | 范式 | 对应现有实现 | 差异点假设 |
|---|------|-------------|-----------|
| 1 | RouterBench predictive（分类器/LLM 路由） | RuleClassifier + SemanticRouter | 现有是规则+余弦，无学习式分类器 |
| 2 | Cascading（成本升序，达阈值即停） | Failover 降级链 + CascadeUpgradeHandler | 现有 cascade 是失败驱动，非质量阈值驱动 |
| 3 | 多臂老虎机（Thompson / UCB / EXP3） | ThompsonSampler | 现有仅 Thompson，无 UCB/EXP3，无上下文 |
| 4 | 上下文老虎机（LinUCB / contextual bandit） | 多维能力评分（启发式权重） | 现有权重是手配，非学习得来 |
| 5 | Embedding/语义路由（Cosine 语义匹配） | SemanticRouterPolicy | 现有语义 phrase 是静态列表，无 embedding 检索 |
| 6 | Compact Input Routing（RouterBench RI/RL） | 无 | SOTA 差距：压缩输入降成本 |
| 7 | 成本-质量 Pareto 前沿 / AIQ | 无（实证层） | SOTA 差距：无凸包定位 |

> 综述需与 `routing.md` 现有契约对照（策略链顺序、Thompson 奖励语义、多维 tolerance、KnownTags）。

## 3. 差距分析与提案（R2）

每条提案模板：**问题陈述 / 方案 / 预期收益 / 成本增量 / 验收标准 / 风险与回滚**。候选提案方向（最终以综述+实证为准）：

- P1 结构化 Reason + 策略链并行化（已知 P2 待办，4 组 A/B/C/D + ParallelGroup 接口）
- P2 能力标签扩展（audio/video/long-context/structured-output，KnownTags > 3）
- P3 分类信号准确率可观测（混淆矩阵落 spec，为学习式分类器铺路）
- P4 上下文老虎机（LinUCB）替代/补充纯 Thompson 的探索
- P5 SemanticRouter 升级为 embedding 检索（静态 phrase → 向量库）
- P6 成本-质量凸包定位（AIQ 比较单模型 vs 融合 vs cascade）
- P7 compact input routing（短输入降成本）
- P8 探索-利用自适应（exploration 衰减 / ε-greedy）

## 4. 实证工具扩展（R3）

### 4.1 `generate_audit_data.py` 新增参数

- `--signal-accuracy`（默认 0.9）：分类信号命中率，控制"实际路由 tier == 应路由 tier"的比例，注入误判。
- `--thompson-rate`（默认 0.0）：生成带 Thompson 奖励/探索轮次的行（`routing_reason` 含 `thompson: reward=X, round=Y`）。
- `--quality-agent`（默认 0.0 关闭）：为每行生成质量代理分数（`cost` 已成，质量代理可用 `routing_reason` 或新逻辑内嵌），供成本-质量凸包构造。
- 模型画像扩展：给 `DEFAULT_MODELS` 加 `quality` 字段（质量代理分数），供成本-质量 Pareto 计算。

### 4.2 `analyze_audit.py` 新增报告段

- `## Single-Model Selection`：
  - **分类信号混淆矩阵 / 准确率**：从 `routing_reason` 解析 `target=Tier(signal)` vs 实际 `routed_tier`，输出混淆矩阵 + 每信号准确率。
  - **Thompson 奖励分布**：解析 `routing_reason` 的 `thompson: reward=X, round=Y`，聚合 reward 直方图 + 每模型 Alpha/Beta 估计 + regret 代理。
  - **成本-质量 Pareto / AIQ**：按模型聚合 cost vs quality 代理，输出凸包与 AIQ 比较（单模型 baseline vs 融合 vs cascade）。
- 列缺失/无数据时优雅降级（AC6）。

### 4.3 向后兼容

- 新参数默认 0 / 关，不改变既有生成行为；新报告段对无单模型列/数据的 DB 返回 "no data"，不崩。

## 5. 研究报告结构（R4）

`research/single-model-routing-algo-research.md`：

1. 现状：单模型路由现有实现解剖（策略链 + Thompson + 多维评分 + 语义路由）
2. 算法综述（R1，≥6 范式，含出处与差异）
3. 差距分析（现有 vs SOTA，含已知 P2 待办评估）
4. 改进提案（≥6 条，P1-P8 方向，优先级排序）
5. 实证分析（合成数据，Q1-Q5 数据表）
6. 结论与优先级排序

## 6. 风险与回滚

- 合成数据不能证真质量收益 → 报告明示边界，质量结论基于文献 + 代理指标。
- signal-accuracy / thompson 解析依赖 `routing_reason` 字符串格式 → 报告标注近似；生成与解析用同一格式约定。
- 新参数默认关闭、新报告段降级 → 无回滚风险。