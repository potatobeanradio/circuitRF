using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Engine;
using CircuitRF.Engine.Transient;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Transient;

/// <summary>A test that needs a live ngspice: skipped, with the reason, when discovery finds none (transient.md §12.4).</summary>
internal sealed class NgspiceFactAttribute : FactAttribute
{
    public NgspiceFactAttribute()
    {
        if (NgspiceLocator.Find() is null)
            Skip = "no usable ngspice is discovered here (Settings ▸ Solvers, CIRCUITRF_NGSPICE or PATH); this gate runs where one is.";
    }
}

/// <summary>
/// brief TRN-7 — ngspice as a transient solver: discovery and the recipes (R-trn7-1/2, gate 6's platform half; the schema half is
/// SolverInstallTests over every shipped recipe), the deck's device terminals, a live run against the closed form and the native
/// engine (gate 2), cancelling a live run (gate 3), and the card and the run with no ngspice (gate 4).
/// </summary>
public sealed class NgspiceSolverTests(ITestOutputHelper output)
{
    private const string Banner47 = """
        ******
        ** ngspice-47 : Circuit level simulation program
        ** Compiled with KLU Direct Linear Solver
        ** The U. C. Berkeley CAD Group
        ******
        """;

    /// <summary>R-trn7-1/2 — the version comes from the banner's own words; conda-forge builds ngspice for x64 only, so macOS and
    /// Linux arm64 say how to get one by hand, and Windows arm64 installs the x64 archive, which says it runs under emulation.</summary>
    [Fact]
    public void Discovery_ReadsTheBanner_AndEachPlatformHasARecipeOrSaysWhy()
    {
        Assert.Equal("47", SolverDiscovery.ParseVersion(SolverTool.Ngspice, Banner47)!.Version);
        Assert.Null(SolverDiscovery.ParseVersion(SolverTool.Ngspice, "4.15.2"));   // a Gmsh is not an ngspice
        Assert.Equal(SolverTool.Ngspice, SolverHomes.ToolFromId("ngspice"));
        Assert.Contains(SolverTool.Ngspice, CircuitSolvers.AllTools);

        SolverRecipe? Recipe(RecipePlatform p, RecipeArchitecture a) => SolverRecipes.For(SolverTool.Ngspice, null, p, a, SolverRecipes.All);
        foreach (var (p, a) in new[] { (RecipePlatform.Windows, RecipeArchitecture.X64), (RecipePlatform.MacOS, RecipeArchitecture.X64),
                                       (RecipePlatform.Linux, RecipeArchitecture.X64) })
            Assert.False(Assert.IsType<SolverRecipe>(Recipe(p, a)).Emulated);
        var arm = Assert.IsType<SolverRecipe>(Recipe(RecipePlatform.Windows, RecipeArchitecture.Arm64));
        Assert.Contains("emulation", arm.EmulationNote);
        Assert.Null(Recipe(RecipePlatform.MacOS, RecipeArchitecture.Arm64));
        Assert.Null(Recipe(RecipePlatform.Linux, RecipeArchitecture.Arm64));
        Assert.Contains("conda-forge publishes no ngspice", SolverRecipes.ByHand(SolverTool.Ngspice));
        // every download of every ngspice recipe is checksummed by circuitRF or names who checks it
        foreach (var r in SolverRecipes.All.Where(r => r.Tool == SolverTool.Ngspice))
            Assert.All(r.Sources, s => Assert.True(s.Sha256 is not null || s.ChecksumNote is { Length: > 0 }, $"{r.Id}: {s.Url}"));
    }

