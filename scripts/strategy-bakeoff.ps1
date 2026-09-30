# 策略横评 harness：同一金标准数据集上，用管理 API 热切换路由策略配置并逐策略跑评测批，
# 产出质量/延迟/成本对比。方法论与报告解读见 docs/strategy-bakeoff.md。
#
# 用法（对已运行、已配置模型与租户 Key 的实例）：
#   powershell -ExecutionPolicy Bypass -File scripts\strategy-bakeoff.ps1 `
#     -BaseUrl http://localhost:5080 -AdminKey <AdminApiKey> `
#     [-CasesPath bench\strategy-bakeoff\cases.json] [-Strategies baseline-cheap,balanced] `
#     [-QualityJudgeModel "stepfun/step-2-16k"] [-OutDir reports\strategy-bakeoff]
#
# 前提：实例已配置至少覆盖 Cheap/Medium/Strong 三档的启用模型（横评才有档位意义）；
#       评测走真实路由与上游计费，响应头 X-Eval-Consumes-Budget 恒为 true。
# 行为：逐策略 PUT 配置（CAS 热生效）→ 学习状态归零（防跨策略串扰）→ 评测 → 保存原始报告；
#       结束后把被改字段恢复为初始值（-SkipRestore 跳过）。

param(
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$AdminKey,
    [string]$CasesPath,
    [string[]]$Strategies = @("baseline-cheap", "baseline-strong", "cost-first", "balanced", "quality-first"),
    [string]$QualityJudgeModel = "",
    [string]$OutDir,
    [switch]$SkipRestore
)

# PS 5.1 下 $PSScriptRoot 在参数默认值里可能为空，统一在函数体解析。
if (-not $CasesPath) { $CasesPath = Join-Path $PSScriptRoot "..\bench\strategy-bakeoff\cases.json" }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "..\reports\strategy-bakeoff" }

# PS 5.1 下 -File 传参 "-Strategies a,b,c" 绑成单字符串，这里拆开
if ($Strategies.Count -eq 1 -and $Strategies[0] -match ",") {
    $Strategies = $Strategies[0].Split(",") | ForEach-Object { $_.Trim() }
}

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$headers = @{ Authorization = "Bearer $AdminKey" }

# ── 策略定义：每个策略显式给出全部相关字段（含显式 false），杜绝上一策略的开关残留串扰 ──
# Tiers = 该策略下启用的模型档位集（真实部署的第一杠杆：cheap-only 部署 vs 全池智能路由），
#         其余档位模型临时 Enabled=false，评测后恢复。
# 字段集 = UpdateSystemConfigRequest DTO 中与策略正交的子集；DTO 未暴露的字段
# （如 CascadeUpgradeVerifierModel/CostAwareWeight）需在实例上预先配置，本脚本不动。
# DefaultTier 仅在规则分类未命中时兜底——档位组合的硬控制靠 Tiers，不靠 DefaultTier。
$script:StrategyDefs = @{
    "baseline-cheap" = @{
        Description            = "仅 Cheap 档模型（成本基线：不部署中/高档）"
        Tiers                  = @("Cheap")
        DefaultTier            = "Cheap"
        EnableThompsonSampling = $false
        EnableContextualBandit = $false
        EnableLatencyAware     = $false
        EnableCascadeUpgrade   = $false
        EnableFusionRouter     = $false
        EnableFusionMode       = $false
    }
    "baseline-strong" = @{
        Description            = "仅 Strong 档模型（质量基线：不部署中/低档）"
        Tiers                  = @("Strong")
        DefaultTier            = "Strong"
        EnableThompsonSampling = $false
        EnableContextualBandit = $false
        EnableLatencyAware     = $false
        EnableCascadeUpgrade   = $false
        EnableFusionRouter     = $false
        EnableFusionMode       = $false
    }
    "full-pool" = @{
        Description            = "全池直通：分类器+故障转移，学习/级联/融合全关（路由器开箱行为）"
        Tiers                  = @("Cheap", "Medium", "Strong")
        DefaultTier            = "Medium"
        EnableThompsonSampling = $false
        EnableContextualBandit = $false
        EnableLatencyAware     = $false
        EnableCascadeUpgrade   = $false
        EnableFusionRouter     = $false
        EnableFusionMode       = $false
    }
    "cost-first" = @{
        Description            = "成本优先：全池 + Thompson + ε 探索 + 延迟感知"
        Tiers                  = @("Cheap", "Medium", "Strong")
        DefaultTier            = "Cheap"
        EnableThompsonSampling = $true
        EnableContextualBandit = $false
        ExplorationEpsilon     = 0.05
        EnableLatencyAware     = $true
        EnableCascadeUpgrade   = $false
        EnableFusionRouter     = $false
        EnableFusionMode       = $false
    }
    "balanced" = @{
        Description             = "均衡：全池 + Thompson + 10% 采样级联升级"
        Tiers                   = @("Cheap", "Medium", "Strong")
        DefaultTier             = "Medium"
        EnableThompsonSampling  = $true
        EnableContextualBandit  = $false
        EnableLatencyAware      = $false
        EnableCascadeUpgrade    = $true
        CascadeUpgradeSampleRate = 0.1
        EnableFusionRouter      = $false
        EnableFusionMode        = $false
    }
    "quality-first" = @{
        Description             = "质量优先：全池 + Fusion panel=2 + 拜占庭共识"
        Tiers                   = @("Cheap", "Medium", "Strong")
        DefaultTier             = "Strong"
        EnableThompsonSampling  = $false
        EnableContextualBandit  = $false
        EnableLatencyAware      = $false
        EnableCascadeUpgrade    = $false
        EnableFusionRouter      = $true
        FusionRouterPanelSize   = 2
        EnableByzantineConsensus = $true
        FusionRouterTemperature = 0.0
        EnableFusionMode        = $false
    }
}

