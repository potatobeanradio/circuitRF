// brief-em3d-43 R-em3d43-6b — the Properties panel: the selection's fields in the document's display unit.
//
//   * an object: kind, material, role, placement, and each dimension;
//   * a face: its name, area and normal;
//   * a vertex: its coordinates.
//
// This brief makes an object's NAME, MATERIAL, ROLE and PLACEMENT editable; brief 47 adds a vertex's coordinates
// (Set Coordinates — the typed Vertex Move, one undo entry) and a face's perimeter, and the distance between two
// selected parallel faces (Face mode's Measure). Each edit is a typed edit — validated, and ONE undo entry — committed on Enter or when
// the field loses focus, never per keystroke. Every length follows the display unit at once (gate 10): the
// panel is reloaded when the unit changes, and it holds no length of its own.

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.ThreeD.Kernel;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.WBond;
using Point3 = CircuitRF.Engine.Em3d.Point3;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One read-only line of the panel; its value is selectable, so it can be copied.</summary>
public sealed record C3dPropertyRow(string Label, string Value);

/// <summary>brief-em3d-75 R-em3d75-1c — one of a thermal place's fields that is not a dimension: a heat source's power, a
/// probe's statistic or limit, a mesh region's grading. Its choices, when it has a fixed set.</summary>
public sealed partial class C3dThermalTextField : ObservableObject
{
    public required string Label { get; init; }
    public required string Key { get; init; }
    public string Hint { get; init; } = "";
    /// <summary>The values the field takes, when it takes one of a few (a lumped element's topology, a unit): a choice, not a
    /// text box. Null for a free field.</summary>
    public IReadOnlyList<string>? Choices { get; init; }
    public bool HasChoices => Choices is not null;
    [ObservableProperty] private string _text = "";
    internal string Loaded { get; set; } = "";
}

/// <summary>3D editor round 1 — one face of the air box in the Inspector: its boundary, editable, and its padding now.</summary>
public sealed partial class C3dAirBoxFaceRow(string face, Em3dBoundaryKind kind, string padding, Action<string, Em3dBoundaryKind> set)
    : ObservableObject
{
    public string Face { get; } = face;
    /// <summary>brief-dfr34-2 R-dfr34-2-5 — the padding as a length and the rule that set it, in a few words.</summary>
    public string Padding { get; } = padding;

    /// <summary>The same, with the rule in full.</summary>
    public string Tip { get; init; } = padding;
    [ObservableProperty] private Em3dBoundaryKind _kind = kind;

    partial void OnKindChanged(Em3dBoundaryKind value) => set(Face, value);
}

/// <summary>
/// brief-em3d-51 R-em3d51-4b — one named dimension of the selected object: its text (a number in the display unit, or an
/// expression) and its resolved value, editable as either. A resolution error shows here, red, with the engine's message;
/// the document keeps the text, and elaboration refuses with the same message.
/// </summary>
public sealed partial class C3dDimensionField : ObservableObject
{
    public required string Path { get; init; }
    /// <summary>3D editor round 3 — what the field is called on screen ("X size", "Radius"); the path is the file's name for it.</summary>
    public string Label { get; init; } = "";
    /// <summary>3D editor round 4 — the row the field shares with its sibling components ("Size"), and its own letter in
    /// that row ("x"); an empty axis is a field alone on its row.</summary>
    public string Group { get; init; } = "";
    public string Axis { get; init; } = "";
    public C3dFieldKind Kind { get; init; }
    public required string ValueText { get; init; }
    public string? Error { get; init; }
    public bool IsExpression { get; init; }
    [ObservableProperty] private string _text = "";
    internal string Loaded { get; set; } = "";
}

/// <summary>
/// 3D editor round 4 — one line of the Inspector's dimensions: a vector's components side by side ("Size: x [ ] y [ ] z [ ]")
/// or one field alone, with the unit its numbers are read in. What an expression resolves to and what refused it are
/// listed under the line, each naming its component.
/// </summary>
public sealed class C3dDimensionRow(string label, string unit, IReadOnlyList<C3dDimensionField> fields)
{
    public string Label { get; } = label;
    public string Unit { get; } = unit;
    public IReadOnlyList<C3dDimensionField> Fields { get; } = fields;

    public string Notes { get; } = string.Join("\n", fields.Where(f => f.ValueText.Length > 0)
                                                        .Select(f => (f.Axis.Length > 0 ? f.Axis + " " : "") + f.ValueText));

    public string Errors { get; } = string.Join("\n", fields.Where(f => !string.IsNullOrEmpty(f.Error))
                                                         .Select(f => (f.Axis.Length > 0 ? f.Axis + ": " : "") + f.Error));
}

/// <summary>
/// 3D editor round 3 — one point of a selected bond wire, its world coordinates editable in the display unit. The first
/// and last are the bonded ends: each sits on the top of its pad, so its z follows the pad.
/// <para>brief-em3d-133 R-em3d133-1 — each coordinate is a dimension field, a number or an expression, read, refused and
/// shown exactly as every other dimension is; what an expression resolves to and what refused it are listed under the row.</para>
/// </summary>
public sealed class C3dWirePointRow(IReadOnlyList<C3dDimensionField> fields)
{
    public required int Index { get; init; }
    public required string Label { get; init; }
    /// <summary>A point may be added between this one and the one before it — every row but the start.</summary>
    public bool CanAddAbove { get; init; }
    /// <summary>A point may be added between this one and the next — every row but the end.</summary>
    public bool CanAddBelow { get; init; }
    /// <summary>An interior point: the two ends are bonded to their pads and stay.</summary>
    public bool CanRemove { get; init; }
    public C3dDimensionField XField { get; } = fields[0];
    public C3dDimensionField YField { get; } = fields[1];
    public C3dDimensionField ZField { get; } = fields[2];
    public string X { get => XField.Text; set => XField.Text = value; }
    public string Y { get => YField.Text; set => YField.Text = value; }
    public string Z { get => ZField.Text; set => ZField.Text = value; }

    /// <summary>What each bound coordinate resolves to (<c>z = 254 µm</c>), and what refused one.</summary>
    public string Notes { get; } = string.Join("\n", fields.Where(f => f.ValueText.Length > 0).Select(f => f.Axis + " " + f.ValueText));
    public string Errors { get; } = string.Join("\n", fields.Where(f => !string.IsNullOrEmpty(f.Error)).Select(f => f.Axis + ": " + f.Error));
}

/// <summary>
/// One point of a polygon sheet's, a prism's or a polyline's outline in the Inspector's point list: its world coordinates in
/// the display unit, editable, and the length of the edge to the next point. The coordinate along a sheet's or prism's
/// normal is read-only when the plane is a world plane — the point stays on it; the Offset field moves the whole outline.
/// </summary>
public sealed class C3dOutlinePointRow
{
    public required int Index { get; init; }
    public required string Label { get; init; }
    public string X { get; set; } = "";
    public string Y { get; set; } = "";
    public string Z { get; set; } = "";
    public string LoadedX { get; init; } = "";
    public string LoadedY { get; init; } = "";
    public string LoadedZ { get; init; } = "";
    public bool XReadOnly { get; init; }
    public bool YReadOnly { get; init; }
    public bool ZReadOnly { get; init; }
    public string EdgeText { get; init; } = "";
    public bool Changed => X != LoadedX || Y != LoadedY || Z != LoadedZ;
}

/// <summary>A ring's heading in the point list — "Hole 1", between the outline's points and the hole's.</summary>
public sealed record C3dOutlineRingHeader(string Text);

public sealed partial class C3dPropertiesViewModel(C3dEditorViewModel editor) : ObservableObject
{
    /// <summary>The editor, for the partial declarations (a primary constructor's parameter is this declaration's alone).</summary>
    private C3dEditorViewModel Editor => editor;

    /// <summary>The Role choices: the material's own, or an override.</summary>
    public const string RoleFromMaterial = "From material";

    public static IReadOnlyList<string> Roles { get; } = [RoleFromMaterial, .. Enum.GetNames<Em3dRole>()];

    private bool _loading;

    /// <summary>The document index of the object being edited, or −1.</summary>
    public int ObjectIndex { get; private set; } = -1;

    public ObservableCollection<C3dPropertyRow> Rows { get; } = [];

    /// <summary>brief-em3d-51 — the selected object's named dimensions, editable as a number or an expression.</summary>
    public ObservableCollection<C3dDimensionField> Fields { get; } = [];

    /// <summary>3D editor round 4 — <see cref="Fields"/> as the Inspector lays them out: a vector's components on one line.</summary>
    public ObservableCollection<C3dDimensionRow> FieldRows { get; } = [];
    /// <summary>The technology's materials and, last, New Material… (brief-em3d-53 R-em3d53-5).</summary>
    public IReadOnlyList<string> Materials => editor.MaterialChoices;

    [ObservableProperty] private string _heading = "Nothing selected";
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string? _material;
    [ObservableProperty] private string? _role = RoleFromMaterial;

    /// <summary>What the Role combo shows when it holds no choice: a group whose members' roles differ.</summary>
    [ObservableProperty] private string _rolePlaceholder = "";

    /// <summary>3D editor groups — the selection is one group: its name, a material and a role for all of it, and its corner.</summary>
    [ObservableProperty] private bool _isGroup;
    private string _groupPath = "";

    /// <summary>The Material and Role combos' text when a group's members differ.</summary>
    public const string Various = "Various";
    [ObservableProperty] private string _rotateText = "";
    [ObservableProperty] private bool _mirrorX;
    [ObservableProperty] private string _error = "";

    /// <summary>3D editor round 3 — false for a bond wire, which has no placement (its points are world points).</summary>
    [ObservableProperty] private bool _isPlaced;
    [ObservableProperty] private string _materialPlaceholder = NoMaterialPlaceholder;

    private const string NoMaterialPlaceholder = "None — a solver needs one";

    // ── a bond wire (3D editor round 3) ─────────────────────────────────────────────────────

    [ObservableProperty] private bool _isWire;
    [ObservableProperty] private WireCrossSection _wireSection;
    [ObservableProperty] private BondStyle _wireStartStyle;
    [ObservableProperty] private BondStyle _wireEndStyle;

    /// <summary>3D editor round 4 — the wire's loop height (pad top to wire top, em-3d.md §6.6) and its foot-to-foot span in
    /// plan, in the display unit: the two a wBond or Layout wire offers, edited the same way.</summary>
    [ObservableProperty] private string _wireLoopHeight = "";
    [ObservableProperty] private string _wireSpan = "";
    private (string LoopHeight, string Span) _wireLoaded;

    /// <summary>brief-em3d-135 — what a held loop height or span resolves to (<c>loop height = 254 µm</c>), and what refused one;
    /// whether each field's expression is refused, for its red outline.</summary>
    [ObservableProperty] private string _wireShapeNotes = "";
    [ObservableProperty] private string _wireShapeErrors = "";
    [ObservableProperty] private bool _wireLoopHeightRefused;
    [ObservableProperty] private bool _wireSpanRefused;

    /// <summary>The selected wire's points, start to end.</summary>
    public ObservableCollection<C3dWirePointRow> WirePoints { get; } = [];

    // ── a polygon sheet's, a prism's or a polyline's points ─────────────────────────────────

    /// <summary>The outline's points (<see cref="C3dOutlinePointRow"/>), a <see cref="C3dOutlineRingHeader"/> before each hole's.</summary>
    public ObservableCollection<object> OutlinePoints { get; } = [];
    [ObservableProperty] private bool _hasOutlinePoints;
    [ObservableProperty] private string _outlinePointsTitle = "";

    // ── a boolean (brief-em3d-66 R-em3d66-4) ────────────────────────────────────────────────

    [ObservableProperty] private bool _isBoolean;
    [ObservableProperty] private CircuitRF.Design.ThreeD.C3dBooleanOp _booleanOperation;
    [ObservableProperty] private bool _booleanEnabled;
    [ObservableProperty] private bool _booleanKeepTools;
    [ObservableProperty] private string? _booleanBlank;
    [ObservableProperty] private bool _booleanCanSwap;
    /// <summary>False without the kernel: every boolean field is shown, disabled, with the capability's sentence.</summary>
    [ObservableProperty] private bool _booleanEditable;
    [ObservableProperty] private string? _booleanTip;
    /// <summary>False for a boolean: its material is its Blank's, changed on the Blank.</summary>
    [ObservableProperty] private bool _isMaterialEditable = true;

