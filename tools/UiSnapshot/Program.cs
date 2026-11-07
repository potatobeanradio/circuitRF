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
// UiSnapshot --yield <file.csch> -o <out.png> [--mode sensitivity|montecarlo|yield|corners|centering|doe] [--run]
//            [--by entry|port|instance] [--select <key>] [--expand <label>] [--exclude <instance>]... [--snp-off] [--sens-display] [--size WxH] [steps]
// UiSnapshot --clay <file.clay> -o <out.png> [--hide <layer>] [--lock <layer>] [--size WxH] [steps]
//            [--rclick x,y] [--menu <header>] [--type <text>] [--button <label>]
//            [--impedance <freq|none>] [--impedance-layer <name>]... [--finding <kind>] [--impedance-panel]
// UiSnapshot --cem <file.cem> -o <out.png> [--size WxH] [steps]
// UiSnapshot --review-panel checks|result|many|mmic -o <out.png> [--size WxH]
// UiSnapshot --symbol <stem> [-o <dir>]
// UiSnapshot --cmc <file.cmc|new:Passive|new:Active> -o <out.png> [--import <data>] [--step <name>] [--run <key>] [--marker]... [--library-button] [--size WxH] [steps]
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
// --yield opens the Yield panel on a schematic as the shell hosts it — or, with --mode sensitivity, the Sensitivity
// panel (the same view over a one-mode panel, brief-sens-3). --exclude unticks an S-parameter block and --snp-off the
// S-parameters master, each as the panel's own edit (on the loaded copy; nothing is saved); --run presses Run and waits;
// --by picks the ranking's level, --expand opens an Instance row, --select picks a ranked row (or a block) by label or
// key, which shows an SnP instance's matrix tile. It prints the blocks, the cost and the ranked rows.
//
// --signal opens the Signal Designer window itself on a MOD_Source carrying those carriers (netlist form,
// Carrier[i]), waits for its preview — generated through the cache a simulation reads — and photographs the
// window; --tab 1 shows the second preview tab (EVM, Pulse or Frequency). --standalone opens it as Tools ▸ Signal
// Designer does, on no MOD_Source; --iq then reads an I/Q file through Load custom.
//
// --clay opens the layout editor view on a .clay, its technology resolved as the workspace resolves it, fitted. A step
// point written @x,y is in the layout's micrometres. --rclick opens the canvas's context menu, --menu clicks one of its
// items by header (prefix), --type types into the open dialog's text box and --button clicks a dialog button by its
// label. --hide and --lock untick a layer's row in the layer list ("Ports" names the Ports row); --layer-list opens it. Messages print as they are posted. An open context
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
// --ipc2581-steps <file.xml> shows the step chooser on a file's own steps; --ipc2581-report <file.xml> imports it and shows the Messages panel holding the report, printing where the technology landed.
// --nets-panel nets|filter|selected|none|many|nolayout shows the Nets panel on a seeded layout and prints where its action row landed.
// --nets a,b (with --clay) shows the real Nets panel on the editor with those rows selected, --nets-filter x narrows it first,
// --nets-dim ticks Dim other copper and --nets-zoom presses Zoom to net; the panel is written beside -o as <out>.nets.png.
// --review (with --clay) runs the editor's own Layout Review and draws its markers on the canvas, as the open Review
// panel does; each finding prints.
//
// --c3d-setup-panel <x.c3d> opens a .c3d's first EM embedded setup in the same panel, embedded, with its Component ports list
// over the document's placed layouts (brief-emb-1); --check-component-ports presses Check, and what it posts to Messages prints.
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
//   --cell-own-values <file.csch> --select <instance>         brief-sdl-10: Make These the Cell's Own Values' confirmation;
//   --schematic <file.csch> [--select <instance>]             the schematic beside the Parameter Editor on one part;
//                                                              --type Name=VendorPartBox types into the Part field
//                                                              --dialog opens the Component Parameters dialog instead
//   --archive <workspace>                                     the Archive Workspace dialog
//   --manage-libraries <workspace>                            File ▸ Manage Libraries… (written to <out>.dialog.png)
//   --add-vendor-library <folder>                             Add Vendor Library's confirm step (<out>.dialog.png)
//   --export-to-library <cell> [--export-library <dir>]       Export to Library's dialog on a cell (<out>.dialog.png)
//     [--export-protect [run]] [--export-encapsulate [run]] [--export-protect-view] [--export-compile-models]
//     [--export-protect [run]] [--export-compile-models]        Compile netlist / Compile Verilog-A models ticked
//       [--export-build <rid>=<path>]…                        with rows of Builds for other platforms
//       [--export-protect [run]]                              Compile netlist ticked; run also exports and shows Builds
//   --yield <csch> --generate-corners                         the Yield panel's Generate Corners dialog (<out>.dialog.png)
//   --import-schematic <file>                                 File ▸ Import ▸ Schematic…'s dialog on a design file (<out>.dialog.png)
//   --repoint-question <part>                                 Copy to Workspace…'s re-point question (<out>.dialog.png)
// With --schematic, a step point written #<instance> is the middle of that part's pins, so --rclick #X1 opens the
// canvas's context menu on X1; the schematic is hosted with a hierarchy host, so its library-part items are offered.
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
string? netsPanel = null;
string? ipc2581Steps = null, ipc2581Report = null;
string? netsHighlight = null;
string? netsFilter = null;
bool netsDim = false, netsZoom = false;
bool planarMemoryDialog = false;
string? dxfMappingDialog = null;     // brief DFR38-1: the Layer Mapping dialog and its preview on a DXF
string? dxfMappingTech = null, dxfMappingMap = null;
bool unsavedSetupDialog = false;     // the question New 3D View from Layout asks of an EM setup with unsaved edits
bool settingsGeneral = false;
bool settingsSolvers = false;
bool reviewRun = false;
string? place = null;
string? cdd = null, output = null, view = "canvas", results = null, fc = "2", iq = null, tuning = null, clay = null, cem = null;
// brief-dfr32-3 — a .c3d's setup cards beside its Properties panel: --delete-fields runs a card's Delete Saved Fields (into a
// scratch folder, never the Trash) and --c3d-row selects an Object Tree row (a field plot) for the panel.
string? c3dSetups = null, c3dDeleteFields = null, c3dRow = null;
string? c3dSetupPanel = null;      // brief-emb-1 — a .c3d's embedded setup in the EM Setup panel, Component ports listed
bool componentPortsCheck = false;
// --c3d-pick <object>: the 3D view's click on that object (hovered at a pixel the CPU picker says shows it).
CircuitRF.Ui.ThreeD.C3dEditorViewModel? c3dShown = null;
string? optimizer = null;
string? yieldCsch = null, yieldMode = null, yieldBy = null, yieldExpand = null;
bool yieldRun = false, yieldSnpOff = false, yieldDisplay = false, yieldGenerate = false;
var yieldExclude = new List<string>();
string? ctech = null;
bool optimize = false;
string? cmc = null, cmcImport = null, cmcStep = null, cmcRun = null;
bool cmcLibraryButton = false;
// File ▸ Export Data… over a results folder (its .npy files are the datasources); --button picks a format.
string? exportData = null;
string? image = null, imageTech = null;
bool digitise = false, makeSchematic = false, stripGeometry = false;
string? imageScalePoints = null;
int cmcMarkers = 0;
string? palette = null, paletteCategory = null, paletteSearch = null, schematic = null, select = null;
bool parameterDialog = false;
string? analysisDialog = null, emSolvesDialog = null;   // brief-sdl-6
string? emRunSettings = null;   // brief-cosim-pattern-2
string? cellOwnValues = null;                            // brief-sdl-10
string? archive = null, manageLibraries = null, addVendorLibrary = null, exportToLibrary = null, exportLibrary = null, importSchematic = null;
var exportBuilds = new List<(string Rid, string Path)>();
string? exportProtect = null;
string? exportEncapsulate = null;   // brief vendorlib-19
bool exportProtectView = false;
bool exportCompileModels = false;
string? repointQuestion = null;
// --project-tree <workspace> [--reveal <file>] [--filter-open] [--filter-all-off]: the Workspace Tree panel; --reveal runs
// a document tab's Reveal in Workspace Tree on that file, --filter-open opens the category filter's flyout.
string? projectTree = null, revealPath = null;
bool filterOpen = false, filterAllOff = false;
CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool? projectTreeTool = null;
string? gdsfactory = null, gdsfactoryPython = null, gdsfactoryKit = null, gdsfactoryCell = null, gdsfactoryWorkspace = null;
string? csym = null, csymPick = null;
bool dark = false;
var layerToggles = new List<(string Name, bool Hide)>();
bool layerList = false;
bool layoutInspector = false;
bool c3dEditor = false;
// --untick <row> (with --c3d-setups): clears that Object Tree row's visibility tick, as a click on it does. Repeatable.
var c3dUntick = new List<string>();
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
        case "--yield" when i + 1 < args.Length: yieldCsch = Path.GetFullPath(args[++i]); break;
        case "--mode" when i + 1 < args.Length: yieldMode = args[++i].ToLowerInvariant(); break;
        case "--by" when i + 1 < args.Length: yieldBy = args[++i].ToLowerInvariant(); break;
        case "--expand" when i + 1 < args.Length: yieldExpand = args[++i]; break;
        case "--exclude" when i + 1 < args.Length: yieldExclude.Add(args[++i]); break;
        case "--snp-off": yieldSnpOff = true; break;
        case "--sens-display": yieldDisplay = true; break;
        case "--generate-corners": yieldGenerate = true; break;
        case "--clay" when i + 1 < args.Length: clay = Path.GetFullPath(args[++i]); break;
        case "--cem" when i + 1 < args.Length: cem = Path.GetFullPath(args[++i]); break;
        case "--ctech" when i + 1 < args.Length: ctech = Path.GetFullPath(args[++i]); break;
        case "--cmc" when i + 1 < args.Length: cmc = args[++i]; break;
        // brief vendorlib-17: connects Model Composer's Export to Library… to a stand-in for the workspace, so the button
        // shows the state an open workspace gives it (without it, the button says it needs one).
        case "--library-button": cmcLibraryButton = true; break;
        case "--image" when i + 1 < args.Length: image = Path.GetFullPath(args[++i]); break;
        case "--tech" when i + 1 < args.Length: imageTech = Path.GetFullPath(args[++i]); break;
        case "--digitise": digitise = true; break;
        case "--make-schematic": makeSchematic = true; break;
        case "--strip-geometry": stripGeometry = true; break;
        case "--scale-points" when i + 1 < args.Length: imageScalePoints = args[++i]; break;
        case "--import" when i + 1 < args.Length: cmcImport = Path.GetFullPath(args[++i]); break;
        case "--export-data" when i + 1 < args.Length: exportData = Path.GetFullPath(args[++i]); break;
        case "--step" when i + 1 < args.Length: cmcStep = args[++i]; break;
        // A bare --run (nothing after it, or another flag) is the Yield panel's Run; with a value, the composer's key.
        case "--run" when i + 1 >= args.Length || args[i + 1].StartsWith('-'): yieldRun = true; break;
        case "--run" when i + 1 < args.Length: cmcRun = args[++i]; break;
        case "--marker": cmcMarkers++; break;
        case "--hide" when i + 1 < args.Length: layerToggles.Add((args[++i], true)); break;
        case "--impedance" when i + 1 < args.Length: impedance = args[++i]; break;
        case "--finding" when i + 1 < args.Length: finding = args[++i]; break;
        case "--impedance-layer" when i + 1 < args.Length: impedanceLayers.Add(args[++i]); break;
        case "--impedance-panel": impedancePanel = true; break;
        case "--review-panel" when i + 1 < args.Length: reviewPanel = args[++i]; break;
        case "--nets-panel" when i + 1 < args.Length: netsPanel = args[++i]; break;
        case "--ipc2581-steps" when i + 1 < args.Length: ipc2581Steps = Path.GetFullPath(args[++i]); break;
        case "--ipc2581-report" when i + 1 < args.Length: ipc2581Report = Path.GetFullPath(args[++i]); break;
        case "--nets" when i + 1 < args.Length: netsHighlight = args[++i]; break;
        case "--nets-filter" when i + 1 < args.Length: netsFilter = args[++i]; break;
        case "--nets-dim": netsDim = true; break;
        case "--nets-zoom": netsZoom = true; break;
        case "--planar-memory-dialog": planarMemoryDialog = true; break;
        case "--dxf-mapping-dialog" when i + 1 < args.Length: dxfMappingDialog = Path.GetFullPath(args[++i]); break;
        case "--dxf-mapping-tech" when i + 1 < args.Length: dxfMappingTech = args[++i]; break;
        case "--dxf-mapping-map" when i + 1 < args.Length: dxfMappingMap = args[++i]; break;
        case "--unsaved-setup-dialog": unsavedSetupDialog = true; break;
        case "--settings-general": settingsGeneral = true; break;
        case "--settings-solvers": settingsSolvers = true; break;
        case "--analysis-dialog" when i + 1 < args.Length: analysisDialog = Path.GetFullPath(args[++i]); break;
        case "--em-solves-dialog" when i + 1 < args.Length: emSolvesDialog = Path.GetFullPath(args[++i]); break;
        case "--em-run-settings" when i + 1 < args.Length: emRunSettings = Path.GetFullPath(args[++i]); break;
        case "--cell-own-values" when i + 1 < args.Length: cellOwnValues = Path.GetFullPath(args[++i]); break;
        case "--review": reviewRun = true; break;
        case "--place" when i + 1 < args.Length: place = args[++i]; break;
        case "--lock" when i + 1 < args.Length: layerToggles.Add((args[++i], false)); break;
        case "--layer-list": layerList = true; break;
        case "--inspector": layoutInspector = true; break;
        case "--editor": c3dEditor = true; break;
        case "--edit-text": break;
        case "--mesh-plane-panel": break;   // with --c3d-setups --editor: the mesh plane's drop-down, opened as a click does
        case "--untick" when i + 1 < args.Length: c3dUntick.Add(args[++i]); break;
        case "--fc" when i + 1 < args.Length: fc = args[++i]; break;
        case "--c3d-setups" when i + 1 < args.Length: c3dSetups = Path.GetFullPath(args[++i]); break;
        case "--c3d-setup-panel" when i + 1 < args.Length: c3dSetupPanel = Path.GetFullPath(args[++i]); break;
        case "--check-component-ports": componentPortsCheck = true; break;
        case "--delete-fields" when i + 1 < args.Length: c3dDeleteFields = args[++i]; break;
        case "--c3d-row" when i + 1 < args.Length: c3dRow = args[++i]; break;
        case "--palette" when i + 1 < args.Length: palette = Path.GetFullPath(args[++i]); break;
        case "--csym" when i + 1 < args.Length: csym = Path.GetFullPath(args[++i]); break;
        case "--pick" when i + 1 < args.Length: csymPick = args[++i]; break;
        case "--dark": dark = true; break;
        case "--messages" or "--film-prompt": break;
        case "--run-messages" when i + 1 < args.Length: i++; break;
        case "--category" when i + 1 < args.Length: paletteCategory = args[++i]; break;
        case "--search" when i + 1 < args.Length: paletteSearch = args[++i]; break;
        case "--schematic" when i + 1 < args.Length: schematic = Path.GetFullPath(args[++i]); break;
        case "--select" when i + 1 < args.Length: select = args[++i]; break;
        case "--dialog": parameterDialog = true; break;
        case "--archive" when i + 1 < args.Length: archive = Path.GetFullPath(args[++i]); break;
        case "--manage-libraries" when i + 1 < args.Length: manageLibraries = Path.GetFullPath(args[++i]); break;
        case "--project-tree" when i + 1 < args.Length: projectTree = Path.GetFullPath(args[++i]); break;
        case "--reveal" when i + 1 < args.Length: revealPath = Path.GetFullPath(args[++i]); break;
        case "--filter-open": filterOpen = true; break;
        case "--filter-all-off": filterAllOff = true; break;
        case "--add-vendor-library" when i + 1 < args.Length: addVendorLibrary = Path.GetFullPath(args[++i]); break;
        // brief vendorlib-10: Export to Library's dialog on a cell folder, its Library field set to --export-library.
        case "--export-to-library" when i + 1 < args.Length: exportToLibrary = Path.GetFullPath(args[++i]); break;
        case "--export-library" when i + 1 < args.Length: exportLibrary = Path.GetFullPath(args[++i]); break;
        case "--export-build" when i + 1 < args.Length:
        {
            string spec = args[++i];
            int eq = spec.IndexOf('=');
            if (eq > 0) exportBuilds.Add((spec[..eq], Path.GetFullPath(spec[(eq + 1)..])));
            break;
        }
        // brief vendorlib-16: Compile netlist ticked; "run" also runs the export, so the Builds table is drawn.
        case "--export-protect":
            exportProtect = i + 1 < args.Length && args[i + 1] == "run" ? args[++i] : "tick";
            break;
        // brief vendorlib-19: Protect the model ticked with Encapsulated netlist (preselected); "run" also exports.
        case "--export-encapsulate":
            exportEncapsulate = i + 1 < args.Length && args[i + 1] == "run" ? args[++i] : "tick";
            break;
        // The Protected model section scrolled into view, nothing ticked.
        case "--export-protect-view": exportProtectView = true; break;
        // Compile Verilog-A models ticked.
        case "--export-compile-models": exportCompileModels = true; break;
        case "--import-schematic" when i + 1 < args.Length: importSchematic = Path.GetFullPath(args[++i]); break;
        case "--repoint-question" when i + 1 < args.Length: repointQuestion = args[++i]; break;
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
        case "--click" or "--drag" or "--drag-hold" or "--key" or "--shot" or "--rclick" or "--menu" or "--type" or "--button" or "--hover" or "--wait"
            or "--unit" or "--rename-stackup" or "--rename-tech" or "--kit-row" or "--c3d-pick"
            when i + 1 < args.Length:
            steps.Add((args[i], args[++i]));
            break;
        default: cdd = args[i]; break;
    }
}
if (args.Contains("--film-prompt") && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    var prompt = args.Contains("--film-level")
        ? CircuitRF.Ui.Views.Dialogs.ResistorFilmPromptDialog.LevelForm(["Nitride", "Line Marker", "New layer…"], ["Metal1", "TFR Isolation", "GaAs"], "TFR Isolation")
        : new CircuitRF.Ui.Views.Dialogs.ResistorFilmPromptDialog();
    prompt.Show();
    Dispatcher.UIThread.RunJobs();
    prompt.CaptureRenderedFrame()!.Save(output);
    Console.WriteLine($"wrote {output}");
    return 0;
}
if ((ipc2581Steps ?? ipc2581Report) is { } ipcFile && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return ipc2581Steps is not null ? ShowIpc2581Steps(ipcFile, output) : ShowIpc2581Report(ipcFile, output, sizeGiven ? width : 760, sizeGiven ? height : 420);
}
if (args.SkipWhile(a => a != "--run-messages").Skip(1).FirstOrDefault() is { } runMessagesCsch && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return ShowRunMessages(Path.GetFullPath(runMessagesCsch), output, sizeGiven ? width : 900, sizeGiven ? height : 320);
}
if (args.Contains("--messages") && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    if (dark && Application.Current is { } darkApp) darkApp.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    return ShowMessagesPanel(output, sizeGiven ? width : 700, sizeGiven ? height : 260);
}
if (dxfMappingDialog is not null && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return ShowDxfMappingDialog(dxfMappingDialog, dxfMappingTech ?? "pcb-2layer_RO4350B_20mil_1oz", dxfMappingMap, output, width, height, sizeGiven);
}
if (planarMemoryDialog && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return ShowPlanarMemoryDialog(output);
}
if (unsavedSetupDialog && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    var dialog = new CircuitRF.Ui.Views.Dialogs.SaveChangesDialog(
        "'Setup.cem' has unsaved changes, and a 3D view takes its ports from the setup as saved. Save them first?",
        dontSaveLabel: "Use Saved", title: "Unsaved EM Setup");
    dialog.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
    dialog.CaptureRenderedFrame()!.Save(output);
    return 0;
}
if (cellOwnValues is { } ownCsch && select is { } ownInstance && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return ShowCellOwnValues(ownCsch, ownInstance, output);
}
// --em-run-settings <file.cemr>: brief-cosim-pattern-2 R-cp2-6 — the settings a saved EM result was made with, what differs from its setup marked.
if (emRunSettings is { } runSettingsRecord && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    var settingsDialog = CircuitRF.Ui.Views.Dialogs.EmRunSettingsDialog.Build(runSettingsRecord);
    settingsDialog.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); settingsDialog.UpdateLayout(); }
    settingsDialog.CaptureRenderedFrame()!.Save(output);
    Console.WriteLine($"wrote {output}");
    return 0;
}
if ((analysisDialog ?? emSolvesDialog) is { } sdl6Csch && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return analysisDialog is not null ? ShowAnalysisDialog(sdl6Csch, select, output) : ShowEmSolvesDialog(sdl6Csch, output);
}
// --settings-solvers: the real Solvers tab, on whatever CRF_STATE_DIR holds (so a scratch install shows its own row).
if (settingsSolvers && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    if (dark && Application.Current is { } solverApp) solverApp.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    return ShowSettingsSolvers(output, sizeGiven ? width : 820, sizeGiven ? height : 1000);
}
if (settingsGeneral && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    if (dark && Application.Current is { } darkApp) darkApp.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    return ShowSettingsGeneral(output, sizeGiven ? width : 0, sizeGiven ? height : 0);
}
if (reviewPanel is not null && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    return ShowReviewPanel(reviewPanel, output, sizeGiven ? width : 420, sizeGiven ? height : 760);
}
if (netsPanel is not null && output is not null)
{
    AppBuilder.Configure<SnapshotApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
    if (dark && Application.Current is { } netsApp) netsApp.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    return ShowSeededNetsPanel(netsPanel, output, sizeGiven ? width : 320, sizeGiven ? height : 460);
}
bool vendorMode = projectTree is not null || palette is not null || schematic is not null || archive is not null || manageLibraries is not null || addVendorLibrary is not null || exportToLibrary is not null || importSchematic is not null || gdsfactory is not null || csym is not null || repointQuestion is not null;
if ((cdd is null && c3dSetups is null && c3dSetupPanel is null && signal.Count == 0 && snp is null && !standalone && tuning is null && optimizer is null && yieldCsch is null && clay is null && cem is null && ctech is null && cmc is null && image is null && exportData is null && !vendorMode) || output is null || view is not ("canvas" or "inspector" or "display"))
{
    Console.Error.WriteLine("usage: UiSnapshot <file.cdd> -o <out.png> [--view canvas|inspector|display] [--plot n] [--size WxH] "
                          + "[--results <dir>] [--click x,y] [--drag x0,y0:x1,y1] [--key <Key>] [--shot <png>] ...\n"
                          + "       UiSnapshot --signal \"<carrier>\" ... -o <out.png> [--tab n] [--fc <expr>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --snp <file.sNp> -o <out.png> [--size WxH] [steps]\n"
                          + "       UiSnapshot --standalone [--iq <file>] -o <out.png> [--tab n] [--size WxH] [steps]\n"
                          + "       UiSnapshot --tuning <file.csch> -o <out.png> [--size WxH] [steps]\n"
                          + "       UiSnapshot --optimizer <file.csch> -o <out.png> [--optimize] [--size WxH] [steps]\n"
                          + "       UiSnapshot --yield <file.csch> -o <out.png> [--mode sensitivity|…] [--run] [--by entry|port|instance] [--select <key>] [--expand <label>] [--exclude <instance>]... [--snp-off] [--size WxH] [steps]\n"
                          + "       UiSnapshot --clay <file.clay> -o <out.png> [--hide <layer>] [--lock <layer>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --cem <file.cem> -o <out.png> [--size WxH] [steps]\n"
                          + "       UiSnapshot --c3d-setup-panel <file.c3d> -o <out.png> [--check-component-ports] [--size WxH] [steps]\n"
                          + "       UiSnapshot --ctech <file.ctech> -o <out.png> [--tab n] [--unit <unit>] [--rename-stackup old=new] [--rename-tech <name>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --cmc <file.cmc|new:Passive|new:Active> -o <out.png> [--import <data>] [--step <name>] [--run <key>] [--size WxH] [steps]\n"
                          + "       UiSnapshot --export-data <results dir> -o <out.png> [--button <format label>] [--size WxH] [steps]\n"
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
    if (exportToLibrary is not null) OpenExportToLibrary(exportToLibrary, exportLibrary);
    if (importSchematic is not null) OpenImportSchematic(importSchematic);
    if (repointQuestion is not null)
    {
        CircuitRF.Ui.Views.Dialogs.RepointCopyDialog.Build(repointQuestion, repointQuestion + "_2", 3, c => Console.WriteLine($"answer: {c}")).Show(host);
        Pump();
    }
    if (gdsfactory is not null && !OpenGdsfactory(gdsfactory)) return 2;
    if (projectTreeTool is { } treeTool)
    {
        if (filterAllOff) treeTool.FilterState.AllOn = false;
        if (revealPath is not null)
        {
            Console.WriteLine($"reveal {revealPath}: {(treeTool.Reveal(revealPath) ? "selected " + treeTool.SelectedItem?.RelativePath : "not listed")}");
            for (int k = 0; k < 6; k++) { Pump(); Thread.Sleep(10); }
            Console.WriteLine($"focus {host.FocusManager?.GetFocusedElement()?.GetType().Name ?? "none"}");
        }
        if (filterOpen && host.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Flyout is not null
                && ToolTip.GetTip(b) as string == "Filter categories") is { } filterButton)
        {
            filterButton.Flyout!.ShowAt(filterButton);
            Pump();
        }
    }
}
else if (c3dSetups is not null)
{
    // The 3D view's setup list (the Analyses panel's cards) and its Properties panel on one editor view model, results read
    // from the workspace's results/ folder as WorkspaceViewModel.EmResultsRoot gives them.
    string? c3dCws = CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(c3dSetups));
    // What the editor posts goes to a real Messages panel as well, drawn to <out>.messages.png.
    var c3dMessages = new MessagesTool();
    var c3dVm = new CircuitRF.Ui.ThreeD.C3dEditorViewModel(c3dSetups, CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(c3dSetups),
        CircuitRF.Ui.Viewer3D.Viewer3DBackends.Create, () => c3dCws, a => Dispatcher.UIThread.Post(a))
    {
        ResultsRootProvider = c3dCws is null ? null : () => Path.Combine(Path.GetDirectoryName(c3dCws)!, "results"),
        PostMessage = (text, warning) =>
        {
            Console.WriteLine($"{(warning ? "warning" : "info")}: {text}");
            c3dMessages.Post(warning ? MessageLevel.Warning : MessageLevel.Info, text);
        },
    };
    bool portsChecked = false;
    c3dVm.LayoutPortsChecked += () => portsChecked = true;
    c3dVm.RestoreActiveSetup(null);
    c3dShown = c3dVm;
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
    foreach (string name in c3dUntick)
        (c3dVm.Tree.SelectMany(g => g.Items).FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException($"--untick: no tree row named '{name}'")).IsVisible = false;
    if (c3dRow is not null)
    {
        static IEnumerable<CircuitRF.Ui.ThreeD.C3dTreeItem> Walk(CircuitRF.Ui.ThreeD.C3dTreeItem t) => t.Children.SelectMany(Walk).Prepend(t);
        c3dVm.SelectedTreeItem = c3dVm.Tree.SelectMany(g => g.Items).SelectMany(Walk).FirstOrDefault(t => t.Name == c3dRow)
                                 ?? throw new InvalidOperationException($"--c3d-row: no tree row named '{c3dRow}'");
    }
    for (int k = 0; k < 100; k++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    // brief-port-width-planar-and-c3d R-pw1-4 — the placed layouts' ports are checked off the UI thread after adoption.
    for (var w = System.Diagnostics.Stopwatch.StartNew(); !portsChecked && c3dVm.Document.Ports.Count > 0 &&
                                                         w.Elapsed < TimeSpan.FromSeconds(30);)
    { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    if (output is not null && c3dMessages.Messages.Count > 0)
    {
        string messagesPng = Path.ChangeExtension(output, ".messages.png");
        var mw = new Window { Width = 900, Height = 200, Content = new CircuitRF.Ui.Views.Messages.MessagesView { DataContext = c3dMessages } };
        mw.Show();
        for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); mw.UpdateLayout(); }
        mw.CaptureRenderedFrame()!.Save(messagesPng);
        Console.WriteLine($"wrote {messagesPng}");
    }
    c3dVm.Properties.Reload();
    var c3dPanel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
    var cards = new CircuitRF.Ui.Views.ThreeD.C3dSetupAnalysesView { DataContext = c3dVm };
    var props = new CircuitRF.Ui.Views.ThreeD.C3dPropertiesView { DataContext = c3dVm.Properties };
    Grid.SetColumn(props, 1);
    c3dPanel.Children.Add(cards);
    c3dPanel.Children.Add(props);
    // --editor: the whole 3D editor view (toolbar, Object Tree, canvas) instead of the cards and Properties panel.
    Control c3dContent = c3dEditor
        ? new CircuitRF.Ui.Views.ThreeD.C3dEditorView { DataContext = new CircuitRF.Ui.ThreeD.C3dEditorDocument(c3dVm) }
        : c3dPanel;
    host = new Window { Width = sizeGiven ? width : 900, Height = sizeGiven ? height : 700, Content = c3dContent };
    host.Show();
    Pump();
    if (args.Contains("--mesh-plane-panel") && c3dContent is CircuitRF.Ui.Views.ThreeD.C3dEditorView c3dEditorView)
    {
        var planeButton = c3dEditorView.FindControl<Button>("MeshPlaneButton")!;
        Console.WriteLine($"mesh available: {c3dVm.Viewer.CanShowMesh}");
        planeButton.Flyout!.ShowAt(planeButton);
        for (var w = System.Diagnostics.Stopwatch.StartNew(); w.Elapsed < TimeSpan.FromSeconds(8) && !c3dVm.Viewer.MeshText.StartsWith("Mesh:", StringComparison.Ordinal);)
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Console.WriteLine($"mesh plane on: {c3dVm.Viewer.MeshPlaneEnabled}, mesh shown: {c3dVm.Viewer.ShowMesh}; {c3dVm.Viewer.MeshText}");
        Pump();
    }
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
    layoutVm.ShowLayerList = layerList;
    foreach (var (name, hide) in layerToggles)
    {
        // The Ports row (brief-dfr33-1) governs every port label whatever its layer; it is not one of LayerRows.
        if (name == "Ports")
        {
            if (hide) layoutVm.PortsVisible = false; else layoutVm.PortsSelectable = false;
            continue;
        }
        var row = layoutVm.LayerRows.FirstOrDefault(r => r.Name == name);
        if (row is null)
        {
            Console.Error.WriteLine($"no layer '{name}'; the layer list has: {string.Join(", ", layoutVm.LayerRows.Select(r => r.Name))}");
            return 2;
        }
        if (hide) row.Visible = false; else row.Selectable = false;
    }
    Control layoutContent = new LayoutEditorView { DataContext = new LayoutDocument(Path.GetFileName(clay), layoutVm, clay) };
    if (layoutInspector)
    {
        // The Properties Inspector beside the canvas, on the editor's own selection, as the shell docks it.
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(layoutVm);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300") };
        grid.Children.Add(layoutContent);
        var inspector = new CircuitRF.Ui.Views.Properties.LayoutShapePropertiesView { DataContext = props };
        Grid.SetColumn(inspector, 1);
        grid.Children.Add(inspector);
        layoutContent = grid;
    }
    host = new Window { Width = width, Height = height, Content = layoutContent };
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
    if (netsHighlight is not null || netsFilter is not null)
    {
        // The real panel on the editor, as the dock shows it, its rows chosen in the list. The canvas is captured as -o.
        if (output is null) return 2;
        ShowNetsPanelOn(layoutVm, netsFilter, netsHighlight, netsDim, netsZoom, Path.ChangeExtension(output, ".nets.png"));
        Pump();
    }
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
else if (c3dSetupPanel is not null)
{
    // brief-emb-1 R-emb1-8 — a .c3d's (only, or first EM) embedded setup in the real EM Setup panel, as the 3D editor opens it,
    // with its Component ports list over the document's placed layouts; --check-component-ports presses Check.
    var c3dDoc = CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(c3dSetupPanel);
    string? panelCws = CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(c3dSetupPanel));
    var embedded = CircuitRF.Design.ThreeD.C3dSetups.Read(c3dDoc).First(s => s.Setup is { IsThermal: false });
    var elaboration = new CircuitRF.Design.ThreeD.C3dElaborator().Elaborate(c3dDoc, c3dSetupPanel, panelCws);
    var panelVm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(c3dSetupPanel, embedded.Setup!, embedded: true)
    {
        ComponentPortsContext = new CircuitRF.Ui.Layout.Em.EmComponentPortsContext(() => c3dDoc, () => c3dSetupPanel, () => panelCws, () => elaboration,
                                                                                    (text, warning) => Console.WriteLine($"message ({(warning ? "warning" : "info")}): {text}")),
    };
    if (componentPortsCheck)
    {
        // Check runs off the UI thread and finishes on it: pump until it has.
        var checking = panelVm.CheckComponentPortsCommand.ExecuteAsync(null);
        while (!checking.IsCompleted) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }
    foreach (var row in panelVm.ComponentPortRows)
        Console.WriteLine($"{row.Refdes} [{(row.IsIncluded ? "x" : " ")}] {row.PadsText}{(row.IsRefused ? " (left out)" : "")}");
    Console.WriteLine($"status: {panelVm.ComponentPortsStatus}");
    host = new Window { Width = sizeGiven ? width : 700, Height = sizeGiven ? height : 2000,
                        Content = new EmSetupEditorView { DataContext = new CircuitRF.Ui.Layout.Em.EmSetupDocument(embedded.Name, panelVm, c3dSetupPanel) } };
    host.Show();
    Pump();
    if (host.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "ComponentPortsSection") is { } section)
        Console.WriteLine($"Component ports section at {section.TranslatePoint(default, host)?.Y:0} px, {section.Bounds.Height:0} px tall");
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
        if (cmcLibraryButton)
        {
            composer.PublishToLibrary = (_, _) => Task.CompletedTask;
            composer.RefreshExportToLibrary();
            Console.WriteLine($"export to library: {(composer.ExportToLibraryBlocked is null ? "live" : "greyed")} — {composer.ExportToLibraryTip}");
        }
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
else if (exportData is not null)
{
    // The Export Data dialog as the Data Display opens it, over a results folder.
    host = new CircuitRF.Ui.Views.Dialogs.DataExporterDialog(new CircuitRF.Ui.DataDisplay.ViewModels.DataExporterViewModel(exportData));
    if (sizeGiven) { host.Width = width; host.Height = height; }
    host.Show();
}
else if (tuning is not null)
{
    // The Tuning window as the shell hosts it: a TuningTool whose panel follows one schematic.
    var (model, _, _) = SchematicPersistence.LoadFromFile(tuning);
    tuningTool = new TuningTool();
    // Start runs a real session on the file's circuit, with no display to publish to (brief-sdl-7: so a running
    // session's toolbar — Solve EM here — can be looked at).
    string tuningFile = tuning;
    tuningTool.Panel.CreateSession = (_, analyses) => CircuitRF.Ui.Tuning.TuneSession.ForCircuit(
        CircuitRF.Design.Circuit.PreparedCircuit.FromSchematic(tuningFile), null, a => Dispatcher.UIThread.Post(a), analyses);
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
else if (yieldCsch is not null)
{
    // The Yield panel — or the Sensitivity panel, the same view over a one-mode panel — as the shell hosts it, its
    // circuit prepared as the Optimizer's branch prepares it, its catalog discovered in the file's workspace.
    var (model, _, _) = SchematicPersistence.LoadFromFile(yieldCsch);
    var vm = new SchematicViewModel(model, messageSink: null);
    string? ws = CircuitRF.Design.Workspace.WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(yieldCsch));
    CircuitRF.Ui.ViewModels.Dock.YieldPanelTool tool = yieldMode == "sensitivity"
        ? new CircuitRF.Ui.ViewModels.Dock.SensitivityTool() : new CircuitRF.Ui.ViewModels.Dock.YieldTool();
    var panel = tool.Panel;
    panel.PostToUi = a => Dispatcher.UIThread.Post(a);
    panel.Discover = t => CircuitRF.Design.Optimization.TunableCatalog.Discover(t.EditModel, DiskCellResolver.Instance, ws);
    panel.PrepareCircuit = t =>
    {
        string name = Path.GetFileNameWithoutExtension(yieldCsch);
        var extracted = NetExtractor.Extract(t.EditModel, name, DiskCellResolver.Instance);
        string text = CircuitRF.Core.Netlist.CnlWriter.Write(extracted.TestBench, extracted.Library, "yield snapshot");
        return CircuitRF.Design.Circuit.PreparedCircuit.FromText(text, ws ?? Path.GetDirectoryName(yieldCsch), ws, name, extracted);
    };
    panel.SourcePathFor = _ => yieldCsch;
    // The placed library parts, as the shell asks for them (brief vendorlib-13).
    panel.LibraryPartsFor = t => panel.PrepareCircuit!(t) is { Tb: { } ltb, Lib: { } llib }
        ? CircuitRF.Design.Statistics.LibraryParts.Placed(ltb, llib) : null;
    // The shell's doors that open another panel: present, so their buttons show as the shell shows them.
    panel.OpenYieldOnRanked = n => Console.WriteLine($"Run Yield on these: {n} untoleranced");
    // The display button writes <design>.sens.cdd as the shell does; snapshot that file with the .cdd mode.
    panel.OpenSensitivityDisplay = (path, data, level, responses) =>
    {
        string cddPath = Path.ChangeExtension(Path.GetFullPath(path), ".cdd");
        var doc = CircuitRF.Render.DataDisplay.ContributionParetoPreset.Document(data, Path.GetFullPath(path),
            CircuitRF.Design.Statistics.SensitivityRun.ResultGroup, level.ToString().ToLowerInvariant(), responses);
        if (doc is null) { Console.WriteLine("display: nothing ranked"); return Task.CompletedTask; }
        File.WriteAllText(cddPath, System.Text.Json.JsonSerializer.Serialize(doc, CircuitRF.Render.DataDisplay.DataDisplayJson.Options));
        Console.WriteLine($"display: wrote {cddPath}");
        return Task.CompletedTask;
    };
    panel.SetActiveSchematic(vm, Path.GetFileName(yieldCsch));
    if (yieldMode is not null and not "sensitivity")
        panel.Mode = yieldMode switch
        {
            "montecarlo" => CircuitRF.Ui.Yield.YieldMode.MonteCarlo, "corners" => CircuitRF.Ui.Yield.YieldMode.Corners,
            "centering" => CircuitRF.Ui.Yield.YieldMode.Centering, "doe" => CircuitRF.Ui.Yield.YieldMode.Doe,
            _ => CircuitRF.Ui.Yield.YieldMode.Yield,
        };
    foreach (var inst in yieldExclude) panel.SetSnpIncluded(inst, false);
    if (yieldSnpOff) panel.SensSnp = false;
    host = new Window { Width = sizeGiven ? width : 460, Height = sizeGiven ? height : 720,
                        Content = new CircuitRF.Ui.Views.Yield.YieldToolView { DataContext = tool } };
    host.Show();
    Pump();
    if (yieldRun)
    {
        panel.RunCommand.Execute(null);
        var until = DateTime.UtcNow.AddSeconds(300);
        while (panel.IsRunActive && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Pump();
    }
    if (yieldBy is not null)
        panel.SensLevel = yieldBy switch
        {
            "port" => CircuitRF.Ui.Yield.SensitivityLevel.Port, "instance" => CircuitRF.Ui.Yield.SensitivityLevel.Instance,
            _ => CircuitRF.Ui.Yield.SensitivityLevel.Entry,
        };
    if (yieldDisplay) panel.OpenDisplayCommand.Execute(null);
    if (yieldGenerate) { panel.GenerateCornersCommand.Execute(null); Pump(); }
    if (yieldExpand is not null && panel.SensRows.FirstOrDefault(r => r.Label == yieldExpand) is { CanExpand: true } open)
        open.ToggleExpandCommand.Execute(null);
    if (select is not null)   // --select, shared with the schematic branch
    {
        if (panel.SensRows.FirstOrDefault(r => r.Label == select || r.Keys.Contains(select)) is { } row) panel.SelectedSensRow = row;
        else if (panel.SnpBlocks.FirstOrDefault(b => b.Instance == select) is { } block) panel.SelectedSnpBlock = block;
        else Console.Error.WriteLine($"--select: nothing named '{select}'");
    }
    Pump();
    foreach (var b in panel.SnpBlocks) Console.WriteLine($"block {(b.IsIncluded ? "[x]" : "[ ]")}{(b.IsEnabled ? "" : " (disabled)")} {b.Instance}: {b.Summary}");
    Console.WriteLine($"plan: {panel.SensPlanText}");
    foreach (var r in panel.SensRows)
        Console.WriteLine($"row {new string(' ', 2 * r.Depth)}{r.Label} | {r.Basis} | {r.DeltaText} | {r.ShareText}{(r.IsExcluded ? " | excluded" : "")}{(r.StatusTip is { } tip ? " | " + tip.Replace('\n', ' ') : "")}");
    foreach (var g in panel.Goals) Console.WriteLine($"goal {g.Name}: {g.SensText} {g.EstimateText}");
    if (panel.SnpTile is { } tile) Console.WriteLine($"tile {tile.Instance}: {tile.Ports} ports, tied={tile.IsTied}, {tile.TieTip}, max {tile.LegendMaxText}");
    Console.WriteLine($"status: {panel.StatusText}");
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
// A relative source reference resolves beside the .cdd first and then against a workspace's results/ folder, which
// in the application the workspace supplies (EMB-5 R-emb5-14). Here the results folder is --results, else the
// results/ of the workspace the .cdd belongs to — exactly as the application resolves it, so a display that would
// open empty there opens empty here too. Defaulting to the .cdd's own folder hid exactly that.
string? resultsRoot = results is not null ? Path.GetFullPath(results)
    : CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(Path.GetFullPath(cdd!))!) is { } cddCws
        ? CircuitRF.Design.Results.ResultsWriter.ResultsDirectory(Path.GetDirectoryName(cddCws)!)
        : null;
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
        case "--drag-hold":
        {
            // The same gesture, not released, so a capture shows its live chrome.
            var ends = arg.Split(':');
            Point a = PointOf(ends[0]), b = PointOf(ends[1]);
            host.MouseDown(a, MouseButton.Left, RawInputModifiers.None);
            for (int k = 1; k <= 10; k++)
            {
                host.MouseMove(a + (b - a) * (k / 10.0), RawInputModifiers.LeftMouseButton);
                Pump();
            }
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
        case "--c3d-pick":
        {
            // The canvas cannot draw headless, but picking is on the CPU: hover a pixel that shows the object, then the
            // viewer's own Click — the path a real click takes, an armed draw tool and an armed terminal Pick included.
            var v = (c3dShown ?? throw new InvalidOperationException("--c3d-pick needs --c3d-setups")).Viewer;
            var obj = v.Scene.Objects.FirstOrDefault(o => o.Name == arg)
                      ?? throw new InvalidOperationException($"--c3d-pick: no object '{arg}'; the scene has {string.Join(", ", v.Scene.Objects.Select(o => o.Name))}");
            var (vw, vh) = v.ViewSize;
            bool hit = false;
            for (int y = 0; y < vh && !hit; y += 2)
                for (int x = 0; x < vw && !hit; x += 2)
                {
                    var (id, face) = CircuitRF.Render.Scene3D.Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, x, y, vw, vh, v.View.Visible);
                    if (id != obj.Id) continue;
                    v.Hover(x, y);
                    // OnPicked is internal (the GPU ID pass's answer); the tool has no InternalsVisibleTo, so by reflection.
                    typeof(CircuitRF.Ui.Viewer3D.Viewer3DViewModel).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Single(m => m.Name == "OnPicked" && m.GetParameters().Length == 5)
                        .Invoke(v, [id, face, System.Numerics.Vector3.Zero, true, null]);
                    v.Click(false);
                    hit = true;
                }
            // brief-dfr34-1 — a port inside the board is never the front pixel: hover its projected centre and let the viewer's
            // own ranking (a port first) decide what the click takes.
            if (!hit && obj.Kind == CircuitRF.Render.Scene3D.Scene3DKind.Port && v.Scene.FeaturesOf(obj.Id) is { Table: { } table } f)
            {
                var corners = Enumerable.Range(0, table.Vertices.Length).Select(f.Vertex).ToList();
                var c = new CircuitRF.Engine.Em3d.Point3(corners.Average(p => p.X), corners.Average(p => p.Y), corners.Average(p => p.Z));
                var (px, py, front) = v.View.Camera.Project(v.Scene.ToLocal(c.X, c.Y, c.Z), vw, vh);
                if (front)
                {
                    v.Hover(px, py);
                    var (id, face) = CircuitRF.Render.Scene3D.Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, px, py, vw, vh, v.View.Visible);
                    typeof(CircuitRF.Ui.Viewer3D.Viewer3DViewModel).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Single(m => m.Name == "OnPicked" && m.GetParameters().Length == 5)
                        .Invoke(v, [id, face, System.Numerics.Vector3.Zero, id != 0, null]);
                    v.Click(false);
                    hit = true;
                    Console.WriteLine($"--c3d-pick: '{arg}' is behind other content; hovered its centre at ({px:F0}, {py:F0}): selection {string.Join(", ", v.Selection.Select(s => v.Scene.Object(s.Object)?.Name))}");
                }
            }
            if (!hit) throw new InvalidOperationException($"--c3d-pick: '{arg}' is not visible from the camera");
            break;
        }
        case "--hover":
            host.MouseMove(PointOf(arg), RawInputModifiers.None);
            break;
        case "--wait":
        {
            // Real time passes with the dispatcher pumped, so a timer the view runs (snap tracking's dwell) fires.
            var until = DateTime.UtcNow.AddMilliseconds(int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
            while (DateTime.UtcNow < until) { Thread.Sleep(20); Pump(); }
            break;
        }
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
            // Name=text with no dialog open types into that named box of the hosted view itself (the 3D Properties panel's).
            var dialog = TopDialog() ?? (imageVm is not null || vendorMode || cem is not null || snp is not null || arg.IndexOf('=') > 0
                ? host : throw new InvalidOperationException("--type: no dialog is open"));
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
            var dialog = TopDialog() ?? (imageVm is not null || vendorMode || cem is not null || snp is not null || exportData is not null || c3dShown is not null ? host : throw new InvalidOperationException("--button: no dialog is open"));
            var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            // "Label#n": the n-th button with that label (1-based), for a table with one per row.
            string label = arg;
            int nth = 1;
            if (arg.LastIndexOf('#') is > 0 and var hash && int.TryParse(arg[(hash + 1)..], out int n)) { label = arg[..hash]; nth = n; }
            // A toolbar button's content is an icon beside a TextBlock, so its label is matched too.
            var matches = buttons.Where(b => b.Content is string c && c == label).ToList();
            if (matches.Count == 0) matches = [.. buttons.Where(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label))];
            var button = matches.Count >= nth ? matches[nth - 1]
                : throw new InvalidOperationException($"--button '{arg}': the dialog has {string.Join(" | ", buttons.Select(b => b.Content))}");
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
    if (c3dShown?.SetupEditor?.ViewModel is { IsStaticSetup: true } esVm)
        Console.WriteLine($"{kind} {arg}: terminals [{string.Join(" | ", esVm.TerminalRows.Select(r => $"{r.Name}={r.Net}{(r.IsPicking ? " (Pick armed)" : "")}"))}], "
                        + $"ground '{esVm.Ground3DText}', bodies '{esVm.BodiesStatus}', view selection [{string.Join(",", c3dShown.Viewer.SelectedObjects().Select(o => o.Name))}], "
                        + $"status '{c3dShown.StatusMessage}', dialog {(TopDialog() is { } td ? "open" : "closed")}");
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

    if (projectTree is not null)
    {
        var tree = projectTreeTool = new CircuitRF.Ui.ViewModels.Dock.ProjectTreeTool();
        tree.SetWorkspace(projectTree);
        return new Window { Width = sizeGiven ? width : 320, Height = sizeGiven ? height : 360,
                            Content = new CircuitRF.Ui.Views.ProjectTree.ProjectTreeView { DataContext = tree } };
    }
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
        var doc = new CircuitRF.Ui.Schematic.SchematicDocument(Path.GetFileNameWithoutExtension(schematic), vm, schematic)
            { Hierarchy = new SnapshotHierarchy() };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,360") };
        grid.Children.Add(new CircuitRF.Ui.Views.Content.SchematicView { DataContext = doc });
        var pe = new CircuitRF.Ui.Views.ParameterEditor.ParameterEditorView { DataContext = editor };
        Grid.SetColumn(pe, 1);
        grid.Children.Add(pe);
        var window = new Window { Width = sizeGiven ? width : 1200, Height = sizeGiven ? height : 720, Content = grid };
        window.Opened += (_, _) => Dispatcher.UIThread.Post(doc.RequestZoomToFit);
        // --edit-text: the inline editor open on the schematic's first text, as a double-click opens it.
        if (args.Contains("--edit-text") && model.CanvasObjects.OfType<EditableText>().FirstOrDefault() is { } editedText)
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                var view = grid.Children.OfType<CircuitRF.Ui.Views.Content.SchematicView>().First();
                typeof(CircuitRF.Ui.Views.Content.SchematicView).GetMethod("OnCanvasTextDoubleTapped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(view, [null, editedText]);
            }, DispatcherPriority.Background);
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
            CircuitRF.Design.VendorLibraries.VendorLibraryRefs.DefaultName(folder), build.SummaryLines(), build.Signature);
    Pump();
}

