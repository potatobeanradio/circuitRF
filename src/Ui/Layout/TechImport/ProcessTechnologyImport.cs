// Finds a process's technology files inside a kit and turns a chosen pair into a Technology.
//
// Discovery is by CONTENT, not by extension or by folder name: a kit arranges its own tree however it
// likes, and the two files this needs are recognisable by their own grammar. It is bounded in every
// direction (depth, file count, file size) so pointing it at a large tree — or at the wrong tree
// entirely — costs a known amount of work rather than an open-ended walk.

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>One process file found while scanning, with enough context to choose between several.</summary>
/// <param name="Path">Absolute path.</param>
/// <param name="RelativePath">Path relative to the scanned root, '/'-separated — what the user reads.</param>
/// <param name="Label">
/// The name the file states for itself (a stack file's technology name), or its file name. Several
/// stack files in one kit are usually the same process at different process corners, and the label is
/// how a user tells them apart.
/// </param>
public sealed record TechnologyFileCandidate(string Path, string RelativePath, string Label);

/// <summary>What a scan of a kit turned up.</summary>
/// <param name="RuleDeckFiles">
/// Every file that reads as part of the process's design-rule deck. Unlike a stack description or a
/// layer table, these are NOT alternatives to choose between — a deck is one program split across
/// many files (its layer bindings in one, its rules in dozens), so they are read together or not at
/// all.
/// </param>
/// <param name="RuleValueTables">
/// The tables the deck reads its numbers out of. Several are a genuine choice (a process states one
/// per corner), so the first is used by default and the rest are reported.
/// </param>
public sealed record TechnologyScanResult(
    IReadOnlyList<TechnologyFileCandidate> StackFiles,
    IReadOnlyList<TechnologyFileCandidate> LayerTables,
    IReadOnlyList<string>                  Notes,
    IReadOnlyList<TechnologyFileCandidate> RuleDeckFiles,
    IReadOnlyList<TechnologyFileCandidate> RuleValueTables)
{
    public TechnologyScanResult(
        IReadOnlyList<TechnologyFileCandidate> stackFiles,
        IReadOnlyList<TechnologyFileCandidate> layerTables,
        IReadOnlyList<string>                  notes)
        : this(stackFiles, layerTables, notes, [], []) { }

    /// <summary>True when there is enough to build a technology from.</summary>
    public bool HasStack => StackFiles.Count > 0;

    /// <summary>True when the kit ships a design-rule deck circuitRF found.</summary>
    public bool HasRuleDeck => RuleDeckFiles.Count > 0;
}

public static class ProcessTechnologyImport
{
    /// <summary>
    /// How deep below the scanned root to look. A kit nests its technology data several levels down,
    /// and a rule deck is deeper still — it splits its rules into a folder per section of the process
    /// (front end, back end, geometry), which on a kit lands seven or eight levels below the kit
    /// root a user actually points the import at. The scan stays bounded in every other direction
    /// (<see cref="MaxFilesExamined"/>, <see cref="MaxFileBytes"/>, and a marker-word peek), so the
    /// cost of the extra depth is bounded too.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// Files bigger than this are not peeked at. Both formats are small — a stack description is a
    /// couple of kilobytes and even a large layer table is a few hundred — while a kit's bulk is
    /// artwork and model data that would cost real time to read and can never match.
    /// </summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>A ceiling on how many files a scan will look at, so a wrong root cannot run away.</summary>
    public const int MaxFilesExamined = 40_000;

    /// <summary>How much of a file is read to decide what it is.</summary>
    private const int PeekBytes = 64 * 1024;

    public static TechnologyScanResult Scan(string rootDirectory)
    {
        var stacks  = new List<TechnologyFileCandidate>();
        var tables  = new List<TechnologyFileCandidate>();
        var decks   = new List<TechnologyFileCandidate>();
        var values  = new List<TechnologyFileCandidate>();
        var notes   = new List<string>();

        if (!Directory.Exists(rootDirectory))
            return new TechnologyScanResult([], [], [$"\"{rootDirectory}\" is not a folder."]);

        int examined = 0;
        bool truncated = false;

        foreach (string path in EnumerateBounded(rootDirectory, MaxDepth))
        {
            if (examined >= MaxFilesExamined) { truncated = true; break; }
            examined++;

            string? text = TryReadCandidate(path);
            if (text is null) continue;

            if (ProcessStackReader.LooksLikeStackFile(text))
            {
                var desc = ProcessStackReader.Read(text);
                stacks.Add(new TechnologyFileCandidate(
                    path, Relative(rootDirectory, path),
                    desc.TechnologyName is { Length: > 0 } n ? n : Path.GetFileName(path)));
            }
            else if (LayerPropertiesReader.LooksLikeLayerPropertiesFile(text))
            {
                tables.Add(new TechnologyFileCandidate(
                    path, Relative(rootDirectory, path), Path.GetFileName(path)));
            }
            else if (RuleDeckReader.LooksLikeRuleValueTable(text))
            {
                values.Add(new TechnologyFileCandidate(
                    path, Relative(rootDirectory, path), Path.GetFileName(path)));
            }
            else if (RuleDeckReader.LooksLikeRuleDeck(text))
            {
                decks.Add(new TechnologyFileCandidate(
                    path, Relative(rootDirectory, path), Path.GetFileName(path)));
            }
        }

        if (truncated)
            notes.Add($"The folder holds more than {MaxFilesExamined:N0} files; the scan stopped there. " +
                      "Point the import at the folder holding the process data rather than the whole kit.");

        if (stacks.Count == 0)
            notes.Add("No interconnect technology file was found. circuitRF builds a stackup from the " +
                      "file that states each layer's thickness, permittivity and sheet resistance; " +
                      "without one there is nothing to derive a substrate from.");

        if (tables.Count == 0 && stacks.Count > 0)
            notes.Add("No layer table was found, so the technology will carry a stackup but no layers. " +
                      "Nothing drawn can be bound to a process layer until one is added.");

        // Ordered so the choice a user is offered is stable run to run.
        stacks.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        tables.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        decks.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        values.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

        return new TechnologyScanResult(stacks, tables, notes, decks, values);
    }

