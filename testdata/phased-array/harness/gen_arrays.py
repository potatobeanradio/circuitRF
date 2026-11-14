# Generate PAA-1 array cells (layout + planar .cem) from the Patch Antenna element.
import json, os, sys, copy
ws = sys.argv[1]
cells_cpw = int(sys.argv[2]) if len(sys.argv) > 2 else 12
edge = (sys.argv[3] == "edge") if len(sys.argv) > 3 else False
MM = 1_000_000                     # DBU per mm (DbuPerMicron 1000)
PITCH = 25.86                      # mm, lambda0/2 at 5.8 GHz
src = json.load(open(os.path.join(ws, "patch/layout/patch.clay")))
cem0 = json.load(open(os.path.join(ws, "patch/em/patch-5p8GHz.cem")))
patch = [s for s in src["Shapes"] if s["$type"] == "Poly"][0]
feed = [s for s in src["Shapes"] if s["$type"] == "Rect" and s["Layer"]["Layer"] == 1][0]
label = [s for s in src["Shapes"] if s["$type"] == "Label"][0]
cx, cy = 8.47 * MM, 6.65 * MM      # element reference point: patch centre

def cell(name, rows, cols):
    shapes = []
    n = 0
    for r in range(rows):          # row 1 is the TOP row (largest y), seen from +z
        for c in range(cols):
            n += 1
            dx = round((c - (cols - 1) / 2) * PITCH * MM - cx)
            dy = round(((rows - 1) / 2 - r) * PITCH * MM - cy)
            p = copy.deepcopy(patch); p["Xy"] = [v + (dx if i % 2 == 0 else dy) for i, v in enumerate(p["Xy"])]
            f = copy.deepcopy(feed); f["X1"] += dx; f["X2"] += dx; f["Y1"] += dy; f["Y2"] += dy
            l = copy.deepcopy(label); l["X"] += dx; l["Y"] += dy; l["Text"] = str(n)
            shapes += [p, f, l]
    lay = dict(src); lay["Shapes"] = shapes; lay["Instances"] = []
    d = os.path.join(ws, name)
    os.makedirs(os.path.join(d, "layout"), exist_ok=True); os.makedirs(os.path.join(d, "em"), exist_ok=True)
    json.dump(lay, open(os.path.join(d, "layout", name + ".clay"), "w"), indent=2)
    cem = copy.deepcopy(cem0)
    cem["Name"] = name
    cem["LayoutRef"] = f"{name}/layout/{name}.clay"
    cem["Frequency"].update(StartExpr="5.8", StopExpr="6.0", NumPoints=3)
    cem["ResonanceSearch"] = False
    cem["PlanarMesh"].update(Auto=False, CellsPerWavelength=cells_cpw, EdgeMesh=edge)
    json.dump(cem, open(os.path.join(d, "em", name + ".cem"), "w"), indent=2)
    print(name, n, "ports")

for name, r, c in [("e1", 1, 1), ("e2x", 1, 2), ("e2y", 2, 1), ("e3x", 1, 4), ("e3y", 4, 1), ("e4", 2, 2), ("e33", 3, 3), ("e5", 5, 5), ("e66", 6, 6), ("l9x", 1, 9), ("l9y", 9, 1)]:
    cell(name, r, c)
