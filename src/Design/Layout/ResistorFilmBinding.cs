using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout;

/// <summary>
/// Where a resistive film may be placed, stated once (brief stackup-render 9, R-stk9-6). <c>TechValidation</c>
/// calls it to report a technology and the stackup canvas calls it to decide whether a drop target is live, so a
/// drop the canvas would allow and validation would then flag cannot happen.
/// </summary>
public static class ResistorFilmBinding
{
    /// <summary>The ids <see cref="Problems"/> reports under.</summary>
    public static class Ids
    {
        public const string OnVia           = "tech.film.on-via";
        public const string OnGround        = "tech.film.on-ground";
        public const string TwoLevels       = "tech.film.two-levels";
        public const string AlsoMetal       = "tech.film.also-metal";
        public const string UnknownLayer    = "tech.film.unknown-layer";
        public const string SheetResistance = "tech.film.sheet-resistance";
        public const string NoLevel         = "tech.film.no-level";
    }

    /// <summary>
    /// Whether the film patterned by <paramref name="film"/> may sit on <paramref name="conductorName"/>,
    /// and when not, why, in one sentence with the rule's id. It asks nothing about where the film is now: a
    /// film may be moved from one level to another, and only a film stated on two levels is refused for it.
    /// </summary>
    public static bool CanPlaceOn(Technology tech, LayerKey film, string conductorName, out string reason)
        => Check(tech, film, conductorName, out _, out reason);

    private static bool Check(Technology tech, LayerKey film, string conductorName, out string id, out string reason)
    {
        ArgumentNullException.ThrowIfNull(tech);
        (id, reason) = ("", "");
        string name = tech.Layers.FirstOrDefault(l => l.Key == film)?.Name ?? $"{film.Layer}/{film.Datatype}";

        var entry = tech.Stackup.Layers.FirstOrDefault(l => string.Equals(l.Name, conductorName, StringComparison.Ordinal));
        if (entry is not { Kind: StackupKind.Conductor })
        {
            id = Ids.OnVia;
            reason = entry is { Kind: StackupKind.Via }
                ? $"Resistor film \"{name}\" cannot sit on the via \"{conductorName}\": a film is a sheet on a conductor level, and a via has none."
                : $"Resistor film \"{name}\" cannot sit on \"{conductorName}\": a film is a sheet on a conductor level.";
            return false;
        }
        if (entry.IsGroundReference)
        {
            id = Ids.OnGround;
            reason = $"Resistor film \"{name}\" cannot sit on \"{conductorName}\", the return plane: the plane is a termination with no cells for a film to sit on.";
            return false;
        }
        if (!tech.Layers.Any(l => l.Key == film))
        {
            id = Ids.UnknownLayer;
            reason = $"Stackup layer \"{conductorName}\" states a resistor film on drawing layer ({film.Layer},{film.Datatype}), which the technology does not declare.";
            return false;
        }
        if (tech.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(film)) is { } metal)
        {
            id = Ids.AlsoMetal;
            reason = $"Drawing layer \"{name}\" is the metal of \"{metal.Name}\", so it cannot also pattern a resistor film: untick it from that level's drawing layers first.";
            return false;
        }
        return true;
    }

    /// <summary>Every rule a technology's films break, for <c>TechValidation</c>.</summary>
    public static IEnumerable<(string Id, string Message)> Problems(Technology tech)
    {
        ArgumentNullException.ThrowIfNull(tech);
        foreach (string lost in tech.UnplacedFilmLayers)
            yield return (Ids.NoLevel, $"Layer \"{lost}\" states a sheet resistance, but the stackup has no conductor level for the film to sit on, " +
                                       "so the value was dropped. Add the conductor and define the film on its card.");

        var seen = new HashSet<LayerKey>();
        foreach (var (conductor, film) in ResistiveFilms.All(tech))
        {
            string name = tech.Layers.FirstOrDefault(l => l.Key == film.Layer)?.Name ?? $"{film.Layer.Layer}/{film.Layer.Datatype}";
            if (!seen.Add(film.Layer))
            {
                string first = ResistiveFilms.ConductorOf(tech, film.Layer)!.Name;
                yield return (Ids.TwoLevels, $"Resistor film \"{name}\" is on both \"{first}\" and \"{conductor.Name}\". A film is deposited on one level: remove it from all but that one.");
            }
            if (!Check(tech, film.Layer, conductor.Name, out string id, out string reason)) yield return (id, reason);
            if (!(film.SheetResistanceOhmPerSq > 0))
                yield return (Ids.SheetResistance, $"Resistor film \"{name}\" on \"{conductor.Name}\" states a sheet resistance of {film.SheetResistanceOhmPerSq:G4} Ω/sq; it must be positive.");
        }
    }

    /// <summary>The face the film would land on if moved to that level: its own statement, then the level's.</summary>
    public static ConductorSheetSurface LandingFace(Technology tech, LayerKey film, string conductorName)
    {
        ArgumentNullException.ThrowIfNull(tech);
        var level = tech.Stackup.Layers.FirstOrDefault(l => string.Equals(l.Name, conductorName, StringComparison.Ordinal));
        return ResistiveFilms.Of(tech, film)?.At ?? level?.SheetAt ?? ConductorSheetSurface.Bottom;
    }

    /// <summary>Moves a film's entry to <paramref name="conductorName"/>, whole. False when the film does not
    /// exist, the level refuses it, or it is already there.</summary>
    public static bool Move(Technology tech, LayerKey film, string conductorName)
    {
        ArgumentNullException.ThrowIfNull(tech);
        if (ResistiveFilms.ConductorOf(tech, film) is not { } from || ResistiveFilms.Of(tech, film) is not { } entry) return false;
        if (string.Equals(from.Name, conductorName, StringComparison.Ordinal) || !CanPlaceOn(tech, film, conductorName, out _)) return false;
        Remove(tech, film);
        var target = tech.Stackup.Layers.First(l => string.Equals(l.Name, conductorName, StringComparison.Ordinal));
        (target.Films ??= []).Add(entry);
        return true;
    }

    /// <summary>Removes a film's entry from every level, leaving its drawing layer. False when there was none.</summary>
    public static bool Remove(Technology tech, LayerKey film)
    {
        ArgumentNullException.ThrowIfNull(tech);
        bool removed = false;
        foreach (var entry in tech.Stackup.Layers)
        {
            if (entry.Films is null) continue;
            removed |= entry.Films.RemoveAll(f => f.Layer == film) > 0;
            if (entry.Films.Count == 0) entry.Films = null;
        }
        return removed;
    }

    /// <summary>Follows a layer's renumbering: every film naming <paramref name="from"/> now names <paramref name="to"/>.</summary>
    public static void RenameLayer(Technology tech, LayerKey from, LayerKey to)
    {
        ArgumentNullException.ThrowIfNull(tech);
        foreach (var (_, film) in ResistiveFilms.All(tech))
            if (film.Layer == from) film.Layer = to;
    }
}
