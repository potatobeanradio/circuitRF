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
