using System.Globalization;
using System.Text;

namespace CircuitRF.Diagnostics;

/// <summary>How much the reader is expected to do about a diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The run explaining itself. Nothing to act on.</summary>
    Info,
    /// <summary>Something the user should look at, but the operation completed.</summary>
    Warning,
    /// <summary>The operation did not do what was asked.</summary>
    Error,
}

/// <summary>
/// A user-facing diagnostic authored BELOW the UI firewall, carried as an id plus typed arguments
/// rather than as a finished English sentence.
///
/// <para><b>Why this exists, in the order the reasons actually matter.</b> Today the only thing that
/// crosses the firewall is the text — an English sentence authored in the numeric layer and, in 118
/// places, laundered through <c>ex.Message</c> into a <c>Messages.Warning</c>/<c>Error</c> call.
/// Because text is all that arrives, the Messages window cannot:</para>
/// <list type="bullet">
///   <item><b>Filter or group by kind.</b> "Show me every technology-resolution failure" is a
///   substring search over prose, and it breaks when someone rewords the prose.</item>
///   <item><b>Deduplicate.</b> A sweep that refuses at 400 points posts 400 near-identical lines
///   that differ only in the numbers inside them. With an id, that collapses to one line and a
///   count.</item>
///   <item><b>Attach an action.</b> A diagnostic that knows it is
///   <c>em.layout.not-under-workspace</c> can offer the walk-up path as a link. A string cannot: by
///   the time it is a sentence, the structure that would have driven the link is gone.</item>
///   <item><b>Be asserted on robustly.</b> A test that pins a sentence fails when the sentence is
///   improved, which teaches people not to improve sentences.</item>
/// </list>
///
/// <para><b>Localizability is the fourth benefit, not the first.</b> All four above are worth having
/// in a product that ships only ever in English, and this type is justified on them alone. That it
/// also puts a resource lookup within reach — at exactly one place, the render point in
/// <c>src/Ui</c> — is a bonus that costs nothing here.</para>
///
/// <para><b>The English default template is carried alongside, always.</b> That is what makes this
/// shippable in one step rather than as a migration: the UI renders <see cref="Render"/> today with
/// no resource lookup and no catalogue to populate, and a lookup can be inserted later without
/// touching a single producer. It is also permanent, not scaffolding — see the CLI note below.</para>
///
/// <para><b>The CLI renders this template forever, in English, regardless of any future language
/// setting.</b> A localized CLI error breaks every user's <c>grep</c>, log scraper and CI job. The
/// GUI localizes; the CLI does not. See <c>docs/design/cli.md</c>.</para>
///
/// <para><b>Rendering is culture-invariant</b> for the same reason: a number inside a diagnostic
/// that reaches stderr must not acquire a comma decimal because of where the machine is. A future
/// localized GUI render is free to format per-locale, at the render point — it is a different
/// method, not this one.</para>
/// </summary>
/// <param name="Id">
/// A stable, dotted, lower-kebab identifier — <c>em.layout.not-found</c>. Stable is the operative
/// word: it is what dedup, filtering and any future resource lookup key on, so it outlives every
/// rewording of the template. Change the template freely; change the id and you have made a new
/// diagnostic.
/// </param>
/// <param name="Severity">How much the reader is expected to do about it.</param>
/// <param name="DefaultTemplate">
/// The English sentence, with <c>{name}</c> placeholders naming entries in <see cref="Arguments"/>.
/// A placeholder with no matching argument is left verbatim rather than throwing — a diagnostic
/// reporting a failure must never fail while being reported.
/// </param>
/// <param name="Arguments">
/// The values the template interpolates, by name. These are the typed half: a consumer that wants
/// the layout's path, or the frequency that was out of range, reads it here instead of parsing the
/// sentence back apart.
/// </param>
public sealed record Diagnostic(
    string Id,
    DiagnosticSeverity Severity,
    string DefaultTemplate,
    IReadOnlyDictionary<string, object?> Arguments)
{
    private static readonly IReadOnlyDictionary<string, object?> NoArguments =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>An argument-free diagnostic — the template is already the whole sentence.</summary>
    public Diagnostic(string id, DiagnosticSeverity severity, string defaultTemplate)
        : this(id, severity, defaultTemplate, NoArguments) { }

    /// <summary>
    /// Builds one with named arguments, in the shape producers actually want to write:
    /// <c>Diagnostic.Create("em.layout.not-found", DiagnosticSeverity.Error, "The layout '{layoutRef}' …",
    /// ("layoutRef", setup.LayoutRef))</c>.
    /// </summary>
    public static Diagnostic Create(
        string id,
        DiagnosticSeverity severity,
        string defaultTemplate,
        params (string Name, object? Value)[] arguments)
    {
        var map = new Dictionary<string, object?>(arguments.Length, StringComparer.Ordinal);
        foreach (var (name, value) in arguments) map[name] = value;
        return new Diagnostic(id, severity, defaultTemplate, map);
    }

    /// <summary>
    /// The English sentence — what the CLI writes to stderr, permanently, and what the Messages
    /// window shows until a localized catalogue exists.
    ///
    /// <para>Invariant by construction: see the type's remarks. An unmatched <c>{placeholder}</c> is
    /// left as written rather than throwing.</para>
    /// </summary>
    public string Render()
    {
        if (DefaultTemplate.IndexOf('{') < 0) return DefaultTemplate;

        var sb = new StringBuilder(DefaultTemplate.Length + 32);
        int i = 0;
        while (i < DefaultTemplate.Length)
        {
            char c = DefaultTemplate[i];
            if (c != '{') { sb.Append(c); i++; continue; }

            int close = DefaultTemplate.IndexOf('}', i + 1);
            if (close < 0) { sb.Append(DefaultTemplate, i, DefaultTemplate.Length - i); break; }

            string name = DefaultTemplate[(i + 1)..close];
            if (Arguments.TryGetValue(name, out var value))
                sb.Append(Format(value));
            else
                sb.Append(DefaultTemplate, i, close - i + 1);   // leave it verbatim

            i = close + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Invariant formatting for every argument type. <see cref="IFormattable"/> covers the numeric
    /// and date types in one line; anything else is already its own string.
    /// </summary>
    private static string Format(object? value) => value switch
    {
        null           => "",
        string s       => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _              => value.ToString() ?? "",
    };

    /// <summary>So a diagnostic dropped into an interpolated string does the obvious thing.</summary>
    public override string ToString() => Render();
}
