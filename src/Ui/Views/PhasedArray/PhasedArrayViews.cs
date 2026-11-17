// brief-paa-9 — the Phased Array window's canvases. Each is a thin Avalonia host for one of src/Render/PhasedArray's renderers: it builds the
// scene from the view model, hands it to the renderer with the theme, and answers the pointer with the layout the renderer drew with.
// Every one carries Copy and Export… on its context menu: the picture as the canvas draws it, at the canvas's size, as PDF and SVG
// first and a 2x PNG beside them (the (u,v) and scan maps are rasters inside the vector document — a heat map is pixels).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Render;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.PhasedArray;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.Theming;
using Material.Icons;
using Material.Icons.Avalonia;
using SkiaSharp;

namespace CircuitRF.Ui.Views.PhasedArray;

/// <summary>
/// A control that paints itself with Skia from the tool's view model and repaints on its Redraw.
/// <para><b>THE DRAWING RUNS ON THE RENDER THREAD, so it may read nothing live.</b> <see cref="Prepare"/> runs on the UI thread in
/// <see cref="Render"/> and reads everything the picture needs — the view model, and this control's own styled properties — into a scene;
/// the render thread only runs the drawing it returns. The first version read <c>Gamma</c> and <c>Kind</c> (styled properties) inside the
/// drawing: Avalonia refuses a property read off the UI thread, so the Scan maps, Failures and Frequency canvases threw on every frame,
/// the window presented older frames in between and the page buttons were seen flashing between the old page and the new. Headless
/// rendering runs on the UI thread, where the read is legal, which is why no snapshot ever showed it.</para>
/// </summary>
public abstract class PhasedArrayCanvas : Control
{
    private PhasedArrayToolViewModel? _vm;

    protected PhasedArrayCanvas()
    {
        ClipToBounds = true;
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Redraw -= OnRedraw;
            _vm = DataContext as PhasedArrayToolViewModel;
            if (_vm is not null) _vm.Redraw += OnRedraw;
            InvalidateVisual();
        };
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();

