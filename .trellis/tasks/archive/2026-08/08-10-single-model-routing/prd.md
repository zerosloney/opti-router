# 强化单模型智能选择路由

## Goal

强化核心「单模型智能选择」路径——即从候选链中为每个请求挑选**唯一**最终模型的能力。范围覆盖四块既有能力：多维能力评分、Thompson 学习路由、延迟感知重排、规则分级代码意图细分。目标是在不破坏策略链叠加语义、不引入新依赖的前提下，让选择更「聪明」：能力/价格权衡更合理、学习信号更丰富、延迟决策更稳健、规则分级更准。

## Background（代码现状，已核实）

单模型选择路径 = `RouterEngine.Decide` 驱动的策略链，最终 `RouterDecision.Primary` 即所选模型。本次涉及四块（均默认关闭，不影响现有默认行为）：

### 1. 多维能力评分（`RuleClassifierPolicy` + `ModelEndpointOptions.GetEffectiveCapability`）
- 评分 = `Σ weight_i × model.GetEffectiveCapability(dimension_i)`（`RuleClassifierPolicy.CalculateMatchScore`）。
- **缺陷**：`GetEffectiveCapability`（ModelEndpointOptions.cs:103-115）对**所有维度**回退到同一 tier 值（Strong 0.9 / Medium 0.6 / Cheap 0.3）。纯语言任务（simple-qa，weight language=1.0）下 Strong=0.9 vs Cheap=0.3，差距 0.6 ≫ 容差 0.15 → Cheap 永远赢不了，违背「能力足够时择廉」的设计目标。
- 现有测试：`MultiDimensionalAndBanditTests`（match score 排序、close-scores 价优、gap 能力优）。

### 2. Thompson 学习路由（`ThompsonSampler` + `ThompsonStateStore` + `LatencyAwarePolicy.ReorderByThompsonSampling` + `OutcomeRecorder.RecordThompsonOutcome`）
- **缺陷**：反馈是**二值**（`isGood` = 成功且延迟 < `ThompsonLatencyTargetMs`）。100ms 与 790ms（target 800）的成功记作相同；无延迟幅度、无成本/质量信号。
- 现有测试：`MultiDimensionalAndBanditTests`（Beta 采样、折扣钳制、LatencyAware+Thompson 重排）。

### 3. 延迟感知重排（`LatencyAwarePolicy` + `LatencyStatsAggregatorService` + `ModelLatencyStats`）
- **缺陷**：分数 = `1/(avg+50)`，只用平均值；无 p95/p99 尾部延迟项；`ModelLatencyStats` 仅存 avg + count。
- 现有测试：`LatencyAwarePolicyTests`。

### 4. 规则分级代码意图细分（`RuleClassifierPolicy.ClassifyCodeIntent`）
- 刚落地（commit 0615055/070b6aa）：复杂 > 简单 > 默认 Strong；意图检测只跑指令文本（剔除 fenced code）。
- 既有防线：三大 gotcha（不跑全量文本、不用英文裸名词、explain 归 Strong）。

## Requirements

### R1 多维能力评分（child: capability-scoring）
- R1.1 维度区分：不同能力维度应有不同的 tier 回退语义（语言是「廉价维度」档距小，推理/代码是「昂贵维度」档距大），让 cheap 在语言任务上可胜、strong 在推理任务上保优。
- R1.2 保持默认关闭（`EnableMultiDimensionalRouting=false`），改动不改变现有默认行为。
- R1.3 现有 match-score 排序/择廉/择优测试保持通过，语义不变。

### R2 Thompson 学习路由（child: thompson-routing）
- R2.1 丰富奖励信号：从二值 `isGood` 升级为带延迟幅度的连续奖励（或分级奖励），区分「快成功」「慢成功」「失败」。
- R2.2 保持 `EnableThompsonSampling` 默认关闭，`ThompsonDiscountFactor`/`ThompsonLatencyTargetMs` 语义兼容。
- R2.3 反馈链（`OutcomeRecorder.RecordThompsonOutcome` → `ThompsonStateStore.RecordOutcome`）契约保持，现有测试不破坏。

### R3 延迟感知重排（child: latency-aware）
- R3.1 在评分中加入尾部延迟（p95/p99）项，避免仅看平均导致「avg 好但 tail 差」的模型被选中。
- R3.2 保持 `EnableLatencyAware` 默认关闭；`LatencyMinSamples`/`LatencyStatsWindowMinutes` 语义兼容。
- R3.3 冷启动透传、样本不足尾部保留的既有行为不变。

### R4 规则分级代码意图细分（child: rule-classifier）
- R4.1 以离线审计（`scripts/analyze_audit.py` 的 By Routing Reason Signal）为实证依据：补全其信号关键词表以覆盖新增子分类（code-complex/code-simple/math-detected/translation-request），形成可监控的调优闭环；仅当发现可低成本验证的精度缺口时才细化判别，且必须有测试锁定。
- R4.2 不破坏三大 gotcha 防线；新增/修改的判别必须有测试锁定。

## Acceptance Criteria

- [ ] 四个 child 各自独立可验收（测试通过 + 行为符合各自 PRD）。
- [ ] 所有改动默认关闭，`dotnet build` 通过，现有测试套件全绿。
- [ ] 每个 child 的非平凡逻辑有可运行测试锁定（行为/边界/回归）。
- [ ] 无新依赖；无跨模块公共 API 破坏（`IRouterPolicy`/`RouterDecision`/`RouterContext` 签名不变）。
- [ ] 更新 `.trellis/spec/backend/routing.md` 记录新语义与契约。

## Out of Scope

- 融合路由（FusionRouter）、并行首试（FusionMode）、级联升级（Cascade）、预算/配额/熔断/能力过滤/会话粘性等**非单模型选择**模块。
- 引入外部分类模型 / 向量数据库 / 新 NuGet 依赖。
- 改变 `RouterDecision`/`RouterContext`/`IRouterPolicy` 公共接口签名。

## Decisions

- 用户已定夺：**根治型**（行为变更，接受默认关闭下开启后的行为变化）。四 child 均按根治型设计。