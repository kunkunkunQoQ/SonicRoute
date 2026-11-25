"""Run the Stage 2 gate against two published Windows binaries using private profiles."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

user32 = ctypes.windll.user32

def command(exe, env, *args, expected=0):
    result = subprocess.run([str(exe), *args], env=env, capture_output=True, timeout=20)
    text = result.stdout.decode("utf-8-sig").strip()
    if result.returncode != expected:
        raise RuntimeError(f"{args}: exit {result.returncode}, output {text}")
    return text

def snapshot(exe, env):
    return json.loads(command(exe, env, "--control", "status"))

def await_ready(exe, proc, env):
    for _ in range(60):
        if proc.poll() is not None:
            raise RuntimeError(f"Host exited early with {proc.returncode}")
        status = subprocess.run([str(exe), "--control", "status"], env=env, capture_output=True, timeout=8)
        if not status.returncode and status.stdout.strip(): return json.loads(status.stdout.decode("utf-8-sig"))
        time.sleep(.1)
    raise TimeoutError("Startup failed")

def loaded_modules(pid):
    script = f"(Get-Process -Id {pid}).Modules | ForEach-Object ModuleName"
    result = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, check=True)
    return sorted(set(result.stdout.decode("ascii", "replace").lower().splitlines()))

def check(binaries, output):
    result, servers = {}, []
    output.mkdir(parents=True, exist_ok=True)
    profile = "host-" + uuid.uuid4().hex
    env = dict(os.environ, SONICROUTE_CANDIDATE_ROOT=str(output.resolve()), SONICROUTE_CANDIDATE_ID=profile)
    for key in ("SONICROUTE_DATA_DIRECTORY", "SONICROUTE_INSTANCE_ID"):
        env.pop(key, None)
    try:
        for edition in ("Full", "Lite"):
            exe = (binaries / (edition + "-x64") / "SonicRoute.exe").resolve()
            proc = subprocess.Popen([str(exe)], env=env)
            servers.append((proc, exe))
            status = await_ready(exe, proc, env)
            assert status["Pid"] == proc.pid and status["Edition"] == edition and status["NativeAot"]
            assert status["TrayRegistered"] and not status["ReflectionJson"] and status["Feedback"] is None
            assert not user32.IsWindowVisible(status["Window"])
            assert Path(status["DataDirectory"]).resolve() == output.resolve() / "SonicRoute.NativeCandidate" / profile / edition / "data"
            modules = loaded_modules(proc.pid)
            assert not {"coreclr.dll", "clrjit.dll", "presentationframework.dll", "wpfgfx_cor3.dll", "d2d1.dll", "dwrite.dll"}.intersection(modules)
            assert user32.PostMessageW(status["Window"], 0x1E, 0, 0)
            assert snapshot(exe, env)["LastSignal"] == "Time"
            taskbar = user32.RegisterWindowMessageW("TaskbarCreated")
            assert taskbar and user32.PostMessageW(status["Window"], taskbar, 0, 0)
            restored = snapshot(exe, env)
            assert restored["TrayRegistrations"] > status["TrayRegistrations"] and restored["TrayRegistered"]
            command(exe, env)
            activated = snapshot(exe, env)
            assert activated["Pid"] == proc.pid and activated["Feedback"]["Generation"] == 1
            assert command(exe, env, "--control", "language", "de-DE") == "ok"
            assert snapshot(exe, env)["Language"] == "de-DE"
            assert json.loads((Path(status["DataDirectory"]) / "config.json").read_text("utf-8-sig"))["Language"] == "de-DE"
            missing = json.loads(command(exe, env, "--run-rule", "absent-" + profile, expected=3))
            assert missing["Code"] == "rule_not_found"
            result[edition] = dict(start=status, restored=restored, activated=activated, modules=modules, missingRule=missing,
                exe=str(exe), sha256=hashlib.sha256(exe.read_bytes()).hexdigest())
        assert result["Full"]["start"]["Window"] != result["Lite"]["start"]["Window"]
        for proc, exe in servers:
            assert command(exe, env, "--control", "exit") == "ok" and proc.wait(timeout=15) == 0
        for edition, record in result.items():
            assert not user32.IsWindow(record["start"]["Window"])
            exe = Path(record["exe"])
            proc = subprocess.Popen([str(exe)], env=env)
            try:
                assert await_ready(exe, proc, env)["Language"] == "de-DE"
            finally:
                command(exe, env, "--control", "exit")
                proc.wait(timeout=15)
            record["exitAndFreshReload"] = True
        (output / "gate.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    finally:
        for proc, exe in servers:
            if proc.poll() is None:
                command(exe, env, "--control", "exit")
                proc.wait(timeout=15)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("binaries", type=Path)
    parser.add_argument("output", type=Path)
    options = parser.parse_args()
    check(options.binaries, options.output)
