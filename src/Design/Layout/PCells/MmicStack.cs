using System.Globalization;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// What an MMIC process offers its passives, read off a technology's stackup and layer table
/// (brief-agent-authoring-overview.md AA-1) — the ONE resolution both halves of the four parts use:
/// the electrical models' injected numbers (<c>MmicPassiveInjection</c>) and the artwork the PCells
/// draw. Two resolutions would be two answers to "which metal is the capacitor's bottom plate", and
/// the schematic and the layout would then describe different parts.
///
/// <para>Every member is nullable on its own: a process with a MIM module but no resistor film can
/// still draw a capacitor. <see cref="MmicStackResolver.Why"/> words what is missing, per part.</para>
/// </summary>
/// <param name="BaseMetal">The metal that lies on the substrate and carries the lines — the bottom
/// plate's metal and the coil's.</param>
/// <param name="BridgeMetal">The metal an air bridge, a coil's escape and a top-plate strap run on.</param>
/// <param name="BridgeVia">The via entry joining <paramref name="BaseMetal"/> to <paramref name="BridgeMetal"/>.</param>
/// <param name="BridgeHeightMeters">The clear height from the top of the base metal to the bottom of the
/// bridge metal.</param>
/// <param name="BridgeEpsR">The permittivity of that gap where nothing is drawn: a patterned film (one
/// tied to a mask by <c>PresentWithLayer</c>) and a conductor band both count as air, because neither
/// is there under an unrelated bridge.</param>
/// <param name="CapDielectric">The capacitor dielectric: a patterned film between two conductors.</param>
/// <param name="TopPlate">The conductor directly above <paramref name="CapDielectric"/>.</param>
/// <param name="CapMask">The drawing layer whose shapes put <paramref name="CapDielectric"/> in place.</param>
/// <param name="TopPlateVia">The via entry that carries the top plate up to the bridge metal.</param>
/// <param name="ResistorLayer">The drawing layer patterning the first resistive film in stackup order.</param>
/// <param name="ResistorFilm">That film's entry.</param>
/// <param name="ResistorLevel">The conductor it is on, which is the metal a <c>TFR</c>'s contacts are drawn on.</param>
/// <param name="Substrate">The substrate under <paramref name="BaseMetal"/>, to its ground.</param>
public sealed record ResolvedMmicStack(
    StackupLayer? BaseMetal,
    StackupLayer? BridgeMetal,
    StackupLayer? BridgeVia,
    double BridgeHeightMeters,
    double BridgeEpsR,
    StackupLayer? CapDielectric,
    StackupLayer? TopPlate,
    LayerDef? CapMask,
    StackupLayer? TopPlateVia,
    LayerDef? ResistorLayer,
    StackupFilm? ResistorFilm,
    StackupLayer? ResistorLevel,
    ResolvedSubstrate? Substrate,
    string? SubstrateFailure)
{
    public static LayerKey Key(StackupLayer? layer, LayerKey fallback)
        => layer is { DrawingLayers.Count: > 0 } ? layer.DrawingLayers[0] : fallback;
}

/// <summary>Which of the four parts is asking — <see cref="MmicStackResolver.Why"/> names what that
/// part needs and the process does not have.</summary>
public enum MmicPart { MimCap, Spiral, Tfr, Airbridge }

public static class MmicStackResolver
{
    private const long DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The shipped GaAs process's layer numbers, which the artwork falls back to when no
    /// technology resolves — a layout with no technology still generates geometry, and without these
    /// every piece of a capacitor would collapse onto one layer and short itself.</summary>
    public static class FallbackLayers
    {
        public static readonly LayerKey Base       = new(1, 0);
        public static readonly LayerKey Bridge     = new(2, 0);
        public static readonly LayerKey BridgeVia  = new(3, 0);
        public static readonly LayerKey Resistor   = new(4, 0);
        public static readonly LayerKey CapMask    = new(6, 0);
        public static readonly LayerKey TopPlate   = new(9, 0);
        public static readonly LayerKey TopPlateVia = new(10, 0);
    }

