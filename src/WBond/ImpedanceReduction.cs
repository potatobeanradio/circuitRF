using System.Numerics;

namespace CircuitRF.WBond;

/// <summary>
/// The array-basis <b>impedance</b> the simulator stamps (wbond.md §5.3, WB19a; R-wbb-3).
///
/// <h3>The exact reduction</h3>
/// <code>
/// Z_arr(ω) = ( Aᵀ Z(ω)⁻¹ A )⁻¹,    Z(ω) = R(ω) + jω( L + L_int(ω) )
/// </code>
/// <para>Owner decision, 2026-08-07: the stamp uses this rather than reducing R and L
/// independently. Both cost one N × N factorisation per frequency, but reducing them separately does
/// so on <b>inconsistent current distributions</b> — it implicitly lets the current share one way for
/// the resistive part and another for the inductive part, when physically the sharing is set by R and
/// L together. The gap is largest exactly where it is easiest to be wrong: low frequency, lossy
/// aluminium, and the 85 °C operating point.</para>
///
/// <h3>Two structural facts that make a sweep affordable</h3>
/// <para><b>L is frequency-independent and is filled once.</b> Refilling it per frequency point would
/// cost ~0.15 s × the sweep length, and is the single easiest way to make this unusably slow.</para>
/// <para><b>The frequency dependence is diagonal.</b> <c>Z(ω) = jω·L + D(ω)</c> with <c>D</c> holding
/// only per-wire <c>R(f)</c> and <c>jω·L_int(f)</c>. When every wire shares a radius and a metal —
/// the common case for an array — <c>D</c> is a scalar multiple of the identity, which opens the
/// eigendecomposition route named in brief-wbond-wbb §0.3 item 3. That route is deliberately
/// <b>not</b> built until a measurement says it is needed.</para>
///
/// <h3>Why not Cholesky</h3>
/// <para><c>Z</c> is complex <b>symmetric</b>, not Hermitian, so <see cref="CholeskyFactor"/> does not
/// apply — see <see cref="ComplexLu"/>.</para>
/// </summary>
public sealed class ImpedanceReduction
{
    private readonly WireMesh _mesh;
    private readonly InductanceMatrix _l;
    private readonly double[] _pathLength;   // metres, per wire
    private readonly double[] _radius;       // metres, per wire
    private readonly double[] _sigma;        // S/m at the operating temperature, per wire
    private readonly bool _includeCapacitance;
    private readonly bool _parallel;
    private CapacitanceReduction? _capacitance;
    private bool _capacitanceBuilt;

    private ImpedanceReduction(WireMesh mesh, InductanceMatrix l,
                               double[] pathLength, double[] radius, double[] sigma,
                               bool includeCapacitance, bool parallel)
    {
        _mesh = mesh;
        _l = l;
        _pathLength = pathLength;
        _radius = radius;
        _sigma = sigma;
        _includeCapacitance = includeCapacitance;
        _parallel = parallel;
    }

    public int WireCount => _l.Order;

    public int ArrayCount => _mesh.ArrayCount;

    /// <summary>The frequency-independent wire-basis inductance, filled once.</summary>
    public InductanceMatrix Inductance => _l;

    /// <summary>
    /// The array-basis capacitance (wbond.md §3.7), or <c>null</c> when there is none to have.
    ///
    /// <para><b>Null means NOT COMPUTED — <b>P</b> is never filled and never factorised.</b> That is
    /// what <c>IncludeCapacitance = false</c> has to mean for the flag-off answer to be bit-identical
    /// to the build before capacitance existed (gate C1); computing it and stamping zeros would leave
    /// the last bits of every reduction at the mercy of a different code path. It is also null when
    /// the ground plane is disabled, because then there is no reference conductor to be capacitive
    /// to — see <see cref="CapacitanceReduction.Create(WBondDesign, bool)"/>.</para>
    ///
    /// <para>Built <b>lazily</b>: a caller that only wants <see cref="ArrayImpedance"/> — the series
    /// arm, which capacitance does not enter — must not pay the ~25 % fill for it.</para>
    /// </summary>
    public CapacitanceReduction? Capacitance
    {
        get
        {
            if (_capacitanceBuilt) return _capacitance;
            _capacitanceBuilt = true;
            _capacitance = _includeCapacitance ? CapacitanceReduction.Create(_mesh, _parallel) : null;
            return _capacitance;
        }
    }

    /// <summary>
    /// Builds the reduction for a design: fills <b>L</b> once and caches each wire's per-metre
    /// properties at the design's operating temperature.
    /// </summary>
    public static ImpedanceReduction Create(WBondDesign design, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(design);
        return Create(design, design.IncludeCapacitance, parallel);
    }

