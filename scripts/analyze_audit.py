#!/usr/bin/env python3
"""OptiRouter 离线审计分析脚本。

读取 SQLite 审计库（request_audit 表），按模型/分档/级联/路由原因/时间维度聚合，
产出 Markdown 报告。用于闭环"数据落盘但无人消费"的断口，验证规则分类误判率、
各档实际成功率/成本分布，为后续路由策略优化提供实证依据。

零外部依赖，仅标准库 sqlite3。
不写 DB，只读。

用法:
    python scripts/analyze_audit.py [--db PATH] [--from YYYY-MM-DD] [--to YYYY-MM-DD] [--out FILE]

默认 --db 为 data/optirouter-budget.db（与 appsettings.json:Budget:StorePath 一致）。
"""

from __future__ import annotations

import argparse
import sqlite3
import statistics
import sys
from datetime import datetime, timezone
from pathlib import Path

DEFAULT_DB = "data/optirouter-budget.db"


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="Analyze OptiRouter request_audit SQLite table, emit Markdown report."
    )
    p.add_argument("--db", default=DEFAULT_DB, help=f"SQLite DB path (default: {DEFAULT_DB})")
    p.add_argument("--from", dest="from_date", default=None, help="Start date YYYY-MM-DD (inclusive, UTC)")
    p.add_argument("--to", dest="to_date", default=None, help="End date YYYY-MM-DD (inclusive, UTC)")
    p.add_argument("--out", default=None, help="Write report to file instead of stdout")
    return p.parse_args(argv)


def percentile(sorted_values: list[float], pct: float) -> float:
    """线性插值百分位。空列表返回 0.0。"""
    if not sorted_values:
        return 0.0
    if len(sorted_values) == 1:
        return sorted_values[0]
    k = (len(sorted_values) - 1) * (pct / 100.0)
    f = int(k)
    c = min(f + 1, len(sorted_values) - 1)
    if f == c:
        return sorted_values[f]
    return sorted_values[f] + (sorted_values[c] - sorted_values[f]) * (k - f)


def table(headers: list[str], rows: list[list]) -> str:
    """对齐的 Markdown 表格。空 rows 返回 "no data\\n"。"""
    if not rows:
        return "no data\n"
    out = []
    out.append("| " + " | ".join(headers) + " |")
    out.append("| " + " | ".join("---" for _ in headers) + " |")
    for r in rows:
        out.append("| " + " | ".join(str(c) for c in r) + " |")
    return "\n".join(out) + "\n"


def where_clause(args: argparse.Namespace) -> tuple[str, list]:
    """构造时间过滤。timestamp 列为 ISO8601 字符串，字典序与时间序一致。"""
    conds: list[str] = []
    params: list = []
    if args.from_date:
        conds.append("timestamp >= ?")
        params.append(f"{args.from_date}T00:00:00.000Z")
    if args.to_date:
        conds.append("timestamp <= ?")
        params.append(f"{args.to_date}T23:59:59.999Z")
    where = (" WHERE " + " AND ".join(conds)) if conds else ""
    return where, params


def column_exists(conn: sqlite3.Connection, col: str) -> bool:
    cur = conn.execute("PRAGMA table_info(request_audit);")
    return any(row[1].lower() == col.lower() for row in cur.fetchall())


def aggregate(rows: list[sqlite3.Row]) -> dict:
    """对一组行聚合统计。返回 dict 含 count/success_rate/latency/token/cost 等。"""
    n = len(rows)
    if n == 0:
        return {"count": 0}
    successes = sum(1 for r in rows if r["success"])
    latencies = sorted(r["latency_ms"] for r in rows)
    prompts = [r["prompt_tokens"] for r in rows]
    completions = [r["completion_tokens"] for r in rows]
    costs = [r["cost"] for r in rows]
    total_cost = sum(costs)
    total_tokens = sum(p + c for p, c in zip(prompts, completions))
    return {
        "count": n,
        "success_rate": successes / n,
        "latency_avg": statistics.mean(latencies),
        "latency_p50": percentile(latencies, 50),
        "latency_p95": percentile(latencies, 95),
        "prompt_avg": statistics.mean(prompts),
        "completion_avg": statistics.mean(completions),
        "total_cost": total_cost,
        "cost_per_1k": total_cost / n * 1000 if n else 0.0,
        "cost_per_1m_tok": total_cost / total_tokens * 1_000_000 if total_tokens else 0.0,
    }


def fmt_pct(x: float) -> str:
    return f"{x:.1%}"


def fmt_f(x: float, digits: int = 4) -> str:
    return f"{x:.{digits}f}"


