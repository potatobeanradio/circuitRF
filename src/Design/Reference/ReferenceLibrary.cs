using System.Linq;
using System.Reflection;

namespace CircuitRF.Design.Reference;

/// <summary>One reference topic: the name a caller asks for, and how to find its bytes.</summary>
/// <param name="Topic">The name on the command line and in the resource URI — the authored file's
/// own stem, so the topic and the page cannot drift apart by being renamed on one side.</param>
/// <param name="Title">A short English title, read out of the page's own front matter.</param>
/// <param name="Summary">The page's own <c>lede</c>, likewise.</param>
public sealed record ReferenceTopic(string Topic, string Title, string Summary, string ResourceName);

/// <summary>
/// The authored reference pages, shipped inside the assembly.
///
/// <para><b>Why they are embedded at all.</b> <c>docs/user/src/</c> is repository content and is not
/// in an installed tree, so a capability that read them off disk would work on a developer's machine
/// and answer nothing anywhere else (R-aut6-5).</para>
///
/// <para><b>Plain .NET <c>EmbeddedResource</c>, exactly as <see cref="CircuitRF.Design.Layout.ShippedTechnologies"/>
/// does it</b> — never Avalonia's <c>AssetLoader</c>, whose <c>Open</c> throws with no live platform.
/// <see cref="Assembly.GetManifestResourceStream"/> has no such requirement and works identically in
/// the desktop app, in <c>circuitrf reference</c> and in headless tests.</para>
///
/// <para><b>The <c>&lt;EmbeddedResource&gt;</c> items and this class ship in the same commit.</b>
/// That is not a convention: <c>ShippedTechnologies</c>' own header records the trap, which has
/// already been paid for once — a class moved without its resources compiles, enumerates nothing and
/// reports nothing. <c>ReferenceLibraryTests</c> is the gate, and it fails if a topic is added to
/// <see cref="Topics"/> and not to the <c>.csproj</c>.</para>
///
/// <para><b>The embedded bytes are the authored file's own.</b> The <c>.csproj</c> references
/// <c>docs/user/src/reference/*.md</c> in place rather than copying them into the project, because a
/// copy is a file that will be edited on one side only and nothing will report it (R-aut6-6). The
/// YAML front matter and the documentation generator's <c>{{…}}</c> placeholders are stripped at
/// READ, not at build, so what is embedded stays byte-identical to what is authored.</para>
///
/// <para><b>The topic set is curated, not "every page".</b> Reading is the expensive direction
/// (<c>automation-architecture.md</c> R-aut-10) and a client pays for every byte, so this is the
/// authoring critical path and nothing else. <c>cli.md</c> is excluded deliberately: at 54 kB it is
/// the largest page of them all, and a protocol client already has every verb's schema from
/// <c>tools/list</c>, so it is the one page it needs least (R-aut6-2).</para>
///
/// <para><b>English and invariant, like every other machine-readable surface</b> (<c>cli.md</c> §7A).
/// There is no localisation here and there will not be.</para>
/// </summary>
public static class ReferenceLibrary
{
    /// <summary>
    /// The topics, in the order a client should read them: the format first, then the language it is
    /// written in, then the things a document refers to.
    ///
    /// <para><b><c>components</c> is NOT here.</b> The prose page under that name would collide with
    /// the generated catalogue, which is what a caller asking about a component actually wants —
    /// so the page ships as <see cref="ComponentNotesTopic"/> and the catalogue keeps the plain
    /// name. See <c>src/Cli/RESOLVED.md</c>.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> TopicNames =
    [
        "netlist",
        "expressions",
        "units",
        "measurements",
        "pins-ports-terms",
        "sdd",
        "file-formats",
        "cmodel",
        "model-data",
        ModelLibraryTopic,
        // brief vendorlib-15 R-vl15-4b: for a device maker deciding whether to publish a protected model.
        "protected-models",
        ComponentNotesTopic,
    ];

    /// <summary>
    /// What <c>components.md</c> is served as.
    ///
    /// <para>The page and the generated catalogue answer two different questions — the catalogue
    /// says what a caller may WRITE, the page says what it MEANS (R-aut6-8) — and both were called
    /// <c>components</c> in the brief. They cannot both be, so the machine answer keeps the plain
    /// name and the prose takes this one. Nothing is dropped: a client that wants both asks for
    /// both, and each says so in its own first line.</para>
    /// </summary>
    public const string ComponentNotesTopic = "component-notes";

    /// <summary>
    /// What <c>model-library-format.md</c> is served as (brief vendorlib-6 R-vl6-3b). The page's name says
    /// what it is among the user docs, where "model library" alone would read as a page about using one;
    /// a caller asking the reference tool is already asking about a format.
    /// </summary>
    public const string ModelLibraryTopic = "model-library";

    private static readonly Lazy<IReadOnlyList<ReferenceTopic>> _topics = new(Discover);

    /// <summary>Every embedded topic, in <see cref="TopicNames"/> order.</summary>
    public static IReadOnlyList<ReferenceTopic> Topics => _topics.Value;

