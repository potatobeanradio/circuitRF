using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Every closed form the four MMIC passives use (<c>MIMCAP</c>, <c>TFR</c>, <c>SPIRAL</c>,
/// <c>AIRBRIDGE</c>), each with its source (brief-agent-authoring-overview.md AA-1). SI throughout:
/// metres, siemens per metre, hertz, henries, ohms, farads.
///
/// <para><b>These are ESTIMATES, and the spiral's most of all.</b> Each form is a published closed
/// form with a stated range; outside it the number is still computed and the component's validity
/// note says so. The reference for a spiral that matters is an EM extraction of the drawn coil
/// (<c>circuitrf em --component</c>), and the component says so.</para>
/// </summary>
public static class MmicPassiveFormulas
{
    public const double Eps0 = 8.8541878128e-12;
    private const double Mu0 = MicrostripLoss.Mu0;
    private const double C0 = 299_792_458.0;

    // ── Conductor loss ─────────────────────────────────────────────────────────────────────────

    /// <summary>The skin depth √(1/(π f µ₀ σ)), m. Infinite at DC.</summary>
    public static double SkinDepth(double freqHz, double sigma)
        => freqHz > 0 && sigma > 0 ? Math.Sqrt(1.0 / (Math.PI * freqHz * Mu0 * sigma)) : double.PositiveInfinity;

    /// <summary>
    /// The thickness a strip of thickness <paramref name="t"/> conducts in at <paramref name="freqHz"/>:
    /// <c>δ·(1 − e^(−t/δ))</c>, which is <c>t</c> at DC and <c>δ</c> once the strip is many skin depths
    /// thick. The usual one-sided approximation for a strip over a ground plane, where the current crowds
    /// to the face nearest the return.
    /// </summary>
    public static double EffectiveThickness(double freqHz, double t, double sigma)
    {
        double d = SkinDepth(freqHz, sigma);
        if (double.IsPositiveInfinity(d)) return t;
        return d * (1.0 - Math.Exp(-t / d));
    }

    /// <summary>A conductor's sheet resistance at <paramref name="freqHz"/>, Ω/sq.</summary>
    public static double SheetResistance(double freqHz, double t, double sigma)
    {
        double te = EffectiveThickness(freqHz, t, sigma);
        return sigma > 0 && te > 0 ? 1.0 / (sigma * te) : 0.0;
    }

    // ── Thin-film resistor ─────────────────────────────────────────────────────────────────────

    /// <summary>What a sharp 90° bend of a film of uniform width is worth, in squares of its corner cell.
    /// Less than one, because the current cuts the inside of the corner.</summary>
    public const double FilmCornerSquares = 0.559;

    /// <summary>
    /// The centreline of a serpentine film: <paramref name="segments"/> runs, each <paramref name="l"/>
    /// between bend centres, joined by links with a clear length <paramref name="link"/> between the runs'
    /// edges, so each link's centreline is <c>w + link</c>. One segment is the straight film, <paramref name="l"/>.
    /// </summary>
    public static double SerpentineLength(double w, double l, int segments, double link)
        => segments * l + (segments - 1) * (w + link);

    /// <summary>
    /// The squares in that serpentine: the straight film, which is the centreline less the two
    /// half-widths into each of its <c>2(segments − 1)</c> corners, plus each corner at
    /// <see cref="FilmCornerSquares"/>. One segment is exactly <c>l/w</c>.
    /// </summary>
    public static double SerpentineSquares(double w, double l, int segments, double link)
    {
        int corners = 2 * (segments - 1);
        return (SerpentineLength(w, l, segments, link) - corners * w) / w + corners * FilmCornerSquares;
    }

    // ── MIM capacitor ──────────────────────────────────────────────────────────────────────────

    /// <summary>The parallel-plate capacitance <c>ε₀εr·W·L/t</c>, F — no fringing.</summary>
    public static double ParallelPlateCapacitance(double epsR, double w, double l, double t)
        => t > 0 ? Eps0 * epsR * w * l / t : 0.0;

    /// <summary>
    /// The capacitance of a rectangular parallel-plate capacitor with its edge fringing,
    /// L. H. Palmer, "The capacitance of a parallel-plate capacitor by the Schwartz-Christoffel
    /// transformation", <i>Trans. AIEE</i> 56, pp. 363–366, 1937:
    /// <c>C = ε₀εr·(W·L/t)·[1 + (t/πW)(1 + ln(2πW/t))]·[1 + (t/πL)(1 + ln(2πL/t))]</c>.
    ///
    /// <para><b>Valid</b> for plates large against their separation (W and L each at least ten times
    /// <paramref name="t"/>), which every MIM capacitor is: the correction is a fraction of a percent at
    /// tens of micrometres over a 0.2 µm film. It treats the plates as equal and infinitely thin; the
    /// bottom plate's enclosure and the plates' own thickness are not in it.</para>
    /// </summary>
    public static double PalmerCapacitance(double epsR, double w, double l, double t)
    {
        if (t <= 0 || w <= 0 || l <= 0) return 0.0;
        double fw = 1.0 + t / (Math.PI * w) * (1.0 + Math.Log(2 * Math.PI * w / t));
        double fl = 1.0 + t / (Math.PI * l) * (1.0 + Math.Log(2 * Math.PI * l / t));
        return ParallelPlateCapacitance(epsR, w, l, t) * fw * fl;
    }

    /// <summary>
    /// A MIM capacitor's equivalent series resistance from its plates, Ω: each plate fed along one
    /// edge and discharging uniformly over its length <paramref name="l"/> contributes
    /// <c>Rs·L/(3W)</c> — the distributed-RC result for a plate fed from one edge (the third is the
    /// integral of a linearly falling current over the plate). Both plates are added.
    /// </summary>
    public static double MimPlateEsr(double rsTop, double rsBottom, double w, double l)
        => w > 0 ? (rsTop + rsBottom) * l / (3.0 * w) : 0.0;

    // ── Thin-film resistor ─────────────────────────────────────────────────────────────────────

    /// <summary>A thin-film resistor's resistance, sheet resistance times squares: <c>Rs·L/W</c>, Ω.
    /// Contact resistance and the metal overlap at each end are not included.</summary>
    public static double ThinFilmResistance(double rs, double w, double l) => w > 0 ? rs * l / w : 0.0;

    // ── Square spiral inductor ─────────────────────────────────────────────────────────────────

    /// <summary>The outer dimension of a square spiral: <c>Din + 2·N·W + 2·(N − 1)·S</c>, m (Mohan et
    /// al., below).</summary>
    public static double SpiralOuterDiameter(double turns, double w, double s, double din)
        => din + 2 * turns * w + 2 * Math.Max(turns - 1, 0) * s;

    /// <summary>
    /// A square spiral's inductance by the modified Wheeler formula, S. S. Mohan, M. del Mar Hershenson,
    /// S. P. Boyd and T. H. Lee, "Simple accurate expressions for planar spiral inductances",
    /// <i>IEEE J. Solid-State Circuits</i> 34(10), pp. 1419–1424, 1999:
    /// <c>L = K₁·µ₀·N²·d_avg / (1 + K₂·ρ)</c> with <c>K₁ = 2.34</c>, <c>K₂ = 2.75</c> for a square
    /// coil, <c>d_avg = (Dout + Din)/2</c> and the fill ratio <c>ρ = (Dout − Din)/(Dout + Din)</c>.
    ///
    /// <para><b>Valid</b> where the paper fitted it: spacing no more than about three widths, and a
    /// coil far from any ground plane. The paper reports errors of a few percent against field solvers
    /// over its test set. It is the coil ALONE — a drawn part's escape and leads are not in it — and a
    /// ground plane under the coil, which it does not see either, lowers the real value. Against a
    /// planar EM extraction of the drawn 2.5-turn coil on the shipped GaAs it read 16 % low
    /// (src/Design/RESOLVED.md, AA-1).</para>
    /// </summary>
    public static double ModifiedWheelerInductance(double turns, double w, double s, double din)
        => ModifiedWheelerInductance(turns, w, s, din, octagonal: false);

    /// <summary>
    /// <see cref="ModifiedWheelerInductance(double, double, double, double)"/> for a square or an
    /// OCTAGONAL coil — the paper's own coefficients for each: <c>K₁ = 2.34, K₂ = 2.75</c> square,
    /// <c>K₁ = 2.25, K₂ = 3.55</c> octagonal, with the diameters measured across the flats. A closed
    /// form, so microseconds: cheap enough to follow every keystroke in a properties panel.
    /// </summary>
    public static double ModifiedWheelerInductance(double turns, double w, double s, double din, bool octagonal)
    {
        double dout = SpiralOuterDiameter(turns, w, s, din);
        double davg = (dout + din) / 2;
        double rho = dout + din > 0 ? (dout - din) / (dout + din) : 0.0;
        var (k1, k2) = octagonal ? (2.25, 3.55) : (2.34, 2.75);
        return k1 * Mu0 * turns * turns * davg / (1.0 + k2 * rho);
    }

    /// <summary>The trace length of a square spiral, <c>4·N·d_avg</c>, m — the four sides of a lap
    /// at the coil's average diameter, N times.</summary>
    public static double SpiralTraceLength(double turns, double w, double s, double din)
        => SpiralTraceLength(turns, w, s, din, octagonal: false);

    /// <summary>The trace length of a square or octagonal spiral, m: N laps at the coil's average
    /// diameter across the flats — a square lap is <c>4·d</c>, a regular octagon's <c>8·tan(π/8)·d</c>
    /// (about 17 % shorter).</summary>
    public static double SpiralTraceLength(double turns, double w, double s, double din, bool octagonal)
    {
        double dout = SpiralOuterDiameter(turns, w, s, din);
        double lap = octagonal ? 8 * Math.Tan(Math.PI / 8) : 4.0;
        return lap * turns * (dout + din) / 2;
    }

    // ── Ribbon (air bridge) ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The partial self-inductance of a straight flat ribbon of length <paramref name="l"/>, width
    /// <paramref name="w"/> and thickness <paramref name="t"/>, F. W. Grover, <i>Inductance
    /// Calculations</i> (1946), the rectangular-bar formula:
    /// <c>L = (µ₀/2π)·ℓ·[ln(2ℓ/(w + t)) + 1/2 + 0.2235·(w + t)/ℓ]</c>. Valid for <c>ℓ</c> longer than
    /// the cross-section; at a span comparable to its own width it is an approximation.
    /// </summary>
    public static double RibbonInductance(double l, double w, double t)
    {
        if (l <= 0 || w + t <= 0) return 0.0;
        double wt = w + t;
        return Mu0 / (2 * Math.PI) * l * (Math.Log(2 * l / wt) + 0.5 + 0.2235 * wt / l);
    }

    // ── Validity ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The frequency at which <paramref name="length"/> is a twentieth of a wavelength in a
    /// medium of <paramref name="epsR"/>, Hz — the lumped models' common upper bound.</summary>
    public static double LumpedValidityFrequency(double length, double epsR)
        => length > 0 ? C0 / (20.0 * length * Math.Sqrt(Math.Max(epsR, 1.0))) : double.PositiveInfinity;
}
