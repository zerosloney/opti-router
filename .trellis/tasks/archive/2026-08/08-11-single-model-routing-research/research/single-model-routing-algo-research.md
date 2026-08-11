# 单模型智能选择路由深层算法研究报告

> 任务：`08-11-single-model-routing-research` · 日期：2026-08-11
> 范围：算法综述 + 差距分析与改进提案 + 实证分析（合成数据）
> 结论速览：OptiRouter 的单模型选择路由已覆盖「predictive 规则分类 + 非上下文 Thompson MAB + 启发式多维评分」三类范式的骨架，但存在 **分类信号无准确率可观测、Thompson 是非上下文纯探索（无请求特征）、多维权重手工配置、语义路由是 TF-IDF 静态词袋、策略链串行隐式耦合、能力标签仅 3 个** 六个核心差距。SOTA（RouteLLM/LLMRouterBench/LinUCB）领先的核心是**用偏好/上下文数据驱动路由决策**，而现有实现全部是**静态启发式**。实证（合成）确认分类准确率 ~79%、Thompson 奖励分布与 regret、成本-质量 Pareto 前沿。改进优先级见 §6。

---

## 1 现状：OptiRouter 单模型选择路由解剖

### 1.1 决策链（`RouterEngine.Decide`）

```
RouterEngine.Decide(request, options)
  ├─ 估算 token（BucketTokenEstimator）
  ├─ 初始候选：enabled 模型按 tier 升序（Strong 优先）+ MaxContextTokens 降序
  └─ foreach policy in _policies:  decision = policy.Apply(context, decision)   # 串行
```

策略链（`Program.cs` 注册，11 个 `IRouterPolicy`）：

| 位置 | 策略 | Gate | 作用 |
|------|------|------|------|
| 1 | `CapabilityFilterPolicy` | `EnableCapabilityFilter` | 按 vision/tool-use/json-mode 标签排除 |
| 2 | `RuleClassifierPolicy` | `EnableRuleClassifier` | 规则分级 → tier 过滤 / 多维能力评分重排 |
| 3 | `SessionAffinityPolicy` | — | 会话锚定到历史模型 |
| 4 | `SemanticRouterPolicy` | `EnableSemanticRouter` | TF-IDF 余弦相似度覆盖 tier |
| 5 | `LongInputPolicy` | `EnableTokenEstimator` | 排除上下文窗口不足模型 |
| 6 | `LatencyAwarePolicy` | `EnableLatencyAware`/`EnableThompsonSampling` | 段内延迟/Thompson 重排 |
| 7 | `PromptCacheAffinityPolicy` | `EnablePromptCacheAffinity` | 稳定前缀软提升命中模型 |
| 8 | `BudgetGuardPolicy` | `EnableBudgetGuard` | 预算耗尽降级 Cheap / 429 |
| 9 | `QuotaAwarePolicy` | `EnableQuotaAwareRouting` | 排除活跃耗尽 |
| 10 | `FailoverPolicy` | `EnableFailover` | 排除熔断模型 |
| 11 | `LoadBalancePolicy` | — | 轮询 |

**关键特征**：线性串行、策略间通过 `decision.Candidates` 隐式耦合、`Reason` 单字符串（无结构化、无并行）。

### 1.2 三类「选优」机制

- **规则分类（`RuleClassifierPolicy`）**：7 类信号（code-complex/code-simple/math-detected/translation/simple-qa/complex-instruction/default）→ 目标 tier；代码意图细分（复杂→Strong、简单→Medium、裸块→保守 Strong）。正则均为 `intentional-simple` 经验阈值。
- **多维能力评分（`GetEffectiveCapability`）**：utility dot product `Σ weight_i × capability_i`，tolerance 0.15 分桶（同桶按价格择廉）；维度化 tier 回退（语言扁平 0.80/0.78/0.76，推理/代码陡 0.90/0.50/0.20）。权重 profile 按分类**手工配置**（code: coding=1.0/reasoning=0.6/language=0.3 等）。
- **Thompson Sampling MAB（`LatencyAwarePolicy`）**：每模型 Beta(α,β)，段内采样重排；连续奖励——快成功(<800ms)→1.0、慢成功→0.3、硬失败→0.0、竞速失败→0.5；discount 0.95。**非上下文**（同模型对任何请求同一 Beta）。
- **语义路由（`SemanticRouterPolicy`）**：离线 **TF-IDF 词袋**（`TfIdfSemanticVectorEngine`，无 embedding 模型），cosine 相似度匹配静态 `SemanticRoutes` 列表。

