using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Gate tests for P1ToneModel (brief-sweep-5-p1tone-source.md).
///
/// Covers:
///   T1 — Construction + GetDeclaredZ defaults
///   T2 — Band-assignment rule (n = roundHalfUp(|f|/f_c))
///   T3 — Pavl → |Vs| formula (matched-load power transfer)
///   T4 — S-param mode: single Z_Port stamp, no drive branch
///   T5 — HB mode: drive branch at fundamental, zero at harmonics
///   T6 — Γ→Z conversion via G[k] parameter
///   T7 — Commensurability: valid passes, off-grid throws
/// </summary>
public class P1ToneTests(ITestOutputHelper output)
{
    private const double TwoPI = 2.0 * Math.PI;

    // ── T1: Default Z lookup ──────────────────────────────────────────────────

    [Fact]
    public void T1_DefaultZ_FallsBackToZdefault()
    {
        var hz = new Dictionary<int, Complex> { [1] = new Complex(50, 0) };
        var m  = new P1ToneModel("P", hz, new Complex(50, 0), 0.0, 1e9, 0.0);

        // Declared Z[1] = 50 Ω.
        Assert.InRange(m.GetDeclaredZ(1).Real,      49.999, 50.001);
        Assert.InRange(m.GetDeclaredZ(1).Imaginary, -1e-9,  1e-9);

        // Undeclared Z[2] falls back to Zdefault = 50 Ω.
        Assert.InRange(m.GetDeclaredZ(2).Real,      49.999, 50.001);

        output.WriteLine($"Z[1]={m.GetDeclaredZ(1)}  Z[2]={m.GetDeclaredZ(2)} (fallback)");
    }

    // ── T2: Band-assignment rule ──────────────────────────────────────────────

    [Fact]
    public void T2_BandAssignment_RoundHalfUp()
    {
        // f_c = 1 GHz.  Bands: n=0 DC, n=1 [0.5,1.5)GHz, n=2 [1.5,2.5)GHz.
        // Declare distinct Z per harmonic so we can detect which band was selected.
        var hz = new Dictionary<int, Complex>
        {
            [0] = new Complex(0, 0),     // DC band
            [1] = new Complex(10, 0),    // band 1: [0.5, 1.5) GHz
            [2] = new Complex(20, 0),    // band 2: [1.5, 2.5) GHz
            [3] = new Complex(30, 0),    // band 3: [2.5, 3.5) GHz
        };
        var m = new P1ToneModel("P", hz, new Complex(99, 0), 0.0, 1e9, 0.0);
        m.SetToneContext(1e9, 1e9);  // fc = 1 GHz

        // DC (ω=0) → band 0 → Z=0
        var ctx0 = new CaptureMnaContext();
        var ec   = MakeEc(m, new[] { 1, 0, 2 });
        m.Stamp(ctx0, ec, 0.0);
        // In S-param mode the drive branch isn't stamped, but after SetToneContext it IS HB mode.
        // DC stamp: drive V=0, Z_Port uses GetZ(0) = _zDefault (isDC guard).
        // With fc>0 and isDC: returns _zDefault=99.  Band [0] declared but isDC path returns _zDefault.
        // (Only the explicit isDC guard path runs; band-rule below it is skipped.)

        // Test band-rule at specific frequencies:
        // 0.99 GHz → ratio=0.99 → floor(0.99+0.5)=floor(1.49)=1 → band 1
        double omega099 = TwoPI * 0.99e9;
        var    ctx099   = new CaptureMnaContext();
        m.Stamp(ctx099, ec, omega099);
        // Z_Port branch constraint contains -Z; the BranchConstraints list captures the Z value.
        var zAt099 = -ctx099.BranchConstraints.LastOrDefault(kv => kv.Item3.Magnitude > 1).Item3;
        Assert.InRange(zAt099.Real, 9.5, 10.5);  // band 1 → Z=10
        output.WriteLine($"0.99 GHz → band 1 → Z={zAt099}  ✓");

        // 1.5 GHz → ratio=1.5 → floor(1.5+0.5)=floor(2.0)=2 → band 2
        double omega150 = TwoPI * 1.5e9;
        var    ctx150   = new CaptureMnaContext();
        m.Stamp(ctx150, ec, omega150);
        var zAt150 = -ctx150.BranchConstraints.LastOrDefault(kv => kv.Item3.Magnitude > 1).Item3;
        Assert.InRange(zAt150.Real, 19.5, 20.5);  // band 2 → Z=20
        output.WriteLine($"1.5 GHz → band 2 → Z={zAt150}  ✓");

        // 2.9 GHz → ratio=2.9 → floor(2.9+0.5)=floor(3.4)=3 → band 3
        double omega290 = TwoPI * 2.9e9;
        var    ctx290   = new CaptureMnaContext();
        m.Stamp(ctx290, ec, omega290);
        var zAt290 = -ctx290.BranchConstraints.LastOrDefault(kv => kv.Item3.Magnitude > 1).Item3;
        Assert.InRange(zAt290.Real, 29.5, 30.5);  // band 3 → Z=30
        output.WriteLine($"2.9 GHz → band 3 → Z={zAt290}  ✓");
    }

