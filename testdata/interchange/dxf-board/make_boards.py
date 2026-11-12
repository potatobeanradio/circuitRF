"""The two-layer RF board drawn as DXF outlines, written the ways other CAD programs write a DXF.

The fixture of DxfBoardRecognitionTests (tests/Ui.Tests/Recognition). One board, one drawing per spelling, every
drawing the same copper, holes and silkscreen, so each must recognise to the same circuit:

  board-r12.dxf      R12: closed POLYLINE/VERTEX outlines, TEXT designators, $INSUNITS millimetres
  board-r2000.dxf    R2000: LWPOLYLINE with bulges, HATCH pours, MTEXT designators, nested INSERT blocks, a LAYER table
  board-inch.dxf     board-r2000.dxf in inches, $INSUNITS inches
  board-nounits.dxf  board-r12.dxf in inches with no $INSUNITS: it needs --dxf-units 1
  board-strokes.dxf  every outline stroked as LINE and ARC entities, the way most drawings of outlines are
  board-mirror.dxf   board-r12.dxf mirrored top to bottom

Written with the Python standard library alone (no DXF package): DXF is text, and the group codes used are the
published ones. Run it from anywhere with  python3 -I make_boards.py ; it rewrites the six files beside it.

The board, 40 x 30 mm, layers COPPER_TOP, COPPER_BOTTOM (a solid plane), DRILL, SILK_TOP and BOARD (the edge):

  P1 -- 1.1 mm line -- C1 across a gap -- line -- the device pad (5 x 5 mm, rounded corners), with C2 and C3 from its
  corners to the pour, a 2 mm bias strip up (R1 across a gap, C4 3.1 mm further, on to the top edge, P2) and its
  mirror image down (R2, C5, P3) -- a 1.1 mm line -- C8 across a gap -- a second pad (3 x 3 mm) -- 1.7 x 5.6 mm with
  the pour 0.80 mm above and 0.51 mm below, C6 and C7 across those gaps -- a 1.1 mm line to the right edge, P4.

No part has a land of its own: each is a silkscreen body outline over a gap in the copper, named by a designator
typed beside it. The pours, 0.5 mm clear of the signal copper, are stitched to the plane through 0.4 mm drill marks
drawn as circles, squares (a closed outline, and four lines), octagons and two-arc circles.
"""
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
UM = 1000  # coordinates are integer micrometres; a mm is 1000

W_BOARD, H_BOARD = 40 * UM, 30 * UM


def r(x0, y0, x1, y1):
    """A rectangle in mm, as integer micrometres."""
    return tuple(int(round(v * UM)) for v in (x0, y0, x1, y1))


# ── the signal copper ───────────────────────────────────────────────────────────────────────────────
SIGNAL = {
    "IN1": r(0, 14.45, 6, 15.55),       # P1 to C1's gap
    "IN2": r(6.5, 14.45, 11.5, 15.55),  # C1's gap to the device pad
    "PADA": r(11.5, 12.5, 16.5, 17.5),  # the device pad
    "BT1": r(13, 17.5, 15, 22.5),       # bias strip up, to R1's gap
    "BT2": r(13, 23, 15, 30),           # R1's gap to the top edge
    "BB1": r(13, 7.5, 15, 12.5),        # bias strip down, its mirror image
    "BB2": r(13, 0, 15, 7),
    "TL3": r(16.5, 14.45, 22.5, 15.55), # device pad to C8's gap
    "PADB": r(23, 13.5, 26, 16.5),      # the second pad
    "F6": r(26, 14.15, 31.6, 15.85),    # 1.7 x 5.6 mm
    "OUT": r(31.6, 14.45, 40, 15.55),   # to P4
}
GAP = 0.5
# The pour's clearance round each piece: GAP, but 0.80 mm above the 1.7 mm run and 0.51 mm below it.
CLEAR = [r(x0 / UM - GAP, y0 / UM - GAP, x1 / UM + GAP, y1 / UM + GAP) for k, (x0, y0, x1, y1) in SIGNAL.items() if k != "F6"]
CLEAR.append(r(26, 14.15 - 0.51, 31.6, 15.85 + 0.80))
POUR = [r(0, 0, 40, 30)]
ROUNDED = (SIGNAL["PADA"], 0.25 * UM)   # the device pad's corners are rounded, 0.25 mm

