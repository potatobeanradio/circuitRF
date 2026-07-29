namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// Static (quasi-static, f→0) microstrip characteristic impedance Z₀ and effective permittivity
/// εeff — brief-L5a-pcell-contract-and-microstrip.md §3.1 layer 1 / microstrip-models.md §2.
///
/// <b>Source: E. Hammerstad and Ø. Jensen, "Accurate Models for Microstrip Computer-Aided
/// Design," IEEE MTT-S International Microwave Symposium Digest, pp. 407–409, June 1980</b> — the
/// accuracy-improved successor to Hammerstad's simpler 1975 synthesis formulas (which is NOT this
/// model; the simpler `εeff ≈ (εᵣ+1)/2 + (εᵣ−1)/2·(1+12h/W)^(−1/2)` form circulating under
/// "Hammerstad" elsewhere is that different, lower-accuracy predecessor and is deliberately not
/// used here). Cross-checked across four independent, non-GPL sources during implementation
/// research: M. Steer, *Fundamentals of Microwave and RF Design* (open-access, LibreTexts) §4.4
/// eq. 4.9–4.16; the scikit-rf project's `skrf/media/mline.py` (BSD-3-Clause,
/// `hammerstad_ab`/`hammerstad_er`/`hammerstad_zl`, citing the same 1980 paper); and two further
/// independent secondary write-ups (nodeloop.org, f4inx.github.io) reproducing the identical
/// closed form. All four agree exactly on every constant below.
///
/// <b>Validity (Steer, explicit): 0.01 ≤ W/h ≤ 100, 1 ≤ εᵣ ≤ 128</b> — R-pc-16/R4: values outside
/// this range are reported (<see cref="MicrostripValidityReporter"/>), never silently returned as
/// if trustworthy. Claimed accuracy: εeff better than 0.2%, Z₀,air (W/h &lt; 1000) better than 0.1%.
///
/// <b>Worked-example check (Steer, Example 4.2, W=600µm h=635µm εᵣ=4.1):</b> u=W/h=0.94488 →
/// εeff=2.967, Z₀,air=129.7 Ω, Z₀=75.4 Ω — the test suite pins this exact triple as the one
/// independently-sourced acceptance row this implementation is checked against.
/// </summary>
public static class HammerstadJensen
{
    public static readonly ValidityRange WOverHRange = new(0.01, 100.0);
    public static readonly ValidityRange EpsRRange = new(1.0, 128.0);

    /// <summary>a(u) — Hammerstad-Jensen's own smoothing exponent, eq. per the cited sources.</summary>
    public static double A(double u)
    {
        double u4 = u * u * u * u;
        double term1 = Math.Log((u4 + (u / 52.0) * (u / 52.0)) / (u4 + 0.432)) / 49.0;
        double term2 = Math.Log(1.0 + Math.Pow(u / 18.1, 3.0)) / 18.7;
        return 1.0 + term1 + term2;
    }

    /// <summary>b(εᵣ).</summary>
    public static double B(double epsR) => 0.564 * Math.Pow((epsR - 0.9) / (epsR + 3.0), 0.053);

    /// <summary>Static effective permittivity εeff(u, εᵣ).</summary>
    public static double StaticEeff(double u, double epsR)
    {
        double avg = (epsR + 1.0) / 2.0;
        double diff = (epsR - 1.0) / 2.0;
        return avg + diff * Math.Pow(1.0 + 10.0 / u, -A(u) * B(epsR));
    }

    /// <summary>f(u), feeding <see cref="StaticZ0Air"/>.</summary>
    public static double F(double u) => 6.0 + (2.0 * Math.PI - 6.0) * Math.Exp(-Math.Pow(30.666 / u, 0.7528));

    /// <summary>Characteristic impedance of the same geometry with air dielectric (εᵣ=1),
    /// Z₀,air(u) — the numerator of the final Z₀ combination.</summary>
    public static double StaticZ0Air(double u)
        => 60.0 * Math.Log(F(u) / u + Math.Sqrt(1.0 + (2.0 / u) * (2.0 / u)));

    /// <summary>Static characteristic impedance Z₀(u, εᵣ) = Z₀,air(u) / √εeff(u, εᵣ).</summary>
    public static double StaticZ0(double u, double epsR) => StaticZ0Air(u) / Math.Sqrt(StaticEeff(u, epsR));

