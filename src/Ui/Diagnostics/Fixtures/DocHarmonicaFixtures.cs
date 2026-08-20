using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Views.Harmonica;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// harmonicaRF figures.
///
/// <para>Every one of them runs the instrument for real: a <see cref="HarmonicaViewModel"/> on the
/// shipped default document, solved through <c>SolveFrame</c> — the same call the frame scheduler
/// makes — so the contours, the loadline and the readouts in these pictures are the ones the tool
/// produces, not a drawing of them.</para>
/// </summary>
public static class DocHarmonicaFixtures
{
    /// <summary>
    /// A solved instrument. The grid is the 5x12 preset rather than the coarsest one: a figure of a
    /// contour tool with four contours in it would understate what the reader sees.
    /// </summary>
    private static HarmonicaViewModel Solved()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 5, Spokes = 12 });

        if (vm.SolveError is { } err)
            throw new InvalidOperationException(
                "The harmonicaRF doc fixture failed to solve its own default document: " + err);
        if (vm.Frame.SmithPower.GridPoints.Count == 0)
            throw new InvalidOperationException(
                "The harmonicaRF doc fixture solved without producing a single grid point, so the "
              + "Smith charts in the figure would be empty chrome.");

        return vm;
    }

    private static HarmonicaView ViewFor(HarmonicaViewModel vm) => new()
    {
        DataContext = new HarmonicaDocument("harmonicaRF", new HarmonicaDocumentViewModel(vm)),
    };

    /// <summary>The whole instrument: both Smith charts, the loadline, the power sweep, the strip.</summary>
    public static FigureScene Instrument()
    {
        var vm = Solved();
        return new FigureScene(ViewFor(vm)) { Popups = _ => Settle(vm) };
    }

    /// <summary>
    /// Clear the "Solving…" progress row before the capture.
    ///
    /// <para>Realizing the view starts a scheduled frame of its own — the instrument is always
    /// solving, which is the point of it — so a capture taken the instant it is shown catches a
    /// half-finished progress bar reading "15 / 37". That documents the act of taking the picture,
    /// not the tool. The frame being drawn is the fully-solved one from <see cref="Solved"/> either
    /// way; only the progress row is suppressed. Ridden in on the popup hook because that is the one
    /// callback the generator makes after the window is shown and arranged.</para>
    /// </summary>
    private static IReadOnlyList<PopupCapture> Settle(HarmonicaViewModel vm)
    {
        // IsSolvingGrid is what the progress row is bound to, and the view only re-reads it on its
        // own Refresh — which a property change is what triggers. Setting both, in that order, is
        // the same pair CancelSolve sets.
        vm.IsSolving     = false;
        vm.IsSolvingGrid = false;
        UiArtworkGenerator.Pump();
        return [];
    }

    /// <summary>
    /// The readout strip on its own, lifted out of a real view.
    ///
    /// <para>Detached rather than cropped, for the reason <c>DocFixtures.Toolbar</c> gives: a crop is
    /// a screenshot with extra steps and moves the moment anything above it changes height. The
    /// detach costs the control its inherited DataContext, so it is captured and re-applied — the
    /// same two lines the toolbar fixture needs, for the same reason.</para>
    /// </summary>
    public static FigureScene ReadoutStrip()
    {
        var view = ViewFor(Solved());

        var probe = new Window { Width = 1600, Height = 1000, Content = view };
        probe.Show();
        UiArtworkGenerator.Pump();
        probe.Measure(new Avalonia.Size(1600, 1000));
        probe.Arrange(new Avalonia.Rect(0, 0, 1600, 1000));
        UiArtworkGenerator.Pump();

        var strip = view.FindControl<ReadoutStripView>("Readouts")
            ?? throw new InvalidOperationException(
                "HarmonicaView no longer has a control named 'Readouts'. The readout-strip figure "
              + "lifts that control out by name; renaming it makes this figure a picture of nothing.");

        var context = strip.DataContext;
        (strip.Parent as Panel)?.Children.Remove(strip);
        (strip.Parent as ContentControl)?.SetCurrentValue(ContentControl.ContentProperty, null);
        (strip.Parent as Decorator)?.SetCurrentValue(Decorator.ChildProperty, null);
        strip.DataContext = context;

        probe.Content = null;
        probe.Close();
        return new FigureScene(strip);
    }

    // A figure of the MENU BAR was attempted and is deliberately absent: a MenuItem's popup does not
    // render under headless capture (neither MenuItem.Open() nor IsSubMenuOpen produced a visual
    // root that drew anything, while ContextMenu.Open — DocFixtures' own popup route — does). The
    // menus are documented as tables instead; see brief-user-docs-content's completion report.
}
