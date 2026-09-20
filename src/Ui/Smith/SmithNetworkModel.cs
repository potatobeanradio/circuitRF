using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// One build of the network strip's drawing: the editable schematic it was projected onto, the render
/// model <c>SchematicRenderer</c> consumes, its spatial index, and the map back from a drawn component
/// to the element it came from.
/// </summary>
/// <param name="Edit">The projection as REAL editable components and wires. Brief 7's copy hands this
/// (or a clone of it) to <c>SchematicClipboard</c>, which is what makes "the copy is the drawing you
/// were looking at" true by construction rather than by a second layout pass.</param>
/// <param name="Model">The immutable render snapshot, built by <c>SchematicEditModel.BuildRenderModel</c>
/// — the schematic editor's own.</param>
/// <param name="Index">Its spatial index, which <c>SchematicHitTest</c> needs.</param>
/// <param name="ElementIndexByComponentId">Which <see cref="SmithDesign.Elements"/> index a drawn
/// component belongs to. A ground, the generator and the load marker are absent from it.</param>
/// <param name="ColumnX">The world x of each element's column, in element order — what a reorder drag
/// measures the drop position against.</param>
public sealed record SmithNetworkProjection(
    SchematicEditModel                Edit,
    SchematicModel                    Model,
    SchematicSpatialIndex             Index,
    IReadOnlyDictionary<string, int>  ElementIndexByComponentId,
    IReadOnlyList<double>             ColumnX)
{
    /// <summary>What an empty cascade draws — the generator and the load, and the spine between.</summary>
    public static SmithNetworkProjection Empty { get; } =
        SmithNetworkModel.Build(new SmithDesign(), documentDirectory: null);

    /// <summary>The component drawn for element <paramref name="index"/>, or null.</summary>
    public SchematicComponent? ComponentFor(int index)
    {
        foreach (var kv in ElementIndexByComponentId)
            if (kv.Value == index)
                return Model.Components.FirstOrDefault(c => c.Id == kv.Key);
        return null;
    }
}

/// <summary>
/// The cascade drawn as a real circuitRF schematic (<c>brief-smith-6-network-strip.md</c>
/// <c>R-smith6-1</c>; <c>docs/design/smith-chart.md</c> §5.5).
/// </summary>
/// <remarks>
/// <b>It is a PROJECTION and the renderer is the schematic editor's.</b> <c>MatchSchematicModel</c> is
/// the worked example and its two findings are this file's too — <b>one ground per COLUMN</b>, sitting
/// on the lowest pin of that column rather than one per shunt element, and <b>spine wires in the GAPS
/// between series bodies</b>, because a built-in glyph carries its own leads out to ±200 and a
/// port-to-port line would lay a second wire across every series body.
///
/// <para><b>Where this DIFFERS from <c>MatchSchematicModel</c>, and why.</b> That one assembles
/// <see cref="SchematicComponent"/> records by hand, which meant hand-writing a glyph extent per symbol
/// kind. This one builds a real <see cref="SchematicEditModel"/> and calls its own
/// <c>BuildRenderModel</c>. The Designer's ladder is two symbol kinds; this tool's vocabulary is
/// eleven, including the three whose glyph is COMPUTED from a parameter (<c>Snp</c>'s body grows with
/// its port count and its <c>RefNode</c>) — so a hand-written extent table here would be eleven
/// chances to size a box wrong, and the labels, the connected-pin markers and the connection dots come
/// free instead. It is also exactly the object brief 7's copy needs.</para>
///
/// <para><b>Nothing here is editable and nothing is persisted.</b> There is no selection model beyond
/// "which element is selected", no wire tool and no free placement, because the topology is a list.
/// The model is rebuilt from the design on every change.</para>
/// </remarks>
public static class SmithNetworkModel
{
    // ── Geometry, on the Designer's own constants ────────────────────────────

    /// <summary>Half the length of a built-in two-terminal symbol's own lead — pins are at ±200.</summary>
    public const double LeadHalf = 200.0;

