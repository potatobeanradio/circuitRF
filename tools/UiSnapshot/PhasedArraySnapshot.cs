using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.PhasedArray;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.PhasedArray;
using SkiaSharp;

/// <summary>
/// brief-paa-9 — the Phased Array tool window on one array of a <c>.cnl</c> bench (<c>--phased-array bench.cnl</c>), the real window with its
/// view model's work run at once. A bench with no cell definitions is drawn as the schematic <c>netlist --to-schematic</c> draws and the tool
/// netlists that drawing, as the application would; a bench that defines cells (a channel, an amplifier) cannot be drawn, so the tool sees
/// the array instance and the VARs and evaluates the bench's own netlist.
/// <para><c>--pa-color</c> amplitude|phase|gamma|power|pout|pae|nf colours the aperture; <c>--pa-tab</c> beam|scan|tapers|failures|frequency
/// picks the tab (its work done first); <c>--pa-drag N</c> drags the beam from boresight to <c>--pa-to</c> θ (default 30°) in N steps and
/// prints each frame's evaluation, forming and drawing — R-paa9-7's measurement.</para>
/// </summary>
static class PhasedArraySnapshot
{
    public static int Run(string cnl, string? block, string color, string tab, int drag, double dragTo, string output, double width, double height)
    {
        string full = Path.GetFullPath(cnl), dir = Path.GetDirectoryName(full)!;
        // the bench's relative file references made absolute in memory, so the drawing (which has no folder of its own) and the netlist
        // the tool reads back from it name the same files
        string text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(full), "(Record|File)=\"(?!/)([^\"]+)\"",
                                                                   m => $"{m.Groups[1].Value}=\"{Path.GetFullPath(Path.Combine(dir, m.Groups[2].Value))}\"");
        var (lib, tb) = new CnlReader().Read(text, "bench", dir);
        SchematicEditModel model;
        Func<SchematicViewModel, PreparedCircuit?> prepare;
        var drawn = lib.Cells.Count == 0 ? NetlistSchematic.Build(lib, tb, null) : null;
        if (drawn?.Schematic is { } s && drawn.Refusals.Count == 0)
        {
            model = s;
            prepare = vm => PreparedCircuit.FromText(SchematicCircuit.CnlTextOf(vm.EditModel, "bench"), dir, dir);
        }
        else
        {
            model = Minimal(tb, block);
            prepare = _ => PreparedCircuit.FromText(text, dir, dir);
        }
        var schematic = new SchematicViewModel(model);
        var instance = model.Components.First(c => c.Symbol == SymbolKind.PhasedArray && (block is null || c.InstanceName == block));
        var clock = Stopwatch.StartNew();
        var vm = new PhasedArrayToolViewModel(schematic, instance, prepare, new ImmediatePhasedArrayHost());
        Console.WriteLine($"opened in {clock.Elapsed.TotalMilliseconds:0} ms: {vm.Status}");
        if (vm.Frame is null) return 1;
        vm.Quantity = color switch
        {
            "phase" => ApertureQuantity.Phase, "gamma" => ApertureQuantity.GammaActive, "power" => ApertureQuantity.DeliveredPower,
            "pout" => ApertureQuantity.PaPout, "pae" => ApertureQuantity.PaPae, "nf" => ApertureQuantity.ChannelNf, _ => ApertureQuantity.Amplitude,
        };
        foreach (var r in vm.Readouts) Console.WriteLine($"  {r.Label}: {r.Value}");

        if (drag > 0) Drag(vm, drag, dragTo);

        var window = new PhasedArrayWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        void Settle()
        {
            for (int k = 0; k < 8; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
        }
        Settle();
        var tabs = window.FindControl<TabControl>("Tabs")!;
        tabs.SelectedIndex = tab switch { "scan" => 1, "tapers" => 2, "failures" => 3, "frequency" => 4, _ => 0 };
        if (tab == "scan") vm.StartScan();
        if (tab == "failures") vm.RunFailures();
        if (tab == "frequency") vm.RunFrequency();
        if (tab == "scan") Console.WriteLine(vm.ScanStatus);
        if (tab == "failures") Console.WriteLine(vm.FailureSummary);
        Settle();
        window.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}");
        return 0;
    }

    /// <summary>R-paa9-7 — a beam drag of <paramref name="steps"/> steps, each frame's evaluation and forming (the view model's) and its
    /// drawing (the window's three renderers at their panel sizes), in milliseconds.</summary>
    private static void Drag(PhasedArrayToolViewModel vm, int steps, double to)
    {
        if (!vm.BeginDrag()) { Console.WriteLine($"no drag: {vm.Steering.Refusal}"); return; }
        var rows = new List<(double Eval, double Form, double Draw)>();
        var theme = RenderTheme.Light;
        using var surface = SKSurface.Create(new SKImageInfo(600, 520));
        for (int i = 1; i <= steps; i++)
        {
            vm.DragToAngles(to * i / steps, 0);
            var sw = Stopwatch.StartNew();
            var c = surface.Canvas;
            ArrayRenderers.DrawAperture(c, new SKRect(0, 0, 600, 520), ApertureCanvas.SceneOf(vm)!, theme);
            ArrayRenderers.DrawUvMap(c, new SKRect(0, 0, 600, 520), UvMapCanvas.SceneOf(vm)!, theme);
            ArrayRenderers.DrawCuts(c, new SKRect(0, 0, 600, 260), CutsCanvas.TracesOf(vm), false, -50, theme);
            c.Flush();
            rows.Add((vm.Frame!.EvaluationMs, vm.Frame.FormingMs, sw.Elapsed.TotalMilliseconds));
            Console.WriteLine($"  step {i,2}: evaluation {rows[^1].Eval,6:0.0} ms, forming {rows[^1].Form,6:0.0} ms ({vm.Frame.Breakdown}), drawing {rows[^1].Draw,5:0.0} ms");
        }
        var settled = rows.Skip(Math.Min(5, rows.Count / 2)).ToList();
        double Median(IEnumerable<double> v) { var o = v.Order().ToList(); return o[o.Count / 2]; }
        Console.WriteLine($"drag median after warm-up: evaluation {Median(settled.Select(r => r.Eval)):0.0} ms, forming {Median(settled.Select(r => r.Form)):0.0} ms, " +
                          $"drawing {Median(settled.Select(r => r.Draw)):0.0} ms, frame {Median(settled.Select(r => r.Eval + r.Form + r.Draw)):0.0} ms");
        vm.EndDrag();
    }

    /// <summary>The array instance and the bench's globals as a VAR block: what the tool reads of a schematic, for a bench that cannot be drawn.</summary>
    private static SchematicEditModel Minimal(CircuitRF.Core.Design.TestBench tb, string? block)
    {
        var model = new SchematicEditModel();
        var vars = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var };
        foreach (var v in tb.GlobalVariables) vars.Parameters.Add(new EditableParameter { Name = v.Name, Expression = v.Expression, Unit = v.Unit ?? "" });
        model.Components.Add(vars);
        var inst = tb.Instances.First(i => i.Reference == "PhasedArray" && (block is null || i.InstanceName == block));
        var pa = new EditableComponent { InstanceName = inst.InstanceName, Symbol = SymbolKind.PhasedArray };
        foreach (var o in inst.Overrides) pa.Parameters.Add(new EditableParameter { Name = o.Name, Expression = o.Expression, Unit = o.Unit ?? "" });
        model.Components.Add(pa);
        return model;
    }
}
