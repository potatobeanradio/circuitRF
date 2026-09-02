using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// What circuitRF makes of a source line — <c>V</c>, <c>I</c>, <c>E</c>, <c>G</c>.
///
/// <para><b>Two components, chosen by the SHAPE of the transfer, not by the letter.</b> A controlled
/// source whose transfer is a constant times one sensed quantity is an ideal <c>VCCS</c>/<c>VCVS</c>:
/// a LINEAR element, stamped straight into the matrix at every frequency, which keeps a linear
/// macromodel linear and therefore keeps an S-parameter run on it from needing an operating point it
/// has no reason to need. Anything else is an equation, and an equation is the equation-defined
/// device — carrying its expression as a port current (<c>I[1,0]</c>, for a behavioural CURRENT
/// source) or as a held port voltage (<c>V[1]</c>, for a behavioural VOLTAGE one).</para>
///
/// <para><b>Every node the expression senses becomes a port that draws no current</b>, and every
/// branch current it reads becomes a control-current reference — both of which the equation-defined
/// device already has, honoured in DC, harmonic balance and S-parameters alike. See
/// <see cref="SpiceBehaviouralSource"/>, which does the reading; this decides what to build from
/// it.</para>
/// </summary>
internal static class SpiceSourceTranslation
{
    /// <summary>The element references the reader emits for a source line.</summary>
    internal static bool Handles(string reference)
        => reference is "V" or "I" or "E" or "G";

