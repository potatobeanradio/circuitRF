using System.Linq;
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

        // Raw S-param directive is promoted to typed SParameterAnalysis on re-read
        Assert.Empty(tb2.RawDirectives);
        var spa = Assert.Single(tb2.Analyses) as SParameterAnalysis;
        Assert.NotNull(spa);
        Assert.Equal("SP", spa.Name);
        Assert.Single(spa.Sweeps);
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

    // ── Test 3: Z_Port instance round-trip (2N ± pair nets) ────────────────

    [Fact]
    public void ZPortInstance_1Port_RoundTrip()
    {
        // 1-port Z_Port: 2 nets (port1+, port1−). RefNetBinding always null.
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("Zs", "Z_Port",
            ["n_src", "0"],
            [new ParameterAssignment("Z[1,1]", "50")]));

        var tb2 = RoundTrip(tb);

        var inst = Assert.Single(tb2.Instances);
        Assert.Equal("Zs", inst.InstanceName);
        Assert.Equal("Z_Port", inst.Reference);
        Assert.Equal(["n_src", "0"], inst.NetBindings.ToList());
        Assert.Null(inst.RefNetBinding);

        var ov = Assert.Single(inst.Overrides);
        Assert.Equal("Z[1,1]", ov.Name);
        Assert.Equal("50", ov.Expression);
    }

    [Fact]
    public void ZPortInstance_2Port_RoundTrip()
    {
        // 2-port Z_Port: 4 nets (port1+, port1−, port2+, port2−). Per-port references.
        var tb = new TestBench("test");
        tb.Instances.Add(new Instance("Z2", "Z_Port",
            ["a", "n_ref1", "b", "n_ref2"],
            [new ParameterAssignment("Z[1,1]", "50"),
             new ParameterAssignment("Z[2,2]", "50"),
             new ParameterAssignment("Z[1,2]", "0"),
             new ParameterAssignment("Z[2,1]", "0")]));

        var tb2 = RoundTrip(tb);

        var inst = Assert.Single(tb2.Instances);
        Assert.Equal("Z_Port", inst.Reference);
        Assert.Equal(["a", "n_ref1", "b", "n_ref2"], inst.NetBindings.ToList());
        Assert.Null(inst.RefNetBinding);
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
#pragma warning disable CS0618
            SweepVarName      = "Pavl_dbm",
            SweepStartExpr    = "-20",
            SweepStopExpr     = "0",
            SweepStepExpr     = "1",
#pragma warning restore CS0618
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
#pragma warning disable CS0618
        Assert.Equal("Pavl_dbm", hb.SweepVarName);
        Assert.Equal("-20", hb.SweepStartExpr);
        Assert.Equal("0", hb.SweepStopExpr);
        Assert.Equal("1", hb.SweepStepExpr);
#pragma warning restore CS0618
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

    // ── Test 7: DcAnalysis round-trip ─────────────────────────────────────────

    [Fact]
    public void DcAnalysis_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.Analyses.Add(new DcAnalysis("DC1"));

        var tb2 = RoundTrip(tb);

        var dc = Assert.Single(tb2.Analyses) as DcAnalysis;
        Assert.NotNull(dc);
        Assert.Equal("DC1", dc.Name);
    }

    // ── Test 8: SParameterAnalysis multi-segment round-trip ───────────────────

    [Fact]
    public void SParameterAnalysis_MultiSegment_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.Analyses.Add(new SParameterAnalysis("SP1",
            new List<FrequencySpec>
            {
                new FrequencySpec("1e9", "5e9", 41),
                new FrequencySpec("5e9", "10e9", 51),
            }));

        // CnlWriter emits two "analysis SP1 type=sparam" lines.
        var text = CnlWriter.Write(tb);
        var lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Count(l => l.TrimStart().StartsWith("analysis SP1")));

        // CnlReader merges both lines back into one SParameterAnalysis with 2 segments.
        var tb2 = RoundTrip(tb);

        var sp = Assert.Single(tb2.Analyses) as SParameterAnalysis;
        Assert.NotNull(sp);
        Assert.Equal("SP1", sp.Name);
        Assert.Equal(2, sp.Sweeps.Count);
        Assert.Equal(FreqSpecMode.PointCount, sp.Sweeps[0].Mode);
        Assert.Equal(41, sp.Sweeps[0].NumPoints);
        Assert.Equal(FreqSpecMode.PointCount, sp.Sweeps[1].Mode);
        Assert.Equal(51, sp.Sweeps[1].NumPoints);
    }

    // ── Test 9: Measurement with unit round-trips ─────────────────────────────

    [Fact]
    public void Measurement_WithUnit_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.Measurements.Add(new Measurement("Pout", "pout()", "dBm"));
        tb.Measurements.Add(new Measurement("IL",   "dB(S(2,1))", "dB"));
        tb.Measurements.Add(new Measurement("PAE",  "pae()", "%"));
        tb.Measurements.Add(new Measurement("Gain", "S21"));   // no unit

        var tb2 = RoundTrip(tb);

        Assert.Equal(4, tb2.Measurements.Count);

        var pout = tb2.Measurements.First(m => m.Name == "Pout");
        Assert.Equal("pout()", pout.Expression);
        Assert.Equal("dBm",    pout.Unit);

        var il = tb2.Measurements.First(m => m.Name == "IL");
        Assert.Equal("dB(S(2,1))", il.Expression);
        Assert.Equal("dB",         il.Unit);

        var pae = tb2.Measurements.First(m => m.Name == "PAE");
        Assert.Equal("pae()", pae.Expression);
        Assert.Equal("%",     pae.Unit);

        var gain = tb2.Measurements.First(m => m.Name == "Gain");
        Assert.Equal("S21", gain.Expression);
        Assert.Null(gain.Unit);
    }

    // ── Tests: labelednets round-trip (brief-cnl-labelednets-provenance) ─────

    /// <summary>
    /// LabeledNets survive a full CnlWriter → CnlReader round-trip.
    /// Regression guard: before fix, CnlWriter never emitted the directive and
    /// LabeledNets was always empty after reading, breaking the node-picker filter.
    /// </summary>
    [Fact]
    public void Cnl_RoundTrips_LabeledNets()
    {
        var tb = new TestBench("test");
        tb.LabeledNets.Add("n_drain");
        tb.LabeledNets.Add("n_gate");

        var text = CnlWriter.Write(tb);

        Assert.Contains("labelednets", text);

        var (_, tb2) = new CnlReader().Read(text);
        Assert.Equal(2, tb2.LabeledNets.Count);
        Assert.Contains("n_drain", tb2.LabeledNets);
        Assert.Contains("n_gate",  tb2.LabeledNets);
    }

    /// <summary>
    /// An empty LabeledNets set must not emit a labelednets line, and re-reading
    /// such a file must leave LabeledNets empty.
    /// </summary>
    [Fact]
    public void Cnl_NoLabeledNets_NoDirective()
    {
        var tb   = new TestBench("test");
        var text = CnlWriter.Write(tb);

        Assert.DoesNotContain("labelednets", text);

        var (_, tb2) = new CnlReader().Read(text);
        Assert.Empty(tb2.LabeledNets);
    }

    /// <summary>
    /// A labelednets directive inside a define…end block must throw CnlReadException.
    /// The directive is only meaningful at top level.
    /// </summary>
    [Fact]
    public void Cnl_LabeledNets_InsideDefine_Throws()
    {
        const string badCnl = """
            define MyCell (a b)
            labelednets n_drain
            end MyCell
            """;
        Assert.Throws<CnlReadException>(() => new CnlReader().Read(badCnl));
    }

    // ── User-defined expression functions ────────────────────────────────────

    /// <summary>
    /// A function declaration must survive the write. This is not symmetry for its own sake: the
    /// run path writes netlist.cnl and re-reads it, and a kit's cells call the kit's functions by
    /// bare name — so a writer that silently drops them turns a perfectly good extraction into
    /// "Unknown function '…'" from the elaborator, naming a file that never contained it.
    /// </summary>
    [Fact]
    public void UserFunctions_RoundTrip()
    {
        var tb = new TestBench("test");
        tb.GlobalVariables.Add(new Variable("CapPerArea", "1.2e-15"));
        tb.Functions.Add(new CircuitRF.Core.Expressions.UserFunction(
            "PadCap", ["w", "l"], "w * l * CapPerArea"));
        tb.Functions.Add(new CircuitRF.Core.Expressions.UserFunction(
            "Half", ["x"], "x / 2"));

        var tb2 = RoundTrip(tb);

        Assert.Equal(2, tb2.Functions.Count);

        var pad = Assert.Single(tb2.Functions, f => f.Name == "PadCap");
        Assert.Equal(new[] { "w", "l" }, pad.Parameters);
        Assert.Equal("w * l * CapPerArea", pad.Body);

        var half = Assert.Single(tb2.Functions, f => f.Name == "Half");
        Assert.Equal(new[] { "x" }, half.Parameters);
        Assert.Equal("x / 2", half.Body);

        // The globals a function's body references must still be there too.
        AssertVariable(tb2, "CapPerArea", "1.2e-15", null);
    }

    /// <summary>
    /// A single-argument declaration must not be mistaken for an ordinary assignment on the way
    /// back in — the reader distinguishes them only by the parenthesised parameter list.
    /// </summary>
    [Fact]
    public void AFunctionAndAVariable_StayDistinct_AcrossTheRoundTrip()
    {
        var tb = new TestBench("test");
        tb.GlobalVariables.Add(new Variable("Scale", "3"));
        tb.Functions.Add(new CircuitRF.Core.Expressions.UserFunction("Scale2", ["x"], "x * Scale"));

        var tb2 = RoundTrip(tb);

        Assert.Equal("Scale2", Assert.Single(tb2.Functions).Name);
        Assert.Equal("Scale", Assert.Single(tb2.GlobalVariables).Name);
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
