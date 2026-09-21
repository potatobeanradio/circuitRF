#!/usr/bin/env python3
"""Regenerates the MMIC cell of the LVS example, and its four part cells.

    python3 "examples/LVS/Bias tee/layout/Bias tee.gen.py" examples/LVS

WHAT ONLY THIS CELL CAN EXERCISE (brief-lvs-5-proving-designs.md R-lvs5-3):

  * THE UNDRAWN GROUND REFERENCE.  `Backside Metal` has `DrawingLayers: []`, so the two backside
    vias here terminate on metal nobody drew and the run has to reach net "0" through the STACKUP
    rather than through the artwork.  On a board that path is never taken.
  * PARAMETERS ON THE LAYOUT SIDE.  Each part cell carries a `PCellOrigin` — the generator, the
    resolved SI parameters it drew from, and which of them the generator DERIVED rather than read.
    A land pattern claims nothing about resistance, so the whole property comparison is unexercised
    without this cell.
  * A MULTI-LAYER DEVICE.  The MIM capacitor's two plates are on different conductors, joined by
    neither a via nor a touch, and its two terminals come out on different layers — Metal1 at one
    end and Metal2 at the other.  A layer-blind pin lookup passes every board test and fails here.

NO BROKEN VARIANT IS SHIPPED FOR THIS CELL and that asymmetry is deliberate (R-lvs5-3e): its
faults are produced in-test by mutating the loaded model, because an MMIC fixture pair would
double the artwork for faults the board already covers structurally.

THE TECHNOLOGY IS THE SHIPPED ONE, BYTE FOR BYTE.  `tech/mmic-GaAs_2LM_100um.ctech` is a copy of
`src/Design/resources/technologies/mmic-GaAs_2LM_100um.ctech` with nothing changed and the same
name — partly to prove the starter technology works, and a renamed copy would invite the question
of whether it had been edited.  Every number below that is not a coordinate is READ OUT OF IT.

WHAT "GENERATED" MEANS HERE.  The part cells are ordinary committed cell folders whose primary
layout carries a `PCellOrigin`; they are not produced by a Python kit at open time.  That is not a
simplification of the fixture, it is what makes it an ORACLE: a kit's generated cell lands under
`.generated-cells/` with no symbol view, so it has no terminal map at all, and a comparison built
on one would be measuring the absence of brief 1's PCell writer rather than the comparison.  The
generator here is this file, its id is recorded in each cell, and the geometry is reproducible
byte for byte from the requested values at the top of it.
"""
import json, math, os, sys

UM = 1000                      # DBU per micron
def um(v): return int(round(v * UM))

L_M1, L_M2, L_POST, L_RES, L_MIM, L_BSV, L_MIMVIA = 1, 2, 3, 4, 9, 8, 10

root = sys.argv[1] if len(sys.argv) > 1 else "."
TECH = os.path.join(root, "tech", "mmic-GaAs_2LM_100um.ctech")
tech = json.load(open(TECH))
stack = {l["Name"]: l for l in tech["Stackup"]["Layers"]}

EPS0 = 8.8541878128e-12
MIM_T   = stack["MIM Dielectric"]["ThicknessDbu"] / UM * 1e-6      # m
MIM_EPS = stack["MIM Dielectric"]["Epsr"]
CAP_PER_AREA = EPS0 * MIM_EPS / MIM_T                              # F/m^2

# The drawing grid every generator here snaps its geometry to.  It is the reason a PCell's
# DERIVED value is not exactly the value that was asked for, and measuring that difference is
# what settles the property tolerances (R-lvs5-4a).
GRID = 0.25                                                        # um
def snap(v): return round(v / GRID) * GRID

# Sheet resistance of the thin-film resistor.  It is a GENERATOR parameter and not a stackup one:
# the technology declares a `Resistor` drawing layer and says nothing about ohms per square, so a
# value read off the stackup here would be invented.
RSHEET = 50.0                                                      # ohm/square

# ── WHAT THE DESIGNER ASKED FOR ────────────────────────────────────────────────────────────────
# These are the schematic's own numbers, and `Bias tee/schematic/Bias tee.csch` states the same
# three.  Everything below is the geometry that comes closest to them on the grid.
WANT_C1 = 0.80e-12      # F   series DC block
WANT_C2 = 4.00e-12      # F   bias-node decoupling
WANT_L1 = 1.20e-9       # H   bias feed
WANT_R1 = 62.0          # ohm de-Q of the feed

report = []             # (part, dimension, requested, resolved) for the tolerance table


# ── the part cells ─────────────────────────────────────────────────────────────────────────────