    public static ReferenceTopic? Find(string topic)
        => Topics.FirstOrDefault(t => string.Equals(t.Topic, topic, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The topic's text, as a caller receives it: the authored Markdown with its front matter and
    /// the documentation generator's placeholders removed.
    /// </summary>
    public static string Read(ReferenceTopic topic) => Strip(Raw(topic));

    /// <summary>The embedded bytes, untouched — what the gate compares against the authored file.</summary>
    public static string Raw(ReferenceTopic topic)
    {
        var asm = typeof(ReferenceLibrary).Assembly;
        using var stream = asm.GetManifestResourceStream(topic.ResourceName)
            ?? throw new InvalidOperationException($"Embedded reference resource \"{topic.ResourceName}\" not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── reading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes what addresses the site generator rather than a reader: the YAML front matter, and
    /// the <c>{{symbol: …}}</c> / <c>{{table: …}}</c> / <c>{{anchor: …}}</c> placeholders the docs
    /// factory expands.
    ///
    /// <para><b>At read, not at build</b> (R-aut6-6) — so the embedded bytes stay the authored
    /// file's own and the gate can compare them.</para>
    ///
    /// <para><b>A placeholder is removed rather than expanded.</b> <c>DocGen</c> lives in
    /// <c>tools/</c>, is referenced by nothing that ships, and dragging its expander below the
    /// firewall is a much larger change than this. The figures it inserts are images, which a text
    /// client cannot use anyway; the tables it inserts are the generated component parameters, which
    /// a caller gets from the catalogue in a form it can actually read. The page's own opening
    /// paragraph says so.</para>
    /// </summary>
    public static string Strip(string markdown)
    {
        string text = StripFrontMatter(markdown);

        var sb    = new System.Text.StringBuilder(text.Length);
        int start = 0;
        while (true)
        {
            int open = text.IndexOf("{{", start, StringComparison.Ordinal);
            if (open < 0) { sb.Append(text, start, text.Length - start); break; }
            int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { sb.Append(text, start, text.Length - start); break; }

            sb.Append(text, start, open - start);
            start = close + 2;

            // A placeholder alone on its line takes the line with it, so removing it does not leave
            // a blank line where a figure used to be — three of those in a row read as a missing
            // section rather than as an omitted picture.
            if (IsAloneOnItsLine(text, open, start))
            {
                while (start < text.Length && (text[start] == '\r' || text[start] == '\n'))
                {
                    bool newline = text[start] == '\n';
                    start++;
                    if (newline) break;
                }
                TrimTrailingBlankLine(sb);
            }
        }
        return sb.ToString();
    }

    private static bool IsAloneOnItsLine(string text, int open, int afterClose)
    {
        for (int i = open - 1; i >= 0 && text[i] != '\n'; i--)
            if (text[i] is not (' ' or '\t' or '\r')) return false;
        for (int i = afterClose; i < text.Length && text[i] != '\n'; i++)
            if (text[i] is not (' ' or '\t' or '\r')) return false;
        return true;
    }

    private static void TrimTrailingBlankLine(System.Text.StringBuilder sb)
    {
        int end = sb.Length;
        while (end > 0 && (sb[end - 1] == ' ' || sb[end - 1] == '\t')) end--;
        sb.Length = end;
    }

    /// <summary>
    /// Drops a leading <c>---</c>-delimited YAML block. It addresses the site generator — a slug, a
    /// breadcrumb, a doc-kind — and not a reader, and the two fields worth keeping are lifted into
    /// <see cref="ReferenceTopic.Title"/> and <see cref="ReferenceTopic.Summary"/> instead.
    /// </summary>
    private static string StripFrontMatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal)) return markdown;
        int first = markdown.IndexOf('\n');
        if (first < 0) return markdown;

        int close = markdown.IndexOf("\n---", first, StringComparison.Ordinal);
        if (close < 0) return markdown;

        int after = markdown.IndexOf('\n', close + 1);
        return after < 0 ? "" : markdown[(after + 1)..].TrimStart('\n');
    }

    private static string FrontMatterField(string markdown, string field)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal)) return "";
        int close = markdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (close < 0) return "";

        foreach (string line in markdown[..close].Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith(field + ":", StringComparison.Ordinal)) continue;
            return trimmed[(field.Length + 1)..].Trim();
        }
        return "";
    }

    // ── discovery ────────────────────────────────────────────────────────────

    /// <summary>
    /// The manifest-name prefix every page is embedded under. Stated in the <c>.csproj</c> as an
    /// explicit <c>LogicalName</c> rather than left to MSBuild's default, because the files are
    /// referenced IN PLACE from <c>docs/user/src/reference/</c> — outside this project's own cone —
    /// and the default name for such an item is derived from a relative path nobody should have to
    /// predict (R-aut6-6).
    /// </summary>
    private const string ResourcePrefix = "CircuitRF.Design.Reference.";

    /// <summary>The authored file behind a topic. Two differ from their own names, and
    /// <see cref="ComponentNotesTopic"/> and <see cref="ModelLibraryTopic"/> say why.</summary>
    public static string FileStem(string topic) => topic switch
    {
        ComponentNotesTopic => "components",
        ModelLibraryTopic   => "model-library-format",
        _                   => topic,
    };

    private static IReadOnlyList<ReferenceTopic> Discover()
    {
        var found = new List<ReferenceTopic>(TopicNames.Count);
        foreach (string topic in TopicNames)
        {
            var    entry = new ReferenceTopic(topic, "", "", ResourcePrefix + FileStem(topic) + ".md");
            string raw   = Raw(entry);
            found.Add(entry with
            {
                Title   = FrontMatterField(raw, "title"),
                Summary = FrontMatterField(raw, "lede"),
            });
        }
        return found;
    }
}
