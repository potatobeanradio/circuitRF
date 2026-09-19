// One part as an element the mesh can carry over frequency — C, L and ESR, each with the provenance
// it has to travel with (docs/sonnet-briefs/brief-railrf-11-part-models.md R-rail11-1, R-rail11-2,
// R-rail11-4, R-rail11-8; railrf.md §2.2, §9, Q-11, Q-12, Q-15).
//
// ── BRIEF 2 BUILT THE REPRESENTATION. THIS IS THE COMPUTED ELEMENT ─────────────────────────────
//
// PartLibraryRow is a row of a maintained table and says what the table says. RailPartModel is what
// railRF WORKED OUT from it at one rail voltage: the capacitance after Q-12's bias correction, the
// inductance derived from C and f0, the ESR from whichever of the three bases was available, and
// the mounting loop. Nothing here reads a file or resolves a path — RailPartResolver does that and
// hands the results in, so this type is inspectable in a test with no disk at all.
//
// ── THE FLAGS ARE THE POINT, NOT DECORATION ────────────────────────────────────────────────────
//
// §9: "a mask margin in dB computed from an INDICATIVE peak looks exactly as authoritative as a
// real one. Every such margin is marked wherever it appears — part row, plot, table, export
// provenance — and THAT MARKING IS NOW LOAD-BEARING RATHER THAN TEMPORARY."
//
// Five places, and the flag has to survive the whole chain to reach them. R-rail11-4 names the
// failure exactly: A FLAG COMPUTED HERE AND DROPPED AT THE DataSet BOUNDARY REACHES NONE OF THEM.
// So it is carried on the model, aggregated on RailPartModelSet, and written into the DataSet by
// RailPartModelSet.Annotate — never emitted as a log line.
//
// NUMBERS ARE BASE SI. Farads, henries, ohms, hertz, volts.

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Design.RailRf;

/// <summary>Where a part model's series inductance came from.</summary>
public enum RailInductanceBasis
{
    /// <summary><c>L = 1/((2·π·f₀)²·C)</c> from the row's own C and self-resonant frequency —
    /// R-rail2-8's derivation, and the common case. <b>Exact</b>, not a fit.</summary>
    DerivedFromResonance,

    /// <summary>The row stated an inductance and stated no self-resonant frequency to derive one
    /// from. Where it states both, the derived value is used and the stated one is COMPARED
    /// (<see cref="PartLibrary.InductanceDisagreements"/>).</summary>
    Stated,

    /// <summary>Read out of the part's own Touchstone file.</summary>
    Measured,
}

/// <summary>
/// Where a part's MOUNTING inductance came from — the loop from the pad through its via to the
/// plane pair and back (R-rail11-8, §2.2).
/// </summary>
public enum RailMountingBasis
{
    /// <summary>
    /// Somebody typed it. <b>Not a placeholder</b>: §6 makes P1 artwork-OPTIONAL, so a typed
    /// mounting inductance is a legitimate input for a board whose artwork has not arrived — and
    /// §2.2 lets a user override a computed one anyway.
    /// </summary>
    Typed,

    /// <summary>Computed from the actual via positions and the plane separation (brief 13). <b>Not
    /// produced in P1</b> and represented here so that a P1 model and a P2a model are the same type
    /// with a different flag rather than two types.</summary>
    ComputedFromGeometry,
}

