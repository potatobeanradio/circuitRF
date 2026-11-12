using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Dialogs;

// HZN-8 — UiSnapshot's views of Run in Hertzon:
//
//   --hertzon run-dialog --hertzon-project <file.hzn> [--hertzon-design <name>]   the run dialog, from the installed hertzon-cli's describe
//   --hertzon progress                                                            the Messages panel mid-run (the live row with its pass counter)
//   --hertzon mismatch                                                            Settings' Hertzon row and the refusal when the schema is not the supported one

internal static class SnapshotHertzon
{
    private static void Pump(Window w)
    {
        for (int k = 0; k < 8; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); w.UpdateLayout(); }
    }

    private static void Save(Window w, string path)
    {
        w.Show();
        Pump(w);
        w.CaptureRenderedFrame()!.Save(path);
        Console.WriteLine($"wrote {path}");
    }

    public static int Run(string mode, string? project, string? design, string output, double width, double height)
    {
        switch (mode)
        {
            case "run-dialog":
            {
                if (project is null) { Console.Error.WriteLine("--hertzon run-dialog needs --hertzon-project <file.hzn>"); return 1; }
                var ready = HertzonDiscovery.Shared.Check();
                if (!ready.Ready) { Console.Error.WriteLine(ready.Refusal); return 1; }
                var described = HertzonRun.Describe(ready.Installation!, project);
                if (!described.Ok) { Console.Error.WriteLine(described.Error); return 1; }
                var chosen = described.Designs.First(d => d.IsThreeD && (design is null || d.Name == design));
                Save(HertzonRunDialog.Build(project, chosen, ready.Installation!.Version, _ => { }), output);
                return 0;
            }
            case "progress":
            {
                var tool = new MessagesTool();
                tool.Post(MessageLevel.Info, "Hertzon 0.3.19: solving 'Microstrip50' (Setup1 / Sweep) from Microstrip.hzn. With w = 2.9mm.");
                var done = tool.BeginProgress("Hertzon 'Coax50'");
                done.Complete(MessageLevel.Success, "Hertzon 0.3.19: 2 adaptive pass(es), 8,676 tetrahedra, 4.1 s");
                var live = tool.BeginProgress("Hertzon 'Microstrip50'");
                live.Update("Hertzon 'Microstrip50' — Adaptive pass 3 of max 20", "pass 3 · 18,234 tetrahedra · ΔS 0.031", 24);
                var window = new Window { Width = width > 0 ? width : 760, Height = height > 0 ? height : 200,
                                          Content = new CircuitRF.Ui.Views.Messages.MessagesView { DataContext = tool } };
                Save(window, output);
                return 0;
            }
            case "mismatch":
            {
                var installed = new HertzonInstallation("/opt/hertzon/.venv/bin/hertzon-cli", "0.4.0", 2, HertzonHowFound.Path, "on PATH");
                var ready = new HertzonReadiness(installed, HertzonDiscovery.SchemaRefusal(installed), []);
                var row = new HertzonSettingsRow();
                row.Show(ready);
                var tool = new MessagesTool();
                tool.Post(MessageLevel.Error, ready.Refusal!);
                var panel = new StackPanel { Spacing = 12, Children = { row, new CircuitRF.Ui.Views.Messages.MessagesView { DataContext = tool, Height = 80 } } };
                Save(new Window { Width = width > 0 ? width : 760, Height = height > 0 ? height : 300, Content = panel }, output);
                return 0;
            }
            default:
                Console.Error.WriteLine($"--hertzon: unknown view '{mode}' (run-dialog, progress, mismatch)");
                return 1;
        }
    }
}
