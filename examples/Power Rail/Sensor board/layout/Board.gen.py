#!/usr/bin/env python3
"""Regenerates the Power Rail example's board, its IPC-D-356 netlist and its placement table.

Kept as the source of the artwork because the three files MUST agree: a pad in the netlist that is
not under a land in the .clay is a part railRF cannot locate, and a land with no anti-pad under it
is a decoupling capacitor shorting the rail to its reference.  Hand-editing three files to stay in
step is how that goes wrong silently.
"""
import json, math, os, sys

MM = 1_000_000          # DBU per mm (DbuPerMicron = 1000)
def mm(v): return int(round(v * MM))

L_TOP, L_GND, L_IN3, L_BOT, L_VIA = 1, 2, 3, 4, 10

VIA_PAD, VIA_DRILL = 0.55, 0.30
ANTI = 0.415746          # anti-pad apothem, as the board's existing three are drawn

shapes = []
def rect(x1, y1, x2, y2, layer):
    shapes.append({"$type": "Rect", "X1": mm(x1), "Y1": mm(y1), "X2": mm(x2), "Y2": mm(y2),
                   "Layer": {"Layer": layer, "Datatype": 0}})

def via(x, y, landing=L_TOP):
    shapes.append({"$type": "Via", "X": mm(x), "Y": mm(y),
                   "PadSize": mm(VIA_PAD), "DrillSize": mm(VIA_DRILL),
                   "LandingLayer": {"Layer": landing, "Datatype": 0},
                   "Layer": {"Layer": L_VIA, "Datatype": 0}})

def octagon(cx, cy, a):
    """The eight-vertex anti-pad the board's existing three are drawn as."""
    t = a * math.tan(math.radians(22.5))
    pts = [(cx + a, cy + t), (cx + a, cy - t), (cx + t, cy - a), (cx - t, cy - a),
           (cx - a, cy - t), (cx - a, cy + t), (cx - t, cy + a), (cx + t, cy + a)]
    out = []
    for px, py in pts:
        out += [mm(px), mm(py)]
    return out

def poly(x1, y1, x2, y2, layer, holes):
    shapes.append({"$type": "Poly",
                   "Xy": [mm(x1), mm(y1), mm(x2), mm(y1), mm(x2), mm(y2), mm(x1), mm(y2)],
                   "Holes": holes,
                   "Layer": {"Layer": layer, "Datatype": 0}})

# ── what was already on this board ─────────────────────────────────────────────────────────────
# The regulator's output land, the two load lands, the supply's long way round on BOT, and the
# three transitions between them.  Unchanged: the DC story of this example is these shapes.
rect(2.0, 10.100, 7.0, 10.500, L_TOP)          # regulator output land
rect(24.0, 14.125, 28.0, 14.475, L_TOP)        # U1's land
rect(24.0, 4.825, 28.0, 5.175, L_TOP)          # U3's land

rect(6.9, 10.2, 7.1, 17.1, L_BOT)              # down the left side
rect(6.9, 16.9, 24.1, 17.1, L_BOT)             # across the top, 0.20 mm wide
rect(23.9, 4.9, 24.1, 17.1, L_BOT)             # down the right side

RAIL_VIAS = [(7.0, 10.3), (24.0, 14.3), (24.0, 5.0)]
for x, y in RAIL_VIAS:
    via(x, y, L_TOP)
    via(x, y, L_BOT)

# ── the parts ──────────────────────────────────────────────────────────────────────────────────
# Each row: refdes, part number, centre, half the land pitch, the fan-out stub, the land size.
# STUB is the whole of the lesson: a via in the land costs nothing to reach, and 0.9 mm of 0.5 mm
# trace to a shared via costs more than the barrel it reaches.
P402  = "CAP-0402-100N-X7R-16V"
P603  = "CAP-0603-1U0-X5R-10V"
P805  = "CAP-0805-10U-X5R-10V"
PBULK = "CAP-BULK-100U-POLY-10V"

#         ref   part    cx     cy    dx    stub  lw    lh
#
# THE GRID IS DELIBERATE AND ITS PITCH IS NOT COSMETIC.  An earlier draft of this field put C3's
# power land 0.15 mm from C7's return land: the two lands MERGED on TOP, which put the rail and its
# reference on one piece of copper, and each one's anti-pad then swallowed the other's barrel so
# neither reached the layer it was drilled for.  Nothing reported it — PdnRailRegions calls extra
# islands ORDINARY on imported artwork, so the board simply solved as a shorted one.  The audit at
# the foot of this file is what now refuses that, and the columns are 2.4 mm apart so it does not
# arise: an anti-pad is 0.83 mm across, so two of them plus a barrel need 1.0 mm between vias.
#
# STUB is the whole of §4.3's lesson, in three tiers: a via in the land (C1-C3, C7-C10), a short
# fan-out (C4-C6), and 0.9 mm of 0.125 mm trace out to a via of its own (C11-C13).
P402  = "CAP-0402-100N-X7R-16V"
P603  = "CAP-0603-1U0-X5R-10V"
P805  = "CAP-0805-10U-X5R-10V"
PBULK = "CAP-BULK-100U-POLY-10V"

