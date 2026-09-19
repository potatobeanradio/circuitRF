// Library row -> model, with the file overriding the row, and the headline counts that make partial
// coverage visible (docs/sonnet-briefs/brief-railrf-11-part-models.md R-rail11-1, R-rail11-2,
// R-rail11-4, R-rail11-6, R-rail11-7; railrf.md §2.2, §9, Q-11, Q-12, Q-15).
//
// ── FOUR WAYS A PART CAN BE MODELLED, AND THE FILE WINS ────────────────────────────────────────
//
//   a library row    C exactly, L DERIVED as 1/((2*pi*f0)^2*C).  CARRIES NO ESR — the common case.
//   a Touchstone     C, L AND A REAL ESR, all the part's own.    RECOMMENDED (§2.2), and already
//                                                                built: PassiveMetrics owns the
//                                                                shunt-through relation.
//   an R-L-C triple  all three, typed.
//   a subcircuit     placed as an ordinary circuitRF subcircuit.
//
// Q-11 closed it as BOTH, with the file overriding the row (R-rail2-11), and the parts table
// reports which won. The last two are ordinary circuitRF component models and nothing here builds
// one — the scope note is explicit: CapacitorModel, SeriesRlcModel, SnpModel and BeadModel cover
// all four forms.
//
// ── THIS BRIEF WRITES NO TOUCHSTONE ARITHMETIC (R-rail11-2) ────────────────────────────────────
//
// PassiveMetrics.Impedance reads a vendor file through the shunt-through relation, and it owns the
// trap that makes doing it here a mistake: reading such a file as a 1-port returns Z || Z0, which is
// smooth, finite and plausible everywhere, is close to right wherever |Z| << Z0 — so a bulk
// decoupling capacitor survives it — and SATURATES AT Z0 otherwise, so a 10 pF part comes back as
// about 50 ohms and its capacitance with it. Neither case announces itself. PassiveMetrics.
// SelfResonance owns the second trap: a real part has several reactance zero crossings and only the
// capacitive-to-inductive one is the SRF. This file CALLS both, and records that the ESR came out
// Measured.
//
// ── THE COUNTS ARE HEADLINE NUMBERS, NOT DETAILS (R-rail11-6) ──────────────────────────────────
//
// §9: "the parts table's count of HOW MANY PARTS ARE MODELLED FROM A FILE is a headline number and
// not a detail", and, of derating: "a library that is only partly populated with curves produces a
// result that is PARTLY DERATED, and that is worse than either extreme unless it is visible."
// Brief 7 puts both on the status strip; they are first-class queries here so that it can.

using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// Every part on a rail, resolved — <b>with the counts that make partial coverage visible, and the
/// indicative flag that has to reach the plot, the tables, the readout and every export.</b>
/// </summary>
public sealed class RailPartModelSet
{
    /// <summary>Every model, in the order the parts were asked about.</summary>
    public required IReadOnlyList<RailPartModel> Models { get; init; }

    /// <summary>The rail voltage the derating was applied at, in VOLTS, or null where none was
    /// known — in which case nothing was derated and every row says so.</summary>
    public double? RailVoltageV { get; init; }

    /// <summary>Parts the library has no row for at all. Brief 7 lists them AS UNRESOLVED.</summary>
    public IReadOnlyList<RailPartModel> Unresolved =>
        [.. Models.Where(m => !m.IsResolved)];

    /// <summary>
    /// <b>§9's headline number</b> (R-rail11-6): how many parts are modelled from their own file —
    /// which is the only route to a real ESR, and therefore the count that says how much of this
    /// answer is measured rather than defaulted.
    /// </summary>
    public int ModelledFromFile =>
        Models.Count(m => m.IsResolved && m.Source == PartModelSource.AttachedFile);

    /// <summary>Parts with no capacitance-versus-bias curve, by name — the count §9 asks the result
    /// to carry, beside brief 2's own <see cref="PartLibrary.Coverage"/>.</summary>
    public IReadOnlyList<string> WithoutBiasCurve =>
        [.. Models.Where(m => m.IsResolved && m.Capacitance.Basis == RailCapacitanceBasis.Marked)
                  .Select(m => m.Name)];

    /// <summary>Parts whose ESR resolves to NOTHING — no file, no stated value, no recognised
    /// dielectric class. Counted and reported, never given a class-II figure (R-rail11-3).</summary>
    public IReadOnlyList<string> WithoutEsrBasis =>
        [.. Models.Where(m => m.IsResolved && m.EsrBasis is null).Select(m => m.Name)];

