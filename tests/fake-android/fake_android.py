#!/usr/bin/env python3
"""A fake Android device for end-to-end tests of Android Dev Monitor without a phone or an emulator.

Invoked as `adb` it behaves like Android Platform Tools for one USB device (serial FAKE0001, a Pixel 8 Pro):
`adb shell` runs the command with /bin/sh on this computer, the way adbd runs it with /system/bin/sh on a phone.
/proc comes from the real Linux kernel, so CPU, memory, network and process numbers are live. Android-only tools
(getprop, dumpsys, pm, am, cmd, logcat, input, screencap, screenrecord, ps in toybox format, ...) are provided by
this same file, invoked through links in <root>/.bin. Device storage (/sdcard, /data/local/tmp, /data/user/0)
lives under FAKE_ANDROID_ROOT. A background process named com.example.fakegame plays the monitored app.

Every adb call is appended to <root>/commands.log with its exit code, so tests can assert what the app ran.
Set FAKE_ANDROID_CRASH=1 to add a FATAL EXCEPTION to logcat, and FAKE_ANDROID_STATE=no-permissions to list the
device the way adb does on Linux without udev rules.
"""
import datetime
import json
import os
import random
import re
import shlex
import shutil
import signal
import struct
import subprocess
import sys
import time
import zlib

SERIAL = "FAKE0001"
PACKAGE = "com.example.fakegame"
ROOT = os.path.abspath(os.environ.get("FAKE_ANDROID_ROOT", "/tmp/fake-android"))
SELF = os.path.realpath(__file__)
TOOLS = ["getprop", "wm", "dumpsys", "pm", "am", "cmd", "logcat", "input", "screencap", "screenrecord", "settings",
         "ps", "pidof", "ip", "monkey", "run-as", "uiautomator", "ls", "setprop", "perfetto"]
DEVICE_DIRS = {"/sdcard": "sdcard", "/storage/emulated/0": "sdcard", "/data/local/tmp": "data/local/tmp",
               "/data/user/0": "data/user/0", "/data/misc": "data/misc", "/data/anr": "data/anr", "/data": "data"}

PROPS = {
    "ro.product.manufacturer": "Google", "ro.product.brand": "google", "ro.product.model": "Pixel 8 Pro",
    "ro.product.name": "husky", "ro.product.device": "husky", "ro.build.version.release": "14",
    "ro.build.version.sdk": "34", "ro.product.cpu.abi": "arm64-v8a", "ro.serialno": SERIAL,
    "ro.build.fingerprint": "google/husky/husky:14/AP2A.240805.005/12025142:user/release-keys",
    "ro.build.type": "user", "ro.debuggable": "0", "persist.sys.locale": "en-US", "ro.hardware": "husky",
    "ro.build.id": "AP2A.240805.005", "ro.build.version.security_patch": "2024-08-05",
}


# ---------- helpers ----------
def state_path():
    return os.path.join(ROOT, "state.json")


def load_state():
    try:
        with open(state_path()) as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return {"packages": [PACKAGE, "com.android.chrome", "com.google.android.youtube", "com.android.settings"],
                "permissions": {}, "forwards": [], "reverses": [], "settings": {}, "airplane": False, "night": False}


def save_state(state):
    with open(state_path(), "w") as handle:
        json.dump(state, handle)


def device_path(path):
    """Maps an Android path to the folder that plays the device storage."""
    for prefix in sorted(DEVICE_DIRS, key=len, reverse=True):
        if path == prefix or path.startswith(prefix + "/"):
            return os.path.join(ROOT, DEVICE_DIRS[prefix], path[len(prefix):].lstrip("/"))
    return path


def rewrite_paths(command):
    pattern = "|".join(re.escape(prefix) for prefix in sorted(DEVICE_DIRS, key=len, reverse=True))
    return re.sub(r"(?<![\w/.])(" + pattern + r")(?=$|[/\s'\";|&)])",
                  lambda match: os.path.join(ROOT, DEVICE_DIRS[match.group(1)]), command)


