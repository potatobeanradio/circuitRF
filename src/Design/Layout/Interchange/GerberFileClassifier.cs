// Which files in a folder are part of a Gerber file set at all
// (docs/sonnet-briefs/brief-L4g-gerber-import-orchestration.md §1).
//
// A Gerber "file" is not the unit of work — a board is a FOLDER, and the folder holds artwork, drill
// data and a scattering of companion files that are neither. R-L4g-1 settles how to tell them apart:
// BY CONTENT, extension second. Extensions in this format are conventional at best and collide at
// worst — artwork extensions vary widely between toolchains, a drill file and its human-readable
// listing routinely share a stem, and a plain .txt is artwork's companion report or a drill file
// depending on who wrote it. So nothing below ever branches on an extension: renaming a file to a
// misleading extension does not change what it is classified as, which is gate 2.

using System.Text;
using System.Text.Json;

namespace CircuitRF.Design.Layout.Interchange;

public enum GerberFileKind
{
    /// <summary>A `%FS…%`/`%MO…%` pair, or a recognizable stream of D-operation blocks.</summary>
    Artwork,

    /// <summary>An `M48` header, or `T&lt;n&gt;C&lt;diameter&gt;` tool definitions over a coordinate
    /// stream.</summary>
    Drill,

    /// <summary>A `.gbrjob` job file — JSON, and the single most valuable file in the set
    /// (R-L4g-5 rung 0).</summary>
    JobFile,

    /// <summary>
    /// GI4 R-gi4-1. A companion file the OUTPUT JOB wrote about itself: a <c>KEYWORD  VALUE</c>
    /// parameter file stating the coordinate format, the units and the zero suppression the whole job
    /// was written with, or (R-gi4-2, the same kind, separately identified by
    /// <see cref="GerberFileClass.Form"/>) a tool listing carrying the plating column a drill file
    /// most often omits.
    ///
    /// <para><b>Recognised by its KEYWORDS, never by a filename convention</b> — the doctrine this
    /// file's own header states, and the reason gate 2 exists. See
    /// <see cref="GerberDeclarationFile"/>.</para>
    /// </summary>
    Declaration,

    /// <summary>
    /// GI4 R-gi4-10. An archive sitting beside the artwork, holding a second copy of all of it.
    /// Reported as skipped exactly like anything else that is not artwork or drill data — but named
    /// as what it is, so a folder whose ONLY artwork is inside one can be an OFFER rather than a dead
    /// end. <b>Never opened unless the folder needs it</b> (R-gi4-11): reading both is how two
    /// versions of one board get silently merged.
    /// </summary>
    Archive,

    /// <summary>
    /// GI5. The board netlist an output set ships beside its artwork: one record per pad and per
    /// hole, carrying the net at that coordinate, whether the hole is plated, the component reference
    /// and pin where there is one, and the layers the feature reaches.
    ///
    /// <para>It is <b>evidence about the artwork and never geometry</b> (R-gi5-1) — see
    /// <see cref="BoardNetlistFile"/>. It is what settles the three questions a Gerber import
    /// currently apologises for in the same run: via versus plated component hole, the net names a
    /// composited layer destroyed, and the layer span no drill file declared.</para>
    /// </summary>
    Netlist,

    /// <summary>
    /// railRF R-rail2-12. The placement (pick-and-place) table an output set ships beside its
    /// artwork: one row per reference, carrying its coordinate, rotation, mirror and footprint.
    ///
    /// <para>It is <b>evidence about the artwork and never geometry</b> (R-rail2-1), exactly as the
    /// board netlist is — see <see cref="PlacementFile"/>. A Gerber import does not consume one, so
    /// it is still reported as skipped; what changes is that it is skipped BY NAME rather than as
    /// "no Gerber or drill content in its head".</para>
    /// </summary>
    Placement,

