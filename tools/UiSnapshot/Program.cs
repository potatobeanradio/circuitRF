using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using CircuitRF.Ui;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Signals;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Views.Signals;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.Views.Tuning;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Views.Layout;
using CircuitRF.Design.Layout;

// UiSnapshot <file.cdd> -o <out.png> [--view canvas|inspector|display] [--plot n] [--size WxH] [--results <dir>]
//            [--click x,y] [--drag x0,y0:x1,y1] [--key <Key>] [--shot <png>] ...
// UiSnapshot --signal "<carrier>" [--signal "<carrier>"] -o <out.png> [--tab n] [--fc <expr>] [--size WxH] [steps]
// UiSnapshot --snp <file.sNp> -o <out.png> [--size WxH] [steps]
// UiSnapshot --standalone [--iq <file>] -o <out.png> [--tab n] [--size WxH] [steps]
// UiSnapshot --tuning <file.csch> -o <out.png> [--size WxH] [steps]
// UiSnapshot --optimizer <file.csch> -o <out.png> [--optimize] [--size WxH] [steps]
// UiSnapshot --clay <file.clay> -o <out.png> [--hide <layer>] [--lock <layer>] [--size WxH] [steps]
//            [--rclick x,y] [--menu <header>] [--type <text>] [--button <label>]
//            [--impedance <freq|none>] [--impedance-layer <name>]... [--finding <kind>] [--impedance-panel]
// UiSnapshot --cem <file.cem> -o <out.png> [--size WxH] [steps]
// UiSnapshot --review-panel checks|result|many|mmic -o <out.png> [--size WxH]
// UiSnapshot --symbol <stem> [-o <dir>]
// UiSnapshot --cmc <file.cmc|new:Passive|new:Active> -o <out.png> [--import <data>] [--step <name>] [--run <key>] [--marker]... [--size WxH] [steps]
//
// Opens the display through DisplayWindowViewModel.LoadAllAsync — the GUI's own Open — and photographs one
// real view: the plot canvas of the active tab, plot n's Plot Inspector (its trace cards), or the whole Data
// Display document view (toolbar, canvas and its key bindings). The input steps run IN ORDER, as real pointer and
// key events through the window, before the final capture; --shot captures an intermediate frame.
//
// --tuning opens the Tuning window on a schematic, its rows the ones the file has ticked Tune; after each step it
// prints what holds the keyboard and every row's value, so a key's effect is read rather than guessed from pixels.
//
// --optimizer opens the Optimizer window on a schematic as the shell hosts it, its circuit prepared as the workspace's
// own PrepareTunedCircuit prepares it; --optimize presses Run and waits for the run to finish before the capture. It
// prints the algorithm menu (a greyed entry with its reason), every variable row and the status line.
//
// --signal opens the Signal Designer window itself on a MOD_Source carrying those carriers (netlist form,
// Carrier[i]), waits for its preview — generated through the cache a simulation reads — and photographs the
// window; --tab 1 shows the second preview tab (EVM, Pulse or Frequency). --standalone opens it as Tools ▸ Signal
// Designer does, on no MOD_Source; --iq then reads an I/Q file through Load custom.
//
// --clay opens the layout editor view on a .clay, its technology resolved as the workspace resolves it, fitted. A step
// point written @x,y is in the layout's micrometres. --rclick opens the canvas's context menu, --menu clicks one of its
// items by header (prefix), --type types into the open dialog's text box and --button clicks a dialog button by its
// label. --hide and --lock untick a layer's row in the layer list. Messages print as they are posted. An open context
// menu is drawn in the window's own frame; a dialog is its own window, so a capture also writes <out>.dialog.png.
// --impedance runs the Impedance Analysis on the editor with that highest frequency (6GHz, or none) and prints each
// trace's findings; --finding then selects the first finding of that kind (pad-shunt, return-broken, …) as the
// panel's row does and zooms to it, so the canvas shows its marker selected. --impedance-layer narrows the run to the
// named copper layers. --impedance-panel then shows the real Impedance panel on the editor, waits for its export
// page counts, prints the export choice and its note, and writes <out>.impedance.png scrolled to the Export button.
//
// --review-panel shows the real Review panel (brief-review-6) on a layout seeded with no artwork: "checks" is the checks
// list with one threshold edited through its row; "mmic" the switch set to MMIC and the checks of a die; "result" a seeded result with a not-run check, an accepted finding
// and a check with no findings; "many" a seeded result of 5,000 findings in one check, its layers opened, and prints
// how many rows the list holds against how many row containers it realised — the proof that it virtualises.
//
// --place <code> (with --clay) drops one component at the origin through the layout palette's own drop, so the
// picture is what placing it draws.
//
// --review (with --clay) runs the editor's own Layout Review and draws its markers on the canvas, as the open Review
// panel does; each finding prints.
//
// --cem opens the EM Setup editor on a .cem, as the workspace opens one: the file's setup, its own view model, the real
// EmSetupEditorView. Every named CheckBox's state and tooltip is printed, so a setting is read as well as seen.
//
// --cmc opens the Model Composer window on a .cmc (OpenFile + BeginLoad, as File ▸ Open does) or on a new document of
// the named kind, chosen. --step selects a step and --run runs one of its tasks (propose, analyse, fit, ...) to the end;
// the status line and any proposal note print. Each --marker adds a marker on the S plot's first trace, its info box
// placed as a new marker's is. --import opens the Import Data dialog on the document instead — for an
// unsaved one the importer works against its provisional .cmc beside the data — after choosing <data> as its source.

//
// Vendor libraries (brief vendorlib-1…3). Each indexes the workspace's referenced vendor libraries first, as a
// workspace open starts them, so captions, counts and lists are filled in:
//   --palette <workspace> [--category <name>] [--search <q>]   the Library palette, a Type entry and a query chosen
//   --schematic <file.csch> [--select <instance>]             the schematic beside the Parameter Editor on one part;
//                                                              --type Name=VendorPartBox types into the Part field
//                                                              --dialog opens the Component Parameters dialog instead
//   --archive <workspace>                                     the Archive Workspace dialog
//   --manage-libraries <workspace>                            File ▸ Manage Libraries… (written to <out>.dialog.png)
//   --add-vendor-library <folder>                             Add Vendor Library's confirm step (<out>.dialog.png)
// A list drop-down, a context menu and a button's menu flyout are drawn in the window's own frame; a popup that opens
// as a top level of its own instead is written to <out>.popup.png.

// --symbol <stem> writes a component's documentation symbol (both variants) through the generator DocGen uses,
// into -o's directory — the same bytes a full DocGen run writes for it. No window is opened.
if (Array.IndexOf(args, "--symbol") is >= 0 and var si && si + 1 < args.Length)
{
    string stem = args[si + 1];
    int oi = Array.IndexOf(args, "-o");
    string dir = oi >= 0 && oi + 1 < args.Length ? Path.GetFullPath(args[oi + 1]) : Path.GetFullPath("docs/user/assets/symbols");
    var written = CircuitRF.Ui.Diagnostics.SymbolArtworkGenerator.GenerateAll(dir, f => f == stem);
    if (written.Count == 0) { Console.Error.WriteLine($"--symbol: no symbol figure '{stem}'"); return 2; }
    foreach (var f in written) Console.WriteLine($"wrote {f}");
    return 0;
}

// --c3d is the 3D editor's picture: no Avalonia window, so it branches off before the headless platform starts (Snapshot3D.cs).
if (args.Contains("--c3d")) return Snapshot3D.Run(args);

string? impedance = null, finding = null;
var impedanceLayers = new List<string>();
bool impedancePanel = false;
string? reviewPanel = null;
bool reviewRun = false;
string? place = null;
string? cdd = null, output = null, view = "canvas", results = null, fc = "2", iq = null, tuning = null, clay = null, cem = null;
// brief-dfr32-3 — a .c3d's setup cards beside its Properties panel: --delete-fields runs a card's Delete Saved Fields (into a
// scratch folder, never the Trash) and --c3d-row selects an Object Tree row (a field plot) for the panel.
string? c3dSetups = null, c3dDeleteFields = null, c3dRow = null;
string? optimizer = null;
string? ctech = null;
bool optimize = false;
string? cmc = null, cmcImport = null, cmcStep = null, cmcRun = null;
string? image = null, imageTech = null;
bool digitise = false, makeSchematic = false, stripGeometry = false;
string? imageScalePoints = null;
int cmcMarkers = 0;
string? palette = null, paletteCategory = null, paletteSearch = null, schematic = null, select = null;
bool parameterDialog = false;
string? archive = null, manageLibraries = null, addVendorLibrary = null;
string? gdsfactory = null, gdsfactoryPython = null, gdsfactoryKit = null, gdsfactoryCell = null, gdsfactoryWorkspace = null;
string? csym = null, csymPick = null;
bool dark = false;
var layerToggles = new List<(string Name, bool Hide)>();
bool standalone = false;
var signal = new List<string>();
string? snp = null;
int plotIndex = 1, previewTab = 0;
bool sizeGiven = false;
double width = 1000, height = 700;
var steps = new List<(string Kind, string Arg)>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" when i + 1 < args.Length:     output = args[++i]; break;
        case "--view" when i + 1 < args.Length: view = args[++i]; break;
        case "--plot" when i + 1 < args.Length: plotIndex = int.Parse(args[++i]); break;
        case "--results" when i + 1 < args.Length: results = args[++i]; break;
        case "--signal" when i + 1 < args.Length: signal.Add(args[++i]); break;
        case "--snp" when i + 1 < args.Length: snp = Path.GetFullPath(args[++i]); break;
        case "--tab" when i + 1 < args.Length: previewTab = int.Parse(args[++i]); break;
        case "--standalone": standalone = true; break;
        case "--iq" when i + 1 < args.Length: iq = Path.GetFullPath(args[++i]); break;
        case "--tuning" when i + 1 < args.Length: tuning = Path.GetFullPath(args[++i]); break;
        case "--optimizer" when i + 1 < args.Length: optimizer = Path.GetFullPath(args[++i]); break;
        case "--optimize": optimize = true; break;
        case "--clay" when i + 1 < args.Length: clay = Path.GetFullPath(args[++i]); break;
        case "--cem" when i + 1 < args.Length: cem = Path.GetFullPath(args[++i]); break;
        case "--ctech" when i + 1 < args.Length: ctech = Path.GetFullPath(args[++i]); break;
        case "--cmc" when i + 1 < args.Length: cmc = args[++i]; break;
        case "--image" when i + 1 < args.Length: image = Path.GetFullPath(args[++i]); break;
        case "--tech" when i + 1 < args.Length: imageTech = Path.GetFullPath(args[++i]); break;
        case "--digitise": digitise = true; break;
        case "--make-schematic": makeSchematic = true; break;
        case "--strip-geometry": stripGeometry = true; break;
        case "--scale-points" when i + 1 < args.Length: imageScalePoints = args[++i]; break;
        case "--import" when i + 1 < args.Length: cmcImport = Path.GetFullPath(args[++i]); break;
        case "--step" when i + 1 < args.Length: cmcStep = args[++i]; break;
        case "--run" when i + 1 < args.Length: cmcRun = args[++i]; break;
        case "--marker": cmcMarkers++; break;
        case "--hide" when i + 1 < args.Length: layerToggles.Add((args[++i], true)); break;
        case "--impedance" when i + 1 < args.Length: impedance = args[++i]; break;
        case "--finding" when i + 1 < args.Length: finding = args[++i]; break;
        case "--impedance-layer" when i + 1 < args.Length: impedanceLayers.Add(args[++i]); break;
        case "--impedance-panel": impedancePanel = true; break;
        case "--review-panel" when i + 1 < args.Length: reviewPanel = args[++i]; break;
        case "--review": reviewRun = true; break;
        case "--place" when i + 1 < args.Length: place = args[++i]; break;
        case "--lock" when i + 1 < args.Length: layerToggles.Add((args[++i], false)); break;
        case "--fc" when i + 1 < args.Length: fc = args[++i]; break;
        case "--c3d-setups" when i + 1 < args.Length: c3dSetups = Path.GetFullPath(args[++i]); break;
        case "--delete-fields" when i + 1 < args.Length: c3dDeleteFields = args[++i]; break;
        case "--c3d-row" when i + 1 < args.Length: c3dRow = args[++i]; break;
        case "--palette" when i + 1 < args.Length: palette = Path.GetFullPath(args[++i]); break;
        case "--csym" when i + 1 < args.Length: csym = Path.GetFullPath(args[++i]); break;
        case "--pick" when i + 1 < args.Length: csymPick = args[++i]; break;
        case "--dark": dark = true; break;
        case "--messages": break;
        case "--category" when i + 1 < args.Length: paletteCategory = args[++i]; break;
        case "--search" when i + 1 < args.Length: paletteSearch = args[++i]; break;
        case "--schematic" when i + 1 < args.Length: schematic = Path.GetFullPath(args[++i]); break;
        case "--select" when i + 1 < args.Length: select = args[++i]; break;
        case "--dialog": parameterDialog = true; break;
        case "--archive" when i + 1 < args.Length: archive = Path.GetFullPath(args[++i]); break;
        case "--manage-libraries" when i + 1 < args.Length: manageLibraries = Path.GetFullPath(args[++i]); break;
        case "--add-vendor-library" when i + 1 < args.Length: addVendorLibrary = Path.GetFullPath(args[++i]); break;
        // brief-gdf-2: the gdsfactory views — trust | pick | interpreter | report | prompt | permissions — with the
        // interpreter that has gdsfactory and (for trust/interpreter/report) a kit source tree.
        case "--gdsfactory" when i + 1 < args.Length: gdsfactory = args[++i]; break;
        case "--python" when i + 1 < args.Length: gdsfactoryPython = args[++i]; break;
        case "--kit" when i + 1 < args.Length: gdsfactoryKit = Path.GetFullPath(args[++i]); break;
        case "--cell" when i + 1 < args.Length: gdsfactoryCell = args[++i]; break;
        case "--workspace" when i + 1 < args.Length: gdsfactoryWorkspace = Path.GetFullPath(args[++i]); break;
        case "--size" when i + 1 < args.Length:
            var wh = args[++i].Split('x');
            width = double.Parse(wh[0]); height = double.Parse(wh[1]);
            sizeGiven = true;
            break;
        case "--click" or "--drag" or "--key" or "--shot" or "--rclick" or "--menu" or "--type" or "--button" or "--hover"
            or "--unit" or "--rename-stackup" or "--rename-tech" or "--kit-row"
            when i + 1 < args.Length:
            steps.Add((args[i], args[++i]));
            break;
        default: cdd = args[i]; break;
    }
}
if (args.Contains("--messages") && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    if (dark && Application.Current is { } darkApp) darkApp.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    return ShowMessagesPanel(output, sizeGiven ? width : 700, sizeGiven ? height : 260);
}
if (reviewPanel is not null && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return ShowReviewPanel(reviewPanel, output, sizeGiven ? width : 420, sizeGiven ? height : 760);
}
bool vendorMode = palette is not null || schematic is not null || archive is not null || manageLibraries is not null || addVendorLibrary is not null || gdsfactory is not null || csym is not null;
if ((cdd is null && c3dSetups is null && signal.Count == 0 && snp is null && !standalone && tuning is null && optimizer is null && clay is null && cem is null && ctech is null && cmc is null && image is null && !vendorMode) || output is null || view is not ("canvas" or "inspector" or "display"))
{
    Console.Error.WriteLine("usage: UiSnapshot <file.cdd> -o <out.png> [--view canvas|inspector|display] [--plot n] [--size WxH] "
                          + "[--results <dir>] [--click x,y] [--drag x0,y0:x1,y1] [--key <Key>] [--shot <png>] ...\n"
                          + "       UiSnapshot --signal \"<carrier>\" ... -o <out.png> [--tab n] [--fc <expr>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --snp <file.sNp> -o <out.png> [--size WxH] [steps]\n"
                          + "       UiSnapshot --standalone [--iq <file>] -o <out.png> [--tab n] [--size WxH] [steps]\n"
                          + "       UiSnapshot --tuning <file.csch> -o <out.png> [--size WxH] [steps]\n"
                          + "       UiSnapshot --optimizer <file.csch> -o <out.png> [--optimize] [--size WxH] [steps]\n"
                          + "       UiSnapshot --clay <file.clay> -o <out.png> [--hide <layer>] [--lock <layer>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --cem <file.cem> -o <out.png> [--size WxH] [steps]\n"
                          + "       UiSnapshot --ctech <file.ctech> -o <out.png> [--tab n] [--unit <unit>] [--rename-stackup old=new] [--rename-tech <name>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --cmc <file.cmc|new:Passive|new:Active> -o <out.png> [--import <data>] [--step <name>] [--run <key>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --image <picture> -o <out.png> [--tech <file.ctech>] [--digitise] [--make-schematic] [--scale-points x0,y0,x1,y1=<len>] [--strip-geometry] [--size WxH] [steps]");
    return 2;
}

