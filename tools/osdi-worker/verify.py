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
