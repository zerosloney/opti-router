# 单模型智能选择路由研究 — 执行计划

## 阶段 0：研究采集（综述 + 差距）

- [ ] 0.1 Web 检索单模型选择/预测路由文献：RouterBench（predictive/cascading/overgenerate-rerank）、LLM-as-router、多臂老虎机（Thompson/UCB/EXP3/LinUCB）、embedding 语义路由、compact input routing、成本-质量 Pareto/AIQ。收集官方文档/论文链接。
- [ ] 0.2 对照现有实现（`RouterEngine.cs` / 策略链 / `ThompsonSampler` / `RuleClassifierPolicy` / `SemanticRouterPolicy` / `routing.md` spec）逐条映射综述范式 → 现有覆盖/差距。
- [ ] 0.3 评估已知 P2 待办（策略链并行化、能力标签扩展）在差距分析中的位置。
- [ ] 0.4 提炼 ≥6 条改进提案，每条含问题/方案/收益/成本/验收/风险。

## 阶段 1：实证工具扩展

- [ ] 1.1 `scripts/generate_audit_data.py`：新增 `--signal-accuracy`、`--thompson-rate`、`--quality-agent`，模型画像加 `quality` 字段；`routing_reason` 输出 `target=Tier(signal)` 与 `thompson: reward=X, round=Y` 可解析格式。
  - 验证：`python scripts/generate_audit_data.py --rows 300 --seed 7 --signal-accuracy 0.85 --thompson-rate 0.5 --db data/audit-single.db` 生成成功，同 seed 可复现；不传新参数时输出与旧行为一致（向后兼容）。
- [ ] 1.2 `scripts/analyze_audit.py`：新增 `build_single_model` 段（分类信号混淆矩阵/准确率、Thompson 奖励分布 + regret 代理、成本-质量 Pareto/AIQ），列/数据缺失时优雅降级。
  - 验证：对 1.1 的 `data/audit-single.db` 跑 `--db data/audit-single.db`，报告含 `## Single-Model Selection` 段；对无单模型列的 DB 跑不崩（AC6）。

## 阶段 2：实证闭环 + 研究报告

- [ ] 2.1 跑通 `generate --signal-accuracy --thompson-rate --quality-agent → analyze` 闭环，产出 `data/audit-single.db` 与报告，填充实证数据表（分类信号准确率、Thompson 奖励、成本-质量 AIQ）。
- [ ] 2.2 撰写 `research/single-model-routing-algo-research.md`：现状解剖 + 综述 + 差距 + 提案 + 实证结论 + 优先级。
- [ ] 2.3 交叉核对报告与 prd.md 验收标准（AC1-AC6）。

## 验证命令

```bash
# 1.1 生成单模型路由维度数据
python scripts/generate_audit_data.py --rows 300 --seed 7 --signal-accuracy 0.85 --thompson-rate 0.5 --db data/audit-single.db
# 1.2 分析（含 Single-Model Selection 段）
python scripts/analyze_audit.py --db data/audit-single.db --out data/audit-single-report.md
# 1.1 向后兼容：旧参数行为不变
python scripts/generate_audit_data.py --rows 100 --seed 42 --db data/audit-demo.db
python scripts/analyze_audit.py --db data/audit-demo.db
# 1.2 列缺失降级（对无单模型列的库）
python scripts/analyze_audit.py --db data/audit-demo.db
```

## 风险点 / 回滚

- 合成数据不能证真质量收益 → 报告明示边界，质量结论基于文献 + 代理指标。
- signal-accuracy / thompson 解析依赖 `routing_reason` 字符串格式 → 生成与解析用同一格式约定，报告标注近似。
- 新参数默认关、新报告段降级 → 无回滚风险，不破坏既有生成/分析行为。