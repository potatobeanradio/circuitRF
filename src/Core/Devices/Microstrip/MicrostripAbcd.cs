using System.Numerics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// A 2-port ABCD (chain) matrix — the natural representation for cascading a non-uniform line's
/// short uniform sections in physical order (MTaper, MKlopf; brief-mtaper-mklopf.md §1). Shared so
/// neither component hand-rolls its own cascade/ABCD-to-Z conversion.
/// </summary>
public readonly struct MicrostripAbcd(Complex a, Complex b, Complex c, Complex d)
{
    public Complex A { get; } = a;
    public Complex B { get; } = b;
    public Complex C { get; } = c;
    public Complex D { get; } = d;

    public static readonly MicrostripAbcd Identity = new(Complex.One, Complex.Zero, Complex.Zero, Complex.One);

    /// <summary>The ABCD matrix of one uniform line section of characteristic impedance
    /// <paramref name="z0"/> and complex electrical length <paramref name="gammaLength"/> = γ·length.</summary>
    public static MicrostripAbcd UniformSection(Complex z0, Complex gammaLength)
    {
        Complex cosh = Complex.Cosh(gammaLength);
        Complex sinh = Complex.Sinh(gammaLength);
        return new MicrostripAbcd(cosh, z0 * sinh, sinh / z0, cosh);
    }

    /// <summary>Cascades <c>this</c> (closer to port 1) with <paramref name="next"/> (closer to
    /// port 2): matrix product <c>this · next</c>, ABCD's own composition rule.</summary>
    public MicrostripAbcd Cascade(in MicrostripAbcd next) => new(
        A * next.A + B * next.C,
        A * next.B + B * next.D,
        C * next.A + D * next.C,
        C * next.B + D * next.D);

    /// <summary>Converts to 2-port Z-parameters (valid whenever <c>C ≠ 0</c>, true for any real
    /// line section with nonzero length).</summary>
    public (Complex Z11, Complex Z12, Complex Z21, Complex Z22) ToZ()
        => (A / C, (A * D - B * C) / C, Complex.One / C, D / C);
}