void OpenExportToLibrary(string cell, string? library)
{
    var vm = new CircuitRF.Ui.ViewModels.ExportToLibraryViewModel(cell, [], library);
    // brief vendorlib-15: rows of Builds for other platforms, each added through the dialog's own command.
    foreach (var (rid, path) in exportBuilds)
    {
        vm.AddBuildCommand.Execute(null);
        vm.BuildRows[^1].Rid = rid;
        vm.BuildRows[^1].Path = path;
    }
    if (exportProtect is not null) vm.CompileNetlist = true;
    if (exportEncapsulate is not null) vm.ProtectModel = true;
    if (exportCompileModels) vm.CompileModels = true;
    _ = CircuitRF.Ui.Views.Dialogs.ExportToLibraryDialog.AskAsync(host, vm);
    Pump();
    // The plan's continuation runs on this dispatcher, so it is pumped for, never waited on.
    for (int k = 0; k < 1500 && (vm.IsPlanning || !vm.Replanned.IsCompleted); k++) { Pump(); Thread.Sleep(20); }
    Pump();
    bool runs = exportProtect == "run" || exportEncapsulate == "run";
    if (runs && vm.CanExport)
    {
        var running = vm.Export(replace: false);
        for (int k = 0; k < 6000 && !running.IsCompleted; k++) { Pump(); Thread.Sleep(20); }
        var done = running.Result;
        if (done.Succeeded && (done.Builds.Count > 0 || done.Protection is not null)) vm.ShowBuilds(done);
        Console.WriteLine($"export: {(done.Succeeded ? done.Message : done.Refusal)}");
        Pump();
    }
    foreach (var l in vm.ProtectLines) Console.WriteLine($"  protect: {l}");
    // The Protected model rows sit low in the left column: bring them into view for the capture.
    if ((exportProtect is not null || exportEncapsulate is not null || exportProtectView || exportCompileModels) && TopDialog() is { } shown
        && shown.GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().FirstOrDefault(c => c.Name == "ProtectModelBox") is { } box)
    {
        box.BringIntoView();
        Pump();
        if (box.FindAncestorOfType<Avalonia.Controls.ScrollViewer>() is { } sv)
        {
            if (runs) sv.ScrollToEnd();
            else sv.Offset = sv.Offset.WithY(Math.Max(0, sv.Offset.Y + 120));
        }
        Pump();
    }
    Console.WriteLine($"plan: {vm.RefusalLines.Count} refusal(s), {vm.CopyLines.Count} copy line(s), symbol {vm.CurrentSymbol?.Value}, can export {vm.CanExport}");
    foreach (var r in vm.RefusalLines) Console.WriteLine($"  refusal: {r}");
}

