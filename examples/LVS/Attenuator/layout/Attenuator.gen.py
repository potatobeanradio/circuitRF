#!/usr/bin/env python3
"""Regenerates the correct board of the LVS example.

    python3 "examples/LVS/Attenuator/layout/Attenuator.gen.py" examples/LVS

IT WRITES THE ARTWORK AND NOTHING ELSE, on `Power Rail/Sensor board/layout/Board.gen.py`'s
precedent (brief-lvs-5-proving-designs.md R-lvs5-1e).  Hand-drawn artwork in a fixture is artwork
nobody can regenerate after a format change, and a fixture nobody can regenerate is one that
quietly stops meaning what it meant.  The schematic, the `.ccell` terminal maps and the part cells
are authored files and are NOT written here.

THE LANDS ARE NOT DRAWN HERE either.  Every part is an INSTANCE of a cell under
`examples/LVS/footprints/`, and this script READS those cells for their pad rectangles rather than
computing a land pattern of its own — so the geometry the audit at the foot of this file measures
is the same geometry the board draws.

WHAT THE BOARD IS.  A 3 dB resistive pi attenuator: R2 in series between the two ports, R1 and R3
shunting each port to ground, and C1 across R2 — a part the topology does not need, which is the
point of it.  R1 and R3 are the same part with the same value in the same orientation, so swapping
them is an AUTOMORPHISM of the netlist and the comparison has to resolve it rather than reading the
answer off the designators (R-lvs5-1c).

WHAT THE GROUND IS, AND WHY IT IS DRAWN.  `Bottom Copper` is the stackup's ground reference AND it
draws, so nothing here relies on undrawn metal — the partition says what touches what and the
reference is ordinary copper.  That is deliberate: it is the half of brief 3's ground handling a
board exercises, and `Bias tee/` is the other half.  It is also what makes F5 a real open: split
the ground and you get two islands, because a net is what the copper says it is.
"""
import json, os, sys

MM = 1_000_000                      # DBU per mm, at DbuPerMicron = 1000
def mm(v): return int(round(v * MM))

L_TOP, L_BOT, L_SILK, L_OUTLINE, L_VIA = 1, 2, 3, 4, 5

VIA_PAD, VIA_DRILL = 0.60, 0.30
BOARD_W, BOARD_H = 20.0, 12.0

root = sys.argv[1] if len(sys.argv) > 1 else "."
FOOTPRINTS = os.path.join(root, "footprints")


# ── the part cells ─────────────────────────────────────────────────────────────────────────────
# What is read out of each is its two COPPER pads, as rectangles in the cell's own frame.

def read_cell(cell):
    path = os.path.join(FOOTPRINTS, cell, "layout", cell + ".clay")
    view = json.load(open(path))
    pads = {}
    for s in view["Shapes"]:
        if s["Layer"]["Layer"] == L_TOP and s.get("Pin") in ("1", "2"):
            pads[s["Pin"]] = (s["X1"] / MM, s["Y1"] / MM, s["X2"] / MM, s["Y2"] / MM)
    if set(pads) != {"1", "2"}:
        raise SystemExit(f"{path} does not hold two copper pads named 1 and 2")
    return pads


#        refdes  cell            x      y    rot
PARTS = [("R2",  "R0402-17R4",  10.00, 6.00,   0),   # series
         ("R1",  "R0402-294R",   6.00, 7.10,  90),   # shunt, port 1
         ("R3",  "R0402-294R",  14.00, 7.10,  90),   # shunt, port 2
         ("C1",  "C0402-1P0",    7.00, 4.70,   0)]   # across R2

CELLS = {cell: read_cell(cell) for _, cell, _, _, _ in PARTS}

shapes, instances, lands = [], [], []


