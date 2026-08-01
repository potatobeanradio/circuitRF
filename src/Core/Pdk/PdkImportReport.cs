using System.Text;

namespace CircuitRF.Core.Pdk;

/// <summary>Overall outcome of importing a kit.</summary>
public enum PdkImportStatus
{
    /// <summary>Everything found was understood.</summary>
    Imported,
    /// <summary>Usable parts were imported, but some of the kit could not be read.</summary>
    PartiallyImported,
    /// <summary>Nothing usable was found. The report says what WAS found.</summary>
    NotRecognized,
    /// <summary>The path itself was the problem — missing, unreadable, empty.</summary>
    Failed,
}

/// <summary>One part a kit offers for placement.</summary>
/// <param name="Id">Stable identifier, unique within the kit.</param>
/// <param name="DisplayName">What the palette shows.</param>
/// <param name="Category">Optional grouping for the palette.</param>
/// <param name="IconRelativePath">Palette icon within the kit, if one was found.</param>
/// <param name="SymbolArtwork">Symbol artwork for this part, if any was found — even when unreadable.</param>
/// <param name="LayoutArtwork">Layout artwork for this part, if any was found — even when unreadable.</param>
/// <param name="Parameters">Parameters the part declares, with the kit's own defaults.</param>
/// <param name="PinCount">Terminals the part declares; 0 when the kit does not state it.</param>
public sealed record PdkPart(
    string                            Id,
    string                            DisplayName,
    string                            Category = "",
    string?                           IconRelativePath = null,
    PdkAsset?                         SymbolArtwork = null,
    PdkAsset?                         LayoutArtwork = null,
    IReadOnlyList<PdkPartParameter>?  Parameters = null,
    int                               PinCount = 0);

/// <summary>
/// One parameter a part declares, and the kit's own default for it.
///
/// <para>The default is the KIT's, carried verbatim — circuitRF never invents one. An empty default
/// means the kit stated a name but no value, in which case whatever supplies the part's behaviour
/// owns it.</para>
/// </summary>
/// <param name="IsText">
/// True when the kit declared the value as a quoted string rather than a number. In practice this
/// marks kit INFRASTRUCTURE — a path to the model's own data files, a mode name — rather than a
/// design quantity the user chooses.
/// </param>
public sealed record PdkPartParameter(string Name, string DefaultExpression = "", bool IsText = false);

/// <summary>
/// The result of importing a kit: what was found, what was understood, what was not, and what to do
/// about the difference.
///
/// <para>This type is the whole point of the import path. Kits arrive in many formats and circuitRF
/// reads a few of them; the common case for a while will be "understood some of this". A boolean
/// success flag would throw away everything useful about that, so nothing here reduces to one.</para>
/// </summary>
public sealed class PdkImportReport
{
    public required string  RootPath      { get; init; }

    /// <summary>
    /// A second folder the kit's own files live in, or null. Set when the imported folder declares a
    /// <c>baseDirectory</c>: what was imported is then a small folder that ADDS to a kit — a
    /// manifest, a translated netlist — while the kit's own symbols, icons and models stay where
    /// they are.
    ///
    /// <para><b>Why that shape exists at all.</b> A supplier's kit is routinely read-only, and
    /// several are far too large to copy. Adding a file to one is not always possible and is never
    /// cheap, so the additions live in their own folder and name the kit they belong to.</para>
    /// </summary>
    public string? KitRoot { get; init; }
    public required string  KitName       { get; init; }
    public PdkImportStatus  Status        { get; set; } = PdkImportStatus.NotRecognized;

    public List<PdkAsset>   Assets        { get; } = [];
    public List<PdkPart>    Parts         { get; } = [];
    public List<PdkFinding> Findings      { get; } = [];

    /// <summary>Layer technology discovered in the kit, if any — the Layout Editor's entry point.</summary>
    public PdkAsset? LayerTechnology { get; set; }

    public IEnumerable<PdkAsset> Supported    => Assets.Where(a => a.Support == PdkAssetSupport.Supported);
    public IEnumerable<PdkAsset> KnownGaps    => Assets.Where(a => a.Support == PdkAssetSupport.RecognizedNotSupported);
    public IEnumerable<PdkAsset> Unrecognized => Assets.Where(a => a.Support == PdkAssetSupport.Unrecognized);

    public bool HasSymbolArtwork => Assets.Any(a => a.Kind == PdkAssetKind.SymbolArtwork);
    public bool HasLayoutArtwork => Assets.Any(a => a.Kind == PdkAssetKind.LayoutArtwork);

    public void Add(PdkAsset a) => Assets.Add(a);
    public void Info(string s, string action = "")    => Findings.Add(new(PdkFindingSeverity.Info, s, action));
    public void Warn(string s, string action = "")    => Findings.Add(new(PdkFindingSeverity.Warning, s, action));
    public void Blocker(string s, string action = "") => Findings.Add(new(PdkFindingSeverity.Blocker, s, action));

    /// <summary>
    /// A plain-text summary suitable for a dialog, the message pane, or a log.
    ///
    /// <para>When nothing was understood this deliberately still lists what WAS seen, grouped by
    /// format. "circuitRF does not recognise this kit" is a dead end; "this kit holds 4 binary cell
    /// views and 3 encrypted model files, and here is what each would need" is a starting point.</para>
    /// </summary>
    public string ToSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{KitName} — {Describe(Status)}");
        sb.AppendLine(RootPath);
        sb.AppendLine();

        if (Parts.Count > 0)
        {
            sb.AppendLine($"Parts available for placement: {Parts.Count}");
            foreach (var p in Parts.Take(20))
                sb.AppendLine($"    {p.DisplayName}{(string.IsNullOrEmpty(p.Category) ? "" : $"  [{p.Category}]")}");
            if (Parts.Count > 20) sb.AppendLine($"    … and {Parts.Count - 20} more");
            sb.AppendLine();
        }

        void Group(string title, IEnumerable<PdkAsset> assets)
        {
            var list = assets.ToList();
            if (list.Count == 0) return;
            sb.AppendLine(title);
            foreach (var g in list.GroupBy(a => a.FormatName).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"    {g.Key} ×{g.Count()}");
                var detail = g.First().Detail;
                if (!string.IsNullOrEmpty(detail)) sb.AppendLine($"        {detail}");
            }
            sb.AppendLine();
        }

        Group("Read:", Supported);
        Group("Recognised, but circuitRF cannot read these yet:", KnownGaps);
        Group("Not recognised:", Unrecognized);

        if (Findings.Count > 0)
        {
            sb.AppendLine("Notes:");
            foreach (var f in Findings.OrderByDescending(f => f.Severity))
            {
                sb.AppendLine($"    [{f.Severity}] {f.Summary}");
                if (!string.IsNullOrEmpty(f.SuggestedAction))
                    sb.AppendLine($"        → {f.SuggestedAction}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Describe(PdkImportStatus s) => s switch
    {
        PdkImportStatus.Imported          => "imported",
        PdkImportStatus.PartiallyImported => "partially imported",
        PdkImportStatus.NotRecognized     => "nothing usable found",
        _                                 => "could not be read",
    };
}
