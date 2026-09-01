using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Ui.Schematic;

/// <summary>One element of a <c>.subckt</c> and what circuitRF makes of it.</summary>
/// <param name="InstanceName">The element's own name, as written — <c>R1</c>, <c>M3</c>, <c>X2</c>.</param>
/// <param name="Reference">What the line named: a value type, a model card, or another subcircuit.</param>
/// <param name="Nets">The nets the line binds, in the element's own terminal order.</param>
/// <param name="Symbol">The palette component to place, or null when this calls a subcircuit.</param>
/// <param name="SubcircuitName">The subcircuit it calls, or null when it places a component.</param>
/// <param name="Parameters">Everything to write onto the placed instance, already in base SI units.</param>
/// <param name="Unmapped">Card parameters circuitRF has no home for. Reported, never dropped in silence.</param>
/// <param name="Notes">Decisions worth showing — which law a card was read as, and so on.</param>
/// <param name="Refusal">Why this element cannot be built. Null when it can.</param>
public sealed record SubcircuitElement(
    string                           InstanceName,
    string                           Reference,
    IReadOnlyList<string>            Nets,
    SymbolKind?                      Symbol,
    string?                          SubcircuitName,
    IReadOnlyList<EditableParameter> Parameters,
    IReadOnlyList<string>            Unmapped,
    IReadOnlyList<string>            Notes,
    string?                          Refusal);

/// <summary>A <c>.subckt</c> definition and what circuitRF makes of it.</summary>
/// <param name="Definition">The cell the reader built, ports and instances as the file wrote them.</param>
/// <param name="Elements">One entry per instance, in file order.</param>
/// <param name="Dependencies">
/// The subcircuits this one calls, transitively, leaf-first. <b>Every one becomes a cell of its
/// own</b>, because a circuitRF cell instance references a cell folder — there is nowhere else for
/// a nested definition to live.
/// </param>
/// <param name="Refusal">Why the whole definition cannot be built. Null when it can.</param>
public sealed record SubcircuitTranslation(
    Cell                             Definition,
    IReadOnlyList<SubcircuitElement> Elements,
    IReadOnlyList<string>            Dependencies,
    string?                          Refusal)
{
    public string Name => Definition.Name;

    /// <summary>True when a cell can actually be built from it.</summary>
    public bool IsSupported => Refusal is null;
}

/// <summary>
/// Turns a <c>.subckt</c> definition into the circuitRF components that implement it.
///
/// <para><b>This lives beside the schematic rather than in <c>src/Core</c>, and the reason is not
/// convenience.</b> Its whole question is "which PALETTE COMPONENT does this element become, and
/// does the element's net count match that component's pins?" — and both halves are
/// <see cref="ComponentTypeRegistry"/>'s and <see cref="SymbolPortDefs"/>' knowledge, which is UI
/// knowledge by construction. <see cref="SpiceModelCardTranslation"/> answers the part that IS
/// core — card to engine reference — and is used verbatim here rather than restated, so a card
/// imported on its own and the same card reached through a subcircuit cannot disagree.</para>
///
/// <para><b>One refused element refuses the whole subcircuit.</b> A netlist with a line missing is
/// not a smaller circuit, it is a DIFFERENT one — and it is a different one that elaborates,
/// simulates and produces numbers. That is the same rule the reader applies with
/// <c>IncompleteCells</c>, applied one level up.</para>
/// </summary>
public static class SubcircuitTranslator
{
    /// <summary>
    /// Translates every <c>.subckt</c> the file defined.
    ///
    /// <para>Nested calls are resolved after every definition has been translated on its own, for
    /// the same reason <see cref="SpicePassiveModelBinding"/> binds cards in a second pass: a
    /// definition may be written after the one that calls it, and a single-pass answer would depend
    /// on file order.</para>
    /// </summary>
    public static IReadOnlyList<SubcircuitTranslation> TranslateAll(SpiceNetlistResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var cards = result.ModelCards.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in result.Library.Cells) byName.TryAdd(c.Name, c);

        var first = new List<SubcircuitTranslation>();
        foreach (var cell in result.Library.Cells)
            first.Add(TranslateOne(cell, cards, byName, result.IncompleteCells));

        return ResolveDependencies(first);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  One definition
    // ─────────────────────────────────────────────────────────────────────────

