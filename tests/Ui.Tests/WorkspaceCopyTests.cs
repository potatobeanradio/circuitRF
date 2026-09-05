using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  File ▸ Save Workspace As… — the whole workspace FOLDER copied elsewhere
//  (owner request, 2026-09-05).
//
//  What the command used to be is the reason these tests are shaped the way they are. It
//  was an ordinary SaveFilePicker that wrote the `.cws` manifest — and only it — to a path
//  of the user's choosing. A workspace is a DIRECTORY whose manifest is a dotfile named
//  literally `.cws`, so the file it produced (`untitled.cws`) was a name nothing in
//  circuitRF looks for; it carried no cell, no technology and no document; and adopting
//  the picked path silently re-rooted the live window at a folder with none of them in it.
//
//  Headless, over real temp-directory workspaces, in the shape TreeMoveTests already uses:
//  the feature IS a rule about the filesystem, and an in-memory double would agree with
//  itself about the thing under test.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class WorkspaceCopyTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;

    public WorkspaceCopyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf_wscopy_" + Guid.NewGuid().ToString("N")[..8]);
        _ws   = Path.Combine(_root, "workspaceA");
        Directory.CreateDirectory(_ws);
        WriteCws(_ws);
        CellSymbolResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellSymbolResolver.InvalidateAll();
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private static void WriteCws(string root, Action<CwsFile>? edit = null)
    {
        var cws = new CwsFile();
        edit?.Invoke(cws);
        WorkspacePersistence.SaveToFile(Path.Combine(root, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();
    }

    private string Cell(string relativePath)
    {
        string parent = Path.Combine(_ws, Path.GetDirectoryName(relativePath) ?? "");
        Directory.CreateDirectory(parent);
        return CellFolder.CreateCellFolder(parent, Path.GetFileName(relativePath));
    }

    /// <summary>A schematic in <paramref name="cellDir"/> placing each target, with the references
    /// written by the SAME producer the editor uses.</summary>
    private static string SchematicPlacing(string cellDir, params string[] targetCellDirs)
    {
        string schDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        Directory.CreateDirectory(schDir);
        string path = Path.Combine(schDir, Path.GetFileName(cellDir) + ".csch");

        var model = new SchematicEditModel();
        int n = 1;
        foreach (var target in targetCellDirs)
            model.Components.Add(new EditableComponent
            {
                InstanceName = "X" + n++,
                Symbol       = SymbolKind.Generic,
                CellRef      = ExternalCellRef.MakeCellRef(schDir, target),
            });
        SchematicPersistence.SaveToFile(path, model);
        return path;
    }

    private string Copy(string destName, out WorkspaceCopyResult result)
    {
        string dest = Path.Combine(_root, destName);
        result = WorkspaceCopy.Run(_ws, dest);
        return dest;
    }

    private static string CellRefIn(string cschPath, int index = 0)
        => JsonNode.Parse(File.ReadAllText(cschPath))!["Components"]!
                   .AsArray()[index]!["CellRef"]!.GetValue<string>();

    private static string Rel(string root, string abs) => Path.GetRelativePath(root, abs);

    // ── Gate 1 — the whole folder travels ────────────────────────────────────

    /// <summary>
    /// The claim the command's whole existence rests on: a cell, its schematic, the technology and a
    /// loose file all land in the copy. The old implementation copied NONE of them — this is the
    /// test that fails against it, and it fails on the first assertion.
    /// </summary>
    [Fact]
    public void TheCopyCarriesTheCellsTheTechnologyAndTheLooseFiles()
    {
        string amp = Cell("cells/Amp");
        SchematicPlacing(amp, Cell("cells/Rload"));

        Directory.CreateDirectory(Path.Combine(_ws, "tech"));
        File.WriteAllText(Path.Combine(_ws, "tech", "board.ctech"), "{}");
        Directory.CreateDirectory(Path.Combine(_ws, "results"));
        File.WriteAllText(Path.Combine(_ws, "results", "run.s2p"), "! measured\n");

        string dest = Copy("workspaceB", out var result);

        Assert.True(File.Exists(Path.Combine(dest, ".cws")));
        Assert.True(File.Exists(Path.Combine(dest, "cells", "Amp", "schematic", "Amp.csch")));
        Assert.True(Directory.Exists(Path.Combine(dest, "cells", "Rload")));
        Assert.True(File.Exists(Path.Combine(dest, "tech", "board.ctech")));
        Assert.True(File.Exists(Path.Combine(dest, "results", "run.s2p")));

        Assert.True(result.FileCount >= 5, $"only {result.FileCount} file(s) copied");
        Assert.True(result.Bytes > 0);
        Assert.Empty(result.Failures);
    }

    /// <summary>An empty folder is something the user made. A copy that drops it has changed the
    /// project tree, which is not what "save a copy" means.</summary>
    [Fact]
    public void AnEmptyFolderSurvivesTheCopy()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "scratch", "later"));

        string dest = Copy("workspaceB", out _);

        Assert.True(Directory.Exists(Path.Combine(dest, "scratch", "later")));
    }

    /// <summary>
    /// The session bookkeeping is left behind, via the skip filter Archive already owns. The advisory
    /// lock is the one that matters: copied through, the destination would come up holding a lock
    /// naming a session that has nothing to do with it. The pCell cache is left behind because it
    /// rebuilds itself.
    /// </summary>
    [Fact]
    public void TheAdvisoryLockTheCachesAndTheOsClutterAreNotCopied()
    {
        File.WriteAllText(Path.Combine(_ws, ".crf-open.json"), "{\"user\":\"someone\"}");
        File.WriteAllText(Path.Combine(_ws, ".DS_Store"), "x");
        Directory.CreateDirectory(Path.Combine(_ws, GeneratedCellStore.ReservedFolderName));
        File.WriteAllText(Path.Combine(_ws, GeneratedCellStore.ReservedFolderName, "gen.clay"), "{}");

        string dest = Copy("workspaceB", out _);

        Assert.False(File.Exists(Path.Combine(dest, ".crf-open.json")));
        Assert.False(File.Exists(Path.Combine(dest, ".DS_Store")));
        Assert.False(Directory.Exists(Path.Combine(dest, GeneratedCellStore.ReservedFolderName)));
    }

    // ── Gate 2 — references ──────────────────────────────────────────────────

    /// <summary>
    /// A reference from one cell to another INSIDE the workspace is left byte for byte alone: both
    /// ends moved by the same delta, so the relative spelling is already correct. Not merely
    /// "resolves" — unchanged, because a copy that re-spells every reference it did not need to is a
    /// copy whose diff against the original is unreadable.
    /// </summary>
    [Fact]
    public void AReferenceInsideTheWorkspaceIsNotRewrittenAtAll()
    {
        string amp = Cell("cells/Amp");
        string sch = SchematicPlacing(amp, Cell("cells/Rload"));
        string before = CellRefIn(sch);

        string dest = Copy("workspaceB", out var result);

        string copied = Path.Combine(dest, Rel(_ws, sch));
        Assert.Equal(before, CellRefIn(copied));
        Assert.DoesNotContain(result.RewrittenFiles, f => f.EndsWith("Amp.csch", StringComparison.Ordinal));
    }

    /// <summary>
    /// The half that a naive folder copy gets wrong. A cell OUTSIDE the workspace is referenced by a
    /// relative chain that climbs out of it — <c>../../../shared/Bias</c> — and that chain is
    /// measured from the referring document. Move the document and the chain lands somewhere else
    /// entirely, silently. The copy re-spells it so it still names the very same directory.
    /// </summary>
    [Fact]
    public void AReferenceOutOfTheWorkspaceStillResolvesToTheSameCellFromTheCopy()
    {
        string outside = Path.Combine(_root, "shared");
        Directory.CreateDirectory(outside);
        string bias = CellFolder.CreateCellFolder(outside, "Bias");

        string amp = Cell("cells/deep/Amp");
        string sch = SchematicPlacing(amp, bias);

        // Precondition: the fixture really does store a climbing relative chain, not an absolute
        // path — otherwise this test would pass for the wrong reason.
        Assert.StartsWith("..", CellRefIn(sch), StringComparison.Ordinal);

        // A copy that lands at a DIFFERENT DEPTH is what makes the chain wrong; same depth would
        // pass with no rewrite at all.
        string dest = Path.Combine(_root, "nested", "deeper", "workspaceB");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var result = WorkspaceCopy.Run(_ws, dest);

        string copied = Path.Combine(dest, Rel(_ws, sch));
        string stored = CellRefIn(copied);

        WorkspaceRootFinder.InvalidateCache();
        string? resolved = ExternalCellRef.ResolveCellDir(
            stored, Path.GetDirectoryName(copied)!);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(bias), Path.GetFullPath(resolved!));
        Assert.Contains(result.RewrittenFiles, f => f.EndsWith("Amp.csch", StringComparison.Ordinal));
    }

    /// <summary>
    /// A copy is not a move, and this is where the two part company. <c>WorkspaceMove.Capture</c>
    /// takes a set of roots; handing it the OTHER open workspaces — which is exactly right for a move
    /// — would repoint their references at the copy and quietly gut the original's referrers. The
    /// original workspace must come through untouched, byte for byte.
    /// </summary>
    [Fact]
    public void TheOriginalWorkspaceIsLeftByteForByteAlone()
    {
        string outside = Path.Combine(_root, "shared");
        Directory.CreateDirectory(outside);
        string bias = CellFolder.CreateCellFolder(outside, "Bias");

        string amp = Cell("cells/deep/Amp");
        string sch = SchematicPlacing(amp, bias);

        var before = Directory.GetFiles(_ws, "*", SearchOption.AllDirectories)
                              .ToDictionary(f => f, File.ReadAllBytes);

        WorkspaceCopy.Run(_ws, Path.Combine(_root, "nested", "workspaceB"));

        foreach (var (file, bytes) in before)
        {
            Assert.True(File.Exists(file), $"{file} disappeared");
            Assert.Equal(bytes, File.ReadAllBytes(file));
        }

        // And the original's own reference still resolves where it always did.
        WorkspaceRootFinder.InvalidateCache();
        Assert.Equal(
            Path.GetFullPath(bias),
            Path.GetFullPath(ExternalCellRef.ResolveCellDir(
                CellRefIn(sch), Path.GetDirectoryName(sch)!)!));
    }

    /// <summary>
    /// The <c>.cws</c>'s own open-document list is workspace-RELATIVE, which is what lets the copy
    /// reopen the same tabs out of its own files rather than the original's. Asserted because the
    /// command switches the window to the copy on the strength of it.
    /// </summary>
    [Fact]
    public void TheCopiedManifestStillNamesItsOwnDocuments()
    {
        string amp = Cell("cells/Amp");
        string sch = SchematicPlacing(amp);
        WriteCws(_ws, c => c.OpenDocuments =
        [
            new CwsOpenDocument { Path = Rel(_ws, sch).Replace('\\', '/'), Kind = "schematic", TabOrder = 0 },
        ]);

        string dest = Copy("workspaceB", out _);

        var copied = WorkspacePersistence.LoadFromFile(Path.Combine(dest, ".cws"));
        string stored = Assert.Single(copied.OpenDocuments!).Path;

        Assert.False(Path.IsPathRooted(stored));
        Assert.True(File.Exists(Path.Combine(dest, stored.Replace('/', Path.DirectorySeparatorChar))));
    }

    // ── Gate 3 — refusals ────────────────────────────────────────────────────

    /// <summary>Copying a workspace into itself walks its own output. The New Workspace dialog's
    /// "not inside another workspace" check does not cover it — a subfolder of a workspace has no
    /// <c>.cws</c> of its own.</summary>
    [Fact]
    public void AWorkspaceCannotBeCopiedIntoItself()
    {
        Assert.NotNull(WorkspaceCopy.Refusal(_ws, Path.Combine(_ws, "backup")));
        Assert.NotNull(WorkspaceCopy.Refusal(_ws, Path.Combine(_ws, "a", "b")));
        Assert.Null(WorkspaceCopy.Refusal(_ws, Path.Combine(_root, "elsewhere")));
    }

    [Fact]
    public void CopyingOverSomethingThatAlreadyExistsIsRefused()
    {
        string taken = Path.Combine(_root, "taken");
        Directory.CreateDirectory(taken);

        Assert.NotNull(WorkspaceCopy.Refusal(_ws, taken));
        Assert.Throws<InvalidOperationException>(() => WorkspaceCopy.Run(_ws, taken));
    }

    [Fact]
    public void AFolderThatIsNotAWorkspaceIsRefusedByName()
    {
        string notAWorkspace = Path.Combine(_root, "plain");
        Directory.CreateDirectory(notAWorkspace);

        string refusal = Assert.IsType<string>(
            WorkspaceCopy.Refusal(notAWorkspace, Path.Combine(_root, "dest")));
        Assert.Contains(".cws", refusal, StringComparison.Ordinal);
    }

    // ── Gate 4 — the command, structurally ───────────────────────────────────

    /// <summary>
    /// The command itself needs a real <c>Window</c> and a modal, so it is gated at source level —
    /// the shape <c>ReadOnlyWorkspaceTests</c> and <c>MultiWorkspaceShellTests</c> already use, with
    /// comments stripped first, because a rule that only matches because the sentence NAMING it is in
    /// a comment is not a rule.
    ///
    /// <para>Three claims, and each one is a way the old command failed: it copies the FOLDER, it
    /// switches this window to the copy (which is what drops the source's advisory lock and takes the
    /// copy's — <c>SaveWorkspaceAs</c> never moved the lock at all), and it no longer reaches a file
    /// picker.</para>
    /// </summary>
    [Fact]
    public void SaveWorkspaceAsCopiesTheFolderSwitchesToItAndUsesNoFilePicker()
    {
        string body = MethodBody("private async Task SaveWorkspaceAs(Window? owner)");

        Assert.Contains("WorkspaceCopy.Run(sourceRoot, destRoot)", body, StringComparison.Ordinal);
        Assert.Contains("SaveWorkspaceAsDialog(sourceRoot, parentDir)", body, StringComparison.Ordinal);
        Assert.Contains("SwitchToWorkspaceReporting(", body, StringComparison.Ordinal);

        // The two lines that WERE the bug: a file picker, and adopting its result as the live
        // workspace path without moving anything to it.
        Assert.DoesNotContain("SaveFilePickerAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentWorkspacePath =", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Save Workspace As… is greyed out with no workspace open, and Save says so rather than falling
    /// through to it. The old Save fell through, which is how a window that had never opened a
    /// workspace could be talked into writing a manifest of its dock layout into an unrelated folder.
    /// </summary>
    [Fact]
    public void WithNoWorkspaceOpenSaveAsIsDisabledAndSaveExplainsItself()
    {
        string src = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));

        Assert.Contains(
            "[RelayCommand(CanExecute = nameof(CanCloseWorkspace))]\n    private async Task SaveWorkspaceAs(",
            src.Replace("\r\n", "\n"), StringComparison.Ordinal);

        string save = MethodBody("private void SaveWorkspace()");
        Assert.DoesNotContain("SaveWorkspaceAs(", save, StringComparison.Ordinal);
        Assert.Contains("No workspace is open.", save, StringComparison.Ordinal);
    }

    // ── Source-scan helpers ──────────────────────────────────────────────────

    private static string MethodBody(string signature)
    {
        string code = StripComments(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs")));

        int start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was renamed — this gate needs re-pointing.");

        // Brace-match from the signature's opening brace to its close.
        int open = code.IndexOf('{', start);
        Assert.True(open >= 0);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        Assert.Fail($"Unbalanced braces after '{signature}'.");
        return "";
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline),
                         @"//[^\r\n]*", "");

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }
}
