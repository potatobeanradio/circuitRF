// ================================================================
//  PaClassPresets.cs — R9D §3
//
//  The five PA-class preset terminations. Source: Sharma, T. (2018). Modelling and Design
//  Methodology of Higher-Efficiency Harmonic Tuned Power Amplifiers for 5G Applications (Doctoral
//  thesis, University of Calgary). https://prism.ucalgary.ca/handle/1880/106695.
//
//  Every value here is INTRINSIC and assumes Z0 = R_opt (the document's own Z0). The extrinsic
//  transform (IntrinsicAbcd) and the marker walk are the src/Ui side — this file is pure math over
//  Complex/double so the physics is testable with no view model.
// ================================================================

using System;
using System.Numerics;

namespace CircuitRF.Harmonica;

/// <summary>R9D §3.1's five presets.</summary>
public enum PaClass { B, J, JStar, F, FInverse }

public static class PaClassPresets
{
    /// <summary>
    /// How far a preset's "short" and "open" bands sit from the reference — owner ruling, Round 10
    /// (2026-08-15): a factor of 100 either side of <c>Z0</c>, i.e. a short is <c>Z0/100</c> and an
    /// open is <c>Z0·100</c>, replacing the absolute 1e-6 Ω / 1e6 Ω pair these used to be.
    ///
    /// <para><b>Why, in the owner's own words: convergence and legibility.</b> A 1e-6 Ω band presented
    /// to a Class-F device is eleven orders of magnitude below its own fundamental load, which is
    /// exactly the condition-number spend §6.2 already records for the ideal 1 F block, and it makes
    /// the contour raster around that band degenerate. Z0/100 and Z0·100 are still a short and an open
    /// for every practical purpose (at Z0 = 50 Ω: 0.5 Ω and 5 kΩ, |Γ| = 0.980 and 0.980) while leaving
    /// the solver a well-scaled problem.</para>
    ///
    /// <para><b>This governs PRESETS only.</b> A band with no marker of its own is not written by a
    /// preset at all and stays at <see cref="TerminationSet.UnmarkedBandOhms"/> — the owner's own
    /// instruction, and the reason that constant is untouched here.</para>
    /// </summary>
    public const double ShortOpenFactor = 100.0;

    /// <summary>The near-short a preset writes at <paramref name="z0"/> — see <see cref="ShortOpenFactor"/>.</summary>
    public static double NearShort(double z0) => z0 / ShortOpenFactor;

    /// <summary>The near-open a preset writes at <paramref name="z0"/> — see <see cref="ShortOpenFactor"/>.</summary>
    public static double NearOpen(double z0) => z0 * ShortOpenFactor;

    /// <summary>
    /// The INTRINSIC target for one load band under one class, given <paramref name="z0"/> = R_opt. Pure.
    ///
    /// <para>Class B/J/J* special-case band 2 (its own formula, distinct from the near-short every band
    /// ≥ 3 gets); Class F/F⁻¹ do not — band 2 falls out of the same even/odd rule as every other band,
    /// per §3.1's own note that the table lists it separately only for readability.</para>
    /// </summary>
    public static Complex IntrinsicLoad(PaClass paClass, int band, double z0)
    {
        if (band < 1) throw new ArgumentOutOfRangeException(nameof(band), "band is 1-based");

        switch (paClass)
        {
            case PaClass.B:
                return band == 1 ? new Complex(z0, 0) : new Complex(NearShort(z0), 0);

            case PaClass.J:
            case PaClass.JStar:
            {
                // α = 0.5 for J, −0.5 for J* — negating α is exactly conjugation of both band-1 and
                // band-2's formulas (Z0 real), which is what makes J* the complex conjugate of J band
                // by band.
                double alpha = paClass == PaClass.J ? 0.5 : -0.5;
                if (band == 1) return z0 * new Complex(1, -alpha);
                if (band == 2) return z0 * new Complex(0, 3.0 * Math.PI * alpha / 8.0);
                return new Complex(NearShort(z0), 0);
            }

            case PaClass.F:
                if (band == 1) return new Complex(2.0 * z0 / Math.Sqrt(3), 0);
                return band % 2 == 0 ? new Complex(NearShort(z0), 0) : new Complex(NearOpen(z0), 0);

            case PaClass.FInverse:
                if (band == 1)
                    return new Complex(Math.Sqrt(2) * z0 / 2 / (0.5 - 8.0 / 9.0 / Math.PI / Math.PI), 0);
                return band % 2 == 0 ? new Complex(NearOpen(z0), 0) : new Complex(NearShort(z0), 0);

            default:
                throw new ArgumentOutOfRangeException(nameof(paClass));
        }
    }
}
