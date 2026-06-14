using System;

namespace CircuitRF.Ui.Messages;

public enum MessageTimestampMode { Time, DateTime, None }

/// <summary>App-wide message-timestamp display mode. Set at startup from AppPreferences and by the
/// Settings dialog; MessageEntry.TimeText reads it. ModeChanged lets the Messages view re-render.</summary>
public static class MessageDisplay
{
    private static MessageTimestampMode _mode = MessageTimestampMode.Time;
    public static MessageTimestampMode Mode
    {
        get => _mode;
        set { if (_mode == value) return; _mode = value; ModeChanged?.Invoke(null, EventArgs.Empty); }
    }
    public static event EventHandler? ModeChanged;
}

/// <summary>
/// A single message in the Messages region. FilePath (nullable) enables the clickable
/// "reveal in OS file manager" feature — any message with a file path shows it as an
/// underlined link that opens the OS file manager (Finder/Explorer/xdg-open).
/// </summary>
public sealed record MessageEntry(
    MessageLevel Level,
    string Text,
    string? FilePath,
    DateTime Timestamp)
{
    public string TimeText => MessageDisplay.Mode switch
    {
        MessageTimestampMode.None     => "",
        MessageTimestampMode.DateTime => Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
        _                             => Timestamp.ToString("HH:mm:ss"),
    };

    /// <summary>Message text with leading + trailing gaps, for inline rendering between the
    /// timestamp and the (separately clickable) file-path link.</summary>
    public string TextInline => "  " + Text + "  ";

    public static MessageEntry Info(string text, string? filePath = null)
        => new(MessageLevel.Info, text, filePath, DateTime.Now);

    public static MessageEntry Success(string text, string? filePath = null)
        => new(MessageLevel.Success, text, filePath, DateTime.Now);

    public static MessageEntry Warning(string text, string? filePath = null)
        => new(MessageLevel.Warning, text, filePath, DateTime.Now);

    public static MessageEntry Error(string text, string? filePath = null)
        => new(MessageLevel.Error, text, filePath, DateTime.Now);
}