    /// <summary>The Blank combo's choices: the Blank (under the boolean's name), then each Tool.</summary>
    public ObservableCollection<string> BooleanOperands { get; } = [];

    public bool BooleanKeepToolsEnabled => BooleanEditable && BooleanOperation == CircuitRF.Design.ThreeD.C3dBooleanOp.Subtract;

    // ── a fillet or chamfer row (brief-em3d-67 R-em3d67-6b) ─────────────────────────────────

    [ObservableProperty] private bool _isFeature;
    [ObservableProperty] private bool _featureIsChamfer;
    [ObservableProperty] private bool _featureEnabled;
    /// <summary>False without the kernel: every field shown, disabled, with the capability's sentence.</summary>
    [ObservableProperty] private bool _featureEditable;
    [ObservableProperty] private string? _featureTip;
    [ObservableProperty] private bool _featureCanFlip;
    /// <summary>The feature's edges, by name.</summary>
    public ObservableCollection<string> FeatureEdges { get; } = [];
    private string _featurePath = "";

    /// <summary>The dimension lines: an object's, or a feature row's size fields.</summary>
    public bool FieldsVisible => IsEditable || IsFeature || IsThermalPlace || IsSymmetryPlane;

    /// <summary>brief-em3d-75 R-em3d75-1c — a heat source, probe or mesh region is shown: its name, its dimensions (through
    /// the same field editor as an object's, expressions included) and its other fields.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FieldsVisible))] private bool _isThermalPlace;

    private string _placeName = "";

    public ObservableCollection<C3dThermalTextField> ThermalFields { get; } = [];

    private void LoadThermalPlace(string name)
    {
        if (editor.ThermalPlace(name) is not { } item) { Heading = "Nothing selected"; return; }
        _placeName = name;
        IsThermalPlace = true;
        NameText = name;
        string kind = item switch { C3dHeatSource => "Heat source", C3dProbe => "Probe", C3dEffectiveBlock => "Effective block", _ => "Mesh region" };
        Heading = $"{kind} {name}";
        void Text(string label, string key, string? value, string hint = "")
            => ThermalFields.Add(new C3dThermalTextField { Label = label, Key = key, Text = value ?? "", Loaded = value ?? "", Hint = hint });
        switch (item)
        {
            case C3dHeatSource h:
                Rows.Add(new C3dPropertyRow("Spread over", h.Solid is { } s ? $"the solid {s} (volumetric)" : "a sheet on the drawing plane"));
                Text("Default power", "Power", h.Power, "W unless Density says otherwise; an expression sweeps");
                Text("Density", "Density", h.Density.ToString(), "Total, PerArea or PerVolume");
                if (h.Solid is not null) Text("Solid", "Solid", h.Solid);
                LoadHeatSourceModel(h);                                  // brief-em3d-93 D1
                break;
            case C3dProbe p:
                Rows.Add(new C3dPropertyRow("Reads", string.Join(", ", p.Kinds())));
                if (p.Face is not null) Text("Face", "Face", string.Join(", ", p.Face), "object/face, or several separated by commas (read as one)");
                if (p.Solid is not null) Text("Solid", "Solid", p.Solid);
                if (p.Wire is not null) Text("Wire", "Wire", p.Wire, "a wire, or one element: w1[3]");
                if (p.Spot is not null) Text("On face", "SpotFace", p.Spot.Face, "object/face the spot lies on");
                if (p.Point is null && p.Line is null) Text("Statistic", "Stat", p.Stat?.ToString(), "Max, Min or Avg");
                Text("Limit, °C", "LimitC", p.LimitC?.ToString("G6", CultureInfo.InvariantCulture), "flagged where crossed; empty for none");
                break;
            case C3dMeshRegion m:
                Text("Grading", "Grading", m.Grading?.ToString("G6", CultureInfo.InvariantCulture), "empty takes the setup's");
                break;
            case C3dEffectiveBlock b:
                // brief-em3d-76 — what it would replace, with its tensor, from the lowering a run would do
                Rows.Add(new C3dPropertyRow("Effect", editor.EffectiveBlockSummary(b)));
                // brief-em3d-93 D1 — a block's Enabled means what Model means elsewhere, so the row says Model: one word for it
                Text("Model", "Enabled", b.Enabled ? "true" : "false", "true replaces what is inside with one anisotropic block (an approximation)");
                break;
        }
        foreach (var f in editor.DimensionFields(item, name, path => C3dEditorViewModel.PlaceFieldLabel(item, path),
                                                 path => C3dEditorViewModel.PlaceFieldGroup(item, path)))
            Fields.Add(f);
        foreach (var r in RowsOf(Fields)) FieldRows.Add(r);
    }

    /// <summary>brief-sar-3 R-sar3-3 — the place page shows a lumped element: its Name and its text fields (the topology, each
    /// value with its unit, and Model), each commit one records entry through <see cref="C3dEditorViewModel.SetLumpedText"/>.</summary>
    private bool _placeIsLumped;

    private static readonly string[] LumpedTopologies = Enum.GetNames<Em3dRlcTopology>();

    private void LoadLumpedElement(string name)
    {
        if (editor.LumpedElement(name) is not { } le) { Heading = "Nothing selected"; return; }
        _placeName = name;
        _placeIsLumped = true;
        IsThermalPlace = true;
        NameText = name;
        Heading = $"Lumped element {name}";
        var r = editor.LumpedResults.FirstOrDefault(x => x.Element.Name == name);
        Rows.Add(new C3dPropertyRow("Gap", r?.Resolved is { } g
            ? $"current along {(Math.Abs(g.Direction.X) > 0.5 ? "x" : Math.Abs(g.Direction.Y) > 0.5 ? "y" : "z")}, found as a port's ends are"
            : r?.Refusal ?? "not resolved yet"));
        void Text(string label, string key, string? value, string hint, IReadOnlyList<string>? choices = null)
            => ThermalFields.Add(new C3dThermalTextField { Label = label, Key = key, Text = value ?? "", Loaded = value ?? "", Hint = hint, Choices = choices });
        Text("Topology", "Topology", le.Topology.ToString(), "R, L and C in parallel, or in series", LumpedTopologies);
        foreach (var (key, value, unit, what) in new[] { ("R", le.R, le.RUnit, "resistance"), ("L", le.L, le.LUnit, "inductance"), ("C", le.C, le.CUnit, "capacitance") })
        {
            var units = C3dEditorViewModel.LumpedUnits[key];
            Text(key, key, value, $"The {what}: a number or an expression (Ctune); empty removes it");
            Text($"{key} unit", key + "Unit", unit ?? (value is null ? null : units[0]), $"The {what}'s unit", units);
        }
        Text("Model", "Model", le.Model ? "true" : "false", "false leaves the gap as the geometry makes it in every run", ["true", "false"]);
    }

    /// <summary>R-em3d75-1c — a thermal place's text field committed: one undo entry, or the refusal and the field put back.</summary>
    public void CommitThermalField(C3dThermalTextField field)
    {
        if (!IsThermalPlace || field.Text == field.Loaded) return;
        if (_placeIsLumped)
        {
            Error = editor.SetLumpedText(_placeName, field.Key, field.Text) ?? "";
            if (Error.Length > 0) field.Text = field.Loaded;
            return;
        }
        Error = editor.SetPlaceText(_placeName, field.Key, field.Text) ?? "";
        if (Error.Length > 0) field.Text = field.Loaded;
    }
    partial void OnIsEditableChanged(bool value) => OnPropertyChanged(nameof(FieldsVisible));
    partial void OnIsFeatureChanged(bool value) => OnPropertyChanged(nameof(FieldsVisible));

    /// <summary>brief-em3d-47 R-em3d47-3e — the selected vertex's world coordinates, editable (Set Coordinates).</summary>
    [ObservableProperty] private bool _isVertexEditable;
    [ObservableProperty] private string _vertexX = "";
    [ObservableProperty] private string _vertexY = "";
    [ObservableProperty] private string _vertexZ = "";

    /// <summary>Raised when the Name field should take the caret (Rename… from a menu); the Inspector's view handles it.</summary>
    public event Action? RenameRequested;

    internal void RequestRename() => RenameRequested?.Invoke();

    /// <summary>Shows the current selection.</summary>
    public void Reload()
    {
        // brief-dfr32-3 R-dfr32-3-1 — a padding box typed in and not yet committed survives a reload of the air box: the scene of
        // the face committed just before it is adopted while the next one is being typed, and that reload wiped the typing,
        // so its Enter then read the old value back and stated nothing.
        var typed = IsAirBox ? PaddingTyped() : null;
        _loading = true;
        try
        {
            Load();
            if (typed is not null && IsAirBox)
                foreach (var (face, text) in typed) SetPadText(face, text);
        }
        finally { _loading = false; }
        EndStrayTransparencyPreview();
        EndStrayAppearancePreview();               // brief-em3d-108 — the Appearance group's drag, the same guard
        EndStrayFaceImagePreview();
        EndStrayPlotOffsetPreview();
        OnPropertyChanged(nameof(Materials));
        OnPropertyChanged(nameof(LengthUnit));
    }

    /// <summary>The display unit's suffix — what a bare length in the panel is read in.</summary>
    public string LengthUnit => LayoutUnits.Suffix(editor.Document.DisplayUnit);

