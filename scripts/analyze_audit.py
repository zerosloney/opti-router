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


def build_fusion(conn: sqlite3.Connection, where: str, params: list,
                 has_fusion_role: bool, has_group: bool) -> str:
    """融合路由（panel→analyst→outer）实证分析。

    回答设计文档的 Q1-Q5：
      Q1 panel 多样性（组内不同模型/provider 数）——质量收益代理
      Q2 融合组总成本 vs 非融合基线成本（成本倍数）
      Q3 analyst 解析失败率
      Q4 融合延迟惩罚（outer 采纳行 vs 非融合）
      Q5 panel 全失败回退串行（组内无 outer 且 panel 全失败）
    列缺失时优雅降级（向后兼容，AC6）。
    """
    lines = ["## Fusion Router\n"]
    if not (has_fusion_role and has_group):
        lines.append("(fusion_role / parallel_group_id column absent — legacy DB)\n")
        return "\n".join(lines) + "\n"

    cur = conn.execute(f"SELECT * FROM request_audit{where}", params)
    rows = cur.fetchall()
    fusion_rows = [r for r in rows if r["fusion_role"] is not None]
    if not fusion_rows:
        lines.append("(no fusion rows in range)\n")
        return "\n".join(lines) + "\n"

    # By FusionRole。
    by_role: dict[str, list[sqlite3.Row]] = {}
    for r in fusion_rows:
        by_role.setdefault(r["fusion_role"], []).append(r)
    lines.append("### By FusionRole\n")
    role_rows = []
    for role in sorted(by_role.keys()):
        a = aggregate(by_role[role])
        role_rows.append([
            role, a["count"], fmt_pct(a["success_rate"]),
            fmt_f(a["latency_p95"], 0), fmt_f(a["total_cost"], 6),
        ])
    lines.append(table(
        ["Role", "Count", "Success", "p95 ms", "Total $"], role_rows))

    # 组级聚合：按 parallel_group_id 分组。
    groups: dict[str, list[sqlite3.Row]] = {}
    for r in fusion_rows:
        gid = r["parallel_group_id"] or "unknown"
        groups.setdefault(gid, []).append(r)

    # Q2: 融合组总成本 vs 非融合基线（融合组内 outer 采纳行成本 vs 同 prompt 单模型成本近似）。
    # 用"非融合请求平均成本"作基线。
    non_fusion = [r for r in rows if r["fusion_role"] is None]
    baseline_avg = aggregate(non_fusion)["total_cost"] / aggregate(non_fusion)["count"] if non_fusion else 0.0
    group_total_cost = sum(r["cost"] for r in fusion_rows)
    fusion_requests = len(groups)
    fusion_avg_cost = group_total_cost / fusion_requests if fusion_requests else 0.0
    cost_multiplier = fusion_avg_cost / baseline_avg if baseline_avg else 0.0

    # Q1: panel 多样性 = 组内不同模型数（provider 用模型名前缀近似）。
    def _provider(model: str) -> str:
        return model.split("-")[0] if "-" in model else model

    panel_rows = by_role.get("panel", [])
    panel_groups: dict[str, set] = {}
    for r in panel_rows:
        gid = r["parallel_group_id"] or "unknown"
        panel_groups.setdefault(gid, set()).add(r["model"])
    diversity_counts = [len(s) for s in panel_groups.values()]
    avg_models_per_group = statistics.mean(diversity_counts) if diversity_counts else 0.0

    # Q3: analyst 失败率。
    analyst_rows = by_role.get("analyst", [])
    analyst_fail = sum(1 for r in analyst_rows if not r["success"])
    analyst_fail_rate = analyst_fail / len(analyst_rows) if analyst_rows else 0.0

    # Q5: panel 全失败且无 outer 的组（回退串行）。
    outer_groups = {r["parallel_group_id"] for r in by_role.get("outer", [])}
    all_panel_fail = 0
    for gid, gs in panel_groups.items():
        g_rows = [r for r in fusion_rows if r["parallel_group_id"] == gid]
        panels = [r for r in g_rows if r["fusion_role"] == "panel"]
        if panels and all(not r["success"] for r in panels) and gid not in outer_groups:
            all_panel_fail += 1

    # Q4: 融合延迟惩罚 = outer 采纳行 p95 vs 非融合 p95。
    outer_p95 = by_role.get("outer", []) and aggregate(by_role["outer"])["latency_p95"] or 0.0
    non_fusion_p95 = aggregate(non_fusion)["latency_p95"] if non_fusion else 0.0

    lines.append("### Group-level metrics\n")
    lines.append(table(
        ["Metric", "Value"],
        [
            ["Fusion groups", fusion_requests],
            ["Fusion total cost (USD)", fmt_f(group_total_cost, 6)],
            ["Avg cost / fusion request (USD)", fmt_f(fusion_avg_cost, 6)],
            ["Non-fusion avg cost (USD)", fmt_f(baseline_avg, 6)],
            ["Cost multiplier (fusion / non-fusion)", fmt_f(cost_multiplier, 2)],
            ["Avg distinct models / panel group", fmt_f(avg_models_per_group, 1)],
            ["Analyst fail rate", fmt_pct(analyst_fail_rate)],
            ["Panel-all-fail groups (serial fallback)", all_panel_fail],
            ["Outer adopted p95 (ms)", fmt_f(outer_p95, 0)],
            ["Non-fusion p95 (ms)", fmt_f(non_fusion_p95, 0)],
        ]
    ))
    lines.append(
        "\n_注：panel 多样性按模型名前缀推断 provider（合成数据可靠；真实数据为近似）。"
        "质量收益无法用合成数据证真，仅作机制性代理。_\n"
    )
    return "\n".join(lines) + "\n"


