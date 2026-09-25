// brief-em3d-4 — bond wires in the 3D problem: a swept section per wire, a foot at each wedge end, a
// ball at each ball end, and the assembly loop height (docs/design/em-3d.md §6.6).
//
// KERNEL W IS NOT TOUCHED. The foot, the ball, the neck and the cross-section exist only here; every
// wire kernel W solves today it solves identically afterwards (gate 7 holds that).
//
// ── What is read, and how ─────────────────────────────────────────────────────────────────────
//
//   * The polyline is the AXIS (§6.6), in the wire model's own z convention: z = 0 is the top of
//     the lowest ground-reference conductor (WBondLayerHeights states it; the method of images
//     reflects in that plane). Plan coordinates are the layout's, nanometres to metres.
//   * A wire end's pad is the conductor piece of THIS problem under the end's plan position — the
//     generator's own merged pieces, so the foot lands on exactly the object the mesher will see —
//     choosing the HIGHEST top surface where pieces stack: a bond lands on exposed metal. It is the
//     any-layer question LVS already asks of a foot (AssemblyRead.Emit), because a foot carries a
//     height, not a layer. A sheet's top is its sheet plane, so a foot shares that surface.
//   * The rings are decided here, once: "up" held at world +z (the section's across-axis stays
//     horizontal, so its bottom face is level along the whole wire), the near-vertical rule, the
//     mitre at every interior vertex, and a foot's bottom face ASSIGNED its pad's top z — never
//     computed as axis minus half a height, which would not round-trip bitwise (R-em3d4-4d).
//
// ── What changes relative to the .wBond polyline, and only this ──────────────────────────────
//
//   * A wedge end's point moves in z to its foot's axis (pad top + half the section's height), so
//     the foot lies on the pad; its plan position is untouched, the foot is added BEYOND it, and a
//     note says how far the point moved.
//   * A ball end's point is replaced by the centre of the ball's top face; when the path does not
//     leave it vertically, a vertical neck rises from there first (R-em3d4-4b).

using System.Globalization;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using BondStyle = CircuitRF.WBond.BondStyle;
using Wire = CircuitRF.WBond.Wire;
using WireArray = CircuitRF.WBond.WireArray;
using WireCrossSection = CircuitRF.WBond.WireCrossSection;
using WireMaterial = CircuitRF.WBond.WireMaterial;
using WBondDesign = CircuitRF.WBond.WBondDesign;
using WBondCell = CircuitRF.Design.Layout.WBondCell;
using WBondIo = CircuitRF.WBond.WBondIo;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>One end of a generated wire, as the 3D model built it.</summary>
/// <param name="Pad">The name of the conductor piece the end is bonded to.</param>
/// <param name="PadTopM">That piece's top surface, metres.</param>
/// <param name="Neck">True when a vertical neck was inserted above a ball.</param>
/// <param name="FootLengthM">The foot's length, for a wedge end; null for a ball.</param>
/// <param name="OverhangM">How far the foot runs past its pad's edge; zero when it does not.</param>
public sealed record Em3dWireEnd(BondStyle Style, string Pad, double PadTopM, bool Neck, double? FootLengthM,
                                 double OverhangM);

/// <summary>
/// What the 3D model made of one wire — the per-wire report <c>explain</c> shows (brief 5), carrying
/// both loop heights side by side because a user holding an assembly specification and a user holding
/// the <c>.wBond</c> each look for their own number (R-em3d4-5b).
/// </summary>
/// <param name="Name">The swept solid's name, <c>wire/&lt;array&gt;/&lt;member&gt;</c>.</param>
/// <param name="AssemblyLoopHeightM">§6.6's definition: the wire's top surface at its apex minus the
/// top surface of the LOWER of its two pads, from the generated solid.</param>
/// <param name="WBondLoopHeightM">wBond's own: the axis polyline's maximum minus minimum z.</param>
public sealed record Em3dWireReport(
    string                Name,
    string                Array,
    int                   Member,
    string                Material,
    Em3dSection           Section,
    double                DiameterM,
    Em3dSectionSize       Size,
    Em3dWireEnd           Start,
    Em3dWireEnd           End,
    WireBondProcessValues Process,
    double                AssemblyLoopHeightM,
    double                WBondLoopHeightM);

