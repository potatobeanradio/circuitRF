using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
//  SL1 gate — brief-shared-library-1-reaching-the-library.md
//
//  Two defects, one workflow: a shared library organised into folders rendered EMPTY (every
//  reference into it resolved — resolution is path arithmetic and never consults the tree — so only
//  the browsing was missing, which is the whole point of a library), and a .cws naming a network
//  share could not be handed to a second user because their spelling of that share differs.
// ═══════════════════════════════════════════════════════════════════════════════

[Collection(CellStatGlobalsCollection.Name)]
public class SharedLibraryReachTests : IDisposable
{
    private readonly string _root;
    private readonly string _lib;

    public SharedLibraryReachTests()
    {
        string stem = Path.Combine(Path.GetTempPath(), "SL1_" + Guid.NewGuid().ToString("N")[..8]);
        _root = Path.Combine(stem, "own");
        _lib  = Path.Combine(stem, "stdlib");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_lib);
        WorkspaceRootFinder.InvalidateCache();
    }

    public void Dispose()
    {
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A cell folder at <paramref name="relPath"/> under <paramref name="root"/>, creating
    /// the intermediate user folders — `passives/R0402` is the first thing a librarian builds.</summary>
    private static string MakeCellAt(string root, string relPath)
    {
        string parent = Path.Combine(root, Path.GetDirectoryName(relPath.Replace('/', Path.DirectorySeparatorChar)) ?? "");
        Directory.CreateDirectory(parent);
        return CellFolder.CreateCellFolder(parent, Path.GetFileName(relPath));
    }

    private static void MakeWorkspace(string dir, CwsFile? cws = null)
    {
        Directory.CreateDirectory(dir);
        WorkspacePersistence.SaveToFile(Path.Combine(dir, ".cws"), cws ?? new CwsFile());
    }

    private void WriteOwnCws(CwsFile cws)
    {
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), cws);
        WorkspaceRootFinder.InvalidateCache();
    }

    private static ProjectTreeNode Child(ProjectTreeNode parent, string name)
        => parent.Children.Single(n => n.Name == name);

    private static ProjectTreeNode FindKind(ProjectTreeNode parent, NodeKind kind)
        => parent.Children.First(n => n.Kind == kind);

    /// <summary>A uniquely-named environment variable, so two of these tests running in parallel in
    /// one process cannot see each other's.</summary>
    private static string UniqueVarName() => "CRF_SL1_" + Guid.NewGuid().ToString("N")[..8];

    // ── 1. A referenced WORKSPACE renders its cells at any depth ──────────────

    [Fact]
    public void ReferencedWorkspace_NestedCells_AreRendered()
    {
        MakeWorkspace(_lib);
        MakeCellAt(_lib, "passives/R0402");
        MakeCellAt(_lib, "passives/C0603");
        MakeCellAt(_lib, "amplifiers/AmpStage");
        MakeCellAt(_lib, "TopLevelCell");

        WriteOwnCws(new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = Path.Combine(_lib, ".cws") }],
        });

        var tree = WorkspaceScanner.Scan(_root);
        var ws   = FindKind(tree, NodeKind.ReferencedWorkspace);

        // Alphabetical (OrdinalIgnoreCase), folders and cells intermixed exactly as the workspace's
        // own scan orders them.
        Assert.Equal(new[] { "amplifiers", "passives", "TopLevelCell" }, ws.Children.Select(n => n.Name));

        var passives = Child(ws, "passives");
        Assert.Equal(NodeKind.UserFolder, passives.Kind);
        Assert.Equal(new[] { "C0603", "R0402" }, passives.Children.Select(n => n.Name));
        Assert.All(passives.Children, c => Assert.Equal(NodeKind.Cell, c.Kind));
    }

    [Fact]
    public void ReferencedWorkspace_CellThreeLevelsDown_IsAnOrdinaryOpenableCellNode()
    {
        MakeWorkspace(_lib);
        string cellDir = MakeCellAt(_lib, "parts/passives/thinfilm/R0402");
        File.WriteAllText(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), "r.csym"), "");
        File.WriteAllText(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), "r.csch"), "");

        WriteOwnCws(new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = Path.Combine(_lib, ".cws") }],
        });

        var ws   = FindKind(WorkspaceScanner.Scan(_root), NodeKind.ReferencedWorkspace);
        var cell = Child(Child(Child(Child(ws, "parts"), "passives"), "thinfilm"), "R0402");

        Assert.Equal(NodeKind.Cell, cell.Kind);
        Assert.Equal(cellDir, cell.AbsolutePath);                       // openable: the real folder on disk
        Assert.Null(cell.WarningReason);

        // Its view sub-folders come along, with the same shape a top-level cell's have.
        var views = cell.Children.Select(n => n.Name).ToList();
        Assert.Contains(CellFolder.SubFolderName(ViewType.Symbol), views);
        Assert.Contains(CellFolder.SubFolderName(ViewType.Schematic), views);
        var sym = Child(cell, CellFolder.SubFolderName(ViewType.Symbol));
        Assert.Equal(NodeKind.CellViewFolder, sym.Kind);
        Assert.True(sym.Children.Single().IsPrimary);                   // sole file → primary
    }

    // ── 2. The same for a referenced .clib library ────────────────────────────

    [Fact]
    public void ReferencedLibrary_NestedCells_AreRendered()
    {
        MakeCellAt(_lib, "passives/R0402");
        MakeCellAt(_lib, "amplifiers/AmpStage");

        WriteOwnCws(new CwsFile { LibraryRefs = [_lib] });

        var lib = FindKind(FindKind(WorkspaceScanner.Scan(_root), NodeKind.LibrariesGroup), NodeKind.Library);
        Assert.Null(lib.WarningReason);
        Assert.Equal(new[] { "amplifiers", "passives" }, lib.Children.Select(n => n.Name));
        Assert.Equal("R0402", Child(lib, "passives").Children.Single().Name);
    }

    [Fact]
    public void ReferencedLibrary_FolderWithNoCellsBeneath_IsNotRendered()
    {
        MakeCellAt(_lib, "passives/R0402");
        Directory.CreateDirectory(Path.Combine(_lib, "docs"));
        File.WriteAllText(Path.Combine(_lib, "docs", "readme.txt"), "");

        WriteOwnCws(new CwsFile { LibraryRefs = [_lib] });

        var lib = FindKind(FindKind(WorkspaceScanner.Scan(_root), NodeKind.LibrariesGroup), NodeKind.Library);
        // A referenced sub-tree carries cells and nothing else, so a folder that leads to none is a
        // dead end rather than content.
        Assert.Equal(new[] { "passives" }, lib.Children.Select(n => n.Name));
    }

    // ── 3. .generated-cells never appears, at any depth (R-sl1-3) ─────────────

    [Fact]
    public void GeneratedCells_NestedInAReferencedWorkspace_IsNotRendered()
    {
        MakeWorkspace(_lib);
        MakeCellAt(_lib, "parts/R0402");
        // A generated-cell store nested inside a user folder — latent before SL1 because nothing
        // recursed here, and because the reserved-folder exclusion lived in Scan's root loop only.
        MakeCellAt(_lib, "parts/" + GeneratedCellStore.ReservedFolderName + "/MLIN_a1b2c3");

        WriteOwnCws(new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = Path.Combine(_lib, ".cws") }],
        });

        var ws    = FindKind(WorkspaceScanner.Scan(_root), NodeKind.ReferencedWorkspace);
        var parts = Child(ws, "parts");
        Assert.Equal(new[] { "R0402" }, parts.Children.Select(n => n.Name));
    }

    [Fact]
    public void GeneratedCells_NestedInTheWorkspacesOwnUserFolder_IsNotRendered()
    {
        // The same pre-existing latent defect on the workspace's own side: BuildUserFolderNode never
        // applied the reserved-folder exclusion, which only ever went unnoticed because
        // .generated-cells lives at a workspace ROOT in practice.
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        MakeCellAt(_root, "work/MyCell");
        MakeCellAt(_root, "work/" + GeneratedCellStore.ReservedFolderName + "/MSTEP_ffee11");

        var work = Child(WorkspaceScanner.Scan(_root), "work");
        Assert.Equal(new[] { "MyCell" }, work.Children.Select(n => n.Name));
    }

    // ── 4. The recursion does not cross a nested .cws (R-sl1-2) ───────────────

    [Fact]
    public void ReferencedWorkspace_DoesNotReachThroughANestedWorkspacesConfiguration()
    {
        MakeWorkspace(_lib);
        MakeCellAt(_lib, "OwnCell");

        // A second workspace nested inside the referenced one, itself referencing a third library.
        string nested = Path.Combine(_lib, "delivery");
        string third  = Path.Combine(Path.GetDirectoryName(_root)!, "third");
        Directory.CreateDirectory(third);
        MakeCellAt(third, "ThirdPartyCell");
        MakeWorkspace(nested, new CwsFile { LibraryRefs = [third] });
        MakeCellAt(nested, "NestedCell");

        WriteOwnCws(new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = Path.Combine(_lib, ".cws") }],
        });

        var ws = FindKind(WorkspaceScanner.Scan(_root), NodeKind.ReferencedWorkspace);

        // The nested workspace's FOLDER is walked like any other folder — its own CONFIGURATION is
        // not, so the third library it references is reached not at all.
        Assert.Equal("NestedCell", Child(ws, "delivery").Children.Single().Name);
        Assert.Empty(Descendants(ws).Where(n => n.Name == "ThirdPartyCell"));
        Assert.Empty(Descendants(ws).Where(n => n.Kind is NodeKind.LibrariesGroup or NodeKind.Library
                                             or NodeKind.KnownFilesGroup or NodeKind.ReferencedWorkspace));

        static System.Collections.Generic.IEnumerable<ProjectTreeNode> Descendants(ProjectTreeNode n)
            => n.Children.Concat(n.Children.SelectMany(Descendants));
    }

    // ── 5. Token expansion (R-sl1-5/-6/-7) ───────────────────────────────────

    [Fact]
    public void TokenisedAlias_ResolvesACell_WhenTheVariableIsSet()
    {
        string var = UniqueVarName();
        MakeWorkspace(_lib);
        string cellDir = MakeCellAt(_lib, "passives/R0402");
        WriteOwnCws(new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = "${" + var + "}/stdlib/.cws" }],
        });

        try
        {
            Environment.SetEnvironmentVariable(var, Path.GetDirectoryName(_lib));
            WorkspaceRootFinder.InvalidateCache();

            string? resolved = ExternalCellRef.ResolveCellDir("ws://stdlib/passives/R0402", _root);
            Assert.Equal(cellDir, resolved);

            var ws = FindKind(WorkspaceScanner.Scan(_root), NodeKind.ReferencedWorkspace);
            Assert.Null(ws.WarningReason);
            Assert.Equal("R0402", Child(ws, "passives").Children.Single().Name);
        }
        finally { Environment.SetEnvironmentVariable(var, null); WorkspaceRootFinder.InvalidateCache(); }
    }

    [Fact]
    public void UnsetToken_IsABrokenReferenceNamingTheToken_NotAnEmptyExpansion()
    {
        string var = UniqueVarName();
        MakeWorkspace(_lib);
        MakeCellAt(_lib, "passives/R0402");
        WriteOwnCws(new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = "${" + var + "}/stdlib/.cws" }],
        });

        Environment.SetEnvironmentVariable(var, null);
        WorkspaceRootFinder.InvalidateCache();

        Assert.Null(ExternalCellRef.ResolveCellDir("ws://stdlib/passives/R0402", _root));

        var ws = FindKind(WorkspaceScanner.Scan(_root), NodeKind.ReferencedWorkspace);
        Assert.NotNull(ws.WarningReason);
        Assert.Contains("${" + var + "}", ws.WarningReason);
        Assert.Contains("not set on this machine", ws.WarningReason);
        // Never the half-truth: an empty expansion would have produced a rooted /stdlib/.cws.
        Assert.DoesNotContain("/stdlib/.cws", ws.WarningReason!.Replace("${" + var + "}", ""));
    }

    [Fact]
    public void UnsetToken_InALibraryRefAndAKnownFile_NamesTheTokenToo()
    {
        string var = UniqueVarName();
        Environment.SetEnvironmentVariable(var, null);
        WriteOwnCws(new CwsFile
        {
            LibraryRefs = ["${" + var + "}/stdlib"],
            KnownFiles  = ["${" + var + "}/meas/thru.s2p"],
        });

        var tree = WorkspaceScanner.Scan(_root);
        var lib  = FindKind(FindKind(tree, NodeKind.LibrariesGroup), NodeKind.Library);
        var kf   = FindKind(FindKind(tree, NodeKind.KnownFilesGroup), NodeKind.KnownFile);

        Assert.Contains("${" + var + "}", lib.WarningReason);
        Assert.Contains("not set on this machine", lib.WarningReason);
        Assert.Contains("${" + var + "}", kf.WarningReason);
    }

    [Fact]
    public void ACellRef_IsNeverExpanded()
    {
        string var = UniqueVarName();
        MakeWorkspace(_lib);
        WriteOwnCws(new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = Path.Combine(_lib, ".cws") }],
        });

        try
        {
            // Set the variable to something real: if the CellRef remainder were expanded, this would
            // resolve. It must not — the remainder is workspace-relative and names no machine.
            Environment.SetEnvironmentVariable(var, "passives");
            WorkspaceRootFinder.InvalidateCache();

            string? resolved = ExternalCellRef.ResolveCellDir("ws://stdlib/${" + var + "}/R0402", _root);
            Assert.Equal(Path.Combine(_lib, "${" + var + "}", "R0402"), resolved);

            // And the same for the plain relative form.
            Assert.Equal(Path.Combine(_root, "${" + var + "}", "R0402"),
                ExternalCellRef.ResolveCellDir("${" + var + "}/R0402", _root));
        }
        finally { Environment.SetEnvironmentVariable(var, null); WorkspaceRootFinder.InvalidateCache(); }
    }

    [Fact]
    public void PathTokens_ExpandsEveryToken_AndLeavesUnterminatedTextAlone()
    {
        string a = UniqueVarName(), b = UniqueVarName();
        try
        {
            Environment.SetEnvironmentVariable(a, "/srv");
            Environment.SetEnvironmentVariable(b, "v2.3");
            Assert.True(PathTokens.TryExpand("${" + a + "}/stdlib/${" + b + "}/.cws", out string expanded, out string? unset));
            Assert.Equal("/srv/stdlib/v2.3/.cws", expanded);
            Assert.Null(unset);

            // An unterminated ${ is literal text, not a failure: a path may legitimately contain one.
            Assert.True(PathTokens.TryExpand("/srv/${broken/.cws", out string literal, out _));
            Assert.Equal("/srv/${broken/.cws", literal);

            // The FIRST unset token is the one reported, and nothing is half-expanded.
            Assert.False(PathTokens.TryExpand("${" + a + "}/${NOPE_" + a + "}/x", out string untouched, out string? which));
            Assert.Equal("${NOPE_" + a + "}", which);
            Assert.Equal("${" + a + "}/${NOPE_" + a + "}/x", untouched);
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, null);
            Environment.SetEnvironmentVariable(b, null);
        }
    }

    // ── 6. Headless: the expander is reachable from src/Design (R-sl1-8) ──────

    [Fact]
    public void TokenisedAlias_ResolvesWithNothingFromSrcUi_TheHeadlessPath()
    {
        // `circuitrf convert` and `circuitrf em` resolve these references with no Avalonia and no
        // WorkspaceViewModel: the whole reason PathTokens lives beside ExternalCellRef in
        // src/Design/Workspace rather than beside WorkspaceRefs in src/Ui. Everything this test
        // touches is on the headless side of the firewall.
        string var = UniqueVarName();
        MakeWorkspace(_lib);
        string cellDir = MakeCellAt(_lib, "cells/Amp");
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile
        {
            ReferencedWorkspaces = [new CwsWorkspaceRef { Alias = "stdlib", Path = "${" + var + "}/stdlib/.cws" }],
        });

        try
        {
            Environment.SetEnvironmentVariable(var, Path.GetDirectoryName(_lib));
            WorkspaceRootFinder.InvalidateCache();

            string docDir = Path.Combine(_root, "designs");
            Directory.CreateDirectory(docDir);

            Assert.Equal(cellDir, ExternalCellRef.ResolveCellDir("ws://stdlib/cells/Amp", docDir));
            Assert.Equal(WorkspaceRootFinder.Normalize(_lib),
                ExternalCellRef.ResolveAliasWorkspaceRoot("ws://stdlib/cells/Amp", docDir));
        }
        finally { Environment.SetEnvironmentVariable(var, null); WorkspaceRootFinder.InvalidateCache(); }
    }
}
