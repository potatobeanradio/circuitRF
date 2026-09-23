// Reusing a part library across workspaces (field report, 2026-09-23): a team that buys the same
// part numbers board after board was rebuilding the `.crlib` from nothing in every workspace.
//
// Two routes, one test each for what they claim:
//  - COPY: the library editor's Import takes another `.crlib` and merges its rows (PartLibraryMerge),
//    and the parts pane's Use existing library… creates this design's library from one.
//  - LINK: the import dialog's Part library row is now written onto the `.crail` — it was read for
//    the session and never recorded, so the design lost its library on the next open.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PartLibraryReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-lib-reuse-" + Guid.NewGuid().ToString("N"));

    public PartLibraryReuseTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// New part numbers arrive whole, an empty field is filled, a disagreement keeps THIS library's
    /// value and is named, and an attached model is restated against this library's folder.
    /// </summary>
    [Fact]
    public void Merge_AddsFillsKeepsAndRebases()
    {
        string otherDir = Path.Combine(_root, "wsA");
        string thisDir  = Path.Combine(_root, "wsB");
        Directory.CreateDirectory(Path.Combine(otherDir, "models"));
        Directory.CreateDirectory(thisDir);

        var source = new PartLibrary { BaseDirectory = otherDir };
        source.Rows.Add(new PartLibraryRow
        {
            PartNumber = "CAP-100N-0402", CapacitanceFarads = 100e-9, SelfResonantFrequencyHz = 39.1e6,
            DielectricClass = "X7R",
        });
        source.Rows.Add(new PartLibraryRow
        {
            PartNumber = "CAP-1U-0603", CapacitanceFarads = 1e-6, ModelRef = "models/cap1u.s2p",
        });
        source.Rows[1].BiasCurve.Add(new PartBiasPoint(3.3, 0.6e-6));

        // A library seeded from this design: one row with only the BOM's marked value, typed as X5R.
        var target = new PartLibrary { BaseDirectory = thisDir };
        target.Rows.Add(new PartLibraryRow
        {
            PartNumber = "cap-100n-0402", CapacitanceFarads = 100e-9, DielectricClass = "X5R",
        });

        var report = PartLibraryMerge.Apply(target, source, "wsA.crlib");

        Assert.Equal(["CAP-1U-0603"], report.Added);
        Assert.Equal(["CAP-100N-0402"], report.Updated);

        var filled = target.Part("CAP-100N-0402")!;
        Assert.Equal(39.1e6, filled.SelfResonantFrequencyHz);
        Assert.Equal("X5R", filled.DielectricClass);
        Assert.Contains(report.Notes, n => n.Contains("class X5R kept", StringComparison.Ordinal));

        var added = target.Part("CAP-1U-0603")!;
        Assert.Equal("../wsA/models/cap1u.s2p", added.ModelRef);
        Assert.Single(added.BiasCurve);
        Assert.Equal(Path.Combine(otherDir, "models", "cap1u.s2p"),
                     CircuitRF.Core.RefPath.Resolve(thisDir, added.ModelRef!));
    }

    /// <summary>The editor's Import reads a <c>.crlib</c> as a library, as one undoable edit.</summary>
    [Fact]
    public void Editor_ImportsAnotherLibrary_AsOneUndoableEdit()
    {
        var source = new PartLibrary { Name = "shared" };
        source.Rows.Add(new PartLibraryRow { PartNumber = "CAP-10N", CapacitanceFarads = 10e-9 });
        string sourcePath = Path.Combine(_root, "shared.crlib");
        PartLibraryIo.SaveToFile(sourcePath, source, validate: false);

        var vm = new PartLibraryEditorViewModel(Path.Combine(_root, "this.crlib"), new PartLibrary());
        vm.ImportTable(sourcePath);

        Assert.Equal(["CAP-10N"], vm.Rows.Select(r => r.PartNumber));
        Assert.StartsWith("shared.crlib: 1 part(s) added", vm.ImportReport[0], StringComparison.Ordinal);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Rows);
    }

    /// <summary>The import dialog's library is recorded on the document, so it survives a reopen.</summary>
    [Fact]
    public void ImportDialogLibrary_IsWrittenOntoTheDocument()
    {
        string crlib = Path.Combine(_root, "wsA", "shared.crlib");
        var vm = new RailRfViewModel(new RailDocument { Name = "b" }, null);

        vm.ApplyImport(
            new RailImportOptions { ArtworkPath = "/b/board.gbr", PartLibraryPath = crlib },
            new RailBoardInputs { Shapes = [], Technology = new CircuitRF.Design.Layout.Technology() },
            library: new PartLibrary());

        Assert.Equal(crlib, vm.Document.PartLibraryRef);

        // A re-import that names no library leaves the one the document already names.
        vm.ApplyImport(
            new RailImportOptions { ArtworkPath = "/b/board.gbr" },
            new RailBoardInputs { Shapes = [], Technology = new CircuitRF.Design.Layout.Technology() });

        Assert.Equal(crlib, vm.Document.PartLibraryRef);
    }
    /// <summary>
    /// Use existing library…: a library from ANOTHER workspace is copied into a new one here, seeded
    /// with this design's part numbers and named by the <c>.crail</c>; one already INSIDE this
    /// workspace is named as it is, with no second file.
    /// </summary>
    [Fact]
    public void UseExisting_CopiesFromElsewhere_AndPointsAtOneAlreadyHere()
    {
        string ws = Path.Combine(_root, "wsB");
        Directory.CreateDirectory(ws);
        string cws = Path.Combine(ws, "wsB.cws");
        File.WriteAllText(cws, "{}");

        string Design(string file)
        {
            var rail = new RailSpec { Name = "VDD" };
            rail.Parts.Add(new RailPart { Refdes = "C1", PartNumber = "CAP-100N" });
            rail.Parts.Add(new RailPart { Refdes = "C2", PartNumber = "CAP-NEW" });
            var doc = new RailDocument();
            doc.Rails.Add(rail);
            string path = Path.Combine(ws, file);
            RailDocumentIo.SaveToFile(path, doc);
            return path;
        }

        var shared = new PartLibrary { Name = "shared" };
        shared.Rows.Add(new PartLibraryRow { PartNumber = "CAP-100N", CapacitanceFarads = 100e-9 });
        shared.Rows.Add(new PartLibraryRow { PartNumber = "CAP-OTHER", CapacitanceFarads = 1e-6 });
        Directory.CreateDirectory(Path.Combine(_root, "wsA"));
        string elsewhere = Path.Combine(_root, "wsA", "shared.crlib");
        PartLibraryIo.SaveToFile(elsewhere, shared, validate: false);

        var workspace = new WorkspaceViewModel { CurrentWorkspacePath = cws };

        // From another workspace: copied.
        string first = Design("a.crail");
        Assert.False(workspace.IsInCurrentWorkspace(elsewhere));
        string? created = workspace.CreatePartLibraryForRailDocument(
            first, "shared", elsewhere, out string? error, out var copied);

        Assert.Null(error);
        Assert.Equal(Path.Combine(ws, "shared.crlib"), created);
        Assert.Equal(["CAP-OTHER"], copied!.Added);
        Assert.Equal(["CAP-100N"], copied.Updated);
        var library = RailArtwork.ResolvePartLibrary(RailDocumentIo.LoadFromFile(first), first, out _, out _)!;
        Assert.Equal(["CAP-100N", "CAP-NEW", "CAP-OTHER"], library.Rows.Select(r => r.PartNumber));
        Assert.Equal(100e-9, library.Part("CAP-100N")!.CapacitanceFarads);

        // Already in this workspace: named as it is.
        string second = Design("b.crail");
        Assert.True(workspace.IsInCurrentWorkspace(created!));
        Assert.Equal(created, workspace.UsePartLibraryForRailDocument(second, created!, out error));
        Assert.Null(error);
        Assert.Equal("shared.crlib", RailDocumentIo.LoadFromFile(second).PartLibraryRef);
        Assert.Single(Directory.GetFiles(ws, "*.crlib"));
    }
    /// <summary>
    /// Use existing library… on a design that already HAS one merges into it, in its open editor, as
    /// an unsaved edit — and picking the design's own library is refused rather than merged into itself.
    /// </summary>
    [Fact]
    public void UseExisting_OnADesignWithALibrary_MergesIntoItsEditor()
    {
        var mine = new PartLibrary { Name = "mine" };
        mine.Rows.Add(new PartLibraryRow { PartNumber = "CAP-100N" });
        string minePath = Path.Combine(_root, "mine.crlib");
        PartLibraryIo.SaveToFile(minePath, mine, validate: false);

        var shared = new PartLibrary { Name = "shared" };
        shared.Rows.Add(new PartLibraryRow { PartNumber = "CAP-100N", CapacitanceFarads = 100e-9 });
        string sharedPath = Path.Combine(_root, "shared.crlib");
        PartLibraryIo.SaveToFile(sharedPath, shared, validate: false);

        var workspace = new WorkspaceViewModel();
        Assert.False(workspace.MergeIntoPartLibrary(minePath, minePath, out string? refusal));
        Assert.Contains("already this design's part library", refusal!, StringComparison.Ordinal);

        Assert.True(workspace.MergeIntoPartLibrary(minePath, sharedPath, out _));
        var doc = Assert.IsType<PartLibraryDocument>(workspace.FindOpenDocument(minePath));
        Assert.Equal(100e-9, doc.ViewModel.Working.Part("CAP-100N")!.CapacitanceFarads);
        Assert.True(doc.IsDirty);
        Assert.Null(PartLibraryIo.LoadFromFile(minePath, validate: false).Part("CAP-100N")!.CapacitanceFarads);
    }
}
