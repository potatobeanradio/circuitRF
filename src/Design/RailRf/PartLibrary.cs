// The part library — the document PARTS ARE IDENTIFIED THROUGH
// (docs/sonnet-briefs/brief-railrf-2-companion-readers.md §3, railrf.md §2.2, Q-11, Q-12, Q-15).
//
// IT IS KEYED BY INTERNAL PART NUMBER, AND THAT IS A MODELLING DECISION RATHER THAN A SCHEMA ONE
// (R-rail2-7). §2.2: "Parts are identified by PART NUMBER, and that is what the model attaches to."
// The bill of materials carries an internal ordering number per part and behind that number sits a
// list of approved manufacturers with their own item codes — so the file splits in two, and this is
// the half that holds the MODEL. A model is entered once and used twenty-two times, which is what
// makes the parts table tractable and what makes a correction ONE EDIT. The same rule holds at DC:
// one on-resistance for every instance of the same FET.
//
// L IS DERIVED, AND IT IS DERIVED ONCE (R-rail2-8). A row carries C and the self-resonant frequency,
// from which L = 1/((2·π·f0)^2·C) follows exactly — a 1 µF part resonating at 5.31 MHz is 898 pH, a
// 33 nF part at 39.1 MHz is 502 pH. railRF reads the table directly and DERIVES L rather than asking
// for it twice. A row carrying C, f0 AND L is the interesting case: THE DERIVED VALUE IS USED, the
// stated one is COMPARED, and a disagreement beyond the tolerance below is reported on the row. That
// is §7's "derived part inductance" gate — an arithmetic gate on the importer, not on the physics.
//
// THE LIBRARY CARRIES NO ESR, AND THAT IS A PERMANENT CONDITION (Q-15, R-rail2-9). There is no
// per-part ESR figure to be had, so a dissipation-factor default per dielectric class IS THE BASIS
// rather than a stopgap, and every peak height computed from one is marked INDICATIVE wherever it
// appears. THIS FILE COMPUTES NONE OF IT — brief 11 does. What it owns is that the model can
// represent all three states honestly, and that a row with neither an ESR, a class NOR a file
// RESOLVES TO NOTHING and is counted, because "a mask margin in dB computed from an indicative peak
// looks exactly as authoritative as a real one."
//
// NUMBERS ARE BASE SI. Farads, hertz, henries, volts, ohms. RailDocumentIo's own header states why:
// a mark read without its scale once produced a run at 2 Hz that looked entirely normal.

namespace CircuitRF.Design.RailRf;

/// <summary>Where a part's ESR came from. Stated on the part row, on the plot, in the anti-resonance
/// table and in the provenance of every export (Q-15).</summary>
public enum EsrProvenance
{
    /// <summary>From the part's own Touchstone file. The only route to a real number (§2.2).</summary>
    Measured,

    /// <summary>Stated on the library row.</summary>
    Stated,

    /// <summary>Derived from a dissipation-factor default for this part's dielectric class.
    /// <b>The normal case, not the degraded one</b> (Q-15) — and every peak height computed from one
    /// is marked INDICATIVE wherever it appears.</summary>
    ClassDefault,
}

/// <summary>Which of the two sources of a part's model won (Q-11, R-rail2-11). Shown in the parts
/// table's <i>model source</i> column, because "both" is only an honest answer if a reader can see
/// which one was used.</summary>
public enum PartModelSource
{
    /// <summary>The library row's own C, f₀ and ESR.</summary>
    LibraryRow,

    /// <summary>A per-part file attached to the row — a Touchstone or a SPICE model. <b>It overrides
    /// the row</b>, which is what Q-11 closed.</summary>
    AttachedFile,
}

/// <summary>One point of a capacitance-versus-bias curve. BASE SI: volts and farads.</summary>
public sealed record PartBiasPoint(double BiasVolts, double CapacitanceFarads);

/// <summary>
/// One library row, keyed by <see cref="PartNumber"/>.
///
/// <para>Every electrical field is optional, because a real maintained table is partly populated and
/// §9's whole point is that partial population must be VISIBLE rather than filled in.</para>
/// </summary>
public sealed class PartLibraryRow
{
    /// <summary>The internal part number. The key, and the thing the model attaches to (§2.2).</summary>
    public string PartNumber { get; set; } = "";