/// <summary>The wires a 3D problem includes, and where they came from.</summary>
public sealed record Em3dWireSource(WBondDesign Design, WireBondWorkspace Workspace, string? Path)
{
    /// <summary>
    /// The <c>.wBond</c> stem-paired with a layout (WB40, <see cref="WBondCell.Resolve"/>), or null
    /// when it has none. A file that cannot be read is a REFUSAL here, not the silent absence the
    /// layout editor reports it as: a 3D model without the wires solves a different circuit.
    /// </summary>
    public static Em3dWireSource? ForLayout(string? absClayPath, out string? note, out string? refusal)
    {
        refusal = null;
        var found = WBondCell.Resolve(absClayPath);
        note = found.Note;
        if (found.Path is not { } path) return null;
        try
        {
            return new Em3dWireSource(WBondIo.ReadFile(path), WireBondWorkspace.ForFile(path), path);
        }
        catch (Exception ex)
        {
            refusal = $"The wires attached to this layout ('{System.IO.Path.GetFileName(path)}') could not be " +
                      $"read: {ex.Message} A 3D model without them would solve a different circuit, so it is " +
                      "not built.";
            return null;
        }
    }
}

/// <summary>A conductor piece a wire can land on: its name, its outline (metres) and its top.</summary>
internal sealed record Em3dWirePad(string Name, PlanarPolygon Poly, double TopM);

/// <summary>What <see cref="Em3dWires.Build"/> produced, in construction order.</summary>
internal sealed class Em3dWireBuild
{
    public List<(string Name, string Material, Em3dPrimitive Primitive)> Solids { get; } = [];
    public List<Em3dWireReport> Reports { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Warnings { get; } = [];
    public string? Refusal { get; set; }

    /// <summary>Wire metals stating σ₂₀ but no α₂₀ (brief-em3d-5's temperature rows).</summary>
    public SortedSet<string> NoAlpha { get; } = new(StringComparer.Ordinal);
}

public static class Em3dWires
{
    /// <summary>
    /// <b>R-em3d4-2c — a segment within this many degrees of vertical does not take its roll from
    /// "up"</b>, which degenerates there (up × tangent → 0); it carries the previous segment's
    /// across-axis instead. Also the tolerance within which a ball end counts as ARRIVING vertically
    /// (R-em3d4-4b). Five degrees keeps the cross product well conditioned and is far steeper than any
    /// loop's own flank.
    /// </summary>
    public const double NearVerticalDegrees = 5.0;

    /// <summary>
    /// The sharpest turn a mitred sweep is built through. Past it the mitre plane is so oblique that
    /// the rings either side of it overlap and the solid intersects itself, so the wire is refused by
    /// name rather than handed to a mesher to fail on.
    /// </summary>
    public const double MaxTurnDegrees = 150.0;

    /// <summary>
    /// The least height of a neck inserted above a ball, in wire diameters, when the path's own end
    /// point is below that. One diameter: enough for the section to exist as a vertical prism before
    /// it turns. <b>Provisional</b>, like the ball's own dimensions.
    /// </summary>
    public const double NeckMinimumDiameters = 1.0;

    private static readonly double CosNearVertical = Math.Cos(NearVerticalDegrees * Math.PI / 180);
    private static readonly double CosMaxTurn      = Math.Cos(MaxTurnDegrees * Math.PI / 180);