    /// <summary>
    /// Column pitch. <b>700, the Designer's own</b> — and for its reason: a label's width in WORLD
    /// units is very nearly constant, so widening the pitch buys real clearance where shrinking
    /// everything by one factor would buy none. A value plus its name needs roughly 300 world units.
    /// </summary>
    public const double Pitch = 700.0;

    /// <summary>The through-path's y.</summary>
    public const double SpineY = 0.0;

    /// <summary>A shunt element's centre y — one lead-length below the spine, so its upper pin lands
    /// exactly ON it and there is no drop wire to draw.</summary>
    public const double ShuntY = SpineY + LeadHalf;

    /// <summary>Where a shunt column's own <c>Ground</c> sits — exactly on the lower pin, so again
    /// there is no wire. <c>Ground</c>'s pin is at its local origin.</summary>
    public const double ShuntGroundY = ShuntY + LeadHalf;

    /// <summary>The suffix a shunt column's own ground carries, so its id is unique.</summary>
    public const string GroundNameSuffix = "_GND";

    /// <summary>The generator termination's instance name.</summary>
    public const string GeneratorName = "Gen";

    /// <summary>The load marker's instance name.</summary>
    public const string LoadName = "load";

    // ── The build ────────────────────────────────────────────────────────────

    /// <summary>
    /// Projects one design's cascade onto a schematic.
    /// </summary>
    /// <param name="design">The document. Read, never written.</param>
    /// <param name="documentDirectory">What a file element's relative <c>FileRef</c> resolves against —
    /// used only to put the generator's own impedance on its label, and a refusal there simply leaves
    /// that one label blank rather than emptying the drawing.</param>
    /// <param name="terminated">
    /// <b>The COPY's projection</b> (<c>R-smith7-2</c>): both ends become a real <c>TermG</c> — port 1
    /// carrying the generator's impedance at the design frequency, port 2 carrying Z₀_chart — so what
    /// lands in a schematic is a complete, runnable two-port rather than a fragment with dangling
    /// ends.
    ///
    /// <para><b>A flag on the ONE projection rather than a second build.</b> Everything brief 7 needs
    /// is already decided here — the mirror's sign and symbol rule, one ground per column, the spine
    /// drawn in the gaps, the labels off <c>ComponentTypeRegistry</c> — and a copy that re-derived any
    /// of it would be a drawing that agreed with the strip until one of them was changed. The two ends
    /// are the whole difference, which is why they are the whole of this parameter.</para>
    /// </param>
    public static SmithNetworkProjection Build(SmithDesign design, string? documentDirectory,
                                               bool terminated = false)
    {
        ArgumentNullException.ThrowIfNull(design);

        bool mirrored = design.View.MirrorNetwork;

        // THE MIRROR IS A SIGN AND A FLAG, and the axis is x = 0 (R-smith6-6). The generator sits at
        // the origin and every column advances by ±Pitch from it, so flipping the drawing is exactly
        // x → −x: the generator stays put, the cascade grows the other way, and the reflection of any
        // point in the drawing is its own negation. That is what lets the gate assert reflected pin
        // coordinates as an equality rather than as "about a centre somebody computed".
        double dir = mirrored ? -1.0 : +1.0;

        var edit = new SchematicEditModel { GridSize = 100.0, GridSnap = false };
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var columns = new List<double>(design.Elements.Count);

        // ── The generator ────────────────────────────────────────────────────
        // A TermG and not a Pin: the generator IS an impedance to ground, and a Pin would say "this
        // net leaves the drawing", which is the one thing it does not do. Its own pin is at local
        // (0, −200), so a centre one lead-length BELOW the spine puts that pin on it.
        edit.Components.Add(Compose(
            SymbolKind.TermG, GeneratorName, 0.0, SpineY + LeadHalf, SymbolRotation.R0, mirrored,
            terminated ? PortParameters(1, GeneratorImpedance(design))
                       : [Param("Z", GeneratorLabel(design), "", UnitDimension.Resistance)]));

        // ── The elements ─────────────────────────────────────────────────────
        for (int i = 0; i < design.Elements.Count; i++)
        {
            var e = design.Elements[i];
            double x = dir * (i + 1) * Pitch;
            columns.Add(x);

            var comp = ElementComponent(e, x, mirrored);
            edit.Components.Add(comp);
            byId[comp.Id] = i;

            if (GroundedPinIndex(e) is { } pin)
            {
                var (gx, gy) = comp.GetPortWorldCoord(pin);
                edit.Components.Add(Compose(
                    SymbolKind.Ground, e.Name + GroundNameSuffix, gx, gy, SymbolRotation.R0, mirrored,
                    []));
            }
        }

        // ── The load end ─────────────────────────────────────────────────────
        // A Pin, and deliberately NOT the Designer's TermG: this tool has no load element and nothing
        // terminates the cascade — "the load is where you read" (§3.2). A TermG here would draw an
        // impedance the document does not have. Brief 7's COPY adds one, because a pasted fragment has
        // to be runnable; the drawing states the model instead.
        //
        // Pin's own pin is at local (100, 0) with the body to its left, so R180 puts the tip on the
        // spine end and the body pointing outward. The COPY's TermG sits where the generator's does,
        // one lead-length below the spine, so its own pin lands on the spine end instead.
        double loadX = dir * (design.Elements.Count + 1) * Pitch;
        edit.Components.Add(terminated
            ? Compose(SymbolKind.TermG, LoadName, loadX, SpineY + LeadHalf, SymbolRotation.R0,
                      mirrored, PortParameters(2, new Complex(design.Chart.Z0Ohm, 0.0)))
            : Compose(SymbolKind.Pin, LoadName, loadX + dir * 100.0, SpineY, SymbolRotation.R180,
                      mirrored, []));

        AddSpineWires(edit, design, mirrored, loadX);

        var (built, _) = edit.BuildRenderModel();
        var model      = WithJunctionDots(built, design, columns);

        return new SmithNetworkProjection(edit, model, new SchematicSpatialIndex(model), byId, columns);
    }

