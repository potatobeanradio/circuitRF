import json, os, struct, subprocess, sys, math

# Resolved from this script's own location, so the check runs from any clone on any machine.
TOOLS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W = os.path.join(TOOLS, "osdi-worker", "osdi-worker")
L = os.path.join(TOOLS, "fake-osdi-model", "fake_osdi.osdi")
p=subprocess.Popen([W,L],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
def send(obj, payload=b""):
    j=json.dumps(obj).encode()
    p.stdin.write(struct.pack("<II",len(j),len(payload))+j+payload); p.stdin.flush()
def recv():
    h=p.stdout.read(8)
    if len(h)<8: raise SystemExit("worker died: "+p.stderr.read().decode())
    jl,bl=struct.unpack("<II",h)
    j=json.loads(p.stdout.read(jl) or b"{}")
    b=p.stdout.read(bl)
    return j, list(struct.unpack("<%dd"%(bl//8), b))

send({"cmd":"describe"}); d,_=recv()
print("DESCRIBE:", json.dumps(d, indent=None))

# Units and descriptions ride on describe — they sit in the descriptor already, so they cost
# nothing and a compact model's hundreds of parameters are unreadable without them.
rc = next(t for t in d["types"] if t["typeId"] == "crf_rc")
by_name = {p["name"]: p for p in rc["params"]}
assert by_name["g0"]["units"] == "S", by_name["g0"]
assert by_name["g0"]["description"] == "conductance at tnom", by_name["g0"]
assert "temp" not in by_name, "op-vars are outputs and must not be offered as settable"
print("DESCRIBE units/description: ok")

# `defaults` is its own command, NOT part of describe: this ABI has no default field, so the only
# way to learn one is to stand a probe model up and read it back — a cost describe must not carry,
# because it runs on every worker launch including a PDK import's walk over every artefact.
send({"cmd":"defaults","typeId":"crf_rc"}); dv,_=recv()
dflt = {p["name"]: p["value"] for p in dv["params"]}
# tnom is defaulted in setup_model, mult in setup_instance — both must be read back, which is what
# proves the probe is genuinely SET UP and not just calloc'd.
assert dflt["tnom"] == 300.0, dflt
assert dflt["mult"] == 1.0,   dflt
assert dflt["g0"]   == 0.0,   dflt          # the model defaults it to nothing; reported honestly
assert "temp" not in dflt,    dflt          # op-var again
print("DEFAULTS crf_rc:", dflt)

send({"cmd":"defaults","typeId":"crf_fet"}); fv,_=recv()
fd = {p["name"]: p["value"] for p in fv["params"]}
assert fd["vth"] == -2.5 and fd["beta"] == 0.06, fd
print("DEFAULTS crf_fet:", fd)

# An unknown type is refused by name rather than answered with an empty set, which would read as
# "this model declares nothing".
send({"cmd":"defaults","typeId":"not_a_type"}); ev,_=recv()
assert "error" in ev, ev
print("DEFAULTS unknown-type refusal: ok")

# The probe model must not occupy one of the finite instance slots — MAX_INSTANCES is small, and a
# defaults call that leaked one would exhaust the table on a long editing session.
for _ in range(64):
    send({"cmd":"defaults","typeId":"crf_rc"}); recv()
send({"cmd":"create","typeId":"crf_rc","params":{},"temperatureK":300.0}); probe,_=recv()
assert "handle" in probe, probe
send({"cmd":"destroy","handle":probe["handle"]}); recv()
print("DEFAULTS leaks no instance slot: ok")

# ── a node's DISCIPLINE, and a terminal the instance does not connect ─────────────
#
# Both facts live in `crf_therm` and nowhere else in this library. The units are the only thing
# separating its node 2 from the two above it, and the terminal count is the only argument to
# setup_instance any device here reads.
th = next(t for t in d["types"] if t["typeId"] == "crf_therm")
tn = {n["index"]: n for n in th["nodes"]}
assert tn[2]["quantityKind"] == "thermal",    tn[2]
assert tn[2]["units"] == "K" and tn[2]["residualUnits"] == "W", tn[2]
assert tn[0]["quantityKind"] == "electrical", tn[0]
# The raw strings ride alongside the classification, so a discipline nobody anticipated arrives as
# itself rather than being silently reported as electrical.
assert tn[0]["units"] == "V" and tn[0]["residualUnits"] == "A", tn[0]
print("DESCRIBE node discipline: ok")

def therm(sh, connected=None):
    msg = {"cmd":"create","typeId":"crf_therm","params":{"g":0.01,"rth":50.0,"sh":sh},
           "temperatureK":300.0}
    if connected is not None: msg["connectedTerminals"] = connected
    send(msg)
    return recv()[0]

def therm_eval(handle, v):
    send({"cmd":"eval","handle":handle,"count":1}, struct.pack("<3d", *v))
    j, vals = recv()
    n = 3
    base = 1
    return vals[base:base+n], vals[base+2*n:base+2*n+n*n]

# All three terminals connected (the default, and what every caller that says nothing means): the
# model leaves its thermal node alone and the host must hold it.
all_on = therm(sh=1)
assert all_on["collapsed"] == [], all_on
I, G = therm_eval(all_on["handle"], (1.0, 0.0, 20.0))
#  I[T] = v2/rth - g*vab^2 = 20/50 - 0.01 = 0.39
assert abs(I[2] - 0.39) < 1e-12, I
assert abs(G[2*3+2] - 1.0/50.0) < 1e-12, G   # dI[T]/dV[T] = 1/rth

# The SAME device told its thermal terminal is not connected grounds it itself — which is the branch
# `$port_connected` exists for, and the one a host that always claims every terminal is connected
# can never reach.
two = therm(sh=1, connected=2)
assert two["collapsed"] == [{"node": 2, "to": -1}], two
I2, G2 = therm_eval(two["handle"], (1.0, 0.0, 20.0))
assert I2[2] == 0.0, I2
# ... and the electrical half is untouched, which is what says these are otherwise the same device.
assert abs(I2[0] - I[0]) < 1e-15 and abs(I2[1] - I[1]) < 1e-15, (I, I2)
print("CREATE connectedTerminals: ok")

# Out of range is refused rather than clamped: above the declared count describes a device this
# library does not have, and below two is not a device at all.
for bad in (1, 4):
    send({"cmd":"create","typeId":"crf_therm","params":{},"connectedTerminals":bad})
    r,_ = recv()
    assert "error" in r and "connectedTerminals" in r["error"], (bad, r)
print("CREATE connectedTerminals refusal: ok")

for h in (all_on["handle"], two["handle"]):
    send({"cmd":"destroy","handle":h}); recv()

send({"cmd":"create","typeId":"crf_rc",
      "params":{"g0":0.002,"c":1e-12,"tc":0.01,"tnom":300.0,"mult":1.0},
      "temperatureK":400.0})
c,_=recv(); print("CREATE:", c)

# two points: v=(1,0) and v=(0.5,0.25)
pts=[(1.0,0.0),(0.5,0.25)]
payload=struct.pack("<%dd"%(2*len(pts)), *[x for pt in pts for x in pt])
send({"cmd":"eval","handle":c["handle"],"count":len(pts)}, payload)
j,vals=recv()
n=2; per=2*n+2*n*n
print("EVAL hdr:", j, "len:", len(vals), "expected:", len(pts)+len(pts)*per)
ok=True
g=0.002*(1+0.01*(400-300)); cap=1e-12
for k,(a,b) in enumerate(pts):
    base=len(pts)+k*per
    I=vals[base:base+n]; Q=vals[base+n:base+2*n]
    G=vals[base+2*n:base+2*n+n*n]; C=vals[base+2*n+n*n:base+2*n+2*n*n]
    v=a-b
    exp_I=[g*v,-g*v]; exp_Q=[cap*v,-cap*v]
    exp_G=[g,-g,-g,g]; exp_C=[cap,-cap,-cap,cap]
    print(f" pt{k} status={vals[k]} I={I} Q={Q}")
    for got,exp,nm in ((I,exp_I,"I"),(Q,exp_Q,"Q"),(G,exp_G,"G"),(C,exp_C,"C")):
        for gv,ev in zip(got,exp):
            if abs(gv-ev) > 1e-18 + 1e-12*abs(ev):
                print(f"   MISMATCH {nm}: got {gv} want {ev}"); ok=False
send({"cmd":"shutdown"}); recv()
print("CLOSED-FORM MATCH:", ok)
sys.exit(0 if ok else 1)
