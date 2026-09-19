// The Smith Chart tool's document model (docs/design/smith-chart.md §3, §7;
// brief-smith-1-document.md R-smith1-1).
//
// NAMING, decided here to avoid a collision discovered late (R-smith1-1a): the MODEL is
// `SmithDesign`, mirroring `MatchDesign`, and brief 4's Dock document is `SmithChartDocument`,
// mirroring `DataDisplayDocument`. `SmithDocument` is used for NEITHER — it is the obvious name for
// both, and whichever one took it would make the other one's name read as a mistake.
//
// NUMBERS ARE IN BASE SI and carry their display scale nowhere near them: hertz, henries, farads,
// ohms. That is the sweep-unit trap recorded in src/Engine/RESOLVED.md — a mark read without its
// scale once produced a run at 2 Hz that looked entirely normal — and it is worth restating for a
// tool whose inputs are all picohenries and gigahertz. THE ONE DELIBERATE EXCEPTION is electrical
// length, stored in degrees in a field NAMED `…Deg`, on RailTarget's millivolts precedent: the rule
// base SI protects is "a number must not be readable at the wrong scale", and a field whose name
// carries its unit satisfies it. Radians would match the letter and disagree with TLIN's own `E`,
// the schematic, the UI and every textbook, at four conversion sites.
//
// NOTHING HERE DRAWS AND NOTHING HERE EVALUATES. The cascade, the trajectories and the gripper
// inverse are briefs 2 and 3, beside this file; the window is brief 4, above the firewall.

using System.Numerics;

namespace CircuitRF.Design.Smith;

// ── the element vocabulary ───────────────────────────────────────────────────

/// <summary>
/// What one element in the cascade IS. <b>The list is closed, and it is closed because every member
/// maps one to one onto a component <c>ComponentTypeRegistry</c> already declares</b> — see
/// <see cref="SmithComponentMap"/>, which is the single place that mapping is written down.
///
/// <para>That is a scope statement rather than a convenience (overview §1g): a new device type would
/// need a factory registration and a golden-reference test, and this tool needs neither. An element
/// that cannot be spelled as an existing component is an element that does not go in — and it is
/// what makes brief 2's engine oracle and brief 7's paste-into-a-real-schematic possible at all.</para>
/// </summary>
public enum SmithElementKind
{
    R,
    L,
    C,

    /// <summary>Series R-L-C in one part (engine <c>SRLC</c>) — a real capacitor with its ESR and
    /// ESL, rather than three components wired together.</summary>
    Srlc,

    /// <summary>Parallel R-L-C in one part (engine <c>PRLC</c>) — a tank.</summary>
    Prlc,

    /// <summary>A complex impedance, constant over frequency (engine <c>Z_Port</c>, one port). The
    /// frequency independence is the point of it.</summary>
    Z1P,

    /// <summary>A one-port Touchstone file (engine <c>SnP</c>, <c>NumPorts=1</c>).</summary>
    S1P,

    /// <summary>A two-port Touchstone file (engine <c>SnP</c>, <c>NumPorts=2</c>). <b>Series
    /// only</b> — see <see cref="SmithComponentMap.AllowedPlacement"/>.</summary>
    S2P,

    /// <summary>An ideal transmission line in the through path (engine <c>TLIN</c>). Series
    /// only.</summary>
    Tline,

    /// <summary>The same line as a stub with its far end OPEN. Shunt only.</summary>
    StubOpen,

    /// <summary>The same line as a stub with its far end GROUNDED. Shunt only.</summary>
    StubShorted,
}

/// <summary>Where an element sits: in the through path, or from the through path to ground. There
/// are no other placements, no branches and no nesting (§3.2) — that constraint is what makes a
/// per-element trajectory <i>mean</i> something, because a walk across a chart has to be a walk.</summary>
public enum SmithPlacement
{
    Series,
    Shunt,
}