    private void Load()
    {
        Rows.Clear();
        Fields.Clear();
        FieldRows.Clear();
        Error = "";
        ObjectIndex = -1;
        IsEditable = false;
        IsPlaced = false;
        IsWire = false;
        WirePoints.Clear();
        OutlinePoints.Clear();
        HasOutlinePoints = false;
        WireShapeNotes = WireShapeErrors = "";
        WireLoopHeightRefused = WireSpanRefused = false;
        MaterialPlaceholder = NoMaterialPlaceholder;
        IsVertexEditable = false;
        IsGroup = false;
        RolePlaceholder = "";
        IsBoolean = false;
        IsFeature = false;
        FeatureEdges.Clear();
        IsMaterialEditable = true;
        BooleanOperands.Clear();
        IsAirBox = false;
        IsAirBoxRow = false;
        AirBoxFaces.Clear();
        IsThermalPlace = false;
        IsPhantom = false;
        PhantomLayers.Clear();
        _placeIsLumped = false;
        ThermalFields.Clear();
        ClearFieldPlot();
        ClearRecords();
        ClearTransparency();
        ClearAppearanceGroup();
        ClearModel();
        ClearImage();
        ClearFaceImage();
        ClearPortPages();
        var viewer = editor.Viewer;
        var sel = viewer.Selection;
        // brief-em3d-67 R-em3d67-6b — a fillet's or chamfer's row: its own fields, not its object's.
        if (editor.SelectedTreeItem is { FeaturePath: { } fp, ObjectIndex: >= 0 and var ft } && ft < editor.Document.Objects.Count
            && (sel.Count == 0 || viewer.SelectMode == Scene3DSelectMode.Object))
        {
            LoadFeature(ft, fp);
            return;
        }
        // brief-em3d-67 R-em3d67-3f — edges: one's name, faces, kind, length (and a circle's radius and centre); several's
        // count and total length. No kernel is needed: a managed object's edges are its own.
        if (viewer.SelectMode == Scene3DSelectMode.Edge && sel.Count > 0 && sel.All(i => i.IsEdge))
        {
            LoadEdges(sel);
            return;
        }
        // brief-em3d-101 R-em3d101-9d — a face image picked in the view, or its row: the image's own section
        if (editor.SelectedFaceImageForInspector() is { } shownImage)
        {
            Heading = $"Image on {editor.Document.Objects[shownImage.Index].Name}/{shownImage.Face}";
            LoadFaceImage(shownImage.Index, shownImage.Face);
            return;
        }
        // brief-em3d-90 R-em3d90-4 — a symmetry plane's or a thermal boundary's row, or a boundary's tint picked in the view
        if (LoadRecord()) return;
        if (sel.Count == 0)
        {
            // 3D editor round 1 — the tree's node when the scene holds nothing selected: an object elaboration refused
            // (its fields are where the refusal is put right), or the air box.
            if (editor.TreeOnlyObjectIndex() is >= 0 and var only) { LoadObject(only, inScene: false); return; }
            if (editor.SelectedTreeItem is { IsGroup: true, GroupPath: { } undrawn }) { LoadGroup(undrawn); return; }
            // An instance's row when nothing of it is drawn (its reference resolves to nothing): its page all the same, which
            // names the reference — the thing to put right.
            if (SelectedInstanceRow() is { } missing) { LoadInstance(missing); return; }
            if (editor.SelectedTreeItem is { IsAirBox: true }) { LoadAirBox(); return; }
            // A drawn port's row with no sheet (a port that does not resolve): its page all the same — its place is what to fix.
            if (editor.SelectedTreeItem is { Kind: "Port" } portRow && editor.PortOfRow(portRow) is { } unresolved)
            {
                Heading = C3dPorts.Label(unresolved);
                LoadPortPage(unresolved);
                LoadPortModel([unresolved]);
                return;
            }
            // A component port's row, its sheet not drawn (a part left out of the run has none): its page all the same.
            if (editor.SelectedTreeItem is { Kind: C3dEditorViewModel.ComponentPortKind } componentRow && editor.ComponentPortOfRow(componentRow) is { } cp)
            {
                LoadComponentPortPage(cp);
                return;
            }
            // brief-em3d-101 — a face image's row when it is not drawn (Hidden): its section all the same
            if (editor.SelectedTreeItem is { Kind: C3dEditorViewModel.FaceImageKind, FaceImageHost: >= 0 and var fh, FaceImageFace: { } ff })
            {
                Heading = $"Image on {editor.Document.Objects[fh].Name}/{ff}";
                LoadFaceImage(fh, ff);
                return;
            }
            // brief-em3d-83 — a field plot's row: a record, in no scene.
            if (editor.SelectedTreeItem is { Kind: C3dEditorViewModel.FieldPlotKind } plotRow) { LoadFieldPlot(plotRow.Name); return; }
            // brief-sar-3 — a lumped element's row: a record, drawn in the overlay only.
            if (editor.SelectedTreeItem is { Kind: C3dEditorViewModel.LumpedElementKind } lumpedRow) { LoadLumpedElement(lumpedRow.Name); return; }
            // brief-em3d-75 — a heat source's, probe's or mesh region's row: the place is in no scene, only the tree.
            if (editor.SelectedTreeItem is { Kind: C3dEditorViewModel.HeatSourceKind or C3dEditorViewModel.ProbeKind or C3dEditorViewModel.MeshRegionKind or C3dEditorViewModel.EffectiveBlockKind } place)
            {
                LoadThermalPlace(place.Name);
                return;
            }
            Heading = "Nothing selected";
            return;
        }
        if (viewer.SelectMode == Scene3DSelectMode.Object &&
            sel.All(i => viewer.Scene.Object(i.Object) is { } b && C3dEditorViewModel.BoxFaceOf(b) is not null))
        {
            LoadAirBox();
            return;
        }
        if (sel.Count == 2 && viewer.SelectMode == Scene3DSelectMode.Face && sel[0].Face >= 0 && sel[1].Face >= 0) { TwoFaces(sel[0], sel[1]); return; }
        // 3D editor groups — a group selected whole is one thing here, however many objects it is.
        if (viewer.SelectMode == Scene3DSelectMode.Object && editor.SelectedGroupPath() is { } group) { LoadGroup(group); return; }
        // An instance's row selects every part it placed: that is the instance, and its page says what it places.
        if (viewer.SelectMode == Scene3DSelectMode.Object && SelectedInstanceRow() is { } whole && SelectsWholeInstance(sel, whole))
        {
            LoadInstance(whole);
            // An instance of one part is that part too: what it is called there, and its material.
            if (sel.Count == 1 && viewer.Scene.Object(sel[0].Object) is { } only)
            {
                if (editor.Elaboration?.Provenance.TryGetValue(only.Name, out var p) == true)
                    Rows.Add(new C3dPropertyRow("Its name there", p.ObjectName));
                if (only.Material is { } m) Rows.Add(new C3dPropertyRow("Material", m));
            }
            return;
        }
        // 3D editor round 4 — every element of one wire row selected (the tree selects them all) is that one wire.
        if (sel.Count > 1 && !OneDocumentObject(sel))
        {
            Heading = $"{sel.Count} selected";
            // brief-em3d-92 R-em3d92-2 — the one property a multi-selection edits here: every selected object and instance.
            // brief-em3d-93 — and their Model; several ports selected, theirs.
            if (viewer.SelectMode == Scene3DSelectMode.Object) LoadSelectionTransparency(sel);
            if (viewer.SelectMode == Scene3DSelectMode.Object && !HasModel) LoadPortModel(editor.SelectedPorts());
            return;
        }
        var item = sel[0];
        if (viewer.Scene.Object(item.Object) is not { } o) { Heading = "Nothing selected"; return; }
        Heading = viewer.Name(item);

        if (viewer.SelectMode == Scene3DSelectMode.Face && item.Face >= 0)
        {
            var (area, normal) = Scene3DFaces.AreaAndNormal(viewer.Scene, o.Id, item.Face);
            Rows.Add(new C3dPropertyRow("Face", o.FaceName(item.Face)));
            Rows.Add(new C3dPropertyRow("Area", AreaText(area)));
            if (Perimeter(o.Id, item.Face) is { } per) Rows.Add(new C3dPropertyRow("Perimeter", viewer.FormatLength(per)));
            Rows.Add(new C3dPropertyRow("Normal", normal is { } n ? $"({Num(n.X)}, {Num(n.Y)}, {Num(n.Z)})" : "varies (a curved face)"));
            Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
            // brief-em3d-101 R-em3d101-9d — the face's image, below its readout
            int host = editor.Document.Objects.FindIndex(d => d.Name == o.Name);
            if (editor.InstanceOf(o) is null && editor.FaceImageAt(host, o.FaceName(item.Face)) is not null) LoadFaceImage(host, o.FaceName(item.Face));
            return;
        }
        if (viewer.SelectMode == Scene3DSelectMode.Vertex)
        {
            // Set Coordinates: the vertex of one of this document's own objects, exactly where the document has it.
            if (editor.VertexSelection() is { Vertex: >= 0 } v)
            {
                IsVertexEditable = true;
                // Spelled losslessly, so a field left alone parses back to the same DBU and its lost focus moves nothing.
                string L(long dbu) => Tools.C3dDimension.Spell(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron);
                VertexX = L(v.World.X); VertexY = L(v.World.Y); VertexZ = L(v.World.Z);
            }
            var (x, y, z) = viewer.Scene.ToWorld(item.Point);
            Rows.Add(new C3dPropertyRow("x", viewer.FormatLength(x)));
            Rows.Add(new C3dPropertyRow("y", viewer.FormatLength(y)));
            Rows.Add(new C3dPropertyRow("z", viewer.FormatLength(z)));
            Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
            return;
        }

        if (editor.InstanceOf(o) is { } inst)
        {
            Rows.Add(new C3dPropertyRow("Instance", inst));
            if (editor.Elaboration?.Provenance.TryGetValue(o.Name, out var p) == true)
            {
                Rows.Add(new C3dPropertyRow("From", p.DocumentPath));
                Rows.Add(new C3dPropertyRow("Its name there", p.ObjectName));
            }
            if (o.Material is { } m) Rows.Add(new C3dPropertyRow("Material", m));
            Rows.Add(new C3dPropertyRow("Editing", "Read-only here: an instance's contents belong to its own cell. Its Transparency is " +
                                                   "the instance's, in this document: it multiplies onto each part's own. Its Appearance " +
                                                   "is the instance's too: each field it states applies to every part that states none."));
            // brief-em3d-92 — the INSTANCE's transparency is this document's, so it is edited here.
            if (InstanceIndex(inst) is >= 0 and var ii)
            {
                LoadTransparency([], [ii], editor.Document.Instances[ii].Name);
                LoadAppearance([], [ii], editor.Document.Instances[ii].Name, o);   // brief-em3d-108 — and its appearance
                LoadModel([], [ii], editor.Document.Instances[ii].Name);      // brief-em3d-93 — the instance's, in this document
            }
            return;
        }
        // A drawn port: its name, number, Z0 and place, and its Model. A component port: its own Z0 and width, the rest read-only.
        if (o.Kind == Scene3DKind.Port)
        {
            if (editor.PortOf(o) is { } drawn) LoadPortPage(drawn);
            else if (editor.ComponentPortOf(o) is { } component) { LoadComponentPortPage(component); return; }
            LoadPortModel(editor.SelectedPorts());
            return;
        }
        // brief-em3d-108 R-em3d108-2d — a setup's view writes nothing: its look, read-only
        if (editor.IsViewOnly) { LoadAppearanceReadOnly(o, ViewOnlyAppearance); return; }

        int index = editor.EditableIndex(o);
        if (index < 0) return;
        LoadObject(index, inScene: true);
    }

    /// <summary>The document index of the instance whose tree row is selected, or null.</summary>
    private int? SelectedInstanceRow()
        => editor.SelectedTreeItem is { InstanceIndex: >= 0 and var i, IsReadOnly: false } && i < editor.Document.Instances.Count ? i : null;

    /// <summary>Whether <paramref name="sel"/> is exactly the parts instance <paramref name="index"/> placed — the selection
    /// its row makes — rather than one part of it picked in the view.</summary>
    private bool SelectsWholeInstance(IReadOnlyList<Scene3DItem> sel, int index)
    {
        string name = editor.Document.Instances[index].Name;
        bool Mine(Scene3DObject o) => editor.InstanceOf(o) is { } path && InstanceIndex(path) == index;
        var picked = sel.Select(i => editor.Viewer.Scene.Object(i.Object)).ToList();
        if (picked.Count == 0 || picked.Any(o => o is null || !Mine(o))) return false;
        return picked.Distinct().Count() == editor.Viewer.Scene.Objects.Count(Mine);
    }

    /// <summary>A placed instance: the reference as written, the file it resolved to — or why it did not, which is shown
    /// for an instance that draws nothing as much as for one that draws — and the fields this document gives it.</summary>
    private void LoadInstance(int index)
    {
        var inst = editor.Document.Instances[index];
        Heading = inst.Name;
        var (file, problem) = editor.InstanceSource(inst);
        Rows.Add(new C3dPropertyRow("Places", inst.CellRef));
        Rows.Add(new C3dPropertyRow("From", file
            ?? (problem is null ? "Not resolved yet"
                : problem == "not found" ? "Not found" : $"Not drawn: {problem}")));
        LoadTransparency([], [index], inst.Name);
        LoadAppearance([], [index], inst.Name);
        LoadModel([], [index], inst.Name);
    }

    /// <summary>brief-em3d-92 — the index of the instance a part's instance path starts with (<c>U1[0,1,0]/U3</c> is U1's), or −1.</summary>
    private int InstanceIndex(string instancePath)
        => editor.Document.Instances.FindIndex(i => i.Name == instancePath.Split('/', '[')[0]);

    /// <summary>brief-em3d-92 — a multi-selection's Transparency row: each selected document object, and each instance a selected
    /// part belongs to.</summary>
    private void LoadSelectionTransparency(IReadOnlyList<Scene3DItem> sel)
    {
        var objects = new List<int>();
        var instances = new List<int>();
        foreach (var item in sel)
        {
            if (editor.Viewer.Scene.Object(item.Object) is not { } o) continue;
            if (editor.InstanceOf(o) is { } path) { if (InstanceIndex(path) is >= 0 and var ii) instances.Add(ii); continue; }
            if (editor.EditableIndex(o) is >= 0 and var i && !C3dEditorViewModel.IsOperandIndex(i)) objects.Add(i);
        }
        int n = objects.Distinct().Count() + instances.Distinct().Count();
        LoadTransparency(objects, instances, n == 1 ? "the selection" : $"{n} items");
        LoadAppearance(objects, instances, n == 1 ? "the selection" : $"{n} items");
        LoadModel(objects, instances, n == 1 ? "the selection" : $"{n} items");
    }

    private bool OneDocumentObject(IReadOnlyList<Scene3DItem> sel)
        => editor.Viewer.SelectMode == Scene3DSelectMode.Object &&
           sel.Select(i => editor.Viewer.Scene.Object(i.Object) is { } o ? editor.EditableIndex(o) : -1).Distinct().ToList() is [>= 0];