    /// <summary>The deck writes a device's DRAWN terminals, which the elaborator expands into internal pins: a MOS's gate, a BJT's
    /// collector with Rb/Re/Rc and a diode's cathode with Rs were written as internal pins before TRN-7's references caught it.</summary>
    [Fact]
    public void Deck_WritesEachDevicesDrawnTerminals()
    {
        var (lib, tb) = new CnlReader().Read("""
            Vdc:V1 vdd 0 Vdc=3
            R:RL vdd d R=1 kOhm
            MOS1_N:M1 d g s b Vto=0.7 Kp=1e-4 W=2e-6 L=1e-6 Rd=1 Rs=2
            BJT_NPN:Q1 c bb e Rb=10 Re=1 Rc=5
            Diode:D1 a k Rs=3
            R:R1 g 0 R=1
            R:R2 s 0 R=1
            R:R3 b 0 R=1
            R:R4 c vdd R=1
            R:R5 bb 0 R=1
            R:R6 e 0 R=1
            R:R7 a vdd R=1
            R:R8 k 0 R=1
            analysis TR1 type=tran Stop=1n
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var deck = SpiceDeckWriter.Write(new SpiceDeckRequest { Netlist = nl, Analysis = tb.Analyses.OfType<TransientAnalysis>().Single() });
        Assert.True(deck.Ok, string.Join("; ", deck.Refusals.Select(r => r.Message)));
        string Terminals(string inst) => string.Join(' ', deck.Text!.Split('\n').Single(l => l.StartsWith(inst + " ")).Split(' ').Skip(1).TakeWhile(w => !w.StartsWith("m_")));
        Assert.Equal("d g s b", Terminals("M1"));
        Assert.Equal("c bb e", Terminals("Q1"));
        Assert.Equal("a k", Terminals("D1"));
    }

    private const string RcBench = """
        V_Pulse:VS in 0 V1=0 V2=1 Delay=0.1u Rise=1n Fall=1n Width=10u Period=40u
        R:R1 in out R=1 kOhm
        C:C1 out 0 C=1 nF
        IProbe:IP1 out x
        R:R2 x 0 R=1 MOhm
        analysis TR1 type=tran Stop=5u Step=10n RelTol=1e-5
        """;

    private static (DataSet Data, ElaboratedNetlist Nl) Run(string cnl, string solver, string? outputDir, RunControl? control = null)
    {
        var (lib, tb) = new CnlReader().Read(cnl.Replace("type=tran ", $"type=tran Solver={solver} "));
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals);
        return (TransientRunner.Run(p, nl, AnalysisSettings.Default.WithRunFiles(outputDir, "rc", "test"), control), nl);
    }

    private static double[] Net(DataSet ds, string net)
    {
        var v = ds["V"];
        int n = Array.IndexOf(v.Axes[1].Labels!.ToArray(), net);
        Assert.True(n >= 0, $"net {net} not recorded");
        int nn = v.Axes[1].Values.Length;
        return [.. Enumerable.Range(0, v.Axes[0].Values.Length).Select(k => v.RealValues[k * nn + n])];
    }

    /// <summary>Gate 2 — the RC with Solver=ngspice: the native run's cubes on the same grid; the output follows the closed form
    /// (the 1 MΩ load included) within ngspice's RelTol 1e-5 of the swing, and equals the native run within 1e-4 of it (two
    /// adaptive steppers about the 1 ns edge). The deck, the version and the probe's current are in the result.</summary>
    [NgspiceFact]
    public void Live_RcEqualsTheClosedFormAndTheNativeRun()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-ngspice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (ng, nl) = Run(RcBench, "ngspice", dir);
            var (nat, _) = Run(RcBench, "native", null);
            Assert.Equal(nat["V"].Axes[0].Values, ng["V"].Axes[0].Values);
            Assert.Equal(nat["V"].Axes[1].Labels, ng["V"].Axes[1].Labels);
            foreach (string cube in new[] { "I", "P", "Vprobe", "OP.V", "Tran.AcceptedSteps" }) Assert.True(ng.Contains(cube), cube);
            var prov = NgspiceRunner.Provenance(ng);
            Assert.Contains(prov, l => l.StartsWith("Ngspice.Version = "));
            Assert.True(File.Exists(prov.Single(l => l.StartsWith("Ngspice.Deck = "))["Ngspice.Deck = ".Length..]));

            double rTh = 1e3 * 1e6 / (1e3 + 1e6), vInf = 1e6 / (1e3 + 1e6), tau = rTh * 1e-9, t0 = 0.1e-6 + 0.5e-9;
            var t = ng["V"].Axes[0].Values;
            var vNg = Net(ng, "out");
            var vNat = Net(nat, "out");
            double closed = 0, native = 0;
            for (int k = 0; k < t.Length; k++)
            {
                if (t[k] < 0.2e-6) continue;   // past the edge, where the closed form's ideal step is the circuit
                closed = Math.Max(closed, Math.Abs(vNg[k] - vInf * (1 - Math.Exp(-(t[k] - t0) / tau))));
                native = Math.Max(native, Math.Abs(vNg[k] - vNat[k]));
            }
            output.WriteLine($"ngspice vs closed form {closed:E3} V, vs native {native:E3} V; {nl.Notes.Count} notes");
            Assert.True(closed < 1e-3, $"ngspice against the closed form: {closed:E3} V");
            Assert.True(native < 1e-4, $"ngspice against native: {native:E3} V");
            // the probe's current is the output's voltage across the 1 MΩ, flowing from its first net
            var i = ng["I"];
            Assert.Equal(vNg[^1] / 1e6, i.RealValues[^1], 9);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>Gate 3 — a long ngspice run cancelled through RunControl: the run ends as cancelled, no ngspice is left running
    /// and nothing is returned.</summary>
    [NgspiceFact]
    public async Task Live_CancelLeavesNoProcessAndNoResult()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-ngspice-" + Guid.NewGuid().ToString("N"));
        int before = Process.GetProcessesByName("ngspice").Length;
        using var cts = new CancellationTokenSource();
        var control = new RunControl { Token = cts.Token };
        // an RC asked for ten million steps: far longer than the test waits
        string longRun = RcBench.Replace("Stop=5u Step=10n RelTol=1e-5", "Stop=0.1 Step=10n MaxStep=10n");
        var run = Task.Run(() => Run(longRun, "ngspice", dir, control));
        await Task.Delay(1500);
        Assert.False(run.IsCompleted, "the long run finished before it was cancelled");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Task.Delay(200);
        Assert.Equal(before, Process.GetProcessesByName("ngspice").Length);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>Gate 4 — with discovery stubbed to find nothing: the card's ngspice choice is disabled with the Settings tooltip, a
    /// bench saying ngspice shows the note, and its run is refused with that sentence and the install verb, never run native. With
    /// discovery stubbed to find one, the same card's choice enables, with no re-opening.</summary>
    [Fact]
    public void NoNgspice_TheChoiceIsDisabled_AndTheRunIsRefused()
    {
        using (NgspiceRunner.Override(() => null))
        {
            var ex = Assert.Throws<TransientRefusedException>(() => Run(RcBench, "ngspice", null));
            Assert.Contains("Settings ▸ Solvers", ex.Message);
            Assert.Contains(NgspiceRunner.InstallVerb, ex.Message);
            Assert.Contains("does not fall back", ex.Message);
        }

        NgspiceAvailability.Set(null);
        var card = TranBodyViewModel.FromAnalysis(new TransientAnalysis("TR1") { StopExpr = "1u", Solver = TransientAnalysis.SolverNgspice });
        Assert.False(card.NgspiceAvailable);
        Assert.Equal(TranBodyViewModel.NgspiceMissing, card.NgspiceTooltip);
        Assert.Contains("Settings ▸ Solvers", card.SolverNote);
        Assert.True(card.SolverGreyed);
        Assert.Equal(1, card.SolverIndex);   // the bench's value is kept, not rewritten to native

        var changed = new List<string?>();
        ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        NgspiceAvailability.Set(new NgspiceProgram("/opt/ngspice/bin/ngspice", "47"));
        Assert.True(card.NgspiceAvailable);
        Assert.Contains(nameof(TranBodyViewModel.NgspiceAvailable), changed);
        Assert.Equal("ngspice 47", card.NgspiceLabel);
        Assert.Equal("", card.SolverNote);
        Assert.False(card.SolverGreyed);
        NgspiceAvailability.Set(null);
    }
}
