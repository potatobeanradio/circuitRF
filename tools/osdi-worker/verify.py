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

# ── op-vars are DECLARED, in their own list, and never in both ────────────────────
#
# A quantity is a parameter or an output, never both: offering an output as settable would put a
# writable box in the editor for a value the model computes. The two lists are therefore checked
# against each other by name across EVERY type, not only on the one that carries them.
fet = next(t for t in d["types"] if t["typeId"] == "crf_fet")
for t in d["types"]:
    pn = {p["name"] for p in t["params"]}
    on = {o["name"] for o in t["opvars"]}
    assert not (pn & on), (t["typeId"], pn & on)

# More than one type declares them, and all three types the ABI allows are present - the string in
# particular, because it is the one that must be DECLARED here and absent from every read-back.
assert {o["name"] for o in rc["opvars"]} == {"temp_k"}, rc["opvars"]
fops = {o["name"]: o for o in fet["opvars"]}
assert set(fops) == {"id", "gm", "gds", "vov", "region", "regime"}, fops
assert fops["gm"]["type"] == "double" and fops["gm"]["units"] == "S", fops["gm"]
assert fops["gm"]["description"] == "transconductance", fops["gm"]
assert fops["region"]["type"] == "int",    fops["region"]
assert fops["regime"]["type"] == "string", fops["regime"]
# A type that computes nothing says so with an empty list rather than by omitting the key, so a
# host never has to tell "declares none" from "does not speak this protocol".
assert next(t for t in d["types"] if t["typeId"] == "crf_therm")["opvars"] == []
print("DESCRIBE opvars: ok")

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
# `$simparam`: gmin is answered by the host, so the model's scan works; shrink must NOT be, as the
# reference SPICE simulator does not answer it. The worker once answered 1.0, which a compiled
# resistor reads as a 1 % geometry shrink.
assert dflt["gmin"]   == 1e-12, dflt
assert dflt["shrink"] == 0.0,   dflt
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

# ── reading op-vars back ──────────────────────────────────────────────────────────
#
# A read-back is a DEREFERENCE, positioned correctly in time - not a computation the worker
# performs. So the check that matters is not "is the number plausible" but "is it the number for the
# bias the caller last evaluated", and the only way to see that is to evaluate at two different
# biases and read after each. A read that lagged by one point would sail through a single-bias test.
def fet_closed_form(vgs, vds, mult=1.0,
                    beta=0.06, vth=-2.5, lam=0.02, alpha=1.5, delta=0.2):
    u    = vgs - vth
    root = math.sqrt(u*u + delta*delta)
    vov  = 0.5 * (u + root)
    dvov = 0.5 * (1.0 + u / root)
    x    = alpha * vds
    sc   = 1.0 / math.sqrt(1.0 + x*x)
    sat  = x * sc
    dsat = alpha * sc*sc*sc
    chan = 1.0 + lam * vds
    return {
        "id":     mult * beta * vov*vov * chan * sat,
        "gm":     mult * 2.0 * beta * vov * dvov * chan * sat,
        "gds":    mult * beta * vov*vov * (lam*sat + chan*dsat),
        "vov":    vov,
        "region": 0.0 if vov <= 0.5*delta else (1.0 if sat < 0.9 else 2.0),
    }

send({"cmd":"create","typeId":"crf_fet","params":{},"temperatureK":300.0}); fh,_=recv()
FH = fh["handle"]

def read_opvars(handle):
    send({"cmd":"opvars","handle":handle})
    j, vals = recv()
    return j, dict(zip(j["names"], vals))

# A READ WITH NO PRIOR EVAL IS DEFINED: it reports the instance as setup_instance left it. Every
# op-var here is written only by the load, so they are all at their zero-initialised value - an
# honest "the model has computed nothing yet", not a refusal and not an invented bias.
j0, before = read_opvars(FH)
# The STRING op-var is declared and is not readable: a single-kind numeric cube has nowhere to put
# it. It must be absent from the names rather than present as a zero.
assert j0["names"] == ["id", "gm", "gds", "vov", "region"], j0
assert all(v == 0.0 for v in before.values()), before
print("OPVARS before any eval:", before)

