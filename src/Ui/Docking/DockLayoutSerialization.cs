using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// Reads and writes the <c>.cws</c> dock-layout block.
///
/// <para><b>Why the block is stored as a <see cref="JsonNode"/> rather than a typed property on
/// <c>CwsFile</c>:</b> R-dock-5 — a layout problem must never prevent a workspace from opening. A
/// strongly-typed property makes a structurally wrong block (a string where an array belongs, a
/// number where an object belongs) throw during the <i>whole file's</i> deserialization, which would
/// take the tree state and the open-document list down with it. Parsing the block separately, behind
/// its own try/catch, contains the blast radius to the one thing that is actually broken. It also
/// means a block written by a NEWER build round-trips through an older one verbatim instead of being
/// silently rewritten to a lossy subset.</para>
/// </summary>
public static class DockLayoutSerialization
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Drops anything a document region cannot mean: a leaf with no documents, a split with fewer
    /// than two surviving children (which is just its child, or nothing), an unrecognised
    /// orientation, and a proportion outside (0,1). Bounded in depth so a hand-authored cycle-shaped
    /// file cannot spin here.
    /// </summary>
    private static CwsDocumentRegion? SanitizeRegion(CwsDocumentRegion? node, int depth = 0)
    {
        if (node is null || depth > 16) return null;

        node.Children  ??= [];
        node.Documents ??= [];

        // NaN is Dock's "no explicit proportion", and NaN fails EVERY comparison — it would slip
        // through a plain range check and then make System.Text.Json throw on write.
        if (!double.IsFinite(node.Proportion) || node.Proportion is <= 0.0 or >= 1.0) node.Proportion = 0.0;

        if (node.Children.Count == 0)
        {
            node.Documents = node.Documents.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (node.Documents.Count == 0) return null;
            node.Orientation = null;
            if (node.Active is not null && !node.Documents.Contains(node.Active)) node.Active = null;
            return node;
        }

        var kept = node.Children
            .Select(c => SanitizeRegion(c, depth + 1))
            .OfType<CwsDocumentRegion>()
            .ToList();

        if (kept.Count == 0) return null;
        if (kept.Count == 1) return kept[0];

        node.Children   = kept;
        node.Documents  = [];
        node.Active     = null;
        node.Orientation = node.Orientation == "Vertical" ? "Vertical" : "Horizontal";
        return node;
    }

    /// <summary>Outcome of reading a layout block. <see cref="Layout"/> null = use the default layout.</summary>
    /// <param name="Layout">The parsed layout, or null when absent/unusable.</param>
    /// <param name="Report">A user-facing reason when the block was present but unusable; null when
    /// there is nothing to say (absent block — R-dock-4's "opens on the default layout, silently").</param>
    public readonly record struct ReadResult(CwsDockLayout? Layout, string? Report);

    /// <summary>
    /// Parses the block. Never throws.
    /// <list type="bullet">
    /// <item>null node → <c>(null, null)</c> — no block, default layout, silently.</item>
    /// <item>version &gt; <see cref="CwsDockLayout.CurrentVersion"/> → <c>(null, report)</c>.</item>
    /// <item>malformed → <c>(null, report)</c>.</item>
    /// </list>
    /// </summary>
    public static ReadResult TryRead(JsonNode? node)
    {
        if (node is null) return new ReadResult(null, null);

        CwsDockLayout? layout;
        try
        {
            layout = node.Deserialize<CwsDockLayout>(Opts);
        }
        catch (Exception ex)
        {
            return new ReadResult(null, $"Saved window layout could not be read; using the default layout. ({ex.Message})");
        }

        if (layout is null)
            return new ReadResult(null, "Saved window layout was empty; using the default layout.");

        if (layout.Version > CwsDockLayout.CurrentVersion)
            return new ReadResult(null,
                $"Saved window layout is version {layout.Version}, newer than this build understands " +
                $"(version {CwsDockLayout.CurrentVersion}); using the default layout. Your layout is left " +
                "unchanged on disk unless you rearrange the panels.");

        if (layout.Version < 1)
            return new ReadResult(null, $"Saved window layout has an invalid version ({layout.Version}); using the default layout.");

        // Null collections are legal input (a hand-edited file, or a future version that dropped a
        // section); normalize so every consumer can iterate without null checks.
        layout.Screens                 ??= [];
        layout.Panels                  ??= [];
        layout.Sides                   ??= [];
        layout.FloatingWindows         ??= [];
        layout.FloatingDocumentWindows ??= [];
        layout.DocumentOrder           ??= [];

        // R-dock-5 again: a structurally odd region must degrade to "no split" (the flat
        // DocumentOrder still applies), never throw and never produce empty panes.
        layout.DocumentRegion = SanitizeRegion(layout.DocumentRegion);

        // Drop entries that cannot mean anything — an unknown panel id (an older build's panel that
        // no longer exists, R-dock-5's own example) and an unknown side.
        layout.Panels = layout.Panels
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && DockPanelIds.All.Contains(p.Id))
            .Select(p =>
            {
                if (!DockSide.IsValid(p.Side)) p.Side = DockSide.Left;
                // Inboard only means something on the left and right — top and bottom panels are inside
                // the document column by construction. See CwsDockPanel.Inboard.
                if (p.Side is not (DockSide.Left or DockSide.Right)) p.Inboard = false;
                return p;
            })
            .ToList();

        layout.Sides = layout.Sides
            .Where(s => s is not null && DockSide.IsValid(s.Side))
            .ToList();

        foreach (var w in layout.FloatingWindows)
            w.Panels = (w.Panels ?? []).Where(id => DockPanelIds.All.Contains(id)).ToList();

        layout.FloatingWindows = layout.FloatingWindows.Where(w => w.Panels.Count > 0).ToList();

        // A panel can be in exactly one place. If a malformed/hand-edited block lists one both docked
        // and floating, the floating entry wins (it is the more specific statement) and the docked
        // duplicate is dropped — silently, because there is nothing the user could act on.
        var floated = layout.FloatingWindows.SelectMany(w => w.Panels).ToHashSet(StringComparer.Ordinal);
        layout.Panels = layout.Panels.Where(p => !floated.Contains(p.Id)).ToList();

        // Same rule between docked entries: first wins.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        layout.Panels = layout.Panels.Where(p => seen.Add(p.Id)).ToList();

        // Document floats: a document can be in exactly one window. Blank keys and duplicates (across
        // windows as well as within one) are dropped, first mention wins, and a window left with no
        // documents is dropped — the same shape of tolerance the tool-panel floats get.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in layout.FloatingDocumentWindows)
        {
            w.Documents = (w.Documents ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d) && claimed.Add(d))
                .ToList();

            if (w.Active is not null && !w.Documents.Contains(w.Active, StringComparer.OrdinalIgnoreCase))
                w.Active = null;
        }
        layout.FloatingDocumentWindows = layout.FloatingDocumentWindows
            .Where(w => w.Documents.Count > 0)
            .ToList();

        return new ReadResult(layout, null);
    }

    /// <summary>Serializes the block for storage in <c>.cws</c>.</summary>
    public static JsonNode? Write(CwsDockLayout? layout) =>
        layout is null ? null : JsonSerializer.SerializeToNode(layout, Opts);

    /// <summary>
    /// R-dock-2: the layout records <b>arrangement</b>, not <b>membership</b>. Reconciles the saved
    /// document order against the list of documents that are actually open (which
    /// <c>.cws</c>'s own <c>OpenDocuments</c> owns): a layout entry naming a document that is not
    /// open is dropped, and a document that is open but absent from the layout is appended in its
    /// own order. Two mechanisms describing the same fact is how they drift — so only one of them
    /// is allowed to decide what exists.
    /// </summary>
    /// <param name="savedOrder">Document keys from the layout block, in saved tab order.</param>
    /// <param name="openKeys">Document keys that are actually open, in their own order.</param>
    public static List<string> ReconcileDocumentOrder(
        IReadOnlyList<string> savedOrder,
        IReadOnlyList<string> openKeys)
    {
        var open   = new HashSet<string>(openKeys, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(openKeys.Count);
        var taken  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in savedOrder)
        {
            if (key is null) continue;
            if (!open.Contains(key)) continue;      // in the layout but not open — dropped
            if (taken.Add(key)) result.Add(key);
        }

        foreach (var key in openKeys)               // open but not in the layout — default position
            if (taken.Add(key)) result.Add(key);

        return result;
    }
}