# 所有策略触碰字段的并集（用于结束后恢复原值）；缓存类开关恒显式关闭，防跨批命中污染对比
$TouchedFields = @(
    "DefaultTier", "EnableThompsonSampling", "EnableContextualBandit", "ExplorationEpsilon",
    "EnableLatencyAware", "EnableCascadeUpgrade", "CascadeUpgradeSampleRate",
    "EnableFusionRouter", "FusionRouterPanelSize", "EnableByzantineConsensus", "FusionRouterTemperature",
    "EnableFusionMode", "EnableResponseCache", "EnableSemanticCache",
    "EnableQualityJudge", "QualityJudgeSampleRate", "QualityJudgeModel",
    "EnableRuleClassifier", "EnableSemanticRouter"
)

# 策略未显式定义字段的"中性默认"：每次 PUT 都把全部触碰字段归位到策略语义或中性值，
# 杜绝上一策略的开关残留串扰（实测 ExplorationEpsilon 会从 cost-first 泄漏到后续策略）。
$NeutralDefaults = @{
    DefaultTier = "Medium"; EnableThompsonSampling = $false; EnableContextualBandit = $false
    ExplorationEpsilon = 0.0; EnableLatencyAware = $false; EnableCascadeUpgrade = $false
    CascadeUpgradeSampleRate = 0.0; EnableFusionRouter = $false; FusionRouterPanelSize = 2
    EnableByzantineConsensus = $false; FusionRouterTemperature = 0.0; EnableFusionMode = $false
    EnableResponseCache = $false; EnableSemanticCache = $false
    EnableQualityJudge = $false; QualityJudgeSampleRate = 0.0; QualityJudgeModel = $null
    # 分类器保持开启（生产默认）：按题分配档位正是混合池路由的核心行为；档位的硬控制由 Tiers 承担
    EnableRuleClassifier = $true; EnableSemanticRouter = $false
}

function Invoke-Api {
    param([string]$Method, [string]$Path, [object]$Body, [int]$TimeoutSec = 600)
    $uri = "$BaseUrl$Path"
    # PS 5.1 的 ConvertTo-Json 会把哈希表里的数组属性包成 {"value":[...]}（服务端反序列化失败），
    # 数组载荷一律由调用方预序列化为字符串传入。
    # 传输层不用 Invoke-RestMethod：PS 5.1 对 body 的编码/分块行为难以精确控制（实测 UTF-8 中文
    # 与 PUT 表单均出现服务端 JSON 解析 400），统一改用 Windows 自带 curl.exe 字节级直发。
    $json = if ($Body -is [string]) { $Body } elseif ($null -ne $Body) { $Body | ConvertTo-Json -Depth 8 } else { $null }
    $bodyFile = $null
    $curlArgs = @("-s", "-m", $TimeoutSec, "-X", $Method, "-H", "Authorization: Bearer $AdminKey")
    if ($null -ne $json) {
        $bodyFile = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($bodyFile, $json, (New-Object Text.UTF8Encoding($false)))
        $curlArgs += @("-H", "Content-Type: application/json", "--data-binary", "@$bodyFile")
    }
    # 追加 \n%{http_code}：单次请求同时拿 body 与状态码（评测计费接口绝不能发两次）
    try {
        $raw = & curl.exe @curlArgs "-w" "`n%{http_code}" $uri
        $code = ($raw | Select-Object -Last 1)
        $payload = ($raw | Select-Object -First (($raw | Measure-Object).Count - 1)) -join "`n"
        if ($code -notmatch "^2") { throw "HTTP $code $payload" }
        return ($payload | ConvertFrom-Json)
    }
    finally {
        if ($bodyFile) { Remove-Item $bodyFile -Force -ErrorAction SilentlyContinue }
    }
}

