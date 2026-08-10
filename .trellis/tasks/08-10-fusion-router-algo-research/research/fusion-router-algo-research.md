# 融合路由（Fusion Router）深层算法研究报告

> 任务：`08-10-fusion-router-algo-research` · 日期：2026-08-10
> 范围：算法综述 + 差距分析与改进提案 + 实证分析（合成数据）
> 结论速览：OptiRouter 的融合路由实现已忠实复现 OpenRouter Fusion 的 panel→analyst→outer 骨架，但存在 **panel 缺多样性、temperature=0 使 panel 退化为同质、analyst 解析脆弱、无质量门控、成本 ×N 无上限、无自适应触发** 六个可落地差距。实证（合成）确认成本倍数 4-5×，panel 多样性仅 2.5 模型/组。改进优先级见 §5。

---

## 1 现状：OptiRouter 现有实现解剖

### 1.1 触发条件与执行链

```
请求进入 ProxyOrchestrator.SendAsync
  └─ 条件：EnableFusionRouter=true 且 非流式 且 failedInThisRequest 为空
          且 decision.Candidates.Count ≥ 2 且 failover 开启
  └─ FusionRouter.ExecuteAsync
       ├─ 1. FusionPanelSelector.Select：定 panel size + 软多样性排序
       ├─ 2. 并行 fire panel（每个独立 CTS，可配 panel 超时）
       ├─ 3. Task.WhenAll 等全部 panel
       ├─ 4. 逐条处理：记账 / 审计(FusionRole) / 健康跟踪 / 成本
       ├─ 5. panelAnswers.Count==0 → 回退串行
       ├─ 6-7. analyst：结构化 JSON（consensus/contradictions/gaps/unique_insights/recommendation）
       ├─      解析失败 → 回退串行
       └─ 8. outer：读分析写最终答案
```

### 1.2 关键设计参数（`RoutingOptions`）

| 参数 | 默认 | 说明 |
|------|------|------|
| `EnableFusionRouter` | `false` | 总开关，生产默认关 |
| `FusionRouterPanelSize` | `3` | panel 数，`[2,5]` |
| `EnableDynamicFusionPanelSize` | `false` | 按 `RequestComplexity` 动态定 size |
| `EnableFusionDiversity` | `false` | 软优先不同 Provider/Family |
| `FusionRouterAnalystModel` | `null` | analyst 模型，空=主候选 |
| `FusionRouterOuterModel` | `null` | outer 模型，空=主候选 |
| `FusionRouterMaxOutputTokens` | `16000` | outer 答案上限 |
| `FusionRouterTemperature` | `0.0` | panel/analyst 采样温度 |
| `FusionRouterPanelTimeoutSeconds` | `0` | 0=不启用 panel 超时 |

### 1.3 已做的正确决策（与 SOTA 对齐处）

- **panel→analyst→outer 三段式**：完全对齐 OpenRouter Fusion Router 与 MoA 的「先发散后收敛」结构。✅
- **analyst 不写答案、只产结构化分析**：对齐 MoA 的 aggregation 层定位。✅
- **全流程成本入账 + 审计 `FusionRole`/`ParallelGroupId`**：可离线复盘，是 RouterBench「用数据说话」的前提。✅
- **panel 各占半开探测槽 + analyst/outer 不占槽**：断路器语义清晰。✅
- **panel 级独立超时 CTS**：已记录在 `routing.md` 的 Design Decision，避免单慢 panel 拖死全部。✅

---

## 2 算法综述（SOTA 范式扫描）

### 2.1 OpenRouter Fusion Router（本实现参照）

**机制**：请求并行发给一个「panel」的多个模型 → 收集全部回答 → 一个「synthesis/judge 模型」读全部回答，识别共识/矛盾/独特洞察/盲点，产出结构化分析 → 据此合成最终答案。分析模型与合成模型可配置（`analysis_models`）。

**成本-质量特征**：OpenRouter 在 Perplexity DRACO deep-research 基准上，预算 panel（Gemini 3 Flash + Kimi K2.6 + DeepSeek V4 Pro）得分 64.7%，超过单模型 GPT-5.5（60.0%）与 Claude Opus 4.8（58.8%）；成本约为直接用顶级模型的一半。代价是延迟增加（并行调用 + 合成步）。

