// A port in the Properties Inspector.
//
// A port DRAWN in the 3D view is the document's: its name, number and Z0, and its place — the plane it lies on, where that plane
// sits, its centre on it and its width and height — each through the editor's own writer (SetPortName, SetPortNumber, SetPortZ0,
// SetPortGeometry), one undo entry each. Changing the plane keeps the port's centre in space and its width and height.
//
// A COMPONENT port is the setup's: built from its part's pads each time, so its place and orientation follow the layout and are
// not edited here. What it states of its own — its Z0 and its width across the gap — is written into the setup's entry for the
// part (SetComponentPortOverride). A part left out of the run has a row and no sheet; its row opens this page all the same, since
// its width is often what was wrong.

using System.Collections.ObjectModel;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    // ── a drawn port ─────────────────────────────────────────────────────────────────────────

    /// <summary>The planes a port lies on, by the two axes they span.</summary>
    public static IReadOnlyList<string> PortPlanes { get; } = [nameof(C3dPlane.XY), nameof(C3dPlane.XZ), nameof(C3dPlane.YZ)];

    [ObservableProperty] private bool _isPortPage;
    /// <summary>False for a cylinder-patch port, whose place is its cylinder's: its name, number and Z0 only.</summary>
    [ObservableProperty] private bool _portPlaceEditable;
    [ObservableProperty] private string _portName = "";
    [ObservableProperty] private string _portNumber = "";
    [ObservableProperty] private string _portZ0 = "";
    [ObservableProperty] private string _portPlane = "XY";
    [ObservableProperty] private string _portAt = "";
    [ObservableProperty] private string _portCentreU = "";
    [ObservableProperty] private string _portCentreV = "";
    [ObservableProperty] private string _portWidth = "";
    [ObservableProperty] private string _portHeight = "";
    /// <summary>The axis each field is along on the port's plane: "Centre x", "Width (x)", "z of the plane".</summary>
    [ObservableProperty] private string _portULabel = "x";
    [ObservableProperty] private string _portVLabel = "y";
    [ObservableProperty] private string _portAtLabel = "z";

    private C3dPort? _shownPort;

    private void ClearPortPages()
    {
        IsPortPage = false;
        IsComponentPortPage = false;
        _shownPort = null;
        _shownComponent = null;
    }

    private string Spell(long dbu) => C3dDimension.Spell(dbu, editor.Document.DisplayUnit, editor.Document.DbuPerMicron);

    /// <summary>A length in metres as a read-only row shows it: the .c3d's display unit, with its suffix.</summary>
    private string Length(double metres)
        => $"{Spell((long)Math.Round(metres * 1e6 * editor.Document.DbuPerMicron))} {LayoutUnits.Suffix(editor.Document.DisplayUnit)}";

    private void LoadPortPage(C3dPort port)
    {
        Heading = $"Port {port.Number} · {C3dPorts.Label(port)}";
        if (editor.IsViewOnly) return;
        _shownPort = port;
        IsPortPage = true;
        PortPlaceEditable = port.Cylinder is null && !C3dPorts.HasTerminals(port);
        PortName = port.Name;
        PortNumber = port.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PortZ0 = port.Z0;
        ShowPortPlace(port);
    }

    private void ShowPortPlace(C3dPort port)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            PortPlane = port.Plane.ToString();
            (PortULabel, PortVLabel, PortAtLabel) = port.Plane switch { C3dPlane.XZ => ("x", "z", "y"), C3dPlane.YZ => ("y", "z", "x"), _ => ("x", "y", "z") };
            var (cu, cv, at, w, h) = C3dEditorViewModel.PortPlace(port);
            PortCentreU = Spell(cu); PortCentreV = Spell(cv); PortAt = Spell(at); PortWidth = Spell(w); PortHeight = Spell(h);
        }
        finally { _loading = was; }
    }

    /// <summary>Another plane: the port keeps its centre in space and its width and height, now along the new plane's axes.</summary>
    partial void OnPortPlaneChanged(string value)
    {
        if (_loading || _shownPort is not { } port || !Enum.TryParse<C3dPlane>(value, out var plane) || plane == port.Plane) return;
        var (cu, cv, at, w, h) = C3dEditorViewModel.PortPlace(port);
        var (x, y, z) = port.Plane switch { C3dPlane.YZ => (at, cu, cv), C3dPlane.XZ => (cu, at, cv), _ => (cu, cv, at) };
        var (u, v, a) = plane switch { C3dPlane.YZ => (y, z, x), C3dPlane.XZ => (x, z, y), _ => (x, y, z) };
        Error = editor.SetPortGeometry(port, plane, a, u, v, w, h) ?? "";
        if (Error.Length > 0) ShowPortPlace(port);
    }

    /// <summary>Enter or lost focus on any of the port's fields: each one that changed is written; the first refusal is shown.</summary>
    public void CommitPort()
    {
        if (!IsPortPage || _shownPort is not { } port) return;
        var doc = editor.Document;
        string? why = null;
        if (PortName.Trim() != port.Name) why ??= editor.SetPortName(port, PortName);
        if (PortNumber.Trim() != port.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)) why ??= editor.SetPortNumber(port, PortNumber);
        if (PortZ0.Trim() != port.Z0) why ??= editor.SetPortZ0(port.Number, PortZ0);
        if (PortPlaceEditable)
        {
            long L(string text, long fallback) => LayoutUnits.TryParse(text, doc.DisplayUnit, doc.DbuPerMicron, out long v) ? v : fallback;
            bool ok = LayoutUnits.TryParse(PortAt, doc.DisplayUnit, doc.DbuPerMicron, out _) && LayoutUnits.TryParse(PortCentreU, doc.DisplayUnit, doc.DbuPerMicron, out _)
                      && LayoutUnits.TryParse(PortCentreV, doc.DisplayUnit, doc.DbuPerMicron, out _) && LayoutUnits.TryParse(PortWidth, doc.DisplayUnit, doc.DbuPerMicron, out _)
                      && LayoutUnits.TryParse(PortHeight, doc.DisplayUnit, doc.DbuPerMicron, out _);
            var was = C3dEditorViewModel.PortPlace(port);
            if (!ok) why ??= $"A port's place is lengths, in {LayoutUnits.Suffix(doc.DisplayUnit)} unless a unit is written.";
            else
            {
                var now = (L(PortCentreU, was.CentreU), L(PortCentreV, was.CentreV), L(PortAt, was.At), L(PortWidth, was.Width), L(PortHeight, was.Height));
                if (now != was) why ??= editor.SetPortGeometry(port, port.Plane, now.Item3, now.Item1, now.Item2, now.Item4, now.Item5);
            }
        }
        Error = why ?? "";
        if (why is not null) { PortName = port.Name; PortNumber = port.Number.ToString(System.Globalization.CultureInfo.InvariantCulture); PortZ0 = port.Z0; ShowPortPlace(port); }
    }

    // ── a component port ─────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isComponentPortPage;
    [ObservableProperty] private string _componentZ0 = "";
    [ObservableProperty] private string _componentWidth = "";
    /// <summary>What a blank width means: the width the part's two pads share.</summary>
    [ObservableProperty] private string _componentWidthPlaceholder = "the pads' shared width";

    private string? _shownComponent;

    private void LoadComponentPortPage((C3dComponentPort Part, C3dPortResult? Result) cp)
    {
        var (part, result) = cp;
        Heading = part.Port is { } port ? $"{port.Name} · component port {port.Number}" : $"{part.Refdes} · component port (left out)";
        Rows.Add(new C3dPropertyRow("Part", part.Refdes + (part.Pin is { } pin ? $", pad {pin} to pad {part.ReferencePin}" : "")));
        if (result?.Resolved is { } r) Rows.Add(new C3dPropertyRow("Between", $"{r.NegativeObject} → {r.PositiveObject}"));
        if (part.Port is not null)
        {
            Rows.Add(new C3dPropertyRow("Gap", $"{Length(part.GapM)} along {(part.AlongX ? "x" : "y")}"));
            Rows.Add(new C3dPropertyRow("Width in use", $"{Length(part.WidthM)} across the gap"));
        }
        if ((part.Refusal ?? result?.Refusal) is { } why) Rows.Add(new C3dPropertyRow("Left out", why));
        Rows.Add(new C3dPropertyRow("Place", "From the part's pads on the layout, at mid-copper height: it follows the layout, so it is not set here."));
        if (editor.IsViewOnly) return;
        _shownComponent = part.Refdes;
        IsComponentPortPage = true;
        var entry = editor.ComponentPortEntry(part.Refdes);
        ComponentZ0 = entry?.Z0 ?? "";
        ComponentWidth = entry?.WidthUm is { } w ? Spell((long)Math.Round(w * editor.Document.DbuPerMicron)) : "";
    }

    /// <summary>Enter or lost focus on Z0 or Width: blank is the default (50 Ω; the pads' shared width).</summary>
    public void CommitComponentPort()
    {
        if (!IsComponentPortPage || _shownComponent is not { } refdes) return;
        var doc = editor.Document;
        var entry = editor.ComponentPortEntry(refdes);
        string? z0 = string.IsNullOrWhiteSpace(ComponentZ0) ? null : ComponentZ0.Trim();
        double? widthUm = null;
        if (!string.IsNullOrWhiteSpace(ComponentWidth))
        {
            if (!LayoutUnits.TryParse(ComponentWidth, doc.DisplayUnit, doc.DbuPerMicron, out long dbu))
            {
                Error = $"A port's width is a length, in {LayoutUnits.Suffix(doc.DisplayUnit)} unless a unit is written; blank is the pads' shared width.";
                return;
            }
            widthUm = (double)dbu / doc.DbuPerMicron;
        }
        if (z0 == entry?.Z0 && widthUm == entry?.WidthUm) { Error = ""; return; }
        Error = editor.SetComponentPortOverride(refdes, z0, widthUm) ?? "";
    }
}
