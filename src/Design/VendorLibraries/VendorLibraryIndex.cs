using System.Text;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;

namespace CircuitRF.Design.VendorLibraries;

/// <summary>
/// One part of a vendor library, as its header and its <c>.subckt</c> line state it.
///
/// <para><b>Self-sufficient by its byte block.</b> <see cref="Offset"/>/<see cref="Length"/> span the
/// <c>.subckt</c> … <c>.ends</c> lines, so the one consumer that needs the circuit (a run) reads
/// exactly that block and nothing else of a file that can hold thousands of parts.</para>
/// </summary>
/// <param name="Part">The subcircuit's name, as written.</param>
/// <param name="File">The file, relative to the library root, with <c>/</c> separators.</param>
/// <param name="Folders">The folder segments between the root and the file.</param>
/// <param name="FileStem">The file's name without its extension.</param>
public sealed record VendorLibraryEntry(
    string Part,
    string File,
    IReadOnlyList<string> Folders,
    string FileStem,
    long Offset,
    int Length,
    IReadOnlyList<string> Ports,
    VendorLibraryHeader Header)
{
    public int PortCount => Ports.Count;

    /// <summary>The part number: the header's <c>P/N</c> line when it has one, the <c>.subckt</c> name otherwise.</summary>
    public string PartNumber => Header.PartNumber ?? Part;

    /// <summary>The part's tolerance (brief vendorlib-5 R-vl5-1), decoded at index build from text the scan already
    /// read; null when the part has none to have (no C or L value) or it is not known — <see cref="ToleranceNote"/>
    /// then says why.</summary>
    public VendorPartTolerance? Tolerance { get; init; }

    /// <summary>Why <see cref="Tolerance"/> is not known, or null when it is (or the part has no C or L value).</summary>
    public string? ToleranceNote { get; init; }

    /// <summary>The part has a C or L value, so a tolerance is asked of it.</summary>
    public bool HasToleranceQuestion => Tolerance is not null || ToleranceNote is not null;

    /// <summary>
    /// Non-null when this part is a MODEL FOLDER rather than a <c>.subckt</c> block (brief vendorlib-9 R-vl9-1c).
    /// <see cref="File"/> is then the model folder, library-relative; <see cref="Folders"/> the category folders
    /// above it; <see cref="FileStem"/> its name; <see cref="Ports"/> its pins; <see cref="Header"/> what
    /// <c>part.cpart</c> states; and <see cref="Offset"/>/<see cref="Length"/> are unused.
    /// </summary>
    public VendorModelInfo? Model { get; init; }

    /// <summary>True for a model folder, false for a SPICE <c>.subckt</c> part.</summary>
    public bool IsModel => Model is not null;

    /// <summary>The kind word <c>find --parts</c> prints and a drag payload carries: <c>model</c> or <c>spice</c>.</summary>
    public string KindWord => IsModel ? VendorModelInfo.KindWord : "spice";
}

/// <summary>
/// What the index keeps of a model folder's <c>part.cpart</c> (brief vendorlib-9 R-vl9-1c) — what finds, describes
/// and places it. Nothing here runs: the model's <c>.cnl</c> is read only by a run (D1, D7).
/// </summary>
/// <param name="Cell">The top <c>define</c> the <c>.cnl</c> holds.</param>
/// <param name="Kind"><c>Active</c> or <c>Passive</c>.</param>
/// <param name="Glyph"><c>R</c>, <c>C</c>, <c>L</c>, <c>FET</c>, or empty for the model's own symbol.</param>
/// <param name="Parameters">The top cell's parameters, each with its default as written.</param>
public sealed record VendorModelInfo(
    string Cell,
    string Kind,
    string Glyph,
    IReadOnlyList<ModelLibrary.ModelPartParameter> Parameters)
{
    public const string KindWord = "model";

    /// <summary>The category folder a model sits in, library-relative (<c>""</c> for the root): what Value ▲/▼
    /// steps within, the way a SPICE part steps within its file.</summary>
    public static string CategoryOf(VendorLibraryEntry e) => string.Join('/', e.Folders);
}

/// <summary>A library file whose tolerance letters were all refused by the one-offset guard (R-vl5-1d), and why.</summary>
public sealed record VendorLibraryToleranceRefusal(string File, string Reason);

/// <summary>A part name defined by more than one file: the copy indexed, and the one that was not.</summary>
public sealed record VendorLibraryDuplicate(string Part, string IndexedFile, string SkippedFile);