/// <summary>
/// One settable number on an element — what a slider adjusts and what a gripper drag inverts to.
///
/// <para><c>ReferenceFrequency</c> is deliberately absent: a TLIN's F_ref is an editable FIELD on the
/// element's row, not a slider and never a gripper's parameter, because a line whose reference
/// frequency moved under a drag would be a different physical line at every sample (§3.3).</para>
/// </summary>
public enum SmithParameter
{
    /// <summary>No draggable parameter — S1P and S2P, whose value is a file.</summary>
    None,

    R,
    L,
    C,

    /// <summary>A TLIN's characteristic impedance. On a Smith chart it sets WHICH CIRCLE the
    /// rotation happens on, which is why a tool that fixed it at 50 Ω could not draw the most common
    /// move there is (§3.3).</summary>
    Z0,

    /// <summary>A TLIN's electrical length, in DEGREES at its own reference frequency.</summary>
    ElectricalLength,

    /// <summary>The real part of a <see cref="SmithElementKind.Z1P"/>'s impedance.</summary>
    ImpedanceReal,

    /// <summary>The imaginary part of a <see cref="SmithElementKind.Z1P"/>'s impedance.</summary>
    ImpedanceImag,
}

/// <summary>What a slider spans for one parameter. Both ends in the parameter's own base SI unit —
/// or degrees, for <see cref="SmithParameter.ElectricalLength"/>.</summary>
public sealed class SmithSliderRange
{
    public double Min { get; set; }
    public double Max { get; set; }

    public SmithSliderRange() { }
    public SmithSliderRange(double min, double max) { Min = min; Max = max; }
}

/// <summary>
/// Every value any element can carry, in ONE flat record.
///
/// <para><b>Deliberately not a per-kind class hierarchy</b> (R-smith1-1a). The window's slider panel,
/// the <c>.csmith</c> reader and brief 7's paste recognizer all have to ask "does this element have
/// an <c>L</c>?", and a polymorphic answer to that question turns three call sites into nine. Which
/// fields a given kind actually USES is <see cref="SmithComponentMap.Parameters"/>'s answer, in one
/// place, and the unused ones are simply not read.</para>
/// </summary>
public sealed class SmithElementValues
{
    /// <summary>Resistance, OHMS.</summary>
    public double ROhm { get; set; }

    /// <summary>Inductance, HENRIES. A picohenry is <c>1e-12</c>, not <c>1</c>.</summary>
    public double LHenry { get; set; }

    /// <summary>Capacitance, FARADS. A picofarad is <c>1e-12</c>, not <c>1</c>.</summary>
    public double CFarad { get; set; }

    /// <summary>A TLIN's characteristic impedance, OHMS.</summary>
    public double Z0Ohm { get; set; } = 50.0;

    /// <summary>A TLIN's electrical length, DEGREES at <see cref="ReferenceFrequencyHz"/>. The one
    /// named exception to base SI — see this file's header.</summary>
    public double ElectricalLengthDeg { get; set; }

    /// <summary>
    /// The frequency a TLIN's length is quoted at, HERTZ, so that θ(f) = (π/180)·E·f/F_ref.
    ///
    /// <para><b>It does not follow the design frequency.</b> It defaults to it at the moment the
    /// element is placed and stays put: a line that silently re-specified itself whenever the user
    /// retuned the chart would be a different physical line each time, and the load points would
    /// stop meaning anything (§3.3).</para>
    /// </summary>
    public double ReferenceFrequencyHz { get; set; }

    /// <summary>A <see cref="SmithElementKind.Z1P"/>'s impedance, OHMS — a complex constant with no
    /// frequency dependence.</summary>
    public Complex ImpedanceOhm { get; set; }
}

/// <summary>One element of the cascade. Index 0 is nearest the generator (§3.2).</summary>
public sealed class SmithElement
{
    public SmithElementKind Kind { get; set; } = SmithElementKind.C;

    public SmithPlacement Placement { get; set; } = SmithPlacement.Series;