# ── 0. 连通性与前提检查 ──
$initial = Invoke-Api -Method GET -Path "/api/dashboard/config"
if (-not $initial.Version) { throw "GET /api/dashboard/config 未返回 Version，实例版本过旧或鉴权失败。" }
Write-Host "✓ 实例连通，配置版本 $($initial.Version)"

# 质量评分口径：配置了 judge 模型则全程用 judge（语义级口径，跨策略恒定），
# 否则评测器按解析顺序回落向量余弦/jaccard（词面重叠，受回答长度影响）。
if ($QualityJudgeModel) {
    $NeutralDefaults.EnableQualityJudge = $true
    $NeutralDefaults.QualityJudgeModel = $QualityJudgeModel
    $NeutralDefaults.QualityJudgeSampleRate = 1.0
}

if (-not (Test-Path $CasesPath)) { throw "金标准数据集不存在：$CasesPath" }
$cases = Get-Content $CasesPath -Raw | ConvertFrom-Json
Write-Host "✓ 数据集 $($cases.Count) 题"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"

# ── 1. 记录被改字段的初始值与模型启停状态（用于恢复） ──
$original = @{}
foreach ($f in $TouchedFields) { $original[$f] = $initial.Routing.$f }
$originalModelEnabled = @{}
foreach ($m in (Invoke-Api -Method GET -Path "/api/models")) { $originalModelEnabled[$m.name] = [bool]$m.enabled }