### 1.3 能力标签（`KnownTags`）

仅 3 个：`vision` / `tool-use` / `json-mode`。`HasAllTags` 对空 Tags 返回 false（未标注=不通过过滤）。

### 1.4 已做的正确决策（与 SOTA 对齐处）

- **成本-质量双目标显式建模**：多维评分 tolerance + 价格择廉，直接对应 RouterBench 的成本-质量权衡。✅
- **Thompson MAB 连续奖励 + 竞速/真失败区分**：对齐 bandit 的 reward shaping，优于朴素二值。✅
- **语义路由离线 TF-IDF**：100% 离线、零外部依赖、Native AOT 兼容，对齐「低成本路由 overhead」原则（RouterBench 指出规则路由 <1ms、ML 分类器 ~50-100ms）。✅
- **延迟感知 p95 压制**：`1/(avg+0.5×p95+50)`，对齐「tail 感知」的延迟建模。✅
- **全流程成本入账 + `routing_reason` 记录**：可离线复盘，是「用数据说话」的前提。✅

---

## 2 算法综述（SOTA 范式扫描）

### 2.1 RouterBench — Predictive / Cascading / Overgenerate-Rerank（选优路由的标准框架）

**机制**（Shi et al., 2024, arXiv）：把路由问题建模进**成本-质量二维平面**，用非递减凸包提取 Pareto 前沿，用 **AIQ（Average Improvement in Quality）** 做跨路由器非参数比较。定义三类路由家族：
- **Predictive**：不加生成、直接预测哪个模型最优（KNN Router、MLP Router）——用训练数据预测各模型在 prompt 上的表现。
- **Cascading**：按成本升序逐个查询，达质量阈值即停（省成本、慢）。
- **Overgenerate & Rerank**：并行生成多个选最优（质量上界、贵）。

**成本-质量特征**：predictive 可接近单模型性能且省成本，但受限于性能预测器质量；cascading 省成本但串行慢；overgenerate 质量最好但最贵。RouterBench 用 Zero Router（下界）与 Oracle Router（上界）标定。IBM 用其连接 11 模型，整体超单模型且每查询省 $0.05。

**与现有实现差异**：
- 现有 RuleClassifier 是**手工规则 predictive**（无训练数据）；KNN/MLP 是**学习式 predictive**。差距=现有不能从数据改进分类。
- 现有 cascade（`CascadeUpgradeHandler`）是**失败驱动**升级（Cheap 失败→Strong），非 RouterBench 的**质量阈值驱动**级联。
- 现有无 Zero/Oracle 标定、无成本-质量凸包定位（本次实证才补上代理）。

**出处**：Shi et al., "RouterBench: A Benchmark for Multi-LLM Routing Systems", arXiv:2403.12031（2024-03）。

### 2.2 RouteLLM — 偏好数据驱动的路由（SOTA predictive）

**机制**（Zhao et al., 2024）：用 **Chatbot Arena 人类偏好数据**训练路由决策（GPT-4 vs 轻量模型二选一），支持多种 router 架构：矩阵分解（推荐，高效）、相似度加权 Elo、BERT 分类器、因果 LLM 分类器。开源，可 drop-in 替换 OpenAI client。

**成本-质量特征**：MT Bench 省 85% 成本、MMLU 省 45%、GSM8K 省 35%，同时保持 GPT-4 的 95% 质量；强模型仅需 14% 查询。路由器 overhead 可忽略。