def placed(cell, cx, cy, rot, pin):
    """One pad of a placed instance, as a board-frame rectangle."""
    x1, y1, x2, y2 = CELLS[cell][pin]
    if rot == 90:                                  # (x, y) -> (-y, x)
        x1, y1, x2, y2 = -y2, x1, -y1, x2
    elif rot != 0:
        raise SystemExit(f"rotation {rot} is not one this generator places")
    return (cx + x1, cy + y1, cx + x2, cy + y2)


def rect(x1, y1, x2, y2, layer, net=None, what=""):
    s = {"$type": "Rect", "X1": mm(x1), "Y1": mm(y1), "X2": mm(x2), "Y2": mm(y2),
         "Layer": {"Layer": layer, "Datatype": 0}}
    if net:
        s["Net"] = net
    shapes.append(s)
    if layer in (L_TOP, L_BOT):
        lands.append((what or "copper", layer, x1, y1, x2, y2))


def via(x, y):
    shapes.append({"$type": "Via", "X": mm(x), "Y": mm(y),
                   "PadSize": mm(VIA_PAD), "DrillSize": mm(VIA_DRILL),
                   "LandingLayer": {"Layer": L_TOP, "Datatype": 0},
                   "Layer": {"Layer": L_VIA, "Datatype": 0}})


def instance(ref, cell, cx, cy, rot):
    """A placed part.  SchematicId — not RefDes — is what Update Layout writes, and it is what
    makes this the easy path: every instance here corresponds to a schematic component by name
    (R-lvs5-1d).  Only the PLACEMENT is stored; the designator's offset is left AUTO."""
    instances.append({"CellRef": "../../footprints/" + cell,
                      "X": mm(cx), "Y": mm(cy),
                      "Rot": {0: "R0", 90: "R90", 180: "R180", 270: "R270"}[rot],
                      "MirrorX": False, "Mag": 1.0,
                      "Rows": 1, "Cols": 1, "PitchX": 0, "PitchY": 0,
                      "SchematicId": ref})
    for pin in ("1", "2"):
        x1, y1, x2, y2 = placed(cell, cx, cy, rot, pin)
        lands.append((f"{ref}.{pin}", L_TOP, x1, y1, x2, y2))


# ── the board outline ──────────────────────────────────────────────────────────────────────────
shapes.append({"$type": "Path",
               "Xy": [mm(0), mm(0), mm(BOARD_W), mm(0), mm(BOARD_W), mm(BOARD_H),
                      mm(0), mm(BOARD_H), mm(0), mm(0)],
               "Width": mm(0.15), "End": "Flush",
               "Layer": {"Layer": L_OUTLINE, "Datatype": 0}})

# ── the parts ──────────────────────────────────────────────────────────────────────────────────
for ref, cell, cx, cy, rot in PARTS:
    instance(ref, cell, cx, cy, rot)

# ── the signal path ────────────────────────────────────────────────────────────────────────────
# NO NET NAME IS STAMPED ON THE SIGNAL COPPER, AND THAT IS NOT AN OVERSIGHT.  `DrcRegions.
# BuildConductors` makes every shape carrying a `Net` a conductor OF ITS OWN, keyed on the name,
# while the rest of the layer is grouped into connected components — so a named trace that touches
# an unnamed one (a footprint's pad, which lives in a shared cell and can never carry a board net)
# overlaps a second conductor, and `MinSpacing` reports the overlap as a spacing violation of zero
# on artwork that is perfectly correct.  `examples/Power Rail` misses it only because its
# technology declares no spacing rule at all.  Recorded in `examples/RESOLVED.md`; until it is
# fixed, a name on this copper would make the fixture fail its own `check`.
#
# Nothing is lost electrically: a net is what the copper says it is, and the ground pour below —
# the one piece of metal on its layer, so there is nothing for it to overlap — carries the only
# name this board states.
rect(0.40, 5.75,  9.10, 6.25, L_TOP, None, "sig_left")
rect(9.00, 5.875, 9.40, 6.125, L_TOP, None, "R2.1 tie")