    /// <summary>
    /// Replaces the render model's connection dots with <b>one per shunt tap, and nothing else</b>.
    /// </summary>
    /// <remarks>
    /// <b>This is the one place the projection overrides the editor's own answer, and it is the
    /// Designer's decision rather than a new one.</b> <c>SchematicEditModel</c> emits an auto-dot
    /// wherever a component pin coincides with another connection endpoint — which on a page someone
    /// wired by hand is exactly right, because a pin that landed on a wire end is worth marking. In a
    /// PROJECTION every such meeting is by construction: each series element's two lead tips sit on the
    /// spine's two wire ends and each shunt arm's ground sits on its own lower pin, so the editor's rule
    /// puts eleven dots on a five-element strip where a reader needs two.
    ///
    /// <para>What a junction dot MEANS is a branch, and the only branch in a cascade is where a shunt
    /// arm taps the through path. That is <c>MatchSchematicModel</c>'s own rule, arrived at over the
    /// owner's review of that pane ("there is no second dot at the bottom any more: the arm's GND is a
    /// component sitting on the pin, not a wire meeting a rail"), and the two panes should not disagree
    /// about it while sitting in one application.</para>
    ///
    /// <para>Everything else about the model is the editor's, untouched — the glyphs, the labels, the
    /// bounding boxes and the connected-pin markers. Rebuilt field by field because
    /// <see cref="SchematicModel"/> is an init-only class rather than a record, so there is no
    /// <c>with</c> to use; the list is short and adding to it without adding a line here would drop the
    /// new field, which is why it is spelled out rather than reflected over.</para>
    /// </remarks>
    private static SchematicModel WithJunctionDots(
        SchematicModel model, SmithDesign design, IReadOnlyList<double> columns)
    {
        var dots = new List<SchematicDot>(columns.Count);
        for (int i = 0; i < design.Elements.Count && i < columns.Count; i++)
            if (design.Elements[i].Placement == SmithPlacement.Shunt)
                dots.Add(new SchematicDot(columns[i], SpineY));

        return new SchematicModel
        {
            Components     = model.Components,
            Wires          = model.Wires,
            ConnectionDots = dots,
            NetLabels      = model.NetLabels,
            Bitmaps        = model.Bitmaps,
            GridSize       = model.GridSize,
            BbMinX = model.BbMinX, BbMinY = model.BbMinY,
            BbMaxX = model.BbMaxX, BbMaxY = model.BbMaxY,
        };
    }

