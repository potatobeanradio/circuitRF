using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Messages;

public partial class MessagesView : UserControl
{
    public MessagesView()
    {
        InitializeComponent();
        // Auto-scroll to newest message when the Messages collection changes.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MessagesTool tool)
                tool.Messages.CollectionChanged += (_, _) => ScrollToBottom();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MessageDisplay.ModeChanged += OnTimestampModeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MessageDisplay.ModeChanged -= OnTimestampModeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTimestampModeChanged(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MessagesTool tool)
            {
                MessagesListBox.ItemsSource = null;          // force TimeText bindings to re-evaluate
                MessagesListBox.ItemsSource = tool.Messages;
            }
        });

    private void ScrollToBottom()
    {
        // Find the ListBox and scroll its ScrollViewer to the end.
        var listBox = this.FindControl<ListBox>("MessagesListBox");
        if (listBox is null) return;
        var scroll = listBox.FindDescendantOfType<ScrollViewer>();
        scroll?.ScrollToEnd();
    }

    private void OnRevealPathTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: MessageEntry { FilePath: { } path } }
            && DataContext is MessagesTool tool)
        {
            tool.RevealFileCommand.Execute(path);
            e.Handled = true;
        }
    }
}
