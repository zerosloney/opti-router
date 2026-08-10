# 合成审计数据生成器（打通数据闭环）

Task: 08-10-audit-data-generator

## Goal

项目已建成完整的审计落盘（`request_audit` 25 列）与离线分析（`scripts/analyze_audit.py`），但**无真实数据、无数据生成工具**——闭环从未被实际跑通过（`data/optirouter-budget.db` 不存在）。目标：提供一个**合成审计数据生成器** `scripts/generate_audit_data.py`，产出符合真实 schema、分布贴近真实负载的 audit 行，跑通 `analyze_audit.py`，验证报告能暴露「规则误判」「分档成本/延迟差异」「级联触发」等预期信号——让后续所有调参（奖励常数、tolerance、权重）有据可依、可复现。

## Background（已核实）

- 审计 schema：`SqliteRequestAuditStore`（25 列：timestamp/request_id/model/estimated_tokens/prompt_tokens/completion_tokens/cost/latency_ms/session_id/routing_reason/success/error_message/is_streaming/routed_tier/cascade_triggered/upgraded_from/is_adopted/parallel_group_id/is_estimated/fusion_role/ttft_ms/cached_input_tokens/cache_write_input_tokens/uncached_input_tokens/quota_limited）。
- 分类信号（`RuleClassifierPolicy` 产出，写入 routing_reason）：`code-detected`/`code-complex`/`code-simple`/`math-detected`/`translation-request`/`simple-qa`/`complex-instruction`/`default`/`fallback-to-default`。
- `analyze_audit.py` 已支持 Summary / By Model / By Routed Tier / Cascade / By Routing Reason Signal / Daily Trend 六维，消费上述列。
- `routed_tier` 列存 **TEXT**（SQLite 迁移定义 `routed_tier TEXT`，非 int——生成器必须写字符串 "Strong"/"Medium"/"Cheap"）。
- 模型配置在 `models-config.json`（示例 appsettings 的 Models 为空；真实模型在 Dashboard 管理）。

## Requirements

- R1 提供 `scripts/generate_audit_data.py`，生成符合 `request_audit` schema 的合成数据写入指定 SQLite 库（默认 `data/optirouter-budget.db`）。
- R2 生成数据覆盖真实分类信号（code-*/math/simple-qa/translation/default 等），routing_reason 含对应信号片段。
- R3 分布贴近真实负载：分档（Strong/Medium/Cheap）成本、延迟、成功率各异（如 Cheap 便宜但成功率略低、Strong 贵但稳定性高），支持受控注入「规则误判」（如本该 Strong 的复杂任务被路由到 Cheap），使报告能暴露误判信号。
- R4 级联触发（`cascade_triggered`/`upgraded_from`）与并行/融合（`is_adopted`/`parallel_group_id`/`fusion_role`）可选生成。
- R5 可复现：支持 seed，默认确定性。
- R6 零外部依赖（仅标准库，与 `analyze_audit.py` 一致），只写不读（不破坏既有库时可合并，或写新库）。
- R7 跑通 `analyze_audit.py` 验证报告有意义的信号。

## Acceptance Criteria

- [ ] `python scripts/generate_audit_data.py --rows N [--seed S] [--db path]` 生成 N 行合法审计数据。
- [ ] 生成的数据可被 `python scripts/analyze_audit.py --db <path>` 完整消费，六维报告均有数据。
- [ ] 报告能暴露：分档成本差异（Cheap 单位成本低）、延迟差异、受控误判信号（如明显 code-complex 命中的 Cheap 档）。
- [ ] 同 seed 两次生成结果一致（可复现）。
- [ ] 级联/并行字段可选生成且合法。
- [ ] 零外部依赖；不破坏既有真实库（默认写独立测试库或提供 --append 显式开关）。

## Out of Scope

- 真实生产流量接入（本工具是合成数据，供验证/调参用）。
- 自动调参（本任务只打通「生成→分析→人工看信号」闭环）。
- 改动 `analyze_audit.py` 的分析逻辑（除非发现必要缺口）。

## Open Questions

- 默认模型集与成本/延迟/成功率参数：从哪来（写死在脚本 OR 从 models-config.json 读）？——设计阶段定夺（倾向脚本内置默认 + 可覆盖）。