    /// <summary>The instance name — unique within the design, and the name every refusal and every
    /// undo entry says.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Whether it contributes. <b>A disabled element is not a deleted one</b>: it draws no
    /// trajectory, contributes nothing, and keeps its values and its place (§3.2). One boolean, and
    /// it is the difference between trying something and losing it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which parameter this element's gripper drags. <see cref="SmithParameter.None"/> on
    /// the two file-valued kinds, which have no gripper at all.</summary>
    public SmithParameter ActiveParameter { get; set; } = SmithParameter.None;

    public SmithElementValues Values { get; set; } = new();

    /// <summary>
    /// The Touchstone file, <b>relative to the document</b> — S1P and S2P only, and nothing else
    /// carries one (<see cref="SmithDesign.Refusal"/>).
    ///
    /// <para>This is the opposite choice from the GENERATOR's <see cref="SmithGenerator.SourcePath"/>
    /// and deliberately so: a file element IS its file, the way a `.cdd` trace is its source, while
    /// the generator's import copies values in and keeps the path as provenance only (§3.1).</para>
    /// </summary>
    public string? FileRef { get; set; }

    /// <summary>What each of this element's sliders spans. Absent for a parameter nobody has
    /// re-ranged; the window then picks a range from the value, which is brief 6's business.</summary>
    public IDictionary<SmithParameter, SmithSliderRange> SliderRange { get; }
        = new Dictionary<SmithParameter, SmithSliderRange>();

    /// <summary>
    /// This element's own half of <see cref="SmithDesign.Refusal"/> — the first thing wrong with it,
    /// naming it, or null.
    ///
    /// <para><b>The placement rule is checked GENERALLY, not just for the shunt S2P the brief names.</b>
    /// R-smith1-2's reason — <i>a 2-port with its second port grounded is a different component than
    /// the one the user placed</i> — is exactly as true of a TLIN in shunt and of an open stub in
    /// series, and both of those are documents <c>SmithCascade</c> has no formula for. One rule
    /// reading <see cref="SmithComponentMap.AllowedPlacement"/> closes all three and cannot fall out
    /// of step with the table brief 6 draws from.</para>
    /// </summary>
    public string? Refusal()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "An element has no name — a name is what every refusal, every slider and every "
                 + "undo entry says about it.";

        if (SmithComponentMap.AllowedPlacement(Kind) is { } only && only != Placement)
            return $"'{Name}' is {Kind} in {Placement}, and a {Kind} goes in {only} only — "
                 + (Kind == SmithElementKind.S2P
                        ? "a 2-port with its second port grounded is a different component than the "
                        + "one you placed, and a shunt one-port is what S1P and Z1P are for."
                        : "the stub kinds are the shunt spelling of a line and Tline is the series "
                        + "one.");

        bool wantsFile = SmithComponentMap.UsesFile(Kind);
        if (wantsFile && string.IsNullOrWhiteSpace(FileRef))
            return $"'{Name}' is a {Kind} and names no file — a file element IS its file.";
        if (!wantsFile && !string.IsNullOrWhiteSpace(FileRef))
            return $"'{Name}' is a {Kind} and carries a file reference ('{FileRef}') — only S1P and "
                 + "S2P read a file, and a reference nothing resolves is one nobody would ever be "
                 + "told had gone stale.";

        // R, L and C are non-negative wherever a kind actually USES them. Checking the whole flat
        // Values bag instead would refuse a document over a number the element does not read —
        // SmithElementValues is one record on purpose (R-smith1-1a), and the price of that is that
        // which fields matter is SmithComponentMap's answer rather than the record's own shape.
        foreach (var p in SmithComponentMap.Parameters(Kind))
        {
            double v = p switch
            {
                SmithParameter.R => Values.ROhm,
                SmithParameter.L => Values.LHenry,
                SmithParameter.C => Values.CFarad,
                _                => 0.0,
            };

            if (p is SmithParameter.R or SmithParameter.L or SmithParameter.C && !(v >= 0))
                return $"'{Name}' has {p} = {SmithDesign.Fmt(v)} — a negative or undefined "
                     + $"{(p == SmithParameter.R ? "resistance" : p == SmithParameter.L ? "inductance" : "capacitance")} "
                     + "is not a component anyone can build, and the trajectory it would draw is a "
                     + "true picture of a nonsensical question.";
        }

