// The neutral primitive vocabulary PL2's readers emit, and the one place it becomes a
// PcbFootprintCell (docs/sonnet-briefs/brief-PL2-component-library-breadth.md).
//
// ── Why this file exists at all ───────────────────────────────────────────────────────────────────
//
// PL1's four formats got their footprint half for free: `.kicad_mod` IS the board format, so
// PcbReader.ReadFootprint already produced a PcbFootprintCell and PL1 added 94 lines
// (src/Design/RESOLVED.md, "Phase PL1 — COMPLETE" §2). NONE of PL2's five formats share that
// lineage. Each states its own pads, its own outlines and its own layer numbering, so each would
// otherwise repeat the same three jobs: turn a pad into a LayoutPin plus its copper, turn an outline
// into a LayoutShape, and put both on a layer NAME the reconciliation step downstream understands.
// Written once here, five readers stay grammar-only.
//
// ── Handedness: the footprint half does NOT flip, and that is the opposite of PL1 ─────────────────
//
// PcbUnits.Y negates, because the board format is +y DOWN and .clay is +y UP. **Every format in this
// phase is already +y UP**, so a reader here passes Y through untouched — and calling PcbUnits.Y out
// of habit mirrors the whole land pattern.
//
// The reason is structural rather than a per-format quirk: the board format PL1 reuses is a PCB
// EDITOR's own on-disk frame, which is +y down like a screen, whereas every format in this phase
// states LIBRARY artwork in the drafting convention, +y up — the same convention .clay uses. So the
// flip PcbUnits.Y performs belongs to that one format and not to these.
//
// ComponentImportBreadthTests' Gate3c is what holds it shut, over a fixture whose pads sit at +30
// and +10 mil and nowhere below the axis: a footprint symmetric about its X axis imports identically
// whether the flip happened or not, which is the same trap PL1 §3 records for the symbol half,
// pointing the other way.
//
// The SYMBOL half is unaffected: readers hand ComponentSymbolPin its coordinates +y up exactly as
// PL1's do, and ComponentImport.FlipY performs the .csym flip downstream. Neither half flips here.

using CircuitRF.Core.Pdk;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>
/// What a source layer MEANS, independent of the number the file spelled it with.
///
/// <para>Every format in this phase numbers its layers differently and none of them ships a legend
/// (R-PL2-14). A reader classifies its own numbers into these, and this file alone decides the
/// canonical name — so "which layer is silkscreen" is answered once per format instead of once per
/// primitive.</para>
/// </summary>
public enum ComponentLayerRole
{
    /// <summary>Nothing said where this belongs. Lands on the fallback drawing layer and is REPORTED
    /// by its source number with a count, never silently given a meaning it does not have.</summary>
    Unknown,
    TopCopper,
    BottomCopper,
    TopSilkscreen,
    TopPaste,
    TopMask,
    TopAssembly,
    TopCourtyard,
    BoardOutline,
}

/// <summary>The pad outlines this phase's formats can state. Anything else is reported by name.</summary>
public enum ComponentPadForm
{
    Rectangle,
    Oval,
    Round,
    RoundedRectangle,
}

/// <summary>
/// One pad, in the source's own units with <b>Y already up</b>.
/// </summary>
/// <param name="PadName">The pad IDENTIFIER, as a string (PL1 R-PL1-9). Never the ordinal — three of
/// this phase's five formats carry both, and a thermal pad is routinely named rather than numbered.</param>
/// <param name="Width">Across X before <paramref name="RotationDeg"/> is applied.</param>
/// <param name="DrillDiameter">Zero for a surface pad.</param>
public sealed record ComponentPadSpec(
    string PadName,
    double X,
    double Y,
    double Width,
    double Height,
    ComponentPadForm Form,
    double RotationDeg = 0,
    double DrillDiameter = 0,
    double CornerRadius = 0);

/// <summary>One drawn element, in the source's own units with <b>Y already up</b>.</summary>
/// <param name="Xy">Flat x,y pairs. Two points is a segment; more is a run.</param>
/// <param name="Width">Pen width; zero draws a hairline.</param>
public sealed record ComponentArtworkPath(
    IReadOnlyList<double> Xy,
    double Width,
    ComponentLayerRole Role,
    bool Closed = false,
    bool Filled = false,
    int SourceLayer = 0);

/// <summary>One circle or circular arc, in the source's own units with <b>Y already up</b>.</summary>
public sealed record ComponentArtworkCircle(
    double Cx,
    double Cy,
    double Radius,
    double Width,
    ComponentLayerRole Role,
    int SourceLayer = 0);