/// <summary>How many parts sit under one top-level folder of a library (<c>""</c> for the root itself).</summary>
public sealed record VendorLibraryGroup(string Folder, int Parts);

/// <summary>What one build of a library's index found.</summary>
public sealed record VendorLibraryBuild(
    string Root,
    IReadOnlyList<VendorLibraryEntry> Entries,
    int FilesScanned,
    int FilesRead,
    int EncryptedFiles,
    int EmptyFiles,
    int UnreadableFiles,
    int UnparsedFields,
    IReadOnlyList<VendorLibraryDuplicate> Duplicates,
    string? Refusal)
{
    /// <summary>Files whose part-number tolerances were all refused by the one-offset guard, each said once.</summary>
    public IReadOnlyList<VendorLibraryToleranceRefusal> ToleranceRefusals { get; init; } = [];

    /// <summary>Model folders found (brief vendorlib-9 R-vl9-1b), readable or not.</summary>
    public int ModelFolders { get; init; }

    /// <summary>Model folders whose <c>part.cpart</c> could not be read, each library-relative with the reader's
    /// sentence (R-vl9-1d). They are left out of <see cref="Entries"/> and never stop the rest of the index.</summary>
    public IReadOnlyList<(string Folder, string Reason)> UnreadableModels { get; init; } = [];

    /// <summary>The parts with a C or L value, and of those the ones whose tolerance is known.</summary>
    public int ToleranceAsked => Entries.Count(e => e.HasToleranceQuestion);
    public int ToleranceKnown => Entries.Count(e => e.Tolerance is not null);

    /// <summary>True when the library holds at least one readable part.</summary>
    public bool IsUsable => Refusal is null;

    private Dictionary<string, VendorLibraryEntry>? _byPart;

    /// <summary>The entry for <paramref name="part"/> (case-insensitive, as duplicates are judged), or
    /// null. In memory only — what a placed part's label and the palette ask per part.</summary>
    public VendorLibraryEntry? Find(string? part)
    {
        if (string.IsNullOrEmpty(part)) return null;
        var map = _byPart;
        if (map is null)
        {
            map = new Dictionary<string, VendorLibraryEntry>(Entries.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries) map.TryAdd(e.Part, e);
            _byPart = map;   // a benign race: two threads build the same map
        }
        return map.GetValueOrDefault(part.Trim());
    }

    /// <summary>The top-level folders and their part counts, in path order.</summary>
    public IReadOnlyList<VendorLibraryGroup> Groups =>
        [.. Entries.GroupBy(e => e.Folders.Count > 0 ? e.Folders[0] : "", StringComparer.Ordinal)
                   .OrderBy(g => g.Key, StringComparer.Ordinal)
                   .Select(g => new VendorLibraryGroup(g.Key, g.Count()))];

    /// <summary>
    /// The one sentence about files that could not be read, or null when every file could. Said ONCE
    /// per library, never once per file — a library of many series can carry dozens of them.
    /// </summary>
    public string? UnreadableSentence => EncryptedFiles > 0
        ? VendorLibraryIndex.EncryptedSentence(EncryptedFiles)
        : UnreadableFiles > 0
            ? $"{Plural(UnreadableFiles, "file", "files")} could not be read."
            : null;

    /// <summary>The lines an Add reports, in the GUI's confirm step and on <c>import library</c>'s
    /// stdout alike: the part count, the folder groups, the unreadable-file sentence, duplicates.</summary>
    public IReadOnlyList<string> SummaryLines()
    {
        var lines = new List<string>
        {
            ModelFolders == 0
                ? $"{Plural(Entries.Count, "part", "parts")} in {Plural(FilesScanned, "file", "files")}."
                : FilesScanned == 0
                    ? $"{Plural(Entries.Count, "part", "parts")} in {Plural(ModelFolders, "model folder", "model folders")}."
                    : $"{Plural(Entries.Count, "part", "parts")} in {Plural(FilesScanned, "file", "files")} and " +
                      $"{Plural(ModelFolders, "model folder", "model folders")}.",
        };
        var groups = Groups;
        if (groups.Count > 1 || (groups.Count == 1 && groups[0].Folder.Length > 0))
            lines.Add("Folders: " + string.Join(", ",
                groups.Select(g => $"{(g.Folder.Length == 0 ? "(top level)" : g.Folder)} ({g.Parts:N0})")) + ".");
        if (UnreadableSentence is { } s) lines.Add(s);
        if (UnreadableModels.Count > 0)
            lines.Add($"{Plural(UnreadableModels.Count, "model folder", "model folders")} could not be read: " +
                      string.Join("; ", UnreadableModels.Take(5).Select(m => $"{m.Folder} ({m.Reason})")) +
                      (UnreadableModels.Count > 5 ? "; …" : "") + ".");
        if (ToleranceAsked > 0)
            lines.Add($"Tolerance known for {ToleranceKnown:N0} of {Plural(ToleranceAsked, "part", "parts")}.");
        foreach (var r in ToleranceRefusals)
            lines.Add($"{r.File}: no tolerance read — {r.Reason}.");
        if (Duplicates.Count > 0)
            lines.Add($"{Plural(Duplicates.Count, "part name is", "part names are")} defined in more than one " +
                      $"file; the first by path is used: " +
                      string.Join(", ", Duplicates.Take(5).Select(d => $"{d.Part} ({d.SkippedFile})")) +
                      (Duplicates.Count > 5 ? ", …" : "") + ".");
        return lines;
    }

    internal static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
}