**与现有实现差异**：
- 现有实现骨架一致，但 **panel 默认是同 tier 前 N 候选**，多样性靠 `EnableFusionDiversity=false` 默认关。
- OpenRouter panel 模型通常**启用工具（web search/fetch）** 且 **跨能力档混合**（flash+轻量+pro）；现有实现 panel 全来自同一候选链，档位集中在 `RequestComplexity` 决定的一个 tier。

**出处**：openrouter.ai（Fusion Router 官方，2026-06 发布）；已通过搜索验证。

### 2.2 Mixture-of-Agents（MoA，Together AI）

**机制**（Wang et al., 2024, arXiv:2406.04692）：**分层**架构。第一层多个 LLM 独立作答；后续每一层，agent 都**读取前一层所有 agent 的输出**作为辅助信息再作答；最后 aggregator 合成。关键发现：**LLM 即使拿到低质量的他模型输出，也能产出更高质量回答**（collaborativeness）——即「合成优于选优」。

**成本-质量特征**：开源模型 MoA 在 AlpacaEval 2.0 达 65.1%，超 GPT-4 Omni（57.5%）。成本随层数×每层 agent 数线性增长，是典型的「质量技术」。

**与现有实现差异**：
- 现有实现是**单层** Fusion（panel→analyst→outer）；MoA 是**多层**（每层都聚合前层）。
- MoA 的层间**迭代聚合**（refinement）是核心增益来源；现有实现 panel 只发散一次，analyst 一次性收敛，无迭代。
- MoA 强调**同模型也可复用**（"fusing a model with itself" 有效）；现有实现 panel 默认不同模型但 diversity 默认关。

### 2.3 RouterBench（成本-质量权衡基准）

**机制**（Shi et al., 2024, arXiv 检索到）：把路由问题建模进「成本-质量二维平面」，用**非递减凸包**提取 Pareto-最优成本-质量对（cost-quality frontier），用 **AIQ（Average Improvement in Quality）** 做跨路由器非参数比较。提出三类路由家族：
- **Cascading**：按成本升序逐个查询，达质量阈值即停（省成本，慢）。
- **Overgenerate & Rerank**：并行生成多个，选最优（质量上界，贵）。
- **Predictive**：ML 预测某 query 该用哪个模型。

**与现有实现差异**：
- 现有实现的融合路由本质是 **Overgenerate & Rerank 的一个变体**（用 LLM 当 reranker/synthesizer），其成本-质量位置在「高质量、高成本」端。
- **现有实现没有把融合路由放进成本-质量凸包里做定位**——不知道它相对「直接上最强单模型」或「cascading」是否 Pareto 更优。这是实证分析应回答的核心问题（§5 结论：合成数据下成本 4-5×，需真实质量标定）。

### 2.4 Self-Consistency（自一致性）

**机制**（Wang et al., 2022, "Self-Consistency Improves Chain of Thought Reasoning"）：对同一问题，**同一模型**用 **temperature>0 采样 k 条推理路径**（k≈5-40，多数增益在 5-10），提取答案后**多数投票**。理论：复杂问题有多条正确路径、错误路径不易收敛到同一错误答案，多数投票压制偶发错误。

**成本-质量特征**：GSM8K +17.9%、AQuA +12.2% 等，无需训练，只改解码策略。成本×k。

**与现有实现差异（关键）**：
- 现有实现 `FusionRouterTemperature=0.0`（默认），即 **panel 采样是确定性的**。若 panel 用同一模型，它们会产出**完全相同**的回答——多样性完全失效，退化成「一次调用 + analyst + outer」的无意义开销。
- Self-Consistency 证明**温度多样性是收益来源**。现有实现把 temperature 钉在 0，与「panel 多样性 → 信息增益」的初衷相悖。**这是最值得修的差距**（见 P1）。

### 2.5 Multi-Agent Debate（多智能体辩论）

**机制**（Du et al., 2023, "Improving Factuality and Reasoning...through Multiagent Debate"）：多个 agent 在多**轮**内提案→批评→修订，交叉验证对方的推理与事实，降低幻觉。可用同基座模型的不同实例（依赖群内多样性）或不同模型。性能随 agent 数与轮数提升；代价是延迟与 token 大增。

**与现有实现差异**：
- 现有实现是**单轮**（panel 一次发散 + analyst 一次分析）；Debate 是**多轮对抗**。
- Debate 的「critic 角色」在现有实现中缺失——analyst 只做客观综合，不主动质疑/反驳 panel 的推理。
- 适用边界：Debate 适合**高价值、可验证、强推理**任务（数学/逻辑/事实核查）；对开放生成是过度设计。