        if (SmithComponentMap.IsLine(Kind))
        {
            if (!(Values.Z0Ohm > 0))
                return $"'{Name}' has Z0 = {SmithDesign.Fmt(Values.Z0Ohm)} Ω — a line's "
                     + "characteristic impedance is what sets which circle the rotation happens on, "
                     + "so it has to be positive.";

            if (!(Values.ElectricalLengthDeg >= 0))
                return $"'{Name}' has an electrical length of "
                     + $"{SmithDesign.Fmt(Values.ElectricalLengthDeg)}° — a line has a non-negative "
                     + "length, and a negative one is a shorter line pointing the other way.";

            if (!(Values.ReferenceFrequencyHz > 0))
                return $"'{Name}' quotes its electrical length at "
                     + $"{SmithDesign.Fmt(Values.ReferenceFrequencyHz)} Hz — the length scales as "
                     + "f/F_ref, so F_ref has to be a positive frequency.";
        }

        return null;
    }
}

// ── the generator ────────────────────────────────────────────────────────────

/// <summary>One row of the generator table: a frequency and a complex impedance (§3.1).</summary>
public sealed class SmithGeneratorRow
{
    /// <summary>HERTZ.</summary>
    public double FrequencyHz { get; set; }

    /// <summary>OHMS.</summary>
    public double ResistanceOhm { get; set; }

    /// <summary>OHMS.</summary>
    public double ReactanceOhm { get; set; }

    public SmithGeneratorRow() { }

    public SmithGeneratorRow(double frequencyHz, double resistanceOhm, double reactanceOhm)
    {
        FrequencyHz   = frequencyHz;
        ResistanceOhm = resistanceOhm;
        ReactanceOhm  = reactanceOhm;
    }

    /// <summary>The row as an impedance.</summary>
    public Complex Impedance => new(ResistanceOhm, ReactanceOhm);
}

/// <summary>
/// The generator — <b>an impedance and nothing else</b> (§3.1). No available power, no dBm: every
/// quantity in this tool is a linear immittance, and a "gain" readout would be inventing a quantity
/// the model does not have.
/// </summary>
public sealed class SmithGenerator
{
    /// <summary>At least one row, sorted by frequency, frequencies unique. A single row is the
    /// ordinary case ("50 Ω at 2 GHz"); a table is what makes the per-frequency load points
    /// interesting.</summary>
    public IList<SmithGeneratorRow> Rows { get; } = new List<SmithGeneratorRow>();

    /// <summary>
    /// Where the rows were imported from, <b>for display and for a Re-import button — provenance
    /// only, and nothing resolves it at load</b> (§3.1, R-smith1-7).
    ///
    /// <para>The import copies the VALUES in, so a `.csmith` is portable on its own and an archived
    /// or moved workspace cannot break it. This is the opposite of both
    /// <see cref="SmithElement.FileRef"/> and the overlays, and deliberately: an overlay is
    /// reference material the user is comparing against, while the generator is part of the design,
    /// and a design that stops opening because a file moved is a design that was never
    /// portable.</para>
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Negate every row's reactance, in place.
    ///
    /// <para><b>A one-shot edit, never a persistent flag</b> (§3.1). A flag would mean the number in
    /// the table and the number the tool uses disagree, and there is no way to display that which
    /// does not eventually mislead someone. Brief 4 makes it an undo entry; here it is a method and
    /// applying it twice is the identity.</para>
    /// </summary>
    public void Conjugate()
    {
        foreach (var row in Rows) row.ReactanceOhm = -row.ReactanceOhm;
    }