/// <summary>
/// The index of a vendor part library: one entry per <c>.subckt</c>, built from HEADERS ONLY
/// (brief vendorlib-1 R-vl1-2).
///
/// <para><b>It never parses a circuit.</b> A whole library is tens of thousands of parts in tens of
/// megabytes, and what tells them apart — value, size, dielectric, frequency range — is all in the
/// comment block above each <c>.subckt</c>. So the walk reads each file's bytes once, finds the
/// <c>.subckt</c>/<c>.ends</c> lines and the comments above them, and stops there:
/// <see cref="CircuitRF.Core.Netlist.Spice.SpiceNetlistReader"/> is never called. A placed part's
/// circuit is read later, from its own byte block only.</para>
///
/// <para><b>In memory only; no on-disk cache</b> (overview D5). Each file's entries are kept keyed on
/// its modification time and length, so a rebuild in the same session re-scans only the files that
/// changed.</para>
/// </summary>
public static class VendorLibraryIndex
{
    /// <summary>The library-wide sentence for encrypted files (R-vl1-2c).</summary>
    public static string EncryptedSentence(int files)
        => $"{VendorLibraryBuild.Plural(files, "file is", "files are")} encrypted for another simulator " +
           "and cannot be read. The plain-text (static) edition of this library can be.";

    // ── The per-file cache ────────────────────────────────────────────────────

    private enum FileKind { Readable, Encrypted, Empty, Unreadable }

    private sealed record FileScan(DateTime Mtime, long Length, FileKind Kind,
                                   IReadOnlyList<VendorLibraryEntry> Entries, int Unparsed, string? ToleranceRefusal = null);

    private static readonly Dictionary<string, FileScan> _files = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    private static readonly Dictionary<string, long> _fileReads = new(StringComparer.Ordinal);

    /// <summary>
    /// How many times this process has read the bytes of a file of the library at
    /// <paramref name="root"/> — a scan or one part's block. The palette's responsiveness rule ("a
    /// keystroke reads no file", brief vendorlib-2 R-vl2-4d) is gated on this counter rather than on a
    /// clock; it is per library so a gate is not disturbed by another library read in parallel.
    /// </summary>
    public static long FileReads(string root)
    {
        lock (_gate) return _fileReads.GetValueOrDefault(KeyOf(root));
    }

    private static void CountRead(string root)
    {
        string key = KeyOf(root);
        lock (_gate) _fileReads[key] = _fileReads.GetValueOrDefault(key) + 1;
    }

