#!/usr/bin/env python3
"""Writes the broken copy of the board, by mutating the correct one.

    python3 "examples/LVS/Attenuator broken/layout/Attenuator.break.py" examples/LVS
    python3 "examples/LVS/Attenuator broken/layout/Attenuator.break.py" examples/LVS --only F4

Six faults, each ONE named minimal mutation of `Attenuator/layout/Attenuator.clay`
(brief-lvs-5-proving-designs.md R-lvs5-2).  `--only Fn` writes a layout carrying exactly one of
them, which is what lets a gate point at a single finding; the no-argument form writes all six,
which is the realistic case and also the one where a comparator reporting at the wrong granularity
looks fine.

THE SCHEMATIC IS NOT TOUCHED.  `Attenuator broken/schematic/Attenuator.csch` is a byte-for-byte
copy of the correct cell's, and the whole point of the fixture is that the two designs differ
ONLY in artwork.  This script reads the correct `.clay` and writes the broken one; it never writes
anything else, and it refuses rather than guessing if the shape it is about to mutate is not the
shape it expects — a mutation that silently found nothing is a fault the gate would report as
absent rather than as broken.

  F1  swapped net    R2's output link re-pointed onto the top ground
  F2  missing part   C1's placement deleted, its risers and run left bare
  F3  extra part     a second, unwired 0402 resistor placed
  F4  short          a 0.2 mm spur of copper joining IN to the bottom ground strip
  F5  open           the upper-left ground's only stitching via deleted
  F6  wrong value    R3 re-pointed from the 294 ohm part to a 150 ohm one
"""
import json, os, sys

MM = 1_000_000
def mm(v): return int(round(v * MM))

L_TOP, L_SILK, L_VIA = 1, 3, 5
FAULTS = ("F1", "F2", "F3", "F4", "F5", "F6")

args = [a for a in sys.argv[1:]]
only = None
out_path = None
root = "."
i = 0
while i < len(args):
    if args[i] == "--only":
        only = args[i + 1].upper(); i += 2
    elif args[i] in ("-o", "--out"):
        out_path = args[i + 1]; i += 2
    else:
        root = args[i]; i += 1

if only is not None and only not in FAULTS:
    raise SystemExit(f"--only takes one of {', '.join(FAULTS)}")

wanted = set(FAULTS) if only is None else {only}

src = os.path.join(root, "Attenuator", "layout", "Attenuator.clay")
clay = json.load(open(src))
shapes, instances = clay["Shapes"], clay["Instances"]
applied = []


def rect_at(x1, y1, x2, y2, layer=L_TOP):
    """The index of the one rectangle with exactly these corners, or a refusal."""
    want = (mm(x1), mm(y1), mm(x2), mm(y2))
    hits = [i for i, s in enumerate(shapes)
            if s["$type"] == "Rect" and s["Layer"]["Layer"] == layer
            and (s["X1"], s["Y1"], s["X2"], s["Y2"]) == want]
    if len(hits) != 1:
        raise SystemExit(f"expected exactly one rectangle at {(x1, y1, x2, y2)} on layer {layer}, "
                         f"found {len(hits)} — the correct board has moved and this script has not")
    return hits[0]


def instance_of(refdes):
    hits = [i for i, n in enumerate(instances) if n.get("SchematicId") == refdes]
    if len(hits) != 1:
        raise SystemExit(f"expected exactly one placement of {refdes}, found {len(hits)}")
    return hits[0]


# ── F1 — a swapped net ─────────────────────────────────────────────────────────────────────────
# The link that ties R2's terminal 2 to the output run is re-pointed: it now runs UP into the
# upper-right ground instead of right into `sig_right`.  R2's terminal 2 is therefore on the
# ground net, and the output net is still there — fed by R3 and C1 — so this is a wrong net and
# not an open, which is the distinction the finding has to make.
if "F1" in wanted:
    del shapes[rect_at(10.60, 5.875, 12.10, 6.125)]
    shapes.append({"$type": "Rect", "X1": mm(10.60), "Y1": mm(5.875),
                   "X2": mm(10.85), "Y2": mm(8.45),
                   "Layer": {"Layer": L_TOP, "Datatype": 0}})
    applied.append("F1 R2 terminal 2 re-pointed onto the top ground")

