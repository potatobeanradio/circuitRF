"""Checks for verify-windows.sh. Runs inside the container, as an unprivileged user, under Wine.

Everything here is a property of a RUNNING process, which is why none of it can be a C# test: a file
staged under a name read out of a model's import table, an import binding to an already-loaded
module, and stdio mode only exist once something is actually loaded.
"""

import glob
import json
import os
import signal
import struct
import subprocess
import sys

WINE  = "/usr/lib/wine/wine64"
SHIM  = "/home/crf/wp/drive_c/users/crf/AppData/Local/circuitRF/hostshim"
MODEL = "/home/crf/fake_model.dll"
ENV   = dict(os.environ, WINEDEBUG="-all", WINEPREFIX="/home/crf/wp", HOME="/home/crf")

failures = []


def check(name, ok, detail=""):
    print(("  PASS  " if ok else "  FAIL  ") + name + (("  — " + detail) if detail else ""))
    if not ok:
        failures.append(name)


_log_seq = [0]


def worker(exe, model):
    """Spawn a worker with stderr going to a FILE, never a pipe.

    A pipe nobody drains is a deadlock waiting to happen: the worker (and Wine behind it) writes
    diagnostics, the 64 KB buffer fills, the write blocks, and our next blocking read never returns —
    which looks exactly like "emulation is slow". Learned here the hard way; do not switch it back.
    """
    _log_seq[0] += 1
    err = open("/tmp/worker-%d.stderr" % _log_seq[0], "wb")
    return subprocess.Popen([WINE, exe, model],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=err, env=ENV)


class Stuck(Exception):
    pass


def _alarm(_sig, _frm):
    raise Stuck()


signal.signal(signal.SIGALRM, _alarm)


def send(p, obj, blob=b""):
    j = json.dumps(obj).encode()
    p.stdin.write(struct.pack("<II", len(j), len(blob)) + j + blob)
    p.stdin.flush()


def recv(p, seconds=120):
    """A frame, or (None, None) if the stream desynced, the worker died, or it went quiet.

    The timeout matters as much as the parsing: a corrupted length field makes the next read ask for
    megabytes that never arrive, and without a deadline that is indistinguishable from a slow run.
    """
    signal.alarm(seconds)
    try:
        return _recv(p)
    except Stuck:
        return None, None
    finally:
        signal.alarm(0)


def _recv(p):
    head = p.stdout.read(8)
    if len(head) < 8:
        return None, None
    jlen, blen = struct.unpack("<II", head)
    if jlen > (1 << 20) or blen > (1 << 28):
        return None, None                      # an implausible length IS the desync symptom
    body = p.stdout.read(jlen)
    blob = p.stdout.read(blen)
    if len(blob) != blen:
        return None, None
    return json.loads(body), blob


def staged():
    return sorted(glob.glob(SHIM + "/*/*.dll"))


