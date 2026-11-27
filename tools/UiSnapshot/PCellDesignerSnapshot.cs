using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Views.PCellDesigner;

/// <summary>
/// The PCell Designer window on a designed cell (<c>--pcell-designer &lt;cell folder&gt;</c>), its canvas planned and its
/// response computed before the capture (brief-pcd-8 R-pcd8-3). The workspace is the folder holding the nearest
/// <c>.cws</c> above the cell.
/// </summary>
static class PCellDesignerSnapshot
{
    public static int Run(string cellDir, string output, double width, double height)
    {
        cellDir = Path.GetFullPath(cellDir);
        string? ws = Path.GetDirectoryName(cellDir);
        while (ws is not null && !File.Exists(Path.Combine(ws, ".cws"))) ws = Path.GetDirectoryName(ws);
        if (ws is null) { Console.Error.WriteLine($"{cellDir} is in no workspace."); return 1; }

        var vm = new PCellDesignerViewModel(cellDir, ws);
        var design = vm.DesignAsync(0);
        while (!design.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();

        var window = new PCellDesignerWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        for (int k = 0; k < 8; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
        Console.WriteLine(vm.Status);
        SnapshotSave.Save(window, output);
        Console.WriteLine($"wrote {output}");
        return 0;
    }
}
