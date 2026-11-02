$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.local'
dotnet run --project Transfer.Client/Transfer.Client.csproj --configuration Release