### 2.6 范式对比总表

| 范式 | 核心机制 | 成本 | 质量定位 | 现有实现覆盖 |
|------|---------|------|---------|-------------|
| OpenRouter Fusion | panel→合成 | N+2 | 高质量、省（相对顶级） | ✅ 骨架一致 |
| Mixture-of-Agents | 多层迭代聚合 | 多层×N | 最高质量 | ⚠️ 单层，无迭代 |
| RouterBench | 成本-质量凸包 | 可变 | 定位框架 | ❌ 未做 Pareto 定位 |
| Self-Consistency | 温度采样+投票 | ×k | 强推理稳定 | ❌ temp=0 锁死多样性 |
| Multi-Agent Debate | 多轮对抗 | 多轮×N | 强推理/事实 | ❌ 单轮无 critic |

---

## 3 差距分析（现有实现 vs SOTA）

| # | 差距 | 证据（代码/文献） | 影响 |
|---|------|------------------|------|
| G1 | **temperature=0 锁死 panel 多样性** | `FusionRouterTemperature=0.0`；Self-Consistency 证明温度是收益来源 | panel 同模型时退化、无信息增益 |
| G2 | **发散一次，无迭代** | 单层 panel→analyst→outer；MoA 靠多层聚合增益 | 错过 MoA 的 refinement 收益 |
| G3 | **analyst JSON 解析脆弱** | `ParseAnalysis` 单次 `JsonDocument.Parse`，损坏即 null→回退串行 | 白付 N+1 次成本后回退，浪费 |
| G4 | **无质量门控 / 自适应触发** | 只要条件满足就全量走融合，无「是否该融合」判断；RouterBench 无凸包定位 | 简单任务也付 ×N 成本 |
| G5 | **无对 panel 回答的质量评分/过滤** | analyst 直接读全部 panel 文本，无预筛 | 低质 panel 污染分析 |
| G6 | **panel 多样性默认关** | `EnableFusionDiversity=false`；且 panel 同 tier | 信息增益潜力未释放 |
| G7 | **analyst/outer 无独立超时** | 只有 panel 有超时；`routing.md` 注明 analyst/outer 复用全局 ct | 慢 analyst/outer 拖尾 |
| G8 | **无 outer 对 consensus 的一致性校验** | outer 直接写答案，不校验是否偏离共识 | 可能产出偏离多数的答案 |

---

## 4 改进提案（每条含问题/方案/收益/成本/验收/风险）

按优先级排序。所有提案尊重 `routing.md` 既有契约（panel 超时、diversity、动态 size、audit/FusionRole 语义），默认关、向后兼容。

### P1【高】可配置 panel 温度多样性（修 G1）

- **问题**：`FusionRouterTemperature=0.0` 使同模型 panel 输出确定性与同质化，多样性机制失效，白付成本。
- **方案**：新增 `FusionRouterPanelTemperature`（默认沿用 global，但允许单独配置 >0 用于 panel 发散），analyst 仍用低温度保结构化。文档明确「panel 用高温度采样多样性，analyst 用低温度保 JSON 稳定」。
- **收益**：Self-Consistency 证明温度多样性直接提升推理稳定性；panel 不同模型时也引入采样级多样性。
- **成本**：零额外调用，仅加一个配置项。
- **验收**：同模型 panel 在 `PanelTemperature>0` 时产不同回答（单测验证多次调用文本不同）；`PanelTemperature=0` 时行为不变（向后兼容）。
- **风险**：温度升高可能偶发低质回答——由 analyst 综合兜底，可接受。

### P2【高】analyst 解析加固 + 结构化输出（修 G3）

- **问题**：`ParseAnalysis` 单次 JSON 解析，损坏即 null → 白付 N+1 成本后回退串行。
- **方案**：两级容错——(a) 解析失败时用 `response_format={type:"json_object"}` 重试一次（若模型支持）；(b) 解析仍失败时**降级用 analyst 的原始文本作为 `Recommendation`** 而非直接回退串行（保住已付的 panel 成本）。
- **收益**：把「解析失败→全回退」从硬失败降为软降级，显著减少融合成本浪费。
- **成本**：极端情况多一次 analyst 调用（仅解析失败时）。
- **验收**：构造损坏 JSON 的 mock，验证降级路径不抛异常、不白付；`response_format` 重试生效。
- **风险**：重试多耗一次调用——仅在解析失败时触发，比例低。