    /// <summary>Free text, as the maintained table carries it.</summary>
    public string? Description { get; set; }

    /// <summary>The footprint this part is bought in. Carried for the parts table and for the
    /// footprint cross-check; nothing here resolves it to artwork.</summary>
    public string? Footprint { get; set; }

    /// <summary>X7R, X5R, C0G/NP0, … <b>Null where the table does not state one</b>, and null is
    /// load-bearing: brief 11's ESR fallback keys on this, and a null one produces a part marked as
    /// having no class rather than a part quietly given X7R's dissipation factor.</summary>
    public string? DielectricClass { get; set; }

    /// <summary>
    /// The <see cref="DielectricClass"/> that says this part is <b>not a capacitor</b> — a ferrite
    /// bead, a resistor. Stated rather than inferred from an empty capacitance, because a row with
    /// no capacitance is far more often a capacitor nobody has filled in yet (a seeded row).
    /// </summary>
    /// <remarks>
    /// Field report, 2026-09-23: a bead and a sense resistor were counted as parts missing a bias
    /// curve, and the designer asked for "Other" on the class drop-down.
    ///
    /// <para><b>An <c>Other</c> row is a SERIES ELEMENT's model</b> (brief 35, R-rail35-2a) — what a
    /// rail part marked series inherits wherever its own row states nothing
    /// (<see cref="RailSeriesModel.Resolve"/>). It reads exactly two fields:</para>
    /// <list type="bullet">
    /// <item><see cref="EsrOhms"/> is its <b>DC resistance</b> — the resistance the load current runs
    ///   through, a bead's DCR or a switch's on-resistance.</item>
    /// <item><see cref="ModelRef"/>, where it is a Touchstone file, is its <b>measured impedance</b>
    ///   over frequency — a TWO-PORT read SERIES-thru, as supplier tools publish a bead's curve. A
    ///   SPICE model is not read for a series element.</item>
    /// </list>
    /// <para><b>Everything else is ignored</b> on an <c>Other</c> row: the capacitance, the
    /// self-resonance, the stated inductance, the bias curve and the dielectric class are a
    /// capacitor's. There is deliberately <b>no impedance-at-one-frequency field</b> (R-rail35-2c):
    /// "220 Ω at 100 MHz" does not determine an R-L — the split between R and ωL is unknown, and any
    /// split chosen would be a guess that prints as a model. A bead's impedance is its Touchstone
    /// curve, or an R-L the rail row states.</para>
    ///
    /// <para><b>A SHUNT rail part whose row is <c>Other</c> is refused at Run</b> (R-rail35-2d): it
    /// is not a capacitor and it is not marked series, which is exactly the state a bead added with
    /// the parts pane's + is in.</para>
    /// </remarks>
    public const string OtherClass = "Other";

