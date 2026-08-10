# 设计：合成审计数据生成器

Task: 08-10-audit-data-generator

## 目标形态

`scripts/generate_audit_data.py`，零依赖（仅 `sqlite3`/`random`/`argparse`/`datetime`），与 `analyze_audit.py` 同目录同风格。

## 数据模型

### 模型画像（内置默认，可 `--models-json` 覆盖）

每个模型 = (name, tier, base_cost_per_1m_in, price params, latency_base_ms, success_rate)。默认集（贴近真实配置）：

| name | tier | 输入价$/1M | 延迟基准ms | 成功率 |
|------|------|-----------|-----------|--------|
| gpt-4o | Strong | 2.5 | 900 | 0.99 |
| gpt-4o-mini | Medium | 0.15 | 500 | 0.97 |
| deepseek-chat | Cheap | 0.01 | 300 | 0.93 |

逻辑：tier 越高 → 成本高、延迟稍高、成功率更高（体现「贵但稳」）；tier 越低 → 便宜、快、成功率略低（体现误判风险来源）。

### 请求画像（按分类信号分布）

按 `RuleClassifierPolicy` 真实信号生成 routing_reason，每信号有「应该命中哪个 tier」的语义：

| signal | reason 片段 | 应路由 tier | 占比 |
|--------|------------|------------|------|
| code-complex | `target=Strong(code-complex)` | Strong | 15% |
| code-simple | `target=Medium(code-simple)` | Medium | 10% |
| math-detected | `target=Strong(math-detected)` | Strong | 8% |
| translation-request | `target=Medium(translation-request)` | Medium | 8% |
| simple-qa | `target=Cheap(simple-qa)` | Cheap | 35% |
| complex-instruction | `target=Strong(complex-instruction)` | Strong | 7% |
| default | `target=Medium(default)` | Medium | 17% |

### 成本模型

按模型输入价 + 随机 token 量（prompt ~N(lognormal 500, 2000)，completion ~N(200, 800)）算 cost：
`cost = (prompt_tokens × in_price + completion_tokens × out_price) / 1e6`。缓存 token 字段（cached/cache_write/uncached）按概率拆分。

### 延迟模型

`latency_ms = model.latency_base + N(0, 150)`，截断非负；部分长尾（5%）加 2-5× 放大制造 p95 差异。`ttft_ms = latency × uniform(0.3, 0.6)`。

### 受控误判注入（`--misclassify N`）

把 N 条原本该 Strong 的信号（code-complex/math）强制写 `routed_tier=Cheap` + `routing_reason` 保留 Strong 信号但 tier 改 Cheap，使 `analyze_audit.py` 的 By Routed Tier 出现「Cheap 档低成本但信号是 code-complex」的异常，验证报告能暴露误判。

### 级联/并行可选生成

- `--cascade-rate R`：cascade_triggered=1 + upgraded_from=<cheap model>（强信号时）。
- `--parallel-rate R`：is_adopted 0/1 + parallel_group_id + fusion_role(panel/analyst/outer)。

## 写入

- 默认写 `--db data/audit-demo.db`（独立测试库，**不碰**真实 `optirouter-budget.db`）。
- `--append` 显式开关才往既有库追加。
- 复用 `analyze_audit.py` 的 schema 列名（25 列），`routed_tier` 写 **TEXT**（"Strong"/"Medium"/"Cheap"），与迁移定义一致。

## 验证

```bash
python scripts/generate_audit_data.py --rows 1000 --seed 42
python scripts/analyze_audit.py --db data/audit-demo.db
```
确认：六维报告有数据；By Routed Tier 显示分档成本/延迟差异；`--misclassify` 后暴露误判信号；同 seed 复现一致。

## 兼容性 / 风险

- 零依赖，纯标准库。
- 不破坏真实库（默认独立库）。
- `routed_tier` TEXT 是关键——写错成 int 会让 analyze_audit 的 By Routed Tier 混型 sort 崩溃（此前踩过）。
- 随机分布用 `random.Random(seed)` 局部实例，不污染全局。

## Rollback

- 单一新脚本，删除即回滚。无 schema/配置变更。