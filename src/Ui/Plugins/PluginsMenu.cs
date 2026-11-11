using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins;

/// <summary>
/// Tools ▸ Plugins, built from the catalogue (brief-plg-1 §6) — one rule for both surfaces, the in-window menu and
/// macOS's native one, so they cannot drift. Plugins are grouped under their manifest's <c>menu</c>; one that cannot
/// run here is listed dimmed with the reason as its tooltip. Building it reads manifests only.
/// </summary>
public static class PluginsMenu
{
    public const string Empty = "No plugins installed";

    /// <summary>The rows, grouped: (group or null for the top level, entries).</summary>
    public static IReadOnlyList<(string? Group, IReadOnlyList<PluginEntry> Entries)> Groups(IReadOnlyList<PluginEntry> entries)
        => entries
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Manifest?.Menu) ? null : e.Manifest!.Menu!.Trim())
            .OrderBy(g => g.Key is null ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, (IReadOnlyList<PluginEntry>)g.ToList()))
            .ToList();

    /// <summary>A <c>window</c> plugin's note: circuitRF starts it, and it draws itself (brief-plg-4 §3).</summary>
    public const string OwnWindowNote = "Opens its own window.";

    /// <summary>The tooltip a row shows: why it is dimmed, else its summary — and, for a <c>window</c> plugin, that it
    /// opens its own window.</summary>
    public static string Tip(PluginEntry e)
    {
        if (PluginHost.UnavailableReason(e) is { } why) return $"{e.DisplayName} {why}.";
        string tip = e.Manifest?.Summary ?? e.DisplayName;
        return e.Manifest?.Kind == PluginKind.Window ? $"{tip}\n{OwnWindowNote}" : tip;
    }

    /// <summary>Refills an in-window submenu. Its <c>Items</c> are replaced; the item itself is kept.</summary>
    public static void Fill(MenuItem target, IReadOnlyList<PluginEntry> entries, Action<PluginEntry> open)
    {
        target.Items.Clear();
        if (entries.Count == 0) { target.Items.Add(new MenuItem { Header = Empty, IsEnabled = false }); return; }

        foreach (var (group, rows) in Groups(entries))
        {
            ItemCollection into = target.Items;
            if (group is not null)
            {
                var sub = new MenuItem { Header = group };
                target.Items.Add(sub);
                into = sub.Items;
            }
            foreach (var e in rows)
            {
                var item = new MenuItem { Header = e.DisplayName, IsEnabled = PluginHost.UnavailableReason(e) is null };
                ToolTip.SetTip(item, Tip(e));
                var entry = e;
                item.Click += (_, _) => open(entry);
                into.Add(item);
            }
        }
    }

    /// <summary>Refills a native submenu. Only its <c>Items</c> change — a window's <c>NativeMenu</c> objects are fixed
    /// for its lifetime (<c>src/Ui/CLAUDE.md</c>).</summary>
    public static void Fill(NativeMenu target, IReadOnlyList<PluginEntry> entries, Action<PluginEntry> open)
    {
        target.Items.Clear();
        if (entries.Count == 0) { target.Items.Add(new NativeMenuItem(Empty) { IsEnabled = false }); return; }

        foreach (var (group, rows) in Groups(entries))
        {
            var into = target;
            if (group is not null)
            {
                into = new NativeMenu();
                target.Items.Add(new NativeMenuItem(group) { Menu = into });
            }
            foreach (var e in rows)
            {
                var item = new NativeMenuItem(e.DisplayName) { IsEnabled = PluginHost.UnavailableReason(e) is null, ToolTip = Tip(e) };
                var entry = e;
                item.Click += (_, _) => open(entry);
                into.Items.Add(item);
            }
        }
    }
}