    /// <summary>The table's span, or null when there are no rows. Rows are sorted, so this is the
    /// first and the last — and it is what a design-frequency refusal names.</summary>
    public (double StartHz, double StopHz)? Span
        => Rows.Count == 0 ? null : (Rows[0].FrequencyHz, Rows[^1].FrequencyHz);

    /// <summary>
    /// The generator's own half of <see cref="SmithDesign.Refusal"/>: at least one row, sorted, and
    /// frequencies unique.
    ///
    /// <para><b>A duplicate frequency is a refusal naming the frequency, never a silent
    /// last-wins</b> (§3.1). Two rows at one frequency are two different answers to the question the
    /// interpolator asks, and picking either of them quietly is how a design comes to be tuned
    /// against a number nobody typed.</para>
    /// </summary>
    public string? Refusal()
    {
        if (Rows.Count == 0)
            return "The generator table is empty — this tool starts from an impedance, so it needs "
                 + "at least one row of one.";

        for (int i = 1; i < Rows.Count; i++)
        {
            double previousHz = Rows[i - 1].FrequencyHz;
            double thisHz     = Rows[i].FrequencyHz;

            if (thisHz == previousHz)
                return $"The generator table has two rows at {SmithDesign.Fmt(thisHz)} Hz — one "
                     + "frequency is one impedance, and two answers there is not something this "
                     + "tool may pick between on its own.";

            if (thisHz < previousHz)
                return $"The generator table is out of order: {SmithDesign.Fmt(thisHz)} Hz follows "
                     + $"{SmithDesign.Fmt(previousHz)} Hz. Rows are sorted by frequency, which is "
                     + "what makes the span an interpolator can be asked about.";
        }

        return null;
    }
}

// ── the chart, the sweep, the arcs, the view ─────────────────────────────────

/// <summary>The Γ extents the chart opens on, or null for "fit". In Γ, which is dimensionless — the
/// ordinary Smith square is (−1,−1)…(1,1).</summary>
public sealed class SmithWindow
{
    public double MinX { get; set; } = -1.0;
    public double MinY { get; set; } = -1.0;
    public double MaxX { get; set; } =  1.0;
    public double MaxY { get; set; } =  1.0;
}

/// <summary>What the chart is, and what is drawn on it.</summary>
public sealed class SmithChartSettings
{
    /// <summary>
    /// <b>A single, real, document-wide reference impedance</b> (§3.4, owner decision), default
    /// 50 Ω. Γ = (Z − Z₀)/(Z + Z₀), the grid is the ordinary fixed Smith grid, and every overlay is
    /// renormalized to this on the way in.
    ///
    /// <para>A generator-referenced, moving normalization was considered and rejected: it makes the
    /// grid's meaning change under the user's hands whenever the generator or the design frequency
    /// is edited. What it would have bought is bought instead by the conjugate-match TARGET glyphs
    /// (<see cref="ShowTargets"/>), at none of that cost.</para>
    /// </summary>
    public double Z0Ohm { get; set; } = 50.0;

    /// <summary>
    /// HERTZ. What the trajectories are drawn at, what the sliders' reactances are computed at, and
    /// what the readout strip reports. <b>It is free</b> — it need not be a row of the generator
    /// table — but it must lie inside the table's span, because Z_gen is interpolated and never
    /// extrapolated (§3.6, and <see cref="SmithDesign.Refusal"/>).
    /// </summary>
    public double DesignFrequencyHz { get; set; }

    /// <summary>Where the chart is scrolled and zoomed to, or null for "fit".</summary>
    public SmithWindow? Window { get; set; }

    public bool ShowGrippers { get; set; } = true;

    /// <summary>The faint, un-selectable conjugate-match glyphs at Γ(conj(Z_gen(f))) — one per
    /// generator-table row. Landing a frequency's load point on its target IS the conjugate
    /// match (§3.4).</summary>
    public bool ShowTargets { get; set; } = true;

    /// <summary>The per-frequency load-point label boxes.</summary>
    public bool ShowLabels { get; set; } = true;
}

