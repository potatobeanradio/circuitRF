using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Matching;

/// <summary>
/// Turns a <see cref="MatchLadderLayout"/> into a real <see cref="SchematicModel"/> — the same read
/// model the schematic editor's canvas consumes.
/// </summary>
/// <remarks>
/// <b>The Designer's network pane is a circuitRF schematic, not a drawing of one</b> (owner,
/// 2026-08-19: <i>"the network schematic does not look good, it looks very different than a regular
/// circuitRF schematic — can we host a virtual schematic view in the Match Designer?"</i>). The
/// previous pane drew its own symbols, its own wires and its own three-line labels with its own
/// geometry, so every convention it shared with the editor was a convention somebody had copied and
/// could stop sharing. Building a <see cref="SchematicModel"/> instead means the pane goes through
/// <c>SchematicRenderer</c>: the grid, the symbol glyphs, the label roles, the connected-pin markers
/// and the LOD thresholds are the editor's, by construction, and a change to any of them lands here
/// with nothing to keep in step.
///
/// <para><b>Nothing here is editable and nothing is persisted.</b> This is a projection of the
/// ladder, rebuilt whenever the ladder changes; there is no <c>EditableSchematic</c> behind it, no
/// net extraction and no cell. The word "virtual" in the owner's ask is the whole contract.</para>
/// </remarks>
public static class MatchSchematicModel
{
    /// <summary>Half the length of a built-in 2-terminal symbol's own lead.</summary>
    private const double LeadHalf = MatchSchematicGeometry.LeadHalf;

    /// <summary>
    /// A shunt element's labels are pushed to the RIGHT of its column rather than left-below it.
    /// Below is where its own GND glyph sits, and a label laid over a symbol is the one place the
    /// editor's own default placement does not survive being reused: on a page the user drags it
    /// clear, and here there is nobody to do that.
    /// </summary>
    public const double ShuntLabelDx = 285.0;

    /// <summary>
    /// The matching vertical shift, which centres the three label rows on the symbol's own centre
    /// (owner, 2026-08-20: "adjust the vertical alignment such that the center of all 3 rows of text
    /// is at the same y coordinate as the center of the component symbol").
    /// </summary>
    /// <remarks>
    /// <b>Derived, not measured.</b> Row <i>i</i>'s baseline is
    /// <c>cy + LabelBaseY + dy + i·LabelWorldStep</c>, so a three-row block runs from
    /// <c>baseline₀ − LabelWorldHeight</c> to <c>baseline₂</c> and its centre sits at
    /// <c>cy + LabelBaseY + dy + LabelWorldStep − LabelWorldHeight/2</c>. Setting that equal to
    /// <c>cy</c> is the expression below — which means it tracks a change to the editor's own label
    /// metrics instead of going quietly stale, the way the hand-tuned −482 it replaces did.
    /// </remarks>
    public const double ShuntLabelDy = -(SchematicComponent.LabelBaseY
                                         + SchematicComponent.LabelWorldStep
                                         - SchematicComponent.LabelWorldHeight / 2.0);

    /// <summary>An empty model — what a refused design shows.</summary>
    public static SchematicModel Empty { get; } = new();

