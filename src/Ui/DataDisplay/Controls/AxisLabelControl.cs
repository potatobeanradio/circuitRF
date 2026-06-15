// ================================================================
//  AxisLabelControl.cs  —  Single vertical trace-label strip
//
//  Rendered via SkiaSharp for PDF/SVG export parity.
//  Width is set by the parent binding (StripLogicalWidth * ZoomLevel).
//  Height fills the parent row (same height as PlotControl).
//
//  The inner edge (facing the plot) carries a 3-px colour bar
//  matching the trace line colour.  The trace description is drawn
//  rotated 90° (right axis: top→bottom) or −90° (left axis:
//  bottom→top), centred in the remaining strip width.
//
//  DoubleTapped is NOT handled here; PlotContainerView checks
//  e.Source to distinguish label taps from plot taps.
// ================================================================

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay.Controls
{
    public class AxisLabelControl : Control
    {
        public const double StripLogicalWidth = 18.0;

        // ============================================================
        //  Trace property
        // ============================================================

        public static readonly DirectProperty<AxisLabelControl, Trace?> TraceProperty =
            AvaloniaProperty.RegisterDirect<AxisLabelControl, Trace?>(
                nameof(Trace), o => o.Trace, (o, v) => o.Trace = v);

        private Trace? _trace;
        public Trace? Trace
        {
            get => _trace;
            set { SetAndRaise(TraceProperty, ref _trace, value); InvalidateVisual(); }
        }

        // ============================================================
        //  IsRightSide property
        // ============================================================

        public static readonly StyledProperty<bool> IsRightSideProperty =
            AvaloniaProperty.Register<AxisLabelControl, bool>(nameof(IsRightSide));

        public bool IsRightSide
        {
            get => GetValue(IsRightSideProperty);
            set => SetValue(IsRightSideProperty, value);
        }

        // ============================================================
        //  ShowFilePrefix property
        // ============================================================

        public static readonly StyledProperty<bool> ShowFilePrefixProperty =
            AvaloniaProperty.Register<AxisLabelControl, bool>(nameof(ShowFilePrefix), defaultValue: true);

        /// <summary>
        /// When false, renders <see cref="Trace.ShortDescription"/> (no source-file prefix)
        /// instead of <see cref="Trace.Description"/>.  Set to false when the plot has
        /// only one data source and the prefix adds no information.
        /// </summary>
        public bool ShowFilePrefix
        {
            get => GetValue(ShowFilePrefixProperty);
            set => SetValue(ShowFilePrefixProperty, value);
        }

        // ============================================================
        //  CustomLabel property — overrides trace description when set
        // ============================================================

        public static readonly DirectProperty<AxisLabelControl, string?> CustomLabelProperty =
            AvaloniaProperty.RegisterDirect<AxisLabelControl, string?>(
                nameof(CustomLabel), o => o.CustomLabel, (o, v) => o.CustomLabel = v);

        private string? _customLabel;
        public string? CustomLabel
        {
            get => _customLabel;
            set { SetAndRaise(CustomLabelProperty, ref _customLabel, value); InvalidateVisual(); }
        }

        // ============================================================
        //  PlotTheme property
        // ============================================================

        public static readonly DirectProperty<AxisLabelControl, RenderTheme> PlotThemeProperty =
            AvaloniaProperty.RegisterDirect<AxisLabelControl, RenderTheme>(
                nameof(PlotTheme), o => o.PlotTheme, (o, v) => o.PlotTheme = v);

        private RenderTheme _theme = RenderTheme.Light;
        public RenderTheme PlotTheme
        {
            get => _theme;
            set { SetAndRaise(PlotThemeProperty, ref _theme, value); InvalidateVisual(); }
        }

        // ============================================================
        //  AppearanceRevision property — bumped by PlotContainerViewModel
        //  on every PlotNeedsRedraw so the control re-renders live when
        //  a trace color or description changes without a full strip rebuild.
        // ============================================================

        public static readonly DirectProperty<AxisLabelControl, int> AppearanceRevisionProperty =
            AvaloniaProperty.RegisterDirect<AxisLabelControl, int>(
                nameof(AppearanceRevision), o => o.AppearanceRevision, (o, v) => o.AppearanceRevision = v);

        private int _appearanceRevision;
        public int AppearanceRevision
        {
            get => _appearanceRevision;
            set { SetAndRaise(AppearanceRevisionProperty, ref _appearanceRevision, value); InvalidateVisual(); }
        }

        // ============================================================
        //  Constructor
        // ============================================================

        public AxisLabelControl()
        {
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        // ============================================================
        //  Render
        // ============================================================

        public override void Render(DrawingContext context)
        {
            if (_trace is null) return;

            var traceColor = RenderTheme.ToSKColor(_trace.Properties.LineColor);
            string desc    = ShowFilePrefix ? _trace.Description : _trace.ShortDescription;

            context.Custom(new LabelDrawOperation(
                new Rect(Bounds.Size),
                desc,
                traceColor,
                IsRightSide,
                _theme,
                _customLabel));
        }

        // ============================================================
        //  ICustomDrawOperation
        // ============================================================

        private sealed class LabelDrawOperation : ICustomDrawOperation
        {
            private readonly Rect        _bounds;
            private readonly string      _text;
            private readonly SKColor     _traceColor;
            private readonly bool        _isRight;
            private readonly RenderTheme _theme;
            private readonly string?     _customLabel;

            public LabelDrawOperation(
                Rect bounds, string text, SKColor color, bool isRight, RenderTheme theme,
                string? customLabel = null)
            {
                _bounds      = bounds;
                _text        = text;
                _traceColor  = color;
                _isRight     = isRight;
                _theme       = theme;
                _customLabel = customLabel;
            }

            public bool Equals(ICustomDrawOperation? other) => false;
            public Rect  Bounds              => _bounds;
            public bool  HitTest(Point p)    => _bounds.Contains(p);
            public void  Dispose()           { }

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature is null) return;

                using var lease  = leaseFeature.Lease();
                var canvas = lease.SkCanvas;
                float w = (float)_bounds.Width;
                float h = (float)_bounds.Height;

                // Font size scales with control height (= ViewHeight = plot height × zoom)
                // so text grows/shrinks with both zoom and manual plot resize.
                // Cap at 85 % of strip width so rotated text fits within the band.
                float cap        = w * 0.85f;
                float fontSizePx = System.Math.Clamp(h * 0.04f, System.Math.Min(6f, cap), cap);

                // Custom label (if set) overrides both text and colour.
                bool   useCustom = !string.IsNullOrEmpty(_customLabel);
                string displayText = useCustom ? _customLabel! : _text;
                SKColor textColor  = useCustom ? _theme.TextColor : _traceColor;

                using var font  = new SKFont(SkiaFonts.PlexRegular, fontSizePx);
                using var paint = new SKPaint { Color = textColor, IsAntialias = true };

                // Trim text to fit the available length (= strip height − margin).
                string text = displayText;
                float maxLen = h - 12f;
                while (text.Length > 1 && font.MeasureText(text) > maxLen)
                    text = text[..^1];
                if (text.Length < displayText.Length) text = text.TrimEnd() + "…";

                float tw = font.MeasureText(text);
                float cx = w / 2f;
                float cy = h / 2f;

                canvas.Save();
                canvas.Translate(cx, cy);
                canvas.RotateDegrees(_isRight ? 90f : -90f);
                canvas.DrawText(text, -tw / 2f, font.Size * 0.35f,
                    SKTextAlign.Left, font, paint);
                canvas.Restore();
            }
        }
    }
}
