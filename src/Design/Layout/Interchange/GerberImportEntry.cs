// Turning what the user pointed at into the file list GerberImport.Import consumes
// (docs/sonnet-briefs/brief-L4h-gerber-import-ui-and-round-trip.md §1).
//
// L4g deliberately stops at "a RESOLVED list of file paths" (its R-L4g-6). This file is the step
// before that, and it is the whole of the decision: a folder means everything in it; a single file
// means asking whether the enclosing folder was the real intent, and stating what that folder holds
// so the question can be answered. Framework-free on purpose — the same reason ImportFolder is:
// WorkspaceViewModel supplies the pickers and the dialog, and every rule below is testable with no
// Avalonia anywhere.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>What the user answered to <see cref="GerberImportEntry.FolderSurvey"/>'s question.
/// <c>null</c> from the prompt is Cancel, and cancel creates nothing (gate 7).</summary>
public enum GerberImportScope
{
    /// <summary>Import every artwork and drill file in the folder holding the chosen file. The
    /// DEFAULT (R-L4h-3): the more common intent, and the cheaper mistake — a folder imported when one
    /// file was wanted is a folder the user deletes, while one file imported when the board was wanted
    /// is a plausible-looking one-layer board.</summary>
    EnclosingFolder,

    /// <summary>Import the chosen file alone. A first-class outcome, not a degraded one (R-L4h-4).</summary>
    ThisFileOnly,

    /// <summary>Neither — open the folder picker and import whatever folder is chosen there
    /// (R-L4h-5's third option, for the user who wants to point at a different folder outright).</summary>
    AnotherFolder,
}

public static class GerberImportEntry
{
    /// <summary>
    /// What the folder holding one chosen file actually contains — the numbers R-L4h-3's prompt has to
    /// state, because "a prompt that asks a question the user has no basis to answer is worse than no
    /// prompt". Counts are of the OTHER files in the folder; the chosen file is never counted as one of
    /// its own siblings.
    /// </summary>
    public sealed record FolderSurvey(
        string ChosenFilePath,
        string FolderPath,
        int OtherArtwork,
        int OtherDrill,
        int OtherJobFiles)
    {
        public string ChosenFileName => Path.GetFileName(ChosenFilePath);
        public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(FolderPath));

        /// <summary>
        /// R-L4h-3's last bullet, stated as a predicate: ask only when the two answers are different
        /// answers. They are the same when the folder holds no other artwork, no drill data and no job
        /// file — importing the folder would then produce exactly the one layer importing the file
        /// produces.
        ///
        /// <para>A job file counts, even though it is neither artwork nor drill data, because it is
        /// rung 0 of L4g's identity cascade: importing the folder reads it and settles every layer's
        /// identity with no heuristic, while importing the file alone falls back to the file's own
        /// attributes or its name. That is a different outcome, so it is a real question.</para>
        ///
        /// <para><b>And when there is nothing to ask, the file is imported ALONE</b> rather than the
        /// folder. The two produce the same layers by construction, but not the same folder name and
        /// not the same skip list: a lone Gerber sitting in a downloads folder among a hundred
        /// unrelated files would otherwise import as a cell named after that folder with a hundred
        /// skip messages above it.</para>
        /// </summary>
        public bool NeedsPrompt => OtherArtwork + OtherDrill + OtherJobFiles > 0;

        /// <summary>The prompt's own text — what the folder holds, by count and by name.</summary>
        public string Question =>
            $"\"{ChosenFileName}\" is one Gerber file: one layer, with no drill data, no other copper " +
            $"and no board outline.\n\n" +
            $"Its folder, \"{FolderName}\", also holds {Describe(OtherArtwork, "artwork file")}, " +
            $"{Describe(OtherDrill, "drill file")} and {Describe(OtherJobFiles, "job file")}.\n\n" +
            "Import the whole folder, or just this one file?";

