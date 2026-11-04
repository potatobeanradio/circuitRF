// EM-TFR — what a resistive film IS, answered once.
//
// A drawing layer carrying LayerDef.SheetResistanceOhmPerSq is a resistor mask. Bound into a Conductor
// entry's DrawingLayers it is ALSO a film ON that level, which the EM extractors mesh; it never becomes
// metal. Everything that reads copper (connectivity, LVS, DRC, the DC rail solver, a line's cross-section)
// asks here so that binding a film changes nothing about what is connected: a resistor drawn between two
// contacts is a device, and reading its film as copper would short it.

namespace CircuitRF.Design.Layout;

public static class ResistiveFilms
{
    /// <summary>The sheet resistance of the film <paramref name="key"/> patterns, or null when it is no film.</summary>
    public static double? SheetResistanceOf(Technology tech, LayerKey key)
    {
        ArgumentNullException.ThrowIfNull(tech);
        foreach (var l in tech.Layers)
            if (l.Key == key) return l.SheetResistanceOhmPerSq is > 0 and var r ? r : null;
        return null;
    }

    /// <summary>True when <paramref name="key"/> is a resistor film's layer, bound to a level or not.</summary>
    public static bool IsFilm(Technology tech, LayerKey key) => SheetResistanceOf(tech, key) is not null;

    /// <summary>The drawing layers of a stackup entry that are metal. The entry's own list, the same
    /// object, when none of them is a film, which is every entry of a technology with no bound film.</summary>
    public static IReadOnlyList<LayerKey> MetalLayers(Technology tech, StackupLayer entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (var k in entry.DrawingLayers)
            if (IsFilm(tech, k)) return [.. entry.DrawingLayers.Where(d => !IsFilm(tech, d))];
        return entry.DrawingLayers;
    }

    /// <summary>The film layers bound to a Conductor entry, in the entry's own order.</summary>
    public static IEnumerable<LayerDef> BoundTo(Technology tech, StackupLayer entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != StackupKind.Conductor) yield break;
        foreach (var k in entry.DrawingLayers)
            if (tech.Layers.FirstOrDefault(l => l.Key == k) is { SheetResistanceOhmPerSq: > 0 } def) yield return def;
    }
}
