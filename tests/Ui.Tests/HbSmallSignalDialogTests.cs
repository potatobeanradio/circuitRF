using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The HB Analysis Setup dialog's <b>Small-signal</b> group — brief-wsprobe-5 R-wsp5-1: the
/// probe-tickle sweep spelled like the S-parameter one, its live point count, and the WSProbe margin
/// threshold that on an HB analysis is read over that sweep and nowhere else.
///
/// <para>The load-bearing assertion is the <b>empty</b> one. Absent <c>SSStart</c>/<c>SSStop</c> means
/// no small-signal solve at all and a run byte-identical to one from before WSP-5, so the dialog must
/// author nothing when the user has typed nothing — a plausible default of "0.1 .. 10 GHz" would
/// silently add a probe sweep to every HB analysis anyone opened, at a cost of (points × sidebands)
/// linear solves per operating point.</para>
/// </summary>
public class HbSmallSignalDialogTests(ITestOutputHelper output)
{
    private static SchematicEditModel Model() => new();

    [Fact]
    public void ANewAnalysisAuthorsNoSmallSignalSweep()
    {
        var vm = new HbBodyViewModel(Model());
        Assert.Equal("", vm.SsStartExpr);
        Assert.Equal("", vm.SsStopExpr);
        Assert.False(vm.SmallSignalExpanded);
        Assert.Contains("no small-signal sweep", vm.SmallSignalPreview);

        var hb = vm.BuildAnalysis("HB1", enabled: true);
        Assert.False(hb.HasSsSweep);
        Assert.Null(hb.SsSweep());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSweepRoundTripsThroughBuildAndFromAnalysis(bool multiTone)
    {
        var vm = new HbBodyViewModel(Model()) { MultiTone = multiTone };
        if (multiTone) while (vm.Tones.Count < 2) vm.AddToneCommand.Execute(null);

        vm.SsStartExpr         = "0.1";
        vm.SsStopExpr          = "fmax";           // an expression, not a number
        vm.SsStepExpr          = "0.05";
        vm.SsNptsExpr          = "991";
        vm.SsUnit              = "GHz";
        vm.SsLog               = true;
        vm.SsMaxHarmExpr       = "3";
        vm.MarginThresholdExpr = "none";

        var hb = vm.BuildAnalysis("HB1", enabled: true);
        Assert.True(hb.HasSsSweep);
        Assert.Equal("0.1",  hb.SsStartExpr);
        Assert.Equal("fmax", hb.SsStopExpr);       // NOT baked to Hz — the expression survives
        Assert.Equal("0.05", hb.SsStepExpr);
        Assert.Equal("991",  hb.SsNptsExpr);
        Assert.Equal("GHz",  hb.SsUnit);
        Assert.Equal("true", hb.SsLogExpr);
        Assert.Equal("3",    hb.SsMaxHarmExpr);
        Assert.Equal("none", hb.MarginThresholdExpr);

        // Npts wins over Step, and the sweep is logarithmic — the FrequencySpec, not a second parser.
        var spec = hb.SsSweep();
        Assert.NotNull(spec);
        Assert.Equal(991, spec.NumPoints);
        Assert.Equal(SweepKind.Log, spec.Kind);
        Assert.Equal("GHz", spec.StartUnit);
        Assert.Equal("GHz", spec.StopUnit);

        var back = HbBodyViewModel.FromAnalysis(hb, Model());
        Assert.Equal("0.1",  back.SsStartExpr);
        Assert.Equal("fmax", back.SsStopExpr);
        Assert.Equal("0.05", back.SsStepExpr);
        Assert.Equal("991",  back.SsNptsExpr);
        Assert.Equal("GHz",  back.SsUnit);
        Assert.True(back.SsLog);
        Assert.Equal("3",    back.SsMaxHarmExpr);
        Assert.Equal("none", back.MarginThresholdExpr);
        Assert.True(back.SmallSignalExpanded);     // an existing sweep is visible on open
    }

    /// <summary>
    /// The live point count (R-wsp5-1) — the same readout the mixing-product count gets, and for the
    /// same reason: the cost of this analysis is (probe frequencies × sidebands) linear solves per
    /// operating point, so the number that sets it belongs next to the knob rather than arriving as a
    /// long run.
    /// </summary>
    [Fact]
    public void ThePointCountIsLive()
    {
        var vm = new HbBodyViewModel(Model()) { SsStartExpr = "0.1", SsStopExpr = "10", SsUnit = "GHz" };
        vm.SsNptsExpr = "991";
        output.WriteLine(vm.SmallSignalPreview);
        Assert.Contains("991", vm.SmallSignalPreview);
        Assert.DoesNotContain("logarithmic", vm.SmallSignalPreview);

        vm.SsLog = true;
        Assert.Contains("logarithmic", vm.SmallSignalPreview);

        vm.SsNptsExpr = "1";
        Assert.Contains("1 frequency:", vm.SmallSignalPreview);

        // Clearing either end returns the group to "no sweep" — the state a run is byte-identical in.
        vm.SsStopExpr = "";
        Assert.Contains("no small-signal sweep", vm.SmallSignalPreview);
        Assert.False(vm.BuildAnalysis("HB1", true).HasSsSweep);
    }

    /// <summary>
    /// A document written before WSP-5 reads back with no sweep and the published margin default, so
    /// nothing about it changes — the dialog half of R-wsp5-9(a).
    /// </summary>
    [Fact]
    public void AnAnalysisFromBeforeWsp5OpensUnchanged()
    {
        var old = new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2", ToneUnit = "GHz" };
        var vm  = HbBodyViewModel.FromAnalysis(old, Model());

        Assert.Equal("", vm.SsStartExpr);
        Assert.Equal("", vm.SsStopExpr);
        Assert.False(vm.SsLog);
        Assert.False(vm.SmallSignalExpanded);
        Assert.Equal(Analysis.MarginThresholdDefault, vm.MarginThresholdExpr);

        var rebuilt = vm.BuildAnalysis("HB1", enabled: true);
        Assert.False(rebuilt.HasSsSweep);
        Assert.Equal(Analysis.MarginThresholdDefault, rebuilt.MarginThresholdExpr);
    }

    /// <summary>
    /// Brief NZ-5: the noise keys survive the dialog — a port the analysis names is in the picker before it is selected, a
    /// loaded spelling is kept while its box is untouched, and an analysis without noise gains no key.
    /// </summary>
    [Fact]
    public void TheNoiseKeysRoundTrip_AndAnAnalysisWithoutThemGainsNone()
    {
        var hb = new HarmonicBalanceAnalysis("HB1")
        {
            ToneExpr = "1", ToneUnit = "GHz", SsStartExpr = "0.1", SsStopExpr = "0.3", SsNptsExpr = "3", SsUnit = "GHz",
            NoiseExpr = "true", NoiseInExpr = "3", NoiseOutExpr = "4", NoiseSidebandExpr = "-1", NoiseContribExpr = "yes",
        };
        var vm = HbBodyViewModel.FromAnalysis(hb, Model());
        Assert.Contains("3", vm.PortChoices);
        Assert.Contains("4", vm.PortChoices);
        Assert.Contains("image at f_out + f0", vm.NoiseSidebandPreview);
        var back = vm.BuildAnalysis("HB1", true);
        Assert.Equal(("true", "3", "4", "-1", "yes"),
                     (back.NoiseExpr, back.NoiseInExpr, back.NoiseOutExpr, back.NoiseSidebandExpr, back.NoiseContribExpr));

        var plain = HbBodyViewModel.FromAnalysis(new HarmonicBalanceAnalysis("HB2") { ToneExpr = "1", ToneUnit = "GHz" }, Model())
                                   .BuildAnalysis("HB2", true);
        Assert.Equal(Analysis.NoiseDefault, plain.NoiseExpr);
        Assert.DoesNotContain("Noise", CircuitRF.Core.Netlist.CnlWriter.Write(new TestBench("tb") { Analyses = { plain } }));
    }
}
