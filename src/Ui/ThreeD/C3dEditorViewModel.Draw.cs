// brief-em3d-45 — drawing in the 3D editor: the drawing plane and its grid, the tools, the typed field, Extrude.
//
// THE PLANE (R-em3d45-1) is editor state: XY, YZ or XZ at an integer-DBU offset, chosen from the toolbar, the 3D
// menu, a face (Face mode's context menu, or Ctrl/Cmd-click with a tool armed) or a snapped point (Ctrl/Cmd+Shift-
// click moves the offset through it). It is saved per document in the workspace's window state, never in the .c3d.
// The camera never moves it; drawing on it edge-on is refused (R-em3d45-1c).
//
// THE GRID (R-em3d45-2) is the pane's (Viewer3DViewState.DrawingGrid, a shader over the viewport that
// casts each pixel's ray at the plane, so it runs to the horizon). The SNAP grid is
// the same plane at the document's SnapDbu — which is not necessarily the drawn minor spacing, so the status line
// shows both when they differ (R-em3d45-2d).
//
// THE TOOLS (R-em3d45-3) are one class each (Tools/). A click goes to the armed tool with the cursor resolved into a
// C3dDrawInput here; what a tool finishes comes back as ONE object, inserted as one undo entry. Nothing is written
// while a gesture runs, so Esc leaves no entry (gate 8). Arming a tool keeps the selection mode; Esc disarms.
//
// THE TYPED FIELD (R-em3d45-4, §8.2 point 5): while a gesture is in progress a digit opens it — never a dialog —
// prefilled with the current values of the dimensions the next click fixes; Tab moves between them, Enter accepts
// the step as if clicked, Esc returns to the mouse. An entry that does not parse stays in the field, red, and
// nothing is written; an expression is recognised and deferred to brief 51, also in red.