    // ── T3: Pavl → |Vs| formula ──────────────────────────────────────────────

    [Fact]
    public void T3_Pavl_ComputesCorrect_VsMagnitude()
    {
        // Pavl = 0 dBm = 1 mW = 1e-3 W.  Z = 50 Ω.
        // |Vs| = sqrt(8 · 50 · 1e-3) = sqrt(0.4) ≈ 0.6325 V.
        double pavlDbm  = 0.0;
        double z        = 50.0;
        double expected = Math.Sqrt(8.0 * z * Math.Pow(10.0, (pavlDbm - 30.0) / 10.0));

        var hz = new Dictionary<int, Complex> { [1] = new Complex(z, 0) };
        var m  = new P1ToneModel("P", hz, new Complex(z, 0), pavlDbm, 1e9, 0.0);
        m.SetToneContext(1e9, 1e9);

        var ctx = new CaptureMnaContext();
        var ec  = MakeEc(m, new[] { 1, 0, 2 });
        m.Stamp(ctx, ec, TwoPI * 1e9);   // stamp at fundamental

        var driven = ctx.SourceValues.Where(v => v.Magnitude > 1e-9).ToList();
        Assert.NotEmpty(driven);
        Assert.InRange(driven[0].Magnitude, expected - 1e-6, expected + 1e-6);
        output.WriteLine($"|Vs|_expected={expected:F6} V, stamped={driven[0].Magnitude:F6} V  ✓");
    }

    // ── T4: S-param mode — Z_Port only, no drive branch ──────────────────────

    [Fact]
    public void T4_SparamMode_NoToneContext_StampsZPortOnly()
    {
        // No SetToneContext call → S-param mode (_fc=0).
        var hz = new Dictionary<int, Complex> { [1] = new Complex(75, 0) };
        var m  = new P1ToneModel("P", hz, new Complex(50, 0), 0.0, 1e9, 0.0);

        var ctx = new CaptureMnaContext();
        var ec  = MakeEc(m, new[] { 1, 0, 2 });
        m.Stamp(ctx, ec, TwoPI * 1e9);

        // In S-param mode: 1 branch (the Z_Port), no source values.
        Assert.Equal(1, ctx.BranchCount);
        Assert.DoesNotContain(ctx.SourceValues, v => v.Magnitude > 1e-9);

        // The Z_Port AddBranchConstraint carries -Z; magnitude > 1 distinguishes it from ±1 node entries.
        var zEntry = ctx.BranchConstraints.FirstOrDefault(kv => kv.Item3.Magnitude > 1);
        var zPort  = -zEntry.Item3;
        Assert.InRange(zPort.Real, 74.5, 75.5);
        output.WriteLine($"S-param: 1 branch, Z_Port={zPort}  ✓");
    }

    // ── T5: HB mode — drive at fundamental only ───────────────────────────────

