using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// Touchstone export (brief-wbond-wbe M3), plus the small shell-facing surface the standalone
/// binary's menu bar drives.
///
/// <para><b>The menu binds the view's OWN methods, never a second implementation.</b> Undo, redo,
/// copy, paste and Select All All-Wires already exist here as the keyboard gestures' handlers; the
/// standalone shell reaches the same ones, so a menu item and its shortcut can never diverge.</para>
/// </summary>
public partial class WBondEditorView
{
    /// <summary>Status line, for a host that has nowhere else to report to (the standalone shell).</summary>
    internal void ShowShellStatus(string message, bool isWarning = false) => ShowStatus(message, isWarning);

    internal void UndoFromShell() { _bound?.Editor.Undo(); RepaintBoth(); }

    internal void RedoFromShell() { _bound?.Editor.Redo(); RepaintBoth(); }

    private async void OnExportTouchstone(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ExportTouchstoneAsync();

    /// <summary>
    /// Writes the design's array network as a Touchstone file (§11's own requirement — a wBond has
    /// never been able to publish its own network).
    ///
    /// <para>The dialog states the port map before anything is written; the write itself goes through
    /// <see cref="WBondTouchstoneExport"/>, which is <c>RFNetwork</c>'s Z→S and <c>TouchstoneExporter</c>
    /// and nothing of its own.</para>
    /// </summary>
    internal async Task ExportTouchstoneAsync()
    {
        if (_bound is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var design = _bound.Editor.Design;
        if (design.Arrays.Count == 0)
        {
            ShowStatus("This design has no wire arrays, so it has no ports to publish.", isWarning: true);
            return;
        }

        var options = await WBondTouchstoneExportDialog.ShowAsync(owner, design);
        if (options is null) return;

        // The suffix is the exporter's to choose from the port count, so the picker asks for a base
        // name and never for an extension it might disagree with.
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Touchstone",
            SuggestedFileName = "wirebonds",
            FileTypeChoices =
            [
                new FilePickerFileType($"Touchstone ({design.Arrays.Count}-port)")
                {
                    Patterns = [$"*.s{design.Arrays.Count}p"],
                },
            ],
        });

        if (file?.TryGetLocalPath() is not { } chosen) return;

        // Strip whatever extension the picker attached: the exporter appends its own .sNp, and a
        // doubled suffix is the one way this write can produce a file nobody can find.
        string baseNoSuffix = Path.Combine(
            Path.GetDirectoryName(chosen) ?? "", Path.GetFileNameWithoutExtension(chosen));

        try
        {
            var result = WBondTouchstoneExport.Export(design, options, baseNoSuffix);

            ShowStatus(result.WrittenPaths.Count > 0
                ? $"Exported {Path.GetFileName(result.WrittenPaths[0])} — " +
                  $"{design.Arrays.Count} port(s), {options.Points} frequency point(s)."
                : "Nothing was written.", isWarning: result.WrittenPaths.Count == 0);
        }
        catch (Exception ex)
        {
            // A refusal (no declared return path) is the message that matters most here — it names a
            // design fault the file would otherwise have carried silently.
            ShowStatus(ex.Message, isWarning: true);
        }
    }
}
