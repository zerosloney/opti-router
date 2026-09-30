# 策略横评（Strategy Bakeoff）

回答一个问题：**智能路由到底省了多少钱、提了多少质量？** 用同一金标准数据集，在真实计费下横向对比路由策略组合，把"要不要上智能路由、开哪个档的策略"变成可测量的决策。

Harness：`scripts/strategy-bakeoff.ps1`（配置热切换 → 学习状态归零 → 评测跑批 → 审计总账取成本 → 恢复原状）。

## 策略定义（6 组）

真实部署的第一杠杆是**模型池组合**（买哪些档位的模型），其次是路由策略开关。因此横评以"启用的模型档位集"为主轴：

| 策略 | 启用档位 | 路由开关 | 回答的问题 |
|------|---------|---------|-----------|
| `baseline-cheap` | 仅 Cheap | 全关 | 只买便宜模型的成本下限与质量下限 |
| `baseline-strong` | 仅 Strong | 全关 | 全买最贵的质量上限与成本上限 |
| `full-pool` | 全部 | 分类器+故障转移，学习/级联/融合全关 | 路由器**开箱行为**的基线 |
| `cost-first` | 全部 | Thompson + ε 探索 + 延迟感知 | 学习类策略在成本敏感场景的收益 |
| `balanced` | 全部 | Thompson + 10% 级联升级 | 级联兜底的性价比 |
| `quality-first` | 全部 | Fusion panel=2 + 拜占庭共识 | 多模型共识的质量收益与 N+2 成本 |

## 指标口径

| 指标 | 来源 | 说明 |
|------|------|------|
| 准确率 / 质量通过率 | 评测报告 `/api/dashboard/eval/run` | 质量分口径 judge → 向量余弦 → token-jaccard 兜底（每批结果的 `qualityMetric` 标注实际口径）；强烈建议 `-QualityJudgeModel` 配第三方档 judge，jaccard 只看词面重叠会被回答长度带偏 |
| 平均延迟 | 评测报告 | 最终答案端到端耗时 |
| **策略总成本 / 总 tokens / 上游调用数** | **审计分析 `/api/dashboard/audit/analysis`（评测时间窗）** | 评测报告的成本只含最终答案那次调用——Fusion 的 panel/analyst、级联的重答都单独计费进审计，横评对比必须用总账 |
| 模型选中分布 | 评测报告 per-case `selectedModel` | 每策略最终落在哪个模型 |

## 运行

前提：
1. 一个**专用于评测**的实例（评测消耗真实预算并写审计，建议隔离运行，避免业务流量污染审计窗口）；
2. 模型池覆盖三档、**每档至少 2 个模型**（融合 panel 需要同池凑 ≥2 候选；分类器把简单题收缩到单档后，每档 1 个模型时融合静默回退串行）；
3. 管理密钥（主密钥，RBAC 下 operator 即可执行评测）。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\strategy-bakeoff.ps1 `
  -BaseUrl http://localhost:5080 -AdminKey <AdminApiKey> `
  -QualityJudgeModel "<与被评模型不同源的 Strong 档模型>" `
  [-Strategies baseline-cheap,baseline-strong,full-pool,cost-first,balanced,quality-first] `
  [-CasesPath bench\strategy-bakeoff\cases.json] [-OutDir reports\strategy-bakeoff]
```

行为：逐策略热切换配置（CAS）与模型启停 → `POST /api/dashboard/learning/reset` 归零学习状态（防跨策略串扰）→ 跑评测 → 记录审计窗口总账 → **恢复初始配置与模型启停状态**（`-SkipRestore` 跳过）。原始报告与审计 JSON 归档在 OutDir。

## 冒烟验证记录（mock 上游，机制验证，非质量数据）

2026-09-30 在临时实例（SQLite 配置库 + 本地 mock 上游，三档各 2 个模拟模型，延迟/长度/价格拉开差距）端到端验证 harness 机制。**下表数字仅证明：配置热切换生效、模型启停生效、学习归零生效、Fusion/级联路径触发、审计总账口径正确——不冒充任何真实质量结论**：

| 策略 | 平均延迟ms | 策略总成本USD | 上游调用数 | 模型选中分布 |
|------|-------:|--------:|-------:|------|
| baseline-cheap | 123 | 0.0001 | 10* | mock-cheap×10 |
| baseline-strong | 912 | 0.0658 | 9* | mock-strong×10 |
| cost-first | 226 | 0.0063 | 9* | cheap 档内部探索（Thompson 在 10 题样本≈随机） |
| balanced | 226 | 0.0077 | 5* | cheap 主导 + strong×1（级联采样触发） |
| quality-first | 238 | 0.0218 | 24 | cheap 主导 + 拜占庭共识捷径（panel 同答 → 2 次调用成交，日志见 `Byzantine consensus achieved`） |

\* 审计窗口边界有 ±1-2 次调用误差（异步批量落库的 flush 时机，已延迟 3 秒缓解）。成本结构符合预期：strong-only ≈ cheap-only 的 500×（mock 定价 15 vs 0.2 美元/M 输出），quality-first ≈ 全池的 3.4×（共识捷径下 Fusion 只多付 panel 一倍，analyst/outer 被捷径省掉）。

### 冒烟暴露的机制事实（对真实跑批同样适用）

1. **规则分类器是档位分配的主手**：分类器开启时多数简单请求被判 Cheap，`DefaultTier` 只兜底未命中——想隔离"档位组合"变量，靠启停模型（Tiers）而不是调 DefaultTier。
2. **融合有共识捷径**：panel 输出高度一致时按多数派直接采纳（成本 N 而非 N+2）；mock 的同质化回答使捷径全命中，真实异质模型上 analyst/outer 会更多触发。
3. **学习类策略需要样本量**：10 题的 Thompson 近似随机，成本-first/balanced 的学习收益要在 ≥50 题或持续流量下才可辨。

## 真实上游报告模板（待填）

跑批后把汇总表替换进来，并写三行结论：成本排序、质量排序、性价比拐点。

```
| 策略 | 准确率 | 质量通过率 | 评分口径 | 平均延迟ms | 策略总成本USD | 策略总tokens | 上游调用数 | 模型选中分布 |
|------|---|---|---|---:|---:|---:|---:|---|
| baseline-cheap  | | | | | | | | |
| baseline-strong | | | | | | | | |
| full-pool       | | | | | | | | |
| cost-first      | | | | | | | | |
| balanced        | | | | | | | | |
| quality-first   | | | | | | | | |
```

结论（示例骨架）：
- 成本：cheap-only < full-pool < balanced < quality-first < strong-only
- 质量：……（judge 口径下的实际排序）
- 拐点：……（在哪个数据集/流量结构上，学习或融合的增量成本开始换到增量质量）

## 已知口径限制

- **小样本**：金标准集 10 题（上限 50/批）。学习类策略与 Fusion 的共识率都对样本量敏感，正式结论建议 ≥50 题 + 多次重复取中位数。
- **审计窗口**：策略总成本取自审计时间窗汇总，评测实例必须独占（业务流量会混入窗口）。
- **单次方差**：上游延迟/采样温度造成单批波动；`CascadeUpgradeSampleRate` 采样式策略天然不可复现。
- **judge 成本**：`-QualityJudgeModel` 每题追加 1 次打分调用，计入该策略审计总账。