# R2's far pad reaches the output run through a link of its own rather than by sitting on it.  That
# is what makes F1 a ONE-SHAPE mutation: re-point the link and R2's terminal 2 is on the wrong net,
# with nothing else on the board touched.
rect(10.60, 5.875, 12.10, 6.125, L_TOP, None, "R2.2 link")
rect(12.00, 5.75, 19.60, 6.25, L_TOP, None, "sig_right")

# ── the shunts, up to the top ground ───────────────────────────────────────────────────────────
for x in (6.00, 14.00):
    rect(x - 0.125, 6.20, x + 0.125, 6.50, L_TOP, None, "shunt tap")
    rect(x - 0.125, 7.70, x + 0.125, 8.45, L_TOP, None, "shunt return")

# ── C1, across R2, routed below ────────────────────────────────────────────────────────────────
rect(6.475, 4.90, 6.725, 5.80, L_TOP, None, "C1.1 riser")
rect(7.550, 4.45, 12.600, 4.70, L_TOP, None, "C1.2 run")
rect(12.350, 4.60, 12.600, 5.80, L_TOP, None, "C1.2 riser")

# ── the ground ─────────────────────────────────────────────────────────────────────────────────
# THREE pieces on top and one pour below, stitched by four barrels.  The top ground is split either
# side of the series part, which is ordinary, and it is also what gives F5 something to break: the
# upper-left piece hangs off ONE via.
rect(0.60, 1.00, 19.40, 3.60, L_TOP, None, "gnd_low")
rect(0.60, 8.40,  9.60, 11.00, L_TOP, None, "gnd_upper_left")
rect(10.40, 8.40, 19.40, 11.00, L_TOP, None, "gnd_upper_right")
rect(0.20, 0.20, 19.80, 11.80, L_BOT, "0", "gnd_pour")

STITCH = [(2.00, 2.30), (18.00, 2.30), (2.00, 9.70), (18.00, 9.70)]
for x, y in STITCH:
    via(x, y)


# ── THE AUDIT ──────────────────────────────────────────────────────────────────────────────────
# An independent partition of the rectangles above — union-find over overlap, plus the four
# barrels — compared against the netlist this board is SUPPOSED to be.  It is not a second copy of
# circuitRF's extractor and it is not meant to be: it is an arithmetic statement about the
# geometry, written where the geometry is, so a coordinate typed wrong refuses here instead of
# producing a plausible board that quietly compares against the wrong thing.

EXPECTED = {
    "IN":  {"R2.1", "R1.1", "C1.1"},
    "OUT": {"R2.2", "R3.1", "C1.2"},
    "0":   {"R1.2", "R3.2"},
}

parent = {}
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]
        a = parent[a]
    return a
def union(a, b):
    ra, rb = find(a), find(b)
    if ra != rb: parent[ra] = rb

for i, _ in enumerate(lands):
    parent[i] = i

def overlaps(a, b):
    _, la, ax1, ay1, ax2, ay2 = a
    _, lb, bx1, by1, bx2, by2 = b
    return la == lb and ax1 <= bx2 and bx1 <= ax2 and ay1 <= by2 and by1 <= ay2

for i in range(len(lands)):
    for j in range(i + 1, len(lands)):
        if overlaps(lands[i], lands[j]):
            union(i, j)

def covering(x, y, layer):
    return [i for i, (_, l, x1, y1, x2, y2) in enumerate(lands)
            if l == layer and x1 <= x <= x2 and y1 <= y <= y2]

for x, y in STITCH:
    touched = covering(x, y, L_TOP) + covering(x, y, L_BOT)
    if len(touched) < 2:
        raise SystemExit(f"the barrel at ({x}, {y}) reaches fewer than two conductors")
    for t in touched[1:]:
        union(touched[0], t)

groups = {}
for i, (name, *_ ) in enumerate(lands):
    groups.setdefault(find(i), set()).add(name)