AppBuilder.Configure<SnapshotApp>()
    .UseSkia()
    // UseHeadlessDrawing = false is load-bearing: the default stubs drawing out and every frame is blank.
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

Window host;
PaletteTool? paletteTool = null;
CircuitRF.Ui.Recognition.ImageSourceViewModel? imageVm = null;
TuningTool? tuningTool = null;
ParameterEditorViewModel? parameterEditor = null;
OptimizerTool? optimizerTool = null;
LayoutCanvas? layoutCanvas = null;
LayoutEditorViewModel? layoutVm = null;
TechEditorViewModel? techVm = null;
SymbolPrimitiveInspectorViewModel? csymInspector = null;
CircuitRF.Design.Symbol.SymbolPrimitive? csymPicked = null;
if (dark && Application.Current is { } app) app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
if (vendorMode)
{
    host = VendorView();
    host.Show();
    Pump();
    if (manageLibraries is not null) OpenManageLibraries(manageLibraries);
    if (addVendorLibrary is not null) OpenAddVendorLibrary(addVendorLibrary);
    if (gdsfactory is not null && !OpenGdsfactory(gdsfactory)) return 2;
}
else if (c3dSetups is not null)
{
    // The 3D view's setup list (the Analyses panel's cards) and its Properties panel on one editor view model, results read
    // from the workspace's results/ folder as WorkspaceViewModel.EmResultsRoot gives them.
    string? c3dCws = CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(c3dSetups));
    var c3dVm = new CircuitRF.Ui.ThreeD.C3dEditorViewModel(c3dSetups, CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(c3dSetups),
        CircuitRF.Ui.Viewer3D.Viewer3DBackends.Create, () => c3dCws, a => Dispatcher.UIThread.Post(a))
    {
        ResultsRootProvider = c3dCws is null ? null : () => Path.Combine(Path.GetDirectoryName(c3dCws)!, "results"),
        PostMessage = (text, warning) => Console.WriteLine($"{(warning ? "warning" : "info")}: {text}"),
    };
    c3dVm.RestoreActiveSetup(null);
    c3dVm.Viewer.Resized(400, 300);
    c3dVm.Viewer.Session.EnsureBackend();
    c3dVm.Start();
    var c3dWait = System.Diagnostics.Stopwatch.StartNew();
    while (c3dVm.AdoptedGeneration != c3dVm.Viewer.Source.Requested && c3dWait.Elapsed < TimeSpan.FromMinutes(1)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    c3dVm.RunFinished();
    foreach (var card in c3dVm.SetupItems)
        Console.WriteLine($"setup {card.Name}: run folder {c3dVm.SavedFieldsRunDirectory(card.Name) ?? "none"}, saved fields {(c3dVm.HasSavedFields(card.Name) ? "yes" : "no")}");
    if (c3dDeleteFields is not null)
    {
        string scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output!))!, "trash");
        c3dVm.MoveToTrash = d => { Directory.CreateDirectory(scratch); Directory.Move(d, Path.Combine(scratch, Guid.NewGuid().ToString("N"))); return null; };
        Console.WriteLine(c3dVm.DeleteSavedFields(c3dDeleteFields)?.Sentence ?? c3dVm.StatusMessage);
    }
    if (c3dRow is not null)
    {
        static IEnumerable<CircuitRF.Ui.ThreeD.C3dTreeItem> Walk(CircuitRF.Ui.ThreeD.C3dTreeItem t) => t.Children.SelectMany(Walk).Prepend(t);
        c3dVm.SelectedTreeItem = c3dVm.Tree.SelectMany(g => g.Items).SelectMany(Walk).FirstOrDefault(t => t.Name == c3dRow)
                                 ?? throw new InvalidOperationException($"--c3d-row: no tree row named '{c3dRow}'");
    }
    for (int k = 0; k < 100; k++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    c3dVm.Properties.Reload();
    var c3dPanel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
    var cards = new CircuitRF.Ui.Views.ThreeD.C3dSetupAnalysesView { DataContext = c3dVm };
    var props = new CircuitRF.Ui.Views.ThreeD.C3dPropertiesView { DataContext = c3dVm.Properties };
    Grid.SetColumn(props, 1);
    c3dPanel.Children.Add(cards);
    c3dPanel.Children.Add(props);
    host = new Window { Width = sizeGiven ? width : 900, Height = sizeGiven ? height : 700, Content = c3dPanel };
    host.Show();
    Pump();
    if (c3dVm.FieldPlot(c3dRow ?? "") is { } shownPlot) Console.WriteLine($"plot {shownPlot.Name}: {c3dVm.FieldPlotProblem(shownPlot) ?? "draws"}");
}
else if (clay is not null)
{
    // The layout editor as the workspace opens a path-backed layout: the view model on the file, its technology
    // resolved from the document's TechRef, the layer list's own rows for hide and lock, the real LayoutEditorView.
    var model = LayoutPersistence.LoadFromFile(clay);
    layoutVm = new LayoutEditorViewModel(model, clay, new ConsoleSink());
    var (resolution, _) = TechnologyResolver.ResolveForDocument(model.TechRef, clay, null, new TechnologyCache());
    layoutVm.Technology = resolution.Tech;   // ApplyTechResolution is internal; TechRefFrom falls back to the stored TechRef
    layoutVm.OpenCreatedLayout = p => { Console.WriteLine($"opens {p}"); return Task.CompletedTask; };
    foreach (var (name, hide) in layerToggles)
    {
        var row = layoutVm.LayerRows.FirstOrDefault(r => r.Name == name);
        if (row is null)
        {
            Console.Error.WriteLine($"no layer '{name}'; the layer list has: {string.Join(", ", layoutVm.LayerRows.Select(r => r.Name))}");
            return 2;
        }
        if (hide) row.Visible = false; else row.Selectable = false;
    }
    host = new Window { Width = width, Height = height,
                        Content = new LayoutEditorView { DataContext = new LayoutDocument(Path.GetFileName(clay), layoutVm, clay) } };
    host.Show();
    Pump();
    layoutCanvas = host.GetVisualDescendants().OfType<LayoutCanvas>().First();
    if (place is not null)
    {
        // The layout palette's own drop, at the origin: what a user placing the part gets.
        if (!CircuitRF.Design.Schematic.ComponentTypeRegistry.TryParseCode(place, out var placeKind, out int placePorts))
        { Console.Error.WriteLine($"--place: '{place}' is not a component code"); return 2; }
        if (!layoutVm.CommitPaletteDrop(placeKind, placePorts is > 0 ? placePorts : 2, 0, 0))
        { Console.Error.WriteLine($"--place: the layout refused {place}"); return 2; }
        Pump();
        Console.WriteLine($"placed {place}: {layoutVm.Model.Instances.Count} instance(s), {layoutVm.SelectionStatusText}");
    }
    layoutCanvas.ZoomToFit();
    if (impedance is not null && !RunImpedance(layoutVm, impedance, finding, impedanceLayers)) return 2;
    if (impedancePanel && output is not null) ShowImpedancePanel(layoutVm, Path.ChangeExtension(output, ".impedance.png"));
    if (reviewRun)
    {
        // The editor's own review run, its markers on as the open Review panel turns them on.
        layoutVm.ShowReviewMarkers = true;
        var run = layoutVm.RunReviewAsync(new CircuitRF.Engine.RunControl());
        Await(run);
        if (run.Result is { } review)
            foreach (var f in review.Findings) Console.WriteLine($"{f.CheckId} {f.LayerName}: {f.Sentence}");
    }
}
else if (image is not null)
{
    // The picture dialog as the workspace opens it: Create Layout from Image… (Create Schematic… with --make-schematic),
    // or --digitise for Tools ▸ Digitise Image…, which opens empty and is handed the picture as a drop is. The workspace
    // is the picture's folder; --tech is its one technology (else none).
    string ws = Path.GetDirectoryName(image)!;
    var source = CircuitRF.Design.Imaging.ImageSource.FromFile(image).Source ?? throw new InvalidOperationException($"{image} is not a picture");
    CircuitRF.Ui.Recognition.ImageTechnologyChoice[] techs = imageTech is null ? [] : [new(Path.GetFileNameWithoutExtension(imageTech), imageTech)];
    imageVm = new CircuitRF.Ui.Recognition.ImageSourceViewModel(ws, techs, 0, digitise ? null : source, makeSchematic,
                                                                post: a => Dispatcher.UIThread.Post(a), debounce: TimeSpan.Zero);
    if (digitise)
    {
        imageVm.StartPlotMode(csv: true, manual: true, classify: true);
        imageVm.LoadFile(image);
    }
    host = new CircuitRF.Ui.Recognition.CreateSchematicFromArtworkDialog(imageVm);
    if (sizeGiven) { host.Width = width; host.Height = height; }
    host.Show();
    Pump();
    WaitImage();
    if (imageScalePoints is not null)
    {
        // Two points… as the user takes it: x0,y0,x1,y1=<distance>, in the picture's pixels.
        int eq = imageScalePoints.IndexOf('=');
        double[] xy = [.. imageScalePoints[..eq].Split(',').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture))];
        imageVm.Scale.StartTwoPointsCommand.Execute(null);
        imageVm.Scale.Click(new CircuitRF.Design.Layout.Recognition.Image.PixelPoint(xy[0], xy[1]));
        imageVm.Scale.Click(new CircuitRF.Design.Layout.Recognition.Image.PixelPoint(xy[2], xy[3]));
        imageVm.Scale.DistanceText = imageScalePoints[(eq + 1)..];
        imageVm.Scale.ApplyDistanceCommand.Execute(null);
        Pump();
        WaitImage();
    }
    PrintImage();
    if (stripGeometry) return StripGeometry();
}
else if (ctech is not null)
{
    // The technology editor as the workspace opens a .ctech (OpenOrActivateTech): the file's own content, its libraries
    // resolved by the editor, the real TechEditorView on a TechDocument. --tab picks the section (1 = Stackup).
    techVm = new TechEditorViewModel(ctech, TechPersistence.LoadOwnFromFile(ctech), p => new TechnologyCache().LoaderFor(p));
    techVm.SelectedTabIndex = previewTab;
    host = new Window { Width = sizeGiven ? width : 1100, Height = sizeGiven ? height : 1400,
                        Content = new TechEditorView { DataContext = new TechDocument(Path.GetFileName(ctech), techVm, ctech) } };
    host.Show();
    Pump();
    PrintTech("opened");
}
else if (cem is not null)
{
    var setupVm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(cem, CircuitRF.Design.Layout.Em.EmSetupPersistence.LoadFromFile(cem))
    {
        // The layout and its technology, resolved as `circuitrf em` resolves them (the workspace adds only the live
        // model of an open .clay), so the panel shows the layout's ports and the stackup's layers.
        ResolveLayout = r => CircuitRF.Design.Layout.Em.EmSetupResolver.Resolve(
            cem, r, CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(cem)), new CircuitRF.Design.Layout.TechnologyCache()).Source,
    };
    setupVm.Refresh();
    host = new Window { Width = sizeGiven ? width : 900, Height = sizeGiven ? height : 1200,
                        Content = new EmSetupEditorView { DataContext = new CircuitRF.Ui.Layout.Em.EmSetupDocument(Path.GetFileName(cem), setupVm, cem) } };
    host.Show();
    Pump();
    foreach (var box in host.GetVisualDescendants().OfType<CheckBox>().Where(b => b.Name is { Length: > 0 } && b.IsEffectivelyVisible))
    {
        var at = box.TranslatePoint(default, host);
        Console.WriteLine($"{box.Name}: {(box.IsChecked == true ? "on" : "off")} at {at?.X:0},{at?.Y:0} — {ToolTip.GetTip(box)}");
    }
}
else if (cmc is not null)
{
    CircuitRF.Ui.ModelComposer.ModelComposerViewModel composer;
    if (cmc.StartsWith("new:", StringComparison.Ordinal))
    {
        composer = new CircuitRF.Ui.ModelComposer.ModelComposerViewModel();
        composer.ChooseKindCommand.Execute(cmc[4..]);
    }
    else
    {
        composer = CircuitRF.Ui.ModelComposer.ModelComposerViewModel.OpenFile(Path.GetFullPath(cmc));
        composer.BeginLoad();
        WaitIdle(composer);
    }
    if (cmcImport is not null)
    {
        // The importer as Import a data set opens it: the document's path, or null for an unsaved document.
        var importer = new CircuitRF.Ui.ModelComposer.ModelImportViewModel(composer.DocumentPath, composer.Document);
        importer.SetSources([cmcImport]);
        Console.WriteLine($"importer: .cmc {importer.CmcPath} (provisional {importer.IsProvisional}); sources {string.Join(", ", importer.Sources)}");
        Console.WriteLine($"importer: pattern '{importer.Pattern}', refused {importer.Refused}: {importer.Line}");
        host = new CircuitRF.Ui.Views.ModelComposer.ModelImportDialog(importer);
        if (sizeGiven) { host.Width = width; host.Height = height; }
    }
    else
    {
        host = new CircuitRF.Ui.Views.ModelComposer.ModelComposerWindow { DataContext = composer };
        if (sizeGiven) { host.Width = width; host.Height = height; }
    }
    host.Show();
    Pump();
    if (cmcImport is null)
    {
        if (cmcStep is not null)
            composer.SelectedStep = composer.Steps.FirstOrDefault(st => st.Name == cmcStep)
                ?? throw new InvalidOperationException($"--step '{cmcStep}': the steps are {string.Join(", ", composer.Steps.Select(st => st.Name))}");
        Pump();
        if (cmcRun is not null)
        {
            Console.WriteLine($"--run {cmcRun}: {(Await2(composer.RunKey(cmcRun)) ? "ok" : "false")}");
            WaitIdle(composer);
        }
        // A marker per --marker on the rectangular plot's first data trace, its box placed as a new marker's is.
        var markedTrace = composer.RectPlot.Traces.FirstOrDefault(t => !t.IsAnnotation && t.Points.Count > 8);
        for (int k = 0; k < cmcMarkers && markedTrace is not null; k++)
        {
            var marker = new CircuitRF.Render.DataDisplay.Marker(markedTrace, 0, false, false, k + 1)
                { PositionStatic = markedTrace.Points[markedTrace.Points.Count * (k + 1) / (cmcMarkers + 1)] };
            markedTrace.Markers.Add(marker);
            composer.RectContainer.OnPlotChanged(null, EventArgs.Empty);     // the plot's change builds the info box first
            composer.RectContainer.OnMarkerAdded(marker, markedTrace);
            composer.PlaceNewInfoBox(composer.RectContainer, marker);
            Pump();
        }
        if (cmcMarkers > 0)
            Console.WriteLine($"markers: trace {markedTrace?.CubeName ?? "none"}; {string.Join(", ", composer.RectPlot.Traces.SelectMany(t => t.Markers).Select(m => $"box at {m.InfoBoxPos}"))}"
                            + $"; {composer.PlotHost.MarkerInfoBoxes.Count} info box view model(s)");
        Console.WriteLine($"status: {composer.StatusText}");
        if (composer.ProposalNote.Length > 0) Console.WriteLine($"proposal note: {composer.ProposalNote}");
        Console.WriteLine($"proposals: {string.Join(" | ", composer.Proposals.Select(r => r.Title))}");
    }
}
else if (tuning is not null)
{
    // The Tuning window as the shell hosts it: a TuningTool whose panel follows one schematic.
    var (model, _, _) = SchematicPersistence.LoadFromFile(tuning);
    tuningTool = new TuningTool();
    tuningTool.Panel.SetActiveSchematic(new SchematicViewModel(model, messageSink: null), Path.GetFileName(tuning));
    host = new Window { Width = sizeGiven ? width : 420, Height = sizeGiven ? height : 500,
                        Content = new TuningToolView { DataContext = tuningTool } };
    host.Show();
}
else if (optimizer is not null)
{
    // The Optimizer window as the shell hosts it, its circuit prepared as WorkspaceViewModel.PrepareTunedCircuit
    // prepares it: the extraction, its netlist text, and the extraction's vendor parts beside it.
    var (model, _, _) = SchematicPersistence.LoadFromFile(optimizer);
    var vm = new SchematicViewModel(model, messageSink: null);
    optimizerTool = new OptimizerTool();
    var panel = optimizerTool.Panel;
    panel.PostToUi = a => Dispatcher.UIThread.Post(a);
    panel.PrepareCircuit = t =>
    {
        string name = Path.GetFileNameWithoutExtension(optimizer);
        var extracted = NetExtractor.Extract(t.EditModel, name, DiskCellResolver.Instance);
        string text = CircuitRF.Core.Netlist.CnlWriter.Write(extracted.TestBench, extracted.Library, "optimizer snapshot");
        string? ws = CircuitRF.Design.Workspace.WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(optimizer));
        return CircuitRF.Design.Circuit.PreparedCircuit.FromText(text, ws ?? Path.GetDirectoryName(optimizer), ws, name, extracted);
    };
    panel.SetActiveSchematic(vm, Path.GetFileName(optimizer));
    host = new Window { Width = sizeGiven ? width : 520, Height = sizeGiven ? height : 640,
                        Content = new CircuitRF.Ui.Views.Optimization.OptimizerToolView { DataContext = optimizerTool } };
    host.Show();
    Pump();
    if (optimize)
    {
        panel.RunCommand.Execute(null);
        var until = DateTime.UtcNow.AddSeconds(300);
        while (panel.IsRunActive && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Pump();
    }
    Console.WriteLine("algorithms: " + string.Join(", ", panel.Algorithms.Select(a => a.IsAvailable ? a.Label : $"[{a.Label}: greyed]")));
    if (panel.Algorithms.FirstOrDefault(a => !a.IsAvailable) is { } greyed) Console.WriteLine($"greyed because: {greyed.DisabledReason}");
    foreach (var r in panel.Variables)
        Console.WriteLine($"variable {r.Key}: {r.DiscreteText} | {r.MinText} .. {r.MaxText} | best {r.BestText} {r.UnitText}");
    Console.WriteLine($"status: {panel.StatusText} | {panel.RunStatusText}");
}
else if (standalone)
{
    // Tools ▸ Signal Designer: the view model's own SetStandalone, and Load custom's own path for a file.
    var designer = new SignalDesignerViewModel();
    designer.SetStandalone(preview: false);
    if (iq is not null) designer.LoadCustomFile(iq);
    host = new SignalDesignerWindow { DataContext = designer };
    if (sizeGiven) { host.Width = width; host.Height = height; }
    host.Show();
    Pump();
    Await(designer.PreviewAsync());
    designer.PreviewTab = previewTab;
    Console.WriteLine(designer.Status);
}
else if (snp is not null)
{
    // The S-Parameter Viewer through ShowForFile itself, so its pickers and its questions are wired as the
    // application wires them; the file is read off the UI thread, and this waits for it.
    var shown = CircuitRF.Ui.Views.SParameters.SParameterViewerWindow.ShowForFile(snp, owner: null);
    host = shown;
    if (sizeGiven) { host.Width = width; host.Height = height; }
    var viewer = (CircuitRF.Ui.SParameters.SParameterViewerViewModel)shown.DataContext!;
    var until = DateTime.UtcNow.AddSeconds(120);
    while (viewer.IsLoading && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    Console.WriteLine(viewer.Status);
}
else if (signal.Count > 0)
{
    // The Designer as the inspector's Design signal... opens it: a MOD_Source in a schematic, SetTarget, the window.
    var model = new SchematicEditModel();
    var src = new EditableComponent { InstanceName = "SRC1", Symbol = SymbolKind.ModSource };
    src.Parameters.Add(new EditableParameter { Name = "Fc", Expression = fc, Unit = "GHz", Dimension = UnitDimension.Frequency });
    src.Parameters.Add(new EditableParameter { Name = "Pavg", Expression = "10", Unit = "dBm", Dimension = UnitDimension.Power });
    for (int k = 0; k < signal.Count; k++)
        src.Parameters.Add(new EditableParameter { Name = $"Carrier[{k + 1}]", Expression = "\"" + signal[k] + "\"" });
    model.Components.Add(src);
    var designer = new SignalDesignerViewModel();
    designer.SetTarget(new SchematicViewModel(model, messageSink: null), src, preview: false);
    host = new SignalDesignerWindow { DataContext = designer };
    if (sizeGiven) { host.Width = width; host.Height = height; }
    host.Show();
    Pump();
    Await(designer.PreviewAsync());
    designer.PreviewTab = previewTab;
    Console.WriteLine(designer.Status);
}
else
{
var document = new DataDisplayDocumentViewModel();
var window   = document.Window;
// A relative source reference resolves against a workspace's results/ folder, which in the application the
// workspace supplies. Here it is --results, or the .cdd's own folder — where `circuitrf plot --write-cdd` puts
// a document beside its result.
string resultsRoot = Path.GetFullPath(results ?? Path.GetDirectoryName(Path.GetFullPath(cdd!))!);
window.DataSourceLibrary.ResultsRootProvider = () => resultsRoot;
window.DataSourceLibrary.RefreshAvailableDataSources();
// Never block on a Task here: its continuations are posted to this same dispatcher, so GetResult() deadlocks.
Await(window.LoadAllAsync(Path.GetFullPath(cdd!)));

var tab = window.ActiveTab ?? throw new InvalidOperationException("the display opened no tab");
Control content;
if (view == "canvas")
    content = new PlotCanvasView { DataContext = tab };
else if (view == "display")
    content = new DataDisplayView { DataContext = new DataDisplayDocument(Path.GetFileName(cdd!), document, cdd) };
else
{
    var plots = tab.DataDisplay.Plots;
    if (plotIndex < 1 || plotIndex > plots.Count)
    {
        Console.Error.WriteLine($"--plot {plotIndex}: the active tab has {plots.Count} plot(s)");
        return 2;
    }
    content = new PlotInspectorView { DataContext = plots[plotIndex - 1].Inspector };
}

host = new Window { Width = width, Height = height, Content = content };
host.Show();
}
Pump();

foreach (var (kind, arg) in steps)
{
    switch (kind)
    {
        case "--click":
        {
            var p = PointOf(arg);
            host.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
            host.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
            break;
        }
        case "--drag":
        {
            // Pressed, moved in steps — a gesture crosses a click threshold before it becomes a drag — and released.
            var ends = arg.Split(':');
            Point a = PointOf(ends[0]), b = PointOf(ends[1]);
            host.MouseDown(a, MouseButton.Left, RawInputModifiers.None);
            for (int k = 1; k <= 10; k++)
            {
                host.MouseMove(a + (b - a) * (k / 10.0), RawInputModifiers.LeftMouseButton);
                Pump();
            }
            host.MouseUp(b, MouseButton.Left, RawInputModifiers.None);
            break;
        }
        case "--key":
        {
            // "Back", "Delete", "Escape", or with modifiers: "Meta+Z", "Ctrl+Shift+A".
            var parts = arg.Split('+');
            var mods  = RawInputModifiers.None;
            foreach (var m in parts[..^1])
                mods |= Enum.Parse<RawInputModifiers>(m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? "Control" : m, ignoreCase: true);
            var key = Enum.Parse<Key>(parts[^1], ignoreCase: true);
            host.KeyPress(key, mods, PhysicalKey.None, null);
            host.KeyRelease(key, mods, PhysicalKey.None, null);
            break;
        }
        case "--shot":
            Capture(arg);
            break;
        case "--hover":
            host.MouseMove(PointOf(arg), RawInputModifiers.None);
            break;
        case "--rclick":
        {
            var p = PointOf(arg);
            host.MouseDown(p, MouseButton.Right, RawInputModifiers.None);
            host.MouseUp(p, MouseButton.Right, RawInputModifiers.None);
            break;
        }
        case "--menu":
        {
            // The item's own Click handler, raised on the real MenuItem the menu was built with; then the menu closes,
            // as a click on an item closes it.
            var menu = OpenContextMenu() ?? throw new InvalidOperationException("--menu: no context menu is open");
            var all = MenuItems(menu.Items.OfType<MenuItem>()).ToList();
            var item = all.FirstOrDefault(m => m.Header is string h && h.StartsWith(arg, StringComparison.Ordinal));
            if (item is null)
                throw new InvalidOperationException($"--menu '{arg}': the menu has {string.Join(" | ", all.Select(m => m.Header))}");
            if (!item.IsEnabled) Console.WriteLine($"--menu '{arg}': the item is disabled ({ToolTip.GetTip(item)})");
            else item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            break;
        }
        case "--type":
        {
            var dialog = TopDialog() ?? (imageVm is not null || vendorMode || cem is not null || snp is not null ? host : throw new InvalidOperationException("--type: no dialog is open"));
            // Name=text types into the text box of that x:Name (an inline box: SizeInput, DistanceInput); else the dialog's
            // first text box.
            int eq = arg.IndexOf('=');
            var named = eq > 0 ? dialog.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == arg[..eq]) : null;
            string text = named is not null ? arg[(eq + 1)..] : arg;
            var box = named ?? dialog.GetVisualDescendants().OfType<TextBox>().First();
            box.Focus();
            box.SelectAll();
            if (named is not null) named.Text = text; else dialog.KeyTextInput(text);
            break;
        }
        case "--unit":
        {
            // The header's "Unit for new layouts" combo, given the unit as a pick from its own list gives it.
            var combo = host.GetVisualDescendants().OfType<ComboBox>().First(c => c.SelectedItem is LayoutUnit);
            combo.SelectedItem = combo.Items.OfType<LayoutUnit>().First(u => LayoutUnits.Suffix(u) == arg || u.ToString().Equals(arg, StringComparison.OrdinalIgnoreCase));
            break;
        }
        case "--rename-stackup":
        {
            // old=new: the stackup card's own name box, typed into and committed with Enter through its KeyDown handler.
            int eq = arg.IndexOf('=');
            var box = host.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.Tag as string == "Name" && t.DataContext is StackupLayerRowViewModel && t.Text == arg[..eq]);
            box.Focus();
            box.Text = arg[(eq + 1)..];
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
            break;
        }
        case "--rename-tech":
        {
            // A real double-click on the header's name, then the text typed into whatever took focus — the box, if it
            // opened and focused itself. Enter is left to a following --key Enter, so the open box can be --shot first.
            var label = host.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == techVm!.Working.Name && t.FontWeight == Avalonia.Media.FontWeight.SemiBold);
            var c = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), host)!.Value;
            for (int k = 0; k < 2; k++)
            {
                host.MouseDown(c, MouseButton.Left, RawInputModifiers.None);
                host.MouseUp(c, MouseButton.Left, RawInputModifiers.None);
            }
            Pump();
            Console.WriteLine($"focus after double-click: {(host.FocusManager?.GetFocusedElement() as Control)?.Name ?? "none"}");
            host.KeyTextInput(arg);
            break;
        }
        case "--kit-row":
        {
            // allow:<kit> | deny:<kit> | clear:<kit> — a real click on that kit's Allowed box or Clear
            // button in Settings' Generated Artwork list (the "permissions" gdsfactory step).
            var dialog = TopDialog() ?? host;
            int colon = arg.IndexOf(':');
            string verb = arg[..colon], kitName = arg[(colon + 1)..];
            var controls = dialog.GetVisualDescendants().OfType<Control>()
                .Where(c => c.DataContext is CircuitRF.Ui.Views.Dialogs.PCellTrustRow r && r.Name == kitName && c.IsEffectivelyVisible);
            Control target = verb == "clear"
                ? controls.OfType<Button>().First(b => b is not Avalonia.Controls.Primitives.ToggleButton)
                : controls.OfType<CheckBox>().First(cb => (cb.IsChecked == true) != (verb == "allow"));
            var c = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(c, MouseButton.Left, RawInputModifiers.None);
            dialog.MouseUp(c, MouseButton.Left, RawInputModifiers.None);
            Pump();
            Console.WriteLine($"{arg}: recorded {string.Join(", ", CircuitRF.Ui.Layout.PCells.PCellTrustPreferences.Load().Select(kv => $"{Path.GetFileName(kv.Key)}={(kv.Value ? "allowed" : "refused")}"))}");
            break;
        }
        case "--button":
        {
            // A real press and release on the button's centre, in the dialog's own window.
            var dialog = TopDialog() ?? (imageVm is not null || vendorMode || cem is not null || snp is not null ? host : throw new InvalidOperationException("--button: no dialog is open"));
            var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            // A toolbar button's content is an icon beside a TextBlock, so its label is matched too.
            var button = buttons.FirstOrDefault(b => b.Content is string c && c == arg)
                ?? buttons.FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == arg))
                ?? throw new InvalidOperationException($"--button '{arg}': the dialog has {string.Join(" | ", buttons.Select(b => b.Content))}");
            var c = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(c, MouseButton.Left, RawInputModifiers.None);
            dialog.MouseUp(c, MouseButton.Left, RawInputModifiers.None);
            break;
        }
    }
    Pump();
    if (imageVm is not null)
    {
        WaitImage();
        Console.Write($"{kind} {arg}: ");
        PrintImage();
    }
    if (parameterEditor is { IsVendorPart: true } pev)
        Console.WriteLine($"{kind} {arg}: vendor part tuned {pev.IsVendorPartTuned}, optimized {pev.IsVendorPartOptimized}");
    if (techVm is not null) PrintTech($"{kind} {arg}");
    if (csymInspector is not null)
        Console.WriteLine($"{kind} {arg}: fill box '{csymInspector.FieldFillText}', slider {csymInspector.FieldFillSlider:0.#}, "
                        + $"stored {(csymPicked is null ? "-" : (CircuitRF.Design.Symbol.SymbolFill.Of(csymPicked) * 100).ToString("0.#"))} %"
                        + (csymInspector.FillInvalid ? $", refused: {csymInspector.FillError}" : ""));
    if (paletteTool is not null)
        Console.WriteLine($"{kind} {arg}: focus {host.FocusManager?.GetFocusedElement()?.GetType().Name ?? "none"}; armed "
                        + (paletteTool.VendorRows.FirstOrDefault(r => r.IsArmed)?.Part ?? "none")
                        + "; Type menu " + TypeMenuState() + "; selected " + (paletteTool.SelectedCategory?.Breadcrumb ?? "none"));
    if (tuningTool is not null)
    {
        var focused = host.FocusManager?.GetFocusedElement();
        string tag = focused is Control { Tag: CircuitRF.Ui.Tuning.TuningRowViewModel r } ? $" (row {r.Key})" : "";
        Console.WriteLine($"{kind} {arg}: focus {focused?.GetType().Name ?? "none"}{tag}; "
                        + string.Join(", ", tuningTool.Panel.Rows.Select(r => $"{r.Key}={r.ValueBoxText}")));
    }
}