    private static SubcircuitTranslation TranslateOne(
        Cell                                     cell,
        IReadOnlyDictionary<string, SpiceModelCard> cards,
        IReadOnlyDictionary<string, Cell>        byName,
        IReadOnlySet<string>                     incomplete)
    {
        var elements = new List<SubcircuitElement>(cell.Instances.Count);

        foreach (var inst in cell.Instances)
            elements.Add(TranslateElement(inst, cards, byName));

        string? refusal = null;

        if (incomplete.Contains(cell.Name))
            refusal =
                $"'{cell.Name}' holds a line circuitRF could not read, so what is left is a "
                + "different circuit rather than a smaller one. See the reader's notes for the "
                + "file and line.";
        else if (cell.Instances.Count == 0)
            refusal = $"'{cell.Name}' defines no elements — there is no circuit to build.";
        else if (cell.Ports.Count == 0)
            refusal =
                $"'{cell.Name}' declares no ports, so the cell it would become could not be placed "
                + "in a schematic at all.";
        else if (elements.FirstOrDefault(e => e.Refusal is not null) is { } bad)
            refusal = $"'{cell.Name}': {bad.Refusal}";

        return new SubcircuitTranslation(cell, elements, [], refusal);
    }

    private static SubcircuitElement TranslateElement(
        Instance                                 inst,
        IReadOnlyDictionary<string, SpiceModelCard> cards,
        IReadOnlyDictionary<string, Cell>        byName)
    {
        SubcircuitElement Refuse(string why) => new(
            inst.InstanceName, inst.Reference, inst.NetBindings,
            null, null, [], [], [], why);

        // A subcircuit call. Whether the definition it names can itself be built is settled in the
        // dependency pass, because the answer may not exist yet.
        if (byName.TryGetValue(inst.Reference, out var target))
        {
            if (inst.NetBindings.Count != target.Ports.Count)
                return Refuse(
                    $"'{inst.InstanceName}' binds {inst.NetBindings.Count} net(s) to subcircuit "
                    + $"'{target.Name}', which declares {target.Ports.Count} port(s). The counts have "
                    + "to agree — binding pin k to port k is the whole of what a call means.");

            return new SubcircuitElement(
                inst.InstanceName, inst.Reference, inst.NetBindings,
                null, target.Name, [.. InstanceParameters(inst, null)], [], [], null);
        }

        // A native passive: the reader already resolved the value, whether it was written
        // positionally or as R=/C=/L=, and whether it came from a passive model card.
        if (PassiveSymbol(inst.Reference) is { } passive)
        {
            var parameters = InstanceParameters(inst, passive).ToList();
            if (parameters.All(p => !p.Name.Equals(ValueParameter(passive), StringComparison.Ordinal)))
                return Refuse(
                    $"'{inst.InstanceName}' is a {inst.Reference} with no value; circuitRF will not "
                    + "place one at a default, because zero simulates.");
            return new SubcircuitElement(
                inst.InstanceName, inst.Reference, inst.NetBindings,
                passive, null, parameters, [], [], null);
        }

        if (inst.Reference.Equals("SemiC", StringComparison.OrdinalIgnoreCase))
            return Refuse(
                $"'{inst.InstanceName}' is a capacitor whose value comes from a process and a "
                + "geometry (circuitRF's SemiC). The engine has that model, but there is no "
                + "schematic component for it, so it cannot be drawn into a cell.");

        // Everything else names a model card.
        if (!cards.TryGetValue(inst.Reference, out var card))
            return Refuse(
                $"'{inst.InstanceName}' names the model '{inst.Reference}', which this file does "
                + "not define and does not include.");

        var translation = SpiceModelCardTranslation.Translate(card);
        if (translation.Binding is not { } binding)
            return Refuse($"'{inst.InstanceName}' names model '{card.Name}': {translation.Refusal}");

        if (ModelCardCellBuilder.SymbolFor(binding.EngineReference) is not { } kind)
            return Refuse(
                $"'{inst.InstanceName}' names model '{card.Name}', which circuitRF implements as "
                + $"'{binding.EngineReference}' — a component with no schematic symbol, so it "
                + "cannot be drawn into a cell.");

        int pins = SymbolPortDefs.For(kind).Length;
        if (inst.NetBindings.Count != pins)
            return Refuse(
                $"'{inst.InstanceName}' binds {inst.NetBindings.Count} net(s) to model "
                + $"'{card.Name}', which circuitRF's {ComponentTypeRegistry.Get(kind).DisplayName} "
                + $"has {pins} terminal(s) for. Nothing is dropped or invented to make the counts "
                + "agree — a terminal quietly tied elsewhere is a different circuit.");

        // A MESFET card's lead resistances are placed as real resistors when a CARD is imported on
        // its own (ModelCardCellBuilder), because a cell IS a schematic and that is where they
        // physically are. Inside a netlist the same move would insert two components and two nets
        // the file never wrote, changing the topology the user is importing — so it is refused
        // instead. Reachable only if a dialect ever spells a MESFET instance in a way the reader
        // takes; today none does.
        bool isMesfet = kind is SymbolKind.FetStatz or SymbolKind.FetCurtice
                             or SymbolKind.PFetStatz or SymbolKind.PFetCurtice;
        var lead = isMesfet ? SpiceModelCardTranslation.MesfetLeadResistance(card) : (null, null);
        if (lead.Item1 is not null || lead.Item2 is not null)
            return Refuse(
                $"'{inst.InstanceName}' names MESFET model '{card.Name}', which states a lead "
                + "resistance. circuitRF's MESFET has no parameter for it, and adding the two "
                + "resistors here would put components and nets in the netlist that the file "
                + "does not.");

        var parms = new List<EditableParameter>();
        foreach (var p in binding.Parameters)
            parms.Add(ModelCardCellBuilder.ImportedParameter(kind, p.Name, p.Expression));
        // The INSTANCE's own words come second, so a line saying `area=2` wins over a card saying
        // the same thing — which is what both mean, the card stating the default and the line
        // stating this one.
        foreach (var p in InstanceParameters(inst, kind))
        {
            parms.RemoveAll(e => e.Name.Equals(p.Name, StringComparison.Ordinal));
            parms.Add(p);
        }

        return new SubcircuitElement(
            inst.InstanceName, inst.Reference, inst.NetBindings,
            kind, null, parms, binding.Unmapped, binding.Notes, null);
    }

