// L8b — the neutral PLANAR problem the surface mesher consumes.
//
// D1: this is a SIBLING of EmProblem, not a subtype and not an extension of it. EmProblem is a
// CROSS-SECTION model — conductor outlines in the (x, y) plane of a slice, horizontal dielectric
// slabs, one ground plane — and it cannot describe a planar layout. Nothing here derives from it,
// shares a base with it, or implements an interface it also implements: two things that are
// genuinely different, described by two types, is the cheapest arrangement to be correct in.
//
// R-mom-1 applies unchanged: metres, siemens/metre, hertz, doubles. This type knows nothing about
// DBU, .clay shapes, LayerKey or Technology — the Ui-side PlanarExtractor produces it, exactly as
// CrossSectionExtractor produces an EmProblem.

namespace CircuitRF.Engine.Mom;

/// <summary>
/// A closed region in the layout plane: one outer ring plus zero or more holes. Rings are implicitly
/// closed (the first vertex is never repeated at the end) and carry no winding requirement —
/// containment is answered by an even–odd ray cast, which is winding-agnostic.
/// </summary>
public sealed record PlanarPolygon(
    IReadOnlyList<EmPoint>                 Outer,
    IReadOnlyList<IReadOnlyList<EmPoint>>? Holes = null)
{
    public IReadOnlyList<IReadOnlyList<EmPoint>> HoleRings => Holes ?? [];

    /// <summary>Axis-aligned bounds, metres.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var p in Outer)
        {
            if (p.X < x0) x0 = p.X;
            if (p.Y < y0) y0 = p.Y;
            if (p.X > x1) x1 = p.X;
            if (p.Y > y1) y1 = p.Y;
        }
        return (x0, y0, x1, y1);
    }

    /// <summary>Enclosed area (outer minus holes), metres². Always ≥ 0.</summary>
    public double Area()
    {
        double a = Math.Abs(RingArea(Outer));
        foreach (var h in HoleRings) a -= Math.Abs(RingArea(h));
        return Math.Max(a, 0);
    }

    /// <summary>Even–odd containment: inside the outer ring and outside every hole.</summary>
    public bool Contains(double x, double y)
    {
        if (!RingContains(Outer, x, y)) return false;
        foreach (var h in HoleRings)
            if (RingContains(h, x, y)) return false;
        return true;
    }

    internal static double RingArea(IReadOnlyList<EmPoint> ring)
    {
        double s = 0;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
            s += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
        return 0.5 * s;
    }

    /// <summary>
    /// Standard even–odd crossing test. The half-open <c>&lt;=</c>/<c>&gt;</c> comparison on the y
    /// interval is what makes a vertex lying exactly on the scan line count once rather than twice —
    /// a Manhattan layout puts a great many vertices on gridlines, so this is not a rare case.
    /// </summary>
    internal static bool RingContains(IReadOnlyList<EmPoint> ring, double x, double y)
    {
        bool inside = false;
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double yi = ring[i].Y, yj = ring[j].Y;
            if (yi > y != yj > y)
            {
                double t = (y - yi) / (yj - yi);
                if (x < ring[i].X + t * (ring[j].X - ring[i].X)) inside = !inside;
            }
        }
        return inside;
    }
}

/// <summary>
/// One conductor level's artwork. <paramref name="ThicknessM"/> is carried for L8c's loss model and
/// for reporting; the surface mesher itself is a <b>sheet</b> mesher and never uses it — a level sits
/// at a single z with no extent in the Green's function.
/// </summary>
/// <param name="ZM">
/// <b>L9c/D6 — the level's height, and it is OPTIONAL with a defaulting rule rather than required.</b>
/// <see cref="double.NaN"/> means "the slab's top surface", which is what L8's D2 fixed and what the
/// Ui-side <c>PlanarExtractor</c> still produces. That choice is deliberate and is D6's answer:
/// making it required would break the extractor at compile time and hand L9d an error instead of a
/// design, and this file is behind the firewall so this slice may not go and fix it. A level whose
/// ZM is set must lie on an interface of <see cref="PlanarProblem.EffectiveStack"/> — see
/// <see cref="PlanarProblem.CanSolve"/>.
/// </param>
public sealed record PlanarConductorLayer(
    string                        Name,
    IReadOnlyList<PlanarPolygon>  Polygons,
    double                        SigmaSm,
    double                        ThicknessM,
    double                        ZM = double.NaN);