Capture(output!);
return 0;

// brief-dfr30-3 gate 1 — the layout tool strip's geometry in every state: no tool armed, each tool armed in turn by a real
// click on it, a hand via selected, and Other… chosen for its drill. Every control of the strip, the legend and the options
// row must keep one place throughout; exit 1 when any moved.
int StripGeometry()
{
    var v = imageVm!;
    if (!v.DrawToolsShown) { Console.WriteLine("strip: this picture has no drawing tools"); return 1; }
    var strip = host.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "ToolStrip");
    var options = host.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "ToolOptions");
    string Label(Control c) => c switch
    {
        Border b when b.Classes.Contains("pill") => "legend",
        ContentControl { Content: string t } => t,
        ContentControl { Content: StackPanel p } => string.Concat(p.Children.OfType<TextBlock>().Select(t => t.Text)),
        _ => ToolTip.GetTip(c) as string ?? c.GetType().Name,
    };
    List<(string Name, Rect Box)> Read()
    {
        Pump();
        var controls = strip.GetVisualDescendants().OfType<Control>()
                            .Where(c => c.IsEffectivelyVisible && (c is Button || c is Border { Classes: var k } && k.Contains("pill")))
                            .Append(options).ToList();
        return [.. controls.Select(c => (Label(c), new Rect(c.TranslatePoint(default, host)!.Value, c.Bounds.Size)))];
    }
    void Click(string tool)
    {
        var b = strip.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => !t.Classes.Contains("legend") && Label(t) == tool);
        var c = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), host)!.Value;
        host.MouseDown(c, MouseButton.Left, RawInputModifiers.None);
        host.MouseUp(c, MouseButton.Left, RawInputModifiers.None);
        Pump();
        WaitImage();
    }
    var states = new List<(string State, List<(string Name, Rect Box)> Boxes)> { ("nothing armed", Read()) };
    foreach (string tool in new[] { "Crop", "Paint Fill", "Via", "Copper", "Outline" })
    {
        Click(tool);
        states.Add(($"{tool} armed ({v.DrawTool})", Read()));
        Click(tool);
    }
    var raster = v.Source!.Raster;
    v.DrawTool = CircuitRF.Ui.Recognition.PictureDrawTool.Via;
    v.PlaceVia(new CircuitRF.Design.Layout.Recognition.Image.PixelPoint(raster.Width / 2.0, raster.Height / 2.0));
    WaitImage();
    v.DrawTool = CircuitRF.Ui.Recognition.PictureDrawTool.None;
    v.SelectedCorrection = v.Corrections.ToList().FindLastIndex(c => c.Kind == CircuitRF.Design.Layout.Recognition.Image.ImageCorrectionKind.Via);
    states.Add(("hand via selected", Read()));
    v.ViaDrill = v.ViaDrillChoices[^1];
    states.Add(($"{v.ViaDrill?.Label} chosen", Read()));
    Click("Via");
    states.Add(("Via armed, Other… chosen", Read()));

    bool same = true;
    var first = states[0].Boxes;
    foreach (var (state, boxes) in states)
    {
        bool ok = boxes.Count == first.Count && boxes.Zip(first).All(p => p.First.Name == p.Second.Name && p.First.Box == p.Second.Box);
        same &= ok;
        Console.WriteLine($"{(ok ? "same " : "MOVED")} {state}: {string.Join(" | ", boxes.Select(b => $"{b.Name} {b.Box.X:0.#},{b.Box.Y:0.#} {b.Box.Width:0.#}×{b.Box.Height:0.#}"))}");
    }
    if (output is not null) Capture(output);
    Console.WriteLine(same ? "strip geometry: identical in every state" : "strip geometry: MOVED");
    return same ? 0 : 1;
}

// A reading is scheduled through the dispatcher, then runs off it and posts back: pump until it has settled.
void WaitImage()
{
    var until = DateTime.UtcNow.AddSeconds(120);
    int quiet = 0;
    while (quiet < 20 && DateTime.UtcNow < until)
    {
        Pump();
        Thread.Sleep(10);
        quiet = imageVm!.IsRecognising || !imageVm.Recognition.IsCompleted ? 0 : quiet + 1;
    }
    Pump();
}

void PrintImage()
{
    var v = imageVm!;
    Console.WriteLine($"{v.Title} | kind {v.EffectiveKind} | make {(v.MakeSchematic ? "schematic" : v.MakeLayout ? "layout" : v.MakeCsv ? "csv" : "touchstone")}"
                    + $" (schematic offered {v.MakeSchematicEnabled}) | scale {v.Scale.ValueText} | tool {v.Scale.Tool}"
                    + $"{(v.Scale.SizeError.Length > 0 ? $" | size error '{v.Scale.SizeError}'" : "")} | prompt '{v.CanvasPromptText}'{(v.CanvasPromptOffersScale ? " + scale buttons" : "")} | status '{v.Status}'");
}

void Pump()
{
    for (int i = 0; i < 8; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        host?.UpdateLayout();
    }
}

// The stackup cards as drawn — each thickness box with the unit label beside it — then every via's span and the
// editor's own problems, which are what the banner shows.
void PrintTech(string when)
{
    Console.WriteLine($"{when}: name '{techVm!.Working.Name}'{(techVm.IsEditingName ? $" (editing: '{techVm.StagedTechName}')" : "")}, "
                    + $"{(techVm.IsDirty ? "unsaved" : "saved")}, unit for new layouts {techVm.DefaultDisplayUnit}");
    foreach (var box in host.GetVisualDescendants().OfType<TextBox>().Where(t => t.Tag as string == "Thickness" && t.IsEffectivelyVisible))
    {
        var label = (box.Parent as Panel)?.Children.SkipWhile(c => c != box).Skip(1).OfType<TextBlock>().FirstOrDefault()?.Text;
        Console.WriteLine($"  {((StackupLayerRowViewModel)box.DataContext!).StagedName}: thickness {box.Text} {label}");
    }
    foreach (var via in techVm.Working.Stackup.Layers.Where(l => l.Kind == StackupKind.Via))
        Console.WriteLine($"  via {via.Name}: {via.SpanFromLayer} -> {via.SpanToLayer}");
    foreach (var problem in techVm.ValidationProblems) Console.WriteLine($"  problem: {problem.Message}");
    if (techVm.ValidationProblems.Count == 0) Console.WriteLine("  problems: none");
}

void Capture(string path)
{
    Pump();
    host.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    if (TopDialog() is { } dialog)
    {
        string d = Path.ChangeExtension(path, ".dialog.png");
        dialog.CaptureRenderedFrame()!.Save(d);
        Console.WriteLine($"wrote {d} ({dialog.Title})");
    }
    // A popup that opened as a top level of its own (rather than in the window's overlay layer).
    var popups = new List<TopLevel>();
    foreach (var w in new Window[] { host }.Concat(host.OwnedWindows))
        foreach (var root in w.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>()
                              .Where(p => p.IsOpen && p.Child is not null).Select(p => TopLevel.GetTopLevel(p.Child)).OfType<TopLevel>())
            if (!popups.Contains(root) && !ReferenceEquals(root, w)) popups.Add(root);
    foreach (var (root, k) in popups.Select((r, k) => (r, k)))
    {
        string pp = Path.ChangeExtension(path, k == 0 ? ".popup.png" : $".popup{k + 1}.png");
        root.CaptureRenderedFrame()!.Save(pp);
        Console.WriteLine($"wrote {pp}");
    }
}

// The palette's Type menu: open or not, and the size its presenter laid out at — a menu that opens with no
// items lays out a couple of pixels wide, which is how an empty menu shows up here.
string TypeMenuState()
{
    var button = host.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "TypeButton");
    if (button?.Flyout is not MenuFlyout flyout) return "absent";
    var popup = typeof(Avalonia.Controls.Primitives.PopupFlyoutBase).GetProperty("Popup",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(flyout) as Avalonia.Controls.Primitives.Popup;
    var size = popup?.Child?.Bounds.Size;
    return $"{(flyout.IsOpen ? "open" : "closed")}, {flyout.Items.Count} items, presenter {size?.Width:0}x{size?.Height:0}";
}

// The vendor-library views, built as the workspace builds them (see the usage note at the top).
Window VendorView()
{
    string? ws = palette ?? archive ?? manageLibraries
              ?? (schematic is not null ? CircuitRF.Design.Workspace.WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(schematic)) : null);
    if (ws is not null) IndexVendorLibraries(ws);

    if (palette is not null)
    {
        var tool = paletteTool = new PaletteTool();
        tool.SetPlacementService(new CircuitRF.Ui.Schematic.PlacementService());   // so a row arms, as in the shell
        tool.SetVendorLibraries(VendorLibrariesOf(palette));
        if (paletteCategory is not null)
            tool.SelectedCategory = tool.Categories.FirstOrDefault(c => c.DisplayName == paletteCategory)
                ?? throw new InvalidOperationException($"--category '{paletteCategory}': {string.Join(", ", tool.Categories.Select(c => c.DisplayName))}");
        if (paletteSearch is not null) tool.SearchQuery = paletteSearch;
        return new Window { Width = sizeGiven ? width : 300, Height = sizeGiven ? height : 640,
                            Content = new CircuitRF.Ui.Views.Palette.PaletteToolView { DataContext = tool } };
    }
    if (schematic is not null)
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(schematic);
        var vm = new SchematicViewModel(model, messageSink: new ConsoleSink());
        // The surfaces the shell gives every session (WorkspaceViewModel): the Tuning, Yield and Optimizer panels
        // following this schematic, so the Inspector shows the Tune, Tolerance and Optimize toggles it shows there.
        var tuningPanel = new CircuitRF.Ui.Tuning.TuningPanelViewModel();
        var yieldPanel  = new CircuitRF.Ui.Yield.YieldPanelViewModel();
        var optPanel    = new CircuitRF.Ui.Optimization.OptimizerPanelViewModel();
        tuningPanel.SetActiveSchematic(vm, Path.GetFileName(schematic));
        yieldPanel.SetActiveSchematic(vm, Path.GetFileName(schematic));
        optPanel.SetActiveSchematic(vm, Path.GetFileName(schematic));
        vm.Tuning    = new CircuitRF.Ui.Tuning.TuningSurfaceRelay(() => tuningPanel);
        vm.Tolerance = new CircuitRF.Ui.Yield.ToleranceSurfaceRelay(() => yieldPanel);
        vm.Optimize  = new CircuitRF.Ui.Optimization.OptimizeSurfaceRelay(() => optPanel);
        var editor = new ParameterEditorViewModel();
        parameterEditor = editor;
        var target = select is null ? null : model.Components.FirstOrDefault(c => c.InstanceName == select)
            ?? throw new InvalidOperationException($"--select '{select}': {string.Join(", ", model.Components.Select(c => c.InstanceName))}");
        if (target is not null) editor.SetTargetDirect(vm, target, showClose: parameterDialog);
        // The dialog a double-click on the part opens (SchematicView.OpenParameterEditorFor), at its own width.
        if (parameterDialog)
            return new CircuitRF.Ui.Views.Dialogs.ParameterEditorDialog { DataContext = editor };
        var doc = new CircuitRF.Ui.Schematic.SchematicDocument(Path.GetFileNameWithoutExtension(schematic), vm, schematic);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,360") };
        grid.Children.Add(new CircuitRF.Ui.Views.Content.SchematicView { DataContext = doc });
        var pe = new CircuitRF.Ui.Views.ParameterEditor.ParameterEditorView { DataContext = editor };
        Grid.SetColumn(pe, 1);
        grid.Children.Add(pe);
        var window = new Window { Width = sizeGiven ? width : 1200, Height = sizeGiven ? height : 720, Content = grid };
        window.Opened += (_, _) => Dispatcher.UIThread.Post(doc.RequestZoomToFit);
        return window;
    }
    if (csym is not null)
    {
        // The symbol editor beside its Properties inspector, as the shell docks them, with one shape selected by a
        // click through the editor's own pointer handling: --pick x,y in symbol coordinates, else the centre of the
        // first tinted shape (whose inside is part of it), else nothing selected.
        var symbol = CircuitRF.Design.Symbol.SymbolPersistence.LoadFromFile(csym);
        var editable = CircuitRF.Ui.Schematic.EditableSymbol.FromSymbol(symbol);
        var vm = new SymbolEditorViewModel(editable);
        (double X, double Y)? pick = null;
        if (csymPick is { } p)
        {
            var xy = p.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            pick = (xy[0], xy[1]);
        }
        else if (symbol.Primitives.FirstOrDefault(p => CircuitRF.Design.Symbol.SymbolFill.Of(p) is > 0 and < 1) is { } tinted)
        {
            var (x0, y0, x1, y1) = CircuitRF.Design.Symbol.SymbolGeometry.BboxOf(tinted);
            pick = ((x0 + x1) / 2, (y0 + y1) / 2);
        }
        if (pick is { } at)
        {
            vm.OnPointerPressed(at.X, at.Y, KeyModifiers.None);
            vm.OnPointerReleased(at.X, at.Y);
        }
        var inspector = csymInspector = new SymbolPrimitiveInspectorViewModel();
        inspector.SetContext(vm);
        if (vm.Overlay.SelectedIndices.Count == 1) csymPicked = editable.Primitives[vm.Overlay.SelectedIndices.First()];
        Console.WriteLine($"selected: {(pick is null ? "nothing" : $"at {pick}")}; fill row {inspector.ShowFill}: {inspector.FieldFillText}");
        var doc = new CircuitRF.Ui.Schematic.SymbolEditorDocument(Path.GetFileNameWithoutExtension(csym), vm);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300") };
        grid.Children.Add(new CircuitRF.Ui.Views.Content.SymbolEditorView { DataContext = doc });
        var props = new CircuitRF.Ui.Views.Properties.SymbolPrimitiveInspectorView { DataContext = inspector };
        Grid.SetColumn(props, 1);
        grid.Children.Add(props);
        var window = new Window { Width = sizeGiven ? width : 1100, Height = sizeGiven ? height : 640, Content = grid };
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            window.GetVisualDescendants().OfType<CircuitRF.Ui.Controls.SymbolEditorCanvas>().FirstOrDefault()?.ZoomToFit());
        return window;
    }
    if (archive is not null)
    {
        var dialog = new CircuitRF.Ui.Views.Dialogs.ArchiveWorkspaceDialog(CircuitRF.Ui.Archive.WorkspaceArchiveScanner.Scan(archive), new ConsoleSink());
        if (sizeGiven) { dialog.Width = width; dialog.Height = height; }
        return dialog;
    }
    return new Window { Width = sizeGiven ? width : 900, Height = sizeGiven ? height : 600 };
}

