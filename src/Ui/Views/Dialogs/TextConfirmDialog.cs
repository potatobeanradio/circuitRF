using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// A question over a long, selectable text, answered by one confirm button or Cancel — the shape of the
/// 3D solver install consent (brief-em3d-24) and of every removal confirmation (brief-em3d-25). Closing the
/// window is Cancel. Selectable, because the paths and URLs are exactly what someone will want to copy.
/// </summary>
public static class TextConfirmDialog
{
    /// <summary>
    /// Asks. With <paramref name="owner"/> null the window is shown on its own rather than as a dialog —
    /// the Windows Apps-list uninstall starts circuitRF with no window to own it. With
    /// <paramref name="confirmLabel"/> null it only informs: one Close button, and the answer is false.
    /// </summary>
    public static async Task<bool> AskAsync(Window? owner, string title, string heading, string text, string? confirmLabel)
    {
        bool yes = false;
        var dialog = new Window
        {
            Title                 = title,
            Width                 = 640,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            CanResize             = false,
        };

        var cancel  = new Button { Content = confirmLabel is null ? "Close" : "Cancel", IsCancel = true, IsDefault = confirmLabel is null };
        cancel.Click  += (_, _) => { yes = false; dialog.Close(); };
        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Children            = { cancel },
        };
        if (confirmLabel is not null)
        {
            var confirm = new Button { Content = confirmLabel, IsDefault = true };
            confirm.Click += (_, _) => { yes = true; dialog.Close(); };
            buttons.Children.Add(confirm);
        }

        dialog.Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                },
                buttons,
            },
        };

        if (owner is not null) await dialog.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }
        return yes;
    }
}
