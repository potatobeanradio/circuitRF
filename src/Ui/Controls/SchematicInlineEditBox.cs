using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// <b>The schematic editor's inline text editor</b> — the box that appears over a component label
/// when it is double-clicked, extracted so a second schematic surface can host the same one.
/// </summary>
/// <remarks>
/// <b>Owner, 2026-08-20:</b> <i>"Allow user to use inline text editor to change any value in the
/// [Match Designer] schematic… Can you reuse the exact same inline text editor from the regular
/// schematic? That is the preferred solution."</i>
///
/// <para>What "the same editor" actually consists of is three things, and all three live here: the
/// box's own appearance (a borderless-looking 11 pt IBM Plex box with a 4 × 2 padding and no minimum
/// height), the <b>placement arithmetic</b> that puts its text exactly on the label's own Skia
/// baseline and left edge, and the font size the renderer draws that label at. The
/// <see cref="SchematicView"/>'s box and the Match Designer's are now one type reading one set of
/// constants, so a change to the label font or the box padding moves both.</para>
///
/// <para>What is NOT here is the hosting: which canvas the box floats over, what a hit means, and
/// what a commit writes. Those genuinely differ — the editor edits an <c>EditableParameter</c> on a
/// page and a <b>transform solve</b> in the Designer — and pretending otherwise would put a
/// schematic's edit model behind a Designer that does not have one.</para>
///
/// <para>The three-key contract (Return commits, LostFocus commits, Escape reverts) is the host's to
/// wire, exactly as it was: this control raises nothing and handles no key itself, so neither host
/// changes behaviour by adopting it.</para>
/// </remarks>
public class SchematicInlineEditBox : TextBox
{
    /// <summary>
    /// The box's text left edge sits this far right of its <c>Margin.Left</c> — its own left padding.
    /// </summary>
    public const double LeftPad = 4.0;

    /// <summary>The box's top padding, between <c>Margin.Top</c> and the text's own line box.</summary>
    public const double TopPad = 2.0;

    /// <summary>
    /// The world-unit label size <see cref="SchematicRenderer"/> draws component labels at. The
    /// on-screen size is this times the canvas zoom, floored for legibility by <see cref="FontSizeAt"/>.
    /// </summary>
    public const double LabelWorldSize = SchematicComponent.LabelWorldHeight;

    /// <summary>Smallest on-screen point size an open editor is ever shown at.</summary>
    public const double MinFontSize = 9.0;

    /// <summary>The point size a label — and therefore its editor — is drawn at, at one zoom level.</summary>
    public static double FontSizeAt(double zoom) => Math.Max(zoom * LabelWorldSize, MinFontSize);

    /// <summary>
    /// The ascender ratio of the label typeface, measured from the typeface itself.
    /// </summary>
    /// <remarks>
    /// Skia reports <c>Ascent</c> as a negative distance above the baseline, so negating it gives the
    /// fraction of the point size that sits above the baseline. Measured rather than assumed so
    /// swapping <c>SkiaFonts.PlexRegular</c> moves every inline editor with it.
    /// </remarks>
    public static double AscenderRatio { get; } = MeasureAscenderRatio();

    private static double MeasureAscenderRatio()
    {
        using var font = new SKFont(SkiaFonts.PlexRegular, 100f);
        font.GetFontMetrics(out var m);
        return -m.Ascent / 100.0;
    }

    /// <summary>
    /// The margin that lands the box's text left edge on <paramref name="screenX"/> and its baseline
    /// on <paramref name="screenY"/> — the two numbers <c>SchematicRenderer.DrawLabels</c> draws at.
    /// </summary>
    public static Thickness MarginFor(double screenX, double screenY, double fontSize) =>
        new(screenX - LeftPad, screenY - TopPad - fontSize * AscenderRatio, 0, 0);

    /// <summary>
    /// How wide the box has to be for <paramref name="text"/>: a measurement against the typeface the
    /// label UNDERNEATH it is rendered in, put through
    /// <see cref="InlineEdit.WidthFromMeasuredText"/>'s shared slack-and-floor rule.
    /// </summary>
    public static double WidthFor(string? text, double fontSize)
    {
        string measured = text ?? "";
        using var font = new SKFont(SkiaFonts.PlexRegular, (float)Math.Max(1.0, fontSize));
        double textWidth = measured.Length == 0 ? 0 : font.MeasureText(measured);
        return InlineEdit.WidthFromMeasuredText(textWidth, fontSize);
    }

    /// <summary>
    /// <b>This control is styled as a <see cref="TextBox"/>, and must say so explicitly.</b>
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported three times, 2026-08-20</b> — "cannot double click on TermG value to get the
    /// inline text editor", then twice more after two fixes that were each a real defect and neither
    /// of them this one. The gesture, the hit-test, the resolve and the open were all working the
    /// whole time; the box simply <b>drew nothing</b>.
    ///
    /// <para>Avalonia resolves a templated control's implicit <c>ControlTheme</c> by the control's OWN
    /// type and <b>does not fall back to a base type</b>. Fluent keys its theme on
    /// <c>typeof(TextBox)</c>, so a subclass finds no theme, gets no <c>Template</c>, builds no visual
    /// children and measures zero high — while remaining focusable, holding its text, and reporting
    /// <c>IsVisible = true</c>. Nothing throws and nothing warns. Measured directly against a plain
    /// <see cref="TextBox"/> in the same panel: <c>template=set, visualChildren=1</c> versus
    /// <c>template=NULL, visualChildren=0</c>.
    ///
    /// <para><b>It broke the schematic PAGE's inline editor too</b>, silently, the moment that AXAML
    /// started declaring this type instead of a bare <c>TextBox</c> — one line in one file taking out
    /// label editing on every schematic in the application. Any future subclass of a templated control
    /// in this codebase needs this override, and needs it in the same commit.</para>
    /// </remarks>
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Builds the box in its resting (hidden) state.</summary>
    public SchematicInlineEditBox()
    {
        IsVisible = false;
        Width = 140;
        FontSize = 11;
        TextAlignment = TextAlignment.Left;
        Padding = new Thickness(LeftPad, TopPad);
        MinHeight = 0;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
    }

    /// <summary>Shows the box over one label and sizes it to the seeded text.</summary>
    /// <param name="text">What the box opens holding.</param>
    /// <param name="screenX">The label's text left edge, in the host panel's pixels.</param>
    /// <param name="screenY">The label's Skia baseline, in the same pixels.</param>
    /// <param name="fontSize">The point size the label is drawn at — see <see cref="FontSizeAt"/>.</param>
    public void Open(string text, double screenX, double screenY, double fontSize)
    {
        FontSize = fontSize;
        Text = text;
        Width = WidthFor(text, fontSize);
        Margin = MarginFor(screenX, screenY, fontSize);
        IsVisible = true;
    }

    /// <summary>Moves an already-open box, keeping its current text and width.</summary>
    public void MoveTo(double screenX, double screenY, double fontSize)
    {
        FontSize = fontSize;
        Width = WidthFor(Text ?? "", fontSize);
        Margin = MarginFor(screenX, screenY, fontSize);
    }

    /// <summary>Pre-selects the numeric part of the seeded text, leaving its unit standing.</summary>
    public void SelectValueOnly()
    {
        string t = Text ?? "";
        int len = InlineEdit.ValueSelectionLength(t);
        SelectionStart = 0;
        SelectionEnd = Math.Clamp(len, 0, t.Length);
    }
}
