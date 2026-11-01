// brief-sar-12 R-sar12-2 — Place Against Phantom…: the phantom and its face, the separation, the tilt and an in-plane offset,
// for the selection. Every rule is C3dDevicePlacement's; this is the dialog's state. Lengths are typed in the document's display
// unit (a bare number), or with a unit of their own (10 mm).

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class DevicePlacementDialogViewModel : ObservableObject
{
    private readonly C3dEditorViewModel _editor;
    private readonly IReadOnlyList<string> _movers;

    /// <summary>Raised with true once placed, false on Cancel.</summary>
    public event Action<bool>? CloseRequested;

    public DevicePlacementDialogViewModel(C3dEditorViewModel editor, IReadOnlyList<string> movers)
    {
        _editor = editor;
        _movers = movers;
        Phantoms = editor.PhantomNames();
        SelectedPhantom = Phantoms.FirstOrDefault();
        SeparationText = "10";
        TiltText = "0";
        OffsetText = "0, 0, 0";
    }

    public string Title => "Place Against Phantom";
    public string MoversText => "Placing: " + string.Join(", ", _movers);
    public string UnitText => $"Lengths in {LayoutUnits.Suffix(_editor.Document.DisplayUnit)} unless typed with a unit.";

    public IReadOnlyList<string> Phantoms { get; }
    public IReadOnlyList<C3dAxis> Axes { get; } = Enum.GetValues<C3dAxis>();

    [ObservableProperty] private string? _selectedPhantom;
    [ObservableProperty] private IReadOnlyList<string> _faces = [];
    [ObservableProperty] private string? _selectedFace;
    [ObservableProperty] private string _separationText = "";
    [ObservableProperty] private string _tiltText = "";
    [ObservableProperty] private C3dAxis _tiltAxis = C3dAxis.X;
    [ObservableProperty] private string _offsetText = "";
    [ObservableProperty] private string? _error;

    partial void OnSelectedPhantomChanged(string? value)
    {
        Faces = value is null ? [] : _editor.PhantomFaces(value);
        SelectedFace = Faces.FirstOrDefault();
    }

    /// <summary>The request the fields make, or why not.</summary>
    public (C3dPlacementRequest? Request, string? Error) Request()
    {
        if (SelectedPhantom is not { } phantom || SelectedFace is not { } face) return (null, "Choose a phantom and one of its faces.");
        var doc = _editor.Document;
        if (!LayoutUnits.TryParse(SeparationText.Trim(), doc.DisplayUnit, doc.DbuPerMicron, out long separation))
            return (null, $"'{SeparationText}' is not a length.");
        if (!NumericText.TryParseDouble(TiltText.Trim(), out double tilt) || !double.IsFinite(tilt))
            return (null, $"'{TiltText}' is not an angle in degrees.");
        var parts = OffsetText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        long[] off = new long[3];
        if (parts.Length is not (0 or 3)) return (null, "Give the offset as three lengths, x, y and z (its part along the face's normal is ignored).");
        for (int i = 0; i < parts.Length; i++)
            if (!LayoutUnits.TryParse(parts[i], doc.DisplayUnit, doc.DbuPerMicron, out off[i])) return (null, $"'{parts[i]}' is not a length.");
        return (new C3dPlacementRequest(_movers, phantom, face, separation, tilt, TiltAxis, new C3dPoint3(off[0], off[1], off[2])), null);
    }

    [RelayCommand]
    private void Place()
    {
        var (request, error) = Request();
        Error = error ?? _editor.PlaceAgainstPhantom(request!);
        if (Error is null) CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
