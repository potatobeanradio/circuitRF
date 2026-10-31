// The Model Composer window's half of the commands (brief-modelcomposer-14 R-mc14-1, R-mc14-3, R-mc14-6 … R-mc14-9;
// overview D13, D15). The window holds a document in memory and edits it; everything it does WITH the document — write
// it, open its data through the cache, de-embed it, install a network, preview an import — is one of these, so the
// window's folders hold no flow and the source scan in ModelCliNoLogicTests stays true. Runs that change results (an
// import, a fit) are the file-based commands of ModelCommands.cs, exactly as the `model` verb calls them.

using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>
/// What opening a document's data gave the window (R-mc14-6): each set's normalised data, its intrinsic data (Active),
/// the pad-removed data of a set measured before the pads, the hygiene flags, the stale steps, and what to tell the user.
/// <see cref="SourceReads"/> counts source files the cache had to open — zero when it answered from its index.
/// </summary>
public sealed record ModelData(
    IReadOnlyDictionary<string, NormalisedData> Data,
    IReadOnlyDictionary<string, DataSet> Intrinsic,
    IReadOnlyDictionary<string, NormalisedData> PadsRemoved,
    IReadOnlyList<HygieneFlag> Flags,
    IReadOnlyList<string> Stale,
    IReadOnlyList<string> Status,
    IReadOnlyList<string> Warnings,
    long SourceReads)
{
    public static ModelData Empty { get; } = new(
        new Dictionary<string, NormalisedData>(), new Dictionary<string, DataSet>(), new Dictionary<string, NormalisedData>(),
        [], [], [], [], 0);
}

/// <summary>An importer preview (R-mc14-6): the first lines of the first source as text, the first points as read, and
/// one line of what was read — or the refusal in its place.</summary>
public sealed record ImportPreview(IReadOnlyList<string> RawLines, IReadOnlyList<BiasPoint> Points, string Line, bool Refused);

public static partial class ModelCommands
{
    /// <summary>Validates and writes <paramref name="doc"/> to <paramref name="cmcPath"/> — the window's Save.</summary>
    public static void Save(string cmcPath, CmcDocument doc) => CmcFile.Write(Path.GetFullPath(cmcPath), doc);

    /// <summary>
    /// Opens <paramref name="doc"/>'s data through the cache and, for an Active document, de-embeds it (MC-4) — the window's
    /// open and its refresh after an edit to the pads or the network. A de-embedding refusal is a warning here, not a
    /// throw: the data still plots, and the Pads or Network step says why it cannot go further. A missing source with no
    /// cache throws <see cref="DataImportException"/>; cancellation throws and writes nothing.
    /// </summary>
    public static ModelData Load(string cmcPath, CmcDocument doc, RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        var cache = new CmcCache(cmcPath);
        var open = cache.Open(doc, rc);
        var warnings = new List<string>(open.Warnings);
        var status = new List<string>(open.Status);

        var intrinsic = new Dictionary<string, DataSet>(StringComparer.Ordinal);
        var padsRemoved = new Dictionary<string, NormalisedData>(StringComparer.Ordinal);
        if (doc.Kind == CmcKind.Active && open.Data.Count > 0)
        {
            try
            {
                var r = new IntrinsicExtraction(cmcPath).Run(doc, open.Data, rc: rc);
                foreach (var (k, v) in r.Sets) intrinsic[k] = v;
                warnings.AddRange(r.Notes);
            }
            catch (Exception ex) when (ex is ModelingException or CmcFormatException) { warnings.Add(ex.Message); }

            var pads = CmcExtrinsic.Pads(doc);
            if (pads.Kind != PadsKind.None)
            {
                try
                {
                    var removal = PadDeembedding.Load(pads, Path.GetDirectoryName(cmcPath)!);
                    foreach (var set in doc.DataSets)
                        if (open.Data.TryGetValue(set.Name, out var d) && d.S is not null
                            && (set.Conditions.ReferencePlane ?? ReferencePlane.Probe) is ReferencePlane.Instrument or ReferencePlane.Probe)
                            padsRemoved[set.Name] = removal.Remove(d, set.Name, rc);
                }
                catch (ModelingException ex) { if (!warnings.Contains(ex.Message)) warnings.Add(ex.Message); }
            }
        }

        var flags = DataHygiene.Check([.. doc.DataSets.Where(s => open.Data.ContainsKey(s.Name)).Select(s => (s, open.Data[s.Name]))]);
        warnings.AddRange(DataHygiene.SetWarnings(flags));
        IReadOnlyList<string> stale = [];
        try { stale = CmcStamps.StaleSteps(cmcPath, doc); }
        catch (Exception ex) when (ex is ModelingException or CmcFormatException) { }
        return new ModelData(open.Data, intrinsic, padsRemoved, flags, stale, status, warnings, cache.SourceReads);
    }

