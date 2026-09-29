<#
.SYNOPSIS
    OptiRouter 生产发版脚本（Windows 服务 OptiRouter，nssm 托管）。

.DESCRIPTION
    把 AGENTS.md 的人工发版流程固化为可执行脚本：
    1. 配置预检——publish 会用 src 的 appsettings*.json 覆盖同名文件，先 diff 两边，
       有差异即中止（防生产配置被旧版回写）；确认无害可加 -Force 继续。
    2. 停服（nssm；输出是 UTF-16 乱码故忽略，以服务状态轮询为准）。
    3. dotnet publish → publish 目录。
    4. 启服。
    5. /health 健康验证（无 Key 端点）；超时则打印最新服务日志尾部辅助排查。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\release.ps1
    powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Force          # 预检有差异仍继续
    powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -SkipService    # 手工管理服务时
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$SkipService,
    [string]$BaseUri = "http://localhost:5080",
    [string]$Nssm = "D:\nssm\nssm.exe",
    [string]$ServiceName = "OptiRouter",
    [int]$HealthTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$srcProjectDir = Join-Path $root "src\OptiRouter"
$publishDir = Join-Path $root "publish"

if (-not (Test-Path (Join-Path $publishDir "OptiRouter.exe"))) {
    throw "publish 目录不存在或为空（$publishDir）——首次部署请先手工执行一次 dotnet publish。"
}

# ---- 1. 配置预检 ----
Write-Host "== 预检：src 与 publish 配置一致性 =="
$conflicts = @()
Get-ChildItem -Path (Join-Path $publishDir "appsettings*.json") -File | ForEach-Object {
    $counterpart = Join-Path $srcProjectDir $_.Name
    if (-not (Test-Path $counterpart)) {
        $conflicts += $_.Name
        Write-Host "  [缺失源文件] $($_.Name)（publish 有、src 无）"
        return
    }
    $publishHash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    $srcHash = (Get-FileHash $counterpart -Algorithm SHA256).Hash
    if ($publishHash -ne $srcHash) {
        $conflicts += $_.Name
        Write-Host "  [差异] $($_.Name)："
        & git diff --no-index -- -- "$($_.FullName)" "$counterpart" | Write-Host
    }
}
if ($conflicts.Count -gt 0) {
    if (-not $Force) {
        throw "publish 与 src 配置存在差异（$($conflicts -join ', ')）。publish 会用 src 版本覆盖生产配置——先同步两边（以 src 为权威或手动合并），确认无害后加 -Force 继续。"
    }
    Write-Host "  -Force 已指定：带着差异继续发版。" -ForegroundColor Yellow
}
else {
    Write-Host "  配置一致，无回写风险。"
}

# ---- 2. 版本标识 ----
$version = (& git describe --tags --always --dirty)
Write-Host "== 发版版本：$version =="

# ---- 3. 停服 ----
if (-not $SkipService) {
    if (-not (Test-Path $Nssm)) { throw "nssm 不存在：$Nssm" }
    Write-Host "== 停止服务 $ServiceName =="
    & $Nssm stop $ServiceName | Out-Null
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    } while ($svc -and $svc.Status -ne "Stopped" -and (Get-Date) -lt $deadline)
    if (-not $svc) { throw "服务 $ServiceName 不存在（-SkipService 可跳过服务管理）。" }
    if ($svc.Status -ne "Stopped") { throw "服务 60 秒内未停止（当前 $($svc.Status)），检查进程占用后重试。" }
}

# ---- 4. 发布（失败则把服务拉起来再中止，不留死服务）----
Write-Host "== dotnet publish =="
& dotnet publish (Join-Path $srcProjectDir "OptiRouter.csproj") -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) {
    if (-not $SkipService) {
        Write-Host "publish 失败，回滚服务状态……" -ForegroundColor Yellow
        & $Nssm start $ServiceName | Out-Null
    }
    throw "publish 失败（exit $LASTEXITCODE）。"
}

# ---- 5. 启服 ----
if (-not $SkipService) {
    Write-Host "== 启动服务 $ServiceName =="
    & $Nssm start $ServiceName | Out-Null
}

# ---- 6. 健康验证 ----
Write-Host "== 健康验证 $BaseUri/health =="
$healthy = $false
$deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    try {
        $resp = Invoke-WebRequest -Uri "$BaseUri/health" -UseBasicParsing -TimeoutSec 5
        if ($resp.StatusCode -eq 200) { $healthy = $true; break }
    }
    catch {
        Start-Sleep -Seconds 3
    }
}
if (-not $healthy) {
    Write-Host "健康验证超时——最新服务日志尾部：" -ForegroundColor Red
    $latestLog = Get-ChildItem -Path (Join-Path $publishDir "logs") -Filter "service-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latestLog) { Get-Content $latestLog.FullName -Tail 20 }
    throw "服务未在 ${HealthTimeoutSeconds}s 内通过 /health（$BaseUri）。"
}

Write-Host "== 发版完成：$version 已上线 $BaseUri（/health 200）==" -ForegroundColor Green
