using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A kit's parts are held in memory and the workspace holds only a reference, so the reference IS the
/// dependency — and a dependency nobody can see or repair is worse than one that was copied in. These
/// cover what the Manage PDKs dialog is a presentation of: the state each reference is in, what
/// validating one finds, and that adding, repairing and removing behave the way the design says.
///
/// <para>Fixtures name no vendor and no part: a symbol file is a FORMAT, so a synthetic kit exercises
/// exactly the code a real one does.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkReferenceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-refmgr-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// The folder is named for the kit ON PURPOSE: an ordinary kit import takes its name from the
    /// folder it was imported from, and that name is what every placed part references.
    /// </summary>
    private string KitDir       => Path.Combine(_root, "SampleKit");
    private string WorkspaceDir => Path.Combine(_root, "ws");

    public PdkReferenceManagerTests()
    {
        PdkKitRegistry.Clear();
        Directory.CreateDirectory(WorkspaceDir);
    }

    public void Dispose()
    {
        PdkKitRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string SymbolFile = """
        1     7.707    0 0
        10    1    "PART_SYM"    2    1    0    0    341    0
        20    0    ""    0 0 0 0 0    2 -3 1    1    0    "schematic.prf" "schematic.lay"
        44    0    -600    600    600    1    0    0
        50    2    0 0 500 0 1    0    0    0    0    0    0    0    0
        60    4    0    2    0 0 500 0 1    0    0    0    0
        70    0 0    500 0
        42    1    2    "gate"      1    2    0    0 0 180000    0    0   ""
        42    2    2    "drain"     2    1    0    500 0 0    0    0   ""
        21
        """;

    private const string LibraryDeviceType = "CRF_LIBONLY_V1";

    /// <summary>
    /// A shared library PACKAGE: the compiled models and nothing else. This is the shape a vendor
    /// delivery actually has — several part kits beside one of these.
    /// </summary>
    private static void WriteLibraryPackage(string dir)
    {
        string buildDir = Path.Combine(dir, "linux_x86_64");
        Directory.CreateDirectory(buildDir);
        File.WriteAllBytes(Path.Combine(buildDir, "models.so"),
        [
            0x7F, (byte)'E', (byte)'L', (byte)'F',
            .. System.Text.Encoding.ASCII.GetBytes(
                DeviceLibraryDiscovery.Profiles[0].ExportPrefix + LibraryDeviceType + "\0"),
        ]);

        var worker = new byte[64];
        worker[0] = 0x7F; worker[1] = (byte)'E'; worker[2] = (byte)'L'; worker[3] = (byte)'F';
        File.WriteAllBytes(Path.Combine(dir, DeviceLibraryDiscovery.Profiles[0].Worker), worker);
    }

    /// <summary>A part kit whose devices are COMPILED — it needs a library it does not itself hold.</summary>
    private static void WriteKitNeedingALibrary(string dir)
    {
        string viewDir = Path.Combine(dir, "PART_A", "symbol");
        Directory.CreateDirectory(viewDir);
        File.WriteAllText(Path.Combine(viewDir, "PART_A.dsn"), SymbolFile);

        string netDir = Path.Combine(dir, "circuit", "models");
        Directory.CreateDirectory(netDir);
        File.WriteAllText(Path.Combine(netDir, "kit.net"), $"""
            define PART_A ( g d s )
              {LibraryDeviceType}:M1  g d s
            end PART_A
            """);
    }

    /// <summary>The same drawing with its pin records removed — a title block, not a part.</summary>
    private const string PinlessSymbolFile = """
        1     7.707    0 0
        10    1    "TITLE_SYM"    2    1    0    0    341    0
        20    0    ""    0 0 0 0 0    2 -3 1    1    0    "schematic.prf" "schematic.lay"
        44    0    -600    600    600    1    0    0
        50    2    0 0 500 0 1    0    0    0    0    0    0    0    0
        60    4    0    2    0 0 500 0 1    0    0    0    0
        70    0 0    500 0
        21
        """;

    /// <summary>
    /// Writes a kit holding the named parts, at <paramref name="dir"/>. The
    /// <c>&lt;cell&gt;/&lt;view&gt;/&lt;file&gt;</c> shape is what makes a drawing a PART rather than
    /// a picture — the importer reads that structure, not a filename convention.
    /// </summary>
    private static void WriteKit(string dir, params string[] partIds)
    {
        foreach (string id in partIds)
        {
            string viewDir = Path.Combine(dir, id, "symbol");
            Directory.CreateDirectory(viewDir);
            File.WriteAllText(Path.Combine(viewDir, id + ".dsn"), SymbolFile);
        }

        // A manifest is what names the parts. Without one the importer has only the folder, and what
        // this file is about is the REFERENCE, not discovery.
        File.WriteAllText(Path.Combine(dir, "device-provider.json"), $$"""
            {
              "provider": "SampleKit",
              "workers":  [ { "platform": "any", "command": "worker" } ]
            }
            """);
    }

    /// <summary>Adds a reference the way the dialog's Add button does.</summary>
    private (List<CwsPdkRef> Refs, string Name) Added(string kitDir)
    {
        var refs = new List<CwsPdkRef>();
        var outcome = PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, kitDir, out string? problem);

        Assert.Null(problem);
        Assert.NotNull(outcome);
        return (refs, outcome!.KitName);
    }

    // ── What a reference is ───────────────────────────────────────────────────

    [Fact]
    public void AddingAKit_RecordsWhereItIsAndWhatWasSettled_AndLoadsItsParts()
    {
        WriteKit(KitDir, "PART_A", "PART_B");

        var (refs, name) = Added(KitDir);

        var r = Assert.Single(refs);
        Assert.Equal("SampleKit", name);
        Assert.Equal("SampleKit", r.Provider);
        Assert.Equal(DsnSymbolReader.TranslationVersion, r.TranslationVersion);
        Assert.NotNull(r.Settings);
        Assert.Equal(2, PdkKitRegistry.PartsOf("SampleKit").Count);
    }

    [Fact]
    public void AKitOutsideTheWorkspace_IsStoredAbsolute_AndSaysSo()
    {
        // No encoding can make an outside reference portable, so it is stored plainly and the user is
        // told — the alternative is a colleague meeting it as a dangling path with no explanation.
        WriteKit(KitDir, "PART_A");

        var (refs, _) = Added(KitDir);
        var status = Assert.Single(PdkReferenceManager.Describe(WorkspaceDir, refs));

        Assert.True(Path.IsPathRooted(refs[0].Path));
        Assert.True(status.IsExternal);
    }

    [Fact]
    public void AKitInsideTheWorkspace_IsStoredRelative_SoTheWholeFolderCanMove()
    {
        string inside = Path.Combine(WorkspaceDir, "kits", "SampleKit");
        WriteKit(inside, "PART_A");

        var (refs, _) = Added(inside);

        Assert.False(Path.IsPathRooted(refs[0].Path));
        Assert.DoesNotContain('\\', refs[0].Path);   // normalized, so it survives a platform crossing
        Assert.False(Assert.Single(PdkReferenceManager.Describe(WorkspaceDir, refs)).IsExternal);
    }

    // ── State ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AReachableKitWithItsPartsLoaded_ReadsOk()
    {
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);

        var status = Assert.Single(PdkReferenceManager.Describe(WorkspaceDir, refs));

        Assert.Equal(PdkReferenceManager.RefState.Ok, status.State);
        Assert.Equal(1, status.PartsLoaded);
        Assert.Equal("", status.Detail);
    }

    [Fact]
    public void AKitThatIsNoLongerThere_ReadsMissing_AndSaysWhatToDo()
    {
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);
        Directory.Delete(KitDir, recursive: true);

        var status = Assert.Single(PdkReferenceManager.Describe(WorkspaceDir, refs));

        Assert.Equal(PdkReferenceManager.RefState.Missing, status.State);
        Assert.NotEqual("", status.Detail);
    }

    [Fact]
    public void AReferenceTranslatedByAnOlderReader_ReadsAsNeedingAttention_NotAsMissing()
    {
        // The two are different problems and send the user to different places. A reader change moves
        // pins, so this is deliberately NOT re-translated on its own.
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);
        refs[0].TranslationVersion = DsnSymbolReader.TranslationVersion + 1;

        var status = Assert.Single(PdkReferenceManager.Describe(WorkspaceDir, refs));

        Assert.Equal(PdkReferenceManager.RefState.Drifted, status.State);
        Assert.Contains("older reader", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribingReferences_LoadsNothing()
    {
        // Asking what a workspace depends on must not change what is loaded, or the answer moves as a
        // side effect of asking it.
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);
        PdkKitRegistry.Clear();

        var status = Assert.Single(PdkReferenceManager.Describe(WorkspaceDir, refs));

        Assert.Equal(0, status.PartsLoaded);
        Assert.Empty(PdkKitRegistry.LoadedKits);
    }

    // ── Validate ──────────────────────────────────────────────────────────────

    [Fact]
    public void ValidatingAHealthyReference_FindsNothing_AndSaysWhatItChecked()
    {
        // "No problems found" on its own cannot be told apart from a check that did nothing, which is
        // the one thing a validation must not be ambiguous about.
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);

        var result = PdkReferenceManager.Validate(
            WorkspaceDir, refs[0], [PdkKitRegistry.RefFor("SampleKit", "PART_A")]);

        Assert.True(result.IsClean);
        Assert.Empty(result.Problems);
        Assert.Equal(1, result.PartsOffered);
        Assert.Equal(1, result.PlacedChecked);
        Assert.Contains("1 part offered",        result.Summary, StringComparison.Ordinal);
        Assert.Contains("1 placed part checked", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatingReportsDrift_NotJustBreakage()
    {
        // The case this exists for: the kit still loads, so nothing is obviously wrong — but a part
        // the design placed is no longer in it, which otherwise surfaces as an unresolved component
        // with no indication of when it stopped resolving.
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);

        var result = PdkReferenceManager.Validate(
            WorkspaceDir, refs[0], [PdkKitRegistry.RefFor("SampleKit", "PART_GONE")]);

        Assert.False(result.IsClean);
        Assert.Contains(result.Problems, p => p.Contains("PART_GONE", StringComparison.Ordinal));
        Assert.Contains("1 problem", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatingIgnoresPartsPlacedFromADifferentKit()
    {
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);

        var result = PdkReferenceManager.Validate(
            WorkspaceDir, refs[0], [PdkKitRegistry.RefFor("OtherKit", "PART_X")]);

        Assert.True(result.IsClean);
        Assert.Equal(0, result.PlacedChecked);   // and it says so, rather than implying it checked one
    }

    [Fact]
    public void ValidatingAMissingKit_SaysSoWithoutThrowing()
    {
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);
        Directory.Delete(KitDir, recursive: true);

        var result = PdkReferenceManager.Validate(WorkspaceDir, refs[0], []);

        Assert.False(result.IsClean);
        Assert.Equal(-1, result.PartsOffered);   // could not be read, which is not "offers nothing"
        Assert.Contains("could not be read", result.Summary, StringComparison.Ordinal);
    }

    // ── Repair and removal ────────────────────────────────────────────────────

    [Fact]
    public void RepairingAMovedKit_ReconnectsEveryPlacedPart_WithoutTouchingASchematic()
    {
        // The payoff of referencing a part by kit NAME rather than by path: the same name at a new
        // path resolves every placed instance again, and no design was edited to do it.
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);
        string cellRef = PdkKitRegistry.RefFor("SampleKit", "PART_A");

        // Moved, not renamed: the kit's NAME is what placed parts reference, and an ordinary kit
        // takes its name from its folder — so a rename is a different kit, not a moved one.
        string moved = Path.Combine(_root, "elsewhere", "SampleKit");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(KitDir, moved);
        PdkKitRegistry.Clear();
        Assert.Null(PdkKitRegistry.Find(cellRef));

        var outcome = PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, moved, out string? problem);

        Assert.Null(problem);
        Assert.Equal("SampleKit", outcome!.KitName);
        Assert.Single(refs);                                   // repaired, never a second entry
        Assert.NotNull(PdkKitRegistry.Find(cellRef));           // the placed part resolves again

        // The caller needs these to put the kit back in the palette — the registry holds neither the
        // icon nor the search terms a palette entry carries.
        Assert.NotEmpty(outcome.Items);
    }

    [Fact]
    public void AddingAFolderThatIsNotAKit_IsRefusedWithAReason_AndAddsNothing()
    {
        string empty = Path.Combine(_root, "not-a-kit");
        Directory.CreateDirectory(empty);

        var refs = new List<CwsPdkRef>();
        var outcome = PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, empty, out string? problem);

        Assert.Null(outcome);
        Assert.NotNull(problem);
        Assert.Empty(refs);
    }

    [Fact]
    public void RemovingAReference_DropsItAndItsParts_AndReportsWhatItLeavesUnresolved()
    {
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);
        string cellRef = PdkKitRegistry.RefFor("SampleKit", "PART_A");

        int affected = PdkReferenceManager.Remove(refs, "SampleKit", [cellRef]);

        Assert.Equal(1, affected);
        Assert.Empty(refs);
        Assert.False(PdkKitRegistry.HasKit("SampleKit"));
    }

    [Fact]
    public void RemovingThenAddingBack_ResolvesThePlacedPartsAgain()
    {
        // Which is why removal is warned rather than blocked: nothing is deleted from any schematic.
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);
        string cellRef = PdkKitRegistry.RefFor("SampleKit", "PART_A");

        PdkReferenceManager.Remove(refs, "SampleKit", [cellRef]);
        Assert.Null(PdkKitRegistry.Find(cellRef));

        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, KitDir, out _);

        Assert.NotNull(PdkKitRegistry.Find(cellRef));
    }

    // ── What .cws carries ─────────────────────────────────────────────────────

    [Fact]
    public void TheWorkspaceRecordsTheDecisions_NeverTheTranslatedContent()
    {
        // The whole point of the design: what circuitRF DECIDED is small, carries no geometry, and is
        // the difference between a workspace that opens the same way twice and one that quietly
        // re-decides. What it TRANSLATED is the vendor's and is rebuilt in memory every open.
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);

        string cws = Path.Combine(WorkspaceDir, ".cws");
        WorkspacePersistence.SaveToFileAtomic(cws, new CwsFile { PdkRefs = refs });

        string json = File.ReadAllText(cws);
        Assert.DoesNotContain("\"Pins\"",      json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Primitives\"", json, StringComparison.Ordinal);

        var reloaded = Assert.Single(WorkspacePersistence.LoadFromFile(cws).PdkRefs!);
        Assert.Equal(refs[0].Provider,           reloaded.Provider);
        Assert.Equal(refs[0].Path,               reloaded.Path);
        Assert.Equal(refs[0].TranslationVersion, reloaded.TranslationVersion);
        Assert.NotNull(reloaded.Settings);
    }

    [Fact]
    public void AWorkspaceWithNoKits_WritesNoPdkBlockAtAll()
    {
        // Purely additive: a workspace that never touched a PDK is byte-identical to before.
        string cws = Path.Combine(WorkspaceDir, ".cws");
        WorkspacePersistence.SaveToFileAtomic(cws, new CwsFile());

        Assert.DoesNotContain("PdkRefs", File.ReadAllText(cws), StringComparison.OrdinalIgnoreCase);
        Assert.Null(WorkspacePersistence.LoadFromFile(cws).PdkRefs);
    }

    // ── What the settings note actually says ──────────────────────────────────

    /// <summary>
    /// A count of platform entries ("3 ways to evaluate its devices") reads as three alternative
    /// methods when it is one method described for three operating systems — and it buries the only
    /// thing the user needs, which is whether their own machine is one of them.
    /// </summary>
    [Fact]
    public void TheSettingsNote_NamesThePlatforms_AndWhetherThisMachineIsOne()
    {
        WriteKit(KitDir, "PART_A");   // its manifest declares platform "any"

        var outcome = PdkPartInstaller.Install(PdkImporter.Import(KitDir));
        string note = Assert.Single(outcome.Notes ?? [], n => n.Contains("simulation settings", StringComparison.Ordinal));

        Assert.Contains("this machine", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("way(s)",  note, StringComparison.Ordinal);
    }

    [Fact]
    public void AKitBuiltForOtherPlatforms_SaysSoAtImport_NotAtRun()
    {
        // Holding a kit for another operating system is completely ordinary, and pressing Run on it is
        // completely useless. Saying which platforms it covers here beats a failure to start a program
        // much later.
        Directory.CreateDirectory(Path.Combine(KitDir, "PART_A", "symbol"));
        File.WriteAllText(Path.Combine(KitDir, "PART_A", "symbol", "PART_A.dsn"), SymbolFile);
        File.WriteAllText(Path.Combine(KitDir, "device-provider.json"), """
            {
              "provider": "SampleKit",
              "workers":  [ { "platform": "some-other-platform", "command": "worker" } ]
            }
            """);

        var outcome = PdkPartInstaller.Install(PdkImporter.Import(KitDir));
        string note = Assert.Single(outcome.Notes ?? [], n => n.Contains("simulation settings", StringComparison.Ordinal));

        Assert.Contains("some-other-platform",  note, StringComparison.Ordinal);
        Assert.Contains("NOT on this machine",  note, StringComparison.Ordinal);
    }

    [Fact]
    public void CountsAreNotWrittenWithAParentheticalS()
    {
        // "1 model-selection parameter(s)" is a template showing through. Pluralised properly.
        Directory.CreateDirectory(Path.Combine(KitDir, "PART_A", "symbol"));
        File.WriteAllText(Path.Combine(KitDir, "PART_A", "symbol", "PART_A.dsn"), SymbolFile);
        File.WriteAllText(Path.Combine(KitDir, "device-provider.json"), """
            {
              "provider": "SampleKit",
              "workers":  [ { "platform": "any", "command": "worker" } ],
              "variants": [ { "parameter": "ModelAs", "choices": ["A","B"], "default": "A" } ]
            }
            """);

        var outcome = PdkPartInstaller.Install(PdkImporter.Import(KitDir));
        string note = Assert.Single(outcome.Notes ?? [], n => n.Contains("simulation settings", StringComparison.Ordinal));

        Assert.Contains("1 model-selection parameter", note, StringComparison.Ordinal);
        Assert.DoesNotContain("(s)", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A drawing with no pins is very often exactly what it looks like — a title block or an
    /// annotation a kit draws alongside its real parts — and the reader cannot tell one from the
    /// other. Reporting it as a PROBLEM turns an ordinary kit into one that fails validation, and a
    /// validation that cries wolf is one nobody reads.
    /// </summary>
    [Fact]
    public void APinlessSymbol_IsANote_NotAProblem()
    {
        Directory.CreateDirectory(Path.Combine(KitDir, "TITLE_BLOCK", "symbol"));
        File.WriteAllText(Path.Combine(KitDir, "TITLE_BLOCK", "symbol", "TITLE_BLOCK.dsn"), PinlessSymbolFile);
        File.WriteAllText(Path.Combine(KitDir, "device-provider.json"), """
            { "provider": "SampleKit", "workers": [ { "platform": "any", "command": "worker" } ] }
            """);

        var refs = new List<CwsPdkRef>();
        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, KitDir, out _);

        var result = PdkReferenceManager.Validate(WorkspaceDir, refs[0], []);

        Assert.True(result.IsClean, "a pin-less drawing failed validation: " + string.Join("; ", result.Problems));
        Assert.Contains(result.Notes, n => n.Contains("cannot be wired", StringComparison.Ordinal));
    }

    /// <summary>The same drawing, with pins, says nothing — so the note above is not just always on.</summary>
    [Fact]
    public void ASymbolWithPins_SaysNothingAboutWiring()
    {
        WriteKit(KitDir, "PART_A");
        var (refs, _) = Added(KitDir);

        var result = PdkReferenceManager.Validate(WorkspaceDir, refs[0], []);

        Assert.DoesNotContain(result.Notes,    n => n.Contains("cannot be wired", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Problems, n => n.Contains("cannot be wired", StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether sharing a workspace carries a kit depends on WHICH kit — one inside the tree travels
    /// with it, one outside does not. A blanket line covering both is wrong for one of them, and the
    /// dialog said "sharing the workspace does not carry the kit" over a kit sitting inside it.
    ///
    /// <para>Pinned by source scan: the dialog is a <c>Window</c> and cannot be constructed headlessly,
    /// which is this suite's standing answer for chrome that needs a live Avalonia host.</para>
    /// </summary>
    [Fact]
    public void TheDialogMakesNoBlanketClaimAboutSharing()
    {
        // Comments stripped first: the markup explains WHY it makes no such claim, and scanning the
        // explanation would fail on the very sentence documenting the rule.
        string axaml = StripXmlComments(
            ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "ManagePdksDialog.axaml")));

        // Only the per-row detail may say it, and that lives in the code-behind.
        Assert.DoesNotContain("carry",   axaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sharing", axaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothSharingOutcomesAreStated_NotJustTheBadOne()
    {
        // "Will this survive being shared" is the question this dialog exists to answer, and silence
        // is not an answer — so the inside case says so too.
        string code = ReadRepoFile(Path.Combine(
            "src", "Ui", "Views", "Dialogs", "ManagePdksDialog.axaml.cs"));

        Assert.Contains("sharing the workspace does not carry", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sharing the workspace carries it",     code, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripXmlComments(string markup)
        => System.Text.RegularExpressions.Regex.Replace(
               markup, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    // ── The shared library package ────────────────────────────────────────────
    //
    // A vendor delivery is several part kits beside ONE package holding the compiled models.
    // Discovery finds that package by ADJACENCY — so referencing a kit from anywhere else (a
    // workspace, say) breaks the link with nothing on disk left to recover it from.

    [Fact]
    public void APackageWithNoParts_ButWithModelLibraries_IsAccepted()
    {
        // It used to be refused for having no placeable parts, which left the user no way at all to
        // say where the models were.
        string pkg = Path.Combine(_root, "SharedModels");
        WriteLibraryPackage(pkg);

        var refs = new List<CwsPdkRef>();
        var outcome = PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, pkg, out string? problem);

        Assert.Null(problem);
        Assert.NotNull(outcome);

        var r = Assert.Single(refs);
        Assert.True(r.IsLibraryOnly);
        Assert.Equal("SharedModels", r.Provider);
    }

    [Fact]
    public void APackageWithNeitherPartsNorLibraries_IsStillRefused()
    {
        // The relaxation is not "accept any folder" — that would let a mistyped path look like it worked.
        string empty = Path.Combine(_root, "nothing-useful");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "readme.txt"), "not a library");

        var refs = new List<CwsPdkRef>();
        Assert.Null(PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, empty, out string? problem));
        Assert.NotNull(problem);
        Assert.Empty(refs);
    }

    /// <summary>
    /// The reported case, end to end: a kit sitting INSIDE the workspace and its library package well
    /// outside it, far enough that no ancestor walk could ever reach it. Referencing the package is
    /// what makes the kit simulable again.
    /// </summary>
    [Fact]
    public void AKitInsideTheWorkspace_FindsItsModels_OnceThePackageIsReferenced()
    {
        string kit = Path.Combine(WorkspaceDir, "kits", "SampleKit");
        WriteKitNeedingALibrary(kit);

        string pkg = Path.Combine(_root, "far", "away", "deeper", "SharedModels");
        WriteLibraryPackage(pkg);

        // Without the package referenced, the kit settles on nothing and says why.
        var refs = new List<CwsPdkRef>();
        var alone = PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, kit, out _);
        Assert.NotNull(alone);
        Assert.Null(alone!.Settings);
        Assert.Contains(alone.Diagnostics, d => d.Contains("Manage PDKs", StringComparison.Ordinal));

        // Reference the package, re-add the kit: it now finds the models.
        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, pkg, out _);
        var withPackage = PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, kit, out string? problem);

        Assert.Null(problem);
        Assert.NotNull(withPackage!.Settings);

        var manifest = PdkPartInstaller.ManifestFrom(withPackage.Settings, kit, withPackage.KitName);
        Assert.NotNull(manifest);
        Assert.All(manifest!.Launches,
            l => Assert.Contains("models.so", string.Join(" ", l.Arguments), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ALibraryPackage_ReadsOk_NotAsDriftedForHavingNoParts()
    {
        // It holds no parts BY DEFINITION, so "no parts loaded" is its healthy state — reporting that
        // as drift would leave every such reference permanently flagged.
        string pkg = Path.Combine(_root, "SharedModels");
        WriteLibraryPackage(pkg);

        var refs = new List<CwsPdkRef>();
        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, pkg, out _);

        var status = Assert.Single(PdkReferenceManager.Describe(WorkspaceDir, refs));

        Assert.Equal(PdkReferenceManager.RefState.Ok, status.State);
        Assert.Equal(0, status.PartsLoaded);
        Assert.Contains("Model libraries only", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatingALibraryPackage_ChecksItStillHoldsALibrary()
    {
        string pkg = Path.Combine(_root, "SharedModels");
        WriteLibraryPackage(pkg);

        var refs = new List<CwsPdkRef>();
        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, pkg, out _);
        Assert.True(PdkReferenceManager.Validate(WorkspaceDir, refs[0], []).IsClean);

        // Emptied out: still a folder, no longer a library package.
        foreach (string f in Directory.EnumerateFiles(pkg, "*", SearchOption.AllDirectories))
            File.Delete(f);

        var after = PdkReferenceManager.Validate(WorkspaceDir, refs[0], []);
        Assert.False(after.IsClean);
        Assert.Contains(after.Problems, p => p.Contains("model library", StringComparison.OrdinalIgnoreCase));
    }

    // ── Selecting nothing is a state ──────────────────────────────────────────
    //
    // Pinned by source scan: the dialog is a Window and cannot be constructed headlessly. These are
    // orderings and defaults that are invisible when wrong — the panel simply appears empty — so they
    // are worth holding in place.

    private static string DialogCode() => ReadRepoFile(System.IO.Path.Combine(
        "src", "Ui", "Views", "Dialogs", "ManagePdksDialog.axaml.cs"));

    [Fact]
    public void ClickingEmptySpaceClearsTheSelection()
    {
        string code = DialogCode();

        Assert.Contains("OnListPointerPressed",          code, StringComparison.Ordinal);
        Assert.Contains("FindAncestorOfType<ListBoxItem>", code, StringComparison.Ordinal);

        // Wired in the markup, or the handler is dead code that looks alive.
        string axaml = ReadRepoFile(System.IO.Path.Combine(
            "src", "Ui", "Views", "Dialogs", "ManagePdksDialog.axaml"));
        Assert.Contains("PointerPressed=\"OnListPointerPressed\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFirstOpenFallsBackToSelectingAKit()
    {
        // Re-selecting for the user on a later rebuild would undo a deliberate "none of these".
        string code = DialogCode();

        Assert.Contains("Refresh(selectFirst: true)", code, StringComparison.Ordinal);
        Assert.Contains("bool selectFirst = false",   code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Changing the selection clears the result panel, so an action that shows a result AND changes the
    /// selection has to select first. Get it backwards and the result is wiped a line after it appears
    /// — with nothing failing anywhere.
    /// </summary>
    [Fact]
    public void AddSelectsTheNewKitBeforeShowingItsResult()
    {
        string code = DialogCode();

        int add = code.IndexOf("private async void OnAddClick", StringComparison.Ordinal);
        Assert.True(add >= 0, "OnAddClick was renamed; re-point this scan.");

        int end  = code.IndexOf("\n    private async void OnRemoveClick", add, StringComparison.Ordinal);
        string body = end > add ? code[add..end] : code[add..];

        int select = body.IndexOf("RefList.SelectedIndex = IndexOfProvider", StringComparison.Ordinal);
        int show   = body.LastIndexOf("ShowResult(", StringComparison.Ordinal);

        Assert.True(select >= 0 && show > select,
            "OnAddClick shows its result before selecting the new kit, so the selection change wipes it.");
    }

    // ── The settled settings must reach the RESOLVER ──────────────────────────
    //
    // Everything above checks what Install returns. None of it catches the settings being computed
    // correctly and then not handed on — which is what a kit part actually needs at Run, and which
    // failed in exactly that way: the manifest was built from the RECORDED settings rather than the
    // settled ones, so a kit that had to derive them registered nothing and every part failed with
    // "no kit settled on a way to evaluate its devices".

    /// <summary>Mirrors what a workspace open does with a reference, and returns what it would register.</summary>
    private DeviceWorkerManifest? ManifestRegisteredFor(CwsPdkRef r, IReadOnlyList<string> libraryRoots)
    {
        string kitPath = WorkspaceRefs.Resolve(r.Path, WorkspaceDir);
        var outcome = PdkPartInstaller.Install(PdkImporter.Import(kitPath), r.Settings, libraryRoots);

        return PdkPartInstaller.ManifestFrom(outcome.Settings, kitPath, r.Provider);
    }

    [Fact]
    public void AKitThatHadToDeriveItsSettings_StillRegistersAManifest()
    {
        // The reported failure. Nothing is recorded for this kit, so the settings are worked out on
        // the spot — and building the manifest from the recorded null left the resolver empty.
        string kit = Path.Combine(WorkspaceDir, "kits", "SampleKit");
        WriteKitNeedingALibrary(kit);

        string pkg = Path.Combine(_root, "far", "away", "SharedModels");
        WriteLibraryPackage(pkg);

        var refs = new List<CwsPdkRef>();
        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, pkg, out _);
        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, kit, out _);

        var kitRef = Assert.Single(refs, r => !r.IsLibraryOnly);
        kitRef.Settings = null;      // as a reference recorded before the package existed

        var manifest = ManifestRegisteredFor(
            kitRef, PdkReferenceManager.LibraryRootsIn(WorkspaceDir, refs));

        Assert.NotNull(manifest);
        Assert.Equal("SampleKit", manifest!.ProviderName);
        Assert.NotEmpty(manifest.Launches);
    }

    [Fact]
    public void WithNoLibraryAnywhere_NothingIsRegistered_RatherThanAnEmptyManifest()
    {
        // The honest negative: a manifest naming nothing would start a worker that cannot load a model.
        string kit = Path.Combine(WorkspaceDir, "kits", "SampleKit");
        WriteKitNeedingALibrary(kit);

        var refs = new List<CwsPdkRef>();
        PdkReferenceManager.AddOrRepair(WorkspaceDir, refs, kit, out _);

        Assert.Null(ManifestRegisteredFor(Assert.Single(refs), []));
    }
}