# ── the parts: a silkscreen body over a gap, and a designator beside it ─────────────────────────────
# (designator, body centre x, y in mm, along x?, where the text's baseline starts)
BODY_LONG, BODY_SHORT = 1.6, 0.8   # an 0603's body
PARTS = [
    ("C1", 6.25, 15.0, True, (5.6, 16.2)),
    ("C2", 12.0, 17.75, False, (10.2, 18.0)),
    ("C3", 12.0, 12.25, False, (10.2, 11.2)),
    ("R1", 14.0, 22.75, False, (15.8, 22.5)),
    ("R2", 14.0, 7.25, False, (15.8, 6.7)),
    ("C4", 15.25, 25.85, True, (16.4, 26.6)),
    ("C5", 15.25, 4.15, True, (16.4, 2.6)),
    ("C8", 22.75, 15.0, True, (22.3, 16.2)),
    ("C6", 31.2, 16.25, False, (32.0, 17.3)),
    ("C7", 31.2, 13.895, False, (32.0, 12.0)),
]
TEXT_HEIGHT = 0.8

DRILL = 0.4
DRILL_KINDS = ["circle", "square", "square-lines", "octagon", "bulge-circle"]


# ── the copper as loops: a boolean on the grid the rectangles' edges make ───────────────────────────