    /// <summary>A document object's editable fields — <paramref name="inScene"/> false when elaboration refused it, whose
    /// reason is then the first row.</summary>
    private void LoadObject(int index, bool inScene)
    {
        // brief-em3d-66 — an entered boolean's operand is edited through its operand index, in its world form.
        if (editor.ObjectAt(index) is not { } obj) return;
        bool operand = C3dEditorViewModel.IsOperandIndex(index);
        // A refused object is either not drawn at all, or — with a material its technology lacks — drawn as a wireframe
        // the solver never sees; either way the refusal Simulate will give is shown on the object it names. One with NO
        // material is drawn the same way and is not a refusal: the solver ignores it (3D editor bugs round 2), and this row
        // says so.
        string? why = editor.Elaboration?.Refusals.FirstOrDefault(r => r.Contains($"'{obj.Name}'", StringComparison.Ordinal))
                      ?? editor.Elaboration?.Warnings.FirstOrDefault(w => w == C3dElaborator.NoMaterialWarning(obj.Name));
        if (!inScene)
        {
            Heading = obj.Name;
            Rows.Add(new C3dPropertyRow("Not drawn", why ?? "It is not in the 3D model."));
        }
        else if (why is not null) Rows.Add(new C3dPropertyRow("Not simulated", why));
        ObjectIndex = index;
        IsEditable = true;
        // brief-em3d-92 — an operand inside an operation has none of its own: the operation's is its result's.
        if (!operand) LoadTransparency([index], [], obj.Name);
        if (!operand) LoadAppearance([index], [], obj.Name);                // brief-em3d-108
        else if (editor.AddressOf(index).Top is >= 0 and var top && top < editor.Document.Objects.Count)
            OperandLookNote = OperandLookText(editor.Document.Objects[top].Name, C3dValidation.EffectiveMaterial(obj));
        if (!operand) LoadModel([index], [], obj.Name);            // brief-em3d-93 — nor a Model: its result's is the operation's
        if (!operand && obj is C3dSheet { Image: not null } imageSheet) LoadImage(index, imageSheet);   // brief-em3d-101
        NameText = editor.ObjectLabel(index);
        Material = C3dValidation.EffectiveMaterial(obj);
        Role = obj.Role?.ToString() ?? RoleFromMaterial;
        var pl = obj.Placement;
        RotateText = string.Join(", ", pl.Rotate.Select(r => $"{r.Axis.ToString().ToLowerInvariant()} {r.Deg.ToString("G", CultureInfo.InvariantCulture)}"));
        MirrorX = pl.MirrorX;
        IsPlaced = obj is not C3dWire;
        if (obj is C3dWire wire) LoadWire(wire);
        Rows.Add(new C3dPropertyRow("Kind", C3dObject.KindOf(obj)));
        // brief-em3d-46 R-em3d46-4d — construction order is invisible unless it is shown: it decides overlap.
        if (!operand)
            Rows.Add(new C3dPropertyRow("Construction order",
                $"{index + 1} of {editor.Document.Objects.Count} — a later object wins where solids overlap (Modify ▸ Order)"));
        else
        {
            var (top, path) = editor.AddressOf(index);
            Rows.Add(new C3dPropertyRow("In", $"{(C3dBooleans.LastStep(path) < 0 ? "the Blank" : "a Tool")} of '{editor.Document.Objects[top].Name}'"));
        }
        if (obj is C3dBoolean b) { LoadBoolean(index, b); return; }
        foreach (var row in Dimensions(obj)) Rows.Add(row);
        if (obj is C3dSheet { Rect: null } or C3dPrism or C3dPolyline) LoadOutlinePoints(obj);
        foreach (var f in editor.DimensionFields(obj).Where(f => obj is not C3dPhantom ph || PhantomUses(ph, f.Path))) Fields.Add(f);
        foreach (var row in RowsOf(Fields)) FieldRows.Add(row);
        if (obj is C3dPhantom phantom && !operand) LoadPhantomLayers(index, phantom);
        if (obj is C3dVoxelBody body && !operand) LoadVoxelLabels(index, body);
    }

    // ── a voxel body's label table (brief-sar-11 R-sar11-1) ──────────────────────────────────

    /// <summary>The heading over <see cref="PhantomLayers"/>: a phantom's layers, or a voxel body's tissues.</summary>
    [ObservableProperty] private string _layersHeader = "Layers, outermost first";

    /// <summary>One combobox per mapped label, in table order — the rows a phantom's layers use; air rows are listed as text.</summary>
    private void LoadVoxelLabels(int index, C3dVoxelBody b)
    {
        IsPhantom = true;
        LayersHeader = "Tissues: label → material";
        IsMaterialEditable = false;
        Material = null;
        MaterialPlaceholder = "Each label names its own, below";
        Rows.Add(new C3dPropertyRow("File", b.File + (b.Raw is { } r ? $" (raw {r.Nx} × {r.Ny} × {r.Nz} {r.Type})" : "")));
        var air = b.Labels.Where(l => l.Air).Select(l => $"{l.Label} ({l.Name})").ToList();
        Rows.Add(new C3dPropertyRow("Air", air.Count == 0 ? "label 0" : "label 0, " + string.Join(", ", air)));
        if (b.Crop is { } c) Rows.Add(new C3dPropertyRow("Crop", $"voxels [{c.I0}, {c.I1}) × [{c.J0}, {c.J1}) × [{c.K0}, {c.K1})"));
        if (editor.Elaboration?.Solids.FirstOrDefault(s => s.Labels?.Body == b.Name)?.Labels is { } v)
            Rows.Add(new C3dPropertyRow("Voxels", $"{v.Nx} × {v.Ny} × {v.Nz} around the tissue, {v.Tissues.Count} tissue(s); openEMS only"));
        for (int i = 0; i < b.Labels.Count; i++)
        {
            if (b.Labels[i].Air) continue;
            int row = i;
            PhantomLayers.Add(new C3dPhantomLayerRow($"{b.Labels[i].Name} ({b.Labels[i].Label})", b.Labels[i].Material,
                m => Error = editor.SetVoxelLabelMaterial(index, row, m) ?? "",
                editor.Post,
                () => editor.RequestMaterialPicker([index], startNew: true,
                    new C3dMaterialPart(b.Labels[row].Material, m => editor.SetVoxelLabelMaterial(index, row, m)))));
        }
    }

    // ── a phantom's layers (brief-sar-3 R-sar3-2) ────────────────────────────────────────────

    /// <summary>One combobox per layer, outermost first: its material, from the technology's; a choice is one undo entry.</summary>
    public ObservableCollection<C3dPhantomLayerRow> PhantomLayers { get; } = [];

    [ObservableProperty] private bool _isPhantom;

    private void LoadPhantomLayers(int index, C3dPhantom p)
    {
        IsPhantom = true;
        LayersHeader = "Layers, outermost first";
        IsMaterialEditable = false;
        Material = null;
        MaterialPlaceholder = "Each layer names its own, below";
        Rows.Add(new C3dPropertyRow("Shape", $"{p.Shape}: {C3dPhantom.LayerCount(p.Shape)} layer(s), the innermost winning where they overlap"));
        for (int i = 0; i < p.Layers.Count; i++)
        {
            int layer = i;
            PhantomLayers.Add(new C3dPhantomLayerRow(p.Layers[i].Name, p.Layers[i].Material,
                m => Error = editor.SetPhantomLayerMaterial(index, layer, m) ?? "",
                editor.Post,
                () => editor.RequestMaterialPicker([index], startNew: true,
                    new C3dMaterialPart(p.Layers[layer].Material, m => editor.SetPhantomLayerMaterial(index, layer, m)))));
        }
    }

    /// <summary>Whether a phantom of its shape has the dimension at <paramref name="path"/>: only those are offered.</summary>
    public static bool PhantomUses(C3dPhantom p, string path)
    {
        string head = path.IndexOf('[') is >= 0 and var b ? path[..b] : path;
        bool three = C3dPhantom.LayerCount(p.Shape) == 3;
        return head switch
        {
            "SemiAxes" => p.Shape is C3dPhantomShape.Ellipsoid3 or C3dPhantomShape.Head,
            "Radius" => p.Shape is C3dPhantomShape.Cylinder3 or C3dPhantomShape.Sphere1 or C3dPhantomShape.Head,
            "Length" => p.Shape is C3dPhantomShape.Cylinder3 or C3dPhantomShape.Head,
            "Size" => p.Shape is C3dPhantomShape.FlatBox or C3dPhantomShape.Box1,
            "Shell1" => three || p.Shape is C3dPhantomShape.FlatBox or C3dPhantomShape.Head,
            "Shell2" => three,
            _ => true,
        };
    }

    /// <summary>
    /// 3D editor groups — a group: its Name; one Material and one Role for every member that has one (the combo reads
    /// Various while they differ); its Corner, which moves every member by the same step; and its Size, read from the
    /// members' box. A group has no placement of its own, so there is none to type.
    /// </summary>
    private void LoadGroup(string path)
    {
        var doc = editor.Document;
        string name = C3dGroups.NameOf(path);
        IsGroup = true;
        _groupPath = path;
        IsEditable = true;
        IsPlaced = false;
        Heading = $"Group '{name}'";
        NameText = name;
        var members = C3dGroups.MembersOf(doc, path);
        var solids = members.Where(m => !m.Instance).Select(m => doc.Objects[m.Index]).Where(o => o is not C3dPolyline).ToList();
        IsMaterialEditable = solids.Count > 0;
        var materials = solids.Select(o => o is C3dWire w ? C3dWires.MaterialOf(w) : C3dValidation.EffectiveMaterial(o)).Distinct().ToList();
        Material = materials.Count == 1 ? materials[0] : null;
        MaterialPlaceholder = materials.Count > 1 ? Various : solids.Count == 0 ? "None — the group holds no solid" : NoMaterialPlaceholder;
        var roles = solids.Select(C3dValidation.EffectiveRole).Distinct().ToList();
        Role = roles.Count == 1 ? roles[0]?.ToString() ?? RoleFromMaterial : null;
        RolePlaceholder = roles.Count > 1 ? Various : "";
        Rows.Add(new C3dPropertyRow("Kind", "Group"));
        // brief-em3d-92 R-em3d92-2 — one row for the whole group: every member at every depth, one undo entry.
        LoadTransparency(members.Where(m => !m.Instance).Select(m => m.Index), members.Where(m => m.Instance).Select(m => m.Index), name);
        LoadAppearance(members.Where(m => !m.Instance).Select(m => m.Index), members.Where(m => m.Instance).Select(m => m.Index), name);
        LoadModel(members.Where(m => !m.Instance).Select(m => m.Index), members.Where(m => m.Instance).Select(m => m.Index), name);
        int objects = members.Count(m => !m.Instance), instances = members.Count - objects;
        var subgroups = C3dGroups.All(doc).Count(g => g.Parent == path);
        Rows.Add(new C3dPropertyRow("Holds", string.Join(", ", new[] { (objects, "object"), (instances, "instance"), (subgroups, "group") }
                                                              .Where(t => t.Item1 > 0).Select(t => t.Item1 == 1 ? $"1 {t.Item2}" : $"{t.Item1} {t.Item2}s"))));
        if (C3dGroups.ParentOf(path) is { } parent) Rows.Add(new C3dPropertyRow("In", $"the group '{C3dGroups.NameOf(parent)}'"));
        if (editor.GroupBoundsDbu(path) is not { } b)
        {
            Rows.Add(new C3dPropertyRow("Corner", "None of it is drawn."));
            return;
        }
        string Spell(double dbu) => Tools.C3dDimension.Spell((long)Math.Round(dbu, MidpointRounding.AwayFromZero), doc.DisplayUnit, doc.DbuPerMicron);
        string Axis(int k) => k switch { 0 => "x", 1 => "y", _ => "z" };
        double[] corner = [b.X0, b.Y0, b.Z0];
        for (int k = 0; k < 3; k++)
        {
            string text = Spell(corner[k]);
            Fields.Add(new C3dDimensionField
            {
                Path = GroupCornerPath + k, Label = "Corner " + Axis(k), Group = "Corner", Axis = Axis(k), Kind = C3dFieldKind.Length,
                ValueText = "", Text = text, Loaded = text,
            });
        }
        foreach (var row in RowsOf(Fields)) FieldRows.Add(row);
        Rows.Add(new C3dPropertyRow("Size", $"{Spell(b.X1 - b.X0)} × {Spell(b.Y1 - b.Y0)} × {Spell(b.Z1 - b.Z0)} {LengthUnit}" +
                                            (b.Exact ? "" : " (≈)") + " — the members' box; each member keeps its own size"));
    }