void OpenImportSchematic(string file)
{
    // Into a throwaway folder: the dialog only reads until Import is pressed.
    string into = Path.Combine(Path.GetTempPath(), "crf-snap-import-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(into);
    var vm = new CircuitRF.Ui.ViewModels.ImportSchematicViewModel(file, into);
    _ = new CircuitRF.Ui.Views.Dialogs.ImportSchematicDialog(vm).ShowDialog<object?>(host);
    for (int k = 0; k < 1500 && !vm.Loaded.IsCompleted; k++) { Pump(); Thread.Sleep(20); }
    for (int k = 0; k < 50; k++) { Pump(); Thread.Sleep(20); }
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

ContextMenu? OpenContextMenu()
    => layoutCanvas?.ContextMenu is { IsOpen: true } m ? m
     : host.GetVisualDescendants().OfType<SchematicCanvas>().FirstOrDefault()?.ContextMenu is { IsOpen: true } s ? s : null;

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
    if (xy.StartsWith('#') && host.GetVisualDescendants().OfType<SchematicCanvas>().FirstOrDefault() is { } canvas
        && host.GetVisualDescendants().OfType<CircuitRF.Ui.Views.Content.SchematicView>().FirstOrDefault()?.DataContext
               is CircuitRF.Ui.Schematic.SchematicDocument sdoc)
    {
        // The middle of the part's pins, which lies on its body.
        var model = sdoc.ActiveViewModel.EditModel;
        var comp = model.Components.FirstOrDefault(c => c.InstanceName == xy[1..])
                   ?? throw new InvalidOperationException($"{xy}: no such part");
        var pins = comp.CellRef is null
            ? Enumerable.Range(0, comp.EffectivePortCount).Select(comp.GetPortWorldCoord).ToList()
            : CellSymbolResolver.Resolve(comp.ExternalSymbolRef!, model.SchematicDirectory, model.HostWorkspaceRoot).Symbol!.Pins
                  .Select(p => (X: comp.X + p.LocalX, Y: comp.Y + p.LocalY)).ToList();
        var (sx, sy) = canvas.WorldToScreen(pins.Average(p => p.X), pins.Average(p => p.Y));
        return canvas.TranslatePoint(new Point(sx, sy), host)!.Value;
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

// --clay ... --nets a,b [--nets-filter x] [--nets-dim] [--nets-zoom]: the real Nets panel on the editor, the named rows selected in
// its list so the highlight reaches the canvas by the path a click takes. The panel is written beside -o as <out>.nets.png.
static void ShowNetsPanelOn(LayoutEditorViewModel vm, string? filter, string? nets, bool dim, bool zoom, string path)
{
    var tool = new CircuitRF.Ui.ViewModels.Dock.NetsTool();
    tool.SetActiveLayout(vm);
    var window = new Window { Width = 320, Height = 620, Content = new CircuitRF.Ui.Views.Nets.NetsToolView { DataContext = tool } };
    window.Show();
    void Settle() { for (int k = 0; k < 4; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); } }
    Settle();
    var panel = tool.Panel;
    if (filter is not null) panel.FilterText = filter;
    Settle();
    var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "RowsList");
    foreach (string name in (nets ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var row = panel.Rows.FirstOrDefault(r => r.Name == name);
        if (row is null) { Console.Error.WriteLine($"no net '{name}'; the panel lists: {string.Join(", ", panel.Rows.Take(40).Select(r => r.Name))}"); continue; }
        list.SelectedItems!.Add(row);
    }
    panel.DimOtherCopper = dim;
    if (zoom) panel.ZoomToNetCommand.Execute(null);
    Settle();
    Console.WriteLine($"header: {panel.HeaderText}");
    foreach (var r in panel.Rows.Take(30)) Console.WriteLine($"  {r.Name,-24} {r.ShapeCount,6}  {r.LayersText}");
    Console.WriteLine($"overlay nets: {string.Join(", ", vm.Overlay?.NetHighlights.Select(h => $"{h.Net} ({h.Shapes.Count})") ?? [])}");
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
}

// --nets-panel nets|filter|selected|none|many: the panel on a seeded layout, and where its action row landed, so the row can be
// compared across the states.
static int ShowSeededNetsPanel(string seed, string path, double w, double h)
{
    LayerKey top = new(1, 0), bottom = new(2, 0);
    var view = new LayoutView { DisplayUnit = LayoutUnit.Mm };
    var vm = new LayoutEditorViewModel(view)
    {
        Technology = new Technology
        {
            Layers =
            [
                new LayerDef { Key = top, Name = "Top Copper", Color = new CircuitRF.Design.Theming.Rgba(200, 60, 60), ZOrder = 2 },
                new LayerDef { Key = bottom, Name = "Bottom Copper", Color = new CircuitRF.Design.Theming.Rgba(60, 90, 200), ZOrder = 1 },
            ],
        },
    };
    void Add(string net, LayerKey layer, long x) =>
        view.Shapes.Add(new RectShape { Layer = layer, X1 = x, Y1 = 0, X2 = x + 1000, Y2 = 1000, Net = net });
    if (seed == "many")
        for (int i = 0; i < 5000; i++) Add($"N{i}", top, i * 2000);
    else if (seed != "none")
    {
        string[] nets = ["+3V3", "GND", "N2", "N10", "N11", "RF_IN", "RF_OUT", "A_VERY_LONG_NET_NAME_THAT_DOES_NOT_FIT_ON_ONE_ROW_OF_THE_PANEL"];
        for (int i = 0; i < nets.Length; i++)
        {
            for (int k = 0; k <= i * 7 % 5; k++) Add(nets[i], top, (i * 10 + k) * 2000);
            if (i % 2 == 0) Add(nets[i], bottom, i * 2000);
        }
        view.Instances.Add(new LayoutInstance { CellRef = "Part" });
    }
    view.NotifyChanged();

    var tool = new CircuitRF.Ui.ViewModels.Dock.NetsTool();
    tool.SetActiveLayout(seed == "nolayout" ? null : vm);
    var window = new Window { Width = w, Height = h, Content = new CircuitRF.Ui.Views.Nets.NetsToolView { DataContext = tool } };
    window.Show();
    void Settle() { for (int k = 0; k < 4; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); } }
    Settle();

    var panel = tool.Panel;
    var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "RowsList");
    switch (seed)
    {
        case "filter": panel.FilterText = "rf"; break;
        case "selected":
            list.SelectedItems!.Add(panel.Rows[1]);
            list.SelectedItems!.Add(panel.Rows[3]);
            break;
        case "edit":
            // The list is replaced by a document change and by a filter; the real list box has to come back holding the same nets.
            list.SelectedItems!.Add(panel.Rows[3]);
            Settle();
            string Held() => $"list [{string.Join(", ", list.SelectedItems!.OfType<LayoutNetRow>().Select(r => r.Name))}] panel [{string.Join(", ", panel.SelectedRows.Select(r => r.Name))}] overlay [{string.Join(", ", vm.Overlay.NetHighlights.Select(n => $"{n.Net}:{n.Shapes.Count}"))}]";
            Console.WriteLine($"selected:       {Held()}");
            view.Shapes.Add(new RectShape { Layer = top, X1 = 0, Y1 = 5000, X2 = 1000, Y2 = 6000, Net = panel.SelectedRows[0].Name });
            view.NotifyChanged();
            Settle();
            Console.WriteLine($"after an edit:  {Held()}");
            panel.FilterText = "N";
            Settle();
            Console.WriteLine($"filter keeps:   {Held()}");
            panel.FilterText = "RF";
            Settle();
            Console.WriteLine($"filter hides:   {Held()}");
            panel.FilterText = "";
            Settle();
            Console.WriteLine($"filter cleared: {Held()}");
            list.SelectedItems!.Add(panel.Rows[0]);
            Settle();
            Console.WriteLine($"click again:    {Held()}");
            break;
    }
    Settle();

    Console.WriteLine($"{seed}: header '{panel.HeaderText}'; empty '{panel.EmptyText}'; rows {panel.Rows.Count:N0}; realised {list.GetRealizedContainers().Count()}; selected {panel.SelectedRows.Count}");
    foreach (var name in new[] { "FilterBox", "ZoomButton", "SelectButton", "DimCheck" })
    {
        var control = window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);
        var at = control.TranslatePoint(new Point(0, 0), window)!.Value;
        Console.WriteLine($"  {name} x={at.X:0.#} y={at.Y:0.#} w={control.Bounds.Width:0.#} h={control.Bounds.Height:0.#} enabled={control.IsEffectivelyEnabled}");
    }
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
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
static int ShowIpc2581Steps(string file, string path)
{
    var listing = CircuitRF.Design.Layout.Interchange.Ipc2581Import.ListSteps(file);
    if (listing.Refusal is { } refusal) { Console.Error.WriteLine(refusal); return 1; }
    var window = new CircuitRF.Ui.Views.Dialogs.Ipc2581StepDialog(listing.Steps!);
    window.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

static int ShowIpc2581Report(string file, string path, double w, double h)
{
    string dir = Directory.CreateTempSubdirectory("ipc2581-report-").FullName;
    var result = CircuitRF.Design.Layout.Interchange.Ipc2581Import.Import(
        file, dir, Path.GetFileNameWithoutExtension(file), new Technology { Name = "report" }, LayoutUnits.DefaultDbuPerMicron);
    if (result.Refusal is { } refusal) { Console.Error.WriteLine(refusal); return 1; }
    var tool = new MessagesTool();
    tool.Post(MessageLevel.Success, $"Imported {result.CreatedCellDirs.Count} cell(s) from the IPC-2581 file into '{Path.GetFileName(dir)}'.");
    foreach (var message in result.Messages) tool.Post(MessageLevel.Info, message);
    var window = new Window { Width = w, Height = h, Content = new CircuitRF.Ui.Views.Messages.MessagesView { DataContext = tool } };
    window.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    Console.WriteLine($"technology {result.TechPath}");
    return 0;
}

// --run-messages <csch>: a real run of a schematic, as Simulate posts it to a real Messages panel, the plan first and the run's
// own row last; and the dialog its radiation-pattern row opens, to <out>.details.png.
static int ShowRunMessages(string csch, string path, double w, double h)
{
    var circuit = CircuitRF.Design.Circuit.PreparedCircuit.FromSchematic(csch);
    var request = new CircuitRF.Design.Circuit.CircuitEvaluationRequest();
    var plan = CircuitRF.Design.Circuit.CircuitEvaluation.Plan(circuit, request);
    var tool = new MessagesTool();
    foreach (string line in plan.Notes.Concat(plan.Lines)) tool.Post(MessageLevel.Info, line);
    var live = tool.BeginProgress($"Running '{Path.GetFileName(csch)}'");
    var result = CircuitRF.Design.Circuit.CircuitEvaluation.Evaluate(circuit, request);
    RunMessages.Post(tool, result, () => null);
    if (result.Status == CircuitRF.Design.Circuit.RunStatus.Success) live.Finish(RunMessages.OutcomeLevel(result), result.Summary, keepBar: false);
    else live.Finish(MessageLevel.Error, result.StatusMessage, keepBar: false);
    tool.Post(MessageLevel.Info, $"Finished '{Path.GetFileName(csch)}'.");
    var window = new Window { Width = w, Height = h, Content = new CircuitRF.Ui.Views.Messages.MessagesView { DataContext = tool } };
    window.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}; {result.Notes.Count} notes, {result.Notes.Count(CircuitRF.Design.Circuit.RunNotes.IsDetail)} folded");
    var folded = result.Notes.Where(CircuitRF.Design.Circuit.RunNotes.IsDetail).ToList();
    if (folded.Count > 0)
    {
        var dialog = CircuitRF.Ui.Views.Dialogs.TextConfirmDialog.Build("Radiation pattern", RunMessages.Sentence(folded), RunMessages.Text(folded), null, _ => { }, centerOnOwner: false);
        dialog.Show();
        for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
        dialog.CaptureRenderedFrame()!.Save(path + ".details.png");
    }
    return 0;
}

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