def build_summary(conn: sqlite3.Connection, where: str, params: list) -> str:
    cur = conn.execute(f"SELECT * FROM request_audit{where}", params)
    rows = cur.fetchall()
    n = len(rows)
    if n == 0:
        return "no data\n"
    agg = aggregate(rows)
    successes = sum(1 for r in rows if r["success"])
    # 最贵/最慢模型
    by_model: dict[str, list[sqlite3.Row]] = {}
    for r in rows:
        by_model.setdefault(r["model"], []).append(r)
    model_aggs = {m: aggregate(rs) for m, rs in by_model.items()}
    costliest = max(model_aggs.items(), key=lambda kv: kv[1]["total_cost"])
    slowest = max(model_aggs.items(), key=lambda kv: kv[1]["latency_p95"])

    lines = []
    lines.append("## Summary\n")
    lines.append(table(
        ["Metric", "Value"],
        [
            ["Total requests", n],
            ["Successes", successes],
            ["Overall success rate", fmt_pct(agg["success_rate"])],
            ["Total cost (USD)", fmt_f(agg["total_cost"], 6)],
            ["Avg latency (ms)", fmt_f(agg["latency_avg"], 1)],
            ["p95 latency (ms)", fmt_f(agg["latency_p95"], 1)],
            ["Costliest model", f"{costliest[0]} (${fmt_f(costliest[1]['total_cost'], 6)})"],
            ["Slowest model (p95)", f"{slowest[0]} ({fmt_f(slowest[1]['latency_p95'], 1)} ms)"],
        ]
    ))
    return "\n".join(lines) + "\n"


def build_by_model(conn: sqlite3.Connection, where: str, params: list) -> str:
    cur = conn.execute(f"SELECT * FROM request_audit{where}", params)
    rows = cur.fetchall()
    by_model: dict[str, list[sqlite3.Row]] = {}
    for r in rows:
        by_model.setdefault(r["model"], []).append(r)

    lines = ["## By Model\n"]
    rows_out = []
    for model in sorted(by_model.keys()):
        a = aggregate(by_model[model])
        rows_out.append([
            model, a["count"], fmt_pct(a["success_rate"]),
            fmt_f(a["latency_p95"], 0), fmt_f(a["total_cost"], 6),
            fmt_f(a["cost_per_1k"], 6), fmt_f(a["cost_per_1m_tok"], 4),
        ])
    lines.append(table(
        ["Model", "Count", "Success", "p95 ms", "Total $", "$/1k req", "$/1M tok"],
        rows_out
    ))
    return "\n".join(lines) + "\n"


def build_by_tier(conn: sqlite3.Connection, where: str, params: list, has_tier: bool) -> str:
    lines = ["## By Routed Tier\n"]
    if not has_tier:
        lines.append("(routed_tier column absent — legacy DB; upgrade OptiRouter to populate)\n")
        return "\n".join(lines) + "\n"

    cur = conn.execute(f"SELECT * FROM request_audit{where}", params)
    rows = cur.fetchall()
    by_tier: dict[str, list[sqlite3.Row]] = {}
    for r in rows:
        tier = r["routed_tier"] or "unknown"
        by_tier.setdefault(tier, []).append(r)

    rows_out = []
    for tier in sorted(by_tier.keys()):
        a = aggregate(by_tier[tier])
        rows_out.append([
            tier, a["count"], fmt_pct(a["success_rate"]),
            fmt_f(a["latency_p95"], 0), fmt_f(a["prompt_avg"], 0), fmt_f(a["completion_avg"], 0),
            fmt_f(a["total_cost"], 6),
        ])
    lines.append(table(
        ["Tier", "Count", "Success", "p95 ms", "Avg prompt", "Avg compl", "Total $"],
        rows_out
    ))
    lines.append(
        "\n_Rule misclassification signal: Cheap tier with low success rate, or Strong tier "
        "handling trivially short prompts, suggests rule over/under-triggering._\n"
    )
    return "\n".join(lines) + "\n"


def build_cascade(conn: sqlite3.Connection, where: str, params: list, has_cascade: bool) -> str:
    lines = ["## Cascade Upgrade\n"]
    if not has_cascade:
        lines.append("(cascade_triggered column absent — legacy DB)\n")
        return "\n".join(lines) + "\n"

    cur = conn.execute(f"SELECT * FROM request_audit{where}", params)
    rows = cur.fetchall()
    total = len(rows)
    triggered = [r for r in rows if r["cascade_triggered"]]
    upgraded = [r for r in triggered if r["upgraded_from"]]
    by_src: dict[str, int] = {}
    for r in upgraded:
        by_src[r["upgraded_from"]] = by_src.get(r["upgraded_from"], 0) + 1

    lines.append(table(
        ["Metric", "Value"],
        [
            ["Total requests", total],
            ["Cascade triggered (self-verify)", len(triggered)],
            ["Trigger rate", fmt_pct(len(triggered) / total) if total else "n/a"],
            ["Upgraded to Strong", len(upgraded)],
            ["Upgrade rate", fmt_pct(len(upgraded) / total) if total else "n/a"],
        ]
    ))
    if by_src:
        lines.append("\n### Upgrade sources\n")
        lines.append(table(
            ["Upgraded from", "Count"],
            [[k, v] for k, v in sorted(by_src.items(), key=lambda kv: -kv[1])]
        ))
    return "\n".join(lines) + "\n"


