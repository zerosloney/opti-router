# 强化多维能力评分（capability-scoring）

Child of: 08-10-single-model-routing

## Goal

修复多维能力路由的根因缺陷：`GetEffectiveCapability` 对**所有**能力维度回退到同一 tier 值，导致语言类任务（simple-qa/translation）下 Strong 模型永远压过 Cheap，违背「能力足够时择廉」的设计目标。目标：按维度区分 tier 回退语义，让 cheap 在廉价维度（语言）可胜、strong 在昂贵维度（推理/代码）保优。

## Background（已核实）

- 评分路径：`RuleClassifierPolicy.Apply` → `CalculateMatchScore(model, weights)` = `Σ weight_i × model.GetEffectiveCapability(dimension_i)`。
- `ModelEndpointOptions.GetEffectiveCapability`（ModelEndpointOptions.cs:103-115）：`Capabilities` dict 有值则取其值；否则**所有维度**回退 `Tier switch`（Strong 0.9 / Medium 0.6 / Cheap 0.3）。
- 权重 profile（`RuleClassifierPolicy.GetWeightsForClassification`）：simple-qa `language=1.0, reasoning=0.1`；translation `language=1.0, coding=0.1`；code-complex `coding=1.0, reasoning=0.8`；math `reasoning=1.0` 等。
- 排序：`floor(score / 0.15)` 桶降序 → 桶内价格升序（`CapabilityScoreTolerance = 0.15`）。
- 现有测试：`MultiDimensionalAndBanditTests`（match score 排序、close-scores 价优、gap 能力优）。

### 缺陷量化

simple-qa（`language=1.0`）：Strong=1.0×0.9=0.9，Cheap=1.0×0.3=0.3。桶：floor(0.9/0.15)=6，floor(0.3/0.15)=2。Strong 桶远高 → Cheap 永不选中。语言是廉价维度，档距应小（Strong 0.8 / Medium 0.7 / Cheap 0.6），使 0.8 vs 0.6 落入同桶（floor(0.8/0.15)=5，floor(0.6/0.15)=4——仍差一桶）。需调容差或档距使廉价维度可同桶。

## Requirements

- R1.1 维度区分：引入**按维度的 tier 回退表**——语言（廉价维度）档距小，推理/代码（昂贵维度）档距大。`Strong/Medium/Cheap` 在不同维度有不同回退值。
- R1.2 显式 `Capabilities` dict 仍优先；仅未配置维度走维度化回退。
- R1.3 保持 `EnableMultiDimensionalRouting=false` 默认关闭；开启后行为受维度化回退影响（根治型，接受行为变化）。
- R1.4 现有 match-score 排序/价优/择优测试语义保持（close-scores 价优、gap 能力优仍成立），必要时更新固化新档距的断言。
- R1.5 权重 profile 与维度名（coding/reasoning/language）不变，仅回退值维度化。

## Acceptance Criteria

- [ ] simple-qa / translation 请求下，能力足够（语言维度分数同桶）时 Cheap 可在价格上胜出 Strong。
- [ ] 推理/数学/复杂代码请求下，Strong 仍因推理维度分数优势胜出（gap > 容差）。
- [ ] 显式配置 `Capabilities` 的模型不受影响（dict 优先）。
- [ ] 新测试锁定：维度化回退、simple-qa 择廉、推理保优、显式能力优先。
- [ ] 现有 `MultiDimensionalAndBanditTests` 全绿（或按新语义更新断言并说明）。

## Out of Scope

- 新增能力维度名（只改现有 coding/reasoning/language 的三维回退值）。
- 改变权重 profile 本身。
- 非多维路径（tier 过滤）行为。

## Open Questions

- 回退表具体数值与容差是否需联动调整，使廉价维度同桶——设计阶段定夺（见 design.md）。