    /// <summary>
    /// R-em3d4-5a — the assembly loop height, from the generated solid: the highest point of the
    /// swept section (its top surface at the apex) minus the top of the lower pad.
    /// </summary>
    public static double AssemblyLoopHeight(Em3dSweep sweep, double lowerPadTopM)
    {
        double top = double.NegativeInfinity;
        foreach (var ring in sweep.Rings)
            foreach (var q in ring)
                top = Math.Max(top, q.Z);
        return top - lowerPadTopM;
    }

    // ── The build ────────────────────────────────────────────────────────────────────────────

    internal static Em3dWireBuild Build(Em3dWireSource source, IReadOnlyList<Em3dWirePad> pads, double zOriginM,
                                        Technology tech, double tempC, Func<Em3dMaterial, bool, string> addMaterial)
    {
        var build  = new Em3dWireBuild();
        var design = source.Design;
        try { design.Validate(); }
        catch (InvalidOperationException ex) { build.Refusal = ex.Message; return build; }

        var materialNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var noAlpha       = build.NoAlpha;
        var builtInFoot   = new SortedSet<long>();
        var builtInBallD  = new SortedSet<long>();
        var builtInBallH  = new SortedSet<long>();
        var moved = new EndMoves();
        WireBondProcessValues? anyProcess = null;

        for (int a = 0; a < design.Arrays.Count; a++)
        {
            var array = design.Arrays[a];
            for (int k = 0; k < array.Wires.Count; k++)
            {
                var wire = array.Wires[k];
                string name = $"wire/{array.Name}/{k + 1}";

                // ── Metal (R-em3d4-6) ──────────────────────────────────────────────────────────
                if (!materialNames.TryGetValue(wire.Material, out string? material))
                {
                    material = ResolveMetal(name, wire.Material, design, tech, tempC, addMaterial, noAlpha,
                                            build, out string? refusal);
                    if (refusal is not null) { build.Refusal = refusal; return build; }
                    materialNames[wire.Material] = material!;
                }

                var process = WireBondProcess.Resolve(wire, array, design, source.Workspace);
                anyProcess ??= process;
                var made = One(name, wire, process, pads, zOriginM, build, moved);
                if (build.Refusal is not null) return build;

                var (sweep, balls, report) = made!.Value;
                build.Solids.Add((name, material!, sweep));
                foreach (var (ballName, ball) in balls) build.Solids.Add((ballName, material!, ball));
                build.Reports.Add(report with { Array = array.Name, Member = k + 1, Material = material! });

                bool hasWedge = report.Start.Style == BondStyle.Wedge || report.End.Style == BondStyle.Wedge;
                bool hasBall  = report.Start.Style == BondStyle.Ball  || report.End.Style == BondStyle.Ball;
                if (hasWedge && process.FootLength.Source == WireBondValueSource.BuiltIn) builtInFoot.Add(process.FootLength.Nm);
                if (hasBall)
                {
                    if (process.BallDiameter.Source == WireBondValueSource.BuiltIn) builtInBallD.Add(process.BallDiameter.Nm);
                    if (process.BallHeight.Source == WireBondValueSource.BuiltIn) builtInBallH.Add(process.BallHeight.Nm);
                }
            }
        }

        // ── Notes: what was resolved, and every guess named (R-em3d4-3b) ─────────────────────────
        if (anyProcess is { } p0)
        {
            foreach (string d in p0.AssemblyRules.Diagnostics) build.Notes.Add(d);
            string where = p0.AssemblyRules.ResolvedPath is { } path
                ? $"the assembly rules '{System.IO.Path.GetFileName(path)}'"
                : "the assembly rules (none resolve: name a .wasm with the .wBond's AssemblyRef or the " +
                  "workspace's DefaultAssemblyRef)";
            void Guess(SortedSet<long> values, string what, double diameters, string field, string extra)
            {
                if (values.Count == 0) return;
                string value = values.Count == 1 ? $" = {Um(values.Min / 1000.0)} µm" : "";
                build.Notes.Add($"No {what} is stated{extra} or in {where}, so the built-in starting value " +
                                $"{diameters:G}·d{value} was used. It is a first guess, not assembly data: set " +
                                $"{field} in the .wasm{(field.Contains("Foot") ? " (or FootLengthNm on the array or the wire)" : "")}.");
            }
            Guess(builtInFoot, "wedge-foot length", WireBondProcess.BuiltInFootLengthDiameters,
                  "DefaultFootLengthNm", " on the wire or its array");
            Guess(builtInBallD, "ball diameter", WireBondProcess.BuiltInBallDiameterDiameters,
                  "DefaultBallDiameterNm", "");
            Guess(builtInBallH, "ball height", WireBondProcess.BuiltInBallHeightDiameters,
                  "DefaultBallHeightNm", "");
        }
        if (moved.Count > 0)
            build.Notes.Add($"{moved.Count} wedge end(s) were moved in z, by at most {Um(moved.Max * 1e6)} µm, so each foot " +
                            "lies on its pad: a foot's axis is its pad's top plus half the section's height. The " +
                            "wires' plan geometry and the rest of their loops are as the .wBond states.");
        if (noAlpha.Count > 0)
            build.Notes.Add($"{string.Join(", ", noAlpha.Select(n => $"'{n}'"))} state" +
                            $"{(noAlpha.Count == 1 ? "s" : "")} no α₂₀, so the wires' σ₂₀ is used at {Um(tempC)} °C.");
        if (design.OperatingTempC != tempC)
            build.Notes.Add($"The .wBond states {Um(design.OperatingTempC)} °C, where kernel W evaluates its wires' " +
                            $"σ; this 3D setup evaluates every solid, wires included, at {Um(tempC)} °C. Check this " +
                            "first when the two disagree.");
        return build;
    }

