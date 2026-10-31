// The Data step's importer (brief-modelcomposer-14 R-mc14-6): a file or folder, the bias pattern or the column map with a
// live preview of the first rows, and the conditions — and one line of what it read. The data set is spelled as JSON and
// read by CmcFile.ParseDataSet, exactly as `model import` builds it, so the dialog and the verb are refused by the same
// reader with the same field names. The import itself is the window's run (ModelCommands.Import).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Design.Modeling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelImportViewModel : ObservableObject
{
    private string? _cmcPath;
    private readonly CmcDocument _doc;
    private bool _loading;

    /// <summary>
    /// <paramref name="cmcPath"/> is the document's file, or null for a document not yet saved: the sources then resolve
    /// against a provisional <c>.cmc</c> in the first source's folder, named after its stem (designer round 28 — the data is
    /// chosen before the document is named), which <see cref="CmcPath"/> reports for the Save dialog to start from.
    /// </summary>
    public ModelImportViewModel(string? cmcPath, CmcDocument doc)
    {
        _cmcPath = cmcPath;
        IsProvisional = cmcPath is null;
        _doc = doc;
        Kinds = doc.Kind == CmcKind.Passive
            ? [DataSetKind.PassiveSParams]
            : [.. Enum.GetValues<DataSetKind>().Where(k => k != DataSetKind.PassiveSParams)];
        _kind = Kinds[0];
        // A passive block has no bias (MC-12): its pattern is optional and only ever names a family's {geometry}, so the
        // field starts empty — a transistor's Vgs/Vds default refused every passive file whose name did not carry a bias.
        if (IsPassive) _pattern = "";
    }

    /// <summary>True when the document is not saved yet and <see cref="CmcPath"/> follows the first source.</summary>
    public bool IsProvisional { get; }

    /// <summary>The <c>.cmc</c> the sources are relative to: the document's, or the provisional one; null until a source is
    /// chosen for an unsaved document.</summary>
    public string? CmcPath => _cmcPath;

    /// <summary>The document models a passive block: no bias pattern by default and no quiescent point.</summary>
    public bool IsPassive => _doc.Kind == CmcKind.Passive;
    public string PatternTip => IsPassive
        ? "Optional: a family's {geometry} from each file name, e.g. L{geometry}nH. Leave empty for a single block."
        : "Fields {vgs} {vds} {T} {id} {ig}; a unit as {id:mA}";

    public IReadOnlyList<DataSetKind> Kinds { get; }
    public IReadOnlyList<string> Planes { get; } = ["instrument", "probe", "device", "intrinsic"];
    public IReadOnlyList<string> PatternSources { get; } = ["name", "comment"];

    [ObservableProperty] private DataSetKind _kind;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _format = "";
    [ObservableProperty] private string _pattern = "Vgs{vgs}_Vds{vds}";
    [ObservableProperty] private string _patternSource = "name";
    /// <summary>CSV columns or array names, one <c>name=quantity[:unit]</c> per line or comma.</summary>
    [ObservableProperty] private string _map = "";
    [ObservableProperty] private bool _header = true;
    [ObservableProperty] private string _ambientC = "25";
    [ObservableProperty] private string _plane = "device";
    [ObservableProperty] private string _quiescentVgs = "";
    [ObservableProperty] private string _quiescentVds = "";
    /// <summary>Every other condition, <c>Key=Value</c> per line (<c>PulseWidth=1e-6</c>, <c>BiasPathOhms.Gate=10</c>).</summary>
    [ObservableProperty] private string _moreConditions = "";

    /// <summary>The sources, relative to the <c>.cmc</c>.</summary>
    public ObservableCollection<string> Sources { get; } = [];

    public string SourcesText => Sources.Count switch
    {
        0 => "",
        1 => Sources[0],
        _ => $"{Sources[0]} … {Sources.Count} files",
    };

    public bool IsTouchstone => Format == ImportSettings.Touchstone;
    /// <summary>The raw pane's title: an array file is listed by its arrays, a text file by its first lines.</summary>
    public string RawTitle => ImportSettings.IsArrayFormat(Format) ? "Arrays in the file" : "First lines";
    public bool IsMapped => Format == ImportSettings.Csv || ImportSettings.IsArrayFormat(Format);

    public ObservableCollection<string> PreviewRaw { get; } = [];
    public ObservableCollection<string> PreviewPoints { get; } = [];
    [ObservableProperty] private string _line = "";
    [ObservableProperty] private bool _refused = true;

    /// <summary>Takes the picked files or folder; a mixture of formats, or nothing readable, is the line.</summary>
    public void SetSources(IReadOnlyList<string> inputs)
    {
        Sources.Clear();
        try
        {
            if (IsProvisional) _cmcPath = ProvisionalPath(inputs);
            foreach (string s in ModelCommands.Sources(_cmcPath!, inputs)) Sources.Add(s);
            _loading = true;
            Format = ModelCommands.FormatOf(Sources[0]) ?? "";
            if (Name.Length == 0) Name = ModelCommands.DefaultName(_doc, inputs);
            if (IsMapped && Map.Length == 0) Map = Format == ImportSettings.Csv ? GuessMap() : SuggestArrayMap();
        }
        catch (ModelingException ex)
        {
            Line = ex.Message;
            Refused = true;
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(SourcesText));
        OnPropertyChanged(nameof(IsTouchstone));
        OnPropertyChanged(nameof(IsMapped));
        OnPropertyChanged(nameof(RawTitle));
        UpdatePreview();
    }

    /// <summary>The first source's folder and stem as a <c>.cmc</c> — a folder input expands to its first readable file.</summary>
    private static string ProvisionalPath(IReadOnlyList<string> inputs)
    {
        string first = inputs.Count > 0 ? Path.GetFullPath(inputs[0]) : throw new ModelingException("Sources", "no source is named.");
        string probe = Path.Combine(Directory.Exists(first) ? first : Path.GetDirectoryName(first)!, "probe" + CmcFile.Extension);
        string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(probe)!, ModelCommands.Sources(probe, inputs)[0]));
        return Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + CmcFile.Extension);
    }

    /// <summary>A CSV whose header names the usual quantities is mapped to them; the user edits what is not.</summary>
    private string GuessMap()
    {
        string first = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_cmcPath!))!, Sources[0]);
        string? header = File.Exists(first) ? File.ReadLines(first).FirstOrDefault() : null;
        if (header is null) return "";
        var map = new List<string>();
        foreach (string col in header.Split([',', '\t', ';'], StringSplitOptions.TrimEntries))
        {
            string q = col.ToLowerInvariant();
            if (CmcFile.PointQuantities.Contains(q) || q is "t") map.Add($"{col}={(q == "t" ? "T" : q)}");
        }
        return string.Join(", ", map);
    }

    /// <summary>An array file's map from its array names (ModelCommands.SuggestArrayMap); empty when nothing is recognised or the
    /// file cannot be listed — the preview then says why.</summary>
    private string SuggestArrayMap()
    {
        try { return ModelCommands.SuggestArrayMap(_cmcPath!, Sources[0], Format).Text; }
        catch (Exception ex) when (ex is DataImportException or IOException) { return ""; }
    }

    partial void OnKindChanged(DataSetKind value) => UpdatePreview();
    partial void OnPatternChanged(string value) => UpdatePreview();
    partial void OnPatternSourceChanged(string value) => UpdatePreview();
    partial void OnMapChanged(string value) => UpdatePreview();
    partial void OnHeaderChanged(bool value) => UpdatePreview();

    /// <summary>Re-reads the first sources with the current settings: the raw first lines, the first points, one line.</summary>
    public void UpdatePreview()
    {
        if (_loading) return;
        PreviewRaw.Clear();
        PreviewPoints.Clear();
        if (Sources.Count == 0) { Line = ""; Refused = true; return; }
        var set = Build(out string? why);
        if (set is null) { Line = why ?? ""; Refused = true; return; }
        var p = ModelCommands.Preview(_cmcPath!, set);
        foreach (string l in p.RawLines) PreviewRaw.Add(l);
        // A passive point has no bias, only a family's geometry when the pattern read one: its Vgs…Ig columns were NaN.
        foreach (var b in p.Points)
            PreviewPoints.Add(IsPassive
                ? double.IsNaN(b.Geometry) ? b.PointId : $"{b.PointId}   geometry {G(b.Geometry)}"
                : string.Join("   ", new[] { b.PointId, $"Vgs {G(b.Vgs)}", $"Vds {G(b.Vds)}", $"Id {G(b.Id)}", $"Ig {G(b.Ig)}" }));
        Line = p.Line;
        Refused = p.Refused;
    }

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

    /// <summary>The data set as the reader reads it, or null with the reader's refusal in <paramref name="why"/>.</summary>
    public CmcDataSet? Build(out string? why)
    {
        why = null;
        if (Sources.Count == 0) { why = "Choose a file or a folder."; return null; }
        var import = new JsonObject { ["Format"] = Format };
        if (IsTouchstone && !(IsPassive && Pattern.Trim().Length == 0))
        {
            import["Pattern"] = Pattern;
            import["PatternSource"] = PatternSource;
        }
        if (IsMapped)
        {
            foreach (string m in Map.Split([',', '\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = m.IndexOf('=');
                if (eq <= 0 || eq == m.Length - 1) { why = $"'{m}' is not name=quantity[:unit]."; return null; }
                string left = m[..eq].Trim(), right = m[(eq + 1)..].Trim();
                if (Format == ImportSettings.Csv)
                {
                    var col = new JsonObject { ["Column"] = left };
                    int colon = right.IndexOf(':');
                    col["Quantity"] = colon < 0 ? right : right[..colon];
                    if (colon >= 0) col["Unit"] = right[(colon + 1)..];
                    (import["Columns"] ??= new JsonArray()).AsArray().Add(col);
                }
                else
                {
                    int colon = right.IndexOf(':');
                    string quantity = colon < 0 ? right : right[..colon];
                    (import["Arrays"] ??= new JsonObject()).AsObject()[quantity] = left;
                    if (colon >= 0) (import["ArrayUnits"] ??= new JsonObject()).AsObject()[quantity] = right[(colon + 1)..];
                }
            }
            if (!Header && Format == ImportSettings.Csv) import["Header"] = false;
        }

        var conditions = new JsonObject();
        if (Num(AmbientC) is { } t) conditions["AmbientC"] = t;
        if (Plane.Length > 0) conditions["ReferencePlane"] = Plane;
        if (Num(QuiescentVgs) is { } qg && Num(QuiescentVds) is { } qd)
            conditions["Quiescent"] = new JsonObject { ["Vgs"] = qg, ["Vds"] = qd };
        foreach (string c in MoreConditions.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = c.IndexOf('=');
            if (eq <= 0) { why = $"'{c}' is not Key=Value."; return null; }
            var node = conditions;
            var keys = c[..eq].Trim().Split('.');
            foreach (string k in keys[..^1]) node = (node[k] ??= new JsonObject()).AsObject();
            string v = c[(eq + 1)..].Trim();
            node[keys[^1]] = Num(v) is { } n ? JsonValue.Create(n) : bool.TryParse(v, out bool b) ? JsonValue.Create(b) : JsonValue.Create(v);
        }

        var set = new JsonObject
        {
            ["Name"] = Name.Trim().Length == 0 ? ModelCommands.DefaultName(_doc, [.. Sources]) : Name.Trim(),
            ["Kind"] = Kind.ToString(),
            ["Sources"] = new JsonArray([.. Sources.Select(s => (JsonNode)s)]),
            ["Import"] = import,
        };
        if (conditions.Count > 0) set["Conditions"] = conditions;
        try { return CmcFile.ParseDataSet(set.ToJsonString()); }
        catch (CmcFormatException ex) { why = ex.Message; return null; }
    }

    private static double? Num(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