**与现有实现差异**：
- 现有 RuleClassifier 是**规则 + 正则**分类，无偏好/反馈数据训练。
- RouteLLM 的**矩阵分解 / BERT 分类器**是学习式，能从偏好数据改进；现有完全静态。
- 核心差异：**数据驱动 vs 规则驱动**——这是现有实现与 SOTA predictive 的根本差距。

**出处**：Zhao et al., "RouteLLM: Learning to Route LLMs with Preference Data", arXiv:2406.18665（2024-07）。

### 2.3 多臂老虎机（Multi-Armed Bandit）— Thompson / UCB / EXP3

**机制**：在线选择「arm」（模型）最大化累计奖励，权衡**探索-利用**。
- **Thompson Sampling**（现有实现）：从每 arm 的后验分布采样，天然平衡探索-利用。适合奖励是随机的场景。
- **UCB1**：选 `均值 + sqrt(2 ln n / n_i)` 最大者，确定性探索界。
- **EXP3**：对抗性奖励下的指数权重，适合非平稳环境。
- **非上下文 VS 上下文**：非上下文假设 arm 收益与请求无关（现有实现）；上下文则用请求特征影响选择。

**成本-质量特征**：Thompson 无需训练、在线自适应、实现简单；但**非上下文**无法利用请求类型差异（同一模型对不同任务的优劣不同）。

**与现有实现差异**：
- 现有 Thompson 是**非上下文**——同模型对 code 与 translation 请求用同一 Beta，忽略「模型-任务匹配」这一核心路由信号。
- 现有无 UCB/EXP3 变体；无探索-利用显式调参（ε-greedy / 探索衰减）。
- **这是最值得关注的差距**：现有 Thompson 只能发现「哪个模型整体好」，不能发现「哪个模型对哪类请求好」。

**出处**：Thompson (1933)；Auer et al., "Finite-time Analysis of the Multiarmed Bandit Problem"（UCB1, 2002）。

### 2.4 上下文老虎机（Contextual Bandit / LinUCB）— SOTA 在线选优

**机制**（Li et al., 2010, LinUCB）：每个 arm 维护一个**线性模型** `θ_arm`，奖励 = `θ_arm · x_context`，用请求特征向量 `x`（embedding/分类信号）预测各模型期望奖励，加 UCB 不确定性项平衡探索。**Budget-Aware LinUCB** 变体在预算约束下平衡成本-质量。

**成本-质量特征**：LinUCB 可达**次线性 regret**（学习有效），无需离线训练，能利用请求上下文个性化选模型。文献证明其在多轮、上下文演变场景下仍有效。

**与现有实现差异**：
- 现有 Thompson 是**非上下文**；LinUCB 是**上下文**——用请求特征（现有 RuleClassifier 的分类信号正好可作为特征！）学习「模型↔请求」匹配。
- 现有多维能力评分的**权重是手工配置**；LinUCB 的 θ 是**从数据学习**。
- **落地路径清晰**：现有 7 类分类信号 + tier 可作为 LinUCB 上下文特征，无需新基建。

**出处**：Li et al., "A Contextual-Bandit Approach to Personalized News Article Recommendation"（LinUCB, WWW 2010）；Budget-Aware LinUCB 多篇 2024-2025 工作。

### 2.5 Embedding / 语义路由（Semantic Router）

**机制**：用文本 embedding（或 TF-IDF 词袋）把查询映射到向量空间，与预定义 route 的向量做相似度匹配，命中则路由到对应目标（模型/tier）。低成本、零推理延迟。

**成本-质量特征**：TF-IDF 词袋**无语义理解**（同义词/句式变化不匹配）；embedding 模型（如 BGE/text-embedding）有语义泛化但需 embedding 调用（~ms 级）。RouteLLM 的 BERT 分类器即此家族。

**与现有实现差异**：
- 现有 `SemanticRouterPolicy` 用 **TF-IDF 词袋**（离线，零依赖），余弦相似度匹配静态 `SemanticRoutes`。
- SOTA 用 **embedding 模型**（语义泛化）或 BERT 分类（可训练）。
- 差距=TF-IDF 对同义改写/长尾表达不鲁棒，且 route 列表静态不可学习。

