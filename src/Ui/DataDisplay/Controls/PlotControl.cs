// ================================================================
//  PlotControl.cs  —  Render-only Avalonia control for a single Plot
//
//  7.1b: render-only shell — no pan/zoom, no pointer handlers, no
//  context menus.  Pan/zoom/interaction comes in 7.1c.
//
//  Pattern mirrors SchematicCanvas: Control + DirectProperty +
//  ICustomDrawOperation + ISkiaSharpApiLeaseFeature (Avalonia.Skia).
//
//  TODO 7.x: wire RenderTheme to circuitRF ColorTheme/.ccolor.
//  For now PlotTheme is selected from Application.ActualThemeVariant.
// ================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay.Controls
{
    public sealed class PlotControl : Control
    {
        // ---- DirectProperties ------------------------------------------

        public static readonly DirectProperty<PlotControl, Plot?> PlotProperty =
            AvaloniaProperty.RegisterDirect<PlotControl, Plot?>(
                nameof(Plot),
                o => o.Plot,
                (o, v) => o.Plot = v);

        public static readonly DirectProperty<PlotControl, RenderTheme> PlotThemeProperty =
            AvaloniaProperty.RegisterDirect<PlotControl, RenderTheme>(
                nameof(PlotTheme),
                o => o.PlotTheme,
                (o, v) => o.PlotTheme = v);

        // ---- Backing fields --------------------------------------------

        private Plot? _plot;
        private RenderTheme _plotTheme = RenderTheme.Light;

        public Plot? Plot
        {
            get => _plot;
            set
            {
                SetAndRaise(PlotProperty, ref _plot, value);
                InvalidateVisual();
            }
        }

        public RenderTheme PlotTheme
        {
            get => _plotTheme;
            set
            {
                SetAndRaise(PlotThemeProperty, ref _plotTheme, value);
                InvalidateVisual();
            }
        }

        // ---- Constructor -----------------------------------------------

        public PlotControl()
        {
            // Pick initial theme from the current app theme variant
            _plotTheme = ResolveTheme();

            // Re-render when the user toggles light/dark
            ActualThemeVariantChanged += (_, _) =>
            {
                PlotTheme = ResolveTheme();
            };
        }

        // ---- Render ----------------------------------------------------

        public override void Render(DrawingContext context)
        {
            if (_plot is null) return;

            var op = new PlotDrawOperation(
                _plot,
                _plotTheme,
                new Rect(Bounds.Size));

            context.Custom(op);
        }

        // ---- Theme resolution ------------------------------------------

        private static RenderTheme ResolveTheme()
        {
            // TODO 7.x: wire RenderTheme to circuitRF ColorTheme/.ccolor
            var variant = Application.Current?.ActualThemeVariant;
            return variant == ThemeVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        }

        // ================================================================
        //  ICustomDrawOperation
        // ================================================================

        private sealed class PlotDrawOperation : ICustomDrawOperation
        {
            private readonly Plot        _plot;
            private readonly RenderTheme _theme;
            private readonly Rect        _bounds;

            public PlotDrawOperation(Plot plot, RenderTheme theme, Rect bounds)
            {
                _plot   = plot;
                _theme  = theme;
                _bounds = bounds;
            }

            public Rect Bounds => _bounds;

            public bool HitTest(Point p) => _bounds.Contains(p);

            public bool Equals(ICustomDrawOperation? other) => false;

            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature is null) return;

                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;

                // Clear to theme background
                canvas.Clear(_theme.BackgroundColor);

                PlotRenderer.Draw(
                    canvas,
                    (_bounds.Width, _bounds.Height),
                    _plot,
                    PlotDetail.Full,
                    _theme);
            }
        }
    }
}
