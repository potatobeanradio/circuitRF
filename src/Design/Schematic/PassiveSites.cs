using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The sites of a schematic's planned passives (docs/design/rf-passives.md, "Sites"), read as a run reads them: each
/// passive's plan from its elaborated model, so a site's value is the run's with the substrate and the Footprint applied,
/// and the parts already on each site, by net. Place site parts fills the empty ones (<see cref="PlaceParts"/>), and LVS
/// checks the filled ones' footprints (<see cref="FootprintMisfits"/>).
/// </summary>
public static class PassiveSites
{
    /// <summary>One site of a placed passive: the nets its pins are on (ground, "0", for a shunt site's second) and every
    /// part across exactly those nets.</summary>
    public sealed record Site(PlanSite Plan, string NetA, string NetB, IReadOnlyList<EditableComponent> Parts);

    /// <summary>A placed passive with its plan and sites. <paramref name="Footprint"/> is its own, when its kind takes
    /// one (<see cref="MicrostripSubstrateInjection.HasSiteFootprint"/>) and it states one.</summary>
    public sealed record Passive(EditableComponent Component, PassivePlan Plan, string? Footprint, IReadOnlyList<Site> Sites);

    /// <summary>What Place site parts adds for one passive: a part on each empty site, the wires joining those not placed
    /// straight on their pins, and a sentence for each site it left.</summary>
    public sealed record Placement(IReadOnlyList<EditableComponent> Parts, IReadOnlyList<EditableWire> Wires,
        IReadOnlyList<string> Notes);

    private const string Ground = "0";

    /// <summary>A lumped part's pins, as every two-terminal lumped symbol has them: 400 apart, centred on its origin.</summary>
    private const double PartSpan = 400.0;

    /// <summary>Every planned passive in <paramref name="model"/> that has sites, with its sites; empty, with
    /// <paramref name="refusal"/> saying why, when the schematic does not elaborate.</summary>
    public static IReadOnlyList<Passive> Read(SchematicEditModel model, out string? refusal)
    {
        refusal = null;
        try
        {
            var extracted = SchematicCircuit.Extract(model, "tb");
            string? baseDir = SchematicCircuit.ReferenceBaseOfDirectory(model.SchematicDirectory, model.HostWorkspaceRoot);
            var (lib, tb) = SchematicCircuit.RoundTrip(SchematicCircuit.CnlTextOf(extracted, "tb"), "tb", baseDir);
            using var elaborated = new Elaborator(lib) { BaseDirectory = baseDir }.Elaborate(tb);
            return Of(tb, PlansOf(elaborated), model.Components);
        }
        catch (Exception ex)
        {
            refusal = ex.Message;
            return [];
        }
    }

    /// <summary>The plan of every planned passive <paramref name="elaborated"/> holds, by instance path.</summary>
    public static Dictionary<string, PassivePlan> PlansOf(ElaboratedNetlist elaborated)
    {
        var plans = new Dictionary<string, PassivePlan>(StringComparer.Ordinal);
        foreach (var ec in elaborated.Components)
            if (ec.Model is MicrostripPlanModel m) plans.TryAdd(ec.InstancePath, m.Plan);
        return plans;
    }

    /// <summary>The passives of <paramref name="tb"/>'s own instances that <paramref name="plans"/> has a plan for, with
    /// the parts of <paramref name="components"/> on each site.</summary>
    public static IReadOnlyList<Passive> Of(TestBench tb, IReadOnlyDictionary<string, PassivePlan> plans,
        IEnumerable<EditableComponent> components)
    {
        var byName = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var c in components) if (c.InstanceName.Length > 0) byName.TryAdd(c.InstanceName, c);