    /// <summary>
    /// railRF R-rail2-12. The BILL OF MATERIALS — the internal part number, the reference, the value,
    /// the footprint and a free-text description. Evidence about the artwork, never geometry, and
    /// skipped by name for <see cref="Placement"/>'s reason. See <see cref="BomFile"/>.
    /// </summary>
    Bom,

    /// <summary>A sibling to skip: a report, a listing, an image, a PDF.
    /// R-L4g-2 — every one of these is reported by name, once.</summary>
    Other,
}

/// <summary>What one file turned out to be, and the evidence that settled it — the "why" is carried
/// so the summary can say more than "skipped".</summary>
public sealed record GerberFileClass(string Path, GerberFileKind Kind, string Why)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>GI4 R-gi4-2: which of the two companion forms this is, for a
    /// <see cref="GerberFileKind.Declaration"/>. <see cref="GerberDeclarationForm.None"/> for every
    /// other kind. Additive with a default, so every existing construction is unchanged.</summary>
    public GerberDeclarationForm Form { get; init; } = GerberDeclarationForm.None;
}

public static class GerberFileClassifier
{
    /// <summary>How much of a file's head is enough to classify it. A Gerber file declares its format
    /// in the first few lines and a drill file its M48 header; anything that needs more than this to
    /// identify itself is not something this import should be guessing about.</summary>
    private const int HeadBytes = 16 * 1024;

    public static GerberFileClass Classify(string path)
    {
        string head;
        try
        {
            head = ReadHead(path);
        }
        catch (IOException ex)
        {
            return new GerberFileClass(path, GerberFileKind.Other, $"could not be read ({ex.GetType().Name})");
        }
        catch (UnauthorizedAccessException)
        {
            return new GerberFileClass(path, GerberFileKind.Other, "could not be read (access denied)");
        }

        return ClassifyContent(path, head);
    }

    /// <summary>The classification itself, over text that is already in hand — the form the gate
    /// drives, so no fixture needs a temporary directory to assert what a byte stream is.</summary>
    public static GerberFileClass ClassifyContent(string path, string head)
    {
        // Before the text test, because an archive IS binary and "not text" is the true, useless
        // answer this replaces (R-gi4-10). The four bytes are ASCII-range, so they survive the UTF-8
        // decode above unchanged and no second read of the file is needed.
        if (LooksLikeZipArchive(head))
            return new GerberFileClass(path, GerberFileKind.Archive, "a ZIP archive; its contents were not read");

        if (LooksBinary(head))
            return new GerberFileClass(path, GerberFileKind.Other, "not text");

        if (LooksLikeJobFile(head))
            return new GerberFileClass(path, GerberFileKind.JobFile, "a job file (JSON naming the set's files)");

        if (ArtworkEvidence(head) is { } artwork)
            return new GerberFileClass(path, GerberFileKind.Artwork, artwork);

        if (DrillEvidence(head) is { } drill)
            return new GerberFileClass(path, GerberFileKind.Drill, drill);

        // GI5, and BEFORE the declaration test rather than after it: a netlist's signature is a
        // specific one — three-digit operation codes over letter-tagged coordinates — while a
        // declaration is recognised by a two-keyword minimum over free-form KEYWORD/VALUE lines, and
        // the specific test must never be reachable only when the loose one happens to miss.
        if (BoardNetlistFile.Recognize(head, out string netlist))
            return new GerberFileClass(path, GerberFileKind.Netlist, netlist);

        // LAST OF THE SPECIFIC TESTS, and that order is gate 2's second half: a real drill file
        // renamed to whatever a declaration is conventionally called still reaches DrillEvidence
        // first and still classifies as drill data. Nothing here can take a file the drill test
        // already claimed.
        if (GerberDeclarationFile.Recognize(head, out string declaration) is var form &&
            form != GerberDeclarationForm.None)
            return new GerberFileClass(path, GerberFileKind.Declaration, declaration) { Form = form };

        // railRF R-rail2-12, and AFTER every test above for the reason that rule states: the
        // placement and bill-of-materials signatures are a HEADER ROW NAMING COLUMNS, which is far
        // weaker than a netlist's three-digit operation codes or a declaration's named keywords, and
        // nothing added here may take a file one of those already claimed.
        //
        // The two are genuinely ambiguous with each other — a comma-separated table is a comma-
        // separated table — so each declines a header the other also matches, and the reader itself
        // refuses naming the two flags. One more refusal is cheaper than a board whose parts are all
        // at (0, 0).
        if (PlacementFile.Recognize(head, out string placement))
            return new GerberFileClass(path, GerberFileKind.Placement, placement);

        if (BomFile.Recognize(head, out string bom))
            return new GerberFileClass(path, GerberFileKind.Bom, bom);

        return new GerberFileClass(path, GerberFileKind.Other, "no Gerber or drill content in its head");
    }