/// <summary>
/// <b>L9c — a via: the metal that carries current from one conductor level to another.</b>
///
/// <para><b>Its footprint is artwork, exactly like a conductor level's</b>, and the mesher resolves
/// it onto the SAME tensor grid every level shares — which is why L8b's D8 chose one shared grid in
/// the first place ("L9's multi-level stack needs vertical current to cross between them, and a
/// per-layer grid would make that a re-mesh rather than an addition"). Every cell of that grid which
/// is covered by the footprint AND carries metal on both levels becomes one vertical basis function.
/// There is no separate via mesh and no conformality question.</para>
/// </summary>
/// <param name="LowerLayerIndex">Index into <see cref="PlanarProblem.Layers"/> of the level the via
/// starts from. Must be the LOWER of the two — the pair is ordered, which is what makes the sign of
/// the divergence pulse a contract rather than an observation (R-via-5).
/// <b><see cref="GroundTerminal"/> instead means the GROUND PLANE</b> — see that constant.</param>
/// <param name="UpperLayerIndex">…and the level it lands on.</param>
/// <param name="Polygons">The via's footprint in the layout plane, metres.</param>
/// <param name="SigmaSm">Conductivity, for the loss model. Not used by the sheet mesher.</param>
public sealed record PlanarVia(
    int                          LowerLayerIndex,
    int                          UpperLayerIndex,
    IReadOnlyList<PlanarPolygon> Polygons,
    double                       SigmaSm)
{
    /// <summary>
    /// <b><see cref="LowerLayerIndex"/> = −1 means "the ground plane"</b>: the laterally infinite PEC
    /// the Green's function terminates on, which is never a meshed level and therefore has no layer
    /// index of its own. This is what a BACKSIDE via is, and on a MMIC it is how a source terminal
    /// reaches ground — the commonest via there is.
    ///
    /// <para>It is a sentinel rather than a separate record because everything else about the via is
    /// unchanged: the same footprint artwork on the same shared tensor grid, the same uniform 1/Area
    /// vertical weight. What differs is one basis (an attachment, one meshed foot — see
    /// <c>PlanarBasisFunctions</c>' header) and one span (<c>PlanarLevels.GroundZ</c> upward).</para>
    ///
    /// <para><b>Only legitimate when the stack's bottom termination genuinely IS that PEC</b>;
    /// <see cref="PlanarProblem.CanSolve"/> refuses it by name otherwise.</para>
    /// </summary>
    public const int GroundTerminal = -1;

    /// <summary>True when this via's lower terminal is the ground plane (<see cref="GroundTerminal"/>).</summary>
    public bool ToGround => LowerLayerIndex == GroundTerminal;
}

/// <summary>
/// R-msh-8a: a note that some part of the analysed geometry already has a validated closed-form
/// model, so the user can choose the full-wave answer <i>knowing</i> the cheap one exists. This is
/// the R-mom-17 shape applied to a COST rather than to a capability — name the thing, name the
/// alternative — and it is never a refusal: a user may legitimately want the full-wave answer, to
/// check a taper's own radiation or its interaction with a neighbour.
///
/// <para>Produced by the Ui-side extractor (only it knows what a PCell is) and carried through as
/// neutral text, so this file stays free of any layout concept.</para>
/// </summary>
public sealed record PlanarAnalyticAlternative(string Subject, string ModelName, string Reason);

