<#
.SYNOPSIS
  生成 FileTransfer.Client 的内网静默更新包和 manifest.json。

.DESCRIPTION
  输出目录只包含可直接部署的两个文件：
    manifest.json
    FileTransfer.Client-<版本>-win-x64.zip
  把这两个文件复制到服务端程序目录的 updates 文件夹即可。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-update.ps1 -Version 1.6.2 -Notes "修复..."
  powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-update.ps1 -Version 1.6.2 -NotesFile .\notes.txt -Destination D:\RPASoftwares\FileTransfer\FileTransfer.Server\updates
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

# 交付目录：只放 manifest.json 和 zip，避免把 publish 构建产物一起拷到服务端。
$output = Join-Path $root "artifacts\updates\$Version"
# 构建目录：与交付目录分开。
$publish = Join-Path $root "artifacts\publish\updates\$Version"
Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $output, $publish | Out-Null

Write-Host "正在发布 FileTransfer.Client v$Version …"
dotnet publish .\RemoteFiles.Client\RemoteFiles.Client.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publish
if ($LASTEXITCODE -ne 0) { throw '客户端发布失败' }

Copy-Item 'README.md' $publish -Force
$zipName = "FileTransfer.Client-$Version-win-x64.zip"
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
    # 清理旧版本更新包，避免 updates 目录里同时存在多个 zip。
    Get-ChildItem -Path $Destination -Filter '*Files.Client-*-win-x64.zip' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Copy-Item $zip (Join-Path $Destination $zipName) -Force
    Copy-Item (Join-Path $output 'manifest.json') (Join-Path $Destination 'manifest.json') -Force
    Write-Host "已复制到更新目录：$Destination"
}

Write-Host ''
Write-Host ("交付目录：{0}" -f $output)
Write-Host ("更新包：{0}（{1:N1} MB）" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host ("SHA256：{0}" -f $hash)
Write-Host '把交付目录里的 manifest.json 和 zip 两个文件复制到服务端 updates 文件夹；不要复制版本目录或 publish 目录。'