    /// <summary>
    /// The finite-conductor-thickness effective-width correction (§3.1 layer 2 of the brief) —
    /// attributed to Hammerstad &amp; Jensen's own paper by both independent sources checked
    /// (nodeloop.org, scikit-rf's own source comment "Hammerstad and Jensen Article"). Two
    /// separately-corrected widths: <paramref name="u1"/> (undilated by dielectric, feeds
    /// <see cref="StaticZ0Air"/>) and <paramref name="uR"/> (dielectric-weighted, feeds
    /// <see cref="StaticEeff"/>) — using the SAME corrected width for both would double-count the
    /// dielectric weighting embedded in <paramref name="uR"/>'s own extra factor.
    /// </summary>
    public static void ThicknessCorrectedWidths(double wOverH, double tOverH, double epsR, out double u1, out double uR)
    {
        if (tOverH <= 0)
        {
            u1 = wOverH;
            uR = wOverH;
            return;
        }

        double cothArg = Math.Sqrt(6.517 * wOverH);
        double tanhCoth = 1.0 / Math.Tanh(cothArg); // coth(x) = 1/tanh(x)
        double cothSq = tanhCoth * tanhCoth;

        double du1 = (tOverH / Math.PI) * Math.Log(1.0 + (4.0 * Math.E) / (tOverH * cothSq));
        double sech = 1.0 / Math.Cosh(Math.Sqrt(epsR - 1.0));
        double duR = 0.5 * du1 * (1.0 + sech);

        u1 = wOverH + du1;
        uR = wOverH + duR;
    }

    /// <summary>
    /// The full static-model computation for one (W, h, t, εᵣ) — thickness correction folded in
    /// when <paramref name="tMeters"/> &gt; 0. Reports (never silently extrapolates) a W/h or εᵣ
    /// outside <see cref="WOverHRange"/>/<see cref="EpsRRange"/> via <paramref name="reporter"/>.
    /// </summary>
    public static (double Z0, double Eeff) Compute(double wMeters, double hMeters, double tMeters, double epsR,
        MicrostripValidityReporter reporter)
    {
        double wOverH = wMeters / hMeters;
        double tOverH = tMeters / hMeters;

        reporter.CheckRange("Hammerstad-Jensen (static)", "W/h", wOverH, WOverHRange.Min, WOverHRange.Max);
        reporter.CheckRange("Hammerstad-Jensen (static)", "er", epsR, EpsRRange.Min, EpsRRange.Max);

        ThicknessCorrectedWidths(wOverH, tOverH, epsR, out double u1, out double uR);

        double eeff = StaticEeff(uR, epsR);
        double z0Air = StaticZ0Air(u1);
        double z0 = z0Air / Math.Sqrt(eeff);
        return (z0, eeff);
    }

    /// <summary>
    /// R-klp-5: the INVERSE of <see cref="Compute"/> — width synthesis from a target static Z₀,
    /// via bisection over <see cref="Compute"/> itself (never a separately-transcribed synthesis
    /// formula — R7/R-pc-12's "one implementation," applied to the inverse direction too: the
    /// forward and inverse directions can never silently disagree because the inverse literally
    /// calls the forward function on every iteration). Z₀(W) is monotonically decreasing in W for
    /// fixed h/εᵣ, so a plain bisection over <see cref="WOverHRange"/>'s own bounds always
    /// converges. <c>W → Z₀ → W</c> round-trips to the bisection tolerance (a few tens of an ULP-
    /// scale, not exact, since it's a numerical inversion — see the round-trip test's own tolerance).
    /// </summary>
    public static double SynthesizeWidth(double targetZ0, double hMeters, double tMeters, double epsR,
        MicrostripValidityReporter reporter, int iterations = 60)
    {
        var quiet = new MicrostripValidityReporter("(width-synthesis-search, not reported)");
        double wLo = WOverHRange.Min * hMeters;
        double wHi = WOverHRange.Max * hMeters;
        double zAtLo = Compute(wLo, hMeters, tMeters, epsR, quiet).Z0;
        double zAtHi = Compute(wHi, hMeters, tMeters, epsR, quiet).Z0;
        if (targetZ0 > zAtLo || targetZ0 < zAtHi)
        {
            // Target Z0 is outside what any width in the valid W/h range can produce for this
            // substrate — report against the model's own bound rather than returning a clamped,
            // silently-wrong width.
            reporter.CheckRange("Hammerstad-Jensen (synthesis)", "targetZ0", targetZ0, zAtHi, zAtLo, "ohm");
        }

        for (int i = 0; i < iterations; i++)
        {
            double wMid = 0.5 * (wLo + wHi);
            double zMid = Compute(wMid, hMeters, tMeters, epsR, quiet).Z0;
            if (zMid > targetZ0) wLo = wMid; // wider line -> lower Z0; need to go wider
            else wHi = wMid;
        }
        return 0.5 * (wLo + wHi);
    }
}