    // ── One element ──────────────────────────────────────────────────────────

    private static EditableComponent ElementComponent(SmithElement e, double x, bool mirrored)
    {
        var binding = SmithComponentMap.Component(e.Kind);
        bool shunt  = e.Placement == SmithPlacement.Shunt;

        var comp = Compose(
            binding.SymbolKind,
            string.IsNullOrWhiteSpace(e.Name) ? e.Kind.ToString() : e.Name,
            x, shunt ? ShuntY : SpineY,
            RotationFor(e), mirrored,
            Parameters(e, binding));

        // A DISABLED element keeps its values and its place and contributes nothing (R-smith6-2), so
        // the drawing says so with the schematic's OWN DisableState convention rather than a dimming
        // of its own. WHICH state is not a style choice: the evaluator skips a disabled element
        // entirely, and skipping a SERIES element is a short through it while skipping a SHUNT one is
        // an open. Drawing both as Open would put an X across a series part whose two nets the
        // cascade has just merged — a true picture of a different circuit.
        comp.Disable = e.Enabled
            ? DisableState.None
            : shunt ? DisableState.Open : DisableState.Short;

        return comp;
    }

    /// <summary>
    /// How an element is turned on the page, before the mirror.
    /// </summary>
    /// <remarks>
    /// The two-terminal lumped glyphs are drawn VERTICAL, which is what a shunt arm wants; a series one
    /// is the same glyph laid on its side at <c>R270</c> — the Designer's own choice, and the rotation
    /// <c>MatchFlatten</c> writes, so a pane and the cell it flattens into are the same drawing.
    ///
    /// <para>The rest are HORIZONTAL two-pin glyphs — <c>TLIN</c>, <c>SnP</c> and a one-port
    /// <c>Z_Port</c> all put their pins at local (±200, 0) — so a series one needs no rotation at all
    /// and a shunt one is stood upright at <c>R90</c>, which puts pin 1 on the spine and pin 2 (or the
    /// implicit reference) below it.</para>
    /// </remarks>
    private static SymbolRotation RotationFor(SmithElement e)
        => RotationFor(e.Kind, e.Placement);

    /// <summary>
    /// The same rule, reachable without an element — what the Add and Insert menus turn each entry's
    /// glyph by, so a menu row and the part it places are drawn the same way up.
    /// </summary>
    public static SymbolRotation RotationFor(SmithElementKind kind, SmithPlacement placement)
        => placement == SmithPlacement.Shunt
               ? (IsVerticalGlyph(kind) ? SymbolRotation.R0  : SymbolRotation.R90)
               : (IsVerticalGlyph(kind) ? SymbolRotation.R270 : SymbolRotation.R0);

    /// <summary>True for the kinds whose built-in glyph runs top to bottom with its pins at
    /// (0, ∓200) — R, L, C, SRLC and PRLC.</summary>
    private static bool IsVerticalGlyph(SmithElementKind kind)
        => kind is SmithElementKind.R or SmithElementKind.L or SmithElementKind.C
                or SmithElementKind.Srlc or SmithElementKind.Prlc;

    /// <summary>
    /// Which of an element's pins a <c>Ground</c> sits on, or null when the column has no grounded pin
    /// to put one on.
    /// </summary>
    /// <remarks>
    /// <b>One ground per COLUMN and not one per shunt element</b> — the Designer's own finding — which
    /// here is the same statement, because a Smith cascade has one element per column. The three
    /// columns that get NONE each have a reason and none of them is an oversight:
    /// a <b>series</b> element has no reference pin at all; an <b>open stub</b>'s far end is open,
    /// which is what tells it apart from the shorted one; and a <b>shunt S1P</b> is a one-port whose
    /// reference is implicit, so it has a single pin and there is nothing for a ground glyph to land
    /// on. Drawing one anyway would put a ground symbol in mid-air.
    /// </remarks>
    private static int? GroundedPinIndex(SmithElement e)
    {
        if (e.Placement != SmithPlacement.Shunt)     return null;
        if (e.Kind == SmithElementKind.StubOpen)     return null;
        if (e.Kind == SmithElementKind.S1P)          return null;
        return 1;
    }

