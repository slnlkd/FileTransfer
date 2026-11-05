<#
.SYNOPSIS
  发布带本机个性化 NG 过滤的 RemoteFiles 客户端和服务端。

.DESCRIPTION
  Local/ 和 Directory.Build.local.props 不纳入版本控制；本脚本调用 dotnet publish，
  由 Directory.Build.local.props 自动把 Oracle 驱动和 NG 过滤代码编译进去。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-ng.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-ng.ps1 -Output D:\Temp\ng-package
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path 'Directory.Build.local.props')) {
    throw '缺少 Directory.Build.local.props，无法编译 NG 过滤。'
}
if (-not (Test-Path 'Local\NgFilter\RemoteBrowser.NgFilter.cs')) {
    throw '缺少 Local\NgFilter 下的 NG 过滤源码。'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root "artifacts\ng-$stamp"
}
$package = Join-Path $Output 'FileTransfer'
New-Item -ItemType Directory -Path $package -Force | Out-Null

$apps = @(
    @{ Name = 'RemoteFiles.Client'; Project = '.\RemoteFiles.Client\RemoteFiles.Client.csproj' },
    @{ Name = 'RemoteFiles.Server'; Project = '.\RemoteFiles.Server\RemoteFiles.Server.csproj' }
)
foreach ($app in $apps) {
    Write-Host "正在发布 $($app.Name) …"
    dotnet publish $app.Project -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o (Join-Path $package $app.Name)
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$($app.Name)" }
}

if (-not (Test-Path (Join-Path $package 'RemoteFiles.Server\Oracle.ManagedDataAccess.dll'))) {
    throw '服务端发布结果缺少 Oracle.ManagedDataAccess.dll，请检查 Directory.Build.local.props。'
}
# 确认 NG 过滤实现确实编译进了客户端（未实现时 partial 方法会被完全移除）。
$clientDll = Join-Path $package 'RemoteFiles.Client\RemoteFiles.Client.dll'
$clientText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($clientDll))
if (-not $clientText.Contains('BuildLocalFilters')) {
    throw '客户端未包含 NG 过滤控件，请检查 Directory.Build.local.props。'
}
$serverDll = Join-Path $package 'RemoteFiles.Server\RemoteFiles.Server.dll'
$serverText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($serverDll))
if (-not $serverText.Contains('OracleNgFolderQuery')) {
    throw '服务端未包含 NG 过滤实现，请检查 Directory.Build.local.props。'
}

Copy-Item 'Local\NgFilter\ngfilter.example.json' $package
Copy-Item 'Local\NG部署说明.md' $package
# 真实 ngfilter.json 含数据库密码，不放进发布包。
$zip = "$Output.zip"
# 杀毒软件可能短暂占用刚生成的压缩包，先重试删除再覆盖。
for ($attempt = 0; $attempt -lt 5 -and (Test-Path $zip); $attempt++) {
    try { Remove-Item $zip -Force -ErrorAction Stop }
    catch { Start-Sleep -Milliseconds 500 }
}
Compress-Archive -Path $package -DestinationPath $zip -Force

$size = (Get-ChildItem $package -Recurse | Measure-Object Length -Sum).Sum
Write-Host ''
Write-Host ("发布目录：{0}" -f $package)
Write-Host ("压缩包：  {0}（{1:N1} MB）" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host ("解压后：  {0:N1} MB" -f ($size / 1MB))
Write-Host ''
Write-Host '提醒：ngfilter.json 需要单独放到服务端 %LOCALAPPDATA%\FileTransferDemo 下，不要随包分发。'