/// <summary>The optional swept band drawn through the load points (§3.6). Off by default: it is P2
/// material and none of it is needed to match an impedance.</summary>
public sealed class SmithSweep
{
    public bool   Enabled { get; set; }
    public double StartHz { get; set; }
    public double StopHz  { get; set; }
    public int    Points  { get; set; } = 51;
}

/// <summary>The constant-Q arc pair (§4.4). <c>Q</c> is |x|/r on the drawn samples.</summary>
public sealed class SmithConstantQ
{
    public bool   Enabled { get; set; }
    public double Q       { get; set; } = 1.0;
}

/// <summary>Where the window was left. Nothing here changes what the design MEANS — it is the
/// `.cdd`'s own convention, and a document opened on another machine simply opens where it was
/// saved.</summary>
public sealed class SmithView
{
    /// <summary>The chart / network splitter, as a fraction of the window.</summary>
    public double SplitterMain { get; set; } = 0.65;

    /// <summary>The network / generator-panel splitter, same units.</summary>
    public double SplitterSide { get; set; } = 0.5;

    public double NetworkScrollX { get; set; }
    public double NetworkScrollY { get; set; }
    public double NetworkZoom    { get; set; } = 1.0;

    /// <summary>
    /// Draw the network right-to-left, so the generator sits on the RIGHT (owner instruction, §5.5).
    ///
    /// <para><b>It is a flag and a sign, not geometry work</b> (overview §1c): brief 6 negates the
    /// direction the projection's x-cursor advances and sets <c>MirrorX</c> on each component, which
    /// <c>SchematicGeometry.LocalToWorld</c> already honours for pin coordinates as well as for the
    /// glyph. It is view-only and changes no value in this document.</para>
    /// </summary>
    public bool MirrorNetwork { get; set; }
}

// ── overlays and markers ─────────────────────────────────────────────────────

/// <summary>Where an overlay's data comes from.</summary>
public enum SmithOverlaySource
{
    /// <summary>A Touchstone file, by a path RELATIVE TO THE DOCUMENT — the `.cdd` convention, and
    /// the one that survives an archived or moved workspace.</summary>
    TouchstoneFile,

    /// <summary>A cube in an open <c>DataSet</c>, referenced the way a Data Display trace card
    /// references one.</summary>
    Cube,
}

/// <summary>
/// One piece of reference material under the work (§5.7). <b>Not part of the cascade</b>: it carries
/// its own colour and style, it may carry markers, and it is excluded from autoscale unless the row
/// says otherwise.
///
/// <para><see cref="Quantity"/> and <see cref="Derived"/> are STRINGS naming members of the Data
/// Display's own <c>DerivedParameters</c> and its quantity spelling, because those types live in
/// <c>src/Render</c> and this project is below it. Brief 8 parses them where it can see the enum;
/// the wire bytes are the same either way, which is the property that matters to a format.</para>
/// </summary>
public sealed class SmithOverlayRef
{
    public SmithOverlaySource SourceKind { get; set; } = SmithOverlaySource.TouchstoneFile;

    /// <summary>The relative path, or the cube reference.</summary>
    public string Source { get; set; } = "";

    /// <summary>A raw S-parameter (<c>S11</c>, <c>S22</c>, …) or a virtual Z/Y spelling. Empty when
    /// <see cref="Derived"/> carries the answer instead.</summary>
    public string Quantity { get; set; } = "";

    /// <summary>A <c>DerivedParameters</c> member name — <c>SourceStabilityCircle</c> and
    /// <c>LoadStabilityCircle</c> are the two this tool was asked for by name. <c>None</c> means the
    /// raw <see cref="Quantity"/>.</summary>
    public string Derived { get; set; } = "None";

    /// <summary>Renormalize to <see cref="SmithChartSettings.Z0Ohm"/> on the way in (§3.4). True for
    /// everything the chart draws; false is a deliberate "show me the file's own numbers".</summary>
    public bool Renormalize { get; set; } = true;

    public bool Visible { get; set; } = true;

