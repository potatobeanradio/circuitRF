namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// Frequency dispersion of εeff and Z₀ (§3.1 layer 3 of brief-L5a-pcell-contract-and-microstrip.md
/// / microstrip-models.md §2) — the standard accurate choice over Getsinger's simpler, less
/// accurate model.
///
/// <b>Source: M. Kirschning and R. H. Jansen, "Accurate Wide-Range Design Equations for the
/// Frequency-Dependent Characteristic of Microstrip Lines," IEEE Trans. Microwave Theory Tech.,
/// (the single-line dispersion paper; not to be confused with their separate 1984 coupled-line
/// paper), 1982.</b>
///
/// <b>Provenance caveat (recorded honestly, per R-pc-15/R1's own instruction not to present an
/// unverified value as certain): during implementation research this formula was located in
/// exactly ONE accessible non-GPL source</b> — the scikit-rf project's `skrf/media/mline.py`
/// (BSD-3-Clause, functions `kirsching_er`/`kirsching_zl`, explicitly citing the 1982 paper),
/// fetched and transcribed VERBATIM from the raw source (not a secondary summary) to eliminate
/// transcription risk. The 1982 Electronics-Letters-length original was not independently
/// re-derivable or cross-checked against a second source (it is a short, paywalled note; no second
/// independent reproduction of its P1–P4/R1–R17 coefficients was found). scikit-rf is a mature,
/// actively maintained, widely-used RF library with an explicit citation trail, which is why this
/// is used rather than left unimplemented — but treat it as single-source, not independently
/// cross-checked against a second author's transcription, and revisit if a second source ever
/// turns up. <b>The exact published validity range could not be confirmed from an accessible
/// source either</b>; the range below is the commonly-cited approximate bound from secondary
/// descriptions (u≈0.1–10, εᵣ≈1–20, up to ~60 GHz·mm) and is marked provisional in the reported
/// message rather than asserted as the paper's own stated bound.
///
/// <b>R13 vs. R14 (verified against scikit-rf's exact source, correcting an earlier
/// misreading during this implementation's own research pass):</b> R13 uses εeff DISPERSED AT THE
/// REAL STAMPING FREQUENCY (i.e. <see cref="DispersiveEeff"/> evaluated at the actual fn — not a
/// fixed 1 GHz reference, which an early draft of this file's research notes incorrectly
/// transcribed); R14 uses the plain STATIC εeff(0) — never run through the dispersion formula at
/// all. Getting this backwards silently produces a plausible-but-wrong Z₀(f) curve, which is
/// exactly the class of bug R1 warns a from-memory transcription risks.
/// </summary>
public static class KirschningJansen
{
    public static readonly ValidityRange WOverHRangeProvisional = new(0.1, 10.0);
    public static readonly ValidityRange EpsRRangeProvisional = new(1.0, 20.0);
    public static readonly ValidityRange NormalizedFreqRangeProvisional = new(0.0, 60.0, "GHz·mm");

    /// <summary>Normalized frequency fn = f·h in GHz·mm, from f in Hz and h in metres.</summary>
    public static double NormalizedFreqGhzMm(double freqHz, double hMeters) => freqHz * hMeters * 1.0e-6;

    /// <summary>Dispersive effective permittivity εeff(f), given the static εeff(0) from
    /// <see cref="HammerstadJensen.Compute"/> and the normalized frequency fn (GHz·mm).</summary>
    public static double DispersiveEeff(double u, double epsR, double eeff0, double fn)
    {
        double p1 = 0.27488 + (0.6315 + 0.525 / Math.Pow(1.0 + 0.0157 * fn, 20.0)) * u
                    - 0.065683 * Math.Exp(-8.7513 * u);
        double p2 = 0.33622 * (1.0 - Math.Exp(-0.03442 * epsR));
        double p3 = 0.0363 * Math.Exp(-4.6 * u) * (1.0 - Math.Exp(-Math.Pow(fn / 38.7, 4.97)));
        double p4 = 1.0 + 2.751 * (1.0 - Math.Exp(-Math.Pow(epsR / 15.916, 8.0)));
        double pf = p1 * p2 * Math.Pow((0.1844 + p3 * p4) * fn, 1.5763);

        return epsR - (epsR - eeff0) / (1.0 + pf);
    }

