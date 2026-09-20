using System.Linq;

namespace CircuitRF.Design.Layout;

/// <summary>
/// The handful of numbers that tell one technology from another at a glance — what Settings ▸
/// Technology shows beside the list so a choice can be made without opening the technology editor.
///
/// <para><b>It is DERIVED, never stored.</b> Every field is read out of the <see cref="Technology"/>
/// itself, so a summary cannot disagree with the file it describes — which is the failure a cached or
/// hand-maintained one would eventually have.</para>
///
/// <para>It lives in <c>src/Design</c> with the model it summarises rather than beside the tab that
/// shows it, so it can be asserted headlessly and so a second caller (a <c>check</c> or
/// <c>explain</c> report) does not have to grow a second copy of the same arithmetic.</para>
/// </summary>
/// <param name="Name">The technology's own authored name.</param>
/// <param name="DrawingLayers">How many drawing layers it declares.</param>
/// <param name="Conductors">Conductor entries in the stackup — for a board, its copper count.</param>
/// <param name="Dielectrics">Dielectric entries in the stackup.</param>
/// <param name="Vias">Via entries in the stackup.</param>
/// <param name="DrcRules">How many design rules it carries.</param>
/// <param name="OverallThicknessDbu">
/// The sum of the non-via entries' thicknesses, or null when the stackup is empty. <b>Not
/// <see cref="Stackup.BoardThicknessDbu"/></b>, which is a second opinion some other document stated
/// and which that property's own remarks explain is never derived from.
/// </param>
/// <param name="StatedThicknessDbu">What some other document said the board measures, or null.</param>
/// <param name="DisplayUnit">The unit its editors open in.</param>
public sealed record TechnologySummary(
    string     Name,
    int        DrawingLayers,
    int        Conductors,
    int        Dielectrics,
    int        Vias,
    int        DrcRules,
    long?      OverallThicknessDbu,
    long?      StatedThicknessDbu,
    LayoutUnit DisplayUnit)
{
    public static TechnologySummary Of(Technology tech)
    {
        var entries = tech.Stackup.Layers;

        return new TechnologySummary(
            Name:                tech.Name,
            DrawingLayers:       tech.Layers.Count,
            Conductors:          entries.Count(l => l.Kind == StackupKind.Conductor),
            Dielectrics:         entries.Count(l => l.Kind == StackupKind.Dielectric),
            Vias:                entries.Count(l => l.Kind == StackupKind.Via),
            DrcRules:            tech.DrcRules.Count,
            OverallThicknessDbu: entries.Count == 0
                                     ? null
                                     : entries.Where(l => l.Kind != StackupKind.Via).Sum(l => l.ThicknessDbu),
            StatedThicknessDbu:  tech.Stackup.BoardThicknessDbu,
            DisplayUnit:         tech.DefaultDisplayUnit);
    }

    /// <summary>
    /// The thickness as a person reads it — in the technology's OWN display unit, with its suffix.
    /// Null when the stackup declares nothing to add up.
    ///
    /// <para>A stackup that adds up to zero is still reported, as <c>0</c>: it means every entry was
    /// authored with no thickness, which is a real and diagnosable state, and hiding it would make it
    /// look like a technology with no stackup at all.</para>
    /// </summary>
    /// <remarks>
    /// Two decimals, not <see cref="LayoutUnits.Format"/>'s default of four. A stackup's sum carries
    /// every entry's rounding — a nominally 20 mil board reads 22.7559 mil once its two 1.4 mil
    /// coppers are added — and the last two digits of that are arithmetic, not a specification. This
    /// is a summary; the technology editor is where the exact figures are.
    /// </remarks>
    public string? ThicknessText => OverallThicknessDbu is not { } dbu
        ? null
        : LayoutUnits.Format(dbu, DisplayUnit, LayoutUnits.DefaultDbuPerMicron, maxDecimals: 2)
          + " " + LayoutUnits.Suffix(DisplayUnit);

    /// <summary>
    /// <c>"8 layers · 4 conductors, 3 dielectrics · 1.6 mm"</c> — one line, for a list row or a
    /// message. Parts that a technology does not have are LEFT OUT rather than printed as zero, so
    /// the line stays about what is there.
    /// </summary>
    public string OneLine
    {
        get
        {
            var parts = new List<string>
            {
                DrawingLayers == 1 ? "1 layer" : $"{DrawingLayers} layers",
            };

            var stack = new List<string>();
            if (Conductors  > 0) stack.Add(Conductors  == 1 ? "1 conductor"  : $"{Conductors} conductors");
            if (Dielectrics > 0) stack.Add(Dielectrics == 1 ? "1 dielectric" : $"{Dielectrics} dielectrics");
            if (Vias        > 0) stack.Add(Vias        == 1 ? "1 via"        : $"{Vias} vias");

            if (stack.Count > 0)   parts.Add(string.Join(", ", stack));
            else                   parts.Add("no stackup");

            if (ThicknessText is { } t) parts.Add(t);

            return string.Join("  ·  ", parts);
        }
    }
}