    /// <summary>Excluded from the chart's autoscale unless this says otherwise (§5.7).</summary>
    public bool IncludeInAutoscale { get; set; }

    /// <summary><c>#rrggbb</c>, or null for the palette's next colour.</summary>
    public string? ColorHex { get; set; }

    public bool Dashed { get; set; }
}

/// <summary>
/// One marker on the chart.
///
/// <para><b>This is the Data Display's own <c>MarkerConfig</c> shape, field for field and default for
/// default</b> (§7). It is a separate type only because <c>MarkerConfig</c> lives in
/// <c>src/Render</c>, which this project is below — so the four enums are held as their own member
/// NAMES, which is exactly what a `.cdd` writes for them under
/// <c>JsonStringEnumConverter</c>. The bytes a `.csmith` puts on disk for a marker are therefore the
/// bytes a `.cdd` puts on disk for the same marker, and brief 8's mapping is one
/// <c>Enum.Parse</c> per field rather than a translation.</para>
///
/// <para><b>A marker is a reading somebody took, and a reading is worth keeping</b> — the same
/// reason <c>RailMarker</c> exists.</para>
/// </summary>
public sealed class SmithMarker
{
    /// <summary>
    /// <b>Which curve this marker is a reading ON</b> — the trace's LABEL, which is an element's
    /// name for a trajectory, <c>load</c> for the load points, or an overlay's file and quantity.
    ///
    /// <para>The one field <c>MarkerConfig</c> does not have, and it is not an invention: a `.cdd`
    /// nests its markers under their trace, so the association is that file's own structure. A
    /// `.csmith` has no trace list to nest them in — every trace on this chart is DERIVED and is
    /// rebuilt from the design on each edit — so the association has to be written down, and it is
    /// written as the label rather than as an index because an index moves when an element is
    /// deleted and a marker that silently jumped to the next curve would be a reading reported
    /// against the wrong thing. Empty means the first curve on the chart.</para>
    /// </summary>
    public string TraceName { get; set; } = "";

    public string Name  { get; set; } = "m0";
    public int    Index { get; set; }

    /// <summary>The marker's frequency, in the unit <see cref="FreqUnits"/> names — the Data
    /// Display's own convention for <c>Marker.Freq</c>, kept verbatim rather than converted, because
    /// a converted copy would be a second spelling of one number.</summary>
    public double Freq { get; set; }

    /// <summary>A <c>FreqUnit</c> member name.</summary>
    public string FreqUnits { get; set; } = "GHz";

    /// <summary>A <c>MatrixFormat</c> member name — how the Γ/S value is spelled.</summary>
    public string MatrixFormat { get; set; } = "MA";

    /// <summary>A <c>MatrixFormat</c> member name — how the IMPEDANCE row is spelled, independently
    /// of <see cref="MatrixFormat"/>.</summary>
    public string MatrixFormatImpedance { get; set; } = "RI";

    /// <summary>A <c>MarkerStyle</c> member name.</summary>
    public string Style { get; set; } = "Medium";

    public bool UseNormalizedImpedance { get; set; } = true;
    public int  MaximumFractionDigits  { get; set; } = 4;

    /// <summary>Info-box position in logical pixels relative to the plot container.</summary>
    public double InfoBoxX { get; set; }
    public double InfoBoxY { get; set; }

    public bool IsMulti { get; set; }
    public bool IsDelta { get; set; }

    public float PositionStaticX { get; set; }
    public float PositionStaticY { get; set; }

    /// <summary>A <c>MarkerKind</c> member name.</summary>
    public string MarkerKind { get; set; } = "Polyline";

    public bool   ShowInfoBox    { get; set; } = true;
    public bool   ContourSnapped { get; set; }

    /// <summary>The constant-VSWR circle about this marker. <b>Not centred on the marker</b> unless
    /// the marker is at Γ = 0 — the correction <c>vswr-locus-gamma-plane.md</c> records, repeated
    /// here because this tool invites the same mistake.</summary>
    public bool   VswrEnabled { get; set; }
    public double VswrValue   { get; set; } = 2.0;
}