    /// <summary>
    /// The element line's own <c>name=value</c> words, as schematic rows.
    ///
    /// <para>Values arrive in base SI — the reader has already turned <c>1u</c> into <c>1e-6</c> —
    /// so every row gets the base unit for its dimension, exactly as an imported card's does. A row
    /// left at the registry's convenience unit would read <c>2e-12</c> as two picofarads' worth of
    /// picofarads.</para>
    /// </summary>
    private static IEnumerable<EditableParameter> InstanceParameters(Instance inst, SymbolKind? kind)
    {
        foreach (var o in inst.Overrides)
            yield return kind is { } k
                ? ModelCardCellBuilder.ImportedParameter(k, o.Name, o.Expression)
                : new EditableParameter { Name = o.Name, Expression = o.Expression };
    }

    /// <summary>The palette component a native passive reference means, or null.</summary>
    private static SymbolKind? PassiveSymbol(string reference) => reference switch
    {
        "R" => SymbolKind.Resistor,
        "C" => SymbolKind.Capacitor,
        "L" => SymbolKind.Inductor,
        _   => null,
    };

    private static string ValueParameter(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor  => "R",
        SymbolKind.Capacitor => "C",
        _                    => "L",
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  Nesting
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills in each definition's transitive dependency list, leaf-first, and refuses a definition
    /// whose child is refused or whose calls form a cycle.
    ///
    /// <para><b>A cycle is refused rather than cut.</b> Cutting one would produce a cell hierarchy
    /// that terminates and is not the file's — the reader permits nesting, so a self-referential
    /// call is a broken file, and saying so is the only honest answer.</para>
    /// </summary>
    private static IReadOnlyList<SubcircuitTranslation> ResolveDependencies(
        List<SubcircuitTranslation> translations)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < translations.Count; i++) index.TryAdd(translations[i].Name, i);

        var result = new SubcircuitTranslation[translations.Count];

        for (int i = 0; i < translations.Count; i++)
        {
            var order   = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? failure = null;

            void Visit(string name)
            {
                if (failure is not null || !visited.Add(name)) return;
                if (!index.TryGetValue(name, out int k)) return;

                stack.Add(name);
                foreach (var dep in translations[k].Elements
                             .Select(e => e.SubcircuitName)
                             .Where(n => n is not null)
                             .Select(n => n!))
                {
                    if (stack.Contains(dep))
                    {
                        failure = $"'{name}' calls '{dep}', which calls back into it. circuitRF "
                                + "cannot build a cell that contains itself.";
                        stack.Remove(name);
                        return;
                    }
                    if (!index.TryGetValue(dep, out int di))
                    {
                        failure = $"'{name}' calls '{dep}', which this file does not define.";
                        stack.Remove(name);
                        return;
                    }
                    Visit(dep);
                    if (failure is not null) { stack.Remove(name); return; }
                    if (translations[di].Refusal is { } why)
                    {
                        failure = $"'{name}' calls '{dep}', which cannot be built: {why}";
                        stack.Remove(name);
                        return;
                    }
                }
                stack.Remove(name);
                order.Add(name);
            }

            Visit(translations[i].Name);
            order.Remove(translations[i].Name);

            var t = translations[i];
            result[i] = t with
            {
                Dependencies = order,
                Refusal      = t.Refusal ?? failure,
            };
        }

        return result;
    }
}