// --planar-memory-dialog: the question Simulate asks before a planar run past this machine's memory — the verdict from
// PlanarMemoryCheck on a de-embedded 36,000-unknown run with 2 GB of calibration standards, in the dialog
// WorkspaceViewModel.ConfirmEmMemory opens for it.
// --dxf-mapping-dialog <file.dxf> [--dxf-mapping-tech <id>] [--dxf-mapping-map <map>]: brief DFR38-1 — the real import up to
// its question, the real dialog on the answer it asks for, and Cancel: the temporary folder it was given stays empty.
static int ShowDxfMappingDialog(string dxf, string techId, string? map, string path, double width, double height, bool sizeGiven)
{
    var tech = CircuitRF.Design.Layout.ShippedTechnologies.Load(techId);
    string scratch = Directory.CreateTempSubdirectory("uisnapshot-dxf-").FullName;
    using var stream = File.OpenRead(dxf);
    CircuitRF.Design.Layout.Interchange.DxfImport.Import(stream, scratch, tech, 1000, rawUnits => CircuitRF.Design.Layout.Interchange.DxfUnits.Millimeters,
        resolveMapping: request =>
        {
            if (map is not null)
            {
                var (rows, refusal) = CircuitRF.Design.Layout.Interchange.DxfBoardReading.ApplyMap(map, request.Rows, tech);
                if (rows is null) { Console.WriteLine(refusal); return null; }
                request = request with { Rows = rows };
            }
            var dialog = new CircuitRF.Ui.Views.Dialogs.LayerMappingDialog("Import DXF — Layer Mapping", tech, request);
            if (sizeGiven) { dialog.Width = width; dialog.Height = height; }
            dialog.Show();
            for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
            dialog.StartPreview().GetAwaiter().GetResult();
            for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
            dialog.CaptureRenderedFrame()!.Save(path);
            Console.WriteLine($"wrote {path}");
            return null;
        });
    Console.WriteLine($"cancelled: {Directory.GetFileSystemEntries(scratch).Length} item(s) created");
    Directory.Delete(scratch, recursive: true);
    return 0;
}

