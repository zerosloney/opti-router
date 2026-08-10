#!/usr/bin/env python3
"""OptiRouter 合成审计数据生成器。

生成符合 request_audit 表 schema 的合成数据，用于打通「落盘 → analyze_audit → 人工看信号 → 调参」数据闭环。
零外部依赖，仅标准库（sqlite3/random/argparse/datetime）。只写不读既有库（默认写独立库）。

用法:
    python scripts/generate_audit_data.py [--rows N] [--seed S] [--db PATH]
        [--append] [--misclassify N] [--cascade-rate R] [--parallel-rate R] [--models-json PATH]

默认 --db 为 data/audit-demo.db（独立演示库，不碰真实 data/optirouter-budget.db）。
--append 显式开启才向既有库追加（不建表/不覆盖）。
同 --seed 输出可复现。
"""

from __future__ import annotations

import argparse
import json
import random
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path

DEFAULT_DB = "data/audit-demo.db"

# request_audit 表 25 列（与 SqliteRequestAuditStore INSERT 顺序一致；routed_tier 为 TEXT）。
COLUMNS = [
    "timestamp", "request_id", "model", "estimated_tokens", "prompt_tokens",
    "completion_tokens", "cost", "latency_ms", "session_id", "routing_reason",
    "success", "error_message", "is_streaming", "routed_tier", "cascade_triggered",
    "upgraded_from", "is_adopted", "parallel_group_id", "is_estimated", "fusion_role",
    "ttft_ms", "cached_input_tokens", "cache_write_input_tokens", "uncached_input_tokens",
    "quota_limited",
]

SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS request_audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    request_id TEXT NOT NULL,
    model TEXT NOT NULL,
    estimated_tokens INTEGER NOT NULL,
    prompt_tokens INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0,
    cost REAL NOT NULL DEFAULT 0,
    latency_ms INTEGER NOT NULL DEFAULT 0,
    session_id TEXT,
    routing_reason TEXT NOT NULL,
    success INTEGER NOT NULL,
    error_message TEXT,
    is_streaming INTEGER NOT NULL DEFAULT 0,
    routed_tier TEXT,
    cascade_triggered INTEGER NOT NULL DEFAULT 0,
    upgraded_from TEXT,
    is_adopted INTEGER NOT NULL DEFAULT 1,
    parallel_group_id TEXT,
    is_estimated INTEGER NOT NULL DEFAULT 0,
    fusion_role TEXT,
    ttft_ms INTEGER,
    cached_input_tokens INTEGER NOT NULL DEFAULT 0,
    cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,
    uncached_input_tokens INTEGER NOT NULL DEFAULT 0,
    quota_limited INTEGER NOT NULL DEFAULT 0
);
"""

# 默认模型画像：tier 越高成本高/延迟高/更稳，tier 越低便宜/快/成功率略低。
DEFAULT_MODELS = [
    {"name": "gpt-4o", "tier": "Strong", "in_price": 2.5, "out_price": 10.0,
     "latency_base": 900, "success_rate": 0.99},
    {"name": "gpt-4o-mini", "tier": "Medium", "in_price": 0.15, "out_price": 0.6,
     "latency_base": 500, "success_rate": 0.97},
    {"name": "deepseek-chat", "tier": "Cheap", "in_price": 0.01, "out_price": 0.03,
     "latency_base": 300, "success_rate": 0.93},
]
BY_TIER = {m["tier"]: m for m in DEFAULT_MODELS}

# 分类信号分布：signal -> (routing_reason 片段中的目标 tier, reason 片段, 权重)。
# 权重经归一化后决定请求的信号占比。
SIGNALS = [
    # (signal, 应路由 tier, reason 片段, 权重)
    ("code-complex", "Strong", "rule-classifier: target=Strong(code-complex)", 15),
    ("code-simple", "Medium", "rule-classifier: target=Medium(code-simple)", 10),
    ("math-detected", "Strong", "rule-classifier: target=Strong(math-detected)", 8),
    ("translation-request", "Medium", "rule-classifier: target=Medium(translation-request)", 8),
    ("simple-qa", "Cheap", "rule-classifier: target=Cheap(simple-qa)", 35),
    ("complex-instruction", "Strong", "rule-classifier: target=Strong(complex-instruction)", 7),
    ("default", "Medium", "rule-classifier: target=Medium(default)", 17),
]
# 适合做「误判」的信号（本该 Strong，被错误路由到 Cheap）。
MISCLASSIFY_SOURCES = ["code-complex", "math-detected"]


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description="Generate synthetic OptiRouter request_audit data.")
    p.add_argument("--rows", type=int, default=1000, help="Number of audit rows (default: 1000)")
    p.add_argument("--seed", type=int, default=42, help="RNG seed for reproducibility (default: 42)")
    p.add_argument("--db", default=DEFAULT_DB, help=f"SQLite output path (default: {DEFAULT_DB})")
    p.add_argument("--append", action="store_true", help="Append to existing DB instead of recreating")
    p.add_argument("--misclassify", type=int, default=0,
                   help="Inject N rows misrouted to Cheap that should be Strong (default: 0)")
    p.add_argument("--cascade-rate", type=float, default=0.0,
                   help="Fraction of Cheap-tier requests with cascade_triggered (Cheap->Strong) in [0,1]")
    p.add_argument("--parallel-rate", type=float, default=0.0,
                   help="Fraction of rows generated as parallel-race/fusion (is_adopted/group/fusion_role)")
    p.add_argument("--models-json", default=None, help="Optional JSON file with model profiles override")
    return p.parse_args(argv)


def lognormal(rng: random.Random, mu: float, sigma: float) -> float:
    """对数正态采样，截断非负。"""
    return max(0.0, rng.lognormvariate(mu, sigma))


def build_model_weights(models: list[dict]) -> dict[str, dict]:
    """模型集按 tier 归组（供按 tier 选模型）。"""
    by_tier: dict[str, list[dict]] = {}
    for m in models:
        by_tier.setdefault(m["tier"], []).append(m)
    return by_tier


def generate_rows(args: argparse.Namespace, rng: random.Random) -> list[tuple]:
    """生成 row 列表（tuple 顺序 = COLUMNS）。"""
    models = DEFAULT_MODELS
    if args.models_json:
        with open(args.models_json, encoding="utf-8") as f:
            models = json.load(f)
    by_tier = build_model_weights(models)

    # 信号权重归一化。
    total_w = sum(s[3] for s in SIGNALS)
    signal_pool = [(s[0], s[1], s[2], s[3] / total_w) for s in SIGNALS]
    # 级联只发生在 Cheap 档请求。
    cheap_signals = [s for s in signal_pool if s[1] == "Cheap"]
    cheap_signals_pool = [(s[0], s[1], s[2], s[3]) for s in cheap_signals]

    rows: list[tuple] = []
    # 固定基准时间（非 wall-clock），保证同 seed 完全可复现。spread 由 rng 决定。
    now = datetime(2026, 8, 10, 12, 0, 0, tzinfo=timezone.utc)
    group_counter = 0

    for i in range(args.rows):
        # 选信号（按权重）。
        r = rng.random()
        acc = 0.0
        signal = signal_pool[-1]
        for s in signal_pool:
            acc += s[3]
            if r <= acc:
                signal = s
                break
        _sig, target_tier, reason_frag, _w = signal

        # 模型：默认按目标 tier 选；部分（8%）随机换 tier 模拟路由抖动。
        tier = target_tier
        if rng.random() < 0.08:
            tier = rng.choice([m["tier"] for m in models])
        model = rng.choice(by_tier.get(tier, DEFAULT_MODELS))

        # token 量。
        prompt = int(lognormal(rng, 6.2, 0.8))  # ~500 中位
        completion = int(lognormal(rng, 5.3, 0.9))  # ~200 中位
        # 缓存拆分：30% 缓存命中，20% 缓存写入，其余未缓存。
        cached = int(prompt * (0.3 if rng.random() < 0.3 else 0))
        cachewrite = int(prompt * (0.2 if rng.random() < 0.2 else 0))
        uncached = max(0, prompt - cached - cachewrite)

        # 成本。
        cost = (prompt * model["in_price"] + completion * model["out_price"]) / 1_000_000.0

        # 延迟：基准 + 噪声 + 5% 长尾放大。
        latency = model["latency_base"] + rng.gauss(0, 150)
        if rng.random() < 0.05:
            latency *= rng.uniform(2.0, 5.0)
        latency = int(max(20, latency))
        ttft = int(latency * rng.uniform(0.3, 0.6))

        # 成功/失败。
        success = rng.random() < model["success_rate"]
        error = None if success else rng.choice(["upstream-status-500", "network-error", "timeout"])

        # routing_reason：把 reason 片段嵌入完整 reason（模拟决策链）。
        cascade = False
        upgraded_from = None
        if target_tier == "Cheap" and args.cascade_rate > 0 and rng.random() < args.cascade_rate:
            cascade = True
            upgraded_from = model["name"]
            reason_frag += "; cascade: upgraded from " + model["name"]

        routing_reason = reason_frag + "; latency-aware: disabled; load-balance: disabled"

        # 并行/融合。
        is_adopted = 1
        parallel_group_id = None
        is_estimated = 0
        fusion_role = None
        if args.parallel_rate > 0 and rng.random() < args.parallel_rate:
            group_counter += 1
            parallel_group_id = f"g{group_counter}"
            is_adopted = 1 if rng.random() < 0.6 else 0
            is_estimated = 0 if is_adopted else 1
            fusion_role = "panel" if is_adopted else "cancelled-by-race"
            reason_frag += "; fusion: " + ("adopted" if is_adopted else "cancelled-by-race")

        request_id = f"{now.strftime('%Y%m%d%H%M%S')}-{i:06d}"
        ts = (now - timedelta(seconds=int(rng.uniform(0, 86400)))).strftime("%Y-%m-%dT%H:%M:%S.000Z")
        session_id = f"sess-{int(rng.uniform(0, 50))}"

        rows.append((
            ts, request_id, model["name"], prompt, prompt, completion, round(cost, 8),
            latency, session_id, routing_reason, 1 if success else 0, error,
            0, tier, 1 if cascade else 0, upgraded_from, is_adopted, parallel_group_id,
            is_estimated, fusion_role, ttft, cached, cachewrite, uncached, 0,
        ))

    # 受控误判注入：把 args.misclassify 行本该 Strong 的信号强制路由到 Cheap。
    # 从 MISCLASSIFY_SOURCES 信号里取，保持 reason 为 Strong 信号但 routed_tier 写 Cheap。
    for j in range(min(args.misclassify, len(rows))):
        idx = rng.randrange(len(rows))
        old = list(rows[idx])
        src = rng.choice(MISCLASSIFY_SOURCES)
        reason_frag = f"rule-classifier: target=Strong({src})"
        # 改成用 Cheap 模型。
        cheap_model = rng.choice(by_tier.get("Cheap", DEFAULT_MODELS))
        old[3] = old[4]  # 用同一 prompt
        old[0] = old[0]  # ts 不变
        old[2] = cheap_model["name"]
        old[9] = reason_frag + "; latency-aware: disabled"
        old[13] = "Cheap"  # routed_tier 误写 Cheap
        old[6] = round((old[4] * cheap_model["in_price"] + old[5] * cheap_model["out_price"]) / 1_000_000.0, 8)
        rows[idx] = tuple(old)

    return rows


def write_db(path: str, rows: list[tuple], append: bool) -> None:
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(path)
    try:
        if not append:
            conn.execute("DROP TABLE IF EXISTS request_audit;")
        conn.execute(SCHEMA_SQL)
        placeholders = ",".join("?" * len(COLUMNS))
        conn.executemany(
            f"INSERT INTO request_audit ({','.join(COLUMNS)}) VALUES ({placeholders})", rows)
        conn.commit()
    finally:
        conn.close()


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    rng = random.Random(args.seed)
    rows = generate_rows(args, rng)
    write_db(args.db, rows, args.append)
    print(f"Generated {len(rows)} rows -> {args.db} (seed={args.seed})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())