<#
.SYNOPSIS
    OptiRouter 一键发布：测试 -> push 远端 -> 停服务 -> 发布 -> 启服务 -> 验证。

.DESCRIPTION
    用此脚本代替裸 `git push`：push 成功后才停服发布，等价于"push 到远端后触发发布"。
    framework-dependent 发布（跑 dotnet.exe OptiRouter.dll），不改动 nssm 服务配置。
    发布目录下的运行时数据/data、/logs、appsettings.json、configure-nssm.ps1 被保留不覆盖。

.PARAMETER SkipPush
    跳过 git push（仅本地发布，不推远端）。

.PARAMETER SkipTest
    跳过 dotnet test 门禁。

.EXAMPLE
    .\scripts\deploy.ps1            # 完整流程：test -> push -> 停服 -> 发布 -> 启服
    .\scripts\deploy.ps1 -SkipPush  # 不 push，仅本地发布（已 push 过时用）
#>
[CmdletBinding()]
param(
    [switch]$SkipPush,
    [switch]$SkipTest
)

$ErrorActionPreference = 'Stop'

# --- 配置（与 configure-nssm.ps1 对齐） ---
$nssm   = 'D:\nssm\nssm.exe'
$svc    = 'OptiRouter'
$target = 'E:\Demo\publish\opti-router'

$repoRoot = Split-Path -Parent $PSScriptRoot   # scripts/ 的上级 = 仓库根
$staging  = Join-Path $env:TEMP 'optirouter-publish-staging'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "✗ $msg" -ForegroundColor Red; exit 1 }

# --- 0. 前置检查 ---
if (-not (Test-Path $nssm)) { Fail "nssm.exe not found at $nssm" }
# 工作树必须干净：dotnet publish 编译的是工作树代码，git push 上传的是 commit，
# 二者不一致会让线上服务跑着查不到的代码。
$dirty = git -C $repoRoot status --porcelain
if ($dirty) {
    Fail "Working tree is dirty; deploy aborted. Commit or stash first so the deployed binary matches the pushed commit."
}

# --- 1. 测试门禁 ---
if (-not $SkipTest) {
    Step 'Running tests (release gate)...'
    dotnet test $repoRoot
    if ($LASTEXITCODE -ne 0) { Fail 'Tests failed; deploy aborted (service not touched).' }
}

# --- 2. push 远端（失败则中止，不触碰服务） ---
if (-not $SkipPush) {
    Step 'Pushing to remote...'
    git -C $repoRoot push
    if ($LASTEXITCODE -ne 0) { Fail 'git push failed; deploy aborted (service not touched).' }
}

# --- 3. 发布到暂存目录（framework-dependent） ---
Step "Publishing (framework-dependent) to staging: $staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish (Join-Path $repoRoot 'src\OptiRouter') -c Release -o $staging --no-self-contained
if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed; deploy aborted (service not touched).' }

# --- 4. 停服务 -> 同步文件 -> 启服务（try/finally 保证服务一定被重启） ---
$stopped = $false
try {
    Step "Stopping service $svc ..."
    & $nssm stop $svc | Out-Null
    $stopped = $true
    Start-Sleep -Seconds 3   # 等 dotnet 进程释放 dll 文件句柄

    Step "Syncing to $target (preserving data/, logs/, appsettings.json, configure-nssm.ps1) ..."
    # robocopy: /E 复制含子目录(不删除目标多余文件，数据安全优先);
    #           /XD 排除目录 /XF 排除文件; 退出码 0-7 成功，>=8 才错。
    robocopy $staging $target /E /XD data logs /XF appsettings.json appsettings.Development.json configure-nssm.ps1 /NP /NFL /NDL /NJH /NJS
    if ($LASTEXITCODE -ge 8) { Fail "robocopy failed (exit $LASTEXITCODE)" }
}
finally {
    if ($stopped) {
        Step "Starting service $svc ..."
        & $nssm start $svc | Out-Null
        Start-Sleep -Seconds 2
    }
}

# --- 5. 验证服务状态 ---
Step "Verifying service state ..."
# 用 Get-Service 而非 nssm status：后者返回 SERVICE_RUNNING 等 UTF-16 宽字符(字符间带空格)，
# 正则匹配不可靠；Get-Service.Status 是干净枚举(Running/Stopped)。
$svcInfo = Get-Service $svc -ErrorAction SilentlyContinue
if ($svcInfo -and $svcInfo.Status -eq 'Running') {
    Write-Host "✓ Service $svc is Running. Deploy complete." -ForegroundColor Green
} else {
    # 未运行视作发布失败（exit 1）：让 pre-push hook 据此阻止 push，也避免静默放过崩溃的服务。
    Fail "Service $svc not running after start (status: $($svcInfo.Status)); check nssm/logs/stderr.log."
}