        var copy = new MenuItem { Header = "Copy", Icon = new MaterialIcon { Kind = MaterialIconKind.ContentCopy } };
        copy.Click += async (_, _) => await CopyAsync();
        var export = new MenuItem { Header = "Export…", Icon = new MaterialIcon { Kind = MaterialIconKind.FileExportOutline } };
        export.Click += async (_, _) => await ExportAsync();
        ContextMenu = new ContextMenu { ItemsSource = new[] { copy, export } };
    }

    protected PhasedArrayToolViewModel? Vm => _vm;

    /// <summary>The picture's name: the suggested file name of an Export, and the circuitRF payload of a Copy.</summary>
    protected abstract string PictureName { get; }

    /// <summary>The canvas as a page of its own size, in the clipboard's colours (Settings ▸ copy colour mode): artwork, so no hover
    /// mark and no drag rectangle.</summary>
    private (Action<SKCanvas> Compose, PagePlacement Page) Composition()
    {
        var (variant, _) = ClipboardRenderPolicy.Resolve();
        var theme = variant == ColorVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        float w = (float)Math.Max(Bounds.Width, 1), h = (float)Math.Max(Bounds.Height, 1);
        var draw = Prepare(theme, exporting: true);
        return (canvas => Draw(canvas, new SKRect(0, 0, w, h), draw, theme), new PagePlacement(w, h, 0));
    }

    /// <summary>Copy: PDF, SVG and a 2x bitmap on the clipboard together, through the Data Display's own writer.</summary>
    public async Task CopyAsync()
    {
        var (compose, page) = Composition();
        byte[] pdf = PlotDocumentWriter.BuildPdfBytes(compose, page);
        string svg = PlotDocumentWriter.BuildSvgString(compose, page);
        Avalonia.Media.Imaging.Bitmap? bitmap = null;
        try
        {
            if (PlotDocumentWriter.BuildPngBytes(compose, page, 2f) is { } png)
            {
                using var ms = new System.IO.MemoryStream(png);
                bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
            }
        }
        catch (Exception) { bitmap = null; }   // a raster failure never costs the vector formats
        await PlotExporter.SetClipboardDataAsync(this, pdf, svg, PictureName, bitmap, page.Width, page.Height);
    }

    /// <summary>Export…: PDF, SVG or PNG, chosen by the file type.</summary>
    public async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Plot",
            SuggestedFileName = PictureName,
            FileTypeChoices =
            [
                new FilePickerFileType("PDF Document") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] },
                new FilePickerFileType("SVG Image") { Patterns = ["*.svg"], MimeTypes = ["image/svg+xml"] },
                new FilePickerFileType("PNG Image") { Patterns = ["*.png"], MimeTypes = ["image/png"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var (compose, page) = Composition();
        try
        {
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) PlotDocumentWriter.WritePdf(path, compose, page);
            else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                System.IO.File.WriteAllBytes(path, PlotDocumentWriter.BuildPngBytes(compose, page, 2f) ?? []);
            else PlotDocumentWriter.WriteSvg(path, compose, page);
            Vm?.ReportExport($"Exported {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Vm?.ReportExport($"Export failed: {ex.Message}");
        }
    }

    private void OnRedraw(object? sender, EventArgs e) => InvalidateVisual();

    protected RenderTheme PlotTheme => ActualThemeVariant == ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;

    /// <summary>
    /// ON THE UI THREAD: reads everything the picture needs and returns the drawing, which may touch nothing but what it captured — it
    /// runs on the render thread. Null when there is nothing to draw (the background is drawn). <paramref name="exporting"/>: a copy or an
    /// export, so artwork — no hover mark, no drag rectangle, and the hit-test layout left as the screen drew it.
    /// </summary>
    protected abstract Action<SKCanvas, SKRect>? Prepare(RenderTheme theme, bool exporting);

    /// <summary>The drawing, or the background when there is none — and the background when the drawing fails, so one bad frame is a
    /// blank pane rather than a frame the window cannot present.</summary>
    private static void Draw(SKCanvas canvas, SKRect bounds, Action<SKCanvas, SKRect>? draw, RenderTheme theme)
    {
        if (draw is not null)
        {
            int save = canvas.Save();
            try { draw(canvas, bounds); return; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PhasedArrayCanvas] draw failed: {ex}"); }
            finally { canvas.RestoreToCount(save); }
        }
        using var bg = new SKPaint { Color = theme.BackgroundColor };
        canvas.DrawRect(bounds, bg);
    }

    public override void Render(DrawingContext context)
    {
        var theme = PlotTheme;
        Action<SKCanvas, SKRect>? draw;
        try { draw = Prepare(theme, exporting: false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PhasedArrayCanvas] prepare failed: {ex}"); draw = null; }
        context.Custom(new Op(new Rect(Bounds.Size), draw, theme));
    }

    private sealed class Op(Rect bounds, Action<SKCanvas, SKRect>? draw, RenderTheme theme) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => true;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            Draw(lease.SkCanvas, new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height), draw, theme);
        }
    }
}

/// <summary>R-paa9-2 — the aperture: click switches an element off or on, a drag selects a rectangle, hover names the element.</summary>
public sealed class ApertureCanvas : PhasedArrayCanvas
{
    private ApertureLayout? _layout;
    private Point? _down;
    private SKRect? _marquee;
    private int _hover = -1;