    /// <summary>A group's Corner field's path: <c>Group.Corner[k]</c> is no field of any object.</summary>
    private const string GroupCornerPath = "Group.Corner";

    /// <summary>brief-em3d-67 R-em3d67-3f — what Properties shows for selected edges, every value selectable and copyable.</summary>
    private void LoadEdges(IReadOnlyList<Scene3DItem> sel)
    {
        var viewer = editor.Viewer;
        var edges = sel.Select(i => (Item: i, Edge: viewer.EdgeOf(i))).Where(t => t.Edge is not null).ToList();
        if (edges.Count == 0) { Heading = "Nothing selected"; return; }
        if (edges.Count > 1)
        {
            Heading = $"{edges.Count} edges";
            Rows.Add(new C3dPropertyRow("Total length", viewer.FormatLength(edges.Sum(t => t.Edge!.Value.Edge.Length))));
            var owners = edges.Select(t => viewer.Scene.Object(t.Item.Object)).OfType<CircuitRF.Render.Scene3D.Scene3DObject>().Distinct().ToList();
            Rows.Add(new C3dPropertyRow(owners.Count == 1 ? "Object" : "Objects", string.Join(", ", owners.Select(viewer.ObjectName))));
            Rows.Add(new C3dPropertyRow("Edges", string.Join("\n", edges.Select(t => t.Edge!.Value.Edge.Name))));
            return;
        }
        var (item, found) = edges[0];
        var (e, off) = found!.Value;
        Heading = viewer.Name(item);
        Rows.Add(new C3dPropertyRow("Edge", e.Name));
        Rows.Add(new C3dPropertyRow("Faces", e.FaceName1.Length > 0 ? $"{e.FaceName0}, {e.FaceName1}" : $"{e.FaceName0} (its rim)"));
        Rows.Add(new C3dPropertyRow("Kind", e.Kind.ToString().ToLowerInvariant() + (e.Closed && e.Kind != Scene3DEdgeKind.Circle ? " (closed)" : "")));
        Rows.Add(new C3dPropertyRow("Length", viewer.FormatLength(e.Length)));
        if (e.Kind is Scene3DEdgeKind.Circle or Scene3DEdgeKind.Arc && e.Centre is { } c)
        {
            Rows.Add(new C3dPropertyRow("Radius", viewer.FormatLength(e.Radius)));
            Rows.Add(new C3dPropertyRow("Centre", $"({viewer.FormatLength(c.X + off.X)}, {viewer.FormatLength(c.Y + off.Y)}, {viewer.FormatLength(c.Z + off.Z)})"));
        }
        if (viewer.Scene.Object(item.Object) is { } o) Rows.Add(new C3dPropertyRow("Object", viewer.ObjectName(o)));
    }

    /// <summary>
    /// brief-em3d-67 R-em3d67-6b — a fillet's or chamfer's row: Radius (or Distance, Distance 2 and Flip), Enabled, its edges
    /// (Show, Edit…) and Remove, each one undo entry, re-evaluated through the client. A failed evaluation is not rolled back:
    /// the refusal is shown here and on the row.
    /// </summary>
    private void LoadFeature(int top, string path)
    {
        var root = editor.Document.Objects[top];
        if (C3dFillets.At(root, path) is not C3dOperation f || !C3dFillets.IsFeature(f)) { Heading = "Nothing selected"; return; }
        IsFeature = true;
        ObjectIndex = top;
        _featurePath = path;
        FeatureIsChamfer = f is C3dChamfer;
        FeatureTip = editor.KernelMissing(f is C3dChamfer ? "Chamfer" : "Fillet");
        FeatureEditable = FeatureTip is null;
        FeatureEnabled = f.Enabled;
        FeatureCanFlip = f is C3dChamfer { Distance2: > 0 } && FeatureEditable;
        foreach (string e in C3dFillets.EdgesOf(f)) FeatureEdges.Add(e);
        Heading = $"{C3dFillets.RowLabel(f, dbu => LayoutUnits.Format(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron) + " " + LengthUnit)} of '{root.Name}'";
        if (editor.Elaboration?.KernelRefusals.TryGetValue(root.Name, out var why) == true) Rows.Add(new C3dPropertyRow("Refused", why));
        var (core, _) = C3dFillets.Core(root);
        Rows.Add(new C3dPropertyRow("Rounds", core is null ? root.Name : $"'{root.Name}' ({C3dObject.KindOf(core)})"));
        string[] own = f is C3dChamfer ? [nameof(C3dChamfer.Distance), nameof(C3dChamfer.Distance2)] : [nameof(C3dFillet.Radius)];
        foreach (var field in editor.DimensionFields(root).Where(x => own.Any(o => x.Path == path + o)))
            Fields.Add(field);
        foreach (var row in RowsOf(Fields)) FieldRows.Add(row);
    }

    partial void OnFeatureEnabledChanged(bool value)
    {
        if (_loading || !IsFeature) return;
        editor.SetFeatureEnabled(ObjectIndex, _featurePath, value);
    }

    /// <summary>Show: the feature's edges selected in the view (Edge mode).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ShowFeatureEdges() { if (IsFeature) editor.ShowFeatureEdges(ObjectIndex, _featurePath); }

    /// <summary>Edit…: the panel on the feature's edges; they are added and removed exactly as at creation.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void EditFeatureEdges() { if (IsFeature) Error = editor.EditFeature(ObjectIndex, _featurePath) ?? ""; }

    /// <summary>Remove: the feature unwrapped, its target back in its place with its name.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void RemoveFeature() { if (IsFeature) editor.RemoveFeature(ObjectIndex, _featurePath); }

    /// <summary>Flip (a chamfer at two distances): which face the first distance is measured on.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void FlipChamfer()
    {
        if (!IsFeature) return;
        int top = ObjectIndex;
        editor.ChangeFeature(top, _featurePath, "Flip the chamfer of " + editor.Document.Objects[top].Name, o =>
        {
            if (o is not C3dChamfer c) return;
            (c.Distance, c.Distance2) = (c.Distance2, c.Distance);
            var e1 = C3dBindings.GetExpr(c, nameof(C3dChamfer.Distance), 0);
            var e2 = C3dBindings.GetExpr(c, nameof(C3dChamfer.Distance2), 0);
            if (C3dBindings.Find(c, nameof(C3dChamfer.Distance)) is { } f1) C3dBindings.SetExpr(f1.Owner, f1.Spec, f1.Component, e2);
            if (C3dBindings.Find(c, nameof(C3dChamfer.Distance2)) is { } f2) C3dBindings.SetExpr(f2.Owner, f2.Spec, f2.Component, e1);
        });
    }

    /// <summary>
    /// brief-em3d-66 R-em3d66-4 — a boolean: its Operation, Enabled, Keep tools, its Blank (and Swap for two operands), the
    /// result's material — the Blank's, read-only here — and its piece count. Its own placement only, never its operands'
    /// fields (they are edited on the operands).
    /// </summary>
    private void LoadBoolean(int index, C3dBoolean b)
    {
        IsBoolean = true;
        IsMaterialEditable = false;
        BooleanTip = editor.KernelMissing("Boolean");
        BooleanEditable = BooleanTip is null;
        BooleanOperation = b.Op;
        BooleanEnabled = b.Enabled;
        BooleanKeepTools = b.KeepTools;
        string blankName = editor.ObjectLabel(index);
        BooleanOperands.Add(blankName);
        foreach (var t in b.Tools) BooleanOperands.Add(t.Name);
        BooleanBlank = blankName;
        BooleanCanSwap = b.Tools.Count == 1 && BooleanEditable;
        OnPropertyChanged(nameof(BooleanKeepToolsEnabled));
        string? material = C3dValidation.EffectiveMaterial(b);
        Rows.Add(new C3dPropertyRow("Material", (material is { Length: > 0 } ? material : "none") + " — the Blank's: change it on the Blank"));
        var build = editor.Elaboration?.KernelBuilds.FirstOrDefault(k => k.Name == blankName && k.Refusal is null);
        if (build is { Solids: > 0 }) Rows.Add(new C3dPropertyRow("Pieces", build.Solids == 1 ? "1" : $"{build.Solids} — one object"));
        foreach (var f in editor.DimensionFields(b).Where(f => !f.Path.StartsWith("Blank.", StringComparison.Ordinal)
                                                             && !f.Path.StartsWith("Tools[", StringComparison.Ordinal)))
            Fields.Add(f);
        foreach (var row in RowsOf(Fields)) FieldRows.Add(row);
    }

    partial void OnBooleanOperationChanged(C3dBooleanOp value)
    {
        OnPropertyChanged(nameof(BooleanKeepToolsEnabled));
        if (_loading || !IsBoolean || ObjectIndex < 0) return;
        int i = ObjectIndex;
        editor.SetBooleanSwitch(i, $"{value} {editor.ObjectLabel(i)}", b => { b.Op = value; if (value != C3dBooleanOp.Subtract) b.KeepTools = false; });
    }

    partial void OnBooleanEnabledChanged(bool value)
    {
        if (_loading || !IsBoolean) return;
        editor.SetOperationEnabled(ObjectIndex, value);
    }

    partial void OnBooleanKeepToolsChanged(bool value)
    {
        if (_loading || !IsBoolean) return;
        int i = ObjectIndex;
        editor.SetBooleanSwitch(i, $"{(value ? "Keep" : "Do not keep")} the Tools of {editor.ObjectLabel(i)}", b => b.KeepTools = value);
    }

    partial void OnBooleanBlankChanged(string? value)
    {
        if (_loading || !IsBoolean || value is null) return;
        int k = BooleanOperands.IndexOf(value) - 1;
        if (k < 0) return;
        var (top, path) = editor.AddressOf(ObjectIndex);
        Error = editor.MakeBlank(top, path, k) ?? "";
    }

    /// <summary>Swap (two operands): the Tool becomes the Blank.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SwapBoolean()
    {
        if (!IsBoolean || BooleanOperands.Count != 2) return;
        var (top, path) = editor.AddressOf(ObjectIndex);
        Error = editor.MakeBlank(top, path, 0) ?? "";
    }

    /// <summary>3D editor round 4 — consecutive components of one vector share a line; every other field has its own.</summary>
    private IEnumerable<C3dDimensionRow> RowsOf(IReadOnlyList<C3dDimensionField> fields)
    {
        for (int i = 0; i < fields.Count;)
        {
            var f = fields[i];
            int j = i + 1;
            if (f.Axis.Length > 0)
                while (j < fields.Count && fields[j].Axis.Length > 0 && fields[j].Group == f.Group) j++;
            string unit = f.Kind switch
            {
                C3dFieldKind.Length or C3dFieldKind.Microns => LayoutUnits.Suffix(editor.Document.DisplayUnit),
                _ => "",                                        // an angle's label says °; a count has no unit
            };
            yield return new C3dDimensionRow(f.Axis.Length > 0 ? f.Group : f.Label, unit, [.. fields.Skip(i).Take(j - i)]);
            i = j;
        }
    }

    /// <summary>
    /// 3D editor round 4 — the line a field sits on and its letter there: "Corner x" is ("Corner", "x"), "X size" is
    /// ("Size", "x"), "Placement origin z" is ("Placement origin", "z"). A field that is no component of a vector is
    /// (its label, "").
    /// </summary>
    public static (string Group, string Axis) FieldGroup(C3dObject obj, string path)
    {
        string label = FieldLabel(obj, path);
        if (label.Length == 6 && label.EndsWith(" size", StringComparison.Ordinal))
            return ("Size", label[..1].ToLowerInvariant());
        int space = label.LastIndexOf(' ');
        if (space > 0 && label.Length - space == 2 && "xyzuv".Contains(label[^1], StringComparison.Ordinal))
            return (label[..space], label[^1..]);
        return (label, "");
    }

