using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Match;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Match Designer, on a real synthesised design.
///
/// <para>Two designs, both synthesised here and now by the shipped code: the one a freshly placed
/// <c>Match</c> carries (50 Ω to 10 Ω over 1.8-2.2 GHz, with a Norton solution already applied), and
/// the interstage problem the synthesis is gated on (200 Ω ‖ 0.125 pF to 1.25 Ω + 10 pF over
/// 3.3-5.0 GHz). Every element value, every response curve and every status number in these figures
/// is what the synthesis computes, not a mock-up of what it would compute.</para>
/// </summary>
public static class DocMatchFixtures
{
    /// <summary>A schematic holding one freshly placed Match, seeded exactly as placement seeds it.</summary>
    private static (SchematicViewModel Vm, EditableComponent Comp) Placed(MatchDesign? design = null)
    {
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Expression,
                Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic,
            });

        if (design is not null)
        {
            Set(comp, "Design", MatchEmbedding.Encode(design));
            Set(comp, "F1", (design.F1 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "F2", (design.F2 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "Order", design.Order.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "R1", design.Term1.R.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "R2", design.Term2.R.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The GLYPH reads Form and Bands off the parameter list, not off the encoded design
            // (EditableComponent.MatchGlyphForm / MatchGlyphBands), so a multiband or lowpass figure
            // whose parameters were left at the placement defaults would draw the WRONG symbol beside
            // a correct ladder. Seeded here rather than at each call site so that cannot be forgotten.
            Set(comp, "Form", design.Form.ToString());
            Set(comp, "Bands", design.BandCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (design.BandCount >= 2)
            {
                Set(comp, "F3", (design.F3 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Set(comp, "F4", (design.F4 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (design.BandCount >= 3)
            {
                Set(comp, "F5", (design.F5 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Set(comp, "F6", (design.F6 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var model = new SchematicEditModel();
        model.Components.Add(comp);
        return (new SchematicViewModel(model), comp);
    }

    /// <summary>Overwrite one already-seeded parameter, or fail rather than silently adding a second.</summary>
    private static void Set(EditableComponent comp, string name, string expression)
    {
        var p = comp.Parameters.FirstOrDefault(x => x.Name == name)
            ?? throw new InvalidOperationException(
                $"A placed Match no longer declares a '{name}' parameter, so the documentation figure "
              + "cannot state its design.");
        p.Expression = expression;
    }

    /// <summary>
    /// The §4.9 interstage problem: a stage's 200 Ω ‖ 0.125 pF output into the next stage's
    /// 1.25 Ω + 10 pF input, over 3.3-5.0 GHz. This is the synthesis's own golden case.
    /// </summary>
    public static MatchDesign Interstage()
    {
        var design = new MatchDesign
        {
            F1 = 3.3e9,
            F2 = 5.0e9,
            Order = 4,
            Response = ResponseShape.ChebyshevFano,
            Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
            Term2 = new Termination(1.25,  ReactanceKind.C, TerminationTopology.Series,   10e-12),
        };

        // A 200 ohm to 1.25 ohm transformation needs Norton transforms to REACH: synthesised bare,
        // the far end sits at 1.68 ohm and the panel flags termination 1 red. The figure applies the
        // search's own first-ranked solution — the same list the Designer offers and the same
        // deterministic ranking — so the picture is of a design that is actually matched, which is
        // what the worked example in the chapter walks through.
        var set = MatchSolutionSearch.Search(design, includeQAdjust: false);
        var pick = set.Solutions.FirstOrDefault(s => s.QAdjust == 0.0 && !s.ImplausibleValues)
            ?? throw new InvalidOperationException(
                "The solution search found no plain solution for the interstage example, so the "
              + "worked-example figure would show an unmatched design.");

        design.Transforms = [.. pick.Transforms];
        design.AppliedSolutions.Add(pick.Fingerprint);
        return design;
    }

    /// <summary>
    /// The DUAL-BAND worked example: a 200 Ω ‖ 0.125 pF stage output into a 1.25 Ω + 10 pF stage
    /// input, matched over <b>1.75–1.9 GHz and 2.1–2.2 GHz together</b> at three match points per
    /// band, with the gap between them deliberately left reflecting.
    ///
    /// <para>The two terminations are the interstage pair on purpose — it is the same transformation
    /// the single-band worked example makes, so a reader can put the two figures side by side and see
    /// what the second band costs and what the gap buys. The numbers are the ones the chapter tells
    /// the reader to type, and they are typed here rather than described: if the synthesis stops
    /// reaching this specification, the docs build fails instead of the page quietly claiming a
    /// result nobody can reproduce.</para>
    /// </summary>
    public static MatchDesign DualBand()
    {
        var design = new MatchDesign
        {
            F1 = 1.75e9,
            F2 = 1.90e9,
            BandCount = 2,
            F3 = 2.10e9,
            F4 = 2.20e9,
            Order = 3,
            Response = ResponseShape.ChebyshevFano,
            Form = NetworkForm.Bandpass,
            Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
            Term2 = new Termination(1.25,  ReactanceKind.C, TerminationTopology.Series,   10e-12),
            // The chapter plots this at +/-20% of the band, which is the value the reader is told to
            // put in the Response pane's own "+/- band" box.
            PlotBandFraction = 0.20,
            PlotPoints = 2001,
        };

        // Same rule as Interstage(): a 200:1.25 transformation does not REACH without a Norton
        // transform, so the figure applies the search's own first-ranked solution rather than
        // photographing an unmatched ladder.
        var set = MatchSolutionSearch.Search(design, includeQAdjust: false);
        var pick = set.Solutions.FirstOrDefault(s => s.QAdjust == 0.0 && !s.ImplausibleValues)
            ?? throw new InvalidOperationException(
                "The solution search found no plain solution for the dual-band example, so the "
              + "worked-example figure would show an unmatched design.");

        design.Transforms = [.. pick.Transforms];
        design.AppliedSolutions.Add(pick.Fingerprint);
        return design;
    }

    /// <summary>The Designer, wired to that instance and refreshed — spec, ladder, plots, solutions.</summary>
    private static MatchDesignerViewModel DesignerVm(MatchDesign? design = null)
    {
        var (schematic, comp) = Placed(design);
        var vm = new MatchDesignerViewModel();
        vm.SetTarget(schematic, comp);

        if (vm.Elements.Count == 0)
            throw new InvalidOperationException(
                "The Match Designer produced no ladder for the design a freshly placed Match carries, "
              + "so every figure on this page would show an empty network pane.");

        PumpUntilSettled(vm);
        return vm;
    }

    /// <summary>
    /// Run the dispatcher until the solution search and the response probe have both landed.
    /// </summary>
    /// <remarks>
    /// <b>Pump, never <c>WaitForAnalysis</c>.</b> That method blocks the calling thread on
    /// <c>Task.WaitAll</c>, and under the docs host the calling thread IS the dispatcher the search's
    /// own batch landings are posted to — so waiting on it is a deadlock rather than a wait. Draining
    /// the dispatcher in a loop lets those landings run, which is what fills the solutions list and
    /// what turns the status strip from "searching…" into the applied solution's numbers.
    ///
    /// <para>Bounded, and it does NOT throw on the bound: a figure of a still-searching panel is a
    /// worse figure, not a broken build, and the two multiband searches are the slowest in the
    /// application. The cap is generous enough for those and small enough that a hung search cannot
    /// stall a docs run indefinitely.</para>
    /// </remarks>
    private static void PumpUntilSettled(MatchDesignerViewModel vm)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < SearchSettleTimeout)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (!vm.IsAnalysing && vm.AnalysisTask.IsCompleted && vm.SolutionSearchTask.IsCompleted)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                return;
            }
            System.Threading.Thread.Sleep(5);
        }
    }

    /// <summary>How long <see cref="PumpUntilSettled"/> will pump before giving up.</summary>
    private static readonly TimeSpan SearchSettleTimeout = TimeSpan.FromSeconds(45);

    /// <summary>The whole Designer window: specification, network, response, transforms, status.</summary>
    public static FigureScene Designer() => WholeWindow(DesignerVm());

    /// <summary>The interstage worked example, solved: the ladder, its values and its response.</summary>
    public static FigureScene Interstage2Stage() => WholeWindow(DesignerVm(Interstage()));

    /// <summary>
    /// The solutions list — every order and every response family, simplest first.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is opened here any more</b> (2026-08-28): the list is the lower half of the
    /// specification pane and is always out.
    ///
    /// <para><b>And nothing WAITS here either.</b> <c>MatchDesignerViewModel.WaitForAnalysis</c>
    /// blocks the calling thread, and under the docs host that thread is the dispatcher the search's
    /// own continuations are posted to — so waiting for them on it is a deadlock, not a wait. The
    /// figure captures the list as the generator's own dispatcher pumping has filled it, exactly as
    /// every other figure in this file captures whatever its view-model has settled to.</para>
    /// </remarks>
    public static FigureScene Solutions() => WholeWindow(DesignerVm());

    /// <summary>The dual-band worked example, solved: specification, ladder, response and status.</summary>
    public static FigureScene DualBandDesigner() => WholeWindow(DesignerVm(DualBand()));

    /// <summary>
    /// The dual-band example's |S11| / |S21| plot ON ITS OWN, at figure size.
    ///
    /// <para><b>Why a bare <c>PlotControl</c> and not the Designer with its Response pane expanded.</b>
    /// The pane lays both plots out at a fixed golden aspect ratio inside a scroll view, so making the
    /// window big makes the pane scroll rather than making either plot big. What this page needs is the
    /// magnitude plot large enough to read two passbands, a gap and a 40 dB axis off — so it is the
    /// Designer's OWN plot object, rendered by the Data Display's own control, in a box of its own.
    /// Nothing is re-computed and no second response model exists.</para>
    /// </summary>
    public static FigureScene DualBandResponse()
    {
        var vm = DesignerVm(DualBand());

        var plot = new PlotControl
        {
            Plot = vm.MagnitudePlot,
            Library = vm.PlotHost.Library,
            CanDeletePlot = false,
            CanEditPlotProperties = false,
        };

        var host = new Border { Padding = new Thickness(8), Child = plot };

        return new FigureScene(host)
        {
            // The variant is applied by the generator AFTER Build() returns, so the plot's own theme
            // has to be set once the window is up — otherwise the dark figure comes back drawn out of
            // the light palette, which is exactly the failure the docs stylesheet cannot correct.
            AfterLayout = _ =>
            {
                var theme = ThemeService.CurrentVariant == ColorVariant.Dark
                    ? RenderTheme.Dark : RenderTheme.Light;
                vm.PlotHost.Theme = theme;
                plot.PlotTheme = theme;
                host.Background = new SolidColorBrush(WindowFrame.DocsSurface(ThemeService.CurrentVariant));
                vm.MagnitudePlot.Autoscale(force: true);
                plot.InvalidateVisual();
            },
        };
    }

    /// <summary>
    /// The five <c>Match</c> glyphs, side by side: the three single-band forms and the two multiband
    /// ones.
    ///
    /// <para>Every tile goes through <c>BuiltInSymbols.PrimitivesForMatch</c> — the same call
    /// <c>EditableComponent</c> makes when it draws a placed <c>Match</c> — so the page cannot show a
    /// symbol the schematic would not draw.</para>
    /// </summary>
    public static FigureScene FormGlyphs()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (form, bands, caption) in (ReadOnlySpan<(NetworkForm, int, string)>)
                 [(NetworkForm.Bandpass, 1, "Bandpass"),
                  (NetworkForm.Lowpass,  1, "Lowpass"),
                  (NetworkForm.Highpass, 1, "Highpass"),
                  (NetworkForm.Bandpass, 2, "Dual-band"),
                  (NetworkForm.Bandpass, 3, "Tri-band")])
            row.Children.Add(GlyphTile(form, bands, caption));

        return new FigureScene(row);
    }

    private static Control GlyphTile(NetworkForm form, int bands, string caption) => new StackPanel
    {
        Spacing = 2,
        Children =
        {
            new DocSymbolGlyph
            {
                Kind = SymbolKind.Match, PortCount = 2, MatchForm = form, MatchBands = bands,
                Width = 150, Height = 120,
            },
            new TextBlock
            {
                Text = caption, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
            },
        },
    };

    /// <summary>
    /// Build the Designer, let it wire itself up, then detach its content so the figure is the panel
    /// and not a nested window — carrying the window's own <c>Styles</c> and <c>Resources</c> across.
    /// </summary>
    /// <remarks>
    /// <para><b>Known gap: the RESPONSE pane is not in these figures</b>, and it is not the styles.
    /// <c>MatchDesignerWindow</c>'s <c>OnLoaded</c> owns what the AXAML cannot — it binds the two
    /// <c>PlotControl</c>s to the view-model's plot host and sets the pane grid's column widths, both
    /// through <c>this.FindControl</c>, i.e. against the WINDOW. A window that is never loaded never
    /// runs any of it. Showing the window first WAS tried and is not the fix: a second top-level
    /// inside the generator's own capture window fails the run outright ("Attempt to call
    /// InvalidateArrange on wrong LayoutManager"). The Match chapter's response figure is captured
    /// from the view-model's own plot instead (<see cref="DualBandResponse"/>), which is larger and
    /// more readable than the pane would have been; making the pane itself capturable needs the
    /// window's wiring to be reachable without a window, which is a change to shipping code.</para>
    ///
    /// <para><b>And the Styles carry-across is load-bearing too, for the same class of reason.</b>
    /// Every size, weight and colour in this window is a style CLASS — <c>detailLabel</c>,
    /// <c>val</c>, <c>cardhdr</c>, <c>note</c>, <c>panelhdr</c> — declared in
    /// <c>&lt;Window.Styles&gt;</c>. A detached content control is no longer a descendant of that
    /// window, so none of those selectors match and every classed control falls back to the inherited
    /// default: the band rows, the solutions panel and the value grid all rendered several points too
    /// large, with labels running into their values, and nothing errored (owner, 2026-08-29). Inline
    /// <c>FontSize</c> attributes kept working throughout, which is what made it read as a rendering
    /// quirk rather than a missing style scope.</para>
    /// </remarks>
    private static FigureScene WholeWindow(MatchDesignerViewModel vm)
    {
        var window = new MatchDesignerWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("MatchDesignerWindow has no content control to capture.");

        if (window.Styles.Count == 0)
            throw new InvalidOperationException(
                "MatchDesignerWindow declares no Styles. Every Match figure's text size comes from "
              + "that block, so a capture without it is silently wrong rather than empty.");

        var styles = window.Styles.ToList();
        var resources = window.Resources;

        window.Content = null;
        window.Styles.Clear();

        foreach (var style in styles) content.Styles.Add(style);
        foreach (var kv in resources) content.Resources[kv.Key] = kv.Value;

        content.DataContext = vm;

        return new FigureScene(content)
        {
            // The variant is applied by the generator AFTER Build() returns, so the plots' own theme
            // is set here — the window's ActualThemeVariantChanged hook is on a window that is no
            // longer in the picture.
            AfterLayout = root => ApplyPlotTheme(root, vm),
        };
    }

    /// <summary>Point every <see cref="PlotControl"/> under <paramref name="root"/> at the variant the
    /// generator has just applied.</summary>
    private static void ApplyPlotTheme(Control root, MatchDesignerViewModel vm)
    {
        var theme = ThemeService.CurrentVariant == ColorVariant.Dark ? RenderTheme.Dark : RenderTheme.Light;
        vm.PlotHost.Theme = theme;
        foreach (var plot in root.GetVisualDescendants().OfType<PlotControl>())
        {
            plot.PlotTheme = theme;
            plot.InvalidateVisual();
        }
    }
}
