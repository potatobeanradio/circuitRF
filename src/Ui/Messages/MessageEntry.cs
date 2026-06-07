using System;

namespace CircuitRF.Ui.Messages;

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
    public static MessageEntry Info(string text, string? filePath = null)
        => new(MessageLevel.Info, text, filePath, DateTime.Now);

    public static MessageEntry Success(string text, string? filePath = null)
        => new(MessageLevel.Success, text, filePath, DateTime.Now);

    public static MessageEntry Warning(string text, string? filePath = null)
        => new(MessageLevel.Warning, text, filePath, DateTime.Now);

    public static MessageEntry Error(string text, string? filePath = null)
        => new(MessageLevel.Error, text, filePath, DateTime.Now);
}