    public static ResolvedMmicStack Resolve(Technology? technology)
    {
        if (technology is null)
            return new(null, null, null, 0, 1, null, null, null, null, null, null, null, null, "no technology resolved for this document");

        var layers = technology.Stackup.Layers;
        bool IsConductor(int i) => i >= 0 && i < layers.Count && layers[i].Kind == StackupKind.Conductor;

        // The capacitor dielectric: a PATTERNED film (PresentWithLayer names its mask) sandwiched
        // directly between two conductors. Requiring the mask is what keeps a board's whole core —
        // also a dielectric between two coppers — from being read as a capacitor film. Where there are
        // several, the one with the most capacitance per area is the MIM film.
        StackupLayer? capDielectric = null;
        int capIndex = -1;
        for (int i = 1; i + 1 < layers.Count; i++)
        {
            var l = layers[i];
            if (l.Kind != StackupKind.Dielectric || l.PresentWithLayer is not { Length: > 0 } || l.ThicknessDbu <= 0) continue;
            if (!IsConductor(i - 1) || !IsConductor(i + 1)) continue;
            if (capDielectric is null || l.Epsr / l.ThicknessDbu > capDielectric.Epsr / capDielectric.ThicknessDbu)
                (capDielectric, capIndex) = (l, i);
        }
        var topPlate = capIndex > 0 ? layers[capIndex - 1] : null;
        var bottomPlate = capIndex > 0 ? layers[capIndex + 1] : null;
        var capMask = capDielectric?.PresentWithLayer is { } maskName
            ? technology.Layers.FirstOrDefault(d => string.Equals(d.Name, maskName, StringComparison.OrdinalIgnoreCase))
            : null;

        bool Spans(StackupLayer via, string name)
            => string.Equals(via.SpanFromLayer, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(via.SpanToLayer, name, StringComparison.OrdinalIgnoreCase);
        StackupLayer? Conductor(string? name)
            => name is null ? null : layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor
                                                             && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

        var topPlateVia = topPlate is null ? null
            : layers.FirstOrDefault(l => l.Kind == StackupKind.Via && Spans(l, topPlate.Name));

        // The bridge via: one joining two signal conductors (neither a ground plane) that is not the
        // top plate's own. When there is a capacitor, the one landing on its bottom plate.
        var bridgeCandidates = layers
            .Where(l => l.Kind == StackupKind.Via
                        && Conductor(l.SpanFromLayer) is { IsGroundReference: false }
                        && Conductor(l.SpanToLayer) is { IsGroundReference: false }
                        && (topPlate is null || !Spans(l, topPlate.Name)))
            .ToList();
        var bridgeVia = bottomPlate is not null
            ? bridgeCandidates.FirstOrDefault(v => Spans(v, bottomPlate.Name)) ?? bridgeCandidates.FirstOrDefault()
            : bridgeCandidates.FirstOrDefault();

        StackupLayer? baseMetal = bottomPlate, bridgeMetal = null;
        double hb = 0, erb = 1;
        if (bridgeVia is not null)
        {
            var a = Conductor(bridgeVia.SpanFromLayer)!;
            var b = Conductor(bridgeVia.SpanToLayer)!;
            int ia = layers.IndexOf(a), ib = layers.IndexOf(b);
            // Stackup layers run top to bottom, so the LOWER conductor has the larger index.
            var (lower, upper) = ia > ib ? (a, b) : (b, a);
            baseMetal ??= lower;
            bridgeMetal = ReferenceEquals(lower, baseMetal) ? upper : null;
            if (bridgeMetal is not null)
                (hb, erb) = Gap(layers, layers.IndexOf(bridgeMetal), layers.IndexOf(baseMetal));
        }
        if (bridgeMetal is null) bridgeVia = null;
        baseMetal ??= layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor);

        ResolvedSubstrate? substrate = null;
        string? substrateFailure = null;
        if (baseMetal is not null)
        {
            var (s, failure, _) = SubstrateResolver.ResolveElectrical(technology, new PCellLayerSelection(baseMetal.Name, null));
            substrate = s;
            substrateFailure = failure?.Reason;
        }
        else substrateFailure = $"technology '{technology.Name}' stackup has no conductor layer";

        var (resistorLevel, resistorFilm) = ResistiveFilms.All(technology)
            .FirstOrDefault(f => f.Film.SheetResistanceOhmPerSq > 0 && technology.Layers.Any(d => d.Key == f.Film.Layer));
        var resistor = resistorFilm is null ? null : technology.Layers.First(d => d.Key == resistorFilm.Layer);

        return new ResolvedMmicStack(baseMetal, bridgeMetal, bridgeVia, hb, erb, capDielectric, topPlate, capMask,
                                     topPlateVia, resistor, resistorFilm, resistorLevel, substrate, substrateFailure);
    }

