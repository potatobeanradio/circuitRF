using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Bidirectional tone sync between a PnTone source and a two-tone HB analysis:
///   A) clicking "Multi" in the HB dialog adopts the PnTone's Freq[i] into the dialog tones;
///   B) placing a PnTone when a two-tone HB already exists adopts the HB tones into the PnTone.
/// Both preserve vars/expressions and fail gracefully when frequencies aren't available.
/// </summary>
public sealed class PnToneHbToneSyncTests
{
    private static EditableComponent PnTone(string f1, string f2, string u = "GHz")
    {
        var c = new EditableComponent { InstanceName = "Pd", Symbol = SymbolKind.PnTone };
        c.Parameters.Add(new EditableParameter { Name = "Freq[1]", Expression = f1, Unit = u });
        c.Parameters.Add(new EditableParameter { Name = "Pavl[1]", Expression = "0", Unit = "dBm" });
        c.Parameters.Add(new EditableParameter { Name = "Freq[2]", Expression = f2, Unit = u });
        c.Parameters.Add(new EditableParameter { Name = "Pavl[2]", Expression = "0", Unit = "dBm" });
        c.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50", Unit = "Ω" });
        return c;
    }

    private static HarmonicBalanceAnalysis TwoToneHb(string t1, string t2, string u = "GHz") =>
        new("HB1") { NumFreqsExpr = "2", ToneExprs = [t1, t2], ToneUnits = [u, u] };

    // ── Direction A: Multi-click adopts PnTone tones into the dialog (vars preserved) ──
    [Fact]
    public void ClickMulti_AdoptsPnToneFrequencies()
    {
        var model = new SchematicEditModel();
        model.Components.Add(PnTone("RFf1", "RFf2"));

        var vm = new HbBodyViewModel(model);
        vm.SetMultiToneCommand.Execute(null);

        Assert.True(vm.MultiTone);
        Assert.Equal("RFf1", vm.ToneCoeff);
        Assert.Equal("GHz",  vm.ToneUnit);
        Assert.Equal("RFf2", vm.Tone2Coeff);
        Assert.Equal("GHz",  vm.Tone2Unit);
    }

    [Fact]
    public void ClickMulti_NoPnTone_IsGracefulNoOp()
    {
        var vm = new HbBodyViewModel(new SchematicEditModel());
        string defaultTone = vm.ToneCoeff;
        vm.SetMultiToneCommand.Execute(null);

        Assert.True(vm.MultiTone);            // still switches to multi
        Assert.Equal(defaultTone, vm.ToneCoeff);   // tones unchanged (graceful)
    }

    // ── Direction B: placing a PnTone adopts an existing two-tone HB's tones (vars preserved) ──
    [Fact]
    public void PlacePnTone_WithTwoToneHb_AdoptsHbTones()
    {
        var model = new SchematicEditModel();
        model.Analyses.Add(TwoToneHb("Fa", "Fb"));

        var pn = PnTone("1.99", "2.01");   // seeded defaults
        SchematicViewModel.AdoptHbTonesIntoPnTone(pn, model);

        Assert.Equal("Fa", pn.Parameters.First(p => p.Name == "Freq[1]").Expression);
        Assert.Equal("Fb", pn.Parameters.First(p => p.Name == "Freq[2]").Expression);
        Assert.Equal("GHz", pn.Parameters.First(p => p.Name == "Freq[1]").Unit);
    }

    [Fact]
    public void PlacePnTone_NoTwoToneHb_KeepsSeededDefaults()
    {
        var model = new SchematicEditModel();
        model.Analyses.Add(new HarmonicBalanceAnalysis("HB1") { ToneExpr = "2", ToneUnit = "GHz" }); // single-tone

        var pn = PnTone("1.99", "2.01");
        SchematicViewModel.AdoptHbTonesIntoPnTone(pn, model);

        Assert.Equal("1.99", pn.Parameters.First(p => p.Name == "Freq[1]").Expression);  // untouched
        Assert.Equal("2.01", pn.Parameters.First(p => p.Name == "Freq[2]").Expression);
    }
}
