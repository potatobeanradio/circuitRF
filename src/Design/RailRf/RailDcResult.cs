// What one rail's DC solve produced (docs/design/railrf.md §2.4, §2.2; brief 5 R-rail5-2,
// R-rail5-3, R-rail5-6, R-rail5-9 … R-rail5-11).
//
// ── THIS IS NOT A SECOND RESULT MODEL ──────────────────────────────────────────────────────────
//
// The NUMERIC result is a DataSet, packed by the same DcResultPacker every other DC run packs
// through, and it is on Data below. What this type adds is the DOMAIN shape §2.4 asks for and a
// DataSet cannot carry: which load a row belongs to, which pad a source sits on, which layer a
// breakdown row's copper is on, and the provenance that has to travel with every number. Brief 5's
// own rule — "no result type of its own beyond the domain-shaped RailDcResult".

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Engine.Pdn;
using RfCore.Data;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// One observation port, as the DC report lists it.
///
/// <para><b>Every port appears, including one that draws nothing</b> (R-rail5-10). §2.2: a port with
/// no stated current is not a load, it is an observation port — and "the DC report lists it as
/// OBSERVED rather than omitting it". Omitting it is the failure: a user who added a port and sees
/// no row for it concludes the port did not take effect, and <i>not added</i> and <i>added with no
/// current</i> must not look the same.</para>
/// </summary>
/// <param name="Index">The port's index, in the order the rail declares its loads.</param>
/// <param name="Name">What the user typed — <c>U1.VDD</c>, never a node number.</param>
/// <param name="Anchor">The anchor it resolved from.</param>
/// <param name="VoltageV">The rail's own voltage here: V(power) − V(reference), which is what the
/// die sees across its pin field.</param>
/// <param name="DropV">How far that is below the source's open-circuit voltage, or null where no
/// source on this rail stated one.</param>
/// <param name="CurrentA">What it draws, or null where it draws nothing.</param>
public sealed record RailPortDrop(
    int Index, string Name, RailPortAnchor Anchor, double VoltageV, double? DropV, double? CurrentA)
{
    /// <summary>True where this row states no current: observed, and nothing else.</summary>
    public bool IsObservationOnly => CurrentA is null;

    /// <summary>
    /// The row as a report prints it, with the observation case SAID.
    /// </summary>
    /// <param name="format">
    /// The units to spell this port's own anchor in, or null to keep <see cref="Name"/> — the anchor
    /// described in whatever units the RUN was asked for. The window passes the board's CURRENT units,
    /// because changing a display unit changes no number in this result and it should not take a
    /// re-solve to see the coordinate re-spelled (owner, 2026-09-19).
    /// </param>
    public string Describe(RailLengthFormat? format = null)
    {
        string name = format is { } f ? Anchor.Describe(f) : Name;

        return IsObservationOnly
            ? $"{name}: {VoltageV:0.####} V — observed (it states no current and draws none)"
            : $"{name}: {VoltageV:0.####} V" +
              (DropV is { } d ? $", {d * 1e3:0.###} mV below the source" : "") +
              $", drawing {CurrentA!.Value * 1e3:0.###} mA";
    }
}

/// <summary>
/// What one source on the rail carried.
///
/// <para><b>The share is a first-class number because the geometry is what decides it</b> (§2.2):
/// "two sources on one rail are two branches in the same mesh and nothing in the solve is
/// special-cased for them: the geometry is what decides how they share, which is the answer a hand
/// calculation cannot give and is a large part of why this is a board tool rather than a
/// spreadsheet."</para>
/// </summary>
/// <param name="Index">Its index in the rail's own source list.</param>
/// <param name="Name">The anchor's own spelling — <c>BT1.1</c>.</param>
/// <param name="OpenCircuitVoltageV">The level it held the rail at, or null where it stated none.</param>
/// <param name="CurrentA">What it delivered into the rail. Negative means it took current in.</param>
/// <param name="ShareOfTotal">Its fraction of every source's delivered current, in 0…1.</param>
public sealed record RailSourceShare(
    int Index, string Name, double? OpenCircuitVoltageV, double CurrentA, double ShareOfTotal);

