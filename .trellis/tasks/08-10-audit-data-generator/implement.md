# 执行：合成审计数据生成器

Task: 08-10-audit-data-generator

## Checklist

1. 新建 `scripts/generate_audit_data.py`：
   - argparse：`--rows`(默认 1000)、`--seed`(默认 42)、`--db`(默认 `data/audit-demo.db`)、`--append`、`--misclassify`(默认 0)、`--cascade-rate`(默认 0)、`--parallel-rate`(默认 0)、`--models-json`(可选)。
   - 内置模型画像（Strong/Medium/Cheap 三档，成本/延迟/成功率差异）。
   - 信号分布表（8 类信号 + 应路由 tier + reason 片段）。
   - 成本模型（token 对数正态 × 输入/输出价，缓存字段拆分）。
   - 延迟模型（基准 + 噪声 + 长尾放大）。
   - 受控误判注入（`--misclassify`）。
   - 级联/并行可选生成。
   - `random.Random(seed)` 局部实例保证可复现。
   - 建表（复用 analyze_audit 的 25 列 schema，`routed_tier` TEXT）+ 写入。
2. 验证：
   - `python scripts/generate_audit_data.py --rows 1000 --seed 42` → 生成 `data/audit-demo.db`。
   - `python scripts/analyze_audit.py --db data/audit-demo.db` → 六维报告有数据、分档差异可见。
   - `--misclassify 50` → 报告暴露误判信号。
   - 同 seed 两次生成 → 行集一致（可复现）。
   - 确认不碰真实 `optirouter-budget.db`（默认独立库）。
3. 更新 `.trellis/spec/backend/database-guidelines.md`（如提及审计 schema）与 README（离线审计分析节加生成器用法）。

## Validation

```bash
python scripts/generate_audit_data.py --rows 1000 --seed 42
python scripts/analyze_audit.py --db data/audit-demo.db
python scripts/generate_audit_data.py --rows 1000 --seed 42 --misclassify 50 --db data/audit-demo2.db
python scripts/analyze_audit.py --db data/audit-demo2.db
# 可复现：两次生成同 seed 行数一致
python scripts/generate_audit_data.py --rows 100 --seed 7 --db /tmp/a.db
python scripts/generate_audit_data.py --rows 100 --seed 7 --db /tmp/b.db
python -c "import sqlite3; a=sqlite3.connect('/tmp/a.db'); b=sqlite3.connect('/tmp/b.db'); print(a.execute('SELECT * FROM request_audit').fetchall()==b.execute('SELECT * FROM request_audit').fetchall())"
```

## Risky Files

- `scripts/generate_audit_data.py`（新脚本）
- 验证用临时 DB（`/tmp/*.db`、`data/audit-demo*.db`——注意 gitignore，勿提交）

## Review Gates

- 生成数据可被 `analyze_audit.py` 完整消费，六维报告有数据。
- 分档成本/延迟差异可见；`--misclassify` 暴露误判。
- 同 seed 可复现。
- 零依赖；不破坏真实库。