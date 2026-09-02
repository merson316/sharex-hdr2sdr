"""Installs hdr2sdr: registers hdr2sdr.exe as a logon task (Task Scheduler, no admin rights) and starts it.
Also removes the obsolete "HDR to SDR" ShareX post-capture action from earlier versions, if present.

Run from WSL or Windows Python:

    python3 tools/install.py [--uninstall] [exe_windows_path]

Default exe path: C:\\Users\\<user>\\Tools\\hdr2sdr\\hdr2sdr.exe (where tools/publish.sh puts it).
Removing the ShareX action needs ShareX closed; the script tells you if it is running and skips that step.
"""
import json, os, shutil, subprocess, sys, time

ON_WSL = os.path.isdir("/mnt/c/Windows/System32")
SYS32 = "/mnt/c/Windows/System32" if ON_WSL else r"C:\Windows\System32"


def run(*args):
    return subprocess.run(list(args), capture_output=True, text=True, cwd="/mnt/c" if ON_WSL else None)


def powershell(script):
    return run(f"{SYS32}/WindowsPowerShell/v1.0/powershell.exe", "-NoProfile", "-Command", script)


def windows_user():
    if not ON_WSL:
        return os.environ.get("USERNAME", "")
    return run(f"{SYS32}/cmd.exe", "/c", "echo %USERNAME%").stdout.strip()


def to_local(win_path):
    return ("/mnt/c/" + win_path[3:].replace("\\", "/")) if ON_WSL else win_path


user = windows_user()
uninstall = "--uninstall" in sys.argv
args = [a for a in sys.argv[1:] if not a.startswith("--")]
exe = args[0] if args else rf"C:\Users\{user}\Tools\hdr2sdr\hdr2sdr.exe"

# 1) logon task
if uninstall:
    run(f"{SYS32}/taskkill.exe", "/IM", "hdr2sdr.exe", "/F")
    r = powershell("Unregister-ScheduledTask -TaskName hdr2sdr -Confirm:$false")
    print("logon task:", "removed" if r.returncode == 0 else "not present")
else:
    if not os.path.isfile(to_local(exe)):
        sys.exit(f"{exe} not found; run tools/publish.sh first or pass the exe path")
    run(f"{SYS32}/taskkill.exe", "/IM", "hdr2sdr.exe", "/F")
    r = powershell(
        f"$a = New-ScheduledTaskAction -Execute '{exe}'; "
        "$t = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; "
        "$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; "
        "Register-ScheduledTask -TaskName hdr2sdr -Action $a -Trigger $t -Settings $s -RunLevel Limited -Force | Out-Null; "
        "Start-ScheduledTask -TaskName hdr2sdr")
    print("logon task:", "registered and started" if r.returncode == 0 else "FAILED " + (r.stdout + r.stderr).strip())
    # earlier versions used a different task name
    powershell("Unregister-ScheduledTask -TaskName hdr2sdr-helper -Confirm:$false -ErrorAction SilentlyContinue")

# 2) obsolete ShareX action from v0.1-v0.3
config = (f"/mnt/c/Users/{user}/Documents/ShareX/ApplicationConfig.json" if ON_WSL
          else os.path.expandvars(r"%USERPROFILE%\Documents\ShareX\ApplicationConfig.json"))
if os.path.isfile(config):
    with open(config, encoding="utf-8-sig") as f:
        cfg = json.load(f)
    task = cfg.get("DefaultTaskSettings", {})
    programs = task.get("ExternalPrograms") or []
    ours = [p for p in programs if p.get("Name") == "HDR to SDR"]
    if ours:
        if "ShareX.exe" in run(f"{SYS32}/tasklist.exe", "/FI", "IMAGENAME eq ShareX.exe").stdout:
            print("ShareX is running: close it and rerun to remove the obsolete 'HDR to SDR' action (ShareX rewrites its config on exit)")
        else:
            shutil.copy2(config, config + "." + time.strftime("%Y%m%d-%H%M%S") + ".bak")
            task["ExternalPrograms"] = [p for p in programs if p.get("Name") != "HDR to SDR"]
            if not any(p.get("IsActive") for p in task["ExternalPrograms"]):
                jobs = [j.strip() for j in task.get("AfterCaptureJob", "").split(",") if j.strip() and j.strip() != "PerformActions"]
                task["AfterCaptureJob"] = ", ".join(jobs)
            with open(config, "w", encoding="utf-8") as f:
                json.dump(cfg, f, indent=2)
            print("removed the obsolete 'HDR to SDR' ShareX action")
