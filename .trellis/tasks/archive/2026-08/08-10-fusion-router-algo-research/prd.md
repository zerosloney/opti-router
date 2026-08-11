# 融合路由（Fusion Router）深层算法研究 + 实证分析

## Goal

对 OptiRouter 现有融合路由（`EnableFusionRouter`，panel→analyst→outer 的 Mixture-of-Agents 风格 quality router）做两层研究：

1. **算法综述 + 改进提案**：对照融合路由/多智能体合成分支的最新算法（OpenRouter Fusion Router、Mixture-of-Agents、LLM 路由、self-consistency、multi-agent debate 等），找出现有实现与 SOTA 的差距，产出**可落地、可验证**的算法改进提案。
2. **实证分析现有实现**：用审计数据（真实或合成）量化现有 panel/analyst/outer 的实际收益与成本，验证现有实现的假设（融合真的提升质量吗？成本是否失控？panel 多样性是否生效？analyst 解析失败率多高？），并补上分析工具链缺口。

产出物是一份**研究报告**（markdown），含综述、差距分析、改进提案（每个提案带验收标准）、实证数据与结论。研究结论用于指导后续实现，但**本任务不做代码实现**——除非提案被采纳后另立实现任务。

## Confirmed Facts（代码勘察结论）

### 现有实现（`src/OptiRouter/Endpoints/FusionRouter.cs`）
- 触发条件：`EnableFusionRouter=true`、非流式、`failedInThisRequest.Count==0`、候选 ≥2、failover 开启。单请求最多尝试一次。
- **Panel**：`FusionPanelSelector.Select` 选前 N（`FusionRouterPanelSize`，[2,5]，默认 3）个候选；`EnableDynamicFusionPanelSize` 按 `RequestComplexity`（Simple→min / Standard→min+1 / Complex→max）动态定 size；`EnableFusionDiversity` 软优先不同 Provider/Family（保留首候选，只对主候选已知维度加分，平则保序）。每个 panel 独立 CTS（`FusionRouterPanelTimeoutSeconds`，默认 0=不启用），`Task.WhenAll` 等全部。拿到探测许可的病号进 `admitted`，`admitted.Count<2` 则放弃。
- **Analyst**：`FusionSynthesis.BuildAnalystRequest` 构造 `[user 原问题, user 分析指令(内嵌全部 panel 回答)]`；`ParseAnalysis` 解析 JSON（容错：围栏剥离、损坏返回 null）。默认 `FusionRouterAnalystModel`=主候选。失败/解析失败 → 回退串行。
- **Outer**：`BuildOuterRequest` 保留完整消息链 + 追加 user 分析摘要；`FusionRouterOuterModel` 默认主候选；`FusionRouterMaxOutputTokens` 默认 16000。
- **成本/审计/健康**：N panel + 1 analyst + 1 outer 全部按真实/预估成本入账（panel 失败按 `EstimateInputCost` 预估）；审计每条记录 `FusionRole`=panel/analyst/outer + 共享 `ParallelGroupId`；panel 各占半开探测槽后 `RecordSuccess/ReleaseProbe` 结算，analyst/outer 直接调用不占槽。`FusionRouterTemperature` 默认 0（采样确定性），原请求显式 temperature 优先。
- 降级链（`ProxyOrchestrator`）：Fusion Router 优先 → 失败后 Fusion-lite（`EnableFusionMode` 并行 race）→ 否则串行降级链。

### 分析工具链缺口
- `scripts/analyze_audit.py`：**不聚合 `FusionRole`**，报告无 panel/analyst/outer 维度（成本/延迟/成功率分开）。
- `scripts/generate_audit_data.py`：**不生成 fusion 行**（无 panel/analyst/outer 合成样本）。
- `/metrics`（`EnableMetrics`）：无 fusion 专用指标。

### 现有 spec 文档
- `.trellis/spec/backend/routing.md` 已含融合路由的 panel 超时决策记录、契约、测试矩阵——研究需与之保持一致，改进提案不得破坏既有契约。

## Requirements

### R1 算法综述
- 覆盖融合路由/多智能体合成分支的代表性范式：OpenRouter Fusion Router、Mixture-of-Agents（Together AI）、LLM 路由（RouterBench 类）、self-consistency / multi-agent debate、ensemble & aggregation。
- 每个范式给出：核心机制、成本-质量特征、适用场景、与现有实现的关键差异点。
- 综述要有出处（论文/官方文档链接），标注哪些已落在现有实现、哪些是 SOTA 差距。

### R2 差距分析 + 改进提案
- 逐条对比现有实现与 SOTA，识别可落地差距。
- 每条改进提案必须包含：**问题陈述、方案、预期收益、成本增量、验收标准（可测试）、风险/回滚**。
- 提案必须尊重现有契约（`routing.md` 的 panel 超时、diversity、动态 size、audit 语义），不得破坏向后兼容。

### R3 实证分析
- 扩展 `scripts/generate_audit_data.py`，支持生成带 `FusionRole`/`ParallelGroupId` 的 fusion 样本（panel/analyst/outer，含成本、延迟、成功/失败、analyst 解析失败）。
- 扩展 `scripts/analyze_audit.py`，新增 Fusion 维度报告：按 `FusionRole` 聚合成本/延迟/成功率；panel 多样性是否生效（不同 Provider/Family 命中）；analyst 解析失败率；outer 采纳后的端到端成本 vs 单模型基线。
- 用合成数据跑通闭环，产出实证报告，回答：融合是否带来质量收益、成本是否×N 失控、panel 多样性是否真的提升信息增益。

### R4 研究报告
- 综合综述 + 实证，形成一份结构化研究报告 `research/fusion-router-algo-research.md`（或类似路径），含结论、优先级排序的改进提案清单、实证数据表。

## Acceptance Criteria

- [ ] **AC1**：`research/` 下存在完整综述章节，覆盖 R1 列出的 ≥5 个代表性范式，每个都有出处与"与现有实现的差异"。
- [ ] **AC2**：改进提案清单 ≥5 条，每条含问题/方案/收益/成本/验收/风险，且与 `routing.md` 现有契约一致。
- [ ] **AC3**：`generate_audit_data.py` 能生成含 `FusionRole`+`ParallelGroupId` 的合成数据（`--fusion-rate` 参数），`analyze_audit.py` 报告新增 Fusion 维度聚合（panel/analyst/outer 成本、延迟、成功率、analyst 解析失败率）。
- [ ] **AC4**：用合成数据跑通 `generate → analyze` 闭环，报告能回答"融合 vs 单模型基线"的成本-质量对比（合成数据前提下）。
- [ ] **AC5**：研究报告结论明确：哪些改进提案值得进入实现阶段（附优先级），哪些是收益不明的探索项。
- [ ] **AC6**：`analyze_audit.py` 对无 fusion 数据的 DB 仍可正常运行（向后兼容，不崩）。

## Out of Scope

- 不实现任何融合路由算法改进（本任务只研究 + 提案）。
- 不改动 `FusionRouter.cs` / `FusionSynthesis.cs` / `FusionPanelSelector.cs` 的生产代码。
- 不接入真实付费上游跑真实流量（实证基于合成数据）。
- 不重写 `routing.md` spec 的既有融合决策记录（只新增研究成果，不破坏既有内容）。

## Open Questions

- 无（研究范围已由用户确认：综述+改进方案 与 实证分析 两者都要）。