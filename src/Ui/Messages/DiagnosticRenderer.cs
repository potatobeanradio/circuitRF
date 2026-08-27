using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Messages;

/// <summary>
/// THE render point: the single place in circuitRF where a <see cref="Diagnostic"/> — an id, typed
/// arguments and an English template, authored below the UI firewall — becomes user-visible text.
///
/// <para><b>Why it is worth having exactly one.</b> Three things want to live here, and each of them
/// is cheap at one site and unmanageable at 118:</para>
/// <list type="number">
///   <item><b>A resource lookup, if circuitRF is ever localized.</b> The insertion is a single
///   branch in <see cref="Render"/> — look the id up in a catalogue, fall back to
///   <see cref="Diagnostic.DefaultTemplate"/> when it is missing. No producer changes, no
///   half-translated state, and an untranslated diagnostic degrades to English rather than to
///   nothing.</item>
///   <item><b>Dedup by id.</b> A sweep that refuses at 400 points posts 400 lines differing only in
///   their numbers. Collapsing them to one line and a count needs the id, and needs to happen where
///   the posting happens.</item>
///   <item><b>Filter and group by kind.</b> Same reason — the id has to survive to the window, and
///   this is the last place it exists before the text does.</item>
/// </list>
///
/// <para><b>None of that is built here yet, and that is the point.</b> This ships as a plain render
/// so the conversion is one step rather than a migration; what it buys today is that the id has
/// somewhere to arrive, and the three features above become local changes instead of another
/// 118-site sweep.</para>
///
/// <para><b>The CLI does not come through here.</b> It calls <see cref="Diagnostic.Render"/>
/// directly and writes English forever — a localized CLI error breaks every user's grep, log
/// scraper and CI job. See <c>docs/design/cli.md</c> §8.</para>
/// </summary>
public static class DiagnosticRenderer
{
    /// <summary>
    /// The diagnostic's user-visible text. Today this is the English default template with its
    /// arguments substituted; a future localized build looks the id up here first.
    /// </summary>
    public static string Render(Diagnostic diagnostic) => diagnostic.Render();

    /// <summary>Maps a diagnostic's severity onto the Messages window's own levels.</summary>
    public static MessageLevel LevelOf(Diagnostic diagnostic) => diagnostic.Severity switch
    {
        DiagnosticSeverity.Info    => MessageLevel.Info,
        DiagnosticSeverity.Warning => MessageLevel.Warning,
        _                          => MessageLevel.Error,
    };
}

/// <summary>Posting a diagnostic, rather than a sentence, to any message sink.</summary>
public static class MessageSinkDiagnosticExtensions
{
    /// <summary>
    /// Posts <paramref name="diagnostic"/> through <see cref="DiagnosticRenderer"/>, at the level its
    /// own severity implies. Prefer this over rendering at the call site: the id reaching this method
    /// is what a later dedup or filter has to work with.
    /// </summary>
    public static void PostDiagnostic(this IMessageSink sink, Diagnostic diagnostic, string? filePath = null)
        => sink.Post(DiagnosticRenderer.LevelOf(diagnostic), DiagnosticRenderer.Render(diagnostic), filePath);
}