    /// <summary>R-em3d4-6 — a wire's metal: the technology's Materials first, then the .wBond's own
    /// list. Never <see cref="WBondDesign.MaterialFor"/>, which falls back silently to gold.</summary>
    private static string? ResolveMetal(string wireName, string metal, WBondDesign design, Technology tech,
                                        double tempC, Func<Em3dMaterial, bool, string> addMaterial,
                                        SortedSet<string> noAlpha, Em3dWireBuild build, out string? refusal)
    {
        refusal = null;
        var own = design.Materials.FirstOrDefault(m => string.Equals(m.Name, metal, StringComparison.OrdinalIgnoreCase));

        if (tech.FindMaterial(metal) is { } tm)
        {
            if (tm.Sigma20 is not { } s20)
            {
                refusal = $"Wire {wireName} is made of '{metal}', which technology '{tech.Name}' defines with no " +
                          "conductivity (Sigma20), so it is not a metal. State its σ₂₀ in the technology's " +
                          "Materials, or set the wire to a metal.";
                return null;
            }
            if (own is not null && (!Same(own.Sigma20, s20) || tm.Alpha20 is not { } ta || !Same(own.Alpha20, ta)))
                build.Warnings.Add(
                    $"'{metal}' is defined differently by technology '{tech.Name}' (σ₂₀ {Sci(s20)} S/m, α₂₀ " +
                    $"{(tm.Alpha20 is { } a1 ? Sci(a1) : "not stated")}) and by the .wBond (σ₂₀ {Sci(own.Sigma20)} S/m, " +
                    $"α₂₀ {Sci(own.Alpha20)}). The 3D model uses the technology's values.");
            double sigma = s20;
            if (tm.Alpha20 is { } alpha) sigma = new WireMaterial(tm.Name, s20, alpha, 0).SigmaAt(tempC);
            else noAlpha.Add(tm.Name);
            return addMaterial(new Em3dMaterial(tm.Name, 1, null, 0, tm.Mur ?? 1, sigma), false);
        }

        if (own is not null)
            return addMaterial(new Em3dMaterial(own.Name, 1, null, 0, 1, own.SigmaAt(tempC)), true);

        var known = tech.Materials.Where(m => m.Sigma20 is not null).Select(m => m.Name)
                        .Concat(design.Materials.Select(m => m.Name))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        refusal = $"Wire {wireName} is made of '{metal}', which neither technology '{tech.Name}' nor the .wBond " +
                  $"defines. Known metals: {(known.Count == 0 ? "none" : string.Join(", ", known))}. A 3D model does " +
                  "not substitute a metal; set the wire's Material to one of them, or define it.";
        return null;
    }