/// <summary>
/// A part's own measured sweep, already reduced to the impedance the shunt-through relation gives.
///
/// <para><b>Nothing here re-derives that relation</b> (R-rail11-2): <see cref="PassiveMetrics"/>
/// owns it — <c>Z = (Z0/2)·S21/(1 − S21)</c> — and it owns the trap that makes it worth owning
/// (reading a shunt-through file as a 1-port returns <c>Z ∥ Z0</c>, which is smooth, finite and
/// plausible everywhere and saturates at Z0). <see cref="RailPartResolver"/> calls it; this record
/// carries the answer.</para>
/// </summary>
/// <param name="FilePath">The file it came from, for the parts table's model-source column.</param>
/// <param name="FrequenciesHz">The file's own frequency axis, ascending.</param>
/// <param name="Impedance">The two-terminal impedance at each of those frequencies.</param>
/// <param name="SelfResonanceHz">The first capacitive-to-inductive reactance crossing, from
/// <see cref="PassiveMetrics.SelfResonance(double[], Complex[])"/>, or null where this sweep does
/// not contain one.</param>
/// <param name="Health">What the file's own contents say about it, where it was measured. Carried
/// rather than judged: <see cref="TouchstoneHealth"/> "measures and states; the caller decides".</param>
public sealed record RailMeasuredPart(
    string FilePath,
    double[] FrequenciesHz,
    Complex[] Impedance,
    double? SelfResonanceHz,
    TouchstoneHealthReport? Health = null)
{
    /// <summary>The band this file actually covers. A vendor part file routinely starts above a PDN
    /// sweep's bottom decade, so <b>out of band is the ordinary case and not an error</b>.</summary>
    public (double LowHz, double HighHz)? Band =>
        FrequenciesHz.Length > 0 ? (FrequenciesHz[0], FrequenciesHz[^1]) : null;

    /// <summary>
    /// Re Z interpolated at one frequency, in ohms — <b>NaN outside the file's own band</b>.
    ///
    /// <para>Clamping to the nearest end would be read as a measurement, which is the rule
    /// <see cref="PassiveMetrics.Evaluate(Complex[], double[], PassiveMetric)"/> already states for
    /// every undefined metric. Interpolated in log f because these sweeps are logarithmic.</para>
    /// </summary>
    public double EsrOhmsAt(double frequencyHz)
    {
        if (FrequenciesHz.Length == 0 || !(frequencyHz > 0)) return double.NaN;
        if (frequencyHz < FrequenciesHz[0] || frequencyHz > FrequenciesHz[^1]) return double.NaN;

        for (int i = 1; i < FrequenciesHz.Length; i++)
        {
            if (frequencyHz > FrequenciesHz[i]) continue;

            double f0 = FrequenciesHz[i - 1], f1 = FrequenciesHz[i];
            double r0 = Impedance[i - 1].Real, r1 = Impedance[i].Real;
            if (f1 <= f0) return r1;

            double t = f0 > 0
                ? (Math.Log(frequencyHz) - Math.Log(f0)) / (Math.Log(f1) - Math.Log(f0))
                : (frequencyHz - f0) / (f1 - f0);
            return r0 + t * (r1 - r0);
        }

        return Impedance[^1].Real;
    }

    /// <summary>
    /// The capacitance this file states, in FARADS, read <b>at the lowest frequency where the part
    /// is still capacitive</b> — the point farthest from its own resonance, where the series
    /// inductance has least influence on C_eff. Null where the sweep has no capacitive branch at
    /// all, which is what a file measured entirely above the part's resonance looks like.
    /// </summary>
    public double? CapacitanceFarads
    {
        get
        {
            for (int i = 0; i < Impedance.Length && i < FrequenciesHz.Length; i++)
            {
                double im = Impedance[i].Imaginary, w = 2.0 * Math.PI * FrequenciesHz[i];
                if (double.IsNaN(im) || im >= 0 || !(w > 0)) continue;
                return -1.0 / (w * im);
            }
            return null;
        }
    }

    /// <summary>
    /// The series inductance this file states, in HENRIES.
    ///
    /// <para><b>Derived from the measured C and the measured f₀ by R-rail2-8's own arithmetic</b>
    /// where the sweep contains a resonance, so the file path and the library-row path use ONE
    /// derivation rather than two that could disagree on the same part. Where it does not, L_eff at
    /// the top of the inductive branch, which is the only reading left.</para>
    /// </summary>
    public double? InductanceHenries
    {
        get
        {
            if (SelfResonanceHz is { } f0 && f0 > 0 && CapacitanceFarads is { } c && c > 0)
            {
                double w = 2.0 * Math.PI * f0;
                return 1.0 / (w * w * c);
            }

            for (int i = Impedance.Length - 1; i >= 0 && i < FrequenciesHz.Length; i--)
            {
                double im = Impedance[i].Imaginary, w = 2.0 * Math.PI * FrequenciesHz[i];
                if (double.IsNaN(im) || im <= 0 || !(w > 0)) continue;
                return im / w;
            }
            return null;
        }
    }
}

/// <summary>
/// One part, resolved into the element a sweep can carry — <b>with every number's provenance beside
/// it</b>.
/// </summary>
public sealed class RailPartModel
{
    /// <summary>The internal part number this was resolved from. The key a correction is made
    /// against (§2.2).</summary>
    public required string PartNumber { get; init; }

    /// <summary>The instance on the board, where this model is of one — <c>C7</c>. Null for a model
    /// resolved per part number rather than per instance.</summary>
    public string? Refdes { get; init; }

    /// <summary>Which of the two sources won (R-rail2-11, Q-11). <b>The file overrides the row</b>,
    /// and the parts table reports which.</summary>
    public PartModelSource Source { get; init; } = PartModelSource.LibraryRow;

    /// <summary>The library row this came from, or null where the part number is not in the library
    /// at all.</summary>
    public PartLibraryRow? Row { get; init; }

