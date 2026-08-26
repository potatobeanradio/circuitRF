using System.Collections.ObjectModel;
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

    // ---- "Reveal in file manager" ---------------------------------------------
    // The per-platform argument forms live in FileReveal, stated once — see RESOLVED.md §4 for
    // what a second, subtly-different copy of them cost.

    // Generates ClearMessagesCommand — bound in MessagesView.axaml as ClearMessagesCommand
    [RelayCommand]
    private void ClearMessages() => ((IMessageSink)this).Clear();

    [RelayCommand]
    private static void RevealFile(string? filePath) => FileReveal.Reveal(filePath);
}
