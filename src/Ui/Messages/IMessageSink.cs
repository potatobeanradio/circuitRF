namespace CircuitRF.Ui.Messages;

/// <summary>
/// Posts messages to the Messages region. All app-layer code (WorkspaceViewModel, run
/// controller, net-extraction validation) uses this interface; the engine itself never
/// calls it directly (it returns a DataSet; the UI layer reads the result and posts).
/// The implementation is MessagesTool (a Dock Tool VM), injected at startup.
/// </summary>
public interface IMessageSink
{
    void Post(MessageLevel level, string text, string? filePath = null);

    /// <summary>
    /// Posts a message that stays LIVE — its text and progress bar are rewritten in place while a long
    /// operation runs, and it settles into an ordinary message on
    /// <see cref="IProgressMessage.Complete"/>. The default posts the opening line and degrades to
    /// an ordinary start/finish pair, so a sink with no live-message support needs no changes.
    /// </summary>
    IProgressMessage BeginProgress(string text)
    {
        Post(MessageLevel.Info, text);
        return new PostOnlyProgressMessage(this);
    }

    void Info(string text, string? filePath = null)    => Post(MessageLevel.Info, text, filePath);
    void Success(string text, string? filePath = null) => Post(MessageLevel.Success, text, filePath);
    void Warning(string text, string? filePath = null) => Post(MessageLevel.Warning, text, filePath);
    void Error(string text, string? filePath = null)   => Post(MessageLevel.Error, text, filePath);

    void Clear();
}
