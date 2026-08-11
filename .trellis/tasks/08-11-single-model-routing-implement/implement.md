# 单模型路由实现 P1+P2+P3 — 执行计划

## 阶段 1：P1 分类准确率可观测

- [ ] 1.1 `RouterDecision` 新增 `ClassificationSignal` / `ClassificationTargetTier` 结构化字段。
- [ ] 1.2 `RuleClassifierPolicy.Apply` 填充结构化字段，保持 `Reason` 的 `target=Tier(signal)` 格式不变。
  - 验证：`RuleClassifierPolicyTests` 新增断言——7 类信号均填充字段 + Reason 含 `target=`。

## 阶段 2：P2 策略链分组契约 + 结构化 Reason

- [ ] 2.1 `PolicyGroup` 枚举 + `IRouterPolicy.Group` 属性；11 个策略声明所属分组。
- [ ] 2.2 `ReasonEvent` record + `RouterDecision.ReasonEvents`；`Reason` 字符串保持原样（不破坏测试断言）。
- [ ] 2.3 各策略 `Apply` 在现有 Reason 拼接之外追加 `ReasonEvent`（结构化、机器可解析）。
- [ ] 2.4 `RouterEngine.Decide` 按分组依赖序执行（Filter→Classify→Order→Constraint），组内保留串行（叠加过滤/fallback/重排语义）。
  - 验证：`RouterEngineTests` 新增「分组执行结果与现有一致」回归测试。
- [ ] 2.5 `Program.cs` 策略注册处无需改（分组由策略自身声明）。

## 阶段 3：P3 上下文老虎机（LinUCB）

- [ ] 3.1 `RoutingOptions` 新增 `EnableContextualBandit` / `ContextualBanditAlpha` / `ContextualBanditDiscountFactor`；`RouterOptionsValidator` 校验。
- [ ] 3.2 新增 `ContextualBanditState`（ArmState: A/b/N，Predict/Update/Retain，线程安全）。
- [ ] 3.3 `LatencyAwarePolicy.ReorderSegment` 优先 `EnableContextualBandit` → LinUCB 打分。
- [ ] 3.4 `OutcomeRecorder` 注入 `ContextualBanditState`，记录 Thompson 奖励时同步更新 bandit（若启用）。
- [ ] 3.5 `Program.cs` 注册 `ContextualBanditState` 单例。
  - 验证：`MultiDimensionalAndBanditTests` / `LatencyAwarePolicyTests` 新增 LinUCB 单测。

## 阶段 4：测试

- [ ] 4.1 新增配置/接口/结构化字段单测（P1 字段、P2 Group、P3 配置校验）。
- [ ] 4.2 策略链并行回归测试（并行 == 串行）。
- [ ] 4.3 LinUCB 单测（上下文影响选型、冷启动、向后兼容）。
- [ ] 4.4 全量 `dotnet test` 通过（现 414 + 新增全绿）。

## 阶段 5：spec + commit

- [ ] 5.1 更新 `.trellis/spec/backend/routing.md`（ParallelGroup 契约、EnableContextualBandit 配置、结构化 Reason 决策记录）。
- [ ] 5.2 Commit。

## 验证命令

```bash
cd src/OptiRouter && dotnet build
cd ../../tests/OptiRouter.Tests && dotnet test
# 全量测试（含新增）
dotnet test
```

## 风险点 / 回滚

- P2 并行正确性：任何组并行结果 != 串行 → 回退该组串行（回归测试锁定）。
- P3 与 Thompson 互斥：`EnableContextualBandit=true` 时 Thompson 段内被替代，需文档明示。
- Reason 格式回归：现有测试断言 Reason 字符串，保持 `policy: detail` 格式。
- 所有新特性默认关，无回滚风险。