    // ── The spine ────────────────────────────────────────────────────────────

    /// <summary>
    /// The through-path, <b>drawn in the GAPS between series bodies and never through them</b>.
    /// </summary>
    /// <remarks>
    /// The Designer's second finding, verbatim: a built-in glyph carries its own leads out to
    /// ±<see cref="LeadHalf"/>, so a port-to-port line would lay a second wire across every series
    /// body. A schematic wire stops at the pin it connects to.
    ///
    /// <para>There is <b>no vertical wire anywhere in the drawing</b>. A shunt element's upper pin is
    /// already on the spine (<see cref="ShuntY"/>) and its ground sits on its lower pin, so both runs
    /// a drop wire would span are zero-length — and a wire whose endpoints coincide is not a shorter
    /// wire, it is no wire.</para>
    /// </remarks>
    private static void AddSpineWires(SchematicEditModel edit, SmithDesign design, bool mirrored,
                                      double loadX)
    {
        double dir = mirrored ? -1.0 : +1.0;

        // Every x the spine must NOT be drawn across: a series element's own body. Walk in drawing
        // order — left to right unmirrored, right to left mirrored — so one cursor covers both.
        var blocked = new List<(double Lo, double Hi)>();
        for (int i = 0; i < design.Elements.Count; i++)
        {
            if (design.Elements[i].Placement != SmithPlacement.Series) continue;
            double x = dir * (i + 1) * Pitch;
            blocked.Add((Math.Min(x - LeadHalf, x + LeadHalf), Math.Max(x - LeadHalf, x + LeadHalf)));
        }
        blocked.Sort((a, b) => a.Lo.CompareTo(b.Lo));

        double from = Math.Min(0.0, loadX);
        double to   = Math.Max(0.0, loadX);

        double cursor = from;
        foreach (var (lo, hi) in blocked)
        {
            if (lo > cursor) edit.Wires.Add(Wire(cursor, SpineY, lo, SpineY));
            cursor = Math.Max(cursor, hi);
        }
        if (to > cursor) edit.Wires.Add(Wire(cursor, SpineY, to, SpineY));
    }

    private static EditableWire Wire(double x0, double y0, double x1, double y1)
    {
        var w = new EditableWire();
        w.Points.Add((x0, y0));
        w.Points.Add((x1, y1));
        return w;
    }

    // ── Parameters, from the registry and nowhere else ───────────────────────

