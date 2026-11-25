"""Two real rule clients share a temporary host and both receive their JSON replies."""
import json
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

exe, root = map(lambda arg: Path(arg).resolve(), sys.argv[1:])
profile = "temp-" + uuid.uuid4().hex
env = dict(os.environ, SONICROUTE_CANDIDATE_ROOT=str(root), SONICROUTE_CANDIDATE_ID=profile)
rules = root / "SonicRoute.NativeCandidate" / profile / "Full/data/Automation"
rules.mkdir(parents=True)
for name, delay in [("first", 900), ("queued", 1750)]:
    identifier = uuid.uuid4().hex
    value = {"Id": identifier, "Name": name, "Enabled": False,
             "Actions": [{"Action": 10, "OsdTitle": name, "DelayMs": delay}]}
    (rules / (identifier + ".json")).write_text(json.dumps(value), encoding="utf-8")
owner = subprocess.Popen([str(exe), "--run-rule", "first"], env=env, stdout=subprocess.PIPE)
time.sleep(.2)
status = json.loads(subprocess.check_output([str(exe), "--control", "status"], env=env, timeout=4))
assert status["Pid"] == owner.pid and not status["TrayRegistered"]
client = subprocess.Popen([str(exe), "--run-rule", "queued"], env=env, stdout=subprocess.PIPE)
second = json.loads(client.communicate(timeout=10)[0].decode("utf-8-sig"))
first = json.loads(owner.communicate(timeout=10)[0].decode("utf-8-sig"))
assert client.returncode == owner.returncode == 0
assert second["RuleName"] == "queued" and first["RuleName"] == "first" and first["Succeeded"] == second["Succeeded"] == 1
(root / "receipt.json").write_text(json.dumps(dict(pid=owner.pid, tray=status["TrayRegistered"], first=first, second=second), indent=2))
