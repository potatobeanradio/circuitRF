using System.Globalization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Filters;

/// <summary>
/// What the Filter Designer's Place filter and Place component hand a schematic (brief FD-P §1). It
/// builds fragments only; landing them is <c>SchematicViewModel.ArmFragmentPlacement</c>, the paste
/// ghost Ctrl+V uses, so nothing here places, renames or snaps.
/// </summary>
public static class FilterPlacement
{
    /// <summary>The stub each end of a placed ladder ends in, in world units: two grid steps.</summary>
    public const double StubLength = 200.0;

    /// <summary>
    /// The ladder as the pane draws it (<see cref="MatchSchematicCopy.Build"/>, whose topology gate
    /// proves it is the ladder), WITHOUT its two terminations: a filter placed into a design
    /// connects to the parts around it, not to a testbench. Each end is a short wire stub carrying a
    /// net label, <paramref name="inName"/> on port 1 and <paramref name="outName"/> on port 2.
    /// </summary>
    /// <returns>An empty model when there is no ladder.</returns>
    public static SchematicEditModel LadderFragment(MatchLadderLayout? layout, string inName, string outName)
    {
        var model = MatchSchematicCopy.Build(layout);
        if (layout is null || model.Components.Count == 0) return model;
        model.Components.RemoveAll(c => c.Symbol is SymbolKind.TermG or SymbolKind.Term);

        double y = MatchLadderLayout.SpineY;
        AddStub(model, layout.PortLeftX, layout.PortLeftX - StubLength, y, inName);
        AddStub(model, layout.PortRightX, layout.PortRightX + StubLength, y, outName);
        return model;
    }

    private static void AddStub(SchematicEditModel model, double inner, double outer, double y, string name)
    {
        var wire = new EditableWire();
        wire.Points.Add((inner, y));
        wire.Points.Add((outer, y));
        model.Wires.Add(wire);
        var label = new EditableNetLabel { Name = name };
        label.AnchorToWire(wire, outer, y);
        model.NetLabels.Add(label);
    }

