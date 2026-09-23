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
        ("_GDSII…", nameof(WorkspaceViewModel.ImportGdsiiLibraryCommand), null),
        ("_DXF…", nameof(WorkspaceViewModel.ImportDxfLibraryCommand), null),
        ("_Board…", nameof(WorkspaceViewModel.ImportBoardCommand),
            "Import a .kicad_pcb board: its stackup, nets, tracks, vias, zone fills and footprints. The file's version is reported, never branched on — every epoch reads."),
        ("Ge_rber…", nameof(WorkspaceViewModel.ImportGerberCommand),
            "Import a Gerber/Excellon file set as one flat cell and a technology of its own. Point at a folder, or at one file and say whether its folder was meant. Vias are rebuilt where the drill data comes too."),
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
            if (tip is not null) ToolTip.SetTip(sub, tip);
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
            Item("Manage PDKs…", ws.ManagePdksCommand, MaterialIconKind.PackageVariant, owner),
            new Separator(),

            // The workspace as a whole.
            Item("Save Workspace As…", ws.SaveWorkspaceAsCommand, MaterialIconKind.ContentSaveEdit, owner),
            Item("Archive Workspace…", ws.ArchiveWorkspaceCommand, MaterialIconKind.ZipBox, owner,
                 "Zip this workspace up so it can be sent to someone on another machine."),
            new Separator(),
            Item(tool.RevealLabel, tool.RevealWorkspaceCommand, MaterialIconKind.FolderSearchOutline),
        };
    }
}
