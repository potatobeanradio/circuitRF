namespace CircuitRF.WBond;

/// <summary>
/// What the inductance panel shows, as plain data (wbond.md §6.8; R-wbc-7).
///
/// <para><b>Picohenries, fixed, never auto-ranged (WB27a / D9.)</b> The panel exists for comparison
/// during a drag — across arrays, and against the same array a second ago. A readout that silently
/// switches nH ↔ pH mid-drag makes a number appear to jump by 1000x when the geometry moved by a
/// mil, which is precisely the illusion a live readout must not create. Wirebond inductances live in
/// the tens-to-thousands of pH, so one unit covers the whole useful range.</para>
///
/// <para>A data record, not a view-model: the bindings and the formatting belong to WB-C2.</para>
/// </summary>
public sealed class PanelReadout
{
    /// <summary>
    /// One geometry quantity summarised across an array's wires (owner, 2026-08-16).
    ///
    /// <para><b>An array is a group of wires, not one wire</b> — its loop height, span, diameter and
    /// material are only single numbers when the wires happen to agree. Reporting the first wire's
    /// value would be silently wrong on the arrays where it matters (a ramp of loop heights across a
    /// lead frame reads as one height), and reporting nothing would cost the row. So: the median,
    /// plus a flag the panel renders as a <c>*</c>, which is the smallest honest answer.</para>
    /// </summary>
    /// <param name="Value">The median across the array's wires.</param>
    /// <param name="Varies">
    /// True when the wires do not all agree. The panel marks the number with a <c>*</c>; nothing
    /// downstream branches on it, so a stale flag can only mis-annotate, never mis-compute.
    /// </param>
    public readonly record struct Aggregate<T>(T Value, bool Varies);

    /// <summary>One array's row in the panel.</summary>
    /// <param name="Name">The array name, which is also its symbol pin name.</param>
    /// <param name="SelfPicoHenries">L_arr diagonal, pH.</param>
    /// <param name="MutualPicoHenries">Mutual to every array including itself, pH.</param>
    /// <param name="CouplingCoefficients">
    /// k = M/sqrt(Lii*Ljj). Offered alongside the pH mutuals because it is scale-free, and it is the
    /// number that says whether two arrays are meaningfully coupled — a bare pH mutual does not,
    /// without mentally dividing by the selfs.
    /// </param>
    /// <param name="WireCount">How many wires are in the array.</param>
    /// <param name="TotalLengthMm">Total developed wire length, mm.</param>
    /// <param name="MaxLandingSpanMm">
    /// The largest distance between any two of the array's landing points. Reported, never warned
    /// about (WB9a): it is the proxy for "is the equipotential-pad assumption being stretched", and
    /// there is no threshold separating good from bad without sheet resistance and frequency.
    /// </param>
    /// <param name="CurrentShares">
    /// Per-wire current for 1 A into this array. Edge wires carry measurably more than centre ones —
    /// real physics the reduction captures for free.
    /// </param>
    /// <param name="LoopHeightMm">Median loop height (max z − min z, §3.1a) across the array's wires.</param>
    /// <param name="SpanMm">Median foot-to-foot chord length across the array's wires.</param>
    /// <param name="DiameterMm">Median wire diameter across the array's wires.</param>
    /// <param name="Material">
    /// The array's material. A <b>median</b> like the three lengths — the middle of the wires'
    /// material names in order — rather than the most common one, so the rule the panel states is the
    /// same rule for every row it shows. On the ordinary uniform array the two agree; when they do
    /// not, the <c>*</c> is what the reader is being pointed at, not the particular name.
    /// </param>
    public readonly record struct ArrayRow(
        string Name,
        double SelfPicoHenries,
        IReadOnlyList<double> MutualPicoHenries,
        IReadOnlyList<double> CouplingCoefficients,
        int WireCount,
        double TotalLengthMm,
        double MaxLandingSpanMm,
        IReadOnlyList<double> CurrentShares,
        Aggregate<double> LoopHeightMm,
        Aggregate<double> SpanMm,
        Aggregate<double> DiameterMm,
        Aggregate<string> Material);

    public required IReadOnlyList<ArrayRow> Rows { get; init; }

    /// <summary>
    /// What the return path currently is — always stated, because reporting an inductance without one
    /// is the most common way a bondwire model is wrong (WB20 / RW13).
    /// </summary>
    public required string ReturnPath { get; init; }

    /// <summary>
    /// True when the return path is the ORDINARY one — the image plane at z = 0.
    ///
    /// <para>The panel suppresses the sentence in that case (owner, 2026-08-16). WB20/RW13's point is
    /// that a reader must never be left guessing what an inductance is referred to; a line that says
    /// the same expected thing on every document all the time does not achieve that, it just costs a
    /// row on every card. The line that matters — the UNDECLARED one — is unaffected, and this flag is
    /// what lets the view tell them apart without string-matching.</para>
    /// </summary>
    public required bool ReturnPathIsDefault { get; init; }

    /// <summary>Builds the readout from a reduction and its design.</summary>
    public static PanelReadout Build(WBondDesign design, WireMesh mesh, ArrayReduction reduction)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(reduction);

        int m = reduction.ArrayCount;
        var rows = new List<ArrayRow>(m);

