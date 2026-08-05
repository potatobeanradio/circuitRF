using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Netlist.Spice;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// What is read must SURVIVE THE ROUND TRIP, not merely be extracted.
///
/// <para><b>Why this file exists separately from the reader's own tests.</b> Nothing runs the object
/// graph the reader returns: the run path is <c>CnlWriter → .cnl text → CnlReader → elaborate</c>,
/// so anything the writer cannot say is gone before the elaborator ever sees it. This repository has
/// already lost three separate things that way — a declaration the writer never emitted, an operator
/// spelled the other dialect's way, and a unit glued to its number — and each looked exactly like an
/// extractor bug and was not. Asserting on the reader's output alone would have caught none of
/// them.</para>
///
/// <para>The assertions are made after re-reading, so they test the value that reaches the engine
/// rather than the text in between.</para>
/// </summary>
public sealed class SpiceNetlistRoundTripTests
{
    /// <summary>Reads the dialect, writes circuitRF's own format, reads that back.</summary>
    private static (Library Library, TestBench Bench) RoundTrip(string spice)
    {
        var read = SpiceNetlistReader.Read(spice);

        var bench = new TestBench("rt");
        bench.GlobalVariables.AddRange(read.Variables);
        bench.Functions.AddRange(read.Functions);

        return new CnlReader().Read(CnlWriter.Write(bench, read.Library));
    }

    private static Cell Cell((Library Library, TestBench Bench) rt, string name)
        => Assert.Single(rt.Library.Cells, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Override(Instance i, string name)
        => i.Overrides.Single(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Expression;

    // ── R1 — a whole definition survives ──────────────────────────────────────

    [Fact]
    public void R1_ADefinitionSurvivesTheRoundTrip()
    {
        var rt = RoundTrip("""
            .subckt divider in out gnd rtop=1k rbot=2k
            R1 in  out rtop
            R2 out gnd rbot
            C1 out gnd 100f
            .ends divider
            """);

        var cell = Cell(rt, "divider");

        Assert.Equal(["in", "out", "gnd"], cell.Ports);
        Assert.Equal(["rtop", "rbot"], cell.Parameters.Select(p => p.Name));
        Assert.Equal(3, cell.Instances.Count);

        Assert.Equal(["in", "out"], cell.Instances[0].NetBindings);
        Assert.Equal("rtop", Override(cell.Instances[0], "R"));
        Assert.Equal("1E-13", Override(cell.Instances[2], "C"));
    }

    // ── R2 — the value spellings that are known to be lost in transit ─────────

    /// <summary>
    /// A rewritten value carries no whitespace, and this is why. circuitRF's generic instance-line
    /// parser splits on whitespace and reads bare words as NETS, so a conditional written
    /// <c>if(a, b, c)</c> comes back as a value plus two phantom nets — which shifts every later node
    /// index and still runs. The assertion is on the re-read net list, because that is where the
    /// damage would show.
    /// </summary>
    [Fact]
    public void R2_AConditionalValueDoesNotBecomePhantomNets()
    {
        var rt = RoundTrip("""
            .param wide=1
            .subckt part a b
            R1 a b {wide > 0 ? 1k : 2k}
            .ends
            """);

        var r1 = Assert.Single(Cell(rt, "part").Instances);

        Assert.Equal(["a", "b"], r1.NetBindings);
        Assert.Equal("if(wide>0,1000,2000)", Override(r1, "R"));
    }

    /// <summary>
    /// The prefix that disagrees with SI, carried all the way through. Left as a suffix for
    /// circuitRF's own unit table to read, a millifarad becomes a megafarad on the far side.
    /// </summary>
    [Fact]
    public void R3_PrefixesAreResolvedBeforeTheyCanBeReReadAsSI()
    {
        var rt = RoundTrip("""
            .subckt part a b
            R1 a b 1MEG
            C1 a b 1M
            .ends
            """);

        var i = Cell(rt, "part").Instances;

        Assert.Equal("1000000", Override(i[0], "R"));
        Assert.Equal("0.001",   Override(i[1], "C"));
    }

    // ── R4 — declarations the cells depend on ─────────────────────────────────

    /// <summary>
    /// Cells reference top-level constants and functions by bare name, so a definition that arrives
    /// without them does not resolve — and the failure surfaces far from the cause, as an unresolved
    /// name reported from inside the elaborator.
    /// </summary>
    [Fact]
    public void R4_GlobalsAndFunctionsArriveWithTheCellsThatUseThem()
    {
        var rt = RoundTrip("""
            .param rsheet=25
            .func squares(w, l) = {l/w}
            .subckt part a b
            R1 a b {rsheet * squares(2u, 10u)}
            .ends
            """);

        Assert.Equal("25", Assert.Single(rt.Bench.GlobalVariables, v => v.Name == "rsheet").Expression);

        var f = Assert.Single(rt.Bench.Functions);
        Assert.Equal("squares", f.Name);
        Assert.Equal(["w", "l"], f.Parameters);

        Assert.Equal("rsheet*squares(2E-06,1E-05)",
                     Override(Assert.Single(Cell(rt, "part").Instances), "R"));
    }

    // ── R5 — a device that names a model card ─────────────────────────────────

    /// <summary>
    /// A device whose behaviour comes from a model card keeps the card's name as its reference. It
    /// will not elaborate until something supplies a device of that name — which is the honest
    /// outcome, and is what makes the missing half visible instead of silently defaulted.
    /// </summary>
    [Fact]
    public void R5_AModelBackedDeviceKeepsTheNameItWasGiven()
    {
        var rt = RoundTrip("""
            .subckt part g d s bulk
            M1 d g s bulk nfet w=2u l=130n m=4
            .ends
            """);

        var m1 = Assert.Single(Cell(rt, "part").Instances);

        Assert.Equal("nfet", m1.Reference);
        Assert.Equal(["d", "g", "s", "bulk"], m1.NetBindings);
        Assert.Equal("2E-06",  Override(m1, "w"));
        Assert.Equal("1.3E-07", Override(m1, "l"));
        Assert.Equal("4",      Override(m1, "m"));
    }

    // ── R6 — hierarchy ────────────────────────────────────────────────────────

    [Fact]
    public void R6_AHierarchySurvivesWithItsBindingsIntact()
    {
        var rt = RoundTrip("""
            .subckt leaf a b r=1k
            R1 a b r
            .ends
            .subckt top in out
            X1 in mid  leaf r=2k
            X2 mid out leaf r=3k
            .ends
            """);

        var top = Cell(rt, "top");

        Assert.Equal(["leaf", "leaf"], top.Instances.Select(i => i.Reference));
        Assert.Equal(["in", "mid"],    top.Instances[0].NetBindings);
        Assert.Equal("2000", Override(top.Instances[0], "r"));
        Assert.Equal("3000", Override(top.Instances[1], "r"));
    }
}
