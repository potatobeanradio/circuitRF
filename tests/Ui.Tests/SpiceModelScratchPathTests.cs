// ================================================================
//  SpiceModelScratchPathTests.cs — store and resolve must use the SAME root (2026-09-04)
//
//  Reported: a placed SPICE model could not find its file, naming a path inside the
//  per-session recovery directory, under a containing folder whose name has a space in it.
//
//  The space is not the cause — nothing here reaches a shell, and the failure is a plain
//  FileInfo.Exists on a path that was assembled wrong. Two rules disagreed about which
//  directory a relative File value is relative TO:
//
//    store   — ParameterEditorViewModel.PickSpiceModelFileAsync passed the OPEN WINDOW's
//              workspace root (SchematicViewModel.WorkspaceRoot).
//    resolve — SpiceModelSymbolProvider.ResolvePath walks UP from the schematic's own
//              directory looking for a .cws, and falls back to that directory when there
//              is none.
//
//  They agree for a saved document inside the open workspace and disagree everywhere
//  else. The sharpest case is a SCRATCH schematic, which has no directory to walk up
//  from: the pick stored a workspace-relative path, and the resolver joined it to whatever
//  base the model happened to be carrying — which, thanks to the recovery autosave
//  rebasing bug this shipped with, was the recovery folder.
//
//  The invariant these tests hold is the round trip: whatever ToStored writes for a
//  directory, ResolvePath must read back as the same absolute file, for the same
//  directory. It cannot be satisfied by two different roots.
// ================================================================

using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SpiceModelScratchPathTests : IDisposable
{
    private readonly string _root;
    private readonly string _modelFile;

    public SpiceModelScratchPathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-spice-path-" + Guid.NewGuid().ToString("N")[..8]);
        // A workspace, with the model file in a subfolder whose name contains a SPACE — the thing
        // the report asked about, kept in every case below so it cannot regress unnoticed.
        Directory.CreateDirectory(Path.Combine(_root, "ws", "model files"));
        File.WriteAllText(Path.Combine(_root, "ws", ".cws"), "{}");
        _modelFile = Path.Combine(_root, "ws", "model files", "part.txt");
        File.WriteAllText(_modelFile, ".model DTEST D(Is=1e-14)\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The round trip, for a schematic saved inside the workspace. Here the stored value is
    /// relative — that is what makes a design portable — and it must still read back.
    /// </summary>
    [Fact]
    public void ASavedSchematic_RoundTripsThroughTheWorkspaceRoot()
    {
        string dir = Path.Combine(_root, "ws", "cells", "amp", "schematic");
        Directory.CreateDirectory(dir);

        string stored = SpiceModelSymbolProvider.ToStored(_modelFile, dir);

        Assert.Equal("model files/part.txt", stored);     // portable, and the space survives verbatim
        Assert.Equal(_modelFile, SpiceModelSymbolProvider.ResolvePath(stored, dir));
    }

    /// <summary>
    /// The round trip for a SCRATCH schematic — no directory at all. There is no root to write a
    /// portable path against, so the absolute one is kept, and it resolves from anywhere.
    ///
    /// <para>This is the reported bug. A relative value stored here could only ever be resolved by
    /// guessing a base directory the user never named.</para>
    /// </summary>
    [Fact]
    public void AScratchSchematic_KeepsTheAbsolutePath()
    {
        string stored = SpiceModelSymbolProvider.ToStored(_modelFile, schematicDir: null);

        Assert.Equal(_modelFile, stored);
        Assert.Equal(_modelFile, SpiceModelSymbolProvider.ResolvePath(stored, null));
    }

    /// <summary>
    /// And it keeps resolving once that scratch schematic IS saved into the workspace — the base
    /// directory appearing under it must not change what an already-stored reference means.
    /// </summary>
    [Fact]
    public void AnAbsolutePath_SurvivesTheScratchToWorkspaceSave()
    {
        string stored = SpiceModelSymbolProvider.ToStored(_modelFile, schematicDir: null);

        string saved = Path.Combine(_root, "ws", "cells", "amp", "schematic");
        Directory.CreateDirectory(saved);

        Assert.Equal(_modelFile, SpiceModelSymbolProvider.ResolvePath(stored, saved));
    }

    /// <summary>
    /// The shape of the failure, stated directly: a workspace-relative value carried by a document
    /// based OUTSIDE that workspace resolves to a file that does not exist. Nothing can repair this
    /// after the fact — which is why the fix is at the store end, and why this test exists to say
    /// what would happen if the two roots ever drift apart again.
    /// </summary>
    [Fact]
    public void AWorkspaceRelativeValue_IsMeaninglessOutsideTheWorkspace()
    {
        string elsewhere = Path.Combine(_root, "recovery", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(elsewhere);

        string? resolved = SpiceModelSymbolProvider.ResolvePath("model files/part.txt", elsewhere);

        Assert.NotNull(resolved);
        Assert.False(File.Exists(resolved));
        Assert.Equal(Path.Combine(elsewhere, "model files", "part.txt"), resolved);
    }

    /// <summary>A space in the path is not the bug: the file reads, by name, through the peek the
    /// symbol resolver and the extractor both use.</summary>
    [Fact]
    public void ASpaceInTheFolderName_ReadsFine()
    {
        var file = SpiceModelPeek.Read(_modelFile);

        Assert.Null(file.Error);
        Assert.Contains(file.Definitions, d => d.Name.Equals("DTEST", StringComparison.OrdinalIgnoreCase));
    }
}