    /// <summary>Parts whose ESR is a class default — <b>Q-15's normal case</b>, and the set every
    /// peak height is marked <see cref="RailEsrDefaults.Marking">indicative</see> from.</summary>
    public IReadOnlyList<string> Indicative =>
        [.. Models.Where(m => m.IsIndicative).Select(m => m.Name)];

    /// <summary>
    /// <b>R-rail11-4.</b> True where any part's ESR is a class default, so every peak height, every
    /// anti-resonance row and every mask margin computed from this set is indicative.
    /// </summary>
    public bool AnyIndicative => Models.Any(m => m.IsIndicative);

    /// <summary>The sentence §9 requires to travel with every such number, or null where nothing
    /// here is indicative. <b>On the result, not in a log line.</b></summary>
    public string? IndicativeLine => AnyIndicative ? RailEsrDefaults.IndicativeLine : null;

    /// <summary>Every part row's own warnings, in order. Warnings, never notes.</summary>
    public IReadOnlyList<string> Warnings => [.. Models.SelectMany(m => m.Warnings)];

    /// <summary>The sentence brief 7's status strip prints, with both headline counts in it.</summary>
    public string Summary
    {
        get
        {
            int resolved = Models.Count(m => m.IsResolved);
            return
                $"{resolved} of {Models.Count} part(s) resolved; {ModelledFromFile} modelled from a " +
                $"file; {WithoutBiasCurve.Count} with no bias curve" +
                (WithoutEsrBasis.Count > 0 ? $"; {WithoutEsrBasis.Count} with no ESR basis" : "") +
                (Indicative.Count > 0
                    ? $"; {Indicative.Count} {RailEsrDefaults.Marking} (class-default ESR)."
                    : ".");
        }
    }

    // ── R-rail11-4: across the DataSet boundary ───────────────────────────────────────────────

    /// <summary>The metadata cube naming each part, and carrying its ESR basis as
    /// <see cref="EsrBasisCode"/>.</summary>
    public const string EsrBasisCube = "__RailPartEsrBasis";

    /// <summary>The metadata cube carrying each part's MARKED capacitance, in farads.</summary>
    public const string MarkedCapacitanceCube = "__RailPartCMarked";

    /// <summary>The metadata cube carrying the capacitance each part was modelled with.</summary>
    public const string UsedCapacitanceCube = "__RailPartCUsed";

    /// <summary>The metadata cube carrying which of the three <see cref="UsedCapacitanceCube"/>
    /// is — <see cref="RailCapacitanceBasis"/>'s own ordinal, −1 for an unresolved part.</summary>
    public const string CapacitanceBasisCube = "__RailPartCBasis";

    /// <summary>
    /// <see cref="EsrBasisCube"/>'s vocabulary. <b>2 is the indicative one</b>, and the two negative
    /// codes are distinct on purpose: a part nobody could resolve and a part resolved with no ESR
    /// basis are different failures and a reader has to be able to tell them apart.
    /// </summary>
    public static double EsrBasisCode(RailPartModel m) =>
        !m.IsResolved       ? -2
        : m.EsrBasis is null ? -1
        : (double)(int)m.EsrBasis.Value;

    /// <summary>
    /// <b>R-rail11-4, and this is the boundary the flag would otherwise be dropped at.</b> Writes
    /// the part provenance into a run's <see cref="DataSet"/> as <c>__</c>-prefixed metadata cubes.
    ///
    /// <para>Five places need the marking — the part row, the plot, the anti-resonance table, the
    /// mask-margin readout and <b>the provenance of every export</b> — and only the first of those
    /// can read a <see cref="RailPartModel"/>. The other four read the result. circuitRF already has
    /// the mechanism for exactly this: a <c>__</c>-prefixed cube is metadata, is sweep-invariant, is
    /// skipped by the Data Display's signal list, and <b>is persisted through <c>.npy</c></b>, which
    /// is what carries it into an export.</para>
    ///
    /// <para>Every cube shares one <c>part</c> axis whose <see cref="Axis.Labels"/> are the part
    /// names, so a reader never has to key two cubes together by position.</para>
    /// </summary>
    /// <param name="data">The run's DataSet.</param>
    /// <param name="group">The analysis group to write into, or the default group.</param>
    public void Annotate(DataSet data, string group = DataSet.DefaultGroup)
    {
        int n = Models.Count;
        var index  = new double[n];
        var labels = new string[n];
        for (int i = 0; i < n; i++) { index[i] = i; labels[i] = Models[i].Name; }

        var axis = new Axis("part", index, "", labels);

        Add(EsrBasisCube,           [.. Models.Select(EsrBasisCode)],                        "");
        Add(MarkedCapacitanceCube,  [.. Models.Select(m => m.Capacitance.MarkedFarads)],     "F");
        Add(UsedCapacitanceCube,    [.. Models.Select(m => m.Capacitance.UsedFarads)],       "F");
        Add(CapacitanceBasisCube,   [.. Models.Select(CapacitanceBasisCode)],                "");

        void Add(string name, double[] values, string unit)
        {
            var cube = new DataCube([axis], values) { Unit = unit };
            data.AddToGroup(group, name, cube);
        }

        static double CapacitanceBasisCode(RailPartModel m) =>
            m.IsResolved ? (double)(int)m.Capacitance.Basis : -1;
    }
}

