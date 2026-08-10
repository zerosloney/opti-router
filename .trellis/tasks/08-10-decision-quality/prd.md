# Strengthen Single-Model Decision Routing — Code-Intent Sub-classification

## Goal

强化单模型智能选择路由的**决策质量**，聚焦**代码能力优先**：让 `RuleClassifierPolicy` 不再一律 `code→Strong`，而是按代码意图细分——
- 调试/修复、重构/优化、复杂算法 → **Strong**
- 简单代码生成（hello world、脚手架）、代码解释 → **Medium**

从而在「代码能力优先」前提下提升成本/质量权衡：明确简单的代码任务不再浪费 Strong 模型。

## Background / Confirmed Facts

### 当前决策链（Program.cs:171-184）
`CapabilityFilter → RuleClassifier → SessionAffinity → SemanticRouter → LongInput → LatencyAware → PromptCacheAffinity → BudgetGuard → QuotaAware → Failover → LoadBalance`

### 当前 RuleClassifierPolicy 分档（RuleClassifierPolicy.cs ClassifyRequest）
| 特征 | Tier | Reason | Complexity |
|------|------|--------|-----------|
| 代码（`CodeIndicatorRegex` 命中） | Strong | code-detected | Complex |
| 数学/公式 | Strong | math-detected | Complex |
| 多轮 + 长 system prompt | Strong | complex-instruction | Complex |
| 翻译 | Medium | translation-request | Standard |
| 单条短消息(<100字) 无代码 | Cheap | simple-qa | Simple |
| 其余 | DefaultTier | default | Standard |

**当前缺陷**：`hasCode` 一旦为真，无条件返回 `(Strong, code-detected, Complex)`。hello world、代码解释、脚手架等简单代码任务被一律路由到 Strong——成本浪费。

代码检测：`CodeIndicatorRegex`（代码块/语言关键字/import/SQL/系统命令等，低误报）。

### 多维路由权重（GetWeightsForClassification）
code-detected: coding=1.0/reasoning=0.6/language=0.3；math: reasoning=1.0/coding=0.5/language=0.3；complex: reasoning=0.8/language=0.7；translation: language=1.0/coding=0.1；simple-qa: language=1.0/reasoning=0.1；default: language=0.8/reasoning=0.5。

### RequestComplexity
`Unknown / Simple / Standard / Complex`。

### 审计闭环
`scripts/analyze_audit.py` 按 Routed Tier / Routing Reason Signal 报误判信号，支持按 reason 关键词（含新增 code-* reason）复核。

## Requirements

### R1 代码意图细分
- 当 `hasCode == true` 时，对请求文本做代码意图子分类：
  - **复杂代码意图**（debug/fix/修复/调试/重构/优化/崩溃/报错/异常/算法/性能/复杂度）→ `(Strong, code-complex, Complex)`
  - **简单代码意图**（hello world/示例/简单的/脚手架/解释/讲解/入门/what does this/explain/example）→ `(Medium, code-simple, Standard)`
  - **无明确意图**（代码存在但无复杂/简单信号）→ 保守保持 `(Strong, code-detected, Complex)`（代码能力优先，宁过度不低估）
- 优先级：complex 信号 > simple 信号 > 默认 Strong。简单信号不覆盖复杂信号。

### R2 多维路由权重
- 新增 `code-complex` 与 `code-simple` 权重：
  - code-complex: coding=1.0/reasoning=0.8/language=0.2（比普通代码更高 reasoning）
  - code-simple: coding=1.0/reasoning=0.3/language=0.4（coding 主导但简单）

### R3 低误报
- 复杂/简单意图正则复用 request 全文（`ConcatMessages`），避免跨消息截断。
- 简单信号不得把含 debug/fix/error 的代码误降级（complex 优先已保证）。

### R4 测试
- 复杂代码（debug/fix/重构/算法）→ Strong + `code-complex`
- 简单代码（hello world/解释/示例）→ Medium + `code-simple`
- 裸代码块（无意图词）→ 仍 Strong + `code-detected`
- code 请求不被 math/translation/simple-qa 抢占
- 既有 3 个多维排序测试不回归

## Acceptance Criteria

- [ ] 含 debug/fix/修复/重构/算法 的代码请求 → `code-complex`，Tier=Strong，Complexity=Complex
- [ ] 含 hello world/解释/示例/脚手架 的代码请求 → `code-simple`，Tier=Medium，Complexity=Standard
- [ ] 裸代码块（```...``` 无意图词）→ 仍 `code-detected`，Tier=Strong（代码能力优先）
- [ ] 复杂+简单信号同现时，complex 优先（不降级）
- [ ] 多维路由权重含 code-complex/code-simple 分支
- [ ] 全量测试通过（`dotnet test`）

## Out of Scope

- 长文档分析、创意写作、非中英语言检测、软分类/置信度
- 成本账本、缓存命中、自适应学习、鲁棒性增强
- 新依赖引入

## Decisions

- 代码意图细分：复杂→Strong，简单→Medium，无明确意图→保守 Strong（代码能力优先）
- 简单代码用「明确 simple/explain 信号」触发降级，避免误伤