def write_cell(name, shapes, pins, origin):
    d = os.path.join(root, "parts", name)
    os.makedirs(os.path.join(d, "layout"), exist_ok=True)
    # EACH PART CELL NAMES ITS OWN TECHNOLOGY.  Without it the cell resolves against the
    # WORKSPACE default, which here is the board process — so a 10 um spiral track is measured
    # against a 0.15 mm copper rule and every part refuses, while the cell that places them is
    # told its sub-cells are "drawn against a different technology".  Both messages are correct
    # and neither names the cause.
    clay = {"FormatVersion": 1, "DbuPerMicron": UM, "DisplayUnit": "Um", "SnapDbu": 5,
            "AngleMode": "AnyAngle",
            "TechRef": "../../../tech/mmic-GaAs_2LM_100um.ctech",
            "PCellOrigin": origin,
            "Pins": pins, "Shapes": shapes, "Instances": []}
    with open(os.path.join(d, "layout", name + ".clay"), "w") as f:
        json.dump(clay, f, indent=2); f.write("\n")
    ccell = {"FormatVersion": 1, "Parameters": [], "IsTestBench": False,
             "Terminals": [{"Port": 1, "Name": "1", "LayoutPin": "1"},
                           {"Port": 2, "Name": "2", "LayoutPin": "2"}],
             "NumPorts": 2}
    with open(os.path.join(d, ".ccell"), "w") as f:
        json.dump(ccell, f, indent=2); f.write("\n")


def rect(x1, y1, x2, y2, layer, pin=None):
    s = {"$type": "Rect", "X1": um(x1), "Y1": um(y1), "X2": um(x2), "Y2": um(y2),
         "Layer": {"Layer": layer, "Datatype": 0}}
    if pin: s["Pin"] = pin
    return s


def pin(name, x, y, w, outward, layer):
    return {"Name": name, "X": um(x), "Y": um(y), "WidthDbu": um(w),
            "OutwardDeg": outward, "Layer": {"Layer": layer, "Datatype": 0}}


def mimcap(name, want_c):
    """A MIM capacitor: a Metal1 bottom plate, a MIM Metal top plate over it, and the top plate
    brought out on Metal2 through a MIM via.  The two plates are joined by neither a via nor a
    touch — the dielectric between them is the device — so terminal 1 is on Metal1 and terminal 2
    is on Metal2, which is the case a layer-blind pin lookup gets wrong."""
    side = snap(math.sqrt(want_c / CAP_PER_AREA) * 1e6)            # um
    got  = CAP_PER_AREA * (side * 1e-6) ** 2
    h    = side / 2
    lead, half_w = 20.0, 5.0

    shapes = [
        rect(-h, -h, h, h, L_M1, "1"),                             # bottom plate
        rect(-h - lead, -half_w, -h, half_w, L_M1, "1"),           # its lead out
        rect(-h, -h, h, h, L_MIM),                                 # top plate
        rect(h - 16, -half_w - 2.5, h - 4, half_w + 2.5, L_MIMVIA), # up to Metal2
        rect(h - 20, -half_w - 5, h + lead, half_w + 5, L_M2, "2"), # the Metal2 lead
    ]
    pins = [pin("1", -h - lead + 5, 0, 2 * half_w, 180, L_M1),
            pin("2",  h + lead - 5, 0, 2 * half_w + 10, 0, L_M2)]
    write_cell(name, shapes, pins, {
        "GeneratorId": "LVSKIT_MIMCAP",
        "Parameters": {"W": side * 1e-6, "L": side * 1e-6, "C": got},
        "ComputedParameters": ["C"],
    })
    report.append((name, "Capacitance", want_c, got))
    return h + lead                                                # half-extent in x


def spiral_points(a, p, rings):
    """A square spiral, centre-line, from the inner tap outward.  Each group of four vertices is
    one turn and successive turns clear each other by exactly `p`."""
    pts = [(a, 0.0)]
    for j in range(rings):
        pts.append(( a + j * p,  a + j * p))
        pts.append((-a - j * p,  a + j * p))
        pts.append((-a - j * p, -a - j * p))
        pts.append(( a + (j + 1) * p, -a - j * p))
    return pts


def spiral_L(a, p, w, rings):
    """Modified Wheeler for a square planar spiral — the published closed form, not a fit of
    ours: L = K1 mu0 n^2 d_avg / (1 + K2 rho)."""
    n = rings
    d_in  = 2 * a - w
    d_out = 2 * (a + (rings - 1) * p) + w
    d_avg = (d_out + d_in) / 2 * 1e-6
    rho   = (d_out - d_in) / (d_out + d_in)
    return 2.34 * (4e-7 * math.pi) * n * n * d_avg / (1 + 2.75 * rho)


