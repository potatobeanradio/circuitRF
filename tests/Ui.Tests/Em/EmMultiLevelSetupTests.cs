using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// <b>Phase L9d / M4 — the Ui half: which levels are in the analysis, and that saying so
/// round-trips.</b>
/// </summary>
public sealed class EmMultiLevelSetupTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    [Fact]
    public void ACemThatNamesNoLevels_RoundTripsByteIdentically()
    {
        // D5's additivity rule, the same one PortZ0s already follows: a setup that never named its
        // levels writes no field, so every .cem written before L9d loads AND re-serialises unchanged.
        var setup = new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar };
        string json = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("AnalysisLevelNames", json);

        var back = EmSetupPersistence.Deserialize(json);
        Assert.Empty(back.AnalysisLevelNames);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));
    }

    [Fact]
    public void NamedLevels_RoundTripInOrder()
    {
        var setup = new EmSetup
        {
            Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar,
            AnalysisLevelNames = ["Metal1", "Metal2"],
        };
        var back = EmSetupPersistence.Deserialize(EmSetupPersistence.Serialize(setup));
        Assert.Equal(["Metal1", "Metal2"], back.AnalysisLevelNames);

        // …and they reach the extractor through the ONE place the two are married, so the panel and
        // the run service cannot disagree about them.
        Assert.Equal(["Metal1", "Metal2"], back.ToExtractionSettings().AnalysisLevelNames);
    }

    [Fact]
    public void AnalysisLevelRows_ListEverySignalConductor_AndToggleCommitsUndoably()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var vm = new EmSetupEditorViewModel(
            Path.Combine(Path.GetTempPath(), "unused-l9d.cem"),
            new EmSetup { Name = "x", LayoutRef = "a.clay", AnalysisKind = EmAnalysisKind.Planar })
        {
            ResolveLayout = _ => new EmLayoutSource("a.clay", new LayoutView { DbuPerMicron = Dbu }, tech, Dbu),
        };
        vm.Refresh();

        var signals = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference)
            .Select(l => l.Name).ToHashSet();
        Assert.Equal(signals, vm.AnalysisLevelRows.Select(r => r.Name).ToHashSet());
        Assert.All(vm.AnalysisLevelRows, r => Assert.False(r.IsIncluded));   // none = infer

        var row = vm.AnalysisLevelRows[0];
        row.IsIncluded = true;
        Assert.Contains(row.Name, vm.Working.AnalysisLevelNames);
        Assert.True(vm.IsDirty);

        vm.UndoCommand.Execute(null);
        Assert.DoesNotContain(row.Name, vm.Working.AnalysisLevelNames);

        vm.RedoCommand.Execute(null);
        Assert.Contains(row.Name, vm.Working.AnalysisLevelNames);
    }
}