PARTS = [("C1",  P402, 25.40, 16.00, 0.55, 0.00, 0.50, 0.55),
         ("C2",  P402, 28.40, 16.00, 0.55, 0.00, 0.50, 0.55),
         ("C3",  P402, 28.40, 12.60, 0.55, 0.00, 0.50, 0.55),
         ("C4",  P402, 28.40,  7.00, 0.55, 0.35, 0.50, 0.55),
         ("C5",  P402, 28.40,  3.20, 0.55, 0.35, 0.50, 0.55),
         ("C6",  P402, 25.40,  3.20, 0.55, 0.35, 0.50, 0.55),
         ("C7",  P603, 25.40, 12.60, 0.70, 0.00, 0.60, 0.80),
         ("C8",  P603, 25.40,  7.00, 0.70, 0.00, 0.60, 0.80),
         ("C9",  P805, 22.40, 16.00, 0.85, 0.00, 0.70, 1.20),
         ("C10", PBULK, 3.40,  8.90, 1.70, 0.00, 1.60, 2.20),
         ("C11", P402, 20.40, 12.60, 0.55, 0.90, 0.50, 0.55),
         ("C12", P402, 20.40,  9.60, 0.55, 0.90, 0.50, 0.55),
         ("C13", P402, 20.40,  6.60, 0.55, 0.90, 0.50, 0.55)]

pads, power_vias, return_vias = [], [], []
lands = []
for ref, pn, cx, cy, dx, stub, lw, lh in PARTS:
    px, rx = cx - dx, cx + dx                      # pin 1 on the rail, pin 2 on the reference
    rect(px - lw / 2, cy - lh / 2, px + lw / 2, cy + lh / 2, L_TOP)
    rect(rx - lw / 2, cy - lh / 2, rx + lw / 2, cy + lh / 2, L_TOP)
    lands.append((ref, "1", px - lw / 2, cy - lh / 2, px + lw / 2, cy + lh / 2))
    lands.append((ref, "2", rx - lw / 2, cy - lh / 2, rx + lw / 2, cy + lh / 2))

    pvx, rvx = px - stub, rx + stub
    if stub > 0:                                    # the fan-out trace out to a via of its own
        rect(pvx, cy - lw / 4, px, cy + lw / 4, L_TOP)
        rect(rx, cy - lw / 4, rvx, cy + lw / 4, L_TOP)

    via(pvx, cy, L_TOP)
    via(rvx, cy, L_TOP)
    power_vias.append((pvx, cy))
    return_vias.append((rvx, cy))
    pads.append((ref, "1", "+3V3", px, cy))
    pads.append((ref, "2", "GND",  rx, cy))

# ── the rail's plane, on IN3 ───────────────────────────────────────────────────────────────────
# TWO islands, and neither bridges the board.  A single pour spanning both ends would put a second
# path in parallel with the 0.20 mm BOT run and delete the finding this example exists for; each of
# these hangs off the rail at ONE place and carries no through current.
LOAD_POUR = (18.60, 1.60, 29.40, 17.60)
REG_POUR  = (1.60, 7.80, 5.60, 11.40)

via(4.5, 10.3, L_TOP)                               # what ties the regulator island to the rail
power_vias.append((4.5, 10.3))

def inside(box, x, y):
    return box[0] <= x <= box[2] and box[1] <= y <= box[3]

for box in (LOAD_POUR, REG_POUR):
    holes = [octagon(x, y, ANTI) for x, y in return_vias if inside(box, x, y)]
    poly(box[0], box[1], box[2], box[3], L_IN3, holes)

# ── the reference, on GND ──────────────────────────────────────────────────────────────────────
# One hole per barrel on the rail — the three original transitions, every capacitor's power via, and
# the regulator island's.  A missing one is a short, and it is invisible on the picture.
gnd_holes = [octagon(x, y, ANTI) for x, y in RAIL_VIAS + power_vias]
poly(0, 0, 30, 20, L_GND, gnd_holes)

# ── THE AUDIT ──────────────────────────────────────────────────────────────────────────────────
# Every check here is one this board has already failed.  The artwork is the input to a DC solve
# that reports a number either way, so a barrel reaching the wrong layer does not announce itself.
def barrel_reaches(layer, x, y, r=VIA_DRILL / 2):
    pts = [(x, y)] + [(x + r * math.cos(i * math.pi / 8), y + r * math.sin(i * math.pi / 8))
                      for i in range(16)]
    for px, py in pts:
        for s in shapes:
            if s["Layer"]["Layer"] != layer:
                continue
            if s["$type"] == "Rect":
                if s["X1"] <= mm(px) <= s["X2"] and s["Y1"] <= mm(py) <= s["Y2"]:
                    return True
            elif s["$type"] == "Poly":
                ring = [(s["Xy"][i], s["Xy"][i + 1]) for i in range(0, len(s["Xy"]), 2)]
                holes = [[(h[i], h[i + 1]) for i in range(0, len(h), 2)] for h in s["Holes"]]
                if _pip(mm(px), mm(py), ring) and not any(_pip(mm(px), mm(py), h) for h in holes):
                    return True
    return False

