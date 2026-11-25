"""Verify production IPC and WASAPI operations against one disposable source application."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import uuid

common = Path(__file__).resolve().parents[1] / "Measurements" / "check-product-host.py"
spec = importlib.util.spec_from_file_location("host_checks", common)
host = importlib.util.module_from_spec(spec)
spec.loader.exec_module(host)

def check(exe, source_exe, out):
    profile = "audio-" + uuid.uuid4().hex
    out.mkdir(parents=True, exist_ok=True)
    fixture_root = out / profile
    fixture_root.mkdir()
    source = fixture_root / ("SRVolume" + profile[-8:] + ".exe")
    shutil.copyfile(source_exe, source)
    env = dict(os.environ, SONICROUTE_CANDIDATE_ROOT=str(out.resolve()), SONICROUTE_CANDIDATE_ID=profile,
               SONICROUTE_CANDIDATE_METRICS="1")
    server = subprocess.Popen([str(exe)], env=env)
    def command(*args): return host.command(exe, env, "--control", *args)
    def read(*args): return json.loads(command(*args))
    def defaults(): return {f: {r: command("default", f, r) for r in ("console", "multimedia", "communications")}
                            for f in ("render", "capture")}
    evidence = {"sha256": hashlib.sha256(exe.read_bytes()).hexdigest()}
    emitter = None
    try:
        evidence["host"] = host.await_ready(exe, server, env)
        initial = defaults()
        devices = {f: read("devices", f) for f in initial}
        evidence["endpoints"] = {f: len(v) for f, v in devices.items()}
        evidence["globalVolumeRead"] = {f: read("volume", f, "default") for f in initial}
        emitter = subprocess.Popen([str(source), "--source"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        assert emitter.stdout.readline().strip() == b"READY"
        pid = str(emitter.pid)
        old = read("volume", "render", pid)
        evidence["fixture"] = {"pid": emitter.pid, "originalVolume": old}
        assert old["Count"] >= 1
        # The shared foreground/app catalog caches one enumeration for one second.
        for _ in range(12):
            if any(app["Pid"] == emitter.pid for app in read("apps")): break
            time.sleep(.12)
        else: raise AssertionError("The fixture was absent after the app snapshot TTL.")
        assert command("observe", "render", pid) == "ok"
        notifications = int(command("notifications"))
        original_routes = {f: command("route", pid, f) for f in initial}
        assert all(r == "default" for r in original_routes.values()), original_routes
        try:
            changed = 41 if old["Percent"] != 41 else 63
            assert read("volume", "render", pid, "level", str(changed))["Percent"] == changed
            assert read("volume", "render", pid, "mute", str(not old["Muted"]))["Muted"] != old["Muted"]
            for value in (26, 39, 63):
                assert int(command("volume-queue", pid, str(value))) > 0
            assert command("volume-flush") == "ok"
            assert read("volume", "render", pid)["Percent"] == 63
            n = int(command("notifications"))
            assert n > notifications
            evidence["fixture"].update(volumeNotifications=n - notifications, flushedPercent=63)
            for flow in ("render", "capture"):
                target = initial[flow]["multimedia"]
                assert command("route", pid, flow, target) == "ok"
                assert command("route", pid, flow).lower() == target.lower()
                assert command("route", pid, flow, "default") == "ok"
                assert command("route", pid, flow) == "default"
            evidence["bothFlowRouteRoundTrips"] = True
        finally:
            # Only this private application's policy & session can have been changed.
            if emitter.poll() is None:
                for flow in original_routes:
                    if command("route", pid, flow) != original_routes[flow]:
                        command("route", pid, flow, original_routes[flow])
                command("unobserve")
                before = int(command("notifications"))
                read("volume", "render", pid, "level", str(old["Percent"]))
                read("volume", "render", pid, "mute", str(old["Muted"]))
                after = read("volume", "render", pid)
                assert after == old and int(command("notifications")) == before
                evidence["fixture"]["restored"] = after
        assert defaults() == initial
        evidence["defaultsPreserved"] = initial
    finally:
        if emitter:
            if emitter.poll() is None: emitter.stdin.close()
            emitter.wait(timeout=15)
        if server.poll() is None: command("exit")
        evidence["exitCode"] = server.wait(timeout=15)
        assert evidence["exitCode"] == 0
        receipt = Path(evidence["host"]["DataDirectory"]).parent / "last-exit.json"
        evidence["release"] = json.loads(receipt.read_text("utf-8-sig"))
        assert all(count == 0 for count in evidence["release"]["AfterServiceStop"].values())
    (out / "audio.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2), encoding="utf-8")

if __name__ == "__main__":
    p = argparse.ArgumentParser()
    for name in ("exe", "source", "output"): p.add_argument(name, type=Path)
    options = p.parse_args()
    check(options.exe.resolve(), options.source.resolve(), options.output.resolve())