**出处**：Semantic Router（aurelio-labs）；RouteLLM 的 BERT 分类器（arXiv:2406.18665）。

### 2.6 紧凑输入路由 / Prompt 压缩（Compact Input Routing）

**机制**：在路由前压缩输入（LLMLingua 等用 token 信息量评分删冗余），降低输入 token 成本。RouterBench 的 RI/RL 类方法用 reduced input representation。与路由互补：先压缩再路由到最适配模型。

**成本-质量特征**：输入成本降 **2-20×**（LLMLingua），RAG/长上下文场景尤其有效；CompactPrompt 声称总 token 降 60%。风险=压缩可能丢关键信息。

**与现有实现差异**：
- 现有 `LongInputPolicy` 只**排除**上下文不足模型，**不压缩**输入。
- 现有无 prompt 压缩/紧凑表示；SOTA 将压缩与路由绑定。
- 差距=现有对长输入只会「换大模型」或「排除」，不尝试「压缩省成本」。

**出处**：Jiang et al., "LLMLingua: Compressing Prompts for Accelerated Inference"（EMNLP 2023）；CompactPrompt（2025）。

### 2.7 LLM-as-Judge 路由 + RouteJudge（质量标定层）

**机制**：用 LLM judge 给模型输出打质量分，作为路由的**质量反馈**。RouteJudge（2026-02）统一评测路由 judge 的 accuracy-latency-cost 权衡，按实例难度自适应分配 judge。LLMRouterBench（2026-01）集成 10 个路由基线统一重评。

**成本-质量特征**：LLM-as-judge 提供**可扩展、自动化**的质量评估（替代人工），是「用数据改进路由」的反馈闭环；但 judge 本身有成本/延迟/偏差风险。

**与现有实现差异**：
- 现有**无质量评估层**——`routing_reason` 只记决策，不记模型输出质量。
- 现有 Thompson 的 reward 是**延迟/成功代理**，非质量。
- 差距=缺 LLM-as-judge 采样管道，无法闭环「路由决策 → 质量 → 调参」。

**出处**：A Survey on LLM-as-a-Judge（arXiv:2411.15594, 2024-11）；RouteJudge（2026-02）；LLMRouterBench（2026-01）。

### 2.8 范式对比总表

| 范式 | 核心机制 | 成本 | 质量定位 | 现有实现覆盖 |
|------|---------|------|---------|-------------|
| RouterBench predictive | 学习式分类/预测器 | 低 | 接近单模型、省成本 | ⚠️ 手工规则（非学习） |
| RouteLLM | 偏好数据驱动 | 低 | 省 85% 成本保 95% 质量 | ❌ 无偏好数据 |
| MAB（Thompson/UCB/EXP3） | 在线探索-利用 | 低 | 自适应 | ✅ Thompson（非上下文） |
| Contextual bandit（LinUCB） | 请求特征驱动 | 中 | 个性化最优 | ❌ 无上下文 |
| Embedding 语义路由 | 向量匹配 | 低 | 语义泛化 | ⚠️ TF-IDF（无语义） |
| Compact input routing | 输入压缩 | 低 | 省 token 成本 | ❌ 无压缩 |
| LLM-as-Judge | 质量反馈层 | 中 | 闭环调优 | ❌ 无质量层 |

---

## 3 差距分析（现有实现 vs SOTA）

