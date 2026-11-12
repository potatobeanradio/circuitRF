using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views.Dialogs;

// HZN-3 — UiSnapshot's two views of a Hertzon project:
//
//   --project-tree <workspace> --tree-expand <row name> [--tree-expand …] [--tree-menu <row name>]
//       the Project Tree with those rows expanded (a .hzn expands to its designs) and that row's context menu open;
//   --hzn-open <file.hzn> --hzn-design <name> -o out.png [--size WxH]
//       the design as the workspace opens it: the 3D editor on a HznDesignSource — or, when the design is refused,
//       the dialog over the refusal. The 3D canvas itself cannot draw headless; the toolbar and the Object Tree do.

internal static class SnapshotHzn
{
    private static IEnumerable<ProjectTreeNodeViewModel> Walk(ProjectTreeNodeViewModel row)
        => row.Children.SelectMany(Walk).Prepend(row);

    /// <summary>Expands every row named in <paramref name="expand"/>, then opens <paramref name="menuRow"/>'s context menu.</summary>
    public static void PrepareTree(ProjectTreeTool tool, Window host, IReadOnlyList<string> expand, string? menuRow, Action pump)
    {
        foreach (string name in expand)
            foreach (var row in tool.RootItems.SelectMany(Walk).Where(r => r.Name == name).ToList())
                row.IsExpanded = true;
        pump();
        if (menuRow is null) return;

        var target = tool.RootItems.SelectMany(Walk).LastOrDefault(r => r.Name == menuRow)
                     ?? throw new InvalidOperationException($"--tree-menu: no row named '{menuRow}'");
        var grid = host.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => ReferenceEquals(g.DataContext, target) && g.ContextMenu is not null)
                   ?? throw new InvalidOperationException($"--tree-menu: '{menuRow}' has no row on screen");
        grid.ContextMenu!.Open(grid);
        pump();
    }

    /// <summary>The window for <c>--hzn-open</c>.</summary>
    public static Window Open(string hznPath, string design, double width, double height)
    {
        var cache = new TechnologyCache();
        var source = new HznDesignSource(hznPath, design, cache);
        C3dDocument document;
        try { document = source.Load(); }
        catch (HznOpenException ex)
        {
            var dialog = TextConfirmDialog.Build(source.Title, WorkspaceViewModel.HznCannotOpen, ex.Message, null, _ => { });
            return dialog;
        }

        var vm = new C3dEditorViewModel(source.FilePath, document, CircuitRF.Ui.Viewer3D.Viewer3DBackends.Create, () => null,
                                        a => Dispatcher.UIThread.Post(a), cache, source: source);
        vm.Viewer.Resized(400, 300);
        vm.Viewer.Session.EnsureBackend();
        vm.Start();
        for (var w = System.Diagnostics.Stopwatch.StartNew();
             vm.AdoptedGeneration != vm.Viewer.Source.Requested && w.Elapsed < TimeSpan.FromMinutes(1);)
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        for (int k = 0; k < 50; k++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Console.WriteLine($"{source.Title}: {vm.Document.Objects.Count} objects, {vm.Document.Ports.Count} ports; save says: {vm.Save()}");
        return new Window
        {
            Width = width, Height = height,
            Content = new CircuitRF.Ui.Views.ThreeD.C3dEditorView { DataContext = new C3dEditorDocument(vm) },
        };
    }
}