    /// <summary>
    /// Why this part could not be modelled, or null. <b>Brief 7 lists such a part AS UNRESOLVED and
    /// populates no numeric column for it</b> (R-rail7-9) — a defaulted row and a resolved one must
    /// not look the same.
    /// </summary>
    public string? UnresolvedReason { get; init; }

    /// <summary>True where this model carries numbers at all.</summary>
    public bool IsResolved => UnresolvedReason is null;

    // ── capacitance: all three of §9's numbers ────────────────────────────────────────────────

    /// <summary>Q-12's triple — marked, derated, and which was used. <b>Never reduced to one
    /// number</b>; see <see cref="RailDerating"/>'s own header.</summary>
    public required RailDeratedCapacitance Capacitance { get; init; }

    /// <summary>The capacitance this model carries, in FARADS. NaN where none was resolvable.</summary>
    public double CapacitanceFarads => Capacitance.UsedFarads;

    // ── inductance ────────────────────────────────────────────────────────────────────────────

    /// <summary>The part's own series inductance, in HENRIES — the package, not the mounting loop.
    /// Null where the row carries neither a derivable nor a stated one.</summary>
    public double? InductanceHenries { get; init; }

    /// <summary>Where <see cref="InductanceHenries"/> came from. Null where there is none.</summary>
    public RailInductanceBasis? InductanceBasis { get; init; }

    /// <summary>
    /// The loop from the pad through its via to the plane pair and back, in HENRIES (R-rail11-8,
    /// §2.2). Null where nothing stated one and nothing computed one.
    ///
    /// <para><b>Typically 0.3–1.5 nH, and it DOMINATES above roughly 50 MHz</b> — §2.2: <i>"it is
    /// the thing your form factor change actually altered."</i> That range is the sanity check a
    /// user has, which is why it is written down here rather than left to a document: a typed 15 nH
    /// is an order of magnitude out and nothing else in railRF would say so.</para>
    /// </summary>
    public double? MountingInductanceHenries { get; init; }

    /// <summary>Typed, or computed from the via geometry. Null where there is no mounting
    /// inductance at all.</summary>
    public RailMountingBasis? MountingBasis { get; init; }

    /// <summary>What the branch actually carries: the part's own inductance plus its mounting loop.
    /// Null where neither is known. <b>This is the L that sets the resonance</b>, which is why Q-9's
    /// arithmetic quotes a bulk part "with 5 nH of mounting".</summary>
    public double? TotalInductanceHenries =>
        InductanceHenries is null && MountingInductanceHenries is null
            ? null
            : (InductanceHenries ?? 0) + (MountingInductanceHenries ?? 0);

    // ── ESR ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which of Q-15's three bases produced this part's ESR, or <b>null where none did</b> — no
    /// file, no stated ESR and no recognised dielectric class. A null one is counted and reported,
    /// never given a class-II figure (R-rail11-3).
    /// </summary>
    public EsrProvenance? EsrBasis { get; init; }

    /// <summary>The dissipation factor a <see cref="EsrProvenance.ClassDefault"/> ESR is computed
    /// from, or null. Carried because the parts table states the basis, and a basis without its
    /// number is not a basis.</summary>
    public double? DissipationFactor { get; init; }

    /// <summary>The canonical dielectric class that dissipation factor came from, or null.</summary>
    public string? DielectricClass { get; init; }

    /// <summary>The ESR the library row states, in OHMS, where it states one.</summary>
    public double? StatedEsrOhms { get; init; }

    /// <summary>The part's own measured sweep, where a Touchstone file won. Null otherwise.</summary>
    public RailMeasuredPart? Measured { get; init; }

    /// <summary>
    /// <b>R-rail11-4, and it is the flag the whole chain exists to carry.</b> True where this part's
    /// ESR is a dissipation-factor default — Q-15's NORMAL case, not the degraded one — so every
    /// peak height and every mask margin computed from it is
    /// <see cref="RailEsrDefaults.Marking">indicative</see>.
    /// </summary>
    public bool IsIndicative => EsrBasis == EsrProvenance.ClassDefault;

    /// <summary>
    /// This part's ESR at one frequency, in OHMS.
    ///
    /// <para>A class default is <c>DF/(2π·f·C)</c> evaluated here (R-rail11-3); a stated ESR is the
    /// one number the row gives, at every frequency, because that is all the row says; a measured
    /// one is Re Z out of the file, and <b>NaN outside the file's own band</b> rather than clamped
    /// to its nearest end.</para>
    /// </summary>
    public double EsrOhmsAt(double frequencyHz) => EsrBasis switch
    {
        EsrProvenance.Measured     => Measured?.EsrOhmsAt(frequencyHz) ?? double.NaN,
        EsrProvenance.Stated       => StatedEsrOhms ?? double.NaN,
        EsrProvenance.ClassDefault => DissipationFactor is { } df
                                        ? RailEsrDefaults.EsrOhms(df, CapacitanceFarads, frequencyHz)
                                        : double.NaN,
        _                          => double.NaN,
    };

