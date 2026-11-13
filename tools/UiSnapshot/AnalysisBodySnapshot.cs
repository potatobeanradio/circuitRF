using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Analyses;

/// <summary>
/// LP-1 — the loadpull (<c>--analysis-body lp</c>) or pursuit (<c>lpp</c>) analysis body as the analysis dialog hosts it,
/// its Advanced section open so the grid-points-at-once spinner is seen, set to 4.
/// </summary>
static class AnalysisBodySnapshot
{
    public static int Run(string kind, string output, double width, double height)
    {
        var model = new SchematicEditModel();
        Control view = kind == "lpp"
            ? new LppBodyView { DataContext = LppBodyViewModel.FromAnalysis(new LoadpullPursuitAnalysis("LPP1") { WorkersExpr = "4" }, model) }
            : new LpBodyView { DataContext = LpBodyViewModel.FromAnalysis(new LoadpullAnalysis("LP1") { GridPath = "load_grid.gam", WorkersExpr = "4" }, model) };
        var window = new Window { Width = width, Height = height, Title = "Analysis", Content = view };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("SystemControlBackgroundAltHighBrush"));
        window.Show();
        void Settle()
        {
            for (int k = 0; k < 8; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
        }
        Settle();
        foreach (var e in view.GetVisualDescendants().OfType<Expander>()) e.IsExpanded = true;
        Settle();
        // The Advanced section is the body's last: scroll to it.
        foreach (var s in view.GetVisualDescendants().OfType<ScrollViewer>().Take(1)) s.Offset = new Vector(0, s.Extent.Height);
        Settle();
        window.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}");
        return 0;
    }
}
