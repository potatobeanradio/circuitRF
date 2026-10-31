// The Workspace panel's BLANK-SPACE menu — a right-click below or beside the rows.
//
// ── EVERY ITEM IS A COMMAND THAT ALREADY EXISTS ────────────────────────────────────────────────
//
// Nothing here creates, imports or saves anything of its own. New Cell and New Folder are the
// panel's own header-button commands; everything else is the WorkspaceViewModel command File ▸
// already binds, handed THIS panel's window as the dialog owner (a floating Workspace panel is its
// own window, and a dialog parented to the main window would open behind it). A menu that
// re-implemented one would be a second copy that drifts, so this is only an ARRANGEMENT of them.
//
// ── WHY A TUNNEL PROBE AND NOT JUST "THE MENU ON THE SCROLLER" ─────────────────────────────────
//
// Each row's Grid carries its own node menu, and that one wins wherever it exists. But a row's
// INDENT and its expander chevron are TreeViewItem template parts outside that Grid, so a
// right-click there would bubble up to the scroller and offer workspace commands while pointing at
// a row. The probe records whether the right-click landed inside ANY TreeViewItem, and the Opening
// handler cancels on that — so this menu opens only over genuinely empty space.

using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Material.Icons;
using Material.Icons.Avalonia;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView
{
    /// <summary>True when the last right-click landed on a row (its indent and chevron included).</summary>
    private bool _contextOnRow;

    /// <summary>
    /// File ▸ Import's items, in File ▸ Import's order and with its words. <b>A test holds this list
    /// to the File menu's</b> (<c>ProjectTreeBlankMenuTests</c>) — two hand-maintained copies is the
    /// shape this codebase already has for the macOS menu, and the test is what stops a third from
    /// drifting silently.
    /// </summary>
    /// <remarks>Headers keep File ▸ Import's access-key underscores, which a context menu honours too.</remarks>
    internal static readonly (string Header, string Command, string? Tip)[] ImportItems =
    [
        ("_Data…", nameof(WorkspaceViewModel.ImportDataCommand), null),
        ("Sc_hematic…", nameof(WorkspaceViewModel.ImportSchematicCommand),
            "Read a schematic design file and write it as a new cell with its own schematic: two-pin resistors, capacitors and inductors become native components, every other part a cell holding the design's own symbol, and the connectivity is checked against the source. Requires an open workspace."),
        // File ▸ Import nests this under Image ▸; the menu below does the same.
        ("As _Touchstone…", nameof(WorkspaceViewModel.ImportImageAsTouchstoneCommand), null),
        ("_GDSII…", nameof(WorkspaceViewModel.ImportGdsiiLibraryCommand), null),
        ("GDSII (gdstk)…", nameof(WorkspaceViewModel.ImportGdsiiGdstkCommand), "{Binding " + nameof(WorkspaceViewModel.GdstkImportGdsiiTip) + "}"),
        ("OASIS (gdstk)…", nameof(WorkspaceViewModel.ImportOasisGdstkCommand), "{Binding " + nameof(WorkspaceViewModel.GdstkImportOasisTip) + "}"),
        ("_DXF…", nameof(WorkspaceViewModel.ImportDxfLibraryCommand), null),
        ("_Board…", nameof(WorkspaceViewModel.ImportBoardCommand),
            "Import a .kicad_pcb board: its stackup, nets, tracks, vias, zone fills and footprints. The file's version is reported, never branched on — every epoch reads."),
        ("Ge_rber…", nameof(WorkspaceViewModel.ImportGerberCommand),
            "Import a Gerber/Excellon file set as one flat cell and a technology of its own. Point at a folder, or at one file and say whether its folder was meant. Vias are rebuilt where the drill data comes too."),
        // File ▸ Import binds this tip, because it says whether the geometry worker can read a STEP file
        // on this machine; the menu here is rebuilt on every open, so it reads the same property then.
        ("_STEP…", nameof(WorkspaceViewModel.ImportStepCommand), "{Binding " + nameof(WorkspaceViewModel.ThreeDImportStepTip) + "}"),
        // brief-sar-11: anatomy into the active 3D view, both under the one tip File ▸ Import binds.
        ("_Voxel Model…", nameof(WorkspaceViewModel.ImportVoxelModelCommand), "{Binding " + nameof(WorkspaceViewModel.ThreeDImportAnatomyTip) + "}"),
        ("Surface _Meshes…", nameof(WorkspaceViewModel.ImportSurfaceMeshesCommand), "{Binding " + nameof(WorkspaceViewModel.ThreeDImportAnatomyTip) + "}"),
        ("_Component…", nameof(WorkspaceViewModel.ImportComponentCommand),
            "Import a component — its symbol, its land pattern(s) and the pin-to-pad map between them — as one cell. Point at a file or a folder; the folder is scanned for files that can be imported, and the formats read are .kicad_sym, .kicad_mod, .lib and .lbr."),
        ("_PDK…", nameof(WorkspaceViewModel.ImportPdkCommand), null),
        ("_Model or Subcircuit…", nameof(WorkspaceViewModel.ImportModelCardCommand),
            "Build a cell from a SPICE .model card or .subckt definition. A card becomes the native circuitRF component carrying its parameters, with an editable copy of that component's symbol; a subcircuit becomes its own netlist, wired as the file wires it, with a generic box for a symbol. Anything circuitRF has no model for is refused by name rather than approximated."),
        ("_Technology…", nameof(WorkspaceViewModel.ImportTechnologyCommand), null),
        ("Into Open _Technology…", nameof(WorkspaceViewModel.ImportIntoTechnologyCommand),
            "Bring layers, stackup or DRC rules from another .ctech into the technology you are editing. Requires an open technology."),
        ("_Wirebond Table…", nameof(WorkspaceViewModel.ImportWireTableCommand),
            "A from-pad / to-pad wirebond table, as every packaging flow already has."),
        ("Wirebond W_ires…", nameof(WorkspaceViewModel.ImportWirebondWiresCommand),
            "Bring a .wBond design's wires into the active schematic — into the selected wBond component, or as a new one. Artwork in the file is left behind. Requires an active schematic."),
        ("Wirebond as _Cell…", nameof(WorkspaceViewModel.ImportWirebondAsCellCommand),
            "Create a cell from a .wBond: its wires become the schematic view, its embedded artwork the layout view."),
    ];

    /// <summary>A tip written as File ▸ Import's <c>{Binding Property}</c> is that property's value now;
    /// any other tip is its own text.</summary>
    private static string? BoundTip(string tip, WorkspaceViewModel ws)
        => tip.StartsWith("{Binding ", StringComparison.Ordinal) && tip.EndsWith('}')
            ? typeof(WorkspaceViewModel).GetProperty(tip["{Binding ".Length..^1].Trim())?.GetValue(ws) as string
            : tip;

    /// <summary>Wires the menu onto the scroller. Called once, from the constructor.</summary>
    private void WireBlankSpaceMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += OnBlankMenuOpening;
        TreeScroll.ContextMenu = menu;

        // TUNNEL, so it runs before the ContextMenu's own handler on the scroller decides to open.
        TreeScroll.AddHandler(ContextRequestedEvent, (_, e) =>
            _contextOnRow = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true) is not null,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// The same empty space with NO workspace open, where the scroller is hidden and the recent list
    /// shows instead — so this menu sits on the area holding both. A recent entry's own menu wins
    /// over it; with a workspace open it stands aside for the scroller's, including when that one
    /// cancelled itself over a row's indent and the request bubbled on up to here.
    /// </summary>
    private void WireNoWorkspaceMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += OnNoWorkspaceMenuOpening;
        TreeArea.ContextMenu = menu;
    }

    private void OnNoWorkspaceMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (DataContext is not ProjectTreeTool { HasWorkspace: false } tool
            || tool.Actions is not WorkspaceViewModel ws)
        {
            e.Cancel = true;
            return;
        }
        menu.ItemsSource = NoWorkspaceMenuItems(tool, ws);
    }

    /// <summary>
    /// The menu offered wherever the panel is right-clicked with NO workspace open — the empty tree
    /// area and the "No workspace open" header alike. ONE list, built here, so the two cannot drift
    /// apart: they used to be two hand-written menus offering different things (the header lacked New
    /// Workspace… and Open Workspace in New Window…, the area lacked Unarchive).
    /// </summary>
    /// <remarks>File ▸'s own commands, handed THIS panel's window as the dialog owner (see the header).
    /// Built fresh on each open: a MenuItem can have only one parent, and two menus share the list.</remarks>
    internal List<object> NoWorkspaceMenuItems(ProjectTreeTool tool, WorkspaceViewModel ws)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        return
        [
            new MenuItem
            {
                Header = "New _Workspace…", Command = ws.NewWorkspaceCommand, CommandParameter = owner,
                Icon   = new MaterialIcon { Kind = MaterialIconKind.FolderPlus, Width = 14, Height = 14 },
            },
            new Separator(),
            new MenuItem
            {
                Header = "Open _Workspace…", Command = ws.OpenWorkspaceCommand, CommandParameter = owner,
                Icon   = new MaterialIcon { Kind = MaterialIconKind.FolderOpenOutline, Width = 14, Height = 14 },
            },
            new MenuItem
            {
                Header = "Open Workspace in _New Window…", Command = ws.OpenWorkspaceInNewWindowCommand, CommandParameter = owner,
                Icon   = new MaterialIcon { Kind = MaterialIconKind.FolderOpenOutline, Width = 14, Height = 14 },
            },
            new MenuItem
            {
                Header = "_Unarchive Workspace…", Command = tool.UnarchiveWorkspaceCommand,
                Icon   = new MaterialIcon { Kind = MaterialIconKind.ZipBoxOutline, Width = 14, Height = 14 },
            },
        ];
    }

    /// <summary>The header's XAML items — the open-workspace set — captured the first time the menu opens.</summary>
    private List<object>? _headerWorkspaceItems;

    /// <summary>
    /// The workspace-name header's menu: its own XAML items with a workspace open, and
    /// <see cref="NoWorkspaceMenuItems"/> — the empty area's menu, exactly — with none.
    /// </summary>
    private void OnHeaderMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || DataContext is not ProjectTreeTool tool) return;
        _headerWorkspaceItems ??= [.. menu.Items.OfType<object>()];
        menu.Items.Clear();
        IEnumerable<object> items = !tool.HasWorkspace && tool.Actions is WorkspaceViewModel ws
            ? NoWorkspaceMenuItems(tool, ws)
            : _headerWorkspaceItems;
        foreach (var item in items) menu.Items.Add(item);
    }

    private void OnBlankMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (_contextOnRow
            || DataContext is not ProjectTreeTool { HasWorkspace: true } tool
            || tool.Actions is not WorkspaceViewModel ws)
        {
            e.Cancel = true;
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;

        MenuItem Item(string header, ICommand command, MaterialIconKind icon, object? parameter = null,
                      string? tip = null)
        {
            var item = new MenuItem
            {
                Header           = header,
                Command          = command,
                CommandParameter = parameter,
                Icon             = new MaterialIcon { Kind = icon, Width = 14, Height = 14 },
            };
            if (tip is not null) ToolTip.SetTip(item, tip);
            return item;
        }

        var import = new MenuItem
        {
            Header = "_Import",
            Icon   = new MaterialIcon { Kind = MaterialIconKind.DatabaseImportOutline, Width = 14, Height = 14 },
        };
        foreach (var (header, command, tip) in ImportItems)
        {
            var sub = new MenuItem
            {
                Header           = header,
                Command          = (ICommand?)typeof(WorkspaceViewModel).GetProperty(command)?.GetValue(ws),
                CommandParameter = owner,
            };
            if (tip is not null) ToolTip.SetTip(sub, BoundTip(tip, ws));
            if (command == nameof(WorkspaceViewModel.ImportImageAsTouchstoneCommand))
                import.Items.Add(new MenuItem { Header = "_Image", Items = { sub } });
            else
                import.Items.Add(sub);
        }

        menu.ItemsSource = new List<object>
        {
            // Make something new, here.
            Item("New Cell…",       tool.NewCellInWorkspaceCommand,   MaterialIconKind.PlusBox),
            Item("New Folder…",     tool.NewFolderInWorkspaceCommand, MaterialIconKind.FolderPlusOutline),
            Item("New Technology…", ws.NewTechnologyCommand,          MaterialIconKind.LayersOutline, owner),
            new Separator(),

            // Bring something in from elsewhere.
            Item("Add Cell to Workspace…", ws.AddCellToWorkspaceCommand, MaterialIconKind.FolderMoveOutline, owner,
                 "Take a cell from another project into this one — copy it in, or reference it where it is."),
            import,
            Item("Add Vendor Library…", ws.AddVendorLibraryCommand, MaterialIconKind.Bookshelf, owner,
                 "Reference a manufacturer's part library (plain-text SPICE) where it is — never copied."),
            Item("Manage Libraries…", ws.ManagePdksCommand, MaterialIconKind.PackageVariant, owner),
            new Separator(),

            // The workspace as a whole.
            Item("Save Workspace As…", ws.SaveWorkspaceAsCommand, MaterialIconKind.ContentSaveEdit, owner),
            Item("Archive Workspace…", ws.ArchiveWorkspaceCommand, MaterialIconKind.ZipBox, owner,
                 "Zip this workspace up so it can be sent to someone on another machine."),
            new Separator(),
            Item(tool.RevealLabel, tool.RevealWorkspaceCommand, MaterialIconKind.FolderSearchOutline),
            new Separator(),
            // The header menu's own command, so both ask about unsaved documents the same way.
            Item("Close Workspace", tool.CloseWorkspaceCommand, MaterialIconKind.FolderRemoveOutline),
        };
    }
}
