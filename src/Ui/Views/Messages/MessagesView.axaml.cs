using Avalonia.Controls;
using Avalonia.VisualTree;
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

    private void ScrollToBottom()
    {
        // Find the ListBox and scroll its ScrollViewer to the end.
        var listBox = this.FindControl<ListBox>("MessagesListBox");
        if (listBox is null) return;
        var scroll = listBox.FindDescendantOfType<ScrollViewer>();
        scroll?.ScrollToEnd();
    }
}