    /// <summary>Drops every cached file scan.</summary>
    public static void InvalidateAll() { lock (_gate) { _files.Clear(); _models.Clear(); _latest.Clear(); } }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks <paramref name="root"/> for files with a SPICE-model extension
    /// (<see cref="SpiceModelPeek.FileExtensions"/>; anything else is ignored silently) and indexes
    /// every <c>.subckt</c> in them. Never throws for a file: an unreadable one is counted. A folder
    /// with no readable part at all comes back with a <see cref="VendorLibraryBuild.Refusal"/>.
    /// </summary>
    public static VendorLibraryBuild Build(string root, RunControl? control = null)
    {
        string full;
        try { full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) { return Refused(root, $"'{root}' is not a usable path: {ex.Message}"); }

        if (!Directory.Exists(full)) return Refused(full, $"'{full}' is not a folder.");

        var files = EnumerateLibraryFiles(full, out var modelDirs);

        var entries    = new List<VendorLibraryEntry>();
        var duplicates = new List<VendorLibraryDuplicate>();
        var tolRefused = new List<VendorLibraryToleranceRefusal>();
        var seen       = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int read = 0, encrypted = 0, empty = 0, unreadable = 0, unparsed = 0;

        foreach (var (abs, rel) in files)
        {
            control?.Token.ThrowIfCancellationRequested();

            var scan = ScanCached(full, abs, rel, ref read);
            switch (scan.Kind)
            {
                case FileKind.Encrypted:  encrypted++;  continue;
                case FileKind.Empty:      empty++;      continue;
                case FileKind.Unreadable: unreadable++; continue;
            }
            unparsed += scan.Unparsed;
            if (scan.ToleranceRefusal is { } tr) tolRefused.Add(new VendorLibraryToleranceRefusal(rel, tr));

            foreach (var e in scan.Entries)
            {
                // First by path order wins; the later copy is counted and NAMED, because two files
                // defining one part with different contents is exactly what a user needs to know
                // before trusting whichever one got placed.
                if (seen.TryGetValue(e.Part, out string? first))
                {
                    duplicates.Add(new VendorLibraryDuplicate(e.Part, first, e.File));
                    continue;
                }
                seen[e.Part] = e.File;
                entries.Add(e);
            }
        }

        // The model folders (brief vendorlib-9 R-vl9-1b): one part.cpart each, and nothing else opened there.
        var unreadableModels = new List<(string, string)>();
        foreach (var (abs, rel) in modelDirs)
        {
            control?.Token.ThrowIfCancellationRequested();
            var (entry, why) = ScanModelCached(full, abs, rel);
            if (entry is null) { unreadableModels.Add((rel, why ?? "unreadable")); continue; }
            if (seen.TryGetValue(entry.Part, out string? first))
            {
                duplicates.Add(new VendorLibraryDuplicate(entry.Part, first, entry.File));
                continue;
            }
            seen[entry.Part] = entry.File;
            entries.Add(entry);
        }

        string? refusal = null;
        if (entries.Count == 0)
            refusal = encrypted > 0
                ? EncryptedSentence(encrypted)
                // Said with what a readable library looks like: the usual way here is picking a
                // manufacturer's design-kit edition for a schematic-capture tool, whose circuits sit in
                // that tool's own database and look nothing like a SPICE file.
                : unreadableModels.Count > 0
                    ? $"'{full}' holds {VendorLibraryBuild.Plural(unreadableModels.Count, "model folder", "model folders")} " +
                      "and none could be read: " + string.Join("; ", unreadableModels.Take(5).Select(m => $"{m.Item1} ({m.Item2})")) + "."
                : $"'{full}' holds no readable .subckt part and no model folder. A vendor library is the plain-text SPICE " +
                  "edition — a folder of model files (.lib, .sub, .cir …) with one .subckt per part — or a model " +
                  "library, a folder of model folders each holding a part.cpart. A " +
                  "design kit made for another tool keeps its circuits in that tool's own format and " +
                  "cannot be read.";

        return new VendorLibraryBuild(full, entries, files.Count, read, encrypted, empty, unreadable,
                                      unparsed, duplicates, refusal)
        {
            ToleranceRefusals = tolRefused, ModelFolders = modelDirs.Count, UnreadableModels = unreadableModels,
        };
    }

    private static VendorLibraryBuild Refused(string root, string why)
        => new(root, [], 0, 0, 0, 0, 0, 0, [], why);

    /// <summary>
    /// The entries of ONE file of the library at <paramref name="root"/> — what a run asks for the files
    /// its placed parts name, so a headless extraction scans those files' headers and not the whole
    /// library (brief vendorlib-3 R-vl3-2b). Served from the same per-file cache a build fills, keyed
    /// on the file's time and length, so a file that changed since it was indexed is re-scanned first
    /// and a stale byte block is never read (R-vl3-2a).
    /// </summary>
    /// <returns>The file's entries, or null when it is missing, encrypted, empty or unreadable.</returns>
    public static IReadOnlyList<VendorLibraryEntry>? ScanFile(string root, string relFile)
    {
        string full = KeyOf(root);
        string rel  = relFile.Replace('\\', '/').TrimStart('/');
        string abs;
        try { abs = Path.GetFullPath(Path.Combine(full, CircuitRF.Core.RefPath.ToNative(rel))); }
        catch { return null; }
        if (!File.Exists(abs)) return null;

        int read = 0;
        var scan = ScanCached(full, abs, rel, ref read);
        return scan.Kind == FileKind.Readable ? scan.Entries : null;
    }