    [Fact]
    public void T5_HbMode_DriveOnlyAtFundamental()
    {
        // Pavl=0dBm, Z=50, f=1GHz.  SetToneContext(1GHz, 1GHz) → HB mode.
        var hz = new Dictionary<int, Complex> { [1] = new Complex(50, 0) };
        var m  = new P1ToneModel("P", hz, new Complex(50, 0), 0.0, 1e9, 0.0);
        m.SetToneContext(1e9, 1e9);

        var ec = MakeEc(m, new[] { 1, 0, 2 });

        // At fundamental (k=1): source value should be non-zero.
        var ctx1 = new CaptureMnaContext();
        m.Stamp(ctx1, ec, TwoPI * 1e9);
        var driven = ctx1.SourceValues.Where(v => v.Magnitude > 1e-9).ToList();
        Assert.NotEmpty(driven);
        double vs = driven[0].Magnitude;
        output.WriteLine($"k=1: |Vs|={vs:F6} V  ✓");

        // At 2nd harmonic (k=2): source value must be zero.
        var ctx2 = new CaptureMnaContext();
        m.Stamp(ctx2, ec, TwoPI * 2e9);
        Assert.DoesNotContain(ctx2.SourceValues, v => v.Magnitude > 1e-9);
        output.WriteLine("k=2: source value = 0  ✓");

        // At DC: source value must be zero.
        var ctx0 = new CaptureMnaContext();
        m.Stamp(ctx0, ec, 0.0);
        Assert.DoesNotContain(ctx0.SourceValues, v => v.Magnitude > 1e-9);
        output.WriteLine("DC: source value = 0  ✓");

        // HB mode stamps 2 branches (drive + Z_Port).
        Assert.Equal(2, ctx1.BranchCount);
    }

    // ── T6: Γ→Z conversion ────────────────────────────────────────────────────

    [Fact]
    public void T6_GammaToZ_ConversionInFactory()
    {
        // G[1]=0.5, Z=50 → Z[1] = 50*(1+0.5)/(1-0.5) = 50*1.5/0.5 = 150 Ω.
        var parameters = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["P1ToneName"] = new Value("Src"),
            ["Pavl"]       = new Value(0.0),
            ["Z"]          = new Value(50.0),
            ["Freq"]       = new Value(1e9),
            ["Phase"]      = new Value(0.0),
            ["G[1]"]       = new Value(0.5),
        };
        var model = ComponentModelFactory.TryCreate("P1Tone", parameters) as P1ToneModel;
        Assert.NotNull(model);