    /// <summary>
    /// The two end-net names for a ladder placed into <paramref name="sheet"/>: <c>IN</c> and
    /// <c>OUT</c>, or <c>IN2</c>/<c>OUT2</c> onward when the sheet already uses either name. Two
    /// filters placed on one sheet would otherwise be joined by name, end to end.
    /// </summary>
    public static (string In, string Out) EndNames(SchematicEditModel sheet)
    {
        var taken = new HashSet<string>(sheet.NetLabels.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
        for (int k = 1; ; k++)
        {
            string suffix = k == 1 ? "" : k.ToString(CultureInfo.InvariantCulture);
            string a = "IN" + suffix, b = "OUT" + suffix;
            if (!taken.Contains(a) && !taken.Contains(b)) return (a, b);
        }
    }

    /// <summary>
    /// One <c>System ▸ Filter</c> carrying <paramref name="parameters"/> — the registry's defaults
    /// with the Designer's specification written over them, by the same function Apply uses.
    /// </summary>
    public static SchematicEditModel ComponentFragment(IEnumerable<EditableParameter> parameters)
    {
        var info = ComponentTypeRegistry.Get(SymbolKind.Filter);
        var comp = new EditableComponent
        {
            InstanceName     = info.InstancePrefix + "1",
            Symbol           = SymbolKind.Filter,
            ShowTypeLabel    = info.DefaultShowTypeLabel,
            ShowInstanceName = info.DefaultShowInstanceName,
        };
        comp.Parameters.AddRange(parameters);
        var model = new SchematicEditModel { GridSize = 100.0 };
        model.Components.Add(comp);
        return model;
    }

    /// <summary>
    /// What Replace this Filter does to <paramref name="sheet"/> (brief FD-P §1.2, phase 2): the
    /// ladder below the block, its ends labelled with the nets the block's two pins were on, and a
    /// free-standing label on each of those pins so they keep their nets once the block is gone.
    /// </summary>
    /// <remarks>
    /// <para><b>Joined by NAME, not by wire.</b> The ladder is wider than the block, so wiring it
    /// where the block was would lay its spine over the wires that ran into the block, and a series
    /// element lying on a wire is shorted by it. Labels join the same nets with nothing drawn across
    /// the parts around the block; the ladder arrives selected, so it can be moved.</para>
    /// <para>A pin whose net already carries a label keeps that name rather than gaining a second
    /// one; otherwise it takes a fresh <c>IN</c>/<c>OUT</c> pair (<see cref="EndNames"/>).</para>
    /// </remarks>
    public static (SchematicEditModel Ladder, List<EditableNetLabel> PinLabels) ReplacementFragment(
        SchematicEditModel sheet, EditableComponent filter, MatchLadderLayout layout)
    {
        var defs = sheet.PortDefsOf(filter).OrderBy(d => d.PortIndex).ToList();
        var pins = defs.Select(d => sheet.PortWorldOf(filter, d)).ToList();
        var existing = ExistingNetNames(sheet, filter);
        var (freshIn, freshOut) = EndNames(sheet);
        string inName = existing.In ?? freshIn;
        string outName = existing.Out ?? freshOut;

        var ladder = LadderFragment(layout, inName, outName);
        // Below the block, centred on it, on the connection grid.
        double grid = sheet.GridSize > 0 ? sheet.GridSize : 100.0;
        double cx = (layout.PortLeftX + layout.PortRightX) / 2;
        double dx = Math.Round((filter.X - cx) / grid) * grid;
        double dy = Math.Round((filter.Y + 800.0 - MatchLadderLayout.SpineY) / grid) * grid;
        foreach (var c in ladder.Components) { c.X += dx; c.Y += dy; }
        foreach (var w in ladder.Wires)
            for (int i = 0; i < w.Points.Count; i++)
                w.Points[i] = (w.Points[i].X + dx, w.Points[i].Y + dy);
        foreach (var n in ladder.NetLabels)
        {
            n.X += dx;
            n.Y += dy;
        }

        var pinLabels = new List<EditableNetLabel>();
        if (pins.Count >= 2)
        {
            if (existing.In is null) pinLabels.Add(new EditableNetLabel { Name = inName, X = pins[0].X, Y = pins[0].Y });
            if (existing.Out is null) pinLabels.Add(new EditableNetLabel { Name = outName, X = pins[1].X, Y = pins[1].Y });
        }
        return (ladder, pinLabels);
    }

    /// <summary>
    /// The label names already on the nets of <paramref name="filter"/>'s two pins, each null when
    /// that net carries no label (or the sheet does not extract).
    /// </summary>
    private static (string? In, string? Out) ExistingNetNames(SchematicEditModel sheet, EditableComponent filter)
    {
        try
        {
            var tb = NetExtractor.Extract(sheet).TestBench;
            var inst = tb.Instances.FirstOrDefault(i => i.InstanceName == filter.InstanceName);
            if (inst is null || inst.NetBindings.Count < 2) return (null, null);
            var labelled = new HashSet<string>(sheet.NetLabels.Select(n => n.Name), StringComparer.Ordinal);
            string? Named(string net) => labelled.Contains(net) ? net : null;
            return (Named(inst.NetBindings[0]), Named(inst.NetBindings[1]));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
        {
            return (null, null);
        }
    }

    /// <summary>The registry's default Filter parameters, as editable ones.</summary>
    public static List<EditableParameter> DefaultParameters() =>
        ComponentTypeRegistry.DefaultParameters(SymbolKind.Filter, 0)
            .Select(d => new EditableParameter
            {
                Name = d.Name, Expression = d.Expression, Unit = d.Unit,
                Dimension = d.Dimension, ShowOnSchematic = d.ShowOnSchematic,
            })
            .ToList();
}
