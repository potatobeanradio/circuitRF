// The menu bar's own commands (owner, 2026-09-19).
//
// ── EVERY ONE OF THESE IS A HOOK, AND NONE OF THEM IS A SECOND IMPLEMENTATION ─────────────────
//
// The View menu is made almost entirely of things this view model already has — the four panel
// toggles and the results-text toggle are RailRfViewModel.Panes.cs's commands, and the menu binds
// them rather than declaring a second spelling of each. What it does NOT have is Zoom to Fit, and
// what the FILE menu does not have is any of its six: the board's viewport belongs to the CANVAS,
// and Open, Import, Report, Export, Compare and Close all need a file picker, a storage provider
// and a top level. Every one of those is a control, and this view model holds no control.
//
// So each is a hook, on the same terms as PostToUi and SaveRequested: the WINDOW is the half that
// knows there is a picker, and it installs the one line that calls the method its TOOLBAR BUTTON
// already calls. That is the whole rule here — a menu item that opened its own picker would be a
// second Open that drifts from the button beside it, silently, the first time either is touched.
//
// A command with a hook also keeps the menu declarative on both surfaces — the macOS NativeMenu and
// the in-window Menu are hand-mirrored, and a NativeMenuItem binds a Command, not a Click handler in
// a code-behind — so the two cannot drift into offering different things either.

using System;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// Frames the whole board in the board canvas. <b>Installed by the window</b>; null until it is,
    /// and null in a test that drives this view model with no application host.
    /// </summary>
    public Action? ZoomToFitHook { get; set; }

    /// <summary>
    /// View ▸ Zoom to Fit — the same gesture the board panel's own button and its <c>F</c> key run.
    /// </summary>
    /// <remarks>
    /// <b>Ungated on purpose.</b> A window with no board imported has a canvas with nothing in it,
    /// and framing nothing is a no-op rather than a wrong answer — so there is no <c>CanExecute</c>
    /// to keep in step with <see cref="HasBoard"/>, and no menu item that dims and undims as an
    /// import lands. The board panel's button is hidden in that state because a toolbar with a dead
    /// glyph in it reads as broken; a menu item does not.
    /// </remarks>
    [RelayCommand]
    private void ZoomToFit() => ZoomToFitHook?.Invoke();

    // ── The File menu ─────────────────────────────────────────────────────────────────────────
    //
    // Save and Save as… are NOT here: they are RailRfViewModel.Save.cs's own commands and the menu
    // binds those. The six below are the rest of that toolbar, in its order.

    /// <summary>Open a <c>.crail</c> or a <c>.clay</c> — the Open button's own picker.</summary>
    public Action? OpenDocumentHook { get; set; }

    /// <summary>Import a board — the Import button's own picker and its two questions.</summary>
    public Action? ImportBoardHook { get; set; }

    /// <summary>
    /// Write what is on screen. <b>The argument is the extension the picker OPENS on</b>, which is
    /// the only difference between Report and Export: both offer every type, because refusing to
    /// write a CSV from Report would be a distinction only the window knows about.
    /// </summary>
    public Action<string>? ExportHook { get; set; }

    /// <summary>Hold this design against another <c>.crail</c>.</summary>
    public Action? CompareHook { get; set; }

    /// <summary>Close the window — which, one window per document, is closing the document.</summary>
    public Action? CloseHook { get; set; }

    [RelayCommand]
    private void OpenDocument() => OpenDocumentHook?.Invoke();

    [RelayCommand]
    private void ImportBoard() => ImportBoardHook?.Invoke();

    /// <summary>The report page. Opens the picker on PDF, because a report IS the page.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Report() => ExportHook?.Invoke("pdf");

    /// <summary>The numbers. Opens the picker on CSV, because that is what Export has promised.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export() => ExportHook?.Invoke("csv");

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Compare() => CompareHook?.Invoke();

    [RelayCommand]
    private void Close() => CloseHook?.Invoke();

    /// <summary>
    /// Re-asks the three that need a result whether they may be pressed.
    /// </summary>
    /// <remarks>
    /// <b>The toolbar buttons do not need this and the menu items do.</b> A button carries
    /// <c>IsEnabled="{Binding CanExport}"</c>, which re-reads itself from the property notification;
    /// a menu item is dimmed by its Command's own <c>CanExecute</c>, and a
    /// <c>RelayCommand</c> re-asks only when it is told to. Called from the one place
    /// <c>CanExport</c> is announced.
    /// </remarks>
    private void RefreshExportCommands()
    {
        ReportCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CompareCommand.NotifyCanExecuteChanged();
    }
}
