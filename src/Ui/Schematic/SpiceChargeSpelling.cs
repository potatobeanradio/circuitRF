using System;
using System.Collections.Generic;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The ways a netlist can state a nonlinear CHARGE directly, rather than through the behavioural
/// source that carries one in the files this reader was measured against.
///
/// <para><b>These spellings are here because the reader accepts any file the user points it at.</b>
/// None of the measured library files uses one — every nonlinear capacitance in them is written as a
/// behavioural voltage source driving a linear capacitor — but they are the ordinary way other
/// suppliers write the same physics, and both are translation-only.</para>
/// </summary>
internal static class SpiceChargeSpelling
{
    /// <summary>The parameter a capacitor line carries a charge expression in.</summary>
    internal const string ChargeParameter = "Q";

    /// <summary>
    /// Reads <c>ddt(x)</c> as the charge marker it is.
    ///
    /// <para>A current source stating the time derivative of something is stating that thing as its
    /// charge, and the equation-defined device has a bucket for exactly that — where harmonic
    /// balance already applies <c>jkω</c> to its harmonics. Evaluating <c>ddt</c> as a function is
    /// the thing that cannot be done: there is no time axis to differentiate along.</para>
    ///
    /// <para>Recognised only when the WHOLE expression is one <c>ddt</c> call. A <c>ddt</c> buried
    /// inside arithmetic mixes a charge with a current in one equation, which the device states as
    /// two separate buckets and cannot take as one.</para>
    /// </summary>
    internal static bool TryReadDdt(string expression, out string? inner)
    {
        inner = null;
        string s = expression.Trim();

        const string head = "ddt(";
        if (!s.StartsWith(head, StringComparison.OrdinalIgnoreCase) || !s.EndsWith(')')) return false;

        // The closing bracket must be the one this call opened, or the `ddt` is only the first term
        // of a longer expression that happens to end in a bracket.
        int depth = 0;
        for (int i = head.Length - 1; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) { if (i != s.Length - 1) return false; break; }
        }
        if (depth != 0) return false;