/// <summary>
/// Q-11's resolution, computed: one part number plus one rail voltage becomes a
/// <see cref="RailPartModel"/>, <b>with the file overriding the row</b>.
/// </summary>
public sealed class RailPartResolver
{
    private readonly PartLibrary _library;

    public RailPartResolver(PartLibrary library) => _library = library;

    /// <summary>
    /// The fixture a vendor part file was measured in. <b>Shunt-through, because that is what nearly
    /// every vendor decoupling-capacitor file is</b> — and because it is a statement about the
    /// FIXTURE rather than about the data, nothing in a Touchstone file records it, which is why it
    /// is a setting here rather than an inference (see <see cref="PassiveExtraction"/>).
    /// </summary>
    public PassiveExtraction Extraction { get; init; } = PassiveExtraction.ShuntThrough;

    /// <summary>
    /// How an attached file is read. <b>Substitutable so a test needs no disk</b>; unset, it is
    /// <see cref="TouchstoneIO.ReadFile"/>. Returning null means the file could not be read, and the
    /// part is then reported as unresolved rather than silently falling back to the row — a silent
    /// fallback would make an unreadable file indistinguishable from one that was never attached.
    /// </summary>
    public Func<string, SNP?>? FileReader { get; init; }

    /// <summary>Whether to measure an attached file's soundness as it is read. On by default:
    /// <see cref="TouchstoneHealth"/> measures and states, and the report rides on the model.</summary>
    public bool MeasureFileHealth { get; init; } = true;

    /// <summary>
    /// R-rail11-1. One part row at one rail voltage.
    /// </summary>
    /// <param name="part">The document's own row — refdes, part number and the typed mounting loop.</param>
    /// <param name="railVoltageV">The bias Q-12's derating is applied at, or null where none is
    /// known.</param>
    public RailPartModel Resolve(RailPart part, double? railVoltageV) =>
        Resolve(part.PartNumber, railVoltageV, part.MountingInductanceHenries, part.Refdes);

    /// <summary>R-rail11-1, by part number.</summary>
    /// <param name="partNumber">The library key.</param>
    /// <param name="railVoltageV">The bias Q-12's derating is applied at, or null.</param>
    /// <param name="mountingInductanceHenries">The typed mounting loop (R-rail11-8), or null.</param>
    /// <param name="refdes">The instance this is of, where it is of one.</param>
    public RailPartModel Resolve(
        string partNumber, double? railVoltageV,
        double? mountingInductanceHenries = null, string? refdes = null)
    {
        var resolution = _library.ResolveModel(partNumber);

        if (resolution.Row is not { } row)
            return Unresolved(partNumber, refdes, mountingInductanceHenries,
                $"'{partNumber}' is not in the part library, so railRF has no C, no f₀ and no " +
                "dielectric class for it. Nothing about it is defaulted.");

        return resolution.Source == PartModelSource.AttachedFile
            ? FromFile(row, resolution, railVoltageV, mountingInductanceHenries, refdes)
            : FromRow(row, railVoltageV, mountingInductanceHenries, refdes);
    }

    /// <summary>
    /// R-rail11-6. Every part on a rail, with the headline counts. The set is what a result carries;
    /// the individual models are what the parts table's rows read.
    /// </summary>
    public RailPartModelSet ResolveAll(IEnumerable<RailPart> parts, double? railVoltageV) =>
        new()
        {
            Models       = [.. parts.Select(p => Resolve(p, railVoltageV))],
            RailVoltageV = railVoltageV,
        };

