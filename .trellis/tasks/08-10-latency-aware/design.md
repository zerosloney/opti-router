# 设计：延迟感知加入尾部延迟（p95）

Task: 08-10-latency-aware

## 问题根因

`ModelLatencyStats` 只存 `(AverageLatencyMs, SampleCount)`，`GetLatencyStatsSince` 只做 `AVG(latency_ms)`。策略评分 `1/(avg+50)` 只看平均，无法惩罚「avg 稳但 tail 差」的模型。

## 方案：数据管线携带 p95，评分加权

### 1. 数据结构（`ILatencyStatsProvider.cs`）

`ModelLatencyStats` 增加 `P95LatencyMs` 字段：

```csharp
public sealed record ModelLatencyStats(double AverageLatencyMs, double P95LatencyMs, int SampleCount);
```

### 2. 存储契约（`IRequestAuditStore`）

`GetLatencyStatsSince` 返回类型从 `(avg, count)` 元组改为 `ModelLatencyStats`：

```csharp
IReadOnlyDictionary<string, ModelLatencyStats> GetLatencyStatsSince(DateTime since);
```

### 3. SQLite 实现（`SqliteRequestAuditStore`）

`AVG()` 无法直接给 p95。两方案：
- **方案 A（推荐）**：SQL `SELECT model, GROUP_CONCAT(latency_ms)` 拉取窗口内成功延迟，C# 侧排序算 p95。窗口 60min、模型 <50，后台低频聚合，内存可接受。
- 方案 B：SQLite 窗口函数 `PERCENTILE` 不可靠（版本依赖）。弃。

采用方案 A：`SELECT model, latency_ms FROM request_audit WHERE timestamp>=@since AND success=1 ORDER BY model`，C# 按 model 分组 → 排序 → avg + p95（线性插值，复用 `scripts/analyze_audit.py` 的 percentile 逻辑）。

```csharp
// p95 = 线性插值；n==0 跳过；n==1 → 该值
```

### 4. InMemory 实现（`InMemoryRequestAuditStore`）

单次遍历收集每模型延迟列表 → 排序 → avg + p95。O(n) 与现一致。

### 5. 聚合服务（`LatencyStatsAggregatorService`）

无需改——它只把 `ModelLatencyStats` 透传进 `_statsProvider.Update`。

### 6. 策略评分（`LatencyAwarePolicy.ReorderByLatencyScore`）

```csharp
// 原: score = 1 / (avg + 50)
// 新: score = 1 / (avg + 0.5 * p95 + 50)
```

0.5 权重压制 tail 抖动：avg 相同、p95 高者分值更低 → 排后。`LatencyFloorMs=50` 保留防除零。

## 兼容性

- `EnableLatencyAware=false`（默认）：聚合服务跳过（`ExecuteAsync` 仅 `EnableLatencyAware` 时聚合），策略透传——**行为不变**。
- `IRequestAuditStore` 为内部接口（非公开 API），可演进。`IRouterPolicy`/`RouterDecision`/`RouterContext` 不变。
- `ModelLatencyStats` 是 `ILatencyStatsProvider` 的返回记录，构造处需更新（聚合服务、测试 stub）。

## 风险

- `StubLatencyStatsProvider` 测试构造 `(Model, AvgMs, Samples)` 需加 p95 参数——更新测试。
- SQLite `GROUP_CONCAT` 大窗口内存；用 ORDER BY + 逐行累积替代 GROUP_CONCAT 更稳（避免超长字符串）。实现选逐行累积分组。
- p95 线性插值需与 `analyze_audit.py` 的 percentile 语义对齐（`(n-1)*pct/100` 索引）。

## Rollback

- 涉及 4 文件 + 测试。回滚还原 `GetLatencyStatsSince` 签名与 `ModelLatencyStats` 字段。无配置迁移、无 schema 变更（审计表原样）。