    /// <summary>Projects one ladder layout onto a schematic read model.</summary>
    public static SchematicModel Build(MatchLadderLayout? layout)
    {
        if (layout is null || layout.Elements.Count == 0) return Empty;

        var comps = new List<SchematicComponent>(layout.Elements.Count * 2 + 2);
        var wires = new List<SchematicWire>();

        foreach (var e in layout.Elements)
        {
            comps.Add(Element(e));

            // Every shunt arm carries its OWN ground, sitting exactly on the element's lower pin —
            // no rail, no drop wire, and no reference pin standing in for one.
            if (e.IsShunt) comps.Add(Ground(e.Name, e.X, MatchLadderLayout.ShuntGroundY));
        }

        // The two ends: a grounded termination each, never an interface pin (owner, 2026-08-20).
        // A Pin says "this net leaves the drawing"; a TermG says what the net actually runs into,
        // which is the whole subject of the Designer.
        foreach (var t in layout.Terminations)
            comps.Add(Termination(t));

        AddWiring(layout, wires);

        // A junction dot wherever a shunt arm taps the spine — the same mark the editor puts on a
        // genuine T-junction, and the reason a drop reads as a connection rather than as a wire that
        // happens to cross. There is no second dot at the bottom any more: the arm's GND is a
        // component sitting on the pin, not a wire meeting a rail.
        var dots = new List<SchematicDot>();
        foreach (var s in layout.Elements.Where(e => e.IsShunt))
            dots.Add(new SchematicDot(s.X, MatchLadderLayout.SpineY));

        Bounds(comps, wires, out double minX, out double minY, out double maxX, out double maxY);

        // The transform brackets live BELOW the ladder, and the model's bounding box has to cover
        // them or zoom-to-fit frames a drawing whose bottom half is off screen.
        if (layout.Brackets.Count > 0)
        {
            int rows = layout.Brackets.Max(b => b.Row) + 1;
            maxY = Math.Max(maxY, MatchLadderLayout.BracketY
                                  + rows * MatchLadderLayout.BracketRowPitch);
            minX = Math.Min(minX, layout.Brackets.Min(b => b.X0));
            maxX = Math.Max(maxX, layout.Brackets.Max(b => b.X1));
        }

        return new SchematicModel
        {
            Components     = comps,
            Wires          = wires,
            ConnectionDots = dots,
            GridSize       = 100.0,
            BbMinX = minX, BbMinY = minY,
            BbMaxX = maxX, BbMaxY = maxY,
        };
    }

    // ── Components ────────────────────────────────────────────────────────────

    private static SchematicComponent Element(MatchLadderElement e)
    {
        var kind = e.Type == ElementType.L ? SymbolKind.Inductor : SymbolKind.Capacitor;

        // The built-in 2-terminal glyphs are VERTICAL, which is what a shunt arm wants; a series arm
        // is the same glyph placed HORIZONTAL. R270, not R90 (owner, 2026-08-20: "have the series L
        // components rotated 180 degrees from their current orientation … make sure the flattened
        // cell uses the same final orientation"): R270 is the rotation MatchFlatten already writes,
        // so the Designer's picture and the cell it flattens into are now the same drawing rather
        // than mirror images of one. A capacitor is symmetric under the swap and does not care; an
        // inductor's coil bulge and its polarity dot do, which is what the owner was looking at.
        var rot = e.IsShunt ? SymbolRotation.R0 : SeriesRotation;

        string type = e.Type == ElementType.L ? "L" : "C";
        var labels = new List<string> { type, e.Name, $"{type} = {e.ValueText}" };
        var offsets = e.IsShunt
            ? Enumerable.Repeat((ShuntLabelDx, ShuntLabelDy), labels.Count).ToList()
            : [];

        return Compose(
            e.Name, kind, e.X, e.Y, rot, labels, offsets,
            [new SchematicPortDef("1", 0, -200, PortConnectionState.Connected),
             new SchematicPortDef("2", 0, +200, PortConnectionState.Connected)],
            // The glyph box is the REAL extent of the built-in symbol — both the inductor and the
            // capacitor run from lead tip to lead tip at ±200 — not that plus a little padding. It
            // feeds SchematicComponent.LabelBaseYFor, which pushes labels clear of a glyph deeper
            // than the default offset, so ten units of invented margin here silently moved every
            // shunt label ten units down and broke the centring ShuntLabelDy computes.
            e.IsShunt ? (-65, -200, 65, 200) : (-200, -65, 200, 65));
    }

