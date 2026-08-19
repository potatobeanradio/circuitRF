using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Engine;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The HB Analysis Setup dialog's tone LIST (harmonic-balance.md §6.5) — add/remove up to the
/// engine's maximum, and a lossless round trip at every tone count.
///
/// <para>The round-trip tests matter more than the add/remove ones: a dialog that drops or bakes
/// a tone expression produces an analysis that RUNS and answers for a different circuit, which is
/// the worst shape a defect can take. Every assertion here checks the raw expression survives,
/// not just the resolved frequency.</para>
/// </summary>
public class HbToneListTests(ITestOutputHelper output)
{
    private static SchematicEditModel Model() => new();

    /// <summary>A PnTone seeded exactly as the palette seeds one — two tone groups, shared Z.</summary>
    private static EditableComponent NewPnTone(string name)
    {
        var comp = new EditableComponent { InstanceName = name, Symbol = SymbolKind.PnTone, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.PnTone, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                Dimension = dp.Dimension, ShowOnSchematic = dp.ShowOnSchematic,
            });
        return comp;
    }

    private static HbBodyViewModel Multi(int tones)
    {
        var vm = new HbBodyViewModel(Model()) { MultiTone = true };
        while (vm.Tones.Count < tones) vm.AddToneCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void StartsSingleTone_WithExactlyOneRow()
    {
        var vm = new HbBodyViewModel(Model());
        Assert.True(vm.IsSingleTone);
        Assert.Single(vm.Tones);
        Assert.Equal("Tone (f₀)", vm.ToneLabel);

        // The named accessor and row 0 are ONE value, in both directions.
        vm.ToneCoeff = "RFfreq";
        Assert.Equal("RFfreq", vm.Tones[0].Coeff);
        vm.Tones[0].Coeff = "2.4";
        Assert.Equal("2.4", vm.ToneCoeff);
    }

    [Fact]
    public void AddTone_GrowsToTheEngineMaximum_ThenStops()
    {
        var vm = new HbBodyViewModel(Model());
        vm.SetMultiToneCommand.Execute(null);
        Assert.Equal(2, vm.Tones.Count);

        for (int i = 3; i <= HbBodyViewModel.MaxTones; i++)
        {
            Assert.True(vm.CanAddTone);
            vm.AddToneCommand.Execute(null);
            Assert.Equal(i, vm.Tones.Count);
            Assert.Equal($"Tone {i}", vm.Tones[i - 1].Caption);
        }

        // At the cap the command is disabled AND a further invocation is a no-op — the gate must
        // hold even if a binding somehow fires the command anyway.
        Assert.False(vm.CanAddTone);
        Assert.False(vm.AddToneCommand.CanExecute(null));
        vm.AddToneCommand.Execute(null);
        Assert.Equal(HbBodyViewModel.MaxTones, vm.Tones.Count);
        Assert.Equal(AnalysisSettings.Default.HbMaxTones, vm.Tones.Count);
        output.WriteLine($"capped at {vm.Tones.Count} tones: {vm.ToneCountSummary}");
    }

    [Fact]
    public void RemoveTone_NeverDropsBelowTwo_AndRenumbersTheRest()
    {
        var vm = Multi(4);
        vm.Tones[1].RemoveCommand.Execute(null);
        Assert.Equal(3, vm.Tones.Count);
        Assert.Equal(["Tone 1", "Tone 2", "Tone 3"], vm.Tones.Select(t => t.Caption));

        vm.Tones[2].RemoveCommand.Execute(null);
        Assert.Equal(2, vm.Tones.Count);

        // Two is the floor for a multi-tone analysis; the row commands go dead there.
        Assert.False(vm.Tones[0].CanRemoveSelf);
        Assert.False(vm.Tones[1].CanRemoveSelf);
        vm.Tones[1].RemoveCommand.Execute(null);
        Assert.Equal(2, vm.Tones.Count);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(6)]
    public void RoundTrip_PreservesEveryToneExpressionAndUnit(int tones)
    {
        var vm = Multi(tones);
        for (int i = 0; i < tones; i++)
        {
            vm.Tones[i].Unit  = "MHz";
            vm.Tones[i].Coeff = $"RFfreq + {i} * Spacing";   // an EXPRESSION, not a number
        }
        vm.MaxMixOrderExpr = "3";

        var hb = vm.BuildAnalysis("HB1", enabled: true);
        Assert.Equal(tones.ToString(), hb.NumFreqsExpr);
        Assert.Equal(tones, hb.ToneExprs.Length);
        Assert.Equal(tones, hb.ToneUnits.Length);

        // Tone 1 is mirrored into the scalar field for the consumers that read it.
        Assert.Equal(hb.ToneExprs[0], hb.ToneExpr);
        Assert.Equal(hb.ToneUnits[0], hb.ToneUnit);

        var back = HbBodyViewModel.FromAnalysis(hb, Model());
        Assert.True(back.IsMultiTone);
        Assert.Equal(tones, back.Tones.Count);
        for (int i = 0; i < tones; i++)
        {
            Assert.Equal($"RFfreq + {i} * Spacing", back.Tones[i].Coeff);
            Assert.Equal("MHz", back.Tones[i].Unit);
        }
        Assert.Equal("3", back.MaxMixOrderExpr);
    }

    [Fact]
    public void SingleToneRoundTrip_IsUnchangedByTheToneList()
    {
        // The list must not leak into the single-tone spelling: a single-tone analysis still
        // writes the scalar Tone=/ToneUnit= and NO NumFreqs/ToneExprs.
        var vm = new HbBodyViewModel(Model()) { ToneCoeff = "RFfreq", ToneUnit = "GHz" };
        var hb = vm.BuildAnalysis("HB1", enabled: true);

        Assert.Equal("RFfreq", hb.ToneExpr);
        Assert.Equal("GHz",    hb.ToneUnit);
        Assert.Empty(hb.ToneExprs);
        Assert.Equal("1", hb.NumFreqsExpr);

        var back = HbBodyViewModel.FromAnalysis(hb, Model());
        Assert.True(back.IsSingleTone);
        Assert.Single(back.Tones);
        Assert.Equal("RFfreq", back.ToneCoeff);
        Assert.Equal("GHz",    back.ToneUnit);
    }

    [Fact]
    public void ChangingAToneUnit_RescalesSoTheFrequencyIsUnchanged()
    {
        // Same contract FrequencySpecViewModel gives a sweep segment: picking a different unit is
        // a display change, not a retune.
        var vm = Multi(3);
        vm.Tones[2].Unit  = "GHz";
        vm.Tones[2].Coeff = "2";
        vm.Tones[2].Unit  = "MHz";
        Assert.Equal("2000", vm.Tones[2].Coeff);
    }

    [Fact]
    public void MixProductPreview_ShowsTheCount_AndFlagsTheOverCapCase()
    {
        var vm = Multi(6);

        vm.MaxMixOrderExpr = "3";
        output.WriteLine(vm.MixProductPreview);
        Assert.Contains("6 tones", vm.MixProductPreview);
        Assert.Contains("189", vm.MixProductPreview);
        Assert.DoesNotContain("OVER", vm.MixProductPreview);

        // 6 tones at the DEFAULT MaxMixOrder=5 is over the ceiling — the dialog must say so while
        // the user is authoring, not leave it to the engine's refusal at Run.
        vm.MaxMixOrderExpr = "5";
        output.WriteLine(vm.MixProductPreview);
        Assert.Contains("1,827", vm.MixProductPreview);
        Assert.Contains("OVER", vm.MixProductPreview);

        // A non-literal order can only be resolved at Run — say nothing rather than guess.
        vm.MaxMixOrderExpr = "MixOrder";
        Assert.Equal("", vm.MixProductPreview);

        // Single-tone has no mixing lattice at all.
        vm.MultiTone = false;
        Assert.Equal("", vm.MixProductPreview);
    }

    [Fact]
    public void ReadingToneAccessors_FromInsideAChangeNotification_NeverThrows()
    {
        // What a bound control does: on PropertyChanged it READS the property. The tone list is
        // rebuilt by CLEARING it and refilling, and Clear() raises the notification while the list
        // is momentarily EMPTY — so a bare Tones[0] accessor throws IndexOutOfRange in the running
        // dialog while every ordinary headless test passes, because nothing in a test subscribes.
        //
        // The reachable path is the ordinary one: the dialog is open (bindings live) and the user
        // clicks Multi with a PnTone on the schematic, so the tone list is replaced by the source's.
        // Subscribing BEFORE that click is what makes this test able to fail.
        var model = Model();
        var pn = NewPnTone("P1");
        pn.Parameters.First(p => p.Name == "Freq[1]").Expression = "f1";
        pn.Parameters.First(p => p.Name == "Freq[2]").Expression = "f2";
        pn.Parameters.Add(new EditableParameter { Name = "Freq[3]", Expression = "f3", Unit = "GHz" });
        model.Components.Add(pn);

        var vm = new HbBodyViewModel(model);

        int reads = 0;
        vm.PropertyChanged += (_, _) =>
        {
            // Touch every accessor a binding could be attached to, whatever the list looks like now.
            _ = vm.ToneCoeff; _ = vm.ToneUnit;  _ = vm.TonePreview;
            _ = vm.Tone2Coeff; _ = vm.Tone2Unit; _ = vm.Tone2Preview;
            _ = vm.MixProductPreview; _ = vm.ToneCountSummary;
            reads++;
        };

        vm.SetMultiToneCommand.Execute(null);        // → AdoptPnToneTones → SetTones → Clear()

        Assert.Equal(3, vm.Tones.Count);
        Assert.Equal(["f1", "f2", "f3"], vm.Tones.Select(t => t.Coeff));
        Assert.True(reads > 0, "no notification was raised — the test would pass vacuously");
        output.WriteLine($"{reads} notifications read back cleanly across the list rebuild");
    }

    [Fact]
    public void SwitchingToMulti_AdoptsEveryToneFromAPnToneOnTheSchematic()
    {
        var model = Model();
        var pn = NewPnTone("P1");

        // A four-tone source: tones 3 and 4 added beyond the seeded pair.
        foreach (var (name, expr, unit) in new[]
                 {
                     ("Freq[3]", "f3", "GHz"),
                     ("Freq[4]", "f4", "GHz"),
                 })
            pn.Parameters.Add(new EditableParameter { Name = name, Expression = expr, Unit = unit });

        pn.Parameters.First(p => p.Name == "Freq[1]").Expression = "f1";
        pn.Parameters.First(p => p.Name == "Freq[2]").Expression = "f2";
        model.Components.Add(pn);

        var vm = new HbBodyViewModel(model);
        vm.SetMultiToneCommand.Execute(null);

        Assert.Equal(4, vm.Tones.Count);
        Assert.Equal(["f1", "f2", "f3", "f4"], vm.Tones.Select(t => t.Coeff));
    }

    [Fact]
    public void PlacingAPnTone_WithAThreeToneHb_CreatesTheMissingToneRows()
    {
        // The complement of the test above, and the one that catches a real mismatch: a freshly
        // placed PnTone carries only two tone groups, so a 3+ tone analysis needs Freq[3] CREATED.
        // Without that the source drives two tones while the analysis declares three, and the only
        // symptom is a commensurability error at Run.
        var model = Model();
        model.Analyses.Add(new HarmonicBalanceAnalysis("HB1")
        {
            NumFreqsExpr = "3",
            ToneExprs    = ["fa", "fb", "fc"],
            ToneUnits    = ["GHz", "GHz", "GHz"],
        });

        var pn = NewPnTone("P1");
        Assert.DoesNotContain(pn.Parameters, p => p.Name == "Freq[3]");

        SchematicViewModel.AdoptHbTonesIntoPnTone(pn, model);

        Assert.Equal("fa", pn.Parameters.First(p => p.Name == "Freq[1]").Expression);
        Assert.Equal("fb", pn.Parameters.First(p => p.Name == "Freq[2]").Expression);
        Assert.Equal("fc", pn.Parameters.First(p => p.Name == "Freq[3]").Expression);

        // The whole group is created, not just the frequency — a tone with no Pavl drives nothing.
        Assert.Contains(pn.Parameters, p => p.Name == "Pavl[3]");
        Assert.Contains(pn.Parameters, p => p.Name == "Phase[3]");
        Assert.Equal(pn.Parameters.First(p => p.Name == "Pavl[1]").Expression,
                     pn.Parameters.First(p => p.Name == "Pavl[3]").Expression);
    }
}
