// SI-LOW — THE SERIES TERM A CONDUCTING SUBSTRATE ADDS TO A LINE, AND A β THAT KNOWS ABOUT IT
//
// Below the crossover γ = √((R + jωL)(jωC*)) takes L from the geometry with the dielectric removed, which
// has no current in the silicon. Above about 10 Ω·cm that holds, because the magnetic field passes through
// the silicon to the backside metal. Below it the return current moves into the silicon, so loss rises and
// L falls, and on 0.01 Ω·cm that read α 22× low and β 44 % high at 20 GHz.
//
// The term is read from a one-dimensional solve across the port's own transverse cells. A pulse of current
// in x sees the layered stack through one transverse wavenumber k, with no displacement current:
//
//     γ_layer = √(k² + jωµσ)     Z_c,layer = jωµ/γ_layer     Z_TE(k) = Z_down ∥ Z_up
//     Z_ij    = (1/π) ∫ Z_TE(k) · sinc(k·dᵢ/2) · sinc(k·dⱼ/2) · cos(k·(cᵢ − cⱼ)) dk
//     Z_ii   += Z_s/dᵢ, the strip's own sheet impedance
//     Z_ser   = 1 / (1ᵀ Z⁻¹ 1)
//
// **γ receives Z_ser(σ) − Z_ser(0), never Z_ser(σ).** R + jωL₀ is already there, off the standard's own
// charge, and subtracting the same solve with σ removed leaves that term alone as σ → 0. It also keeps the
// floor from being counted twice, since Z_down ends on the floor's surface impedance in both solves.
//
// Against a 2D finite-element solve of the same cross-section, R and ΔL agree within 0.7 % from 1 to 40 GHz
// at 10 to 1e4 S/m (RESOLVED.md §SI-LOW). The cost is milliseconds per frequency and no mesh.
//
// **A second solve over the static TM line gives C*, so the same object knows β at any frequency without a
// standard, a mesh or a previous point.** That is what sizes a conducting stack's separations and predicts
// its branch: `EstimateBeta` reads only εᵣ and sat 3× low at 40 GHz on 0.1 Ω·cm, which aliased the two-line
// extraction past βΔℓ = π, and scaling the previous point's β by frequency took a wrong branch on 1 Ω·cm.

using System.Collections.Concurrent;
using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>The series impedance and the propagation constant one conducting stack gives one port's strip.</summary>
public sealed class PlanarSubstrateReturn
{
    /// <summary>The term is supplied above this conductivity, S/m, and not at it. A substrate at 10 Ω·cm and
    /// above keeps the numbers it has: the term there is 8.5 Ω/m at 5 GHz and 545 Ω/m at 40 GHz, on a line
    /// whose ωL is thousands.</summary>
    public const double MinConductivitySm = 10.0;

    // The wavenumber grid. k = 1 rad/m is far below anything a planar stack resolves, and 10^8.5 rad/m is far
    // above any cell. The difference γ reads falls off as k⁻³ past 1/δ, so the top of the grid only has to
    // bound the part both solves share.
    private const int    Wavenumbers = 6001;
    private const double DecadesOfK  = 8.5;

    private readonly LayerStack _stack;
    private readonly int        _layersBelow;
    private readonly double[]   _centre, _width;
    private readonly double     _stripSigmaSm, _stripThicknessM;
    private readonly double[]   _k, _dk;
    private readonly double[,]  _sinc;

    private readonly ConcurrentDictionary<double, Complex> _withSigma = new(), _withoutSigma = new();
    private readonly ConcurrentDictionary<double, double>  _beta      = new();

    private PlanarSubstrateReturn(LayerStack stack, int layersBelow, IReadOnlyList<double> lines,
                                  double stripSigmaSm, double stripThicknessM)
    {
        _stack = stack;
        _layersBelow = layersBelow;
        _stripSigmaSm = stripSigmaSm;
        _stripThicknessM = stripThicknessM;

        int n = lines.Count - 1;
        _centre = new double[n];
        _width  = new double[n];
        for (int i = 0; i < n; i++)
        {
            _centre[i] = 0.5 * (lines[i] + lines[i + 1]);
            _width[i]  = lines[i + 1] - lines[i];
        }

        _k  = new double[Wavenumbers];
        _dk = new double[Wavenumbers];
        for (int q = 0; q < Wavenumbers; q++) _k[q] = Math.Pow(10.0, DecadesOfK * q / (Wavenumbers - 1));
        for (int q = 0; q < Wavenumbers; q++)
            _dk[q] = q == 0 ? _k[1] - _k[0]
                   : q == Wavenumbers - 1 ? _k[q] - _k[q - 1]
                   : 0.5 * (_k[q + 1] - _k[q - 1]);

        _sinc = new double[n, Wavenumbers];
        for (int i = 0; i < n; i++)
            for (int q = 0; q < Wavenumbers; q++)
            {
                double x = _k[q] * _width[i] / 2.0;
                _sinc[i, q] = x < 1e-8 ? 1.0 : Math.Sin(x) / x;
            }
    }