    /// <summary>Every library file under <paramref name="root"/>, in ordinal relative-path order, and every model
    /// folder. Hidden files and folders (a leading dot) are skipped. A model folder is a LEAF (brief vendorlib-9
    /// R-vl9-1b): it is listed, and nothing inside it is — its <c>models/</c> hold the model's own files, never
    /// parts.</summary>
    private static List<(string Abs, string Rel)> EnumerateLibraryFiles(string root, out List<(string Abs, string Rel)> modelDirs)
    {
        var exts = new HashSet<string>(SpiceModelPeek.FileExtensions, StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        var models = new List<(string, string)>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            if (ModelLibrary.ModelFolder.Is(dir))
            {
                models.Add((dir, dir.Length == root.Length ? "" : Path.GetRelativePath(root, dir).Replace('\\', '/')));
                continue;
            }
            IEnumerable<string> subdirs, here;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
                here    = Directory.EnumerateFiles(dir);
                foreach (string f in here)
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith('.') || !exts.Contains(Path.GetExtension(f))) continue;
                    list.Add((f, Path.GetRelativePath(root, f).Replace('\\', '/')));
                }
                foreach (string d in subdirs)
                    if (!Path.GetFileName(d).StartsWith('.')) stack.Push(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        models.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        modelDirs = models;
        return list;
    }

    // ── Model folders ─────────────────────────────────────────────────────────

    private sealed record ModelScan(DateTime Mtime, long Length, VendorLibraryEntry? Entry, string? Reason);
    private static readonly Dictionary<string, ModelScan> _models = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> _manifestReads = new(StringComparer.Ordinal);

    /// <summary>
    /// How many <c>part.cpart</c> files this process has read for the library at <paramref name="root"/> —
    /// the counter brief vendorlib-9 R-vl9-1e gates the index on: one per model folder per change, and no
    /// <c>.cnl</c>, <c>.csch</c> or <c>.csym</c> at all (those are not counted here because nothing here opens
    /// them; <see cref="FileReads"/> counts SPICE files only).
    /// </summary>
    public static long ManifestReads(string root)
    {
        lock (_gate) return _manifestReads.GetValueOrDefault(KeyOf(root));
    }

    /// <summary>
    /// The entry of ONE model folder of the library at <paramref name="root"/> — what a placed model's symbol, its
    /// parameter interface and a run ask, served from the cache a build fills (keyed on <c>part.cpart</c>'s time and
    /// length). Null when the folder holds no readable <c>part.cpart</c>.
    /// </summary>
    public static VendorLibraryEntry? ModelEntry(string root, string modelFolderRel)
    {
        string full = KeyOf(root);
        string rel  = modelFolderRel.Replace('\\', '/').Trim('/');
        string dir;
        try { dir = Path.GetFullPath(Path.Combine(full, CircuitRF.Core.RefPath.ToNative(rel))); }
        catch { return null; }
        return ModelLibrary.ModelFolder.Is(dir) ? ScanModelCached(full, dir, rel).Entry : null;
    }

    /// <summary>One model folder's entry, from its <c>part.cpart</c>, re-read only when that file's time or
    /// length changed. Null with the reader's sentence when it cannot be read.</summary>
    private static (VendorLibraryEntry? Entry, string? Reason) ScanModelCached(string root, string dir, string rel)
    {
        string path = Path.Combine(dir, ModelLibrary.ModelFolder.PartFileName);
        DateTime mtime; long length;
        try
        {
            var info = new FileInfo(path);
            (mtime, length) = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, ex.Message); }

        string key = root + "\n" + path;
        lock (_gate)
            if (_models.TryGetValue(key, out var hit) && hit.Mtime == mtime && hit.Length == length)
                return (hit.Entry, hit.Reason);