def spiral(name, want_l, w=10.0, s=6.0, rings=3):
    p = w + s
    lo, hi = 10.0, 200.0
    for _ in range(80):
        mid = (lo + hi) / 2
        if spiral_L(mid, p, w, rings) < want_l: lo = mid
        else: hi = mid
    a = snap((lo + hi) / 2)
    got = spiral_L(a, p, w, rings)

    pts = spiral_points(a, p, rings)
    xy = []
    for x, y in pts: xy += [um(x), um(y)]

    out_x, out_y = pts[-1]                                         # the outer end, on the right
    lead_x = a + rings * p + 10
    reach  = a + rings * p + 25

    shapes = [
        {"$type": "Path", "Xy": xy, "Width": um(w), "End": "Flush",
         "Layer": {"Layer": L_M1, "Datatype": 0}},
        # the outer end, out to the right edge and up to the cell's axis
        rect(out_x, out_y - w / 2, lead_x + w / 2, out_y + w / 2, L_M1, "2"),
        rect(lead_x - w / 2, out_y - w / 2, lead_x + w / 2, w / 2, L_M1, "2"),
        rect(lead_x - w / 2, -w / 2, reach + 5, w / 2, L_M1, "2"),
        # the inner tap, down onto Metal2 and out under the winding to the left edge
        rect(a - w / 2, -w / 2, a + w / 2, w / 2, L_POST),
        rect(-lead_x - w / 2, -w / 2 - 2, a + w / 2, w / 2 + 2, L_M2),
        rect(-lead_x - w / 2, -w / 2, -lead_x + w / 2, w / 2, L_POST),
        rect(-reach - 5, -w / 2, -lead_x + w / 2, w / 2, L_M1, "1"),
    ]
    pins = [pin("1", -reach, 0, w, 180, L_M1),
            pin("2",  reach, 0, w, 0, L_M1)]
    write_cell(name, shapes, pins, {
        "GeneratorId": "LVSKIT_SPIRAL",
        "Parameters": {"Width": w * 1e-6, "Space": s * 1e-6, "Inner": (2 * a - w) * 1e-6,
                       "Turns": rings, "L": got},
        "ComputedParameters": ["L"],
    })
    report.append((name, "Inductance", want_l, got))
    return reach


def tfr(name, want_r, w=20.0):
    length = snap(want_r / RSHEET * w)
    got    = RSHEET * length / w
    h, hw, head = length / 2, w / 2, 15.0
    shapes = [
        rect(-h - head, -hw, -h, hw, L_M1, "1"),
        rect( h,        -hw,  h + head, hw, L_M1, "2"),
        rect(-h - 2,    -hw,  h + 2,    hw, L_RES),
    ]
    pins = [pin("1", -h - head + 5, 0, w, 180, L_M1),
            pin("2",  h + head - 5, 0, w, 0, L_M1)]
    write_cell(name, shapes, pins, {
        "GeneratorId": "LVSKIT_TFR",
        "Parameters": {"W": w * 1e-6, "L": length * 1e-6, "Rsheet": RSHEET, "R": got},
        "ComputedParameters": ["R"],
    })
    report.append((name, "Resistance", want_r, got))
    return h + head


C1_HALF = mimcap("MIM-0P8P", WANT_C1)
C2_HALF = mimcap("MIM-4P0P", WANT_C2)
L1_HALF = spiral("SPIRAL-1N2", WANT_L1)
R1_HALF = tfr("TFR-62R", WANT_R1)


# ── the cell itself ────────────────────────────────────────────────────────────────────────────
#
#   RF1 ──C1── RF2 ──L1── VB ──R1── VG
#                          └──C2── 0   (two backside vias, through metal nobody drew)

# NOTHING HERE STAMPS A `Net`, AND THAT IS DELIBERATE.  This cell does not need to: its ground is
# the stackup's undrawn `Backside Metal`, so the two backside vias reach net "0" through
# `CopperPieces.Ground` rather than through a label, and its three ports are boundary PINS, which
# the terminal map resolves.  It also could not: a `Net` on copper that touches unnamed copper is
# reported by `MinSpacing` as a zero-clearance violation — see `Attenuator.gen.py` and
# `examples/RESOLVED.md`.
shapes, instances, lands = [], [], []

def copper(x1, y1, x2, y2, layer, what, net=None):
    s = {"$type": "Rect", "X1": um(x1), "Y1": um(y1), "X2": um(x2), "Y2": um(y2),
         "Layer": {"Layer": layer, "Datatype": 0}}
    if net: s["Net"] = net
    shapes.append(s)
    lands.append((what, layer, x1, y1, x2, y2))