void OpenManageLibraries(string ws)
{
    var cws = CircuitRF.Design.Workspace.WorkspacePersistence.LoadFromFile(Path.Combine(ws, ".cws"));
    var ctx = new CircuitRF.Ui.Views.Dialogs.ManagePdksDialog.Context(
        ws, cws.PdkRefs ?? [], [], () => { }, _ => { }, () => { }, (l, t) => Console.WriteLine($"[{l}] {t}"),
        VendorRefs: cws.VendorLibraries ?? []);
    _ = CircuitRF.Ui.Views.Dialogs.ManagePdksDialog.ShowAsync(host, ctx);
    Pump();
}

// The gdsfactory import's views, each opened as the application opens it (brief-gdf-2 §6).
bool OpenGdsfactory(string step)
{
    var python = gdsfactoryPython ?? "python3";
    var tree = gdsfactoryKit is null ? null : CircuitRF.Design.Layout.PCells.Wire.GdsfactoryKitDiscovery.FindSourceTree(gdsfactoryKit);
    switch (step)
    {
        case "prompt":
            _ = CircuitRF.Ui.Views.Dialogs.PdkImportPromptDialog.PickAsync(host);
            break;
        case "pick":
            _ = CircuitRF.Ui.Views.Dialogs.GdsfactoryKitDialog.PickKitAsync(host, [python]);
            break;
        case "interpreter":
            _ = CircuitRF.Ui.Views.Dialogs.GdsfactoryKitDialog.PickInterpreterAsync(host, tree?.Module ?? "synthkit", [python]);
            break;
        case "trust":
        {
            string dir = Path.Combine(Path.GetTempPath(), "ws", $"{tree?.Module ?? "synthkit"}-pcells");
            var kit = new CircuitRF.Design.Layout.PCells.Wire.PCellKit(dir, Path.Combine(dir, "kit_entry.py"))
            {
                Module = tree?.Module ?? "synthkit", ModuleVersion = "0.4.1",
                Location = tree?.InstalledDirectory ?? "/opt/envs/rf/lib/python3.12/site-packages/synthkit",
                Interpreter = python,
            };
            kit = kit with { TrustKey = kit.Location! };
            _ = CircuitRF.Ui.Views.Dialogs.PCellTrustDialog.ShowAsync(host, [kit]);
            break;
        }
        case "report" or "report-open":
        {
            if (tree is null) { Console.Error.WriteLine("--gdsfactory report needs --kit <source tree>"); return false; }
            var report = CircuitRF.Design.Layout.PCells.Wire.GdsfactoryKitImport.ReadReport(python, tree, out var problem);
            if (report is null) { Console.Error.WriteLine($"no report: {problem}"); return false; }
            Console.WriteLine(report.Tally());
            // Two of the kit's parts declaring the two models the synthetic kit's cells declare: one the
            // cell covers, one asking for a parameter the cell does not take — so both pairing rows show.
            IReadOnlyList<CircuitRF.Design.Schematic.PaletteItem> parts =
            [
                new(SymbolKind.Generic, 2, "synth_res_part", ComponentCategory.Other, [], false,
                    Pdk: new CircuitRF.Design.Schematic.PdkPartRef("upstream", "synth_res_part", ModelName: "synth_res", ParameterNames: ["size", "count"])),
                // Two parts on one model, each asking for something the cell does not take: refused.
                new(SymbolKind.Generic, 2, "synth_cap_part", ComponentCategory.Other, [], false,
                    Pdk: new CircuitRF.Design.Schematic.PdkPartRef("upstream", "synth_cap_part", ModelName: "synth_cap", ParameterNames: ["w", "l"])),
                new(SymbolKind.Generic, 2, "synth_cap_rf_part", ComponentCategory.Other, [], false,
                    Pdk: new CircuitRF.Design.Schematic.PdkPartRef("upstream", "synth_cap_rf_part", ModelName: "synth_cap", ParameterNames: ["length", "w"])),
            ];
            var kits = report.Offered.ToDictionary(c => c, _ => "synthkit-pcells");
            var parameters = report.Parameters.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
            var composed = CircuitRF.Ui.Schematic.KitPaletteMerge.Compose(parts, kits, report.Models, parameters);
            var pairings = CircuitRF.Ui.Schematic.KitPaletteMerge.ExplainModelPairings(composed, report.Models, parameters);
            // The technology offer, read as the application reads it (brief-gdf-3 R-gdf3-3a).
            var stack = CircuitRF.Design.Layout.PCells.Wire.GdsfactoryKitImport.ReadTechnology(python, tree, out _, out var techProblem);
            if (techProblem is not null) Console.WriteLine($"technology: {techProblem}");
            var offer = stack is { Stack.Count: > 0 }
                ? new CircuitRF.Ui.Views.Dialogs.GdsfactoryImportReportDialog.TechnologyOffer(stack.Stack.Count, stack.Materials.Count, tree.Module + ".ctech")
                : null;
            // The kit's drawings made into symbols, as Import PDK makes them, into a scratch kit folder (brief-gdf-4).
            string symbolFolder = Path.Combine(Path.GetTempPath(), "crf-gdf4-report-" + Guid.NewGuid().ToString("N")[..8]);
            var symbols = CircuitRF.Design.Schematic.KitGlyphSymbol.Install(symbolFolder, report);
            Console.WriteLine(symbols.Tally());
            var dialog = CircuitRF.Ui.Views.Dialogs.GdsfactoryImportReportDialog.Build(tree, report, null, pairings, allowed: true, offer, symbols);
            // report: as it first opens, every section collapsed; report-open: every section but the
            // long list of offered cells expanded, so the rest is on screen.
            if (step == "report-open")
                foreach (var expander in dialog.GetLogicalDescendants().OfType<Expander>().Skip(1)) expander.IsExpanded = true;
            _ = dialog.ShowDialog(host);
            if (step == "report-open")
            {
                Pump();
                dialog.GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
            }
            break;
        }
        case "pdk-report":
        {
            // The ordinary Import PDK report for a folder, as File ▸ Import PDK opens it — through the one
            // import both callers run, into a scratch workspace (brief-gdf-6 R-gdf6-2). On a folder holding
            // a kit tree and a gdsfactory package it says that both are imported, and from where.
            if (gdsfactoryKit is null) { Console.Error.WriteLine("--gdsfactory pdk-report needs --kit <kit folder>"); return false; }
            string scratch = Path.Combine(Path.GetTempPath(), "crf-gdf6-report-" + Guid.NewGuid().ToString("N")[..8]);
            var created = CircuitRF.Design.Workspace.WorkspaceCreate.Create(scratch, "ws", technologyId: null);
            var result = CircuitRF.Design.Schematic.PdkWorkspaceImport.Import(gdsfactoryKit, created.CwsPath);
            Console.WriteLine(CircuitRF.Design.Schematic.PdkWorkspaceImport.TallySentence(result.Report));
            Console.WriteLine(result.Gdsfactory is { } gf ? $"gdsfactory half: {gf.Module} at {gf.InstalledDirectory}" : "no gdsfactory half");
            _ = CircuitRF.Ui.Views.Dialogs.PdkImportReportDialog.ShowAsync(host, result.Report);
            break;
        }
        case "tech-overwrite":
        {
            // "Also build the kit's technology" over a .ctech of the same name (brief-gdf-3 R-gdf3-3a): the
            // merge question File ▸ Import ▸ Technology asks, against an existing file someone has edited
            // (a recoloured layer and a renamed slab), so the conflicts it lists are real ones.
            if (tree is null) { Console.Error.WriteLine("--gdsfactory tech-overwrite needs --kit <source tree>"); return false; }
            var stack = CircuitRF.Design.Layout.PCells.Wire.GdsfactoryKitImport.ReadTechnology(python, tree, out var table, out var problem);
            if (stack is null) { Console.Error.WriteLine($"no technology: {problem}"); return false; }
            var built = CircuitRF.Design.Layout.TechImport.GdsfactoryStackConverter.Convert(stack, table, tree.Module);
            Console.WriteLine(built.Summary);
            var existing = TechPersistence.Deserialize(TechPersistence.Serialize(built.Technology));
            if (existing.Layers.Count > 0) existing.Layers[0].Color = new CircuitRF.Design.Theming.Rgba(200, 40, 40);
            if (existing.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor) is { } c) c.SigmaSm = 5.8e7;
            var sections = CircuitRF.Ui.Layout.TechnologyMerge.SectionsPresentIn(built.Technology);
            var conflicts = CircuitRF.Ui.Layout.TechnologyMerge.FindConflicts(existing, built.Technology, sections);
            _ = new CircuitRF.Ui.Views.Dialogs.TechnologyMergeDialog(tree.Module + ".ctech", sections, isReimport: true, conflicts)
                .ShowDialog<CircuitRF.Ui.Views.Dialogs.TechnologyMergeResult?>(host);
            break;
        }
        case "cell":
        {
            // A real gdsfactory cell, generated through the worker, placed in --clay's layout: the
            // palette tile beside the layout, and the cell's parameter dialog with each row's tooltip.
            if (clay is null) { Console.Error.WriteLine("--gdsfactory cell needs --clay <layout in a workspace that declares the kit>"); return false; }
            string ws = CircuitRF.Design.Workspace.WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(clay)!)
                        ?? throw new InvalidOperationException($"'{clay}' is not in a workspace");
            var resolver = new CircuitRF.Design.Layout.PCells.Wire.PCellWorkerResolver(ws,
                report: m => Console.WriteLine($"[pcell] {m}"),
                trust: _ => CircuitRF.Design.Layout.PCells.Wire.PCellTrustDecision.Allowed,
                moduleInterpreter: _ => python);
            CircuitRF.Design.Layout.PCells.PCellRegistry.AddResolver(resolver);
            string cellId = gdsfactoryCell ?? "kinds";

            var model = LayoutPersistence.LoadFromFile(clay);
            var vm = new LayoutEditorViewModel(model, clay, new ConsoleSink());
            var (resolution, _) = TechnologyResolver.ResolveForDocument(model.TechRef, clay, null, new TechnologyCache());
            vm.Technology = resolution.Tech;
            if (!vm.PlacePCell(cellId, resolver.DeclaredDefaults(cellId) ?? new Dictionary<string, CircuitRF.Design.Layout.PCells.PCellValue>(), 0, 0))
            { Console.Error.WriteLine($"'{cellId}' could not be placed"); return false; }

            var models = new Dictionary<string, string>();
            foreach (var gid in resolver.KnownGeneratorIds)
                if (resolver.DeclaredDefaults(gid)?.FirstOrDefault(kv => kv.Key == "model") is { Key: not null } m)
                    models[gid] = m.Value.ToString().Split(':').Last();
            var tool = new PaletteTool();
            tool.SetPdkParts(CircuitRF.Ui.Schematic.KitPaletteMerge.Compose([], resolver.KitNameByGeneratorId, models));
            tool.SearchQuery = cellId;

            var doc = new LayoutDocument(Path.GetFileName(clay), vm, clay);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
            grid.Children.Add(new CircuitRF.Ui.Views.Palette.PaletteToolView { DataContext = tool });
            var editor = new LayoutEditorView { DataContext = doc };
            Grid.SetColumn(editor, 1);
            grid.Children.Add(editor);
            host.Content = grid;
            host.Width = 1100; host.Height = 640;
            Pump();
            host.GetVisualDescendants().OfType<LayoutCanvas>().FirstOrDefault()?.ZoomToFit();

            var props = new LayoutShapePropertiesViewModel();
            props.SetContext(vm);
            props.PinToInstance(0);
            foreach (var row in props.PCellParamRows?.ToList() ?? [])
                Console.WriteLine($"row {row.Label}: {row.ValueText}  tip: {row.Tip ?? "(none)"}");
            var dialog = new CircuitRF.Ui.Views.Dialogs.LayoutPCellParameterDialog { DataContext = props };
            dialog.Show(host);
            Pump();
            // The tooltip of the first real-valued row, open, so the capture shows it.
            if (dialog.GetVisualDescendants().OfType<TextBlock>()
                      .FirstOrDefault(t => t.DataContext is PCellParamRowViewModel r && r.Tip is { } tip
                                           && tip.Contains("kit units") && ToolTip.GetTip(t) is not null) is { } label)
                ToolTip.SetIsOpen(label, true);
            break;
        }
        case "symbols":
        {
            // brief-gdf-4: the kit's own drawings made into symbols as Import PDK makes them — the real bridge
            // report, KitGlyphSymbol.Install into a fresh workspace's kit folder — then mounted as the workspace
            // mounts them, each placed on a schematic through the placement path, beside the palette tiles.
            if (tree is null) { Console.Error.WriteLine("--gdsfactory symbols needs --kit <source tree>"); return false; }
            var report = CircuitRF.Design.Layout.PCells.Wire.GdsfactoryKitImport.ReadReport(python, tree, out var problem);
            if (report is null) { Console.Error.WriteLine($"no report: {problem}"); return false; }
            string parent = Path.Combine(Path.GetTempPath(), "crf-gdf4-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(parent);
            string ws = CircuitRF.Design.Workspace.WorkspaceCreate.Create(parent, "glyphs", null).WorkspaceDir;
            string folder = CircuitRF.Design.Layout.PCells.Wire.GdsfactoryKitImport.Declare(ws, tree, out var declareProblem, out _)
                            ?? throw new InvalidOperationException(declareProblem);
            var installed = CircuitRF.Design.Schematic.KitGlyphSymbol.Install(folder, report);
            Console.WriteLine(installed.Tally());
            foreach (var (cell, text, warning) in installed.Lines) Console.WriteLine($"{(warning ? "warning" : "note")}: {cell}: {text}");

            string kitName = Path.GetFileName(folder);
            var parts = new List<CircuitRF.Design.Schematic.PdkKitPart>();
            var tiles = new List<CircuitRF.Design.Schematic.PaletteItem>();
            foreach (string cell in report.Offered)
                if (CircuitRF.Design.Schematic.PCellKitSchematicParts.TryBuild(kitName, cell, folder, null, out var partProblem) is { } part)
                {
                    parts.Add(part);
                    tiles.Add(CircuitRF.Design.Schematic.PCellKitSchematicParts.PaletteItemFor(kitName, part));
                    foreach (var pin in part.Symbol.Pins) Console.WriteLine($"{cell}: pin {pin.Name} at ({pin.LocalX}, {pin.LocalY}) {pin.NameAlign}");
                }
                else if (partProblem is not null) Console.WriteLine($"{cell}: {partProblem}");
            CircuitRF.Design.Schematic.PdkKitRegistry.SetPCellParts(ws, kitName, parts);
            var composed = CircuitRF.Ui.Schematic.KitPaletteMerge.Compose(tiles, report.Offered.ToDictionary(c => c, _ => kitName));

            var tool = new PaletteTool();
            tool.SetPlacementService(new CircuitRF.Ui.Schematic.PlacementService());
            tool.SetPdkParts(composed);
            // The kit's own heading, where a user browsing the kit finds its tiles.
            tool.SelectedCategory = tool.Categories.FirstOrDefault(c => c.DisplayName == kitName) ?? tool.SelectedCategory;
            if (gdsfactoryCell is not null) tool.SearchQuery = gdsfactoryCell;
            Console.WriteLine($"palette: {string.Join(", ", tool.Categories.Select(c => c.DisplayName))}");

            string schDir = Path.Combine(ws, "glyphs");
            Directory.CreateDirectory(schDir);
            var model = new SchematicEditModel { SchematicDirectory = schDir };
            var vm = new SchematicViewModel(model, messageSink: new ConsoleSink());
            double x = 0;
            foreach (var part in parts)
            {
                Await(vm.CommitCellPlacementAsync(CircuitRF.Design.Schematic.PdkKitRegistry.RefFor(kitName, part.PartId), x, 0, SymbolRotation.R0));
                x += 800;
            }
            vm.Selection.Clear();
            var doc = new CircuitRF.Ui.Schematic.SchematicDocument("glyphs", vm, Path.Combine(schDir, "glyphs.csch"));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
            grid.Children.Add(new CircuitRF.Ui.Views.Palette.PaletteToolView { DataContext = tool });
            var view = new CircuitRF.Ui.Views.Content.SchematicView { DataContext = doc };
            Grid.SetColumn(view, 1);
            grid.Children.Add(view);
            host.Content = grid;
            host.Width = sizeGiven ? width : 1200; host.Height = sizeGiven ? height : 560;
            Pump();
            doc.RequestZoomToFit();
            break;
        }
        case "palette":
        {
            // brief-gdf-5: a workspace's kit section of the Library palette as a workspace open composes it — every
            // referenced kit's own parts, then its generators (gdsfactory or otherwise) with their declared models and
            // parameters, the parts synthesised from a kit's own drawings, then KitPaletteMerge — and the tiles counted:
            // symbol and artwork, symbol only, layout only. Every kit is allowed to run, as a user who said Allow.
            if (gdsfactoryWorkspace is null) { Console.Error.WriteLine("--gdsfactory palette needs --workspace <dir>"); return false; }
            string ws = gdsfactoryWorkspace;
            var refs = CircuitRF.Design.Workspace.WorkspacePersistence.LoadFromFile(Path.Combine(ws, ".cws")).PdkRefs ?? [];
            var roots = CircuitRF.Ui.Schematic.PdkReferenceManager.LibraryRootsIn(ws, refs);
            var parts = new List<PaletteItem>();
            foreach (var r in refs.Where(x => !x.IsLibraryOnly))
            {
                var read = PdkWorkspaceImport.ReadReferenced(r, ws, roots);
                if (read.Outcome is not { } outcome) { Console.WriteLine($"{r.Provider}: not loaded: {read.Refusal ?? "missing"}"); continue; }
                PdkKitRegistry.SetKit(ws, r.Provider, outcome.Parts ?? [], outcome.OsdiModels);
                parts.AddRange(outcome.Items);
            }

            var resolver = new CircuitRF.Design.Layout.PCells.Wire.PCellWorkerResolver(ws,
                report: m => Console.WriteLine($"[pcell] {m}"),
                trust: _ => CircuitRF.Design.Layout.PCells.Wire.PCellTrustDecision.Allowed,
                moduleInterpreter: _ => python);
            CircuitRF.Design.Layout.PCells.PCellRegistry.AddResolver(resolver);
            var builtIn = new HashSet<string>(CircuitRF.Design.Layout.PCells.PCellRegistry.KnownGeneratorIds, StringComparer.OrdinalIgnoreCase);
            var kits = resolver.KitNameByGeneratorId.Where(kv => !builtIn.Contains(kv.Key))
                                                     .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var synthesised = new List<PaletteItem>();
            var mounted = new Dictionary<string, List<PdkKitPart>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (gid, kitName) in kits)
            {
                if (resolver.DeclaredDefaults(gid) is { } d)
                {
                    names[gid] = [.. d.Keys];
                    if (d.FirstOrDefault(kv => string.Equals(kv.Key, "model", StringComparison.OrdinalIgnoreCase)) is { Key: not null } hit
                        && hit.Value.ToString() is { Length: > 0 } raw)
                        models[gid] = (raw.IndexOf(':') is var colon and >= 0 ? raw[(colon + 1)..] : raw).Trim();
                }
                if (PCellKitSchematicParts.TryBuild(kitName, gid, resolver.KitDirectoryByGeneratorId.GetValueOrDefault(gid),
                                                    resolver.DeclaredParameters(gid), out _) is { } part)
                {
                    (mounted.TryGetValue(kitName, out var list) ? list : mounted[kitName] = []).Add(part);
                    synthesised.Add(PCellKitSchematicParts.PaletteItemFor(kitName, part));
                }
            }
            foreach (var (kitName, list) in mounted) PdkKitRegistry.SetPCellParts(ws, kitName, list);
            foreach (var g in kits.GroupBy(kv => kv.Value)) Console.WriteLine($"generators {g.Key}: {g.Count()}");
            var claimed = parts.Where(i => i.Pdk is not null).Select(i => (i.Pdk!.KitName, i.Pdk.PartId)).ToHashSet();
            IReadOnlyList<PaletteItem> withSymbols = [.. parts, .. synthesised.Where(i => !claimed.Contains((i.Pdk!.KitName, i.Pdk.PartId)))];
            var composed = CircuitRF.Ui.Schematic.KitPaletteMerge.Compose(withSymbols, kits, models, names);

            // A layout-only tile carries a PdkPartRef too (named after its generator), so "has a symbol" is "was
            // one of the parts that went in", never "has a Pdk".
            var symbolled = withSymbols.Where(i => i.Pdk is not null).Select(i => (i.Pdk!.KitName, i.Pdk.PartId)).ToHashSet();
            bool HasSymbol(PaletteItem i) => i.Pdk is { } r && symbolled.Contains((r.KitName, r.PartId));
            foreach (var group in composed.GroupBy(i => i.Pdk?.KitName ?? "?"))
                Console.WriteLine($"palette {group.Key}: {group.Count(i => HasSymbol(i) && i.PCellGeneratorId is not null)} symbol and artwork, " +
                                  $"{group.Count(i => HasSymbol(i) && i.PCellGeneratorId is null)} symbol only, " +
                                  $"{group.Count(i => !HasSymbol(i) && i.PCellGeneratorId is not null)} layout only");

            var tool = new PaletteTool();
            tool.SetPlacementService(new CircuitRF.Ui.Schematic.PlacementService());
            tool.SetPdkParts(composed);
            if (paletteCategory is not null)
                tool.SelectedCategory = tool.Categories.FirstOrDefault(c => c.DisplayName == paletteCategory) ?? tool.SelectedCategory;
            if (gdsfactoryCell is not null) tool.SearchQuery = gdsfactoryCell;
            Console.WriteLine($"palette headings: {string.Join(", ", tool.Categories.Select(c => c.DisplayName))}");
            // --cell naming parts with symbols (comma-separated): each placed on a schematic through the placement path,
            // so its symbol can be looked at at full size — beside the palette for one, in a row on their own for several.
            var toPlace = (gdsfactoryCell ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => composed.FirstOrDefault(i => HasSymbol(i) && string.Equals(i.Pdk!.PartId, n, StringComparison.OrdinalIgnoreCase)))
                .Where(i => i is not null).Select(i => i!.Pdk!).ToList();
            if (toPlace.Count > 0)
            {
                string schDir = Path.Combine(ws, "palette-snapshot");
                Directory.CreateDirectory(schDir);
                var model = new SchematicEditModel { SchematicDirectory = schDir };
                var vm = new SchematicViewModel(model, messageSink: new ConsoleSink());
                foreach (var part in toPlace)
                    if (PdkKitRegistry.Find(PdkKitRegistry.RefFor(part.KitName, part.PartId), ws) is { } kp)
                        Console.WriteLine($"pins {part.PartId}: {string.Join(" ", kp.Symbol.Pins.Select(p => $"{p.Name}({p.LocalX:0.##},{p.LocalY:0.##})"))}");
                for (int k = 0; k < toPlace.Count; k++)
                    Await(vm.CommitCellPlacementAsync(PdkKitRegistry.RefFor(toPlace[k].KitName, toPlace[k].PartId), k * 600, 0, SymbolRotation.R0));
                vm.Selection.Clear();
                var doc = new CircuitRF.Ui.Schematic.SchematicDocument("palette", vm, Path.Combine(schDir, "palette.csch"));
                var view = new CircuitRF.Ui.Views.Content.SchematicView { DataContext = doc };
                if (toPlace.Count == 1)
                {
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };
                    grid.Children.Add(new CircuitRF.Ui.Views.Palette.PaletteToolView { DataContext = tool });
                    Grid.SetColumn(view, 1);
                    grid.Children.Add(view);
                    host.Content = grid;
                }
                else host.Content = view;
                host.Width = sizeGiven ? width : 1100; host.Height = sizeGiven ? height : 560;
                Pump();
                doc.RequestZoomToFit();
                break;
            }
            host.Content = new CircuitRF.Ui.Views.Palette.PaletteToolView { DataContext = tool };
            host.Width = sizeGiven ? width : 320; host.Height = sizeGiven ? height : 720;
            break;
        }
        case "permissions" or "permissions-many":
        {
            // Settings ▸ Security & Permissions on a throwaway state directory seeded with three answers:
            // a gdsfactory package allowed (with its interpreter), a script kit refused, and a kit that
            // is no longer on disk — so each row shape shows, and --kit-row changes are real writes.
            string state = Path.Combine(Path.GetTempPath(), "crf-permissions-" + Guid.NewGuid().ToString("N")[..8]);
            CircuitRF.Ui.AppDataRoot.RedirectTo(state);
            string package = tree?.InstalledDirectory ?? Path.Combine(state, "site-packages", "synthkit");
            Directory.CreateDirectory(package);
            if (tree is null) File.WriteAllText(Path.Combine(package, "__init__.py"), "");
            string scripts = Path.Combine(state, "kits", "mmic-pcells");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "pcell-generators.json"), "{}");
            CircuitRF.Ui.Layout.PCells.PCellTrustPreferences.Set(package, true);
            CircuitRF.Ui.Layout.PCells.PCellTrustPreferences.Set(scripts, false);
            CircuitRF.Ui.Layout.PCells.PCellTrustPreferences.Set(Path.Combine(state, "old-env", "lib", "python3.12", "site-packages", "retired_kit"), true);
            CircuitRF.Ui.Layout.PCells.GdsfactoryInterpreterChoices.Record(Path.GetFileName(package), python);
            // permissions-many: two hundred more, the scale a shared tool area reaches, for the list's own scrolling.
            if (step == "permissions-many")
                CircuitRF.Ui.Theming.AppPreferencesIo.Update(p =>
                {
                    for (int k = 0; k < 200; k++)
                        p.PCellTrust![Path.Combine(state, "area", $"cell_lib_{k:D3}")] = k % 7 != 0;
                });
            var settings = new CircuitRF.Ui.Views.Dialogs.SettingsView(null);
            if (sizeGiven) { settings.Width = width; settings.Height = height; }
            _ = settings.ShowDialog(host);
            Pump();
            settings.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
            break;
        }
        default:
            Console.Error.WriteLine($"--gdsfactory: unknown step '{step}' (prompt, pick, interpreter, trust, report, report-open, tech-overwrite, cell, symbols, palette, permissions, permissions-many)");
            return false;
    }
    Pump();
    return true;
}

