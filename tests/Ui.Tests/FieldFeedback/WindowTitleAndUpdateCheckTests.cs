// WindowTitleAndUpdateCheckTests.cs — field feedback, 2026-10-15: the window title shows when any
// document is unsaved, and a manual update check that finds nothing still answers in a dialog.

using System;
using System.IO;
using System.Threading.Tasks;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.FieldFeedback;

[Collection(UserStateDirectoryCollection.Name)]
public sealed class WindowTitleAndUpdateCheckTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-dirty-title-" + Guid.NewGuid().ToString("N")[..12]);

    public WindowTitleAndUpdateCheckTests()
    {
        Directory.CreateDirectory(_tmp);
        AppDataRoot.RedirectTo(Path.Combine(_tmp, "state"));
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
    }

    private sealed class NoopEdit : IUiCommand
    {
        public string Description => "Edit";
        public void Execute() { }
        public void Undo() { }
    }

    [Fact]
    public async Task TheWindowTitleCarriesTheDirtyMark_WhileAnyDocumentIsUnsaved()
    {
        var vm = new WorkspaceViewModel();
        await vm.NewScratchSchematicCommand.ExecuteAsync(null);
        Assert.Equal("circuitRF", vm.WindowTitle);

        var doc = vm.ActiveSchematicDocument!;
        doc.UndoRedo.Execute(new NoopEdit());
        Assert.Equal(WorkspaceViewModel.DirtyMark + "circuitRF", vm.WindowTitle);

        doc.UndoRedo.Undo();
        Assert.Equal("circuitRF", vm.WindowTitle);
    }

    [Fact]
    public void AManualCheckAnswersInADialog_ExceptForCancelAndAFinishedDownload()
    {
        var upToDate = WorkspaceViewModel.DescribeManualUpdateCheck(
            new CircuitRF.Ui.Updates.CheckResult(CircuitRF.Ui.Updates.CheckOutcome.UpToDate), null);
        Assert.NotNull(upToDate);
        Assert.Contains("up to date", upToDate!.Value.Heading, StringComparison.Ordinal);

        Assert.NotNull(WorkspaceViewModel.DescribeManualUpdateCheck(null, "no route to host"));
        Assert.Null(WorkspaceViewModel.DescribeManualUpdateCheck(
            new CircuitRF.Ui.Updates.CheckResult(CircuitRF.Ui.Updates.CheckOutcome.Cancelled), null));
        Assert.Null(WorkspaceViewModel.DescribeManualUpdateCheck(
            new CircuitRF.Ui.Updates.CheckResult(CircuitRF.Ui.Updates.CheckOutcome.Staged), null));
    }
}