    /// <summary>
    /// The same, with the capacitance flag given explicitly — the route a placed component takes,
    /// because its <c>IncludeCapacitance</c> instance parameter overrides what the design itself says.
    /// </summary>
    public static ImpedanceReduction Create(WBondDesign design, bool includeCapacitance, bool parallel)
    {
        ArgumentNullException.ThrowIfNull(design);

        var mesh = WireMesh.Build(design);
        var l = InductanceMatrix.Fill(mesh, parallel);

        int n = mesh.WireCount;
        var pathLength = new double[n];
        var radius = new double[n];
        var sigma = new double[n];

        for (int w = 0; w < n; w++)
        {
            var wire = mesh.Wires[w];
            pathLength[w] = wire.PathLengthMetres();
            radius[w] = wire.RadiusMetres;
            sigma[w] = design.MaterialFor(wire).SigmaAt(design.OperatingTempC);
        }

        return new ImpedanceReduction(mesh, l, pathLength, radius, sigma, includeCapacitance, parallel);
    }

    /// <summary>
    /// The per-wire series impedance at one frequency — the diagonal <c>D(ω)</c>.
    ///
    /// <para><c>R(f)</c> and <c>L_int(f)</c> come from the exact round-wire Bessel solution, scaled by
    /// the wire's developed path length.</para>
    /// </summary>
    public Complex WireInternalImpedance(int wire, double frequencyHz)
    {
        var (rPerMetre, lIntPerMetre) = InternalImpedance.PerMetre(frequencyHz, _radius[wire], _sigma[wire]);
        double omega = 2.0 * Math.PI * frequencyHz;
        return new Complex(rPerMetre * _pathLength[wire], omega * lIntPerMetre * _pathLength[wire]);
    }

    /// <summary>
    /// Assembles the full wire-basis <c>Z(ω) = jω·L + D(ω)</c>, row-major.
    /// </summary>
    public Complex[] AssembleZ(double frequencyHz)
    {
        int n = WireCount;
        double omega = 2.0 * Math.PI * frequencyHz;

        var z = new Complex[n * n];
        var values = _l.Values;

        for (int i = 0; i < n; i++)
        {
            int row = i * n;
            for (int j = 0; j < n; j++)
                z[row + j] = new Complex(0.0, omega * values[row + j]);

            z[row + i] += WireInternalImpedance(i, frequencyHz);
        }

        return z;
    }

    /// <summary>
    /// <c>Z_arr(ω) = (Aᵀ Z(ω)⁻¹ A)⁻¹</c>, an M × M row-major matrix in ohms.
    /// </summary>
    public Complex[] ArrayImpedance(double frequencyHz)
    {
        int n = WireCount;
        int m = ArrayCount;
        var map = _mesh.ArrayOfWire;

        var lu = ComplexLu.Factor(AssembleZ(frequencyHz), n);

        // X = Z^-1 A, one solve per array. A's columns are 0/1 indicators, so the right-hand sides
        // are built by marking membership rather than by a matrix product.
        var x = new Complex[n * m];
        var rhs = new Complex[n];
        for (int a = 0; a < m; a++)
        {
            Array.Clear(rhs);
            for (int i = 0; i < n; i++)
                if (map[i] == a) rhs[i] = Complex.One;

            var column = lu.Solve(rhs);
            for (int i = 0; i < n; i++)
                x[i * m + a] = column[i];
        }

        // Y_arr = A^T X — a scatter-add over wires, not a GEMM, because A is 0/1.
        var yArr = new Complex[m * m];
        for (int i = 0; i < n; i++)
        {
            int row = map[i] * m;
            int xr = i * m;
            for (int a = 0; a < m; a++)
                yArr[row + a] += x[xr + a];
        }

        // Symmetrise: Y_arr is symmetric in exact arithmetic, and forcing it makes reciprocity
        // structural in the returned matrix rather than true only to rounding.
        for (int i = 0; i < m; i++)
        {
            for (int j = i + 1; j < m; j++)
            {
                Complex mean = 0.5 * (yArr[i * m + j] + yArr[j * m + i]);
                yArr[i * m + j] = mean;
                yArr[j * m + i] = mean;
            }
        }

        return InvertSmall(yArr, m);
    }

    /// <summary>
    /// The DC-limit cross-check that ties this path to the editor's fast one (WB19b / tier 0):
    /// <c>Z_arr(ω)/jω → L_arr</c> as R → 0.
    /// </summary>
    public ArrayReduction InductanceOnlyReduction() =>
        ArrayReduction.Reduce(_l, null, _mesh.ArrayOfWire, _mesh.ArrayCount, _mesh.ArrayNames);

    private static Complex[] InvertSmall(Complex[] a, int m)
    {
        var lu = ComplexLu.Factor(a, m);
        var inverse = new Complex[m * m];
        var rhs = new Complex[m];

        for (int j = 0; j < m; j++)
        {
            Array.Clear(rhs);
            rhs[j] = Complex.One;
            var column = lu.Solve(rhs);
            for (int i = 0; i < m; i++)
                inverse[i * m + j] = column[i];
        }

        for (int i = 0; i < m; i++)
        {
            for (int j = i + 1; j < m; j++)
            {
                Complex mean = 0.5 * (inverse[i * m + j] + inverse[j * m + i]);
                inverse[i * m + j] = mean;
                inverse[j * m + i] = mean;
            }
        }

        return inverse;
    }
}