void OpenAddVendorLibrary(string folder)
{
    var build = CircuitRF.Design.VendorLibraries.VendorLibraryIndex.Build(folder);
    _ = CircuitRF.Ui.Views.Dialogs.AddVendorLibraryDialog.AskAsync(host, folder,
            CircuitRF.Design.VendorLibraries.VendorLibraryRefs.DefaultName(folder), build.SummaryLines());
    Pump();
}

static IReadOnlyList<(string Name, string? Root)> VendorLibrariesOf(string ws)
{
    var cws = CircuitRF.Design.Workspace.WorkspacePersistence.LoadFromFile(Path.Combine(ws, ".cws"));
    return [.. (cws.VendorLibraries ?? []).Select(r =>
        (r.Name, CircuitRF.Design.VendorLibraries.VendorLibraryRefs.ResolveLibraryRoot(ws, r.Name, out _)))];
}

static void IndexVendorLibraries(string ws)
{
    foreach (var (name, root) in VendorLibrariesOf(ws))
    {
        if (root is null) { Console.WriteLine($"vendor library '{name}': does not resolve"); continue; }
        var build = CircuitRF.Design.VendorLibraries.VendorLibraryIndex.StartBuild(root);
        Await(build);
        Console.WriteLine($"vendor library '{name}': {build.Result.Entries.Count} parts");
    }
}