def currents_from(blob, voltages):
    """Per point: I[2], Q[2], G[2x2], C[2x2], after a status double per point."""
    per = 2 * 2 + 2 * 2 * 2
    vals = struct.unpack("<%dd" % (len(blob) // 8), blob)
    return [(vals[len(voltages) + i * per], vals[len(voltages) + i * per + 1])
            for i in range(len(voltages))]


# ── 1. derive → stage → load → boot, from a read-only install ────────────────
print("1. derive the host module, stage the shim, load the model, boot the family")

p = worker("/opt/crf/senior_worker.exe", MODEL)
described, _ = recv(p)

check("describe answers with the family the library serves",
      described is not None and described.get("ok")
      and described["types"][0]["typeId"] == "CRF_TEST_V1")

if described:
    t = described["types"][0]
    check("its pin and parameter counts came from the model, not from us",
          t["externalPinCount"] == 2 and t["internalNodeCount"] == 0
          and t["params"][0]["name"] == "W")

check("the shim was staged under the name derived from the model's own import table",
      any(f.endswith("crf_test_host.dll") for f in staged()),
      ", ".join(os.path.basename(f) for f in staged()))

# ── 2. R-win-7: raw doubles containing 0x0A survive stdio, both directions ───
print("2. R-win-7: raw doubles containing 0x0A survive stdio in both directions")

send(p, {"cmd": "create", "typeId": "CRF_TEST_V1", "params": {"W": 1e-4}})
created, _ = recv(p)

voltages = []
for k in range(1, 40000):
    v = k * 1e-3
    if b"\x0a" in struct.pack("<2d", v, 0.0) and b"\x0a" in struct.pack("<d", 0.01 * v):
        voltages.append(v)
        if len(voltages) == 3:
            break

payload = b"".join(struct.pack("<2d", v, 0.0) for v in voltages)
check("the payload really does contain 0x0A in both directions (else this proves nothing)",
      len(voltages) == 3 and b"\x0a" in payload)

exact = created is not None
if exact:
    send(p, {"cmd": "eval", "handle": created["handle"], "count": len(voltages)}, payload)
    evaluated, blob = recv(p)
    exact = evaluated is not None
    if exact:
        for v, (i0, i1) in zip(voltages, currents_from(blob, voltages)):
            if i0 != 0.01 * v or i1 != -0.01 * v:
                exact = False

check("every current is bit-exact and the stream stays framed", exact)

send(p, {"cmd": "shutdown"})
recv(p)
p.wait(timeout=60)

# ── 3. the control — the same payload with _setmode removed MUST break ───────
print("3. the control — the same payload against a build with _setmode removed")

os.makedirs("/tmp/nosmrun", exist_ok=True)
os.system("cp /opt/crf/senior_worker.exe /tmp/nosmrun/ && "
          "cp /tmp/nosm/crf-model-host-nosetmode.dll /tmp/nosmrun/crf-model-host.dll && "
          "chown -R crf:crf /tmp/nosmrun 2>/dev/null; true")

q = worker("/tmp/nosmrun/senior_worker.exe", MODEL)
control_described, _ = recv(q)

# The point of this one: a describe-only test passes with the bug present, which is exactly why
# the bug is easy to ship.
check("describe STILL passes without it — so a describe test could never catch this",
      control_described is not None and control_described.get("ok"))

broken = True
send(q, {"cmd": "create", "typeId": "CRF_TEST_V1", "params": {"W": 1e-4}})
control_created, _ = recv(q)
if control_created is not None:
    send(q, {"cmd": "eval", "handle": control_created["handle"], "count": len(voltages)}, payload)
    control_eval, control_blob = recv(q)
    if control_eval is not None:
        broken = any(i0 != 0.01 * v
                     for v, (i0, _) in zip(voltages, currents_from(control_blob, voltages)))

check("the doubles ARE corrupted without _setmode — so the fix is load-bearing", broken)
try:
    q.kill()
except Exception:
    pass

# ── 4. staging: a second derived name coexists; a newer shim refreshes ───────
print("4. staging")

p2 = worker("/opt/crf/senior_worker.exe", "/home/crf/fake_model2.dll")
described2, _ = recv(p2)
check("a model naming a DIFFERENT host module boots too",
      described2 is not None and described2.get("ok"))

names = [os.path.basename(f) for f in staged()]
check("both staged names coexist, each in its own directory",
      "crf_test_host.dll" in names and "crf_other_host.dll" in names, ", ".join(names))

send(p2, {"cmd": "shutdown"})
recv(p2)
p2.wait(timeout=60)

before = [os.path.getsize(f) for f in staged() if f.endswith("crf_test_host.dll")][0]
os.system("chmod -R u+w /opt/crf && "
          "cp /tmp/nosm/crf-model-host-nosetmode.dll /opt/crf/crf-model-host.dll && "
          "touch /opt/crf/crf-model-host.dll && chmod -R 555 /opt/crf")

p3 = worker("/opt/crf/senior_worker.exe", MODEL)
recv(p3)
after = [os.path.getsize(f) for f in staged() if f.endswith("crf_test_host.dll")][0]
check("a newer shipped shim refreshes the staged copy",
      after != before and after == os.path.getsize("/opt/crf/crf-model-host.dll"),
      "%d -> %d bytes" % (before, after))
try:
    p3.kill()
except Exception:
    pass

print()
print("RESULT: " + ("PASS" if not failures else "FAIL (" + "; ".join(failures) + ")"))
sys.exit(1 if failures else 0)