def ensure_device():
    for folder in ["sdcard/Download", "sdcard/DCIM/Camera", "sdcard/Pictures", "sdcard/Movies", "sdcard/Documents",
                   "sdcard/Android/data", "data/local/tmp", f"data/user/0/{PACKAGE}/shared_prefs",
                   f"data/user/0/{PACKAGE}/databases", f"data/user/0/{PACKAGE}/cache", "data/misc/perfetto-traces", ".bin"]:
        os.makedirs(os.path.join(ROOT, folder), exist_ok=True)
    readme = os.path.join(ROOT, "sdcard/Download/readme.txt")
    if not os.path.exists(readme):
        with open(readme, "w") as handle:
            handle.write("Files on the fake Android device.\n")
        with open(os.path.join(ROOT, "sdcard/Download/it's a \"quoted\" name.txt"), "w") as handle:
            handle.write("quote test\n")
        with open(os.path.join(ROOT, f"data/user/0/{PACKAGE}/shared_prefs/settings.xml"), "w") as handle:
            handle.write('<map><int name="level" value="7" /></map>\n')
        with open(os.path.join(ROOT, "sdcard/DCIM/Camera/PXL_20260930.jpg"), "wb") as handle:
            handle.write(os.urandom(2048))
    for tool in TOOLS:
        link = os.path.join(ROOT, ".bin", tool)
        if not os.path.lexists(link):
            os.symlink(SELF, link)
    ensure_app_process()


def app_pid():
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            with open(f"/proc/{pid}/cmdline", "rb") as handle:
                if handle.read().split(b"\0")[0].decode(errors="replace") == PACKAGE:
                    return int(pid)
        except OSError:
            continue
    return None


