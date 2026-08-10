# 执行：规则分级离线验证闭环

Task: 08-10-rule-classifier

## Checklist

1. `scripts/analyze_audit.py`：`build_by_reason` 的 `keywords` 列表追加 `code-complex`/`code-simple`/`math-detected`/`translation-request`。
2. 审视 `RuleClassifierPolicy` 意图判别：仅修正可低成本验证的精度缺口（若有），否则保持现状。
3. 验证分组：构造含各新信号的 reason 样例，确认 `build_by_reason` 不再归入 `default`。
4. 更新 `.trellis/spec/backend/routing.md`（若分类器有修正）与 README 的信号列（若提及）。

## Validation

```bash
# 构造含新信号的审计 reason，跑脚本验证分组
python scripts/analyze_audit.py --db <test-db>   # 或注入样例后检查 By Routing Reason Signal
dotnet build OptiRouter.sln -c Release
dotnet test OptiRouter.sln -c Release --filter "FullyQualifiedName~RuleClassifierPolicyTests"
dotnet test OptiRouter.sln -c Release
```

若分类器有修正：新增 `RuleClassifierPolicyTests` 用例。

## Risky Files

- `scripts/analyze_audit.py`（keyword 表）
- 可选：`src/OptiRouter/Routing/RuleClassifierPolicy.cs`（仅精度缺口修正时）

## Review Gates

- `build_by_reason` 覆盖全部新信号。
- 脚本跑通（可用样例 DB 验证）。
- 分类器改动（若有）有测试锁定；`RuleClassifierPolicyTests` 全绿。