    /// <summary>Dispersive characteristic impedance Z₀(f). <paramref name="eeff0"/> is the plain
    /// static εeff (feeds R14); <paramref name="eeffAtF"/> is εeff dispersed at the ACTUAL stamping
    /// frequency via <see cref="DispersiveEeff"/> (feeds R13) — see this class's own doc comment
    /// for why these must not be swapped or conflated.</summary>
    public static double DispersiveZ0(double u, double epsR, double z0Static, double eeff0,
        double eeffAtF, double fn)
    {
        double r1 = Math.Min(0.03891 * Math.Pow(epsR, 1.4), 20.0);
        double r2 = Math.Min(0.2671 * Math.Pow(u, 7.0), 20.0);
        double r3 = 4.766 * Math.Exp(-3.228 * Math.Pow(u, 0.641));
        double r4 = 0.016 + Math.Pow(0.0514 * epsR, 4.524);
        double r5 = Math.Pow(fn / 28.843, 12.0);
        double r6 = Math.Min(22.20 * Math.Pow(u, 1.92), 20.0);
        double r7 = 1.206 - 0.3144 * Math.Exp(-r1) * (1.0 - Math.Exp(-r2));
        double r8 = 1.0 + 1.275 * (1.0 - Math.Exp(-0.004625 * r3 * Math.Pow(epsR, 1.674) * Math.Pow(fn / 18.365, 2.745)));
        double epsRm1 = epsR - 1.0;
        double epsRm1p6 = Math.Pow(epsRm1, 6.0);
        double r9 = 5.086 * r4 * r5 / (0.3838 + 0.386 * r4) * Math.Exp(-r6) / (1.0 + 1.2992 * r5)
                    * epsRm1p6 / (1.0 + 10.0 * epsRm1p6);
        double r10 = 0.00044 * Math.Pow(epsR, 2.136) + 0.0184;
        double r11term = Math.Pow(fn / 19.47, 6.0);
        double r11 = r11term / (1.0 + 0.0962 * r11term);
        double r12 = 1.0 / (1.0 + 0.00245 * u * u);
        double r13 = 0.9408 * Math.Pow(eeffAtF, r8) - 0.9603;
        double r14 = (0.9408 - r9) * Math.Pow(eeff0, r8) - 0.9603;
        double r15 = 0.707 * r10 * Math.Pow(fn / 12.3, 1.097);
        double r16 = 1.0 + 0.0503 * epsR * epsR * r11 * (1.0 - Math.Exp(-Math.Pow(u / 15.0, 6.0)));
        double r17 = r7 * (1.0 - 1.1241 * r12 / r16 * Math.Exp(-0.026 * Math.Pow(fn, 1.15656) - r15));

        return z0Static * Math.Pow(r13 / r14, r17);
    }

    /// <summary>
    /// The full dispersive computation at one stamping frequency, folding in the f=1GHz auxiliary
    /// εeff term <see cref="DispersiveZ0"/> needs internally. Reports (never silently extrapolates)
    /// a W/h, εᵣ, or normalized-frequency value outside the provisional ranges above.
    /// </summary>
    public static (double Z0, double Eeff) Compute(double freqHz, double u, double epsR, double hMeters,
        double z0Static, double eeff0, MicrostripValidityReporter reporter)
    {
        double fn = NormalizedFreqGhzMm(freqHz, hMeters);

        reporter.CheckRange("Kirschning-Jansen (dispersion, provisional range)", "W/h", u,
            WOverHRangeProvisional.Min, WOverHRangeProvisional.Max);
        reporter.CheckRange("Kirschning-Jansen (dispersion, provisional range)", "er", epsR,
            EpsRRangeProvisional.Min, EpsRRangeProvisional.Max);
        reporter.CheckRange("Kirschning-Jansen (dispersion, provisional range)", "f*h", fn,
            NormalizedFreqRangeProvisional.Min, NormalizedFreqRangeProvisional.Max, NormalizedFreqRangeProvisional.Units);

        double eeffAtF = DispersiveEeff(u, epsR, eeff0, fn);
        double z0 = DispersiveZ0(u, epsR, z0Static, eeff0, eeffAtF, fn);
        return (z0, eeffAtF);
    }
}
