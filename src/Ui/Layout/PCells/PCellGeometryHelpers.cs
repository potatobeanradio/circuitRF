namespace CircuitRF.Ui.Layout.PCells;

/// <summary>Shared geometry construction helpers for the built-in microstrip PCell generators —
/// kept in one place so "how an arm rectangle is built" and "how junction arms are unioned into
/// one outline" (brief-L5a-pcell-contract-and-microstrip.md §3: "Junction geometry is a union, not
/// overlapping rectangles") are never reimplemented per component.</summary>
internal static class PCellGeometryHelpers
{
    /// <summary>
    /// How far a junction arm's drawn stub extends, as a multiple of that arm's own width, when the
    /// cell declares no explicit length for it. MBend still derives BOTH its arms this way; MTee and
    /// MCross now declare an explicit L per arm and fall back to this only for a cell authored
    /// before those parameters existed (see <see cref="ResolveArmLength"/>).
    ///
    /// <para><b>Why MTee/MCross stopped deriving it</b> (owner report, 2026-08-12): the derived rule
    /// makes an arm's LENGTH a function of its own WIDTH, so dragging W1's width gripper moved the
    /// junction — and therefore pins 2 and 3 — along the PERPENDICULAR axis. A width gripper that
    /// relocates the other end of the component reads as a bug however defensible the arithmetic is.
    /// An explicit L per arm makes width and length independent, which is what a layout editor's
    /// gripper vocabulary already implies.</para>
    ///
    /// <para>The stub is still ARTWORK, not an electrical length: <c>MicrostripTeeModel</c>/
    /// <c>MicrostripCrossModel</c> model the junction discontinuity and have no length term at all
    /// (the reference planes are at the arm edges). Real line length is a separate, user-placed
    /// MLIN — that was true of the derived stub too, and setting L longer does not change what is
    /// simulated.</para>
    /// </summary>
    internal const double StubLengthFactor = 2.5;

    /// <summary>
    /// A parameter the cell genuinely declares, or <c>null</c> when it does not carry that name at
    /// all. The distinction matters and <c>Real(name, 0.0)</c> cannot make it: absent means "derive
    /// the legacy way", while a declared value — including a negative one mid-drag — is the length the
    /// cell is currently being asked to draw.
    /// </summary>
    internal static double? Declared(IReadOnlyDictionary<string, PCellValue> p, string name)
        => p.TryGetValue(name, out var v) ? v.AsReal(0.0) : null;

