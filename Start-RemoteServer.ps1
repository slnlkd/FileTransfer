$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.local'
dotnet run --project RemoteFiles.Server/RemoteFiles.Server.csproj --configuration Release
