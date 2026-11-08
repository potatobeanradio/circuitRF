using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;

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
    /// <summary>
    /// The size discovery reads a netlist whole up to, as a multiple of eight. It no longer decides
    /// whether a file is OPENED for classification — see <see cref="PeekChars"/>.
    /// </summary>
    private const long PeekLimitBytes = 512 * 1024;
    /// <summary>
    /// How much of a candidate file a recogniser gets to look at.
    ///
    /// <para><b>Raised from 4,096 after it misclassified real files by eighteen bytes.</b> Two of a
    /// kit's corner files declare their first <c>.lib</c> at byte 4,114 and 4,184 — behind a
    /// license header and a long parameter block — so a 4,096-byte window ended just short of the one
    /// marker that identifies them, and the corners they declare were invisible to the import. The
    /// same shape of mistake as reading a network's port labels off a capped read: a format's marker
    /// is wherever the file puts it, and a cap chosen for cost quietly becomes a correctness rule.</para>
    ///
    /// <para><b>Every file gets this window, whatever its size.</b> A file over
    /// <see cref="PeekLimitBytes"/> used to be classified by name only and never opened — and a kit's
    /// one model library, every corner it offers in a single 755 KB file, landed in the import as
    /// unrecognised, so no part of that kit got a model. The same cap-becomes-a-rule mistake as the
    /// window above, one level up. What bounds the cost is this window, never the file: the head is
    /// read in chunks and the read stops at the first zero byte, so a large binary file — a layout
    /// stream, an archive — costs one chunk.</para>
    /// </summary>
    private const int  PeekChars      = 64 * 1024;

    /// <summary>How much of the window is read before checking it for a zero byte.</summary>
    private const int  PeekChunk      = 4 * 1024;

    public static PdkImportReport Import(string path) => Import(path, kitName: null);

    /// <summary>
    /// <see cref="Import(string)"/>, with the kit called <paramref name="kitName"/> rather than what its
    /// folder or manifest says — the name every part reference is built from. Null keeps the kit's own.
    /// </summary>
    public static PdkImportReport Import(string path, string? kitName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failed(path ?? "", "No path was given.", "Choose the kit's folder or .zip file.");

        try
        {
            if (Directory.Exists(path))
            {
                // What was imported may be a small folder that ADDS to a kit rather than being one:
                // it names the kit it belongs to, and both are read as one. See PdkImportReport.KitRoot.
                var additions = DeclaredKitRoot(path);
                var entries = additions is null
                    ? EnumerateFolder(path)
                    : EnumerateFolder(path).Concat(EnumerateFolder(additions.Value.Root));

                // An additions folder may be called anything — it is not the kit. The kit's own name
                // comes from what the manifest declares, or from the kit's folder; getting it wrong
                // would leave every step working except the one that asks for the provider by name.
                string name = kitName is { Length: > 0 } given ? given
                    : additions is null
                    ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : additions.Value.Provider is { Length: > 0 } declared
                        ? declared
                        : Path.GetFileName(additions.Value.Root.TrimEnd(Path.DirectorySeparatorChar,
                                                                       Path.AltDirectorySeparatorChar));

                return ImportEntries(path, name, entries, additions?.Root);
            }

            if (File.Exists(path) && Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                using (var zip = ZipFile.OpenRead(path))
                    return ImportEntries(path, kitName is { Length: > 0 } given ? given
                                                   : Path.GetFileNameWithoutExtension(path), EnumerateZip(zip));

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

    /// <summary>
    /// A version-control tool's own bookkeeping, never a kit's content. A kit is very often a CLONE, and
    /// walking into its history folder listed hooks, packs and indexes as kit files in the report — one
    /// clone of an open kit added 50 "unrecognised" files, and peeked a pack file the size of the kit.
    /// </summary>
    private static readonly HashSet<string> VersionControlDirs =
        new([".git", ".svn", ".hg"], StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<Entry> EnumerateFolder(string root)
    {
        // Walked one folder at a time, in the order the recursive enumeration it replaces used (a folder's
        // files, then its subfolders queued), and with the same handling of an unreadable folder.
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 };
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            string dir = pending.Dequeue();
            foreach (var f in Directory.EnumerateFiles(dir, "*", options))
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                yield return new(rel, len, () => File.OpenRead(f));
            }
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                if (!VersionControlDirs.Contains(Path.GetFileName(sub))) pending.Enqueue(sub);
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

    /// <summary>
    /// The kit an "additions" folder names, or null. Read straight from the manifest's own
    /// <c>baseDirectory</c> — the same field that already means "the kit's own folder" everywhere
    /// else — so nothing new has to be declared to get this.
    /// </summary>
    private static (string Root, string? Provider)? DeclaredKitRoot(string path)
    {
        try
        {
            string manifestPath = Path.Combine(path, DeviceWorkerManifest.FileName);
            if (!File.Exists(manifestPath)) return null;

            var manifest = DeviceWorkerManifest.TryRead(manifestPath, out _);
            string? baseDir = manifest?.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) return null;

            // A manifest pointing at its own folder adds nothing and must not be enumerated twice.
            return Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar)
                       .Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase)
                ? null
                : (baseDir, manifest!.ProviderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static PdkImportReport ImportEntries(
        string root, string kitName, IEnumerable<Entry> entries, string? kitRoot = null)
    {
        var report = new PdkImportReport
        {
            RootPath = root,
            KitRoot  = kitRoot,
            KitName  = string.IsNullOrEmpty(kitName) ? "Kit" : kitName,
        };
        var recognizers = PdkFormatRegistry.All;

        var files = new List<Entry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            if (seenPaths.Add(e.RelativePath)) files.Add(e);   // additions folder listed first, so it wins
        if (files.Count == 0)
        {
            report.Status = PdkImportStatus.Failed;
            report.Blocker("The kit is empty.", "Check that the folder or archive is the kit root.");
            return report;
        }

        foreach (var e in files)
        {
            // Memoized, and that is a cost fix rather than a tidy-up: every recogniser in the
            // registry is offered the same file in turn, each one calls Peek, and an un-memoized
            // closure re-OPENS and re-READS the file for every call. Measured on a real kit inside
            // the application: 4,805 reads and ~83 MB for 1,266 files, where one read each — ~21 MB —
            // is the whole of what is being asked for. The recognisers themselves are unchanged;
            // nothing here decides which of them wins.
            string? peeked = null;
            string Peek() => peeked ??= PeekText(e.Open);

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
        DiscoverCorners(report);
        DiscoverVerilogASources(report);
        Summarize(report);
        return report;
    }

    private static readonly Regex RxVaModule = new(
        @"\b(?:macro)?module\s+([A-Za-z_][A-Za-z0-9_$]*)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Which modules the kit's Verilog-A sources declare (<see cref="PdkImportReport.VerilogASources"/>).
    /// Read, never compiled. The first file declaring a module keeps it, by asset order, so the answer is
    /// the same on every machine; a file that cannot be read declares nothing.
    /// </summary>
    private static void DiscoverVerilogASources(PdkImportReport report)
    {
        foreach (var va in report.Assets.Where(a => a.FormatName == ExtensionRecognizer.VerilogASourceFormat))
        {
            if (ResolveAssetPath(report.RootPath, report.KitRoot, va.RelativePath) is not { } full) continue;
            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (Match m in RxVaModule.Matches(VerilogASourceCompiler.StripComments(text)))
                report.VerilogASources.TryAdd(m.Groups[1].Value, va.RelativePath);
        }
    }

    /// <summary>
    /// Learns which corner choices the kit offers, from the netlists it already recognised.
    ///
    /// <para>Recorded against each file's own KIT-RELATIVE path, never an absolute one: a design's
    /// recorded corner has to survive the kit being moved, re-cloned, or referenced from a different
    /// machine, and the only stable identity a corner file has is where it sits inside its kit.</para>
    /// </summary>
    private static void DiscoverCorners(PdkImportReport report)
    {
        var candidates = new List<(string AbsolutePath, string AxisId)>();
        foreach (var net in report.Assets.Where(a => a.Kind == PdkAssetKind.Netlist))
        {
            string? full = ResolveAssetPath(report.RootPath, report.KitRoot, net.RelativePath);
            if (full is not null) candidates.Add((full, net.RelativePath));
        }

        if (candidates.Count == 0) return;

        IReadOnlyList<PdkCornerAxis> axes;
        try { axes = PdkCorners.Discover(candidates); }
        catch { return; }                 // a kit that will not yield corners still imports

        report.CornerAxes.AddRange(RestrictToUsedFlavours(axes, report));
    }

    /// <summary>
    /// Keeps only the corner files that sit beside a subcircuit some part is actually built from.
    ///
    /// <para><b>Which "flavour" of a corner applies is DERIVED, never offered as a choice.</b> A kit
    /// that supports several simulators files a complete set of corner files per simulator — one real
    /// one ships twelve for six device families — and they are not interchangeable: measured on that
    /// kit, both MOS families differ between flavours by a parameter NAME, so the wrong one binds a
    /// name the model never reads. The user cannot be expected to answer that, and should not be
    /// asked to: the corner that applies is the one in the same directory as the subcircuit circuitRF
    /// resolved the part to. Anything else is a corner for a simulator that is not running.</para>
    ///
    /// <para>A kit whose parts are all compiled models defines no subcircuits, so there is nothing to
    /// derive from — every axis is kept rather than silently dropping the lot.</para>
    /// </summary>
    private static IReadOnlyList<PdkCornerAxis> RestrictToUsedFlavours(
        IReadOnlyList<PdkCornerAxis> axes, PdkImportReport report)
    {
        var usedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in report.Parts)
            if (part.DefinitionRelativePath is { Length: > 0 } rel)
                usedDirs.Add(DirectoryOf(rel));

        if (usedDirs.Count == 0) return axes;

        var kept = axes.Where(a => usedDirs.Contains(DirectoryOf(a.AxisId))).ToList();

        // A kit that files its corners apart from its subcircuits would otherwise lose all of them,
        // which is a worse answer than showing more than are strictly relevant.
        return kept.Count > 0 ? kept : axes;
    }

    private static string DirectoryOf(string relativePath)
    {
        int slash = relativePath.LastIndexOf('/');
        return slash <= 0 ? "" : relativePath[..slash];
    }

    /// <summary>
    /// Where an asset's kit-relative path actually lands on disk. An imported "additions" folder is
    /// read together with the kit it names, so a relative path belongs to one root or the other.
    /// </summary>
    private static string? ResolveAssetPath(string root, string? kitRoot, string relativePath)
    {
        string rel  = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.Combine(root, rel);
        if (File.Exists(full)) return full;

        if (kitRoot is { Length: > 0 })
        {
            full = Path.Combine(kitRoot, rel);
            if (File.Exists(full)) return full;
        }

        return null;
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
            foreach (var sub in SubcircuitsIn(report.RootPath, report.KitRoot, net.RelativePath))
                subcircuits.Add(sub);

        // A kit's test, regression and LVS folders hold netlists it checks ITSELF against — copies and
        // fragments that merely share names with its devices. They never define a part, by any
        // pairing (brief-gdf-1b: measured, every one of one kit's name-likeness pairings landed in a
        // regression netlist, the failure that had name pairing reverted once before).
        subcircuits = PreferTheKitsMainLibrary(
            [.. subcircuits.Where(s => !IsCheckingFolder(s.SourceRelativePath))], report);

        // ── The parts a CATALOG declares ─────────────────────────────────────
        //
        // Read first, and it wins, because it is the kit SAYING what it offers rather than circuitRF
        // inferring it from what files happen to be lying around. A kit with a catalog may have no
        // netlist and no cell tree at all — its parts' behaviour coming from a compiled model
        // library — and without this such a kit yields an empty palette.
        // Symbol libraries are read once, up front. A handful of templates can serve a whole kit —
        // measured at 7 for 109 parts — so the same template is handed to every part that names it,
        // and the palette shows exactly what the schematic will.
        var templates = new Dictionary<string, KitSymbolTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in report.Assets.Where(a => a.Kind == PdkAssetKind.SymbolLibrary))
            foreach (var t in ReadSymbolLibrary(report.RootPath, report.KitRoot, lib.RelativePath))
            {
                templates.TryAdd(t.Name, t);      // first wins; a later duplicate is not a better one
                // Also under a separator-insensitive key. A kit's CATALOG and its own symbol LIBRARY
                // can spell the same symbol differently — one kit references `A_B` for a symbol
                // it declares as `A B`, which silently cost every part of that family its pins. This
                // is the same insensitivity `Normalize` already applies to matching artwork.
                templates.TryAdd(Normalize(t.Name), t);
            }

        foreach (var cat in report.Assets.Where(a => a.Kind == PdkAssetKind.ComponentCatalog))
            foreach (var entry in CatalogEntriesIn(report.RootPath, report.KitRoot, cat.RelativePath))
            {
                if (!seen.Add(entry.Id)) continue;

                // A symbol reference is "<name>@<library>". Only the name is looked up: the library
                // part is a path relative to wherever the kit put its catalog, and the templates are
                // already indexed by the name a part actually asks for.
                var sub  = BestSubcircuitFor(entry.Id, subcircuits);
                string want = entry.Symbol.Split('@')[0].Trim();
                if (!templates.TryGetValue(want, out var tpl))
                    templates.TryGetValue(Normalize(want), out tpl);

                report.Parts.Add(new PdkPart(
                    Id: entry.Id, DisplayName: entry.Id, Category: entry.Category,
                    IconRelativePath: FindArtworkFor(entry.Id, artwork, PdkAssetKind.PaletteIcon)?.RelativePath,
                    SymbolArtwork:    FindArtworkFor(entry.Id, artwork, PdkAssetKind.SymbolArtwork),
                    LayoutArtwork:    FindArtworkFor(entry.Id, artwork, PdkAssetKind.LayoutArtwork),
                    Parameters:       sub?.Parameters,
                    // The symbol is the better authority on terminals than a name-matched subcircuit:
                    // the kit DREW it, and a part that names one is telling us how many pins it has.
                    PinCount:         tpl?.Pins.Count ?? sub?.PinCount ?? 0,
                    Pins:             tpl?.Pins,
                    DefinitionRelativePath: sub?.SourceRelativePath,
                    DefinitionCell:         sub?.Name));
            }

        if (report.Parts.Count > 0) return;

        // ── The parts: one readable SYMBOL FILE each ─────────────────────────
        //
        // A kit of this shape has no catalog and no cell-database tree: its symbols sit together in
        // a folder, one file per part, and the behaviour behind them lives somewhere else entirely —
        // a netlist, or a compiled model library, in a different folder with no naming relationship
        // to the symbols at all. Nothing above finds such a kit, and it imports as a pile of
        // recognised files with an empty palette.
        //
        // Tried before the cell-database shape because the evidence is stronger: circuitRF actually
        // READ the terminals out of these files, rather than inferring a part from the shape of a
        // directory path.
        Dictionary<string, List<string>>? cardFiles = null;
        foreach (var a in report.Assets.Where(IsRecordSymbolFile))
        {
            var symbol = ReadSymbolFile(report.RootPath, report.KitRoot, a.RelativePath);
            if (symbol is null || symbol.Pins.Count == 0) continue;

            string id = Path.GetFileNameWithoutExtension(a.FileName);
            if (id.Length == 0 || !seen.Add(id)) continue;

            // A symbol whose own netlist line is a PRIMITIVE element on a card states what it calls,
            // so it is never handed a subcircuit by name likeness: it is pointed at the file holding
            // that card (brief-gdf-1b §4), and extraction builds the element from it.
            bool primitive = symbol.Element is "D" or "Q" or "R" or "C" or "M" && symbol.CalledName is not null;
            bool stated    = symbol.Element == "X" && symbol.CalledName is not null;
            var sub = primitive ? null
                    : stated    ? StatedSubcircuitFor(symbol.CalledName!, symbol.Pins.Count,
                                                      symbol.ExtraTerminals?.Count ?? 0,
                                                      subcircuits, report)
                    : BestSubcircuitFor(id, subcircuits);
            string? cardFile = null;
            if (primitive)
            {
                cardFiles ??= CardFiles(report);
                cardFile = CardFileFor(symbol.CalledName!, cardFiles, subcircuits, report);
            }

            report.Parts.Add(new PdkPart(
                Id: id, DisplayName: id,
                // The kit's own word for what kind of device this is, so the palette groups the way
                // the kit does rather than the way circuitRF would guess.
                Category: symbol.TypeWord.Length > 0 ? symbol.TypeWord : "symbols",
                IconRelativePath: FindArtworkFor(id, artwork, PdkAssetKind.PaletteIcon)?.RelativePath,
                // Deliberately NOT pointed at the file it came from. The terminals are already read
                // and attached below; handing the path to the installer would send a DIFFERENT
                // record-based text reader at it, and two such formats quietly reading each other's
                // files produces a symbol that is drawn, placeable and wrong.
                SymbolArtwork: null,
                LayoutArtwork: FindArtworkFor(id, artwork, PdkAssetKind.LayoutArtwork),
                // The symbol's own template is the kit stating this part's interface WITH its
                // defaults, so it outranks a subcircuit matched by name. The subcircuit stands in
                // when the symbol declares no template.
                Parameters: symbol.Parameters.Count > 0 ? symbol.Parameters : sub?.Parameters,
                PinCount:   symbol.Pins.Count,
                Pins:       symbol.Pins,
                DefinitionRelativePath: primitive ? cardFile : sub?.SourceRelativePath,
                DefinitionCell:         primitive ? (cardFile is null ? null : symbol.CalledName) : sub?.Name,
                // The kit's own drawing. Non-null even when empty — see PdkPart.Body for why the
                // distinction between "drew nothing" and "has no drawing" is load-bearing: the two
                // sources place their pins under different axis conventions.
                Body:       symbol.Body,
                ExtraTerminals: symbol.ExtraTerminals is { Count: > 0 } extra ? extra : null,
                Element:          symbol.Element,
                CalledName:       symbol.CalledName,
                ElementArguments: symbol.ElementArguments));
        }

        if (report.Parts.Count > 0) return;

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
                PinCount:         match?.PinCount ?? 0,
                DefinitionRelativePath: match?.SourceRelativePath,
                DefinitionCell:         match?.Name));
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
                PinCount:         sub.PinCount,
                DefinitionRelativePath: sub.SourceRelativePath,
                DefinitionCell:         sub.Name));
        }
    }

    /// <summary>
    /// Whether a netlist is written in the SPICE dialect. Keyed on the one construct that declares a
    /// subcircuit there, at the start of a line — a mention of the word inside a comment or a path
    /// is not a declaration.
    /// </summary>
    private static bool LooksLikeSpiceNetlist(string text)
        => RxSpiceSubckt.IsMatch(text);

    private static readonly Regex RxSpiceSubckt =
        new(@"^\s*\.subckt\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Whether a default reads as a number, which is what decides the parameter editor's shape.</summary>
    private static bool IsNumericText(string s)
        => s.Length > 0 && double.TryParse(s, System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Orders the kit's subcircuits so its MAIN library comes first, because the matcher below keeps
    /// the first of equally-good candidates.
    ///
    /// <para><b>Why this is needed at all.</b> A kit that supports several simulators defines the same
    /// subcircuit once per simulator, and beside each main library sits a handful of small variant
    /// files. Left in directory-walk order, one part binds to one flavour and the next part to
    /// another — and a part can land on a variant file rather than the library it belongs to. Every
    /// one of those still reads, still places, and quietly pairs the part with process constants from
    /// a corner set that is not the one being applied.</para>
    ///
    /// <para><b>The order is derived from the kit, not from any name.</b> The directory defining the
    /// most subcircuits is that kit's main library tree; within a directory, the file defining the
    /// most is the main library rather than a variant of it. Both are facts about how the kit is
    /// built, so this carries no knowledge of any supplier's folder or file naming.</para>
    /// </summary>
    private static List<SubcircuitDef> PreferTheKitsMainLibrary(
        List<SubcircuitDef> subcircuits, PdkImportReport report)
    {
        if (subcircuits.Count < 2) return subcircuits;

        var canonical = CanonicalLibraries(report);

        var perDir  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in subcircuits)
        {
            string dir = DirectoryOf(sub.SourceRelativePath);
            perDir[dir]  = perDir.TryGetValue(dir, out var d) ? d + 1 : 1;
            perFile[sub.SourceRelativePath] =
                perFile.TryGetValue(sub.SourceRelativePath, out var f) ? f + 1 : 1;
        }

        // OrderBy is stable, so anything the counts do not separate keeps the order it was found in.
        return [.. subcircuits
            .OrderByDescending(x => canonical.Contains(FileNameOf(x.SourceRelativePath)))
            .ThenByDescending(x => perDir[DirectoryOf(x.SourceRelativePath)])
            .ThenByDescending(x => perFile[x.SourceRelativePath])];
    }

    private static readonly Regex RxModelCard =
        new(@"^[ \t]*\.model[ \t]+(\S+)[ \t]+\S", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every <c>.model</c> card name the kit's netlists declare, and the files declaring it — outside
    /// test, regression and LVS folders, which hold copies a kit checks itself against rather than the
    /// models it ships.
    /// </summary>
    private static Dictionary<string, List<string>> CardFiles(PdkImportReport report)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var net in report.Assets.Where(a => a.Kind == PdkAssetKind.Netlist))
        {
            if (IsCheckingFolder(net.RelativePath)) continue;
            string? full = ResolveAssetPath(report.RootPath, report.KitRoot, net.RelativePath);
            if (full is null) continue;

            string text;
            try
            {
                if (new FileInfo(full).Length > PeekLimitBytes * 8) continue;
                text = File.ReadAllText(full);
            }
            catch { continue; }

            foreach (Match m in RxModelCard.Matches(text))
            {
                if (!result.TryGetValue(m.Groups[1].Value, out var files)) result[m.Groups[1].Value] = files = [];
                if (!files.Contains(net.RelativePath, StringComparer.OrdinalIgnoreCase)) files.Add(net.RelativePath);
            }
        }
        return result;
    }

    /// <summary>
    /// The one file a card is declared in — the kit's canonical library when several declare it, then
    /// the one the kit's NOMINAL corner reads (the tie-break <see cref="StatedSubcircuitFor"/> makes, for
    /// a kit stating one card file per corner), then the one on the kit's main library tree when the
    /// others are copies for another simulator (<see cref="OnTheMainTree"/>), and nothing when that
    /// still leaves more than one, because picking between two definitions of one name is the guess
    /// the kit did not make.
    /// </summary>
    private static string? CardFileFor(string card, Dictionary<string, List<string>> cardFiles,
                                       List<SubcircuitDef> subcircuits, PdkImportReport report)
    {
        if (!cardFiles.TryGetValue(card, out var files) || files.Count == 0) return null;
        if (files.Count == 1) return files[0];

        var canonical = CanonicalLibraries(report);
        var preferred = files.Where(f => canonical.Contains(FileNameOf(f))).ToList();
        if (preferred.Count == 1) return preferred[0];

        var reached = NominalCornerFiles(report);
        var nominal = files
            .Where(f => ResolveAssetPath(report.RootPath, report.KitRoot, f) is { } full
                        && reached.Contains(Path.GetFullPath(full)))
            .ToList();
        if (nominal.Count == 1) return nominal[0];
        return OnTheMainTree(nominal.Count > 1 ? nominal : preferred.Count > 1 ? preferred : files, subcircuits);
    }

    /// <summary>
    /// The one of several equally good definitions that lies on the kit's MAIN library tree, or null.
    ///
    /// <para><b>The shape this resolves.</b> A kit supporting several simulators ships the same library
    /// once per simulator, each copy in a directory of its own beside its own corner files. The copies
    /// share a file name, so the canonical-library test keeps them all, and each is its own tree's
    /// nominal, so the nominal-corner test does too. Every part the kit defines in more than one tree
    /// was left unpaired by those two alone — measured on a real kit, every device but the three it
    /// defines in one tree only. The copies are not alternatives: circuitRF reads one tree, and the
    /// corner flavour is derived from it afterwards (<see cref="RestrictToUsedFlavours"/>).</para>
    ///
    /// <para>The main tree is the directory defining strictly the most subcircuits — the fact
    /// <see cref="PreferTheKitsMainLibrary"/> already orders by. Candidates all in one directory are
    /// variants rather than copies, two trees of equal size are a tie, and more than one candidate on
    /// the main tree is a variant question again: each of those stays unpaired.</para>
    /// </summary>
    private static string? OnTheMainTree(IReadOnlyList<string> tied, List<SubcircuitDef> subcircuits)
    {
        var size = tied.Select(DirectoryOf).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
            d => d,
            d => subcircuits.Count(s => DirectoryOf(s.SourceRelativePath).Equals(d, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        if (size.Count < 2) return null;

        var ranked = size.OrderByDescending(kv => kv.Value).ToList();
        if (ranked[0].Value == ranked[1].Value) return null;

        var onMain = tied.Where(f => DirectoryOf(f).Equals(ranked[0].Key, StringComparison.OrdinalIgnoreCase)).ToList();
        return onMain.Count == 1 ? onMain[0] : null;
    }

    /// <summary>
    /// A kit's own test, regression or LVS folder: a folder name holding one of those WORDS, read by
    /// splitting it on case changes, digits and separators. A prefix match ("test…") also took a
    /// folder of real models whose name merely begins with the letters — a chip, a technology.
    /// </summary>
    private static bool IsCheckingFolder(string relativePath)
        => relativePath.Split('/', '\\').SkipLast(1).Any(segment => WordsOf(segment).Any(CheckingWords.Contains));

    private static readonly HashSet<string> CheckingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "testing", "testbench", "testbenches", "testcase", "testcases", "testdata",
        "testsuite", "regression", "regressions", "lvs",
    };

    /// <summary>A folder name's words: split on anything not a letter, and before an upper-case letter that follows a lower-case one.</summary>
    private static IEnumerable<string> WordsOf(string segment)
    {
        var word = new StringBuilder();
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            bool boundary = !char.IsLetter(c) || (char.IsUpper(c) && i > 0 && char.IsLower(segment[i - 1]));
            if (boundary && word.Length > 0) { yield return word.ToString(); word.Clear(); }
            if (char.IsLetter(c)) word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString();
    }

    /// <summary>
    /// The model libraries the kit's own corner files name as their NOMINAL pairing.
    ///
    /// <para>A corner file's first section is the kit listing its ordinary alternative first, and that
    /// section says which library it goes with — the later sections point at statistical and mismatch
    /// variants of the same library instead. So the kit itself states which file is the canonical one,
    /// and nothing here has to recognise a variant by its name. That matters: a variant library
    /// declares extra process constants of its own, so a part bound to one is silently paired with a
    /// model the selected corner never configures.</para>
    /// </summary>
    private static HashSet<string> CanonicalLibraries(PdkImportReport report)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var net in report.Assets.Where(a => a.Kind == PdkAssetKind.Netlist))
        {
            string? full = ResolveAssetPath(report.RootPath, report.KitRoot, net.RelativePath);
            if (full is null) continue;

            string[] lines;
            try
            {
                if (new FileInfo(full).Length > PeekLimitBytes * 8) continue;
                lines = File.ReadAllLines(full);
            }
            catch { continue; }

            bool inFirst = false, sawFirst = false;
            foreach (var raw in lines)
            {
                string ln = raw.Trim();
                if (ln.StartsWith(".lib", StringComparison.OrdinalIgnoreCase) && !sawFirst)
                {
                    // A '.lib' with exactly one word after it OPENS a section; two words REQUESTS one.
                    var w = ln.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length == 2) { inFirst = true; sawFirst = true; }
                    continue;
                }
                if (ln.StartsWith(".endl", StringComparison.OrdinalIgnoreCase)) { inFirst = false; continue; }

                if (inFirst && ln.StartsWith(".include", StringComparison.OrdinalIgnoreCase))
                {
                    var w = ln.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length >= 2) result.Add(FileNameOf(w[1].Trim('"')));
                }
            }
        }

        return result;
    }

    private static string FileNameOf(string path)
    {
        int slash = path.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// The subcircuit a record symbol's own netlist line CALLS (<c>X… @pinlist name</c>), or null.
    /// Decision D1 of brief-gdf-1b, taken 2026-10-30.
    ///
    /// <para><b>Why this is not the exact-name binding that was reverted (2026-08-08).</b> That rule
    /// paired a part with whatever subcircuit happened to share the PART's name, which circuitRF had
    /// inferred, and it attached most of a palette to regression netlists; its values also sat behind
    /// a corner nobody could select yet. Here the SYMBOL states the name its line calls — the kit
    /// saying what the part is — checking folders are excluded before anything is compared, and
    /// corner selection exists. Measured on three open kits before building it: the name-likeness
    /// matcher this replaces for such symbols paired 23 parts with a DIFFERENT device than the symbol
    /// names — a p-channel part with an n-channel definition, a 20 V device with its isolated variant,
    /// one kit's every pairing with a regression netlist.</para>
    ///
    /// <para><b>Strict, and never a fallback to a guess.</b> The name must match (the dialect is
    /// case-insensitive) and the terminal count must equal the symbol's pins plus its stated extra
    /// terminals — or the pins alone, which extraction accepts as well (a definition that leaves the
    /// body to its own default); the stated count is preferred when both exist. Several matching
    /// definitions are resolved only by the kit's own canonical library, its nominal corner, and — for
    /// one library shipped once per simulator — its main library tree (<see cref="OnTheMainTree"/>);
    /// still more than one, or none, leaves the part unpaired — a symbol that STATES what it calls and
    /// is handed something else would build a circuit the kit never described.</para>
    /// </summary>
    private static SubcircuitDef? StatedSubcircuitFor(
        string called, int pins, int extras, List<SubcircuitDef> subcircuits, PdkImportReport report)
    {
        var named = subcircuits.Where(s => s.Name.Equals(called, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = named.Where(s => s.PinCount == pins + extras).ToList();
        if (matches.Count == 0 && extras > 0) matches = [.. named.Where(s => s.PinCount == pins)];
        if (matches.Count <= 1) return matches.FirstOrDefault();

        var canonical = CanonicalLibraries(report);
        var preferred = matches.Where(s => canonical.Contains(FileNameOf(s.SourceRelativePath))).ToList();
        if (preferred.Count == 1) return preferred[0];

        // A kit that states one definition per corner (a `__tt`, `__ss`, `__ff` … file each) is resolved
        // by what its NOMINAL corner reads — the first section of each corner file, the alternative the
        // kit lists first. Selecting another corner at run time puts that corner's definition in place
        // (KitCornerVariants), so pairing to the nominal one is what makes every corner reachable.
        var reached = NominalCornerFiles(report);
        var nominal = matches
            .Where(s => ResolveAssetPath(report.RootPath, report.KitRoot, s.SourceRelativePath) is { } full
                        && reached.Contains(Path.GetFullPath(full)))
            .ToList();
        if (nominal.Count == 1) return nominal[0];

        var tied = nominal.Count > 1 ? nominal : preferred.Count > 1 ? preferred : matches;
        string? main = OnTheMainTree([.. tied.Select(s => s.SourceRelativePath)], subcircuits);
        return main is null ? null : tied.Single(s => s.SourceRelativePath == main);
    }

    /// <summary>
    /// Every file the first section of each of the kit's corner files reaches, absolute. Built only
    /// when a stated name is otherwise ambiguous, and by following INCLUSION LINES as text: which
    /// files a section reaches is all this needs, and parsing the model libraries behind it to learn
    /// that cost one kit five seconds of import.
    /// </summary>
    private static HashSet<string> NominalCornerFiles(PdkImportReport report)
    {
        if (report.NominalCornerFilesCache is { } cached) return cached;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string AbsolutePath, string AxisId)>();
        foreach (var net in report.Assets.Where(a => a.Kind == PdkAssetKind.Netlist))
            if (!IsCheckingFolder(net.RelativePath) &&
                ResolveAssetPath(report.RootPath, report.KitRoot, net.RelativePath) is { } full)
                candidates.Add((full, net.RelativePath));

        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var axis in PdkCorners.Discover(candidates))
                if (axis.Options.Count > 0)
                    Reach(candidates.First(c => c.AxisId == axis.AxisId).AbsolutePath, axis.Options[0],
                          files, visited, depth: 0);
        }
        catch { /* a kit whose corners will not read pairs nothing by this rule */ }

        report.NominalCornerFilesCache = files;
        return files;
    }

    /// <summary>The files <paramref name="file"/> (one section of it, when named) includes, recursively.</summary>
    private static void Reach(string file, string? section, HashSet<string> files, HashSet<string> visited, int depth)
    {
        if (depth > 16 || !visited.Add(file + "\n" + section)) return;

        string[] lines;
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > PeekLimitBytes * 16) return;
            lines = File.ReadAllLines(file);
        }
        catch { return; }

        string dir = Path.GetDirectoryName(file) ?? "";
        bool inScope = section is null;
        foreach (var raw in lines)
        {
            string ln = raw.Trim();
            if (ln.Length == 0 || ln[0] != '.') continue;
            var w = ln.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string head = w[0].ToLowerInvariant();

            if (section is not null)
            {
                // One word after `.lib` OPENS a section; `.endl` closes it.
                if (head == ".lib" && w.Length == 2) { inScope = w[1].Trim('"', '\'').Equals(section, StringComparison.OrdinalIgnoreCase); continue; }
                if (head == ".endl") { inScope = false; continue; }
            }
            if (!inScope || w.Length < 2) continue;

            string? target = head is ".include" or ".inc" || (head == ".lib" && w.Length >= 3)
                ? Path.GetFullPath(Path.Combine(dir, w[1].Trim('"', '\'')))
                : null;
            if (target is null) continue;

            files.Add(target);
            Reach(target, head == ".lib" ? w[2].Trim('"', '\'') : null, files, visited, depth + 1);
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
    /// filesystems, so `%K%I%T_%M%O%D%E%L` reads back as `KIT_MODEL`. Harmless on names that use no
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
    private sealed record SubcircuitDef(string Name, int PinCount, IReadOnlyList<PdkPartParameter> Parameters,
                                        string SourceRelativePath = "");

    /// <summary>One part a catalog declares.</summary>
    /// <param name="Symbol">The symbol it references, as "&lt;name&gt;@&lt;library&gt;", or empty.</param>
    private sealed record CatalogEntry(string Id, string Category, string Symbol);

    /// <summary>Reads a symbol library the kit ships, resolving it against either root.</summary>
    private static IReadOnlyList<KitSymbolTemplate> ReadSymbolLibrary(
        string root, string? kitRoot, string relativePath)
    {
        string rel  = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.Combine(root, rel);
        if (!File.Exists(full) && kitRoot is { Length: > 0 })
            full = Path.Combine(kitRoot, rel);
        return KitSymbolLibraryReader.TryReadFile(full);
    }

    /// <summary>
    /// Whether an asset is a symbol in the plain-text record format — one THIS importer reads,
    /// rather than artwork some other reader handles. The distinction decides both which parts are
    /// discovered and, when none are, which explanation the user gets.
    /// </summary>
    private static bool IsRecordSymbolFile(PdkAsset a)
        => a.Kind == PdkAssetKind.SymbolArtwork
        && a.Support == PdkAssetSupport.Supported
        && a.FormatName == SymbolRecordFileRecognizer.Format;

    /// <summary>Reads one plain-text record symbol file, resolving it against either root.</summary>
    private static KitSymbolFile? ReadSymbolFile(string root, string? kitRoot, string relativePath)
    {
        string rel  = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.Combine(root, rel);
        if (!File.Exists(full) && kitRoot is { Length: > 0 })
            full = Path.Combine(kitRoot, rel);
        return KitSymbolFileReader.TryReadFile(full);
    }

    /// <summary>
    /// Reads the parts an XML component catalog declares.
    ///
    /// <para>Parsed with a regex rather than an XML parser on purpose. These files carry a default
    /// namespace naming the tool that wrote them, so element lookups would have to be
    /// namespace-qualified against a URI circuitRF must not know — and a kit written by a different
    /// tool with the same shape would then silently yield nothing. Matching the element by local
    /// name reads both.</para>
    ///
    /// <para>The identity taken is the one the kit will be ASKED for: the model name where the entry
    /// gives one, falling back to the display name. Real catalogs disagree between the two — a
    /// catalog entry named for a package variant can point at a shared model — and taking the
    /// display name would name a part nothing can resolve.</para>
    /// </summary>
    private static IEnumerable<CatalogEntry> CatalogEntriesIn(string root, string? kitRoot, string relativePath)
    {
        string rel  = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.Combine(root, rel);
        if (!File.Exists(full) && kitRoot is { Length: > 0 })
            full = Path.Combine(kitRoot, rel);

        string text;
        try
        {
            if (!File.Exists(full) || new FileInfo(full).Length > PeekLimitBytes * 8) yield break;
            text = File.ReadAllText(full);
        }
        catch { yield break; }

        // The catalog's file name is the only grouping a kit of this shape offers, and it is a real
        // one: kits split their catalog by part family, one file each.
        string category = Path.GetFileNameWithoutExtension(rel);

        foreach (Match m in RxCatalogEntry.Matches(text))
        {
            string declared = m.Groups["name"].Value.Trim();
            string model    = RxCatalogModel.Match(m.Value) is { Success: true } mm
                            ? mm.Groups["v"].Value.Trim() : "";
            string symbol = RxCatalogSymbol.Match(m.Value) is { Success: true } sm
                          ? sm.Groups["v"].Value.Trim() : "";
            string id = model.Length > 0 ? model : declared;
            if (id.Length > 0) yield return new CatalogEntry(id, category, symbol);
        }
    }

    /// <summary>One catalog entry: the element, its name, and everything up to its close.</summary>
    private static readonly Regex RxCatalogEntry = new(
        @"<\s*(?:\w+:)?COMPONENT\b[^>]*?\bName\s*=\s*""(?<name>[^""]*)""[^>]*>(?<body>.*?)<\s*/\s*(?:\w+:)?COMPONENT\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RxCatalogModel = new(
        @"<\s*(?:\w+:)?MODEL\s*>(?<v>[^<]*)<", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxCatalogSymbol = new(
        @"<\s*(?:\w+:)?SYMBOL\s*>(?<v>[^<]*)<", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // define NAME ( t1 t2 … )   — the terminal list may run across several lines.
    private static readonly Regex RxDefine = new(
        @"^[ \t]*define[ \t]+([A-Za-z_]\w*)[ \t]*(\(([^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // parameters a=1 b="x" …    — continued onto the next line by a trailing backslash.
    private static readonly Regex RxParamAssign = new(
        @"([A-Za-z_]\w*)\s*=\s*(""[^""]*""|\S+)", RegexOptions.Compiled);

    private static IEnumerable<SubcircuitDef> SubcircuitsIn(string root, string? kitRoot, string relativePath)
    {
        string rel  = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.Combine(root, rel);

        // An imported "additions" folder is read together with the kit it names, so a netlist's own
        // relative path belongs to one root or the other.
        if (!File.Exists(full) && kitRoot is { Length: > 0 })
            full = Path.Combine(kitRoot, rel);
        string text;
        try
        {
            if (!File.Exists(full) || new FileInfo(full).Length > PeekLimitBytes * 8) yield break;
            text = File.ReadAllText(full);
        }
        catch { yield break; }

        // A netlist in the SPICE dialect states its subcircuits as `.subckt`, which the reader
        // written for that format already turns into ordinary cells with ports and a parameter
        // interface. Reaching for it here rather than adding a second grammar to the regex below is
        // the whole point of that reader existing — and it is what gives a kit of this shape its
        // parameter defaults and its terminal count.
        if (LooksLikeSpiceNetlist(text))
        {
            SpiceNetlistResult? read = null;
            try { read = SpiceNetlistReader.Read(text, Path.GetDirectoryName(full), full); }
            catch { read = null; }               // a netlist that will not read is not a failed import

            if (read is not null)
            {
                foreach (var cell in read.Library.Cells)
                    yield return new SubcircuitDef(
                        cell.Name,
                        cell.Ports.Count,
                        [.. cell.Parameters.Select(p => SeededParameter(p.Name, p.DefaultExpression))],
                        relativePath);
                yield break;
            }
        }

        foreach (Match m in RxDefine.Matches(text))
        {
            string name  = m.Groups[1].Value;
            int    pins  = m.Groups[3].Success
                ? m.Groups[3].Value.Split((char[])[' ', '\t', '\r', '\n', ','],
                                          StringSplitOptions.RemoveEmptyEntries).Length
                : 0;

            var declared = ParametersAfter(text, m.Index + m.Length);
            yield return new SubcircuitDef(name, pins, ResolveSentinels(text, m.Index + m.Length, declared),
                                          relativePath);
        }
    }

    /// <summary>
    /// A subcircuit parameter as a placed part's row is seeded with. A default holding a distribution is seeded
    /// BLANK, which leaves the subcircuit's own default standing: the row is written into the user's schematic,
    /// so a live call there would not open in a circuitRF that predates distributions, and even its nominal
    /// would turn the subcircuit's per-instance mismatch draw into a fixed override (docs/design/spice-models.md
    /// §8.4).
    /// </summary>
    private static PdkPartParameter SeededParameter(string name, string expression)
        => Evaluator.ContainsStatisticalCall(expression)
            ? new PdkPartParameter(name, "", IsText: !IsNumericText(SpiceExpression.ReduceDistributions(expression)))
            : new PdkPartParameter(name, expression, IsText: !IsNumericText(expression));

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
    /// does not match <c>TECH_FET_TYPEB</c>, because <c>include</c> is absent from it.</para>
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

        // A kit that yields NO PARTS must say so, always. Recognising a pile of files and reporting
        // nothing leaves the user with an empty palette and no reason for it — and this is the one
        // outcome where the reason is the entire message. The three support states exist precisely
        // so "I do not know what this is" and "I know exactly what this is and cannot read it yet"
        // are different answers; a silent empty import gives neither.
        else if (report.Parts.Count == 0)
        {
            int catalogs = report.Assets.Count(a => a.Kind == PdkAssetKind.ComponentCatalog);
            int netlists = report.Assets.Count(a => a.Kind == PdkAssetKind.Netlist);
            int symbols  = report.Assets.Count(IsRecordSymbolFile);
            int drawings = report.Assets.Count(a => a.Kind is PdkAssetKind.SymbolArtwork
                                                            or PdkAssetKind.LayoutArtwork);

            string why =
                catalogs > 0 ? "its catalog declared none that could be read"
              // Symbols in a format circuitRF READS, yielding no part, means one thing: none of
              // them declares a terminal. Sending the user to look at the folder layout instead —
              // which is what the cell-database message does — would be the wrong place entirely.
              : symbols  > 0 ? "its symbol files declare no terminals that circuitRF could read, " +
                               "so nothing in the kit can be wired to anything"
              : netlists > 0 ? "its netlists declare no subcircuit that the kit also draws"
              : drawings > 0 ? "its drawings are not arranged as a cell database, so no part name " +
                               "could be read from them"
              :                "it declares its parts in none of the ways circuitRF reads — no " +
                               "component catalog, no netlist, no symbol files, and no cell database";

            report.Warn(
                $"No placeable parts were found in this kit: {why}.",
                "Its files are listed above and any usable data is still recorded. If this kit " +
                "states its parts in a form circuitRF does not read yet, that is a feature " +
                "request — say which file lists them.");
        }

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
            int n = 0;
            while (n < buf.Length)
            {
                int got = s.Read(buf, n, Math.Min(PeekChunk, buf.Length - n));
                if (got <= 0) break;
                for (int i = n; i < n + got; i++) if (buf[i] == 0) return "";   // binary
                n += got;
            }
            return n == 0 ? "" : Encoding.UTF8.GetString(buf, 0, n);
        }
        catch { return ""; }
    }
}
