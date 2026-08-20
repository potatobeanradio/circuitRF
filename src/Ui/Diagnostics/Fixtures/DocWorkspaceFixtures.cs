using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;
using CircuitRF.Ui.Views;
using CircuitRF.Ui.Views.Content;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The WORKSPACE figure: circuitRF's whole window — menu bar, toolbar, the docked tool panels and a
/// document open in the middle — captured from the real <see cref="WorkspaceWindow"/> driven by a
/// real <see cref="WorkspaceViewModel"/>.
///
/// <para><b>Nothing here is a mock-up of the shell.</b> Every other figure in the catalog captures
/// one view; this one captures the thing those views live inside, which is what a reader needs
/// before any of them mean anything. It is the same window <c>App</c> shows, holding the same dock
/// tree, opened on a workspace through the same "open a workspace" command a double-click uses.</para>
///
/// <para><b>Three things had to be arranged, and each is load-bearing.</b></para>
///
/// <list type="number">
/// <item><b>A workspace to open.</b> There is no example workspace tracked in this repository
/// (<c>circuitRF_demo/</c> is git-ignored — see <see cref="DocFixtures"/>), so this fixture writes
/// one, into a temporary directory it deletes afterwards. It writes it through the real
/// <c>CellPersistence</c>/<c>SchematicPersistence</c>/<c>LayoutPersistence</c>/<c>WorkspacePersistence</c>
/// writers and fills it with the shipped schematic template and the shipped starter technology, so
/// the content is real and a format change breaks this loudly rather than quietly.</item>
///
/// <item><b>The window's DataContext, set on the CONTENT.</b> The capture renders the window's
/// content, not the <see cref="Window"/> (a captured window is wrapped in the generator's own
/// synthetic frame, §3.3). Detaching the content costs it the inherited DataContext, and the whole
/// dock tree is bound through it — the first capture came back as a toolbar over an empty grey
/// rectangle, with nothing reported, because <c>Layout</c> bound to nothing.</item>
///
/// <item><b>The in-window menu bar, forced visible.</b> It carries
/// <c>IsVisible="{OnPlatform True, macOS=False}"</c>, because macOS puts those menus in the system
/// menu bar, which is not in any visual tree and cannot be captured (§3.3). A figure generated on a
/// macOS machine would therefore be missing a menu bar that Windows and Linux readers have on
/// screen — and would silently differ from one generated on Linux. Forcing it on makes the figure
/// the same everywhere, and the page says where macOS puts it.</item>
/// </list>
///
/// <para><b>Determinism.</b> <see cref="WorkspaceViewModel"/>'s constructor reads the real
/// preferences file, so the capture would otherwise carry whoever generated it: their launch window
/// layout (this was visible — the Library panel moved columns), their colour scheme and their
/// installed PDKs in the palette. <c>tools/DocGen</c> redirects
/// <see cref="CircuitRF.Ui.AppDataRoot"/> to a throwaway directory before it starts, which is what
/// makes this a first-launch installation every time. The two remaining sources of run-to-run churn
/// are handled here: the message log is cleared of the absolute temporary paths it just logged, and
/// message timestamps are switched off for the capture.</para>
/// </summary>
public static class DocWorkspaceFixtures
{
    /// <summary>The workspace folder name — it is the Project panel's root label in the figure.</summary>
    private const string WorkspaceName = "Amplifier Design";

    private const string AmplifierCell = "FET Amplifier";
    private const string BendCell      = "Mitred Bend";

    // ── The figures ───────────────────────────────────────────────────────────

    /// <summary>The workspace as it stands with a schematic open: the plain overview figure.</summary>
    public static FigureScene Overview() => Build(withCallouts: false);

    /// <summary>The same capture with a numbered dot on each region of <see cref="WorkspaceRegions"/>.</summary>
    public static FigureScene Regions() => Build(withCallouts: true);

    // ── Building it ───────────────────────────────────────────────────────────

    private static FigureScene Build(bool withCallouts)
    {
        string root = NewTempDir();
        string cws  = WriteWorkspace(Path.Combine(root, WorkspaceName));

        var priorMode = MessageDisplay.Mode;
        MessageDisplay.Mode = MessageTimestampMode.None;

        var vm = new WorkspaceViewModel();

        // The window is built only to get its content: the generator draws its own frame, and a real
        // Window cannot be the captured visual anyway.
        var window  = new WorkspaceWindow { DataContext = vm };
        var content = (Control)window.Content!;
        window.Content = null;
        content.DataContext = vm;

        ShowInWindowMenuBar(content);

        vm.OpenWorkspacePath(cws);
        Pump();

        // Two tabs, so the figure shows that a workspace holds more than one kind of document — and
        // the schematic re-opened last, so it is the one in front. Re-opening an open document
        // activates its tab; it does not open a second one.
        OpenCellView(vm, BendCell, ViewType.Layout);
        OpenCellView(vm, AmplifierCell, ViewType.Schematic);

        DescribeInMessages(vm);

        var captured = withCallouts ? WorkspaceRegions.Overlay(content) : content;

        return new FigureScene(captured)
        {
            AfterLayout = c =>
            {
                SelectSomethingWorthInspecting(c);
                if (withCallouts) WorkspaceRegions.Fill(c);
            },
            Cleanup = () =>
            {
                MessageDisplay.Mode = priorMode;
                TryDelete(root);
            },
        };
    }

    // ── The workspace on disk ─────────────────────────────────────────────────

