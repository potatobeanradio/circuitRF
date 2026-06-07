using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Messages region (bottom). Implements IMessageSink so all app-layer
/// code can post messages here without referencing Avalonia directly.
/// Color + icon coded (never color alone — accessibility). Clickable file links reveal
/// the file in the OS file manager.
/// </summary>
public partial class MessagesTool : Tool, IMessageSink
{
    public ObservableCollection<MessageEntry> Messages { get; } = new();

    public MessagesTool()
    {
        Id    = "Messages";
        Title = "Messages";
    }

    // ---- IMessageSink ----------------------------------------------------------

    public void Post(MessageLevel level, string text, string? filePath = null)
    {
        var entry = new MessageEntry(level, text, filePath, System.DateTime.Now);

        // Always post on the UI thread — callers may be on background threads in 6e.
        if (Dispatcher.UIThread.CheckAccess())
            Messages.Add(entry);
        else
            Dispatcher.UIThread.Post(() => Messages.Add(entry));
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Messages.Clear();
        else
            Dispatcher.UIThread.Post(Messages.Clear);
    }

    // ---- "Reveal in file manager" -- OS-specific file reveal -------------------

    // Generates ClearMessagesCommand — bound in MessagesView.axaml as ClearMessagesCommand
    [RelayCommand]
    private void ClearMessages() => ((IMessageSink)this).Clear();

    [RelayCommand]
    private static void RevealFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // open -R reveals the file in Finder with it selected.
                Process.Start("open", $"-R \"{filePath}\"");
            }
            else if (OperatingSystem.IsWindows())
            {
                // /select highlights the file; explorer.exe is in PATH on Windows.
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else
            {
                // Linux: xdg-open on the containing directory.
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start("xdg-open", dir);
            }
        }
        catch { /* Non-critical: ignore if the reveal fails. */ }
    }
}
