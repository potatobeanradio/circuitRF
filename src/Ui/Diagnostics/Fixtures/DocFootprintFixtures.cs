using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The land patterns the built-in case table generates, drawn by the real generator.
///
/// <para><b>Nothing here draws a picture of a footprint.</b> Every shape in both figures comes out
/// of <see cref="ChipLandPatternGenerator.Generate"/> against the shipped PCB starter technology —
/// the same call the parameter editor's picker, Update Layout and <c>circuitrf render</c> all make
/// — so what a reader sees is the artwork their own board gets, and a change to the fillet goals
/// moves the picture.</para>
///
/// <para><b>Every panel is drawn at ONE scale, and that is the point of the first figure.</b> An
/// 0402 and a 1206 differ by a factor of three; four canvases each fitting their own content would
/// come out the same size on the page and the figure would then say the opposite of what it is for.
/// <see cref="LayoutViewport.ZoomToFit"/> takes <c>min(W/worldW, H/worldH) * 0.8</c>, so a panel
/// whose pixel width is its own world width times a shared constant — and whose height is the
/// TALLEST pattern's, so height never binds — lands at exactly that constant. Hence
/// <see cref="PxPerMm"/> and the arithmetic in <see cref="Panel"/>: the shared scale is
/// constructed, not eyeballed.</para>
///
/// <para><b>The case code is a caption, not silkscreen.</b> Drawn into the layout it would be a
/// <see cref="LabelShape"/> on the silk role, which is <c>#F2F2F2</c> — invisible on the light
/// variant of every figure, which is generated from the same scene as the dark one. A caption is
/// ordinary interface text and is legible in both.</para>
///
/// <para><b>The courtyard is absent on purpose.</b> No shipped technology declares a courtyard or
/// assembly layer, so <see cref="LandPatternLayers"/> omits that outline and says so — and these
/// figures show what the shipped technologies actually produce rather than a technology invented
/// here to make the picture complete.</para>
/// </summary>
public static class DocFootprintFixtures
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>Device-independent pixels per millimetre of board, shared by every panel of a
    /// figure — see the class remarks for why this is a constant rather than a fit.</summary>
    private const double PxPerMm = 42.0;

    /// <summary>The margin fraction <see cref="LayoutViewport.ZoomToFit"/> applies, which the panel
    /// width has to undo to land on <see cref="PxPerMm"/>. Named rather than inlined: if that
    /// default ever moves, a figure silently drawn at the wrong scale is the symptom.</summary>
    private const double FitMargin = 0.1;

    /// <summary>
    /// Four case sizes at the nominal density, all at one scale — the span of what the table
    /// generates, from the smallest anyone places by hand to a moulded tantalum.
    /// </summary>
    public static FigureScene CaseSizes() => Row(
    [
        ("0402",    DensityLevel.Nominal, "0402"),
        ("0805",    DensityLevel.Nominal, "0805"),
        ("1206",    DensityLevel.Nominal, "1206"),
        ("3216-18", DensityLevel.Nominal, "3216-18"),
    ]);

    /// <summary>
    /// One case at all three IPC-7351B density levels. The part is the same in all three; what
    /// changes is how far the land reaches past it, which is the whole meaning of the density
    /// picker beside the footprint row.
    /// </summary>
    public static FigureScene Densities() => Row(
    [
        ("0805", DensityLevel.Most,    "M — most"),
        ("0805", DensityLevel.Nominal, "N — nominal"),
        ("0805", DensityLevel.Least,   "L — least"),
    ]);

    // ── the scene ─────────────────────────────────────────────────────────────

    private sealed record Pattern(LayoutView View, double WidthMm, double HeightMm, string Caption);

    private static FigureScene Row(IReadOnlyList<(string Code, DensityLevel Density, string Caption)> entries)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var patterns = entries.Select(e => Generate(tech, e.Code, e.Density, e.Caption)).ToList();

        // One height for every panel — the tallest pattern's — so no panel's fit is height-limited
        // and every one of them resolves to PxPerMm.
        double tallestMm = patterns.Max(p => p.HeightMm);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var p in patterns) row.Children.Add(Panel(tech, p, tallestMm));
        return new FigureScene(row);
    }

    private static Control Panel(Technology tech, Pattern pattern, double tallestMm)
    {
        var vm = new LayoutEditorViewModel(pattern.View) { Technology = tech };
        var canvas = new LayoutCanvas
        {
            ViewModel = vm,
            Width  = Math.Round(pattern.WidthMm * PxPerMm / (1.0 - 2 * FitMargin)),
            Height = Math.Round(tallestMm      * PxPerMm / (1.0 - 2 * FitMargin)),
            ClipToBounds = true,
        };

        return new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children =
            {
                new Border
                {
                    BorderThickness = new Avalonia.Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                    Child = canvas,
                },
                new TextBlock
                {
                    Text = pattern.Caption,
                    FontSize = 11,
                    Width = canvas.Width,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };
    }

    /// <summary>One case at one density, plus the world extent the panel's scale is built on.</summary>
    private static Pattern Generate(Technology tech, string code, DensityLevel density, string caption)
    {
        var smtCase = SmtCaseTable.Find(code)
            ?? throw new InvalidOperationException(
                $"The case table no longer holds '{code}'. It holds: "
              + string.Join(", ", SmtCaseTable.Codes) + ".");

        var reference = FootprintRef.For(smtCase, density);
        var result = ChipLandPatternGenerator.Generate(reference, tech, PCellLayerSelection.Default);
        if (result.Shapes.Count == 0)
            throw new InvalidOperationException(
                $"The generator drew nothing for {reference} on '{tech.Name}', so this figure would be an "
              + "empty canvas under a caption claiming otherwise. It said: "
              + string.Join(" ", result.Diagnostics ?? []));

        var view = new LayoutView
        {
            DbuPerMicron = Dbu,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };
        foreach (var shape in result.Shapes) view.Shapes.Add(shape);

        var (wDbu, hDbu) = Extent(result.Shapes);
        double perMm = LayoutUnits.ToDbu(1m, LayoutUnit.Mm, Dbu);
        return new Pattern(view, wDbu / perMm, hDbu / perMm, caption);
    }

    /// <summary>
    /// The drawn extent, in DBU. Only the two shape kinds the generator emits are measured, and an
    /// unknown one is a hard failure — a panel sized from an extent that left a shape out is a
    /// figure drawn at a different scale from its neighbours, which is exactly the thing this
    /// fixture exists to prevent, and nothing would report it.
    /// </summary>
    private static (long W, long H) Extent(IReadOnlyList<LayoutShape> shapes)
    {
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;

        void Add(long x, long y)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case RectShape r:
                    Add(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2));
                    Add(Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));
                    break;

                case PathShape p:
                    long half = p.Width / 2;
                    for (int i = 0; i + 1 < p.Xy.Length; i += 2)
                    {
                        Add(p.Xy[i] - half, p.Xy[i + 1] - half);
                        Add(p.Xy[i] + half, p.Xy[i + 1] + half);
                    }
                    break;

                default:
                    throw new InvalidOperationException(
                        $"The land-pattern generator now emits a {shape.GetType().Name}, which this figure "
                      + "does not know how to measure. Add it here, or the panels stop sharing a scale.");
            }
        }

        if (minX > maxX) throw new InvalidOperationException("A generated land pattern has no extent.");
        return (maxX - minX, maxY - minY);
    }
}