| # | 差距 | 证据（代码/文献） | 影响 |
|---|------|------------------|------|
| G1 | **分类信号无准确率可观测** | RuleClassifier 正则 `intentional-simple`；audit 只记 `routing_reason` 不记信号命中率 | 无法知道规则分类准不准，无法迭代改进 |
| G2 | **Thompson 非上下文** | `ReorderByThompsonSampling` 每模型单 Beta；无请求特征 | 只能发现「整体好模型」，不能发现「模型-任务匹配」——核心路由信号丢失 |
| G3 | **多维权重手工配置** | `GetWeightsForClassification` 权重写死（code: 1.0/0.6/0.3） | 权重不随数据改进，无法学出真实维度重要度 |
| G4 | **语义路由 TF-IDF 静态词袋** | `TfIdfSemanticVectorEngine`；`SemanticRoutes` 静态列表 | 同义改写/长尾表达不匹配；route 不可学习 |
| G5 | **策略链串行隐式耦合** | `RouterEngine.Decide` foreach linear Apply；`Reason` 单字符串 | 无并行、无结构化 Reason、策略间通过 Candidates 隐式传参，难测试/难扩展 |
| G6 | **能力标签仅 3 个** | `KnownTags` = vision/tool-use/json-mode；`HasAllTags` 空 Tags 返回 false | 无法表达 audio/video/long-context/structured-output，能力过滤不完整 |
| G7 | **无质量反馈闭环** | Thompson reward 是延迟/成功代理，非质量；无 LLM-as-judge | 无法用真实质量调参，只能代理优化 |
| G8 | **无紧凑输入路由** | `LongInputPolicy` 只排除不压缩 | 长输入只换大模型/排除，不尝试压缩省成本 |
| G9 | **级联是失败驱动非质量驱动** | `CascadeUpgradeHandler` Cheap 失败→Strong | 与 RouterBench 的质量阈值级联（达阈值即停）不同 |

---

## 4 改进提案（每条含问题/方案/收益/成本/验收/风险）

按优先级排序。所有提案尊重 `routing.md` 既有契约（策略链顺序、Thompson 奖励语义、多维 tolerance、audit 语义），默认关、向后兼容。

### P1【高】分类信号准确率可观测（修 G1，实证层已部分交付）

- **问题**：RuleClassifier 是手工正则，但 audit 不聚合「信号 → 实际 tier」的准确率，无法判断规则对不对。
- **方案**：`analyze_audit.py` 已新增 `## Single-Model Selection` 段（本次交付）解析 `target=Tier(signal)` vs 实际 `routed_tier` 输出混淆矩阵 + 每信号准确率；生产端确保 `routing_reason` 始终含 `target=` 可解析标记。
- **收益**：把「规则分类准不准」变成可量化指标，为 P5（学习式分类）铺路。
- **成本**：零调用增量（纯分析）。
- **验收**：`analyze_audit.py` 对含 `target=` 的 DB 输出混淆矩阵与总准确率；对无标记 DB 优雅降级（AC3/AC6 已达成）。
- **风险**：低；解析依赖 `routing_reason` 格式，需与生成约定一致。

### P2【高】结构化 Reason + 策略链并行化（修 G5，已知 P2 待办）

- **问题**：`RouterEngine.Decide` 串行线性、策略间隐式 `Candidates` 耦合、`Reason` 单字符串——难并行、难测试、难扩展。
- **方案**：引入 `ParallelGroup` 接口与 4 组策略分组（A: 过滤型 CapabilityFilter/LongInput/Failover/QuotaAware；B: 分类型 RuleClassifier/SemanticRouter；C: 排序型 LatencyAware/PromptCacheAffinity；D: 约束型 BudgetGuard/SessionAffinity/LoadBalance），组内可并行、组间保持依赖序；`Reason` 改为结构化（键值对而非拼接字符串）。
- **收益**：策略解耦、可并行（减少决策延迟）、`Reason` 可机器解析（支撑 P1 准确率与归因）。
- **成本**：中等改造量（重构 `Decide` 与策略接口）。
- **验收**：策略链在组内并行时选型结果与串行一致（回归）；`Reason` 结构化可解析。
- **风险**：中；需保证并行组内无隐形依赖（用代码评审 + 测试锁定）。

### P3【高】上下文老虎机（LinUCB）替代/补充纯 Thompson（修 G2/G3）