    /// <summary>False where the row's class is <see cref="OtherClass"/>. A bias curve, and the
    /// counts of parts lacking one, are about capacitors only.</summary>
    public bool IsCapacitor =>
        !string.Equals(DielectricClass?.Trim(), OtherClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>The voltage rating, in VOLTS. What derating (Q-12) is judged against.</summary>
    public double? VoltageRatingV { get; set; }

    /// <summary>The marked capacitance, in FARADS.</summary>
    public double? CapacitanceFarads { get; set; }

    /// <summary>The self-resonant frequency, in HERTZ. With <see cref="CapacitanceFarads"/> this is
    /// what <see cref="DerivedInductanceHenries"/> is derived from.</summary>
    public double? SelfResonantFrequencyHz { get; set; }

    /// <summary>An inductance the table states, in HENRIES. <b>Compared, never used</b> — see
    /// <see cref="InductanceDisagreement"/> and R-rail2-8.</summary>
    public double? StatedInductanceHenries { get; set; }

    /// <summary>An ESR the row states, in OHMS. Null is the normal case (Q-15).</summary>
    public double? EsrOhms { get; set; }

    /// <summary>A per-part Touchstone or SPICE model, relative to the library file. <b>It overrides
    /// the row</b> (Q-11, R-rail2-11), and it is the only route to a real ESR.</summary>
    public string? ModelRef { get; set; }

    /// <summary>The capacitance-versus-bias curve, where one has been obtained (Q-12). Empty is
    /// ordinary, and <see cref="PartLibrary.Coverage"/> is what makes the emptiness visible — "a
    /// library that is only partly populated with curves produces a result that is partly derated,
    /// and that is worse than either extreme unless it is visible" (§9).</summary>
    public List<PartBiasPoint> BiasCurve { get; } = [];

    /// <summary>
    /// R-rail2-8: <c>L = 1/((2·π·f₀)²·C)</c>, in HENRIES. Null where the row does not carry both.
    /// <b>This is the value used.</b>
    /// </summary>
    public double? DerivedInductanceHenries
    {
        get
        {
            if (CapacitanceFarads is not { } c || SelfResonantFrequencyHz is not { } f) return null;
            if (!(c > 0) || !(f > 0)) return null;
            double w = 2 * Math.PI * f;
            return 1.0 / (w * w * c);
        }
    }

    /// <summary>The mounting-loop-free series inductance this row contributes: the derived one where
    /// it exists, otherwise the stated one. <b>Derived first</b> — the stated value is the one that
    /// is compared.</summary>
    public double? InductanceHenries => DerivedInductanceHenries ?? StatedInductanceHenries;

    /// <summary>How far apart the derived and the stated inductance are, as a FRACTION of the derived
    /// one, or null where the row does not carry both. <see cref="PartLibrary.InductanceTolerance"/>
    /// is what a report calls a disagreement.</summary>
    public double? InductanceDisagreement =>
        DerivedInductanceHenries is { } derived && StatedInductanceHenries is { } stated && derived > 0
            ? Math.Abs(stated - derived) / derived
            : null;

    /// <summary>
    /// R-rail2-9. Which of the three states this row's ESR is in, or <b>null where it is in none of
    /// them</b> — a row with no ESR, no attached file and no dielectric class resolves to nothing,
    /// and <see cref="PartLibrary.Coverage"/> counts those.
    ///
    /// <para>The order is Q-11's: <b>the file overrides the row.</b></para>
    /// </summary>
    public EsrProvenance? EsrBasis =>
        ModelRef is { Length: > 0 } && PartLibrary.IsTouchstone(ModelRef) ? EsrProvenance.Measured
        : EsrOhms is not null ? EsrProvenance.Stated
        : DielectricClass is { Length: > 0 } ? EsrProvenance.ClassDefault
        : null;

    /// <summary>Which source this row's model comes from (R-rail2-11). Reported per part, because
    /// that is what the parts table's <i>model source</i> column shows.</summary>
    public PartModelSource ModelSource =>
        ModelRef is { Length: > 0 } ? PartModelSource.AttachedFile : PartModelSource.LibraryRow;

    /// <summary>Null when this row is well formed, or the sentence saying why not.</summary>
    public string? Refusal()
    {
        if (string.IsNullOrWhiteSpace(PartNumber))
            return "A part library row has no part number. The part number is the key the model " +
                   "attaches to, so every row carries one.";
        if (CapacitanceFarads is { } c && !(c > 0))
            return $"Part '{PartNumber}' states a capacitance of {c} F. State it in farads, above zero.";
        if (SelfResonantFrequencyHz is { } f && !(f > 0))
            return $"Part '{PartNumber}' states a self-resonant frequency of {f} Hz. State it in " +
                   "hertz, above zero.";
        if (EsrOhms is { } r && r < 0)
            return $"Part '{PartNumber}' states an ESR of {r} Ω. State it in ohms, at or above zero.";
        foreach (var point in BiasCurve)
            if (!(point.CapacitanceFarads > 0))
                return $"Part '{PartNumber}' has a bias-curve point at {point.BiasVolts} V stating " +
                       $"{point.CapacitanceFarads} F. State each point's capacitance in farads, above zero.";
        return null;
    }
}

/// <summary>
/// R-rail2-10's answer, as first-class numbers. <b>Coverage is a reported quantity, not a
/// footnote</b>: §9 names the residual risk precisely — "a library that is only partly populated with
/// curves produces a result that is partly derated, and that is worse than either extreme unless it
/// is visible."
/// </summary>
/// <param name="Referenced">Distinct part numbers asked about.</param>
/// <param name="Known">How many of them the library has a row for.</param>
/// <param name="Unknown">Part numbers with no row at all, by part number.</param>
/// <param name="WithoutBiasCurve">Known part numbers carrying no capacitance-versus-bias curve, by
/// part number — brief 7's status strip prints the count ("3 parts with no bias curve"), brief 11
/// applies it and brief 12's export carries it.</param>
/// <param name="WithoutEsrBasis">Known part numbers whose ESR resolves to NOTHING — no attached file,
/// no stated ESR and no dielectric class. Brief 11 reports this as a headline number, because a mask
/// margin in dB computed from an indicative peak looks exactly as authoritative as a real one.</param>
/// <param name="Indicative">Known part numbers whose ESR resolves to a class default — the NORMAL
/// case (Q-15), and the set every peak height is marked indicative from.</param>
/// <param name="NotCapacitors">Known part numbers whose row is classed
/// <see cref="PartLibraryRow.OtherClass"/>. In neither bias-curve count, since a curve is a
/// capacitor's.</param>
public sealed record PartLibraryCoverage(
    int Referenced,
    int Known,
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> WithoutBiasCurve,
    IReadOnlyList<string> WithoutEsrBasis,
    IReadOnlyList<string> Indicative,
    IReadOnlyList<string> NotCapacitors)
{
    public int WithBiasCurve => Known - NotCapacitors.Count - WithoutBiasCurve.Count;

    /// <summary>The sentence brief 7's status strip prints.</summary>
    public string Summary =>
        $"{Known} of {Referenced} part number(s) are in the library" +
        (Unknown.Count > 0 ? $", {Unknown.Count} are not" : "") +
        $"; {WithoutBiasCurve.Count} have no bias curve" +
        (WithoutEsrBasis.Count > 0 ? $", {WithoutEsrBasis.Count} resolve no ESR at all" : "") + ".";
}

/// <summary>What one part number resolved to, and <b>which source won</b> (R-rail2-11).</summary>
/// <param name="FilePath">The attached file, resolved against the library's own folder, where the
/// file won. Null where the row did.</param>
public sealed record PartModelResolution(
    string PartNumber,
    PartLibraryRow? Row,
    PartModelSource Source,
    string? FilePath,
    EsrProvenance? EsrBasis)
{
    /// <summary>The sentence the parts table's model-source column reads.</summary>
    public string Summary => Row is null
        ? $"{PartNumber} is not in the library."
        : Source == PartModelSource.AttachedFile
            ? $"{PartNumber}: the attached file {System.IO.Path.GetFileName(FilePath ?? "")}, which " +
              "overrides the library row."
            : $"{PartNumber}: the library row.";
}

/// <summary>
/// The part library document — a <c>.crlib</c> beside the <c>.crail</c>, read headlessly so the
/// <c>rail</c> verb works in CI.
///
/// <para><b>Framework-free, and it draws nothing.</b> Same terms as <see cref="RailDocument"/>.</para>
/// </summary>
public sealed class PartLibrary
{
    /// <summary>How far the stated and the derived inductance may differ before the row is reported.
    /// 5 %, which is well inside the round-off of a table quoting f₀ to three figures and well
    /// outside a real disagreement — a row whose stated L is the mounting loop rather than the part's
    /// own is out by a factor, not by a few per cent.</summary>
    public const double InductanceTolerance = 0.05;

