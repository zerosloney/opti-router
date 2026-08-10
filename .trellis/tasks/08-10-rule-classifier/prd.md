# 强化规则分级代码意图细分（rule-classifier）

Child of: 08-10-single-model-routing

## Goal

让代码意图细分的**离线验证闭环**真正可用：`scripts/analyze_audit.py` 是规则调优的实证依据，但它按 `routing_reason` 分组的信号关键词表（`build_by_reason`，第 255-259 行）**缺失**刚落地的 `code-complex`/`code-simple`/`math-detected`/`translation-request` 信号——导致新增子分类无法被监控，违反「以离线审计为实证依据」的设计意图。同时审视意图判别的精度缺口。

## Background（已核实）

- 分类器：`RuleClassifierPolicy.ClassifyCodeIntent`（复杂 > 简单 > 默认 Strong），意图检测只跑 `ExtractInstructionText`（最后一条 user 消息、剔除 fenced code）。
- 细分信号：`code-complex`（Strong）、`code-simple`（Medium）、`code-detected`（Strong 保守）、`math-detected`（Strong）、`translation-request`（Medium）。
- 测试：`RuleClassifierPolicyTests` 覆盖复杂/简单/保守 Strong、两大误降级防护（`Example`/`BasicAuth` 类名）、代码正文泄漏防护、explain 不降级。
- **闭环缺口**：`scripts/analyze_audit.py` 的 `build_by_reason` 关键词列表只含 `code-detected`/`simple-qa`/`complex-instruction`/`semantic-router: matched`/`long-input: filtered`/`fallback-to-default`/`session-affinity: promoted`，**不含** `code-complex`/`code-simple`/`math-detected`/`translation-request`。这些请求会被归入 `default` 桶，无法按子类聚合成功率/成本/误判率。

## Requirements

- R4.1 补全 `analyze_audit.py` 的信号关键词表，纳入 `code-complex`/`code-simple`/`math-detected`/`translation-request`，使新增子分类可离线监控。
- R4.2 审视并修正意图判别的精度缺口（若有），以测试锁定；无新依赖、不改语料。
- R4.3 不破坏三大 gotcha 防线（不跑全量文本、不用英文裸名词、explain 归 Strong）。
- R4.4 现有 `RuleClassifierPolicyTests` 全绿。

## Acceptance Criteria

- [ ] `analyze_audit.py build_by_reason` 能按 `code-complex`/`code-simple`/`math-detected`/`translation-request` 分组聚合。
- [ ] 用含各新信号的样例 reason 跑 `analyze_audit.py`，对应信号桶不再落入 `default`。
- [ ] 任何新增/修改的判别规则有测试锁定。
- [ ] `RuleClassifierPolicyTests` 全绿；`dotnet build` 通过。

## Out of Scope

- 改变 `ClassifyCodeIntent` 的 tier 映射方向（复杂→Strong 等既定语义）。
- 引入外部 NLP/分类模型。
- 改动 `routing_reason` 的生成格式（只补脚本侧关键词）。

## Open Questions

- 意图判别精度缺口的具体方向（脚本补全后，由真实审计数据驱动——本次先补闭环，精度微调留待有数据时）——见设计。