    /// <summary>One wire: its sweep, its balls, and its report (array, member and material are filled
    /// in by the caller). Sets <see cref="Em3dWireBuild.Refusal"/> and returns null on refusal.</summary>
    private static (Em3dSweep Sweep, List<(string, Em3dPrimitive)> Balls, Em3dWireReport Report)? One(
        string name, Wire wire, WireBondProcessValues process, IReadOnlyList<Em3dWirePad> pads, double zOriginM,
        Em3dWireBuild build, EndMoves moved)
    {
        double d = wire.DiameterNm * 1e-9;
        var section = wire.CrossSection == WireCrossSection.Round ? Em3dSection.Circle : Em3dSection.Hexagon;
        var size = Em3dWireSection.Of(section, d);
        double h = size.Height;

        var pts = wire.Points.Select(q => new Point3(q.X * 1e-9, q.Y * 1e-9, zOriginM + q.Z * 1e-9)).ToList();
        int n = pts.Count;

        // ── The pads (R-em3d4-4c) ───────────────────────────────────────────────────────────────
        Em3dWirePad? PadUnder(Point3 q) =>
            pads.Where(p => p.Poly.Contains(q.X, q.Y)).OrderByDescending(p => p.TopM).FirstOrDefault();
        var startPad = PadUnder(pts[0]);
        var endPad   = PadUnder(pts[^1]);
        foreach (var (pad, end, q) in new[] { (startPad, "start", pts[0]), (endPad, "end", pts[^1]) })
            if (pad is null)
            {
                build.Refusal = $"Wire {name}'s {end} at ({Um(q.X * 1e6)}, {Um(q.Y * 1e6)}) µm is over no conductor " +
                                "in this problem, so there is no pad for it to be bonded to. A 3D model does not put " +
                                "a foot on nothing: move the end onto its pad, or draw the pad.";
                return null;
            }

        var path     = new List<Point3>();
        var footRing = new Dictionary<int, double>();   // ring index → the pad top its lowest vertices lie on
        var flatRing = new Dictionary<int, double>();   // ring index → the ball top the ring lies in
        var balls    = new List<(string, Em3dPrimitive)>();

        int Append(Point3 q)
        {
            if (path.Count > 0 && Dist(path[^1], q) <= 1e-12) return path.Count - 1;
            path.Add(q);
            return path.Count - 1;
        }

        Em3dWireEnd? EndOf(bool start, Em3dWirePad pad)
        {
            var style = (start ? wire.StartBond : wire.EndBond) ?? BondStyle.Wedge;
            var e     = start ? pts[0] : pts[^1];
            string which = start ? "start" : "end";

            if (style == BondStyle.Wedge)
            {
                // ── R-em3d4-4a — a foot, outward, along the plan direction of the end segment ────
                if (PlanDirection(pts, start) is not { } dir)
                {
                    build.Refusal = $"Wire {name} has no plan direction at its {which} — every segment is vertical — " +
                                    "so there is no direction for its foot to lie along.";
                    return null;
                }
                double footLen = process.FootLength.Nm * 1e-9;
                double za   = pad.TopM + h / 2;
                var near    = new Point3(e.X, e.Y, za);
                var far     = new Point3(e.X + footLen * dir.X, e.Y + footLen * dir.Y, za);
                double move = Math.Abs(za - e.Z);
                if (move > 1e-9) { moved.Count++; moved.Max = Math.Max(moved.Max, move); }

                if (start) { footRing[Append(far)] = pad.TopM; footRing[Append(near)] = pad.TopM; }
                else       { footRing[Append(near)] = pad.TopM; footRing[Append(far)] = pad.TopM; }

                double overhang = Overhang(pad.Poly, e.X, e.Y, dir.X, dir.Y, footLen);
                if (overhang > 1e-9)
                    build.Warnings.Add($"Wire {name}'s foot at its {which} overhangs '{pad.Name}' by {Um(overhang * 1e6)} " +
                                       $"µm: its {Um(footLen * 1e6)} µm foot runs past the pad's edge. It is modelled as " +
                                       "stated — a foot longer than its pad is an assembly defect worth seeing, not " +
                                       "a reason to refuse.");
                return new Em3dWireEnd(style, pad.Name, pad.TopM, false, footLen, overhang);
            }

            // ── R-em3d4-4b — a flattened ball, and a vertical neck unless the path arrives vertically ──
            double D = process.BallDiameter.Nm * 1e-9, H = process.BallHeight.Nm * 1e-9;
            if (!(H < D))
            {
                build.Refusal = $"Wire {name}'s ball is {Um(H * 1e6)} µm high and {Um(D * 1e6)} µm across. A flattened " +
                                "ball must be wider than it is high; check DefaultBallHeightNm and DefaultBallDiameterNm.";
                return null;
            }
            double zt = pad.TopM + H;                                   // ONE variable: the ball's top and the ring's plane
            balls.Add(($"{name}/ball/{which}",
                       new Em3dTruncatedSphere(new Point3(e.X, e.Y, pad.TopM + H / 2), D / 2, pad.TopM, zt)));
            double faceRadius = Math.Sqrt(D * D / 4 - H * H / 4);
            if (faceRadius < size.Width / 2)
                build.Warnings.Add($"Wire {name} is {Um(size.Width * 1e6)} µm wide and the top face of its ball at its " +
                                   $"{which} is only {Um(2 * faceRadius * 1e6)} µm across, so the wire overhangs the face " +
                                   "it lands on.");

            var neighbour = start ? pts[1] : pts[^2];
            var arrival   = Unit(Sub(neighbour, e));
            var top       = new Point3(e.X, e.Y, zt);
            bool vertical = Math.Abs(arrival.Z) >= CosNearVertical;
            if (vertical && !(neighbour.Z > zt))
            {
                build.Refusal = $"Wire {name} leaves its ball at its {which} downward, into the ball, so it cannot land " +
                                "on the ball's top face.";
                return null;
            }
            bool neck = !vertical;
            if (start)
            {
                flatRing[Append(top)] = zt;
                if (neck) Append(new Point3(e.X, e.Y, Math.Max(e.Z, zt + NeckMinimumDiameters * d)));
            }
            else
            {
                if (neck) Append(new Point3(e.X, e.Y, Math.Max(e.Z, zt + NeckMinimumDiameters * d)));
                flatRing[Append(top)] = zt;
            }
            return new Em3dWireEnd(style, pad.Name, pad.TopM, neck, null, 0);
        }

        var startEnd = EndOf(true, startPad!);
        if (startEnd is null) return null;
        for (int i = 1; i < n - 1; i++) Append(pts[i]);
        var endEnd = EndOf(false, endPad!);
        if (endEnd is null) return null;

        if (path.Count < 2)
        {
            build.Refusal = $"Wire {name} has fewer than two distinct points once its ends are placed.";
            return null;
        }

        var rings = Rings(name, path, section, d, build);
        if (rings is null) return null;

        // Exact surfaces: a foot's bottom on its pad (R-em3d4-4d), a ball end's ring in the ball's top face.
        foreach (var (i, z) in flatRing)
            rings[i] = [.. rings[i].Select(q => q with { Z = z })];
        foreach (var (i, z) in footRing)
        {
            double low = rings[i].Min(q => q.Z);
            double tol = 1e-9 * h;
            rings[i] = [.. rings[i].Select(q => q.Z - low <= tol ? q with { Z = z } : q)];
        }

        var sweep = new Em3dSweep(path, section, d, [.. rings.Select(r => (IReadOnlyList<Point3>)r)]);
        double assembly = AssemblyLoopHeight(sweep, Math.Min(startPad!.TopM, endPad!.TopM));
        var report = new Em3dWireReport(name, "", 0, "", section, d, size, startEnd, endEnd, process,
                                        assembly, wire.LoopHeightNm * 1e-9);
        return (sweep, balls, report);
    }

