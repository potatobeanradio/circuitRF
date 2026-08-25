using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Content;
using CircuitRF.Ui.Views.DataDisplay;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The figures a reader is meant to REBUILD: the inline value editor, the two worked examples from
/// the New User's Guide, and the Pin / Port / Term comparison.
///
/// <para>All of them are drawn from real <c>.csch</c> documents under
/// <c>src/Ui/resources/doc-schematics/</c>, read through the ordinary <c>SchematicPersistence</c>
/// reader — <see cref="DocFixtures"/>'s rule, for its reason: a hand-built view-model keeps
/// compiling while the model moves under it and quietly starts drawing something the application no
/// longer does. They are embedded with a distinct logical name so they do NOT appear in the template
/// picker; a documentation figure is not a product feature.</para>
///
/// <para><b>Square windows, on purpose.</b> A schematic a reader is copying and the plot it produces
/// are shown at the same size on the same slide, so the pair reads as one instruction rather than as
/// two unrelated pictures (owner, 2026-08-24).</para>
/// </summary>
public static class DocExampleFixtures
{
    /// <summary>The side of the square window the worked-example figures are captured in.</summary>
    public const int Square = 560;

    /// <summary>
    /// Wheel notches to zoom in past Zoom to Fit on the inline-editor figure. One notch is one
    /// scroll click, whatever the wheel delta — <c>ZoomAtPoint</c> reads only the SIGN of it.
    /// </summary>
    private const int ZoomNotches = 3;

    // ── The inline value editor ───────────────────────────────────────────────

