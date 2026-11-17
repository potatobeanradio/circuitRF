using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Filters;
using CircuitRF.Ui.Views.Filters;

/// <summary>
/// The Filter Designer window over an empty schematic (<c>--filter-designer any|mlad|mcrc|mcla</c>), its responses computed before the
/// capture; <c>--size</c>, <c>--fd-left</c> and <c>--fd-bottom</c> show how the panes clip when small. <c>--fd-busy</c> captures it as it looks while a realisation computes:
/// the bar beside "Realisers" shown (the headless platform never fires the bar's delay timer, so it is elapsed here by hand).
/// </summary>
static class FilterDesignerSnapshot
{
    public static int Run(string family, bool busy, double? left, double? bottom, string output, double width, double height)
    {
        SymbolKind? kind = family switch { "mlad" => SymbolKind.Mlad, "mcrc" => SymbolKind.Mcrc, "mcla" => SymbolKind.Mcla, _ => null };
        var vm = new FilterDesignerViewModel(new CircuitRF.Ui.ViewModels.SchematicViewModel(new SchematicEditModel()), kind);
        // the responses come back through the dispatcher (the await resumes on the UI thread): pumped, never blocked on
        void Drain() { while (!vm.Realising.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } Dispatcher.UIThread.RunJobs(); }
        Drain();
        var window = new FilterDesignerWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        void Settle()
        {
            for (int k = 0; k < 8; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
        }
        Settle();
        // --fd-left / --fd-bottom: the left column and the What Place writes row as a splitter would leave them (the splitters' own minimums hold)
        if ((left ?? bottom) is not null
            && window.GetVisualDescendants().OfType<Avalonia.Controls.Grid>().FirstOrDefault(g => g.Children.OfType<Avalonia.Controls.GridSplitter>().Any()) is { } panes)
        {
            if (left is { } l) panes.ColumnDefinitions[0].Width = new Avalonia.Controls.GridLength(Math.Max(l, panes.ColumnDefinitions[0].MinWidth));
            if (bottom is { } b) panes.RowDefinitions[4].Height = new Avalonia.Controls.GridLength(Math.Max(b, panes.RowDefinitions[4].MinHeight));
            Settle();
        }
        if (busy)
        {
            vm.Working = true;
            var bar = typeof(FilterDesignerWindow).GetField("_realiseBar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            bar.GetType().GetMethod("Elapsed")!.Invoke(bar, null);
            Settle();
        }
        Console.WriteLine(vm.Status);
        window.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}");
        return 0;
    }
}