    /// <summary>
    /// The section at every path vertex (R-em3d4-2b/2c): the across-axis horizontal ("up" held at +z),
    /// carried over through near-vertical segments and kept continuous (never flipped between
    /// neighbours), square at the ends, mitred at interior vertices.
    /// </summary>
    private static List<Point3[]>? Rings(string name, List<Point3> path, Em3dSection section, double d,
                                         Em3dWireBuild build)
    {
        int m = path.Count - 1;
        var t = new Point3[m];
        var s = new Point3[m];
        var u = new Point3[m];
        for (int j = 0; j < m; j++) t[j] = Unit(Sub(path[j + 1], path[j]));

        static Point3 Level(Point3 tan) => Unit(new Point3(-tan.Y, tan.X, 0));     // +z × tangent
        Point3 prev = new(1, 0, 0);
        for (int j = 0; j < m; j++)
            if (Math.Abs(t[j].Z) < CosNearVertical) { prev = Level(t[j]); break; }
        for (int j = 0; j < m; j++)
        {
            Point3 across;
            if (Math.Abs(t[j].Z) >= CosNearVertical)
                across = Unit(Sub(prev, Scale(t[j], Dot(prev, t[j]))));        // carried over
            else
            {
                // Continuous, never flipped — judged against the previous across-axis CARRIED THROUGH
                // the turn from t[j-1] to t[j]. Compared unrotated, a plan turn beyond 90° flipped it,
                // which put the section's "up" at −z and mitred the joint ring to zero area.
                across = Level(t[j]);
                var carried = j == 0 ? prev : Transport(prev, t[j - 1], t[j]);
                if (Dot(across, carried) < 0) across = Scale(across, -1);
            }
            s[j] = across;
            u[j] = Cross(t[j], across);
            prev = across;
        }

        var outline = Em3dWireSection.Outline(section, d);
        var rings = new List<Point3[]>(m + 1);
        for (int i = 0; i <= m; i++)
        {
            var ring = new Point3[outline.Count];
            if (i == 0 || i == m)
            {
                int j = i == 0 ? 0 : m - 1;
                for (int k = 0; k < outline.Count; k++)
                    ring[k] = Add(path[i], Add(Scale(s[j], outline[k].Across), Scale(u[j], outline[k].Up)));
            }
            else
            {
                var (t1, t2) = (t[i - 1], t[i]);
                if (Dot(t1, t2) < CosMaxTurn)
                {
                    var q = path[i];
                    build.Refusal = $"Wire {name} turns by {Um(Math.Acos(Math.Clamp(Dot(t1, t2), -1, 1)) * 180 / Math.PI)}° " +
                                    $"at ({Um(q.X * 1e6)}, {Um(q.Y * 1e6)}, {Um(q.Z * 1e6)}) µm. A swept section cannot " +
                                    $"turn more sharply than {MaxTurnDegrees:G}° without crossing itself; smooth the " +
                                    "wire there.";
                    return null;
                }
                var normal = Unit(Add(t1, t2));
                for (int k = 0; k < outline.Count; k++)
                {
                    var o1 = Add(Scale(s[i - 1], outline[k].Across), Scale(u[i - 1], outline[k].Up));
                    var o2 = Add(Scale(s[i], outline[k].Across), Scale(u[i], outline[k].Up));
                    var q1 = Sub(o1, Scale(t1, Dot(o1, normal) / Dot(t1, normal)));
                    var q2 = Sub(o2, Scale(t2, Dot(o2, normal) / Dot(t2, normal)));
                    // Identical for a wire whose across-axis does not turn (every loop in one vertical
                    // plane); the mean keeps a turning one symmetric about the mitre plane.
                    ring[k] = Add(path[i], Scale(Add(q1, q2), 0.5));
                }
            }
            rings.Add(ring);
        }
        return rings;
    }