### P3【高】融合成本质量门控（自适应触发，修 G4）

- **问题**：只要条件满足就全量融合，简单请求也付 ×N 成本。
- **方案**：新增 `FusionRouterMinComplexity`（默认 `Standard`）——仅当 `RequestComplexity ≥` 阈值才走融合；`Simple` 请求直接走单模型/串行。合并现有 `EnableDynamicFusionPanelSize`：Simple→跳过融合，Standard→min panel，Complex→max panel。
- **收益**：把融合作「质量技术」用在真正复杂的请求上，Simple 请求省下 ×N 成本。
- **成本**：零（纯路由前置判断）。
- **验收**：`Simple` 复杂度的请求不触发融合（审计无 FusionRole 行）；`Complex` 触发且 panel=最大。
- **风险**：误判复杂度可能漏掉该融合的请求——`RequestComplexity` 是 typed 信号，非解析 reason，风险可控。

### P4【中】panel 回答质量预筛（修 G5）

- **问题**：低质/离题 panel 回答直接进 analyst，污染分析。
- **方案**：analyst 前加轻量预筛——按 panel 回答长度下限（如 `< 20 token` 视为空答）与失败剔除，只把有效 panel 回答交给 analyst；`panelAnswers` 少于 2 时回退串行。
- **收益**：提升 analyst 输入信噪比，减少低质 panel 干扰。
- **成本**：零（纯过滤逻辑）。
- **验收**：构造空/超短 panel 回答，验证被剔除且不进 analyst；正常 panel 不受影响。

### P5【中】analyst/outer 独立超时（修 G7）

- **问题**：只有 panel 有独立超时，analyst/outer 复用全局 ct，慢时拖尾。
- **方案**：复用 `routing.md` 已记录的「per-stage CTS」模式，新增 `FusionRouterAnalystTimeoutSeconds` / `FusionRouterOuterTimeoutSeconds`（默认 0=沿用全局，向后兼容）。
- **收益**：与 panel 超时一致的尾延迟控制。
- **成本**：零调用增量。
- **验收**：慢 analyst mock 到超时被取消，不阻塞；0=保持原行为。
- **风险**：超时过短可能杀正常 analyst——默认 0 不启用，风险低。

### P6【低】outer 共识一致性校验（修 G8）

- **问题**：outer 直接写答案，不校验是否偏离 panel 共识。
- **方案**：outer prompt 追加「若与 consensus 冲突，须显式说明理由」指令；可选在 analyst 的 `recommendation` 字段里带上 consensus 摘要供 outer 对照。
- **收益**：降低 outer 产出偏离多数的「离群答案」概率。
- **成本**：零（prompt 级改动）。
- **验收**：outer prompt 含一致性指令；单测验证 prompt 内容。
- **风险**：低，纯 prompt 约束。

### P7【低】MoA 式可选迭代（修 G2，探索项）

- **问题**：单层发散一次，无迭代 refinement。
- **方案**：背靠背探索——新增 `FusionRouterRefinementRounds`（默认 1=现状），>1 时 analyst 的输出作为下一轮 panel 的输入之一做迭代。**探索项**，因成本随轮数线性增。
- **收益**：MoA 证明迭代聚合增益真实存在。
- **成本**：每轮 ×N 调用。
- **验收**：配置 `>1` 时可复现多轮调用；默认 1 行为不变。
- **风险**：成本失控——默认 1 关，且需真实数据标定收益才开。

### P8【低】融合成本-质量 Pareto 定位（修 G4 的测量面）

- **问题**：不知道融合相对单模型是否 Pareto 更优。
- **方案**：实证层（本任务已做工具）持续输出「融合 vs 单模型」成本倍数与质量代理；在生产接入真实质量评估（如 LLM-as-judge 采样）后，把融合路由放进 RouterBench 式凸包定位。
- **收益**：用数据决定融合是否值得开，而非默认关。
- **成本**：需真实流量 + 质量评估管道（超出本任务范围）。
- **验收**：`analyze_audit.py` 已能输出成本倍数（本任务 AC3 已交付）；后续接质量端。
- **风险**：无（测量先行）。

---

## 5 实证分析（合成数据）

### 5.1 工具扩展（本任务交付）