Window? TopDialog() => host.OwnedWindows.LastOrDefault(w => w.IsVisible);

ContextMenu? OpenContextMenu() => layoutCanvas?.ContextMenu is { IsOpen: true } m ? m : null;

static IEnumerable<MenuItem> MenuItems(IEnumerable<MenuItem> items)
{
    foreach (var m in items)
    {
        yield return m;
        foreach (var sub in MenuItems(m.Items.OfType<MenuItem>())) yield return sub;
    }
}

// x,y in the window's DIPs; @x,y in the layout's micrometres, through the canvas's own viewport.
Point PointOf(string xy)
{
    if (xy.StartsWith('@') && layoutCanvas is not null && layoutVm is not null)
    {
        var w = ParsePoint(xy[1..]);
        double dbu = layoutVm.Model.DbuPerMicron;
        var (sx, sy) = layoutCanvas.WorldToScreen(w.X * dbu, w.Y * dbu);
        return layoutCanvas.TranslatePoint(new Point(sx, sy), host)!.Value;
    }
    return ParsePoint(xy);
}

static Point ParsePoint(string xy)
{
    var parts = xy.Split(',');
    return new Point(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                     double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
}

// A Model Composer task posts its result back through the dispatcher: pump until nothing is running.
static void WaitIdle(CircuitRF.Ui.ModelComposer.ModelComposerViewModel vm)
{
    var until = DateTime.UtcNow.AddSeconds(120);
    do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } while (vm.IsRunning && DateTime.UtcNow < until);
}