        for (int a = 0; a < m; a++)
        {
            var drive = new double[m];
            drive[a] = 1.0;
            var allShares = reduction.CurrentShares(drive);

            var shares = new List<double>();
            for (int w = 0; w < mesh.WireCount; w++)
                if (mesh.ArrayOfWire[w] == a) shares.Add(allShares[w]);

            var mutuals = new double[m];
            var coupling = new double[m];
            for (int b = 0; b < m; b++)
            {
                mutuals[b] = reduction.PicoHenries(a, b);
                coupling[b] = reduction.CouplingCoefficient(a, b);
            }

            var wires = design.Arrays[a].Wires;
            rows.Add(new ArrayRow(
                Name: design.Arrays[a].Name,
                SelfPicoHenries: reduction.PicoHenries(a, a),
                MutualPicoHenries: mutuals,
                CouplingCoefficients: coupling,
                WireCount: wires.Count,
                TotalLengthMm: wires.Sum(w => w.PathLengthMetres()) * 1e3,
                MaxLandingSpanMm: MaxLandingSpan(wires) * 1e3,
                CurrentShares: shares,
                LoopHeightMm: MedianLengthMm(wires, w => w.LoopHeightNm),
                SpanMm: MedianLengthMm(wires, SpanNm),
                DiameterMm: MedianLengthMm(wires, w => w.DiameterNm),
                Material: MedianMaterial(wires)));
        }

        return new PanelReadout
        {
            Rows = rows,
            ReturnPath = design.GroundPlane.Enabled
                ? "image plane at z = 0"
                : "UNDECLARED — the ground plane is disabled and no array is nominated as the return",
            ReturnPathIsDefault = design.GroundPlane.Enabled,
        };
    }

    /// <summary>One wire's foot-to-foot span, in whole nanometres — the unit everything else is stored in.</summary>
    private static long SpanNm(Wire wire) =>
        (long)Math.Round(wire.ChordLengthMetres() * WBondUnits.NmPerMetre);

    /// <summary>
    /// The median of a per-wire length, in millimetres, plus whether the wires agreed.
    ///
    /// <para><b>Compared as integer NANOMETRES, not as the double the readout returns.</b> Loop height
    /// and diameter are stored that way; span is computed, and two wires cut to the same span at
    /// different orientations differ in the last bits of the double while rounding to the same
    /// nanometre. Comparing the doubles would mark such an array non-uniform, which is a <c>*</c> the
    /// user cannot act on and cannot make go away.</para>
    ///
    /// <para>The median itself is the textbook one — the middle value, or the mean of the two middle
    /// values on an even count.</para>
    /// </summary>
    private static Aggregate<double> MedianLengthMm(IReadOnlyList<Wire> wires, Func<Wire, long> valueNm)
    {
        if (wires.Count == 0) return new Aggregate<double>(0.0, false);

        var sorted = wires.Select(valueNm).Order().ToArray();
        bool varies = sorted[0] != sorted[^1];

        double medianNm = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;

        return new Aggregate<double>(medianNm * 1e-6, varies);   // nm -> mm
    }

    /// <summary>
    /// The median material name, and whether the wires agreed.
    ///
    /// <para>The LOWER middle of the ordered names on an even count — there is no averaging two
    /// materials, and picking a value some wire actually has keeps the number the panel shows the
    /// same number the "set every wire" prompt opens on.</para>
    /// </summary>
    private static Aggregate<string> MedianMaterial(IReadOnlyList<Wire> wires)
    {
        if (wires.Count == 0) return new Aggregate<string>("", false);

        var sorted = wires.Select(w => w.Material ?? "").Order(StringComparer.Ordinal).ToArray();
        bool varies = !string.Equals(sorted[0], sorted[^1], StringComparison.Ordinal);

        return new Aggregate<string>(sorted[(sorted.Length - 1) / 2], varies);
    }

    /// <summary>
    /// The largest distance between any two landing points of an array (WB9a's diagnostic).
    /// Both feet of every wire count — an array can span a long lead frame finger at either end.
    ///
    /// <para>No longer shown in the panel — the row's Span is now the median of the wires' OWN spans,
    /// which is the quantity a user can set. This stays because it is a different and still-real
    /// diagnostic: how far apart the array's landing points are is the proxy for whether the
    /// equipotential-pad assumption is being stretched, and no per-wire span reports it.</para>
    /// </summary>
    private static double MaxLandingSpan(IReadOnlyList<Wire> wires)
    {
        var feet = new List<Point3>(wires.Count * 2);
        foreach (var wire in wires)
        {
            if (wire.Points.Count < 2) continue;
            feet.Add(wire.Points[0]);
            feet.Add(wire.Points[^1]);
        }

        double worst = 0.0;
        for (int i = 0; i < feet.Count; i++)
        {
            for (int j = i + 1; j < feet.Count; j++)
            {
                double dx = WBondUnits.ToMetres(feet[j].X - feet[i].X);
                double dy = WBondUnits.ToMetres(feet[j].Y - feet[i].Y);
                double dz = WBondUnits.ToMetres(feet[j].Z - feet[i].Z);
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
        }

        return worst;
    }
}