def build_by_reason(conn: sqlite3.Connection, where: str, params: list) -> str:
    """按 routing_reason 分组聚合，作为规则误判率的代理指标。

    routing_reason 是分号拼接的决策链字符串；取首个非策略名片段做粗分组。
    精确判定需人工结合 reason 文本复核，脚本只给分组统计。
    """
    cur = conn.execute(f"SELECT * FROM request_audit{where}", params)
    rows = cur.fetchall()
    if not rows:
        return "## By Routing Reason\n\nno data\n"

    # 抽 reason 中含的 classifier 信号（code-detected/simple-qa/semantic-matched/default 等）。
    signals: dict[str, list[sqlite3.Row]] = {}
    keywords = [
        "code-detected", "code-complex", "code-simple",   # 代码意图子分类
        "math-detected", "translation-request",           # 数学/翻译
        "simple-qa", "complex-instruction",
        "semantic-router: matched", "long-input: filtered", "fallback-to-default",
        "session-affinity: promoted",
        "cancelled-by-race",  # 竞速失败（并行 racing 中被更快者比下去而取消）
    ]
    for r in rows:
        reason = r["routing_reason"] or ""
        matched = "default"
        for kw in keywords:
            if kw in reason:
                matched = kw
                break
        signals.setdefault(matched, []).append(r)

    lines = ["## By Routing Reason Signal\n"]
    rows_out = []
    for sig in sorted(signals.keys()):
        a = aggregate(signals[sig])
        rows_out.append([
            sig, a["count"], fmt_pct(a["success_rate"]),
            fmt_f(a["total_cost"], 6), fmt_f(a["prompt_avg"], 0),
        ])
    lines.append(table(
        ["Signal", "Count", "Success", "Total $", "Avg prompt"],
        rows_out
    ))
    lines.append(
        "\n_Misclassification proxy: a signal with low success or abnormal cost vs. peers "
        "(e.g. 'code-detected' on short natural-language) warrants manual reason audit._\n"
    )
    return "\n".join(lines) + "\n"


def build_daily_trend(conn: sqlite3.Connection, where: str, params: list) -> str:
    lines = ["## Daily Trend\n"]
    # timestamp 形如 '2026-08-07T12:34:56.789Z'；日期 = 前 10 字符。
    sql = f"SELECT substr(timestamp,1,10) AS day, COUNT(*) AS n, SUM(cost) AS cost, SUM(success) AS ok FROM request_audit{where} GROUP BY day ORDER BY day ASC;"
    cur = conn.execute(sql, params)
    rows_out = []
    for day, n, cost, ok in cur.fetchall():
        rate = (ok or 0) / n if n else 0.0
        rows_out.append([day, n, fmt_pct(rate), fmt_f(cost or 0.0, 6)])
    lines.append(table(["Date", "Requests", "Success", "Cost $"], rows_out))
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)

    db_path = Path(args.db)
    if not db_path.exists():
        print(f"error: DB file not found: {db_path}", file=sys.stderr)
        print(f"hint: default path is '{DEFAULT_DB}' (appsettings.json Budget:StorePath); pass --db PATH", file=sys.stderr)
        return 1

    try:
        conn = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
        conn.row_factory = sqlite3.Row
        # 表存在性检查
        tbl = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='request_audit';"
        ).fetchone()
        if tbl is None:
            print(f"error: table 'request_audit' not found in {db_path}", file=sys.stderr)
            return 1

        where, params = where_clause(args)
        has_tier = column_exists(conn, "routed_tier")
        has_cascade = column_exists(conn, "cascade_triggered")

        total = conn.execute(f"SELECT COUNT(*) FROM request_audit{where}", params).fetchone()[0]
        if total == 0:
            report = f"# OptiRouter Audit Report\n\nGenerated {datetime.now(timezone.utc).isoformat()}\nDB: {db_path}\n\n**no data** in selected range.\n"
        else:
            parts = [
                f"# OptiRouter Audit Report\n\nGenerated {datetime.now(timezone.utc).isoformat()}\nDB: {db_path}\n",
                build_summary(conn, where, params),
                build_by_model(conn, where, params),
                build_by_tier(conn, where, params, has_tier),
                build_cascade(conn, where, params, has_cascade),
                build_by_reason(conn, where, params),
                build_daily_trend(conn, where, params),
            ]
            report = "\n".join(parts)
    except sqlite3.Error as e:
        print(f"error: sqlite failure: {e}", file=sys.stderr)
        return 1
    finally:
        if 'conn' in locals():
            conn.close()

    if args.out:
        Path(args.out).write_text(report, encoding="utf-8")
        print(f"report written to {args.out}")
    else:
        print(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
