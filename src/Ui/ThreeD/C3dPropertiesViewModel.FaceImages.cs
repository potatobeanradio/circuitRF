// brief-em3d-101 R-em3d101-9d — the Inspector's section for an image mapped onto a face: File (Browse…, Reveal), Pixels, its OWN
// Transparency (slider, box and Default from C3dTransparency.Max — a drag previews live and is one entry, written on release), Rotation (degrees, and
// ⟲ 90° / ⟳ 90°), Width and Height with Keep aspect, Offset (right, up), Fit to Face, Hidden and Remove Image. Shown for the image
// selected in the view or its tree row, and below a face's readout when that face carries one. Every write is the editor's
// SetFaceImage (one undo entry), and Remove Image is RemoveFaceImages — the one remover.

using System.Globalization;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dPropertiesViewModel
{
    [ObservableProperty] private bool _hasFaceImage;
    [ObservableProperty] private string _faceImageTitle = "";
    [ObservableProperty] private string _faceImageFile = "";
    [ObservableProperty] private string _faceImagePixels = "";
    [ObservableProperty] private double _faceImageTransparencySlider;
    [ObservableProperty] private string _faceImageTransparencyText = "";
    [ObservableProperty] private string _faceImageRotation = "";
    [ObservableProperty] private string _faceImageWidth = "";
    [ObservableProperty] private string _faceImageHeight = "";
    [ObservableProperty] private bool _faceImageKeepAspect = true;
    [ObservableProperty] private string _faceImageOffsetRight = "";
    [ObservableProperty] private string _faceImageOffsetUp = "";
    [ObservableProperty] private bool _faceImageHidden;
    [ObservableProperty] private string? _faceImageError;
    [ObservableProperty] private string? _faceImageProblem;

    private (int Index, string Face)? _faceImage;

    /// <summary>A slider drag's value not yet written (the preview shows it); written on release.</summary>
    private int? _faceImageDragged;

    /// <summary>The drag's value across a reload: each preview's scene reloads this panel mid-drag, and the row must come back
    /// showing the value being dragged, not the document's (as the object's Transparency row does).</summary>
    private int? _faceImageDraggedAcrossReload;

    private void ClearFaceImage()
    {
        HasFaceImage = false;
        _faceImage = null;
        FaceImageError = null;
        _faceImageDraggedAcrossReload = _faceImageDragged;
        _faceImageDragged = null;
    }

    /// <summary>The section for the image on face <paramref name="face"/> of the object at <paramref name="index"/>.</summary>
    private void LoadFaceImage(int index, string face)
    {
        if (editor.FaceImageAt(index, face) is not { } fi) return;
        var doc = editor.Document;
        _faceImage = (index, face);
        HasFaceImage = true;
        FaceImageTitle = $"Image on {doc.Objects[index].Name}/{face}";
        FaceImageFile = fi.Image.Path;
        var px = C3dImages.PixelSize(C3dImages.Resolve(editor.FilePath, fi.Image.Path));
        FaceImagePixels = px is { } p ? $"{p.Width} × {p.Height}" : "File not found";
        FaceImageTransparencySlider = fi.Transparency ?? 0;
        FaceImageTransparencyText = fi.Transparency?.ToString(CultureInfo.InvariantCulture) ?? "";
        FaceImageRotation = fi.RotationDeg.ToString("0.###", CultureInfo.InvariantCulture);
        string L(long dbu) => C3dDimension.Spell(dbu, doc.DisplayUnit, doc.DbuPerMicron);
        FaceImageWidth = fi.Width is { } w ? L(w) : "";
        FaceImageHeight = fi.Height is { } h ? L(h) : "";
        FaceImageOffsetRight = L(fi.Offset.U);
        FaceImageOffsetUp = L(fi.Offset.V);
        FaceImageHidden = fi.Hidden;
        FaceImageProblem = editor.FaceImageProblem(doc.Objects[index].Name, fi);
        FaceImageError = null;
        if (_faceImageDraggedAcrossReload is { } dragged && editor.IsPreviewingFaceImageTransparency(index, face))
        {
            _faceImageDragged = dragged;
            FaceImageTransparencySlider = dragged;
            FaceImageTransparencyText = dragged.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>After a reload: a drag whose image is no longer shown here ends, so the view stops drawing a value nothing will
    /// write.</summary>
    private void EndStrayFaceImagePreview()
    {
        _faceImageDraggedAcrossReload = null;
        if (_faceImageDragged is null) editor.EndFaceImageTransparencyPreview();
    }

    partial void OnFaceImageTransparencySliderChanged(double value)
    {
        if (_loading || _faceImage is not { } f) return;
        int v = (int)Math.Clamp(Math.Round(value), 0, C3dTransparency.Max);
        _faceImageDragged = v;
        _loading = true;
        try { FaceImageTransparencyText = v == 0 ? "" : v.ToString(CultureInfo.InvariantCulture); }
        finally { _loading = false; }
        editor.PreviewFaceImageTransparency(f.Index, f.Face, v == 0 ? null : v);
    }

    private void Write(string what, Action<C3dFaceImage> change)
    {
        if (_faceImage is not { } f) return;
        editor.SetFaceImage(f.Index, f.Face, $"{what} of the image on {editor.Document.Objects[f.Index].Name}/{f.Face}", change);
    }

    /// <summary>The slider's release (and a key on it): its value written, one entry per drag.</summary>
    public void CommitFaceImageTransparencySlider()
    {
        if (_loading || !HasFaceImage || _faceImageDragged is not { } v) return;
        _faceImageDragged = null;
        Write("Transparency", fi => fi.Transparency = v == 0 ? null : v);
    }

    /// <summary>The box: a whole percent in range, or red and unchanged.</summary>
    public void CommitFaceImageTransparencyText()
    {
        if (!HasFaceImage) return;
        if (string.IsNullOrWhiteSpace(FaceImageTransparencyText)) { Write("Transparency", fi => fi.Transparency = null); return; }
        if (!int.TryParse(FaceImageTransparencyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || !C3dTransparency.InRange(v))
        {
            FaceImageError = C3dTransparency.OutOfRange("The image", int.TryParse(FaceImageTransparencyText, out int n) ? n : -1);
            return;
        }
        Write("Transparency", fi => fi.Transparency = v);
    }

    [RelayCommand] private void FaceImageDefaultTransparency() => Write("Transparency", fi => fi.Transparency = null);

    public void CommitFaceImageRotation()
    {
        if (!HasFaceImage) return;
        if (!NumericText.TryParseDouble(FaceImageRotation, out double deg) || !double.IsFinite(deg))
        {
            FaceImageError = "A rotation is a number of degrees.";
            return;
        }
        Write("Rotate", fi => fi.RotationDeg = Normal(deg));
    }

    [RelayCommand] private void FaceImageRotateLeft() => Write("Rotate", fi => fi.RotationDeg = Normal(fi.RotationDeg + 90));
    [RelayCommand] private void FaceImageRotateRight() => Write("Rotate", fi => fi.RotationDeg = Normal(fi.RotationDeg - 90));

    private static double Normal(double deg)
    {
        double d = deg % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return Math.Abs(d) < 1e-9 ? 0 : d;
    }

    /// <summary>Width or height committed: explicit from now on; with Keep aspect the other follows at the image's own aspect. A box
    /// left EMPTY is that size unstated — it follows the other at the image's aspect — and both empty is the default, fitted to
    /// the face (its offset kept; Fit to Face clears that too).</summary>
    public void CommitFaceImageSize(bool width)
    {
        if (_faceImage is not { } f || editor.FaceImageAt(f.Index, f.Face) is not { } fi) return;
        var doc = editor.Document;
        string text = width ? FaceImageWidth : FaceImageHeight;
        if (string.IsNullOrWhiteSpace(text))
        {
            FaceImageError = null;
            if ((width ? fi.Width : fi.Height) is null) return;
            Write(string.IsNullOrWhiteSpace(width ? FaceImageHeight : FaceImageWidth) ? "Fit" : "Resize", x =>
            {
                if (width) x.Width = null; else x.Height = null;
                if (string.IsNullOrWhiteSpace(width ? FaceImageHeight : FaceImageWidth)) (x.Width, x.Height) = (null, null);
            });
            return;
        }
        var d = C3dDimension.Parse(text, doc.DisplayUnit, doc.DbuPerMicron);
        if (d.Kind != C3dDimensionKind.Value || d.Dbu <= 0) { FaceImageError = d.Why ?? "A width and a height are above zero."; return; }
        double aspect = C3dImages.PixelSize(C3dImages.Resolve(editor.FilePath, fi.Image.Path)) is { } px ? (double)px.Width / px.Height
                      : fi.Width is { } w0 && fi.Height is { } h0 && h0 > 0 ? (double)w0 / h0 : 4.0 / 3.0;
        long other = (long)Math.Round(width ? d.Dbu / aspect : d.Dbu * aspect);
        Write("Resize", x =>
        {
            if (width) { x.Width = d.Dbu; if (FaceImageKeepAspect || x.Height is null) x.Height = Math.Max(1, other); }
            else { x.Height = d.Dbu; if (FaceImageKeepAspect || x.Width is null) x.Width = Math.Max(1, other); }
        });
    }

    public void CommitFaceImageOffset()
    {
        if (!HasFaceImage) return;
        var doc = editor.Document;
        var r = C3dDimension.Parse(FaceImageOffsetRight, doc.DisplayUnit, doc.DbuPerMicron);
        var u = C3dDimension.Parse(FaceImageOffsetUp, doc.DisplayUnit, doc.DbuPerMicron);
        if (r.Kind != C3dDimensionKind.Value || u.Kind != C3dDimensionKind.Value) { FaceImageError = r.Why ?? u.Why; return; }
        Write("Move", fi => fi.Offset = new C3dPoint2(r.Dbu, u.Dbu));
    }

    /// <summary>Fit to Face: no size and no offset — fitted, centred.</summary>
    [RelayCommand] private void FitFaceImage() => Write("Fit", fi => { fi.Width = null; fi.Height = null; fi.Offset = default; });

    partial void OnFaceImageHiddenChanged(bool value)
    {
        if (_loading || _faceImage is not { } f) return;
        editor.SetFaceImagesHidden([(f.Index, f.Face)], value);
    }

    [RelayCommand] private void RemoveFaceImage() { if (_faceImage is { } f) editor.RemoveFaceImages([f]); }

    [RelayCommand]
    private async Task BrowseFaceImage()
    {
        if (_faceImage is not { } f || editor.PickImageFile is not { } pick || await pick("Choose Image") is not { } file) return;
        string full = Path.GetFullPath(file);
        CircuitRF.Render.Scene3D.Scene3DTextures.Refresh(full);
        Write("Re-point", fi => fi.Image.Path = C3dImages.Store(editor.FilePath, full));
    }

    [RelayCommand]
    private void RevealFaceImage()
    {
        if (_faceImage is { } f && editor.FaceImageAt(f.Index, f.Face) is { } fi && C3dImages.Resolve(editor.FilePath, fi.Image.Path) is { } file)
            FileReveal.Reveal(file);
    }
}
