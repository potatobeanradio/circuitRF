using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Views.Layout;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The figures of <c>reference/antennas.html</c> — ANT-12's example antenna, drawn by the real layout
/// renderer and photographed in the real EM Setup panel.
///
/// <para><b>The design is NOT built here.</b> It is read out of <c>testdata/antenna/</c>, which is the
/// workspace the page is written about and which <c>AntennaExampleTests</c> runs end to end through the
/// CLI. A second copy under <c>src/Ui</c> would agree with the first on the day it was written and drift
/// silently afterwards — <see cref="DocWsProbeFixtures"/> states the rule and the reason, and this file
/// follows it, including the walk up to <c>circuitrf.slnx</c> and the hard failure if it is not found.
/// What a reader sees is therefore the artwork that produced the numbers beside it.</para>
/// </summary>
public static class DocAntennaFixtures
{
    /// <summary>The repository root, found by walking up from this assembly's own location. Both
    /// callers — <c>tools/DocGen</c> and <c>Ui.Tests</c> — run out of a <c>bin/</c> directory inside
    /// the tree, and the shipping application never enters this file.</summary>
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "The antenna documentation figures read their design out of testdata/antenna/, and no "
          + $"circuitrf.slnx was found above '{AppContext.BaseDirectory}'.");
    });

    private static string Example(params string[] parts) =>
        Path.Combine([RepoRoot.Value, "testdata", "antenna", .. parts]);

    /// <summary>The example's <c>.cem</c> and the layout it resolves to, through the SAME resolver the
    /// `em` verb and the Simulate button use — so the figure cannot be of a layout the run would not
    /// have found.</summary>
    private static (EmSetup Setup, EmLayoutSource Source) Load()
    {
        string cem = Example("patch", "em", "patch-5p8GHz.cem");
        var setup = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(
            cem, setup.LayoutRef, Example(".cws"), new TechnologyCache());

        if (resolved.Source is null || resolved.Source.Technology is null)
            throw new InvalidOperationException(
                "The antenna example's layout or technology did not resolve: "
              + string.Join(" ", resolved.Diagnostics));

        return (setup, resolved.Source);
    }

    private static LayoutEditorViewModel EditorVm(EmLayoutSource source)
        => new(source.View) { Technology = source.Technology };

    /// <summary>
    /// <b>The whole example, ground pour and all.</b> Both copper layers are on, which is the point —
    /// the patch and its inset feed on top, the 40 × 40 mm pour under them, and the port mark on the
    /// feed's end face. The pour is what makes the run able to report the plane's size, and a figure
    /// with it switched off would teach the opposite.
    /// </summary>
    public static FigureScene PatchLayout()
    {
        var (_, source) = Load();
        return Framed(EditorVm(source), 760, 660, marginX: 0.06, marginY: 0.06);
    }

    /// <summary>
    /// <b>The feed, close up</b> — the inset notch, the 1 mm gaps either side of the 1.68 mm line, and
    /// the edge port's bar and arrow on the end face. This is the part of the artwork a reader has to
    /// copy, and at the whole-board zoom above it is four millimetres of a forty-millimetre picture.
    /// </summary>
    public static FigureScene PatchFeed()
    {
        var (_, source) = Load();
        var vm = EditorVm(source);

        var canvas = new LayoutCanvas { ViewModel = vm, Width = 560, Height = 380, ClipToBounds = true };
        var framed = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Child = canvas,
        };

        // An explicit WINDOW rather than a fit, and it is sized by the PORT MARKER rather than by the
        // artwork. The marker's bar spans the port's own width and its arrow is a fraction of that, so
        // on a 1.68 mm feed it is 1.68 mm of picture however wide the frame is: at the whole-board
        // zoom above, the mark a reader is supposed to look at is two per cent of the figure. An 8 mm
        // window round the feed's end face puts it at a fifth of the frame, which is what makes it
        // readable — the same trade DocLayoutFixtures.EdgePortsOnATaper makes by choosing a part whose
        // ports are wide.
        const double Mm = 1e6;    // the example's .clay is 1000 DBU/µm, so 1e6 DBU per mm
        double cx = 8.47 * Mm, cy = -2.6 * Mm;
        double zoom = canvas.Width / (10.0 * Mm);
        var vp = new LayoutViewport(cx - canvas.Width / (2.0 * zoom),
                                   cy - canvas.Height / (2.0 * zoom),
                                   zoom, canvas.Width, canvas.Height);
        canvas.SetViewport(vp);
        return new FigureScene(framed) { AfterLayout = _ => canvas.SetViewport(vp) };
    }

    /// <summary>
    /// <b>The port setup, in the panel that owns it.</b> The real EM Setup editor on the real
    /// <c>.cem</c>: the kernel the registry chose, the resolved port with its side and its impedance,
    /// the mesh report for the radiating-sheet intent, and the Radiation pattern group that is the one
    /// control this whole page is about.
    ///
    /// <para>Meshed but not solved (<c>BuildActiveMesh</c>), which is what the panel itself shows
    /// before Simulate — and a full solve here would be minutes inside a figure run.</para>
    /// </summary>
    public static FigureScene PatchEmSetup()
    {
        var (setup, source) = Load();
        string cem = Example("patch", "em", "patch-5p8GHz.cem");

        var vm = new EmSetupEditorViewModel(cem, setup) { ResolveLayout = _ => source };
        vm.BuildActiveMesh(null);

        if (vm.PlanarPorts.Count == 0)
            throw new InvalidOperationException(
                "The antenna EM Setup figure resolved no port on its own artwork. A panel figure "
              + "showing an unresolved port would document the empty state, not the populated one.");

        return new FigureScene(new EmSetupEditorView
        {
            DataContext = new EmSetupDocument("patch-5p8GHz", vm, cem),
        });
    }

    /// <summary>
    /// <b>The one control this whole page is about, at a size a reader can read it at.</b> A CROP of
    /// the same panel rather than a picture of its own (see <see cref="FigureCrop"/>): the group is
    /// one checkbox and a header near the bottom of a panel 2,000 pixels tall, and in the whole-panel
    /// figure above it is below the fold.
    ///
    /// <para>The rectangle comes from the live visual tree by the checkbox's NAME, never from a pixel
    /// offset — a measured offset is right until the panel gains a row, and then the figure is of the
    /// wrong part of it with nothing to say so.</para>
    /// </summary>
    public static FigureScene RadiationPatternControl()
    {
        var (setup, source) = Load();
        string cem = Example("patch", "em", "patch-5p8GHz.cem");

        var vm = new EmSetupEditorViewModel(cem, setup) { ResolveLayout = _ => source };
        vm.BuildActiveMesh(null);

        var panel = new EmSetupEditorView
        {
            DataContext = new EmSetupDocument("patch-5p8GHz", vm, cem),
        };

        var crop = FigureCrop.Around(
            panel, 560, 2400,
            c => c.GetVisualDescendants().OfType<CheckBox>().Where(b => b.Name == "RadiationPatternCheck"),
            pad: 10,
            describeWhatIsMissing:
                "This figure crops to the EM Setup panel's Radiation pattern checkbox, by the name "
              + "RadiationPatternCheck in EmSetupEditorView.axaml. If that control is renamed or "
              + "removed, this fixture must follow it rather than guess at an offset.");

        return new FigureScene(crop.Content) { AfterLayout = _ => crop.Apply() };
    }

    /// <summary>Frame a layout view model into a bordered canvas sized to its own artwork — the same
    /// arithmetic <see cref="DocLayoutFixtures"/>' port figures use, restated here rather than made
    /// public there: it is six lines and this file's margins are its own.</summary>
    private static FigureScene Framed(LayoutEditorViewModel vm, int width, int height,
                                      double marginX, double marginY)
    {
        var canvas = new LayoutCanvas { ViewModel = vm, Width = width, Height = height, ClipToBounds = true };
        var framed = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Child = canvas,
        };

        var bb = Bbox.Empty;
        foreach (var s in vm.Model.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        if (bb.IsEmpty)
            throw new InvalidOperationException("The antenna figure was built on artwork with no extent.");

        double zoom = Math.Min(width  / ((bb.MaxX - bb.MinX) * (1 + 2 * marginX)),
                               height / ((bb.MaxY - bb.MinY) * (1 + 2 * marginY)));
        double cx = 0.5 * (bb.MinX + bb.MaxX), cy = 0.5 * (bb.MinY + bb.MaxY);
        var vp = new LayoutViewport(cx - width / (2.0 * zoom), cy - height / (2.0 * zoom),
                                   zoom, width, height);

        canvas.SetViewport(vp);
        return new FigureScene(framed) { AfterLayout = _ => canvas.SetViewport(vp) };
    }
}
