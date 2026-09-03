using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  .csch file format — rev 1 (alpha, no back-compat per policy).
//  JSON serialisation modelled on splotRF's DataDisplayConfig.cs:
//    - System.Text.Json with enum-as-string
//    - Paths not payloads (bitmaps store file path)
//    - Nullable/defaulted fields for within-version graceful load
//    - format_version: reject on mismatch (alpha policy)
// ──────────────────────────────────────────────────────────────────────────────

// ── JSON model types ──────────────────────────────────────────────────────────

public sealed class CschFile
{
    public int    FormatVersion { get; set; } = 2;
    public string CellName      { get; set; } = "";
    public double GridSize           { get; set; } = 100.0;
    public bool   GridSnap           { get; set; } = true;
    /// <summary>Fine authoring grid divisor k (p = GridSize/k). Absent in old files → default 20.</summary>
    public int    AuthorGridDivisor  { get; set; } = 20;

    public List<CschComponent>  Components    { get; set; } = [];
    public List<CschWire>       Wires         { get; set; } = [];
    public List<CschNetLabel>   NetLabels     { get; set; } = [];
    public List<CschDot>        Dots          { get; set; } = [];
    public List<CschCanvasObject> CanvasObjects { get; set; } = [];
    public CschViewState        View          { get; set; } = new();

    // Absent in old files → null → loaded as empty (graceful within-version load).
    // Written only when non-empty to keep analysis-free .csch files compact.
    public List<CschAnalysis>?    Analyses     { get; set; }
    public List<CschMeasurement>? Measurements { get; set; }

    /// <summary>User override for the run's results file name (blank/absent = default
    /// &lt;schematicKey&gt;.npy). See SchematicEditModel.ResultsFileName.</summary>
    public string? ResultsFileName { get; set; }

    /// <summary>Corner selections, keyed by <c>&lt;kit&gt;|&lt;kit-relative corner file&gt;</c>.
    /// Absent (null) when no corner is selected — the case for every design that never opens the
    /// Corners block. See SchematicEditModel.CornerSelections.</summary>
    public Dictionary<string, string>? CornerSelections { get; set; }
}

public sealed class CschComponent
{
    // Id is NOT persisted — it is runtime identity only; assigned fresh on import.
    public string InstanceName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolKind Symbol   { get; set; }

    /// <summary>The original "Symbol" string when it didn't match any known <see cref="SymbolKind"/>
    /// (R-hk-19a) — set by <see cref="Deserialize"/>'s per-component tolerant parse, never written
    /// by the ordinary path. Null for every recognized component.</summary>
    public string? UnknownSymbolRawName { get; set; }