    /// <summary>Classifies every file directly inside <paramref name="dir"/> (never recursively — a
    /// sub-folder is a different set, and R-L4g-3's reach outside the chosen folder is an OFFER, not
    /// an import). Ordered by name so a summary reads the same way twice.</summary>
    public static IReadOnlyList<GerberFileClass> ClassifyFolder(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(Classify)];
    }

    /// <summary>
    /// R-L4g-3: drill files frequently do NOT live with the artwork — a production output set commonly
    /// puts artwork in one folder and drill data in a sibling. Looks one level UP and one level DOWN
    /// from the chosen files' own folder for drill files whose stem matches an artwork stem, and
    /// returns them as CANDIDATES.
    ///
    /// <para><b>Never pull them in silently.</b> An import that quietly reached outside the folder the
    /// user pointed at is a surprise, and a surprise in a file importer is a support question forever.
    /// <c>GerberImport.ImportResult.DrillCandidates</c> carries these; L4h asks.</para>
    /// </summary>
    public static IReadOnlyList<string> FindSiblingDrillCandidates(IReadOnlyList<string> chosenFiles)
    {
        var chosen = new HashSet<string>(chosenFiles.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        var chosenNames = new HashSet<string>(chosenFiles.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        var stems = new List<string>();

        foreach (var file in chosenFiles)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(file));
            if (dir is not null && !folders.Contains(dir, StringComparer.OrdinalIgnoreCase)) folders.Add(dir);
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem.Length > 0 && !stems.Contains(stem, StringComparer.OrdinalIgnoreCase)) stems.Add(stem);
        }

        // "One level up and one level down", read as the case R-L4g-3 actually names: a production
        // output set commonly puts artwork in one folder and drill data in a SIBLING. So the search is
        // the parent's own files, the parent's other sub-folders, and the chosen folder's own
        // sub-folders — and no further. Anything deeper stops being "next to" anything.
        var searchDirs = new List<string>();
        foreach (var folder in folders)
        {
            if (Directory.GetParent(folder)?.FullName is { } up)
            {
                AddDir(searchDirs, up);
                foreach (var sibling in SubDirectories(up))
                    if (!string.Equals(sibling, folder, StringComparison.OrdinalIgnoreCase)) AddDir(searchDirs, sibling);
            }
            foreach (var down in SubDirectories(folder)) AddDir(searchDirs, down);
        }

        // THE NAME TEST RUNS BEFORE ANY FILE IS OPENED, and that ordering is the whole cost of this
        // search. Classifying is a 16 KB read per file, and "one level up" is whatever folder the
        // user's board happens to sit in — a downloads folder, a home directory, a scratch directory
        // with a thousand neighbours. Classify-then-filter reads every file in every one of those:
        // measured at over ten minutes, on an import that in the end offered nothing, because
        // nothing there shared the board's name. Filtering on the stem first makes the same search
        // read only the handful of files that could possibly be an answer, and usually none at all.
        var candidates = new List<string>();
        foreach (var dir in searchDirs)
            foreach (var path in FilesInDirectory(dir))
            {
                if (chosen.Contains(NormalizePath(path))) continue;
                // A file NAME already in the set is the same drill data reached by a second route (a
                // previous import's copy, a mirror of the output folder). Offering it back would ask
                // the user to import a file they already imported.
                if (chosenNames.Contains(Path.GetFileName(path))) continue;
                string stem = Path.GetFileNameWithoutExtension(path);
                if (!stems.Any(s => StemsMatch(s, stem))) continue;
                if (Classify(path).Kind != GerberFileKind.Drill) continue;
                candidates.Add(path);
            }

        return [.. candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The file names directly inside a directory, or nothing at all if it cannot be read —
    /// an unreadable neighbour is not a reason to fail an import of somewhere else.</summary>
    private static IEnumerable<string> FilesInDirectory(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SubDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (IOException) { return []; }              // an unreadable neighbour is not a reason to fail
        catch (UnauthorizedAccessException) { return []; }
    }

    private static void AddDir(List<string> dirs, string dir)
    {
        if (!dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    /// <summary>Two stems belong to the same board when they are equal, or when one is the other
    /// followed by a separator — "board" and "board-PTH" are the same board; "board" and "boardroom"
    /// are not, which is why the separator is required rather than a bare prefix test.</summary>
    internal static bool StemsMatch(string artworkStem, string drillStem)
    {
        if (string.Equals(artworkStem, drillStem, StringComparison.OrdinalIgnoreCase)) return true;
        return HasSeparatedPrefix(artworkStem, drillStem) || HasSeparatedPrefix(drillStem, artworkStem);
    }

    private static bool HasSeparatedPrefix(string prefix, string full) =>
        full.Length > prefix.Length &&
        full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        !char.IsLetterOrDigit(full[prefix.Length]);

    // ── Content sniffing ──────────────────────────────────────────────────────

    /// <summary>The ZIP local-file-header signature, <c>PK\x03\x04</c>. Only ZIP, deliberately:
    /// this recognition exists to support R-gi4-10's OFFER to look inside, and offering to open
    /// something circuitRF cannot open would be worse than saying nothing. Anything else stays "not
    /// text".</summary>
    private static bool LooksLikeZipArchive(string head) =>
        head.StartsWith("PK\u0003\u0004", StringComparison.Ordinal) ||
        head.StartsWith("PK\u0005\u0006", StringComparison.Ordinal) ||     // an empty archive
        head.StartsWith("PK\u0007\u0008", StringComparison.Ordinal);       // a spanned one

    private static bool LooksBinary(string head)
    {
        int limit = Math.Min(head.Length, 512);
        for (int i = 0; i < limit; i++)
        {
            char c = head[i];
            if (c is '\t' or '\n' or '\r' or '\f' or '\uFEFF') continue;
            if (c < 0x20 || c == 0x7F) return true;
        }
        return false;
    }

    private static bool LooksLikeJobFile(string head)
    {
        string trimmed = head.TrimStart('\uFEFF').TrimStart();
        if (!trimmed.StartsWith('{')) return false;

        // The three keys the format's own schema puts at the top level. Any one of them is enough:
        // L4c's own writer emits Header + FilesAttributes and nothing else.
        if (trimmed.Contains("FilesAttributes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("GeneralSpecs", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("MaterialStackup", StringComparison.OrdinalIgnoreCase))
            return true;

        // A JSON document that names none of them is still not artwork or drill data, but calling it a
        // job file would make the cascade read a stackup out of somebody's unrelated settings file.
        return false;
    }

    /// <summary>R-L4g-1's artwork test. The format statement is decisive; failing that, a stream of
    /// D-operation blocks is what a headerless fragment still looks like, and a drill file has none
    /// (its coordinate lines carry no `*` terminator and no D word).</summary>
    private static string? ArtworkEvidence(string head)
    {
        bool fs = head.Contains("%FS", StringComparison.Ordinal);
        bool mo = head.Contains("%MO", StringComparison.Ordinal);
        if (fs && mo) return "a Gerber format/unit declaration (%FS and %MO)";
        if (fs || mo) return fs ? "a Gerber format declaration (%FS)" : "a Gerber unit declaration (%MO)";
        if (head.Contains("%AD", StringComparison.Ordinal)) return "Gerber aperture definitions (%AD)";

        int ops = CountDrawOperations(head);
        if (ops >= 2) return $"{ops} Gerber draw/flash operation block(s)";
        return null;
    }

    private static int CountDrawOperations(string text)
    {
        int n = 0;
        for (int i = 0; i + 3 < text.Length; i++)
            if (text[i] == 'D' && text[i + 1] == '0' && text[i + 2] is '1' or '2' or '3' && text[i + 3] == '*') n++;
        return n;
    }

    /// <summary>R-L4g-1's drill test — deliberately not the mirror image of the artwork one. `M48` is
    /// decisive; without it the evidence is a TOOL TABLE over a coordinate stream, and both halves are
    /// required. A human-readable drill LISTING is the file this must not take: it names the same
    /// tools in a table of prose, so a `T&lt;n&gt;` line alone is not enough — the `C&lt;diameter&gt;`
    /// word and real coordinate lines are what separate the two.</summary>
    private static string? DrillEvidence(string head)
    {
        int tools = 0, coords = 0;
        bool m48 = false, m30 = false;

        foreach (var raw in head.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Equals("M48", StringComparison.Ordinal)) m48 = true;
            if (line.Equals("M30", StringComparison.Ordinal) || line.Equals("M00", StringComparison.Ordinal)) m30 = true;
            if (IsToolDefinition(line)) tools++;
            if (IsDrillCoordinate(line)) coords++;
        }

        if (m48) return "an M48 drill header";
        if (tools > 0 && coords > 0)
            return $"{tools} drill tool definition(s) over {coords} coordinate line(s)";
        if (m30 && coords > 0) return $"{coords} drill coordinate line(s) ending in M30";
        return null;
    }

    /// <summary>`T&lt;n&gt;` followed, somewhere on the line, by a `C&lt;number&gt;` diameter word.</summary>
    private static bool IsToolDefinition(string line)
    {
        if (line.Length < 4 || line[0] != 'T') return false;
        int i = 1;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == 1) return false;
        while (i < line.Length)
        {
            if (line[i] == 'C' && i + 1 < line.Length && (char.IsAsciiDigit(line[i + 1]) || line[i + 1] == '.'))
                return true;
            i++;
        }
        return false;
    }

    /// <summary>An `X`/`Y` coordinate line with NO Gerber block terminator. The `*` is what makes this
    /// unambiguous: `X100Y100D03*` is artwork, `X100Y100` is a drill hit.</summary>
    private static bool IsDrillCoordinate(string line)
    {
        if (line.Length < 2 || (line[0] != 'X' && line[0] != 'Y')) return false;
        if (line.Contains('*')) return false;
        foreach (char c in line)
            if (!char.IsAsciiDigit(c) && c is not ('X' or 'Y' or '+' or '-' or '.' or 'G' or 'A' or 'I' or 'J'))
                return false;
        return true;
    }

    private static string ReadHead(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] buffer = new byte[HeadBytes];
        // ReadAtLeast, not Read: a single Read is allowed to return fewer bytes than asked for even
        // when the file has them, and a short head here does not fail \u2014 it silently classifies a real
        // Gerber file as "no Gerber or drill content in its head" and drops a whole layer from the set.
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read).TrimStart('\uFEFF');
    }

    /// <summary>Whether <paramref name="text"/> parses as JSON at all — used by the job-file reader's
    /// own diagnostics rather than by the classification above, which must not pay for a full parse of
    /// every file in a folder.</summary>
    internal static bool IsWellFormedJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
