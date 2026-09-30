Android Dev Monitor for Linux
=============================

Run it without installing:
    ./AndroidDevMonitor            (live ADB mode)
    ./AndroidDevMonitor --demo     (sample data, no phone needed)

Install for your user (adds a menu entry and the android-dev-monitor command):
    ./install.sh
Remove it again:
    ./install.sh --uninstall

Requirements
- 64-bit Linux (x86_64) with X11 or Wayland (through XWayland). No .NET install is needed.
- adb from Android Platform Tools. The app looks in $ANDROID_HOME, $ANDROID_SDK_ROOT, ~/Android/Sdk,
  /usr/lib/android-sdk, /opt/android-sdk and PATH, or you can pick the adb file in Settings.
    Ubuntu/Debian: sudo apt install adb
    Fedora:        sudo dnf install android-tools
    Arch:          sudo pacman -S android-tools

USB permissions
If the phone shows as "no permissions" or "unauthorized", add the Android udev rules and replug the cable:
    Ubuntu/Debian: sudo apt install android-sdk-platform-tools-common
    Arch:          sudo pacman -S android-udev
    Other:         put SUBSYSTEM=="usb", ATTR{idVendor}=="18d1", MODE="0660", TAG+="uaccess"
                   (use your phone maker's USB vendor ID) in /etc/udev/rules.d/51-android.rules,
                   then run: sudo udevadm control --reload-rules
Then unlock the phone and accept the "Allow USB debugging" prompt.

Optional tools: scrcpy (full-speed mirror window) and the Android SDK emulator (AVD controls).

Your data: ~/.local/share/AndroidDevMonitor (sessions, media, logs).
Exports:   ~/Documents/Android Dev Monitor