        private static string Describe(int n, string noun) =>
            n == 1 ? $"1 other {noun}" : $"{n} other {noun}s";
    }

    /// <summary>Classifies every file beside <paramref name="chosenFilePath"/> — by CONTENT, through
    /// the same classifier the import itself uses (R-L4g-1), never by extension, so the prompt's counts
    /// and the import's own counts can never disagree.</summary>
    public static FolderSurvey Survey(string chosenFilePath)
    {
        string full = Path.GetFullPath(chosenFilePath);
        string dir = Path.GetDirectoryName(full) ?? full;

        int artwork = 0, drill = 0, job = 0;
        foreach (var file in GerberFileClassifier.ClassifyFolder(dir))
        {
            if (string.Equals(Path.GetFullPath(file.Path), full, StringComparison.OrdinalIgnoreCase)) continue;
            switch (file.Kind)
            {
                case GerberFileKind.Artwork: artwork++; break;
                case GerberFileKind.Drill: drill++; break;
                case GerberFileKind.JobFile: job++; break;
                default: break;
            }
        }

        return new FolderSurvey(full, dir, artwork, drill, job);
    }

    /// <summary>Every file directly inside <paramref name="dir"/>, in a stable order — what a FOLDER
    /// choice resolves to. Classification still runs inside <see cref="GerberImport.Import"/>, which is
    /// what decides which of these are artwork, which are drill data and which are reported as
    /// skipped (R-L4h-2).</summary>
    public static IReadOnlyList<string> FilesIn(string dir) =>
        Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)]
            : [];

    /// <summary>The file list and the import name for a settled scope. The name is the folder's for a
    /// folder and the file's own stem for a single file, which is what
    /// <see cref="GerberImport.Import"/> uses for the import folder, its technology and its cell.</summary>
    public static (IReadOnlyList<string> Files, string ImportName) Resolve(FolderSurvey survey, GerberImportScope scope)
        => scope switch
        {
            GerberImportScope.ThisFileOnly =>
                ([survey.ChosenFilePath], Path.GetFileNameWithoutExtension(survey.ChosenFilePath)),
            _ => (FilesIn(survey.FolderPath), survey.FolderName),
        };

    /// <summary>The same pair for a folder the user pointed at directly — the folder picker's result,
    /// and <see cref="GerberImportScope.AnotherFolder"/>'s.</summary>
    public static (IReadOnlyList<string> Files, string ImportName) ResolveFolder(string dir)
        => (FilesIn(dir), Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir))));

    /// <summary>R-L4h-3's prompt. Returning null is Cancel, which aborts and creates nothing.</summary>
    public delegate GerberImportScope? PromptForScope(FolderSurvey survey);

    /// <summary>R-L4h-5's folder picker, reached only through
    /// <see cref="GerberImportScope.AnotherFolder"/>. Returning null is a dismissed picker — the same
    /// as Cancel.</summary>
    public delegate string? PickFolder();

    /// <summary>
    /// The whole entry flow, from the one file the user picked to a finished
    /// <see cref="GerberImport.ImportResult"/>: survey the enclosing folder, ask R-L4h-3's question if
    /// there is one to ask, resolve the answer into a file list and a name, and hand both to L4g.
    ///
    /// <para><b>It lives here rather than in the view model so that the flow itself is gated.</b>
    /// Gates 3-7 are about which files an answer resolves to and about a cancel creating nothing on
    /// disk — properties of this method, not of a dialog. What stays in the view model is the two
    /// pickers, the two dialogs and <c>Messages</c>; every decision between them is below, and every
    /// one is reachable from a test with no Avalonia anywhere.</para>
    /// </summary>
    public static GerberImport.ImportResult Run(
        string chosenFilePath,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        PromptForScope promptForScope,
        PickFolder pickFolder,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        GerberImport.ResolveDrillFormat? resolveDrillFormat = null)
    {
        var survey = Survey(chosenFilePath);

        IReadOnlyList<string> files;
        string importName;

        if (!survey.NeedsPrompt)
        {
            (files, importName) = Resolve(survey, GerberImportScope.ThisFileOnly);
        }
        else if (promptForScope(survey) is not { } scope)
        {
            return Cancelled("Import cancelled before anything was read — nothing was created.");
        }
        else if (scope == GerberImportScope.AnotherFolder)
        {
            if (pickFolder() is not { } dir)
                return Cancelled("No folder was chosen — nothing was created.");
            (files, importName) = ResolveFolder(dir);
        }
        else
        {
            (files, importName) = Resolve(survey, scope);
        }

        if (files.Count == 0)
            return Cancelled("That folder holds no files, so nothing was imported.");

        return GerberImport.Import(
            files, parentDir, importName, destTech, destDbuPerMicron,
            resolveLayerMapping, resolveDrillFormat);
    }

    /// <summary>The same flow for a folder chosen outright, with no file and therefore no
    /// question.</summary>
    public static GerberImport.ImportResult RunFolder(
        string dir,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        GerberImport.ResolveDrillFormat? resolveDrillFormat = null)
    {
        var (files, importName) = ResolveFolder(dir);
        if (files.Count == 0)
            return Cancelled("That folder holds no files, so nothing was imported.");
        return GerberImport.Import(
            files, parentDir, importName, destTech, destDbuPerMicron, resolveLayerMapping, resolveDrillFormat);
    }

    private static GerberImport.ImportResult Cancelled(string why)
        => new(true, [], null, null, null, null, [], [], [], [why]);
}
