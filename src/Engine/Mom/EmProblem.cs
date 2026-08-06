using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Physical constants for the MoM kernel.  µ₀ is the pre-2019 exact value, matching
/// <c>CircuitRF.Core.Devices.Microstrip.MicrostripLoss.Mu0</c> so the two loss models can be
/// compared without a constant-level disagreement; ε₀ is then *derived* as 1/(µ₀c²) so that
/// µ₀ε₀ = 1/c² holds to the last bit — the TEM identity [L] = µ₀ε₀[C₀]⁻¹ depends on it.
/// </summary>
public static class EmConstants
{
    public const double Mu0 = 4.0 * Math.PI * 1.0e-7;          // H/m
    public const double C0  = 2.99792458e8;                    // m/s, exact by SI definition
    public const double Eps0 = 1.0 / (Mu0 * C0 * C0);          // F/m  (8.8541878176e-12)
}

/// <summary>A point in the cross-section plane. Metres (R-mom-2) — never DBU.</summary>
public readonly record struct EmPoint(double X, double Y)
{
    public static EmPoint operator +(EmPoint a, EmPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static EmPoint operator -(EmPoint a, EmPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static EmPoint operator *(EmPoint a, double s)   => new(a.X * s, a.Y * s);

    public double Dot(EmPoint o)  => X * o.X + Y * o.Y;
    public double Norm            => Math.Sqrt(X * X + Y * Y);

    /// <summary>The left normal (−y, x) — the frame convention of the design brief §3.2.</summary>
    public EmPoint LeftNormal  => new(-Y, X);
    /// <summary>The right normal (y, −x) — outward for a CCW-wound polygon.</summary>
    public EmPoint RightNormal => new(Y, -X);
}

/// <summary>
/// A linear isotropic dielectric. <see cref="EpsComplex"/> is the R-mom-6 complex relative
/// permittivity ε* = εᵣ(1 − j·tanδ): loss enters the system as an imaginary part, not as a
/// separate partial-capacitance accumulation.
/// </summary>
public sealed record EmMaterial(double EpsR, double TanD = 0, double MuR = 1)
{
    public static readonly EmMaterial Air = new(1.0);

    public Complex EpsComplex => new(EpsR, -EpsR * TanD);
}

/// <summary>
/// A laterally infinite horizontal dielectric slab. Regions are ordered bottom-to-top and must
/// tile the y axis without gaps or overlap; the topmost and bottommost extend to ±infinity
/// (<see cref="double.PositiveInfinity"/> / <see cref="double.NegativeInfinity"/>).
/// R-mom-3: interfaces are *implied* by this list, never authored directly.
/// </summary>
public sealed record EmDielectricRegion(double YBottom, double YTop, EmMaterial Material);

/// <summary>
/// A closed, simple polygon in the cross-section plane with finite thickness (R-mom-4 — never a
/// zero-thickness sheet). The outline is not required to be wound CCW on input; the mesher
/// normalises the winding so the outward normal is well defined.
/// </summary>
public sealed record EmConductor(string Name, IReadOnlyList<EmPoint> Outline, double SigmaSm);

/// <summary>
/// Laterally infinite perfect/lossy plane at y = <paramref name="Y"/>. Handled by an exact image
/// (R-mom-7), never meshed. <c>SigmaSm = double.PositiveInfinity</c> means a perfect conductor.
/// </summary>
public sealed record EmGroundPlane(double Y, double SigmaSm);

/// <summary>
/// A transmission-line port. <c>ReferenceConductor == null</c> means "the ground plane". Kernel W
/// will require this to be explicit; carrying it from day one is what keeps that promise cheap.
/// </summary>
public sealed record EmPort(int Number, string Conductor, string? ReferenceConductor, Complex Z0);

/// <summary>
/// The neutral EM problem the kernel consumes (R-mom-1). SI units throughout: metres,
/// siemens/metre, radians, hertz. It knows nothing about DBU, <c>.clay</c> shapes, layer tables
/// or <c>LayerKey</c> — the Ui-side cross-section extractor produces this, and producing it is
/// what extraction already had to do.
/// </summary>
public sealed record EmProblem(
    IReadOnlyList<EmConductor>        Conductors,
    IReadOnlyList<EmDielectricRegion> Regions,
    EmGroundPlane?                    Ground,
    IReadOnlyList<EmPort>             Ports,
    double                            LengthMeters)
{
    /// <summary>Relative complex permittivity at height <paramref name="y"/>.</summary>
    public Complex EpsAt(double y)
    {
        for (int i = 0; i < Regions.Count; i++)
        {
            var r = Regions[i];
            bool last = i == Regions.Count - 1;
            if (y >= r.YBottom && (y < r.YTop || (last && y <= r.YTop)))
                return r.Material.EpsComplex;
        }
        // Outside every listed region (a caller that skipped CanSolve) — treat as free space
        // rather than throwing from inside a fill loop.
        return Complex.One;
    }

    public EmConductor? FindConductor(string name)
    {
        foreach (var c in Conductors)
            if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
        return null;
    }

    public int IndexOfConductor(string name)
    {
        for (int i = 0; i < Conductors.Count; i++)
            if (string.Equals(Conductors[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
