"""Registers hdr2sdr.exe as a ShareX post-capture action.

Usage (from WSL, with ShareX closed):
    python3 tools/install_sharex_action.py [--keep-auto-jpeg] [config_path] [exe_windows_path]
Defaults: C:\\Users\\<you>\\Documents\\ShareX\\ApplicationConfig.json and C:\\Users\\<you>\\Tools\\hdr2sdr\\hdr2sdr.exe

By default this also turns off ShareX's "automatically use JPEG above N KB" option, because the
action only processes PNG files: with it on, large captures would silently skip the HDR fix.
Pass --keep-auto-jpeg to leave that setting alone.
"""
import json, shutil, subprocess, sys, time

args = [a for a in sys.argv[1:] if not a.startswith("--")]
keep_auto_jpeg = "--keep-auto-jpeg" in sys.argv
config = args[0] if len(args) > 0 else "/mnt/c/Users/<you>/Documents/ShareX/ApplicationConfig.json"
exe = args[1] if len(args) > 1 else r"C:\Users\<you>\Tools\hdr2sdr\hdr2sdr.exe"

running = subprocess.run(["/mnt/c/Windows/System32/tasklist.exe", "/FI", "IMAGENAME eq ShareX.exe"],
                         capture_output=True, text=True, cwd="/mnt/c").stdout
if "ShareX.exe" in running:
    sys.exit("ShareX is running. Close it first (it rewrites its config on exit), then rerun this script.")

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
    "Extensions": "png",
    "HiddenWindow": True,
    "DeleteInputFile": False,
})
task["ExternalPrograms"] = programs

jobs = [j.strip() for j in task.get("AfterCaptureJob", "").split(",") if j.strip()]
if "PerformActions" not in jobs:
    jobs.append("PerformActions")
task["AfterCaptureJob"] = ", ".join(jobs)

image = task.setdefault("ImageSettings", {})
if not keep_auto_jpeg and image.get("ImageAutoUseJPEG"):
    image["ImageAutoUseJPEG"] = False
    print("ImageAutoUseJPEG turned off so large captures stay PNG and get processed")

with open(config, "w", encoding="utf-8") as f:
    json.dump(cfg, f, indent=2)
print(f"updated {config} (backup: {backup})")
print(f"AfterCaptureJob = {task['AfterCaptureJob']}")
