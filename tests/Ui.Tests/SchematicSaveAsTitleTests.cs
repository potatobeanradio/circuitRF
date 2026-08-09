using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report: after "Save Schematic As…", the document tab still showed the old name.
///
/// Two causes, both covered here. A <see cref="SchematicDocument"/> — unlike
/// <c>LayoutDocument</c>/<c>SymbolEditorDocument</c>, which subscribe to their own view model's
/// path property — has no path-to-title link at all, so its title only moves when a caller says so.
/// The already-materialized branch did (<c>OnSavedAs</c>); the scratch branch called
/// <c>Materialize(filePath)</c> with no name and left the tab reading "Untitled-Schematic-N".
/// Separately, <c>Materialize</c> refreshed the title only as a side effect of the dirty flag
/// changing, so a rename of an already-clean document was silently dropped.
/// </summary>
public sealed class SchematicSaveAsTitleTests
{
    private static SchematicDocument NewScratch(string title = "Untitled-Schematic-1")
        => new(title, new SchematicViewModel(new SchematicEditModel(), messageSink: null));

    [Fact]
    public void MaterializingAScratchDocumentWithAName_RetitlesTheTab()
    {
        var doc = NewScratch();
        Assert.Equal("Untitled-Schematic-1", doc.Title);

        doc.Materialize("/tmp/amp/schematic/Amp.csch", "Amp");

        Assert.Equal("Amp", doc.Title);
        Assert.Equal("Amp", doc.Id);
        Assert.False(doc.IsScratch);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void MaterializingWithNoName_LeavesTheTitleAlone()
    {
        // The SavePlanExecutor path deliberately supplies the CELL name rather than the file stem,
        // and the plain re-save-in-place path (`doc.Materialize(doc.FilePath)`, used only to clear
        // the dirty flag) supplies neither. Both must keep working.
        var doc = NewScratch();

        doc.Materialize("/tmp/amp/schematic/Amp.csch");

        Assert.Equal("Untitled-Schematic-1", doc.Title);
        Assert.False(doc.IsScratch);
    }

    [Fact]
    public void RenamingAnAlreadyCleanDocument_StillRefreshesTheTitle()
    {
        // The regression this one guards: Materialize used to refresh the title only as a side
        // effect of IsDirty changing, and IsDirty's setter returns early when the value is unchanged.
        var doc = NewScratch();
        doc.Materialize("/tmp/a/A.csch", "A");
        Assert.False(doc.IsDirty);          // already clean — the flag cannot change again

        doc.Materialize("/tmp/b/B.csch", "B");

        Assert.Equal("B", doc.Title);
        Assert.Equal("B", doc.Id);
    }

    [Fact]
    public void OnSavedAs_RetitlesTheTab_AndIsRepeatable()
    {
        var doc = NewScratch();
        doc.Materialize("/tmp/a/A.csch", "A");

        doc.OnSavedAs("/tmp/b/B.csch", "B");
        Assert.Equal("B", doc.Title);

        doc.OnSavedAs("/tmp/c/C.csch", "C");
        Assert.Equal("C", doc.Title);
        Assert.Equal("/tmp/c/C.csch", doc.FilePath);
    }

    [Fact]
    public void ADirtyRenamedDocument_KeepsItsDirtyBullet()
    {
        var doc = NewScratch();
        doc.Materialize("/tmp/a/A.csch", "A");
        doc.ViewModel.EditModel.Components.Add(new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
        });
        doc.ViewModel.Execute(new NoOpDirtyingCommand(doc.ViewModel.EditModel));
        Assert.True(doc.IsDirty);

        doc.OnSavedAs("/tmp/b/B.csch", "B");

        // OnSavedAs does not clear dirty for a schematic (unlike layout/symbol, which mark saved) —
        // the caller writes the file first. What matters here is that the NAME moved with the bullet.
        Assert.Equal("• B", doc.Title);
    }

    /// <summary>
    /// The Save-As command must act on the document in front of the user. It previously fell back to
    /// "the first dirty scratch document" whenever the active one merely wasn't scratch, which
    /// re-targeted a Save As on a materialized schematic at an unrelated tab.
    /// <c>WorkspaceViewModel</c> cannot be constructed headlessly (its constructor stands up a Dock
    /// layout and posts to the UI thread), so the resolution is pinned by reading the source — the
    /// same approach this suite already uses for other WorkspaceViewModel-only logic.
    /// </summary>
    [Fact]
    public void SaveLooseSchematic_TargetsTheActiveDocument_NotAnUnrelatedScratchTab()
    {
        string src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        int at = src.IndexOf("private async Task SaveLooseSchematic", System.StringComparison.Ordinal);
        Assert.True(at > 0, "SaveLooseSchematic not found.");
        string body = src[at..(at + 1600)];

        Assert.Contains("ResolveActiveDocumentForCommands() as SchematicDocument", body);
        Assert.Contains("?? _scratchDocs.FirstOrDefault(d => d.IsDirty)", body);
        Assert.DoesNotContain("if (doc is null || !doc.IsScratch)", body);
    }

    [Fact]
    public void BothScratchSavePaths_PassTheFileStemAsTheNewTitle()
    {
        string src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");

        // Scoped to the two SCHEMATIC loose-save tiers by name. The layout and symbol scratch-save
        // paths deliberately call Materialize(filePath) with no name — those document types DO
        // subscribe to their own view model's path and retitle themselves.
        foreach (var method in new[] { "SaveLooseToWorkspace", "SaveLoosePlainFile" })
        {
            int at = src.IndexOf($"private async Task {method}(SchematicDocument", System.StringComparison.Ordinal);
            Assert.True(at > 0, $"{method} not found.");
            string body = src[at..(at + 2200)];

            Assert.False(Regex.IsMatch(body, @"doc\.Materialize\(filePath\)\s*;"),
                $"{method} still calls Materialize(filePath) with no name — the tab will keep its " +
                "Untitled title after Save As.");
            Assert.Matches(@"doc\.Materialize\(filePath,\s*Path\.GetFileNameWithoutExtension\(filePath\)\)", body);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ReadRepoFile(string relative)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// <summary>Minimal undoable edit — enough to make the document report dirty.</summary>
    private sealed class NoOpDirtyingCommand(SchematicEditModel model) : Commands.IUiCommand
    {
        private readonly EditableNetLabel _label = new() { Name = "n1", X = 0, Y = 0 };

        public string Description => "test edit";
        public void Execute() { model.NetLabels.Add(_label); model.NotifyChanged(); }
        public void Undo()    { model.NetLabels.Remove(_label); model.NotifyChanged(); }
    }
}
