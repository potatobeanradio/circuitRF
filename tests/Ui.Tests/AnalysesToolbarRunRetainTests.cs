using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for brief-analyses-toolbar-run-retain:
/// - RunCommand.CanExecute reflects active schematic state
/// - RunCommand fires RunRequested event
/// </summary>
public sealed class AnalysesToolbarRunRetainTests
{
    private static SchematicViewModel MakeSchVm()
    {
        var model = new SchematicEditModel();
        model.Analyses.Add(new DcAnalysis("DC1"));
        return new SchematicViewModel(model, messageSink: null);
    }

    private static AnalysesListViewModel MakeListVm() => new AnalysesListViewModel();

    [Fact]
    public void RunCommand_CanExecute_FalseWhenNoSchematic()
    {
        var listVm = MakeListVm();
        Assert.False(listVm.RunCommand.CanExecute(null));
    }

    [Fact]
    public void RunCommand_CanExecute_TrueAfterSetActiveSchematic()
    {
        var listVm = MakeListVm();
        listVm.SetActiveSchematic(MakeSchVm(), "test.csch");
        Assert.True(listVm.RunCommand.CanExecute(null));
    }

    [Fact]
    public void RunCommand_Execute_FiresRunRequested()
    {
        var listVm = MakeListVm();
        listVm.SetActiveSchematic(MakeSchVm(), "test.csch");

        bool fired = false;
        listVm.RunRequested += () => fired = true;

        listVm.RunCommand.Execute(null);

        Assert.True(fired);
    }

    [Fact]
    public void RunCommand_CanExecute_FalseAfterClearSchematic()
    {
        var listVm = MakeListVm();
        listVm.SetActiveSchematic(MakeSchVm(), "test.csch");
        Assert.True(listVm.RunCommand.CanExecute(null));

        listVm.SetActiveSchematic(null);
        Assert.False(listVm.RunCommand.CanExecute(null));
    }
}