    /// <summary>
    /// The labelled parameters one element draws.
    /// </summary>
    /// <remarks>
    /// <b>The NAMES and the UNITS are <c>ComponentTypeRegistry</c>'s</b> — there is no second set of
    /// either here, which is what makes the drawn label and the netlist brief 7 copies out agree about
    /// what a part is. What this supplies is the VALUE, formatted out of the element's own base-SI
    /// number by <see cref="MatchValueFormat"/>: a document stores 3.9e-9 H and a schematic label says
    /// <c>L = 3.9 nH</c>, and the scale lives only in the label.
    /// </remarks>
    private static IReadOnlyList<EditableParameter> Parameters(
        SmithElement e, SmithComponentBinding binding)
    {
        var v = e.Values;

        switch (e.Kind)
        {
            case SmithElementKind.R:
                return [Value("R", v.ROhm, MatchQuantity.Resistance, UnitDimension.Resistance)];

            case SmithElementKind.L:
                return [Value("L", v.LHenry, MatchQuantity.Inductance, UnitDimension.Inductance)];

            case SmithElementKind.C:
                return [Value("C", v.CFarad, MatchQuantity.Capacitance, UnitDimension.Capacitance)];

            case SmithElementKind.Srlc:
            case SmithElementKind.Prlc:
                return
                [
                    Value("R", v.ROhm,   MatchQuantity.Resistance,  UnitDimension.Resistance),
                    Value("L", v.LHenry, MatchQuantity.Inductance,  UnitDimension.Inductance),
                    Value("C", v.CFarad, MatchQuantity.Capacitance, UnitDimension.Capacitance),
                ];

            case SmithElementKind.Z1P:
                // The engine's own spelling of a complex constant, so what is drawn is what a netlist
                // would carry — `Z_Port` reads `complex(re,im)` and nothing else.
                return
                [
                    Param("NumPorts", "1", "", UnitDimension.None, show: false),
                    Param("Z[1,1]",
                          $"complex({Num(v.ImpedanceOhm.Real)},{Num(v.ImpedanceOhm.Imaginary)})",
                          "Ω", UnitDimension.Resistance),
                ];

            case SmithElementKind.S1P:
            case SmithElementKind.S2P:
                // A one-port file in SERIES binds N+1 nets, the extra one being its floating
                // reference — which on the symbol is `RefNode`, the pin that makes a one-port
                // drawable in a through path at all. In shunt the reference is ground and there is
                // no second pin.
                return
                [
                    Param("NumPorts", binding.NumPorts.ToString(CultureInfo.InvariantCulture), "",
                          UnitDimension.None, show: false),
                    Param("File", e.FileRef ?? "", "", UnitDimension.None),
                    Param("RefNode",
                          e.Kind == SmithElementKind.S1P && e.Placement == SmithPlacement.Series
                              ? "true" : "false",
                          "", UnitDimension.None, show: false),
                ];

            default:   // the three TLIN-backed kinds
                return
                [
                    Value("Z", v.Z0Ohm, MatchQuantity.Resistance, UnitDimension.Resistance),
                    Param("E", MatchValueFormat.Significant(v.ElectricalLengthDeg, 5), "deg",
                          UnitDimension.Angle),
                    Value("F", v.ReferenceFrequencyHz, MatchQuantity.Frequency, UnitDimension.Frequency),
                ];
        }
    }

    private static EditableParameter Value(string name, double value, MatchQuantity quantity,
                                           UnitDimension dimension)
    {
        var (text, unit) = MatchValueFormat.Format(value, quantity, MatchValueFormat.AutoUnit, 5);
        return Param(name, text, unit, dimension);
    }

    private static EditableParameter Param(string name, string expression, string unit,
                                           UnitDimension dimension, bool show = true) => new()
    {
        Name = name, Expression = expression, Unit = unit,
        ShowOnSchematic = show, Dimension = dimension,
    };

    private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>
    /// The generator's impedance at the design frequency, or Z₀_chart when it cannot be evaluated
    /// there — the COPY's own reading of it.
    /// </summary>
    /// <remarks>
    /// <b>A refusal falls back rather than throwing</b>, on <see cref="GeneratorLabel"/>'s own terms:
    /// the copy's whole job is to produce a runnable two-port, and a port with no impedance at all is
    /// not one. Z₀_chart is the only other number in the document that is certainly a real impedance.
    /// </remarks>
    private static Complex GeneratorImpedance(SmithDesign design)
    {
        try   { return SmithCascade.GeneratorImpedance(design.Generator, design.Chart.DesignFrequencyHz); }
        catch { return new Complex(design.Chart.Z0Ohm, 0.0); }
    }

    /// <summary>
    /// A terminated end's two parameters — <c>Num</c> and a <c>Z</c> a netlist reader will actually
    /// take (<c>R-smith7-2</c>).
    /// </summary>
    /// <remarks>
    /// <b>Two spellings, and the split is the engine's rather than a preference.</b> A real impedance
    /// is written as a number and its unit, which is what the Designer's own copy writes and what
    /// reads naturally on a figure — <c>Z = 50 Ω</c>. A COMPLEX one has no such spelling: the only
    /// form the expression engine parses is <c>complex(re,im)</c>, which is exactly what brief 2's
    /// oracle <c>.cnl</c> writes for the generator and what <c>SParameterEngine.GetZ0</c> reads back
    /// as a complex reference impedance.
    ///
    /// <para><b>And the complex form carries NO unit.</b> Ω is scale 1.0, so a unit would buy nothing
    /// — and <c>""</c> is not "no unit" to the extractor's own convention, it is a unit that fails
    /// elaboration with <c>Unknown unit ''</c> three layers from anything the user did
    /// (<c>src/Core/CLAUDE.md</c>). <c>EditableParameter.Unit</c> of <c>""</c> is what
    /// <c>NetExtractor</c> turns into the <c>null</c> that means no unit.</para>
    /// </remarks>
    private static IReadOnlyList<EditableParameter> PortParameters(int num, Complex z)
    {
        var numParam = Param("Num", num.ToString(CultureInfo.InvariantCulture), "", UnitDimension.None);

        bool real = Math.Abs(z.Imaginary) <= 1e-12 * Math.Max(1.0, Math.Abs(z.Real));
        var zParam = real
            ? Value("Z", z.Real, MatchQuantity.Resistance, UnitDimension.Resistance)
            : Param("Z", $"complex({Num(z.Real)},{Num(z.Imaginary)})", "", UnitDimension.Resistance);

        return [numParam, zParam];
    }

