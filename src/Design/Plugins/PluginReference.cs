namespace CircuitRF.Design.Plugins;

/// <summary>
/// What a plugin author — or an agent writing a plugin — may write (brief-plg-5 §3, <c>reference plugins</c>): the
/// sections of <c>docs/design/plugins.md</c> that ARE the contract, from the manifest (§3) to <c>window</c> plugins (§8),
/// embedded from the note itself so the reference and the design cannot say different things. The note's decisions,
/// installing, the vetted list and versioning are left out: they are about circuitRF, not about what a plugin sends.
/// </summary>
public static class PluginReference
{
    public const string Topic = "plugins";
    public const string Title = "Plugin contract";
    public const string Summary =
        "The plugin.json manifest and its parameter kinds, the panel's layout tree, the views a reply carries, the " +
        "frames and messages of the conversation, placement, the host services and window plugins — what a plugin " +
        "sends and receives. 'check <plugin folder>' validates a manifest against it.";

    private const string ResourceName = "CircuitRF.Design.Plugins.plugins.md";
    private const string From = "## 3. ";
    private const string Until = "## 9. ";

    private static readonly Lazy<string> _text = new(Load);

    /// <summary>The contract sections, as Markdown.</summary>
    public static string Text => _text.Value;

    private static string Load()
    {
        using var stream = typeof(PluginReference).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource \"{ResourceName}\" not found.");
        string all = new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n");
        int start = all.IndexOf("\n" + From, StringComparison.Ordinal);
        int end = all.IndexOf("\n" + Until, StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException("docs/design/plugins.md no longer has its §3 and §9 headings.");
        string body = all[(start + 1)..end].TrimEnd().TrimEnd('-').TrimEnd();
        return "# circuitRF plugin contract\n\n" +
               "From docs/design/plugins.md §3–§8. Every physical value is base SI with its unit; layout geometry is int64 DBU.\n\n" +
               body + "\n";
    }
}
