// The land-pattern PCell generator — R-fp1-3/4/6.
//
// It draws nothing, it names no Avalonia type, and src/Cli can call it. That is the point of
// putting it here and not beside the six microstrip generators in src/Ui (series overview §1a): a
// footprint has to be generatable with no display, because `circuitrf netlist`, `render` and
// `convert` all need one.
//
// LAYERS BY ROLE, NEVER BY KEY. A shipped .clay per case size cannot work and would fail silently —
// the shipped technologies disagree about every layer key, so an 0402 .clay dropped into the
// four-layer technology would put its silkscreen on layer 5, which is Soldermask Top, and nothing
// would say so (overview §1b).
//
// THE ORIGIN IS THE PATTERN'S CENTRE, not pin 1. This is a deliberate departure from the microstrip
// generators' R-pc-3 ("pin 1 at the origin"), and the reason is that a land pattern is not a
// transmission line: every board format places a footprint by its BODY CENTRE, so that is where an
// imported cell's origin already is, and brief 4's picker lists built-in patterns and imported cells
// in one list. Two origin conventions in one list is a part that jumps when you change its
// footprint.

using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// Produces the artwork for one case size at one density, on the layers the resolved technology
/// actually has.
/// </summary>
public static class ChipLandPatternGenerator
{
    /// <summary>
    /// Bumped whenever the GEOMETRY this produces changes for an input that already worked — the
    /// same obligation <c>PCellRegistry</c>'s own <c>_generatorVersions</c> table carries, and for
    /// the same reason: without it a generated cell already on disk keeps its name and its old,
    /// superseded artwork, and every placed instance keeps resolving to it.
    /// </summary>
    public const string AlgorithmVersion = "1";

    /// <summary>Mask opening expansion per side. A fabrication default rather than a technology
    /// one: a Technology declares no mask expansion, and 50 um is the value every board house
    /// assumes when a design states none.</summary>
    private const decimal MaskExpansionMm = 0.05m;

    /// <summary>Silkscreen line width, and its clearance from the mask opening. A silk line ON a
    /// mask opening is printed over solder and is the classic silkscreen defect.</summary>
    private const decimal SilkLineWidthMm = 0.12m;
    private const decimal SilkClearanceMm = 0.10m;

    /// <summary>Courtyard outline line width — thin, because the courtyard is the OUTLINE's own
    /// extent and a fat line would make the keepout ambiguous by half a line width.</summary>
    private const decimal CourtyardLineWidthMm = 0.05m;

    /// <summary>Every canonical generator id — one per case per density (R-fp1-2b). What the
    /// registry resolver lists.</summary>
    public static IReadOnlyList<string> GeneratorIds { get; } =
        (from c in SmtCaseTable.All
         from d in new[] { DensityLevel.Most, DensityLevel.Nominal, DensityLevel.Least }
         select new FootprintRef(c, d).ToString()).ToArray();

    /// <summary>
    /// The <see cref="PCellGenerator"/> for one reference. A closure, because the contract's
    /// delegate takes parameters, a technology and a layer selection — and a land pattern's case and
    /// density are neither: they are the IDENTITY of the cell, which is what R-fp1-4c's "no handles"
    /// and R-fp1-2b's "two densities are two cells" both say in their own way.
    /// </summary>
    public static PCellGenerator GeneratorFor(FootprintRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return (parameters, technology, layerSelection) =>
            Generate(reference, technology, layerSelection, parameters);
    }

