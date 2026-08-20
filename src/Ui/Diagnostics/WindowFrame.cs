using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Diagnostics;

/// <summary>
/// A SYNTHETIC window frame drawn around a captured figure.
///
/// <para>Native title bars belong to the operating system and are not in Avalonia's visual tree, so
/// a captured "window" has no frame at all unless the generator draws one. Drawing our own is not a
/// compromise: it renders identically on Windows, macOS and Linux, which is better for the docs than
/// shipping whichever OS the author happened to be sitting at.</para>
///
/// <para>It is deliberately NEUTRAL — a plain bar, a title, three small dots at the trailing edge.
/// It must not read as an imitation of a specific operating system's chrome, so there are no
/// traffic-light colours, no Segoe glyphs and no platform-shaped corners.</para>
///
/// <para>Colours are the docs stylesheet's own surface tokens
/// (<c>docs/user/assets/css/circuitrf-docs.css</c>), so a framed figure sits on the page as if it
/// belonged there. None of them is pure black, which also keeps the frame clear of the Skia
/// black-alpha trap described in <see cref="DocsPaintRemap"/>.</para>
/// </summary>
public sealed class WindowFrame
{
    /// <summary>Height of the synthetic title bar, in the same units as the capture size.</summary>
    public const int TitleBarHeight = 34;

    private const double CornerRadius = 8;
    private const double DotDiameter  = 9;

    public string Title { get; }

    private WindowFrame(string title) => Title = title;

    /// <summary>A frame carrying <paramref name="title"/> in its title bar.</summary>
    public static WindowFrame Titled(string title) => new(title);

    // Docs-stylesheet surface tokens, light and dark.
    private static (Color Bar, Color Border, Color Text, Color Dot, Color Body) Palette(ColorVariant v)
        => v == ColorVariant.Dark
            ? (Color.Parse("#1C2C39"), Color.Parse("#283A48"), Color.Parse("#9DB0C0"),
               Color.Parse("#3C4E5D"), Color.Parse("#0E1820"))
            : (Color.Parse("#EEF2F5"), Color.Parse("#DCE3EA"), Color.Parse("#5C6B7A"),
               Color.Parse("#C3CDD6"), Color.Parse("#FFFFFF"));

    /// <summary>
    /// Wrap <paramref name="content"/> (sized <paramref name="w"/> x <paramref name="h"/>) in the
    /// frame. The result is <paramref name="w"/> x (<paramref name="h"/> +
    /// <see cref="TitleBarHeight"/>) — the catalog's stated size is the CONTENT size, and the frame
    /// is added around it, so adding a frame never changes what is inside the figure.
    /// </summary>
    public Control Wrap(Control content, int w, int h, ColorVariant variant)
    {
        var p = Palette(variant);

        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0),
        };
        for (int i = 0; i < 3; i++)
            dots.Children.Add(new Ellipse
            {
                Width = DotDiameter, Height = DotDiameter,
                Fill = new SolidColorBrush(p.Dot),
            });

        var bar = new Panel
        {
            Height = TitleBarHeight,
            Background = new SolidColorBrush(p.Bar),
            Children =
            {
                new TextBlock
                {
                    Text = Title,
                    Foreground = new SolidColorBrush(p.Text),
                    FontSize = 12.5,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                dots,
            },
        };

        content.Width  = w;
        content.Height = h;

        var stack = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = new SolidColorBrush(p.Body),
        };
        Grid.SetRow(bar, 0);
        Grid.SetRow(content, 1);
        stack.Children.Add(bar);
        stack.Children.Add(content);

        return new Border
        {
            Width = w,
            Height = h + TitleBarHeight,
            CornerRadius = new CornerRadius(CornerRadius),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(p.Border),
            ClipToBounds = true,
            Child = stack,
        };
    }
}
