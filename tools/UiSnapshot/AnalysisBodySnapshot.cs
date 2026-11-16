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
/// its Advanced section open so the grid-points-at-once spinner is seen, set to 4. brief TRN-7 R-trn7-7 — the transient body of a
/// bench that says Solver=ngspice, with discovery stubbed to find none (<c>tran-ngspice-missing</c>: the choice greyed, the note
/// beneath) or ngspice 47 (<c>tran-ngspice-found</c>).
/// </summary>
static class AnalysisBodySnapshot
{
    public static int Run(string kind, string output, double width, double height)
    {
        var model = new SchematicEditModel();
        if (kind.StartsWith("tran-ngspice-", StringComparison.Ordinal))
            return Tran(kind == "tran-ngspice-found", output, width, height);
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

    private static int Tran(bool found, string output, double width, double height)
    {
        NgspiceAvailability.Set(found ? new CircuitRF.Engine.Transient.NgspiceProgram("/usr/local/bin/ngspice", "47") : null);
        var view = new TranBodyView
        {
            DataContext = TranBodyViewModel.FromAnalysis(new TransientAnalysis("TR1") { StopExpr = "2u", StepExpr = "1n", Solver = TransientAnalysis.SolverNgspice }),
        };
        var window = new Window { Width = width, Height = height, Title = "Analysis", Content = view };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("SystemControlBackgroundAltHighBrush"));
        window.Show();
        for (int k = 0; k < 8; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
        window.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}");
        return 0;
    }
}
