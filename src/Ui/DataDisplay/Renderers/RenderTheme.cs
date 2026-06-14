// ================================================================
//  RenderTheme.cs  —  Color and style constants for Skia renderers
//
//  Ported from splotRF/src/Renderers/RenderTheme.cs — namespace
//  renamed to CircuitRF.Ui.DataDisplay.
//
//  TODO 7.x: wire RenderTheme to circuitRF ColorTheme/.ccolor
//  For now pick RenderTheme.Light vs .Dark from ActualThemeVariant.
// ================================================================

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay
{
    public record RenderTheme(
        SKColor GridColor,
        SKColor MinorGridColor,
        SKColor TickColor,
        SKColor TextColor,
        SKColor BackgroundColor,
        SKColor BorderColor,
        bool    DarkMode)
    {
        // ---- Preset themes ----------------------------------------------

        public static RenderTheme Light { get; } = new(
            GridColor       : new SKColor(160, 160, 160),
            MinorGridColor  : new SKColor(200, 200, 200),
            TickColor       : new SKColor( 60,  79,  79),
            TextColor       : new SKColor( 60,  79,  79),
            BackgroundColor : SKColors.White,
            BorderColor     : new SKColor( 60,  79,  79),
            DarkMode        : false);

        public static RenderTheme Dark { get; } = new(
            GridColor       : new SKColor(100, 100, 100),
            MinorGridColor  : new SKColor( 70,  70,  70),
            TickColor       : new SKColor(200, 200, 200),
            TextColor       : new SKColor(210, 210, 210),
            BackgroundColor : new SKColor( 30,  30,  30),
            BorderColor     : new SKColor(200, 200, 200),
            DarkMode        : true);

        // ---- Helpers ----------------------------------------------------

        public static SKColor SelectionColorFallback = new SKColor(33, 150, 175);
        public static byte SelectionAlpha = 175;

        /// <summary>
        /// Converts an Avalonia Color to an SKColor, optionally overriding
        /// the alpha with an opacity in [0, 1].
        /// </summary>
        public static SKColor ToSKColor(Avalonia.Media.Color c, double opacity = -1)
        {
            byte a = opacity >= 0 ? (byte)(opacity * 255) : c.A;
            return new SKColor(c.R, c.G, c.B, a);
        }

        /// <summary>Returns <paramref name="color"/> with alpha set from [0,1] opacity.</summary>
        public static SKColor WithOpacity(SKColor color, double opacity) =>
            color.WithAlpha((byte)(opacity * 255));

        /// <summary>
        /// Gets the current Fluent theme system accent color with a custom alpha transparency.
        /// </summary>
        public static SKColor GetTransparentAccent(byte alpha)
        {
            if (Dispatcher.UIThread.CheckAccess())
                return FetchColorFromResources(alpha);

            return Dispatcher.UIThread.Invoke(() => FetchColorFromResources(alpha));
        }

        private static SKColor FetchColorFromResources(byte alpha)
        {
            if (Application.Current != null)
            {
                ThemeVariant currentTheme = Application.Current.ActualThemeVariant;

                if (Application.Current.TryGetResource("SystemAccentColor", currentTheme, out var resource) &&
                    resource is Color systemAccent)
                {
                    return new SKColor(systemAccent.R, systemAccent.G, systemAccent.B, alpha);
                }
            }
            return new SKColor(SelectionColorFallback.Red, SelectionColorFallback.Green, SelectionColorFallback.Blue, alpha);
        }
    }
}