    /// <summary>The unit plan direction pointing OUT of the loop at one end, from the end segment,
    /// walking inward past vertical segments; null when every segment is vertical.</summary>
    private static Point3? PlanDirection(List<Point3> pts, bool start)
    {
        int n = pts.Count;
        for (int i = 0; i + 1 < n; i++)
        {
            var (outer, inner) = start ? (pts[i], pts[i + 1]) : (pts[n - 1 - i], pts[n - 2 - i]);
            double dx = outer.X - inner.X, dy = outer.Y - inner.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 1e-12) return new Point3(dx / len, dy / len, 0);
        }
        return null;
    }

    /// <summary>How far a foot of length <paramref name="len"/> from (x, y) along (dx, dy) runs past
    /// the pad's boundary: the length beyond the first boundary crossing, or zero.</summary>
    private static double Overhang(PlanarPolygon pad, double x, double y, double dx, double dy, double len)
    {
        double first = double.PositiveInfinity;
        void Ring(IReadOnlyList<EmPoint> ring)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double ex = ring[i].X - ring[j].X, ey = ring[i].Y - ring[j].Y;
                double den = dx * ey - dy * ex;
                if (Math.Abs(den) < 1e-30) continue;
                double wx = ring[j].X - x, wy = ring[j].Y - y;
                double along = (wx * ey - wy * ex) / den;       // distance along the foot
                double on    = (wx * dy - wy * dx) / den;       // position along the edge, 0..1
                if (along > 1e-15 && on >= 0 && on <= 1) first = Math.Min(first, along);
            }
        }
        Ring(pad.Outer);
        foreach (var hole in pad.HoleRings) Ring(hole);
        return first < len ? len - first : 0;
    }

    /// <summary>How many wedge ends moved in z to sit their foot on the pad, and the largest move.</summary>
    private sealed class EndMoves
    {
        public int Count;
        public double Max;
    }

    // ── Small vector arithmetic ──────────────────────────────────────────────────────────────────

    private static Point3 Add(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Point3 Scale(Point3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary><paramref name="v"/> rotated by the rotation that takes unit <paramref name="from"/> to
    /// unit <paramref name="to"/> about their common normal (Rodrigues). Parallel tangents leave it as it
    /// is; an antiparallel pair cannot reach here — a turn past 150° is refused before the rings.</summary>
    private static Point3 Transport(Point3 v, Point3 from, Point3 to)
    {
        var k = Cross(from, to);
        double sin = Math.Sqrt(Dot(k, k)), cos = Dot(from, to);
        if (sin < 1e-12) return v;
        k = Scale(k, 1 / sin);
        var kxv = Cross(k, v);
        double kv = Dot(k, v) * (1 - cos);
        return new Point3(v.X * cos + kxv.X * sin + k.X * kv,
                          v.Y * cos + kxv.Y * sin + k.Y * kv,
                          v.Z * cos + kxv.Z * sin + k.Z * kv);
    }
    private static Point3 Cross(Point3 a, Point3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Dist(Point3 a, Point3 b) => Math.Sqrt(Dot(Sub(a, b), Sub(a, b)));
    private static Point3 Unit(Point3 a) { double l = Math.Sqrt(Dot(a, a)); return l > 0 ? Scale(a, 1 / l) : a; }

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));
    private static string Um(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string Sci(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
