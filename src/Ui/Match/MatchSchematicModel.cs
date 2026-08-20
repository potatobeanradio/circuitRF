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
    /// Below is where the drop to the ground rail runs, and a label laid over a wire is the one place
    /// the editor's own default placement does not survive being reused: on a page the user drags it
    /// clear, and here there is nobody to do that.
    /// </summary>
    private const double ShuntLabelDx = 285.0;

    /// <summary>Matching vertical shift — see <see cref="ShuntLabelDx"/>.</summary>
    private const double ShuntLabelDy = -482.0;

    /// <summary>An empty model — what a refused design shows.</summary>
    public static SchematicModel Empty { get; } = new();

    /// <summary>Projects one ladder layout onto a schematic read model.</summary>
    public static SchematicModel Build(MatchLadderLayout? layout)
    {
        if (layout is null || layout.Elements.Count == 0) return Empty;

        var comps = new List<SchematicComponent>(layout.Elements.Count + 4);
        var wires = new List<SchematicWire>();

        foreach (var e in layout.Elements)
            comps.Add(Element(e));

        // The interface pins: the two signal ports, and — only when the network HAS a ground rail —
        // the two reference terminals at its ends. Drawing a reference onto a rail that is not there
        // would be a picture of a different circuit.
        bool hasShunt = layout.Elements.Any(e => e.IsShunt);
        comps.Add(Pin("P1", "1", layout.PortLeftX,  MatchLadderLayout.SpineY, pointsRight: true));
        comps.Add(Pin("P2", "2", layout.PortRightX, MatchLadderLayout.SpineY, pointsRight: false));
        if (hasShunt)
        {
            comps.Add(Pin("P3", "3", layout.PortLeftX,  MatchLadderLayout.GroundY, pointsRight: true));
            comps.Add(Pin("P4", "4", layout.PortRightX, MatchLadderLayout.GroundY, pointsRight: false));
        }

        AddWiring(layout, wires);

        // A junction dot wherever a shunt arm meets the spine or the rail — the same mark the editor
        // puts on a genuine T-junction, and the reason the drops read as connections rather than as
        // wires that happen to cross.
        var dots = new List<SchematicDot>();
        foreach (var s in layout.Elements.Where(e => e.IsShunt))
        {
            dots.Add(new SchematicDot(s.X, MatchLadderLayout.SpineY));
            dots.Add(new SchematicDot(s.X, MatchLadderLayout.GroundY));
        }

        Bounds(comps, wires, out double minX, out double minY, out double maxX, out double maxY);

        // The transform brackets live BELOW the rail, and the model's bounding box has to cover them
        // or zoom-to-fit frames a drawing whose bottom half is off screen.
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
        // is the same glyph placed at R90, exactly as it would be on a page.
        var rot = e.IsShunt ? SymbolRotation.R0 : SymbolRotation.R90;

        string type = e.Type == ElementType.L ? "L" : "C";
        var labels = new List<string> { type, e.Name, $"{type} = {e.ValueText}" };
        var offsets = e.IsShunt
            ? Enumerable.Repeat((ShuntLabelDx, ShuntLabelDy), labels.Count).ToList()
            : [];

        return Compose(
            e.Name, kind, e.X, e.Y, rot, labels, offsets,
            [new SchematicPortDef("1", 0, -200, PortConnectionState.Connected),
             new SchematicPortDef("2", 0, +200, PortConnectionState.Connected)],
            e.IsShunt ? (-65, -210, 65, 210) : (-210, -65, 210, 65));
    }

    /// <summary>
    /// One interface pin, its terminal landing exactly on the wire it marks. Pin's own terminal is at
    /// local (+100, 0), so the glyph sits 100 behind the tip — R180 for the pair that face inward
    /// from the right, which is what mirroring one on a page does.
    /// </summary>
    private static SchematicComponent Pin(string name, string num, double tipX, double tipY, bool pointsRight)
    {
        double sx = pointsRight ? 1.0 : -1.0;
        double cx = tipX - sx * 100.0;
        var rot = pointsRight ? SymbolRotation.R0 : SymbolRotation.R180;

        // Only the port NUMBER is labelled. A Pin is registered with the type label and the instance
        // name off by default (ComponentTypeRegistry), and repeating "Pin"/"P1" beside four of them
        // says nothing the glyph has not already said.
        return Compose(
            name, SymbolKind.Pin, cx, tipY, rot, [num], [],
            [new SchematicPortDef("1", 100, 0, PortConnectionState.Connected)],
            (-100, -50, 100, 50));
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
    /// The spine, the shunt drops and the ground rail. <b>The spine is drawn in the GAPS between
    /// series elements, never through them</b>: a built-in glyph carries its own leads out to ±200,
    /// so a port-to-port line would lay a second wire across every series body. A schematic wire
    /// stops at the pin it connects to.
    /// </summary>
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

        var shunts = layout.Elements.Where(e => e.IsShunt).ToList();
        if (shunts.Count == 0) return;

        double gy = MatchLadderLayout.GroundY;
        wires.Add(Wire(layout.PortLeftX, gy, layout.PortRightX, gy));
        foreach (var s in shunts)
        {
            wires.Add(Wire(s.X, y, s.X, s.Y - LeadHalf));
            wires.Add(Wire(s.X, s.Y + LeadHalf, s.X, gy));
        }
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
