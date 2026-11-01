// Framework-free. No Avalonia, no SkiaSharp.
//
// ── WHY A PORT'S CONDUCTOR IS REMEMBERED ACROSS FRAMES ───────────────────────────────────────────
//
// Every port glyph asks LayoutConductorLookup what metal it stands on, and the answer is the UNION of that
// shape with every same-layer shape overlapping it (copper that overlaps is one conductor). That union is a
// polygon boolean, and the lookup's own memo lived for one frame. On a 4-layer evaluation board with 14 edge
// ports on traces crossing a pour it was 98 % of every frame: about 33 ms in Debug at any zoom, with the
// artwork itself drawing in 16 ms at fit and 0.1 ms zoomed in. A pan or a zoom changes no geometry, so
// nothing about the answer changed between those frames.
//
// The answers are kept per (point, layer) for as long as nothing they depend on changes: the model (the
// owner clears this from Model.Changed, as it applies the path and tile caches), the technology INSTANCE
// (the editor hands a new DrawTechnology clone whenever a layer is shown, hidden or locked, and visibility
// decides which metal a gesture sees), the base directory, and CellLayoutResolver.Generation (a placed
// cell's own artwork changed on disk). A port being dragged asks at a new point each frame and pays for
// that one point; the ports at rest are free.

using System.Collections.Generic;

namespace CircuitRF.Render;

/// <summary>
/// Cross-frame memo over <see cref="LayoutConductorLookup.LookupFor(LayoutView, Technology?, string, long)"/>.
/// Held by whoever owns a canvas, beside <see cref="LayoutPathCache"/>; a one-shot render passes none.
/// <see cref="Clear"/> on every model change.
/// </summary>
public sealed class LayoutConductorCache
{
    /// <summary>The most answers kept before starting again — a guard against a long drag over a big board, not a
    /// limit an ordinary layout reaches.</summary>
    public const int Capacity = 4096;

    private readonly object _gate = new();
    private LayoutView? _view;
    private Technology? _tech;
    private string? _baseDir;
    private long _generation = -1;
    private LayoutPortDirection.ConductorLookup? _inner;
    private Dictionary<(long X, long Y, LayerKey? Layer), LayoutPortDirection.ConductorInfo?> _answers = [];

    /// <summary>How many answers this cache has had to compute — the counter a test reads to see that a second
    /// frame at a new pan computed none.</summary>
    public int Misses { get; private set; }

    /// <summary>Forget every answer. Call when the model changes.</summary>
    public void Clear()
    {
        lock (_gate) { _inner = null; _answers = []; }
    }

    /// <summary>The lookup for this frame: the same answers <see cref="LayoutConductorLookup.LookupFor(LayoutView, Technology?, string, long)"/>
    /// gives, remembered.</summary>
    public LayoutPortDirection.ConductorLookup LookupFor(LayoutView view, Technology? tech, string baseDir)
    {
        lock (_gate)
        {
            long generation = CellLayoutResolver.Generation;
            if (_inner is null || !ReferenceEquals(view, _view) || !ReferenceEquals(tech, _tech)
                || baseDir != _baseDir || generation != _generation)
            {
                _view = view; _tech = tech; _baseDir = baseDir; _generation = generation;
                _inner = LayoutConductorLookup.LookupFor(view, tech, baseDir);
                _answers = [];
            }

            // Captured, so a Clear between two calls of one frame starts a new table rather than mixing two.
            var inner = _inner;
            var answers = _answers;
            return (x, y, onLayer) =>
            {
                lock (_gate)
                    if (answers.TryGetValue((x, y, onLayer), out var known)) return known;
                var answer = inner(x, y, onLayer);
                lock (_gate)
                {
                    Misses++;
                    if (answers.Count >= Capacity) answers.Clear();
                    answers[(x, y, onLayer)] = answer;
                }
                return answer;
            };
        }
    }
}