    /// <summary>The clear gap between two stackup entries (upper index < lower index) and its
    /// permittivity, where a patterned film and a conductor band count as air.</summary>
    private static (double H, double EpsR) Gap(IReadOnlyList<StackupLayer> layers, int upper, int lower)
    {
        double sumT = 0, sumTOverEr = 0;
        for (int i = upper + 1; i < lower; i++)
        {
            var l = layers[i];
            if (l.Kind == StackupKind.Via || l.ThicknessDbu <= 0) continue;
            double t = l.ThicknessDbu / (double)DbuPerMicron * 1e-6;
            double er = l.Kind == StackupKind.Dielectric && l.PresentWithLayer is null && l.Epsr >= 1 ? l.Epsr : 1.0;
            sumT += t;
            sumTOverEr += t / er;
        }
        return sumT > 0 ? (sumT, sumT / sumTOverEr) : (0, 1);
    }

    /// <summary>The names a device-recognition formula may use without the technology declaring them (see
    /// <c>DeviceRecognition.ConstantsOf</c>), for a caller that
    /// lists what a formula may use. Absent where the process does not state the number.</summary>
    public static IReadOnlyList<(string Name, string Expression, string? Unit)> DeckConstants(Technology tech)
    {
        var list = new List<(string, string, string?)>();
        var stack = Resolve(tech);

        if (stack.ResistorFilm is { } film0)
            list.Add(("TfrSheetResistance", film0.SheetResistanceOhmPerSq.ToString("R", CultureInfo.InvariantCulture), "Ohm"));

        if (stack.CapDielectric is { ThicknessDbu: > 0, Epsr: > 0 } film)
        {
            double density = 8.8541878128e-12 * film.Epsr / Metres(film.ThicknessDbu);
            list.Add(("MimCapDensity", density.ToString("R", CultureInfo.InvariantCulture), null));
        }
        return list;
    }

    /// <summary>Metres from a stackup thickness.</summary>
    public static double Metres(long dbu) => dbu / (double)DbuPerMicron * 1e-6;

    /// <summary>
    /// Why <paramref name="part"/> cannot be resolved on this process, or null when it can. One
    /// sentence naming the missing piece of the technology, the convention every other
    /// technology-resolution failure here follows.
    /// </summary>
    public static string? Why(ResolvedMmicStack stack, MmicPart part, string technologyName)
    {
        if (stack.Substrate is null) return stack.SubstrateFailure ?? "no substrate resolved";
        return part switch
        {
            MmicPart.MimCap when stack.CapDielectric is null =>
                $"technology '{technologyName}' has no capacitor dielectric — a dielectric directly between two " +
                "conductors and tied to a mask by PresentWithLayer",
            MmicPart.Tfr when stack.ResistorLayer is null =>
                $"technology '{technologyName}' has no resistor film — no conductor in its stackup states one",
            MmicPart.Spiral or MmicPart.Airbridge when stack.BridgeMetal is null =>
                $"technology '{technologyName}' has no bridge metal — no via joins '{stack.BaseMetal?.Name}' to a " +
                "second, non-ground conductor above it",
            _ => null,
        };
    }
}