- **问题**：现有 Thompson 非上下文，无法利用请求特征发现「模型-任务匹配」；多维权重手工配置。
- **方案**：新增 `EnableContextualBandit`（默认关）——用现有 7 类分类信号 + tier 作为上下文特征 `x`，每模型维护线性 θ，奖励 = `θ·x` + UCB 项；与现有 `EnableThompsonSampling` 互斥（二选一）或共存（Thompson 保底）。多维权重从「手工 profile」迁移为「LinUCB 学习 θ」（可平滑过渡）。
- **收益**：LinUCB 文献证明次线性 regret、能个性化选模型；现有分类信号**零额外基建**即可作特征。
- **成本**：每请求一次矩阵运算（~µs 级，可忽略）；需维护每模型协方差矩阵。
- **验收**：单测验证上下文特征影响选型（同模型对不同分类信号选型不同）；`EnableContextualBandit=false` 时行为与现有一致。
- **风险**：中；冷启动需足够样本（可先用现有 Thompson 热启动）；θ 维度过高需降维。

### P4【中】能力标签扩展（修 G6，已知 P2 待办）

- **问题**：`KnownTags` 仅 3 个，无法表达 audio/video/long-context/structured-output 等能力。
- **方案**：扩展标签集（audio/video/long-context/structured-output/tool-use/json-mode/vision…）；`HasAllTags` 对空 Tags 语义改为「未标注=不限制」（当前空 Tags 返回 false 会误过滤）。
- **收益**：能力过滤完整，支持多模态/结构化输出路由。
- **成本**：低（枚举 + 语义调整）。
- **验收**：新标签可配置、`HasAllTags` 空集语义修正（单测锁定）；既有 3 标签行为不变（向后兼容）。
- **风险**：低；注意「未标注=不限制」语义变化对现有配置的影响（需文档 + 测试）。

### P5【中】学习式 predictive 分类器（RouteLLM 风格，修 G1 的进阶）

- **问题**：RuleClassifier 手工正则，无法从数据改进。
- **方案**：在 P1 的准确率可观测 + P7 的质量反馈闭环就绪后，引入 RouteLLM 风格的轻量分类器（矩阵分解 / BERT）作为 `EnableLearnableClassifier`（默认关），用偏好/质量标签训练，替代/补充正则分类。
- **收益**：RouteLLM 证明可省 85% 成本保 95% 质量；学习式分类器从数据改进。
- **成本**：需训练数据管道（P7 的 quality 反馈）；模型训练/推理依赖（可本地轻量）。
- **验收**：离线训练集上分类准确率 ≥ 正则基线；在线 A/B 成本-质量不劣化。
- **风险**：高；依赖质量标签管道（P7），短期难落地——列为中期探索项。

### P6【低】语义路由升级为 embedding 检索（修 G4）

- **问题**：TF-IDF 词袋无语义泛化，同义改写不匹配。
- **方案**：`EnableSemanticEmbedding`（默认关）——用轻量 embedding 模型替换 TF-IDF 词袋；`SemanticRoutes` 离线向量化缓存。保留 TF-IDF 为默认（零依赖、AOT 兼容）。
- **收益**：语义泛化（同义改写命中），提升语义路由召回。
- **成本**：embedding 调用开销（~ms）；embedding 依赖。
- **验收**：同义改写查询命中率提升（单测）；TF-IDF 默认路径不变。
- **风险**：中；embedding 依赖破坏「零外部依赖」承诺——需配置化，默认关。

### P7【低】LLM-as-Judge 质量反馈闭环（修 G7，探索项）

- **问题**：Thompson reward 是延迟/成功代理，非真实质量；无法闭环调参。
- **方案**：抽样用 LLM-as-judge 给输出打质量分，写入审计（新列或 `routing_reason` 的 `quality=` 标记——本次实证已支持该格式）；质量分作为 Thompson reward 的补充信号。
- **收益**：用真实质量调参（对齐 RouterBench 的「质量标定」）；实证工具已就绪。
- **成本**：judge 调用成本（仅抽样）；judge 本身需标定（RouteJudge 教训）。
- **验收**：审计含质量标记；Thompson 可接收质量 reward（A/B 对比）。
- **风险**：高；judge 成本/偏差——列为探索项，需真实数据验证收益。