        var z1 = model!.GetDeclaredZ(1);
        Assert.InRange(z1.Real,      149.999, 150.001);
        Assert.InRange(z1.Imaginary, -1e-9,   1e-9);
        output.WriteLine($"G[1]=0.5, Z0=50 → Z[1]={z1.Real:F2}+j{z1.Imaginary:F2} Ω  ✓");
    }

    // ── T7: Commensurability check ────────────────────────────────────────────

    [Fact]
    public void T7_CommensurabilityCheck_ValidAndInvalid()
    {
        // P1Tone at f0=1GHz with R load on a grid f0=1GHz, K=1 → passes commensurability.
        // (R load provides a non-degenerate linear circuit; no nonlinear device needed.)
        var cnlOk = "P1Tone:P1  rf 0  Pavl=0  Z=50  Freq=1e9  Phase=0\n" +
                    "R:R1  rf 0  R=50\n" +
                    "analysis HB1  type=hb  Tone=1e9  MaxHarm=1  Tol=1e-6\n";
        var (libOk, tbOk) = new CnlReader().Read(cnlOk, sourceDirectory: null);
        var netlistOk     = new Elaborator(libOk).Elaborate(tbOk);
        var hbaOk         = tbOk.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var pOk           = HbEngine.Resolve(hbaOk, netlistOk.ResolvedGlobals);

        // Must not throw a commensurability error (other errors, e.g. no-nonlinear, are fine).
        Exception? commensurabilityEx = null;
        try
        {
            new HbEngine(netlistOk, tbOk).Run(pOk);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("Commensurability"))
        {
            commensurabilityEx = e;
        }
        catch { /* other engine errors (DC singular, etc.) are acceptable */ }

        Assert.Null(commensurabilityEx);
        output.WriteLine("P1Tone@1GHz on f0=1GHz K=1: no commensurability error  ✓");

        // P1Tone at 1.5GHz on a grid with f0=1GHz, K=3 → must throw commensurability.
        var cnlBad = "P1Tone:P1  rf 0  Pavl=0  Z=50  Freq=1.5e9  Phase=0\n" +
                     "R:R1  rf 0  R=50\n" +
                     "analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-6\n";
        var (libBad, tbBad) = new CnlReader().Read(cnlBad, sourceDirectory: null);
        var netlistBad      = new Elaborator(libBad).Elaborate(tbBad);
        var hbaBad          = tbBad.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var pBad            = HbEngine.Resolve(hbaBad, netlistBad.ResolvedGlobals);

        InvalidOperationException? exBad = null;
        try { new HbEngine(netlistBad, tbBad).Run(pBad); }
        catch (InvalidOperationException e) { exBad = e; }

        Assert.NotNull(exBad);
        Assert.Contains("Commensurability", exBad!.Message);
        output.WriteLine($"P1Tone@1.5GHz on f0=1GHz K=3: '{exBad.Message}'  ✓");
    }

    // ── T7b: the swept-tone diagnosis ─────────────────────────────────────────
    //
    //  The way this check actually fires in practice: a frequency sweep moves the SOURCE
    //  (Freq=RFfreq GHz) while the analysis's own Tone is left as a fixed number, so the tone grid
    //  stays where it started and every sweep point past the first is off-grid. The generic message
    //  names the source — the half that is right — and sends the reader to the wrong place, so the
    //  message must also name the variable and the fix.

    [Fact]
    public void T7b_OffGridSourceFollowingASweptVariable_NamesTheVariableAndTheFix()
    {
        var cnl = "RFfreq = 3\n" +
                  "P1Tone:P1  rf 0  Pavl=0  Z=50  Freq=RFfreq GHz  Phase=0\n" +
                  "R:R1  rf 0  R=50\n" +
                  "analysis HB1  type=hb  Tone=2  ToneUnit=GHz  MaxHarm=3  Tol=1e-6\n";
        var (lib, tb) = new CnlReader().Read(cnl, sourceDirectory: null);
        var netlist   = new Elaborator(lib).Elaborate(tb);
        var hba       = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p         = HbEngine.Resolve(hba, netlist.ResolvedGlobals, netlist.GlobalsWithExplicitUnit);

        InvalidOperationException? ex = null;
        try { new HbEngine(netlist, tb).Run(p); }
        catch (InvalidOperationException e) { ex = e; }

        Assert.NotNull(ex);
        Assert.Contains("Commensurability", ex!.Message);
        Assert.Contains("'RFfreq'", ex.Message);
        Assert.Contains("Tone", ex.Message);
        output.WriteLine(ex.Message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ElaboratedComponent MakeEc(P1ToneModel model, int[] nodes)
        => new("P1Tone", "P1", nodes, new Dictionary<string, Value>(), model);

    private sealed class CaptureMnaContext : CircuitRF.Core.IMnaContext
    {
        private int _br;
        public int BranchCount => _br;
        public List<Complex> SourceValues { get; } = [];

        // (branchIdx, nodeOrBr, coefficient) for AddConstraint and AddBranchConstraint
        public List<(int, int, Complex)> BranchConstraints { get; } = [];

        public int  AddBranch()                                      => _br++;
        public void AddAdmittance(int na, int nb, Complex y)        { }
        public void AddBlockAdmittance(int rn, int cn, Complex y)   { }
        public void AddBranchCurrent(int b, int na, int nb)         { }
        public void AddConstraint(int b, int n, Complex c)          { BranchConstraints.Add((b, n, c)); }
        public void AddNodeBranchCoupling(int n, int b, Complex c)  { BranchConstraints.Add((n, b, c)); }
        public void AddBranchConstraint(int b1, int b2, Complex c)  { BranchConstraints.Add((b1, b2, c)); }
        public void AddCurrentInjection(int n, Complex i)           { }
        public void AddSourceValue(int b, Complex v)                { SourceValues.Add(v); }
    }
}