    public ApertureCanvas()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;   // a click takes the keyboard, so Q reaches the window rather than a picker that had it
    }

    protected override string PictureName => $"{Vm?.Instance.InstanceName} aperture".Trim();

    /// <summary>The aperture scene of the view model's frame.</summary>
    public static ApertureScene? SceneOf(PhasedArrayToolViewModel vm, int hover = -1, SKRect? marquee = null)
    {
        if (vm.Frame is not { } f) return null;
        var lattice = f.State.Lattice;
        int n = lattice.Count;
        var (values, unit) = vm.ApertureValues();
        var finite = values.Where(double.IsFinite).ToArray();
        bool cyclic = vm.Quantity == ApertureQuantity.Phase;
        double min = cyclic ? -180 : vm.Quantity is ApertureQuantity.Amplitude or ApertureQuantity.GammaActive ? 0 : finite.DefaultIfEmpty(0).Min();
        double max = cyclic ? 180 : vm.Quantity == ApertureQuantity.Amplitude ? 1 : finite.DefaultIfEmpty(1).Max();
        if (vm.Quantity == ApertureQuantity.GammaActive) max = Math.Max(max, 1e-3);
        string title = vm.Quantities.FirstOrDefault(q => q.Value == vm.Quantity)?.Label ?? "";
        return new ApertureScene(lattice.Rows, lattice.Cols, lattice.Dx, lattice.Dy,
                                 [.. Enumerable.Range(0, n).Select(e => lattice.Position(e).X)], [.. Enumerable.Range(0, n).Select(e => lattice.Position(e).Y)],
                                 values, [.. Enumerable.Range(0, n).Select(e => !lattice.IsOn(e))], min, max, cyclic, title, unit)
        {
            Selected = vm.Selected.Select(k => k - 1).ToHashSet(),
            Hover = hover,
            Marquee = marquee,
        };
    }

    protected override Action<SKCanvas, SKRect>? Prepare(RenderTheme theme, bool exporting)
    {
        if (Vm is not { } vm || SceneOf(vm, exporting ? -1 : _hover, exporting ? null : _marquee) is not { } scene) return null;
        return (canvas, bounds) =>
        {
            var layout = ArrayRenderers.DrawAperture(canvas, bounds, scene, theme);
            if (!exporting) Volatile.Write(ref _layout, layout);   // the hit test reads it on the UI thread
        };
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _down = e.GetPosition(this);
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_down is { } d && (Math.Abs(p.X - d.X) > 4 || Math.Abs(p.Y - d.Y) > 4))
        {
            _marquee = new SKRect((float)Math.Min(d.X, p.X), (float)Math.Min(d.Y, p.Y), (float)Math.Max(d.X, p.X), (float)Math.Max(d.Y, p.Y));
            InvalidateVisual();
            return;
        }
        int hit = _layout?.HitTest(p.X, p.Y) ?? -1;
        if (hit != _hover)
        {
            _hover = hit;
            Vm?.HoverElement(hit + 1);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_down is null || Vm is not { } vm) return;
        e.Pointer.Capture(null);
        var p = e.GetPosition(this);
        if (_marquee is { } m && _layout is { } layout)
        {
            vm.Selected.Clear();
            foreach (int k in layout.Within(m)) vm.Selected.Add(k + 1);
        }
        else if (_layout?.HitTest(p.X, p.Y) is int hit and >= 0) vm.ToggleElement(hit + 1);
        else vm.ClearSelection();   // a click on the aperture's empty space or its legend: nothing selected
        _down = null;
        _marquee = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        Vm?.HoverElement(0);
        InvalidateVisual();
    }
}

/// <summary>R-paa9-3 — the (u,v) map: drag the beam marker to steer, when the weights are a steer().</summary>
public sealed class UvMapCanvas : PhasedArrayCanvas
{
    private UvLayout? _layout;
    private bool _dragging;

    protected override string PictureName => $"{Vm?.Instance.InstanceName} realized gain in (u, v)".Trim();

    /// <summary>The (u,v) scene of the view model's frame.</summary>
    public static UvScene? SceneOf(PhasedArrayToolViewModel vm)
    {
        if (vm.Frame is not { } f) return null;
        var m = f.Map;
        (double, double)? beam = vm.SteerAngles is { } s
            ? (Math.Sin(s.ThetaDeg * Math.PI / 180) * Math.Cos(s.PhiDeg * Math.PI / 180), Math.Sin(s.ThetaDeg * Math.PI / 180) * Math.Sin(s.PhiDeg * Math.PI / 180))
            : (m.PeakU, m.PeakV);
        return new UvScene(m.Db, m.Nu, m.Nv, m.Du, m.Dv, Math.Ceiling(m.MaxDb), 40, "dBi")
        {
            Beam = beam,
            BeamDraggable = vm.Steering.Draggable,
            GratingLobes = PhasedArrayFrames.GratingLobes(f.State.Lattice, f.State.Hz, beam.Value.Item1, beam.Value.Item2),
            ScanLimitSin = Math.Sin(Math.Clamp(vm.ScanLimitDeg, 0, 90) * Math.PI / 180),
            // brief-pch-9 R-pch9-7 — the channel's rays at this end and the codebook's beams
            Rays = [.. vm.MapRays().Select(r => (r.U, r.V, r.Size))],
            CodebookBeams = vm.Channel is { Table: not null } ch && (ch.IsTx ? ch.Table.TxSteered : ch.Table.RxSteered) ? [.. ch.Beams.Select(b => (b.U, b.V))] : [],
            SelectedBeam = vm.Channel is { Table: not null } c2 && (c2.IsTx ? c2.Table.TxSteered : c2.Table.RxSteered) && c2.Chosen is { } sel ? (sel.U, sel.V) : null,
        };
    }