/// <summary>
/// What the chain found at one regulator's input pin field (§2.2's own Q-20 finding).
///
/// <para><b>Both numbers, or no finding at all.</b> R-rail5-9: railRF reports the drop always and the
/// headroom finding only where a minimum was stated — never against a defaulted one. Where none was
/// stated the regulator still appears here with a null <see cref="MinimumInputVoltageV"/>, so the
/// ABSENCE is visible rather than read as a pass.</para>
/// </summary>
/// <param name="Refdes">The regulator.</param>
/// <param name="RailName">The rail its input pin field sits on.</param>
/// <param name="InputVoltageV">The UPSTREAM ANSWER at that pin field — not a nominal.</param>
/// <param name="MinimumInputVoltageV">What it needs to regulate, where that was stated.</param>
public sealed record RailRegulatorHeadroom(
    string Refdes, string RailName, double InputVoltageV, double? MinimumInputVoltageV)
{
    /// <summary>How much is left, or null where nothing was stated to compare against.</summary>
    public double? MarginV => MinimumInputVoltageV is { } m ? InputVoltageV - m : null;

    /// <summary>True where the input is at or above the stated minimum; null where none was stated.</summary>
    public bool? Ok => MarginV is { } m ? m >= 0 : null;

    /// <summary>The sentence, with BOTH numbers in it where there are two.</summary>
    public string Describe() =>
        MinimumInputVoltageV is not { } min
            ? $"{Refdes} sees {InputVoltageV:0.####} V on rail '{RailName}'. It states no minimum " +
              "input voltage, so railRF reports the voltage and makes no headroom finding."
            : MarginV >= 0
                ? $"{Refdes} sees {InputVoltageV:0.####} V on rail '{RailName}' against the " +
                  $"{min:0.####} V it needs — {MarginV!.Value * 1e3:0.###} mV of headroom."
                : $"{Refdes} sees {InputVoltageV:0.####} V on rail '{RailName}' and needs " +
                  $"{min:0.####} V: the drop on this rail has taken it {-MarginV!.Value * 1e3:0.###} mV " +
                  "BELOW the input voltage it needs to regulate.";
}

/// <summary>
/// Where one breakdown row's group actually IS on the board — <b>the answer
/// <see cref="PdnBreakdownRow.GroupKey"/> has always promised and nothing produced</b>
/// (<c>brief-railrf-19-unreachable-states.md</c> R-rail19-3).
/// </summary>
/// <remarks>
/// <b>Built where the group keys are, and nowhere else.</b> The keys are minted inside
/// <c>RailDcRun</c>'s own aggregation — <c>copper|…</c>, <c>vias|…</c>, <c>section|…</c>, a part's
/// kind and refdes — and a caller that re-derived them in order to map a row back to copper would be
/// holding a second copy of a private spelling, which would go on compiling and silently stop
/// matching the first time a key gained a field.
///
/// <para><b>A row with no place is ABSENT rather than empty.</b> A source's own series resistance,
/// a part's ESR and an observation port are not copper and have nowhere on the board to point at;
/// a caller learns that from finding no entry, and says so, rather than from a zero-size box
/// appearing at the origin (R-rail19-3b).</para>
/// </remarks>
/// <param name="GroupKey">The row's own <see cref="PdnBreakdownRow.GroupKey"/>.</param>
/// <param name="Bounds">The extent of every cell the group's elements touch, DBU.</param>
/// <param name="Cells">Those cell centres, deduplicated — what a locator marks and what a test
/// checks the bounds against.</param>
public sealed record RailBreakdownLocation(
    string GroupKey, Bbox Bounds, IReadOnlyList<(long X, long Y)> Cells);

/// <summary>
/// One rail's DC answer: the field, the ranked breakdown, the ports, the sources and the provenance.
/// </summary>
public sealed class RailDcResult
{
    /// <summary>The rail this is of. The extractor extracts one rail and this solve solves one.</summary>
    public required string RailName { get; init; }

    /// <summary>The extraction it was solved from — the origins the breakdown is aggregated over,
    /// the port bindings, the node→cell map the drop map is drawn from, and the provenance.</summary>
    public required PdnNetlist Netlist { get; init; }

    /// <summary>
    /// The numeric result, in the shape every other circuitRF analysis produces — a <c>V</c> cube on
    /// a named node axis. Nothing here invents a result type; this is what exports, plots and the
    /// Data Display read.
    /// </summary>
    public required DataSet Data { get; init; }

    /// <summary>Netlist node → its voltage, ground included at exactly zero. <b>Complete</b>: every
    /// node of <see cref="PdnNetlist.NodeCells"/> has one, because a hole in a heat map reads as a
    /// value rather than as an absence (R-rail5-2).</summary>
    public required IReadOnlyDictionary<int, double> NodeVoltages { get; init; }

    /// <summary>§2.4's ranked table, aggregated by origin (R-rail5-3, R-rail5-4).</summary>
    public required IReadOnlyList<PdnBreakdownRow> Breakdown { get; init; }