def post(cx, cy, what):
    """A Metal1-Metal2 post, with the patch of each metal that lands on it."""
    copper(cx - 12, cy - 12, cx + 12, cy + 12, L_M2,   what)
    copper(cx - 12, cy - 12, cx + 12, cy + 12, L_M1,   what)
    shapes.append({"$type": "Rect", "X1": um(cx - 7), "Y1": um(cy - 7),
                   "X2": um(cx + 7), "Y2": um(cy + 7),
                   "Layer": {"Layer": L_POST, "Datatype": 0}})
    lands.append((what, L_POST, cx - 7, cy - 7, cx + 7, cy + 7))

def place(ref, cell, cx, cy, half, pin_layers):
    instances.append({"CellRef": "../../parts/" + cell,
                      "X": um(cx), "Y": um(cy), "Rot": "R0", "MirrorX": False, "Mag": 1.0,
                      "Rows": 1, "Cols": 1, "PitchX": 0, "PitchY": 0, "SchematicId": ref})
    # Only the two pin POINTS are tracked for the audit; the cell's own copper is its business.
    lands.append((f"{ref}.1", pin_layers[0], cx - half + 5, cy - 3, cx - half + 5, cy + 3))
    lands.append((f"{ref}.2", pin_layers[1], cx + half - 5, cy - 3, cx + half - 5, cy + 3))

# ── the RF path ────────────────────────────────────────────────────────────────────────────────
copper(0, 240, 80, 320, L_M1, "RF1 pad")
copper(70, 270, 165, 290, L_M1, "RF1 feed")
place("C1", "MIM-0P8P", 200, 280, C1_HALF, (L_M1, L_M2))
copper(200 + C1_HALF - 20, 268, 262, 292, L_M2, "C1.2 reach")
post(270, 280, "post A")
copper(264, 270, 340, 290, L_M1, "RF2 feed")
copper(330, 240, 410, 320, L_M1, "RF2 pad")

# ── the bias feed ──────────────────────────────────────────────────────────────────────────────
copper(340, 310, 360, 470, L_M1, "VB drop")
copper(340, 455, 365, 475, L_M1, "L1.1 reach")
place("L1", "SPIRAL-1N2", 350 + L1_HALF, 465, L1_HALF, (L_M1, L_M1))
VBX = 350 + 2 * L1_HALF
copper(VBX - 10, 455, VBX + 90, 475, L_M1, "VB run")

# ── the decoupling capacitor, and the ground it reaches through the wafer ─────────────────────
C2X = VBX + 90 + C2_HALF - 15
place("C2", "MIM-4P0P", C2X, 465, C2_HALF, (L_M1, L_M2))
copper(C2X + C2_HALF - 20, 453, C2X + C2_HALF + 30, 477, L_M2, "C2.2 reach")
post(C2X + C2_HALF + 20, 465, "post B")
copper(C2X + C2_HALF + 8, 420, C2X + C2_HALF + 210, 520, L_M1, "ground pad")

BSV = [(C2X + C2_HALF + 60, 470), (C2X + C2_HALF + 160, 470)]
for bx, by in BSV:
    shapes.append({"$type": "Via", "X": um(bx), "Y": um(by),
                   "PadSize": um(80), "DrillSize": um(60),
                   "LandingLayer": {"Layer": L_M1, "Datatype": 0},
                   "Layer": {"Layer": L_BSV, "Datatype": 0}})

# ── the de-Q resistor, out to the gate pad ─────────────────────────────────────────────────────
copper(VBX + 10, 470, VBX + 30, 640, L_M1, "VG drop")
copper(VBX + 10, 620, VBX + 60, 640, L_M1, "R1.1 reach")
R1X = VBX + 50 + R1_HALF
place("R1", "TFR-62R", R1X, 630, R1_HALF, (L_M1, L_M1))
copper(R1X + R1_HALF - 10, 620, R1X + R1_HALF + 60, 640, L_M1, "VG run")
copper(R1X + R1_HALF + 50, 590, R1X + R1_HALF + 130, 670, L_M1, "VG pad")


# ── THE AUDIT ──────────────────────────────────────────────────────────────────────────────────
# Same shape as the board's: an independent partition of the root's own copper, plus the pin
# POINTS of each placed part, compared against the netlist this cell is supposed to be.  It knows
# nothing about the inside of a part cell, which is the point — what it checks is that every
# terminal lands on the run the schematic says it lands on.

EXPECTED = {
    "RF1": {"C1.1"},
    "RF2": {"C1.2", "L1.1"},
    "VB":  {"L1.2", "C2.1", "R1.1"},
    "0":   {"C2.2"},
    "VG":  {"R1.2"},
}

