// Framework-free. No Avalonia, no SkiaSharp — the renderer, the snap index and the port resolver all
// read this, and only the renderer is allowed to know about Skia.

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Design.Layout.PCells;   // PCellGenerator — the delegate GeneratorSource hands back.

namespace CircuitRF.Design.Layout;

/// <summary>
/// A resolved cell's own pins — the ONE place "what pins does this cell have?" is answered.
///
/// <para><b>Why this exists (2026-08-09).</b> Three callers needed the identical two-branch answer
/// (persisted <see cref="LayoutView.Pins"/> first, else re-invoke the generator) and each had grown
/// its own copy: the renderer's pin overlay, <see cref="LayoutSnapFeatures"/>, and — the one that
/// exposed it — <see cref="LayoutPortDirection"/>'s instance lookup. Three copies of a two-branch
/// rule with a cache attached to one of them is exactly the shape that drifts, and the branch that
/// matters most is the SECOND one: it only fires for a generated cell written before pins were
/// persisted, so a copy that omits it looks correct on every freshly-regenerated cell and silently
/// finds nothing on an older one.</para>
///
/// <para><b>The persisted list wins, and that is what makes an IMPORTED cell work at all.</b> A cell
/// that arrived through GDSII import has no generator to re-invoke; gating pin resolution on
/// <see cref="LayoutView.PCellOrigin"/> made its pins invisible to everything here.</para>
/// </summary>
public static class CellPins
{
    /// <summary>Live-resolved pin cache, keyed by the resolved sub-cell's <see cref="LayoutView"/>
    /// REFERENCE — self-invalidating by construction: a file or in-session edit produces a NEW
    /// reference on the next resolve, which is simply a cache miss here, and the old entry becomes
    /// unreachable with no explicit eviction call needed.</summary>
    private static readonly ConditionalWeakTable<LayoutView, PinCacheEntry> Cache = new();

    private sealed class PinCacheEntry
    {
        public required IReadOnlyList<LayoutPin> Pins;
    }

    /// <summary>
    /// How a generated cell's own generator is reached.
    ///
    /// <para>It was a seam set by a GUI module initializer (R-rnd1-3) while <c>PCellRegistry</c> lived
    /// in <c>src/Ui</c>, which left a headless process unable to re-derive a pre-pin-persistence
    /// cell's pins. The registry crossed the firewall in brief-generated-cells-2 (R-gc2-1), so it is
    /// simply the registry now, in every process; the field stays assignable for a test that wants to
    /// count or replace the calls.</para>
    /// </summary>
    public static Func<string, PCellGenerator?>? GeneratorSource =
        id => PCellRegistry.TryGet(id, out var generator) ? generator : null;

    /// <summary>
    /// brief-sdl-10 D4 — a layout Update Layout from Schematic made states no pins, but it placed a numbered port label on
    /// the face of the metal at every schematic Port (brief-sdl-5), and those Ports ARE the cell's pins (brief-sdl-6 D8:
    /// its symbol's pins are numbered by Num). Each such label — linked to its Port, its text a number — is read as the pin
    /// of that number: at the label, facing out of the metal its arrow points into, as wide as the placer drew it (a placed
    /// label's height is half the line's width). A label the user placed by hand is not a pin: it names nothing in a
    /// schematic. Without this a placed cell had no pins, so nothing in a parent could be joined to it, LVS could not map
    /// its ports and a longer cell could not carry its parent's lines with it.
    /// </summary>
    private static IReadOnlyList<LayoutPin> FromSchematicPorts(LayoutView view)
    {
        var pins = new List<LayoutPin>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in view.Shapes)
        {
            if (s is not LabelShape { IsPort: true, SchematicId: { Length: > 0 }, PortDirection: { } inward } label) continue;
            string text = label.Text.Trim();
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
                || n <= 0 || !seen.Add(text)) continue;
            pins.Add(new LayoutPin
            {
                Name = text, X = label.X, Y = label.Y, WidthDbu = 2 * label.Height,
                OutwardDeg = LayoutAngle.Normalize((int)inward * 90.0 + 180.0),
                Layer = label.PortLayer ?? label.Layer,
            });
        }
        return pins;
    }

    /// <summary>
    /// <paramref name="view"/>'s pins: its own persisted list when it has one, else the generator's
    /// own answer for a generated cell that predates pin persistence.
    ///
    /// <para><b>Never throws.</b> A generator can now be a script in another process, and every
    /// caller here is on an interaction or render path where losing a cell's pins is a degradation
    /// and an escaping exception is a crash. A generator that fails is asked once per resolved cell
    /// (the empty answer is cached) rather than on every repaint.</para>
    /// </summary>
    public static IReadOnlyList<LayoutPin> Resolve(LayoutView view, Technology? tech)
    {
        if (view.Pins.Count > 0) return view.Pins;
        // Not cached: a layout made from its schematic is edited in place — its labels move in the editor, and Update
        // Layout from Schematic re-plans the open view itself — so a cached answer would hold the pins where they were.
        // The cache is for a generated cell, whose view never changes. The scan is one pass over the shapes.
        if (view.PCellOrigin is not { } origin) return FromSchematicPorts(view);
        if (Cache.TryGetValue(view, out var cached)) return cached.Pins;

        IReadOnlyList<LayoutPin> pins = [];
        if (GeneratorSource?.Invoke(origin.GeneratorId) is { } generator)
        {
            try
            {
                pins = [.. generator(origin.Parameters, tech, PCellLayerSelection.Default).Pins
                    .Select(p => new LayoutPin
                    {
                        Name      = p.Name,
                        X         = p.X,
                        Y         = p.Y,
                        WidthDbu  = p.WidthDbu,
                        OutwardDeg = p.OutwardDirectionDeg,
                        Layer     = p.Layer,
                    })];
            }
            // A generator that fails is asked once (the empty answer is cached) rather than on
            // every repaint. The catch is deliberately broad for the reason the summary states: an
            // out-of-process script can fail in any number of ways and losing a cell's pins is a
            // degradation where an escaping exception is a crash. It was `PCellWireException` while
            // this file and the generator registry were in one assembly.
            catch (Exception) { pins = []; }
        }

        Cache.AddOrUpdate(view, new PinCacheEntry { Pins = pins });
        return pins;
    }
}
