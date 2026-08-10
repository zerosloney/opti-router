# 融合路由研究 — 执行计划

## 阶段 0：研究采集（综述 A + 差距 B）

- [ ] 0.1 Web 检索融合路由/多智能体合成分支文献：OpenRouter Fusion Router、Mixture-of-Agents、RouterBench、Self-Consistency、Multi-Agent Debate、LLM ensemble。收集出处的官方文档/论文链接。
- [ ] 0.2 对照现有实现（`FusionRouter.cs` / `FusionSynthesis.cs` / `FusionPanelSelector.cs` / `routing.md` spec）逐条映射综述范式 → 现有实现覆盖/差距。
- [ ] 0.3 提炼 ≥5 条改进提案，每条含问题/方案/收益/成本/验收/风险，尊重既有契约。

## 阶段 1：实证工具扩展（部分 C）

- [ ] 1.1 `scripts/generate_audit_data.py`：新增 `--fusion-rate`（默认 0）与 `--fusion-analyst-fail-rate`（默认 0.1），生成自洽融合组（N panel + 1 analyst + 1 outer，共享 `parallel_group_id`，`is_adopted` 语义正确）。
  - 验证：`python scripts/generate_audit_data.py --rows 300 --seed 5 --fusion-rate 0.5 --db data/audit-fusion.db` 生成成功，同 seed 可复现；不传 `--fusion-rate` 时输出与旧行为一致。
- [ ] 1.2 `scripts/analyze_audit.py`：新增 `build_fusion` 段（By FusionRole / 组成本倍数 / panel 多样性 / analyst 失败率 / panel 全失败），列缺失时优雅降级。
  - 验证：对 1.1 的 `data/audit-fusion.db` 跑 `--db data/audit-fusion.db`，报告含 Fusion 段；对无 fusion 列/数据的 DB 跑不崩（AC6）。

## 阶段 2：实证闭环 + 研究报告

- [ ] 2.1 跑通 `generate --fusion-rate → analyze` 闭环，产出 `data/audit-fusion.db` 与报告，填充 Q1-Q5 数据表。
- [ ] 2.2 撰写 `research/fusion-router-algo-research.md`：现状解剖 + 综述 + 差距 + 提案 + 实证结论 + 优先级。
- [ ] 2.3 交叉核对报告与 prd.md 验收标准（AC1-AC6）。

## 验证命令

```bash
# 1.1 生成融合数据（含 analyst 失败）
python scripts/generate_audit_data.py --rows 300 --seed 5 --fusion-rate 0.5 --db data/audit-fusion.db
# 1.2 分析（含 Fusion 段）
python scripts/analyze_audit.py --db data/audit-fusion.db --out data/audit-fusion-report.md
# 1.2 向后兼容：旧 behavior 不变
python scripts/generate_audit_data.py --rows 100 --seed 42 --db data/audit-demo.db
python scripts/analyze_audit.py --db data/audit-demo.db
# 1.2 列缺失降级（对无 fusion 的库）
python scripts/analyze_audit.py --db data/audit-demo.db
```

## 风险点 / 回滚

- 合成数据不能证真质量收益 → 报告明示边界，质量结论基于文献 + 代理指标。
- Provider 推断仅靠模型名前缀 → 报告标注近似。
- `--fusion-rate` 默认 0，不破坏既有生成行为；`build_fusion` 为新增段，列缺失降级 → 无回滚风险。