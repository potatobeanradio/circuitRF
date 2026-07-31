using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Core.Pdk;

/// <summary>
/// Imports a process design kit from a folder or a .zip, and reports what it found.
///
/// <para>Two things shape the design. First, kits arrive in many formats and circuitRF reads a few
/// of them, so the normal outcome is "understood some of this" and the report says exactly which
/// part. Second, artwork is treated as a first-class result even where it cannot be read — a kit
/// that ships symbol or layout drawings should say so, and say what it would take to use them,
/// rather than have them vanish because no reader exists yet.</para>
/// </summary>
public static class PdkImporter
{
    /// <summary>Files bigger than this are classified by name only; nothing peeks inside them.</summary>
    private const long PeekLimitBytes = 512 * 1024;
    private const int  PeekChars      = 4096;

    public static PdkImportReport Import(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failed(path ?? "", "No path was given.", "Choose the kit's folder or .zip file.");

        try
        {
            if (Directory.Exists(path))
                return ImportEntries(path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                                                          Path.AltDirectorySeparatorChar)),
                                     EnumerateFolder(path));

            if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                using (var zip = ZipFile.OpenRead(path))
                    return ImportEntries(path, Path.GetFileNameWithoutExtension(path), EnumerateZip(zip));

            return File.Exists(path)
                ? Failed(path, "That file is not a kit.",
                         "Choose the kit's folder, or a .zip containing it.")
                : Failed(path, "Nothing exists at that path.",
                         "Check the location — the kit may have moved or not be mounted.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(path, $"Access denied while reading the kit: {ex.Message}",
                          "Check the folder's permissions and try again.");
        }
        catch (InvalidDataException ex)
        {
            return Failed(path, $"The archive could not be opened: {ex.Message}",
                          "The .zip may be corrupt or only partly downloaded.");
        }
        catch (IOException ex)
        {
            return Failed(path, $"The kit could not be read: {ex.Message}");
        }
    }

    // ── entry enumeration ─────────────────────────────────────────────────────

    private readonly record struct Entry(string RelativePath, long Length, Func<Stream> Open);

    private static IEnumerable<Entry> EnumerateFolder(string root)
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            long len;
            try { len = new FileInfo(f).Length; } catch { continue; }
            yield return new(rel, len, () => File.OpenRead(f));
        }
    }

    private static IEnumerable<Entry> EnumerateZip(ZipArchive zip)
    {
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;          // directory entry
            yield return new(e.FullName.Replace('\\', '/'), e.Length, e.Open);
        }
    }

    // ── the import itself ─────────────────────────────────────────────────────

    private static PdkImportReport ImportEntries(string root, string kitName, IEnumerable<Entry> entries)
    {
        var report = new PdkImportReport { RootPath = root, KitName = string.IsNullOrEmpty(kitName) ? "Kit" : kitName };
        var recognizers = PdkFormatRegistry.All;

        var files = entries.ToList();
        if (files.Count == 0)
        {
            report.Status = PdkImportStatus.Failed;
            report.Blocker("The kit is empty.", "Check that the folder or archive is the kit root.");
            return report;
        }

        foreach (var e in files)
        {
            string Peek() => e.Length > PeekLimitBytes ? "" : PeekText(e.Open);

            PdkAsset? asset = null;
            foreach (var r in recognizers)
            {
                try { asset = r.Recognize(e.RelativePath, Peek); }
                catch { asset = null; }                          // a bad recogniser must not fail the import
                if (asset is not null) break;
            }

            report.Add(asset ?? new PdkAsset(e.RelativePath, PdkAssetKind.Other,
                                             PdkAssetSupport.Unrecognized, DescribeUnknown(e.RelativePath)));
        }

        DiscoverParts(report);
        Summarize(report);
        return report;
    }

    /// <summary>
    /// Find the parts a kit offers, and — the point of this pass — locate whatever artwork exists
    /// for each of them, wherever in the tree it happens to live.
    ///
    /// <para>Kits are not consistently organised: symbol drawings may sit in a folder of their own,
    /// or inside a per-cell database directory, or beside the netlist. Rather than assume a layout,
    /// this indexes every artwork and icon asset by a normalised name and matches parts against it,
    /// so a part finds its artwork regardless of where the kit chose to put it.</para>
    /// </summary>
    private static void DiscoverParts(PdkImportReport report)
    {
        var artwork = new List<(string Key, PdkAsset Asset)>();
        foreach (var a in report.Assets)
        {
            if (a.Kind is not (PdkAssetKind.SymbolArtwork or PdkAssetKind.LayoutArtwork or PdkAssetKind.PaletteIcon))
                continue;
            foreach (var key in NameKeys(a.RelativePath))
                artwork.Add((key, a));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Subcircuits are read first, but they are NOT the parts. They are the kit's implementation:
        // a component is assembled from them, and several revisions of one component share them.
        // What they carry that matters here is the terminal count and the parameter interface.
        var subcircuits = new List<SubcircuitDef>();
        foreach (var net in report.Assets.Where(a => a.Kind == PdkAssetKind.Netlist))
            foreach (var sub in SubcircuitsIn(report.RootPath, net.RelativePath))
                subcircuits.Add(sub);

        // ── The parts: cells the kit drew ────────────────────────────────────
        //
        // A component is a CELL — the thing a kit gives an icon and a symbol to, and the thing a
        // user reaches for. Only the <cell>/<view>/<file> shape qualifies; a flat folder of drawings
        // has no cell directory, and reading its parent as a cell name invents parts that do not
        // exist. A cell whose own name marks it as a drawing (WIDGET_SYM) is where a picture lives,
        // not a part, and is skipped.
        foreach (var a in report.Assets.Where(a => a.Kind is PdkAssetKind.SymbolArtwork or PdkAssetKind.LayoutArtwork))
        {
            var segs = a.RelativePath.Split('/');
            if (segs.Length < 3 || !IsViewDirectory(segs[^2])) continue;

            string cell = Unescape(segs[^3]);
            if (cell.Length == 0 || LooksLikeADrawingName(cell) || !seen.Add(cell)) continue;

            var match = BestSubcircuitFor(cell, subcircuits);
            report.Parts.Add(new PdkPart(
                Id: cell, DisplayName: cell, Category: "cell library",
                IconRelativePath: FindArtworkFor(cell, artwork, PdkAssetKind.PaletteIcon)?.RelativePath,
                SymbolArtwork:    FindArtworkFor(cell, artwork, PdkAssetKind.SymbolArtwork),
                LayoutArtwork:    FindArtworkFor(cell, artwork, PdkAssetKind.LayoutArtwork),
                Parameters:       match?.Parameters,
                PinCount:         match?.PinCount ?? 0));
        }

        // A kit that ships no cell database at all still has to yield something placeable, so its
        // subcircuits stand in as the parts. Only those the kit actually drew: one without artwork
        // is an internal building block, not a component.
        if (report.Parts.Count > 0) return;

        foreach (var sub in subcircuits)
        {
            if (!seen.Add(sub.Name)) continue;

            var sym = FindArtworkFor(sub.Name, artwork, PdkAssetKind.SymbolArtwork);
            var ico = FindArtworkFor(sub.Name, artwork, PdkAssetKind.PaletteIcon);
            if (sym is null && ico is null) continue;

            report.Parts.Add(new PdkPart(
                Id: sub.Name, DisplayName: sub.Name, Category: "netlist",
                IconRelativePath: ico?.RelativePath,
                SymbolArtwork:    sym,
                LayoutArtwork:    FindArtworkFor(sub.Name, artwork, PdkAssetKind.LayoutArtwork),
                Parameters:       sub.Parameters,
                PinCount:         sub.PinCount));
        }
    }

    /// <summary>
    /// The subcircuit that supplies a cell's parameter interface: the one sharing the most name
    /// tokens with it. Returns null when nothing shares at least two, rather than guessing.
    /// </summary>
    private static SubcircuitDef? BestSubcircuitFor(string cellName, List<SubcircuitDef> subcircuits)
    {
        // A word that appears in most of a kit's names is its family prefix and carries no
        // information — matching on it alone pairs a component with an unrelated subcircuit that
        // merely belongs to the same kit. Score on the distinctive words only.
        var common = CommonTokens(subcircuits);

        var cellTokens = new HashSet<string>(
            Tokens(cellName).Where(t => !common.Contains(t)), StringComparer.Ordinal);
        if (cellTokens.Count == 0) return null;

        SubcircuitDef? best = null;
        int bestShared = 0;

        foreach (var sub in subcircuits)
        {
            var subTokens = Tokens(sub.Name).Distinct(StringComparer.Ordinal)
                                            .Where(t => !common.Contains(t)).ToList();
            if (subTokens.Count == 0) continue;
            int shared    = subTokens.Count(cellTokens.Contains);
            int unshared  = subTokens.Count - shared;

            // Most of the subcircuit's name must appear in the cell's. Sharing only a family prefix
            // is not evidence: every name in a kit starts with the same one or two words, so a bare
            // "at least two shared" rule hands one component's parameters to an unrelated one.
            if (shared < MinMatchTokens || shared <= unshared) continue;

            if (shared > bestShared) { bestShared = shared; best = sub; }
        }

        return best;
    }

    /// <summary>
    /// Words appearing in at least half of the kit's subcircuit names — its family prefix. Empty
    /// for a kit with too few names for "most" to mean anything.
    /// </summary>
    private static HashSet<string> CommonTokens(List<SubcircuitDef> subcircuits)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (subcircuits.Count < 3) return result;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sub in subcircuits)
            foreach (var t in Tokens(sub.Name).Distinct(StringComparer.Ordinal))
                counts[t] = counts.GetValueOrDefault(t) + 1;

        int threshold = (subcircuits.Count + 1) / 2;
        foreach (var (token, n) in counts)
            if (n >= threshold) result.Add(token);

        return result;
    }

    /// <summary>View directory names used by per-cell database layouts.</summary>
    private static bool IsViewDirectory(string name) => name.ToLowerInvariant() is
        "symbol" or "layout" or "schematic" or "schematic_symbol" or "abstract" or "netlist";

    private static IEnumerable<string> NameKeys(string relativePath)
    {
        // RAW names, not normalised: matching is token-based, and normalising away the separators
        // first would collapse every name into one unsplittable run.
        var parts = relativePath.Split('/');
        yield return Path.GetFileNameWithoutExtension(parts[^1]);
        if (parts.Length >= 3 && IsViewDirectory(parts[^2]))
            yield return Unescape(parts[^3]);                                 // <cell>/<view>/<file>
    }

    /// <summary>
    /// Undo the `%X` case-escaping some tools use for cell directory names on case-insensitive
    /// filesystems, so `%F%S%L_%M%O%D%E%L` reads back as `FSL_MODEL`. Harmless on names that use no
    /// escaping.
    /// </summary>
    internal static string Unescape(string s) => s.Replace("%", "");

    /// <summary>Case- and separator-insensitive key for matching a part to its artwork.</summary>
    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>One subcircuit a netlist declares: its name, terminals and declared parameters.</summary>
    private sealed record SubcircuitDef(string Name, int PinCount, IReadOnlyList<PdkPartParameter> Parameters);

    // define NAME ( t1 t2 … )   — the terminal list may run across several lines.
    private static readonly Regex RxDefine = new(
        @"^[ \t]*define[ \t]+([A-Za-z_]\w*)[ \t]*(\(([^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // parameters a=1 b="x" …    — continued onto the next line by a trailing backslash.
    private static readonly Regex RxParamAssign = new(
        @"([A-Za-z_]\w*)\s*=\s*(""[^""]*""|\S+)", RegexOptions.Compiled);

    private static IEnumerable<SubcircuitDef> SubcircuitsIn(string root, string relativePath)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string text;
        try
        {
            if (!File.Exists(full) || new FileInfo(full).Length > PeekLimitBytes * 8) yield break;
            text = File.ReadAllText(full);
        }
        catch { yield break; }

        foreach (Match m in RxDefine.Matches(text))
        {
            string name  = m.Groups[1].Value;
            int    pins  = m.Groups[3].Success
                ? m.Groups[3].Value.Split((char[])[' ', '\t', '\r', '\n', ','],
                                          StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

            var declared = ParametersAfter(text, m.Index + m.Length);
            yield return new SubcircuitDef(name, pins, ResolveSentinels(text, m.Index + m.Length, declared));
        }
    }

    /// <summary>
    /// Reads the <c>parameters</c> declaration belonging to the subcircuit that starts at
    /// <paramref name="from"/>, stopping at the next <c>define</c> so one subcircuit can never
    /// inherit the next one's parameters. Backslash line-continuation is honoured.
    /// </summary>
    private static IReadOnlyList<PdkPartParameter> ParametersAfter(string text, int from)
    {
        var result = new List<PdkPartParameter>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int pos = from;
        bool continuing = false;

        while (pos < text.Length)
        {
            int nl   = text.IndexOf('\n', pos);
            string ln = (nl < 0 ? text[pos..] : text[pos..nl]).Trim();
            pos = nl < 0 ? text.Length : nl + 1;

            if (ln.Length == 0) { continuing = false; continue; }
            if (ln.StartsWith(';') || ln.StartsWith('#')) continue;

            if (ln.StartsWith("define", StringComparison.OrdinalIgnoreCase)) break;

            bool isParamLine = continuing ||
                               ln.StartsWith("parameters", StringComparison.OrdinalIgnoreCase) ||
                               ln.StartsWith("parameter",  StringComparison.OrdinalIgnoreCase);
            if (!isParamLine) { continuing = false; continue; }

            continuing = ln.EndsWith('\\');

            foreach (Match pm in RxParamAssign.Matches(ln))
            {
                string pname = pm.Groups[1].Value;
                if (pname.Equals("parameters", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(pname)) continue;
                string raw = pm.Groups[2].Value;
                result.Add(new PdkPartParameter(pname, raw.Trim('"', '\\'),
                                                IsText: raw.StartsWith('"')));
            }

            if (!continuing) break;
        }

        return result;
    }


    // NAME = if(PARAM == SENTINEL) then (EXPR) else (…) endif
    private static readonly Regex RxSentinel = new(
        @"^[ \t]*\w+\s*=\s*if\s*\(\s*(\w+)\s*==\s*([^)\s]+)\s*\)\s*then\s*(.+?)\s*else\s*.+?\s*endif",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Replaces a parameter's SENTINEL default with the value the kit itself computes for it.
    ///
    /// <para>Kits declare a thermal parameter as <c>RTH=-1</c> and resolve it inside the subcircuit:
    /// <c>if(RTH==-1) then (1.0e-6) else (RTH) endif</c>. That resolution lives in the netlist
    /// WRAPPER, which circuitRF does not execute — it hands the part straight to a device provider.
    /// A sentinel left verbatim would therefore reach the model raw, and a thermal resistance of −1
    /// is not a default, it is nonsense.</para>
    ///
    /// <para>The replacement is computed from the kit's OWN expression, never invented here, and
    /// only where the declared default matches the sentinel that expression tests for. Anything that
    /// cannot be read or evaluated is left exactly as the kit wrote it.</para>
    /// </summary>
    private static IReadOnlyList<PdkPartParameter> ResolveSentinels(
        string text, int from, IReadOnlyList<PdkPartParameter> declared)
    {
        if (declared.Count == 0) return declared;

        // Only this subcircuit's own body, so one subcircuit's expressions can never resolve
        // another's parameters.
        var nextDefine = RxDefine.Match(text, from);
        string body = text[from..(nextDefine.Success ? nextDefine.Index : text.Length)];

        var resolved = new Dictionary<string, (string Sentinel, double Value)>(StringComparer.OrdinalIgnoreCase);
        Collect(body);

        // A kit often ships several variants of one part that share an interface, with only some of
        // them spelling out how the sentinel resolves. Falling back to the whole netlist finds the
        // kit's own answer instead of leaving a raw sentinel to reach the model. Where two
        // subcircuits resolve the same name differently the first wins — they are variants of one
        // part, so a genuine disagreement here would be the kit contradicting itself.
        if (declared.Any(d => !d.IsText && !resolved.ContainsKey(d.Name)))
            Collect(text);

        void Collect(string scope)
        {
            foreach (Match sm in RxSentinel.Matches(scope))
            {
                string param = sm.Groups[1].Value;
                if (resolved.ContainsKey(param)) continue;
                if (!TryEvaluateNumber(sm.Groups[3].Value.Trim(), out double v)) continue;
                resolved[param] = (sm.Groups[2].Value.Trim(), v);
            }
        }

        var outp = new List<PdkPartParameter>(declared.Count);
        foreach (var p in declared)
        {
            if (!p.IsText &&
                resolved.TryGetValue(p.Name, out var hit) &&
                SameNumber(p.DefaultExpression, hit.Sentinel))
            {
                outp.Add(p with { DefaultExpression = Format(hit.Value) });
            }
            else outp.Add(p);
        }

        return outp;
    }

    private static bool SameNumber(string a, string b) =>
        double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
        double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
        Math.Abs(x - y) < 1e-12;

    private static string Format(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Evaluates a plain arithmetic expression — parentheses and <c>+ - * /</c> over numeric
    /// literals, which is all a kit's default expressions are. Returns false for anything else (a
    /// reference to another parameter, a function call), so an expression this cannot fully
    /// understand leaves the kit's own text untouched rather than producing a wrong number.
    /// </summary>
    public static bool TryEvaluateNumber(string expr, out double value)
    {
        value = 0;
        int i = 0;
        try
        {
            double v = ParseSum(expr, ref i);
            SkipWs(expr, ref i);
            if (i != expr.Length || !double.IsFinite(v)) return false;
            value = v;
            return true;
        }
        catch { return false; }
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static double ParseSum(string s, ref int i)
    {
        double v = ParseProduct(s, ref i);
        while (true)
        {
            SkipWs(s, ref i);
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                char op = s[i++];
                double r = ParseProduct(s, ref i);
                v = op == '+' ? v + r : v - r;
            }
            else return v;
        }
    }

    private static double ParseProduct(string s, ref int i)
    {
        double v = ParseAtom(s, ref i);
        while (true)
        {
            SkipWs(s, ref i);
            if (i < s.Length && (s[i] == '*' || s[i] == '/'))
            {
                char op = s[i++];
                double r = ParseAtom(s, ref i);
                v = op == '*' ? v * r : v / r;
            }
            else return v;
        }
    }

    private static double ParseAtom(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw new FormatException("unexpected end");

        if (s[i] == '(')
        {
            i++;
            double v = ParseSum(s, ref i);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ')') throw new FormatException("unbalanced");
            i++;
            return v;
        }

        if (s[i] == '-') { i++; return -ParseAtom(s, ref i); }
        if (s[i] == '+') { i++; return  ParseAtom(s, ref i); }

        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                ((s[i] == '+' || s[i] == '-') && i > start &&
                                 (s[i - 1] == 'e' || s[i - 1] == 'E'))))
            i++;

        if (i == start) throw new FormatException("not a number");
        return double.Parse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture);
    }
    /// <summary>
    /// Trailing name tokens that mark a name as a DRAWING of something rather than a thing:
    /// a cell called <c>WIDGET_SYM</c> is where the picture of <c>WIDGET</c> lives, not a part.
    /// </summary>
    private static readonly string[] DrawingNameTokens = ["sym", "symbol", "icon", "art"];

    /// <summary>Splits a name into lower-cased word tokens, dropping separators and empties.</summary>
    internal static List<string> Tokens(string name)
    {
        var outp = new List<string>();
        var sb   = new StringBuilder();

        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0) { outp.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) outp.Add(sb.ToString());
        return outp;
    }

    /// <summary>True when the last token marks this name as a drawing rather than a component.</summary>
    internal static bool LooksLikeADrawingName(string name)
    {
        var t = Tokens(name);
        return t.Count > 1 && DrawingNameTokens.Contains(t[^1], StringComparer.Ordinal);
    }

    /// <summary>
    /// Finds the artwork that belongs to a part.
    ///
    /// <para>A drawing is rarely named exactly like the component it depicts. A kit names the
    /// component for the part plus a revision tail, and the drawing for the part alone
    /// (<c>WIDGET_Rev0_MODEL</c> ↔ <c>WIDGET_SYM</c>), or names a shared drawing after the FUNCTION
    /// it depicts rather than the part (<c>PART_A_TECH_INCLUDE</c> ↔ <c>TECH_INCLUDE_SYM</c>). A
    /// prefix rule catches the first shape and misses the second.</para>
    ///
    /// <para>So the rule is token containment: every word in the drawing's name (minus a trailing
    /// <c>_SYM</c>-style token) must also appear in the part's name. That catches both shapes, while
    /// still refusing two unrelated names that merely share a family word — <c>TECH_INCLUDE_SYM</c>
    /// does not match <c>TECH_FET_ROOT</c>, because <c>include</c> is absent from it.</para>
    /// </summary>
    private static PdkAsset? FindArtworkFor(string partName,
                                            List<(string Key, PdkAsset Asset)> artwork,
                                            PdkAssetKind kind)
    {
        var target = Tokens(partName);
        if (target.Count == 0) return null;
        var targetSet = new HashSet<string>(target, StringComparer.Ordinal);

        PdkAsset? best = null;
        (int Readable, int Matched) bestRank = (-1, -1);

        foreach (var (key, asset) in artwork)
        {
            if (asset.Kind != kind) continue;

            var drawing = StripDrawingTokens(Tokens(key));
            if (!drawing.All(targetSet.Contains)) continue;

            // Two shared words are needed in general, but a single-word name is common and carries
            // real information when the word is distinctive — a short one like a kit's own prefix
            // does not.
            // …or when the whole part name IS that one word, which is as exact as a match gets.
            if (drawing.Count < MinMatchTokens &&
                !(drawing.Count == 1 &&
                  (drawing[0].Length >= MinSingleTokenLength ||
                   (target.Count == 1 && target[0] == drawing[0])))) continue;

            // A drawing circuitRF can READ beats a more specifically-named one it cannot: a kit
            // commonly ships the same symbol twice — once as text, once in a binary cell database —
            // and the binary copy often carries the more precise name. Preferring specificity alone
            // attaches the unusable copy and leaves the part unplaceable, which defeats the point.
            var rank = (Readable: asset.Support == PdkAssetSupport.Supported ? 1 : 0,
                        Matched:  drawing.Count);
            if (rank.Readable > bestRank.Readable ||
                (rank.Readable == bestRank.Readable && rank.Matched > bestRank.Matched))
            {
                bestRank = rank;
                best     = asset;
            }
        }

        return best;
    }

    /// <summary>One shared word says nothing about two names — refuse rather than guess.</summary>
    private const int MinMatchTokens = 2;

    /// <summary>A one-word name must be at least this long to be evidence of anything.</summary>
    private const int MinSingleTokenLength = 4;

    private static List<string> StripDrawingTokens(List<string> tokens)
    {
        var t = new List<string>(tokens);
        while (t.Count > 1 && DrawingNameTokens.Contains(t[^1], StringComparer.Ordinal))
            t.RemoveAt(t.Count - 1);
        return t;
    }

    // ── outcome ───────────────────────────────────────────────────────────────

    private static void Summarize(PdkImportReport report)
    {
        int supported = report.Supported.Count();
        int gaps      = report.KnownGaps.Count();

        report.LayerTechnology = report.Assets
            .FirstOrDefault(a => a.Kind == PdkAssetKind.LayerTechnology &&
                                 a.Support == PdkAssetSupport.Supported);

        report.Status = report.Parts.Count > 0 || supported > 0
            ? (gaps > 0 || report.Unrecognized.Any() ? PdkImportStatus.PartiallyImported
                                                     : PdkImportStatus.Imported)
            : PdkImportStatus.NotRecognized;

        if (report.Status == PdkImportStatus.NotRecognized)
            report.Blocker(
                "circuitRF did not recognise anything it can use in this kit.",
                "The formats found are listed above. If one of them is a format circuitRF should " +
                "read, that is a feature request; if the kit needs a device provider, register one " +
                "and import again.");

        if (report.Parts.Count > 0 && !report.Assets.Any(a => a.Kind == PdkAssetKind.ModelData &&
                                                             a.Support == PdkAssetSupport.Supported))
        {
            var data = report.Assets.Count(a => a.Kind == PdkAssetKind.ModelData);
            if (data > 0)
                report.Warn(
                    $"{data} model-data file(s) found, which circuitRF does not read itself.",
                    "Register a device provider that declares this kit's device types; the parts " +
                    "are placeable but will not simulate until one is available.");
        }

        // Artwork is reported whether or not it can be read, so a kit never loses it silently.
        int sym = report.Assets.Count(a => a.Kind == PdkAssetKind.SymbolArtwork);
        int lay = report.Assets.Count(a => a.Kind == PdkAssetKind.LayoutArtwork);
        int ico = report.Assets.Count(a => a.Kind == PdkAssetKind.PaletteIcon);

        if (ico > 0)
            report.Info($"{ico} palette icon(s) found and will be used in the component library.");

        if (sym > 0 && !report.Assets.Any(a => a.Kind == PdkAssetKind.SymbolArtwork &&
                                               a.Support == PdkAssetSupport.Supported))
            report.Warn(
                $"{sym} symbol drawing(s) found, in a format circuitRF cannot read yet.",
                "Parts will be placed with a generated symbol derived from their pin list. The " +
                "drawings are recorded against each part, so they can be used once a reader exists.");

        if (lay > 0 && !report.Assets.Any(a => a.Kind == PdkAssetKind.LayoutArtwork &&
                                               a.Support == PdkAssetSupport.Supported))
            report.Warn(
                $"{lay} layout drawing(s) found, in a format circuitRF cannot read yet.",
                "Export them as GDSII from the tool that wrote them if you need the geometry.");

        if (report.LayerTechnology is not null)
            report.Info(
                $"Layer technology found ({report.LayerTechnology.FileName}).",
                "It can be imported as a Layout Editor technology — layer names, stream numbers " +
                "and display style — even though this kit ships no geometry.");
        else if (lay > 0)
            report.Warn("Layout drawings were found but no layer technology accompanied them.",
                        "Without layer definitions the geometry could not be displayed correctly.");
    }

    private static PdkImportReport Failed(string path, string why, string action = "")
    {
        var r = new PdkImportReport
        {
            RootPath = path,
            KitName  = string.IsNullOrEmpty(path) ? "Kit" : Path.GetFileName(path.TrimEnd('/', '\\')),
            Status   = PdkImportStatus.Failed,
        };
        r.Blocker(why, action);
        return r;
    }

    private static string DescribeUnknown(string relativePath)
    {
        string name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 ? $"unknown ({name[dot..].ToLowerInvariant()})" : "unknown (no extension)";
    }

    private static string PeekText(Func<Stream> open)
    {
        try
        {
            using var s = open();
            var buf = new byte[PeekChars];
            int n = s.Read(buf, 0, buf.Length);
            if (n <= 0) return "";
            for (int i = 0; i < n; i++) if (buf[i] == 0) return "";   // binary
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        catch { return ""; }
    }
}
