param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\win-x64'))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    dotnet restore AndroidDevMonitor.slnx
    dotnet format AndroidDevMonitor.slnx --verify-no-changes --no-restore
    dotnet build AndroidDevMonitor.slnx -c Release --no-restore
    dotnet test AndroidDevMonitor.slnx -c Release --no-build
    dotnet publish src/AndroidDevMonitor.App/AndroidDevMonitor.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o $Output
    Write-Host "Portable build: $Output"
} finally { Pop-Location }