# ── 2. 逐策略执行 ──
$reports = @()
foreach ($name in $Strategies) {
    if (-not $StrategyDefs.ContainsKey($name)) {
        Write-Warning "未知策略 '$name'，可选：$($StrategyDefs.Keys -join ', ')。跳过。"
        continue
    }
    $def = $StrategyDefs[$name]
    Write-Host "`n═══ 策略 [$name] $($def.Description) ═══"

    # 2a-1. 按策略档位集启停模型（Tiers 之外的临时 Disabled，评测后统一恢复）
    $models = Invoke-Api -Method GET -Path "/api/models"
    foreach ($m in $models) {
        $targetEnabled = $def.Tiers -contains $m.tier
        if ($m.enabled -ne $targetEnabled) {
            $null = Invoke-Api -Method PUT -Path "/api/models/$($m.name)" -Body @{ enabled = $targetEnabled }
            Write-Host ("  · 模型 {0}（{1} 档）→ {2}" -f $m.name, $m.tier, $(if ($targetEnabled) { "启用" } else { "停用" }))
        }
    }

    # 2a-2. 热切换配置（每次重取版本，CAS 防并发覆盖）。全部触碰字段按"策略定义 > 中性默认"归位，
    #       并统一关闭规则分类器与语义路由——否则分类器把多数问题判到固定档，档位差异被掩盖。
    $cur = Invoke-Api -Method GET -Path "/api/dashboard/config"
    $putBody = @{ ExpectedVersion = $cur.Version }
    foreach ($f in $TouchedFields) {
        if ($def.ContainsKey($f)) { $putBody[$f] = $def[$f] }
        else { $putBody[$f] = $NeutralDefaults[$f] }
    }
    $putResp = Invoke-Api -Method PUT -Path "/api/dashboard/config" -Body $putBody
    if ($putResp.diagnostics -and $putResp.diagnostics.Count -gt 0) {
        Write-Warning "  组合诊断（不影响执行，但横评解读时要考虑）："
        foreach ($d in $putResp.diagnostics) { Write-Warning "  - $($d.code): $($d.message)" }
    }
    Write-Host "  ✓ 配置已热生效（v$($putResp.version)）"

    # 2b. 学习状态归零：Thompson/LinUCB 的历史会跨策略残留（延迟画像、奖励先验），
    #     不归零则"cost-first 的 Thompson"继承的是"上一策略攒的状态"，对比失真。
    $null = Invoke-Api -Method POST -Path "/api/dashboard/learning/reset"
    Write-Host "  ✓ 学习状态已归零"

    # 2c. 跑评测批（真实路由 + 真实上游计费）。评测体手工拼 JSON：cases 数组经管道序列化，
    #     绕开 PS 5.1 哈希表数组属性被包成 {"value":[...]} 的坑。
    Write-Host "  … 评测执行中（$($cases.Count) 题，融合策略约 $($cases.Count * 4) 次上游调用）…"
    $evalStart = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $evalBody = '{"cases": ' + ($cases | ConvertTo-Json -Depth 6 -Compress) + '}'
    $report = Invoke-Api -Method POST -Path "/api/dashboard/eval/run" -Body $evalBody
    $evalEnd = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    # 2d. 策略级总成本取审计窗口汇总：评测报告的 tokens/cost 只含最终答案那次调用，
    #     Fusion 的 panel/analyst 内部调用、级联升级重答都单独计费进审计——横评对比必须用总账。
    #     审计走异步批量落库，先等一批 flush，否则窗口尾部请求漏计。
    Start-Sleep -Seconds 3
    $audit = Invoke-Api -Method GET -Path "/api/dashboard/audit/analysis?from=$evalStart&to=$evalEnd"
    $file = Join-Path $OutDir "$stamp-$name.json"
    $report | ConvertTo-Json -Depth 10 | Set-Content -Path $file -Encoding UTF8
    ($audit | ConvertTo-Json -Depth 10) | Set-Content -Path (Join-Path $OutDir "$stamp-$name-audit.json") -Encoding UTF8
    Write-Host ("  ✓ 报告已存 {0} ｜ 准确率 {1:P0} ｜ 平均延迟 {2:N0}ms ｜ 评测内成本 `${3:N4} ｜ 策略总成本(审计) `${4:N4} ｜ 上游调用 {5}" -f `
            $file, $report.accuracyRate, $report.avgLatencyMs, $report.totalCost, $audit.summary.totalCostUsd, $audit.totalRequests)
    $reports += [pscustomobject]@{ Name = $name; Report = $report; Audit = $audit; File = $file }
}

# ── 3. 汇总对比 ──
Write-Host "`n═══ 横评汇总（每题统一口径；学习类策略在 <50 题样本下 Thompson 近似随机，结构性差异才可信）═══"
$rows = foreach ($r in $reports) {
    $models = $r.Report.results | Group-Object { $_.selectedModel } |
        Sort-Object Count -Descending | ForEach-Object { "$($_.Name)×$($_.Count)" }
    [pscustomobject]@{
        策略          = $r.Name
        准确率        = "{0:P0}" -f $r.Report.accuracyRate
        质量通过率    = "{0:P0}" -f $r.Report.qualityPassRate
        评分口径      = ($r.Report.results | Select-Object -First 1).qualityMetric
        平均延迟ms    = [math]::Round($r.Report.avgLatencyMs)
        策略总成本USD = [math]::Round($r.Audit.summary.totalCostUsd, 4)
        策略总tokens  = ($r.Audit.summary.promptTokens + $r.Audit.summary.completionTokens)
        上游调用数    = $r.Audit.totalRequests
        模型选中分布  = $models -join " "
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host

# ── 4. 恢复原配置与模型启停状态 ──
if (-not $SkipRestore) {
    $cur = Invoke-Api -Method GET -Path "/api/dashboard/config"
    $restore = @{ ExpectedVersion = $cur.Version }
    foreach ($f in $TouchedFields) { $restore[$f] = $original[$f] }
    $null = Invoke-Api -Method PUT -Path "/api/dashboard/config" -Body $restore
    foreach ($m in (Invoke-Api -Method GET -Path "/api/models")) {
        if ($originalModelEnabled.ContainsKey($m.name) -and $m.enabled -ne $originalModelEnabled[$m.name]) {
            $null = Invoke-Api -Method PUT -Path "/api/models/$($m.name)" -Body @{ enabled = $originalModelEnabled[$m.name] }
        }
    }
    Write-Host "`n✓ 原配置与模型启停状态已恢复"
}

$summaryFile = Join-Path $OutDir "$stamp-summary.csv"
$rows | Export-Csv -Path $summaryFile -NoTypeInformation -Encoding UTF8
Write-Host "✓ 汇总 CSV：$summaryFile"
Write-Host "下一步：把汇总表粘进 docs/strategy-bakeoff.md 的报告节，或直接归档 $OutDir。"
