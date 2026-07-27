using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Thin Avalonia host for <see cref="LayoutRulerRenderer"/> — a top (Horizontal) or left (Vertical)
/// ruler strip. Driven entirely from code-behind by <c>LayoutEditorView</c> (via
/// <see cref="SetViewport"/>/<see cref="SetUnits"/>/<see cref="SetCursorWorld"/>), mirroring
/// <see cref="LayoutCanvas"/>'s own viewport exactly — there is no independent state here.
/// </summary>
public sealed class LayoutRulerControl : Control
{
    public LayoutRulerOrientation Orientation { get; set; } = LayoutRulerOrientation.Horizontal;

    private double _panX, _panY, _zoom = 1.0, _canvasWidth, _canvasHeight;
    private int _dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private LayoutUnit _displayUnit = LayoutUnit.Um;
    private double? _cursorWorld;
    private ColorTheme _activeTheme = ColorTheme.BuiltIn;

    public LayoutRulerControl()
    {
        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Mirrors <see cref="LayoutCanvas"/>'s current pan/zoom and pixel size exactly.</summary>
    public void SetViewport(double panX, double panY, double zoom, double canvasWidth, double canvasHeight)
    {
        _panX = panX; _panY = panY; _zoom = zoom; _canvasWidth = canvasWidth; _canvasHeight = canvasHeight;
        InvalidateVisual();
    }

    public void SetUnits(int dbuPerMicron, LayoutUnit displayUnit)
    {
        _dbuPerMicron = dbuPerMicron; _displayUnit = displayUnit;
        InvalidateVisual();
    }

    /// <summary>The single relevant coordinate for this ruler's axis, or null when the pointer has
    /// left the canvas.</summary>
    public void SetCursorWorld(double? value)
    {
        _cursorWorld = value;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        _activeTheme = ThemeService.Active;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _activeTheme = ThemeService.Active;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme = LayoutRenderTheme.FromTheme(_activeTheme, variant);
        var vp = new LayoutViewport(_panX, _panY, _zoom, _canvasWidth, _canvasHeight);

        context.Custom(new RulerDrawOperation(new Rect(Bounds.Size), Orientation, vp, _dbuPerMicron, _displayUnit, _cursorWorld, theme));
    }

    private sealed class RulerDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly LayoutRulerOrientation _orientation;
        private readonly LayoutViewport _vp;
        private readonly int _dbuPerMicron;
        private readonly LayoutUnit _displayUnit;
        private readonly double? _cursorWorld;
        private readonly LayoutRenderTheme _theme;

        public RulerDrawOperation(
            Rect bounds, LayoutRulerOrientation orientation, LayoutViewport vp,
            int dbuPerMicron, LayoutUnit displayUnit, double? cursorWorld, LayoutRenderTheme theme)
        {
            _bounds = bounds; _orientation = orientation; _vp = vp;
            _dbuPerMicron = dbuPerMicron; _displayUnit = displayUnit; _cursorWorld = cursorWorld; _theme = theme;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            LayoutRulerRenderer.Draw(
                lease.SkCanvas, (_bounds.Width, _bounds.Height), _orientation,
                _vp, _dbuPerMicron, _displayUnit, _cursorWorld, _theme);
        }

        public void Dispose() { }
    }
}
