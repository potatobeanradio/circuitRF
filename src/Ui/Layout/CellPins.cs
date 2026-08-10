// Framework-free. No Avalonia, no SkiaSharp — the renderer, the snap index and the port resolver all
// read this, and only the renderer is allowed to know about Skia.

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Layout;

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
        if (view.PCellOrigin is not { } origin) return [];
        if (Cache.TryGetValue(view, out var cached)) return cached.Pins;

        IReadOnlyList<LayoutPin> pins = [];
        if (PCellRegistry.TryGet(origin.GeneratorId, out var generator))
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
            catch (PCells.Wire.PCellWireException) { pins = []; }
        }

        Cache.AddOrUpdate(view, new PinCacheEntry { Pins = pins });
        return pins;
    }
}