    /// <summary>
    /// Where each breakdown row's copper is, keyed by <see cref="PdnBreakdownRow.GroupKey"/> — the
    /// locator of R-rail19-3. Rows that are not copper have no entry; see
    /// <see cref="RailBreakdownLocation"/>.
    /// </summary>
    public IReadOnlyDictionary<string, RailBreakdownLocation> BreakdownLocations { get; init; } =
        new Dictionary<string, RailBreakdownLocation>(StringComparer.Ordinal);

    /// <summary>
    /// The galvanic region walk this rail's copper produced — the islands, per drawing layer.
    /// </summary>
    /// <remarks>
    /// <b>Carried on the RESULT because the picture is drawn from it</b> (brief 8). The extraction
    /// already produces it as a first-class output rather than a diagnostic (R-rail3-4), and a window
    /// that re-walked the copper in order to draw the rail would be a second reading of the geometry
    /// that could disagree with the one the numbers came from. Null on a result whose extractor
    /// produced none.
    /// </remarks>
    public PdnRailRegionSet? Regions { get; init; }

    /// <summary>
    /// Which SECTION of this rail each of <see cref="Regions"/>' power islands is in, by island
    /// index — <b>what the copper map shades from</b> (brief 25, R-rail25-4c; brief 35, R-rail35-3d).
    /// Section 0 is the source's; <see cref="SectionNames"/> names each one.
    /// </summary>
    /// <remarks>
    /// <b>Empty on every rail with no series element</b>, which is every rail written before brief
    /// 25 — so the copper tab draws exactly what it drew. Where it is not empty, <i>which side of
    /// the ferrite am I on</i> is answerable by LOOKING, which is the same question the class map
    /// already answers for trace-versus-mesh and through the same overlay.
    ///
    /// <para>It is the partition the SOLVE used, carried on the result, rather than a second walk
    /// the picture does for itself — the section a part is shaded in has to be the section its
    /// branch was stamped on.</para>
    /// </remarks>
    public IReadOnlyDictionary<int, int> Sections { get; init; } = new Dictionary<int, int>();

    /// <summary>What a reader calls each section, by section index — "upstream"/"downstream" on a
    /// one-element rail, "beyond FB1" on a chain (<see cref="RailSeriesPartition.SectionName"/>).
    /// Empty wherever <see cref="Sections"/> is.</summary>
    public IReadOnlyList<string> SectionNames { get; init; } = [];

    /// <summary>
    /// Which copper the fast model treated as a trace and which it meshed, with the reason for each
    /// (R-rail4-3). Empty from the accurate reading, which meshes everything and so classifies
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <b>§2.9 rule 2 is not satisfiable unless this reaches the window.</b> "A silent
    /// misclassification is the one failure mode of this design" — a wide supply polygon mistaken for
    /// a trace is optimistic and the number looks entirely ordinary. Brief 8's class tab draws THIS
    /// list rather than re-deriving a classification that could differ from the one the extraction
    /// actually priced.
    /// </remarks>
    public IReadOnlyList<PdnClassification> Classification { get; init; } = [];

    /// <summary>
    /// §2.4's via check: every layer transition, the current in EACH of its vias, and the ones whose
    /// WORST via is over its limit (brief 6).
    ///
    /// <para><b>Part of the answer rather than a separate run</b>, because the currents it reads are
    /// the ones this solve already produced — brief 3 stamped each barrel as its own element and
    /// nothing about a group is special-cased anywhere, so the split is a result rather than a
    /// model. <see cref="PdnViaCheckResult.Empty"/> on a rail with no vias.</para>
    /// </summary>
    public PdnViaCheckResult ViaCheck { get; init; } = PdnViaCheckResult.Empty;

    /// <summary>Every observation port, in declaration order — including the ones that draw
    /// nothing.</summary>
    public required IReadOnlyList<RailPortDrop> Ports { get; init; }

    /// <summary>Every source, with the share of the load it carried.</summary>
    public required IReadOnlyList<RailSourceShare> Sources { get; init; }

    /// <summary>Every regulator whose input pin field is a load on THIS rail, with the upstream
    /// answer it sees (R-rail5-7, R-rail5-9).</summary>
    public IReadOnlyList<RailRegulatorHeadroom> Regulators { get; init; } = [];

    /// <summary>
    /// The level this rail's own source started from where it came from UPSTREAM rather than from
    /// the document — the answer at the regulator's input pin field on the rail above (R-rail5-7).
    /// Null on a rail with no upstream, which is most of them.
    /// </summary>
    public RailChainStart? ChainedFrom { get; init; }

