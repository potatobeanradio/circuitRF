using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Tests for the two-tone HB directive parsing (NumFreqs / Tone[i] / MaxMixOrder)
/// and the multi-tone commensurability contract.
/// </summary>
public class HbDirectiveTests
{
    // ── Locate hero5.cnl ─────────────────────────────────────────────────────

    private static string Hero5Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "testdata", "Hero5");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Cannot find testdata/Hero5");
    }

    // ── Parse hero5.cnl and verify the multi-tone directive ──────────────────

    [Fact]
    public void Hero5_ParsesNumFreqs2_AndToneExprs()
    {
        var path    = Path.Combine(Hero5Dir(), "hero5.cnl");
        var (_, tb) = CnlReader.ReadFile(path);

        // Find the HB analysis directive.
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().FirstOrDefault();
        Assert.NotNull(hba);

        // NumFreqs = 2
        Assert.Equal("2", hba.NumFreqsExpr);

        // ToneExprs should have two entries.
        Assert.Equal(2, hba.ToneExprs.Length);

        // MaxMixOrder expression
        Assert.Equal("MaxMixOrder", hba.MaxMixOrderExpr);

        // MaxHarm expression
        Assert.Equal("MaxHarm", hba.MaxHarmonicExpr);
    }

    [Fact]
    public void Hero5_Resolve_ProducesCorrectToneFreqs()
    {
        var path    = Path.Combine(Hero5Dir(), "hero5.cnl");
        var (_, tb) = CnlReader.ReadFile(path);
        var hba     = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();

        // Build resolved globals from the testbench variables.
        var globals = ResolveGlobals(tb);

        var p = HbEngine.Resolve(hba, globals);

        Assert.True(p.IsMultiTone);
        Assert.Equal(2, p.ToneFreqsHz.Length);

        // RFfreq=2e9, ToneSpacing=10e6 → f1=1.995GHz, f2=2.005GHz
        Assert.Equal(1.995e9, p.ToneFreqsHz[0], 1e3);
        Assert.Equal(2.005e9, p.ToneFreqsHz[1], 1e3);

        // MaxMixOrder = 5
        Assert.Equal(5, p.MaxMixOrder);

        // MaxHarmonic = 4 (hero5 sets MaxHarm=4)
        Assert.Equal(4, p.MaxHarmonic);
    }

    // ── Single-tone backward compat ───────────────────────────────────────────

    [Fact]
    public void SingleTone_ScalarTone_ResolvedCorrectly()
    {
        // Simulate a single-tone directive with no NumFreqs.
        var hba = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr        = "2e9",
            NumFreqsExpr    = "1",
            ToneExprs       = [],
            MaxHarmonicExpr = "7",
            MaxMixOrderExpr = "5",
        };
        var p = HbEngine.Resolve(hba, new Dictionary<string, Value>());

        Assert.False(p.IsMultiTone);
        Assert.Single(p.ToneFreqsHz);
        Assert.Equal(2e9, p.ToneHz, 1.0);
    }

    // ── Commensurability (forward-tested via MixingGrid; full circuit test deferred
    //    to Hero5 integration; just confirm the grid math is right here) ────────

    [Fact]
    public void MixingGrid_Hero5Tones_CarriersOnGrid()
    {
        const double f1 = 1.995e9;
        const double f2 = 2.005e9;
        var g = new MixingGrid(5);

        // Carriers are in the retained set.
        Assert.NotEqual(-1, g.IndexOf(1, 0));
        Assert.NotEqual(-1, g.IndexOf(0, 1));

        // Their physical frequencies match the declared tones.
        Assert.Equal(f1, g.FrequencyOf(g.IndexOf(1, 0), f1, f2), 1.0);
        Assert.Equal(f2, g.FrequencyOf(g.IndexOf(0, 1), f1, f2), 1.0);
    }

    [Fact]
    public void MixingGrid_Hero5Tones_Im3Im5FrequenciesCorrect()
    {
        const double f1 = 1.995e9;
        const double f2 = 2.005e9;
        var g = new MixingGrid(5);

        // IM3 lower: 2f1−f2 = 1.985 GHz
        double im3lo = g.FrequencyOf(g.IndexOf(2, -1), f1, f2);
        Assert.Equal(2 * f1 - f2, im3lo, 1e3);

        // IM5 lower: 3f1−2f2 = 1.975 GHz
        double im5lo = g.FrequencyOf(g.IndexOf(3, -2), f1, f2);
        Assert.Equal(3 * f1 - 2 * f2, im5lo, 1e3);

        // IM2 baseband: f1−f2 = −10 MHz (signed; magnitude = 10 MHz)
        double im2bb = g.FrequencyOf(g.IndexOf(1, -1), f1, f2);
        Assert.Equal(f1 - f2, im2bb, 1e3);   // −10 MHz
        Assert.Equal(10e6, Math.Abs(im2bb), 1e3);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static Dictionary<string, Value> ResolveGlobals(TestBench tb)
    {
        // Simple resolver: evaluate each top-level variable in declaration order.
        var scope = new Scope("test-globals");
        var ev    = new Evaluator();
        var result = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in tb.GlobalVariables)
        {
            try
            {
                scope.Bind(v.Name, v.Expression);
                var ast = Parser.Parse(v.Expression);
                var val = ev.EvalExpr(ast, scope);
                ev.InjectResolved("test-globals", v.Name, val);
                result[v.Name] = val;
            }
            catch { /* skip unresolvable at this simplistic pass */ }
        }
        return result;
    }
}
