// ================================================================
//  SddPortCountTests.cs  —  R-h9c-11 of brief-harmonicarf-r1c-chrome-readouts-dut-and-export
//
//  "User should have option to use SDD2 or SDD3." Port 1 = gate-vs-source, port 2 = drain-vs-source
//  (unchanged); SDD3 adds port 3 = source-vs-ground.
// ================================================================

using System;
using System.Collections.Generic;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class SddPortCountTests(ITestOutputHelper output)
{
    private static CircuitModel Model(int sddPortCount) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD", SddPortCount = sddPortCount,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "_v2/1000",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings { HarmonicCount = 2, FrequencyHz = 2e9 },
    };

    [Fact]
    public void DefaultPortCount_Is2_AndTheGeneratedLineHasTwoPortPairs()
    {
        var model = Model(2);
        Assert.Equal(2, model.Dut.SddPortCount);

        string text = HarmonicaNetlist.Build(model).Text;
        string sddLine = FindSddLine(text);
        output.WriteLine(sddLine);

        // "SDD:DUT  gate source  drain source  I[1,0]=... I[2,0]=..." — 4 net tokens before the
        // first "I[" token, i.e. two port pairs.
        Assert.Equal(2, PortPairCount(sddLine));
    }

    [Fact]
    public void Sdd3_AddsAThirdPortPair_SourceAgainstGround()
    {
        var model = Model(3);
        string text = HarmonicaNetlist.Build(model).Text;
        string sddLine = FindSddLine(text);
        output.WriteLine(sddLine);

        Assert.Equal(3, PortPairCount(sddLine));
        // The third pair is the source terminal against ground ("0") — literally present as a
        // trailing " 0" net token before the equations start.
        Assert.Contains(" 0  I[", sddLine.Replace("   ", "  "), StringComparison.Ordinal);
    }

    [Fact]
    public void Sdd2AndSdd3_BothElaborateAndSolve()
    {
        foreach (int ports in new[] { 2, 3 })
        {
            var ctx = HarmonicaContext.Create(Model(ports));
            var terms = new TerminationSet(2);
            terms.Set(TerminationSide.Source, 1, new System.Numerics.Complex(50, 0));
            terms.Set(TerminationSide.Load,   1, new System.Numerics.Complex(50, 0));

            var point = ctx.Solve(terms, pavlDbm: -10);
            Assert.True(point.Converged, $"SDD{ports} did not converge");
            output.WriteLine($"SDD{ports}: converged in {point.Iterations} iterations");
        }
    }

    [Fact]
    public void PortCountIsStructural_ChangingItRebuildsTheContext()
    {
        var ctx = HarmonicaContext.Create(Model(2));
        Assert.Equal(1, ctx.RebuildCount);

        bool rebuilt = ctx.Apply(Model(3));
        Assert.True(rebuilt);
        Assert.Equal(2, ctx.RebuildCount);
    }

    [Fact]
    public void PortCountEntersTheStructuralKey()
    {
        Assert.NotEqual(Model(2).StructuralKey, Model(3).StructuralKey);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string FindSddLine(string netlistText)
    {
        foreach (string line in netlistText.Split('\n'))
            if (line.TrimStart().StartsWith("SDD:", StringComparison.Ordinal))
                return line.Trim();
        throw new InvalidOperationException("no SDD: line found in the generated netlist");
    }

    /// <summary>Counts net-token pairs before the first "I[" equation header — the SDD line's own
    /// port-pair convention (two net names per port).</summary>
    private static int PortPairCount(string sddLine)
    {
        int cut = sddLine.IndexOf("I[", StringComparison.Ordinal);
        string prefix = cut >= 0 ? sddLine[..cut] : sddLine;
        var tokens = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // First token is "SDD:DUT" (the instance header) — everything after it is net names, 2 per port.
        return (tokens.Length - 1) / 2;
    }
}