    /// <summary>The reference every <see cref="RailPortDrop.DropV"/> is measured from: the highest
    /// open-circuit voltage any source on this rail stated. Null where none did, which is exactly
    /// when the drops are null too.</summary>
    public double? SourceVoltageV { get; init; }

    /// <summary>Whether the worst port drop is inside the rail's stated budget — null where the
    /// rail states none, which is reported rather than substituted for.</summary>
    public bool? WithinDropBudget { get; init; }

    /// <summary>Findings a reader should act on. Warnings, never notes.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>Everything railRF WORKED OUT that the document did not state, including the
    /// extraction's own. Never mixed with <see cref="Findings"/>.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// <b>R-rail5-11, and it is one line.</b> §2.4: review is running this as a room-temperature
    /// selection tool and the design is measured over temperature in the lab regardless — so railRF
    /// computes at one stated temperature, says so, and does not pretend to be a thermal tool.
    /// </summary>
    public string TemperatureLine =>
        $"Every resistance here is at {Netlist.Provenance.CopperTemperatureCelsius:0.#} °C. " +
        "Copper is +0.39 %/K, so at 85 °C the same trace is about 25 % worse; railRF is not a " +
        "thermal tool and does not derate.";

    // ── R-rail14-3: the highest-value sanity check in the tool, and it must not be buried ───────

    /// <summary>
    /// <b>The extracted plane capacitance, as ONE number, in the same words the window and the
    /// headless report both print.</b>
    ///
    /// <para>§9: <i>"The stackup is usually wrong. Designers copy a stackup from the last board. …
    /// railRF shows the extracted plane capacitance as a single number early and prominently,
    /// because a designer recognises a wrong one instantly and would never notice it buried in a
    /// curve."</i> It is <c>ε₀εᵣA/h</c> over the real OVERLAP of the two conductors, so one glance
    /// checks the permittivity, the area and the dielectric thickness at once — which is what makes
    /// it worth more than its cost.</para>
    ///
    /// <para><b>One sentence on the RESULT, for the reason <see cref="VoltageAt"/> is one rule on
    /// the result</b> (R-rail5-2): a window and a report that disagreed about the same number at the
    /// same moment would make both unusable, and the two are written by different briefs.</para>
    ///
    /// <para>It is present on a DC run, where nothing was stamped from it. At ω = 0 §4.1's shunt
    /// branch vanishes; the stackup is exactly as worth checking.</para>
    /// </summary>
    public string PlaneCapacitanceLine
    {
        get
        {
            var p = Netlist.Provenance;

            // R-rail18-2b. THREE STATES, and only one of them is about the user's stackup. A model
            // that did not compute the number must say that and name itself; a rail whose copper
            // simply does not overlap its reference has a computed zero and nothing to complain
            // about. Reaching the stackup sentence through `<= 0` told every user of the default
            // model that their stackup was wrong.
            if (p.PlaneCapacitanceBasis == PdnPlaneCapacitanceBasis.NotComputed)
                return $"Plane capacitance: not computed by {p.Model}. Nothing here is a statement " +
                       "about the stackup — run the other model to have it checked.";

            if (p.PlaneCapacitanceBasis == PdnPlaneCapacitanceBasis.NoDielectricStated)
                return "Plane capacitance: none. The stackup states no dielectric between this " +
                       "rail's copper and its reference, so ε₀εᵣA/h has no h — state the dielectric " +
                       "entries between them.";

            if (!(p.PlaneCapacitanceFarads > 0))
                return "Plane capacitance: none — this rail's copper and its reference do not " +
                       "overlap anywhere, so ε₀εᵣA/h has no A. The stackup is not the problem; the " +
                       "reference extent or the reference layer is.";

            return $"Plane capacitance {Farads(p.PlaneCapacitanceFarads)} — ε₀εᵣA/h over " +
                   $"{p.PlaneOverlapSquareMetres * 1e4:0.##} cm² of OVERLAP at εr " +
                   $"{p.RelativePermittivity:0.###}, tan δ {p.LossTangent:0.####}" +
                   (p.LossTangentIsClassDefault ? $" ({RailEsrDefaults.Marking})" : "") + ".";
        }
    }