    private void LoadWire(C3dWire wire)
    {
        IsWire = true;
        MaterialPlaceholder = $"{C3dWires.MaterialOf(wire)} (the default)";
        WireSection = C3dWires.SectionOf(wire);
        WireStartStyle = wire.Start.Style;
        WireEndStyle = wire.End.Style;
        string L(long dbu) => Tools.C3dDimension.Spell(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron);
        // brief-em3d-135 — a held field shows what holds it (an expression, with its value listed under the row); an unheld
        // one shows the shape's own number.
        var loop = editor.WireHeldField(wire, nameof(C3dWire.LoopHeight));
        var span = editor.WireHeldField(wire, nameof(C3dWire.Span));
        WireLoopHeight = loop?.Text ?? (editor.WireLoopHeightDbu(wire) is { } h ? L(h) : "");
        WireSpan = span?.Text ?? L(C3dEditorViewModel.WireSpanDbu(wire));
        _wireLoaded = (WireLoopHeight, WireSpan);
        (string, C3dDimensionField?)[] held = [("loop height", loop), ("span", span)];
        WireShapeNotes = string.Join("\n", held.Where(f => f.Item2 is { ValueText.Length: > 0 }).Select(f => $"{f.Item1} {f.Item2!.ValueText}"));
        WireShapeErrors = string.Join("\n", held.Where(f => !string.IsNullOrEmpty(f.Item2?.Error)).Select(f => $"{f.Item1}: {f.Item2!.Error}"));
        WireLoopHeightRefused = !string.IsNullOrEmpty(loop?.Error);
        WireSpanRefused = !string.IsNullOrEmpty(span?.Error);
        int last = wire.Points.Count - 1;
        for (int k = 0; k <= last; k++)
        {
            string label = k == 0 ? "Start" : k == last ? "End" : k.ToString(CultureInfo.InvariantCulture);
            WirePoints.Add(new C3dWirePointRow(editor.WirePointFields(wire, k))
            {
                Index = k, Label = label, CanAddAbove = k > 0, CanAddBelow = k < last, CanRemove = k > 0 && k < last,
            });
        }
    }