# ── F2 — a missing part ────────────────────────────────────────────────────────────────────────
# The PLACEMENT goes and the root's own copper stays, so the two risers and the run that fed C1
# are still there with nothing between them.  That is what a part left off a board looks like.
if "F2" in wanted:
    del instances[instance_of("C1")]
    applied.append("F2 C1's placement deleted")

# ── F3 — an extra part ─────────────────────────────────────────────────────────────────────────
# A second 294 ohm resistor, placed and wired to nothing, carrying a designator of its own rather
# than a SchematicId: there is no schematic component for it to have one of.
if "F3" in wanted:
    instances.append({"CellRef": "../../footprints/R0402-294R",
                      "X": mm(17.00), "Y": mm(4.70), "Rot": "R0", "MirrorX": False,
                      "Mag": 1.0, "Rows": 1, "Cols": 1, "PitchX": 0, "PitchY": 0,
                      "RefDes": "R4"})
    applied.append("F3 an unwired R4 placed at (17.00, 4.70)")

# ── F4 — a short ───────────────────────────────────────────────────────────────────────────────
# 0.2 mm of copper from the lower ground strip up to the input run.  Nothing else changes, the
# board still looks entirely ordinary, and every net on it is still named — which is exactly why a
# short has to be reported with its PATH and the spur's own coordinate (R-lvs5-2d).
if "F4" in wanted:
    shapes.append({"$type": "Rect", "X1": mm(1.40), "Y1": mm(3.55),
                   "X2": mm(1.60), "Y2": mm(5.80),
                   "Layer": {"Layer": L_TOP, "Datatype": 0}})
    applied.append("F4 a 0.2 mm spur at (1.40, 3.55)-(1.60, 5.80) joins IN to ground")

# ── F5 — an open ───────────────────────────────────────────────────────────────────────────────
# The upper-left ground hangs off one barrel.  Delete it and that copper — with R1's return on it —
# becomes an island of its own.
#
# TWO ISLANDS, NOT THREE, and that is a correction to the brief rather than a shortfall: a barrel
# joins at most ONE piece per conductor (`DrcConnectivity.FirstTouching`), so on a two-layer board
# a via is an edge of degree two and removing one edge of a tree splits it in two.  Three islands
# from one deleted via is not reachable here at any geometry.
if "F5" in wanted:
    hits = [i for i, s in enumerate(shapes)
            if s["$type"] == "Via" and (s["X"], s["Y"]) == (mm(2.00), mm(9.70))]
    if len(hits) != 1:
        raise SystemExit("expected exactly one stitching via at (2.00, 9.70)")
    del shapes[hits[0]]
    applied.append("F5 the stitching via at (2.00, 9.70) deleted")

# ── F6 — a wrong value ─────────────────────────────────────────────────────────────────────────
# R3's placement now points at the 150 ohm part.  The land pattern is identical, the designator is
# unchanged and the topology is unchanged: only what the artwork CLAIMS the part is has moved.
if "F6" in wanted:
    n = instances[instance_of("R3")]
    if n["CellRef"] != "../../footprints/R0402-294R":
        raise SystemExit(f"R3 points at {n['CellRef']}, not the 294 ohm part")
    n["CellRef"] = "../../footprints/R0402-150R"
    applied.append("F6 R3 re-pointed from R0402-294R to R0402-150R")

out = out_path or os.path.join(root, "Attenuator broken", "layout", "Attenuator.clay")
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out, "w") as f:
    json.dump(clay, f, indent=2)
    f.write("\n")

for line in applied:
    print(line)
print(f"{len(applied)} fault(s) written to {out}")
