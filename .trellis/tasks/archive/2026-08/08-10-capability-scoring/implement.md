# 执行：多维能力评分维度化 tier 回退

Task: 08-10-capability-scoring

## Checklist

1. 修改 `src/OptiRouter/Configuration/ModelEndpointOptions.cs`：
   - 新增 `DimensionFallbacks` 静态维度回退表（language 近扁平 0.80/0.78/0.76；reasoning 陡 0.90/0.50/0.20；coding 陡 0.90/0.60/0.30）。
   - `GetEffectiveCapability` 改为：显式 `Capabilities` 优先 → 维度表 → 未知维度回退 0.5。
2. 核对并更新 `tests/OptiRouter.Tests/Routing/MultiDimensionalAndBanditTests.cs`：
   - 检查现有 match-score/close-scores/gap 断言是否依赖旧 tier 回退值；按新维度语义更新并加注释。
   - 新增测试：simple-qa 择廉（Cheap 同桶价胜）、math/reasoning 保优（Strong 分桶胜）、显式 `Capabilities` 优先、未知维度回退 0.5。
3. 更新 `.trellis/spec/backend/routing.md` 的多维评分契约段（维度化回退表）。

## Validation

```bash
dotnet build OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~MultiDimensionalAndBanditTests"
dotnet test OptiRouter.sln -c Release
```

## Risky Files

- `src/OptiRouter/Configuration/ModelEndpointOptions.cs`（核心改动）
- `tests/OptiRouter.Tests/Routing/MultiDimensionalAndBanditTests.cs`（断言可能需更新）

## Review Gates

- 数学推导（design.md 的 simple-qa / math 两例）与测试结果一致。
- `dotnet build` + 全量测试绿。
- 无公共 API 签名变更。