static int ShowPlanarMemoryDialog(string path)
{
    const int n = 36_000, cells = 18_000;
    const long std = 2_000_000_000;
    long physical = CircuitRF.Design.Em3d.MachineMemory.PhysicalBytes;
    var size = new CircuitRF.Engine.Mom.PlanarRunSize(n, cells, false,
        CircuitRF.Engine.Mom.PlanarSystem.ResidentBytes(n, cells) + std) { StandardBytes = std };
    var verdict = CircuitRF.Design.Layout.Em.PlanarMemoryCheck.Evaluate(size, physical);
    Console.WriteLine($"{verdict.Level}: {verdict.Text}");
    var dialog = new CircuitRF.Ui.Views.Dialogs.SaveChangesDialog(
        verdict.Text ?? "", saveLabel: "Simulate Anyway", dontSaveLabel: null, cancelLabel: "Don't Simulate",
        title: "This Run May Not Fit in Memory");
    dialog.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
    dialog.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

// --analysis-dialog <csch> [--select <analysis>]: brief-sdl-6 — the Edit Analysis dialog on one of the schematic's
// analyses, as the Analyses panel opens it, with its Simulate by and EM setup row.
static int ShowAnalysisDialog(string csch, string? name, string path)
{
    var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
    model.SchematicDirectory = Path.GetDirectoryName(csch);
    var existing = name is null ? model.Analyses.First(a => a is not CircuitRF.Core.Design.ParametricSweepAnalysis)
                                : model.Analyses.First(a => a.Name == name);
    var vm = new AnalysisEditorViewModel(model, existing);
    var dialog = new CircuitRF.Ui.Views.Dialogs.AnalysisEditorDialog(vm, isEdit: true);
    dialog.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
    Console.WriteLine($"Simulate by: {vm.SimulateByOptions[vm.SimulateByIndex]}; EM setup: {vm.EmSetupOptions[vm.EmSetupIndex]} " +
                      $"(choosable: {vm.EmSetupChoosable})");
    if (vm.IsFields)
        foreach (var row in vm.FieldsBody.Drives)
            Console.WriteLine($"Drive: {row.Driver}; amplitude: '{row.Weight}'; list: {string.Join(" | ", row.Items)}; problem: {row.Problem ?? "none"}");
    dialog.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

// --em-solves-dialog <csch>: brief-sdl-6 D5 — the question Simulate asks when a run needs more than one uncached EM solve,
// with the count the run's own function takes of the schematic, in the dialog WorkspaceWindow opens for it.
// --cell-own-values <csch> --select <instance>: brief-sdl-10 D7 — Make These the Cell's Own Values' confirmation, with the
// listing of every other placement, built from the plan and the dialog the properties panel's button uses. Nothing written.
static int ShowCellOwnValues(string csch, string instance, string path)
{
    var plan = CircuitRF.Design.Optimization.CellOwnValues.Plan(csch, instance);
    Console.WriteLine(plan.Summary());
    var dialog = CircuitRF.Ui.Views.Dialogs.TextConfirmDialog.Build("Make These the Cell's Own Values",
        plan.Refusal is null ? $"Make {plan.Instance}'s values {plan.CellName}'s own?" : "Nothing was changed",
        plan.Summary(), plan.Refusal is null ? "Make Them the Cell's Own" : null, _ => { }, centerOnOwner: false);
    dialog.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
    dialog.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

static int ShowEmSolvesDialog(string csch, string path)
{
    var plan = CircuitRF.Design.Circuit.CircuitEvaluation.Plan(CircuitRF.Design.Circuit.PreparedCircuit.FromSchematic(csch),
                                                                new CircuitRF.Design.Circuit.CircuitEvaluationRequest());
    var count = CircuitRF.Design.Circuit.CircuitEvaluation.CountEmSolves(plan);
    Console.WriteLine(count.Sentence);
    var dialog = new CircuitRF.Ui.Views.Dialogs.SaveChangesDialog(
        count.Sentence + " Run them?", saveLabel: "Run", dontSaveLabel: null, cancelLabel: "Cancel", title: "EM Solves");
    dialog.Show();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); dialog.UpdateLayout(); }
    dialog.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}");
    return 0;
}

