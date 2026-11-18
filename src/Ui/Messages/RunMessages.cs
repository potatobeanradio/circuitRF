using Avalonia.Controls;
using CircuitRF.Design.Circuit;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Messages;

/// <summary>What a finished run posts to the Messages panel beside its own row: what needs reading on rows of its own, and a
/// radiation pattern's explanations (<see cref="RunNotes"/>) behind one.</summary>
public static class RunMessages
{
    /// <summary>
    /// The run's notes and warnings, then the folded pattern notes, then each analysis that failed beside one that finished.
    /// A failure whose missing file a document in the workspace writes carries an Open button for that document
    /// (<paramref name="open"/>); a run where every analysis failed reports its errors on its own row, which has no button, so
    /// each such writer gets a row of its own.
    /// </summary>
    public static void Post(IMessageSink sink, RunResult result, Func<Window?> owner, Func<string, bool>? open = null)
    {
        var pattern = new List<string>();
        foreach (var n in result.Notes)
        {
            if (RunNotes.IsDetail(n)) pattern.Add(n);
            else sink.Post(MessageLevel.Info, n);
        }
        foreach (var w in result.Warnings) sink.Post(MessageLevel.Warning, w);
        PostPatternNotes(sink, pattern, owner);
        foreach (var e in result.Errors)
        {
            if (open is not null && result.FileWriters.FirstOrDefault(w => w.Error == e) is { } w)
                sink.PostAction(MessageLevel.Error, e, "Open", Opening(open, w.DocumentPath), w.DocumentPath);
            else
                sink.Post(MessageLevel.Error, e);
        }
        if (result.Status == RunStatus.EngineError && open is not null)
            foreach (var w in result.FileWriters)
                sink.PostAction(MessageLevel.Info, w.Sentence, "Open", Opening(open, w.DocumentPath), w.DocumentPath);
    }

    private static Func<Task> Opening(Func<string, bool> open, string document) => () =>
    {
        open(document);
        return Task.CompletedTask;
    };

    /// <summary>The level of a successful run's own row: a warning when an analysis in it failed.</summary>
    public static MessageLevel OutcomeLevel(RunResult result) => result.Errors.Count > 0 ? MessageLevel.Warning : MessageLevel.Success;

    /// <summary>One row for <paramref name="notes"/>, with the notes a click away; nothing for none.</summary>
    public static void PostPatternNotes(IMessageSink sink, IReadOnlyList<string> notes, Func<Window?> owner)
    {
        if (notes.Count == 0) return;
        string sentence = Sentence(notes);
        sink.PostAction(MessageLevel.Info, sentence, "Details…",
            () => TextConfirmDialog.AskAsync(owner(), "Radiation pattern", sentence, Text(notes), null));
    }

    public static string Sentence(IReadOnlyList<string> notes) => $"Radiation pattern: {notes.Count} more note{(notes.Count == 1 ? "" : "s")}.";

    public static string Text(IReadOnlyList<string> notes) => string.Join(Environment.NewLine + Environment.NewLine, notes);
}