    /// <summary>
    /// The document with network <paramref name="stockId"/> chosen (R-mc14-8) — copied beside the <c>.cmc</c> as the stock
    /// networks always are — or with no network at all when it is null (the data is taken as intrinsic).
    /// </summary>
    public static CmcDocument ChooseNetwork(CmcDocument doc, string cmcPath, string? stockId)
    {
        if (doc.Kind != CmcKind.Active) throw new ModelingException("Network", "a Passive document has no six-pin extrinsic network.");
        return stockId is null ? CmcExtrinsic.With(doc, NetworkSettings.None) : StockNetworks.Choose(doc, cmcPath, stockId);
    }

    /// <summary>
    /// The document with the schematic <paramref name="cschJson"/> installed as its network (R-mc14-8): checked for the six
    /// pins first — a refusal names the pins it found and writes nothing — then written beside the <c>.cmc</c> as
    /// <c>&lt;stem&gt;.csch</c> (or <c>&lt;stem&gt;-2.csch</c> …, never over a file) with every variable at its defaults.
    /// The flatness band is kept.
    /// </summary>
    public static CmcDocument InstallNetwork(CmcDocument doc, string cmcPath, string stem, string cschJson)
    {
        if (doc.Kind != CmcKind.Active) throw new ModelingException("Network", "a Passive document has no six-pin extrinsic network.");
        SixPinNetwork.FromText(stem + ".csch", cschJson);
        string folder = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, stem + ".csch");
        for (int k = 2; File.Exists(path); k++) path = Path.Combine(folder, $"{stem}-{k}.csch");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var w = new StreamWriter(fs)) w.Write(cschJson);
        return UseNetworkFile(doc, cmcPath, path);
    }

    /// <summary>The document with the six-pin <c>.csch</c> at <paramref name="cschPath"/> as its network, every variable at its
    /// defaults — the window's Open of a drawn network. The flatness band is kept.</summary>
    public static CmcDocument UseNetworkFile(CmcDocument doc, string cmcPath, string cschPath)
    {
        if (doc.Kind != CmcKind.Active) throw new ModelingException("Network", "a Passive document has no six-pin extrinsic network.");
        string folder = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        string full = Path.GetFullPath(Path.IsPathRooted(cschPath) ? cschPath : Path.Combine(folder, cschPath));
        var network = SixPinNetwork.Load(full);
        var settings = network.DefaultSettings(Path.GetRelativePath(folder, full).Replace('\\', '/'));
        return CmcExtrinsic.With(doc, settings with { FlatnessBand = CmcExtrinsic.Network(doc).FlatnessBand });
    }

    /// <summary>
    /// What importing <paramref name="set"/> would read (R-mc14-6), from at most its first <paramref name="sources"/> sources:
    /// the first <paramref name="rows"/> lines of the first source as text (a text source only), the first points, and one
    /// line. A refusal is the line, never a throw. Writes nothing and touches no cache.
    /// </summary>
    /// <summary>
    /// What an array source holds, one line per array — its name, shape and whether it is complex — then each variable that
    /// cannot be read, with why: the array file's counterpart of a text file's first lines. Never throws; a file that cannot
    /// be listed is one line saying why. Cached by path, size and time, since the preview re-runs on every keystroke.
    /// </summary>
    public static IReadOnlyList<string> ArrayListing(string path, string format)
    {
        if (!File.Exists(path)) return [];
        var info = new FileInfo(path);
        var key = (path, format, info.Length, info.LastWriteTimeUtc.Ticks);
        lock (ListingLock)
            if (_listing is { } c && c.Key == key) return c.Lines;
        List<string> lines;
        try
        {
            var (arrays, skipped) = DataImport.ListArrays(path, format);
            int width = arrays.Select(a => a.Name.Length).DefaultIfEmpty(0).Max();
            lines = [.. arrays.Select(a => $"{a.Name.PadRight(width)}   {a.ShapeText}{(a.IsComplex ? "   complex" : "")}")];
            lines.AddRange(skipped.Select(x => $"{x.Name.PadRight(width)}   not read: it {x.Why}"));
            if (lines.Count == 0) lines.Add("it holds no arrays.");
        }
        catch (Exception ex) when (ex is DataImportException or IOException or UnauthorizedAccessException) { lines = [ex.Message]; }
        lock (ListingLock) _listing = (key, lines);
        return lines;
    }

    private static readonly object ListingLock = new();
    private static ((string, string, long, long) Key, List<string> Lines)? _listing;

    public static ImportPreview Preview(string cmcPath, CmcDataSet set, int rows = 6, int sources = 3)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        var raw = new List<string>();
        if (set.Sources.Count > 0 && set.Import.Format is ImportSettings.Csv or ImportSettings.Touchstone)
        {
            string first = DataImport.ResolveSource(dir, set.Sources[0]);
            if (File.Exists(first))
                raw.AddRange(File.ReadLines(first).Where(l => l.Trim().Length > 0).Take(rows));
        }
        else if (set.Sources.Count > 0 && ImportSettings.IsArrayFormat(set.Import.Format))
            raw.AddRange(ArrayListing(DataImport.ResolveSource(dir, set.Sources[0]), set.Import.Format));
        try
        {
            var reduced = set with { Sources = [.. set.Sources.Take(sources)] };
            var r = DataImport.Import(reduced, dir);
            string line = set.Sources.Count > sources ? $"first {sources} of {set.Sources.Count} files: {r.Summary}" : r.Summary;
            if (r.Notes.Count > 0) line += " " + string.Join(" ", r.Notes);
            return new ImportPreview(raw, [.. r.Data.Points.Take(rows)], line, false);
        }
        catch (Exception ex) when (ex is DataImportException or CmcFormatException or ModelingException or IOException)
        {
            return new ImportPreview(raw, [], ex.Message, true);
        }
    }

    /// <summary>
    /// A Passive document with the schematic <paramref name="cschJson"/> as its step's topology (R-mc14-9): checked for pins
    /// numbered 1…N first — a refusal writes nothing — then written beside the <c>.cmc</c> as <c>&lt;stem&gt;.csch</c> (never
    /// over a file) and named by the step's settings.
    /// </summary>
    public static CmcDocument InstallTopology(CmcDocument doc, string cmcPath, string stem, string cschJson)
    {
        if (doc.Kind != CmcKind.Passive) throw new ModelingException("Topology", "an Active document has no passive topology.");
        PassiveNetwork.FromText(stem + ".csch", cschJson);
        string folder = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, stem + ".csch");
        for (int k = 2; File.Exists(path); k++) path = Path.Combine(folder, $"{stem}-{k}.csch");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var w = new StreamWriter(fs)) w.Write(cschJson);
        string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
        return PassiveStep.WithSettings(doc, PassiveStep.Settings(doc) with { Topology = relative });
    }

    /// <summary>The ranked topology proposals of a Passive document's step (R-mc14-9, MC-12 R-mc12-4), the first three
    /// offered. Writes nothing; choosing one is an edit of the step's settings.</summary>
    public static IReadOnlyList<PassiveProposal> Propose(string cmcPath, List<string>? notes = null, RunControl? rc = null) =>
        PassiveStep.ProposeFile(cmcPath, notes, rc);

    /// <summary>The analysis lines of a Passive document's block, or each of a family's (R-mc14-9, MC-12 R-mc12-2).</summary>
    public static IReadOnlyList<string> Analyse(string cmcPath, RunControl? rc = null) =>
        [.. PassiveStep.AnalyseFile(cmcPath, rc).SelectMany(a => a.Lines)];
}