    /// <summary>A capacitance as this report spells it, on <c>PdnMask.Ohms</c>' reasoning: one
    /// spelling, so a status strip and a report page cannot disagree about what 2.1e-9 F is called.</summary>
    internal static string Farads(double c) =>
        !double.IsFinite(c) || c <= 0 ? "(none)"
        : c >= 1e-6 ? $"{c * 1e6:0.###} µF"
        : c >= 1e-9 ? $"{c * 1e9:0.###} nF"
        : c >= 1e-12 ? $"{c * 1e12:0.###} pF"
        :              $"{c * 1e15:0.###} fF";

    // ── R-rail5-2: the FIELD, and its interpolation stated once ────────────────────────────────

    /// <summary>
    /// The voltage anywhere on the net, not merely at a cell centre.
    ///
    /// <para><b>The rule is stated here, once, because the window and the headless report may not
    /// differ about it</b> (R-rail5-2). A cursor readout that disagreed with the report at the same
    /// coordinate would make both unusable, and the two are written by different briefs.</para>
    ///
    /// <para><b>Inverse-distance-SQUARED over the four nearest cells on the same drawing layer and
    /// the same side of the rail</b>, with an exact hit on a cell centre answering that cell's own
    /// value. Four rather than three because the mesh is a grid and three points on a grid pick a
    /// direction; squared rather than linear because a linear weight leaves visible creases along the
    /// cell rows. It serves BOTH readings of the geometry — the mesh's cells are a regular grid and
    /// the graph's are junctions at irregular spacings, and one rule has to cover both or the fast
    /// and accurate drop maps would be drawn by different arithmetic.</para>
    ///
    /// <para>Null where the layer and side carry no cells at all, which is an honest answer: there is
    /// no copper of this rail there to read a voltage from.</para>
    /// </summary>
    /// <param name="layer">The drawing layer the cursor is over.</param>
    /// <param name="isReference">True to read the reference conductor, false for the rail's copper.</param>
    /// <param name="xDbu">Where, in the artwork's own coordinates.</param>
    /// <param name="yDbu">Same.</param>
    public double? VoltageAt(LayerKey layer, bool isReference, long xDbu, long yDbu)
    {
        var cells = FieldFor(layer, isReference);
        if (cells.Count == 0) return null;

        Span<double> bestD = stackalloc double[InterpolationNeighbours];
        Span<double> bestV = stackalloc double[InterpolationNeighbours];
        int held = 0;

        foreach (var (cx, cy, v) in cells)
        {
            double dx = cx - xDbu, dy = cy - yDbu;
            double d2 = dx * dx + dy * dy;
            if (d2 <= 0) return v;                       // exactly on a cell centre

            if (held < InterpolationNeighbours)
            {
                bestD[held] = d2;
                bestV[held] = v;
                held++;
                continue;
            }

            int worst = 0;
            for (int i = 1; i < held; i++) if (bestD[i] > bestD[worst]) worst = i;
            if (d2 < bestD[worst]) { bestD[worst] = d2; bestV[worst] = v; }
        }

        double num = 0, den = 0;
        for (int i = 0; i < held; i++)
        {
            double w = 1.0 / bestD[i];
            num += w * bestV[i];
            den += w;
        }

        return den > 0 ? num / den : null;
    }

    /// <summary>See <see cref="VoltageAt"/>. Four, and the reason is there.</summary>
    public const int InterpolationNeighbours = 4;

    private Dictionary<(LayerKey, bool), List<(long X, long Y, double V)>>? _field;

    private List<(long X, long Y, double V)> FieldFor(LayerKey layer, bool isReference)
    {
        if (_field is null)
        {
            _field = [];
            foreach (var (node, cell) in Netlist.NodeCells)
            {
                if (!NodeVoltages.TryGetValue(node, out double v)) continue;
                var key = (cell.Layer, cell.IsReference);
                if (!_field.TryGetValue(key, out var list)) _field[key] = list = [];
                list.Add((cell.CentreX, cell.CentreY, v));
            }
        }

        return _field.TryGetValue((layer, isReference), out var found) ? found : [];
    }
}

/// <summary>
/// Where a downstream rail's source got its level, when it got it from the rail above (R-rail5-7).
/// </summary>
/// <param name="UpstreamRail">The rail that was solved first.</param>
/// <param name="Refdes">The part that is a load there and a source here.</param>
/// <param name="VoltageV">The upstream ANSWER at that part's input pin field — never a nominal.</param>
public sealed record RailChainStart(string UpstreamRail, string Refdes, double VoltageV)
{
    public string Describe() =>
        $"This rail's source at {Refdes} starts from {VoltageV:0.####} V — the answer at {Refdes}'s " +
        $"own input pin field on rail '{UpstreamRail}', not a nominal.";
}