    /// <summary>The ray under <paramref name="p"/>, or -1.</summary>
    private int RayAt(Point p)
    {
        if (Vm is not { } vm || _layout is null) return -1;
        foreach (var (k, u, v, size) in vm.MapRays().OrderByDescending(r => r.Size))
        {
            var (x, y) = _layout.ToPixel(u, v);
            double r = ArrayRenderers.RayMinRadius + (ArrayRenderers.RayMaxRadius - ArrayRenderers.RayMinRadius) * size + 2;
            if ((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= r * r) return k;
        }
        return -1;
    }

    protected override Action<SKCanvas, SKRect>? Prepare(RenderTheme theme, bool exporting)
    {
        if (Vm is not { } vm || SceneOf(vm) is not { } scene) return null;
        return (canvas, bounds) =>
        {
            var layout = ArrayRenderers.DrawUvMap(canvas, bounds, scene, theme);
            if (!exporting) Volatile.Write(ref _layout, layout);   // the hit test reads it on the UI thread
        };
    }

    private bool NearBeam(Point p)
    {
        if (Vm is not { } vm || _layout is null || SceneOf(vm)?.Beam is not { } b) return false;
        var (x, y) = _layout.ToPixel(b.U, b.V);
        return Math.Abs(p.X - x) < 14 && Math.Abs(p.Y - y) < 14;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        if (Vm is not { } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!NearBeam(p))
        {
            // brief-pch-9 — a click on a ray selects its beams on the channel
            if (RayAt(p) is int ray and >= 0) { vm.SelectRay(ray); e.Handled = true; }
            return;
        }
        if (!vm.BeginDrag()) return;
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging && _layout is { } layout && Vm is { } vm)
        {
            var (u, v) = layout.ToUv(p.X, p.Y);
            vm.DragTo(u, v);
            InvalidateVisual();
            return;
        }
        bool near = NearBeam(p);
        int ray = near ? -1 : RayAt(p);
        Cursor = new Cursor(near && Vm?.Steering.Draggable == true ? StandardCursorType.SizeAll : ray >= 0 ? StandardCursorType.Hand : StandardCursorType.Arrow);
        ToolTip.SetTip(this, near ? Vm?.Steering.Refusal ?? "Drag to steer the beam."
                           : ray >= 0 && Vm?.Channel?.Table is { } t
                               ? $"Ray {ray + 1}: {t.Rays[ray].PowerDb:0.0} dB. Click to select its beams on {Vm.Channel.Channel} (BeamSelect=index)."
                               : null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Vm?.EndDrag();
    }
}

/// <summary>R-paa9-4 — the two principal cuts.</summary>
public sealed class CutsCanvas : PhasedArrayCanvas
{
    protected override string PictureName => $"{Vm?.Instance.InstanceName} principal cuts".Trim();

    public static IReadOnlyList<CutTraces> TracesOf(PhasedArrayToolViewModel vm)
        => vm.Frame is not { } f ? [] :
           [new CutTraces(f.Elevation.Plane, f.Elevation.OffsetDeg, f.Elevation.CoDb, f.Elevation.CrossDb, f.Elevation.IsotropicDb),
            new CutTraces(f.Azimuth.Plane, f.Azimuth.OffsetDeg, f.Azimuth.CoDb, f.Azimuth.CrossDb, f.Azimuth.IsotropicDb)];

    protected override Action<SKCanvas, SKRect>? Prepare(RenderTheme theme, bool exporting)
    {
        var traces = Vm is { } vm ? TracesOf(vm) : [];
        bool polar = Vm?.CutsPolar == true;
        return (canvas, bounds) => ArrayRenderers.DrawCuts(canvas, bounds, traces, polar, -50, theme);
    }
}

/// <summary>R-paa9-5 — a scan map: realized gain (<see cref="Gamma"/> false) or worst |Γ_act|.</summary>
public sealed class ScanMapCanvas : PhasedArrayCanvas
{
    public static readonly StyledProperty<bool> GammaProperty = AvaloniaProperty.Register<ScanMapCanvas, bool>(nameof(Gamma));

    public bool Gamma { get => GetValue(GammaProperty); set => SetValue(GammaProperty, value); }

    protected override string PictureName => $"{Vm?.Instance.InstanceName} scan map {(Gamma ? "worst active reflection" : "realized gain")}".Trim();

    public static ScanScene? SceneOf(PhasedArrayToolViewModel vm, bool gamma)
    {
        if (vm.ScanPoints is not { Count: > 0 } pts) return null;
        double[] values = [.. pts.Select(p => gamma ? p.WorstGamma : p.RealizedGainDbi)];
        var finite = values.Where(double.IsFinite).ToArray();
        double max = finite.DefaultIfEmpty(0).Max();
        double min = gamma ? 0 : Math.Max(finite.DefaultIfEmpty(0).Min(), max - 20);
        return new ScanScene([.. pts.Select(p => p.ThetaDeg)], [.. pts.Select(p => p.PhiDeg)], values, 5, 15, min, gamma ? Math.Max(max, 1e-3) : max,
                             gamma ? "Worst |Γ active|" : "Realized gain at the steer", gamma ? "" : "dBi", Reversed: gamma);
    }