parent = {i: i for i in range(len(lands))}
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]; a = parent[a]
    return a
def union(a, b):
    ra, rb = find(a), find(b)
    if ra != rb: parent[ra] = rb

def touch(a, b):
    _, la, ax1, ay1, ax2, ay2 = a
    _, lb, bx1, by1, bx2, by2 = b
    return la == lb and ax1 <= bx2 and bx1 <= ax2 and ay1 <= by2 and by1 <= ay2

for i in range(len(lands)):
    for j in range(i + 1, len(lands)):
        if touch(lands[i], lands[j]): union(i, j)

# A post joins the Metal1 and Metal2 patches that land on it; a backside via reaches metal the
# technology states and the artwork does not draw, which is the whole point of it.
for name in ("post A", "post B"):
    members = [i for i, l in enumerate(lands) if l[0] == name]
    for m in members[1:]: union(members[0], m)

problems = []
groups = {}
for i, l in enumerate(lands): groups.setdefault(find(i), set()).add(l[0])
by_name = {}
for members in groups.values():
    for n in members: by_name[n] = members

for net, terminals in EXPECTED.items():
    seen = [by_name.get(t) for t in sorted(terminals)]
    if any(s is None for s in seen):
        problems.append(f"net {net}: one of {sorted(terminals)} is on no copper")
    elif any(s is not seen[0] for s in seen):
        problems.append(f"net {net} is not one piece of copper: {sorted(terminals)}")

names = list(EXPECTED)
for i in range(len(names)):
    for j in range(i + 1, len(names)):
        a, b = next(iter(EXPECTED[names[i]])), next(iter(EXPECTED[names[j]]))
        if by_name.get(a) is not None and by_name.get(a) is by_name.get(b):
            problems.append(f"nets {names[i]} and {names[j]} are one piece of copper")

MIN_CLEAR = 4.0                                                    # the technology's own rule
def gap(a, b):
    _, _, ax1, ay1, ax2, ay2 = a
    _, _, bx1, by1, bx2, by2 = b
    dx = max(bx1 - ax2, ax1 - bx2, 0.0)
    dy = max(by1 - ay2, ay1 - by2, 0.0)
    return math.hypot(dx, dy)

for i in range(len(lands)):
    for j in range(i + 1, len(lands)):
        if lands[i][1] not in (L_M1, L_M2) or lands[i][1] != lands[j][1]: continue
        if find(i) == find(j): continue
        d = gap(lands[i], lands[j])
        if d < MIN_CLEAR:
            problems.append(f"{lands[i][0]} and {lands[j][0]} are {d:.2f} um apart on layer "
                            f"{lands[i][1]} and are different nets (the rule is {MIN_CLEAR} um)")

if problems:
    for p in problems: print("AUDIT: " + p)
    raise SystemExit("the artwork is not sound; nothing was written")

clay = {"FormatVersion": 1, "DbuPerMicron": UM, "DisplayUnit": "Um", "SnapDbu": 5,
        "AngleMode": "AnyAngle", "TechRef": "../../tech/mmic-GaAs_2LM_100um.ctech",
        "Pins": [pin("RF1", 20,  280, 60, 180, L_M1),
                 pin("RF2", 390, 280, 60, 0,   L_M1),
                 pin("VG",  R1X + R1_HALF + 120, 630, 60, 0, L_M1)],
        "Shapes": shapes, "Instances": instances}

out = os.path.join(root, "Bias tee", "layout", "Bias tee.clay")
with open(out, "w") as f:
    json.dump(clay, f, indent=2); f.write("\n")


# ── THE TOLERANCE MEASUREMENT (R-lvs5-4) ───────────────────────────────────────────────────────
# What the schematic asked for, what the geometry on the grid actually produces, and the gap
# between them.  This is the only honest way to choose a property tolerance: the spread of a
# CORRECT design is the floor, and a default below it fails good artwork.
print(f"{len(shapes)} shapes, {len(instances)} instances, "
      f"{len(set(find(i) for i in range(len(lands))))} nets, {len(BSV)} backside vias")
print()
print(f"{'part':<12} {'dimension':<12} {'requested':>14} {'resolved':>14} {'spread':>10}")
worst = 0.0
for name, dim, want, got in report:
    rel = abs(got - want) / abs(want)
    worst = max(worst, rel)
    print(f"{name:<12} {dim:<12} {want:>14.6g} {got:>14.6g} {rel * 100:>9.4f}%")
print(f"{'':<12} {'grid':<12} {GRID:>14} um {'':>11} worst {worst * 100:.4f}%")
