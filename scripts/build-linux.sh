#!/usr/bin/env bash
# Builds, tests and publishes the Linux app, then packs it as artifacts/AndroidDevMonitor-<tag>-linux-x64.tar.gz.
# Usage: scripts/build-linux.sh [tag]      (tag defaults to "dev")
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
tag=${1:-dev}
rid=linux-x64
out="$root/artifacts/$rid/AndroidDevMonitor"
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$root"

# -p:EnableWindowsTargeting=true lets Linux compile the Windows (WPF) project too, so one build checks everything.
dotnet build AndroidDevMonitor.slnx -c Release -p:EnableWindowsTargeting=true
dotnet test AndroidDevMonitor.slnx -c Release --no-build -p:EnableWindowsTargeting=true

rm -rf "$out"
dotnet publish src/AndroidDevMonitor.Desktop/AndroidDevMonitor.Desktop.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -o "$out"
cp packaging/linux/install.sh packaging/linux/README.txt packaging/linux/android-dev-monitor.desktop packaging/linux/android-dev-monitor.png "$out/"
chmod +x "$out/AndroidDevMonitor" "$out/install.sh"

archive="$root/artifacts/AndroidDevMonitor-$tag-$rid.tar.gz"
tar -czf "$archive" -C "$(dirname "$out")" AndroidDevMonitor
echo "Linux package: $archive"