        lock (_gate) _manifestReads[root] = _manifestReads.GetValueOrDefault(root) + 1;
        var (manifest, refusal) = ModelLibrary.ModelPartManifest.Read(path);
        var scan = manifest is null
            ? new ModelScan(mtime, length, null, refusal)
            : new ModelScan(mtime, length, EntryOf(manifest, rel), null);
        lock (_gate) _models[key] = scan;
        return (scan.Entry, scan.Reason);
    }

    /// <summary>
    /// A model folder as an index entry: <c>part.cpart</c>'s Description, Value, Frequency and case codes fill the
    /// fields a SPICE header fills (R-vl9-1c), so value-aware search, the range warning and the case footprint
    /// treat both kinds alike. The part is named by its FOLDER, which is how a placed model refers to it; check
    /// says so when Part disagrees.
    /// </summary>
    internal static VendorLibraryEntry EntryOf(ModelLibrary.ModelPartManifest m, string rel)
    {
        string[] segs = rel.Length == 0 ? [] : rel.Split('/');
        string folder = segs.Length == 0 ? m.Part : segs[^1];
        string[] categories = segs.Length == 0 ? [] : segs[..^1];

        string? description = m.Description.Trim().Length > 0 ? m.Description.Trim() : null;
        double? value = null;
        var quantity = VendorPartQuantity.None;
        string v = m.Value.Replace(" ", "");
        if (v.Length > 0 && VendorLibrarySearch.TryReadValue(v, out double read, out var unit))
            (value, quantity) = (read, unit);

        double? lo = null, hi = null;
        if (m.Frequency is { } f)
        {
            lo = HzOf(f.Low);
            hi = HzOf(f.High);
        }

        var header = new VendorLibraryHeader
        {
            Description       = description,
            DescriptionPlain  = description,
            DescriptionFields = description is null ? [] : [.. description.Split('/').Select(x => x.Trim())],
            PartNumber        = m.Part,
            Quantity          = quantity,
            Value             = value,
            FrequencyLowHz    = lo,
            FrequencyHighHz   = hi,
            CaseMetric        = m.CaseMetric.Trim().Length > 0 ? m.CaseMetric.Trim() : null,
            CaseImperial      = m.CaseImperial.Trim().Length > 0 ? m.CaseImperial.Trim() : null,
        };
        return new VendorLibraryEntry(folder, rel, categories, folder, 0, 0, [.. m.Pins], header)
        {
            Model = new VendorModelInfo(m.Cell, m.Kind, m.Glyph, [.. m.Parameters]),
        };
    }

    /// <summary><c>"20 GHz"</c> → 2e10, through the expression engine's own unit table; null when it does not read.</summary>
    private static double? HzOf(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var (expr, unit) = CircuitRF.Core.Expressions.Units.LiftInlineUnit(t);
        try
        {
            double x = new CircuitRF.Core.Expressions.Evaluator()
                .Eval(expr, new CircuitRF.Core.Expressions.Scope("hz"), unit).AsReal();
            return double.IsFinite(x) ? x : null;
        }
        catch { return null; }
    }

    private static FileScan ScanCached(string root, string abs, string rel, ref int read)
    {
        DateTime mtime;
        long length;
        try
        {
            var info = new FileInfo(abs);
            mtime  = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileScan(default, 0, FileKind.Unreadable, [], 0);
        }

        // Keyed on root AND file: an entry's relative path and folders are root-relative.
        string key = root + "\n" + abs;
        lock (_gate)
            if (_files.TryGetValue(key, out var hit) && hit.Mtime == mtime && hit.Length == length)
                return hit;

        read++;
        CountRead(root);
        FileScan scan;
        if (length == 0) scan = new FileScan(mtime, 0, FileKind.Empty, [], 0);
        else
        {
            try { scan = Scan(File.ReadAllBytes(abs), rel) with { Mtime = mtime, Length = length }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                scan = new FileScan(mtime, length, FileKind.Unreadable, [], 0);
            }
        }

        lock (_gate) _files[key] = scan;
        return scan;
    }

    // ── The scan ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One file's bytes → its entries. A line-by-line pass over the raw bytes, tracking each line's
    /// byte offset so an entry's block can be read back alone. The text is single-byte for every
    /// token this looks at (directives, names, header field names), so the bytes are decoded only for
    /// the lines that are kept.
    /// </summary>
    private static FileScan Scan(byte[] bytes, string rel)
    {
        // A NUL in the first few kilobytes is a binary file, not a netlist.
        int probe = Math.Min(bytes.Length, 4096);
        if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0)
            return new FileScan(default, 0, FileKind.Unreadable, [], 0);

        string[] folders = rel.Contains('/') ? rel[..rel.LastIndexOf('/')].Split('/') : [];
        string stem = Path.GetFileNameWithoutExtension(rel);

        var entries = new List<VendorLibraryEntry>();
        var header  = new List<string>();
        int unparsed = 0;

        // The open subcircuit, if any.
        string? part = null;
        List<string>? ports = null;
        long start = 0;
        bool collectingPorts = false;
        int depth = 0;
        int encodedRun = 0;

        int pos = 0;
        while (pos < bytes.Length)
        {
            int lineStart = pos;
            int nl = Array.IndexOf(bytes, (byte)'\n', pos);
            int lineEnd = nl < 0 ? bytes.Length : nl;          // exclusive, before '\n'
            pos = nl < 0 ? bytes.Length : nl + 1;

            int s = lineStart;
            while (s < lineEnd && bytes[s] is (byte)' ' or (byte)'\t') s++;
            int e = lineEnd;
            while (e > s && bytes[e - 1] is (byte)'\r' or (byte)' ' or (byte)'\t') e--;
            if (s == e) { collectingPorts = false; continue; }

            byte first = bytes[s];

            if (IsEncryptionMarker(bytes, s, e)) return Encrypted();

            if (first == (byte)'*')
            {
                collectingPorts = false;
                if (part is null) header.Add(Encoding.Latin1.GetString(bytes, s, e - s));
                continue;
            }

            if (first == (byte)'+' && collectingPorts && ports is not null)
            {
                if (AddPorts(Encoding.Latin1.GetString(bytes, s + 1, e - s - 1), ports)) collectingPorts = false;
                continue;
            }
            collectingPorts = false;

            if (StartsWithWord(bytes, s, e, ".subckt"))
            {
                if (part is not null) { depth++; continue; }   // nested: part of the outer body

                string line = Encoding.Latin1.GetString(bytes, s, e - s);
                var words = line.Split((char[]?)[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 2) { header.Clear(); continue; }

                part  = words[1];
                ports = [];
                start = lineStart;
                encodedRun = 0;
                collectingPorts = !AddPorts(string.Join(' ', words.Skip(2)), ports);
                continue;
            }

            if (StartsWithWord(bytes, s, e, ".ends"))
            {
                if (part is null) { header.Clear(); continue; }
                if (depth > 0) { depth--; continue; }

                var parsed = VendorLibraryHeader.Parse(header, out int bad);
                unparsed += bad;
                entries.Add(new VendorLibraryEntry(part, rel, folders, stem, start,
                                                   checked((int)(pos - start)), ports!, parsed));
                part = null; ports = null;
                header.Clear();
                continue;
            }

            // A body line made of nothing but a long hex/base64 run, several in a row, is an encoded
            // block — the body of a part that cannot be read.
            if (part is not null)
            {
                encodedRun = LooksEncoded(bytes, s, e) ? encodedRun + 1 : 0;
                if (encodedRun >= 3) return Encrypted();
            }
        }

        // The tolerances, from the headers and names just read — no further read of anything (R-vl5-1f).
        var decoded = VendorPartToleranceDecoder.DecodeFile(entries, out string? tolRefusal);
        return new FileScan(default, 0, FileKind.Readable, decoded, unparsed, tolRefusal);

        static FileScan Encrypted() => new(default, 0, FileKind.Encrypted, [], 0);
    }

    /// <summary>Adds the port words of a <c>.subckt</c> line; true once the parameter section starts.</summary>
    private static bool AddPorts(string words, List<string> ports)
    {
        foreach (string w in words.Split((char[]?)[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Contains('=') || w.EndsWith(':')) return true;
            ports.Add(w);
        }
        return false;
    }

    private static bool StartsWithWord(byte[] b, int s, int e, string word)
    {
        if (e - s < word.Length) return false;
        for (int i = 0; i < word.Length; i++)
            if (char.ToLowerInvariant((char)b[s + i]) != word[i]) return false;
        return e - s == word.Length || b[s + word.Length] is (byte)' ' or (byte)'\t';
    }

    /// <summary>
    /// The structural shapes of an encrypted model, from the format rather than from any tool:
    /// a protection directive (<c>.protect</c>), a protection pragma (<c>pragma protect</c>), a
    /// begin-marker naming encryption, or a comment stating the content is encrypted.
    /// </summary>
    private static bool IsEncryptionMarker(byte[] b, int s, int e)
    {
        if (StartsWithWord(b, s, e, ".protect")) return true;
        // Only a short line can be a marker; a long body line is checked by LooksEncoded instead.
        if (e - s > 200) return false;
        // Allocation-free pre-test: almost no line holds either word, and this runs on every line.
        if (!ContainsAscii(b, s, e, "encrypt") && !ContainsAscii(b, s, e, "protect")) return false;
        string line = Encoding.Latin1.GetString(b, s, e - s).ToLowerInvariant();
        if (line.Contains("pragma protect", StringComparison.Ordinal)) return true;
        if (!line.Contains("encrypt", StringComparison.Ordinal)) return false;
        if (line.Contains("not encrypt", StringComparison.Ordinal) || line.Contains("unencrypt", StringComparison.Ordinal))
            return false;
        // A comment saying so, or a begin/start marker for an encrypted block.
        return line[0] is '*' or ';' or '$' or '/' || line.Contains("begin", StringComparison.Ordinal)
                                                    || line.Contains("start", StringComparison.Ordinal);
    }

    /// <summary>Case-insensitive search for a lower-case ASCII word in a byte range.</summary>
    private static bool ContainsAscii(byte[] b, int s, int e, string word)
    {
        for (int i = s; i <= e - word.Length; i++)
        {
            int k = 0;
            while (k < word.Length && (b[i + k] | 0x20) == word[k]) k++;
            if (k == word.Length) return true;
        }
        return false;
    }

    /// <summary>A line of 40+ characters drawn only from the hex/base64 alphabet, with no spaces.</summary>
    private static bool LooksEncoded(byte[] b, int s, int e)
    {
        if (e - s < 40) return false;
        for (int i = s; i < e; i++)
        {
            byte c = b[i];
            bool ok = c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                         or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/' or (byte)'=';
            if (!ok) return false;
        }
        return true;
    }

    // ── The session's indexes, built in the background ────────────────────────
    //
    // R-vl1-2e: a workspace open STARTS a build and never waits for one. What a caller can ask in the
    // meantime is whether one is running, and what the last finished build found.

    private static readonly Dictionary<string, Task<VendorLibraryBuild>> _running = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, VendorLibraryBuild> _latest = new(StringComparer.Ordinal);

    /// <summary>Raised on a thread-pool thread when a background build finishes, with the library root.</summary>
    public static event Action<string>? Built;

    /// <summary>
    /// Starts rebuilding <paramref name="root"/>'s index on a background thread, or returns the build
    /// already running. A rebuild re-scans only the files whose time or length changed.
    /// </summary>
    public static Task<VendorLibraryBuild> StartBuild(string root)
    {
        string key = KeyOf(root);
        lock (_gate)
        {
            if (_running.TryGetValue(key, out var running)) return running;
            var task = Task.Run(() =>
            {
                VendorLibraryBuild b;
                try { b = Build(key); }
                catch (Exception ex) { b = Refused(key, $"'{key}' could not be indexed: {ex.Message}"); }
                lock (_gate) { _latest[key] = b; _running.Remove(key); }
                Built?.Invoke(key);
                return b;
            });
            _running[key] = task;
            return task;
        }
    }

    /// <summary>The last finished build of <paramref name="root"/>, or null when none has finished.</summary>
    public static VendorLibraryBuild? Latest(string root)
    {
        lock (_gate) return _latest.GetValueOrDefault(KeyOf(root));
    }

    /// <summary>True while a background build of <paramref name="root"/> is running.</summary>
    public static bool IsBuilding(string root)
    {
        lock (_gate) return _running.ContainsKey(KeyOf(root));
    }

    /// <summary>Records a build made in the foreground (an Add, a Validate) as the latest.</summary>
    internal static void Remember(VendorLibraryBuild build)
    {
        lock (_gate) _latest[KeyOf(build.Root)] = build;
    }

    private static string KeyOf(string root)
    {
        try { return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return root; }
    }

    // ── Reading one block ─────────────────────────────────────────────────────

    /// <summary>The text of one entry's <c>.subckt</c> … <c>.ends</c> block, read alone.</summary>
    public static string ReadBlock(string libraryRoot, VendorLibraryEntry entry)
    {
        string path = Path.Combine(libraryRoot, CircuitRF.Core.RefPath.ToNative(entry.File));
        CountRead(libraryRoot);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(entry.Offset, SeekOrigin.Begin);
        var buf = new byte[entry.Length];
        fs.ReadExactly(buf);
        return Encoding.Latin1.GetString(buf);
    }
}
