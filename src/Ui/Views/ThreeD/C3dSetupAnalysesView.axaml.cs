using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Views.Layout;

namespace CircuitRF.Ui.Views.ThreeD;

/// <summary>
/// A 3D view's embedded EM setups as analysis cards — hosted by Simulate ▸ Setup Analyses… and, 3D editor round 5, by the
/// Analyses panel while a .c3d is the active document (the panel had shown the last schematic's analyses beside it).
/// Holds no state of its own: every action is one of <see cref="C3dEditorViewModel"/>'s setup edits, so undo and the dirty
/// mark are the document's.
/// </summary>
public partial class C3dSetupAnalysesView : UserControl
{
    private C3dEditorViewModel? Vm => DataContext as C3dEditorViewModel;

    public C3dSetupAnalysesView()
    {
        InitializeComponent();
        // The double-click is read off the PRESS, handled or not, rather than from DoubleTapped: Avalonia raises that only
        // when both presses land on the same element, so a card whose parts the first press re-laid (the selection
        // rebuilding the panel under it) or took (the list item's own selection) opened nothing (owner report, 2026-09-28).
        Rows.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // ── the card's own actions: select the card first, then the view model's edit on the selection ─────────────

    private C3dSetupItem? Select(object? sender)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not C3dSetupItem item) return null;
        vm.SelectedSetupItem = item;
        return item;
    }

    private void OnActiveClick(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is { } item) Vm!.SetActiveSetup(item.Name);
    }

    private void OnMenuMakeActive(object? sender, RoutedEventArgs e) => OnActiveClick(sender, e);

    private void OnMenuRun(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is { } item && Vm is { RunRequested: { } run } vm) _ = run(vm, item.IsExternal ? null : item.Name);
    }

    private void OnMenuDuplicate(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) Vm!.DuplicateSetup();
    }

    /// <summary>brief-dfr32-3 — whether the card offers Delete Saved Fields, read from its run folder as the menu opens.</summary>
    private void OnCardMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e) => Vm?.RefreshSavedFields();

    private void OnMenuDeleteSavedFields(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is C3dSetupItem { IsExternal: false } item) Vm?.DeleteSavedFields(item.Name);
    }

    private void OnMenuRemove(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) Vm!.RemoveSetup();
    }

    private void OnMenuRename(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) OnRenameClick(sender, e);
    }

    private void OnMenuEdit(object? sender, RoutedEventArgs e)
    {
        if (Select(sender) is not null) OpenEditor();
    }

    private void OnEditClick(object? sender, RoutedEventArgs e) => OpenEditor();

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || !e.GetCurrentPoint(Rows).Properties.IsLeftButtonPressed) return;
        // Only a card, and not its active mark (a button of its own): a double-click on the list's empty space edits nothing.
        // The card is found by its list item, not by the pressed element's DataContext: a solver-note line's is its own row,
        // so a double-click there opened nothing (owner report, 2026-09-28, after round 6).
        if (e.Source is not Visual source || CardOf(source) is not { } item) return;
        if (source.FindAncestorOfType<Button>(includeSelf: true) is { } button && Rows.IsVisualAncestorOf(button)) return;
        if (Vm is { } vm) vm.SelectedSetupItem = item;
        // After the press has finished routing, so the list item's own selection is not handed a modal window mid-gesture.
        Dispatcher.UIThread.Post(OpenEditor);
    }

    /// <summary>The setup of the card <paramref name="pressed"/> lies in, whatever part of the card it is.</summary>
    internal static C3dSetupItem? CardOf(Visual pressed) =>
        pressed.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as C3dSetupItem;

    private async void OnMenuCopyNotes(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not C3dSetupItem { HasFidelity: true } item) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(string.Join(Environment.NewLine, item.Fidelity.Select(r => r.Text)));
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedSetupItem: { IsExternal: false } item } vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var text = await new Dialogs.InputNameDialog("Rename Setup", "Setup name:", item.Name).ShowDialog<string?>(owner);
        if (text is null) return;
        ShowError(vm.RenameSetup(text));
    }

    private void ShowError(string? why)
    {
        ErrorText.Text = why;
        ErrorText.IsVisible = why is not null;
    }

    /// <summary>
    /// The selected setup in the <c>.cem</c> panel, in its own modal window: the view model's
    /// <see cref="C3dEditorViewModel.SetupEditor"/>, bound, so an undo that rebuilds it is followed.
    /// </summary>
    private void OpenEditor()
    {
        if (Vm is not { SelectedSetupItem: { } item } vm || vm.SetupEditor is null) return;
        var view = new EmSetupEditorView();
        view.Bind(DataContextProperty, new Binding(nameof(C3dEditorViewModel.SetupEditor)) { Source = vm });
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, Padding = new(20, 6), Margin = new(12, 8),
                                 HorizontalAlignment = HorizontalAlignment.Right };
        var body = new DockPanel();
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
        body.Children.Add(close);
        body.Children.Add(view);
        var window = new Window
        {
            Title = $"Setup — {item.Name}" + (item.IsExternal ? " (read-only)" : ""),
            Width = 520, Height = 680,
            CanResize = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = body,
        };
        close.Click += (_, _) => window.Close();
        // With the focus on Close or nothing, the panel's own binding is not on the way up.
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = vm.SaveDocumentCommand });
        window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Meta), Command = vm.SaveDocumentCommand });
        // brief-dfr31-1 R-dfr31-1-4 — not modal: a terminal is picked by clicking the 3D view behind it.
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner); else window.Show();
    }
}
