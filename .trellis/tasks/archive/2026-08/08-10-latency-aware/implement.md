# 执行：延迟感知加入尾部延迟（p95）

Task: 08-10-latency-aware

## Checklist

1. `src/OptiRouter/Routing/ILatencyStatsProvider.cs`：`ModelLatencyStats` 增加 `P95LatencyMs` 字段。
2. `src/OptiRouter/Routing/IRequestAuditStore.cs`：`GetLatencyStatsSince` 返回类型改为 `IReadOnlyDictionary<string, ModelLatencyStats>`。
3. `src/OptiRouter/Routing/SqliteRequestAuditStore.cs`：`GetLatencyStatsSince` 改为按 model 逐行拉取成功延迟 → C# 分组排序算 avg + p95（线性插值）。
4. `src/OptiRouter/Routing/InMemoryRequestAuditStore.cs`：同改，单次遍历收集每模型延迟列表 → 排序 → avg + p95。
5. `src/OptiRouter/Routing/LatencyAwarePolicy.cs`：`ReorderByLatencyScore` 评分改为 `1/(avg + 0.5×p95 + 50)`。
6. `src/OptiRouter/Health/LatencyStatsAggregatorService.cs`：确认只透传 `ModelLatencyStats`，无需改（核对）。
7. 更新测试：
   - `LatencyAwarePolicyTests.StubLatencyStatsProvider` 构造加 p95。
   - 新增：avg 相近但 p95 差异大 → tail 优者靠前。
   - 新增：`GetLatencyStatsSince` p95 聚合正确（SQLite + InMemory）。
   - 既有 avg 排序/冷启动/样本不足/单候选/跨 tier 断言保持或按新语义更新。
8. 更新 `.trellis/spec/backend/routing.md`（延迟统计契约 + 评分公式）。

## Validation

```bash
dotnet build OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~LatencyAwarePolicyTests"
dotnet test OptiRouter.sln -c Release
```

## Risky Files

- `src/OptiRouter/Routing/IRequestAuditStore.cs` + 两个实现（接口签名变更）
- `src/OptiRouter/Routing/ILatencyStatsProvider.cs`（记录字段变更）
- `src/OptiRouter/Routing/LatencyAwarePolicy.cs`（评分公式）
- `src/OptiRouter/Health/LatencyStatsAggregatorService.cs`（透传核对）
- 测试 stub + 审计存储测试

## Review Gates

- `dotnet build` + 全量测试绿。
- p95 聚合在 SQLite 与 InMemory 两实现均有测试。
- 冷启动/样本不足/跨 tier 行为不变。
- 无公开 API（`IRouterPolicy`/`RouterDecision`/`RouterContext`）变更。