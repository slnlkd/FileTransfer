<#
.SYNOPSIS
  生成 RemoteFiles.Client 的内网静默更新包和 manifest.json。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-update.ps1 -Version 1.6.0 -Notes "修复..."
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-update.ps1 -Version 1.6.0 -Destination \\10.100.40.181\updates
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = '',
    [string]$NotesFile = '',
    [string]$Destination = ''
)

$ErrorActionPreference = 'Stop'
if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    $Notes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile), [Text.Encoding]::UTF8).Trim()
}
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$output = Join-Path $root "artifacts\updates\$Version"
Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $output | Out-Null
$publish = Join-Path $output 'publish'

Write-Host "正在发布 RemoteFiles.Client v$Version …"
dotnet publish .\RemoteFiles.Client\RemoteFiles.Client.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publish
if ($LASTEXITCODE -ne 0) { throw '客户端发布失败' }

Copy-Item 'README.md' $publish -Force
$zipName = "RemoteFiles.Client-$Version-win-x64.zip"
$zip = Join-Path $output $zipName
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
$manifest = @{
    version = $Version
    file    = $zipName
    sha256  = $hash
    notes   = $Notes
} | ConvertTo-Json
# 显式写成无 BOM 的 UTF-8，避免不同 PowerShell 版本的编码差异。
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), $manifest, (New-Object Text.UTF8Encoding($false)))

if (-not [string]::IsNullOrWhiteSpace($Destination)) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item $zip (Join-Path $Destination $zipName) -Force
    Copy-Item (Join-Path $output 'manifest.json') (Join-Path $Destination 'manifest.json') -Force
    Write-Host "已复制到更新目录：$Destination"
}

Write-Host ''
Write-Host ("更新包：{0}（{1:N1} MB）" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host ("SHA256：{0}" -f $hash)
Write-Host ("清单：{0}" -f (Join-Path $output 'manifest.json'))
Write-Host '把 manifest.json 和 zip 放到服务端程序目录的 updates 文件夹下。'