    /// <summary>
    /// A shunt arm's own ground. <b>Neither the type label nor the instance name is drawn</b> — the
    /// glyph is unambiguous and the editor's own registry turns both off for a <c>Ground</c> by
    /// default (<c>ComponentTypeRegistry</c>), which is the setting this reproduces.
    /// </summary>
    private static SchematicComponent Ground(string ofElement, double x, double y) =>
        Compose(
            ofElement + GroundIdSuffix, SymbolKind.Ground, x, y, SymbolRotation.R0, [], [],
            [new SchematicPortDef("1", 0, 0, PortConnectionState.Connected)],
            (-45, 0, 45, 70));

    /// <summary>The suffix a shunt arm's own ground carries in the model, so it has a unique id.</summary>
    public const string GroundIdSuffix = "_GND";

    /// <summary>The rotation every SERIES element is placed at, here and in the flattened cell.</summary>
    public const SymbolRotation SeriesRotation = SymbolRotation.R270;

    /// <summary>
    /// One end's grounded termination, its "+" pin landing exactly on the end of the spine.
    /// </summary>
    /// <remarks>
    /// <c>TermG</c>'s own pin is at local (0, −200) and its ground bars run down to local +270, so
    /// the glyph is placed a lead-length OUTWARD of the pin and turned to face the ladder: R90 maps
    /// local (0, −200) to world (+200, 0) — the left end, body to the left of its pin — and R270 maps
    /// it to (−200, 0) for the right. The three label rows are the editor's own, with the resistance
    /// spelled the way a <c>Term</c>'s <c>Z</c> is.
    /// </remarks>
    private static SchematicComponent Termination(MatchLadderTermination t)
    {
        bool left = t.End == 1;
        double cx = left ? t.X - LeadHalf : t.X + LeadHalf;
        var rot = left ? SymbolRotation.R90 : SymbolRotation.R270;

        // The glyph box is given in WORLD offsets, so it carries the rotation itself: TermG's local
        // extent is x ∈ [−70, +45] (the polarity glyphs to the left of the body) and y ∈ [−200, +270]
        // (pin to the last ground bar), and R90 maps (x, y) → (−y, x) while R270 maps it → (y, −x).
        var glyph = left ? (-270.0, -70.0, 200.0, 45.0) : (-200.0, -45.0, 270.0, 70.0);

        return Compose(
            t.InstanceName, SymbolKind.TermG, cx, MatchLadderLayout.SpineY, rot,
            ["TermG", t.InstanceName, $"Z = {t.ResistanceText}"], [],
            [new SchematicPortDef("+", 0, -200, PortConnectionState.Connected)],
            glyph);
    }

    /// <summary>
    /// Assembles one component and its three bounding boxes. Every box the renderer and the spatial
    /// index read is computed here from <see cref="SchematicComponent"/>'s own label constants, so a
    /// change to the editor's label geometry moves this pane's culling with it.
    /// </summary>
    private static SchematicComponent Compose(
        string name, SymbolKind kind, double cx, double cy, SymbolRotation rot,
        IReadOnlyList<string> labels, IReadOnlyList<(double DX, double DY)> offsets,
        IReadOnlyList<SchematicPortDef> ports,
        (double MinX, double MinY, double MaxX, double MaxY) glyph)
    {
        double gMinX = cx + glyph.MinX, gMinY = cy + glyph.MinY;
        double gMaxX = cx + glyph.MaxX, gMaxY = cy + glyph.MaxY;

        double fullMinX = gMinX, fullMinY = gMinY, fullMaxX = gMaxX, fullMaxY = gMaxY;
        for (int i = 0; i < labels.Count; i++)
        {
            if (string.IsNullOrEmpty(labels[i])) continue;
            var (dx, dy) = i < offsets.Count ? offsets[i] : (0.0, 0.0);
            var (lx, ly, _, _) = SchematicComponent.LabelRowGeometry(
                cx, cy, i, dx, dy, kind, ports.Count / 2, gMaxY - cy);
            fullMinX = Math.Min(fullMinX, lx);
            fullMinY = Math.Min(fullMinY, ly - SchematicComponent.LabelWorldHeight);
            fullMaxX = Math.Max(fullMaxX, lx + SchematicComponent.LabelWidthFor(labels[i]));
            fullMaxY = Math.Max(fullMaxY, ly + 20.0);
        }

        return new SchematicComponent
        {
            Id = name, InstanceName = name, Symbol = kind,
            X = cx, Y = cy, Rotation = rot,
            Ports = ports, Labels = labels, LabelOffsets = offsets,
            BbMinX = cx - 200, BbMinY = cy - 200, BbMaxX = cx + 200, BbMaxY = cy + 200,
            GlyphBbMinX = gMinX, GlyphBbMinY = gMinY, GlyphBbMaxX = gMaxX, GlyphBbMaxY = gMaxY,
            FullBbMinX = fullMinX, FullBbMinY = fullMinY,
            FullBbMaxX = fullMaxX, FullBbMaxY = fullMaxY,
        };
    }