/// <summary>
/// One land pattern as a reader recovered it, before units, layer names or DBU are involved.
///
/// <para><see cref="Variant"/> is PL1 R-PL1-25's density level. This phase's formats state it
/// differently from PL1's — inside the file rather than in the file NAME (the <c>.hkp</c> cell file
/// holds all three blocks, R-PL2-9; the <c>.p</c> part-type line separates them with colons,
/// R-PL2-5) — so a reader sets it explicitly instead of relying on
/// <c>ComponentRead.SplitDensityVariant</c>.</para>
/// </summary>
public sealed class ComponentArtwork
{
    public string Name { get; set; } = "";

    /// <summary><c>""</c> for the nominal pattern; <c>-M</c>/<c>-L</c> for a density sibling.</summary>
    public string Variant { get; set; } = "";

    public List<ComponentPadSpec> Pads { get; } = [];

    public List<ComponentArtworkPath> Paths { get; } = [];

    public List<ComponentArtworkCircle> Circles { get; } = [];

    /// <summary>Source layer numbers that classified as <see cref="ComponentLayerRole.Unknown"/>, by
    /// number with a count (R-PL2-14).</summary>
    public Dictionary<int, int> UnknownLayerCounts { get; } = [];

    /// <summary>Records one primitive drawn on a layer the format's own classifier did not
    /// recognise.</summary>
    public void NoteUnknownLayer(int sourceLayer)
        => UnknownLayerCounts[sourceLayer] = UnknownLayerCounts.GetValueOrDefault(sourceLayer) + 1;
}

/// <summary>
/// Turns a <see cref="ComponentArtwork"/> into the <see cref="PcbFootprintCell"/> the rest of the
/// import already consumes.
/// </summary>
public static class ComponentFootprintBuilder
{
    /// <summary>Microns in one mil — the scale every format here but <c>.cxf</c> states lengths in.</summary>
    public const double MicronsPerMil = 25.4;