    /// <summary>
    /// Write a small but real workspace: two cells — one carrying the shipped FET S-parameter test
    /// bench as its schematic, one carrying a layout — plus the starter PCB technology the layout
    /// is drawn on. Returns the path of its <c>.cws</c>.
    /// </summary>
    private static string WriteWorkspace(string dir)
    {
        Directory.CreateDirectory(dir);

        // The technology the layout cell resolves against, written as an ordinary workspace file.
        var tech = StarterTechnologies.Pcb2Layer();
        string techFile = Path.Combine(dir, tech.Name + ".ctech");
        TechPersistence.SaveToFile(techFile, tech);

        // Cell 1 — a test bench, from the shipped schematic template.
        string amp = Path.Combine(dir, AmplifierCell);
        Directory.CreateDirectory(Path.Combine(amp, CellFolder.SchematicSubFolder));
        CellPersistence.SaveToFile(Path.Combine(amp, CellFolder.CcellFileName),
                                   new CcellFile { IsTestBench = true });
        SchematicPersistence.SaveToFile(
            Path.Combine(amp, CellFolder.SchematicSubFolder, AmplifierCell + ".csch"),
            ShippedSchematicTemplates.Load(DocFixtures.SchematicTemplateId), AmplifierCell);

        // Cell 2 — a layout, so the tree shows a cell with a layout view and the figure can show one.
        string bend = Path.Combine(dir, BendCell);
        Directory.CreateDirectory(Path.Combine(bend, CellFolder.LayoutSubFolder));
        CellPersistence.SaveToFile(Path.Combine(bend, CellFolder.CcellFileName), new CcellFile());
        LayoutPersistence.SaveToFile(
            Path.Combine(bend, CellFolder.LayoutSubFolder, BendCell + ".clay"),
            DocLayoutFixtures.Artwork());

        string cws = Path.Combine(dir, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile { DefaultTechRef = Path.GetFileName(techFile) });
        return cws;
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "circuitRF-docs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* a leftover temp directory is not worth failing a documentation run over */ }
    }

    // ── Driving the workspace ─────────────────────────────────────────────────

    /// <summary>
    /// Open one view of one cell the way a user does — by finding the cell in the Project panel's own
    /// tree and asking the workspace to open it. Going through the tree rather than calling an
    /// internal open path means primacy resolution, tab de-duplication and the active-tab bookkeeping
    /// all run exactly as they do for a double-click.
    /// </summary>
    private static void OpenCellView(WorkspaceViewModel vm, string cellName, ViewType view)
    {
        var tree = ((CircuitRfDockFactory)vm.DockFactory).ProjectTreeTool
                   ?? throw new InvalidOperationException("the dock factory built no Project Tree tool.");

        var cell = tree.RootItems.SelectMany(r => r.Children)
                       .FirstOrDefault(n => n.Kind == NodeKind.Cell && n.Name == cellName)
            ?? throw new InvalidOperationException(
                $"the docs workspace has no cell '{cellName}'. The Project Tree scanned: "
              + string.Join(", ", tree.RootItems.SelectMany(r => r.Children).Select(n => $"{n.Kind} {n.Name}")));

        switch (view)
        {
            case ViewType.Schematic: vm.OpenCellSchematic(cell); break;
            case ViewType.Layout:    vm.OpenCellLayout(cell);    break;
            default:                 vm.OpenCellSymbol(cell);    break;
        }
        Pump();
    }

    /// <summary>
    /// Replace the log with what just happened, said without the absolute temporary paths.
    ///
    /// <para>The real messages are correct and are exactly what a user sees — they name the file that
    /// was opened, in full. But the file here lives in a per-run temporary directory, so leaving them
    /// would put a different machine-specific path into the committed SVG on every regeneration and
    /// <c>tools/DocGen/check-docs-current.sh</c> would never pass twice. Clearing and re-stating is
    /// the smallest change that keeps the panel showing a real log rather than an empty box.</para>
    /// </summary>
    private static void DescribeInMessages(WorkspaceViewModel vm)
    {
        var messages = ((CircuitRfDockFactory)vm.DockFactory).MessagesTool
                       ?? throw new InvalidOperationException("the dock factory built no Messages tool.");
        messages.Clear();
        messages.Post(MessageLevel.Info, $"Opened workspace '{WorkspaceName}'.");
        messages.Post(MessageLevel.Info, $"Opened schematic '{AmplifierCell}'.");
        messages.Post(MessageLevel.Info, $"Opened layout '{BendCell}'.");
        Pump();
    }

    /// <summary>
    /// Select one component on the open schematic, so the Properties panel in the figure is showing a
    /// component's parameters rather than "Select object to inspect its properties."
    /// </summary>
    private static void SelectSomethingWorthInspecting(Control root)
    {
        var doc = root.GetVisualDescendants().OfType<SchematicView>()
                      .Select(v => v.DataContext).OfType<SchematicDocument>().FirstOrDefault();
        if (doc is null) return;

        var comp = doc.ViewModel.EditModel.Components.FirstOrDefault(c => c.InstanceName == "Q1")
                ?? doc.ViewModel.EditModel.Components.FirstOrDefault();
        if (comp is not null) doc.ViewModel.SelectIfUnselected(comp.Id);
        Pump();
    }

    /// <summary>
    /// Show the in-window menu bar. See the type header, item 3: on macOS it is deliberately hidden
    /// because the menus are native, and a native menu bar cannot be captured.
    /// </summary>
    private static void ShowInWindowMenuBar(Control content)
    {
        var menu = content.GetLogicalDescendants().OfType<Menu>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "the workspace window no longer contains a Menu. The figure's menu bar comes from the "
              + "real in-window menu, so if that has moved or been replaced this fixture must follow it "
              + "rather than draw one of its own.");
        menu.IsVisible = true;
    }

    private static void Pump()
    {
        for (int i = 0; i < 12; i++) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