/// <summary>
/// The neutral planar EM problem: conductor artwork in the layout plane, on the top surface of one
/// grounded dielectric slab (L8a's D2). <b>Not an <see cref="EmProblem"/> and not derived from
/// one</b> — see the file header.
/// </summary>
/// <param name="Layers">Conductor levels. D2 permits exactly one; the type carries a list so L9's
/// multi-level stack is a capability change rather than a type change, and so the mesher's ordering
/// contract (R-msh-2) is stated over a layer index from day one.</param>
/// <param name="Slab">The grounded slab, exactly as L8a's Green's function sees it.</param>
/// <param name="MaxFrequencyHz">
/// R-msh-3/D4: the <b>highest</b> frequency of the sweep, and the only thing about the sweep the
/// mesher is told. The mesh is frequency-dependent but computed ONCE per sweep, not once per point —
/// the Green's function is the genuinely per-frequency thing (L8a's R-lgf-5) and that must not leak
/// in here. Zero or negative means "no wavelength cap", i.e. a geometry-only mesh.
/// </param>
/// <param name="AnalyticAlternatives">R-msh-8a, may be empty.</param>
/// <param name="MediumStack">
/// <b>L9c/D6 — the general stratified medium, OPTIONAL, defaulting to the one-slab expression of
/// <paramref name="Slab"/>.</b> <see cref="GroundedSlab"/> is not deleted in favour of it: L9a's D5
/// precedent applies ("do not delete it in favour of the general type; gate the general path against
/// it and let collapsing them be a later, separate, measured decision"), and keeping it optional is
/// what lets the Ui-side extractor go on producing the old shape until L9d adapts it.
/// </param>
/// <param name="Vias">L9c — vertical connections between levels, may be empty.</param>
public sealed record PlanarProblem(
    IReadOnlyList<PlanarConductorLayer>       Layers,
    GroundedSlab                              Slab,
    double                                    MaxFrequencyHz,
    IReadOnlyList<PlanarAnalyticAlternative>? AnalyticAlternatives = null,
    LayerStack?                               MediumStack = null,
    IReadOnlyList<PlanarVia>?                 Vias = null)
{
    public IReadOnlyList<PlanarAnalyticAlternative> Alternatives => AnalyticAlternatives ?? [];
    public IReadOnlyList<PlanarVia> ViaList => Vias ?? [];

    /// <summary>The medium this problem actually sits in: <see cref="MediumStack"/> if given, else
    /// the one-slab stack <paramref name="Slab"/> describes. Built fresh each call; it is a value.</summary>
    public LayerStack EffectiveStack => MediumStack ?? LayerStack.FromGroundedSlab(Slab);

    /// <summary>The z of conductor level <paramref name="layerIndex"/> — its own
    /// <see cref="PlanarConductorLayer.ZM"/>, or the stack's top surface when unset (L8's D2).</summary>
    public double LevelZ(int layerIndex)
    {
        double z = Layers[layerIndex].ZM;
        return double.IsNaN(z) ? EffectiveStack.TopZ : z;
    }

    /// <summary>
    /// <b>L9d/M1 — whether this problem needs L9's general kernel rather than L8's shipped one, and
    /// it is the single place that decision is made.</b>
    ///
    /// <para>R-mlp-1 requires the one-level path to stay BIT-IDENTICAL, and the only way to promise
    /// that is to keep it on exactly the code L8d shipped — <see cref="PlanarKernelPair"/> over
    /// <c>Dcim.Fit</c> and <c>PlanarFill.Fill</c>. So a problem that is a single level on a single
    /// grounded slab, with no via and no explicitly-given <see cref="MediumStack"/>, takes that path
    /// unchanged; anything else takes the general one. <b>An explicit <see cref="MediumStack"/>
    /// counts even at one level</b>, because <see cref="Slab"/> may not describe it and silently
    /// solving the slab instead would be the plausible-wrong-answer failure this whole phase is
    /// built to avoid.</para>
    /// </summary>
    public bool RequiresGeneralKernel =>
        MediumStack is not null || Layers.Count > 1 || ViaList.Count > 0;

    /// <summary>
    /// <b>Whether conductor level <paramref name="layerIndex"/> sits exactly on the top surface of
    /// <see cref="Slab"/></b> — the one configuration for which L8d's quasi-static C_pul route (an
    /// electrostatic image series over a grounded slab, <c>PlanarKernelTerms.StaticScalar</c>) is the
    /// right electrostatic problem for a line on that level. See <see cref="PlanarDeembed"/> for the
    /// refusal this gates.
    /// </summary>
    public bool LevelIsOnSlabTop(int layerIndex)
        => Math.Abs(LevelZ(layerIndex) - Slab.HeightM) <= 1e-12 * Math.Max(1.0, Slab.HeightM);

    /// <summary>
    /// λ_g at <see cref="MaxFrequencyHz"/>, <b>in the fastest-slowing medium anywhere in the stack</b>
    /// (R-msh-3). Not ε_eff and not free space: it is the shortest wavelength any part of the
    /// structure can see, so it is the conservative (finer-mesh) direction, and it is the only one
    /// available before a solve. Getting this wrong is a factor of √εᵣ — 2.10 on FR-4, 3.59 on GaAs —
    /// in cell count <i>and</i> in accuracy, in opposite directions.
    ///
    /// <para><b>L9c — with N dielectrics there is no single εᵣ, and R-msh-3's own rule says which one
    /// to take: the maximum of εᵣµᵣ over every REGION, terminations included.</b> On a one-slab
    /// problem that maximum is the slab's own εᵣ (air above and, for a PEC floor, a nominal air
    /// below), so this is bit-identical to what L8b shipped — the generalisation costs nothing on the
    /// case that already worked, which is how it should be checked.</para>
    /// </summary>
    public double GuidedWavelengthM
    {
        get
        {
            if (!(MaxFrequencyHz > 0)) return double.PositiveInfinity;
            var stack = EffectiveStack;
            double worst = 1.0;
            for (int r = 0; r < stack.RegionCount; r++)
            {
                var m = stack.MaterialOfRegion(r);
                worst = Math.Max(worst, m.EpsR * m.MuR);
            }
            return EmConstants.C0 / (MaxFrequencyHz * Math.Sqrt(worst));
        }
    }

    /// <summary>
    /// <b>R-mom-17 / R-via-6 — what this problem type can and cannot describe, refused by name.</b>
    /// Every refusal here is EARNED: each names a configuration that is genuinely representable in
    /// the type but that no part of the engine can answer, and each says where it would arrive.
    /// </summary>
    public EmSuitability CanSolve()
    {
        var stack = EffectiveStack;

        for (int i = 0; i < Layers.Count; i++)
        {
            double z = LevelZ(i);
            bool onInterface = false;
            foreach (double zi in stack.InterfaceZ)
                if (Math.Abs(z - zi) <= 1e-12 * Math.Max(1.0, stack.TopZ)) onInterface = true;
            if (!onInterface)
                return EmSuitability.No(
                    $"Conductor level '{Layers[i].Name}' sits at z = {z:G6} m, which is not an " +
                    $"interface of the medium ({stack}). A level BURIED inside a dielectric is " +
                    $"representable in the Green's function — LayeredSpectralGreens takes any height " +
                    $"— but the mesher's shared tensor grid and the via basis both assume a level is " +
                    $"an interface, so a floating sheet would silently get a via footprint it cannot " +
                    $"attach to. Place the level on an interface, or split the layer so that it is one.");
        }

        for (int i = 1; i < Layers.Count; i++)
            if (!(LevelZ(i) > LevelZ(i - 1)))
                return EmSuitability.No(
                    $"Conductor levels must be ordered strictly BOTTOM-TO-TOP: level {i - 1} " +
                    $"('{Layers[i - 1].Name}') is at z = {LevelZ(i - 1):G6} m and level {i} " +
                    $"('{Layers[i].Name}') at z = {LevelZ(i):G6} m. The mesh's cell order is " +
                    $"(LayerIndex, IY, IX) and a via's basis is signed by which of its two levels is " +
                    $"the lower one (R-msh-2, R-via-5); both are contracts everything downstream " +
                    $"indexes by, and neither survives an unordered level list.");

        foreach (var v in ViaList)
        {
            // ── The GROUND-ATTACHMENT form, and its one earned refusal ────────────────────────
            //
            // A via naming PlanarVia.GroundTerminal claims its lower terminal is the PEC the Green's
            // function terminates on. That is only true when the stack actually terminates in one:
            // on an open-below or PMC stack the same drawing means something else entirely, and
            // accepting it would produce a complete, plausible, wrong answer for a structure nobody
            // drew — the failure mode L9's own phase-gate finding is about.
            if (v.ToGround)
            {
                if (stack.Bottom.Kind != TerminationKind.Pec)
                    return EmSuitability.No(
                        $"A via runs to the GROUND PLANE, but this medium's bottom termination is " +
                        $"{stack.Bottom} rather than a PEC. The ground-attachment basis is a half " +
                        $"rooftop whose lower terminal is the laterally infinite PERFECT CONDUCTOR " +
                        $"the Green's function handles analytically — its return charge is that " +
                        $"plane's own image, and there is no image to be the return without it. " +
                        $"Terminate the stack in a ground plane, or take the via to a meshed " +
                        $"conductor level instead.");

                if (v.UpperLayerIndex < 0 || v.UpperLayerIndex >= Layers.Count)
                    return EmSuitability.No(
                        $"A ground via lands on level {v.UpperLayerIndex}, which is not inside " +
                        $"0..{Layers.Count - 1}.");
                continue;
            }

            if (v.LowerLayerIndex < 0 || v.UpperLayerIndex >= Layers.Count ||
                v.LowerLayerIndex >= v.UpperLayerIndex)
                return EmSuitability.No(
                    $"A via connects levels {v.LowerLayerIndex} → {v.UpperLayerIndex}, which is not " +
                    $"an ordered pair inside 0..{Layers.Count - 1}.");
            if (v.UpperLayerIndex != v.LowerLayerIndex + 1)
                return EmSuitability.No(
                    $"A via connects levels {v.LowerLayerIndex} → {v.UpperLayerIndex}, skipping " +
                    $"{v.UpperLayerIndex - v.LowerLayerIndex - 1} level(s) in between. The vertical " +
                    $"basis pairs a cell on one level with the cell directly above it, so a via must " +
                    $"connect ADJACENT levels; a stacked via is a chain of them and should be given " +
                    $"as one PlanarVia per gap.");
        }

        return EmSuitability.Yes;
    }

    /// <summary>Union bounds of every polygon on every layer, metres. Empty geometry gives an
    /// inverted (infinite) box, which the mesher checks for.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
        double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
        foreach (var l in Layers)
            foreach (var p in l.Polygons)
            {
                var (a, b, c, d) = p.Bounds();
                if (a < x0) x0 = a;
                if (b < y0) y0 = b;
                if (c > x1) x1 = c;
                if (d > y1) y1 = d;
            }
        return (x0, y0, x1, y1);
    }

    public int PolygonCount
    {
        get { int n = 0; foreach (var l in Layers) n += l.Polygons.Count; return n; }
    }
}
