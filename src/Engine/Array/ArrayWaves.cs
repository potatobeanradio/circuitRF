// brief-paa-2 R-paa2-6 — what the array's coupling does to the waves (note §2.3, §2.4):
//
//     b = S·a,     Γ_act,n = b_n / a_n,     P_acc = ½·Σ (|a_n|² − |b_n|²)
//
// in the co-simulation's peak-wave convention. The product is sparse (ArraySparseMatrix), so nothing here is N×N. An element whose
// a_n is zero — switched off — has no active reflection and reports none, rather than a number divided by nothing.
//
// The power balance is what the pattern radiates over what the array accepts: on a lossless structure solved whole it is the
// radiation efficiency, and how far an approximate level drifts from that is the readout note §2.4 asks for. The radiated power is
// the integral of the pattern's U over its own grid by PlanarFarFieldPattern's quadrature, so it is the number a single EM run states.

using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>R-paa2-6 — the waves leaving the elements, each element's active reflection (null where switched off), and the powers, in
/// the units of |a|² with ½ for peak waves.</summary>
public sealed record ArrayActive(Complex[] B, Complex?[] Gamma, double IncidentW, double AcceptedW)
{
    /// <summary>The largest |Γ_act| over the elements that are on; zero when none is.</summary>
    public double WorstGamma => Gamma.Where(g => g.HasValue).Select(g => g!.Value.Magnitude).DefaultIfEmpty(0).Max();
}

/// <summary>R-paa2-6 — active reflection and power.</summary>
public static class ArrayWaves
{
    /// <summary>Free-space impedance, µ₀·c.</summary>
    public const double Eta0 = EmConstants.Mu0 * EmConstants.C0;

    /// <summary>b = S·a and Γ_act per element, with the incident and accepted powers.</summary>
    public static ArrayActive Active(ArraySparseMatrix s, IReadOnlyList<Complex> a)
    {
        int n = s.Size;
        if (a.Count != n) throw new ArgumentException($"{n} elements and {a.Count} incident waves.");
        var incident = new Complex[n];
        for (int i = 0; i < n; i++) incident[i] = a[i];
        var b = new Complex[n];
        s.Multiply(incident, b);
        var gamma = new Complex?[n];
        double inc = 0, acc = 0;
        for (int i = 0; i < n; i++)
        {
            double ai = incident[i].Magnitude, bi = b[i].Magnitude;
            inc += 0.5 * ai * ai;
            acc += 0.5 * (ai * ai - bi * bi);
            gamma[i] = incident[i] == Complex.Zero ? null : b[i] / incident[i];
        }
        return new ArrayActive(b, gamma, inc, acc);
    }

    /// <summary>The power a pattern on a grid carries away, W, with U = (|Eθ|² + |Eφ|²)/2η₀ — the fields in r-normalised volts.</summary>
    public static double RadiatedPowerW(ArrayField field)
    {
        if (field.Directions.Grid is not { } grid)
            throw new ArgumentException("A radiated power is integrated over a grid: evaluate the pattern on the record's grid (the overview).");
        return Pattern(field, grid).RadiatedPowerW;
    }

    /// <summary>The pattern as a planar run states one, so its quadrature and its U are that run's.</summary>
    public static PlanarFarFieldPattern Pattern(ArrayField field, PlanarFarFieldGrid grid, double hz = 0)
    {
        var u = new double[field.ETheta.Length];
        for (int d = 0; d < u.Length; d++) u[d] = field.Power(d) / (2 * Eta0);
        return new PlanarFarFieldPattern(grid, field.ETheta, field.EPhi, u, 1, hz);
    }

    /// <summary>Radiated over accepted power: the readout of note §2.4.</summary>
    public static double PowerBalance(double radiatedW, double acceptedW) => radiatedW / acceptedW;
}