    public double X            { get; set; }
    public double Y            { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolRotation Rotation { get; set; }
    public bool   MirrorX         { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DisableState Disable   { get; set; } = DisableState.None;

    public List<CschParameter> Parameters { get; set; } = [];

    /// <summary>Per-label world-position deltas [[dx,dy], ...]. Null when all zero (omitted from file).</summary>
    public List<double[]>? LabelOffsets { get; set; }

    /// <summary>Whether to render the type label; omitted (null) when true (default).</summary>
    public bool? ShowTypeLabel    { get; set; }
    /// <summary>Whether to render the instance name; omitted (null) when true (default).</summary>
    public bool? ShowInstanceName { get; set; }

    /// <summary>
    /// Relative path from the schematic directory to the referenced cell folder.
    /// Null for built-in components. Omitted from file when null (WhenWritingNull).
    /// </summary>
    public string? CellRef { get; set; }

    /// <summary>
    /// Explicitly detached port indices. Omitted (null) when empty — the common case.
    /// </summary>
    public List<int>? DetachedPorts { get; set; }

    /// <summary>
    /// The content hash of the referenced cell's published INTERFACE at the moment this component was
    /// placed (SL3 R-sl3-4) — pins, port count and declared parameter names, reduced by
    /// <see cref="CellInterfaceHash"/>. Written beside <see cref="CellRef"/> because that is the only
    /// place the fact "this design was authored against THAT shape" can live.
    ///
    /// <para><b>Absent is not a warning</b> (R-sl3-5): every <c>.csch</c> written before this field
    /// existed reads back null, which means <i>never recorded</i> and renders exactly as it did
    /// before. <c>WhenWritingNull</c>, no <c>FormatVersion</c> bump — the established convention for
    /// every optional field these formats have gained.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CellInterfaceHash { get; set; }
}

public sealed class CschParameter
{
    public string Name            { get; set; } = "";

    /// <summary>
    /// The expression, when it is one. <b>Null — and therefore absent from the file — when
    /// <see cref="Value"/> carries the parameter instead</b>, so a payload parameter does not also
    /// write an empty string beside its own document. An ordinary parameter with nothing in it is
    /// omitted for the same reason and reads back as "".
    /// </summary>
    public string? Expression     { get; set; }

    public string Unit            { get; set; } = "";
    public bool   ShowOnSchematic { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UnitDimension Dimension { get; set; } = UnitDimension.None;

    /// <summary>
    /// A parameter whose value is a JSON DOCUMENT, stored as one — a nested object rather than a
    /// string full of <c>\"</c>.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"how much work is it to support the expression as a nested object
    /// in the .csch json? If it won't break anything else or conflict with another philosophy, then
    /// please do it."</i>
    ///
    /// <para>It is the last step of making an embedded design readable. <c>MatchEmbedding.Encode</c>
    /// already writes plain JSON instead of base64, but a JSON document inside a JSON STRING is
    /// escaped — <c>"{\"F1\":1800000000,…}"</c> — so every quote in it is two characters and the
    /// whole design is one very long line. Written here it is an ordinary nested object under
    /// <c>WriteIndented</c>: one field per line, no escapes, and diffable.</para>
    ///
    /// <para><b>The rule is general, not a Match special case.</b> Any parameter whose expression
    /// begins with <c>{</c> is stored this way, because that is exactly the set of parameters whose
    /// value is a document rather than an expression: circuitRF's expression language has no brace
    /// token, so no real expression can collide with the discriminator. It is the same test
    /// <c>MatchEmbedding.TryDecode</c> and <c>CnlWriter.FormatParam</c> already branch on, which is
    /// what keeps the three from disagreeing about what a payload is.</para>
    ///
    /// <para>Exactly one of <see cref="Expression"/> and this is written. The reader accepts either —
    /// a hand-authored file, or one written before this existed, still loads.</para>
    /// </remarks>
    public JsonNode? Value { get; set; }

    /// <summary>The expression this parameter carries, whichever form it was written in.</summary>
    public string ReadExpression() => Value?.ToJsonString() ?? Expression ?? "";

    /// <summary>Builds one, choosing the form from the expression itself.</summary>
    public static CschParameter From(
        string name, string expression, string unit, bool showOnSchematic, UnitDimension dimension)
    {
        var p = new CschParameter
        {
            Name = name, Unit = unit, ShowOnSchematic = showOnSchematic, Dimension = dimension,
        };

        // TryParse, not just a leading '{': a value that merely STARTS with a brace and is not valid
        // JSON has to survive verbatim, and losing it to an exception on save would be the worst
        // possible trade for a formatting improvement.
        if (expression.TrimStart().StartsWith('{'))
        {
            try
            {
                p.Value = JsonNode.Parse(expression);
                if (p.Value is not null) return p;
            }
            catch (JsonException) { /* not a document after all — store it as the string it is */ }
        }

        p.Expression = expression;
        return p;
    }
}

public sealed class CschWire
{
    // Id is NOT persisted — assigned fresh on import.
    public List<double[]> Points { get; set; } = [];   // [[x,y], ...]
}

public sealed class CschNetLabel
{
    // Id is NOT persisted — assigned fresh on import.
    public double X    { get; set; }
    public double Y    { get; set; }
    public string Name { get; set; } = "";

    // Wire anchor. OwnerWireIndex null ⇒ legacy/unanchored (omitted from file when null).
    public int?   OwnerWireIndex { get; set; }
    public int    SegmentIndex   { get; set; }
    public double AlongT         { get; set; }
    public double OffsetX        { get; set; }
    public double OffsetY        { get; set; }
}

public sealed class CschDot
{
    // Id is NOT persisted — assigned fresh on import.
    public double X  { get; set; }
    public double Y  { get; set; }
}

public sealed class CschCanvasObject
{
    // Id is NOT persisted — assigned fresh on import.

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CanvasObjectKind Kind { get; set; }

    // Shared placement
    public double X            { get; set; }
    public double Y            { get; set; }
    public double Width        { get; set; } = 300.0;
    public double Height       { get; set; } = 200.0;
    public double RotationDeg  { get; set; }
    public double Transparency { get; set; }
    public bool   IsLocked     { get; set; }
    public int    ZOrder       { get; set; }

    // Bitmap
    public string? ImagePath { get; set; }

    // Text
    public string? Text       { get; set; }
    public string? FontFamily { get; set; }
    public float   FontSize   { get; set; } = 12f;
    public bool    IsBold     { get; set; }
    public bool    IsItalic   { get; set; }
    public uint    ColorArgb  { get; set; } = 0xFF202020;

    // Primitive shape
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PrimitiveShape Shape { get; set; }
    public float LineWidth { get; set; } = 2f;
    public uint  PrimColor { get; set; } = 0xFF202020;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ArrowheadStyle Arrowheads { get; set; }
    public float ArrowSize { get; set; } = 12f;
    public double P1X { get; set; }
    public double P1Y { get; set; }
    public double P2X { get; set; }
    public double P2Y { get; set; } = 200.0;
}

public sealed class CschViewState
{
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double Zoom { get; set; } = 1.0;
}

// ── Serializer ────────────────────────────────────────────────────────────────

/// <summary>
/// Reads and writes .csch files. Framework-free (no Avalonia).
/// Modelled on splotRF's DataDisplayConfig / PlotExporter pattern:
/// references not payloads, enum-as-string, format_version reject-on-mismatch.
/// </summary>
public static class SchematicPersistence
{
    public const int CurrentFormatVersion = 2;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                 = { new JsonStringEnumConverter() },
    };

    // ── Write ─────────────────────────────────────────────────────────────────

    public static string Serialize(SchematicEditModel model, string cellName = "",
                                   double panX = 0, double panY = 0, double zoom = 1.0)
    {
        var file = ToFileModel(model, cellName, panX, panY, zoom);
        return JsonSerializer.Serialize(file, _jsonOpts);
    }

    public static void SaveToFile(string path, SchematicEditModel model, string cellName = "",
                                  double panX = 0, double panY = 0, double zoom = 1.0)
    {
        AtomicFile.WriteAllText(path, Serialize(model, cellName, panX, panY, zoom));
        // A model saved to a path now has a known on-disk location. Record its directory so
        // CellRef relative-path resolution works — mirrors what LoadFromFile does on open.
        // Without this, a schematic created via New Schematic keeps SchematicDirectory = null
        // for its whole session (saving alone never reloaded it), so cell placement — which needs
        // the base dir to compute and resolve CellRef — aborted silently at its first guard.
        model.SchematicDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public static (SchematicEditModel model, CschViewState view, string cellName) Deserialize(
        string json, string? cschDirectory = null)
    {
        // The "Symbol" field is parsed per-component, tolerant of a value naming a type this
        // build doesn't recognize (R-hk-19a — e.g. a since-removed type, or a file from a newer
        // version). A single unrecognized component must never abort the WHOLE file: the
        // Components array is pulled out and parsed element-by-element BEFORE the one-shot typed
        // deserialize of everything else, so a bad "Symbol" string can never propagate up through
        // List<CschComponent>'s own converter and fail the entire CschFile.
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Failed to deserialize .csch file.");

        var componentsNode = root["Components"] as JsonArray;
        root.Remove("Components");

        var file = JsonSerializer.Deserialize<CschFile>(root.ToJsonString(), _jsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .csch file.");

        if (file.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException(
                $".csch format_version {file.FormatVersion} does not match " +
                $"expected {CurrentFormatVersion}. Regenerate the file.");

        if (componentsNode is not null)
            foreach (var elem in componentsNode)
                file.Components.Add(ParseComponentTolerant(elem));

        return (FromFileModel(file, cschDirectory), file.View, file.CellName);
    }

    /// <summary>
    /// Parses one Components[] array element. Falls back to <see cref="SymbolKind.Unknown"/> +
    /// <see cref="CschComponent.UnknownSymbolRawName"/> (never throws, never drops the component)
    /// when "Symbol" doesn't match any current <see cref="SymbolKind"/> member — every OTHER field
    /// on that same element (InstanceName/X/Y/Rotation/etc.) still parses normally when present, so
    /// the component still renders at its original position, just as a generic placeholder.
    /// </summary>
    private static CschComponent ParseComponentTolerant(JsonNode? elem)
    {
        if (elem is not JsonObject obj)
            return new CschComponent { InstanceName = "?", Symbol = SymbolKind.Unknown, UnknownSymbolRawName = "(malformed)" };

        try
        {
            return JsonSerializer.Deserialize<CschComponent>(obj.ToJsonString(), _jsonOpts)
                ?? new CschComponent { InstanceName = "?", Symbol = SymbolKind.Unknown, UnknownSymbolRawName = "(null)" };
        }
        catch (JsonException)
        {
            string rawSymbol = TryGetString(obj, "Symbol") ?? "(missing)";
            string instanceName = TryGetString(obj, "InstanceName") ?? "?";
            try
            {
                // Retry with just "Symbol" patched to a known value — recovers every OTHER field
                // (X/Y/Rotation/Parameters/...) exactly, on the assumption the Symbol string was
                // the only thing that didn't match. Falls through to the minimal fallback below if
                // something else on this element was also malformed.
                var patched = obj.DeepClone().AsObject();
                patched["Symbol"] = nameof(SymbolKind.Unknown);
                var cc = JsonSerializer.Deserialize<CschComponent>(patched.ToJsonString(), _jsonOpts);
                if (cc is not null)
                {
                    cc.Symbol = SymbolKind.Unknown;
                    cc.UnknownSymbolRawName = rawSymbol;
                    return cc;
                }
            }
            catch (JsonException) { /* fall through to the minimal, always-safe placeholder */ }

            return new CschComponent
            {
                InstanceName = instanceName,
                Symbol = SymbolKind.Unknown,
                UnknownSymbolRawName = rawSymbol,
                X = TryGetDouble(obj, "X"),
                Y = TryGetDouble(obj, "Y"),
            };
        }
    }

    private static double TryGetDouble(JsonObject obj, string prop)
    {
        try { return obj[prop]?.GetValue<double>() ?? 0; }
        catch (Exception) { return 0; }
    }

    private static string? TryGetString(JsonObject obj, string prop)
    {
        try { return obj[prop]?.GetValue<string>(); }
        catch (Exception) { return null; }
    }

    public static (SchematicEditModel model, CschViewState view, string cellName) LoadFromFile(
        string path)
    {
        string json = File.ReadAllText(path);
        return Deserialize(json, Path.GetDirectoryName(path));
    }

    // ── Convert SchematicEditModel ↔ CschFile ─────────────────────────────────

    private static CschFile ToFileModel(
        SchematicEditModel m, string cellName,
        double panX, double panY, double zoom)
    {
        var file = new CschFile
        {
            FormatVersion    = CurrentFormatVersion,
            CellName         = cellName,
            GridSize         = m.GridSize,
            GridSnap         = m.GridSnap,
            AuthorGridDivisor = m.AuthorGridDivisor,
            View             = new CschViewState { PanX = panX, PanY = panY, Zoom = zoom },
        };

        foreach (var c in m.Components)
        {
            var cc = new CschComponent
            {
                InstanceName = c.InstanceName,
                Symbol = c.Symbol, X = c.X, Y = c.Y,
                Rotation = c.Rotation, MirrorX = c.MirrorX,
                Disable = c.Disable,
                UnknownSymbolRawName = c.UnknownSymbolRawName,
            };
            foreach (var p in c.Parameters)
                cc.Parameters.Add(CschParameter.From(
                    p.Name, p.Expression, p.Unit, p.ShowOnSchematic, p.Dimension));
            if (c.LabelOffsets.Any(o => o.DX != 0 || o.DY != 0))
                cc.LabelOffsets = c.LabelOffsets.Select(o => new[] { o.DX, o.DY }).ToList();
            // Omit when true (the default) to keep files compact.
            if (!c.ShowTypeLabel)    cc.ShowTypeLabel    = false;
            if (!c.ShowInstanceName) cc.ShowInstanceName = false;
            if (c.CellRef is not null) cc.CellRef = c.CellRef;
            // SL3 R-sl3-10: SAVE never records, refreshes or clears this — it writes back exactly
            // what was loaded. The recorded hash is the only evidence that the design was authored
            // against a different interface, and a product that erases that evidence on save has
            // implemented nothing. Accept (an explicit gesture) is the one thing that rewrites it.
            if (c.CellInterfaceHash is not null) cc.CellInterfaceHash = c.CellInterfaceHash;
            if (c.DetachedPorts.Count > 0) cc.DetachedPorts = c.DetachedPorts.OrderBy(i => i).ToList();
            file.Components.Add(cc);
        }

        foreach (var w in m.Wires)
        {
            var cw = new CschWire();
            foreach (var (x, y) in w.Points)
                cw.Points.Add([x, y]);
            file.Wires.Add(cw);
        }

        foreach (var n in m.NetLabels)
        {
            int? ownerIdx = null;
            if (n.IsAnchored)
            {
                int idx = m.Wires.FindIndex(w => w.Id == n.OwnerWireId);
                if (idx >= 0) ownerIdx = idx;
            }
            file.NetLabels.Add(new CschNetLabel
            {
                X = n.X, Y = n.Y, Name = n.Name,
                OwnerWireIndex = ownerIdx,
                SegmentIndex   = n.SegmentIndex, AlongT = n.AlongT,
                OffsetX        = n.OffsetX, OffsetY = n.OffsetY,
            });
        }

        foreach (var d in m.Dots)
            file.Dots.Add(new CschDot { X = d.X, Y = d.Y });

        foreach (var obj in m.CanvasObjects)
            file.CanvasObjects.Add(ToCanvasObjectRecord(obj));

        // Analyses + measurements via the shared encoder (§5.4).
        if (m.Analyses.Count > 0)
            file.Analyses = m.Analyses.Select(AnalysisSerialization.ToDto).ToList();
        if (m.Measurements.Count > 0)
            file.Measurements = m.Measurements.Select(AnalysisSerialization.ToDto).ToList();

        file.ResultsFileName = string.IsNullOrEmpty(m.ResultsFileName) ? null : m.ResultsFileName;

        // Written only when a corner is actually selected, so a design that never touched the
        // Corners block re-serializes byte-identically.
        if (m.CornerSelections.Count > 0)
            file.CornerSelections = new Dictionary<string, string>(m.CornerSelections, StringComparer.Ordinal);

        return file;
    }

    private static SchematicEditModel FromFileModel(CschFile file, string? directory)
    {
        var m = new SchematicEditModel
        {
            GridSize            = file.GridSize,
            GridSnap            = file.GridSnap,
            AuthorGridDivisor   = file.AuthorGridDivisor,
            SchematicDirectory  = directory,
        };

        foreach (var cc in file.Components)
        {
            var c = new EditableComponent
            {
                InstanceName = cc.InstanceName,
                Symbol = cc.Symbol, X = cc.X, Y = cc.Y,
                Rotation = cc.Rotation, MirrorX = cc.MirrorX,
                Disable = cc.Disable,
                UnknownSymbolRawName = cc.UnknownSymbolRawName,
            };
            foreach (var cp in cc.Parameters)
                c.Parameters.Add(new EditableParameter
                    { Name = cp.Name, Expression = cp.ReadExpression(), Unit = cp.Unit, ShowOnSchematic = cp.ShowOnSchematic, Dimension = cp.Dimension });
            if (cc.LabelOffsets is not null)
                foreach (var lo in cc.LabelOffsets)
                    if (lo.Length >= 2) c.LabelOffsets.Add((lo[0], lo[1]));
            // Null means "not written" → use the persisted default (true).
            if (cc.ShowTypeLabel    is bool stl) c.ShowTypeLabel    = stl;
            if (cc.ShowInstanceName is bool sin) c.ShowInstanceName = sin;
            if (cc.CellRef is not null) c.CellRef = cc.CellRef;
            if (cc.CellInterfaceHash is not null) c.CellInterfaceHash = cc.CellInterfaceHash;
            if (cc.DetachedPorts is not null)
                foreach (var idx in cc.DetachedPorts) c.DetachedPorts.Add(idx);
            m.Components.Add(c);
        }

        foreach (var cw in file.Wires)
        {
            var w = new EditableWire();
            foreach (var pt in cw.Points)
            {
                if (pt.Length >= 2) w.Points.Add((pt[0], pt[1]));
            }
            m.Wires.Add(w);
        }

        foreach (var n in file.NetLabels)
        {
            var lbl = new EditableNetLabel { X = n.X, Y = n.Y, Name = n.Name };
            if (n.OwnerWireIndex is int wi && wi >= 0 && wi < m.Wires.Count)
            {
                lbl.OwnerWireId  = m.Wires[wi].Id;
                lbl.SegmentIndex = n.SegmentIndex;
                lbl.AlongT       = n.AlongT;
                lbl.OffsetX      = n.OffsetX;
                lbl.OffsetY      = n.OffsetY;
            }
            m.NetLabels.Add(lbl);
        }

        foreach (var d in file.Dots)
            m.Dots.Add(new EditableDot { X = d.X, Y = d.Y });

        foreach (var co in file.CanvasObjects)
        {
            var obj = FromCanvasObjectRecord(co, directory);
            if (obj is not null) m.CanvasObjects.Add(obj);
        }

        // Analyses + measurements via the shared encoder (§5.4); absent → empty (graceful load).
        if (file.Analyses is not null)
            foreach (var dto in file.Analyses)
            {
                var a = AnalysisSerialization.FromDto(dto);
                if (a is not null) m.Analyses.Add(a);
            }
        if (file.Measurements is not null)
            foreach (var dto in file.Measurements)
                m.Measurements.Add(AnalysisSerialization.FromDto(dto));

        m.ResultsFileName = string.IsNullOrEmpty(file.ResultsFileName) ? null : file.ResultsFileName;

        if (file.CornerSelections is not null)
            foreach (var (key, section) in file.CornerSelections)
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(section))
                    m.CornerSelections[key] = section;

        return m;
    }

    // ── Canvas object helpers ──────────────────────────────────────────────────

    private static CschCanvasObject ToCanvasObjectRecord(EditableCanvasObject obj)
    {
        var rec = new CschCanvasObject
        {
            Kind = obj.Kind,
            X = obj.X, Y = obj.Y, Width = obj.Width, Height = obj.Height,
            RotationDeg = obj.RotationDeg, Transparency = obj.Transparency,
            IsLocked = obj.IsLocked, ZOrder = obj.ZOrder,
        };

        if (obj is EditableBitmap bm)
            rec.ImagePath = bm.ImagePath;

        else if (obj is EditableText txt)
        {
            rec.Text = txt.Text; rec.FontFamily = txt.FontFamily;
            rec.FontSize = txt.FontSize; rec.IsBold = txt.IsBold;
            rec.IsItalic = txt.IsItalic; rec.ColorArgb = txt.ColorArgb;
        }

        else if (obj is EditablePrimitive prim)
        {
            rec.Shape = prim.Shape; rec.LineWidth = prim.LineWidth;
            rec.PrimColor = prim.ColorArgb;
            rec.Arrowheads = prim.Arrowheads; rec.ArrowSize = prim.ArrowSize;
            rec.P1X = prim.P1X; rec.P1Y = prim.P1Y;
            rec.P2X = prim.P2X; rec.P2Y = prim.P2Y;
        }

        return rec;
    }

    private static EditableCanvasObject? FromCanvasObjectRecord(CschCanvasObject rec, string? dir)
    {
        void ApplyBase(EditableCanvasObject o)
        {
            o.X = rec.X; o.Y = rec.Y; o.Width = rec.Width; o.Height = rec.Height;
            o.RotationDeg = rec.RotationDeg; o.Transparency = rec.Transparency;
            o.IsLocked = rec.IsLocked; o.ZOrder = rec.ZOrder;
        }

        switch (rec.Kind)
        {
            case CanvasObjectKind.Bitmap:
            {
                var bm = new EditableBitmap { ImagePath = rec.ImagePath ?? "" };
                // Resolve relative path
                if (!string.IsNullOrEmpty(bm.ImagePath) && !Path.IsPathFullyQualified(bm.ImagePath)
                    && dir is not null)
                    bm.ImagePath = Path.GetFullPath(Path.Combine(dir, bm.ImagePath));
                ApplyBase(bm);
                return bm;
            }
            case CanvasObjectKind.Text:
            {
                var txt = new EditableText
                {
                    Text = rec.Text ?? "Text", FontFamily = rec.FontFamily ?? "",
                    FontSize = rec.FontSize, IsBold = rec.IsBold, IsItalic = rec.IsItalic,
                    ColorArgb = rec.ColorArgb,
                };
                ApplyBase(txt);
                return txt;
            }
            default:   // Rect, Circle, Line
            {
                var prim = new EditablePrimitive
                {
                    Shape = rec.Shape, LineWidth = rec.LineWidth, ColorArgb = rec.PrimColor,
                    Arrowheads = rec.Arrowheads, ArrowSize = rec.ArrowSize,
                    P1X = rec.P1X, P1Y = rec.P1Y, P2X = rec.P2X, P2Y = rec.P2Y,
                };
                ApplyBase(prim);
                return prim;
            }
        }
    }

    // ── Clipboard (JSON selection fragment) ──────────────────────────────────

    /// <summary>Serializes a selection of objects to a JSON fragment for clipboard use.
    /// <paramref name="sourceGridSize"/> is the source design's P (connection grid pitch);
    /// it is embedded in the payload so the paste path can detect cross-grid pastes (§5).</summary>
    public static string SerializeSelection(
        IEnumerable<EditableComponent> comps,
        IEnumerable<EditableWire> wires,
        IEnumerable<EditableCanvasObject> canvasObjs,
        double sourceGridSize = 100.0)
    {
        var scratch = new SchematicEditModel { GridSize = sourceGridSize };
        foreach (var c in comps)      scratch.Components.Add(c.Clone());
        foreach (var w in wires)      scratch.Wires.Add(w.Clone());
        foreach (var o in canvasObjs) scratch.CanvasObjects.Add(o.Clone());
        return Serialize(scratch);
    }

    /// <summary>
    /// Deserializes a clipboard fragment (produced by SerializeSelection).
    /// Returns empty lists if the JSON is not a valid schematic fragment.
    /// <c>SourceGridSize</c> is the P that was in effect when the content was copied — used by
    /// <c>SchematicPasteCommand</c> to detect and handle cross-grid pastes (§5).
    /// </summary>
    public static (List<EditableComponent> Comps, List<EditableWire> Wires,
                   List<EditableCanvasObject> CanvasObjs, double SourceGridSize) DeserializeSelection(string json)
    {
        var file = JsonSerializer.Deserialize<CschFile>(json, _jsonOpts);
        if (file is null) return ([], [], [], 100.0);

        // Accept any version for clipboard fragments — don't reject on version mismatch.
        var model = FromFileModel(file, null);
        return (model.Components, model.Wires, model.CanvasObjects, file.GridSize);
    }
}
