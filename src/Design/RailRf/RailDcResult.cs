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

    /// <summary>The row as a report prints it, with the observation case SAID.</summary>
    public string Describe() =>
        IsObservationOnly
            ? $"{Name}: {VoltageV:0.####} V — observed (it states no current and draws none)"
            : $"{Name}: {VoltageV:0.####} V" +
              (DropV is { } d ? $", {d * 1e3:0.###} mV below the source" : "") +
              $", drawing {CurrentA!.Value * 1e3:0.###} mA";
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