def region(adds, cuts):
    """The filled cells of (the union of adds) minus (the union of cuts), on the grid of their edges."""
    xs = sorted({v for q in adds + cuts for v in (q[0], q[2])})
    ys = sorted({v for q in adds + cuts for v in (q[1], q[3])})

    def inside(q, cx, cy):
        return q[0] < cx < q[2] and q[1] < cy < q[3]

    filled = set()
    for i in range(len(xs) - 1):
        for j in range(len(ys) - 1):
            cx, cy = (xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2
            if any(inside(q, cx, cy) for q in adds) and not any(inside(q, cx, cy) for q in cuts):
                filled.add((i, j))
    return xs, ys, filled


def components(filled):
    """The filled cells in 4-connected pieces."""
    left, pieces = set(filled), []
    while left:
        seed = left.pop()
        piece, stack = {seed}, [seed]
        while stack:
            i, j = stack.pop()
            for n in ((i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1)):
                if n in left:
                    left.remove(n)
                    piece.add(n)
                    stack.append(n)
        pieces.append(piece)
    return sorted(pieces, key=lambda p: min(p))


def loops(xs, ys, cells):
    """A piece's boundary as loops of points, the copper on the left: the outer loop anticlockwise, holes clockwise."""
    edges = {}
    for i, j in cells:
        x0, x1, y0, y1 = xs[i], xs[i + 1], ys[j], ys[j + 1]
        for (a, b), n in ((((x0, y0), (x1, y0)), (i, j - 1)), (((x1, y0), (x1, y1)), (i + 1, j)),
                          (((x1, y1), (x0, y1)), (i, j + 1)), (((x0, y1), (x0, y0)), (i - 1, j))):
            if n not in cells:
                assert a not in edges, f"two boundaries leave {a}: a pinch the drawing must not have"
                edges[a] = b
    result = []
    while edges:
        start = next(iter(edges))
        ring, at = [start], edges.pop(start)
        while at != start:
            ring.append(at)
            at = edges.pop(at)
        # collinear points out
        pts = [p for k, p in enumerate(ring)
               if (ring[k - 1][0] - p[0]) * (ring[(k + 1) % len(ring)][1] - p[1])
               - (ring[k - 1][1] - p[1]) * (ring[(k + 1) % len(ring)][0] - p[0]) != 0]
        result.append(pts)
    return sorted(result, key=lambda l: -abs(area(l)))


def area(pts):
    return 0.5 * sum(pts[k - 1][0] * pts[k][1] - pts[k][0] * pts[k - 1][1] for k in range(len(pts)))


def rounded(pts, box, radius):
    """The loop with each corner of box that it has as a convex vertex filleted: (x, y, bulge) per vertex, the bulge
    being the DXF one of the edge that leaves it."""
    corners = {(box[0], box[1]), (box[2], box[1]), (box[2], box[3]), (box[0], box[3])}
    out = []
    n = len(pts)
    for k, p in enumerate(pts):
        a, b = pts[k - 1], pts[(k + 1) % n]
        turn = (p[0] - a[0]) * (b[1] - p[1]) - (p[1] - a[1]) * (b[0] - p[0])
        if p in corners and turn > 0:
            la, lb = math.hypot(p[0] - a[0], p[1] - a[1]), math.hypot(b[0] - p[0], b[1] - p[1])
            din, dout = ((p[0] - a[0]) / la, (p[1] - a[1]) / la), ((b[0] - p[0]) / lb, (b[1] - p[1]) / lb)
            out.append((p[0] - radius * din[0], p[1] - radius * din[1], math.tan(math.pi / 8)))
            out.append((p[0] + radius * dout[0], p[1] + radius * dout[1], 0.0))
        else:
            out.append((p[0], p[1], 0.0))
    return out


def copper():
    """Every piece of top copper: (its loops, each a list of (x, y, bulge); is it a pour)."""
    signal = list(SIGNAL.values())
    pieces = []
    for adds, cuts, pour in ((signal, [], False), (POUR, CLEAR, True)):
        xs, ys, filled = region(adds, cuts)
        for piece in components(filled):
            rings = loops(xs, ys, piece)
            rings = [rounded(l, *ROUNDED) if not pour else [(x, y, 0.0) for x, y in l] for l in rings]
            pieces.append((rings, pour))
    return pieces


def pour_vias():
    """A 4 mm grid of stitching holes, kept where the pour is at least 0.5 mm all round."""
    xs, ys, filled = region(POUR, CLEAR)

    def is_pour(x, y):
        i = next((k for k in range(len(xs) - 1) if xs[k] <= x <= xs[k + 1]), None)
        j = next((k for k in range(len(ys) - 1) if ys[k] <= y <= ys[k + 1]), None)
        return i is not None and j is not None and (i, j) in filled

    keep = []
    for gx in range(2, 40, 4):
        for gy in range(2, 30, 4):
            x, y = gx * UM, gy * UM
            if all(is_pour(x + dx, y + dy) for dx in (-500, 0, 500) for dy in (-500, 0, 500)):
                keep.append((x, y))
    return keep


# ── a DXF, entity by entity ─────────────────────────────────────────────────────────────────────────

class Dxf:
    """Group-code pairs. `scale` turns micrometres into drawing units; `mirror` flips the board top to bottom."""

    def __init__(self, scale, mirror=False):
        self.out, self.scale, self.mirror = [], scale, mirror
        self.handle = 0x100

    def g(self, code, value):
        self.out.append(f"{code:>3}\n{value}\n")

    def num(self, code, value):
        if isinstance(value, float):
            value = f"{value:.7f}".rstrip("0").rstrip(".")
            value = "0" if value in ("", "-0") else value
        self.g(code, value)

    def x(self, v):
        return v / UM * self.scale

    def y(self, v):
        return (H_BOARD - v if self.mirror else v) / UM * self.scale

    def b(self, bulge):
        return -bulge if self.mirror else bulge

    def point(self, code, x, y):
        self.num(code, self.x(x))
        self.num(code + 10, self.y(y))

    def head(self, kind, layer, handles=True):
        self.g(0, kind)
        if handles:
            self.g(5, f"{self.handle:X}")
            self.handle += 1
        self.g(8, layer)

    # R12 ---------------------------------------------------------------------------------------------
    def polyline(self, layer, pts, closed=True):
        """POLYLINE / VERTEX / SEQEND; pts are (x, y, bulge)."""
        self.head("POLYLINE", layer, handles=False)
        self.g(66, 1)
        self.point(10, 0, H_BOARD if self.mirror else 0)
        self.g(70, 1 if closed else 0)
        for x, y, bulge in pts:
            self.head("VERTEX", layer, handles=False)
            self.point(10, x, y)
            if bulge:
                self.num(42, self.b(bulge))
        self.head("SEQEND", layer, handles=False)

    def text(self, layer, x, y, height, value):
        self.head("TEXT", layer, handles=False)
        # mirrored, the baseline moves so the text's box lands where the mirrored box is
        self.point(10, x, y + height * UM if self.mirror else y)
        self.num(40, height * self.scale)
        self.g(1, value)

    # R2000 -------------------------------------------------------------------------------------------
    def lwpolyline(self, layer, pts, closed=True):
        self.head("LWPOLYLINE", layer)
        self.g(100, "AcDbEntity")
        self.g(100, "AcDbPolyline")
        self.g(90, len(pts))
        self.g(70, 1 if closed else 0)
        for x, y, bulge in pts:
            self.point(10, x, y)
            if bulge:
                self.num(42, self.b(bulge))

    def hatch(self, layer, rings):
        """A solid HATCH, its first loop the outline and the rest its holes, each a polyline boundary."""
        self.head("HATCH", layer)
        self.g(100, "AcDbEntity")
        self.g(100, "AcDbHatch")
        self.point(10, 0, H_BOARD if self.mirror else 0)
        self.num(30, 0.0)
        self.num(210, 0.0); self.num(220, 0.0); self.num(230, 1.0)
        self.g(2, "SOLID")
        self.g(70, 1)
        self.g(71, 0)
        self.g(91, len(rings))
        for k, ring in enumerate(rings):
            self.g(92, 3 if k == 0 else 2)   # a polyline loop; the first the external one
            has_bulge = any(bulge for *_, bulge in ring)
            self.g(72, 1 if has_bulge else 0)
            self.g(73, 1)
            self.g(93, len(ring))
            for x, y, bulge in ring:
                self.point(10, x, y)
                if has_bulge:
                    self.num(42, self.b(bulge))
            self.g(97, 0)
        self.g(75, 0)
        self.g(76, 1)
        self.g(98, 0)

    def mtext(self, layer, x, y, height, value):
        self.head("MTEXT", layer)
        self.g(100, "AcDbEntity")
        self.g(100, "AcDbMText")
        # attachment 7, bottom left: the insertion point is where a TEXT's baseline would start
        self.point(10, x, y + height * UM if self.mirror else y)
        self.num(30, 0.0)
        self.num(40, height * self.scale)
        self.g(71, 7)
        self.g(72, 1)
        self.g(1, value)

    def insert(self, layer, block, x, y, rotation=0.0):
        self.head("INSERT", layer)
        self.g(100, "AcDbEntity")
        self.g(100, "AcDbBlockReference")
        self.g(2, block)
        self.point(10, x, y)
        if rotation:
            self.num(50, -rotation if self.mirror else rotation)

    # both --------------------------------------------------------------------------------------------
    def line(self, layer, a, b):
        self.head("LINE", layer)
        self.point(10, *a)
        self.point(11, *b)

    def arc(self, layer, cx, cy, radius, start, end):
        """An ARC, anticlockwise from start to end degrees (mirrored, the same arc in the flipped drawing)."""
        self.head("ARC", layer)
        self.point(10, cx, cy)
        self.num(40, radius / UM * self.scale)
        if self.mirror:
            start, end = -end, -start
        self.num(50, start % 360)
        self.num(51, end % 360)

    def circle(self, layer, cx, cy, radius):
        self.head("CIRCLE", layer)
        self.point(10, cx, cy)
        self.num(40, radius / UM * self.scale)

    def text_of(self):
        return "".join(self.out)


def strokes(d, layer, ring):
    """A loop of (x, y, bulge) as LINE and ARC entities."""
    n = len(ring)
    for k in range(n):
        x0, y0, bulge = ring[k]
        x1, y1, _ = ring[(k + 1) % n]
        if not bulge:
            d.line(layer, (x0, y0), (x1, y1))
            continue
        # The arc of a bulge: included angle 4·atan(bulge), anticlockwise for a positive bulge.
        theta = 4 * math.atan(bulge)
        chord = math.hypot(x1 - x0, y1 - y0)
        radius = chord / (2 * math.sin(theta / 2))
        mx, my = (x0 + x1) / 2, (y0 + y1) / 2
        h = radius * math.cos(theta / 2)
        nx, ny = -(y1 - y0) / chord, (x1 - x0) / chord
        cx, cy = mx + nx * h, my + ny * h
        a0 = math.degrees(math.atan2(y0 - cy, x0 - cx))
        a1 = math.degrees(math.atan2(y1 - cy, x1 - cx))
        d.arc(layer, cx, cy, radius, a0, a1)


def body(cx, cy, along_x):
    """A part's body outline: a rectangle centred at (cx, cy) mm."""
    hl, hs = BODY_LONG / 2, BODY_SHORT / 2
    hx, hy = (hl, hs) if along_x else (hs, hl)
    x0, y0, x1, y1 = r(cx - hx, cy - hy, cx + hx, cy + hy)
    return [(x0, y0, 0.0), (x1, y0, 0.0), (x1, y1, 0.0), (x0, y1, 0.0)]


def drill_marks(x, y, kind):
    """One drill mark as loops of (x, y, bulge), or ('circle', radius)."""
    h = DRILL / 2 * UM
    if kind == "circle":
        return ("circle", h)
    if kind in ("square", "square-lines"):
        return [(x - h, y - h, 0.0), (x + h, y - h, 0.0), (x + h, y + h, 0.0), (x - h, y + h, 0.0)]
    if kind == "octagon":
        # flats on the axes: the box is the drill across
        t = h * math.tan(math.pi / 8)
        return [(x + h, y - t, 0.0), (x + h, y + t, 0.0), (x + t, y + h, 0.0), (x - t, y + h, 0.0),
                (x - h, y + t, 0.0), (x - h, y - t, 0.0), (x - t, y - h, 0.0), (x + t, y - h, 0.0)]
    return [(x - h, y, 1.0), (x + h, y, 1.0)]   # two half-circle arcs


# ── the drawings ────────────────────────────────────────────────────────────────────────────────────

LAYERS = ["COPPER_TOP", "COPPER_BOTTOM", "DRILL", "SILK_TOP", "BOARD"]


def header(d, version, insunits):
    d.g(0, "SECTION"); d.g(2, "HEADER")
    d.g(9, "$ACADVER"); d.g(1, version)
    if insunits is not None:
        d.g(9, "$INSUNITS"); d.g(70, insunits)
    d.g(0, "ENDSEC")


def r12(scale, insunits, mirror=False):
    d = Dxf(scale, mirror)
    header(d, "AC1009", insunits)
    d.g(0, "SECTION"); d.g(2, "ENTITIES")
    for rings, _ in copper():
        for ring in rings:
            d.polyline("COPPER_TOP", ring)
    d.polyline("COPPER_BOTTOM", [(0, 0, 0.0), (W_BOARD, 0, 0.0), (W_BOARD, H_BOARD, 0.0), (0, H_BOARD, 0.0)])
    for k, (x, y) in enumerate(pour_vias()):
        kind = DRILL_KINDS[k % len(DRILL_KINDS)]
        mark = drill_marks(x, y, kind)
        if mark[0] == "circle":
            d.circle("DRILL", x, y, mark[1])
        elif kind == "square-lines":
            for i in range(4):
                d.line("DRILL", mark[i][:2], mark[(i + 1) % 4][:2])
        else:
            d.polyline("DRILL", mark)
    for name, cx, cy, along_x, (tx, ty) in PARTS:
        d.polyline("SILK_TOP", body(cx, cy, along_x))
        d.text("SILK_TOP", tx * UM, ty * UM, TEXT_HEIGHT, name)
    d.polyline("BOARD", [(0, 0, 0.0), (W_BOARD, 0, 0.0), (W_BOARD, H_BOARD, 0.0), (0, H_BOARD, 0.0)])
    d.g(0, "ENDSEC"); d.g(0, "EOF")
    return d.text_of()


def r2000(scale, insunits):
    d = Dxf(scale)
    header(d, "AC1015", insunits)
    d.g(0, "SECTION"); d.g(2, "TABLES")
    d.g(0, "TABLE"); d.g(2, "LAYER"); d.g(70, len(LAYERS) + 1)
    for k, name in enumerate(["0"] + LAYERS):
        d.g(0, "LAYER"); d.g(2, name); d.g(70, 0); d.g(62, k + 1); d.g(6, "CONTINUOUS")
    d.g(0, "ENDTAB")
    d.g(0, "ENDSEC")

    # Blocks: a drill mark of each kind on layer 0, a stitching pair placing two of them (a block inside a block),
    # and a part body, inserted turned for the parts that lie along y.
    d.g(0, "SECTION"); d.g(2, "BLOCKS")
    for name in ("*Model_Space", "*Paper_Space"):
        d.g(0, "BLOCK"); d.g(8, "0"); d.g(2, name); d.g(70, 0); d.point(10, 0, 0); d.g(3, name)
        d.g(0, "ENDBLK"); d.g(8, "0")

    def block(name, draw):
        d.g(0, "BLOCK"); d.g(8, "0"); d.g(2, name); d.g(70, 0); d.point(10, 0, 0); d.g(3, name)
        draw()
        d.g(0, "ENDBLK"); d.g(8, "0")

    for kind in DRILL_KINDS:
        def draw(kind=kind):
            mark = drill_marks(0, 0, kind)
            if mark[0] == "circle":
                d.circle("0", 0, 0, mark[1])
            elif kind == "square-lines":
                for i in range(4):
                    d.line("0", mark[i][:2], mark[(i + 1) % 4][:2])
            else:
                d.lwpolyline("0", mark)
        block("DRILL_" + kind.upper().replace("-", "_"), draw)
    block("STITCH_PAIR", lambda: (d.insert("0", "DRILL_CIRCLE", 0, 0), d.insert("0", "DRILL_SQUARE", 0, 4 * UM)))
    block("BODY_0603", lambda: d.lwpolyline("0", body(0, 0, True)))
    d.g(0, "ENDSEC")

    d.g(0, "SECTION"); d.g(2, "ENTITIES")
    for rings, pour in copper():
        if pour:
            d.hatch("COPPER_TOP", rings)
        else:
            for ring in rings:
                d.lwpolyline("COPPER_TOP", ring)
    d.lwpolyline("COPPER_BOTTOM", [(0, 0, 0.0), (W_BOARD, 0, 0.0), (W_BOARD, H_BOARD, 0.0), (0, H_BOARD, 0.0)])
    vias = pour_vias()
    paired = set()
    for k, (x, y) in enumerate(vias):
        # a circle with a square 4 mm above it is one STITCH_PAIR
        if k in paired:
            continue
        kind = DRILL_KINDS[k % len(DRILL_KINDS)]
        above = next((j for j, v in enumerate(vias) if v == (x, y + 4 * UM)), None)
        if kind == "circle" and above is not None and DRILL_KINDS[above % len(DRILL_KINDS)] == "square" and above not in paired:
            d.insert("DRILL", "STITCH_PAIR", x, y)
            paired.update((k, above))
            continue
        d.insert("DRILL", "DRILL_" + kind.upper().replace("-", "_"), x, y)
    for name, cx, cy, along_x, (tx, ty) in PARTS:
        d.insert("SILK_TOP", "BODY_0603", cx * UM, cy * UM, 0.0 if along_x else 90.0)
        d.mtext("SILK_TOP", tx * UM, ty * UM, TEXT_HEIGHT, name)
    c = 1.0 * UM   # the board's edge with rounded corners
    d.lwpolyline("BOARD", [(c, 0, 0.0), (W_BOARD - c, 0, math.tan(math.pi / 8)), (W_BOARD, c, 0.0),
                           (W_BOARD, H_BOARD - c, math.tan(math.pi / 8)), (W_BOARD - c, H_BOARD, 0.0),
                           (c, H_BOARD, math.tan(math.pi / 8)), (0, H_BOARD - c, 0.0), (0, c, math.tan(math.pi / 8))])
    d.g(0, "ENDSEC"); d.g(0, "EOF")
    return d.text_of()


def stroked(scale, insunits):
    d = Dxf(scale)
    header(d, "AC1009", insunits)
    d.g(0, "SECTION"); d.g(2, "ENTITIES")
    for rings, _ in copper():
        for ring in rings:
            strokes(d, "COPPER_TOP", ring)
    strokes(d, "COPPER_BOTTOM", [(0, 0, 0.0), (W_BOARD, 0, 0.0), (W_BOARD, H_BOARD, 0.0), (0, H_BOARD, 0.0)])
    for k, (x, y) in enumerate(pour_vias()):
        kind = DRILL_KINDS[k % len(DRILL_KINDS)]
        mark = drill_marks(x, y, kind)
        if mark[0] == "circle":
            d.circle("DRILL", x, y, mark[1])
        else:
            strokes(d, "DRILL", mark)
    for name, cx, cy, along_x, (tx, ty) in PARTS:
        strokes(d, "SILK_TOP", body(cx, cy, along_x))
        d.text("SILK_TOP", tx * UM, ty * UM, TEXT_HEIGHT, name)
    c = 1.0 * UM
    strokes(d, "BOARD", [(c, 0, 0.0), (W_BOARD - c, 0, math.tan(math.pi / 8)), (W_BOARD, c, 0.0),
                         (W_BOARD, H_BOARD - c, math.tan(math.pi / 8)), (W_BOARD - c, H_BOARD, 0.0),
                         (c, H_BOARD, math.tan(math.pi / 8)), (0, H_BOARD - c, 0.0), (0, c, math.tan(math.pi / 8))])
    d.g(0, "ENDSEC"); d.g(0, "EOF")
    return d.text_of()


MM, INCH = 1.0, 1 / 25.4
DRAWINGS = {
    "board-r12.dxf": lambda: r12(MM, 4),
    "board-r2000.dxf": lambda: r2000(MM, 4),
    "board-inch.dxf": lambda: r2000(INCH, 1),
    "board-nounits.dxf": lambda: r12(INCH, None),
    "board-strokes.dxf": lambda: stroked(MM, 4),
    "board-mirror.dxf": lambda: r12(MM, 4, mirror=True),
}

if __name__ == "__main__":
    for name, make in DRAWINGS.items():
        with open(os.path.join(HERE, name), "w", newline="\n") as f:
            f.write(make())
        print(name)