// --settings-general: Settings ▸ General on a throwaway per-user state directory, Create or update EM setup on
// (brief-sdl-5), scrolled to the Update Layout from Schematic options.
static int ShowSettingsSolvers(string path, double w, double h)
{
    // Redirecting app data would also hide CRF_STATE_DIR's installs, which are what this mode exists to show.
    if (Environment.GetEnvironmentVariable("CRF_STATE_DIR") is not { Length: > 0 })
        CircuitRF.Ui.AppDataRoot.RedirectTo(Path.Combine(Path.GetTempPath(), "crf-settings-" + Guid.NewGuid().ToString("N")[..8]));
    var view = new CircuitRF.Ui.Views.Dialogs.Em3dSolverSettingsView();
    var window = new Window { Width = w, Height = h, Content = view };
    window.Show();
    // The rows ask discovery off the UI thread, and EMerge's probe starts a Python: pump until its row has answered.
    var install = view.FindControl<Button>("EmergeInstall")!;
    var status = view.FindControl<SelectableTextBlock>("EmergeStatus")!;
    for (int k = 0; k < 600 && (string.IsNullOrEmpty(status.Text) || status.Text == "Checking…"); k++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(100); }
    for (int k = 0; k < 20; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); System.Threading.Thread.Sleep(50); }
    status.BringIntoView();
    for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
    window.CaptureRenderedFrame()!.Save(path);
    Console.WriteLine($"wrote {path}: install button '{install.Content}', visible {install.IsVisible}");
    return 0;
}