// ── the document ─────────────────────────────────────────────────────────────

/// <summary>
/// A `.csmith` — a generator impedance and an ordered cascade of two-pin elements, plus what the
/// chart is normalized to and what is drawn on it.
///
/// <para><b>Nothing here evaluates anything.</b> <c>SmithCascade</c> (brief 2) takes one of these and
/// returns impedances; this type is the document and its well-formedness.</para>
/// </summary>
public sealed class SmithDesign
{
    public string Name { get; set; } = "";

    public SmithChartSettings Chart     { get; set; } = new();
    public SmithGenerator     Generator { get; set; } = new();

    /// <summary>The cascade, index 0 nearest the generator.</summary>
    public IList<SmithElement> Elements { get; } = new List<SmithElement>();

    public SmithSweep     Sweep     { get; set; } = new();
    public SmithConstantQ ConstantQ { get; set; } = new();

    public IList<SmithOverlayRef> Overlays { get; } = new List<SmithOverlayRef>();
    public IList<SmithMarker>     Markers  { get; } = new List<SmithMarker>();

    public SmithView View { get; set; } = new();

    /// <summary>
    /// <b>The first thing wrong with this document, as a sentence naming the offending object, or
    /// null.</b> <c>RailDocument.Refusal</c>'s pattern, and <see cref="SmithDesignIo.Serialize"/>
    /// throws on it rather than writing: §7 — <i>a document that cannot be read back is a document
    /// that was never written</i>, and the alternative is a file on the user's disk whose only
    /// symptom is that it refuses to open next week.
    ///
    /// <para><b>Order is not arbitrary.</b> The generator table is checked first because the design
    /// frequency's rule is stated against its span, and an element's rules are checked in cascade
    /// order so that the first refusal names the first element a reader would reach.</para>
    /// </summary>
    public string? Refusal()
    {
        if (Generator.Refusal() is { } g) return g;

        // The design frequency lies inside the table's span, because Z_gen is INTERPOLATED and never
        // extrapolated (§3.6). One row is the stated exception: one row means one impedance, flat,
        // and every design frequency is legal against it.
        if (Generator.Rows.Count > 1)
        {
            var (startHz, stopHz) = Generator.Span!.Value;
            if (!(Chart.DesignFrequencyHz >= startHz && Chart.DesignFrequencyHz <= stopHz))
                return $"The design frequency {Fmt(Chart.DesignFrequencyHz)} Hz is outside the "
                     + $"generator table's span {Fmt(startHz)} Hz to {Fmt(stopHz)} Hz — the "
                     + "generator impedance is interpolated between rows, never extrapolated past "
                     + "them.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Elements)
        {
            if (e.Refusal() is { } r) return r;

            if (!seen.Add(e.Name))
                return $"Two elements are both called '{e.Name}' — an element name is what every "
                     + "refusal, every slider and every undo entry says, so it has to name one of "
                     + "them.";
        }

        if (Sweep.Enabled)
        {
            if (!(Sweep.StartHz < Sweep.StopHz))
                return $"The swept band starts at {Fmt(Sweep.StartHz)} Hz and stops at "
                     + $"{Fmt(Sweep.StopHz)} Hz — a band has to go somewhere.";

            if (Sweep.Points < 2)
                return $"The swept band asks for {Sweep.Points} point(s); two is the fewest that "
                     + "draws a band.";
        }

        if (ConstantQ.Enabled && !(double.IsFinite(ConstantQ.Q) && ConstantQ.Q > 0))
            return $"The constant-Q arcs are on at Q = {Fmt(ConstantQ.Q)}; Q is |x|/r and has to be "
                 + "a finite positive number.";

        return null;
    }

    /// <summary>The house number spelling for a refusal: enough digits to identify the value the
    /// user typed, and never scientific notation for an ordinary one.</summary>
    internal static string Fmt(double v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
}