    internal static SubcircuitElement Translate(Instance inst)
    {
        SubcircuitElement Refuse(string why) => new(
            inst.InstanceName, inst.Reference, inst.NetBindings, null, null, [], [], [], why);

        if (inst.NetBindings.Count != 2)
            return Refuse(
                $"'{inst.InstanceName}' binds {inst.NetBindings.Count} net(s); a source connects "
                + "across exactly two.");

        return inst.Reference switch
        {
            "V" => Independent(inst, current: false),
            "I" => Independent(inst, current: true),
            _   => Controlled(inst),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  V and I
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An independent source at its DC value.
    ///
    /// <para><b>A zero-volt <c>V</c> is a current SENSOR, not a source, and that is the majority of
    /// them.</b> The idiom exists so something else can name its branch current; circuitRF's
    /// <c>IProbe</c> is exactly that component, it is what a control-current reference can name, and
    /// it is what the line means. Placing a zero-volt supply instead would be electrically identical
    /// and would read as a mistake on the schematic.</para>
    /// </summary>
    private static SubcircuitElement Independent(Instance inst, bool current)
    {
        string dc = Expression(inst, SpiceNetlistReader.SourceDcParameter) ?? "0";
        var notes = new List<string>();

        if (!current && IsExactlyZero(dc))
        {
            notes.Add("A zero-volt source is the current-sensor idiom, so it is placed as an "
                    + "IProbe — the component whose branch current a behavioural source can name.");
            return new SubcircuitElement(
                inst.InstanceName, inst.Reference, inst.NetBindings,
                SymbolKind.IProbe, null, [], [], notes, null);
        }

        if (!current)
            return new SubcircuitElement(
                inst.InstanceName, inst.Reference, inst.NetBindings,
                SymbolKind.Vdc, null,
                [ModelCardCellBuilder.ImportedParameter(SymbolKind.Vdc, "Vdc", dc)],
                [], notes, null);

        // circuitRF has no DC-only current-source component. ITone at zero amplitude IS one — its
        // DC offset is a parameter of its own, and a tone of zero amplitude at zero frequency
        // contributes nothing else — so this places the smallest thing that says what the line says
        // rather than adding a component that would exist for this one case.
        notes.Add("circuitRF has no DC-only current source; this is an ITone carrying the line's "
                + "DC value as its offset, with no tone.");
        return new SubcircuitElement(
            inst.InstanceName, inst.Reference, inst.NetBindings,
            SymbolKind.CurrentToneSource, null,
            [
                ModelCardCellBuilder.ImportedParameter(SymbolKind.CurrentToneSource, "Idc",  dc),
                ModelCardCellBuilder.ImportedParameter(SymbolKind.CurrentToneSource, "I",    "0"),
                ModelCardCellBuilder.ImportedParameter(SymbolKind.CurrentToneSource, "Freq", "0"),
            ],
            [], notes, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  E and G
    // ─────────────────────────────────────────────────────────────────────────

    private static SubcircuitElement Controlled(Instance inst)
    {
        SubcircuitElement Refuse(string why) => new(
            inst.InstanceName, inst.Reference, inst.NetBindings, null, null, [], [], [], why);

        bool isVoltage = inst.Reference == "E";
        string what    = isVoltage ? "voltage" : "current";

        string? value = Expression(inst, SpiceNetlistReader.SourceValueParameter);
        if (value is null)
            return Refuse($"'{inst.InstanceName}' states no transfer expression.");

        // `ddt(x)` marks a CHARGE, not a function to evaluate: a current source stating the time
        // derivative of something is stating that thing as its charge, and the equation-defined
        // device has a bucket for exactly that. Recognised before the expression is read, because
        // afterwards it is indistinguishable from an unknown function call.
        string? charge = null;
        if (!isVoltage && SpiceChargeSpelling.TryReadDdt(value, out string? inner))
        {
            charge = inner;
            value  = inner;
        }

        var form = SpiceBehaviouralSource.Read(
            value!, inst.NetBindings[0], inst.NetBindings[1]);

        if (form.Refusal is { } why)
            return Refuse($"'{inst.InstanceName}' is a behavioural {what} source, and {why}.");

        var notes = new List<string>();

        // ── the ideal controlled source ───────────────────────────────────────
        //
        // One sensed quantity, one coefficient, no offset — and the sensed quantity is a VOLTAGE
        // pair other than the source's own. That is precisely what circuitRF's VCCS and VCVS state,
        // and stating it that way keeps the element LINEAR.
        if (charge is null && form.IsAffine && !form.AffineIsCurrent && form.AffineOf >= 1)
        {
            var ctrl = form.Pairs[form.AffineOf];
            var kind = isVoltage ? SymbolKind.Vcvs : SymbolKind.Vccs;
            string gainParam = isVoltage ? "E" : "G";

            return new SubcircuitElement(
                inst.InstanceName, inst.Reference,
                [inst.NetBindings[0], inst.NetBindings[1], ctrl.Plus, ctrl.Minus],
                kind, null,
                [ModelCardCellBuilder.ImportedParameter(kind, gainParam, form.AffineGain!)],
                [], notes, null);
        }

        // ── the equation-defined device ───────────────────────────────────────
        var nets = new List<string>(form.Pairs.Count * 2);
        foreach (var p in form.Pairs) { nets.Add(p.Plus); nets.Add(p.Minus); }

        var parameters = new List<EditableParameter>
        {
            new() { Name = "NumPorts", Expression = form.Pairs.Count.ToString(CultureInfo.InvariantCulture) },
        };

        string slot = charge is not null ? "I[1,1]" : isVoltage ? "V[1]" : "I[1,0]";
        parameters.Add(new EditableParameter { Name = slot, Expression = form.Equation });

        for (int n = 0; n < form.ControlSources.Count; n++)
            parameters.Add(new EditableParameter
            {
                Name       = $"C[{n + 1}]",
                Expression = form.ControlSources[n],
            });

        if (form.Pairs.Count > 1)
            notes.Add($"Its expression senses {form.Pairs.Count - 1} node pair(s) the source is not "
                    + "connected to; each is an extra port that carries no current.");
        if (form.ControlSources.Count > 0)
            notes.Add($"It reads the branch current of {string.Join(", ", form.ControlSources)}, "
                    + "carried as a control-current reference.");
        notes.Add(charge is not null
            ? "'ddt' marks a charge rather than a function to evaluate, so the expression inside it "
            + "is the device's charge equation."
            : isVoltage
                ? "Its expression HOLDS the source's own pair at a voltage, which is a branch "
                + "equation — solved at DC and linearised for S-parameters. Harmonic balance "
                + "refuses it by name."
                : "Its expression is the current the source delivers.");

        return new SubcircuitElement(
            inst.InstanceName, inst.Reference, nets,
            SymbolKind.Sdd, null, parameters, [], notes, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string? Expression(Instance inst, string name)
        => inst.Overrides
               .FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?.Expression;

    /// <summary>
    /// Whether a value is the literal zero — which is what makes a <c>V</c> line a sensor.
    ///
    /// <para>A value that is an EXPRESSION is not tested for being zero even if it would evaluate to
    /// one: what a parameter resolves to is a property of the call site, and a component's identity
    /// cannot depend on that.</para>
    /// </summary>
    private static bool IsExactlyZero(string expression)
        => double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
        && v == 0.0;
}
