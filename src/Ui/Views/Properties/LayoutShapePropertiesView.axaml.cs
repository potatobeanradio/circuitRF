using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>Layout Editor's shape-properties panel (L1c, restructured by brief-L1j-properties-
/// inspector.md). Commit convention mirrors <c>LayoutEditorView.axaml.cs</c>'s toolbar fields exactly:
/// LostFocus commits, Enter commits, Escape reverts (new in L1j).
///
/// Every static field is dispatched generically by its <c>Tag</c> (mirrors <c>TechEditorView</c>'s
/// Tag-keyed <c>CommitField</c>/<c>OnComboSelectionChanged</c> dispatcher) — three handlers cover every
/// field in the panel instead of one pair per field. Vertex-list rows are dispatched the same way, but
/// via the control's DataContext (a <see cref="VertexRowViewModel"/>) rather than the top-level VM,
/// since each row owns its own X/Y commit/revert.</summary>
public partial class LayoutShapePropertiesView : UserControl
{
    public LayoutShapePropertiesView() => InitializeComponent();

    private LayoutShapePropertiesViewModel? Vm => DataContext as LayoutShapePropertiesViewModel;

    // ── Static fields (Tag = field key, e.g. "CornerRadius", "RectWidth") ───────────────────────

    private void OnFieldGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string key }) Vm?.SetFocusedField(key);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        Vm?.SetFocusedField(null);
        Vm?.CommitField(key, tb.Text ?? "");
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } tb) return;
        if (e.Key is Key.Enter or Key.Return) Vm?.CommitField(key, tb.Text ?? "");
        else if (e.Key == Key.Escape) Vm?.RevertField(key);
    }

    // ── Bitmap: Browse… (UI firewall — the file picker lives in code-behind, never the VM) ───────

    private async void OnBitmapBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var picker = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (picker is null) return;

        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Bitmap Image",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.tif", "*.webp" } }
            }
        });
        if (files.Count > 0)
            Vm.CommitBitmapPathText(files[0].Path.LocalPath);
    }

    // ── Instance: Re-target… (UI firewall — the cell-picker dialog lives in code-behind, never the
    // VM; mirrors LayoutEditorView.axaml.cs's own OnInstanceTool exactly, minus the placement-arming
    // step at the end — this button retargets the ALREADY-PLACED selected instance in place) ─────────

    private async void OnInstanceRetargetClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.EditorVm is not { } editorVm) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var dialog = new InstanceCellPickerDialog(editorVm.WorkspaceRootDir, editorVm.InstanceBaseDir, editorVm.CurrentCellDir);
        var cellRef = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrEmpty(cellRef)) return;

        editorVm.RetargetSelectedInstance(cellRef);
    }

    // ── Vertex-list rows (Tag = "X" or "Y"; DataContext = the row itself) ───────────────────────

    private void OnVertexFieldGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string tag, DataContext: VertexRowViewModel row })
            Vm?.SetFocusedField(tag == "Y" ? row.FieldKeyY : row.FieldKeyX);
    }

    private void OnVertexFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string tag, DataContext: VertexRowViewModel row } tb) return;
        Vm?.SetFocusedField(null);
        if (tag == "Y") row.CommitY(tb.Text ?? ""); else row.CommitX(tb.Text ?? "");
    }

    private void OnVertexFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string tag, DataContext: VertexRowViewModel row } tb) return;
        if (e.Key is Key.Enter or Key.Return)
        {
            if (tag == "Y") row.CommitY(tb.Text ?? ""); else row.CommitX(tb.Text ?? "");
        }
        else if (e.Key == Key.Escape)
        {
            row.Revert(isY: tag == "Y");
        }
    }

    // PCell parameter list rows moved to PCellParameterListView.axaml.cs (brief-L5-followups.md §5,
    // extracted so LayoutPCellParameterDialog can host the same surface).
}