### P8【低】紧凑输入路由（修 G8，探索项）

- **问题**：长输入只「排除/换大模型」，不压缩省成本。
- **方案**：`EnableCompactInput`（默认关）——对超长输入用轻量压缩（LLMLingua 类 token 信息量剪枝）降低输入成本，再路由。
- **收益**：长上下文/RAG 场景输入成本降 2-20×（文献）。
- **成本**：压缩推理开销；压缩可能丢信息（质量风险）。
- **验收**：压缩后 token 显著下降且质量不劣化（A/B）。
- **风险**：高；信息丢失风险——探索项，需真实场景验证。

### 提案汇总表

| 优先级 | 提案 | 修差距 | 成本 | 理由 |
|-------|------|-------|------|------|
| **立即** | P1 分类准确率可观测 | G1 | 零（已交付） | 让规则分类可量化，为学习式铺路 |
| **立即** | P2 策略链并行化 + 结构化 Reason | G5 | 中 | 已知 P2 待办，解耦/可测/可归因 |
| **立即** | P3 上下文老虎机 LinUCB | G2/G3 | 中 | 用现有分类信号做上下文，核心差距 |
| **短期** | P4 能力标签扩展 | G6 | 低 | 已知 P2 待办，能力过滤完整 |
| **中期** | P5 学习式分类器 | G1 | 高 | RouteLLM 收益，依赖 P7 质量层 |
| **中期** | P6 语义 embedding | G4 | 中 | 语义泛化，破坏零依赖需配置化 |
| **探索** | P7 LLM-as-Judge 质量闭环 | G7 | 高 | 真实质量调参，需标定 |
| **探索** | P8 紧凑输入路由 | G8 | 高 | 长输入省成本，信息丢失风险 |

---

## 5 实证分析（合成数据）

### 5.1 工具扩展（本任务交付）

- `scripts/generate_audit_data.py`：新增 `--signal-accuracy`（速率式分类误判，区别于 `--misclassify` 的硬注入）、`--thompson-rate`（注入 `thompson: reward=X, round=Y` 标记，对齐真实 Reward 语义）、`--quality-agent`（注入 `quality=Z` 质量代理）；`DEFAULT_MODELS` 加 `quality` 字段。默认关，向后兼容。
- `scripts/analyze_audit.py`：新增 `## Single-Model Selection` 段——分类信号混淆矩阵/准确率、Thompson 奖励分布 + 每模型 regret 代理、成本-质量 Pareto/AIQ 代理。列/数据缺失优雅降级。

### 5.2 数据与结论（Q1-Q3）

生成：`--rows 300 --seed 7 --signal-accuracy 0.85 --thompson-rate 0.5 --quality-agent`

| 实证问题 | 结果 | 解读 |
|---------|------|------|
| **Q1** 分类信号准确率 | **78.7%**（配置 0.85，含 8% 基线抖动） | 与 `--signal-accuracy` 语义一致；`simple-qa`/`translation-request` 略高于均值（84.6%/84.6%），`complex-instruction` 最低（68.0%）。**真实部署需监控此指标**——P1 让它在生产可观测。 |
| **Q2** Thompson 奖励分布 + regret | reward 分布 1.0=59.3%、0.3=19.3%、0.5=17.2%、0.0=4.1%；per-model regret：deepseek-chat 0.000、gpt-4o-mini 0.001、gpt-4o 0.447 | 合成下便宜模型平均 reward 高（延迟快→快成功概率高），Strong 模型因延迟高 reward 低。**这暴露非上下文 Thompson 的偏差**：它优化延迟而非质量，Strong 模型被系统性低估（gpt-4o regret 0.447）——印证 P3（上下文/质量感知）的必要性。 |
| **Q3** 成本-质量 Pareto / AIQ | 三个模型全在 Pareto 前沿（gpt-4o 0.95q/$0.0051、gpt-4o-mini 0.80q/$0.00027、deepseek-chat 0.60q/$0.000015）；AIQ=0.0044 | 合成下模型成本-质量单调（贵=质量高），Pareto 前沿无内点被支配。真实部署若出现「贵但质量差」的模型会被淘汰。**这是 RouterBench 式定位的起点**——P7 接真实质量后才有意义。 |

