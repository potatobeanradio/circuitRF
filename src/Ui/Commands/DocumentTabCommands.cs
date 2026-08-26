using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Commands;

/// <summary>
/// Commands for circuitRF's own entries in the document tab-strip context menu
/// (<c>Styles/DocumentTabContextMenu.axaml</c>).
///
/// Static, and parameterized by the dockable itself, on purpose. The menu is one shared
/// <see cref="Avalonia.Controls.ContextMenu"/> instance whose DataContext is whichever
/// <c>IDockable</c> was right-clicked, and a torn-off document lives in a floating host window
/// whose DataContext is NOT the WorkspaceViewModel — so routing through
/// <c>$parent[DockControl].DataContext</c> would work in the main window and quietly do nothing in
/// a floating one.
/// </summary>
public static class DocumentTabCommands
{
    /// <summary>Platform-correct label, bound by the menu item via <c>x:Static</c>.</summary>
    public static string RevealLabel => FileReveal.Label;

    /// <summary>
    /// Reveals the right-clicked document's file. The parameter is the dockable (the menu's own
    /// DataContext); a document that is not file-backed, or is still a scratch document, is a
    /// no-op — the menu item hides itself in that case rather than showing as disabled.
    /// </summary>
    public static IRelayCommand<object?> Reveal { get; } = new RelayCommand<object?>(
        dockable => FileReveal.Reveal((dockable as IFileBackedDocument)?.FilePath));
}
