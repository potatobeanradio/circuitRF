// brief-paa-2 — the array engine's arithmetic gates (2, 4, 6, 8): the array factor against the Chain Budget Steering bench's closed form
// and against direct sums, rectangular, triangular and in (u, v); the phase tables built once; the coupling extension against the
// 5×5 fixture's own S; and the extension's sparsity at 64 × 64. The physics gates on records are in Ui.Tests' PhasedArrayRecordTests.

using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Engine.AntennaArray;
using RfCore;
using Xunit;

namespace CircuitRF.Engine.Tests.AntennaArray;

public sealed class ArrayEngineTests
{
    private const double F = 5.9e9;
    private static readonly double Lambda = CircuitRF.Engine.Mom.EmConstants.C0 / F, K = 2 * Math.PI / Lambda;

    private static string Testdata(string name, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata", "phased-array", name));

    /// <summary>Σ_n w_n·exp(j·k·û·r_n), one exponential per element and direction: what the tables must equal.</summary>
    private static Complex[] Direct(ArrayLattice lattice, IReadOnlyList<Complex> w, ArrayDirections dirs)
    {
        var af = new Complex[dirs.Count];
        for (int d = 0; d < dirs.Count; d++)
            for (int n = 0; n < lattice.Count; n++)
            {
                if (!lattice.IsOn(n)) continue;
                var (x, y) = lattice.Position(n);
                af[d] += w[n] * Complex.FromPolarCoordinates(1, K * (x * dirs.U[d] + y * dirs.V[d]));
            }
        return af;
    }

    private static ArrayDirections Scattered(int count, int seed)
    {
        var rng = new Random(seed);
        var t = new double[count];
        var p = new double[count];
        for (int i = 0; i < count; i++) { t[i] = Math.Acos(rng.NextDouble()); p[i] = 2 * Math.PI * rng.NextDouble(); }
        return ArrayDirections.Of(t, p);
    }

    private static Complex[] Steer(ArrayLattice lattice, double theta0Deg, double phi0Deg, Func<int, int, double>? taper = null)
    {
        var (u0, v0, _) = ArrayDirections.Vector(theta0Deg * Math.PI / 180, phi0Deg * Math.PI / 180);
        return [.. Enumerable.Range(0, lattice.Count).Select(n =>
        {
            var (x, y) = lattice.Position(n);
            return (taper?.Invoke(n / lattice.Cols, n % lattice.Cols) ?? 1) * Complex.FromPolarCoordinates(1, -K * (x * u0 + y * v0));
        })];
    }

    private static void Within(Complex[] want, Complex[] got, double scale, double tol, string what)
    {
        double worst = 0;
        for (int i = 0; i < want.Length; i++) worst = Math.Max(worst, (want[i] - got[i]).Magnitude / scale);
        Assert.True(worst <= tol, $"{what}: worst difference {worst:E2} of the scale, over {tol:E0}.");
    }

    /// <summary>Gate 2 — isotropic elements on a 1×N line: |AF|/Σa_k is the Steering bench's |Σ a_k·e^(−j(k−1)(ψ_in − ψ_s))| / Σ a_k.</summary>
    [Fact]
    public void ALine_IsTheSteeringBenchsClosedForm()
    {
        const int N = 8;
        double d = 0.5 * Lambda;
        var lattice = new ArrayLattice(1, N, d, d);
        double[] taper = [.. Enumerable.Range(0, N).Select(k => 0.5 + 0.5 * Math.Cos(Math.PI * (k - (N - 1) / 2.0) / N))];
        const double ThetaSteer = 25;
        var w = Steer(lattice, ThetaSteer, 0, (_, c) => taper[c]);
        double[] thetaIn = [.. Enumerable.Range(0, 181).Select(i => -90.0 + i)];
        var dirs = ArrayDirections.Of([.. thetaIn.Select(t => Math.Abs(t) * Math.PI / 180)], [.. thetaIn.Select(t => t < 0 ? Math.PI : 0)]);
        var af = ArrayPattern.Factor(lattice, w, dirs, F);
        double sum = taper.Sum();
        for (int i = 0; i < thetaIn.Length; i++)
        {
            double psd = 2 * Math.PI * (d / Lambda) * (Math.Sin(thetaIn[i] * Math.PI / 180) - Math.Sin(ThetaSteer * Math.PI / 180));
            Complex closed = 0;
            for (int k = 1; k <= N; k++) closed += taper[k - 1] * Complex.FromPolarCoordinates(1, -(k - 1) * psd);
            Assert.True(Math.Abs(af[i].Magnitude / sum - closed.Magnitude / sum) <= 1e-12, $"θ_in {thetaIn[i]}°: {af[i].Magnitude / sum} against {closed.Magnitude / sum}");
        }
    }

    /// <summary>Gate 2 — on 32×32 and 64×64 rectangular lattices the row/column path, general and separable, is the direct sum.</summary>
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void TheRowColumnPath_IsTheDirectSum(int side)
    {
        var lattice = new ArrayLattice(side, side, 0.5 * Lambda, 0.48 * Lambda);
        var dirs = Scattered(300, side);
        var rng = new Random(7);
        Complex[] general = [.. Enumerable.Range(0, lattice.Count).Select(_ => new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5))];
        Within(Direct(lattice, general, dirs), ArrayPattern.Factor(lattice, general, dirs, F), general.Sum(c => c.Magnitude), 1e-12, "general weights");

        long separable = ArrayPattern.SeparableSums;
        var steered = Steer(lattice, 40, 20, (r, c) => (1 + Math.Cos(Math.PI * (r - side / 2.0) / side)) * (1 + Math.Cos(Math.PI * (c - side / 2.0) / side)));
        Within(Direct(lattice, steered, dirs), ArrayPattern.Factor(lattice, steered, dirs, F), steered.Sum(c => c.Magnitude), 1e-12, "steered and tapered");
        Assert.Equal(separable + 1, ArrayPattern.SeparableSums);

        // An element switched off makes the weighting general again, and the sum still the direct one.
        var off = lattice.With(off: [5, side * side / 2]);
        Within(Direct(off, steered, dirs), ArrayPattern.Factor(off, steered, dirs, F), steered.Sum(c => c.Magnitude), 1e-12, "with elements off");
    }

