// A resistive film, answered once. A film is an entry on a Conductor's StackupLayer.Films, patterned by a
// drawing layer that is bound to nothing else. Metal is DrawingLayers and films are not in it, so a reader of
// copper (connectivity, LVS, DRC, the DC rail solver, a line's cross-section) cannot take one for the other:
// a resistor drawn between two contacts is a device, and reading its film as copper would short it.

namespace CircuitRF.Design.Layout;

public static class ResistiveFilms
{
    /// <summary>Every film of a technology in stackup order, with the level it is on.</summary>
    public static IEnumerable<(StackupLayer Conductor, StackupFilm Film)> All(Technology tech)
    {
        ArgumentNullException.ThrowIfNull(tech);
        foreach (var entry in tech.Stackup.Layers)
            if (entry.Films is { } films)
                foreach (var film in films) yield return (entry, film);
    }

    /// <summary>The film entry <paramref name="key"/> patterns, or null when it patterns none.</summary>
    public static StackupFilm? Of(Technology tech, LayerKey key)
    {
        foreach (var (_, film) in All(tech))
            if (film.Layer == key) return film;
        return null;
    }

    /// <summary>The level the film <paramref name="key"/> patterns is on, or null when it is no film.</summary>
    public static StackupLayer? ConductorOf(Technology tech, LayerKey key)
    {
        foreach (var (conductor, film) in All(tech))
            if (film.Layer == key) return conductor;
        return null;
    }

    /// <summary>The sheet resistance of the film <paramref name="key"/> patterns, or null when it is no
    /// usable film. A stated value that is not positive is an error <c>TechValidation</c> reports.</summary>
    public static double? SheetResistanceOf(Technology tech, LayerKey key)
        => Of(tech, key) is { SheetResistanceOhmPerSq: > 0 } film ? film.SheetResistanceOhmPerSq : null;

    /// <summary>True when <paramref name="key"/> patterns a film.</summary>
    public static bool IsFilm(Technology tech, LayerKey key) => SheetResistanceOf(tech, key) is not null;

    /// <summary>Which face of its band a film sits on: its own statement, then the level's sheet, then Bottom.</summary>
    public static ConductorSheetSurface FaceOf(StackupFilm film, StackupLayer conductor)
        => film.At ?? conductor.SheetAt ?? ConductorSheetSurface.Bottom;
}