using System.Collections.ObjectModel;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD.Operations;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel : IC3dDrawHost
{
    /// <summary>R-em3d45-3 — the Shift+A popup: one letter arms each tool (the owner confirms them at the owner check).</summary>
    public static IReadOnlyList<(C3dToolKind Kind, char Letter, string Icon)> DrawTools { get; } =
    [
        (C3dToolKind.Box, 'B', "CubeOutline"),
        // A Material kind, or a Viewer3DPathGlyph constant's name: the icon set has no cylinder worth using.
        (C3dToolKind.Cylinder, 'Y', nameof(Viewer3DPathGlyph.Cylinder)),
        // brief-em3d-102 — Sph_ere (owner decision D1: S, P and H are taken); the icon set has no sphere either (D5).
        (C3dToolKind.Sphere, 'E', nameof(Viewer3DPathGlyph.Sphere)),
        // brief-sar-3 — an _Oval: E and I read as Ellipsoid's letters and E is the sphere's; drawn, as the sphere is.
        (C3dToolKind.Ellipsoid, 'O', nameof(Viewer3DPathGlyph.Ellipsoid)),
        (C3dToolKind.Sheet, 'S', "RectangleOutline"),
        (C3dToolKind.Polygon, 'G', "VectorPolygon"),
        (C3dToolKind.Polyline, 'L', "VectorPolyline"),
        // brief-em3d-49 R-em3d49-2d — a port, drawn like a sheet.
        (C3dToolKind.Port, 'P', "ArrowUpBoldBoxOutline"),
        // brief-sar-3 R-sar3-3 — a lumped element, drawn like a port: _C for its capacitor glyph.
        (C3dToolKind.LumpedElement, 'C', nameof(Viewer3DPathGlyph.Capacitor)),
        // brief-em3d-50 R-em3d50-3a — a bond wire, pad to pad.
        (C3dToolKind.Wire, 'W', "VectorCurve"),
        // brief-em3d-75 R-em3d75-1a — the thermal places: a heat source, a probe, a mesh region.
        (C3dToolKind.HeatSource, 'H', "FireCircle"),
        (C3dToolKind.ProbePoint, 'T', "ThermometerProbe"),
        (C3dToolKind.MeshRegion, 'M', "CubeScan"),
    ];

    public const string EdgeOnFormat = "the {0} plane is edge-on; orbit or choose another plane";

    private DrawingPlane _plane = DrawingPlane.Default;
    private readonly DrawingGridSettings _grid = new();
    private C3dDrawTool? _tool;
    private (DrawSegment A, DrawSegment B)? _crossing;

    /// <summary>Raised when the Shift+A popup should open at the cursor.</summary>
    public event Action? DrawMenuRequested;

    /// <summary>Raised when the typed field opened and should take the keyboard.</summary>
    public event Action? FieldFocusRequested;

    /// <summary>Raised when the drawing plane changed (the shell stores it in the window state).</summary>
    public event Action? DrawingPlaneChanged;

    // ── the drawing plane (R-em3d45-1) ───────────────────────────────────────────────────────

    public DrawingPlane Plane => _plane;

    public int DbuPerMicron => Document.DbuPerMicron;

    /// <summary>Sets the plane. The camera is not touched; the grid and the snap follow at once.</summary>
    public void SetPlane(DrawingPlane plane)
    {
        if (plane == _plane) { SyncPlaneTexts(); return; }
        _plane = plane;
        ApplySnapGrid();
        SyncPlaneTexts();
        Viewer.RequestFrame();
        DrawingPlaneChanged?.Invoke();
    }

    public bool IsPlaneXY { get => _plane.Plane == C3dPlane.XY; set { if (value) SetPlane(_plane with { Plane = C3dPlane.XY }); else OnPropertyChanged(); } }
    public bool IsPlaneYZ { get => _plane.Plane == C3dPlane.YZ; set { if (value) SetPlane(_plane with { Plane = C3dPlane.YZ }); else OnPropertyChanged(); } }
    public bool IsPlaneXZ { get => _plane.Plane == C3dPlane.XZ; set { if (value) SetPlane(_plane with { Plane = C3dPlane.XZ }); else OnPropertyChanged(); } }

    /// <summary>3D editor round 1 — the toolbar's plane combo: the three planes in one control.</summary>
    public static IReadOnlyList<C3dPlane> PlaneChoices { get; } = [C3dPlane.XY, C3dPlane.YZ, C3dPlane.XZ];

    /// <summary>The drawing plane's orientation, for the toolbar's combo (its offset is kept).</summary>
    public C3dPlane PlaneKind
    {
        get => _plane.Plane;
        set { if (value != _plane.Plane) SetPlane(_plane with { Plane = value }); }
    }

    /// <summary>The offset field, in the display unit (a suffix is honoured). Committed with <see cref="CommitPlaneOffset"/>.</summary>
    [ObservableProperty] private string _planeOffsetText = "0";
    [ObservableProperty] private string? _planeOffsetError;

    /// <summary>Commits the offset field: exact, or red and unchanged.</summary>
    public void CommitPlaneOffset()
    {
        var d = C3dDimension.Parse(PlaneOffsetText, Document.DisplayUnit, Document.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value) { PlaneOffsetError = d.Why; return; }
        PlaneOffsetError = null;
        SetPlane(_plane with { OffsetDbu = d.Dbu });
    }

    private void SyncPlaneTexts()
    {
        PlaneOffsetText = C3dDimension.Spell(_plane.OffsetDbu, Document.DisplayUnit, Document.DbuPerMicron);
        PlaneOffsetError = null;
        OnPropertyChanged(nameof(IsPlaneXY));
        OnPropertyChanged(nameof(IsPlaneYZ));
        OnPropertyChanged(nameof(IsPlaneXZ));
        OnPropertyChanged(nameof(PlaneKind));
        OnPropertyChanged(nameof(PlaneText));
        RefreshGridText();
    }

    /// <summary><c>XY at z = 0 µm</c>.</summary>
    public string PlaneText => $"{_plane.Plane} at {_plane.Normal.ToString().ToLowerInvariant()} = {Length(_plane.OffsetDbu)}";

    /// <summary>R-em3d45-1b — the plane through a face: its axis and offset when the face is axis-aligned; a tilted face is
    /// refused, naming its normal. Returns the refusal, or null.</summary>
    public string? PlaneFromFace(uint objectId, int face)
    {
        var scene = Viewer.Scene;
        if (scene.Object(objectId) is not { } o || face < 0) return "There is no face under the cursor.";
        var (_, normal) = Scene3DFaces.AreaAndNormal(scene, objectId, face);
        if (normal is not { } n) return $"Face {o.FaceName(face)} is curved: a drawing plane lies on a flat face.";
        if (DrawingPlane.AxisOfNormal(n) is not { } axis)
            return $"Face {o.FaceName(face)} is tilted — its normal is ({N(n.X)}, {N(n.Y)}, {N(n.Z)}) — and the drawing planes are XY, YZ and XZ only.";
        if (scene.FeaturesOf(objectId) is not { Table: { } t } fr || face >= t.FaceCount || t.FaceVertexStart[face] == t.FaceVertexStart[face + 1])
            return $"Face {o.FaceName(face)} has no corners to measure.";
        var corner = fr.Vertex(t.FaceVertices[t.FaceVertexStart[face]]);
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        long offset = (long)Math.Round(DrawingPlane.Get(corner, axis) / per, MidpointRounding.AwayFromZero);
        SetPlane(new DrawingPlane(DrawingPlane.PlaneNormalTo(axis), offset));
        StatusMessage = $"Drawing plane: {PlaneText}, from face {o.FaceName(face)} of \"{o.Name}\".";
        return null;

        static string N(float v) => (MathF.Abs(v) < 5e-7f ? 0f : v).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>R-em3d45-1b — the offset moved through the snapped point, keeping the axis.</summary>
    public string? PlaneOffsetFromSnap()
    {
        if (!Viewer.Snap.IsSnap) return "Nothing is snapped: move onto a vertex, an edge or a face centre.";
        var p = ToDocumentPoint(Viewer.Snap);
        SetPlane(_plane with { OffsetDbu = _plane.W(p.Dbu) });
        StatusMessage = $"Drawing plane: {PlaneText}" + (p.Exact ? "." : " (the snapped point was not an exact DBU point; rounded).");
        return null;
    }

    // ── the grid (R-em3d45-2) ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _showDrawingGrid = true;
    [ObservableProperty] private string _gridText = "";

    partial void OnShowDrawingGridChanged(bool value)
    {
        _grid.Visible = value;
        Viewer.RequestFrame();
    }

    /// <summary>The pane's grid follows the plane, the document's DBU, its display unit and its snap step.</summary>
    private void ApplyDrawingGrid()
    {
        _grid.Plane = _plane;
        _grid.DbuPerMicron = Document.DbuPerMicron;
        _grid.Unit = Document.DisplayUnit;
        _grid.Dark = ThemeService.CurrentVariant == ColorVariant.Dark;
        _grid.SnapDbu = SnapPitch;          // 3D editor bugs round 3: close in, the minor spacing is the snap step
        _grid.Visible = ShowDrawingGrid;
        Viewer.View.DrawingGrid = _grid;
    }

    /// <summary>
    /// The pane's background and the grid's line colour, from the variant being rendered. Driven by
    /// <see cref="ThemeService.ThemeChanged"/> and never by a control's <c>ActualThemeVariantChanged</c>: App assigns
    /// <see cref="ThemeService.CurrentVariant"/> in a POSTED callback, so a control's own variant event runs first and reads
    /// the old variant — which left the background in one theme and the grid in the other.
    /// </summary>
    private void ApplyThemeVariant()
    {
        Viewer.View.Background = Viewer3DViewState.ThemeBackground(ThemeService.CurrentVariant == ColorVariant.Dark);
        ApplyDrawingGrid();
        Viewer.RequestFrame();
    }

    /// <summary>A palette or variant switch: the pane recolours now; the scene, whose palette is per variant, is rebuilt.</summary>
    private void OnThemeChanged(object? sender, EventArgs e) => _post(() =>
    {
        ApplyThemeVariant();
        Viewer.Invalidate();
    });

    /// <summary>The snap step: the document's, or its technology's default when it states none.</summary>
    public long SnapPitch => Document.SnapDbu > 0 ? Document.SnapDbu : Elaboration?.Technology?.DefaultSnapDbu ?? 0;

    // ── the snap distance (3D editor bugs round 2) ───────────────────────────────────────────

    /// <summary>The toolbar's Snap ladder: the layout editor's (<see cref="Layout.SnapLadder"/>), off the technology's default
    /// step. A function of the technology and the display unit only — never of the snap chosen from it
    /// (brief-snap-ladder-crash.md R-crash-1), so a pick cannot rebuild the list it was picked from.</summary>
    public ObservableCollection<string> SnapLadderOptions { get; } = [];
    private (long Base, LayoutUnit Unit, int Dbu)? _snapLadderKey;

    /// <summary>The snap step as the editable combobox shows it; committed with <see cref="CommitSnapDistanceText"/>.</summary>
    [ObservableProperty] private string _snapDistanceText = "";

    /// <summary>Typed or picked text: a positive length in the display unit (a suffix is honoured) becomes the document's
    /// snap step — a document preference like the display unit, so it dirties the document, adds no undo entry and moves
    /// nothing. Anything else puts the current step back. Zero is not "off" here, as it is in the layout editor: a
    /// .c3d's zero means "the technology's default", and the grid snap's own switch (F9) is what turns it off.</summary>
    public void CommitSnapDistanceText(string text)
    {
        if (Layout.SnapLadder.TryParse(text, Document.DisplayUnit, Document.DbuPerMicron, out long dbu) && dbu > 0 && dbu != SnapPitch)
        {
            Document.SnapDbu = dbu;
            _preferenceDirty = true;
            OnPropertyChanged(nameof(IsDirty));
            ApplySnapGrid();          // refreshes the text too
            RefreshGridText();
            return;
        }
        RefreshSnapDistance();
    }

    private void RefreshSnapDistance()
    {
        long baseDbu = Elaboration?.Technology is { DefaultSnapDbu: > 0 } t ? t.DefaultSnapDbu : 0;
        var key = (baseDbu, Document.DisplayUnit, Document.DbuPerMicron);
        if (_snapLadderKey != key)
        {
            _snapLadderKey = key;
            SnapLadderOptions.Clear();
            foreach (var rung in Layout.SnapLadder.Build(baseDbu, Document.DisplayUnit, Document.DbuPerMicron)) SnapLadderOptions.Add(rung);
        }
        long pitch = SnapPitch;
        SnapDistanceText = pitch > 0 ? Layout.SnapLadder.Spell(pitch, Document.DisplayUnit, Document.DbuPerMicron) : "";
    }

    /// <summary>R-em3d45-2d — <c>Grid 10 µm</c>, and <c>· snap 1 µm</c> when the snap step is not the drawn one.</summary>
    public void RefreshGridText()
    {
        if (!ShowDrawingGrid) { GridText = ""; return; }
        var (_, h) = Viewer.ViewSize;
        var s = PlaneGrid.SpacingFor(Viewer.Scene, Viewer.View.Camera, _grid, h);
        if (s.MinorDbu <= 0) { GridText = ""; return; }
        string text = "Grid " + Length(s.MinorDbu);
        long snap = SnapPitch;
        if (snap > 0 && snap != s.MinorDbu) text += " · snap " + Length(snap);
        GridText = text;
    }

    // ── the tools (R-em3d45-3) ───────────────────────────────────────────────────────────────

    /// <summary>The armed tool's kind, or null.</summary>
    public C3dToolKind? ArmedTool => _tool?.Kind;

    public C3dDrawTool? Tool => _tool;

    public bool IsBoxArmed      { get => ArmedTool == C3dToolKind.Box;      set => ArmToggle(C3dToolKind.Box, value); }
    public bool IsSheetArmed    { get => ArmedTool == C3dToolKind.Sheet;    set => ArmToggle(C3dToolKind.Sheet, value); }
    public bool IsPolygonArmed  { get => ArmedTool == C3dToolKind.Polygon;  set => ArmToggle(C3dToolKind.Polygon, value); }
    public bool IsPolylineArmed { get => ArmedTool == C3dToolKind.Polyline; set => ArmToggle(C3dToolKind.Polyline, value); }
    public bool IsCylinderArmed { get => ArmedTool == C3dToolKind.Cylinder; set => ArmToggle(C3dToolKind.Cylinder, value); }
    public bool IsSphereArmed   { get => ArmedTool == C3dToolKind.Sphere;   set => ArmToggle(C3dToolKind.Sphere, value); }
    public bool IsEllipsoidArmed { get => ArmedTool == C3dToolKind.Ellipsoid; set => ArmToggle(C3dToolKind.Ellipsoid, value); }
    public bool IsLumpedElementArmed { get => ArmedTool == C3dToolKind.LumpedElement; set => ArmToggle(C3dToolKind.LumpedElement, value); }
    public bool IsPortArmed { get => ArmedTool == C3dToolKind.Port; set => ArmToggle(C3dToolKind.Port, value); }
    // brief-em3d-75 — the thermal tools' toolbar toggles.
    public bool IsHeatSourceArmed { get => ArmedTool is C3dToolKind.HeatSource or C3dToolKind.HeatSourcePolygon; set => ArmToggle(C3dToolKind.HeatSource, value); }
    public bool IsProbeArmed { get => ArmedTool is C3dToolKind.ProbePoint or C3dToolKind.ProbeSpot or C3dToolKind.ProbeLine; set => ArmToggle(C3dToolKind.ProbePoint, value); }
    public bool IsMeshRegionArmed { get => ArmedTool == C3dToolKind.MeshRegion; set => ArmToggle(C3dToolKind.MeshRegion, value); }

    private void ArmToggle(C3dToolKind kind, bool on)
    {
        if (on) Arm(kind);
        else if (ArmedTool == kind || (_tool is HeatSourcePolygonTool && kind == C3dToolKind.HeatSource)
                 || (_tool is ProbeTool && kind == C3dToolKind.ProbePoint)) Disarm();
    }

    /// <summary>Arms a tool (from the toolbar, 3D ▸ Draw or the Shift+A popup). The selection mode is kept.</summary>
    [RelayCommand]
    public void Arm(C3dToolKind kind)
    {
        if (kind == C3dToolKind.Extrude) { Extrude(); return; }
        SetTool(kind switch
        {
            C3dToolKind.Box => new BoxTool(this),
            C3dToolKind.Sheet => new SheetTool(this),
            C3dToolKind.Polygon => new PolygonTool(this),
            C3dToolKind.Polyline => new PolylineTool(this),
            C3dToolKind.Sphere => new SphereTool(this),
            C3dToolKind.Ellipsoid => new EllipsoidTool(this),
            C3dToolKind.LumpedElement => new LumpedElementTool(this, NewLumpedElementTemplate),
            C3dToolKind.Port => new PortTool(this, () => NewPortTemplate()),
            C3dToolKind.Wire => WireToolArmed(),
            C3dToolKind.HeatSource => new HeatSourceTool(this),
            C3dToolKind.HeatSourcePolygon => new HeatSourcePolygonTool(this),
            C3dToolKind.ProbePoint => new ProbeTool(this, C3dProbeShape.Point),
            C3dToolKind.ProbeSpot => new ProbeTool(this, C3dProbeShape.Spot),
            C3dToolKind.ProbeLine => new ProbeTool(this, C3dProbeShape.Line),
            C3dToolKind.MeshRegion => new MeshRegionTool(this),
            C3dToolKind.EffectiveBlock => new MeshRegionTool(this, block: true),
            C3dToolKind.TemperatureAlong => new TemperatureAlongTool(this),
            C3dToolKind.SectionThrough => new SectionThroughTool(this),
            _ => new CylinderTool(this),
        });
    }

    /// <summary>The popup's letter: arms its tool; false for a letter that is none of them.</summary>
    public bool ArmByLetter(char letter)
    {
        foreach (var (kind, l, _) in DrawTools)
            if (char.ToUpperInvariant(letter) == l) { Arm(kind); return true; }
        return false;
    }

    [RelayCommand]
    public void Disarm() => SetTool(null);

    private void SetTool(C3dDrawTool? tool)
    {
        CloseField();
        _typedExpressions.Clear();
        EndGroup();
        _crossing = null;
        if (_tool is C3dOperationTool && !ReferenceEquals(_tool, tool)) EndOperation();
        if (_tool is C3dFaceEditTool && !ReferenceEquals(_tool, tool)) EndFaceEdit();
        _tool = tool;
        // The Wire tool shows the top face an end lands on (FillWireOverlay); the whole object lit under it said otherwise.
        if (Viewer.View.HoverHidden != tool is WireTool)
        {
            Viewer.View.HoverHidden = tool is WireTool;
            Viewer.RequestFrame();
        }
        // brief-em3d-46 — one gesture at a time: arming a tool ends a measurement.
        if (tool is not null) Viewer.EndMeasure();
        // Snap tracking (SnapTracker3D) works while a tool is armed; a new tool starts with no references.
        Viewer.ClearSnapReferences();
        Viewer.SnapTrackingActive = tool is not null;
        foreach (string p in new[] { nameof(ArmedTool), nameof(Tool), nameof(IsBoxArmed), nameof(IsSheetArmed), nameof(IsPolygonArmed),
                                     nameof(IsPolylineArmed), nameof(IsCylinderArmed), nameof(IsSphereArmed), nameof(IsEllipsoidArmed), nameof(IsLumpedElementArmed), nameof(IsPortArmed), nameof(IsWireArmed), nameof(ToolPrompt),
                                     nameof(IsHeatSourceArmed), nameof(IsProbeArmed), nameof(IsMeshRegionArmed) })
            OnPropertyChanged(p);
        Viewer.RequestFrame();
    }

    /// <summary>What the armed tool asks for next — the status line.</summary>
    public string ToolPrompt => _tool?.Prompt ?? "";

    /// <summary>Undo entries a tool has added — gate 8 counts them.</summary>
    public int ToolCommits { get; private set; }

    /// <summary>The cursor, as a tool reads it: the snap in force and the ray.</summary>
    public C3dDrawInput CursorInput()
    {
        C3dPoint3? snap = null;
        bool exact = false, geometry = false;
        if (Viewer.Snap.IsSnap)
        {
            var p = ToDocumentPoint(Viewer.Snap);
            snap = p.Dbu;
            exact = p.Exact;
            geometry = Viewer.Snap.Kind != Snap3DKind.Grid;
        }
        var ray = Viewer.CursorRay();
        return new C3dDrawInput(snap, exact, geometry, ray?.Origin, ray?.Direction, Viewer.ShiftHeld, Viewer.CommandHeld);
    }

    public C3dPoint3? PlanePoint(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        if (input.HasRay && _plane.IsEdgeOn(input.RayDirection!.Value))
        {
            refusal = "Cannot draw: " + string.Format(EdgeOnFormat, _plane.Plane) + ".";
            return null;
        }
        if (input.Snap is { } s) return _plane.Project(s);
        if (input.HasRay && _plane.Hit(input.RayOrigin!.Value, input.RayDirection!.Value, Document.DbuPerMicron) is { } hit)
        {
            double per = C3dLowering.Metres(1, Document.DbuPerMicron);
            long R(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
            return _plane.Project(new C3dPoint3(R(hit.X), R(hit.Y), R(hit.Z)));
        }
        refusal = input.HasRay ? $"The {_plane.Plane} drawing plane is not under the cursor: point below its horizon, or turn the view toward it."
                               : "Move the cursor over the view.";
        return null;
    }

    public C3dPoint3? FreePoint(in C3dDrawInput input, out string? refusal)
    {
        refusal = null;
        if (input.Snap is { } s && input.SnapOnGeometry) return s;
        return PlanePoint(input, out refusal);
    }

    public long? Along(C3dPoint3 through, C3dAxis axis, in C3dDrawInput input)
    {
        // Near a feature: its own coordinate along the line — the projection of the snap onto it.
        if (input.Snap is { } s && input.SnapOnGeometry) return DrawingPlane.Get(s, axis);
        if (!input.HasRay) return null;
        var p = DrawGeometry.Metres(through, Document.DbuPerMicron);
        if (DrawingPlane.AlongAxisClosestToRay(p, axis, input.RayOrigin!.Value, input.RayDirection!.Value) is not { } t) return null;
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        double w = DrawingPlane.Get(through, axis) + t / per;
        long pitch = Viewer.SnapEnabled && Viewer.SnapGridOn ? SnapPitch : 0;
        return pitch > 0 ? (long)Math.Round(w / pitch, MidpointRounding.AwayFromZero) * pitch : (long)Math.Round(w, MidpointRounding.AwayFromZero);
    }

    public C3dPoint3? DepthPoint(C3dPoint3 through, in C3dDrawInput input)
    {
        if (input.Snap is { } s && input.SnapOnGeometry) return s;
        if (!input.HasRay) return null;
        var p = DrawGeometry.Metres(through, Document.DbuPerMicron);
        var o = input.RayOrigin!.Value;
        var d = input.RayDirection!.Value;
        double len2 = d.X * d.X + d.Y * d.Y + d.Z * d.Z;
        if (len2 < 1e-24) return null;
        double t = ((p.X - o.X) * d.X + (p.Y - o.Y) * d.Y + (p.Z - o.Z) * d.Z) / len2;
        if (t <= 0) return null;
        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        // The STEP is on the grid, not the point: a point that is off the grid stays where it is until the cursor moves it.
        long pitch = Viewer.SnapEnabled && Viewer.SnapGridOn ? SnapPitch : 0;
        long Step(double from, double to)
        {
            double w = (to - from) / per;
            return pitch > 0 ? (long)Math.Round(w / pitch, MidpointRounding.AwayFromZero) * pitch : (long)Math.Round(w, MidpointRounding.AwayFromZero);
        }
        return new C3dPoint3(through.X + Step(p.X, o.X + t * d.X), through.Y + Step(p.Y, o.Y + t * d.Y), through.Z + Step(p.Z, o.Z + t * d.Z));
    }

    /// <summary>The smallest <c>&lt;prefix&gt;&lt;n&gt;</c> no object or instance is called — unique and renamable.</summary>
    public string NextName(string prefix)
    {
        // brief-em3d-66 — a Tool's name inside a boolean is taken too: names are unique across the document.
        // brief-em3d-75 — and a thermal place's: brief 73 makes names unique across objects, instances and places.
        var used = new HashSet<string>(Document.Objects.Select(o => o.Name).Concat(Document.Instances.Select(i => i.Name)).Concat(NestedNames())
                                               .Concat(ThermalPlaceNames()).Concat(Document.LumpedElements.Select(l => l.Name)), StringComparer.Ordinal);
        for (int n = 1; ; n++)
            if (!used.Contains(prefix + n)) return prefix + n;
    }

    /// <summary>The material every new object gets — the toolbar combo, from the technology's materials.</summary>
    [ObservableProperty] private string? _currentMaterial;

    /// <summary>Keeps the current material one the technology has (the first, until the user picks).</summary>
    private void SyncCurrentMaterial()
    {
        var mats = Materials;
        // brief-em3d-53 — a material just made in the picker is kept until the elaboration that holds it arrives.
        if (CurrentMaterial is not null && CurrentMaterial == _pendingMaterial && !mats.Contains(CurrentMaterial)) return;
        if (CurrentMaterial is null || !mats.Contains(CurrentMaterial)) CurrentMaterial = mats.Count > 0 ? mats[0] : null;
        if (CurrentMaterial is not null && mats.Contains(CurrentMaterial)) _pendingMaterial = null;
    }

    public double? ThicknessUmFor(string? material)
    {
        if (material is null || Elaboration?.Technology is not { } tech) return null;
        var layer = tech.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.Material == material && l.ThicknessDbu > 0);
        return layer is null ? null : (double)LayoutUnits.FromDbu(layer.ThicknessDbu, LayoutUnit.Um, Document.DbuPerMicron);
    }

    /// <summary>One tool step's outcome: a finished object is inserted (one undo entry); a refusal goes to the status line.</summary>
    private void Apply(C3dToolStep step)
    {
        _crossing = null;
        if (step.Refusal is { } why)
        {
            StatusMessage = why;
            if (step.Crossing is { } x && _tool is PolygonTool pt) _crossing = pt.CrossingSegments(x);
        }
        else if (step.Advanced) StatusMessage = "";
        // A finished gesture's tracking references were for it: the next one starts with none.
        if (step.Result is not null || step.Finished) Viewer.ClearSnapReferences();
        if (step.Finished && _tool is Hierarchy.PlaceInstanceTool place) { CommitPlacement(place); return; }
        // brief-em3d-75 — a thermal place is a record, inserted as a port is; Temperature Along makes nothing but a plot.
        if (step.Finished && _tool is IC3dRecordTool recordTool)
        {
            CommitRecordTool(recordTool);
            OnPropertyChanged(nameof(ToolPrompt));
            Viewer.RequestFrame();
            return;
        }
        // brief-dfr34-3 — Section Through…: the plot, the clip plane and the camera, one entry; the pick is put away
        if (step.Finished && _tool is SectionThroughTool section && section.Take() is { } picked)
        {
            SetTool(null);
            if (CommitSectionThrough(picked.From, picked.To) is { } sectionWhy) StatusMessage = sectionWhy;
            OnPropertyChanged(nameof(ToolPrompt));
            Viewer.RequestFrame();
            return;
        }
        if (step.Finished && _tool is TemperatureAlongTool along && along.Take() is { } ends)
        {
            if (PlotTemperatureAlong(ends.From, ends.To) is { } alongWhy) StatusMessage = alongWhy;
            OnPropertyChanged(nameof(ToolPrompt));
            Viewer.RequestFrame();
            return;
        }
        if (step.Finished && _tool is LumpedElementTool lumpedTool && lumpedTool.TakeMade() is { } lumped)
        {
            AddLumpedElement(lumped);
            OnPropertyChanged(nameof(ToolPrompt));
            Viewer.RequestFrame();
            return;
        }
        if (step.Finished && _tool is PortTool portTool && portTool.Made is { } port)
        {
            AddPort(port);
            OnPropertyChanged(nameof(ToolPrompt));
            Viewer.RequestFrame();
            return;
        }
        if (step.Finished && _tool is C3dOperationTool op) CommitOperation(op);
        else if (step.Finished && _tool is C3dFaceEditTool ft) CommitFaceEdit(ft);
        if (step.Result is { } obj)
        {
            if (_tool is ExtrudeTool ex) CommitExtrude(ex, obj);
            else if (_tool is ExtrudeFaceTool ef) CommitExtrudeFace(ef, obj);
            else
            {
                _evaluatedOnce = "";
                if (_tool is { } drawing) obj = BindTyped(drawing, obj);
                Push(new C3dEdit($"Draw {C3dObject.KindOf(obj).ToLowerInvariant()} {obj.Name}",
                                 [new C3dEditSlot(false, Document.Objects.Count, null, C3dPersistence.SerializeObject(obj))], ApplySlots));
                ToolCommits++;
                StatusMessage = $"Drew {C3dObject.KindOf(obj).ToLowerInvariant()} \"{obj.Name}\"" + (obj.Material is { } m ? $" in {m}." : ".") + _evaluatedOnce;
                if (_tool is WireTool wt)
                {
                    RememberWire(wt);
                    // Drawn from above, its height was never seen: say what it passes through, as the red in the preview did.
                    if (obj is C3dWire drawn && WireClashes(drawn.Points, wt.StartPad, wt.EndPad).Names is { Count: > 0 } through)
                        StatusMessage += $" ⚠ It passes through {string.Join(", ", through.Select(n => $"'{n}'"))}: raise its loop height " +
                                         "(Properties ▸ Loop height) or undo.";
                }
            }
            // brief-em3d-51 R-em3d51-3c — a Define strip's definitions and the object they sized are one entry.
            EndGroup();
        }
        // A wire's ends placed from above (Top view, the easy way to place them in plan): its loop height cannot be seen or
        // set with the mouse there, so the height field opens at once, saying what Enter takes — the third step is never
        // left waiting invisibly.
        if (step.Advanced && _tool is WireTool { Step: 2 } wire)
        {
            SuggestLoopHeight(wire);
            UpdateWireClash(wire);
            if (WireTool.LooksDown(CursorInput()))
            {
                OpenField(null);
                FieldLabel = $"loop height ({Suffix}) — Enter: {Length(wire.DefaultLoopHeight)}";
            }
        }
        OnPropertyChanged(nameof(ToolPrompt));
        Viewer.RequestFrame();
    }

    // ── input (IViewer3DEditHost) ────────────────────────────────────────────────────────────

    private static bool Command(KeyModifiers m) => (m & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    public bool DrawClick(KeyModifiers modifiers, int clickCount)
    {
        // brief-dfr31-3 R-dfr31-3-2 — a field plot's armed "Through…" takes the click before anything, and selects nothing.
        if (_plotThrough is not null)
        {
            if (PlotThroughObject(Viewer.HoveredItem?.Object ?? 0) is { } why) StatusMessage = why;
            return true;
        }
        if (Command(modifiers))
        {
            // R-em3d45-1b — Ctrl/Cmd+Shift-click: the offset through the snapped point; Ctrl/Cmd-click a face (tool armed).
            if ((modifiers & KeyModifiers.Shift) != 0)
            {
                if (PlaneOffsetFromSnap() is { } why) StatusMessage = why;
                return true;
            }
            if (_tool is null) return false;
            // brief-em3d-48 R-em3d48-1b — Ctrl/Cmd while placing is the bottom-centre handle, not a plane pick.
            if (_tool is not Hierarchy.PlaceInstanceTool)
            {
                if (PlaneFromFace(Viewer.LastPick.Object, Viewer.LastPick.Face) is { } refusal) StatusMessage = refusal;
                return true;
            }
        }
        // brief-em3d-66 R-em3d66-5a — a double-click on a boolean's result, with no tool armed, enters it. 3D editor bugs
        // round 5 — and a click away from its operands leaves it. 3D editor groups — on a group's member it takes that member alone.
        if (_tool is null)
            return clickCount >= 2 && modifiers == KeyModifiers.None ? DoubleClickMember() || DoubleClickEnter()
                                                                     : clickCount == 1 && ClickAwayLeaves(modifiers);
        CloseField();
        var input = CursorInput();
        if (Command(modifiers)) input = input with { Command = true };
        string? moved = clickCount == 1 ? PlaneThroughFirstSnap(_tool, input) : null;
        Apply(clickCount >= 2 ? _tool.DoubleClick(input) : _tool.Click(input));
        if (moved is not null && _tool is { InProgress: true }) StatusMessage = moved;
        return true;
    }

    /// <summary>
    /// The first click of a tool that takes the drawing plane, on a geometry snap off that plane: the plane moves along its
    /// normal through the snap, and the sentence saying so is returned (else null, nothing changed). Without it the click
    /// projected the snap onto the plane — a corner snapped on a substrate's top started the box on its bottom, away from
    /// the glyph the cursor showed. Moving the plane, not just the gesture's, keeps the grid, the grid snap and the
    /// rubber band's ray on the same plane as the object.
    /// </summary>
    private string? PlaneThroughFirstSnap(C3dDrawTool tool, in C3dDrawInput input)
    {
        if (tool.Step != 0 || !tool.TakesPlaneAtFirstClick) return null;
        if (input.Snap is not { } s || !input.SnapOnGeometry) return null;
        if (input.HasRay && _plane.IsEdgeOn(input.RayDirection!.Value)) return null;       // the tool refuses; change nothing
        long w = _plane.W(s);
        if (w == _plane.OffsetDbu) return null;
        SetPlane(_plane with { OffsetDbu = w });
        return $"Drawing plane: {PlaneText}, through the snapped point.";
    }

    public bool DrawKey(Key key, KeyModifiers modifiers)
    {
        if (_plotThrough is not null && key == Key.Escape) { CancelPlotThrough(); return true; }
        if (key == Key.A && modifiers == KeyModifiers.Shift) { DrawMenuRequested?.Invoke(); return true; }
        // brief-em3d-66 — the panel's Esc and Enter; entering and leaving a boolean. brief-em3d-67 — the Fillet panel's.
        if (_tool is not { InProgress: true } && FilletKey(key, modifiers)) return true;
        if (_tool is not { InProgress: true } && BooleanKey(key, modifiers)) return true;
        // brief-em3d-48 — Ctrl/Cmd+] and Ctrl/Cmd+[: Push In and Pop Out, the layout editor's keys.
        if (_tool is not { InProgress: true } && HierarchyKey(key, modifiers)) return true;
        // 3D editor groups — Ctrl/Cmd+G and Ctrl/Cmd+Shift+G.
        if (_tool is not { InProgress: true } && GroupKey(key, modifiers)) return true;
        // brief-em3d-46 — G, R and Ctrl/Cmd+D start an operation on the selection (no gesture in progress).
        if (_tool is not { InProgress: true } && OperationKey(key, modifiers)) return true;
        // W arms the Wire tool, as its toolbar button and Shift+A, W do; W again puts it away. Never mid-gesture.
        if (_tool is not { InProgress: true } && key == Key.W && modifiers == KeyModifiers.None && IsEditable)
        {
            IsWireArmed = !IsWireArmed;
            return true;
        }
        if (_tool is not { } tool) return false;
        if (tool is C3dOperationTool opTool && opTool.Key(key, modifiers)) { OperationChanged(); return true; }
        if (tool is C3dFaceEditTool faceTool && faceTool.Key(key, modifiers)) { FaceToolChanged(); OnPropertyChanged(nameof(ToolPrompt)); return true; }
        bool plain = modifiers == KeyModifiers.None;
        bool gesture = tool.Kind == C3dToolKind.Extrude || tool is C3dOperationTool || tool is C3dFaceEditTool;
        switch (key)
        {
            case Key.Escape:
                EscapeTool(tool, gesture);
                return true;
            case Key.Enter when tool.InProgress:
                Apply(tool.Enter(CursorInput()));
                return true;
            case Key.Back or Key.Delete when tool.InProgress:
                if (tool.Backspace()) { _crossing = null; OnPropertyChanged(nameof(ToolPrompt)); Viewer.RequestFrame(); }
                return true;
            case Key.K when plain && tool is ExtrudeTool ex:
                ex.Keep = !ex.Keep;
                OnPropertyChanged(nameof(ToolPrompt));
                return true;
        }
        if (!tool.InProgress || tool.Dimensions.Count == 0) return false;
        if (key == Key.Tab || (key == Key.OemPlus && plain)) { OpenField(null); return true; }
        if (TypedChar(key, modifiers) is { } c)
        {
            // 3D editor bugs round 2 — the field takes focus a dispatcher turn after the key that opened it, so a quickly
            // typed second key can still arrive here. Re-opening then threw the first away ("10" became "0", a move of
            // nothing); while the field is open a key is APPENDED to what it holds.
            if (FieldOpen && _fieldIndex < _fieldTexts.Length)
            {
                FieldText += c;
                FieldFocusRequested?.Invoke();
            }
            else OpenField(c.ToString());
            return true;
        }
        return false;
    }

    /// <summary>
    /// 3D round 1 — Esc with a tool armed steps back ONE stage (C3dDrawTool.StepBack): a box at its height goes back
    /// to the second corner, then to the first point, and only an Esc with nothing in progress disarms. A gesture
    /// with no earlier stage (an extrude, a rotation, a face edit) ends at once, as before. Stepping back keeps the
    /// typed expressions of the stages still standing and drops the rest; a Define group stays open, since its
    /// definitions are still the gesture's.
    /// </summary>
    private void EscapeTool(C3dDrawTool tool, bool gesture)
    {
        if (tool.InProgress && tool.StepBack())
        {
            CloseField();
            _typedExpressions.RemoveAll(t => t.Step >= tool.Step);
            _crossing = null;
            if (tool is C3dOperationTool)
            {
                // A Move back at its base point: nothing follows the cursor, and the selection attracts the snap again.
                EndOperation();
                OperationChanged();
            }
            StatusMessage = tool.InProgress ? $"{tool.Name}: back one step." : $"{tool.Name}: back to the first point. Esc again puts the tool away.";
            OnPropertyChanged(nameof(ToolPrompt));
            Viewer.RequestFrame();
            return;
        }
        StatusMessage = tool.InProgress || gesture ? $"{tool.Name} cancelled." : "";
        Disarm();
    }

    /// <summary>3D round 1 — a tool or an operation is armed: the pane orbits on Ctrl/Cmd + drag only.</summary>
    public bool DrawArmed => _tool is not null || _plotThrough is not null;

    /// <summary>A key that starts a typed value: a digit, a decimal point or a minus sign.</summary>
    private static char? TypedChar(Key key, KeyModifiers modifiers)
    {
        if (modifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return null;
        if (modifiers == KeyModifiers.None)
        {
            if (key >= Key.D0 && key <= Key.D9) return (char)('0' + (key - Key.D0));
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return (char)('0' + (key - Key.NumPad0));
            if (key is Key.OemPeriod or Key.Decimal) return '.';
            if (key is Key.OemMinus or Key.Subtract) return '-';
        }
        return null;
    }

    public void FillDrawOverlay(Viewer3DDrawOverlay overlay)
    {
        int dbu = Document.DbuPerMicron;
        foreach (var o in Document.Objects)
        {
            if (o is not C3dPolyline l || l.Hidden) continue;
            var points = Points3Of(l);
            var t = l.Placement.IsDefault ? (C3dTransform?)null : l.Placement.ToTransform();
            if (t is { } tr)
                points = [.. points.Select(p => { var (x, y, z) = tr.Apply(p); return new C3dPoint3((long)Math.Round(x), (long)Math.Round(y), (long)Math.Round(z)); })];
            DrawGeometry.Chain(points, l.Closed, dbu, SelectedTreeItem?.Name == l.Name ? overlay.Selected : overlay.Construction);
        }
        if (_tool is { } tool)
        {
            // brief-em3d-51 R-em3d51-3b — while the field is open, the rubber band follows what it previews.
            if (FieldOpen && _fieldValues is { } typed) tool.PreviewTyped(typed, CursorInput(), overlay.Rubber, overlay.Fixed);
            else tool.Preview(CursorInput(), overlay.Rubber, overlay.Fixed);
        }
        FillFaceOverlay(overlay);
        FillFieldPlaneOverlay(overlay);
        FillHierarchyOverlay(overlay);
        FillWireOverlay(overlay);
        if (_tool is C3dOperationTool { ShowsPivot: true } op) overlay.Pivots.Add(DrawGeometry.Metres(op.Pivot, dbu));
        if (_crossing is { } x) { overlay.Crossing.Add(x.A); overlay.Crossing.Add(x.B); }
        FillSimulateOverlay(overlay);
        FillThermalOverlay(overlay);
        FillLumpedOverlay(overlay);                // brief-sar-3
    }

    private static List<C3dPoint3> Points3Of(C3dPolyline l)
    {
        if (l.Points3 is { } p3) return p3;
        var plane = new DrawingPlane(l.Plane, l.Offset);
        return [.. l.Points.Select(plane.FromUv)];
    }

    public IEnumerable<Viewer3DMenuItem> DrawMenuItems()
    {
        // brief-em3d-101 R-em3d101-9e — a face image clicked in the view: the record's menu alone
        if (FaceImageRecordMenuItems() is { Count: > 0 } faceImage)
        {
            foreach (var item in faceImage) yield return item;
            yield break;
        }
        // brief-em3d-90 — a boundary's tint selected: the boundary's menu alone (the face under it is one B away)
        if (TintMenuItems() is { Count: > 0 } tint)
        {
            foreach (var item in tint) yield return item;
            yield break;
        }
        // 3D editor groups — Group Objects and Ungroup first: a click selects a whole group, so they are what it is for.
        var groups = GroupMenuItems().ToList();
        foreach (var item in groups) yield return item;
        if (groups.Count > 0) yield return Viewer3DMenuItem.Separator;
        foreach (var item in ImageMenuItems()) yield return item;           // brief-em3d-101 — an image sheet's own items
        foreach (var item in OperationMenuItems()) yield return item;
        foreach (var item in BooleanMenuItems()) yield return item;
        foreach (var item in StepMenuItems()) yield return item;
        foreach (var item in FaceMenuItems()) yield return item;
        foreach (var item in FaceImageMenuItems()) yield return item;     // brief-em3d-101 — Map Image… on a flat face
        foreach (var item in HierarchyMenuItems()) yield return item;
        foreach (var item in SimulateMenuItems()) yield return item;
        foreach (var item in FieldMenuItems()) yield return item;         // brief-em3d-82 — beside Plot Temperature
        foreach (var item in SectionMenuItems()) yield return item;       // brief-dfr34-3 — Section Through…, with a field present
        foreach (var item in ThermalMenuItems()) yield return item;
        foreach (var item in WireMenuItems()) yield return item;
        if (Viewer.SelectMode == Scene3DSelectMode.Face && Viewer.Selection is [{ Face: >= 0 } f])
            yield return new Viewer3DMenuItem("Drawing Plane from Face", () => { if (PlaneFromFace(f.Object, f.Face) is { } why) StatusMessage = why; });
        if (ExtrudeSource() is { } src)
            yield return new Viewer3DMenuItem("Extrude", Extrude, Enabled: ExtrudeTool.CannotExtrude(src.Obj) is null,
                                              Tip: ExtrudeTool.CannotExtrude(src.Obj));
    }

    // ── the typed field (R-em3d45-4) ─────────────────────────────────────────────────────────

    private string[] _fieldTexts = [];
    private int _fieldIndex;

    [ObservableProperty] private bool _fieldOpen;
    [ObservableProperty] private string _fieldText = "";
    [ObservableProperty] private string _fieldLabel = "";
    [ObservableProperty] private string? _fieldError;
    [ObservableProperty] private double _fieldX;
    [ObservableProperty] private double _fieldY;

    /// <summary>The dimension the field is on, 0-based.</summary>
    public int FieldIndex => _fieldIndex;

    /// <summary>Opens the field at the cursor, prefilled with each dimension's current value; the first dimension's
    /// text is <paramref name="first"/> when a key opened it (a digit replaces the prefill, Tab or = starts it empty).</summary>
    public void OpenField(string? first)
    {
        if (_tool is not { InProgress: true } tool || tool.Dimensions.Count == 0) return;
        var current = tool.Current(CursorInput());
        _fieldTexts = new string[tool.Dimensions.Count];
        for (int i = 0; i < _fieldTexts.Length; i++)
            _fieldTexts[i] = i < current.Length && current[i] is { } v ? tool.SpellField(i, v, Document.DisplayUnit, Document.DbuPerMicron) : "";
        _fieldTexts[0] = first ?? "";
        _fieldIndex = 0;
        FieldX = Math.Max(0, Viewer.View.CursorX) + 16;
        FieldY = Math.Max(0, Viewer.View.CursorY) + 12;
        FieldError = null;
        ShowFieldIndex();
        FieldOpen = true;
        FieldFocusRequested?.Invoke();
    }

    private bool _showingField;

    private void ShowFieldIndex()
    {
        _showingField = true;
        FieldText = _fieldTexts[_fieldIndex];
        _showingField = false;
        var dims = _tool!.Dimensions;
        string suffix = _tool.FieldSuffix(_fieldIndex) ?? Suffix;
        FieldLabel = dims.Count > 1
            ? $"{dims[_fieldIndex]} ({suffix}) — Tab: {dims[(_fieldIndex + 1) % dims.Count]}"
            : $"{dims[_fieldIndex]} ({suffix})";
        UpdateFieldPreview();
    }

    private string Suffix => LayoutUnits.Suffix(Document.DisplayUnit);

    partial void OnFieldTextChanged(string value)
    {
        if (_showingField || !FieldOpen || _fieldIndex >= _fieldTexts.Length) return;
        _fieldTexts[_fieldIndex] = value;
        FieldError = null;
        UpdateFieldPreview();
    }

    /// <summary>Tab: the next dimension of the same step.</summary>
    public void FieldTab()
    {
        if (!FieldOpen || _fieldTexts.Length == 0) return;
        _fieldTexts[_fieldIndex] = FieldText;
        _fieldIndex = (_fieldIndex + 1) % _fieldTexts.Length;
        ShowFieldIndex();
    }

    /// <summary>Enter: every dimension parsed exactly and the step taken as if clicked — or, if one does not parse, the
    /// field stays open on it, red, and nothing is written.</summary>
    /// <para>brief-em3d-51 — a dimension may be an expression: evaluated in the document's scope and, where it lands in a
    /// named field, bound there; an unknown name opens the Define strip instead of refusing.</para>
    public void FieldEnter()
    {
        if (!FieldOpen || _tool is not { } tool) return;
        _fieldTexts[_fieldIndex] = FieldText;
        TypedEnter(tool);
    }

    /// <summary>Esc in the field: back to the mouse; the gesture goes on.</summary>
    public void FieldEscape() => CloseField();

    private void CloseField()
    {
        FieldOpen = false;
        FieldError = null;
        FieldPreview = "";
        _fieldValues = null;
        DefineOpen = false;
        DefineRows.Clear();
        _fieldTexts = [];
        _fieldIndex = 0;
    }

    // ── Extrude (R-em3d45-5) ─────────────────────────────────────────────────────────────────

    /// <summary>What Extrude would act on: the one selected sheet (Object mode), or the polyline selected in the tree.</summary>
    public (C3dObject Obj, int Index)? ExtrudeSource()
    {
        if (SelectedTreeItem is { ObjectIndex: >= 0 } item && item.ObjectIndex < Document.Objects.Count
            && Document.Objects[item.ObjectIndex] is C3dPolyline l)
            return (l, item.ObjectIndex);
        if (Viewer.SelectMode != Scene3DSelectMode.Object) return null;
        var sel = Viewer.SelectedObjects();
        if (sel.Count != 1) return null;
        int i = DocumentIndex(sel[0]);
        return i >= 0 && Document.Objects[i] is C3dSheet s ? (s, i) : null;
    }

    /// <summary>3D ▸ Modify ▸ Extrude, and the context menu's: starts the extrude gesture on the source.</summary>
    [RelayCommand]
    public void Extrude()
    {
        if (ExtrudeSource() is not { } src)
        {
            StatusMessage = "Extrude takes one selected sheet, or a polyline selected in the tree.";
            return;
        }
        if (ExtrudeTool.CannotExtrude(src.Obj) is { } why) { StatusMessage = why; return; }
        SetTool(new ExtrudeTool(this, src.Obj, src.Index));
        StatusMessage = "";
    }

    /// <summary>The extrude's one undo entry: the source replaced by the result (consumed), or the result inserted (kept).</summary>
    private void CommitExtrude(ExtrudeTool ex, C3dObject result)
    {
        result = BindTyped(ex, result);
        // brief-em3d-101 R-em3d101-5d (D9) — an image sheet extrudes as any sheet; the solid carries no picture.
        bool fromImage = ex.SourceIndex >= 0 && ex.SourceIndex < Document.Objects.Count && Document.Objects[ex.SourceIndex] is C3dSheet { Image: not null };
        string after = C3dPersistence.SerializeObject(result);
        string kind = C3dObject.KindOf(result).ToLowerInvariant();
        if (ex.Keep)
            Push(new C3dEdit($"Extrude to {kind} {result.Name}", [new C3dEditSlot(false, Document.Objects.Count, null, after)], ApplySlots));
        else
        {
            string before = C3dPersistence.SerializeObject(Document.Objects[ex.SourceIndex]);
            Viewer.ExpectRename(Document.Objects[ex.SourceIndex].Name, result.Name);
            Push(new C3dEdit($"Extrude to {kind} {result.Name}", [new C3dEditSlot(false, ex.SourceIndex, before, after)], ApplySlots));
        }
        ToolCommits++;
        StatusMessage = $"Extruded to {kind} \"{result.Name}\"; the source was " + (ex.Keep ? "kept." : "consumed.")
                        + (fromImage ? " The solid has no image: a picture belongs to a sheet." : "");
        SetTool(null);
    }

    /// <summary>brief-em3d-47 — Extrude to New Solid's one undo entry: the new object inserted; the source unchanged.</summary>
    private void CommitExtrudeFace(ExtrudeFaceTool ef, C3dObject result)
    {
        string kind = C3dObject.KindOf(result).ToLowerInvariant();
        Push(new C3dEdit($"{ef.Describe}: {result.Name}",
                         [new C3dEditSlot(false, Document.Objects.Count, null, C3dPersistence.SerializeObject(result))], ApplySlots));
        FaceEdits++;
        StatusMessage = $"Extruded {kind} \"{result.Name}\" from '{ef.Editor.Source.Name}'" + (result.Material is { } m ? $" in {m}." : ".");
        SetTool(null);
    }
}
