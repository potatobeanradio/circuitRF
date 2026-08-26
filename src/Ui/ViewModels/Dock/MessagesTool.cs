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

    /// <summary>
    /// The live-message implementation: one real <see cref="MessageEntry"/> in the list, rewritten in
    /// place. Every mutation is marshalled to the UI thread exactly like <see cref="Post"/>, because
    /// the engine reports progress from the background thread it is running on.
    /// </summary>
    public IProgressMessage BeginProgress(string text)
    {
        var entry = new MessageEntry(MessageLevel.Info, text, null, System.DateTime.Now)
        {
            // Starts indeterminate: at the moment the run is announced nothing has reported a
            // denominator yet, and a bar sitting at 0% reads as stalled rather than starting.
            ProgressIndeterminate = true,
            ProgressPercent       = 0,
        };

        OnUi(() => Messages.Add(entry));
        return new LiveProgressMessage(entry, OnUi);
    }

    private static void OnUi(System.Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
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
        if (string.IsNullOrWhiteSpace(filePath)) return;

        bool isDir  = Directory.Exists(filePath);
        bool isFile = !isDir && File.Exists(filePath);
        if (!isDir && !isFile) return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // -R selects a file; bare open opens a directory.
                //
                // ArgumentList, never the single-string overload: on Unix .NET PARSES that string
                // into argv itself, honouring quotes, so a file whose NAME contains a double quote
                // closes ours and everything after it becomes further arguments to `open` — and
                // `open` takes `-a <application>` (security review, 2026-08-25). The path arrives
                // from a message-panel entry, i.e. from whatever a workspace or a kit put on disk.
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                if (isFile) psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(filePath);
                Process.Start(psi);
            }
            else if (OperatingSystem.IsWindows())
            {
                // /select highlights a file; bare path opens a directory. Explorer wants
                // `/select,<path>` as ONE argument, which is why this is not two.
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                psi.ArgumentList.Add(isFile ? $"/select,{filePath}" : filePath);
                Process.Start(psi);
            }
            else
            {
                // Linux: xdg-open on the directory (or containing directory for files).
                var target = isDir ? filePath : Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(target))
                    Process.Start("xdg-open", target);
            }
        }
        catch { /* Non-critical: ignore if the reveal fails. */ }
    }
}