    /// <summary>The same, by bare part number — for a caller that has a BOM's distinct part numbers
    /// rather than the board's instances.</summary>
    public RailPartModelSet ResolveAll(IEnumerable<string> partNumbers, double? railVoltageV) =>
        new()
        {
            Models       = [.. partNumbers.Select(p => Resolve(p, railVoltageV))],
            RailVoltageV = railVoltageV,
        };

    // ── the library-row path ──────────────────────────────────────────────────────────────────

    private static RailPartModel FromRow(
        PartLibraryRow row, double? railVoltageV, double? mountingH, string? refdes)
    {
        var capacitance = RailDerating.Apply(row, railVoltageV);
        var warnings = new List<string>();
        if (capacitance.Warning is { } w) warnings.Add(w);

        // R-rail2-8: the DERIVED value is used and the stated one is compared. The comparison
        // itself is PartLibrary.InductanceDisagreements' — repeating it here would be a second
        // tolerance that could drift from the first.
        double? inductance = row.DerivedInductanceHenries ?? row.StatedInductanceHenries;
        RailInductanceBasis? inductanceBasis =
            row.DerivedInductanceHenries is not null ? RailInductanceBasis.DerivedFromResonance
            : row.StatedInductanceHenries is not null ? RailInductanceBasis.Stated
            : null;

        // Q-15's order, which is PartLibraryRow.EsrBasis' order minus the file case this path
        // cannot reach: a stated ESR, then the class default. A part with neither gets NEITHER.
        double? df = null;
        string? dielectric = null;
        EsrProvenance? esrBasis = null;

        if (row.EsrOhms is not null)
        {
            esrBasis = EsrProvenance.Stated;
        }
        else if (RailEsrDefaults.CanonicalClass(row.DielectricClass) is { } cls)
        {
            esrBasis   = EsrProvenance.ClassDefault;
            dielectric = cls;
            df         = RailEsrDefaults.DissipationFactors[cls];
        }
        else
        {
            warnings.Add(
                row.DielectricClass is { Length: > 0 } stated
                    ? $"Part '{row.PartNumber}' states a dielectric class of '{stated}', which is " +
                      "not one railRF has a dissipation factor for, so it has NO ESR. Its resonance " +
                      "can be placed and not sized. Known classes: " +
                      string.Join(", ", RailEsrDefaults.KnownClasses) + "."
                    : $"Part '{row.PartNumber}' has no ESR, no attached file and no dielectric " +
                      "class, so railRF can place its resonance and not size it. It was NOT given a " +
                      "class default — see the parts with no ESR basis on the result.");
        }

        return new RailPartModel
        {
            PartNumber                = row.PartNumber,
            Refdes                    = refdes,
            Source                    = PartModelSource.LibraryRow,
            Row                       = row,
            Capacitance               = capacitance,
            InductanceHenries         = inductance,
            InductanceBasis           = inductanceBasis,
            MountingInductanceHenries = mountingH,
            MountingBasis             = mountingH is not null ? RailMountingBasis.Typed : null,
            EsrBasis                  = esrBasis,
            DissipationFactor         = df,
            DielectricClass           = dielectric,
            StatedEsrOhms             = row.EsrOhms,
            Warnings                  = warnings,
        };
    }

    // ── the file path: R-rail11-2, and it calls rather than rebuilds ──────────────────────────

