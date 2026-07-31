using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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

    /// <summary>
    /// Copies the current selection, falling back to the whole message when there is none.
    ///
    /// <para>Deliberately does NOT read <c>SelectableTextBlock.SelectedText</c>: that is the property
    /// carrying the defect this handler exists to fix — it returns "" (and disables the stock Copy)
    /// whenever the selection end runs past <c>Inlines.Text.Length</c>, which an
    /// <c>InlineUIContainer</c> on every row makes easy to do. <see cref="MessageTextSelection.Clamp"/>
    /// re-derives the substring with the clamp Avalonia omits.</para>
    /// </summary>
    private void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        // The MenuItem's DataContext is the row's MessageEntry (the ContextMenu is declared inside
        // the ItemTemplate), which is also the fallback when nothing is selected.
        var entry = (sender as Control)?.DataContext as MessageEntry;

        var text = string.Empty;
        if (FindOwningTextBlock(sender) is { } stb)
        {
            var source = stb.Inlines?.Text ?? stb.Text;
            text = MessageTextSelection.Clamp(source, stb.SelectionStart, stb.SelectionEnd);
        }

        if (string.IsNullOrEmpty(text) && entry is not null)
            text = MessageTextSelection.FormatEntry(entry);

        CopyToClipboard(text);
    }

    private void OnCopyAllMessagesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MessagesTool tool)
            CopyToClipboard(MessageTextSelection.FormatAll(tool.Messages));
    }

    /// <summary>
    /// Walks from the clicked MenuItem back to the SelectableTextBlock the ContextMenu belongs to.
    /// A ContextMenu is hosted in its own popup visual tree, so an ordinary ancestor walk from the
    /// MenuItem does not reach the row — <see cref="Control.Parent"/> on the menu itself does.
    /// </summary>
    private static SelectableTextBlock? FindOwningTextBlock(object? sender)
    {
        var current = sender as ILogical;
        while (current is not null)
        {
            if (current is ContextMenu menu)
                return menu.Parent as SelectableTextBlock;
            current = current.LogicalParent;
        }
        return null;
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        _ = clipboard.SetTextAsync(text);
    }
}