    /// <summary>
    /// The one ESR figure a parts table shows, in OHMS: this part's ESR <b>at its own
    /// resonance</b>, because that is where ESR sets the depth of the minimum and the height of the
    /// anti-resonance it takes part in. NaN where there is no resonance to evaluate it at or no
    /// basis to evaluate.
    /// </summary>
    public double EsrOhms =>
        SelfResonanceHz is { } f0 ? EsrOhmsAt(f0) : double.NaN;

    // ── resonance ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The self-resonant frequency of the mounted part, in HERTZ — <c>1/(2π·√(L_total·C))</c> over
    /// the numbers this model actually carries. Null where either is missing.
    ///
    /// <para><b>It is not the row's stated f₀ and it should not be.</b> The row's figure is the
    /// unmounted part at its marked capacitance; this one includes the mounting loop and Q-12's bias
    /// correction, both of which move it — derating RAISES it by √(marked/derated), which is a real
    /// effect and one a reader should see rather than one to hide by re-printing the row's number.
    /// Q-9's own four figures are the sanity check: 470 µF with 5 nH → 104 kHz, 1 µF → 5.3 MHz,
    /// 100 nF with 1 nH → 16 MHz.</para>
    /// </summary>
    public double? SelfResonanceHz
    {
        get
        {
            double c = CapacitanceFarads;
            if (!(c > 0) || TotalInductanceHenries is not { } l || !(l > 0)) return null;
            return 1.0 / (2.0 * Math.PI * Math.Sqrt(l * c));
        }
    }

    /// <summary>The self-resonant frequency the library row STATES, in hertz, or null. Compared,
    /// never used — see <see cref="SelfResonanceHz"/>.</summary>
    public double? StatedSelfResonanceHz => Row?.SelfResonantFrequencyHz;

    // ── what a reader is told ─────────────────────────────────────────────────────────────────

    /// <summary>Findings a reader should act on — a missing bias curve, an unresolvable ESR, a rail
    /// above the curve's last point. Warnings, never notes; the split is
    /// <see cref="RailDcResult"/>'s own.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The sentence the parts table's own row reads, with every provenance said out
    /// loud.</summary>
    public string Describe()
    {
        if (UnresolvedReason is { } why) return $"{Name}: unresolved — {why}";

        string esr = EsrBasis switch
        {
            EsrProvenance.Measured     => $"ESR {Ohms(EsrOhms)} measured",
            EsrProvenance.Stated       => $"ESR {Ohms(EsrOhms)} as stated",
            EsrProvenance.ClassDefault => $"ESR {Ohms(EsrOhms)} from a {DissipationFactor:0.###} " +
                                          $"dissipation factor for {DielectricClass} — " +
                                          RailEsrDefaults.Marking,
            _                          => "no ESR: no file, no stated value and no dielectric class",
        };

        string l = TotalInductanceHenries is { } lt
            ? $"L {lt * 1e12:0.#} pH" +
              (MountingInductanceHenries is { } lm
                  ? $" ({InductanceHenries * 1e12:0.#} pH part + {lm * 1e12:0.#} pH mounting, " +
                    $"{MountingBasis.ToString()!.ToLowerInvariant()})"
                  : "")
            : "no inductance";

        string f0 = SelfResonanceHz is { } f ? $", resonant at {Hertz(f)}" : "";

        return $"{Name}: {Capacitance.Describe()}; {l}; {esr}{f0}.";
    }

    /// <summary>What a report calls this part — its refdes where there is one, and its part number
    /// otherwise.</summary>
    public string Name => Refdes is { Length: > 0 } r ? $"{r} ({PartNumber})" : PartNumber;

    private static string Ohms(double r) =>
        double.IsNaN(r) ? "(none)"
        : r >= 1         ? $"{r:0.###} Ω"
        : r >= 1e-3      ? $"{r * 1e3:0.###} mΩ"
        :                  $"{r * 1e6:0.###} µΩ";

    private static string Hertz(double f) =>
        f >= 1e9 ? $"{f / 1e9:0.###} GHz"
        : f >= 1e6 ? $"{f / 1e6:0.###} MHz"
        : f >= 1e3 ? $"{f / 1e3:0.###} kHz"
        :            $"{f:0.###} Hz";
}