    private RailPartModel FromFile(
        PartLibraryRow row, PartModelResolution resolution,
        double? railVoltageV, double? mountingH, string? refdes)
    {
        string path = resolution.FilePath ?? row.ModelRef ?? "";

        // A SPICE subcircuit is a model rather than a measurement, so it does not make the ESR
        // measured (PartLibrary.IsTouchstone states the same rule). It is placed as an ordinary
        // circuitRF subcircuit and this brief builds no component model, so the ROW is what carries
        // the numbers a parts table shows — with the model source still reported as the file.
        if (!PartLibrary.IsTouchstone(path))
        {
            var subcircuit = FromRow(row, railVoltageV, mountingH, refdes);
            return CopyWithSource(subcircuit, PartModelSource.AttachedFile);
        }

        SNP? snp;
        string? readFailure = null;
        try   { snp = (FileReader ?? (p => TouchstoneIO.ReadFile(p)))(path); }
        catch (Exception ex) { snp = null; readFailure = ex.Message; }

        if (snp is null || snp.IsEmpty)
            return Unresolved(row.PartNumber, refdes, mountingH,
                $"Part '{row.PartNumber}' attaches the file '{System.IO.Path.GetFileName(path)}', " +
                "which could not be read" + (readFailure is { } m ? $" ({m})" : "") + ". The file " +
                "OVERRIDES the row, so railRF did not silently fall back to the row's own C and f₀ " +
                "— a file that cannot be read and a file that was never attached must not produce " +
                "the same answer.");

        int portA = 1, portB = snp.Ports >= 2 ? 2 : 1;
        var mode = snp.Ports >= 2 ? Extraction : PassiveExtraction.OnePort;

        Mat<Complex>[] matrices = snp.Matrices;
        Complex[] z0 = snp.Z0PerPort ?? [.. Enumerable.Repeat(snp.Z0, snp.Ports)];

        var z = PassiveMetrics.Impedance(matrices, z0, mode, portA, portB);
        double? srf = PassiveMetrics.SelfResonance(snp.Frequencies, z);

        var health = MeasureFileHealth ? TryHealth(snp) : null;

        var measured = new RailMeasuredPart(path, snp.Frequencies, z, srf, health);

        var warnings = new List<string>();
        if (measured.CapacitanceFarads is null)
            warnings.Add(
                $"Part '{row.PartNumber}''s file is capacitive nowhere in its own band " +
                $"({measured.Band?.LowHz:0.###e+00} … {measured.Band?.HighHz:0.###e+00} Hz), so no " +
                "capacitance was read from it.");
        if (srf is null)
            warnings.Add(
                $"Part '{row.PartNumber}''s file contains no capacitive-to-inductive reactance " +
                "crossing, so it does not contain the part's self-resonance. Its inductance was read " +
                "off the top of the inductive branch instead.");

        double marked = measured.CapacitanceFarads ?? row.CapacitanceFarads ?? double.NaN;

        // R-rail11-2: the row's own C is NOT used. The file states the part's capacitance at
        // whatever bias it was measured at, and nothing in it says what that bias was — so Q-12's
        // curve is not applied on top of it, which would derate an already-derated number.
        var capacitance = new RailDeratedCapacitance(
            marked, null, marked, RailCapacitanceBasis.Measured, railVoltageV, null);

        return new RailPartModel
        {
            PartNumber                = row.PartNumber,
            Refdes                    = refdes,
            Source                    = PartModelSource.AttachedFile,
            Row                       = row,
            Capacitance               = capacitance,
            InductanceHenries         = measured.InductanceHenries,
            InductanceBasis           = measured.InductanceHenries is not null
                                          ? RailInductanceBasis.Measured : null,
            MountingInductanceHenries = mountingH,
            MountingBasis             = mountingH is not null ? RailMountingBasis.Typed : null,
            EsrBasis                  = EsrProvenance.Measured,
            StatedEsrOhms             = row.EsrOhms,
            Measured                  = measured,
            Warnings                  = warnings,
        };
    }

    private static TouchstoneHealthReport? TryHealth(SNP snp)
    {
        // Measured and stated, never judged — and a health measurement that throws on an unusual
        // file must not cost the part its model.
        try { return TouchstoneHealth.Analyze(snp); }
        catch { return null; }
    }

    private static RailPartModel Unresolved(
        string partNumber, string? refdes, double? mountingH, string reason) =>
        new()
        {
            PartNumber                = partNumber,
            Refdes                    = refdes,
            Row                       = null,
            UnresolvedReason          = reason,
            Capacitance               = new RailDeratedCapacitance(
                                            double.NaN, null, double.NaN,
                                            RailCapacitanceBasis.Marked, null, null),
            MountingInductanceHenries = mountingH,
            MountingBasis             = mountingH is not null ? RailMountingBasis.Typed : null,
            Warnings                  = [reason],
        };

    private static RailPartModel CopyWithSource(RailPartModel m, PartModelSource source) =>
        new()
        {
            PartNumber                = m.PartNumber,
            Refdes                    = m.Refdes,
            Source                    = source,
            Row                       = m.Row,
            UnresolvedReason          = m.UnresolvedReason,
            Capacitance               = m.Capacitance,
            InductanceHenries         = m.InductanceHenries,
            InductanceBasis           = m.InductanceBasis,
            MountingInductanceHenries = m.MountingInductanceHenries,
            MountingBasis             = m.MountingBasis,
            EsrBasis                  = m.EsrBasis,
            DissipationFactor         = m.DissipationFactor,
            DielectricClass           = m.DielectricClass,
            StatedEsrOhms             = m.StatedEsrOhms,
            Measured                  = m.Measured,
            Warnings                  = m.Warnings,
        };
}