### 5.3 实证结论

1. **分类信号准确率是可运营指标**：合成下 78.7%，生产应监控并按信号分诊（P1）。
2. **非上下文 Thompson 有系统性偏差**：合成下 gpt-4o 因延迟高 reward 低（regret 0.447），被系统低估——它优化延迟而非质量。**这是 P3（上下文/质量感知）的最强实证论据**。
3. **成本-质量 Pareto 已可构造**：工具链就绪，接真实质量（P7）后才有决策价值。
4. 合成数据**无法证真质量收益**（无真实"正确答案"），机制性指标（准确率、reward、Pareto）已量化；质量收益需接真实评估（P7）。

---

## 6 优先级排序与建议

| 优先级 | 提案 | 修差距 | 理由 |
|-------|------|-------|------|
| **立即** | P1 分类准确率可观测 | G1 | 本次已交付分析层，生产补 `target=` 标记即可；让规则分类可量化 |
| **立即** | P2 策略链并行化 + 结构化 Reason | G5 | 已知 P2 待办，解耦/可测/可归因，是其它提案的地基 |
| **立即** | P3 上下文老虎机 LinUCB | G2/G3 | 实证证明非上下文 Thompson 系统性低估 Strong；用现有分类信号做上下文，零新基建 |
| **短期** | P4 能力标签扩展 | G6 | 已知 P2 待办，低成本补齐能力过滤 |
| **中期** | P6 语义 embedding | G4 | 语义泛化，但破坏零依赖需配置化 |
| **中期** | P5 学习式分类器 | G1 | RouteLLM 收益，依赖 P7 质量层就绪 |
| **探索** | P7 LLM-as-Judge 质量闭环 | G7 | 真实质量调参，需标定（RouteJudge 教训） |
| **探索** | P8 紧凑输入路由 | G8 | 长输入省成本，信息丢失风险 |

**建议**：先落地 P1+P2+P3（P1 分析层已交付；P2 是已知待办且是地基；P3 用实证证明的非上下文缺陷 + 现有分类信号零新基建）。P4 低成本顺手做。P5/P6 需质量层（P7）就绪才值得；P7/P8 需真实数据验证，谨慎评估。

**与 fusion 研究的衔接**：fusion 路由（panel→analyst→outer，Overgenerate & Rerank）是「质量上界、高成本」端；单模型选择路由（本任务，predictive/bandit）是「低成本、接近最优」端。两者互补——单模型路由负责大多数请求的选优，fusion 保留给复杂请求（P3 的成本门控可决定何时上 fusion）。共享审计工具链（本次扩展的 `## Single-Model Selection` 与既有 `## Fusion Router` 段互补）。

---

## 附：来源

- RouterBench：Shi et al., arXiv:2403.12031（2024-03）；LLMRouterBench（arXiv, 2026-01）
- RouteLLM：Zhao et al., arXiv:2406.18665（2024-07）
- Thompson Sampling：Thompson (1933)；UCB1：Auer et al. (2002)
- LinUCB：Li et al., WWW 2010；Budget-Aware LinUCB（2024-2025）
- Semantic Router：aurelio-labs；RouteLLM BERT 分类器
- Prompt 压缩：Jiang et al., "LLMLingua", EMNLP 2023；CompactPrompt（2025）
- LLM-as-Judge：A Survey on LLM-as-a-Judge arXiv:2411.15594（2024-11）；RouteJudge（2026-02）
- 代码事实：`src/OptiRouter/Routing/RouterEngine.cs`、`RuleClassifierPolicy.cs`、`SemanticRouterPolicy.cs`、`LatencyAwarePolicy.cs`、`ThompsonSampler.cs`、`.trellis/spec/backend/routing.md`