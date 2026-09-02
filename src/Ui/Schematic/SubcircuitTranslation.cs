using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
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
        {
            CarryGlobals(cell, result.Variables);
            var t = Inlined(TranslateOne(cell, cards, byName, result.IncompleteCells),
                            result.Functions);
            first.Add(SpiceChargePairCollapse.Collapse(RefuseTransientTime(t, result.Variables)));
        }

        return ResolveDependencies(first);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  What a definition needs from the rest of its FILE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives a cell the file's own global <c>.param</c>s, which its elements reference by bare name.
    ///
    /// <para><b>They are carried as the CELL's variables rather than pushed into the design's
    /// globals, and that is the difference between working and colliding.</b> A design has one
    /// global namespace; two library files that each declare <c>Rd</c> would meet in it, silently,
    /// first one winning. A cell's own variables are scoped to the cell, so two imported parts each
    /// keep their own — which is exactly what one-cell-per-subcircuit already buys for a
    /// <c>.param</c> written INSIDE a definition, extended to the ones written outside it.</para>
    ///
    /// <para>A name the definition already declares is left alone. A cell parameter is the call
    /// site's to override, and a variable of the same name would bind over it and seal it
    /// shut.</para>
    /// </summary>
    private static void CarryGlobals(Cell cell, IReadOnlyList<Variable> globals)
    {
        if (globals.Count == 0) return;

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in cell.Parameters) taken.Add(p.Name);
        foreach (var v in cell.Variables)  taken.Add(v.Name);

        foreach (var g in globals)
        {
            // A global stated in terms of the transient time variable has NO steady-state value, so
            // carrying it would put an unevaluable variable on every cell in the file — including
            // every cell that never mentions it. It is dropped here and refused, by name, at
            // whichever element actually reads it (RefuseTransientTime).
            if (SpiceExpression.ReferencesTime(g.Expression)) continue;
            if (taken.Add(g.Name)) cell.Variables.Add(g);
        }
    }

    /// <summary>
    /// Refuses, by name, a definition whose elements depend on the transient time variable.
    ///
    /// <para><b>The reader refuses a <c>time</c> written on an element line; this is the same
    /// refusal one hop further out.</b> A file writes <c>.param tr = time*2</c> and then
    /// <c>R1 a b {tr}</c>: the element's own text names no time at all, so nothing on the line can
    /// see the problem, and left alone it becomes a cell carrying a variable that fails to evaluate
    /// at simulate time with an unbound name and no mention of the file it came from.</para>
    ///
    /// <para>The taint is followed to a FIXED POINT through the definition's own parameters and
    /// variables, because <c>.param a = time</c>, <c>.param b = a*2</c> is the same statement
    /// written twice. Only an element that actually reads a tainted name is refused: a default no
    /// call site uses is a default, and refusing on it would refuse definitions that work.</para>
    /// </summary>
    private static SubcircuitTranslation RefuseTransientTime(
        SubcircuitTranslation t, IReadOnlyList<Variable> globals)
    {
        var tainted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in globals)
            if (SpiceExpression.ReferencesTime(g.Expression)) tainted.Add(g.Name);

        var candidates = new List<(string Name, string Expression)>();
        foreach (var p in t.Definition.Parameters) candidates.Add((p.Name, p.DefaultExpression));
        foreach (var v in t.Definition.Variables)  candidates.Add((v.Name, v.Expression));

        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var (name, expr) in candidates)
                if (!tainted.Contains(name) && Reads(expr, tainted) is not null)
                    grew = tainted.Add(name);
        }

        string? refusal = null;
        var elements = new List<SubcircuitElement>(t.Elements.Count);
        foreach (var e in t.Elements)
        {
            string? via = null;
            if (e.Refusal is null)
                foreach (var q in e.Parameters)
                    if ((via = Reads(q.Expression, tainted)) is not null) break;

            if (via is null) { elements.Add(e); continue; }

            string why =
                $"'{e.InstanceName}' depends on the transient time variable"
                + (via.Equals(SpiceExpression.TimeIdentifier, StringComparison.OrdinalIgnoreCase)
                    ? ". " : $", through '{via}'. ")
                + "circuitRF has no transient analysis, and outside a condition there is no "
                + "steady-state value to read it as.";
            refusal ??= why;
            elements.Add(e with { Refusal = why });
        }

        return refusal is null
            ? t
            : t with { Elements = elements, Refusal = t.Refusal ?? $"'{t.Name}': {refusal}" };

        // The first name an expression reads that has no steady-state value — `time` itself, or a
        // name that resolves to it. Null when the expression is clean.
        static string? Reads(string expression, IReadOnlySet<string> tainted)
        {
            if (SpiceExpression.ReferencesTime(expression)) return SpiceExpression.TimeIdentifier;
            if (tainted.Count == 0) return null;

            Expr ast;
            try { ast = Parser.Parse(expression); }
            catch { return null; }

            foreach (string r in AstWalker.CollectRefs(ast))
                if (tainted.Contains(r)) return r;
            return null;
        }
    }

    /// <summary>
    /// Substitutes every <c>.func</c> the file declared at its call sites, in everything the
    /// translation emits.
    ///
    /// <para><b>Here, and not later, because a written cell has to be self-contained.</b> An
    /// imported subcircuit becomes a cell FOLDER on disk; there is nowhere in it for a function
    /// definition to live (<c>UserFunction</c> exists only on a TestBench, one flat namespace for a
    /// whole design). A cell whose equations still called <c>ni(T)</c> would resolve only in a design
    /// that happened to declare <c>ni</c>, and would collide with any other file that declared one.
    /// A body substituted at its call site never enters a namespace at all.</para>
    ///
    /// <para>An expression the substitution does not touch keeps its exact text — the AST is only
    /// printed back out when something actually changed, so nothing is re-spelled for the sake of
    /// passing through.</para>
    /// </summary>
    private static SubcircuitTranslation Inlined(
        SubcircuitTranslation t, IReadOnlyList<UserFunction> functions)
    {
        // A parameter DECLARATION is immutable, so a default that calls a function is replaced in
        // place in the list rather than edited.
        for (int k = 0; k < t.Definition.Parameters.Count; k++)
        {
            var d = t.Definition.Parameters[k];
            if (Inline(d.DefaultExpression, functions) is { } changed && changed != d.DefaultExpression)
                t.Definition.Parameters[k] = new ParameterDeclaration(d.Name, changed, d.Unit, d.Hidden);
        }

        for (int k = 0; k < t.Definition.Variables.Count; k++)
        {
            var v = t.Definition.Variables[k];
            if (Inline(v.Expression, functions) is { } changed && changed != v.Expression)
                t.Definition.Variables[k] = new Variable(v.Name, changed, v.Unit);
        }

        var elements = new List<SubcircuitElement>(t.Elements.Count);
        string? refusal = null;

        foreach (var e in t.Elements)
        {
            if (e.Refusal is not null || e.Parameters.Count == 0) { elements.Add(e); continue; }

            var replaced = new List<EditableParameter>(e.Parameters.Count);
            foreach (var p in e.Parameters)
            {
                string? changed;
                try { changed = Inline(p.Expression, functions); }
                catch (UserFunctionInlineException ex)
                {
                    refusal ??= $"'{e.InstanceName}': {ex.Message}";
                    replaced.Add(p);
                    continue;
                }
                string text = changed ?? p.Expression;

                // WITH THE FILE'S OWN FUNCTIONS SUBSTITUTED, ANY CALL LEFT IS ONE CIRCUITRF DOES NOT
                // HAVE — and this is the last moment it can be said usefully. Left alone it parses,
                // elaborates, and throws "unknown function" from inside the solver at simulate time,
                // in a message that names neither the file nor the line nor the element.
                if (UnknownCall(text, e.Symbol == SymbolKind.Sdd) is { } unknown)
                    refusal ??= unknown.Equals("TABLE", StringComparison.OrdinalIgnoreCase)
                        ? $"'{e.InstanceName}' states part of its transfer as a piecewise-linear "
                        + "table. circuitRF has no table-driven source: a table is a chain of "
                        + "breakpoints, and a breakpoint is a discontinuity in the derivative that "
                        + "harmonic balance cannot resolve."
                        : $"'{e.InstanceName}' calls '{unknown}', which is not a function "
                        + "circuitRF has and which this file does not define.";

                replaced.Add(changed is null || changed == p.Expression
                    ? p
                    : new EditableParameter { Name = p.Name, Expression = changed, Unit = p.Unit });
            }
            elements.Add(e with { Parameters = replaced });
        }

        return t with
        {
            Elements = elements,
            Refusal  = t.Refusal ?? (refusal is null ? null : $"'{t.Name}': {refusal}"),
        };
    }

    /// <summary>
    /// One expression with its function calls substituted, or null when nothing changed.
    ///
    /// <para>A value that is not an expression at all — a file path, an enum name, an instance name
    /// on a control reference — is left exactly as written rather than refused: this is a
    /// substitution pass, and a value with no function call in it has nothing to substitute.</para>
    /// </summary>
    /// <summary>
    /// The name of the first call in an expression that whatever will EVALUATE it cannot, or null.
    ///
    /// <para><b>Which evaluator that is has to be asked, because the two do not implement the same
    /// set.</b> A device equation is evaluated with a derivative alongside every value, so the
    /// rounding family (<c>floor</c>, <c>ceil</c>, <c>round</c>, <c>int</c>) is absent there and
    /// present in an ordinary parameter expression. Reading them from one list let <c>INT(V(a,b))</c>
    /// through import and threw it from inside the solver — which is precisely the failure this
    /// check exists to move forward to the file.</para>
    /// </summary>
    private static string? UnknownCall(string expression, bool deviceEquation)
    {
        if (!expression.Contains('(')) return null;

        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch { return null; }        // not an expression at all; someone else's problem to report

        var known = deviceEquation
            ? SpiceExpression.DeviceEquationFunctions
            : SpiceExpression.KnownFunctions;
        return Walk(ast, known);

        static string? Walk(Expr e, IReadOnlySet<string> known)
        {
            switch (e)
            {
                case CallExpr c:
                    if (!known.Contains(c.Name)) return c.Name;
                    foreach (var a in c.Args) if (Walk(a, known) is { } n) return n;
                    return null;
                case UnaryExpr u:       return Walk(u.Operand, known);
                case BinaryExpr b:      return Walk(b.Left, known)  ?? Walk(b.Right, known);
                case CompareExpr cp:    return Walk(cp.Left, known) ?? Walk(cp.Right, known);
                case LogicExpr l:       return Walk(l.Left, known)  ?? Walk(l.Right, known);
                case ConditionalExpr d: return Walk(d.Condition, known) ?? Walk(d.Then, known)
                                            ?? Walk(d.Else, known);
                default:                return null;
            }
        }
    }

    private static string? Inline(string expression, IReadOnlyList<UserFunction> functions)
    {
        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch { return null; }

        var inlined = UserFunctionInliner.Inline(ast, functions);
        return ReferenceEquals(inlined, ast) ? null : SpiceBehaviouralSource.Print(inlined);
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

        // A source line. Dispatched FIRST, and on the instance's own leading letter as well as on
        // the reference, so a file that happens to define a subcircuit called 'E' still calls it.
        if (SpiceSourceTranslation.Handles(inst.Reference) &&
            inst.InstanceName.Length > 0 &&
            char.ToUpperInvariant(inst.InstanceName[0]) is 'V' or 'I' or 'E' or 'G' or 'F' or 'H')
            return SpiceSourceTranslation.Translate(inst);

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
            // A capacitor may state its stored CHARGE instead of its capacitance, which is a
            // nonlinear capacitance written directly rather than through a behavioural source. It is
            // an equation, so it becomes the equation-defined device's charge bucket — where
            // harmonic balance already applies jkω to its harmonics.
            //
            // The trap this AVOIDS, stated once because getting it wrong is silent: a capacitance
            // C(v) is the DERIVATIVE of the charge, so Q = ∫C dv and NOT C(v)·v. A charge stated
            // directly has no such conversion to get wrong, which is why it is the spelling to
            // recommend for anything that is not a polynomial.
            if (passive == SymbolKind.Capacitor &&
                inst.Overrides.FirstOrDefault(o =>
                    o.Name.Equals(SpiceChargeSpelling.ChargeParameter, StringComparison.OrdinalIgnoreCase))
                is { } chargeOverride)
                return SpiceChargeSpelling.CapacitorCharge(inst, chargeOverride.Expression);

            // And it may state a capacitance that VARIES with its own voltage, which is the other
            // way the same physics is written. Only a polynomial has a symbolic integral here, and
            // the integral is the whole point: C(v) is dQ/dv, so the charge is ∫C dv and never
            // C(v)·v. Anything else is refused by name rather than approximated.
            if (passive == SymbolKind.Capacitor &&
                inst.Overrides.FirstOrDefault(o =>
                    o.Name.Equals(ValueParameter(SymbolKind.Capacitor), StringComparison.OrdinalIgnoreCase))
                is { } valueOverride &&
                SpiceChargeSpelling.CapacitorCapacitance(inst, valueOverride.Expression) is { } varying)
                return varying;

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
