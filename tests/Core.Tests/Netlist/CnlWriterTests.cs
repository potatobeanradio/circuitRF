using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Layer 1 gate: CnlWriter → text → CnlReader round-trip.
/// Gate: hand-built TestBench → Write → text → Read → equivalent TestBench
/// (same instances, references, net-bindings-in-order, params, ref-nodes, variables).
/// </summary>
public class CnlWriterTests
{
    // ── Round-trip helpers ───────────────────────────────────────────────────

    /// <summary>Write then re-read to produce a fresh TestBench.</summary>
    private static TestBench RoundTrip(TestBench tb)
    {
        var text = CnlWriter.Write(tb);
        var (_, tb2) = new CnlReader().Read(text);
        return tb2;
    }

    // ── Test 1: standard instances, variables, measurement, raw directive ───

    [Fact]
    public void StandardInstances_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.GlobalVariables.Add(new Variable("f0", "1e9"));
        tb.GlobalVariables.Add(new Variable("Z0", "50", "Ohm"));

        tb.Instances.Add(new Instance("P1", "Port", ["sig1", "0"],
            [new ParameterAssignment("Num", "1"),
             new ParameterAssignment("Z", "50")]));
        tb.Instances.Add(new Instance("R1", "R", ["sig1", "sig2"],
            [new ParameterAssignment("R", "50", "Ohm")]));
        tb.Instances.Add(new Instance("C1", "C", ["sig2", "0"],
            [new ParameterAssignment("C", "1", "pF")]));

        tb.Measurements.Add(new Measurement("IL", "dB(S(2,1))"));
        tb.RawDirectives.Add(new RawDirective("analysis", "SP type=sparam start=1GHz stop=3GHz step=1GHz"));

        var tb2 = RoundTrip(tb);

        // Variables
        AssertVariable(tb2, "f0", "1e9", null);
        AssertVariable(tb2, "Z0", "50", "Ohm");

        // Instances
        AssertInstance(tb2, "P1", "Port", ["sig1", "0"],
            [("Num", "1", null), ("Z", "50", null)]);
        AssertInstance(tb2, "R1", "R", ["sig1", "sig2"],
            [("R", "50", "Ohm")]);
        AssertInstance(tb2, "C1", "C", ["sig2", "0"],
            [("C", "1", "pF")]);

        // Measurement
        var m = Assert.Single(tb2.Measurements);
        Assert.Equal("IL", m.Name);
        Assert.Equal("dB(S(2,1))", m.Expression);