def _pip(x, y, ring):
    n, inside_, j = len(ring), False, len(ring) - 1
    for i in range(n):
        xi, yi = ring[i]; xj, yj = ring[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside_ = not inside_
        j = i
    return inside_

problems = []
for x, y in power_vias:
    if barrel_reaches(L_GND, x, y):
        problems.append(f"power via at ({x}, {y}) reaches GND: its anti-pad is missing or cut away")
    if not barrel_reaches(L_IN3, x, y):
        problems.append(f"power via at ({x}, {y}) reaches no rail plane on IN3")
for x, y in return_vias:
    if not barrel_reaches(L_GND, x, y):
        problems.append(f"return via at ({x}, {y}) reaches no reference copper")
    if barrel_reaches(L_IN3, x, y) or barrel_reaches(L_BOT, x, y):
        problems.append(f"return via at ({x}, {y}) reaches the rail: it shorts the supply")

# Two lands of different parts that touch are one piece of copper, whatever the netlist says.
for i in range(len(lands)):
    for j in range(i + 1, len(lands)):
        a, b = lands[i], lands[j]
        if a[0] == b[0]:
            continue
        if a[2] < b[4] and b[2] < a[4] and a[3] < b[5] and b[3] < a[5]:
            problems.append(f"{a[0]}.{a[1]} and {b[0]}.{b[1]} overlap on TOP")

# Two barrels closer than two anti-pads and a barrel: each one's clearance eats the other's landing.
allv = [(x, y) for x, y in power_vias + return_vias] + RAIL_VIAS
for i in range(len(allv)):
    for j in range(i + 1, len(allv)):
        d = math.hypot(allv[i][0] - allv[j][0], allv[i][1] - allv[j][1])
        if 0 < d < 2 * ANTI + VIA_DRILL / 2:
            problems.append(f"vias at {allv[i]} and {allv[j]} are {d:.3f} mm apart")

if problems:
    for p in problems:
        print("AUDIT: " + p)
    raise SystemExit("the artwork is not sound; nothing was written")

clay = {"FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 0,
        "AngleMode": "AnyAngle", "TechRef": "../../tech/pcb-4layer-1p6mm.ctech",
        "Shapes": shapes, "Instances": []}

root = sys.argv[1]
with open(os.path.join(root, "Sensor board/layout/Board.clay"), "w") as f:
    json.dump(clay, f, indent=2)
    f.write("\n")

# ── the board netlist ──────────────────────────────────────────────────────────────────────────
# IPC-D-356A, metric, one count = 0.001 mm.  Columns: the three-digit code, a fourteen-wide net
# field from column 3, the reference-and-pin span from column 19, and the letter-tagged tail from
# column 30.
def record(net, ref, pin, x, y, w, h, access=1):
    head = "317" + net.ljust(14) + "  " + f"{ref}-{pin}".ljust(11)
    tail = ("A%02d" % access
            + "X%07d" % int(round(x * 1000))
            + "Y%07d" % int(round(y * 1000))
            + "X%06d" % int(round(w * 1000))
            + "Y%06d" % int(round(h * 1000)))
    return head + tail

lines = ["C  Sensor board - power rail integrity example",
         "C  Written by hand for the circuitRF Power Rail example; no board tool produced it.",
         "P  JOB SENSOR BOARD",
         "P  UNITS CUST 1",
         "P  DIM MM"]

sizes = {ref: (lw, lh) for ref, _, _, _, _, _, lw, lh in PARTS}
for ref, pin, net, x, y in pads:
    lw, lh = sizes[ref]
    lines.append(record(net, ref, pin, x, y, lw, lh))

# The regulator and the two loads, so their anchors can be a REFDES rather than a coordinate.
for ref, pin, net, x, y, w, h in [("U2", "OUT", "+3V3", 2.20, 10.30, 0.40, 0.40),
                                  ("U1", "VDD", "+3V3", 27.80, 14.30, 0.40, 0.40),
                                  ("U3", "VDD", "+3V3", 27.80,  5.00, 0.40, 0.40)]:
    lines.append(record(net, ref, pin, x, y, w, h))

lines.append("999")
with open(os.path.join(root, "Sensor board/layout/Board.ipc"), "w") as f:
    f.write("\n".join(lines) + "\n")

# ── the placement table ────────────────────────────────────────────────────────────────────────
place = ["# Sensor board placement",
         "# Units: mm",
         "# Origin: body centre",
         "Refdes,X,Y,Rotation,Side"]
for ref, _, cx, cy, _, _, _, _ in PARTS:
    place.append(f"{ref},{cx:.3f},{cy:.3f},0,top")
for ref, cx, cy in [("U2", 4.50, 10.30), ("U1", 26.00, 14.30), ("U3", 26.00, 5.00)]:
    place.append(f"{ref},{cx:.3f},{cy:.3f},0,top")
with open(os.path.join(root, "Sensor board/layout/Board.placement.csv"), "w") as f:
    f.write("\n".join(place) + "\n")

print(f"{len(shapes)} shapes, {len(pads)} capacitor pads, {len(gnd_holes)} anti-pads on GND")