    /// <summary>Gate 2 — a triangular lattice is its direct sum.</summary>
    [Fact]
    public void ATriangularLattice_IsItsDirectSum()
    {
        var lattice = new ArrayLattice(9, 12, 0.6 * Lambda, 0.52 * Lambda, ArrayLatticeKind.Tri);
        Assert.Equal(lattice.Position(lattice.Cols).X - lattice.Position(0).X, 0.3 * Lambda, 12);
        var dirs = Scattered(400, 3);
        var w = Steer(lattice, 30, 60);
        Within(Direct(lattice, w, dirs), ArrayPattern.Factor(lattice, w, dirs, F), lattice.Count, 1e-12, "triangular");
    }

    /// <summary>Gate 2 — the (u, v) FFT is the direct sum at the same points, rectangular (an element off) and triangular.</summary>
    [Theory]
    [InlineData(16, 16, false)]
    [InlineData(7, 9, true)]
    [InlineData(1, 12, false)]
    public void TheUvFft_IsTheDirectSum_AtItsPoints(int rows, int cols, bool tri)
    {
        var lattice = new ArrayLattice(rows, cols, 0.55 * Lambda, 0.5 * Lambda, tri ? ArrayLatticeKind.Tri : ArrayLatticeKind.Rect, off: [2]);
        var w = Steer(lattice, 35, 120, (r, c) => 1 + 0.3 * r - 0.1 * c);
        var map = ArrayUv.Map(lattice, w, F);
        Assert.True(map.SizeU >= ArrayUv.Padding * cols || cols == 1);
        Assert.True(map.U.Length > 100);
        var dirs = ArrayDirections.Of([.. map.U.Select((u, i) => Math.Asin(Math.Min(1, Math.Sqrt(u * u + map.V[i] * map.V[i]))))],
                                      [.. map.U.Select((u, i) => Math.Atan2(map.V[i], u))]);
        Within(Direct(lattice, w, dirs), map.Factor, w.Sum(c => c.Magnitude), 1e-12, "the (u,v) map");
    }

    /// <summary>Gate 6 — a second sum with new weights over the same directions builds no phase table.</summary>
    [Fact]
    public void ASecondSumWithNewWeights_BuildsNoTable()
    {
        var lattice = new ArrayLattice(20, 20, 0.5 * Lambda, 0.5 * Lambda);
        var dirs = Scattered(200, 11);
        ArrayPattern.Factor(lattice, Steer(lattice, 0, 0), dirs, F);
        long built = ArrayPattern.TablesBuilt;
        ArrayPattern.Factor(lattice, Steer(lattice, 50, 10), dirs, F);
        ArrayPattern.Factor(lattice.With(off: [3]), Steer(lattice, 20, 90), dirs, F);
        Assert.Equal(built, ArrayPattern.TablesBuilt);
    }

    private static (Complex[,] S, ArrayLattice Sub) E5()
    {
        var snp = TouchstoneIO.ReadFile(Testdata(Path.Combine("results", "e5.s25p")), readComments: false);
        double pitch = 25.86e-3;
        var m = snp.Matrices[0];
        var s = new Complex[25, 25];
        for (int i = 0; i < 25; i++)
            for (int j = 0; j < 25; j++) s[i, j] = m[i, j];
        return (s, new ArrayLattice(5, 5, pitch, pitch));
    }

