using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins;

/// <summary>One row of Settings ▸ Plugins ▸ Installed.</summary>
/// <param name="SourceText">Plugin list, GitHub, GitLab, Folder or Linked folder.</param>
/// <param name="UpdateVersion">The newer version the list offers, or null.</param>
/// <param name="OutsideNotAllowed">Installed by the command line or MCP and not asked yet — listed first and marked.</param>
/// <param name="Note">The sentence under the row: where an outside install came from, why it cannot run, or null.</param>
/// <param name="CanRollBack">An update replaced an earlier version that is still on disk.</param>
public sealed record PluginInstalledRow(
    PluginEntry Entry,
    string Id,
    string Name,
    string Version,
    string SourceText,
    PluginTrustDecision Permitted,
    string? UpdateVersion,
    bool OutsideNotAllowed,
    string? Note,
    bool CanRollBack,
    PluginActiveRecord? Record)
{
    public bool IsLinked => Entry.Source == PluginSource.Linked;
}

/// <summary>One row of Settings ▸ Plugins ▸ Available, from the vetted list.</summary>
/// <param name="Dim">Why it cannot be installed here — "needs circuitRF 1.9.0" — or null.</param>
public sealed record PluginAvailableRow(PluginIndexEntry Entry, PluginIndexVersion? Latest, string? Dim, string? InstalledVersion)
{
    public string Platforms => string.Join(", ", Entry.Platforms);
}

/// <summary>
/// What Settings ▸ Plugins lists (brief-plg-6 §4), worked out with no control in sight so a test can read it: the
/// catalogue, each installed plugin's <c>active.json</c>, the trust table <b>as the file holds it</b>, and the vetted
/// list. Reads files only — opening the tab makes no request (gate 5).
/// </summary>
public static class PluginsSettingsModel
{
    public const string OutsideMark = "Installed outside circuitRF — not yet allowed";

    public static IReadOnlyList<PluginInstalledRow> Installed(PluginCatalogSources sources, PluginTrustStore trust, PluginIndex index)
    {
        var rows = new List<PluginInstalledRow>();
        foreach (var e in PluginCatalog.Discover(sources))
        {
            var record = e.Source == PluginSource.Installed ? PluginInstaller.RecordFor(e.Directory) : null;
            var decision = trust.Decide(e.Directory);
            string version = e.Manifest?.Version ?? "-";
            bool outside = record is { OutsideWindow: true } && decision == PluginTrustDecision.Unknown;
            string? note = outside
                ? $"{OutsideMark}. From {OriginText(record!.Origin)}, {record.Installed.ToLocalTime():yyyy-MM-dd HH:mm}; {SourceDetail(record.Source)}."
                : e.DimReason is { } dim ? $"Cannot run here: {dim}."
                : record is { NeedsEnvironment: true } && record.Environment != PluginActiveRecord.Pending ? $"Its Python environment could not be made: {record.Environment}"
                : null;
            rows.Add(new PluginInstalledRow(
                e, e.Manifest?.Id ?? e.DisplayName, e.DisplayName, version, SourceText(e, record), decision,
                e.Manifest is { } m && record is not null ? PluginInstaller.UpdateAvailable(index, m.Id, m.Version, record) : null,
                outside, note,
                record?.Previous is { } prev && System.IO.Directory.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(e.Directory)!, prev)),
                record));
        }
        // An install the person has not seen yet comes first: this tab is where it becomes runnable.
        return rows.OrderBy(r => r.OutsideNotAllowed ? 0 : 1).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<PluginAvailableRow> Available(PluginIndex index, IReadOnlyList<PluginInstalledRow> installed)
        => index.Plugins
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var latest = p.LatestInstallable() ?? p.Latest;
                return new PluginAvailableRow(p, latest, latest?.Unavailable(),
                    installed.FirstOrDefault(r => r.Id == p.Id)?.Version);
            })
            .ToList();

    public static string SourceText(PluginEntry e, PluginActiveRecord? record)
    {
        if (e.Source == PluginSource.Linked) return "Linked folder";
        string s = record?.Source ?? "";
        return s == "list" ? "Plugin list"
             : s.StartsWith("https://github.com/", StringComparison.Ordinal) ? "GitHub"
             : s.StartsWith("https://gitlab.com/", StringComparison.Ordinal) ? "GitLab"
             : "Folder";
    }

    private static string OriginText(string origin) => origin switch
    {
        "cli" => "the command line", "mcp" => "an agent (MCP)", _ => "Settings",
    };

    /// <summary>Where it came from, short enough to sit on a row: a folder by its name (the full path is the row's
    /// tooltip), a repository by its address and tag.</summary>
    private static string SourceDetail(string source)
        => source == "list" ? "circuitRF's plugin list"
         : source.StartsWith("folder ", StringComparison.Ordinal)
             ? $"the folder {System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(source[7..]))}"
         : source;

    /// <summary>The picker's three answers, in its order.</summary>
    public static readonly string[] PermissionChoices = ["Ask", "Allowed", "Denied"];

    public static int PermissionIndex(PluginTrustDecision d) => d switch
    {
        PluginTrustDecision.Allowed => 1, PluginTrustDecision.Denied => 2, _ => 0,
    };
}