def build_single_model(conn: sqlite3.Connection, where: str, params: list,
                       has_tier: bool, has_reason: bool) -> str:
    """单模型智能选择路由实证分析。

    回答设计文档的实证问题：
      Q1 分类信号准确率——routing_reason 里的 target=Tier(signal) vs 实际 routed_tier 的混淆。
      Q2 Thompson 奖励分布——routing_reason 里的 thompson: reward=X, round=Y，含每模型 Alpha/Beta 与 regret 代理。
      Q3 成本-质量 Pareto / AIQ——routing_reason 里的 quality=Z 与 cost 构造凸包，比较单模型 vs 融合 vs 基线。
    解析依赖 routing_reason 字符串格式（生成与解析同一约定）；列/数据缺失时优雅降级（AC6）。
    """
    lines = ["## Single-Model Selection\n"]
    if not has_reason:
        lines.append("(routing_reason column absent — legacy DB)\n")
        return "\n".join(lines) + "\n"

    cur = conn.execute(f"SELECT * FROM request_audit{where}", params)
    rows = cur.fetchall()
    if not rows:
        lines.append("(no data in range)\n")
        return "\n".join(lines) + "\n"

    adopted = [r for r in rows if r["is_adopted"]]
    if not adopted:
        adopted = rows  # is_adopted 列可能恒 1；退化用全部行

    # Q1: 分类信号混淆 / 准确率。从 reason 解析 target=Tier(signal)。
    import re as _re
    target_re = _re.compile(r"target=(Strong|Medium|Cheap|Unknown)\(([^)]+)\)")
    sig_rows: dict[str, list[tuple[str, sqlite3.Row]]] = {}  # signal -> [(target_tier, row)]
    for r in adopted:
        reason = r["routing_reason"] or ""
        m = target_re.search(reason)
        if m:
            sig_rows.setdefault(m.group(2), []).append((m.group(1), r))

    if sig_rows:
        lines.append("### Classification signal accuracy\n")
        header = ["Signal", "Count", "Target-tier accurate", "Accuracy"]
        out = []
        correct_total = 0
        count_total = 0
        for sig in sorted(sig_rows.keys()):
            pairs = sig_rows[sig]
            routed_tier_col = "routed_tier"
            acc = 0.0
            correct = 0
            if has_tier:
                for target_tier, r in pairs:
                    actual = r[routed_tier_col] or "unknown"
                    # 目标 tier 与信号应路由 tier 一致判定准确（同档即准）。
                    if actual == target_tier:
                        correct += 1
                        correct_total += 1
                acc = correct / len(pairs) if pairs else 0.0
                count_total += len(pairs)
            out.append([sig, len(pairs), correct, fmt_pct(acc)])
        lines.append(table(header, out))
        if has_tier and count_total:
            lines.append(f"_Overall signal accuracy: {fmt_pct(correct_total / count_total)}_\n")
        else:
            lines.append("_(routed_tier absent — accuracy unavailable, signal counts only)_\n")

    # Q2: Thompson 奖励分布 + regret 代理。解析 thompson: reward=X, round=Y。
    import re as _re2
    thompson_re = _re2.compile(r"thompson: reward=([0-9.]+), round=(\d+)")
    model_alpha: dict[str, list[float]] = {}
    reward_hist: dict[float, int] = {}
    for r in adopted:
        reason = r["routing_reason"] or ""
        m = thompson_re.search(reason)
        if m:
            reward = float(m.group(1))
            reward_hist[reward] = reward_hist.get(reward, 0) + 1
            model_alpha.setdefault(r["model"], []).append(reward)

    if reward_hist:
        lines.append("### Thompson reward distribution\n")
        lines.append(table(
            ["Reward", "Count", "Share"],
            [[k, v, fmt_pct(v / sum(reward_hist.values()))]
             for k, v in sorted(reward_hist.items())]
        ))
        # regret 代理：最优平均奖励（最强模型）vs 各模型平均奖励的差距。
        avg_reward = {m: sum(v) / len(v) for m, v in model_alpha.items()}
        if avg_reward:
            best = max(avg_reward.values())
            lines.append("\n### Thompson regret proxy (per-model avg reward vs best)\n")
            lines.append(table(
                ["Model", "Avg reward", "Samples", "Regret vs best"],
                [[m, fmt_f(avg_reward[m], 3), len(model_alpha[m]),
                  fmt_f(best - avg_reward[m], 3)]
                 for m in sorted(avg_reward.keys(), key=lambda k: -avg_reward[k])]
            ))
            lines.append(
                "\n_注：reward 由生成器按真实语义注入（快成功 1.0/慢成功 0.3/失败 0.0/竞速 0.5）；"
                "regret 为合成数据下的代理，非真实质量。_\n"
            )

    # Q3: 成本-质量 Pareto / AIQ。解析 quality=Z 与 cost。
    import re as _re3
    quality_re = _re3.compile(r"quality=([0-9.]+)")
    model_pq: dict[str, list[tuple[float, float]]] = {}  # model -> [(cost, quality)]
    for r in adopted:
        reason = r["routing_reason"] or ""
        m = quality_re.search(reason)
        if m is not None:
            model_pq.setdefault(r["model"], []).append(
                (r["cost"] or 0.0, float(m.group(1))))

    if model_pq:
        lines.append("### Cost-quality Pareto (AIQ)\n")
        # 每模型聚合 (avg_cost, avg_quality)。
        agg = {m: (sum(c for c, _ in pts) / len(pts), sum(q for _, q in pts) / len(pts))
               for m, pts in model_pq.items()}
        # 凸包（Pareto 前沿）：按成本升序，保留质量不低于已见最大质量的点。
        pts_sorted = sorted(agg.items(), key=lambda kv: kv[1][0])
        frontier: list[tuple[str, float, float]] = []
        max_q = -1.0
        for m, (c, q) in pts_sorted:
            if q > max_q:
                frontier.append((m, c, q))
                max_q = q
        lines.append(table(
            ["Model", "Avg cost $", "Avg quality", "On Pareto frontier"],
            [[m, fmt_f(c, 6), fmt_f(q, 3), "yes" if (m, c, q) in frontier else "no"]
             for m, (c, q) in sorted(agg.items(), key=lambda kv: -kv[1][1])]
        ))
        # AIQ 代理：前沿下面积（梯形积分，按成本归一）。
        if len(frontier) >= 2:
            area = 0.0
            for i in range(1, len(frontier)):
                c0, q0 = frontier[i - 1][1], frontier[i - 1][2]
                c1, q1 = frontier[i][1], frontier[i][2]
                area += (q0 + q1) / 2.0 * (c1 - c0)
            lines.append(f"_AIQ proxy (frontier area under cost): {fmt_f(area, 4)}_\n")
        lines.append(
            "\n_注：quality 为合成代理分数；真实部署需 LLM-as-judge 采样。"
            "Pareto 前沿回答「哪个模型在成本-质量上占优」，融合/级联成本已由其它段给出。_\n"
        )

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
        has_fusion_role = column_exists(conn, "fusion_role")
        has_group = column_exists(conn, "parallel_group_id")
        has_reason = column_exists(conn, "routing_reason")

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
                build_fusion(conn, where, params, has_fusion_role, has_group),
                build_single_model(conn, where, params, has_tier, has_reason),
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