    // ── Wiring ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The spine and the shunt drops — <b>and nothing below a shunt arm</b>, because each carries its
    /// own <c>Ground</c> on its lower pin (owner, 2026-08-20).
    /// </summary>
    /// <remarks>
    /// <b>The spine is drawn in the GAPS between series elements, never through them</b>: a built-in
    /// glyph carries its own leads out to ±200, so a port-to-port line would lay a second wire across
    /// every series body. A schematic wire stops at the pin it connects to.
    /// </remarks>
    private static void AddWiring(MatchLadderLayout layout, List<SchematicWire> wires)
    {
        double y = MatchLadderLayout.SpineY;

        double cursor = layout.PortLeftX;
        foreach (var e in layout.Elements.Where(e => !e.IsShunt).OrderBy(e => e.X))
        {
            double left = e.X - LeadHalf;
            if (left > cursor) wires.Add(Wire(cursor, y, left, y));
            cursor = Math.Max(cursor, e.X + LeadHalf);
        }
        if (layout.PortRightX > cursor) wires.Add(Wire(cursor, y, layout.PortRightX, y));

        foreach (var s in layout.Elements.Where(e => e.IsShunt))
            wires.Add(Wire(s.X, y, s.X, s.Y - LeadHalf));
    }

    private static SchematicWire Wire(double x0, double y0, double x1, double y1) => new()
    {
        Points = [(x0, y0), (x1, y1)],
        BbMinX = Math.Min(x0, x1) - 5, BbMinY = Math.Min(y0, y1) - 5,
        BbMaxX = Math.Max(x0, x1) + 5, BbMaxY = Math.Max(y0, y1) + 5,
        StartConnected = true, EndConnected = true,
    };

    private static void Bounds(
        List<SchematicComponent> comps, List<SchematicWire> wires,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = double.MaxValue;
        maxX = maxY = double.MinValue;
        foreach (var c in comps)
        {
            minX = Math.Min(minX, c.FullBbMinX); minY = Math.Min(minY, c.FullBbMinY);
            maxX = Math.Max(maxX, c.FullBbMaxX); maxY = Math.Max(maxY, c.FullBbMaxY);
        }
        foreach (var w in wires)
        {
            minX = Math.Min(minX, w.BbMinX); minY = Math.Min(minY, w.BbMinY);
            maxX = Math.Max(maxX, w.BbMaxX); maxY = Math.Max(maxY, w.BbMaxY);
        }
        if (minX == double.MaxValue) { minX = minY = -100; maxX = maxY = 100; }
    }
}

/// <summary>Geometry constants the ladder projection and its overlay both read.</summary>
public static class MatchSchematicGeometry
{
    /// <summary>Half the length of a built-in 2-terminal symbol's own lead — the pins are at ±200.</summary>
    public const double LeadHalf = 200.0;
}
