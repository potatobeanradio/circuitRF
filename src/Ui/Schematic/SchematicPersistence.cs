using System.Text.Json;
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
    public int    FormatVersion { get; set; } = 1;
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
}

public sealed class CschComponent
{
    // Id is NOT persisted — it is runtime identity only; assigned fresh on import.
    public string InstanceName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SymbolKind Symbol   { get; set; }
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
}

public sealed class CschParameter
{
    public string Name            { get; set; } = "";
    public string Expression      { get; set; } = "";
    public string Unit            { get; set; } = "";
    public bool   ShowOnSchematic { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UnitDimension Dimension { get; set; } = UnitDimension.None;
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
    public const int CurrentFormatVersion = 1;

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
        => File.WriteAllText(path, Serialize(model, cellName, panX, panY, zoom));

    // ── Read ──────────────────────────────────────────────────────────────────

    public static (SchematicEditModel model, CschViewState view, string cellName) Deserialize(
        string json, string? cschDirectory = null)
    {
        var file = JsonSerializer.Deserialize<CschFile>(json, _jsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .csch file.");

        if (file.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException(
                $".csch format_version {file.FormatVersion} does not match " +
                $"expected {CurrentFormatVersion}. Regenerate the file.");

        return (FromFileModel(file, cschDirectory), file.View, file.CellName);
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
            };
            foreach (var p in c.Parameters)
                cc.Parameters.Add(new CschParameter
                    { Name = p.Name, Expression = p.Expression, Unit = p.Unit, ShowOnSchematic = p.ShowOnSchematic, Dimension = p.Dimension });
            if (c.LabelOffsets.Any(o => o.DX != 0 || o.DY != 0))
                cc.LabelOffsets = c.LabelOffsets.Select(o => new[] { o.DX, o.DY }).ToList();
            // Omit when true (the default) to keep files compact.
            if (!c.ShowTypeLabel)    cc.ShowTypeLabel    = false;
            if (!c.ShowInstanceName) cc.ShowInstanceName = false;
            if (c.CellRef is not null) cc.CellRef = c.CellRef;
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
            file.NetLabels.Add(new CschNetLabel { X = n.X, Y = n.Y, Name = n.Name });

        foreach (var d in m.Dots)
            file.Dots.Add(new CschDot { X = d.X, Y = d.Y });

        foreach (var obj in m.CanvasObjects)
            file.CanvasObjects.Add(ToCanvasObjectRecord(obj));

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
            };
            foreach (var cp in cc.Parameters)
                c.Parameters.Add(new EditableParameter
                    { Name = cp.Name, Expression = cp.Expression, Unit = cp.Unit, ShowOnSchematic = cp.ShowOnSchematic, Dimension = cp.Dimension });
            if (cc.LabelOffsets is not null)
                foreach (var lo in cc.LabelOffsets)
                    if (lo.Length >= 2) c.LabelOffsets.Add((lo[0], lo[1]));
            // Null means "not written" → use the persisted default (true).
            if (cc.ShowTypeLabel    is bool stl) c.ShowTypeLabel    = stl;
            if (cc.ShowInstanceName is bool sin) c.ShowInstanceName = sin;
            if (cc.CellRef is not null) c.CellRef = cc.CellRef;
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
            m.NetLabels.Add(new EditableNetLabel { X = n.X, Y = n.Y, Name = n.Name });

        foreach (var d in file.Dots)
            m.Dots.Add(new EditableDot { X = d.X, Y = d.Y });

        foreach (var co in file.CanvasObjects)
        {
            var obj = FromCanvasObjectRecord(co, directory);
            if (obj is not null) m.CanvasObjects.Add(obj);
        }

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