        var passives = new List<Passive>();
        foreach (var inst in tb.Instances)
        {
            if (!plans.TryGetValue(inst.InstanceName, out var plan) || plan.Sites.Count == 0
                || !byName.TryGetValue(inst.InstanceName, out var comp)) continue;
            var pins = plan.Pins.ToList();
            string Net(string pin) => inst.NetBindings[pins.IndexOf(pin)];
            var sites = plan.Sites.Select(site =>
            {
                string a = Net(site.PinA), b = site.PinB is { } pb ? Net(pb) : Ground;
                var parts = tb.Instances.Where(o => !ReferenceEquals(o, inst) && Across(TerminalNets.Of(o), a, b, site.Kind))
                                        .Select(o => byName.GetValueOrDefault(o.InstanceName))
                                        .OfType<EditableComponent>().ToList();
                return new Site(site, a, b, parts);
            }).ToList();
            passives.Add(new Passive(comp, plan, FootprintOf(comp), sites));
        }
        return passives;
    }

    /// <summary>A part across a site: two terminals on its two nets, either way round, or for a shunt site one terminal
    /// on its pin (a via, whose ground is its own).</summary>
    private static bool Across(IReadOnlyList<string> nets, string a, string b, PlanSiteKind kind)
        => nets.Count == 2 ? (nets[0] == a && nets[1] == b) || (nets[0] == b && nets[1] == a)
         : nets.Count == 1 && kind == PlanSiteKind.Shunt && nets[0] == a;

    /// <summary>A passive's own <c>Footprint</c>, as stored: the row of a kind whose site span it sets.</summary>
    public static string? FootprintOf(EditableComponent comp)
        => MicrostripSubstrateInjection.HasSiteFootprint(comp.Symbol)
           && comp.Parameters.FirstOrDefault(p => ArtworkParameters.IsArtworkOnly(p.Name))?.Expression is { } e
           && !string.IsNullOrWhiteSpace(e) ? e.Trim() : null;

    /// <summary>Whether two stored footprints name the same land: a built-in by its canonical spelling (<c>smt:0402</c>
    /// is <c>smt:0402@N</c>), a path by its text.</summary>
    public static bool SameFootprint(string? a, string? b)
    {
        if (a is null || b is null) return a == b;
        if (FootprintRef.TryParse(a, out var ra, out _) && FootprintRef.TryParse(b, out var rb, out _))
            return ra!.ToString() == rb!.ToString();
        return string.Equals(a.Trim().Replace('\\', '/'), b.Trim().Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Each part on a site whose span its passive's <c>Footprint</c> sets (every series site of MWPD and MCBL)
    /// with another footprint, or none: a part whose pads will not land on the pins the layout draws.</summary>
    public static IEnumerable<(Passive Passive, Site Site, EditableComponent Part)> FootprintMisfits(IEnumerable<Passive> passives)
        => from p in passives
           where p.Footprint is not null
           from s in p.Sites
           where s.Plan.Kind == PlanSiteKind.Series
           from part in s.Parts
           where !SameFootprint(p.Footprint, part.Footprint)
           select (p, s, part);

    /// <summary>
    /// Place site parts for <paramref name="passive"/>: an ordinary part on each empty site, of the site's value, with the
    /// passive's <c>Footprint</c> on a series site (or the R/C default on <paramref name="technology"/>), a via on a via
    /// site. Pure: the caller adds them, as one undoable step.
    ///
    /// <para><b>Where they go.</b> A series part whose two pins are a lumped part's span apart in line, with no other
    /// pin of the passive between them, is laid straight on them, as a resistor dropped on MWPD's R2-R3 is. Otherwise, pins
    /// farther apart or closer together (a ladder's G pins, 200 apart), it stands beside the pins, outward from the passive
    /// and one grid step further for each part already there, with a wire to each pin. A shunt part stands on its pin pointing away from the passive, a ground under its far end. A
    /// jumper (a bond wire or an air bridge) and a load are left, each with a note: neither is an R, L or C, and a load is
    /// the circuit's, not the part's.</para>
    /// </summary>
    /// <param name="taken">Every instance name already in use, when that is more than <paramref name="model"/>'s own (the
    /// designators a cell's layout holds too); the model's when null.</param>
    public static Placement PlaceParts(SchematicEditModel model, Passive passive, Technology? technology,
        IEnumerable<string>? taken = null)
    {
        var comp = passive.Component;
        var defs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
        var pinPoints = defs.Select((_, i) => comp.GetPortWorldCoord(i)).ToList();
        (double X, double Y) At(string pin) => pinPoints[Array.FindIndex(defs, d => d.Name == pin)];

        var parts = new List<EditableComponent>();
        var wires = new List<EditableWire>();
        var notes = new List<string>();
        var beside = new Dictionary<(bool, double, int), int>();     // parts already standing beside each line of pins
        var names = (taken ?? model.Components.Select(c => c.InstanceName)).ToList();

        foreach (var site in passive.Sites.Where(s => s.Parts.Count == 0))
        {
            var p = site.Plan;
            var kind = p.Role switch
            {
                PlanSiteRole.R => SymbolKind.Resistor,
                PlanSiteRole.L => SymbolKind.Inductor,
                PlanSiteRole.C => SymbolKind.Capacitor,
                PlanSiteRole.Via => SymbolKind.ViaGnd,
                _ => (SymbolKind?)null,
            };
            if (kind is not { } k)
            {
                notes.Add(p.Role == PlanSiteRole.Jumper
                    ? $"{comp.InstanceName} {p.Name}: a jumper is a bond wire or an air bridge, which is placed by hand."
                    : $"{comp.InstanceName} {p.Name}: the load across it is the circuit's, so it is left to you.");
                continue;
            }

            var a = At(p.PinA);
            if (p.Kind == PlanSiteKind.Shunt)
            {
                // Away from the passive along the pin's longer offset: the part's body (local +y) turned that way.
                double dx = a.X - comp.X, dy = a.Y - comp.Y;
                var rotation = Math.Abs(dx) >= Math.Abs(dy)
                    ? (dx < 0 ? SymbolRotation.R90 : SymbolRotation.R270)
                    : (dy < 0 ? SymbolRotation.R180 : SymbolRotation.R0);
                var (ox, oy) = SchematicGeometry.LocalToWorld(0f, -200f, 0, 0, rotation, false);
                var part = New(k, a.X - ox, a.Y - oy, rotation, p.Value, null);
                if (k != SymbolKind.ViaGnd)
                {
                    var (gx, gy) = part.GetPortWorldCoord(1);
                    New(SymbolKind.Ground, gx, gy, SymbolRotation.R0, null, null);
                }
                continue;
            }

            var b = At(p.PinB!);
            bool vertical = a.X == b.X;
            if (!vertical && a.Y != b.Y)
            {
                notes.Add($"{comp.InstanceName} {p.Name}: its pins are not in line, so its part is placed by hand.");
                continue;
            }
            // Along the pins' line (u) and across it (v).
            double ua = vertical ? a.Y : a.X, ub = vertical ? b.Y : b.X, v = vertical ? a.X : a.Y;
            double lo = Math.Min(ua, ub), hi = Math.Max(ua, ub), mid = (lo + hi) / 2.0;
            bool between = pinPoints.Any(q => (vertical ? q.X : q.Y) == v && (vertical ? q.Y : q.X) is var u && u > lo && u < hi);
            double offset = 0.0;
            if (hi - lo != PartSpan || between)
            {
                int outward = (vertical ? a.X - comp.X : a.Y - comp.Y) < 0 ? -1 : 1;
                var line = (vertical, v, outward);
                int n = beside[line] = beside.GetValueOrDefault(line) + 1;
                offset = outward * 200.0 * n;
            }
            (double X, double Y) Pt(double u, double across) => vertical ? (across, u) : (u, across);
            var (cx, cy) = Pt(mid, v + offset);
            New(k, cx, cy, vertical ? SymbolRotation.R0 : SymbolRotation.R90, p.Value, p.SpanMeters);
            if (offset == 0.0 && hi - lo == PartSpan) continue;
            // A wire from each pin across to the part's line, then along it to the part's own pin.
            foreach (var (end, pin) in new[] { (lo, mid - PartSpan / 2.0), (hi, mid + PartSpan / 2.0) })
            {
                var wire = new EditableWire();
                foreach (var point in new[] { Pt(end, v), Pt(end, v + offset), Pt(pin, v + offset) })
                    if (wire.Points.Count == 0 || wire.Points[^1] != point) wire.Points.Add(point);
                if (wire.Points.Count > 1) wires.Add(wire);
            }
        }
        return new Placement(parts, wires, notes);

        // A part as a palette drop makes it, with the site's value and, for an R, L or C on a board, the passive's footprint,
        // else a built-in land that spans the site (an 0402 for MWPD's default Gap), else the technology's default.
        EditableComponent New(SymbolKind kind, double x, double y, SymbolRotation rotation, double? value, double? span)
        {
            var info = ComponentTypeRegistry.Get(kind);
            var part = new EditableComponent
            {
                InstanceName = SchematicEditModel.NextAvailableName(names, ComponentTypeRegistry.InstancePrefix(kind)),
                Symbol = kind, X = x, Y = y, Rotation = rotation,
                ShowTypeLabel = info.DefaultShowTypeLabel, ShowInstanceName = info.DefaultShowInstanceName,
            };
            foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, SymbolPortDefs.For(kind).Length))
                part.Parameters.Add(new EditableParameter
                    { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
            if (value is { } siteValue) SetValue(part, kind, siteValue);
            if (FootprintDefaults.IsDiscreteRlc(kind)
                && (passive.Footprint ?? (FootprintDefaults.For(kind, technology) is { } fallback ? Spanning(span) ?? fallback : null))
                   is { } footprint)
                part.Parameters.Add(new EditableParameter
                    { Name = ArtworkParameters.FootprintName, Expression = footprint, ShowOnSchematic = false });
            names.Add(part.InstanceName);
            parts.Add(part);
            return part;
        }
    }

    /// <summary>The first built-in land, nominal density first, whose span is <paramref name="span"/> to the nanometre.</summary>
    private static string? Spanning(double? span)
        => span is not { } s ? null
         : (from density in new[] { DensityLevel.Nominal, DensityLevel.Most, DensityLevel.Least }
            from c in SmtCaseTable.All
            let reference = FootprintRef.For(c, density).ToString()
            where MicrostripSubstrateInjection.SiteSpanMeters(reference, null, null, out _) is { } d && Math.Abs(d - s) < 1e-9
            select reference).FirstOrDefault();

    /// <summary>The site's value in the part's own value row (R, L or C), in that row's unit.</summary>
    private static void SetValue(EditableComponent part, SymbolKind kind, double value)
    {
        string name = kind switch { SymbolKind.Inductor => "L", SymbolKind.Capacitor => "C", _ => "R" };
        if (part.Parameters.FirstOrDefault(q => q.Name == name) is not { } row) return;
        double scale = Units.Scale(UnitNormalizer.ToEngineUnit(row.Unit)) ?? 1.0;
        row.Expression = (value / scale).ToString("G6", CultureInfo.InvariantCulture);
    }
}
