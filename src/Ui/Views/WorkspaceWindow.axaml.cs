using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views;

public partial class WorkspaceWindow : Window
{
    public WorkspaceWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (App.Current as App)?.NotifyWindowCountChanged();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => (App.Current as App)?.NotifyWindowCountChanged(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // About menu item click (used for the in-window Help menu on Windows/Linux).
    private async void OnAboutMenuItemClick(object? sender, RoutedEventArgs e)
    {
        await new Dialogs.AboutWindow().ShowDialog(this);
    }
}
