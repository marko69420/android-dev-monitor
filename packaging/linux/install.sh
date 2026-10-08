#!/usr/bin/env sh
# Installs Android Dev Monitor for the current user (no root needed):
#   app      ~/.local/share/android-dev-monitor/
#   command  ~/.local/bin/android-dev-monitor
#   menu     ~/.local/share/applications/android-dev-monitor.desktop
# Run ./install.sh --uninstall to remove it again. Sessions and media in ~/.local/share/AndroidDevMonitor are kept.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
app="$data/android-dev-monitor"
bin="$HOME/.local/bin"
desktop="$data/applications/android-dev-monitor.desktop"
icon="$data/icons/hicolor/256x256/apps/android-dev-monitor.png"

if [ "${1:-}" = "--uninstall" ]; then
    rm -rf "$app" "$bin/android-dev-monitor" "$desktop" "$icon"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
    echo "Android Dev Monitor was removed. Your sessions and media are still in $data/AndroidDevMonitor."
    exit 0
fi

mkdir -p "$app" "$bin" "$(dirname "$desktop")" "$(dirname "$icon")"
cp -R "$here/." "$app/"
chmod +x "$app/AndroidDevMonitor"
ln -sf "$app/AndroidDevMonitor" "$bin/android-dev-monitor"
cp "$here/android-dev-monitor.png" "$icon"
# Quoted, so a home folder with spaces still starts; desktop entries need %% for a literal %.
exec_path=$(printf '%s' "$app/AndroidDevMonitor" | sed -e 's/[\\"`$]/\\&/g' -e 's/%/%%/g' -e 's/[\\&|]/\\&/g')
sed "s|^Exec=.*|Exec=\"$exec_path\"|" "$here/android-dev-monitor.desktop" > "$desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true

echo "Installed Android Dev Monitor to $app"
echo "Start it from your applications menu, or run: android-dev-monitor"
if ! command -v adb >/dev/null 2>&1 && [ ! -x "$HOME/Android/Sdk/platform-tools/adb" ]; then
    echo
    echo "adb was not found. Install Android Platform Tools, for example:"
    echo "  Ubuntu/Debian: sudo apt install adb"
    echo "  Fedora:        sudo dnf install android-tools"
    echo "  Arch:          sudo pacman -S android-tools"
fi