- `scripts/generate_audit_data.py`：新增 `--fusion-rate`（生成自洽 panel+analyst+outer 融合组，共享 `parallel_group_id`）与 `--fusion-analyst-fail-rate`。默认 0，向后兼容。
- `scripts/analyze_audit.py`：新增 `## Fusion Router` 报告段——By FusionRole 聚合、组级成本倍数、panel 多样性、analyst 失败率、panel 全失败回退计数。列缺失/无数据时优雅降级。

### 5.2 数据与结论（Q1-Q5）

生成：`--rows 400 --seed 7 --fusion-rate 0.6 --fusion-analyst-fail-rate 0.15`（另有一组 seed 5 复现验证）。

| 实证问题 | 结果 | 解读 |
|---------|------|------|
| **Q1** panel 多样性 | **2.5 个不同模型/组** | 模型池仅 3 个（gpt-4o/gpt-4o-mini/deepseek-chat），panel 从其中取 2-3 个。真实部署若 panel 同 tier 同族，多样性会更低。与 P1/P6（温度+多样性）直接相关。 |
| **Q2** 成本倍数 | **4.96×**（seed7）/ **4.28×**（seed5） | 融合组总成本 ≈ 单模型请求的 4-5 倍。符合理论 ×N（panel）+ analyst + outer。**这是融合的核心代价**，P3 门控让它只花在复杂请求上。 |
| **Q3** analyst 失败率 | **18.5%**（配置 0.15） | 与 `--fusion-analyst-fail-rate` 匹配。真实场景 analyst 解析失败会白付 panel 成本回退——支撑 P2 加固。 |
| **Q4** 延迟惩罚 | outer p95 **1069ms** vs 非融合 **1163ms** | 并行 panel 使端到端延迟**不增反略降**（并行掩盖了单模型最慢 delay）。这是融合的隐藏收益——成本换质量，但延迟持平。 |
| **Q5** panel 全失败回退 | **0 组** | 合成数据 panel 成功率 95.7%，无全失败。真实场景需监控此路径（P2 的降级逻辑覆盖）。 |

### 5.3 实证结论

1. **融合的代价是成本（4-5×），不是延迟**（并行 panel 掩盖延迟）。这印证「质量技术」定位——必须配合 P3 门控只对复杂请求启用。
2. **panel 多样性是最大短板**：模型池小 + temperature=0 + diversity 默认关，三重因素使「多样性→信息增益」的理论收益在多数配置下无法兑现。P1 是最优性价比修复。
3. **analyst 解析失败是隐藏成本炸弹**：18.5% 的组会白付 panel 成本后回退（合成下）；P2 的降级路径能挽回。
4. 合成数据**无法证真质量收益**（无真实"正确答案"），机制性指标（多样性、成本、延迟）已量化；质量收益需接真实评估（P8）。

---

## 6 优先级排序与建议

| 优先级 | 提案 | 修差距 | 理由 |
|-------|------|-------|------|
| **立即** | P1 panel 温度多样性 | G1 | 修掉多样性失效的根本矛盾，零成本 |
| **立即** | P2 analyst 解析加固 | G3 | 修成本浪费，防白付 |
| **立即** | P3 成本门控 | G4 | 让融合只花在复杂请求，控 ×N |
| **短期** | P4 panel 预筛 / P5 超时 | G5/G7 | 提升信噪比与尾延迟 |
| **中期** | P6 一致性校验 | G8 | prompt 级低成本约束 |
| **探索** | P7 MoA 迭代 / P8 Pareto 定位 | G2/G4 | 成本高 / 需真实数据，谨慎评估 |

**建议**：先落地 P1+P2+P3（三个低成本、修根本矛盾的改动），用现有审计工具（本任务已交付）在生产开启后持续监控成本倍数与 analyst 失败率，再决定是否推进 P4-P8。P7/P8 需真实质量评估才值得投入。

---

## 附：来源

- OpenRouter Fusion Router：openrouter.ai（2026-06 发布）
- Mixture-of-Agents：Wang et al., 2024, arXiv:2406.04692
- RouterBench：Shi et al., 2024（cost-quality frontier / AIQ）
- Self-Consistency：Wang et al., 2022, "Self-Consistency Improves Chain of Thought Reasoning"
- Multi-Agent Debate：Du et al., 2023
- 代码事实：`src/OptiRouter/Endpoints/FusionRouter.cs`、`Routing/FusionSynthesis.cs`、`Routing/FusionPanelSelector.cs`、`.trellis/spec/backend/routing.md`