    /// <summary>
    /// One 50 Ω resistor, zoomed to fill a square sheet, with the <b>inline value editor open on
    /// R</b> — the gesture the guide tells a reader to use.
    ///
    /// <para>The editor is opened through <c>SchematicCanvas.BeginInlineParamEdit</c>, which finds
    /// the label through the real hit test and verifies it landed on R before opening anything. The
    /// zoom happens FIRST: the edit box is positioned in screen coordinates, so opening it before the
    /// fit would leave it wherever the label used to be.</para>
    /// </summary>
    public static FigureScene InlineValueEditor()
    {
        var view = SchematicFor("Inline_Value_Editor");
        return new FigureScene(view)
        {
            AfterLayout = root =>
            {
                var canvas = Canvas(root);

                // Fit, then zoom IN on top of it. Zoom to Fit frames a component's whole drawn
                // extent — glyph, name and value rows — and a one-component sheet is mostly that
                // label block, so fitting alone leaves the resistor a fifth of the window with the
                // rest empty. Scrolling in twice is the gesture a reader would use, and it is what
                // makes the part and the edit box big enough to read from a projector.
                canvas.ZoomToFit();
                UiArtworkGenerator.Pump();
                // Zoomed about a point BELOW the middle, not the middle: the value row sits under
                // the glyph, so zooming about the centre walks it off the bottom edge — which is
                // where the edit box goes with it, and the edit box is the subject.
                var centre = new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height * 0.62);
                for (int notch = 0; notch < ZoomNotches; notch++) canvas.ZoomAtPoint(centre, 120);
                UiArtworkGenerator.Pump();

                canvas.BeginInlineParamEdit("R1", "R");
            },
        };
    }

    // ── Worked example A: Ohm's law ───────────────────────────────────────────

    /// <summary>10 V across 100 Ω with a DC analysis on it — the guide's first worked example.</summary>
    public static FigureScene DcExampleSchematic()
    {
        var view = SchematicFor("Example_DC_Ohms_Law");
        return new FigureScene(view) { AfterLayout = root => Fill(Canvas(root), notches: 2) };
    }

    // ── Worked example B: a first S-parameter run ─────────────────────────────

    /// <summary>Two Terms around a series L and a shunt C, with a 1-5 GHz S-parameter analysis.</summary>
    public static FigureScene SParamExampleSchematic()
    {
        var view = SchematicFor("Example_SParam_LC");
        return new FigureScene(view) { AfterLayout = root => Fill(Canvas(root), notches: 1) };
    }

    /// <summary>
    /// <b>How an EM run with an internal delta-gap port is consumed.</b> The <c>.s3p</c> the EM
    /// analysis wrote, dropped in as an ordinary SnP: ports 1 and 2 are the line's ends, and the
    /// series component sits on port 3 — the gap — where it acts in series in the metal.
    ///
    /// <para>The point of photographing it rather than describing it is that the connection looks
    /// like a SHUNT element and is not one. Port 3's terminals are the two lips of the cut, and
    /// terminating that port with an impedance is what puts the impedance INTO the cut; the shared
    /// ground reference in the schematic is the N-port formalism's bookkeeping, not a claim that one
    /// lip is grounded.</para>
    /// </summary>
    public static FigureScene EmSeriesGapCoSimulation()
    {
        var view = SchematicFor("Example_EM_SeriesGap");
        return new FigureScene(view) { AfterLayout = root => Fill(Canvas(root), notches: 0) };
    }

    /// <summary>
    /// What that schematic produces: <c>S(2,1)</c> in dB against frequency, in a Data Display window
    /// the same square as the schematic beside it.
    ///
    /// <para>The plot is sized to nearly fill the canvas rather than to the house default — the pair
    /// exists so a reader can check their own result against it, and a small chart in a large empty
    /// window is a worse check.</para>
    /// </summary>
    public static FigureScene SParamExamplePlot()
        => DocDataDisplayFixtures.ExampleLowPassResponse();

    // ── Pin and Term ──────────────────────────────────────────────────────────

    /// <summary>
    /// The Pin and Term symbols themselves, side by side and labelled — the glyphs, not a schematic
    /// containing them.
    ///
    /// <para>This began as three little schematics showing one network as Pins, as an instanced
    /// cell's ports and as Terms. The owner's verdict was that it showed the wrong thing: the
    /// question a reader has is what these two symbols ARE, and a sheet of wires around each one is
    /// noise around the answer (2026-08-24). The documentation page had it right all along — it
    /// shows the two glyphs, and explains Port in prose because <b>Port has no symbol</b>: it is the
    /// abstract idea that a Pin realises inside a cell and a Term realises on a test bench.</para>
    ///
    /// <para>Drawn by <see cref="DocSymbolGlyph"/>, which shares its draw call with the emitted
    /// <c>assets/symbols/*.svg</c> — so the glyph carries its <b>unconnected port markers</b>. Those
    /// squares are the point: they are how a reader sees that a Pin's line ends in a CONNECTION
    /// POINT rather than merely stopping.</para>
    /// </summary>
    public static FigureScene PinAndTerm()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 40 };
        foreach (var (kind, title, caption) in Pair())
            panel.Children.Add(Glyph(kind, title, caption));
        return new FigureScene(panel);
    }

    private static (SymbolKind Kind, string Title, string Caption)[] Pair() =>
    [
        (SymbolKind.Pin,  "Pin",
         "A cell's own connection point. Lives on the cell's symbol, carries no electrical model — "
       + "pure connectivity. Use it to expose a reusable cell's connections to its parent."),
        (SymbolKind.Term, "Term",
         "A numbered S-parameter port termination, 50 Ω by default. The point an S-parameter "
       + "analysis injects a wave and measures the scattered result."),
    ];

    private static Control Glyph(SymbolKind kind, string title, string caption)
        => new StackPanel
        {
            Width = 330, Spacing = 10,
            Children =
            {
                new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
                    CornerRadius = new CornerRadius(6),
                    Height = 150,
                    Child = new DocSymbolGlyph
                    {
                        Kind = kind, PortCount = 1, Margin = new Thickness(18),
                    },
                },
                new TextBlock
                {
                    Text = title, FontWeight = FontWeight.SemiBold, FontSize = 19,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = caption, FontSize = 12, Opacity = 0.75, Width = 330,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                },
            },
        };

    // ── Shared ────────────────────────────────────────────────────────────────

    private static SchematicView SchematicFor(string stem)
    {
        var model = ShippedSchematicTemplates.LoadDocSchematic(stem);
        var vm = new SchematicViewModel(model);
        return new SchematicView { DataContext = new SchematicDocument(stem, vm) };
    }

    /// <summary>
    /// Fit, then zoom IN on top of the fit.
    ///
    /// <para>Zoom to Fit frames a component's whole drawn extent — glyph, name row and value row —
    /// so a sheet holding two or three parts is mostly label block and the parts come out a fifth of
    /// the window with the rest empty. Scrolling in a notch or two is the gesture a reader would use,
    /// and it is what makes the schematic big enough to copy from a projector.</para>
    ///
    /// <para>Zoomed about a point BELOW the middle, not the middle: label rows sit UNDER their
    /// component, so zooming about the centre walks them off the bottom edge.</para>
    /// </summary>
    private static void Fill(SchematicCanvas canvas, int notches)
    {
        canvas.ZoomToFit();
        UiArtworkGenerator.Pump();

        var about = new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height * 0.62);
        for (int i = 0; i < notches; i++) canvas.ZoomAtPoint(about, 120);
        UiArtworkGenerator.Pump();
    }

    private static SchematicCanvas Canvas(Control root)
        => root.GetVisualDescendants().OfType<SchematicCanvas>().FirstOrDefault()
           ?? throw new InvalidOperationException(
               "This figure needs the schematic canvas and its view does not contain one.");
}
