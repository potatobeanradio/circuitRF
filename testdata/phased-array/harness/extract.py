# Write a fields bench for a record: one drive per port (embedded patterns) plus one multi-drive check (FW).
# Usage: extract.py <ws> <name> <nports> <fGHz> [weights as "re:im,..." for FW]
import sys, os
ws, name, n, f = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]
lines = [f"; PAA-1 embedded-pattern extraction for {name}"]
for k in range(1, n + 1):
    lines.append(f"Port:P{k} p{k} 0 Num={k} Z=50 Ohm")
lines.append(f"EmFieldData:EM1 {' '.join(f'p{k}' for k in range(1, n + 1))} File=\"../results/{name}.cemr\"")
for k in range(1, n + 1):
    lines.append(f"analysis F{k} type=fields block=EM1 freq={f} freqUnit=GHz drive={k}")
if len(sys.argv) > 5:
    w = sys.argv[5].split(",")
    for k, s in enumerate(w, 1):
        re_, im_ = s.split(":")
        lines.append(f"W{k} = {re_} + j*({im_})")
    lines.append(f"analysis FW type=fields block=EM1 freq={f} freqUnit=GHz drive={','.join(str(k) for k in range(1, n + 1))} weights={','.join(f'W{k}' for k in range(1, n + 1))}")
os.makedirs(os.path.join(ws, "bench"), exist_ok=True)
p = os.path.join(ws, "bench", f"{name}_fields.cnl")
open(p, "w").write("\n".join(lines) + "\n")
print(p)
