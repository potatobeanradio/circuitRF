using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The cell picker behind every "place an instance of a cell" gesture that is not a drag — the layout
/// editor's Instance toolbar button, the Properties Inspector's re-target button, and Design ▸ Place
/// Cell Instance… for a schematic or a layout (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md §6:
/// "pick a cell (a dialog listing the workspace's cells with a layout view)"; refined by
/// brief-L3a-followups.md §1/R-fix-1). Scans <paramref name="workspaceRootDir"/> for cell folders (a
/// directory carrying <c>.ccell</c>) and lists the cells this workspace REFERENCES as well, mirroring
/// <c>ChangeTechnologyDialog</c>'s "always-selectable list + one quiet escape hatch" shape.
///
/// <para><b>One dialog, both editors, deliberately.</b> The layout and schematic entry points differ
/// only in which view the placement draws from — the exclusion rule, the list, the refusals and the
/// external-reference escape hatch are the same question in both, and two dialogs would be two places
/// for that question to drift.</para>
///
/// <b>R-fix-1 — exclude the parent cell only; everything else is offered, even a cell that would form
/// a deeper cycle.</b> Self-reference is obvious enough that a user never wonders why it's missing —
/// leave it out entirely. A DEEPER cycle (A instantiates B; editing B) is not obvious: silently
/// omitting A from the picker would leave the user hunting for a cell that appears to have vanished,
/// with nothing on screen to explain it. A visible, explanatory error (R-L3a-2's edit-time refusal,
/// naming the full path) teaches the user why in a way a missing row never could — so every cell
/// except the parent is listed and attemptable, and the existing cycle guard is what actually catches
/// a deeper cycle at commit time. A cell with no view of the kind being placed is listed too, for the
/// identical "visible and explained, never silently absent" principle — disabled with its reason in a
/// layout, and enabled with a remark in a schematic, where the placement offers to generate the
/// missing symbol itself.
/// </summary>
public partial class InstanceCellPickerDialog : Window
{
    private readonly string _baseDir = "";
    private readonly bool _canReferenceExternal;

    public InstanceCellPickerDialog() => InitializeComponent();

    /// <param name="parentCellDir">The currently-open document's own cell folder (<see
    /// cref="Layout.LayoutEditorViewModel.CurrentCellDir"/>) — the ONE entry excluded from the list
    /// (R-fix-1). Null for a scratch document (nothing to exclude — see that property's own doc
    /// comment).</param>
    /// <param name="view">Which view the placement draws from — <see cref="ViewType.Layout"/> for a
    /// layout instance, <see cref="ViewType.Symbol"/> for a schematic one.</param>
    /// <param name="canReferenceExternal">
    /// True when the caller can run the cross-workspace flow (a workspace is open). The escape-hatch
    /// button then reads "Reference Cell…" and simply CLOSES with
    /// <see cref="CellPickResult.Reference"/> — the caller runs the flow and re-asks.
    /// <para><b>It closes rather than running the flow itself, and that is the point.</b> That flow
    /// shows modal dialogs of its own on the same owner window; opening one from inside this dialog
    /// would stack two modals over one owner. It also keeps this class free of any knowledge of the
    /// workspace view-model.</para>
    /// False falls back to the older plain folder-browse, which is all a document with no workspace
    /// behind it can offer.
    /// </param>
    public InstanceCellPickerDialog(
        string? workspaceRootDir, string baseDir, string? parentCellDir = null,
        ViewType view = ViewType.Layout, bool canReferenceExternal = false) : this()
    {
        _baseDir = baseDir;
        _canReferenceExternal = canReferenceExternal;

        PromptText.Text = view == ViewType.Symbol
            ? "Choose a cell to place in this schematic."
            : "Choose a cell to place as an instance.";

        // The actual scan/exclusion/disabled-reason logic lives in InstanceCellChoices (framework-
        // free, headlessly testable) — this constructor only turns the result into ListBox state.
        var items = workspaceRootDir is { Length: > 0 } root
            ? InstanceCellChoices.CollectWithReferences(root, parentCellDir, view)
            : new List<InstanceCellChoice>();
        items.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        ChoiceList.ItemsSource = items;
        // Prefer the first PLACEABLE (enabled) item so a routine placement doesn't land on a
        // disabled-with-reason row by accident — but a workspace of nothing but no-layout-view cells
        // still gets an initial selection (better than none) rather than silently picking nothing.
        int firstEnabled = items.FindIndex(i => i.IsEnabled);
        if (items.Count > 0) ChoiceList.SelectedIndex = firstEnabled >= 0 ? firstEnabled : 0;
        EmptyText.IsVisible = items.Count == 0;

        if (!canReferenceExternal)
        {
            BrowseButton.Content = "Browse…";
            BrowseButton.SetValue(ToolTip.TipProperty,
                "Choose a cell folder outside the automatically-scanned list");
        }

        UpdateOkEnabled();

        BrowseButton.Click += async (_, _) => await OnBrowseAsync();
        CancelButton.Click += (_, _) => Close(null);
        ChoiceList.SelectionChanged += (_, _) => UpdateOkEnabled();
        ChoiceList.DoubleTapped += (_, _) => TryAccept();
        OkButton.Click += (_, _) => TryAccept();
    }

    private void TryAccept()
    {
        if (ChoiceList.SelectedItem is InstanceCellChoice { IsEnabled: true } chosen)
            Close(new CellPickResult(RelativeCellRef(chosen.AbsoluteCellDir), chosen.AbsoluteCellDir));
    }

    private void UpdateOkEnabled()
    {
        bool enabled = ChoiceList.SelectedItem is InstanceCellChoice { IsEnabled: true };
        OkButton.IsEnabled = enabled;
        DisabledReasonText.Text = !enabled && ChoiceList.SelectedItem is InstanceCellChoice { DisabledReason: { } reason }
            ? reason : "";
        DisabledReasonText.IsVisible = !enabled && DisabledReasonText.Text.Length > 0;
    }

    private string RelativeCellRef(string absoluteCellDir)
    {
        try { return ExternalCellRef.MakeCellRef(_baseDir, absoluteCellDir); }
        catch { return absoluteCellDir; }
    }

    private async Task OnBrowseAsync()
    {
        // The workspace-backed case hands the whole question back to the caller — see the ctor note.
        if (_canReferenceExternal) { Close(CellPickResult.Reference); return; }

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a Cell Folder",
            AllowMultiple = false,
        });
        if (result.Count == 0) return;

        string chosenDir = result[0].Path.LocalPath;
        var primary = CellFolder.ResolvePrimary(chosenDir, ViewType.Layout);
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
        {
            // Not a resolvable layout cell — still allow it through as a raw CellRef rather than
            // silently refusing; the placement will render as R-L3a-1's "No Layout" placeholder,
            // which is a more honest outcome than pretending the picker validated it.
        }
        Close(new CellPickResult(RelativeCellRef(chosenDir), Path.GetFullPath(chosenDir)));
    }
}