    /// <summary>
    /// The term for one port on this problem, or null when it does not apply: nothing here conducts above
    /// <see cref="MinConductivitySm"/>, or the standard is not one strip over the floor. A coplanar pair, a
    /// standard widened for a neighbour and a coupled group each carry more than one current, so they decline
    /// and <see cref="PlanarConductingMedium.Findings"/> keeps naming the range.
    /// </summary>
    public static PlanarSubstrateReturn? TryFor(PlanarProblem problem, PlanarPortResolution port, bool perfectStrip)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(port);

        var stack = problem.EffectiveStack;
        if (!stack.Layers.Any(l => l.Material.SigmaSm > MinConductivitySm)) return null;
        if (port.Reference != PlanarPortReference.GroundPlane || port.Group is not null ||
            port.Neighbourhood is not null || port.TransverseLines.Count < 2) return null;

        // A magnetic wall has no surface impedance for this ladder to end on, and nothing ships one.
        if (stack.Bottom.Kind == TerminationKind.Pmc || stack.Top.Kind == TerminationKind.Pmc) return null;

        double levelZ = problem.LevelZ(port.LayerIndex);
        int below = 0;
        while (below < stack.LayerCount && stack.InterfaceZ[below + 1] <= levelZ + 1e-12) below++;

        var metal = problem.Layers[Math.Clamp(port.LayerIndex, 0, problem.Layers.Count - 1)];
        return new PlanarSubstrateReturn(stack, below, port.TransverseLines,
                                         perfectStrip ? double.PositiveInfinity : metal.SigmaSm, metal.ThicknessM);
    }

    /// <summary>What the conducting layers add to the line's R + jωL, Ω/m: zero as σ → 0.</summary>
    public Complex SeriesPerMetreAt(double fHz) => Series(fHz, true) - Series(fHz, false);

    /// <summary>β at one frequency, rad/m, with no mesh and no earlier point. Within 3 % of the two-line value
    /// at the 13 points measured, against the ±20 % a separation or a 2π branch needs.</summary>
    public double EstimatedBetaAt(double fHz) => _beta.GetOrAdd(fHz, f =>
    {
        double omega = 2.0 * Math.PI * f;
        Complex y = Complex.ImaginaryOne * omega * StaticCapacitance(f);
        return Complex.Sqrt(Series(f, true) * y).Imaginary;
    });

    /// <summary>Z_ser, with or without the layers' conductivity.</summary>
    private Complex Series(double fHz, bool withSigma) =>
        (withSigma ? _withSigma : _withoutSigma).GetOrAdd(fHz, f =>
        {
            double omega = 2.0 * Math.PI * f;
            var kernel = new Complex[Wavenumbers];
            for (int q = 0; q < Wavenumbers; q++) kernel[q] = MagneticLine(_k[q], omega, withSigma);

            var z = PulseMatrix(kernel);
            Complex sheet = PlanarSurfaceImpedance.Sheet(_stripSigmaSm, _stripThicknessM, omega);
            for (int i = 0; i < _width.Length; i++) z[i, i] += sheet / _width[i];
            return Complex.One / SumOfSolution(z);
        });

    /// <summary>C*, F/m, from the static TM line: charge pulses at one volt over ε* of this frequency.</summary>
    private Complex StaticCapacitance(double fHz)
    {
        var medium = _stack.AtFrequency(fHz);
        var kernel = new Complex[Wavenumbers];
        for (int q = 0; q < Wavenumbers; q++)
            kernel[q] = 1.0 / (EmConstants.Eps0 * _k[q] * (LookDownTm(medium, _k[q]) + LookUpTm(medium, _k[q])));
        return SumOfSolution(PulseMatrix(kernel));
    }

    /// <summary>The Galerkin matrix of a spectral kernel over the transverse pulses.</summary>
    private Complex[,] PulseMatrix(Complex[] kernel)
    {
        int n = _width.Length;
        var weight = new Complex[Wavenumbers];
        for (int q = 0; q < Wavenumbers; q++) weight[q] = kernel[q] * (_dk[q] / Math.PI);

        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                double gap = _centre[i] - _centre[j];
                double re = 0, im = 0;
                for (int q = 0; q < Wavenumbers; q++)
                {
                    double shape = _sinc[i, q] * _sinc[j, q] * Math.Cos(_k[q] * gap);
                    re += weight[q].Real * shape;
                    im += weight[q].Imaginary * shape;
                }
                m[i, j] = m[j, i] = new Complex(re, im);
            }
        return m;
    }

    /// <summary>Z_TE at the level: the stack below and the stack above, in parallel.</summary>
    private Complex MagneticLine(double k, double omega, bool withSigma)
    {
        Complex down = EndImpedance(_stack.Bottom, k, omega, withSigma);
        for (int i = 0; i < _layersBelow; i++)
            down = Through(_stack.Layers[i], down, k, omega, withSigma);

        Complex up = EndImpedance(_stack.Top, k, omega, withSigma);
        for (int i = _stack.LayerCount - 1; i >= _layersBelow; i--)
            up = Through(_stack.Layers[i], up, k, omega, withSigma);

        return down * up / (down + up);
    }

    private static Complex EndImpedance(Termination end, double k, double omega, bool withSigma)
    {
        if (end.Kind != TerminationKind.HalfSpace) return end.SurfaceImpedanceAt(omega);
        double sigma = withSigma ? end.Material.SigmaSm : 0.0;
        Complex jwm = new Complex(0.0, omega * EmConstants.Mu0 * end.Material.MuR);
        return jwm / Complex.Sqrt(new Complex(k * k, 0.0) + jwm * sigma);
    }

    private static Complex Through(MediumLayer layer, Complex load, double k, double omega, bool withSigma)
    {
        var m = layer.Material;
        Complex jwm   = new Complex(0.0, omega * EmConstants.Mu0 * m.MuR);
        Complex gamma = Complex.Sqrt(new Complex(k * k, 0.0) + jwm * (withSigma ? m.SigmaSm : 0.0));
        Complex zc    = jwm / gamma;
        Complex t     = TanhOf(gamma * layer.ThicknessM);
        return zc * (load + zc * t) / (zc + load * t);
    }

    private Complex LookDownTm(LayerStack medium, double k)
    {
        Complex? y = medium.Bottom.Kind == TerminationKind.HalfSpace ? medium.Bottom.Material.EpsComplex : null;
        for (int i = 0; i < _layersBelow; i++) y = ThroughTm(medium.Layers[i], y, k);
        return y ?? new Complex(1e30, 0.0);
    }

    private Complex LookUpTm(LayerStack medium, double k)
    {
        Complex? y = medium.Top.Kind == TerminationKind.HalfSpace ? medium.Top.Material.EpsComplex : null;
        for (int i = medium.LayerCount - 1; i >= _layersBelow; i--) y = ThroughTm(medium.Layers[i], y, k);
        return y ?? new Complex(1e30, 0.0);
    }

    /// <summary>ε*·k-normalised admittance through one layer; a null load is a conductor, an electrostatic short.</summary>
    private static Complex ThroughTm(MediumLayer layer, Complex? load, double k)
    {
        Complex eps = layer.Material.EpsComplex;
        Complex t   = TanhOf(new Complex(k * layer.ThicknessM, 0.0));
        return load is { } y ? eps * (y + eps * t) / (eps + y * t) : eps / t;
    }

    /// <summary>tanh, which is 1 well past where <see cref="Complex.Tanh"/> overflows to NaN.</summary>
    private static Complex TanhOf(Complex x) => x.Real > 20.0 ? Complex.One : Complex.Tanh(x);

    /// <summary>1ᵀ A⁻¹ 1, by elimination with partial pivoting. A is destroyed.</summary>
    private static Complex SumOfSolution(Complex[,] a)
    {
        int n = a.GetLength(0);
        var b = new Complex[n];
        Array.Fill(b, Complex.One);

        for (int p = 0; p < n; p++)
        {
            int pivot = p;
            for (int r = p + 1; r < n; r++)
                if (a[r, p].Magnitude > a[pivot, p].Magnitude) pivot = r;
            if (pivot != p)
            {
                for (int j = p; j < n; j++) (a[p, j], a[pivot, j]) = (a[pivot, j], a[p, j]);
                (b[p], b[pivot]) = (b[pivot], b[p]);
            }
            for (int r = p + 1; r < n; r++)
            {
                Complex f = a[r, p] / a[p, p];
                if (f == Complex.Zero) continue;
                for (int j = p; j < n; j++) a[r, j] -= f * a[p, j];
                b[r] -= f * b[p];
            }
        }

        Complex sum = Complex.Zero;
        for (int p = n - 1; p >= 0; p--)
        {
            Complex v = b[p];
            for (int j = p + 1; j < n; j++) v -= a[p, j] * b[j];
            b[p] = v / a[p, p];
            sum += b[p];
        }
        return sum;
    }
}
