"""Registers hdr2sdr.exe as a ShareX post-capture action and enables "Perform actions".

Run from WSL (or Windows Python) with ShareX closed, because ShareX rewrites its config on exit:

    python3 tools/install_sharex_action.py [config_path] [exe_windows_path]

Defaults: the ShareX config in the current Windows user's Documents folder, and
C:\\Users\\<user>\\Tools\\hdr2sdr\\hdr2sdr.exe (where tools/publish.sh installs the exe).
A timestamped backup of the config is written next to it.
"""
import json, os, shutil, subprocess, sys, time

ON_WSL = os.path.isdir("/mnt/c/Windows/System32")
SYS32 = "/mnt/c/Windows/System32" if ON_WSL else r"C:\Windows\System32"


def windows_user():
    if not ON_WSL:
        return os.environ.get("USERNAME", "")
    out = subprocess.run([f"{SYS32}/cmd.exe", "/c", "echo %USERNAME%"], capture_output=True, text=True, cwd="/mnt/c").stdout
    return out.strip()


user = windows_user()
args = [a for a in sys.argv[1:] if not a.startswith("--")]
if ON_WSL:
    config = args[0] if len(args) > 0 else f"/mnt/c/Users/{user}/Documents/ShareX/ApplicationConfig.json"
else:
    config = args[0] if len(args) > 0 else os.path.expandvars(r"%USERPROFILE%\Documents\ShareX\ApplicationConfig.json")
exe = args[1] if len(args) > 1 else rf"C:\Users\{user}\Tools\hdr2sdr\hdr2sdr.exe"

running = subprocess.run([f"{SYS32}/tasklist.exe", "/FI", "IMAGENAME eq ShareX.exe"],
                         capture_output=True, text=True, cwd="/mnt/c" if ON_WSL else None).stdout
if "ShareX.exe" in running:
    sys.exit("ShareX is running. Close it first (it rewrites its config on exit), then rerun this script.")
if not os.path.isfile(config):
    sys.exit(f"ShareX config not found at {config}; pass its path as the first argument.")

with open(config, encoding="utf-8-sig") as f:
    cfg = json.load(f)
backup = config + "." + time.strftime("%Y%m%d-%H%M%S") + ".bak"
shutil.copy2(config, backup)

task = cfg.setdefault("DefaultTaskSettings", {})
programs = task.setdefault("ExternalPrograms", []) or []
programs = [p for p in programs if p.get("Name") != "HDR to SDR"]
programs.append({
    "IsActive": True,
    "Name": "HDR to SDR",
    "Path": exe,
    "Args": "\"$input\"",
    "OutputExtension": None,
    "Extensions": "png, jpg, jpeg, bmp, gif, tif, tiff, webp",
    "HiddenWindow": True,
    "DeleteInputFile": False,
})
task["ExternalPrograms"] = programs

jobs = [j.strip() for j in task.get("AfterCaptureJob", "").split(",") if j.strip()]
if "PerformActions" not in jobs:
    jobs.append("PerformActions")
task["AfterCaptureJob"] = ", ".join(jobs)

with open(config, "w", encoding="utf-8") as f:
    json.dump(cfg, f, indent=2)
print(f"updated {config} (backup: {backup})")
print(f"action path: {exe}")
print(f"AfterCaptureJob = {task['AfterCaptureJob']}")