        inner = s[head.Length..^1];
        return inner.Length > 0;
    }

    /// <summary>
    /// The highest power of its own terminal voltage a <c>C={…}</c> may state. Well past anything
    /// physical; it exists so a runaway <c>^</c> is refused rather than expanded.
    /// </summary>
    private const int MaxPolynomialDegree = 8;

    /// <summary>
    /// A capacitor whose VALUE depends on its own terminal voltage.
    ///
    /// <para><b>The trap this exists for is silent, and it is the reason a general expression is
    /// refused rather than approximated.</b> <c>C = f(v)</c> declares the small-signal
    /// CAPACITANCE, so the stored charge is <c>Q = ∫₀ᵛ f(u) du</c> — <b>not</b> <c>f(v)·v</c>. The
    /// two agree only when <c>f</c> is constant, and everywhere else the wrong one converges and
    /// produces plausible numbers. <c>NonlinearCModel</c> integrates a polynomial exactly, which is
    /// why a polynomial is the shape that is accepted; a general expression has no symbolic
    /// integral available here, so it is refused BY NAME with the spelling that has no conversion
    /// to get wrong.</para>
    ///
    /// <para>Returns null when the value senses nothing at all — an ordinary capacitor, and the
    /// caller's own path.</para>
    /// </summary>
    internal static SubcircuitElement? CapacitorCapacitance(Instance inst, string expression)
    {
        SubcircuitElement Refuse(string why) => new(
            inst.InstanceName, inst.Reference, inst.NetBindings, null, null, [], [], [], why);

        if (inst.NetBindings.Count != 2) return null;

        var form = SpiceBehaviouralSource.Read(expression, inst.NetBindings[0], inst.NetBindings[1]);
        if (form.Refusal is not null) return null;          // not readable as one; the caller reports
        if (SpiceBehaviouralSource.SensesNothing(Parser.Parse(form.Equation))) return null;

        string advice = $"Write '{inst.InstanceName}' as a stored charge instead — Q={{…}} — which "
                      + "is the same physics with nothing to integrate: a capacitance is the "
                      + "DERIVATIVE of a charge, so circuitRF would have to integrate this "
                      + "expression to get the quantity it actually solves with, and there is no "
                      + "symbolic integral of it available.";

        // NonlinearCModel is a one-port whose capacitance depends on its OWN terminal voltage. A
        // value that reads somewhere else in the circuit is a different device entirely.
        if (form.Pairs.Count > 1 || form.ControlSources.Count > 0)
            return Refuse(
                $"'{inst.InstanceName}' states a capacitance that depends on a quantity other than "
              + "its own terminal voltage, which circuitRF has no capacitor for. " + advice);

        if (TryPolynomial(Parser.Parse(form.Equation)) is not { } coefficients)
            return Refuse(
                $"'{inst.InstanceName}' states a capacitance that is not a polynomial in its own "
              + "terminal voltage. " + advice);

        var parameters = new List<EditableParameter>();
        for (int k = 0; k < coefficients.Count; k++)
            parameters.Add(new EditableParameter
            {
                Name       = $"C{k}",
                Expression = SpiceBehaviouralSource.Print(coefficients[k]),
            });

        return new SubcircuitElement(
            inst.InstanceName, inst.Reference, inst.NetBindings, SymbolKind.NonlinearC, null,
            parameters, [],
            [$"It states a capacitance that varies with its own voltage, read as the polynomial "
           + $"C(V) = Σ Cₖ·Vᵏ of degree {coefficients.Count - 1}. circuitRF solves with the "
           + "CHARGE, which is that polynomial integrated — ∫C dv, not C(V)·V; the two agree only "
           + "for a constant capacitance."],
            null);
    }

    /// <summary>
    /// The coefficients of a polynomial in the device's own port voltage, lowest power first, or
    /// null when the expression is not one.
    ///
    /// <para>Decided on the AST rather than the text, so <c>C0+C1*v</c>, <c>v*C1+C0</c>,
    /// <c>C1*v^2/2</c> and <c>-(C1*v)</c> are all read for what they are. A coefficient may itself
    /// be any expression that senses no voltage — a cell parameter is still a coefficient.</para>
    /// </summary>
    private static List<Expr>? TryPolynomial(Expr e)
    {
        switch (e)
        {
            case RefExpr r when r.Name == PortVoltage:
                return [new NumberExpr(0), new NumberExpr(1)];

            case UnaryExpr { Op: "+" } u:
                return TryPolynomial(u.Operand);

            case UnaryExpr { Op: "-" } u when TryPolynomial(u.Operand) is { } inner:
                return [.. inner.Select(c => (Expr)new UnaryExpr("-", c))];

            case BinaryExpr { Op: "+" or "-" } b
                when TryPolynomial(b.Left) is { } l && TryPolynomial(b.Right) is { } r:
            {
                var sum = new List<Expr>(Math.Max(l.Count, r.Count));
                for (int k = 0; k < Math.Max(l.Count, r.Count); k++)
                {
                    Expr? a = k < l.Count ? l[k] : null;
                    Expr? c = k < r.Count ? r[k] : null;
                    if (b.Op == "-" && c is not null) c = new UnaryExpr("-", c);
                    sum.Add(a is null ? c! : c is null ? a : new BinaryExpr("+", a, c));
                }
                return sum;
            }

            case BinaryExpr { Op: "*" } b
                when TryPolynomial(b.Left) is { } l && TryPolynomial(b.Right) is { } r:
            {
                if (l.Count + r.Count - 2 > MaxPolynomialDegree) return null;
                var product = new List<Expr>();
                for (int k = 0; k < l.Count + r.Count - 1; k++) product.Add(new NumberExpr(0));
                for (int i = 0; i < l.Count; i++)
                    for (int j = 0; j < r.Count; j++)
                        product[i + j] = new BinaryExpr(
                            "+", product[i + j], new BinaryExpr("*", l[i], r[j]));
                return product;
            }

            case BinaryExpr { Op: "/" } b
                when SpiceBehaviouralSource.SensesNothing(b.Right) && TryPolynomial(b.Left) is { } l:
                return [.. l.Select(c => (Expr)new BinaryExpr("/", c, b.Right))];

            case BinaryExpr { Op: "^" } b when b.Right is NumberExpr { Value: var p }
                                            && p >= 0 && p == Math.Floor(p) && p <= MaxPolynomialDegree:
            {
                if (TryPolynomial(b.Left) is not { } bas) return null;
                List<Expr> acc = [new NumberExpr(1)];
                for (int n = 0; n < (int)p; n++)
                {
                    if (TryPolynomial(new BinaryExpr("*", Rebuild(acc), Rebuild(bas))) is not { } next)
                        return null;
                    acc = next;
                }
                return acc;
            }

            default:
                // Anything that senses no voltage is a constant term, whatever shape it has.
                return SpiceBehaviouralSource.SensesNothing(e) ? [e] : null;
        }
    }

    /// <summary>A coefficient list back as one expression — <c>c0 + c1·v + c2·v² …</c>.</summary>
    private static Expr Rebuild(IReadOnlyList<Expr> coefficients)
    {
        Expr acc = coefficients[^1];
        for (int k = coefficients.Count - 2; k >= 0; k--)
            acc = new BinaryExpr("+", coefficients[k],
                                 new BinaryExpr("*", new RefExpr(PortVoltage), acc));
        return acc;
    }

    /// <summary>The device's own port voltage, as the behavioural reader spells it.</summary>
    private const string PortVoltage = "_v1";

    /// <summary>
    /// A capacitor stating <c>Q={…}</c> becomes the equation-defined device's charge bucket.
    ///
    /// <para>The expression is read exactly as a behavioural source's is — every node voltage it
    /// senses becomes a port that draws no current, every branch current a control reference — so
    /// a charge that depends on somewhere else in the circuit works for the same reason a current
    /// that does works.</para>
    /// </summary>
    internal static SubcircuitElement CapacitorCharge(Instance inst, string expression)
    {
        SubcircuitElement Refuse(string why) => new(
            inst.InstanceName, inst.Reference, inst.NetBindings, null, null, [], [], [], why);

        if (inst.NetBindings.Count != 2)
            return Refuse($"'{inst.InstanceName}' binds {inst.NetBindings.Count} net(s); a capacitor "
                        + "connects across exactly two.");

        var form = SpiceBehaviouralSource.Read(expression, inst.NetBindings[0], inst.NetBindings[1]);
        if (form.Refusal is { } why)
            return Refuse($"'{inst.InstanceName}' states a charge, and {why}.");

        var nets = new List<string>(form.Pairs.Count * 2);
        foreach (var p in form.Pairs) { nets.Add(p.Plus); nets.Add(p.Minus); }

        var parameters = new List<EditableParameter>
        {
            new() { Name = "NumPorts", Expression = form.Pairs.Count.ToString(CultureInfo.InvariantCulture) },
            new() { Name = "I[1,1]",   Expression = form.Equation },
        };
        for (int n = 0; n < form.ControlSources.Count; n++)
            parameters.Add(new EditableParameter { Name = $"C[{n + 1}]", Expression = form.ControlSources[n] });

        return new SubcircuitElement(
            inst.InstanceName, inst.Reference, nets, SymbolKind.Sdd, null, parameters, [],
            ["It states a stored CHARGE rather than a capacitance, so it is the equation-defined "
           + "device's charge equation. A capacitance would have had to be integrated to get here; "
           + "a charge is already the quantity the simulator wants."],
            null);
    }
}