// --impedance: the editor's own run (RunTraceImpedanceAsync), then --finding selected as the panel's row selects it.
static bool RunImpedance(LayoutEditorViewModel vm, string freq, string? kind, List<string> layerNames)
{
    List<CircuitRF.Design.Layout.LayerKey>? layers = null;
    if (layerNames.Count > 0)
    {
        layers = [];
        foreach (string name in layerNames)
        {
            var def = vm.Technology?.Layers.FirstOrDefault(l => l.Name == name);
            if (def is null) { Console.Error.WriteLine($"--impedance-layer: no layer '{name}'"); return false; }
            layers.Add(def.Key);
        }
    }
    double? hz = null;
    if (!freq.Equals("none", StringComparison.OrdinalIgnoreCase))
    {
        var m = System.Text.RegularExpressions.Regex.Match(freq, @"^([0-9.]+)\s*(GHz|MHz|kHz|Hz)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) { Console.Error.WriteLine($"--impedance takes a frequency with its unit (6GHz) or none, not '{freq}'"); return false; }
        hz = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
           * m.Groups[2].Value.ToLowerInvariant() switch { "ghz" => 1e9, "mhz" => 1e6, "khz" => 1e3, _ => 1 };
    }
    var task = vm.RunTraceImpedanceAsync(new CircuitRF.Design.Layout.Em.TraceImpedanceOptions { MaxFrequencyHz = hz, Layers = layers },
                                         new CircuitRF.Engine.RunControl());
    Await(task);
    if (task.Result is not { } report) { Console.Error.WriteLine("the Impedance Analysis gave no report"); return false; }
    foreach (var t in report.AllTraces)
    {
        Console.WriteLine($"{t.Id} {t.Verdict} on {t.LayerName}");
        foreach (var i in t.Issues) Console.WriteLine($"  {i.Kind} {i.Severity}: {i.Text}");
    }
    if (kind is null) return true;
    string Id(CircuitRF.Design.Layout.Em.TraceIssueKind k) =>
        System.Text.RegularExpressions.Regex.Replace(k.ToString(), "(?<=[a-z])(?=[A-Z])", "-").ToLowerInvariant();
    var hit = report.AllTraces.SelectMany(t => t.Issues.Select(i => (t, i))).FirstOrDefault(p => Id(p.i.Kind) == kind.ToLowerInvariant());
    if (hit.t is null) { Console.Error.WriteLine($"no {kind} finding"); return false; }
    vm.SelectImpedance(hit.t, hit.i);
    vm.ZoomToImpedance(hit.t, hit.i);
    Console.WriteLine($"selected {hit.t.Id}'s {kind}; {vm.SelectionStatusText}");
    return true;
}

// --impedance-panel: the real panel on the editor, as the dock shows it, its export counted, scrolled to Export.
static void ShowImpedancePanel(LayoutEditorViewModel vm, string path)
{
    var tool = new CircuitRF.Ui.ViewModels.Dock.ImpedanceTool();
    tool.SetActiveLayout(vm);
    var window = new Window { Width = 400, Height = 900, Content = new CircuitRF.Ui.Views.Impedance.ImpedanceToolView { DataContext = tool } };
    window.Show();
    var until = DateTime.UtcNow.AddSeconds(120);
    do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    while ((tool.Panel.FullPageCount is null || tool.Panel.IsSurveying) && DateTime.UtcNow < until);
    var panel = tool.Panel;
    Console.WriteLine($"export: [{(panel.ExportFull ? "x" : " ")}] {panel.FullStyleText}   [{(panel.ExportSummary ? "x" : " ")}] {panel.SummaryStyleText}");
    Console.WriteLine($"note: {(panel.ExportStyleNote.Length > 0 ? panel.ExportStyleNote : "(none)")}");
    for (int k = 0; k < 4; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.ScrollToEnd();
    for (int k = 0; k < 4; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
}

// --messages: the real Messages panel holding one row of each kind — plain text, a file link, a live progress bar, the
// Relaunch button and an expandable row. Prints each row's height and where its text and its inline control sit, so a
// row that draws taller than the others, or a control off the text's centre line, is read rather than eyeballed.
static int ShowMessagesPanel(string path, double w, double h)
{
    var tool = new MessagesTool();
    tool.Post(MessageLevel.Info, "Plain message before");
    tool.Post(MessageLevel.Info, "Wrote results to", "/tmp/demo/results/SP1.s2p");
    tool.Post(MessageLevel.Info, "Plain message between");
    tool.PostAction(MessageLevel.Info, "circuitRF 1.6.3 installed", "Relaunch", () => Task.CompletedTask);
    tool.Post(MessageLevel.Info, "Plain message after");
    tool.PostItems(MessageLevel.Warning, "Three places need attention", [new("one"), new("two"), new("three")]);
    var live = tool.BeginProgress("S-parameter analysis SP1");
    live.Update("S-parameter analysis SP1", "12 / 51", 24);
    var window = new Window { Width = w, Height = h, Content = new CircuitRF.Ui.Views.Messages.MessagesView { DataContext = tool } };
    window.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    foreach (var item in window.GetVisualDescendants().OfType<ListBoxItem>())
    {
        var entry = (MessageEntry)item.DataContext!;
        var row = item.TranslatePoint(default, window) ?? default;
        var text = item.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var textAt = text.TranslatePoint(default, window) ?? default;
        string inline = "";
        foreach (var c in item.GetVisualDescendants().OfType<Control>()
                     .Where(c => c is Button or ProgressBar or (SelectableTextBlock and { Name: null }) && c != text && c.IsEffectivelyVisible))
        {
            var at = c.TranslatePoint(default, window) ?? default;
            inline += $"; {c.GetType().Name} y {at.Y:0.0}..{at.Y + c.Bounds.Height:0.0} (centre {at.Y + c.Bounds.Height / 2:0.0})";
        }
        Console.WriteLine($"row '{entry.Text}': y {row.Y:0.0}, height {item.Bounds.Height:0.0}; text y {textAt.Y:0.0} height {text.Bounds.Height:0.0} "
                        + $"(centre {textAt.Y + text.Bounds.Height / 2:0.0}){inline}");
    }
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

// --review-panel: the real Review panel on a seeded layout — no artwork is reviewed, the result is handed in.
static int ShowReviewPanel(string seed, string path, double w, double h)
{
    var view = new LayoutView { DisplayUnit = LayoutUnit.Um };
    var vm = new LayoutEditorViewModel(view);
    var tool = new ReviewTool();
    tool.SetActiveLayout(vm);
    var panel = tool.Panel;
    var window = new Window { Width = w, Height = h, Content = new CircuitRF.Ui.Views.Review.ReviewToolView { DataContext = tool } };
    window.Show();
    void Settle() { for (int k = 0; k < 4; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); } }
    Settle();

    long Mm(double mm) => (long)Math.Round(mm * 1000 * view.DbuPerMicron);
    CircuitRF.Design.Layout.Review.ReviewFinding Finding(string check, string layer, double x, double y, string sentence, string? measured)
    {
        var box = new Bbox(Mm(x), Mm(y), Mm(x + 0.3), Mm(y + 0.3));
        return new(check, new LayerKey(1, 0), CircuitRF.Design.Layout.Review.ReviewSeverity.Warning, sentence, measured,
                   [[box.MinX, box.MinY, box.MaxX, box.MinY, box.MaxX, box.MaxY, box.MinX, box.MaxY]], box,
                   CircuitRF.Design.Layout.Review.ReviewFinding.KeyOf(check, new LayerKey(1, 0), box)) { LayerName = layer };
    }
    string Title(string id) => CircuitRF.Design.Layout.Review.LayoutReview.FindCheck(id)!.Title;
    CircuitRF.Design.Layout.Review.ReviewCheckRow Row(string id, CircuitRF.Design.Layout.Review.ReviewCheckState state, string? why, int n) =>
        new(id, Title(id), state, why, n, 0);

    switch (seed)
    {
        case "checks":
        {
            var row = panel.Checks.First(c => c.Def.Id == "edge-copper").Thresholds[0];
            row.Text = "500";
            Console.WriteLine($"edge-copper pullback edited to {row.Text} {row.UnitText}; modified: {row.IsModified}; saved: "
                + string.Join(", ", view.Review?.Thresholds?.Select(kv => $"{kv.Key}={kv.Value}") ?? []));
            break;
        }
        case "result":
        {
            var vias = new[]
            {
                Finding("via-in-pad", "Top copper", 1, 1, "A 0.3 mm drill in C209 pad 2 (0.6 × 0.5 mm).", "0.3 mm"),
                Finding("via-in-pad", "Top copper", 4, 2, "A 0.3 mm drill in U3 pad 7 (1.0 × 1.0 mm).", "0.3 mm"),
            };
            var result = new CircuitRF.Design.Layout.Review.LayoutReviewResult(
                [Row("via-in-pad", CircuitRF.Design.Layout.Review.ReviewCheckState.Ran, null, 2),
                 Row("ground-via-distance", CircuitRF.Design.Layout.Review.ReviewCheckState.NotRun,
                     "No ground reference is flagged in the stackup. Flag the ground plane in the Technology Editor.", 0),
                 Row("edge-copper", CircuitRF.Design.Layout.Review.ReviewCheckState.Ran, null, 0)],
                vias, "Seeded", [], false) { DisplayUnit = LayoutUnit.Mm };
            view.ReviewAcceptances.Add(CircuitRF.Design.Layout.Review.ReviewAcceptance.For(vias[1], "Top copper", "Filled and capped via", DateTime.UtcNow));
            vm.ReviewResult = CircuitRF.Design.Layout.Review.ReviewAcceptance.Apply(result, view.ReviewAcceptances);
            break;
        }
        case "many":
        {
            var findings = Enumerable.Range(0, 5000).Select(i => Finding("stacked-traces", i % 2 == 0 ? "Top copper" : "Inner 1",
                i % 100 * 0.5, i / 100 * 0.5, $"Traces of two nets run on top of each other for {1 + i % 7}.2 mm.", $"{1 + i % 7}.2 mm")).ToList();
            vm.ReviewResult = new CircuitRF.Design.Layout.Review.LayoutReviewResult(
                [Row("stacked-traces", CircuitRF.Design.Layout.Review.ReviewCheckState.Ran, null, findings.Count)],
                findings, "Seeded", [], false) { DisplayUnit = LayoutUnit.Mm };
            Settle();
            foreach (var layer in panel.Rows.OfType<CircuitRF.Ui.Layout.Review.ReviewLayerResultRow>().ToList())
                if (!layer.IsExpanded) panel.Rows.OfType<CircuitRF.Ui.Layout.Review.ReviewLayerResultRow>().First(r => r.Title == layer.Title).ToggleExpandedCommand.Execute(null);
            break;
        }
        case "mmic":
            panel.IsMmic = true;
            Console.WriteLine($"reviewing as MMIC: {string.Join(", ", panel.Checks.Select(c => c.Def.Id))}");
            break;
        default:
            Console.Error.WriteLine($"--review-panel takes checks, result, many or mmic, not '{seed}'");
            return 2;
    }
    Settle();
    var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "RowsList");
    int realised = list.GetRealizedContainers().Count();
    Console.WriteLine($"rows: {panel.Rows.Count:N0}; realised row containers: {realised}");
    foreach (var c in panel.Rows.OfType<CircuitRF.Ui.Layout.Review.ReviewCheckResultRow>())
        Console.WriteLine($"  {c.Title}: {c.CountText}{(c.HasDetail ? " — " + c.DetailText : "")}");
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

static bool Await2(Task<bool> task)
{
    Await(task);
    return task.Result;
}

static void Await(Task task)
{
    var until = DateTime.UtcNow.AddSeconds(120);
    while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    if (!task.IsCompleted) throw new TimeoutException("the display did not finish loading");
    task.GetAwaiter().GetResult();
}

/// <summary>The application's own styles, resources and view templates, without circuitRF's App — whose
/// start-up opens windows and restores a workspace. Unlike tools/DocGen's DocsApp it merges no docs-only paint
/// remap, so the picture is what the application paints.</summary>
sealed class SnapshotApp : Application
{
    public override void Initialize()
    {
        var baseUri = new Uri("avares://CircuitRF.Ui/");
        Resources.MergedDictionaries.Add(
            new ResourceInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfResources.axaml") });
        Styles.Add(
            new StyleInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfStyles.axaml") });
        var app = new App();
        app.Initialize();
        foreach (var template in app.DataTemplates) DataTemplates.Add(template);
    }
}

/// <summary>The Messages panel, for --clay: each line printed as it is posted.</summary>
sealed class ConsoleSink : IMessageSink
{
    public void Post(MessageLevel level, string text, string? filePath = null) => Console.WriteLine($"[{level}] {text}");
    public void Clear() { }
}
