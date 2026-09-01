using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The ideal-S-block stamp end to end (brief-sys-2): a one-block netlist terminated in ideal ports,
/// swept, returns EXACTLY the S its parameters state.
///
/// <para>Every expected number is computed here from the dB values on the netlist line — never read
/// back out of the model — so what is gated is the whole trip from "10 dB of loss" through the
/// constraint rows, the solve and the wave extraction. The tolerance is 1e-12, because a wave
/// constraint row solved exactly should return the matrix it was built from to machine precision;
/// anything looser would hide a dropped √Z0.</para>
/// </summary>
public class IdealSBlockSParamTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);

    private static Complex[,] SAt(string cnl, double freqHz = 1e9)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [freqHz]);
        var c  = ds["S"];
        int n  = c.Axes[1].Length;
        var s  = new Complex[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            s[i, j] = (Complex)c[0, i, j];
        return s;
    }

    private static double Amp(double db) => Math.Pow(10.0, -db / 20.0);

    private static void Near(Complex expected, Complex actual, double tol = 1e-12)
        => Assert.True((expected - actual).Magnitude < tol,
                       $"expected {expected}, got {actual} (|Δ| = {(expected - actual).Magnitude:G6})");

    // ── S in, S out: the attenuator ───────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(10.0)]
    [InlineData(30.0)]
    public void Attenuator_MeasuresTheLossItWasGiven(double lossDb)
    {
        // 0 dB is in the list on purpose: it is the ideal through, S = [[0,1],[1,0]], the matrix
        // with no Z form at all. A Z- or Y-derived stamp cannot produce this row of the table.
        var s = SAt($@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  b 0  Num=2  Z=50 Ohm
Atten:A1 a 0 b 0  Loss={N(lossDb)}  Z0=50  RL=200
");
        Near(new Complex(Amp(lossDb), 0), s[1, 0]);
        Near(new Complex(Amp(lossDb), 0), s[0, 1]);
        Near(Complex.Zero, s[0, 0]);
        Near(Complex.Zero, s[1, 1]);
    }

    [Fact]
    public void Attenuator_AStatedReturnLossComesBackAsAStatedReturnLoss()
    {
        var s = SAt(@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  b 0  Num=2  Z=50 Ohm
Atten:A1 a 0 b 0  Loss=10  Z0=50  RL=15
");
        Near(new Complex(Amp(15.0), 0), s[0, 0]);
        Near(new Complex(Amp(15.0), 0), s[1, 1]);
        Near(new Complex(Amp(10.0), 0), s[1, 0]);
    }

    // ── S in, S out: the switch, every state, both off-state behaviours ───────

    public static TheoryData<int, int, double, double, double, string> SwitchCases()
    {
        var d = new TheoryData<int, int, double, double, double, string>();
        foreach (string off in new[] { "Reflective", "Absorptive" })
        {
            d.Add(1, 1, 0.0, 200.0, 200.0, off);      // SPST closed, ideal
            d.Add(1, 0, 0.0, 200.0, 200.0, off);      // SPST open, ideal
            d.Add(1, 1, 0.6,  25.0,  18.0, off);      // SPST closed, real numbers
            d.Add(1, 0, 0.6,  25.0,  18.0, off);      // SPST open, real numbers
            d.Add(2, 1, 0.0, 200.0, 200.0, off);      // SPDT throw 1, ideal
            d.Add(2, 2, 0.0, 200.0, 200.0, off);      // SPDT throw 2, ideal
            d.Add(2, 0, 0.0, 200.0, 200.0, off);      // SPDT both open, ideal
            d.Add(2, 1, 0.4,  30.0,  20.0, off);      // SPDT throw 1, real numbers
            d.Add(2, 2, 0.4,  30.0,  20.0, off);
            d.Add(2, 0, 0.4,  30.0,  20.0, off);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(SwitchCases))]
    public void Switch_MeasuresTheMatrixItsParametersDescribe(
        int throws, int state, double il, double iso, double rl, string offState)
    {
        string ports = string.Join("\n",
            Enumerable.Range(1, 1 + throws).Select(k => $"Port:P{k}  n{k} 0  Num={k}  Z=50 Ohm"));
        string nets  = string.Join(" ", Enumerable.Range(1, 1 + throws).Select(k => $"n{k} 0"));

        var s = SAt($@"
{ports}
Switch:SW1  {nets}  Throws={throws} State={state} IL={N(il)} Isolation={N(iso)} RL={N(rl)} OffState={offState}
");

        // The same arithmetic the model's doc comment states, done here from the dB values.
        int    n    = 1 + throws;
        double thru = Amp(il);
        double leak = iso >= 150 ? 0.0 : Amp(iso);
        double refl = rl  >= 150 ? 0.0 : Amp(rl);
        double open = offState == "Reflective" ? 1.0 : 0.0;
        bool   made = state >= 1 && state <= throws;

        var t = new double[n];
        for (int p = 1; p < n; p++) t[p] = p == state ? thru : leak;

        var expected = new Complex[n, n];
        expected[0, 0] = new Complex(made ? refl : open, 0);
        for (int p = 1; p < n; p++)
        {
            expected[p, p] = new Complex(p == state ? refl : open, 0);
            expected[0, p] = expected[p, 0] = new Complex(t[p], 0);
            for (int q = p + 1; q < n; q++)
                expected[p, q] = expected[q, p] = new Complex(t[p] * t[q], 0);
        }

        for (int p = 0; p < n; p++)
        for (int q = 0; q < n; q++)
            Near(expected[p, q], s[p, q]);
    }

    // ── The gate that catches a dropped √Z0 ───────────────────────────────────

    /// <summary>
    /// The same attenuator measured against 50 Ω ports and against 75 Ω ports must give S matrices
    /// that renormalise into each other. Nothing else here catches a <c>√Z0</c> dropped from the
    /// constraint row: with every reference impedance equal, the factor cancels out of the answer
    /// and a wrong stamp still measures right.
    ///
    /// <para>The renormalisation is the ordinary uniform-reference one, and it is derived rather
    /// than borrowed: for real, EQUAL reference impedances the diagonal scaling matrix commutes
    /// through and cancels, leaving <c>S' = (S − Γ)(I − Γ·S)⁻¹</c> with
    /// <c>Γ = (Z0' − Z0)/(Z0' + Z0)</c>. Checked on a one-port before it is used: a load equal to
    /// the new reference must renormalise to zero.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(20.0)]
    public void ReferenceImpedanceIndependence_A75OhmMeasurementRenormalisesOntoThe50OhmOne(double lossDb)
    {
        string Net(double portZ) => $@"
Port:P1  a 0  Num=1  Z={N(portZ)} Ohm
Port:P2  b 0  Num=2  Z={N(portZ)} Ohm
Atten:A1 a 0 b 0  Loss={N(lossDb)}  Z0=50  RL=200
";
        var s50 = SAt(Net(50.0));
        var s75 = SAt(Net(75.0));

        // A matched 50 Ω PAD is not matched when it is looked at through 75 Ω ports, so this is a
        // real measurement rather than the same numbers twice. The 0 dB case is the exception and
        // is in the list anyway: an ideal through is a WIRE, and a wire is matched in every
        // reference system, so it carries no information for this particular gate.
        if (lossDb > 0)
            Assert.True(s75[0, 0].Magnitude > 0.1, "the 75 Ω measurement should show a mismatch");
        else
            Assert.True(s75[0, 0].Magnitude < 1e-12, "an ideal through is a wire in any reference");

        var back = Renormalise(s75, from: 75.0, to: 50.0);
        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            Near(s50[p, q], back[p, q], 1e-11);

        output.WriteLine($"loss {lossDb} dB: S11(75Ω) = {s75[0, 0]:G6}, renormalised = {back[0, 0]:G6}");
    }

    [Fact]
    public void TheRenormalisationUsedAbove_IsItselfCorrect()
    {
        // A 75 Ω load measured in a 50 Ω system reads (75−50)/(75+50) = 0.2. Renormalised to 75 Ω it
        // must read exactly 0, and to 25 Ω it must read (75−25)/(75+25) = 0.5. If this is wrong,
        // the gate above proves nothing.
        var s = new Complex[1, 1];
        s[0, 0] = new Complex(0.2, 0);
        Assert.True(Renormalise(s, from: 50.0, to: 75.0)[0, 0].Magnitude < 1e-15);
        Near(new Complex(0.5, 0), Renormalise(s, from: 50.0, to: 25.0)[0, 0], 1e-12);
    }

    /// <summary>S renormalised from one uniform real reference impedance to another.</summary>
    private static Complex[,] Renormalise(Complex[,] s, double from, double to)
    {
        int n = s.GetLength(0);
        double g = (to - from) / (to + from);

        // (I − Γ·S), then its inverse by Gauss–Jordan; n is 1 or 2 here.
        var a = new Complex[n, n];
        for (int p = 0; p < n; p++)
        for (int q = 0; q < n; q++)
            a[p, q] = (p == q ? Complex.One : Complex.Zero) - g * s[p, q];
        var inv = Invert(a);

        var num = new Complex[n, n];
        for (int p = 0; p < n; p++)
        for (int q = 0; q < n; q++)
            num[p, q] = s[p, q] - (p == q ? new Complex(g, 0) : Complex.Zero);

        var outM = new Complex[n, n];
        for (int p = 0; p < n; p++)
        for (int q = 0; q < n; q++)
        {
            Complex acc = Complex.Zero;
            for (int k = 0; k < n; k++) acc += num[p, k] * inv[k, q];
            outM[p, q] = acc;
        }
        return outM;
    }

    private static Complex[,] Invert(Complex[,] m)
    {
        int n = m.GetLength(0);
        var a = (Complex[,])m.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = Complex.One;

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
                if (a[r, col].Magnitude > a[pivot, col].Magnitude) pivot = r;
            if (pivot != col)
                for (int c = 0; c < n; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                    (inv[col, c], inv[pivot, c]) = (inv[pivot, c], inv[col, c]);
                }

            Complex d = a[col, col];
            for (int c = 0; c < n; c++) { a[col, c] /= d; inv[col, c] /= d; }

            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                Complex f = a[r, col];
                if (f == Complex.Zero) continue;
                for (int c = 0; c < n; c++) { a[r, c] -= f * a[col, c]; inv[r, c] -= f * inv[col, c]; }
            }
        }
        return inv;
    }

    // ── Unequal port impedances: the ideal transformer ────────────────────────

    /// <summary>
    /// A block with <c>Z0₁ = 50</c>, <c>Z0₂ = 75</c> and <c>S = [[0,1],[1,0]]</c> is a LOSSLESS
    /// IDEAL TRANSFORMER, and it is worth writing down why: the two constraint rows added give
    /// <c>√Z0₁·i₁ = −√Z0₂·i₂</c> and subtracted give <c>v₂/√Z0₂ = v₁/√Z0₁</c>, i.e.
    /// <c>v₂ = n·v₁</c> and <c>i₁ = −n·i₂</c> with <c>n = √(Z0₂/Z0₁)</c>.
    ///
    /// <para>Measured in a uniform 50 Ω system that transformer has the closed form below —
    /// <c>Zin = Z0/n²</c> at port 1 — which the test computes itself.</para>
    ///
    /// <para>No SYS-2 component exposes per-port reference impedances — the attenuator's two ports
    /// share one <c>Z0</c> and the switch's all do — so the block is built here as a two-line
    /// subclass and dropped into an elaborated netlist directly. That IS the gate: the per-port
    /// <c>√Z0</c> lives in the shared base, and this is what proves the base carries it rather than
    /// its two users happening never to need it.</para>
    /// </summary>
    [Theory]
    [InlineData(50.0, 75.0)]
    [InlineData(50.0, 12.5)]
    [InlineData(25.0, 100.0)]
    public void UnequalPortImpedances_AreALosslessIdealTransformer(double z1, double z2)
    {
        var (lib, tb) = new CnlReader().Read(@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  b 0  Num=2  Z=50 Ohm
");
        var nl = new Elaborator(lib).Elaborate(tb);
        nl.AddComponent(new ElaboratedComponent(
            "IdealThrough", "T1",
            [nl.Nodes.GetOrAssign("a"), 0, nl.Nodes.GetOrAssign("b"), 0],
            new Dictionary<string, Value>(),
            new IdealThroughBlock(z1, z2)));

        var ds = SParameterEngine.Run(nl, [1e9]);
        var c  = ds["S"];
        var s  = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            s[i, j] = (Complex)c[0, i, j];

        double n  = Math.Sqrt(z2 / z1);
        double n2 = n * n;
        Near(new Complex((1.0 - n2) / (1.0 + n2), 0), s[0, 0]);
        Near(new Complex((n2 - 1.0) / (1.0 + n2), 0), s[1, 1]);
        Near(new Complex(2.0 * n / (1.0 + n2), 0),    s[1, 0]);
        Near(new Complex(2.0 * n / (1.0 + n2), 0),    s[0, 1]);

        // Lossless, which is the other half of "ideal transformer".
        double sumSq = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
        Assert.Equal(1.0, sumSq, 12);
    }

    /// <summary>An ideal through between two DIFFERENT reference impedances — nothing but S and Z0.</summary>
    private sealed class IdealThroughBlock(double z1, double z2) : IdealSBlockModel([z1, z2])
    {
        protected override void FillS(double omega, Complex[,] s)
            => s[0, 1] = s[1, 0] = Complex.One;
    }

    // ── Cascade: the branch bookkeeping is not order-dependent ────────────────

    [Fact]
    public void TwoTenDbPadsMeasureTwenty()
    {
        var s = SAt(@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  c 0  Num=2  Z=50 Ohm
Atten:A1 a 0 b 0  Loss=10  Z0=50  RL=200
Atten:A2 b 0 c 0  Loss=10  Z0=50  RL=200
");
        Near(new Complex(Amp(20.0), 0), s[1, 0]);
        Near(Complex.Zero, s[0, 0]);
    }

    [Fact]
    public void APadAndALineGiveTheSameSInEitherOrder()
    {
        // Both blocks are matched to 50 Ω, so the cascade commutes physically. What it exercises is
        // the bookkeeping: two Group-2 branch pairs allocated in the opposite order must land in
        // the same solve.
        var padFirst = SAt(@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  c 0  Num=2  Z=50 Ohm
Atten:A1 a 0 b 0  Loss=6  Z0=50  RL=200
TLIN:T1  b c  Z=50 E=37 deg F=1e9
");
        var lineFirst = SAt(@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  c 0  Num=2  Z=50 Ohm
TLIN:T1  a b  Z=50 E=37 deg F=1e9
Atten:A1 b 0 c 0  Loss=6  Z0=50  RL=200
");
        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            Near(padFirst[p, q], lineFirst[p, q], 1e-12);

        // and the answer is the pad's loss with the line's phase on it, not something else.
        Near(Complex.FromPolarCoordinates(Amp(6.0), -37.0 * Math.PI / 180.0), padFirst[1, 0], 1e-12);
    }

    // ── DC: the degenerate cases, on purpose ──────────────────────────────────

    [Fact]
    public void AnIdealThroughAndAnIdealOpenBothSolveAtDc()
    {
        // ω = 0 is where a Z-derived or Y-derived stamp of these two would have nothing to write:
        // the ideal through has no Z matrix and the ideal open no Y. Both are ordinary here.
        var through = SAt(@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  b 0  Num=2  Z=50 Ohm
Atten:A1 a 0 b 0  Loss=0  Z0=50  RL=200
", freqHz: 0.0);
        Near(Complex.One,  through[1, 0]);
        Near(Complex.Zero, through[0, 0]);

        var open = SAt(@"
Port:P1  a 0  Num=1  Z=50 Ohm
Port:P2  b 0  Num=2  Z=50 Ohm
Switch:SW1 a 0 b 0  Throws=1 State=0 IL=0 Isolation=200 RL=200 OffState=Reflective
", freqHz: 0.0);
        Near(Complex.One,  open[0, 0]);
        Near(Complex.One,  open[1, 1]);
        Near(Complex.Zero, open[1, 0]);
    }

    [Fact]
    public void ADcSolveSeesTheClosedSwitchAsAWireAndTheOpenOneAsABreak()
    {
        // The same two degenerate matrices through the NONLINEAR DC engine rather than the wave
        // path — a 10 V source behind 50 Ω into a 50 Ω load, with the switch between them.
        double Vout(int state) => NodeVoltage($@"
Vdc:VS     s 0   Vdc=10
R:Rs       s a   R=50
Switch:SW1 a 0 b 0  Throws=1 State={state} IL=0 Isolation=200 RL=200 OffState=Reflective
R:RL       b 0   R=50
", "b");

        Assert.Equal(5.0, Vout(1), 9);   // closed: an ideal wire, so an ordinary 50/50 divider
        Assert.Equal(0.0, Vout(0), 9);   // open:   no current anywhere, so no drop across RL
    }

    private static double NodeVoltage(string cnl, string net)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var r  = NonlinearDcEngine.Run(nl);
        Assert.True(r.Converged, $"DC did not converge (residual {r.FinalResidual:G3})");
        int i = nl.Nodes.GetOrAssign(net);
        return i == 0 ? 0.0 : r.NodeVoltages[i - 1];
    }
}