    /// <summary>The point list of a polygon sheet, a prism (its bottom) or a polyline: world coordinates, as the layout
    /// editor's Inspector lists a polygon's vertices.</summary>
    private void LoadOutlinePoints(C3dObject obj)
    {
        var doc = editor.Document;
        var world = editor.OutlineWorldPoints(obj);
        var rings = C3dFaceEditor.Rings(obj);
        if (world.Count == 0) return;
        string L(long dbu) => Tools.C3dDimension.Spell(dbu, doc.DisplayUnit, doc.DbuPerMicron);
        // Held to its plane: the normal's coordinate is the plane's, so it is shown and not typed — when the plane is still a
        // world plane after the placement (an unrotated object).
        int normal = C3dFaceEditor.PlaneOf(obj) is { } plane && obj.Placement.Rotate.Count == 0
            ? plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 } : -1;
        bool closed = obj is not C3dPolyline { Closed: false };
        int start = 0;
        for (int ring = 0; ring < rings.Count; ring++)
        {
            int n = rings[ring];
            if (ring > 0) OutlinePoints.Add(new C3dOutlineRingHeader($"Hole {ring}"));
            for (int k = 0; k < n; k++)
            {
                var p = world[start + k];
                string edge = "";
                if (k + 1 < n || (closed && n > 2))
                {
                    var q = world[start + (k + 1) % n];
                    double dx = q.X - p.X, dy = q.Y - p.Y, dz = q.Z - p.Z;
                    edge = LayoutUnits.Format((long)Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz)), doc.DisplayUnit, doc.DbuPerMicron);
                }
                string x = L(p.X), y = L(p.Y), z = L(p.Z);
                OutlinePoints.Add(new C3dOutlinePointRow
                {
                    Index = start + k, Label = (k + 1).ToString(CultureInfo.InvariantCulture),
                    X = x, Y = y, Z = z, LoadedX = x, LoadedY = y, LoadedZ = z,
                    XReadOnly = normal == 0, YReadOnly = normal == 1, ZReadOnly = normal == 2, EdgeText = edge,
                });
            }
            start += n;
        }
        HasOutlinePoints = true;
        OutlinePointsTitle = obj is C3dPrism ? $"Points (x, y, z) — its bottom, {LengthUnit}" : $"Points (x, y, z), {LengthUnit}";
    }

    /// <summary>A point's Enter or lost focus: three lengths in the display unit (a suffix may name another), the point moved
    /// there as one undo entry — or the refusal, the fields kept for correcting.</summary>
    public void CommitOutlinePoint(C3dOutlinePointRow row)
    {
        if (!HasOutlinePoints || ObjectIndex < 0 || !row.Changed) return;
        var doc = editor.Document;
        if (!LayoutUnits.TryParse(row.X, doc.DisplayUnit, doc.DbuPerMicron, out long x) ||
            !LayoutUnits.TryParse(row.Y, doc.DisplayUnit, doc.DbuPerMicron, out long y) ||
            !LayoutUnits.TryParse(row.Z, doc.DisplayUnit, doc.DbuPerMicron, out long z))
        {
            Error = $"A point is three lengths, in {LengthUnit} unless a unit is written.";
            return;
        }
        Error = editor.SetOutlinePoint(ObjectIndex, row.Index, new C3dPoint3(x, y, z)) ?? "";
    }

    /// <summary>3D editor round 3 — a wire point's Enter or lost focus: the point moved and the feet re-seated exactly as a
    /// Vertex-mode drag's are, as one undo entry — or the refusal, with the fields left for correcting. brief-em3d-133: each
    /// changed coordinate is a number or an expression; one left as loaded is not written.</summary>
    public void CommitWirePoint(C3dWirePointRow row)
    {
        C3dDimensionField[] fields = [row.XField, row.YField, row.ZField];
        if (!IsWire || ObjectIndex < 0 || fields.All(f => f.Text == f.Loaded)) return;
        if (fields.FirstOrDefault(f => f.Text != f.Loaded && RefusedName(f.Text)) is { } named) { named.Text = named.Loaded; return; }
        Error = editor.SetWirePoint(ObjectIndex, row.Index, [.. fields.Select(f => f.Text == f.Loaded ? null : f.Text)]) ?? "";
    }

    /// <summary>A point added between <paramref name="row"/> and the one before it (<paramref name="below"/> false) or after
    /// it, on the cubic through the points around them: one undo entry, or the refusal.</summary>
    public void AddWirePoint(C3dWirePointRow row, bool below)
    {
        if (!IsWire || ObjectIndex < 0) return;
        Error = editor.InsertWirePoint(ObjectIndex, below ? row.Index : row.Index - 1) ?? "";
    }

    /// <summary>An interior point taken out: one undo entry, or the refusal.</summary>
    public void RemoveWirePoint(C3dWirePointRow row)
    {
        if (!IsWire || ObjectIndex < 0) return;
        Error = editor.RemoveWirePoint(ObjectIndex, row.Index) ?? "";
    }

    /// <summary>3D editor round 4 — the loop height's Enter or lost focus: one undo entry, or the refusal.</summary>
    public void CommitWireLoopHeight()
    {
        if (!IsWire || ObjectIndex < 0 || WireLoopHeight == _wireLoaded.LoopHeight) return;
        if (RefusedName(WireLoopHeight)) { WireLoopHeight = _wireLoaded.LoopHeight; return; }
        Error = editor.SetWireLoopHeight(ObjectIndex, WireLoopHeight) ?? "";
    }

    /// <summary>3D editor round 4 — the span's Enter or lost focus: the end foot moves, one undo entry, or the refusal.</summary>
    public void CommitWireSpan()
    {
        if (!IsWire || ObjectIndex < 0 || WireSpan == _wireLoaded.Span) return;
        if (RefusedName(WireSpan)) { WireSpan = _wireLoaded.Span; return; }
        Error = editor.SetWireSpan(ObjectIndex, WireSpan) ?? "";
    }

    /// <summary>A typed dimension naming something nothing defines: the refusal shown, true — the caller puts the field's
    /// old text back, so the value it had is what it shows (the owner's rule, 2026-10-09).</summary>
    private bool RefusedName(string text)
    {
        if (editor.UndefinedNamesIn(text) is not { } why) return false;
        Error = why;
        return true;
    }

    partial void OnWireSectionChanged(WireCrossSection value) => ChangeWire("Section", w => w.Section = value);

    partial void OnWireStartStyleChanged(BondStyle value) => ChangeWire("Start bond", w => w.Start.Style = value);

    partial void OnWireEndStyleChanged(BondStyle value) => ChangeWire("End bond", w => w.End.Style = value);

    private void ChangeWire(string what, Action<C3dWire> mutate)
    {
        if (_loading || !IsWire || ObjectIndex < 0) return;
        int i = ObjectIndex;
        editor.ChangeObjects($"{what} of {editor.Document.Objects[i].Name}", [i], o => { if (o is C3dWire w) mutate(w); });
    }

    /// <summary>
    /// 3D editor round 3 — a dimension's name on screen, in the words of the object's own axes: a box's <c>Size[0]</c> is
    /// its "X size"; a sheet's <c>Rect.Size[1]</c> on the XZ plane is its "Z size". The path stays the file's name for it.
    /// </summary>
    public static string FieldLabel(C3dObject obj, string path)
    {
        static string Xyz(int k) => k switch { 0 => "x", 1 => "y", _ => "z" };
        static (string U, string V, string N) Axes(C3dPlane p) => p switch
        {
            C3dPlane.YZ => ("y", "z", "x"),
            C3dPlane.XZ => ("x", "z", "y"),
            _ => ("x", "y", "z"),
        };
        static string Up(string a) => a.ToUpperInvariant();
        int bracket = path.IndexOf('[');
        string head = bracket >= 0 ? path[..bracket] : path;
        int k = bracket >= 0 && bracket + 1 < path.Length && char.IsDigit(path[bracket + 1]) ? path[bracket + 1] - '0' : 0;
        if (head == "Placement.Rotate") return $"Rotation {k + 1} (°)";
        if (head == "Placement.Origin") return $"Placement origin {Xyz(k)}";
        switch (obj)
        {
            case C3dBox:
                if (head == "Min") return $"Corner {Xyz(k)}";
                if (head == "Size") return $"{Up(Xyz(k))} size";
                break;
            case C3dPrism pr:
            {
                var (u, v, n) = Axes(pr.Plane);
                if (head == "Offset") return $"Offset ({n})";
                if (head == "Height") return $"Height ({n})";
                if (head == "Shear") return $"Shear {(k == 0 ? u : v)}";
                break;
            }
            case C3dSheet sh:
            {
                var (u, v, n) = Axes(sh.Plane);
                if (head == "Offset") return $"Offset ({n})";
                if (head == "Rect.Min") return $"Corner {(k == 0 ? u : v)}";
                if (head == "Rect.Size") return $"{Up(k == 0 ? u : v)} size";
                if (head == "ThicknessUm") return "Thickness";
                break;
            }
            case C3dPolyline pl:
                if (head == "Offset") return $"Offset ({Axes(pl.Plane).N})";
                break;
            case C3dCylinder c:
                if (head == "Base") return $"Base {Xyz(k)}";
                if (head == "Length") return $"Length (along {c.Axis.ToString().ToLowerInvariant()})";
                if (head == "Radius") return "Radius";
                break;
            case C3dSphere:
                if (head == "Centre") return $"Centre {Xyz(k)}";
                if (head == "Radius") return "Radius";
                break;
            // brief-sar-8b — a sheet on a cylinder: its axis's base, radius and length, and the azimuth it starts at and sweeps.
            case C3dCylinderSheet cs:
                if (head == "Base") return $"Base {Xyz(k)}";
                if (head == "Length") return $"Length (along {cs.Axis.ToString().ToLowerInvariant()})";
                if (head == "Radius") return "Radius";
                if (head == "StartDeg") return "Start azimuth (°)";
                if (head == "SweepDeg") return "Sweep (°)";
                break;
            // brief-sar-3 — an ellipsoid's semi-axes along its own x, y and z; a phantom's dimensions by its shape.
            case C3dEllipsoid:
                if (head == "Centre") return $"Centre {Xyz(k)}";
                if (head == "SemiAxes") return $"Semi-axis {Xyz(k)}";
                break;
            case C3dPhantom ph:
                if (head == "Centre") return $"Centre {Xyz(k)}";
                if (head == "SemiAxes") return $"Semi-axis {Xyz(k)}";
                // brief-sar-12 — the generic head's ear spacers: a radius, and how far each stands off the head.
                if (head == "Radius") return ph.Shape == C3dPhantomShape.Head ? "Ear spacer radius" : "Radius";
                if (head == "Length") return ph.Shape == C3dPhantomShape.Head ? "Ear spacer thickness"
                                           : $"Length (along {(ph.Axis ?? C3dAxis.Z).ToString().ToLowerInvariant()})";
                if (head == "Size") return $"{Up(Xyz(k))} size";
                if (head == "Shell1") return ph.Shape is C3dPhantomShape.FlatBox or C3dPhantomShape.Head ? "Shell" : "Outer shell";
                if (head == "Shell2") return "Second shell";
                if (head == "Voxel") return "Voxel size";
                break;
            case C3dVoxelBody:
                if (head == "GridStep") return "Grid step (0: a line per voxel)";
                break;
            case C3dWire:
                if (head == "DiameterUm") return "Diameter";
                // 3D editor round 4 — a wire row.
                if (head == "Array.Count") return "Number of wires";
                if (head == "Array.Pitch") return $"Pitch {Xyz(k)}";
                break;
        }
        return path;
    }

    // ── the air box (3D editor round 1) ─────────────────────────────────────────────────────

    /// <summary>The active setup's air box is what is shown: its padding per axis and each face's boundary.</summary>
    [ObservableProperty] private bool _isAirBox;

    /// <summary>3D editor round 3 — the air box is what is shown, with or without a setup: its material is editable either way.</summary>
    [ObservableProperty] private bool _isAirBoxRow;

    /// <summary>3D editor round 3 — the material that fills the air box (Air by default); a choice is one undo entry.</summary>
    [ObservableProperty] private string? _airBoxMaterial;

    public IReadOnlyList<string> AirBoxMaterials => editor.AirBoxMaterialChoices;

    partial void OnAirBoxMaterialChanged(string? value)
    {
        if (_loading || !IsAirBoxRow || value is null) return;
        Error = editor.SetAirBoxMaterial(value) ?? "";
    }
    // One padding box per face: the first row the low faces, the second the high ones.
    [ObservableProperty] private string _padXMinPercent = "";
    [ObservableProperty] private string _padXMaxPercent = "";
    [ObservableProperty] private string _padYMinPercent = "";
    [ObservableProperty] private string _padYMaxPercent = "";
    [ObservableProperty] private string _padZMinPercent = "";
    [ObservableProperty] private string _padZMaxPercent = "";
    // Designer round 33 — each padding box's tooltip: the length it stands for and, for a default, the rule that set it.
    // The face rows used to print this beside each boundary picker, which was more text than the panel could carry.
    [ObservableProperty] private string _padXMinTip = "";
    [ObservableProperty] private string _padXMaxTip = "";
    [ObservableProperty] private string _padYMinTip = "";
    [ObservableProperty] private string _padYMaxTip = "";
    [ObservableProperty] private string _padZMinTip = "";
    [ObservableProperty] private string _padZMaxTip = "";
    private readonly Dictionary<string, string> _padLoaded = [];

    /// <summary>One row per face: its boundary (editable) and the padding it has now.</summary>
    public ObservableCollection<C3dAirBoxFaceRow> AirBoxFaces { get; } = [];

    public static IReadOnlyList<Em3dBoundaryKind> BoundaryKinds { get; } = Enum.GetValues<Em3dBoundaryKind>();

    private void LoadAirBox()
    {
        IsAirBox = true;
        IsAirBoxRow = true;
        AirBoxMaterial = C3dProblemAssembly.BoxFill(editor.Document);
        OnPropertyChanged(nameof(AirBoxMaterials));
        // No setup active: the document's own box, drawn on a new setup's defaults — sized and walled here all the same, and
        // what a setup added later starts with.
        var active = editor.ActiveSetup;
        var stated = active is not null ? active.AirBox : EmSetupPersistence.FromCem(editor.Document.AirBox);
        var setup = active ?? new EmSetup { Solver3D = CircuitRF.Design.Em3d.Em3dSolvers.Default, AirBox = stated };
        Heading = "Air box";
        if (editor.ShownAirBox is not { } box)
        {
            Rows.Add(new C3dPropertyRow("Setup", active is null
                ? "The box is drawn round the geometry: it appears with the first solid or sheet."
                : "The active setup's air box could not be built around this geometry; Simulate ▸ Run reports why."));
            IsAirBox = false;
            return;
        }
        Rows.Add(new C3dPropertyRow("Setup", active is null ? "None active: a setup added now starts with this box" : editor.ActiveSetupLabel));
        // brief-dfr34-2 R-dfr34-2-5 — each box shows the padding in use as a LENGTH in the display unit (a percentage of a
        // 1.5 mm z extent read 9986 %, which nobody can judge); a face that states a percentage shows it with its % sign, and
        // typing "10 %" states one.
        string Pct(string face, EmAirBoxFace? s)
            => s?.PaddingPercent is { } p ? p.ToString("G6", CultureInfo.InvariantCulture) + " %" : editor.AirBoxPaddingShort(face);
        _padLoaded["xmin"] = PadXMinPercent = Pct("xmin", stated?.XMin);
        _padLoaded["xmax"] = PadXMaxPercent = Pct("xmax", stated?.XMax);
        _padLoaded["ymin"] = PadYMinPercent = Pct("ymin", stated?.YMin);
        _padLoaded["ymax"] = PadYMaxPercent = Pct("ymax", stated?.YMax);
        _padLoaded["zmin"] = PadZMinPercent = Pct("zmin", stated?.ZMin);
        _padLoaded["zmax"] = PadZMaxPercent = Pct("zmax", stated?.ZMax);
        var f = box.Faces;
        string DefaultBasis(string face) => editor.Elaboration is { } elab && C3dProblemAssembly.DefaultPaddingBasis(setup, elab, face) is { } basis
            ? $"default: {basis}" : "default";
        string DefaultShort(string face) => editor.Elaboration is { } elab && C3dProblemAssembly.DefaultPaddingShortBasis(setup, elab, face) is { } brief
            ? brief : "default";
        foreach (var (face, kind, s) in new[]
                 {
                     ("xmin", f.XMin, stated?.XMin), ("xmax", f.XMax, stated?.XMax), ("ymin", f.YMin, stated?.YMin),
                     ("ymax", f.YMax, stated?.YMax), ("zmin", f.ZMin, stated?.ZMin), ("zmax", f.ZMax, stated?.ZMax),
                 })
        {
            // brief-dfr34-2 R-dfr34-2-5 — the length first, then the rule that set it ("150 mm — λ/4 at 0.5 GHz", "0.69 mm — 10 × H");
            // a stated percentage keeps its share as the reason. The tooltip carries the rule in full.
            string len = editor.AirBoxPaddingM(face) is { } m ? CircuitRF.Render.Em3dSectionScene.FormatLength(m) : "";
            string pad, tip;
            if (s?.PaddingPercent is { } stPct)
            {
                pad = $"{len} — {stPct.ToString("G6", CultureInfo.InvariantCulture)} % of the extent, stated";
                tip = pad;
            }
            else if (s?.PaddingUm is not null)
                pad = tip = $"{len} — stated";
            else
            {
                pad = $"{len} — {DefaultShort(face)}";
                tip = $"{len} — {DefaultBasis(face)}";
            }
            // Designer round 33 — a floor on an unbroken bottom ground plane has no padding rule to explain.
            if (face == "zmin" && s is null && kind == Em3dBoundaryKind.Pec)
            {
                pad = "0 — floor on the bottom plane";
                tip = "0 — on the underside of the ground plane at the bottom, with nothing of another net in its holes: nothing below it carries " +
                      "field. Type a padding to put air below it instead.";
            }
            switch (face)
            {
                case "xmin": PadXMinTip = tip; break;
                case "xmax": PadXMaxTip = tip; break;
                case "ymin": PadYMinTip = tip; break;
                case "ymax": PadYMaxTip = tip; break;
                case "zmin": PadZMinTip = tip; break;
                default:     PadZMaxTip = tip; break;
            }
            AirBoxFaces.Add(new C3dAirBoxFaceRow(face, kind, pad, (fc, k) => editor.Post(() => Error = editor.SetAirBoxBoundary(fc, k) ?? ""))
                            { Tip = tip });
        }
        Rows.Add(new C3dPropertyRow("Size", $"{editor.Viewer.FormatLength(box.Max.X - box.Min.X)} × " +
                                            $"{editor.Viewer.FormatLength(box.Max.Y - box.Min.Y)} × {editor.Viewer.FormatLength(box.Max.Z - box.Min.Z)}"));
        Rows.Add(new C3dPropertyRow("Editing", active is null
            ? "The air box is not geometry: it cannot be deleted or duplicated."
            : "The air box is the active setup's, not the geometry's: it cannot be deleted or duplicated."));
    }

    private static readonly string[] BoxFaces = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];

    private string PadText(string face) => face switch
    {
        "xmin" => PadXMinPercent, "xmax" => PadXMaxPercent, "ymin" => PadYMinPercent,
        "ymax" => PadYMaxPercent, "zmin" => PadZMinPercent, _ => PadZMaxPercent,
    };

    private void SetPadText(string face, string text)
    {
        switch (face)
        {
            case "xmin": PadXMinPercent = text; break;
            case "xmax": PadXMaxPercent = text; break;
            case "ymin": PadYMinPercent = text; break;
            case "ymax": PadYMaxPercent = text; break;
            case "zmin": PadZMinPercent = text; break;
            default: PadZMaxPercent = text; break;
        }
    }

    /// <summary>brief-dfr32-3 — the padding boxes whose text differs from what was loaded into them: typed, not yet committed.</summary>
    private List<(string Face, string Text)> PaddingTyped()
        => [.. BoxFaces.Where(f => _padLoaded.TryGetValue(f, out string? loaded) && PadText(f) != loaded).Select(f => (f, PadText(f)))];

    /// <summary>A face's padding — Enter or lost focus: a length in the display unit (a suffix may name another), or a share of
    /// the content's extent along its axis when it ends in % ("10 %"); empty keeps the face as it is. One undo entry.</summary>
    public void CommitAirBoxPercent(string face)
    {
        if (!IsAirBox) return;
        string text = PadText(face);
        // Unchanged (focus passing through) states nothing: a default stays a default.
        if (string.IsNullOrWhiteSpace(text) || text.Trim() == _padLoaded.GetValueOrDefault(face)?.Trim()) return;
        Error = (text.TrimEnd().EndsWith('%') ? editor.SetAirBoxPaddingPercent(face, text) : editor.SetAirBoxPadding(face, text)) ?? "";
    }

    /// <summary>A dimension's Enter or lost focus: a number replaces the expression; an expression is bound (and kept even
    /// when it does not resolve — the field then says why). One undo entry.</summary>
    public void CommitField(C3dDimensionField field)
    {
        if (field.Text != field.Loaded && RefusedName(field.Text)) { field.Text = field.Loaded; return; }
        if (IsGroup && field.Path.StartsWith(GroupCornerPath, StringComparison.Ordinal))
        {
            if (field.Text != field.Loaded) Error = editor.SetGroupCorner(_groupPath, field.Path[^1] - '0', field.Text) ?? "";
            return;
        }
        if (IsThermalPlace)
        {
            if (field.Text != field.Loaded) Error = editor.SetPlaceFieldText(_placeName, field.Path, field.Text) ?? "";
            return;
        }
        // brief-em3d-90 — a symmetry plane's At: refused off the model's extent, with the rule's own sentence
        if (IsSymmetryPlane)
        {
            if (field.Text != field.Loaded) Error = editor.SetSymmetryPlaneAt(_planeAxis, field.Text) ?? "";
            if (Error.Length > 0) field.Text = field.Loaded;
            return;
        }
        if (ObjectIndex < 0 || field.Text == field.Loaded) return;
        Error = editor.SetFieldText(ObjectIndex, field.Path, field.Text) ?? "";
    }

    /// <summary>A face's perimeter (metres): the scene's own feature edges around it, which are the solid's edges, not its
    /// triangles'. Null when the face has none (a sweep names no faces).</summary>
    private double? Perimeter(uint id, int face)
    {
        if (editor.Viewer.Scene.FeaturesOf(id) is not { Table: { } t } fr || face >= t.FaceCount) return null;
        double p = 0;
        for (int k = t.FaceEdgeStart[face]; k < t.FaceEdgeStart[face + 1]; k++)
        {
            int e = t.FaceEdges[k];
            var a = fr.Vertex(t.EdgeA[e]);
            var b = fr.Vertex(t.EdgeB[e]);
            p += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        }
        return p > 0 ? p : null;
    }

    /// <summary>brief-em3d-47 R-em3d47-4 — Measure's second face: the distance between two parallel faces, or that they are not.</summary>
    private void TwoFaces(Scene3DItem a, Scene3DItem b)
    {
        var viewer = editor.Viewer;
        var scene = viewer.Scene;
        if (scene.Object(a.Object) is not { } oa || scene.Object(b.Object) is not { } ob) { Heading = "2 selected"; return; }
        Heading = "2 faces";
        Rows.Add(new C3dPropertyRow("First", viewer.Name(a)));
        Rows.Add(new C3dPropertyRow("Second", viewer.Name(b)));
        var (_, na) = Scene3DFaces.AreaAndNormal(scene, oa.Id, a.Face);
        var (_, nb) = Scene3DFaces.AreaAndNormal(scene, ob.Id, b.Face);
        if (na is not { } n1 || nb is not { } n2) { Rows.Add(new C3dPropertyRow("Distance", "A curved face has no single plane.")); return; }
        var c = System.Numerics.Vector3.Cross(n1, n2);
        if (c.Length() > 1e-5f) { Rows.Add(new C3dPropertyRow("Distance", "Not parallel.")); return; }
        if (Corner(oa.Id, a.Face) is not { } pa || Corner(ob.Id, b.Face) is not { } pb) return;
        double d = Math.Abs(n1.X * (pb.X - pa.X) + n1.Y * (pb.Y - pa.Y) + n1.Z * (pb.Z - pa.Z));
        Rows.Add(new C3dPropertyRow("Distance", viewer.FormatLength(d)));
        Rows.Add(new C3dPropertyRow("Facing", System.Numerics.Vector3.Dot(n1, n2) < 0 ? "each other" : "the same way"));
    }

    private Point3? Corner(uint id, int face)
        => editor.Viewer.Scene.FeaturesOf(id) is { Table: { } t } fr && face < t.FaceCount && t.FaceVertexStart[face] < t.FaceVertexStart[face + 1]
            ? fr.Vertex(t.FaceVertices[t.FaceVertexStart[face]]) : null;

    /// <summary>brief-em3d-47 R-em3d47-3e — Set Coordinates' Enter or lost focus: three lengths in the display unit (a suffix
    /// may name another), the vertex moved there as one undo entry — or the refusal, and the fields put back.</summary>
    public void CommitVertex()
    {
        if (!IsVertexEditable) return;
        var doc = editor.Document;
        if (!LayoutUnits.TryParse(VertexX, doc.DisplayUnit, doc.DbuPerMicron, out long x) ||
            !LayoutUnits.TryParse(VertexY, doc.DisplayUnit, doc.DbuPerMicron, out long y) ||
            !LayoutUnits.TryParse(VertexZ, doc.DisplayUnit, doc.DbuPerMicron, out long z))
        {
            Error = $"A vertex is three lengths, in {LayoutUnits.Suffix(doc.DisplayUnit)} unless a unit is written.";
            return;
        }
        if (editor.SetVertexCoordinates(new C3dPoint3(x, y, z)) is { } why) { Error = why; return; }
        Error = "";
    }

    // ── read-only dimensions, per kind ──────────────────────────────────────────────────────

    /// <summary>What the editable fields do not already show (3D editor round 4: a corner, a size, a radius were listed
    /// twice — once to edit and once to read).</summary>
    private IEnumerable<C3dPropertyRow> Dimensions(C3dObject obj)
    {
        switch (obj)
        {
            case C3dPrism p:
                yield return new("Plane", p.Plane.ToString());
                yield return new("Outline", $"{p.Outline.Count} points" + (p.Holes.Count > 0 ? $", {p.Holes.Count} hole(s)" : ""));
                break;
            case C3dCylinder c:
                yield return new("Axis", c.Axis.ToString());
                break;
            case C3dCylinderSheet cs:
                yield return new("Axis", cs.Axis.ToString());
                yield return new("Solved by", "openEMS's cylindrical grid about the same axis only; every other solver refuses it");
                break;
            case C3dSphere sp:
                // brief-em3d-102 R-em3d102-6d — read-only: the Radius is the field to edit.
                yield return new("Diameter", LayoutUnits.Format(2 * sp.Radius, editor.Document.DisplayUnit, editor.Document.DbuPerMicron) + " " + LengthUnit);
                break;
            case C3dSheet s:
                yield return new("Plane", s.Plane.ToString());
                if (s.Rect is null) yield return new("Outline", $"{s.Outline.Count} points" + (s.Holes.Count > 0 ? $", {s.Holes.Count} hole(s)" : ""));
                break;
            case C3dPolyline l:
                yield return new("Plane", l.Plane.ToString());
                yield return new("Points", l.VertexCount + (l.Closed ? ", closed" : "") + (l.Points3 is not null ? ", leaving its plane (stored in 3D)" : ""));
                yield return new("In the problem", "No: a polyline is construction geometry.");
                break;
            case C3dPolyhedron ph:
                yield return new("Vertices", ph.Vertices.Count.ToString(CultureInfo.InvariantCulture));
                yield return new("Faces", ph.Faces.Count.ToString(CultureInfo.InvariantCulture));
                break;
            case C3dMesh m:
                // brief-sar-11 — an imported surface: its file and unit; openEMS solves it, Palace does not.
                yield return new("File", $"{m.File} ({m.Unit}{(m.Flip ? ", turned round" : "")})");
                yield return new("Solved by", "openEMS only (a PolyhedronReader); Palace and the thermal solver refuse it");
                break;
        }
    }

    private static string Num(float v) => (MathF.Abs(v) < 5e-7f ? 0f : v).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>An area in the display unit squared.</summary>
    private string AreaText(double m2)
    {
        var unit = editor.Document.DisplayUnit;
        // One display unit in metres, from the DBU conversion itself: 1 unit = FromDbu⁻¹ …
        long dbuPerUnit = LayoutUnits.ToDbu(1m, unit, editor.Document.DbuPerMicron);
        double metresPerUnit = dbuPerUnit * 1e-6 / editor.Document.DbuPerMicron;
        double v = m2 / (metresPerUnit * metresPerUnit);
        return v.ToString("G6", CultureInfo.InvariantCulture) + " " + LayoutUnits.Suffix(unit) + "²";
    }

    // ── commits: one typed edit, one undo entry, validated ──────────────────────────────────

    /// <summary>The Name field's Enter or lost focus.</summary>
    public void CommitName()
    {
        if (IsFieldPlot)
        {
            if (NameText.Trim() == _plotName) return;
            Error = editor.RenameFieldPlot(_plotName, NameText) ?? "";
            if (Error.Length > 0) NameText = _plotName;
            return;
        }
        if (IsThermalPlace)
        {
            if (NameText.Trim() == _placeName) return;
            Error = (_placeIsLumped ? editor.RenameLumpedElement(_placeName, NameText) : editor.RenameThermalPlace(_placeName, NameText)) ?? "";
            if (Error.Length > 0) NameText = _placeName;
            return;
        }
        if (IsGroup)
        {
            Error = editor.RenameGroup(_groupPath, NameText) ?? "";
            if (Error.Length > 0) NameText = C3dGroups.NameOf(_groupPath);
            return;
        }
        if (ObjectIndex < 0) return;
        Error = editor.Rename(ObjectIndex, NameText) ?? "";
        if (Error.Length > 0) NameText = editor.ObjectLabel(ObjectIndex);
    }

    /// <summary>Materials editor redesign (2026-09-29) — the Materials dialog on this object's material (or the group's
    /// solids'), where every property of it and of the technology's other materials is shown and edited.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void EditMaterials()
    {
        if (!IsMaterialEditable) return;
        if (IsGroup) editor.RequestMaterialPicker(editor.GroupSolidIndices(_groupPath), startNew: false);
        else if (ObjectIndex >= 0) editor.RequestMaterialPicker([ObjectIndex], startNew: false);
    }

    partial void OnMaterialChanged(string? value)
    {
        if (_loading || value is null) return;
        if (IsGroup)
        {
            if (value == C3dEditorViewModel.NewMaterialItem) { _loading = true; Material = null; _loading = false; }
            editor.SetGroupMaterial(_groupPath, value);
            return;
        }
        if (ObjectIndex < 0) return;
        int i = ObjectIndex;
        if (value == C3dEditorViewModel.NewMaterialItem)
        {
            // A command, not a material: the picker opens on a new row; the field keeps what the object states.
            _loading = true;
            Material = editor.ObjectAt(i) is { } was ? C3dValidation.EffectiveMaterial(was) : null;
            _loading = false;
            editor.RequestMaterialPicker([i], startNew: true);
            return;
        }
        if (!IsMaterialEditable) return;
        editor.ChangeObjects($"Material of {editor.ObjectLabel(i)}", [i], o => C3dEditorViewModel.SetMaterialOf(o, value));
    }

    partial void OnRoleChanged(string? value)
    {
        // A combo cleared by its items (Various, which is no choice) writes null back: that is not an edit.
        if (_loading || value is null) return;
        Em3dRole? role = Enum.TryParse<Em3dRole>(value, out var r) ? r : null;
        if (IsGroup) { editor.SetGroupRole(_groupPath, role); return; }
        if (ObjectIndex < 0) return;
        int i = ObjectIndex;
        editor.ChangeObjects($"Role of {editor.ObjectLabel(i)}", [i], o => o.Role = role);
    }

    partial void OnMirrorXChanged(bool value)
    {
        if (_loading || ObjectIndex < 0) return;
        int i = ObjectIndex;
        editor.ChangeObjects($"Mirror {editor.ObjectLabel(i)}", [i], o => o.Placement.MirrorX = value);
    }

    /// <summary>The rotations field: <c>z 30, x 90</c> — each an axis and degrees, applied in order.</summary>
    public void CommitRotate()
    {
        if (ObjectIndex < 0) return;
        if (ParseRotations(RotateText) is not { } list)
        {
            Error = "Rotations are written as an axis and degrees, in order: \"z 30, x 90\".";
            return;
        }
        Error = "";
        int i = ObjectIndex;
        editor.ChangeObjects($"Rotate {editor.ObjectLabel(i)}", [i], o => o.Placement.Rotate = list);
    }

    /// <summary>Parses <c>z 30, x 90</c>; null when it is not that shape. An empty text is no rotation.</summary>
    public static List<C3dRotation>? ParseRotations(string text)
    {
        var list = new List<C3dRotation>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length != 2 || !Enum.TryParse<C3dAxis>(bits[0], ignoreCase: true, out var axis) ||
                !double.TryParse(bits[1].TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double deg) ||
                !double.IsFinite(deg))
                return null;
            list.Add(new C3dRotation { Axis = axis, Deg = deg });
        }
        return list;
    }
}

/// <summary>brief-sar-3 R-sar3-2 — one phantom layer in the Inspector: its name and its material, written through
/// <paramref name="commit"/> when the user picks another. The list's last item, New Material…, is a command: it opens the
/// picker through <paramref name="newMaterial"/>, which writes what is made there to the layer, and the row keeps what the
/// layer states. Both run through <paramref name="post"/>, after the pick: a commit rebuilds the rows, and doing it inside the
/// ComboBox's own selection change crashed Avalonia's selection model.</summary>
public sealed partial class C3dPhantomLayerRow(string name, string? material, Action<string?> commit, Action<Action> post, Action newMaterial)
    : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private string? _material = material;
    private bool _restoring;

    // A combobox whose items are re-read pushes null through its selection; a layer is never cleared that way.
    partial void OnMaterialChanged(string? oldValue, string? newValue)
    {
        if (_restoring || newValue is null) return;
        if (newValue != C3dEditorViewModel.NewMaterialItem) { post(() => commit(newValue)); return; }
        post(() =>
        {
            _restoring = true;
            Material = oldValue;
            _restoring = false;
            newMaterial();
        });
    }
}