problems = []

# Every named pad is where the netlist says it is, and no two nets share a piece of copper.
by_pad = {}
for members in groups.values():
    for name in members:
        if "." in name and name[0] in "RC":
            by_pad[name] = members

for net, pads in EXPECTED.items():
    seen = [by_pad.get(p) for p in sorted(pads)]
    if any(s is None for s in seen):
        problems.append(f"net {net}: a pad of {sorted(pads)} is on no copper at all")
        continue
    if any(s is not seen[0] for s in seen):
        problems.append(f"net {net} is not one piece of copper: {sorted(pads)} are on "
                        + " / ".join(sorted(str(sorted(p & set(by_pad))) for p in seen)))

for a in EXPECTED:
    for b in EXPECTED:
        if a >= b: continue
        pa, pb = next(iter(EXPECTED[a])), next(iter(EXPECTED[b]))
        if by_pad.get(pa) is not None and by_pad.get(pa) is by_pad.get(pb):
            problems.append(f"nets {a} and {b} are the same piece of copper — the board is shorted")

# The two boundary pins stand on the runs they are meant to name.
for pin_name, probe, net in (("IN", (0.50, 6.00), "IN"), ("OUT", (19.50, 6.00), "OUT")):
    hit = covering(probe[0], probe[1], L_TOP)
    if not hit:
        problems.append(f"the '{pin_name}' pin at {probe} stands on no copper")
    elif find(hit[0]) != find(next(i for i, (n, *_) in enumerate(lands)
                                   if n == sorted(EXPECTED[net])[0])):
        problems.append(f"the '{pin_name}' pin at {probe} is not on the {net} net")

# Design rules the technology states, checked where the artwork is written: two pieces of copper on
# one layer that are NOT the same net must clear each other by the stackup's own minimum.
MIN_CLEAR = 0.15
def gap(a, b):
    _, _, ax1, ay1, ax2, ay2 = a
    _, _, bx1, by1, bx2, by2 = b
    dx = max(bx1 - ax2, ax1 - bx2, 0.0)
    dy = max(by1 - ay2, ay1 - by2, 0.0)
    return (dx * dx + dy * dy) ** 0.5

for i in range(len(lands)):
    for j in range(i + 1, len(lands)):
        if lands[i][1] != lands[j][1] or find(i) == find(j):
            continue
        d = gap(lands[i], lands[j])
        if d < MIN_CLEAR:
            problems.append(f"{lands[i][0]} and {lands[j][0]} are {d:.3f} mm apart on layer "
                            f"{lands[i][1]} and are different nets: the rule is {MIN_CLEAR} mm")

if problems:
    for p in problems:
        print("AUDIT: " + p)
    raise SystemExit("the artwork is not sound; nothing was written")

clay = {"FormatVersion": 1, "DbuPerMicron": 1000, "DisplayUnit": "Um", "SnapDbu": 25000,
        "AngleMode": "AnyAngle", "TechRef": "../../tech/pcb-2layer-lvs.ctech",
        "Pins": [
            {"Name": "IN",  "X": mm(0.50),  "Y": mm(6.00), "WidthDbu": mm(0.50),
             "OutwardDeg": 180, "Layer": {"Layer": L_TOP, "Datatype": 0}},
            {"Name": "OUT", "X": mm(19.50), "Y": mm(6.00), "WidthDbu": mm(0.50),
             "OutwardDeg": 0,   "Layer": {"Layer": L_TOP, "Datatype": 0}},
        ],
        "Shapes": shapes, "Instances": instances}

out = os.path.join(root, "Attenuator", "layout", "Attenuator.clay")
with open(out, "w") as f:
    json.dump(clay, f, indent=2)
    f.write("\n")

print(f"{len(shapes)} shapes, {len(instances)} instances, "
      f"{len(set(find(i) for i in range(len(lands))))} nets, {len(STITCH)} stitching vias")
