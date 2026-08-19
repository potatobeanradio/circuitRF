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
    /// <param name="SelfPicoHenries">
    /// <b>What the panel prints — the EFFECTIVE inductance at <see cref="ReadoutFrequencyGHz"/>, pH.</b>
    ///
    /// <para>This used to be the frequency-independent partial inductance <c>L_arr</c>, and it was
    /// right to be: before capacitance existed there was nothing frequency-dependent to report. With
    /// shunt capacitance the wire has a self-resonance and the inductance seen at the terminals rises
    /// toward it, so the panel quotes <c>L_eff(f) = Im(Z_in)/ω</c> with the far end shorted to the
    /// reference plane (see <see cref="CapacitanceReduction.EffectiveInductance"/>).</para>
    ///
    /// <para><b>With capacitance off this is <c>L_arr</c> at every frequency, exactly</b> — see
    /// <see cref="PartialPicoHenries"/>, which is always <c>L_arr</c> whatever the flag says.</para>
    /// </param>
    /// <param name="PartialPicoHenries">
    /// The frequency-independent external partial inductance <c>L_arr</c>, pH — pure geometry, and
    /// unchanged by capacitance. Kept alongside <see cref="SelfPicoHenries"/> so the two quantities
    /// never have to be told apart by which build produced them.
    /// </param>
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
    /// <param name="SpanMm">Median foot-to-foot span (XY) across the array's wires.</param>
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
        double PartialPicoHenries,
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

    /// <summary>
    /// The frequency <see cref="ArrayRow.SelfPicoHenries"/> is quoted at, in GHz — the panel's own
    /// settable row (<see cref="WBondDesign.ReadoutFrequencyGHz"/>).
    ///
    /// <para><b>A readout setting, never a simulation input.</b> The schematic's own analysis sweep is
    /// what the engine stamps against; this decides only which frequency the panel's number is quoted
    /// at.</para>
    /// </summary>
    public required double ReadoutFrequencyGHz { get; init; }

    /// <summary>
    /// Whether capacitance is actually in the reported numbers. False either because the design turns
    /// it off, or because the ground plane is disabled and there is no reference conductor to be
    /// capacitive to — the panel reports the same partial inductance in both cases.
    /// </summary>
    public required bool CapacitanceIncluded { get; init; }

    /// <summary>
    /// Whether the design ASKS for capacitance — <see cref="WBondDesign.IncludeCapacitance"/>.
    ///
    /// <para><b>Not the same as <see cref="CapacitanceIncluded"/>, and the gap is the point.</b> A
    /// design can ask for capacitance and not get it, because the ground plane is disabled and there
    /// is then no reference conductor to be capacitive to. The panel's toggle has to show what was
    /// ASKED, or it would appear to turn itself off; the panel's own note is what explains the
    /// difference when the two disagree.</para>
    /// </summary>
    public required bool CapacitanceRequested { get; init; }

    /// <summary>The lowest self-resonance of the shorted-far-end network in GHz, or 0 when there is none.</summary>
    public required double SelfResonanceGHz { get; init; }

    /// <summary>
    /// True at and above 0.95 × the self-resonance, where the effective inductance runs to ±∞ and
    /// then negative.
    ///
    /// <para><b>The panel must print the state, not the number.</b> A readout that swings through
    /// infinity and comes back negative is not a wrong number a reader can discount — it is a number
    /// that looks like an answer. <see cref="ResonanceWarning"/> is what is shown instead.</para>
    /// </summary>
    public required bool AboveSelfResonance { get; init; }

    /// <summary>The sentence shown in place of the numbers above resonance, or empty below it.</summary>
    public required string ResonanceWarning { get; init; }

    /// <summary>Builds the readout from a reduction and its design.</summary>
    /// <param name="capacitance">
    /// The array-basis capacitance, or null for none. <b>During a drag this is deliberately the LAST
    /// COMMITTED one</b> — capacitance is not in the incremental path (wbond.md §4.4), so it is
    /// recomputed on drag end rather than per frame. See <c>WBondViewModel</c>'s own note.
    /// </param>
    public static PanelReadout Build(WBondDesign design, WireMesh mesh, ArrayReduction reduction,
                                     CapacitanceReduction? capacitance = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(reduction);

        int m = reduction.ArrayCount;
        var rows = new List<ArrayRow>(m);

        // A capacitance whose array count has drifted from the reduction's is a stale one from before
        // a structural edit; dropping it is right, because the alternative is indexing past its end.
        bool includeCapacitance = design.IncludeCapacitance
                               && capacitance is not null
                               && capacitance.ArrayCount == m;

        double[] shunt = includeCapacitance ? capacitance!.TerminalShuntMatrix() : new double[m * m];
        double srfHz = includeCapacitance
            ? CapacitanceReduction.SelfResonanceHz(reduction, shunt)
            : double.PositiveInfinity;

        double frequencyHz = design.ReadoutFrequencyGHz * 1e9;
        bool aboveResonance = includeCapacitance && double.IsFinite(srfHz) && frequencyHz >= 0.95 * srfHz;

        // Below resonance the effective inductance is the number; at or above it the expression runs
        // to +/-inf and then negative, so it is not evaluated at all.
        //
        // WITH THE FLAG OFF IT IS NOT EVALUATED EITHER, and that is not an optimisation. The zero-shunt
        // expression is L_arr in exact arithmetic but not in the last bit of double arithmetic — it
        // inverts a matrix to get there — and "off reproduces today's answer exactly" is a bit-identity
        // claim, not a tolerance (gate C1). So the flag-off path reads the reduction directly, exactly
        // as it did before capacitance existed.
        double[]? effective = null;
        if (!aboveResonance && includeCapacitance)
        {
            try
            {
                effective = CapacitanceReduction.EffectiveInductance(reduction, shunt, frequencyHz);
            }
            catch (InvalidOperationException)
            {
                // Landing exactly on a singular terminal network is the above-resonance state by
                // another route. The 0.95 guard band keys off the LOWEST resonance, found by power
                // iteration; if that iteration ever under-estimates it, the panel must report the
                // state rather than take a UI thread down with it.
                aboveResonance = true;
            }
        }

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
                SelfPicoHenries: effective is null ? reduction.PicoHenries(a, a) : effective[a] * 1e12,
                PartialPicoHenries: reduction.PicoHenries(a, a),
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
            ReadoutFrequencyGHz = design.ReadoutFrequencyGHz,
            CapacitanceIncluded = includeCapacitance,
            CapacitanceRequested = design.IncludeCapacitance,
            SelfResonanceGHz = double.IsFinite(srfHz) ? srfHz * 1e-9 : 0.0,
            AboveSelfResonance = aboveResonance,
            ResonanceWarning = aboveResonance
                ? $"Above self-resonance (SRF {srfHz * 1e-9:F1} GHz) — the effective inductance is " +
                  "not meaningful here."
                : "",
        };
    }

    /// <summary>One wire's foot-to-foot span IN XY, in whole nanometres — the unit everything else is
    /// stored in. See <see cref="Wire.SpanMetres"/> for why there is no z in it.</summary>
    private static long SpanNm(Wire wire) =>
        (long)Math.Round(wire.SpanMetres() * WBondUnits.NmPerMetre);

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
    /// The largest distance IN XY between any two landing points of an array (WB9a's diagnostic).
    /// Both feet of every wire count — an array can span a long lead frame finger at either end.
    ///
    /// <para><b>XY, like every other span in this application</b> (owner, 2026-08-19). It used to
    /// include the feet's z difference, which on an array landing on two levels reported the pads as
    /// further apart than any distance along either pad — and the equipotential question this exists
    /// to flag is about extent ACROSS the conductor, which is a plan-view quantity.</para>
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
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
            }
        }

        return worst;
    }
}