    /// <summary>
    /// A source length in MILS as DBU. Rounds away from zero rather than casting: <c>(long)(x * k)</c>
    /// truncates toward zero and is therefore wrong only on the negative side, which is exactly what a
    /// fixture drawn in the first quadrant cannot see (PcbUnits' own note, R-L4d-2).
    /// </summary>
    public static long Mils(double mils, int dbuPerMicron)
        => (long)Math.Round(mils * MicronsPerMil * dbuPerMicron, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A source length in NANOMETRES as DBU — <c>.cxf</c>'s own unit (R-PL2-13).
    ///
    /// <para>At the default 1000 DBU/µm this is the IDENTITY: one DBU is one nanometre, so a pad the
    /// file writes as <c>-2140001</c> lands on <c>-2140001</c> with no rounding at all. Kept as a
    /// method anyway so a non-default resolution still converts, and so the identity is something a
    /// test can point at rather than arithmetic a reader open-codes.</para>
    /// </summary>
    public static long Nanometres(double nanometres, int dbuPerMicron)
        => (long)Math.Round(nanometres * dbuPerMicron / 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>The canonical layer name for a role, spelled as
    /// <see cref="PcbReader.SynthesiseFootprintLayerTable"/> spells it so both halves of the import
    /// reconcile against one vocabulary.</summary>
    public static string LayerName(ComponentLayerRole role) => role switch
    {
        ComponentLayerRole.TopCopper => "F.Cu",
        ComponentLayerRole.BottomCopper => "B.Cu",
        ComponentLayerRole.TopSilkscreen => "F.SilkS",
        ComponentLayerRole.TopPaste => "F.Paste",
        ComponentLayerRole.TopMask => "F.Mask",
        ComponentLayerRole.TopAssembly => "F.Fab",
        ComponentLayerRole.TopCourtyard => "F.CrtYd",
        ComponentLayerRole.BoardOutline => "Edge.Cuts",
        _ => PcbLayerNaming.FallbackName,
    };

    /// <summary>What <see cref="Build"/> produced, plus what it could not.</summary>
    public sealed record Result(
        PcbFootprintCell Cell,
        IReadOnlyList<PcbLayerTableEntry> LayerTable,
        IReadOnlyList<string> PadNames,
        IReadOnlyList<string> Messages);

    /// <summary>
    /// Builds the cell. <paramref name="toDbu"/> converts one source length to DBU — the ONLY thing
    /// that differs between a mil-stated format and <c>.cxf</c>'s nanometres, so it is a parameter
    /// rather than five near-copies of this method.
    /// </summary>
    public static Result Build(ComponentArtwork artwork, Func<double, long> toDbu)
    {
        var cell = new PcbFootprintCell { LibraryName = artwork.Name };
        var padNames = new List<string>();
        var messages = new List<string>();

        foreach (var pad in artwork.Pads)
        {
            long x = toDbu(pad.X);

            // Y passes through — see this file's header. The negation PcbUnits.Y performs belongs to
            // the board format alone.
            long y = toDbu(pad.Y);
            long w = Math.Abs(toDbu(pad.Width));
            long h = Math.Abs(toDbu(pad.Height));

            var copper = PadShape(pad, x, y, w, h, toDbu);
            copper.Layer = default;
            copper.Pin = pad.PadName;
            cell.Shapes.Add(new PcbImportedShape(copper, LayerName(ComponentLayerRole.TopCopper)));

            if (pad.DrillDiameter > 0)
            {
                var via = new ViaShape
                {
                    X = x,
                    Y = y,
                    PadSize = Math.Max(w, h),
                    DrillSize = Math.Abs(toDbu(pad.DrillDiameter)),
                    Pin = pad.PadName,
                };
                cell.Shapes.Add(new PcbImportedShape(via, "Drill", LayerName(ComponentLayerRole.TopCopper)));
            }

            cell.Pins.Add(new PcbImportedPin(
                new LayoutPin
                {
                    Name = pad.PadName,
                    X = x,
                    Y = y,
                    WidthDbu = Math.Min(w, h),
                    OutwardDeg = pad.RotationDeg,
                },
                LayerName(ComponentLayerRole.TopCopper)));

            padNames.Add(pad.PadName);
        }

        foreach (var path in artwork.Paths)
        {
            if (path.Xy.Count < 4) continue;
            long[] xy = new long[path.Xy.Count];
            for (int i = 0; i < path.Xy.Count; i++) xy[i] = toDbu(path.Xy[i]);

            LayoutShape shape = path.Filled
                ? new PolygonShape { Xy = xy }
                : new PathShape { Xy = xy, Width = Math.Abs(toDbu(path.Width)), End = PathEndStyle.Round };

            // A CLOSED run that is not filled is an outline: repeat the first point so the run closes
            // as a stroke rather than leaving the last edge undrawn.
            if (path is { Closed: true, Filled: false } && shape is PathShape stroke)
                stroke.Xy = [.. xy, xy[0], xy[1]];

            cell.Shapes.Add(new PcbImportedShape(shape, LayerName(path.Role)));
        }

        foreach (var circle in artwork.Circles)
            cell.Shapes.Add(new PcbImportedShape(
                new CircleShape { Cx = toDbu(circle.Cx), Cy = toDbu(circle.Cy), R = Math.Abs(toDbu(circle.Radius)) },
                LayerName(circle.Role)));

        foreach (var (layer, count) in artwork.UnknownLayerCounts.OrderBy(kv => kv.Key))
            messages.Add($"Layer {layer} has no known meaning in this format — " +
                         $"{count:N0} primitive(s) placed on {PcbLayerNaming.FallbackName}.");

        cell.ContentKey = artwork.Name;
        return new Result(cell, PcbReader.SynthesiseFootprintLayerTable(), padNames, messages);
    }

    /// <summary>The pad's own copper outline. An unrecognised form never reaches here — a reader
    /// reports it by name and omits the pad rather than substituting a rectangle (R-PL2-14).</summary>
    private static LayoutShape PadShape(
        ComponentPadSpec pad, long x, long y, long w, long h, Func<double, long> toDbu)
    {
        // A rotation that is not a multiple of 90° would need the outline expressed as a polygon; the
        // formats here state 0/90/180/270 and a quarter turn is just a width/height swap.
        bool quarter = Math.Abs(((pad.RotationDeg % 180) + 180) % 180 - 90) < 1e-6;
        if (quarter) (w, h) = (h, w);

        long hw = w / 2, hh = h / 2;

        return pad.Form switch
        {
            ComponentPadForm.Round =>
                new CircleShape { Cx = x, Cy = y, R = Math.Max(hw, hh) },

            ComponentPadForm.Oval =>
                new RoundedRectShape
                {
                    X1 = x - hw, Y1 = y - hh, X2 = x + hw, Y2 = y + hh,
                    CornerRadius = Math.Min(hw, hh),
                },

            ComponentPadForm.RoundedRectangle =>
                new RoundedRectShape
                {
                    X1 = x - hw, Y1 = y - hh, X2 = x + hw, Y2 = y + hh,
                    CornerRadius = Math.Min(Math.Abs(toDbu(pad.CornerRadius)), Math.Min(hw, hh)),
                },

            _ => new RectShape { X1 = x - hw, Y1 = y - hh, X2 = x + hw, Y2 = y + hh },
        };
    }
}