    protected override Action<SKCanvas, SKRect>? Prepare(RenderTheme theme, bool exporting)
    {
        // Gamma is a styled property: read here, on the UI thread, never inside the drawing
        if (Vm is not { } vm || SceneOf(vm, Gamma) is not { } scene) return null;
        return (canvas, bounds) => ArrayRenderers.DrawScanMap(canvas, bounds, scene, theme);
    }
}

/// <summary>R-paa9-6 — the failures and frequency charts.</summary>
public sealed class ArrayChartCanvas : PhasedArrayCanvas
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ArrayChartCanvas, string>(nameof(Kind), "frequency");

    /// <summary>"frequency" or "failures".</summary>
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override string PictureName => $"{Vm?.Instance.InstanceName} {(Kind == "failures" ? "failures" : "beam against frequency")}".Trim();

    /// <summary>The failure trials, or with <paramref name="f"/> null the pointing against frequency — results already in hand, so this
    /// can run on the render thread.</summary>
    private static void PaintChart(SKCanvas canvas, SKRect bounds, FailureTrials? f, FrequencyPointing? q, RenderTheme theme)
    {
        if (f is not null)
        {
            var order = Enumerable.Range(0, f.GainChangeDb.Length).OrderBy(i => f.GainChangeDb[i]).ToArray();
            double[] x = [.. Enumerable.Range(1, order.Length).Select(i => (double)i)];
            var top = new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.MidY);
            var bottom = new SKRect(bounds.Left, bounds.MidY, bounds.Right, bounds.Bottom);
            ArrayRenderers.DrawChart(canvas, top, $"{f.Off} elements off at random, {order.Length} trials, sorted by gain change: peak gain against the intact array",
                                     "trial", "dB", [new ChartSeries(x, [.. order.Select(i => f.GainChangeDb[i])], ArrayRenderers.Accent(theme), "gain change", Markers: true)], theme);
            ArrayRenderers.DrawChart(canvas, bottom, "First sidelobe, same trials", "trial", "dB",
                                     [new ChartSeries(x, [.. order.Select(i => f.SidelobeDb[i])], ArrayRenderers.Second(theme), "first sidelobe", Markers: true)], theme);
        }
        else if (q is not null)
        {
            double[] ghz = [.. q.Hz.Select(h => h / 1e9)];
            ArrayRenderers.DrawChart(canvas, bounds, q.ArrayFactorOnly ? "Beam elevation against frequency (array factor)" : "Beam elevation against frequency",
                                     "frequency (GHz)", "θ of the peak (°)",
            [
                new ChartSeries(ghz, q.PhaseShifterThetaDeg, ArrayRenderers.Accent(theme), "phase shifters", Markers: true),
                new ChartSeries(ghz, q.DelayThetaDeg, ArrayRenderers.Second(theme), "true delays", Markers: true, Dashed: true),
            ], theme);
        }
    }

    protected override Action<SKCanvas, SKRect>? Prepare(RenderTheme theme, bool exporting)
    {
        // Kind is a styled property, and the results are the view model's: both read here, on the UI thread
        if (Vm is not { } vm) return null;
        var failures = Kind == "failures" ? vm.Failures : null;
        var frequency = Kind == "frequency" ? vm.Frequency : null;
        if (failures is null && frequency is null) return null;
        return (canvas, bounds) => PaintChart(canvas, bounds, failures, frequency, theme);
    }
}

/// <summary>A page shown when the tool's <see cref="PhasedArrayToolViewModel.Page"/> is the one named by the parameter.</summary>
public sealed class PageIs : Avalonia.Data.Converters.IValueConverter
{
    public static readonly PageIs Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is ArrayPage page && parameter is string name && page.ToString() == name;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>brief-pch-9 — the Beams list's chosen row in bold.</summary>
public sealed class ChosenWeight : Avalonia.Data.Converters.IValueConverter
{
    public static readonly ChosenWeight Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>brief-pch-9 — a progress bar that keeps its place: shown while busy, transparent otherwise.</summary>
public sealed class BusyOpacity : Avalonia.Data.Converters.IValueConverter
{
    public static readonly BusyOpacity Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is true ? 1.0 : 0.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
