using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Views.ThreeD;

/// <summary>--hzn-export-dialog &lt;file.c3d&gt; [--hzn-into &lt;project.hzn&gt;] [--hzn-name &lt;name&gt;]: File ▸ Export ▸ Hertzon Project… as it opens (HZN-4).</summary>
internal static class HznExportSnapshot
{
    public static int Show(string c3d, string? into, string? name, string output)
    {
        var doc = C3dPersistence.LoadFromFile(c3d);
        var vm = new HznExportDialogViewModel(doc, c3d, null, Path.GetDirectoryName(c3d) ?? ".");
        if (into is not null)
        {
            vm.NewProject = false;
            vm.TargetPath = into;
        }
        if (name is not null) vm.DesignName = name;
        var plan = vm.PlanAsync();
        while (!plan.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        plan.GetAwaiter().GetResult();
        var dialog = new HznExportDialog(vm);
        dialog.Show();
        for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
        dialog.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}");
        return 0;
    }
}
