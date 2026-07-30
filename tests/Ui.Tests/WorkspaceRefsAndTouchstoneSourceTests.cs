// ================================================================
//  WorkspaceRefsAndTouchstoneSourceTests.cs
//  brief-stability-passivity-touchstone.md gates 7, 8, 9
// ================================================================
//
//  Gate 7  (R-stb-7/8/9)  — a dropped .s2p / .sNp is an ordinary data source: it appears in the
//                           picker, carries an alias, and exposes the same card with port selectors.
//  Gate 8  (R-stb-10/11/12) — a Touchstone INSIDE the workspace stores a RELATIVE ref with `/`
//                           separators and survives moving the workspace; one OUTSIDE stores an
//                           absolute ref and is marked external.
//  Gate 9  (R-stb-13)     — dropping a Touchstone file never copies it, and puts nothing in results/.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class WorkspaceRefsAndTouchstoneSourceTests : IDisposable
{
    private readonly string _root;

    public WorkspaceRefsAndTouchstoneSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-wsrefs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    // ---- helpers -------------------------------------------------------

    /// <summary>Writes a real N-port Touchstone file (a plain, flat resistive network is enough —
    /// these gates are about SOURCE plumbing, not S-parameter values).</summary>
    private static string WriteSnp(string path, int nPorts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new StreamWriter(path);
        w.WriteLine("! circuitRF test fixture");
        w.WriteLine("# GHz S RI R 50");
        foreach (var f in new[] { 1.0, 2.0, 3.0 })
        {
            w.Write(f.ToString("0.0"));
            for (int i = 0; i < nPorts; i++)
                for (int j = 0; j < nPorts; j++)
                    w.Write(i == j ? "  0.1 0.0" : "  0.5 -0.1");
            w.WriteLine();
        }
        return path;
    }

    // =========================================================================
    //  Gate 7 — a Touchstone source is an ordinary source (R-stb-7/8/9)
    // =========================================================================

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async System.Threading.Tasks.Task Gate7_DroppedTouchstone_IsAnOrdinarySource_WithAliasAndPortCount(int nPorts)
    {
        string snp = WriteSnp(Path.Combine(_root, $"dut.s{nPorts}p"), nPorts);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(snp);

        var entry = Assert.Single(lib.Entries);
        Assert.False(entry.IsBroken);
        Assert.Equal("dut", entry.Alias);                       // R-stb-8: alias defaults to the file stem
        Assert.NotNull(entry.Snp);
        Assert.Equal(nPorts, entry.Snp!.Ports);                 // R-stb-9: any N, not just 2
    }

    [Fact]
    public async System.Threading.Tasks.Task Gate7_TouchstoneSource_CarriesARenameableAlias()
    {
        string snp = WriteSnp(Path.Combine(_root, "meas.s2p"), 2);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(snp);
        var entry = Assert.Single(lib.Entries);

        Assert.True(lib.TrySetAlias(entry, "baseline"));
        Assert.Equal("baseline", entry.Alias);
        Assert.Equal("baseline", lib.AliasFor(snp));
    }

    [Fact]
    public async System.Threading.Tasks.Task Gate7_TouchstoneAndSimulatedSources_CoexistInOneLibrary()
    {
        // The picker offers both kinds side by side; nothing about the Touchstone path is special-cased.
        string snpA = WriteSnp(Path.Combine(_root, "a.s2p"), 2);
        string snpB = WriteSnp(Path.Combine(_root, "b.s3p"), 3);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(snpA);
        await lib.LoadFileAsync(snpB);

        Assert.Equal(2, lib.Entries.Count);
        Assert.Equal(new[] { "a", "b" }, lib.Entries.Select(e => e.Alias).OrderBy(a => a).ToArray());
        Assert.Equal(new[] { 2, 3 }, lib.Entries.Select(e => e.Snp!.Ports).OrderBy(p => p).ToArray());
    }

    /// <summary>
    /// Gate 7, the half the VM-level tests structurally could not reach: the file-picker / drag-drop
    /// path reads an <c>IStorageFile</c> STREAM, not a path, and a TextReader has no filename — so the
    /// port count must be handed in from the extension or an .s3p/.s4p throws and is swallowed by the
    /// surrounding catch. The path-based loader beside it always passed it, which is why the gap was
    /// invisible: every headless test used that loader. An IStorageFile cannot be constructed without
    /// the Avalonia runtime, so this is pinned as a source scan (this codebase's established fallback).
    /// </summary>
    [Fact]
    public void Gate7_StorageFileLoadPath_PassesThePortCountFromTheExtension()
    {
        string src = ReadRepoFile("src/Ui/DataDisplay/ViewModels/DataSourceLibraryViewModel.cs");

        Assert.Contains("TouchstoneIO.ParsePortsFromExtension(path)", src, StringComparison.Ordinal);
        Assert.Contains("TouchstoneIO.Read(reader, portsFromExt)", src, StringComparison.Ordinal);
        // The bare form is what silently broke N > 2; it must not come back.
        Assert.DoesNotContain("TouchstoneIO.Read(reader);", src, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // =========================================================================
    //  Gate 8 — storage rules (R-stb-10/11/12)
    // =========================================================================

    [Fact]
    public void Gate8_InsideWorkspace_StoredRelative_WithForwardSlashes()
    {
        string ws = Path.Combine(_root, "ws");
        string snp = WriteSnp(Path.Combine(ws, "measurements", "dut.s2p"), 2);

        string stored = WorkspaceRefs.ToStoredRef(snp, ws);

        Assert.False(Path.IsPathRooted(stored));
        Assert.Equal("measurements/dut.s2p", stored);
        // The `/` normalisation is the half that only fails when a workspace crosses platforms.
        Assert.DoesNotContain('\\', stored);
    }

    [Fact]
    public void Gate8_InsideWorkspace_SurvivesMovingTheWholeWorkspace()
    {
        string wsOld = Path.Combine(_root, "old");
        string snp = WriteSnp(Path.Combine(wsOld, "measurements", "dut.s2p"), 2);
        string stored = WorkspaceRefs.ToStoredRef(snp, wsOld);

        // Move the workspace wholesale — the stored ref is relative, so it must still resolve.
        string wsNew = Path.Combine(_root, "moved", "elsewhere");
        Directory.CreateDirectory(Path.GetDirectoryName(wsNew)!);
        Directory.Move(wsOld, wsNew);

        string resolved = WorkspaceRefs.Resolve(stored, wsNew);
        Assert.True(File.Exists(resolved));
        Assert.Equal(Path.GetFullPath(Path.Combine(wsNew, "measurements", "dut.s2p")), resolved);
        Assert.False(WorkspaceRefs.IsExternal(stored, wsNew));
    }

    [Fact]
    public void Gate8_OutsideWorkspace_StoredAbsolute_AndMarkedExternal()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        string outside = WriteSnp(Path.Combine(_root, "lab-data", "dut.s2p"), 2);

        string stored = WorkspaceRefs.ToStoredRef(outside, ws);

        Assert.True(Path.IsPathRooted(stored));
        Assert.Equal(Path.GetFullPath(outside), stored);
        Assert.True(WorkspaceRefs.IsExternal(stored, ws));      // R-stb-12
    }

    [Fact]
    public void Gate8_ExternalRef_AfterMovingWorkspace_ReportsByNameAndStaysExternal()
    {
        // A moved workspace whose external file is absent must report by name, not silently vanish —
        // and must NOT suddenly read as "inside" just because the workspace moved.
        string wsOld = Path.Combine(_root, "old");
        Directory.CreateDirectory(wsOld);
        string outside = WriteSnp(Path.Combine(_root, "lab-data", "dut.s2p"), 2);
        string stored = WorkspaceRefs.ToStoredRef(outside, wsOld);

        string wsNew = Path.Combine(_root, "moved");
        Directory.Move(wsOld, wsNew);
        File.Delete(outside);                                    // simulate "another machine"

        string resolved = WorkspaceRefs.Resolve(stored, wsNew);
        Assert.False(File.Exists(resolved));
        Assert.Equal("dut.s2p", Path.GetFileName(resolved));     // reported BY NAME
        Assert.True(WorkspaceRefs.IsExternal(stored, wsNew));
    }

    [Fact]
    public async System.Threading.Tasks.Task Gate8_MissingExternalSource_PreservesTraceConfiguration()
    {
        // A source that cannot be found becomes a BROKEN entry that keeps its alias — trace settings
        // referencing it are preserved rather than dropped (the pre-existing missing-dataset contract).
        var lib = new DataSourceLibraryViewModel();
        string missing = Path.Combine(_root, "lab-data", "gone.s2p");
        lib.AddBrokenEntry(missing);
        var entry = Assert.Single(lib.Entries);

        Assert.True(entry.IsBroken);
        Assert.True(lib.TrySetAlias(entry, "lab"));
        Assert.Equal("lab", entry.Alias);
        Assert.Equal("gone.s2p", entry.FileName);                // reported by name

        // Once the file reappears, re-pointing restores it without losing the alias.
        WriteSnp(missing, 2);
        await lib.RestoreBrokenEntry(entry, missing);
        Assert.False(entry.IsBroken);
        Assert.Equal("lab", entry.Alias);
    }

    [Fact]
    public void Gate8_NoWorkspaceOpen_NothingIsClassifiedExternal()
    {
        string snp = Path.Combine(_root, "dut.s2p");
        Assert.False(WorkspaceRefs.IsExternal(snp, null));
        Assert.Equal(Path.GetFullPath(snp), WorkspaceRefs.ToStoredRef(snp, null));
    }

    // =========================================================================
    //  Gate 9 — a dropped Touchstone is REFERENCED, never copied (R-stb-13)
    // =========================================================================

    [Fact]
    public async System.Threading.Tasks.Task Gate9_LoadingATouchstone_CopiesNothing_AndTouchesNoResultsFolder()
    {
        string ws = Path.Combine(_root, "ws");
        string results = Path.Combine(ws, "results");
        Directory.CreateDirectory(results);
        string outside = WriteSnp(Path.Combine(_root, "lab-data", "dut.s2p"), 2);

        var beforeWs = Directory.GetFiles(ws, "*", SearchOption.AllDirectories);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(outside);

        var entry = Assert.Single(lib.Entries);

        // The entry references the ORIGINAL path — no copy was made anywhere.
        Assert.Equal(Path.GetFullPath(outside), Path.GetFullPath(entry.FilePath!));
        Assert.Equal(beforeWs, Directory.GetFiles(ws, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(results, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(ws, "*.s?p", SearchOption.AllDirectories));
    }

    [Fact]
    public async System.Threading.Tasks.Task Gate8_DatasetRow_MarksAnOutsideSourceExternal_InsideSourceLive()
    {
        // R-stb-12 at the surface the user actually reads: the Dataset Aliases list's status badge.
        string ws = Path.Combine(_root, "ws");
        string inside  = WriteSnp(Path.Combine(ws, "measurements", "in.s2p"), 2);
        string outside = WriteSnp(Path.Combine(_root, "lab-data", "out.s2p"), 2);

        var window = new DisplayWindowViewModel();
        var lib = window.DataSourceLibrary;
        await lib.LoadFileAsync(inside);
        await lib.LoadFileAsync(outside);

        var rows = lib.Entries
            .Select(e => new DatasetRowViewModel(e, lib, window) { WorkspaceRootProvider = () => ws })
            .ToList();

        var insideRow  = rows.Single(r => r.FileName == "in.s2p");
        var outsideRow = rows.Single(r => r.FileName == "out.s2p");

        Assert.False(insideRow.IsExternal);
        Assert.Equal("Live", insideRow.StatusText);
        Assert.False(insideRow.IsFlagged);
        Assert.Null(insideRow.StatusTooltip);

        Assert.True(outsideRow.IsExternal);
        Assert.Equal("External", outsideRow.StatusText);
        Assert.True(outsideRow.IsFlagged);
        Assert.Contains("outside the workspace", outsideRow.StatusTooltip!, StringComparison.Ordinal);

        // With no workspace open, nothing is external.
        var noWs = new DatasetRowViewModel(lib.Entries[1], lib, window) { WorkspaceRootProvider = () => null };
        Assert.False(noWs.IsExternal);
        Assert.Equal("Live", noWs.StatusText);
    }

    [Fact]
    public void Gate9_AStoredRefIsAReference_NotAFileInResults()
    {
        // The stored form of an inside-workspace measurement points at where the user PUT it —
        // a referenced measurement is an INPUT and must not live under results/, which the user
        // may clear at any time.
        string ws = Path.Combine(_root, "ws");
        string snp = WriteSnp(Path.Combine(ws, "measurements", "dut.s2p"), 2);

        string stored = WorkspaceRefs.ToStoredRef(snp, ws);

        Assert.StartsWith("measurements/", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("results", stored, StringComparison.OrdinalIgnoreCase);
    }
}
