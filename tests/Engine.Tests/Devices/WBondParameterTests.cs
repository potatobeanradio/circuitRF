using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Oracle tiers 8 and 9 of brief-wbond-wbb §4 — expression-bound parameters, the loop-height sweep,
/// and the coupling audit reaching the run.
/// </summary>
public class WBondParameterTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>A design of arched wires, so a loop-height override has a rise to rescale.</summary>
    private string WriteArchedDesign(double loopHeightMil = 20.0, int wires = 4, double yOffsetMil = 0.0)
    {
        long loopNm = WBondUnits.ToNm(loopHeightMil, WBondUnit.Mil);
        var design = new WBondDesign();

        var array = new WireArray { Name = "G1" };
        for (int i = 0; i < wires; i++)
        {
            double y = yOffsetMil + i * 6.0;
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, y, 4), Point3.Mils(100, y, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm));
        }
        design.Arrays.Add(array);

        string path = Path.Combine(Path.GetTempPath(), $"wbond-param-{Guid.NewGuid():N}.wBond");
        WBondIo.WriteFile(path, design);
        _temporaryFiles.Add(path);
        return path;
    }

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static WBondModel ModelIn(ElaboratedNetlist netlist) =>
        netlist.Components.Select(c => c.Model).OfType<WBondModel>().First();

    // ---------------------------------------------------------------- tier 8

    /// <summary>
    /// TIER 8 — <b>a taller loop is a more inductive one, monotonically.</b>
    ///
    /// <para>This is the feature a PA designer actually buys the tool for, and it works only if a
    /// loop-height override regenerates the geometry and refills the inductance matrix. Scaling a
    /// stored number would give a plausible curve that is not the physics.</para>
    /// </summary>
    [Fact]
    public void Tier8_RaisingTheLoopHeight_RaisesTheArrayInductanceMonotonically()
    {
        string wbond = WriteArchedDesign(loopHeightMil: 20.0);
        double previous = 0.0;

        foreach (double heightMil in new[] { 10.0, 20.0, 30.0, 45.0 })
        {
            string cnl = $@"
loopH = {heightMil.ToString("R", CultureInfo.InvariantCulture)} mil
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 p2 0   File=""{wbond}"" LoopHeight=loopH
";
            double l = ModelIn(Elaborate(cnl)).InductanceOnly()[0, 0];

            Assert.True(l > previous,
                $"A {heightMil} mil loop must be more inductive than the one below it; " +
                $"got {l * 1e12:F1} pH after {previous * 1e12:F1} pH.");
            previous = l;
        }
    }

    /// <summary>
    /// TIER 8 — the override is an ordinary EXPRESSION in a global, which is what makes
    /// <c>parametric_sweep</c> work over it: the sweep engine re-elaborates each point, so the model
    /// is rebuilt from new geometry every time.
    /// </summary>
    [Fact]
    public void Tier8_LoopHeightIsAnOrdinaryExpression_SoASweepOverAGlobalWorks()
    {
        string wbond = WriteArchedDesign();

        static string Cnl(string wb, double mils) => $@"
base = {mils.ToString("R", CultureInfo.InvariantCulture)} mil
scale = 1.5
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 p2 0   File=""{wb}"" LoopHeight=base*scale
";
        double small = ModelIn(Elaborate(Cnl(wbond, 12.0))).InductanceOnly()[0, 0];
        double large = ModelIn(Elaborate(Cnl(wbond, 40.0))).InductanceOnly()[0, 0];

        Assert.True(large > small * 1.05,
            $"Sweeping the global must move the inductance: {small * 1e12:F1} pH vs {large * 1e12:F1} pH.");
    }

    /// <summary>The operating temperature is overridable per instance, and it moves the resistance.</summary>
    [Fact]
    public void TemperatureOverride_RaisesTheArrayResistance()
    {
        string wbond = WriteArchedDesign();

        static string Cnl(string wb, double tempC) => $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 p2 0   File=""{wb}"" Temp={tempC.ToString("R", CultureInfo.InvariantCulture)}
";
        double cold = ModelIn(Elaborate(Cnl(wbond, 20.0))).ArrayImpedance(0.0)[0].Real;
        double hot = ModelIn(Elaborate(Cnl(wbond, 150.0))).ArrayImpedance(0.0)[0].Real;

        Assert.True(hot > cold * 1.3,
            $"150 C must be materially more resistive than 20 C: {cold:E3} vs {hot:E3} ohm.");
    }

    /// <summary>A non-positive loop height is refused rather than producing a degenerate wire.</summary>
    [Fact]
    public void NonPositiveLoopHeight_IsRefused()
    {
        string wbond = WriteArchedDesign();
        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 p2 0   File=""{wbond}"" LoopHeight=0
";
        var ex = Assert.Throws<InvalidOperationException>(() => Elaborate(cnl));
        Assert.Contains("loop height", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- tier 9

    /// <summary>
    /// TIER 9 — <b>the coupling audit reaches a real elaborated netlist</b> and names both instances
    /// plus the manual remedy. In v1 this is the whole safety mechanism for inter-component coupling
    /// (WB30a), so it is gated at the netlist level and not only as a library call.
    /// </summary>
    [Fact]
    public void Tier9_TwoAdjacentWBondsInOneNetlist_AreReportedByTheAudit()
    {
        string a = WriteArchedDesign(yOffsetMil: 0.0);
        string b = WriteArchedDesign(yOffsetMil: 30.0);

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WBA p1 mid 0   File=""{a}""
wBond:WBB mid p2 0   File=""{b}""
";
        var netlist = Elaborate(cnl);
        var findings = WBondCouplingAudit.Audit(netlist);

        var finding = Assert.Single(findings);
        Assert.Contains("WBA", finding.Message, StringComparison.Ordinal);
        Assert.Contains("WBB", finding.Message, StringComparison.Ordinal);
        Assert.Contains("single wBond", finding.Message, StringComparison.OrdinalIgnoreCase);

        // And it reaches the user through the ordinary warning channel.
        int count = WBondCouplingAudit.AuditAndWarn(netlist);
        Assert.Equal(1, count);
        Assert.Contains(netlist.Warnings, w => w.Contains("NOT modelled", StringComparison.Ordinal));
    }

    /// <summary>Two wBonds far apart produce no finding — the audit must not be noise.</summary>
    [Fact]
    public void Tier9_TwoDistantWBonds_ProduceNoFinding()
    {
        string a = WriteArchedDesign(yOffsetMil: 0.0);
        string b = WriteArchedDesign(yOffsetMil: 20_000.0);

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WBA p1 mid 0   File=""{a}""
wBond:WBB mid p2 0   File=""{b}""
";
        Assert.Empty(WBondCouplingAudit.Audit(Elaborate(cnl)));
    }

    /// <summary>A single wBond has nothing to be audited against.</summary>
    [Fact]
    public void Tier9_ASingleWBond_ProducesNoFinding()
    {
        string wbond = WriteArchedDesign();
        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 p2 0   File=""{wbond}""
";
        Assert.Empty(WBondCouplingAudit.Audit(Elaborate(cnl)));
    }
}