def fet_eval(handle, vgs, vds, want_opvars=False):
    msg = {"cmd":"eval","handle":handle,"count":1}
    if want_opvars: msg["opvars"] = True
    send(msg, struct.pack("<3d", vgs, vds, 0.0))     # v = (G, D, S) with S at 0
    return recv()

for vgs, vds in ((-1.0, 8.0), (-2.4, 0.3)):
    fet_eval(FH, vgs, vds)
    _, got = read_opvars(FH)
    want = fet_closed_form(vgs, vds)
    for k, w in want.items():
        assert abs(got[k] - w) <= 1e-15 + 1e-12*abs(w), (vgs, vds, k, got[k], w)
    print(f"OPVARS after eval({vgs}, {vds}):", {k: round(v, 9) for k, v in got.items()})
print("OPVARS track the last evaluated bias: ok")
# crf_fet calls through its $limit table and keeps state on every eval, so getting this far with the
# closed form intact is the check that the worker fills the table with identities and the state vectors.
print("$limit table + state vectors: ok")

# The second bias is deliberately in a DIFFERENT region from the first, so a read that lagged by one
# point would disagree on an integer and not merely in the last digits.
assert fet_closed_form(-1.0, 8.0)["region"] != fet_closed_form(-2.4, 0.3)["region"]

# Reading allocates nothing: it touches an instance the host already owns, so unlike `defaults`
# there is no probe to stand up and no slot to leak. 64 reads must leave the table where it started.
before_h = fh["handle"]
for _ in range(64): read_opvars(FH)
send({"cmd":"create","typeId":"crf_rc","params":{},"temperatureK":300.0}); nxt,_=recv()
assert nxt["handle"] == before_h + 1, (before_h, nxt)
send({"cmd":"destroy","handle":nxt["handle"]}); recv()
print("OPVARS leak no instance slot: ok")

# An unknown handle is refused by name rather than answered with an empty set, which would read as
# "this device declares nothing".
send({"cmd":"opvars","handle":9999}); bad,_=recv()
assert "error" in bad and "opvars" in bad["error"], bad
print("OPVARS unknown-handle refusal: ok")

# ── op-vars PER POINT, which is the only shape harmonic balance can use ───────────
#
# `opvars` reads the storage, which holds the LAST bias evaluated - right for a DC operating point
# and useless for a time grid handed over in one call. Asked for on the eval itself, the values ride
# in the same payload, one block per point, and every one must match ITS OWN point.
pts = [(-1.0, 8.0), (-2.4, 0.3), (-0.5, 2.0), (-3.4, 5.0)]
msg = {"cmd":"eval","handle":FH,"count":len(pts),"opvars":True}
send(msg, struct.pack("<%dd" % (3*len(pts)), *[x for (g, dd) in pts for x in (g, dd, 0.0)]))
j, vals = recv()
names = j["opvarNames"]
assert names == ["id", "gm", "gds", "vov", "region"], j
n = 3
per = 2*n + 2*n*n + len(names)
assert len(vals) == len(pts) + len(pts)*per, (len(vals), len(pts), per)
for k, (vgs, vds) in enumerate(pts):
    base = len(pts) + k*per + 2*n + 2*n*n
    got  = dict(zip(names, vals[base:base+len(names)]))
    want = fet_closed_form(vgs, vds)
    for nm, w in want.items():
        assert abs(got[nm] - w) <= 1e-15 + 1e-12*abs(w), (k, nm, got[nm], w)
# Not four copies of one point, which is exactly what a capture taken after the loop would give.
assert len({round(v, 12) for v in
            [vals[len(pts) + k*per + 2*n + 2*n*n] for k in range(len(pts))]}) == len(pts)
print("EVAL opvars per point: ok")

# Without the flag the payload is the shape it always was - an existing caller sees no change.
_, plain = fet_eval(FH, -1.0, 8.0)
assert len(plain) == 1 + (2*n + 2*n*n), len(plain)
print("EVAL without the flag is unchanged: ok")

