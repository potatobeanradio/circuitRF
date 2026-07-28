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
/// The Instance-place tool's cell picker (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md §6:
/// "pick a cell (a dialog listing the workspace's cells with a layout view)"; refined by
/// brief-L3a-followups.md §1/R-fix-1). Scans <paramref name="workspaceRootDir"/> for cell folders (a
/// directory carrying <c>.ccell</c>), mirroring <c>ChangeTechnologyDialog</c>'s "always-selectable
/// list + Browse…" shape. Returns the chosen cell's <c>CellRef</c> ALREADY RELATIVE TO <paramref
/// name="baseDir"/> (the currently-open document's own directory — <c>LayoutEditorViewModel.
/// InstanceBaseDir</c>), so the caller (<see cref="Views.Layout.LayoutEditorView"/>'s code-behind) can
/// pass the result straight to <c>LayoutEditorViewModel.BeginInstancePlacement</c> with no further
/// path math.
///
/// <b>R-fix-1 — exclude the parent cell only; everything else is offered, even a cell that would form
/// a deeper cycle.</b> Self-reference is obvious enough that a user never wonders why it's missing —
/// leave it out entirely. A DEEPER cycle (A instantiates B; editing B) is not obvious: silently
/// omitting A from the picker would leave the user hunting for a cell that appears to have vanished,
/// with nothing on screen to explain it. A visible, explanatory error (R-L3a-2's edit-time refusal,
/// naming the full path) teaches the user why in a way a missing row never could — so every cell
/// except the parent is listed and attemptable, and the existing cycle guard is what actually catches
/// a deeper cycle at commit time. A cell with no layout view is listed too, but disabled with its
/// reason shown inline, for the identical "visible and explained, never silently absent" principle.
/// </summary>
public partial class InstanceCellPickerDialog : Window
{
    private readonly string _baseDir = "";

    public InstanceCellPickerDialog() => InitializeComponent();

    /// <param name="parentCellDir">The currently-open document's own cell folder (<see
    /// cref="Layout.LayoutEditorViewModel.CurrentCellDir"/>) — the ONE entry excluded from the list
    /// (R-fix-1). Null for a scratch document (nothing to exclude — see that property's own doc
    /// comment).</param>
    public InstanceCellPickerDialog(string? workspaceRootDir, string baseDir, string? parentCellDir = null) : this()
    {
        _baseDir = baseDir;

        // The actual scan/exclusion/disabled-reason logic lives in InstanceCellChoices (framework-
        // free, headlessly testable) — this constructor only turns the result into ListBox state.
        var items = workspaceRootDir is { Length: > 0 } root
            ? InstanceCellChoices.Collect(root, parentCellDir)
            : new List<InstanceCellChoice>();
        items.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        ChoiceList.ItemsSource = items;
        // Prefer the first PLACEABLE (enabled) item so a routine placement doesn't land on a
        // disabled-with-reason row by accident — but a workspace of nothing but no-layout-view cells
        // still gets an initial selection (better than none) rather than silently picking nothing.
        int firstEnabled = items.FindIndex(i => i.IsEnabled);
        if (items.Count > 0) ChoiceList.SelectedIndex = firstEnabled >= 0 ? firstEnabled : 0;
        EmptyText.IsVisible = items.Count == 0;
        UpdateOkEnabled();

        BrowseButton.Click += async (_, _) => await OnBrowseAsync();
        CancelButton.Click += (_, _) => Close(null);
        ChoiceList.SelectionChanged += (_, _) => UpdateOkEnabled();
        OkButton.Click += (_, _) =>
        {
            if (ChoiceList.SelectedItem is InstanceCellChoice { IsEnabled: true } chosen)
                Close(RelativeCellRef(chosen.AbsoluteCellDir));
        };
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
        try { return Path.GetRelativePath(_baseDir, absoluteCellDir); }
        catch { return absoluteCellDir; }
    }

    private async Task OnBrowseAsync()
    {
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
        Close(RelativeCellRef(chosenDir));
    }
}
