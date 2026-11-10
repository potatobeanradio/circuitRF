// ================================================================
//  CombinerTests.cs — the ideal N-way Combiner (system-components.md, Combiner)
//
//  Run as Simulate runs a netlist (SchematicRunService.RunNetlist), every expected number written out
//  here from the closed form: S(S,k) = S(k,S) = 10^(−IL_k/20)·exp(j·Phase_k)/√N and nothing else; as a
//  splitter from S its NF into path k is that path's whole loss; and a matched passive two-port at T0 has
//  NF = −20·log10|S21|, so a splitter feeding a combiner whose phases cancel is noiseless when lossless.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

[Collection(CellStatGlobalsCollection.Name)]
public sealed class CombinerTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-cmb-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    private const string Sp = "analysis SP1 type=sparam start=\"2450000000\" stop=\"2450000000\" npts=1";

    private static double Amp(double db) => Math.Pow(10, -db / 20);

    [Fact]
    public void TheSMatrix_IsTheClosedForm_PathByPath_AndNothingElse()
    {
        // IL for every path but 3, which states its own; a phase on paths 2 (with its unit) and 4 (bare: degrees).
        var rows = new List<string>();
        for (int i = 1; i <= 5; i++)
            for (int j = 1; j <= 5; j++)
                rows.Add($"measure re{i}{j} = real(SP1.S[0, {i}, {j}])\nmeasure im{i}{j} = imag(SP1.S[0, {i}, {j}])");
        var m = Run($"""
            Port:P1 s 0 Num=1 Z=50 Ohm
            Port:P2 a 0 Num=2 Z=50 Ohm
            Port:P3 b 0 Num=3 Z=50 Ohm
            Port:P4 c 0 Num=4 Z=50 Ohm
            Port:P5 d 0 Num=5 Z=50 Ohm
            Combiner:CMB s 0 a 0 b 0 c 0 d 0 N=4 IL=0.5 dB IL[3]=2 dB Phase[2]=-60 deg Phase[4]=30
            {Sp}
            {string.Join("\n", rows)}
            """);

        double[] il = [0.5, 0.5, 2, 0.5], ph = [0, -60, 0, 30];
        var expected = new Complex[5, 5];
        for (int k = 0; k < 4; k++)
            expected[0, k + 1] = expected[k + 1, 0] = Complex.FromPolarCoordinates(Amp(il[k]) / 2, ph[k] * Math.PI / 180);
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                Assert.Equal(expected[i, j].Real, m[$"re{i + 1}{j + 1}"], 12);
                Assert.Equal(expected[i, j].Imaginary, m[$"im{i + 1}{j + 1}"], 12);
            }
    }

    [Theory]
    [InlineData(1, 0.5)]   // a path at the common IL
    [InlineData(2, 1.7)]   // a path with its own
    public void AsASplitter_ItsNoiseFigureIntoAPath_IsThatPathsWholeLoss(int path, double pathLossDb)
    {
        // The other paths terminated in resistors at the ambient: isolated from the measured path, they add nothing.
        string[] nets = ["a", "b", "c", "d"];
        string terms = string.Join("\n", nets.Select((n, i) => i + 1 == path ? $"Port:P2 {n} 0 Num=2 Z=50 Ohm" : $"R:RT{i + 1} {n} 0 R=50 Ohm"));
        var m = Run($"""
            temp = 16.85
            P1Tone:SRC s 0 Num=1 Freq=2450000000 Pavl=-30 dBm Z=50 Ohm
            {terms}
            Combiner:CMB s 0 a 0 b 0 c 0 d 0 N=4 IL=0.5 dB IL[2]=1.7 dB
            {Sp} Noise=yes
            analysis HBN type=hb Tone="2450000000" ToneUnit=Hz MaxHarm=3 Tol=1e-12 SSStart="2450001000" SSStop="2450001000" SSNpts=1 SSUnit=Hz Noise=yes NoiseIn=1 NoiseOut=2
            measure nf_sp = at(SP1.NF, "freq", 0)
            measure nf_hb = at(HBN.NF, "ssfreq", 0)
            """);
        double expected = 10 * Math.Log10(4) + pathLossDb;
        Assert.Equal(expected, m["nf_sp"], 9);
        Assert.Equal(expected, m["nf_hb"], 6);
    }

    [Theory]
    [InlineData(new[] { 0.0, 0.0, 0.0, 0.0 })]
    [InlineData(new[] { 0.0, 1.0, 2.0, 3.0 })]
    public void SplitThenCombinedInPhase_TheAmplitudesAdd_AndAMatchedPassivesNoiseIsItsLoss(double[] splitLossDb)
    {
        // The splitter puts a phase on each path and the combiner takes it out again, so the four arrive in phase:
        // S21 = Σ a_k·1 / (√4·√4). A matched passive two-port at T0 has F = 1/|S21|², so lossless it is noiseless.
        string losses = string.Join(" ", splitLossDb.Select((l, k) => $"IL[{k + 1}]={l} dB"));
        var m = Run($"""
            temp = 16.85
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            Combiner:SPL in 0 a 0 b 0 c 0 d 0 N=4 {losses} Phase[2]=-45 Phase[3]=-90 Phase[4]=-135
            Combiner:CMB out 0 a 0 b 0 c 0 d 0 N=4 Phase[2]=45 Phase[3]=90 Phase[4]=135
            {Sp} Noise=yes
            measure g = mag(SP1.S[0, 2, 1])
            measure nf = at(SP1.NF, "freq", 0)
            """);
        double s21 = splitLossDb.Sum(Amp) / 4;
        Assert.Equal(s21, m["g"], 12);
        Assert.Equal(-20 * Math.Log10(s21), m["nf"], 9);
    }

    [Fact]
    public void TheGlyph_HasNPlusOnePins_OnTheGrid_AndALineDrawsAndExtractsAsItself()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Combiner, 4);
        Assert.Equal(new[] { ("S", 300f, 0f), ("1", -300f, -300f), ("2", -300f, -100f), ("3", -300f, 100f), ("4", -300f, 300f) },
                     sym.Pins.Select(p => (p.Name!, (float)p.LocalX, (float)p.LocalY)));
        Assert.Equal("N", ComponentTypeRegistry.PortCountParameter(SymbolKind.Combiner));

        const string line = "Combiner:CMB sum 0 b1 0 b2 0 b3 0 b4 0 N=4 IL=0.5 dB IL[2]=1 dB Phase[3]=-dpsi deg";
        var (lib, tb) = new CnlReader().Read($"dpsi = 30\nPort:P1 sum 0 Num=1 Z=50 Ohm\n{line}\n", "tb", null);
        var drawn = NetlistSchematic.Build(lib, tb, null).Schematic;
        Assert.NotNull(drawn);
        var cmb = Assert.Single(drawn!.Components, c => c.InstanceName == "CMB");
        Assert.Equal(SymbolKind.Combiner, cmb.Symbol);
        Assert.Equal(4, cmb.PortCount);

        var inst = Assert.Single(NetExtractor.Extract(drawn).TestBench.Instances, i => i.InstanceName == "CMB");
        Assert.Equal("Combiner", inst.Reference);
        Assert.Equal(new[] { "sum", "0", "b1", "0", "b2", "0", "b3", "0", "b4", "0" }, inst.NetBindings);
        var ov = inst.Overrides.ToDictionary(o => o.Name, o => o.Expression);
        Assert.Equal("4", ov["N"]);
        Assert.Equal("1", ov["IL[2]"]);
        Assert.Equal("-dpsi", ov["Phase[3]"]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="netlist"/> in a fresh workspace and returns its measurements.</summary>
    private Dictionary<string, double> Run(string netlist)
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, ".cws"), "{\"FormatVersion\": 2, \"LibraryRefs\": [], \"KnownFiles\": []}");
        string path = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(path, netlist + "\n");
        var run = SchematicRunService.RunNetlist(path, _ws);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage + "\n" + string.Join("\n", run.Errors));
        Assert.Empty(run.Errors);
        Assert.DoesNotContain(run.Warnings, w => w.Contains("failed to evaluate", StringComparison.Ordinal));
        return run.GroupedResults!.CubesIn("measurements").ToDictionary(kv => kv.Key, kv => kv.Value.RealValues[0]);
    }
}