    /// <summary>
    /// A junction arm's drawn length in DBU: the declared <c>L</c> when the cell carries one, else the
    /// legacy <see cref="StubLengthFactor"/>×width derivation. That fall-back is what keeps a cell
    /// authored before the L parameters existed byte-identical, so <c>PCellRegistry.GeneratorVersion</c>
    /// needs no bump and no placed instance is repointed.
    ///
    /// <para><b>Sign-transparent on purpose, which is not the same as unbounded.</b> A declared
    /// NEGATIVE length is drawn as asked here — the arm runs the other way, over the arm that belongs
    /// there. What stops a DRAG ever reaching that is the length handle's own <c>Min</c>, declared by
    /// each junction generator at exactly the crossing-width minimum its own clamp enforces, so the
    /// grip simply stops.</para>
    ///
    /// <para>The bound belongs on the HANDLE and not in here, and that distinction is the part worth
    /// remembering. <c>PCellHandleSolver</c> measures a grip's sensitivity ONCE, at the drag's starting
    /// value, and <c>Propose</c> clamps only the candidate derived from it — so the map it measured
    /// stays intact and the grip stops cleanly. A clamp in this method would instead flatten that map
    /// below the floor, leaving the solver nothing to measure and the grip refusing to follow the
    /// cursor at all. So the generator stays honest about what it was asked to draw, and the bound
    /// lives where it can be enforced without the geometry lying about it.</para>
    /// </summary>
    internal static long ResolveArmLength(double? lengthMeters, long widthDbu, int dbuPerMicron)
        => lengthMeters is { } l
            ? PCellUnits.MetresToDbu(l, dbuPerMicron)
            : (long)Math.Round(StubLengthFactor * widthDbu, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Holds an explicitly-declared arm length at or above <paramref name="minimumDbu"/>, reporting
    /// when it had to. Below that the crossing arm overhangs this arm's own end cap — drawable, but
    /// not a junction anyone drew on purpose.
    ///
    /// <para>Only ever applied to an EXPLICIT, already-POSITIVE length. Clamping the derived
    /// <see cref="StubLengthFactor"/> path too would change the geometry of cells authored before the
    /// L parameters existed whenever one arm is far wider than another, which is exactly what the
    /// "no <c>GeneratorVersion</c> bump" claim rests on not happening. A NEGATIVE length is left
    /// alone here too — a drag cannot produce one (the length handle's own <c>Min</c> is this very
    /// minimum, so the two agree to the last DBU and the grip stops exactly where the geometry stops
    /// changing), so the only ways to reach it are a hand-typed number, a script or an older file,
    /// where this editor's standing rule is to report a bad parameter rather than forbid one.</para>
    /// </summary>
    internal static long ClampArmLength(
        long lengthDbu, long minimumDbu, string generatorId, string paramName, string crossingWidthName,
        List<string> diagnostics)
    {
        if (minimumDbu <= 0 || lengthDbu >= minimumDbu) return lengthDbu;
        diagnostics.Add(
            $"{generatorId}: {paramName} is shorter than half of {crossingWidthName} and was clamped — " +
            $"below that the crossing arm overhangs this arm's own end.");
        return minimumDbu;
    }

    /// <summary>
    /// Builds a straight rectangular arm of <paramref name="widthDbu"/> running from
    /// <paramref name="originX"/>,<paramref name="originY"/> for <paramref name="lengthDbu"/> in
    /// direction <paramref name="directionDeg"/> (0 = +X, 90 = +Y, CCW-positive — same sense as
    /// <c>LayoutArc</c>'s own convention). Axis-aligned cases return a <see cref="RectShape"/>
    /// (cheaper, and the common case); any other angle returns a 4-vertex <see cref="PolygonShape"/>.
    /// </summary>
    internal static LayoutShape BuildArmRect(long originX, long originY, double directionDeg,
        long lengthDbu, long widthDbu, LayerKey layer)
    {
        double rad = directionDeg * Math.PI / 180.0;
        double dx = Math.Cos(rad), dy = Math.Sin(rad);

        // Snap near-axis directions exactly (avoids float-noise producing a 4-vertex polygon for
        // what is visually and electrically an axis-aligned arm).
        bool alongX = Math.Abs(dy) < 1e-9;
        bool alongY = Math.Abs(dx) < 1e-9;
        long halfW = widthDbu / 2;

        if (alongX)
        {
            long x2 = originX + (long)Math.Round(dx * lengthDbu, MidpointRounding.AwayFromZero);
            long xMin = Math.Min(originX, x2), xMax = Math.Max(originX, x2);
            return new RectShape { Layer = layer, X1 = xMin, Y1 = originY - halfW, X2 = xMax, Y2 = originY + halfW };
        }
        if (alongY)
        {
            long y2 = originY + (long)Math.Round(dy * lengthDbu, MidpointRounding.AwayFromZero);
            long yMin = Math.Min(originY, y2), yMax = Math.Max(originY, y2);
            return new RectShape { Layer = layer, X1 = originX - halfW, Y1 = yMin, X2 = originX + halfW, Y2 = yMax };
        }

        double nx = -dy, ny = dx; // left-hand normal, unit length since (dx,dy) is unit length
        double endX = originX + dx * lengthDbu, endY = originY + dy * lengthDbu;

        long[] xy =
        [
            Round(originX + nx * halfW), Round(originY + ny * halfW),
            Round(endX + nx * halfW),    Round(endY + ny * halfW),
            Round(endX - nx * halfW),    Round(endY - ny * halfW),
            Round(originX - nx * halfW), Round(originY - ny * halfW),
        ];
        return new PolygonShape { Layer = layer, Xy = xy };
    }

    /// <summary>Unions a set of arm shapes into ONE outline (Clipper2, via <see cref="LayoutBooleans.Union"/>)
    /// — never overlapping rects, per the brief's own explicit instruction. All operands must
    /// already carry the resolved <paramref name="layer"/>. Returns the single merged outline
    /// (a <see cref="PolygonShape"/> or <see cref="CurveShape"/> — Clipper2's own output shape),
    /// or the sole operand unchanged if only one was given (union of one is a no-op, but still goes
    /// through the same call path so the layer/holes normalization stays uniform).</summary>
    internal static LayoutShape UnionArms(IReadOnlyList<LayoutShape> arms, LayerKey layer, Technology? tech)
    {
        if (arms.Count == 1) return arms[0];
        var result = LayoutBooleans.Union(arms, tech);
        // A well-formed set of overlapping/touching arms unions to exactly one region; if geometry
        // ever produced more than one (arms that don't actually touch — a caller bug, not a user
        // input this phase exposes), keep the largest by bounding-box area rather than silently
        // dropping data.
        if (result.Shapes.Count <= 1)
            return result.Shapes.Count == 1 ? result.Shapes[0] : arms[0];

        return result.Shapes
            .OrderByDescending(s =>
            {
                var bb = LayoutGeometry.BboxOf(s);
                return (bb.MaxX - bb.MinX) * (double)(bb.MaxY - bb.MinY);
            })
            .First();
    }

    private static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);
}
