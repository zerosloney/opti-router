# 强化延迟感知重排（latency-aware）

Child of: 08-10-single-model-routing

## Goal

修复延迟感知路由只看**平均延迟**的缺陷：`ModelLatencyStats` 仅存 avg + count，评分 `1/(avg+50)` 无法反映尾部延迟（p95/p99）。目标：在评分中加入尾部延迟项，避免「avg 好但 tail 差」的模型被选中（交互场景 tail 抖动更伤体验）。

## Background（已核实）

- 数据流：`LatencyStatsAggregatorService`（后台周期）→ `IRequestAuditStore.GetLatencyStatsSince(since)` → `ILatencyStatsProvider.Update` → `LatencyAwarePolicy.ReorderByLatencyScore`。
- `GetLatencyStatsSince` 返回 `(AverageLatencyMs, SampleCount)`（`IRequestAuditStore.cs:57`），SQLite 实现 `AVG(latency_ms), COUNT(*)`（SqliteRequestAuditStore.cs:282-311），InMemory 实现同模式（InMemoryRequestAuditStore.cs:185-）。
- `ModelLatencyStats(double AverageLatencyMs, int SampleCount)`（ILatencyStatsProvider.cs）。
- 评分：`ReorderByLatencyScore` 中 `score = 1/(avg + 50)`，降序；样本不足 `LatencyMinSamples` 的进尾部。
- 现有测试：`LatencyAwarePolicyTests`（StubLatencyStatsProvider 注入 `(Model, AvgMs, Samples)`）。

### 缺陷

无 p95/p99。仅平均延迟会漏掉「avg 稳定但偶发长尾」的模型；且 `ModelLatencyStats` 结构不承载尾部信息。

## Requirements

- R3.1 数据管线携带尾部延迟：`GetLatencyStatsSince` 与 `ModelLatencyStats` 增加 p95（必要时 p99）。
- R3.2 评分加入尾部项，平衡 avg 与 tail（如 `score = 1/(avg + α·p95 + 50)` 或加权），避免 tail 差模型凭 avg 胜出。
- R3.3 `EnableLatencyAware=false` 默认关闭；`LatencyMinSamples`/`LatencyStatsWindowMinutes` 语义兼容。
- R3.4 冷启动透传、样本不足尾部保留、跨 tier 顺序不变等既有行为保持。
- R3.5 现有 `LatencyAwarePolicyTests` 的 avg 排序断言保持（或按新评分语义更新并说明）。

## Acceptance Criteria

- [ ] 两个 avg 接近但 p95 差异大的模型，重排后 p95 优者靠前（tail 项生效）。
- [ ] `GetLatencyStatsSince`（SQLite + InMemory）正确返回 p95。
- [ ] 冷启动/样本不足/单候选/跨 tier 既有测试全绿。
- [ ] 新增测试：tail 项排序、p95 聚合正确性。
- [ ] 无公共接口（`IRouterPolicy`/`RouterDecision`/`RouterContext`）签名变更；`IRequestAuditStore`（内部）可演进。

## Out of Scope

- 引入流式 TTFT 作为排序信号（本次仅端到端 LatencyMs）。
- 引入毫秒级实时测量（继续用后台聚合快照）。
- 改变 `LatencyStatsWindowMinutes` 窗口语义。

## Open Questions

- tail 项权重（α）与是否用 p95 或 p99——设计阶段定夺。