def ensure_app_process():
    """Starts the long-running process that plays the app being monitored (light, steady CPU use)."""
    if app_pid() is not None:
        return
    code = ("import time,math\n"
            "while True:\n"
            "  end=time.time()+0.03\n"
            "  while time.time()<end: math.sqrt(12345.678)\n"
            "  time.sleep(0.07)\n")
    # exec -a names the process after the package, so ps, pidof and /proc/<pid>/cmdline show it like an Android app.
    subprocess.Popen(["bash", "-c", f"exec -a {PACKAGE} python3 -c {shlex.quote(code)}"], start_new_session=True,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(50):
        if app_pid() is not None:
            break
        time.sleep(0.05)


def png(width, height, seed):
    """A small phone-screen PNG: status bar, a colored card and a navigation bar, varying with seed."""
    rows = []
    hue = (seed * 37) % 255
    for y in range(height):
        row = bytearray([0])
        for x in range(width):
            if y < height * 0.04:
                pixel = (20, 20, 24)
            elif y > height * 0.94:
                pixel = (30, 30, 34)
            elif 0.2 * height < y < 0.5 * height and 0.1 * width < x < 0.9 * width:
                pixel = (hue, 120, 255 - hue)
            else:
                pixel = (240, 240, 245)
            row.extend(pixel)
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


def logcat_lines(count):
    now = datetime.datetime.now()
    pid = app_pid() or 4242
    messages = [
        ("I", "FakeGame", "Frame rendered in 12 ms"),
        ("D", "Unity", "Scene loaded: Level 7"),
        ("W", "FakeGame", "Texture cache above 80%"),
        ("I", "ActivityManager", f"Displayed {PACKAGE}/.MainActivity: +412ms"),
        ("E", "FakeGame", "Network request failed: timeout"),
    ]
    lines = []
    for index in range(count):
        stamp = now - datetime.timedelta(milliseconds=(count - index) * 250)
        level, tag, text = messages[index % len(messages)]
        lines.append(f"{stamp:%m-%d %H:%M:%S}.{stamp.microsecond // 1000:03d}  {pid:5d}  {pid + 17:5d} {level} {tag:<8}: {text}")
    if os.environ.get("FAKE_ANDROID_CRASH") == "1":
        stamp = now
        head = f"{stamp:%m-%d %H:%M:%S}.{stamp.microsecond // 1000:03d}  {pid:5d}  {pid:5d} E AndroidRuntime"
        lines += [f"{head}: FATAL EXCEPTION: main", f"{head}: Process: {PACKAGE}, PID: {pid}",
                  f"{head}: java.lang.IllegalStateException: boss fight state lost"]
    return "\n".join(lines) + "\n"


# ---------- device tools (run inside `adb shell`) ----------
def tool(name, args):
    state = load_state()
    out = sys.stdout.write
    if name == "getprop":
        if args:
            out(PROPS.get(args[0], "") + "\n")
        else:
            out("".join(f"[{key}]: [{value}]\n" for key, value in sorted(PROPS.items())))
    elif name == "setprop":
        pass
    elif name == "wm":
        if args[:1] == ["size"]:
            out("Physical size: 1344x2992\n")
        elif args[:1] == ["density"]:
            out("Physical density: 480\n")
    elif name == "pidof":
        pid = app_pid() if args and args[-1] == PACKAGE else None
        if pid is None:
            return 1
        out(f"{pid}\n")
    elif name == "ps":
        out(toybox_ps())
    elif name == "ip":
        out("30: wlan0    inet 192.168.1.23/24 brd 192.168.1.255 scope global wlan0\\       valid_lft forever preferred_lft forever\n")
    elif name == "ls":
        os.execv("/bin/ls", ["ls", "--time-style=long-iso"] + args)
    elif name == "logcat":
        count = int(args[args.index("-t") + 1]) if "-t" in args else 40
        out(logcat_lines(min(count, 60)))
    elif name == "dumpsys":
        return dumpsys(args, state)
    elif name in ("pm", "cmd"):
        return package_manager(name, args, state)
    elif name == "am":
        if args[:1] == ["force-stop"]:
            return 0
        out("Starting: Intent { act=android.intent.action.MAIN cmp=" + PACKAGE + "/.MainActivity }\n")
    elif name == "monkey":
        if "-p" in args and args[args.index("-p") + 1] not in state["packages"]:
            sys.stderr.write("** No activities found to run, monkey aborted.\n")
            return 252
        out("  bash arg: -p\nEvents injected: 1\n## Network stats: elapsed time=12ms (0ms mobile, 0ms wifi, 12ms not connected)\n")
    elif name == "input":
        return 0
    elif name == "settings":
        if args[:1] == ["get"]:
            out(state["settings"].get(args[-1], "null") + "\n")
        elif args[:1] == ["put"] and len(args) >= 4:
            state["settings"][args[2]] = args[3]
            save_state(state)
    elif name == "screencap":
        target = [arg for arg in args if not arg.startswith("-")]
        data = png(270, 600, int(time.time()))
        if target:
            with open(device_path(target[0]), "wb") as handle:
                handle.write(data)
        else:
            sys.stdout.flush()
            sys.stdout.buffer.write(data)
    elif name == "screenrecord":
        target = device_path(args[-1])
        limit = int(args[args.index("--time-limit") + 1]) if "--time-limit" in args else 180
        stop = {"flag": False}
        signal.signal(signal.SIGINT, lambda *_: stop.update(flag=True))
        signal.signal(signal.SIGTERM, lambda *_: stop.update(flag=True))
        started = time.time()
        with open(target, "wb") as handle:
            handle.write(b"\x00\x00\x00\x18ftypmp42" + os.urandom(4096))
            while not stop["flag"] and time.time() - started < limit:
                time.sleep(0.1)
                handle.write(os.urandom(512))
    elif name == "run-as":
        package = args[0]
        if package != PACKAGE:
            sys.stderr.write(f"run-as: package not debuggable: {package}\n")
            return 1
        os.chdir(os.path.join(ROOT, "data/user/0", package))
        return subprocess.call(" ".join(args[1:]), shell=True)
    elif name == "uiautomator":
        target = device_path(args[-1]) if len(args) > 1 else device_path("/sdcard/window_dump.xml")
        with open(target, "w") as handle:
            handle.write('<?xml version="1.0"?><hierarchy rotation="0"><node class="android.widget.FrameLayout" '
                         f'package="{PACKAGE}" bounds="[0,0][1344,2992]"/></hierarchy>\n')
        out(f"UI hierchary dumped to: {args[-1] if len(args) > 1 else '/sdcard/window_dump.xml'}\n")
    elif name == "perfetto":
        sys.stderr.write("perfetto: tracing is not available on this fake device\n")
        return 1
    return 0


def toybox_ps():
    rows = ["USER            PID   PPID S    RSS NAME                        ARGS"]
    app = app_pid()
    for pid in sorted((int(p) for p in os.listdir("/proc") if p.isdigit())):
        try:
            with open(f"/proc/{pid}/stat") as handle:
                stat = handle.read()
            with open(f"/proc/{pid}/cmdline", "rb") as handle:
                argv = [part.decode(errors="replace") for part in handle.read().split(b"\0") if part]
            with open(f"/proc/{pid}/status") as handle:
                rss = next((int(line.split()[1]) for line in handle if line.startswith("VmRSS:")), 0)
        except (OSError, ValueError, IndexError):
            continue
        comm = stat[stat.index("(") + 1:stat.rindex(")")]
        fields = stat[stat.rindex(")") + 2:].split()
        name = argv[0] if argv else f"[{comm}]"
        user = "u0_a211" if pid == app else ("root" if pid < 100 else "shell")
        args = " ".join(argv) if argv else f"[{comm}]"
        rows.append(f"{user:<12} {pid:6d} {int(fields[1]):6d} {fields[0]} {rss:6d} {os.path.basename(name)[:27]:<27} {args[:120]}")
    return "\n".join(rows) + "\n"


def dumpsys(args, state):
    out = sys.stdout.write
    service = args[0] if args else ""
    if service == "battery":
        out("Current Battery Service state:\n  AC powered: false\n  USB powered: true\n  Wireless powered: false\n"
            "  Max charging current: 500000\n  status: 2\n  health: 2\n  present: true\n  level: 87\n  scale: 100\n"
            "  voltage: 4312\n  temperature: 314\n  technology: Li-ion\n")
    elif service == "thermalservice":
        out("IsStatusOverride: false\nThermalEventListeners:\n\tcallbacks: 1\nThermal Status: 1\nCached temperatures:\n"
            "\tTemperature{mValue=36.4, mType=3, mName=skin, mStatus=0}\nHAL Ready: true\n")
    elif service == "activity" and args[1:2] == ["activities"]:
        out("ACTIVITY MANAGER ACTIVITIES (dumpsys activity activities)\nDisplay #0 (activities from top to bottom):\n"
            f"  * Task{{8a1b2c3 #42 type=standard A=10211:{PACKAGE}}}\n"
            f"  topResumedActivity=ActivityRecord{{5f3e2d1 u0 {PACKAGE}/.MainActivity t42}}\n"
            f"  mResumedActivity: ActivityRecord{{5f3e2d1 u0 {PACKAGE}/.MainActivity t42}}\n")
    elif service == "meminfo":
        out(f"Applications Memory Usage (in Kilobytes):\n** MEMINFO in pid {app_pid() or 0} [{PACKAGE}] **\n"
            "                   Pss  Private  Private  SwapPss      Rss     Heap     Heap     Heap\n"
            "        Native Heap    41234    41200        0        0    43000    65536    52000    13536\n"
            "        TOTAL   187654   160000    12000        0   240000   120000    95000    25000\n")
    elif service == "gfxinfo":
        base = time.monotonic_ns() - 2_000_000_000
        lines = ["Applications Graphics Acceleration Info:", f"** Graphics info for pid {app_pid() or 0} [{PACKAGE}] **",
                 "---PROFILEDATA---", "Flags,IntendedVsync,Vsync,OldestInputEvent,NewestInputEvent,HandleInputStart,"
                 "AnimationStart,PerformTraversalsStart,DrawStart,SyncQueued,SyncStart,IssueDrawCommandsStart,SwapBuffers,FrameCompleted,"]
        for frame in range(90):
            intended = base + frame * 16_666_667
            duration = random.choice([9, 11, 12, 14, 15, 18, 24]) * 1_000_000
            values = [0, intended] + [intended] * 11 + [intended + duration]
            lines.append(",".join(str(value) for value in values) + ",")
        lines.append("---PROFILEDATA---")
        out("\n".join(lines) + "\n")
    elif service == "storaged":
        out("")
    elif service == "package":
        package = args[-1]
        if package not in state["packages"]:
            out("Unable to find package: " + package + "\n")
            return 0
        out(f"Packages:\n  Package [{package}] (1a2b3c):\n    versionCode=70 minSdk=24 targetSdk=34\n    versionName=1.7.0\n"
            "    runtime permissions:\n" + "".join(
                f"      {perm}: granted={str(granted).lower()}\n" for perm, granted in state["permissions"].get(package, {}).items()))
    else:
        out(f"Service {service}: fake dumpsys has no data\n")
    return 0


def package_manager(name, args, state):
    out = sys.stdout.write
    words = args[1:] if name == "cmd" and args[:1] == ["package"] else args
    if name == "cmd" and args[:1] not in (["package"],):
        if args[:2] == ["connectivity", "airplane-mode"]:
            if len(args) > 2:
                state["airplane"] = args[2] == "enable"
                save_state(state)
            else:
                out("enabled\n" if state["airplane"] else "disabled\n")
        elif args[:2] == ["uimode", "night"]:
            if len(args) > 2:
                state["night"] = args[2] == "yes"
                save_state(state)
            out(f"Night mode: {'yes' if state['night'] else 'no'}\n")
        elif args[:2] == ["appops", "reset"]:
            return 0
        return 0
    verb = words[0] if words else ""
    if verb == "list" and words[1:2] == ["packages"]:
        out("".join(f"package:{package}\n" for package in state["packages"]))
    elif verb == "query-activities":
        out("".join(f"{package}/.MainActivity\n" for package in state["packages"]))
    elif verb == "path":
        out(f"package:/data/app/~~abc==/{words[-1]}-1/base.apk\n" if words[-1] in state["packages"] else "")
        return 0 if words[-1] in state["packages"] else 1
    elif verb in ("grant", "revoke"):
        state["permissions"].setdefault(words[1], {})[words[2]] = verb == "grant"
        save_state(state)
    elif verb == "clear":
        out("Success\n")
    elif verb == "uninstall":
        package = words[-1]
        if package in state["packages"]:
            state["packages"].remove(package)
            save_state(state)
            out("Success\n")
        else:
            out("Failure [DELETE_FAILED_INTERNAL_ERROR]\n")
            return 1
    elif verb == "list" and words[1:2] == ["instrumentation"]:
        out(f"instrumentation:{PACKAGE}.test/androidx.test.runner.AndroidJUnitRunner (target={PACKAGE})\n")
    return 0


# ---------- adb ----------
def adb(argv):
    serial = None
    while argv and argv[0] in ("-s", "-t", "-H", "-P", "-d", "-e"):
        if argv[0] in ("-d", "-e"):
            argv = argv[1:]
            continue
        serial, argv = argv[1], argv[2:]
    if not argv:
        sys.stderr.write("adb: usage: no command\n")
        return 1
    command, rest = argv[0], argv[1:]
    out = sys.stdout.write
    if command == "version":
        out(f"Android Debug Bridge version 1.0.41\nVersion 35.0.2-12147458\nInstalled as {SELF}\nRunning on Linux (fake device)\n")
        return 0
    if command in ("start-server", "kill-server"):
        return 0
    if command == "devices":
        if os.environ.get("FAKE_ANDROID_STATE") == "no-permissions":
            # What adb prints on Linux when udev gives the user no access to the phone.
            out("List of devices attached\n" + f"{SERIAL}\tno permissions (missing udev rules? user is in the plugdev group); "
                "see [http://developer.android.com/tools/device.html] usb:1-1 transport_id:1\n\n")
        else:
            out("List of devices attached\n" + f"{SERIAL}               device usb:1-1 product:husky model:Pixel_8_Pro device:husky transport_id:1\n\n")
        return 0
    if command == "mdns":
        out("mdns daemon version [Openscreen discovery 0.0.0]\n" if rest[:1] == ["check"] else "List of discovered mdns services\n")
        return 0
    if command in ("connect", "pair"):
        out(f"failed to connect to '{rest[0] if rest else ''}': Connection refused\n")
        return 1
    if command == "disconnect":
        out("disconnected everything\n")
        return 0
    if serial not in (None, SERIAL):
        sys.stderr.write(f"adb: device '{serial}' not found\n")
        return 1
    state = load_state()
    if command == "get-state":
        out("device\n")
    elif command == "wait-for-device":
        pass
    elif command == "emu":
        sys.stderr.write("error: no emulator detected\n")
        return 1
    elif command in ("forward", "reverse"):
        key = command + "s"
        if rest[:1] == ["--list"]:
            out("".join(f"{SERIAL} {mapping}\n" for mapping in state[key]))
        elif rest[:1] == ["--remove"]:
            state[key] = [mapping for mapping in state[key] if not mapping.startswith(rest[1] + " ")]
            save_state(state)
        else:
            state[key].append(" ".join(rest[-2:]))
            save_state(state)
            out(rest[0].split(":")[-1] + "\n" if command == "forward" else "")
    elif command == "install":
        apk = rest[-1]
        if not os.path.isfile(apk):
            sys.stderr.write(f"adb: failed to stat {apk}: No such file or directory\n")
            return 1
        out("Performing Streamed Install\nSuccess\n")
    elif command == "pull":
        source, target = device_path(rest[0]), rest[1]
        if not os.path.exists(source):
            sys.stderr.write(f"adb: error: failed to stat remote object '{rest[0]}': No such file or directory\n")
            return 1
        if os.path.isdir(target):
            target = os.path.join(target, os.path.basename(source.rstrip("/")))
        (shutil.copytree if os.path.isdir(source) else shutil.copyfile)(source, target)
        out(f"{rest[0]}: 1 file pulled, 0 skipped. 42.0 MB/s ({os.path.getsize(target) if os.path.isfile(target) else 0} bytes in 0.001s)\n")
    elif command == "push":
        source, target = rest[0], device_path(rest[1])
        if os.path.isdir(target):
            target = os.path.join(target, os.path.basename(source))
        shutil.copyfile(source, target)
        out(f"{source}: 1 file pushed, 0 skipped. 40.1 MB/s ({os.path.getsize(source)} bytes in 0.001s)\n")
    elif command == "logcat":
        count = int(rest[rest.index("-t") + 1]) if "-t" in rest else 60
        out(logcat_lines(min(count, 60)))
    elif command == "exec-out":
        return run_shell(rest, binary=True)
    elif command == "shell":
        return run_shell(rest, binary=False)
    else:
        sys.stderr.write(f"adb: unknown command {command}\n")
        return 1
    return 0


def run_shell(args, binary):
    # Like adbd: the arguments are joined with spaces and run by the device shell.
    command = " ".join(args) if args else "sh"
    env = dict(os.environ, PATH=os.path.join(ROOT, ".bin") + ":" + os.environ.get("PATH", "/usr/bin:/bin"), HOME=ROOT)
    return subprocess.call(["/bin/sh", "-c", rewrite_paths(command)], env=env, cwd=ROOT)


def main():
    name = os.path.basename(sys.argv[0])
    os.makedirs(ROOT, exist_ok=True)
    if name in TOOLS:
        try:
            return tool(name, sys.argv[1:])
        except BrokenPipeError:
            return 0
    ensure_device()
    code = 1
    try:
        code = adb(sys.argv[1:])
        return code
    finally:
        with open(os.path.join(ROOT, "commands.log"), "a") as handle:
            handle.write(f"{time.strftime('%H:%M:%S')} exit={code} adb {' '.join(shlex.quote(a) for a in sys.argv[1:])}\n")


if __name__ == "__main__":
    sys.exit(main())
