// Gate for docs/sonnet-briefs/brief-L4h-gerber-import-ui-and-round-trip.md §1 and §5 gates 2-9 —
// the menu entry, the folder-or-file question, the drill-format prompt, and what the summary says.
//
// The FLOW is gated, not a dialog: GerberImportEntry.Run is the shipped code path from "the user
// picked one file" to a finished ImportResult, and the two prompts reach it as delegates. That is the
// reason the decision lives outside WorkspaceViewModel at all — a Window cannot be constructed in this
// headless suite, and every rule below would otherwise be untestable.
//
// Gate 18: COUNTERS ONLY. There is no wall-clock assertion anywhere in this file.

using System.Text.RegularExpressions;

using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class GerberImportEntryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gerber-entry-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // -- Fixtures ---------------------------------------------------------------------------------

    private const string MmHeader = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Artwork(string? fileFunction = null, double xMm = 1.0, double yMm = 1.0)
    {
        string attribute = fileFunction is { Length: > 0 } fn ? $"%TF.FileFunction,{fn}*%\n" : "";
        return MmHeader + attribute + "%ADD10C,0.400*%\nD10*\n" +
               $"X{(long)Math.Round(xMm * 1_000_000)}Y{(long)Math.Round(yMm * 1_000_000)}D03*\n" + "M02*\n";
    }

    /// <summary>A drill file that states everything: units, and decimal-point coordinates that make the
    /// digit count and the suppression convention moot. Nothing here needs a prompt.</summary>
    private static string HeaderedDrill(double xMm = 1.0, double yMm = 1.0) =>
        "M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\n" + $"X{xMm:0.000000}Y{yMm:0.000000}\n" + "M30\n";

    /// <summary>The same holes, with nothing said: no unit keyword, no format comment, and integer
    /// coordinates, so the unit comes from the tool diameters and the digits and suppression are
    /// defaulted. This is exactly the file R-L4h-6's prompt exists for.</summary>
    private static string HeaderlessDrill() =>
        "M48\nT1C0.300\n%\nT1\nX001000Y001000\nM30\n";

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string fileName, string content)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Where imports land — a directory of its own, so "nothing was created" can be asserted
    /// against the FILESYSTEM (gate 7) rather than against a return value.</summary>
    private string Destination([System.Runtime.CompilerServices.CallerMemberName] string name = "")
        => Folder("dest_" + name);

    private static GerberImport.ImportResult Run(
        string chosenFile, string parentDir,
        GerberImportScope? scope,
        string? anotherFolder = null,
        GerberImport.ResolveDrillFormat? drillFormat = null)
    {
        return GerberImportEntry.Run(
            chosenFile, parentDir, destTech: null, LayoutUnits.DefaultDbuPerMicron,
            promptForScope: _ => scope,
            pickFolder: () => anotherFolder,
            resolveDrillFormat: drillFormat);
    }

    private static LayoutView LoadCell(GerberImport.ImportResult result) =>
        LayoutPersistence.LoadFromFile(Directory.EnumerateFiles(
            CellFolder.SubFolderPath(result.CellDir!, ViewType.Layout), "*.clay").Single());

    // ── Gate 3 — a folder means everything in it ─────────────────────────────────────────────────

    [Fact]
    public void ChoosingAFolder_ImportsEveryArtworkAndDrillFileInIt()
    {
        var dir = Folder("board_out");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", 2.0, 2.0));
        Write(dir, "board.drl", HeaderedDrill());
        Write(dir, "board-bom.csv", "Ref,Val\nR1,10k\n");

        var result = GerberImportEntry.RunFolder(dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron);

        Assert.False(result.Cancelled);
        Assert.Equal(["board.gbl", "board.gtl"], result.Layers.Select(l => l.FileName).Order());
        Assert.Equal(["board-bom.csv"], result.SkippedFiles);
        Assert.Single(LoadCell(result).Shapes.OfType<ViaShape>());
    }

    // ── Gate 4 — one file chosen, the folder accepted ────────────────────────────────────────────

    [Fact]
    public void ChoosingOneFile_AndAcceptingThePrompt_ImportsTheWholeEnclosingFolder()
    {
        var dir = Folder("set");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", 2.0, 2.0));
        Write(dir, "board.drl", HeaderedDrill());

        var result = Run(chosen, Destination(), GerberImportScope.EnclosingFolder);

        Assert.False(result.Cancelled);
        Assert.Equal(2, result.Layers.Count);
        Assert.Single(LoadCell(result).Shapes.OfType<ViaShape>());
        // Named after the FOLDER, since that is what was imported.
        Assert.Equal("set", Path.GetFileName(result.ImportDir));
    }

    /// <summary>R-L4h-3: "the prompt states what the enclosing folder actually holds — how many
    /// artwork files, how many drill files, and its name", because a prompt asking a question the
    /// user has no basis to answer is worse than no prompt. The counts come from the same content
    /// classifier the import itself uses, so the two can never disagree.</summary>
    [Fact]
    public void ThePrompt_StatesTheFoldersName_AndItsArtworkAndDrillCounts()
    {
        var dir = Folder("prod_out");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", 2.0, 2.0));
        Write(dir, "board.gts", Artwork("Soldermask,Top", 3.0, 3.0));
        Write(dir, "board.drl", HeaderedDrill());
        Write(dir, "board-npth.drl", HeaderedDrill(2.0, 2.0));
        Write(dir, "readme.txt", "hello, this is prose and not a drill file at all\n");

        var survey = GerberImportEntry.Survey(chosen);

        Assert.True(survey.NeedsPrompt);
        Assert.Equal(2, survey.OtherArtwork);
        Assert.Equal(2, survey.OtherDrill);
        Assert.Contains("prod_out", survey.Question, StringComparison.Ordinal);
        Assert.Contains("board.gtl", survey.Question, StringComparison.Ordinal);
        Assert.Contains("2 other artwork files", survey.Question, StringComparison.Ordinal);
        Assert.Contains("2 other drill files", survey.Question, StringComparison.Ordinal);

        // The chosen file is never counted among its own siblings — a bare "3 artwork files" here
        // would be a different, wrong number, and the user cannot tell which was meant.
        Assert.DoesNotContain("3 other artwork", survey.Question, StringComparison.Ordinal);
    }

    // ── Gate 5 — one file chosen, the prompt declined ────────────────────────────────────────────

    [Fact]
    public void ChoosingOneFile_AndDecliningThePrompt_ImportsThatFileAlone_AsAOneLayerCell()
    {
        var dir = Folder("set");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbl", Artwork("Copper,L2,Bot,Signal", 2.0, 2.0));
        Write(dir, "board.drl", HeaderedDrill());

        var result = Run(chosen, Destination(), GerberImportScope.ThisFileOnly);

        Assert.False(result.Cancelled);
        Assert.Equal(["board.gtl"], result.Layers.Select(l => l.FileName));
        Assert.Equal("board", Path.GetFileName(result.ImportDir));   // named after the FILE

        // R-L4h-4: a first-class outcome, not a degraded one — and it says plainly that no drill data
        // was read and therefore no vias were reconstructed (L4g's R-L4g-4).
        Assert.Empty(LoadCell(result).Shapes.OfType<ViaShape>());
        Assert.Contains(result.Messages, m =>
            m.Contains("No drill data was read", StringComparison.Ordinal) &&
            m.Contains("no vias were reconstructed", StringComparison.Ordinal));
    }

    // ── Gate 6 — nothing to ask ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AFileAloneInItsFolder_ImportsWithNoPrompt()
    {
        var dir = Folder("lonely");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));

        Assert.False(GerberImportEntry.Survey(chosen).NeedsPrompt);

        int prompts = 0;
        var result = GerberImportEntry.Run(
            chosen, Destination(), null, LayoutUnits.DefaultDbuPerMicron,
            promptForScope: _ => { prompts++; return GerberImportScope.EnclosingFolder; },
            pickFolder: () => null);

        Assert.Equal(0, prompts);
        Assert.False(result.Cancelled);
        Assert.Equal(["board.gtl"], result.Layers.Select(l => l.FileName));
    }

    /// <summary>A job file is neither artwork nor drill data and still changes the answer: importing
    /// the folder reads it and settles every layer's identity at rung 0 of L4g's cascade, while
    /// importing the file alone falls back to the file's own attributes. So it is a real question, and
    /// the prompt fires.</summary>
    [Fact]
    public void AFileWhoseOnlyCompanionIsAJobFile_StillAsks()
    {
        var dir = Folder("withjob");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.gbrjob", "{\"Header\":{},\"FilesAttributes\":[{\"Path\":\"board.gtl\"}]}");

        var survey = GerberImportEntry.Survey(chosen);
        Assert.True(survey.NeedsPrompt);
        Assert.Equal(1, survey.OtherJobFiles);
    }

    /// <summary>A companion that is neither artwork, drill data nor a job file does NOT make it a
    /// question — the two answers still produce the same layers, and asking would train users to
    /// dismiss the prompt unread.</summary>
    [Fact]
    public void AFileWhoseCompanionsAreNeitherArtworkNorDrillData_DoesNotAsk()
    {
        var dir = Folder("withjunk");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.pdf", "%PDF-1.4\n binary\n");
        Write(dir, "notes.txt", "some prose about the board, with no tool table in it\n");

        Assert.False(GerberImportEntry.Survey(chosen).NeedsPrompt);
    }

    // ── Gate 7 — cancel creates nothing, asserted on the filesystem ──────────────────────────────

    [Fact]
    public void CancellingThePrompt_CreatesNothing_OnDisk()
    {
        var dir = Folder("set");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderedDrill());
        string dest = Destination();

        var result = Run(chosen, dest, scope: null);

        Assert.True(result.Cancelled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
    }

    [Fact]
    public void DismissingTheFolderPicker_CreatesNothing_OnDisk()
    {
        var dir = Folder("set");
        string chosen = Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderedDrill());
        string dest = Destination();

        var result = Run(chosen, dest, GerberImportScope.AnotherFolder, anotherFolder: null);

        Assert.True(result.Cancelled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
    }

    /// <summary>R-L4h-5's third option, which is the only way the file-picker-first flow reaches a
    /// folder the user did not start in.</summary>
    [Fact]
    public void ChoosingAnotherFolder_ImportsThatFolder_NotTheOneTheFileWasIn()
    {
        var here = Folder("here");
        string chosen = Write(here, "stray.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(here, "stray.drl", HeaderedDrill());

        var there = Folder("there");
        Write(there, "real.gtl", Artwork("Copper,L1,Top,Signal", 5.0, 5.0));

        var result = Run(chosen, Destination(), GerberImportScope.AnotherFolder, anotherFolder: there);

        Assert.False(result.Cancelled);
        Assert.Equal(["real.gtl"], result.Layers.Select(l => l.FileName));
        Assert.Equal("there", Path.GetFileName(result.ImportDir));
    }

    // ── Gate 8 — the drill-format prompt ─────────────────────────────────────────────────────────

    [Fact]
    public void AHeaderlessDrillFile_RaisesThePrompt_PreFilledWithTheInferenceAndItsEvidence()
    {
        var dir = Folder("headerless");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderlessDrill());

        DrillFormatInference? seen = null;
        DrillExtentsCheck? crossCheck = null;
        string? seenFile = null;

        var result = GerberImportEntry.RunFolder(
            dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron,
            resolveDrillFormat: (fileName, inferred, check, _) =>
            {
                seenFile = fileName;
                seen = inferred;
                crossCheck = check;
                return new GerberImport.DrillFormatChoice(null);   // accept the inference as it stands
            });

        Assert.False(result.Cancelled);
        Assert.Equal("board.drl", seenFile);
        Assert.NotNull(seen);
        Assert.True(seen!.RequiredAGuess);

        // Pre-filled with the inference: the unit came from the tool diameters (0.300 is millimetres,
        // not inches), and the digits and suppression were defaulted because the file says nothing.
        Assert.Equal(GerberUnit.Millimetres, seen.Unit);
        Assert.Equal(DrillFormatEvidence.ToolDiameters, seen.UnitEvidence);

        // ...AND the evidence behind it (R-L4h-6): one sentence per part of the format naming the
        // source that settled it, plus the artwork cross-check, which is the strongest single piece
        // available and free here because this is the only place holding both readers' output.
        Assert.Equal(3, seen.Evidence.Count);
        Assert.Contains(seen.Evidence, e => e.Contains("tool table", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(crossCheck);
        Assert.NotEmpty(crossCheck!.Report);
    }

    [Fact]
    public void AFullyHeaderedDrillFile_DoesNotRaiseThePromptAtAll()
    {
        var dir = Folder("headered");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderedDrill());

        int prompts = 0;
        var result = GerberImportEntry.RunFolder(
            dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron,
            resolveDrillFormat: (_, _, _, _) => { prompts++; return new GerberImport.DrillFormatChoice(null); });

        Assert.False(result.Cancelled);
        Assert.Equal(0, prompts);
        Assert.Single(LoadCell(result).Shapes.OfType<ViaShape>());
    }

    [Fact]
    public void CancellingTheDrillFormatPrompt_AbortsTheWholeImport_AndLeavesNothingBehind()
    {
        var dir = Folder("headerless");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderlessDrill());
        string dest = Destination();

        var result = GerberImportEntry.RunFolder(
            dir, dest, null, LayoutUnits.DefaultDbuPerMicron,
            resolveDrillFormat: (_, _, _, _) => null);

        Assert.True(result.Cancelled);
        Assert.Null(result.CellDir);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
    }

    /// <summary>A set's drill files come out of one exporter in one format, so asking the same
    /// question once per file is repetition rather than diligence — and the second dialog is the one a
    /// user answers without reading. "Apply to all" answers the rest without asking.</summary>
    [Fact]
    public void AnApplyToAllAnswer_SettlesTheRemainingDrillFiles_WithoutAskingAgain()
    {
        var dir = Folder("headerless-pair");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderlessDrill());
        Write(dir, "board.rou", HeaderlessDrill());

        int prompts = 0;
        var result = GerberImportEntry.RunFolder(
            dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron,
            resolveDrillFormat: (_, _, _, _) =>
            {
                prompts++;
                return new GerberImport.DrillFormatChoice(null, ApplyToAll: true);
            });

        Assert.False(result.Cancelled);
        Assert.Equal(1, prompts);
    }

    /// <summary>And without it, each file is still asked about separately — the box is an opt-in, not
    /// a change to what happens when nobody ticks it.</summary>
    [Fact]
    public void WithoutApplyToAll_EachDrillFileIsStillAskedAbout()
    {
        var dir = Folder("headerless-pair-asked");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderlessDrill());
        Write(dir, "board.rou", HeaderlessDrill());

        int prompts = 0;
        var result = GerberImportEntry.RunFolder(
            dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron,
            resolveDrillFormat: (_, _, _, _) => { prompts++; return new GerberImport.DrillFormatChoice(null); });

        Assert.False(result.Cancelled);
        Assert.Equal(2, prompts);
    }

    /// <summary>The prompt is told how many files it would otherwise ask about again, so it can offer
    /// "apply to all" only when there is something to apply it to.</summary>
    [Fact]
    public void ThePromptIsToldHowManyDrillFilesRemain()
    {
        var dir = Folder("headerless-pair-count");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderlessDrill());
        Write(dir, "board.rou", HeaderlessDrill());

        var remaining = new List<int>();
        GerberImportEntry.RunFolder(
            dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron,
            resolveDrillFormat: (_, _, _, n) => { remaining.Add(n); return new GerberImport.DrillFormatChoice(null); });

        Assert.Equal([1, 0], remaining);
    }

    /// <summary>An override from the prompt is what the file is then RE-READ with — not a label
    /// applied to an already-parsed result. Reading the headerless file as inches instead of
    /// millimetres moves every hole by a factor of 25.4, which is exactly the silent failure the
    /// prompt exists to prevent, so it must be observable in the geometry.</summary>
    [Fact]
    public void AnOverrideFromThePrompt_ChangesWhatTheFileIsReadAs()
    {
        var dir = Folder("headerless");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));
        Write(dir, "board.drl", HeaderlessDrill());

        long HoleDiameterUnder(DrillFormatOverride? overrides)
        {
            var r = GerberImportEntry.RunFolder(
                Folder("headerless"), Destination(Guid.NewGuid().ToString("N")), null,
                LayoutUnits.DefaultDbuPerMicron,
                resolveDrillFormat: (_, _, _, _) => new GerberImport.DrillFormatChoice(overrides));

            // On the DRILL layer only — the copper flash is 0.4 mm whatever the drill file says, and
            // reading past it would measure the artwork rather than the answer under test.
            var drillLayers = r.Technology!.Layers
                .Where(l => l.Name.StartsWith("Drill", StringComparison.Ordinal))
                .Select(l => l.Key).ToHashSet();
            var shapes = LoadCell(r).Shapes.Where(s => drillLayers.Contains(s.Layer)).ToList();
            return shapes.OfType<ViaShape>().Select(v => v.DrillSize)
                .Concat(shapes.OfType<CircleShape>().Select(c => c.R * 2))
                .Max();
        }

        long asInferred = HoleDiameterUnder(null);
        long asInches = HoleDiameterUnder(new DrillFormatOverride(Unit: GerberUnit.Inches));

        Assert.Equal(300_000, asInferred);                       // 0.300 mm
        Assert.Equal(300_000 * 254 / 10, asInches);              // 0.300 inch — the same number, 25.4x
    }

    // ── Gate 9 — the losses are named in the summary (R-L4h-8) ───────────────────────────────────

    /// <summary>
    /// A design containing a rounded rect, a non-round-capped path and a via reports all three
    /// collapses. The import cannot know which of them the source design used — the files carry no
    /// such types to lose, which is the whole point — so it names the CLASS, and only when the class
    /// applies: this set has regions in it and a via, so both are said.
    /// </summary>
    [Fact]
    public void TheSummary_NamesWhatTheFormatCannotCarryBack()
    {
        var dir = Folder("lossy");
        // A region (what a rounded rect and a square-capped path both become) plus a flash and a hole.
        Write(dir, "board.gtl",
            MmHeader + "%TF.FileFunction,Copper,L1,Top,Signal*%\n" +
            "%ADD10C,0.400*%\nD10*\nX1000000Y1000000D03*\n" +
            "G36*\nX2000000Y0D02*\nX3000000Y0D01*\nX3000000Y1000000D01*\nX2000000Y1000000D01*\nX2000000Y0D01*\nG37*\n" +
            "M02*\n");
        Write(dir, "board.drl", HeaderedDrill());

        var result = GerberImportEntry.RunFolder(dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron);

        string losses = Assert.Single(result.Messages,
            m => m.StartsWith("What this format cannot carry back", StringComparison.Ordinal));

        Assert.Contains("rounded-rectangle", losses, StringComparison.Ordinal);
        Assert.Contains("square or extended end style", losses, StringComparison.Ordinal);
        Assert.Contains("copper flash PLUS a drill hit", losses, StringComparison.Ordinal);
        Assert.Contains("text is not a type this format carries", losses, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLossSummary_SaysNothingAboutRegions_WhenTheSetHasNone()
    {
        var dir = Folder("flashonly");
        Write(dir, "board.gtl", Artwork("Copper,L1,Top,Signal"));

        var result = GerberImportEntry.RunFolder(dir, Destination(), null, LayoutUnits.DefaultDbuPerMicron);

        string losses = Assert.Single(result.Messages,
            m => m.StartsWith("What this format cannot carry back", StringComparison.Ordinal));
        Assert.DoesNotContain("arrived as POLYGONS", losses, StringComparison.Ordinal);

        // With no drill file, the via half says the OTHER thing — that every via is here as its
        // copper pad alone.
        Assert.Contains("copper pad alone", losses, StringComparison.Ordinal);
    }

    // ── Gate 2's other half, and R-L4h-7 — the dialogs exist, and there are only two ─────────────
    //
    // A Window cannot be constructed in this headless suite, so the dialogs themselves are verified
    // against their real source, the way every prior menu/dialog phase in this codebase has done it.

    private static string ReadRepoFile(string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void TheScopeDialog_DefaultsToTheWholeFolder_AndOffersTheFolderPicker()
    {
        var xaml = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "GerberImportScopeDialog.axaml"));

        // R-L4h-3: the whole folder is the default, because the cost of the wrong answer is asymmetric.
        var wholeFolder = Regex.Match(xaml, @"<Button Content=""Whole Folder""[^>]*?IsDefault=""True""", RegexOptions.Singleline);
        Assert.True(wholeFolder.Success, "\"Whole Folder\" must be the default button.");

        Assert.Contains("Content=\"This File Only\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Another Folder…\"", xaml, StringComparison.Ordinal);   // R-L4h-5
        Assert.Contains("Content=\"Cancel\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDrillFormatDialog_GivesUnitsAndZeroSuppression_TwoSeparateControls()
    {
        var xaml = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Dialogs", "GerberDrillFormatPromptDialog.axaml"));

        // R-L4h-6: "Units and zero suppression are two separate unknowns and get two separate
        // controls" — two RadioButton groups, not one combined list of format presets.
        Assert.Contains("GroupName=\"DrillUnits\"", xaml, StringComparison.Ordinal);
        Assert.Contains("GroupName=\"DrillZeros\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EvidenceText\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>R-L4h-7: Gerber import interrupts for the shared layer-mapping dialog, for R-L4h-3 and
    /// for R-L4h-6, and for nothing else. In particular there is no import fidelity dialog to match
    /// the export one — an import's losses are visible in the cell that just opened and reported in
    /// the summary above it, whereas a lossy WRITE is about to leave the building.</summary>
    [Fact]
    public void ImportGerber_ShowsExactlyThreeDialogs_AndNoFidelityDialog()
    {
        var vm = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        int start = vm.IndexOf("private async Task ImportGerberAsync(", StringComparison.Ordinal);
        Assert.True(start > 0, "ImportGerberAsync is gone.");
        int end = vm.IndexOf("\n    /// <summary>R-L4h-6's own prompt", start, StringComparison.Ordinal);
        string method = vm[start..end];

        Assert.Contains("GerberImportScopeDialog", method, StringComparison.Ordinal);
        Assert.Contains("ResolveGerberDrillFormatAsync", method, StringComparison.Ordinal);
        // The SHARED bridge, and it must name Gerber: four importers call this one method, and it used
        // to hard-code GDSII for all of them.
        Assert.Contains("ResolveImportLayerMappingAsync(window, \"Gerber\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("FidelityDialog", method, StringComparison.Ordinal);

        // Everything else is Messages.
        Assert.Contains("foreach (var msg in result.Messages) Messages.Info(msg);", method, StringComparison.Ordinal);
    }
}
