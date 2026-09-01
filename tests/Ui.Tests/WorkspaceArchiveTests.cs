using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Ui.Archive;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// File ▸ Archive Workspace… / Unarchive Workspace… (owner request, 2026-08-15) — "an easy way for a
/// user to share workspaces with other users on different machines".
///
/// <para>The whole decision layer is framework-free, so these run against real folders on disk with
/// no window anywhere: what is skipped, what is offered, what each default is, and — the part that
/// actually makes an archive portable — that every reference the user chose to include is repointed
/// at the copy inside the archive.</para>
/// </summary>
public sealed class WorkspaceArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-archive-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private string File_(string relative, string content)
    {
        var p = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    /// <summary>A workspace with a cell, results of every category, and the usual OS clutter.</summary>
    private string BuildWorkspace(string name = "ws", string? cwsJson = null)
    {
        var ws = Dir(name);
        System.IO.File.WriteAllText(Path.Combine(ws, ".cws"),
            cwsJson ?? """{"FormatVersion":2,"LibraryRefs":[],"KnownFiles":[]}""");

        File_($"{name}/Amp/.ccell", """{"FormatVersion":1,"PrimarySchematic":"Amp.csch"}""");
        File_($"{name}/Amp/schematic/Amp.csch", """{"FormatVersion":2,"CellName":"Amp.csch","Components":[]}""");
        File_($"{name}/tech/pcb.ctech", "{}");

        File_($"{name}/results/Amp.npy", new string('n', 100));
        File_($"{name}/results/Amp.cdd", "{}");
        File_($"{name}/results/em.s2p", "! touchstone");
        File_($"{name}/results/notes.txt", "x");

        // Clutter that must never reach the archive.
        File_($"{name}/.DS_Store", "junk");
        File_($"{name}/Amp/.DS_Store", "junk");
        File_($"{name}/.generated-cells/MLIN_abc/layout/MLIN_abc.clay", "{}");
        File_($"{name}/Amp/schematic/Amp.csch.crf-tmp-9", "half-written");

        return ws;
    }

    // ── What is always in, and what never is ──────────────────────────────────

    [Fact]
    public void EveryCellIsArchived_WithoutBeingAsked()
    {
        var ws = BuildWorkspace();

        var plan = WorkspaceArchiveScanner.Scan(ws);

        // The owner's own rule: "It is assumed that all the cells will be archived. The user expects
        // that." So a cell is never an OPTION — it is in the unconditional list.
        Assert.Contains("Amp/.ccell", plan.AlwaysIncluded);
        Assert.Contains("Amp/schematic/Amp.csch", plan.AlwaysIncluded);
        Assert.Contains("tech/pcb.ctech", plan.AlwaysIncluded);
        Assert.Contains(".cws", plan.AlwaysIncluded);
        Assert.DoesNotContain(plan.Options, o => o.SourcePath.EndsWith(".csch", StringComparison.Ordinal));
    }

    [Fact]
    public void TempAndOsClutterIsSkipped_AndSaidSo()
    {
        var plan = WorkspaceArchiveScanner.Scan(BuildWorkspace());

        Assert.DoesNotContain(plan.AlwaysIncluded, p => p.Contains(".DS_Store"));
        Assert.DoesNotContain(plan.AlwaysIncluded, p => p.Contains(".generated-cells"));
        Assert.DoesNotContain(plan.AlwaysIncluded, p => p.Contains(".crf-tmp"));
        Assert.Contains(plan.SkippedPaths, p => p.Contains(".DS_Store"));
        // `.generated-cells` is pruned at the DIRECTORY level rather than file by file — a vendor-
        // sized cache must not be walked just to list what was left out.
        Assert.Contains(plan.SkippedPaths, p => p.Contains(".crf-tmp"));
    }

    [Theory]
    [InlineData("Amp.cdd",     "Data Displays")]
    [InlineData("em.s2p",      "Touchstone")]
    [InlineData("x.s16p",      "Touchstone")]
    [InlineData("sweep.spl",   "Loadpull")]
    [InlineData("g.lpcwave",   "Loadpull")]
    [InlineData("Amp.npy",     "Analysis")]
    [InlineData("Amp.mat",     "Analysis")]
    [InlineData("notes.txt",   "Other")]
    public void ResultsAreGroupedByExtension_AndEveryGroupIsOnByDefault(string file, string group)
    {
        // Owner, 2026-09-01: a colleague could not run what they were sent, and a `.cdd` that arrives
        // with no `.npy` behind it renders nothing at all — so what is being SENT is the result, and
        // every results file is ticked. The earlier "the recipient can re-simulate it" default for
        // `.npy` assumed the recipient had the whole kit chain to re-simulate WITH.
        var (g, s) = WorkspaceArchiveScanner.ClassifyResult(file);
        Assert.Equal(group, g);
        Assert.True(s);
    }

    [Fact]
    public void TheResultsBranchIsBuiltFromTheRealFolder()
    {
        var plan = WorkspaceArchiveScanner.Scan(BuildWorkspace());

        var results = plan.Results.ToList();
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.StartsWith("results/", r.ArchivePath));
        Assert.All(results, r => Assert.True(r.Selected));         // every one, by default
        Assert.All(results, r => Assert.True(r.SizeBytes >= 0));   // a size to show the user
    }

    // ── Kits ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AReferencedKitIsOffered_OffByDefault_AndOnlyWhenItIsOutsideTheWorkspace()
    {
        var kit = Dir("vendorkit");
        File_("vendorkit/device-provider.json", "{}");
        var inside = "libs/inhouse";

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":["{{kit.Replace("\\", "/")}}","{{inside}}"],"KnownFiles":[]}
            """);
        File_($"ws/{inside}/x.clib", "{}");

        var plan = WorkspaceArchiveScanner.Scan(ws);

        var offered = Assert.Single(plan.Kits);
        Assert.Equal(kit, offered.SourcePath);
        Assert.False(offered.Selected);                   // the owner's default
        Assert.Equal("kits/vendorkit", offered.ArchivePath);
        // A kit already inside the workspace is not a question — it travels with everything else.
        Assert.DoesNotContain(plan.Kits, k => k.SourcePath.Contains("inhouse"));
    }

    [Fact]
    public void EachReferencedKitIsItsOwnRow_NamedByTheKit_NotByItsFolder()
    {
        // Owner request (2026-08-15, round 2): "broken up by actual named kits so user can choose
        // which kits to include in archive and which ones are to remain as references." The folders a
        // vendor's installer creates are named after builds and dates; the name the user knows a kit
        // by — and the name a placed part resolves through — is the PDK ref's own Provider.
        var kitA = Dir("SIM_2025_linux_x86_64_GCC1210");
        File_("SIM_2025_linux_x86_64_GCC1210/device-provider.json", "{}");
        var kitB = Dir("delivery-2024-11");
        File_("delivery-2024-11/models.so", "x");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":[],"KnownFiles":[],
             "PdkRefs":[
               {"Path":"{{kitA.Replace("\\", "/")}}","Provider":"RfPowerDesignKit","TranslationVersion":2},
               {"Path":"{{kitB.Replace("\\", "/")}}","Provider":"RfPowerModels","TranslationVersion":2,"IsLibraryOnly":true}]}
            """);

        var kits = WorkspaceArchiveScanner.Scan(ws).Kits.ToList();

        Assert.Equal(2, kits.Count);
        var partKit = Assert.Single(kits, k => k.SourcePath == kitA);
        Assert.Equal("RfPowerDesignKit", partKit.DisplayName);
        Assert.Contains(kitA, partKit.Detail);            // the path is still there, in the tooltip
        Assert.StartsWith("PDK ·", partKit.Detail);

        // A library-only reference supplies no parts — taking the kit but not this one gives a
        // workspace that opens and will not simulate, so the row says which one it is.
        var library = Assert.Single(kits, k => k.SourcePath == kitB);
        Assert.Equal("RfPowerModels (model library)", library.DisplayName);

        // Each row decides for itself: one in, one left as a reference.
        partKit.Selected = true;
        Assert.False(library.Selected);
    }

    [Fact]
    public void AKitReferencedTwice_IsOneRowCarryingBothNames()
    {
        // One copy is made, so one row — but titling it with only one of the two names would hide a
        // kit the user is deciding about.
        var kit = Dir("shared");
        File_("shared/device-provider.json", "{}");
        var p = kit.Replace("\\", "/");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":["{{p}}"],"KnownFiles":[],
             "PdkRefs":[{"Path":"{{p}}","Provider":"VendorKit","TranslationVersion":2}]}
            """);

        var row = Assert.Single(WorkspaceArchiveScanner.Scan(ws).Kits);
        Assert.Contains("shared", row.DisplayName);        // the LibraryRef's only name
        Assert.Contains("VendorKit", row.DisplayName);     // and the PDK's
        Assert.Equal("kits/shared", row.ArchivePath);      // still one copy, under its folder name
    }

    [Fact]
    public void AKitFolderKeepsItsFolderNameInsideTheArchive_NotTheKitName()
    {
        // A kit's own files reference each other against its folder; renaming it inside the archive
        // would be a change the kit never agreed to.
        var kit = Dir("vendor-build-7");
        File_("vendor-build-7/device-provider.json", "{}");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":[],"KnownFiles":[],
             "PdkRefs":[{"Path":"{{kit.Replace("\\", "/")}}","Provider":"NiceName","TranslationVersion":2}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        var row  = Assert.Single(plan.Kits);
        row.Selected = true;

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        Assert.Equal("NiceName", row.DisplayName);
        Assert.Contains("ws/kits/vendor-build-7/device-provider.json", EntryNames(zip));
        Assert.Equal("kits/vendor-build-7",
                     JsonNode.Parse(ReadEntry(zip, "ws/.cws"))!["PdkRefs"]![0]!["Path"]!.GetValue<string>());
    }

    [Fact]
    public void IncludingOneKitAndNotAnother_RepointsOnlyTheOneIncluded()
    {
        var taken = Dir("taken");
        File_("taken/device-provider.json", "{}");
        var left = Dir("left");
        File_("left/device-provider.json", "{}");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":[],"KnownFiles":[],
             "PdkRefs":[
               {"Path":"{{taken.Replace("\\", "/")}}","Provider":"Taken","TranslationVersion":2},
               {"Path":"{{left.Replace("\\", "/")}}","Provider":"Left","TranslationVersion":2}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        plan.Kits.Single(k => k.DisplayName == "Taken").Selected = true;

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var pdks = JsonNode.Parse(ReadEntry(zip, "ws/.cws"))!["PdkRefs"]!.AsArray();
        Assert.Equal("kits/taken", pdks[0]!["Path"]!.GetValue<string>());
        Assert.Equal(left,         pdks[1]!["Path"]!.GetValue<string>());   // stays a reference
        Assert.Contains(EntryNames(zip), n => n.StartsWith("ws/kits/taken/", StringComparison.Ordinal));
        Assert.DoesNotContain(EntryNames(zip), n => n.StartsWith("ws/kits/left/", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPermissionToRunAKitsScriptsTravelsInsideTheArchive()
    {
        // Owner question (2026-08-15): a kit that generates its artwork with Python is third-party
        // code, and the recipient — not the sender — has to agree to run it. Consent lives in the
        // RECIPIENT's own preferences.json, keyed by the kit's absolute directory, and is never
        // written into the workspace: an archive arriving with its scripts pre-marked trusted would
        // run them on open with no prompt, which defeats the mechanism entirely.
        var kit = Dir("vendorkit");
        File_("vendorkit/pcell-generators.json", """{"schemaVersion":1,"entry":"main.py"}""");
        File_("vendorkit/main.py", "print('artwork')");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":[],"KnownFiles":[],
             "PdkRefs":[{"Path":"{{kit.Replace("\\", "/")}}","Provider":"VendorKit","TranslationVersion":2}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        Assert.Single(plan.Kits).Selected = true;

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        // The scripts travel (the user chose to include the kit) …
        Assert.Contains("ws/kits/vendorkit/main.py", EntryNames(zip));
        // … but nothing that says they may RUN does.
        Assert.DoesNotContain(EntryNames(zip), n => n.Contains("preferences.json", StringComparison.OrdinalIgnoreCase));
        var cws = ReadEntry(zip, "ws/.cws");
        Assert.DoesNotContain("trust", cws, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePcellArtworkCacheIsNotArchived_SoTheRecipientRebuildsItUnderTheirOwnPermission()
    {
        // The owner asked for these to be skipped ("those pCell artwork files that are sometimes
        // generated"), and the trust model is the reason it is safe: a generated cell is a pure
        // cache, rebuilt from each layout's own recorded snapshots once the recipient allows the kit
        // to run. Until they do, those cells draw as placeholders — which is the honest state, not a
        // silent one (RequestPCellConsent says so).
        var ws = BuildWorkspace();
        File_("ws/.generated-cells/MLIN_9f2c/layout/MLIN_9f2c.clay", "{}");

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(WorkspaceArchiveScanner.Scan(ws), zip);

        Assert.DoesNotContain(EntryNames(zip), n => n.Contains(".generated-cells"));
    }

    [Fact]
    public void AnArchivedKitLandsWhereTheGeneratorScanCanFindIt()
    {
        // `PCellWorkerResolver` scans the workspace root and one level down. `kits/<kit>/` is two, so
        // the scan was taught to look through the kits folder — see
        // PCellTrustTests.AKitBundledInsideAnUnarchivedWorkspace_IsStillFound. This pins the other
        // half of that contract: the layout the archive actually writes.
        Assert.Equal("kits", WorkspaceArchiveScanner.KitsFolder);

        var kit = Dir("vendorkit");
        File_("vendorkit/pcell-generators.json", """{"schemaVersion":1,"entry":"main.py"}""");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":["{{kit.Replace("\\", "/")}}"],"KnownFiles":[]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        Assert.Single(plan.Kits).Selected = true;

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);
        var opened = WorkspaceArchiveExtractor.Extract(zip, Dir("landing"));

        // Exactly one level below the kits folder, which is what the scan reaches.
        Assert.True(File.Exists(Path.Combine(
            opened.WorkspaceDir, WorkspaceArchiveScanner.KitsFolder, "vendorkit", "pcell-generators.json")));
    }

    [Fact]
    public void AKitFolderIsMeasuredLazily_SoTheDialogCanOpenBeforeTheWalkFinishes()
    {
        var kit = Dir("vendorkit");
        File_("vendorkit/a.so", new string('x', 500));
        File_("vendorkit/sub/b.so", new string('x', 300));
        File_("vendorkit/.DS_Store", "junk");

        var ws   = BuildWorkspace("ws", $$"""{"FormatVersion":2,"LibraryRefs":["{{kit.Replace("\\", "/")}}"],"KnownFiles":[]}""");
        var plan = WorkspaceArchiveScanner.Scan(ws);

        Assert.Equal(-1, Assert.Single(plan.Kits).SizeBytes);      // not measured during the scan

        var bytes = WorkspaceArchiveScanner.MeasureDirectory(kit, out var complete);
        Assert.True(complete);
        Assert.Equal(800, bytes);                                  // and the skip rules apply to the total too
    }

    // ── External references ───────────────────────────────────────────────────

    [Fact]
    public void AnOutsideFileAKnownFileOrADocumentPointsAt_IsOffered_OnByDefault()
    {
        var outside = File_("elsewhere/underlay.png", "PNG");
        var touch   = File_("elsewhere/dut.s2p", "! touchstone");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":[],"KnownFiles":["{{outside.Replace("\\", "/")}}"]}
            """);

        File_("ws/Amp/layout/Amp.clay", $$"""
            {"FormatVersion":1,"Shapes":[{"$type":"Bitmap","ImagePathRef":"{{outside.Replace("\\", "/")}}"}]}
            """);
        File_("ws/Amp/schematic/Amp.csch", $$"""
            {"FormatVersion":2,"CellName":"Amp.csch","Components":[
              {"InstanceName":"S1","Symbol":"Snp","Parameters":[{"Name":"File","Expression":"{{touch.Replace("\\", "/")}}"}]}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);

        var names = plan.ExternalFiles.Select(e => Path.GetFileName(e.SourcePath)).ToList();
        Assert.Contains("underlay.png", names);
        Assert.Contains("dut.s2p", names);
        Assert.All(plan.ExternalFiles, e => Assert.True(e.Selected));
        Assert.All(plan.ExternalFiles, e => Assert.StartsWith("external/", e.ArchivePath));
    }

    [Fact]
    public void AReferenceThatAlreadyLivesInsideTheWorkspace_IsNotOffered()
    {
        var ws = BuildWorkspace();
        File_("ws/dut.s2p", "! touchstone");
        File_("ws/Amp/schematic/Amp.csch", """
            {"FormatVersion":2,"Components":[{"Parameters":[{"Name":"File","Expression":"../../dut.s2p"}]}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);

        Assert.Empty(plan.ExternalFiles);
    }

    [Fact]
    public void AWorkspaceRelativeSnpRefPointingOutsideTheWorkspace_IsOffered_AndRepointedAgainstTheWorkspaceRoot()
    {
        // The bug a colleague hit (2026-09-01): "I couldn't run it because it was missing its
        // Touchstone files." SnpPathPolicy stores an SnP `File` parameter WORKSPACE-relative and
        // tolerates up to two levels ABOVE the root, and Elaborator.ResolveSnpFilePath reads it back
        // the same way — but the archive scan only ever tried the DOCUMENT's own folder, so this
        // file resolved to nothing, was never offered, and never travelled.
        var touch = File_("refdata/dut.s2p", "! touchstone");
        var ws    = BuildWorkspace();
        File_("ws/Amp/schematic/Amp.csch", """
            {"FormatVersion":2,"Components":[{"InstanceName":"S1","Parameters":[
              {"Name":"File","Expression":"../refdata/dut.s2p"}]}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);

        var offered = Assert.Single(plan.ExternalFiles, e => e.SourcePath == touch);
        Assert.True(offered.Selected);

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        Assert.Contains("ws/external/dut.s2p", EntryNames(zip));

        // Repointed against the WORKSPACE ROOT, because that is the base the elaborator will resolve
        // it with. A document-relative "../../external/dut.s2p" would read as <ws>/../../external/…
        // and open with nothing behind the component.
        var csch = JsonNode.Parse(ReadEntry(zip, "ws/Amp/schematic/Amp.csch"))!;
        Assert.Equal("external/dut.s2p",
                     csch["Components"]![0]!["Parameters"]![0]!["Expression"]!.GetValue<string>());
    }

    [Fact]
    public void AnAbsoluteSnpRef_IsRepointedAgainstTheWorkspaceRootToo()
    {
        // SnpPathPolicy falls back to an absolute path for a file more than two levels above the
        // workspace — which is the ordinary case for a shared reference library. An absolute path
        // says nothing about how its replacement will be READ, so the `File` parameter's own
        // convention decides.
        var touch = File_("far/away/lib/dut.s2p", "! touchstone");
        var ws    = BuildWorkspace();
        File_("ws/Amp/schematic/Amp.csch", $$"""
            {"FormatVersion":2,"Components":[{"Parameters":[
              {"Name":"File","Expression":"{{touch.Replace("\\", "/")}}"}]}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        var zip  = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var csch = JsonNode.Parse(ReadEntry(zip, "ws/Amp/schematic/Amp.csch"))!;
        Assert.Equal("external/dut.s2p",
                     csch["Components"]![0]!["Parameters"]![0]!["Expression"]!.GetValue<string>());
    }

    [Fact]
    public void ABitmapKeepsTheDocumentRelativeConvention_WhileAnSnpInTheSameFileGetsTheWorkspaceOne()
    {
        // The two conventions live side by side in one schematic, so a single global rule for
        // repointing is wrong whichever one it picks.
        var png   = File_("elsewhere/underlay.png", "PNG");
        var touch = File_("elsewhere/dut.s2p", "! touchstone");
        var ws    = BuildWorkspace();
        File_("ws/Amp/schematic/Amp.csch", $$"""
            {"FormatVersion":2,
             "Shapes":[{"$type":"Bitmap","ImagePathRef":"{{png.Replace("\\", "/")}}"}],
             "Components":[{"Parameters":[{"Name":"File","Expression":"{{touch.Replace("\\", "/")}}"}]}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        var zip  = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var csch = JsonNode.Parse(ReadEntry(zip, "ws/Amp/schematic/Amp.csch"))!;
        Assert.Equal("../../external/underlay.png", csch["Shapes"]![0]!["ImagePathRef"]!.GetValue<string>());
        Assert.Equal("external/dut.s2p", csch["Components"]![0]!["Parameters"]![0]!["Expression"]!.GetValue<string>());
    }

    [Fact]
    public void ADataDisplaysOwnSources_AreFoundAndRepointed_EvenThoughItLivesUnderResults()
    {
        // A `.cdd` is an OPTION, not unconditional material, so the scan's document walk never
        // reached one and the writer never repointed one — an archive carried the display and left
        // its data behind (2026-09-01). The reference sits in a map KEY, which a values-only walk
        // cannot see either.
        var meas = File_("elsewhere/meas.s2p", "! touchstone");
        var ws   = BuildWorkspace();
        File_("ws/results/Amp.cdd",
            "{\"FormatVersion\":2,\"SelectedDataSource\":\"" + meas.Replace("\\", "/") + "\"," +
            "\"SourceAliases\":{\"" + meas.Replace("\\", "/") + "\":\"measured\"}}");

        var plan = WorkspaceArchiveScanner.Scan(ws);

        Assert.Single(plan.ExternalFiles, e => e.SourcePath == meas);

        var zip = Path.Combine(_root, "out.zip");
        var result = WorkspaceArchiveWriter.Write(plan, zip);

        Assert.Contains("ws/external/meas.s2p", EntryNames(zip));
        Assert.Contains("results/Amp.cdd", result.Repointed);

        // Relative to results/, which is how a Data Display reads a data source back.
        var cdd = JsonNode.Parse(ReadEntry(zip, "ws/results/Amp.cdd"))!.AsObject();
        Assert.Equal("../external/meas.s2p", cdd["SelectedDataSource"]!.GetValue<string>());
        Assert.Equal("measured", cdd["SourceAliases"]!["../external/meas.s2p"]!.GetValue<string>());
    }

    [Fact]
    public void AResultTheUserUnticked_ButADisplayPlots_IsReportedRatherThanSilentlyMissing()
    {
        var ws = BuildWorkspace();
        File_("ws/results/Amp.cdd", """{"FormatVersion":2,"SelectedDataSource":"Amp.npy"}""");

        var plan = WorkspaceArchiveScanner.Scan(ws);
        plan.Results.Single(r => r.DisplayName == "Amp.npy").Selected = false;

        var result = WorkspaceArchiveWriter.Write(plan, Path.Combine(_root, "out.zip"));

        Assert.Contains(result.ExcludedResults, p => p.EndsWith("Amp.npy", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExpressionThatIsNotAPath_IsNotMistakenForOne()
    {
        var ws = BuildWorkspace();
        File_("ws/Amp/schematic/Amp.csch", """
            {"FormatVersion":2,"Components":[{"Parameters":[
              {"Name":"R","Expression":"50"},{"Name":"C","Expression":"1.5e-12"},
              {"Name":"F","Expression":"freq*2"}]}]}
            """);

        Assert.Empty(WorkspaceArchiveScanner.Scan(ws).ExternalFiles);
    }

    // ── Writing, and the repointing that makes it portable ────────────────────

    private static string[] EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    private static string ReadEntry(string zipPath, string entry)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var s   = zip.GetEntry(entry)!.Open();
        return new StreamReader(s).ReadToEnd();
    }

    [Fact]
    public void TheArchiveHasOneRootFolder_SoUnzippingItDoesNotScatterFiles()
    {
        var ws  = BuildWorkspace();
        var zip = Path.Combine(_root, "out.zip");

        WorkspaceArchiveWriter.Write(WorkspaceArchiveScanner.Scan(ws), zip);

        Assert.All(EntryNames(zip), n => Assert.StartsWith("ws/", n));
        Assert.Equal("ws", WorkspaceArchiveExtractor.CommonRootFolder(EntryNames(zip)));
    }

    [Fact]
    public void EveryResultIsWrittenByDefault_AndUntickingOneLeavesItOut()
    {
        var ws   = BuildWorkspace();
        var plan = WorkspaceArchiveScanner.Scan(ws);
        var zip  = Path.Combine(_root, "out.zip");

        WorkspaceArchiveWriter.Write(plan, zip);

        var names = EntryNames(zip);
        Assert.Contains("ws/results/Amp.cdd", names);
        Assert.Contains("ws/results/em.s2p", names);
        Assert.Contains("ws/results/Amp.npy", names);            // the data the .cdd plots
        Assert.Contains("ws/results/notes.txt", names);

        // …and the choice is still the user's, file by file.
        plan.Results.Single(r => r.DisplayName == "Amp.npy").Selected = false;
        var zip2 = Path.Combine(_root, "out2.zip");
        WorkspaceArchiveWriter.Write(plan, zip2);

        Assert.DoesNotContain("ws/results/Amp.npy", EntryNames(zip2));
        Assert.Contains("ws/results/Amp.cdd", EntryNames(zip2));
    }

    [Fact]
    public void AnIncludedKit_IsCopiedIn_AndTheCwsPointsAtTheCopy()
    {
        var kit = Dir("vendorkit");
        File_("vendorkit/device-provider.json", "{}");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":["{{kit.Replace("\\", "/")}}"],"KnownFiles":[],
             "PdkRefs":[{"Path":"{{kit.Replace("\\", "/")}}","Provider":"vendor","TranslationVersion":2}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        foreach (var k in plan.Kits) k.Selected = true;

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        Assert.Contains("ws/kits/vendorkit/device-provider.json", EntryNames(zip));

        var cws = JsonNode.Parse(ReadEntry(zip, "ws/.cws"))!.AsObject();
        Assert.Equal("kits/vendorkit", cws["LibraryRefs"]![0]!.GetValue<string>());
        Assert.Equal("kits/vendorkit", cws["PdkRefs"]![0]!["Path"]!.GetValue<string>());
        // Everything else in the .cws survives the rewrite untouched.
        Assert.Equal("vendor", cws["PdkRefs"]![0]!["Provider"]!.GetValue<string>());
    }

    [Fact]
    public void AnUntickedKit_LeavesItsReferenceAlone_AndIsReported()
    {
        var kit = Dir("vendorkit");
        File_("vendorkit/device-provider.json", "{}");
        var stored = kit.Replace("\\", "/");

        var ws   = BuildWorkspace("ws", $$"""{"FormatVersion":2,"LibraryRefs":["{{stored}}"],"KnownFiles":[]}""");
        var plan = WorkspaceArchiveScanner.Scan(ws);
        var zip  = Path.Combine(_root, "out.zip");

        WorkspaceArchiveWriter.Write(plan, zip);

        // Rewriting it to a copy that is not there would be worse than leaving an honest absolute path.
        var cws = JsonNode.Parse(ReadEntry(zip, "ws/.cws"))!.AsObject();
        Assert.Equal(stored, cws["LibraryRefs"]![0]!.GetValue<string>());
        Assert.DoesNotContain(EntryNames(zip), n => n.Contains("kits/"));
    }

    [Fact]
    public void AnIncludedBitmap_IsCopiedIn_AndTheDocumentPointsAtItRelatively()
    {
        var png = File_("elsewhere/underlay.png", "PNG");
        var ws  = BuildWorkspace();
        File_("ws/Amp/layout/Amp.clay", $$"""
            {"FormatVersion":1,"Shapes":[{"$type":"Bitmap","ImagePathRef":"{{png.Replace("\\", "/")}}"}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        var zip  = Path.Combine(_root, "out.zip");
        var result = WorkspaceArchiveWriter.Write(plan, zip);

        Assert.Contains("ws/external/underlay.png", EntryNames(zip));

        // Relative to the DOCUMENT, because that is what resolves wherever the archive is unpacked —
        // the recipient's absolute paths are unknowable at archive time.
        var clay = JsonNode.Parse(ReadEntry(zip, "ws/Amp/layout/Amp.clay"))!.AsObject();
        Assert.Equal("../../external/underlay.png",
                     clay["Shapes"]![0]!["ImagePathRef"]!.GetValue<string>());
        Assert.Contains("Amp/layout/Amp.clay", result.Repointed);
        Assert.Empty(result.StillExternal);
    }

    [Fact]
    public void AnExcludedExternalReference_IsReportedRatherThanSilentlyBroken()
    {
        var png = File_("elsewhere/underlay.png", "PNG");
        var ws  = BuildWorkspace();
        File_("ws/Amp/layout/Amp.clay", $$"""
            {"FormatVersion":1,"Shapes":[{"$type":"Bitmap","ImagePathRef":"{{png.Replace("\\", "/")}}"}]}
            """);

        var plan = WorkspaceArchiveScanner.Scan(ws);
        foreach (var e in plan.ExternalFiles) e.Selected = false;

        var result = WorkspaceArchiveWriter.Write(plan, Path.Combine(_root, "out.zip"));

        Assert.Contains(result.StillExternal, p => p.EndsWith("underlay.png", StringComparison.Ordinal));
    }

    [Fact]
    public void ARepointedBitmapStillResolves_WhenTheArchiveIsUnpackedSomewhereElse()
    {
        var png = File_("elsewhere/underlay.png", "PNG");
        var ws  = BuildWorkspace();
        File_("ws/Amp/layout/Amp.clay", $$"""
            {"FormatVersion":1,"Shapes":[{"$type":"Bitmap","ImagePathRef":"{{png.Replace("\\", "/")}}"}]}
            """);

        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(WorkspaceArchiveScanner.Scan(ws), zip);

        // …a different machine, i.e. a completely different folder.
        var landing = Dir("landing");
        var opened  = WorkspaceArchiveExtractor.Extract(zip, landing);

        Assert.NotNull(opened.CwsPath);
        var view = CircuitRF.Design.Layout.LayoutPersistence.LoadFromFile(
            Path.Combine(opened.WorkspaceDir, "Amp", "layout", "Amp.clay"));

        var bmp = Assert.Single(view.Shapes.OfType<CircuitRF.Design.Layout.BitmapShape>());
        Assert.True(System.IO.File.Exists(bmp.ImagePathRef));    // the whole point of the exercise
        Assert.StartsWith(opened.WorkspaceDir, bmp.ImagePathRef);
    }

    // ── Unarchive ─────────────────────────────────────────────────────────────

    [Fact]
    public void UnarchiveRoundTripsTheWorkspace()
    {
        var ws  = BuildWorkspace();
        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(WorkspaceArchiveScanner.Scan(ws), zip);

        var landing = Dir("landing");
        var opened  = WorkspaceArchiveExtractor.Extract(zip, landing);

        Assert.Equal(Path.Combine(landing, "ws"), opened.WorkspaceDir);
        Assert.Equal(Path.Combine(landing, "ws", ".cws"), opened.CwsPath);
        Assert.True(System.IO.File.Exists(Path.Combine(opened.WorkspaceDir, "Amp", "schematic", "Amp.csch")));
        Assert.Empty(opened.Rejected);
    }

    [Fact]
    public void TheDefaultArchive_ArrivesCompleteOnAnotherMachine()
    {
        // The owner's own check (2026-09-01): "double check that these are getting archived — I
        // didn't get all of the files I needed." One workspace carrying every reference shape at
        // once, archived with NOTHING touched in the dialog, and unpacked somewhere else.
        var known    = File_("elsewhere/measured.s2p", "! a Known File, outside the workspace");
        var wsRel    = File_("refdata/dut.s2p",        "! workspace-relative, one level above");
        var absolute = File_("far/away/lib/pad.s2p",   "! too far above to be relative");

        var ws = BuildWorkspace("ws", $$"""
            {"FormatVersion":2,"LibraryRefs":[],"KnownFiles":["{{known.Replace("\\", "/")}}"]}
            """);

        File_("ws/Amp/schematic/Amp.csch", $$"""
            {"FormatVersion":2,"Components":[
              {"InstanceName":"S1","Parameters":[{"Name":"File","Expression":"../refdata/dut.s2p"}]},
              {"InstanceName":"S2","Parameters":[{"Name":"File","Expression":"{{absolute.Replace("\\", "/")}}"}]}]}
            """);
        File_("ws/results/Amp.cdd", """{"FormatVersion":2,"SelectedDataSource":"Amp.npy"}""");

        var plan = WorkspaceArchiveScanner.Scan(ws);          // defaults only — nothing is ticked here
        var zip  = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var opened = WorkspaceArchiveExtractor.Extract(zip, Dir("landing"));
        var root   = opened.WorkspaceDir;

        // Every external reference travelled …
        foreach (var name in new[] { "measured.s2p", "dut.s2p", "pad.s2p" })
            Assert.True(System.IO.File.Exists(Path.Combine(root, "external", name)), $"missing external/{name}");

        // … the results the displays plot travelled …
        Assert.True(System.IO.File.Exists(Path.Combine(root, "results", "Amp.npy")));
        Assert.True(System.IO.File.Exists(Path.Combine(root, "results", "Amp.cdd")));

        // … and each reference RESOLVES the way its own loader resolves it, from the new location.
        var cws = JsonNode.Parse(System.IO.File.ReadAllText(opened.CwsPath!))!.AsObject();
        var storedKnown = cws["KnownFiles"]![0]!.GetValue<string>();
        Assert.True(System.IO.File.Exists(
            CircuitRF.Ui.Schematic.WorkspaceRefs.Resolve(storedKnown, root)), storedKnown);

        var comps = JsonNode.Parse(System.IO.File.ReadAllText(
            Path.Combine(root, "Amp", "schematic", "Amp.csch")))!["Components"]!.AsArray();
        foreach (var comp in comps)
        {
            // Elaborator.ResolveSnpFilePath: relative resolves against the WORKSPACE ROOT.
            var stored = comp!["Parameters"]![0]!["Expression"]!.GetValue<string>();
            Assert.False(Path.IsPathRooted(stored), $"still absolute: {stored}");
            Assert.True(System.IO.File.Exists(Path.Combine(root, stored.Replace('/', Path.DirectorySeparatorChar))), stored);
        }
    }

    [Fact]
    public void UnarchivingOntoAnExistingWorkspace_RefusesRatherThanMerging()
    {
        var ws  = BuildWorkspace();
        var zip = Path.Combine(_root, "out.zip");
        WorkspaceArchiveWriter.Write(WorkspaceArchiveScanner.Scan(ws), zip);

        var landing = Path.GetDirectoryName(ws)!;     // extracting back on top of itself

        Assert.Throws<IOException>(() => WorkspaceArchiveExtractor.Extract(zip, landing));
    }

    [Fact]
    public void AnEntryPointingOutsideTheDestination_IsRefused()
    {
        // A zip is a file that arrives from someone else. "../escaped.txt" is the classic way to make
        // an extractor write outside the folder the user chose.
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "evil.zip");
        using (var stream = new FileStream(zip, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("ws/.cws").Open())) w.Write("{\"FormatVersion\":2}");
            using (var w = new StreamWriter(archive.CreateEntry("ws/../../escaped.txt").Open())) w.Write("pwned");
        }

        var landing = Dir("landing");
        var opened  = WorkspaceArchiveExtractor.Extract(zip, landing);

        Assert.Contains(opened.Rejected, r => r.Contains("escaped"));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void TickingAGroupThatWasNeverOpened_StillReachesTheRowsItStandsFor()
    {
        // What Include All / Include None rely on. A collapsed heading holds a placeholder, not its
        // real rows, so a group that decides for rows it has not built has to write through to the
        // OPTIONS — the rows are built later and read their state from there.
        var plan = WorkspaceArchiveScanner.Scan(BuildWorkspace());
        var node = ArchiveTreeNode.Group("Results", () => plan.Results.Select(ArchiveTreeNode.Leaf));

        node.IsChecked = false;
        Assert.All(plan.Results, r => Assert.False(r.Selected));

        node.IsChecked = true;
        Assert.All(plan.Results, r => Assert.True(r.Selected));

        // …and opening it afterwards shows what was decided, rather than the state it was built with.
        node.IsExpanded = true;
        Assert.All(node.Children, c => Assert.Equal(true, c.IsChecked));
    }

    [Fact]
    public void SizesAreFormattedTheWayAFileManagerWritesThem()
    {
        Assert.Equal("512 B",  WorkspaceArchivePlan.FormatSize(512));
        Assert.Equal("1.0 KB", WorkspaceArchivePlan.FormatSize(1024));
        Assert.Equal("1.5 MB", WorkspaceArchivePlan.FormatSize(1024 * 1024 * 3 / 2));
        Assert.Equal("…",      WorkspaceArchivePlan.FormatSize(-1));       // not measured yet
    }
}