    /// <summary>Gate 4 — extending E5 to 5×5 gives E5's own S at every offset, from the pair nearest its centre; a 9×9 is zero beyond ±4
    /// and says so.</summary>
    [Fact]
    public void ExtendingE5_ReturnsItsOwnS_AndZeroBeyondItsOffsets()
    {
        var (s, sub) = E5();
        var same = ArrayCoupling.Extend(s, sub, sub);
        for (int a = 0; a < 25; a++)
            for (int b = 0; b < 25; b++)
            {
                int dr = b / 5 - a / 5, dc = b % 5 - a % 5;
                // The pair with this offset whose midpoint is nearest the centre, the first in row-major order on a tie.
                int ri = (int)Math.Floor(2 - dr / 2.0), ci = (int)Math.Floor(2 - dc / 2.0);
                Complex want = a == b ? s[12, 12] : s[ri * 5 + ci, (ri + dr) * 5 + ci + dc];
                Assert.Equal(want, same.S[a, b]);
            }

        var big = ArrayCoupling.Extend(s, sub, new ArrayLattice(9, 9, sub.Dx, sub.Dy));
        Assert.Equal((4, 4), (big.RowOffsets, big.ColumnOffsets));
        Assert.Contains("beyond that is taken as zero", big.Note);
        for (int a = 0; a < 81; a++)
            for (int b = 0; b < 81; b++)
            {
                bool held = Math.Abs(b / 9 - a / 9) <= 4 && Math.Abs(b % 9 - a % 9) <= 4;
                Assert.Equal(held, big.S[a, b] != Complex.Zero);
            }

        var off = ArrayCoupling.Extend(s, sub, new ArrayLattice(9, 9, sub.Dx, sub.Dy), coupled: false);
        Assert.Equal(81, off.S.NonZeros);
        Assert.Equal(s[12, 12], off.S[40, 40]);
    }

    /// <summary>Gate 8 — E5 extended to 64 × 64 holds at most 81 non-zeros per row, and the active reflection allocates no N×N array.</summary>
    [Fact]
    public void ExtendedTo64x64_IsSparse_AndActiveAllocatesNoSquareArray()
    {
        var (s, sub) = E5();
        var lattice = new ArrayLattice(64, 64, sub.Dx, sub.Dy);
        var ext = ArrayCoupling.Extend(s, sub, lattice, classes: true);
        Assert.True(ext.S.MaxNonZerosPerRow <= 81, $"{ext.S.MaxNonZerosPerRow} non-zeros in a row");
        Assert.InRange(ext.S.NonZeros, 300_000, 81 * 4096);

        var a = Steer(lattice, 30, 0);
        ArrayWaves.Active(ext.S, a);                                   // warm
        long before = GC.GetAllocatedBytesForCurrentThread();
        var active = ArrayWaves.Active(ext.S, a);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long square = 16L * lattice.Count * lattice.Count;             // 268 MB
        Assert.True(allocated < square / 100, $"Active allocated {allocated:N0} bytes; an N×N array is {square:N0}.");
        Assert.Equal(lattice.Count, active.Gamma.Length);
        Assert.True(active.AcceptedW > 0 && active.AcceptedW < active.IncidentW);
    }

    /// <summary>R-paa2-6 — an element switched off (a = 0) reports no active reflection.</summary>
    [Fact]
    public void AnElementSwitchedOff_HasNoActiveReflection()
    {
        var (s, sub) = E5();
        var a = Enumerable.Repeat(Complex.One, 25).ToArray();
        a[7] = 0;
        var active = ArrayWaves.Active(ArraySparseMatrix.FromDense(s), a);
        Assert.Null(active.Gamma[7]);
        Assert.Equal(24, active.Gamma.Count(g => g.HasValue));
    }

    /// <summary>R-paa2-7 — the grating-lobe margin from the pitch and frequency alone: a half-wave lattice at boresight has the lobe at
    /// |u| = 2, at 30° it is 0.5 from the circle, and a one-wavelength pitch scanned off boresight puts one in real space.</summary>
    [Fact]
    public void TheGratingLobeMargin_IsTheReciprocalLattice()
    {
        var half = new ArrayLattice(8, 8, 0.5 * Lambda, 0.5 * Lambda);
        Assert.Equal(1.0, ArrayBeam.GratingLobe(half, F, 0, 0).Margin, 12);
        Assert.Equal(0.5, ArrayBeam.GratingLobe(half, F, 30, 0).Margin, 12);
        Assert.True(ArrayBeam.GratingLobe(new ArrayLattice(8, 8, Lambda, Lambda), F, 20, 0).Margin < 0);
        // A triangular lattice's nearest lobe is along its diagonal reciprocal vector.
        var tri = ArrayBeam.GratingLobe(new ArrayLattice(8, 8, Lambda / Math.Sqrt(3) * 2 * 0.5, 0.5 * Lambda, ArrayLatticeKind.Tri), F, 0, 0);
        Assert.True(tri.Margin > 0);
    }
}
