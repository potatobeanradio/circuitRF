using System;
using System.IO;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

// File ▸ New Schematic / New Layout with a workspace open make a new CELL holding that view, through
// New Cell's own creation — owner decision, 2026-09-24, after a designer ended up with loose documents
// under Known Files that nothing could simulate, compare or place. With no workspace they stay scratch.
public sealed class NewViewMakesACellTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf_newview_" + Guid.NewGuid().ToString("N")[..8]);

    public NewViewMakesACellTests()
    {
        Directory.CreateDirectory(_ws);
        WorkspacePersistence.SaveToFile(Path.Combine(_ws, ".cws"), new CwsFile());
        WorkspaceRootFinder.InvalidateCache();
    }

    public void Dispose()
    {
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_ws, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(ViewType.Schematic, "schematic", ".csch")]
    [InlineData(ViewType.Layout,    "layout",    ".clay")]
    public async System.Threading.Tasks.Task WithAWorkspaceOpen_ANewViewIsACellHoldingIt(
        ViewType view, string subFolder, string ext)
    {
        var vm = new WorkspaceViewModel { CurrentWorkspacePath = Path.Combine(_ws, ".cws") };

        string? cell = await vm.CreateCellHoldingViewAsync("Amp", view);

        Assert.Equal(Path.Combine(_ws, "Amp"), cell);
        Assert.True(File.Exists(Path.Combine(_ws, "Amp", CellFolder.CcellFileName)));
        Assert.True(File.Exists(Path.Combine(_ws, "Amp", subFolder, "Amp" + ext)));
    }
}
