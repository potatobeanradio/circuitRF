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

    void Info(string text, string? filePath = null)    => Post(MessageLevel.Info, text, filePath);
    void Success(string text, string? filePath = null) => Post(MessageLevel.Success, text, filePath);
    void Warning(string text, string? filePath = null) => Post(MessageLevel.Warning, text, filePath);
    void Error(string text, string? filePath = null)   => Post(MessageLevel.Error, text, filePath);

    void Clear();
}