static int ShowSettingsGeneral(string path, double w, double h)
{
    CircuitRF.Ui.AppDataRoot.RedirectTo(Path.Combine(Path.GetTempPath(), "crf-settings-" + Guid.NewGuid().ToString("N")[..8]));
    CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.EmSetupOnUpdateLayout = true);
    var settings = new CircuitRF.Ui.Views.Dialogs.SettingsView(null);
    if (w > 0) { settings.Width = w; settings.Height = h; }
    settings.Show();
    void Settle() { for (int k = 0; k < 6; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); settings.UpdateLayout(); } }
    Settle();
    settings.FindControl<CheckBox>("EmSetupOnUpdateCheck")?.BringIntoView();
    Settle();
    settings.CaptureRenderedFrame()!.Save(path);
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

/// <summary>What the shell gives a hosted schematic for Push In and the library-part commands: the answers, printed
/// rather than acted on.</summary>
sealed class SnapshotHierarchy : CircuitRF.Ui.Schematic.IHierarchyHost
{
    public bool CanPushInto(EditableComponent? comp, SchematicEditModel? parentModel, out string? reason)
        => HierarchyResolver.CanPushInto(comp, parentModel, out reason);
    public void PushIntoCell(CircuitRF.Ui.Schematic.SchematicDocument doc, EditableComponent comp) => Console.WriteLine($"push into {comp.InstanceName}");
    public void PopOutOf(CircuitRF.Ui.Schematic.SchematicDocument doc) { }
    public void PopToLevel(CircuitRF.Ui.Schematic.SchematicDocument doc, int frameIndex) { }
    public void OpenCellInNewTab(CircuitRF.Ui.Schematic.SchematicDocument fromDoc, EditableComponent comp) { }
    public Task SaveSchematicDocumentAsync(CircuitRF.Ui.Schematic.SchematicDocument doc) => Task.CompletedTask;
    public Task ReReferenceCellAsync(CircuitRF.Ui.Schematic.SchematicDocument doc, EditableComponent comp) => Task.CompletedTask;
    public Task<string?> ReferenceExternalCellAsync() => Task.FromResult<string?>(null);
    public bool CanReferenceExternalCell => true;
    public void ShowStatus(string text) => Console.WriteLine($"[status] {text}");
}

/// <summary>The Messages panel, for --clay: each line printed as it is posted.</summary>
sealed class ConsoleSink : IMessageSink
{
    public void Post(MessageLevel level, string text, string? filePath = null) => Console.WriteLine($"[{level}] {text}");
    public void Clear() { }
}
