# 融合路由研究 — 技术设计

## 1. 目标与边界

本任务产出**研究报告 + 实证分析工具扩展**，不改生产代码。研究分三块：

- **A. 算法综述**：融合路由/多智能体合成分支的范式扫描（纯文档研究）。
- **B. 差距分析 + 改进提案**：基于综述 + 代码勘察，产出带验收标准的提案清单（纯文档）。
- **C. 实证分析**：扩展合成数据生成器与分析器，让融合路由的 panel/analyst/outer 成本-质量能被量化验证（代码，但只在 `scripts/` 下，不动 `src/`）。

## 2. 实证分析设计（部分 C）

### 2.1 目标问题

在设计工具前先锁定实证要回答的问题（避免工具做了但没回答）：

| # | 实证问题 | 需要的字段/聚合 |
|---|---------|----------------|
| Q1 | 融合是否带来质量收益（代理） | panel 多样性（同组不同模型/Provider/Family 命中数） |
| Q2 | 成本是否 ×N 失控 | 融合组总成本 vs 单模型基线成本 |
| Q3 | analyst 解析失败率多高 | `fusion_role="analyst"` 且 `success=0` 占比 |
| Q4 | 融合延迟惩罚多大 | 融合组端到端延迟（outer 采纳行）vs 非融合 |
| Q5 | panel 全失败回退串行多频繁 | 组内 panel 全失败 + 无 outer 采纳 |

### 2.2 `generate_audit_data.py` 扩展

新增 `--fusion-rate R`（`[0,1]`，默认 0）参数，生成**自洽的融合组**（区别于现有 `--parallel-rate` 的单行 race 伪造）：

- 命中融合的请求，生成一个 `parallel_group_id`，组内包含：
  - **N 个 panel 行**：`fusion_role="panel"`，`is_adopted=0`，`is_estimated`（未采纳按估算），模型从候选链取（含 Provider/Family 多样性模拟——不同模型名代表不同 provider）。
  - **1 个 analyst 行**：`fusion_role="analyst"`，`is_adopted=0`，成功或失败（`--fusion-analyst-fail-rate` 控制，模拟解析失败 → 组内无 outer）。
  - **1 个 outer 行**（analyst 成功时）：`fusion_role="outer"`，`is_adopted=1`，是组内唯一采纳行。
- 组内 panel 数在 `[2,3]` 随机（模拟 `FusionRouterPanelSize` 动态）。
- 现有 `--parallel-rate` 保留不动（race 语义），两者可同用但语义独立。

新增参数：`--fusion-rate`、`--fusion-analyst-fail-rate`（默认 `0.1`）。

### 2.3 `analyze_audit.py` 扩展

新增 `build_fusion` 报告段（`main` 中插入，位于 `build_cascade` 之后）：

- **前置检查**：`fusion_role` 与 `parallel_group_id` 列均存在才跑；否则输出 `(fusion columns absent — legacy DB)` 并返回（向后兼容，AC6）。
- **Fusion 组统计**：`SELECT DISTINCT parallel_group_id WHERE fusion_role IS NOT NULL` 得组数。
- **By FusionRole**：对 panel/analyst/outer 各自 `aggregate()`，输出 count/success/p95/总成本。
- **组级成本**：按组聚合总成本，对比非融合请求平均成本 → 成本倍数（Q2）。
- **panel 多样性**：组内 `COUNT(DISTINCT model)`、按模型名推断 provider 前缀的 distinct 数（合成数据模型名含 provider 时可靠；真实数据标注为近似）→ Q1 代理。
- **analyst 失败率**：`fusion_role="analyst"` 且 `success=0` 占比 → Q3。
- **panel 全失败**：组内 panel 全失败且无 outer 的组数 → Q5。
- 无 fusion 数据时输出 `(no fusion rows in range)`，不崩。

### 2.4 向后兼容

- `analyze_audit.py`：fusion 段是**新增**，旧列缺失 → 优雅降级；其余段不变。
- `generate_audit_data.py`：`--fusion-rate` 默认 0，不传则行为与现在完全一致（同 seed 输出不变，不影响既有用法与文档）。

## 3. 研究文档结构（部分 A + B）

研究报告落 `research/fusion-router-algo-research.md`（任务目录或仓库 `docs/` 下，用任务目录避免污染仓库）。结构：

```
# 融合路由深层算法研究
## 1 现状：OptiRouter 现有实现解剖    ← 代码事实 + 契约
## 2 算法综述（SOTA 范式扫描）
   2.1 OpenRouter Fusion Router
   2.2 Mixture-of-Agents (Together AI)
   2.3 LLM 路由 (RouterBench 类)
   2.4 Self-Consistency / Multi-Agent Debate
   2.5 Ensemble & Aggregation
   （每节：机制 / 成本-质量 / 适用 / 与现有实现差异）
## 3 差距分析（现有 vs SOTA）
## 4 改进提案（≥5 条，每条含问题/方案/收益/成本/验收/风险）
## 5 实证分析
   5.1 工具扩展说明
   5.2 合成数据结论（Q1-Q5 数据表）
   5.3 结论与优先级
```

## 4. 关键权衡

- **合成数据的局限**：质量收益无法用合成数据证真（合成数据无真实"正确答案"），只能验证**成本/延迟/多样性的机制性预期**。质量收益的结论来自综述的文献证据 + 代理指标（panel 多样性 → 信息增益潜力）。报告需明确这一边界。
- **Provider 推断**：合成模型名含 provider 前缀（如 `gpt-4o`→openai），panel 多样性可按模型名前缀推断；真实数据的 Provider/Family 在审计表里没有独立列，只能靠模型名前缀近似。报告标注为近似。
- **不碰生产代码**：所有改动在 `scripts/` 与 `research/`，STOP 线（跨业务文件/公共 API）不触发。

## 5. 兼容与回滚

- 脚本改动向后兼容（默认参数不变），无回滚风险。
- 研究报告为新增文件，无回滚。