    /// <summary>
    /// The land pattern for <paramref name="reference"/> on <paramref name="technology"/>.
    ///
    /// <para>A missing OPTIONAL role omits its shapes and says so in
    /// <see cref="PCellResult.Diagnostics"/> (R-fp1-3a). A missing COPPER role is a REFUSAL: an
    /// empty result carrying one sentence (R-fp1-3b) — there is no land pattern without lands, and
    /// <see cref="PCellResult"/>'s diagnostics list is the only channel a pure generator has.</para>
    /// </summary>
    public static PCellResult Generate(
        FootprintRef reference,
        Technology? technology,
        PCellLayerSelection layerSelection,
        IReadOnlyDictionary<string, PCellValue>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(reference);

        // Every parameter this generator is handed is UNREAD, and saying so is not a formality: the
        // case and the density come from the cell's identity, so a user typing into a width field
        // would otherwise watch nothing happen with nothing to explain it.
        var unread = parameters is { Count: > 0 } ? parameters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray() : null;

        var diagnostics = new List<string>();
        var roles = LandPatternLayers.Resolve(technology, layerSelection, diagnostics);
        if (roles.Copper is not { } copper)
            return new PCellResult([], [], diagnostics, Handles: [], UnreadParameters: unread);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var pattern = LandPattern.For(reference.Case, reference.Density, dbuPerMicron);

        if (pattern.GapWasClamped)
        {
            diagnostics.Add(
                $"case {reference.Case.Code} at density {reference.DensityCode}: the termination bands leave no " +
                "separation at worst-case material condition, so the two lands meet. The pattern is drawn with a " +
                "zero gap; check the case's termination length before fabricating.");
        }

        long padHalfW = pattern.PadWidthDbu / 2;
        long padHalfH = pattern.PadHeightDbu / 2;
        long x1 = pattern.Pad1CentreXDbu;
        long x2 = pattern.Pad2CentreXDbu;

        var shapes = new List<LayoutShape>(8);

        // ── copper ──────────────────────────────────────────────────────────────────────────────
        shapes.Add(Pad(copper, x1, padHalfW, padHalfH, "1"));
        shapes.Add(Pad(copper, x2, padHalfW, padHalfH, "2"));

        // ── soldermask openings ─────────────────────────────────────────────────────────────────
        if (roles.Soldermask is { } mask)
        {
            long grow = LandPattern.Mm(MaskExpansionMm, dbuPerMicron);
            shapes.Add(Pad(mask, x1, padHalfW + grow, padHalfH + grow, "1"));
            shapes.Add(Pad(mask, x2, padHalfW + grow, padHalfH + grow, "2"));
        }

        // ── silkscreen ──────────────────────────────────────────────────────────────────────────
        // Two lines outside the lands, running the length of the body — the chip silkscreen, and the
        // only one that stays clear of the mask openings at every case size and density.
        if (roles.Silkscreen is { } silk)
        {
            long line = LandPattern.Mm(SilkLineWidthMm, dbuPerMicron);
            long clear = LandPattern.Mm(SilkClearanceMm, dbuPerMicron);
            long maskHalfH = padHalfH + (roles.Soldermask is null ? 0 : LandPattern.Mm(MaskExpansionMm, dbuPerMicron));
            long bodyHalfH = LandPattern.Mm(reference.Case.BodyWidthMm, dbuPerMicron) / 2;
            long y = Math.Max(maskHalfH, bodyHalfH) + clear + line / 2;
            long halfLen = LandPattern.Mm(reference.Case.BodyLengthMm, dbuPerMicron) / 2;

            shapes.Add(new RectShape { Layer = silk, X1 = -halfLen, Y1 =  y - line / 2, X2 = halfLen, Y2 =  y + line / 2 });
            shapes.Add(new RectShape { Layer = silk, X1 = -halfLen, Y1 = -y - line / 2, X2 = halfLen, Y2 = -y + line / 2 });
        }

        // ── courtyard ───────────────────────────────────────────────────────────────────────────
        // R-fp1-3c: never on Edge.Cuts. Where nothing declares an assembly layer this is omitted with
        // a diagnostic and is NOT relocated — LandPatternLayers has already said so.
        if (roles.Assembly is { } assembly)
        {
            long hx = pattern.CourtyardWidthDbu / 2;
            long hy = pattern.CourtyardHeightDbu / 2;
            shapes.Add(new PathShape
            {
                Layer = assembly,
                Xy = [-hx, -hy, hx, -hy, hx, hy, -hx, hy, -hx, -hy],
                Width = LandPattern.Mm(CourtyardLineWidthMm, dbuPerMicron),
                End = PathEndStyle.Flush,
            });
        }

        // R-fp1-4a/b: two pins, named for their ORDINAL. Not A/K, not +/-: a polarised part's
        // orientation is a property of the symbol and the placement, and a generator that named pins
        // +/- would have to know a capacitor from a resistor, which it does not and must not.
        var pins = new[]
        {
            new PCellPin("1", x1, 0, copper, pattern.PadHeightDbu, 180.0),
            new PCellPin("2", x2, 0, copper, pattern.PadHeightDbu,   0.0),
        };

        // R-fp1-4c: no handles. A case size is a discrete choice from a table, not a continuous
        // dimension — a grip here would let a user drag an 0402 into a shape no case code names, and
        // the reference string would then lie about what the artwork is.
        return new PCellResult(shapes, pins, diagnostics.Count > 0 ? diagnostics : null,
                               Handles: [], UnreadParameters: unread);
    }

    private static RectShape Pad(LayerKey layer, long cx, long halfW, long halfH, string pin) => new()
    {
        Layer = layer,
        X1 = cx - halfW, Y1 = -halfH,
        X2 = cx + halfW, Y2 =  halfH,
        Pin = pin,
    };
}
