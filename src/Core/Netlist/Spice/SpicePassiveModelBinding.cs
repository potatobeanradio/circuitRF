using System.Globalization;
using CircuitRF.Core.Design;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// Binds a <c>.model</c> card of a PASSIVE type onto the circuitRF primitive that implements it.
///
/// <para><b>Why this is a separate pass and not part of reading the element line.</b> A card may be
/// declared after the subcircuit that uses it, and an <c>.include</c> may put it in another file
/// entirely — both occur in kits. Binding at the element line would therefore work or not work
/// depending on the order two files happen to be read in, which is the worst possible property for
/// something whose failure is a component with no value.</para>
///
/// <para><b>What is bound, and what is deliberately left alone.</b> A card whose type is <c>C</c> or
/// <c>R</c> states a value in terms of a process and a geometry, which circuitRF's own
/// <c>SemiC</c> and <c>R</c> express directly. Every other card type — a diode, a transistor, a
/// compiled model — is the parameter block of a device something ELSE supplies, and is left exactly
/// as it was so whoever supplies that device still sees it. That split is what keeps this pass from
/// having any opinion about semiconductors.</para>
///
/// <para><b>Nothing is guessed.</b> A card this pass recognises but cannot complete — a capacitor
/// card with an area coefficient and no geometry to apply it to — is REPORTED and left unbound,
/// because the alternative is a capacitance of zero that simulates perfectly.</para>
/// </summary>
internal static class SpicePassiveModelBinding
{
    /// <summary>
    /// Rewrites every instance that names a passive model card. <paramref name="note"/> receives a
    /// message; <paramref name="markIncomplete"/> receives the name of a cell whose definition could
    /// not be completed.
    /// </summary>
    public static void Bind(
        Library                       library,
        IReadOnlyList<SpiceModelCard> cards,
        Action<string>                note,
        Action<string>                markIncomplete)
    {
        if (cards.Count == 0) return;

        foreach (var cell in library.Cells)
            for (int i = 0; i < cell.Instances.Count; i++)
            {
                var inst = cell.Instances[i];

                var card = cards.FirstOrDefault(
                    c => c.Name.Equals(inst.Reference, StringComparison.OrdinalIgnoreCase));
                if (card is null) continue;

                // The card's TYPE is what decides, never the element letter — the letter says how
                // the line is laid out, the type says what the card describes.
                string type = card.ModelType.Trim();
                if (!type.Equals("C", StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("R", StringComparison.OrdinalIgnoreCase))
                    continue;

                char letter = char.ToUpperInvariant(inst.InstanceName[0]);
                if (letter != char.ToUpperInvariant(type[0]))
                {
                    note($"'{inst.InstanceName}' in '{cell.Name}' names model '{card.Name}', which is " +
                         $"a '{type}' card, but the element is a '{letter}'. Left unbound rather than " +
                         "read as a device of a kind the card does not describe.");
                    markIncomplete(cell.Name);
                    continue;
                }

                var rewritten = type.Equals("C", StringComparison.OrdinalIgnoreCase)
                    ? BindCapacitor(cell.Name, inst, card, note, markIncomplete)
                    : BindResistor (cell.Name, inst, card, note, markIncomplete);

                if (rewritten is not null) cell.Instances[i] = rewritten;
            }
    }

    /// <summary>
    /// Rewrites every subcircuit call's parameter names into the spelling its DEFINITION declared.
    ///
    /// <para><b>This dialect is case-insensitive and circuitRF compares parameter names ordinally.</b>
    /// A call writing <c>W=10u</c> against a definition declaring <c>w</c> is the same parameter to
    /// every simulator that reads this format, and a name circuitRF has never heard of — which the
    /// elaborator rightly refuses. Aligning the call to the declaration is the only direction that
    /// cannot invent a parameter: an unmatched name is left exactly as written, so a genuine typo is
    /// still refused by name.</para>
    ///
    /// <para>A separate pass for the same reason as the card binding: a definition may be read after
    /// the call that uses it.</para>
    /// </summary>
    public static void AlignSubcircuitParameterCase(Library library)
    {
        var byName = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in library.Cells) byName.TryAdd(c.Name, c);

        foreach (var cell in library.Cells)
            for (int i = 0; i < cell.Instances.Count; i++)
            {
                var inst = cell.Instances[i];
                if (!byName.TryGetValue(inst.Reference, out var target)) continue;

                List<ParameterAssignment>? rewritten = null;
                for (int k = 0; k < inst.Overrides.Count; k++)
                {
                    var o = inst.Overrides[k];
                    var declared = target.Parameters.FirstOrDefault(
                        p => p.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase));

                    if (declared is null || declared.Name.Equals(o.Name, StringComparison.Ordinal))
                        continue;

                    rewritten ??= [.. inst.Overrides];
                    rewritten[k] = new ParameterAssignment(declared.Name, o.Expression);
                }

                if (rewritten is not null)
                    cell.Instances[i] = new Instance(
                        inst.InstanceName, inst.Reference, inst.NetBindings, rewritten);
            }
    }

    // ── the capacitor ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>C = ( Cfixed + CJ·(L−NARROW)·(W−NARROW) + 2·CJSW·((L−NARROW)+(W−NARROW)) ) · scale</c>,
    /// then the card's own temperature polynomial — which is exactly what circuitRF's <c>SemiC</c>
    /// computes, so this is a mapping of names and not a second implementation of the arithmetic.
    ///
    /// <para><b>The units are the file's own and are never converted.</b> A card's <c>CJ</c> is a
    /// capacitance per unit area in whatever unit the instance states its geometry in; a kit
    /// routinely passes microns and states <c>CJ</c> per square micron to match. Rescaling either
    /// side would break the pairing the file itself set up.</para>
    ///
    /// <para><b><c>scale</c> multiplies the capacitance, so it is folded into the coefficients</b>
    /// rather than into the geometry. The two readings coincide at the <c>scale=1</c> every real
    /// card uses, and this one is what the parameter is documented to mean.</para>
    /// </summary>
    private static Instance? BindCapacitor(
        string cellName, Instance inst, SpiceModelCard card,
        Action<string> note, Action<string> markIncomplete)
    {
        string? Card(string name) => card.Parameters.TryGetValue(name, out var v) ? v : null;
        string? Inst(string name) => inst.Overrides
            .FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Expression;

        string? cj     = Card("CJ");
        string? cjsw   = Card("CJSW");
        string? fixedC = Inst("c") ?? Card("C");
        string? narrow = Card("NARROW");
        string? scale  = Inst("scale");

        // Geometry. DEFW is the card's own default width, which is what it exists for.
        string? w = Inst("w") ?? Card("DEFW");
        string? l = Inst("l");

        bool geometric = cj is not null || cjsw is not null;
        if (geometric && (w is null || l is null))
        {
            note($"'{inst.InstanceName}' in '{cellName}' names capacitor model '{card.Name}', which " +
                 "states a capacitance per unit area or edge, but the instance gives no " +
                 $"{(w is null ? "width" : "length")}. Left unbound: applying the coefficient to no " +
                 "geometry would give a capacitance of zero, which simulates and is not the part.");
            markIncomplete(cellName);
            return null;
        }

        if (!geometric && fixedC is null)
        {
            note($"'{inst.InstanceName}' in '{cellName}' names capacitor model '{card.Name}', which " +
                 "states no capacitance and no coefficient. Left unbound.");
            markIncomplete(cellName);
            return null;
        }

        string wEff = Shrink(w, narrow);
        string lEff = Shrink(l, narrow);

        var over = new List<ParameterAssignment>();

        void Add(string name, string? expr)
        {
            if (expr is not null) over.Add(new ParameterAssignment(name, expr));
        }

        Add("C",    Scaled(fixedC, scale));
        Add("Cj",   Scaled(cj,     scale));
        Add("Cjsw", Scaled(cjsw,   scale));

        if (geometric) { Add("W", wEff); Add("L", lEff); }

        // The instance's own coefficients outrank the card's: the card is the process, the instance
        // is this device. Tnom belongs to the parameter set and is never an instance's to state.
        Add("TC1",  Inst("tc1") ?? Card("TC1"));
        Add("TC2",  Inst("tc2") ?? Card("TC2"));
        Add("Tnom", Card("TNOM"));

        CarryDeviceParameters(inst, over, note, cellName);

        return new Instance(inst.InstanceName, "SemiC", inst.NetBindings, over);
    }

    // ── the resistor ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>R = RSH·(L−NARROW)/(W−NARROW)</c>, with the card's temperature coefficients handed to
    /// circuitRF's own resistor, which already carries them.
    ///
    /// <para><b>Gated on the card actually stating a sheet resistance.</b> A resistor card without
    /// one describes its device some other way, and there is no reading of the remaining parameters
    /// that yields a resistance without inventing one.</para>
    /// </summary>
    private static Instance? BindResistor(
        string cellName, Instance inst, SpiceModelCard card,
        Action<string> note, Action<string> markIncomplete)
    {
        string? Card(string name) => card.Parameters.TryGetValue(name, out var v) ? v : null;
        string? Inst(string name) => inst.Overrides
            .FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Expression;

        string? rsh    = Card("RSH");
        string? narrow = Card("NARROW");
        string? w = Inst("w") ?? Card("DEFW");
        string? l = Inst("l");

        if (rsh is null || w is null || l is null)
        {
            note($"'{inst.InstanceName}' in '{cellName}' names resistor model '{card.Name}', which " +
                 "gives no sheet resistance or no geometry to apply it to. Left unbound rather than " +
                 "given a resistance nothing in the file states.");
            markIncomplete(cellName);
            return null;
        }

        var over = new List<ParameterAssignment>
        {
            new("R", $"({rsh}) * ({Shrink(l, narrow)}) / ({Shrink(w, narrow)})"),
        };

        void Add(string name, string? expr)
        {
            if (expr is not null) over.Add(new ParameterAssignment(name, expr));
        }

        Add("TC1",  Inst("tc1") ?? Card("TC1"));
        Add("TC2",  Inst("tc2") ?? Card("TC2"));
        Add("Tnom", Card("TNOM"));

        CarryDeviceParameters(inst, over, note, cellName);

        return new Instance(inst.InstanceName, "R", inst.NetBindings, over);
    }

    // ── shared ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carries across the parameters that belong to the DEVICE rather than to the card's arithmetic,
    /// and reports the ones circuitRF has nowhere to put — silently dropping a stated initial
    /// condition is exactly the kind of quiet loss this reader exists not to have.
    /// </summary>
    private static void CarryDeviceParameters(
        Instance inst, List<ParameterAssignment> over, Action<string> note, string cellName)
    {
        foreach (var o in inst.Overrides)
        {
            // Consumed by the arithmetic above, or already carried.
            if (Consumed.Contains(o.Name)) continue;

            if (o.Name.Equals("m", StringComparison.Ordinal) ||
                o.Name.Equals("Temp",  StringComparison.OrdinalIgnoreCase) ||
                o.Name.Equals("Dtemp", StringComparison.OrdinalIgnoreCase))
            {
                // circuitRF spells these exactly, and comparison is ordinal.
                string name = o.Name.Equals("m", StringComparison.Ordinal) ? "m"
                            : o.Name.Equals("Temp", StringComparison.OrdinalIgnoreCase) ? "Temp"
                            : "Dtemp";
                over.Add(new ParameterAssignment(name, o.Expression));
                continue;
            }

            if (o.Name.Equals("ic", StringComparison.OrdinalIgnoreCase))
            {
                note($"'{inst.InstanceName}' in '{cellName}' states an initial condition, which " +
                     "belongs to a transient analysis circuitRF does not have; dropped.");
                continue;
            }

            note($"'{inst.InstanceName}' in '{cellName}' states '{o.Name}', which the model card " +
                 "binding has nowhere to put; dropped.");
        }
    }

    private static readonly HashSet<string> Consumed = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "r", "w", "l", "scale", "tc1", "tc2",
    };

    /// <summary>A drawn dimension less the card's edge loss. Absent or zero narrowing is a no-op.</summary>
    private static string Shrink(string? dimension, string? narrow)
    {
        if (dimension is null) return "0";
        if (narrow is null || IsZero(narrow)) return dimension;
        return $"(({dimension}) - ({narrow}))";
    }

    private static string? Scaled(string? expr, string? scale)
    {
        if (expr is null) return null;
        if (scale is null || IsOne(scale)) return expr;
        return $"(({expr}) * ({scale}))";
    }

    private static bool IsZero(string e) => IsLiteral(e, 0.0);
    private static bool IsOne (string e) => IsLiteral(e, 1.0);

    private static bool IsLiteral(string e, double want) =>
        double.TryParse(e.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
        && v == want;
}
