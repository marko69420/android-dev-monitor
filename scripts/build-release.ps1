param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\win-x64'))
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    dotnet restore AndroidDevMonitor.slnx -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
    dotnet build AndroidDevMonitor.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
    dotnet test AndroidDevMonitor.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
    dotnet publish src/AndroidDevMonitor.App/AndroidDevMonitor.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    Write-Host "Portable build: $Output"
} finally { Pop-Location }
