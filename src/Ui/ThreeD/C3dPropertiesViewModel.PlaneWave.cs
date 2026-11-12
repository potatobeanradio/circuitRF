// brief-hzn-12 (follow-up) — the Properties Inspector's page for the active setup's incident plane wave: what its row or its arrows
// select. Each box commits on Enter or on leaving it, as one undoable edit of the setup (C3dEditorViewModel.SetPlaneWave).

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.ThreeD.Tools;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    [ObservableProperty] private bool _isPlaneWave;
    [ObservableProperty] private string _planeWaveDirection = "";
    [ObservableProperty] private string _planeWaveEField = "";
    [ObservableProperty] private string _planeWaveE0 = "";
    [ObservableProperty] private string _planeWaveOrigin = "";
    [ObservableProperty] private string _planeWaveThetaStep = "";
    [ObservableProperty] private string _planeWavePhiStep = "";
    [ObservableProperty] private string _planeWaveSolver = "";
    [ObservableProperty] private bool _planeWaveEnabled;

    private string _planeWaveLoaded = "";

    /// <summary>The wave the page shows, by its name when it was loaded.</summary>
    private string _planeWaveName = "";

    /// <summary>The step boxes' placeholder: the default.</summary>
    public static string PlaneWaveDefaultStep { get; } = Em3dPlaneWave.DefaultStepDeg.ToString(CultureInfo.InvariantCulture);

    private void LoadPlaneWave()
    {
        if (editor.SelectedPlaneWave() is not { } rec || C3dPlaneWaves.ToSetup(rec, editor.Document.DbuPerMicron) is not { } w)
        { Heading = "Nothing selected"; return; }
        IsPlaneWave = true;
        Heading = "Plane wave";
        _planeWaveName = rec.Name;
        NameText = rec.Name;
        PlaneWaveEnabled = rec.Enabled;
        PlaneWaveDirection = C3dEditorViewModel.VectorText(w.Direction);
        PlaneWaveEField = C3dEditorViewModel.VectorText(w.EField);
        PlaneWaveE0 = w.E0?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        PlaneWaveOrigin = w.PhaseOriginUm is { } o ? OriginText(o) : "";
        PlaneWaveThetaStep = w.ThetaStepDeg?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        PlaneWavePhiStep = w.PhiStepDeg?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        PlaneWaveSolver = editor.ActiveSetup is { } s && CircuitRF.Design.Em3d.Em3dPlaneWaves.SolverCannot(s.Solver3D) is { } no
            ? (rec.Enabled ? "The active setup's solver cannot run it: " : "It cannot be enabled while the active setup's solver cannot run it: ") + no : "";
        _planeWaveLoaded = PlaneWaveState();
    }

    private string PlaneWaveState() => string.Join("|", PlaneWaveDirection, PlaneWaveEField, PlaneWaveE0, PlaneWaveOrigin, PlaneWaveThetaStep, PlaneWavePhiStep);

    /// <summary>The boxes as they read now, written to the active setup; nothing when nothing changed. A box that does not parse,
    /// or a wave that cannot be solved, is said and not written.</summary>
    public void CommitPlaneWave()
    {
        if (!IsPlaneWave || _loading || PlaneWaveState() == _planeWaveLoaded) return;
        var k = Vector(PlaneWaveDirection);
        var e = Vector(PlaneWaveEField);
        if (k is null || e is null) { Error = "Direction and E-field are three numbers each, such as 0, 0, -1."; return; }
        Point3? origin = null;
        if (PlaneWaveOrigin.Trim().Length > 0)
        {
            if (Origin(PlaneWaveOrigin, out string? why) is not { } o) { Error = why!; return; }
            origin = o;
        }
        double? e0 = Number(PlaneWaveE0), dt = Number(PlaneWaveThetaStep), dp = Number(PlaneWavePhiStep);
        if (e0 is double.NaN || dt is double.NaN || dp is double.NaN) { Error = "E0 and the two steps are numbers, or blank for their defaults."; return; }
        var wave = new EmPlaneWave3D(k.Value, e.Value, e0, origin) { ThetaStepDeg = dt, PhiStepDeg = dp };
        Error = editor.SetPlaneWaveValues(_planeWaveName, wave) ?? "";
        if (Error.Length == 0) _planeWaveLoaded = PlaneWaveState();
    }

    /// <summary>The Enabled box: enabling disables every other wave; refused (and the box put back) when the active setup's solver
    /// cannot run one.</summary>
    partial void OnPlaneWaveEnabledChanged(bool value)
    {
        if (_loading || !IsPlaneWave) return;
        Error = editor.SetPlaneWaveEnabled(_planeWaveName, value) ?? "";
        if (Error.Length > 0) { _loading = true; try { PlaneWaveEnabled = !value; } finally { _loading = false; } }
    }

    private static Point3? Vector(string text)
    {
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var v = new double[3];
        for (int i = 0; i < 3; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        return new Point3(v[0], v[1], v[2]);
    }

    private static double? Number(string text)
        => text.Trim().Length == 0 ? null
         : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

    /// <summary>The phase origin as the Inspector shows it: µm in the document's display unit.</summary>
    private string OriginText(Point3 um)
    {
        var doc = editor.Document;
        string One(double v) => C3dDimension.Spell((long)Math.Round(v * doc.DbuPerMicron), doc.DisplayUnit, doc.DbuPerMicron);
        return $"{One(um.X)}, {One(um.Y)}, {One(um.Z)}";
    }

    /// <summary>Three lengths in the display unit (a suffix may name another), as µm.</summary>
    private Point3? Origin(string text, out string? why)
    {
        why = null;
        var doc = editor.Document;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) { why = "The phase origin is three lengths, x, y, z."; return null; }
        var um = new double[3];
        for (int i = 0; i < 3; i++)
        {
            var d = C3dDimension.Parse(parts[i], doc.DisplayUnit, doc.DbuPerMicron);
            if (d.Kind != C3dDimensionKind.Value) { why = d.Why; return null; }
            um[i] = (double)LayoutUnits.FromDbu(d.Dbu, LayoutUnit.Um, doc.DbuPerMicron);
        }
        return new Point3(um[0], um[1], um[2]);
    }
}