send({"cmd":"destroy","handle":FH}); recv()

# ── noise: sources in describe, densities at the bias the caller states ──────────
#
# Every type carries the key; crf_noise is the only one with sources. Node pairs use describe's own
# node numbering (-1 = ground, unused here).
assert all("noise" in t for t in d["types"]), d
assert next(t for t in d["types"] if t["typeId"] == "crf_rc")["noise"] == []
nz = next(t for t in d["types"] if t["typeId"] == "crf_noise")
assert nz["noise"] == [{"name": "thermal", "nodes": [0, 1]},
                       {"name": "shot",    "nodes": [0, 1]},
                       {"name": "flicker", "nodes": [0, 1]}], nz["noise"]
print("DESCRIBE noise sources: ok")

K_B, Q_E = 1.380649e-23, 1.602176634e-19
def noise(handle, volts, freqs):
    send({"cmd":"noise","handle":handle,"freqCount":len(freqs)},
         struct.pack("<%dd" % (len(volts) + len(freqs)), *volts, *freqs))
    return recv()

def close(a, b): return abs(a - b) <= 1e-12 * abs(b)

freqs = [1.0e3, 2.5e9]
g, kf = 2.0e-3, 1.0e-12
for T in (300.0, 400.0):
    send({"cmd":"create","typeId":"crf_noise","params":{"g":g,"kf":kf},"temperatureK":T})
    hn = recv()[0]["handle"]
    # A different bias evaluated first: noise must use the voltages it is given, not this one.
    send({"cmd":"eval","handle":hn,"count":1}, struct.pack("<2d", 3.0, 0.0)); recv()
    for va, vb in ((1.0, 0.0), (-0.3, 0.2)):
        j, dens = noise(hn, (va, vb), freqs)
        assert j == {"sourceCount": 3, "freqCount": 2}, j
        i = abs(g * (va - vb))
        for k, f in enumerate(freqs):
            th, sh, fl = dens[k*3:k*3+3]          # frequency-major: [k*S + s]
            assert close(th, 4*K_B*T*g) and close(sh, 2*Q_E*i) and close(fl, kf*i/f), (T, va, vb, f, dens)
        print(f"NOISE T={T} v={va-vb}: thermal={dens[0]:.6e} shot={dens[1]:.6e} flicker@1k={dens[2]:.6e}")
    send({"cmd":"destroy","handle":hn}); recv()

# Defaults (g = 1e-3, kf = 0): thermal and shot only, and eval is the plain conductance.
send({"cmd":"create","typeId":"crf_noise","params":{},"temperatureK":290.0}); hn = recv()[0]["handle"]
_, dens = noise(hn, (0.5, 0.0), [1e6])
assert close(dens[0], 4*K_B*290.0*1e-3) and close(dens[1], 2*Q_E*0.5e-3) and dens[2] == 0.0, dens
send({"cmd":"eval","handle":hn,"count":1}, struct.pack("<2d", 0.5, 0.0)); _, ev = recv()
assert ev[0] == 1.0 and close(ev[1], 0.5e-3) and close(ev[2], -0.5e-3), ev
# The payload must be exactly the node voltages followed by freqCount frequencies.
send({"cmd":"noise","handle":hn,"freqCount":2}, struct.pack("<3d", 0.5, 0.0, 1e6)); bad,_ = recv()
assert "error" in bad, bad
send({"cmd":"destroy","handle":hn}); recv()

# A type without noise answers zero sources, not an error.
send({"cmd":"create","typeId":"crf_rc","params":{"g0":0.001},"temperatureK":300.0}); hr = recv()[0]["handle"]
j, dens = noise(hr, (1.0, 0.0), freqs)
assert j == {"sourceCount": 0, "freqCount": 2} and dens == [], (j, dens)
send({"cmd":"destroy","handle":hr}); recv()
print("NOISE closed form, stated bias, zero-source type: ok")

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
