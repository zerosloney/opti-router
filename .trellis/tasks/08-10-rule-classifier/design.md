# 设计：规则分级离线验证闭环 + 意图精度审视

Task: 08-10-rule-classifier

## 问题根因

`scripts/analyze_audit.py build_by_reason` 是「数据落盘但无人消费」断口的消费端，其信号关键词表滞后于 `RuleClassifierPolicy` 刚新增的细分 reason。`code-complex`/`code-simple`/`math-detected`/`translation-request` 全部落入 `default` 桶，无法按子类聚合成功率/成本/误判率——规则调优失去实证依据。

## 方案 A：补全脚本信号表（必做，根治闭环）

`scripts/analyze_audit.py` 第 255-259 行 `keywords` 列表追加：

```python
keywords = [
    "code-detected", "code-complex", "code-simple",       # 代码意图子分类
    "math-detected", "translation-request",               # 数学/翻译
    "simple-qa", "complex-instruction",
    "semantic-router: matched", "long-input: filtered", "fallback-to-default",
    "session-affinity: promoted",
]
```

`build_by_reason` 按 `kw in reason` 顺序匹配，`code-detected` 在 `code-complex`/`code-simple` 之前——需确认 reason 字符串中 `code-complex` 含子串 `code-`… 实际 `code-complex` 不含 `code-detected` 子串，顺序无冲突（`code-detected` ≠ `code-complex`）。但注意 `code-complex` 与 `code-simple` 互不包含。安全。`math-detected`/`translation-request` 独立。无歧义。

（`RuleClassifierPolicy` 生成的 reason 形如 `rule-classifier: target=Strong(code-complex)`，子串匹配正确。）

## 方案 B：意图精度审视（本次仅做低成本、可验证的修正）

已核实的判别逻辑健康（三 gotcha 防护到位、测试覆盖全）。**不主动新增启发式**——正确的做法是：补全脚本闭环后，由真实审计数据驱动后续微调（R4.2 的「若有」条件本次不强制扩张）。若实现时发现可低成本修正的精度缺口（如某正则明显误配），则修 + 测试锁定；否则保持现状并记录。

## 验证

构造含各新信号的 reason 样例，跑 `analyze_audit.py` 确认分组：
- 用 `--db` 指向含这类 reason 的测试 DB，或直接构造。脚本只读，安全。

## 兼容性

- 脚本零外部依赖不变；`build_by_reason` 输出 Markdown 结构不变。
- 分类器本身行为不变（本次以闭环补全为主）。
- 无公共 API 变更（脚本非编译产物）。

## Rollback

- 仅脚本 keyword 表追加，回滚即删行。无状态、无迁移。