    /// <summary>
    /// The generator's impedance at the design frequency, for its termination's one label — or "" when
    /// the design cannot be evaluated there.
    /// </summary>
    /// <remarks>
    /// <b>A refusal here leaves the label blank and draws everything else.</b> The status strip is
    /// already saying what is wrong, with its numbers in it (<c>R-smith4-8</c>); emptying the network
    /// pane as well would take the drawing away at exactly the moment the user is looking for the part
    /// that caused it.
    /// </remarks>
    private static string GeneratorLabel(SmithDesign design)
    {
        try
        {
            var z = SmithCascade.GeneratorImpedance(design.Generator, design.Chart.DesignFrequencyHz);
            string sign = z.Imaginary < 0 ? "−" : "+";
            return $"{MatchValueFormat.Significant(z.Real, 4)} {sign} j"
                 + $"{MatchValueFormat.Significant(Math.Abs(z.Imaginary), 4)}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ── Placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// One placed component. <paramref name="x"/> and <paramref name="y"/> are already WORLD
    /// coordinates — the caller advances its cursor in the mirrored direction — and what this applies
    /// is the mirror's effect on the SYMBOL.
    /// </summary>
    /// <remarks>
    /// <b>The mirror is <c>MirrorX</c> AND the opposite rotation, and the second half is not optional.</b>
    /// <c>SchematicGeometry.LocalToWorld</c> applies the mirror in the symbol's own LOCAL frame, before
    /// the rotation — so on a part standing at <c>R270</c>, <c>MirrorX</c> flips the glyph across the
    /// wire rather than along it, and the part's two pins stay exactly where they were. A reflection of
    /// the DRAWING is <c>Wₓ ∘ R(θ) = R(−θ) ∘ Mₓ</c>, which is this one line: negate the rotation and
    /// set the flag. It costs nothing on a symbol already at <c>R0</c> — which is every horizontal
    /// glyph, including the <c>S2P</c> whose port-1 marking is the reason the brief asks for the
    /// mirror at all — and it is what makes a mirrored pin coordinate the true reflection of the
    /// unmirrored one rather than its body's reflection with its ends swapped.
    /// </remarks>
    private static EditableComponent Compose(
        SymbolKind kind, string name, double x, double y, SymbolRotation rotation, bool mirrored,
        IReadOnlyList<EditableParameter> parameters)
    {
        var info = ComponentTypeRegistry.Get(kind);

        var comp = new EditableComponent
        {
            InstanceName     = name,
            Symbol           = kind,
            X                = x,
            Y                = y,
            Rotation         = mirrored ? Opposite(rotation) : rotation,
            MirrorX          = mirrored,
            ShowTypeLabel    = info.DefaultShowTypeLabel,
            ShowInstanceName = info.DefaultShowInstanceName,
        };
        comp.Parameters.AddRange(parameters);
        return comp;
    }

    /// <summary>R(−θ): the rotation a horizontal reflection turns this one into.</summary>
    private static SymbolRotation Opposite(SymbolRotation r) => r switch
    {
        SymbolRotation.R90  => SymbolRotation.R270,
        SymbolRotation.R270 => SymbolRotation.R90,
        _                   => r,          // R0 and R180 are their own opposites
    };
}