    /// <summary>What this library is called on a report. The file name serves where it is empty.</summary>
    public string Name { get; set; } = "";

    /// <summary>Where this library was read from, so <see cref="ResolveModel"/> can resolve a row's
    /// attached file relative to it. Empty for a library built in memory.</summary>
    public string BaseDirectory { get; set; } = "";

    /// <summary>Every row, in declaration order.</summary>
    public List<PartLibraryRow> Rows { get; } = [];

    /// <summary>The row for a part number, or null. Case-insensitive, because a part number typed in
    /// one case and exported in another is the same part.</summary>
    public PartLibraryRow? Part(string partNumber) =>
        Rows.FirstOrDefault(r => string.Equals(r.PartNumber, partNumber, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// R-rail2-11: resolves a part number to its model <b>in Q-11's order — the file overrides the
    /// row</b> — and reports which won.
    /// </summary>
    public PartModelResolution ResolveModel(string partNumber)
    {
        var row = Part(partNumber);
        if (row is null) return new PartModelResolution(partNumber, null, PartModelSource.LibraryRow, null, null);

        string? file = row.ModelRef is { Length: > 0 } r
            ? (BaseDirectory.Length > 0 ? System.IO.Path.GetFullPath(System.IO.Path.Combine(BaseDirectory, r)) : r)
            : null;

        return new PartModelResolution(partNumber, row, row.ModelSource, file, row.EsrBasis);
    }

    /// <summary>
    /// R-rail2-10. How well this library covers the part numbers actually referenced — <b>by part
    /// number, because that is what a correction is made against.</b>
    /// </summary>
    public PartLibraryCoverage Coverage(IEnumerable<string> referencedPartNumbers)
    {
        var referenced = referencedPartNumbers
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unknown = new List<string>();
        var noCurve = new List<string>();
        var noEsr = new List<string>();
        var indicative = new List<string>();
        var notCapacitors = new List<string>();

        foreach (string part in referenced)
        {
            var row = Part(part);
            if (row is null) { unknown.Add(part); continue; }

            if (!row.IsCapacitor) notCapacitors.Add(part);
            else if (row.BiasCurve.Count == 0) noCurve.Add(part);
            if (row.EsrBasis is null) noEsr.Add(part);
            else if (row.EsrBasis == EsrProvenance.ClassDefault) indicative.Add(part);
        }

        return new PartLibraryCoverage(
            referenced.Count, referenced.Count - unknown.Count, unknown, noCurve, noEsr, indicative,
            notCapacitors);
    }

    /// <summary>Every row whose stated inductance disagrees with the derived one by more than
    /// <see cref="InductanceTolerance"/> — §7's "derived part inductance" gate, as a REPORT. The
    /// derived value is the one used regardless (R-rail2-8).</summary>
    public IReadOnlyList<string> InductanceDisagreements() =>
        [.. Rows.Where(r => r.InductanceDisagreement > InductanceTolerance)
                .Select(r =>
                    $"Part '{r.PartNumber}' states L = {r.StatedInductanceHenries:0.###e+00} H, and " +
                    $"C with f0 derive {r.DerivedInductanceHenries:0.###e+00} H — " +
                    $"{r.InductanceDisagreement:P1} apart. The derived value was used.")];

    /// <summary>Null when every row is well formed and no two share a part number, or the first
    /// refusal sentence. <see cref="PartLibraryIo"/> applies this on both read AND write, so a
    /// library that cannot be read back is one that was never written.</summary>
    public string? Refusal()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (row.Refusal() is { } r) return r;
            if (!seen.Add(row.PartNumber))
                return $"Two rows are both for part '{row.PartNumber}'. The part number is the key a " +
                       "model attaches to, so they are distinct — a part with two approved " +
                       "manufacturers is one row, not two.";
        }
        return null;
    }

    /// <summary>Whether an attached model file is a Touchstone one, which is the only route to a
    /// MEASURED ESR (§2.2). A SPICE subcircuit is a model and not a measurement, so it does not make
    /// the ESR measured.</summary>
    public static bool IsTouchstone(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        if (ext.Length < 4 || ext[0] != '.') return false;
        if (ext[1] is not ('s' or 'S') || ext[^1] is not ('p' or 'P')) return false;
        for (int i = 2; i < ext.Length - 1; i++) if (!char.IsAsciiDigit(ext[i])) return false;
        return ext.Length > 4 || char.IsAsciiDigit(ext[2]);
    }
}
