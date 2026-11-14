// brief-icp-10 — which stackup conductor a cell pin sits on, read from the pin's own drawing layer.
//
// A kit that marks a pin with a box on a PIN-purpose layer ("Metal1.pin", GDS 8/2) states the pin on a layer no conductor lists:
// a stackup names its drawing layers ("Metal1.drawing", 8/0), never their pin, label or text purposes. Matching the key exactly
// therefore refused every pin of such a kit as "not an analysis level". The pin is on the metal its layer is a purpose OF, so the
// match falls back, in order, to the conductor drawing a layer of the same name before its purpose, then to the conductor drawing
// the same GDS layer number. Either fallback must name exactly one conductor, or none is chosen.

namespace CircuitRF.Design.Layout.Em;

/// <summary>The conductor a pin's layer is on.</summary>
public static class EmPinConductor
{
    /// <summary>The conductor of <paramref name="tech"/>'s stackup that <paramref name="layer"/> is drawn on or is a purpose of; null when
    /// none, or more than one, fits.</summary>
    public static StackupLayer? Of(Technology? tech, LayerKey layer)
    {
        if (tech is null) return null;
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.FirstOrDefault(l => l.DrawingLayers.Contains(layer)) is { } exact) return exact;

        // The layer's name before its purpose: "Metal1.pin" → "Metal1".
        string? Base(LayerKey k) => tech.Layers.FirstOrDefault(d => d.Key == k)?.Name is { Length: > 0 } n
            ? (n.IndexOf('.') is int dot and > 0 ? n[..dot] : n)
            : null;
        if (Base(layer) is { } name)
        {
            var byName = conductors.Where(c => c.DrawingLayers.Any(k => string.Equals(Base(k), name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1) return null;
        }
        var byNumber = conductors.Where(c => c.DrawingLayers.Any(k => k.Layer == layer.Layer)).ToList();
        return byNumber.Count == 1 ? byNumber[0] : null;
    }
}