    /// <summary>
    /// Reads the chosen files and builds the technology. A layer table is optional — a stackup with no
    /// layer table is a degraded but honest result, and refusing would leave a user with a process
    /// whose stack circuitRF can plainly read and no way to get at it.
    /// </summary>
    public static TechnologyImportResult Import(
        string                 stackFilePath,
        string?                layerTablePath,
        IReadOnlyList<string>? ruleDeckPaths       = null,
        IReadOnlyList<string>? ruleValueTablePaths = null)
    {
        var stack = ProcessStackReader.Read(File.ReadAllText(stackFilePath));

        ProcessLayerTable? table = null;
        if (layerTablePath is { Length: > 0 })
            table = LayerPropertiesReader.Read(File.ReadAllText(layerTablePath));

        var deck = ReadRuleDeck(ruleDeckPaths, ruleValueTablePaths);

        return ProcessTechnologyBuilder.Build(
            stack, table, Path.GetFileNameWithoutExtension(stackFilePath), deck);
    }

    /// <summary>
    /// Reads the deck's files as one program. A file that cannot be read is skipped rather than
    /// failing the import — a deck is dozens of files and one unreadable corner of it must not cost a
    /// user the technology.
    /// </summary>
    public static ProcessRuleDeck ReadRuleDeck(
        IReadOnlyList<string>? ruleDeckPaths,
        IReadOnlyList<string>? ruleValueTablePaths)
    {
        if (ruleDeckPaths is not { Count: > 0 }) return ProcessRuleDeck.Empty;

        var tables = new List<IReadOnlyDictionary<string, double>>();
        foreach (var p in ruleValueTablePaths ?? [])
        {
            try { tables.Add(RuleDeckReader.ReadRuleValues(File.ReadAllText(p))); }
            catch (SystemException) { tables.Add(new Dictionary<string, double>()); }
        }

        var texts = new List<string>(ruleDeckPaths.Count);
        foreach (var p in ruleDeckPaths)
        {
            try { texts.Add(File.ReadAllText(p)); }
            catch (SystemException) { /* skip: see this method's own doc comment */ }
        }

        return RuleDeckReader.Read(texts, tables);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateBounded(string root, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (SystemException) { continue; }   // unreadable folder: skip, never fail the scan

            foreach (var f in files) yield return f;

            if (depth >= maxDepth) continue;

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (SystemException) { continue; }

            foreach (var s in subs) queue.Enqueue((s, depth + 1));
        }
    }

    /// <summary>
    /// A file's full text when its opening looks like one of the two formats, otherwise null.
    ///
    /// <para>Two stages, and the split is load-bearing rather than an optimisation. A recogniser has
    /// to see a file WHOLE — a layer table is XML, and a truncated document does not parse at all, so
    /// deciding on a fixed-size peek would reject every real one — but reading every file in a kit
    /// whole would mean reading its artwork and model data too. So the peek only looks for the
    /// format's own marker word, which is cheap and appears in the first few lines of both, and the
    /// file is read entire only once that has matched.</para>
    ///
    /// <para>Binary is detected by a NUL byte in the peek, the same rule the kit importer already uses.</para>
    /// </summary>
    private static string? TryReadCandidate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) return null;

            string peek;
            using (var fs = File.OpenRead(path))
            {
                var buffer = new byte[(int)Math.Min(PeekBytes, info.Length)];
                int read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0) return null;
                peek = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }

            bool promising =
                peek.Contains("layer-properties", StringComparison.Ordinal) ||
                peek.Contains("TECHNOLOGY",       StringComparison.OrdinalIgnoreCase) ||
                // A rule deck's own two grammar markers: it binds a drawn layer to a stream number,
                // or reads a value out of the rule table. Both appear within the first few lines of
                // any deck file that carries anything circuitRF can use.
                peek.Contains("get_polygons",     StringComparison.Ordinal) ||
                peek.Contains("drc_rules",        StringComparison.Ordinal);

            if (!promising) return null;

            return info.Length <= PeekBytes ? peek : File.ReadAllText(path);
        }
        catch (SystemException)
        {
            return null;
        }
    }

    private static string Relative(string root, string path)
    {
        string rel = Path.GetRelativePath(root, path);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