        // Raw directive
        var rd = Assert.Single(tb2.RawDirectives);
        Assert.Equal("analysis", rd.Kind);
        Assert.Equal("SP type=sparam start=1GHz stop=3GHz step=1GHz", rd.RawLine);
    }

    // ── Test 2: SDD instance round-trip ─────────────────────────────────────

    [Fact]
    public void SddInstance_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("M1", "SDD",
            ["n_gate", "0", "n_drain", "0"],
            [new ParameterAssignment("I[1,0]", "_v1/50"),
             new ParameterAssignment("I[2,0]", "_v2*0.02")]));

        var tb2 = RoundTrip(tb);

        AssertInstance(tb2, "M1", "SDD",
            ["n_gate", "0", "n_drain", "0"],
            [("I[1,0]", "_v1/50", null),
             ("I[2,0]", "_v2*0.02", null)]);
    }

    // ── Test 3: Z_Port instance round-trip (N-or-N+1 rule) ──────────────────

    [Fact]
    public void ZPortInstance_GroundRef_RoundTrip()
    {
        // Ground-referenced (N nets, RefNetBinding null): just N signal nets.
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("Zs", "Z_Port",
            ["n_src"],
            [new ParameterAssignment("Z[1,1]", "50")]));

        var tb2 = RoundTrip(tb);

        var inst = Assert.Single(tb2.Instances);
        Assert.Equal("Zs", inst.InstanceName);
        Assert.Equal("Z_Port", inst.Reference);
        Assert.Equal(["n_src"], inst.NetBindings.ToList());
        Assert.Null(inst.RefNetBinding);

        var ov = Assert.Single(inst.Overrides);
        Assert.Equal("Z[1,1]", ov.Name);
        Assert.Equal("50", ov.Expression);
    }

    [Fact]
    public void ZPortInstance_FloatingRef_RoundTrip()
    {
        // Floating reference (N+1 nets): signal nets + RefNetBinding.
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("Zs", "Z_Port",
            ["n_src"],
            [new ParameterAssignment("Z[1,1]", "50")])
        { RefNetBinding = "n_ref" });

        var tb2 = RoundTrip(tb);

        var inst = Assert.Single(tb2.Instances);
        Assert.Equal(["n_src"], inst.NetBindings.ToList());
        Assert.Equal("n_ref", inst.RefNetBinding);
    }

    // ── Test 4: HarmonicBalanceAnalysis round-trip ───────────────────────────

    [Fact]
    public void HarmonicBalanceAnalysis_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr          = "RFfreq",
            MaxHarmonicExpr   = "MaxHarm",
            FFTOverSampleExpr = "OverSamp",
            TolExpr           = "HBtol",
            DriveSteppingExpr = "DriveStep",
            GuardHarmonicExpr = "Guard",
            LambdaExpr        = "1",
            MaxIterExpr       = "100",
            SweepVarName      = "Pavl_dbm",
            SweepStartExpr    = "-20",
            SweepStopExpr     = "0",
            SweepStepExpr     = "1",
        });

        var tb2 = RoundTrip(tb);

        var hb = Assert.Single(tb2.Analyses) as HarmonicBalanceAnalysis;
        Assert.NotNull(hb);
        Assert.Equal("HB1", hb.Name);
        Assert.Equal("RFfreq", hb.ToneExpr);
        Assert.Equal("MaxHarm", hb.MaxHarmonicExpr);
        Assert.Equal("OverSamp", hb.FFTOverSampleExpr);
        Assert.Equal("HBtol", hb.TolExpr);
        Assert.Equal("DriveStep", hb.DriveSteppingExpr);
        Assert.Equal("Guard", hb.GuardHarmonicExpr);
        Assert.Equal("1", hb.LambdaExpr);
        Assert.Equal("100", hb.MaxIterExpr);
        Assert.Equal("Pavl_dbm", hb.SweepVarName);
        Assert.Equal("-20", hb.SweepStartExpr);
        Assert.Equal("0", hb.SweepStopExpr);
        Assert.Equal("1", hb.SweepStepExpr);
    }

    // ── Test 5: header comment is a comment, not a directive ─────────────────

    [Fact]
    public void HeaderComment_NotEmittedAsDirective()
    {
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("R1", "R", ["a", "b"], []));

        var text = CnlWriter.Write(tb, "Generated from TestBench");

        Assert.Contains("; Generated from TestBench", text);

        var tb2 = RoundTrip(tb);
        Assert.Single(tb2.Instances);
    }

    // ── Test 6: SnP instance with floating reference ─────────────────────────

    [Fact]
    public void SnpInstance_FloatingRef_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("X1", "SnP",
            ["n1", "n2"],
            [new ParameterAssignment("NumPorts", "2"),
             new ParameterAssignment("File", "\"/path/to/file.s2p\""),
             new ParameterAssignment("Type", "\"touchstone\"")])
        { RefNetBinding = "n_ref" });

        var tb2 = RoundTrip(tb);

        var inst = Assert.Single(tb2.Instances);
        Assert.Equal("X1", inst.InstanceName);
        Assert.Equal("SnP", inst.Reference);
        Assert.Equal(["n1", "n2"], inst.NetBindings.ToList());
        Assert.Equal("n_ref", inst.RefNetBinding);

        // NumPorts, File, Type should round-trip (NumPorts is kept for SnP — only skipped for SDD/ZPort)
        Assert.Contains(inst.Overrides, ov => ov.Name == "NumPorts" && ov.Expression == "2");
        Assert.Contains(inst.Overrides, ov => ov.Name == "File");
        Assert.Contains(inst.Overrides, ov => ov.Name == "Type");
    }

    // ── Assertion helpers ────────────────────────────────────────────────────

    private static void AssertVariable(TestBench tb, string name, string expr, string? unit)
    {
        var v = tb.GlobalVariables.FirstOrDefault(v => v.Name == name);
        Assert.NotNull(v);
        Assert.Equal(expr, v.Expression);
        Assert.Equal(unit, v.Unit);
    }

    private static void AssertInstance(
        TestBench tb,
        string instanceName,
        string reference,
        string[] nets,
        (string Name, string Expr, string? Unit)[] overrides)
    {
        var inst = tb.Instances.FirstOrDefault(i => i.InstanceName == instanceName);
        Assert.NotNull(inst);
        Assert.Equal(reference, inst.Reference);
        Assert.Equal(nets, inst.NetBindings.ToArray());

        Assert.Equal(overrides.Length, inst.Overrides.Count);
        for (int i = 0; i < overrides.Length; i++)
        {
            Assert.Equal(overrides[i].Name, inst.Overrides[i].Name);
            Assert.Equal(overrides[i].Expr, inst.Overrides[i].Expression);
            Assert.Equal(overrides[i].Unit, inst.Overrides[i].Unit);
        }
    }
}
