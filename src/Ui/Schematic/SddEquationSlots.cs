using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// One equation slot an SDD can actually use, as offered by the "Add Equation…" picker.
/// </summary>
/// <param name="Category">Grouping label shown in the picker ("Current", "Charge", …).</param>
/// <param name="Name">The parameter name to create — always a spelling the factory reads.</param>
/// <param name="Summary">The picker's one-line entry.</param>
/// <param name="Detail">What the slot means, and what its seeded value does.</param>
/// <param name="DefaultExpression">
/// What the row is seeded with. <b>Never blank.</b> An SDD parameter reaches the elaborator
/// verbatim and <c>ComponentModelFactory</c> parses it, so an empty expression is a
/// <c>ParseException</c> at Run — which is what the old "+" produced. For a current or a charge the
/// seed is <c>0</c>, which is not a guess: <c>SddModel</c> documents an absent equation as zero, so
/// the seeded row means exactly what leaving the slot out means.
/// </param>
/// <param name="CompanionName">A second parameter created with this one, or "" for none.</param>
/// <param name="CompanionExpression">The companion's seeded expression.</param>
public sealed record SddEquationSlot(
    string Category,
    string Name,
    string Summary,
    string Detail,
    string DefaultExpression,
    string CompanionName = "",
    string CompanionExpression = "")
{
    /// <summary>Name as the picker shows it — "I[1,2] + H[2]" when a companion rides along.</summary>
    public string DisplayName => CompanionName.Length == 0 ? Name : $"{Name} + {CompanionName}";
}

/// <summary>
/// Enumerates the equation slots a given SDD can use — the data behind the "Add Equation…" picker
/// (owner report, 2026-09-02).
///
/// <para><b>Why a catalog rather than the generic "+" template.</b> The parameter editor's indexed
/// group descriptor adds <c>Name[n]</c> for an ever-increasing n, which cannot express what an SDD
/// accepts. The SDD's slots are two-dimensional (<c>I[p,w]</c> — port and weighting index), bounded
/// by the port count, and spelled several ways for the same thing. The generic index parser reads
/// neither dimension: it saw the seeded <c>I[1,0]</c> as an unindexed name, so the first "+" offered
/// <c>I[1]</c> — which is valid sugar for <c>I[1,0]</c> and therefore SILENTLY REPLACED the seeded
/// equation — and a few more presses reached <c>I[3]</c> on a 2-port, which is a hard refusal at Run
/// ("references port 3 but only 2 port(s) of nets were given").</para>
///
/// <para><b>Every slot returned is one this SDD can use, at the value it is created with.</b> That is
/// the property the picker exists to guarantee, so it is this class's job and not the dialog's:
/// nothing already present is offered, no port beyond the port count is offered, and a weighted
/// current is never offered without the <c>H[w]</c> it needs.</para>
///
/// <para>Framework-free by design — it is pure schematic-model reasoning, and its tests need no UI.</para>
/// </summary>
public static class SddEquationSlots
{
    // The spellings ComponentModelFactory.CreateSddModel reads. Mirrored here rather than shared
    // because the factory's are private and this is the UI's own reading of the same contract; the
    // gate tests drive a real elaboration, so the two cannot drift silently.
    private static readonly Regex RxCurrent2 = new(@"^I\[(\d+),(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex RxCurrent1 = new(@"^I\[(\d+)\]$",       RegexOptions.Compiled);
    private static readonly Regex RxCharge1  = new(@"^Q\[(\d+)\]$",       RegexOptions.Compiled);
    private static readonly Regex RxWeight   = new(@"^H\[(\d+)\]$",       RegexOptions.Compiled);
    private static readonly Regex RxVoltage  = new(@"^V\[(\d+)\]$",       RegexOptions.Compiled);
    private static readonly Regex RxControl  = new(@"^C\[(\d+)\]$",       RegexOptions.Compiled);
    private static readonly Regex RxCport    = new(@"^Cport\[(\d+)\]$",   RegexOptions.Compiled);

    /// <summary>A control current an equation reads — <c>_c1</c>, <c>_c2</c>, …</summary>
    private static readonly Regex RxControlVar = new(@"_c(\d+)\b", RegexOptions.Compiled);

    /// <summary>The placeholder name a "named constant" slot is created under.</summary>
    public const string ConstantPrefix = "Param";

    /// <summary>
    /// The slots <paramref name="portCount"/> ports can still take, given the parameters the
    /// component already carries. Ordered by category, ports ascending within each.
    /// </summary>
    /// <param name="existing">Every parameter on the component, as (name, expression) pairs — the
    /// expressions are read too, because a <c>C[n]</c> is only offered once an equation asks for it.</param>
    public static IReadOnlyList<SddEquationSlot> Available(
        int portCount, IEnumerable<(string Name, string Expression)> existing)
    {
        if (portCount < 1) portCount = 1;
        var have = existing?.ToList() ?? [];

        // ── What is already taken ────────────────────────────────────────────
        // I[p] and Q[p] are sugar for I[p,0] and I[p,1]; a slot occupied under either spelling is
        // occupied. Offering the other one would create the duplicate this class exists to stop.
        var taken   = new HashSet<(int Port, int W)>();
        var voltage = new HashSet<int>();
        var weights = new HashSet<int>();
        var control = new HashSet<int>();
        var cport   = new HashSet<int>();
        int constants = 0;

        foreach (var (name, _) in have)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (RxCurrent2.Match(name) is { Success: true } m2)
            { taken.Add((int.Parse(m2.Groups[1].Value), int.Parse(m2.Groups[2].Value))); continue; }

            if (RxCurrent1.Match(name) is { Success: true } m1)
            { taken.Add((int.Parse(m1.Groups[1].Value), 0)); continue; }

            if (RxCharge1.Match(name) is { Success: true } mq)
            { taken.Add((int.Parse(mq.Groups[1].Value), 1)); continue; }

            if (RxVoltage.Match(name) is { Success: true } mv)
            { voltage.Add(int.Parse(mv.Groups[1].Value)); continue; }

            if (RxWeight.Match(name) is { Success: true } mh)
            { weights.Add(int.Parse(mh.Groups[1].Value)); continue; }

            if (RxCport.Match(name) is { Success: true } mcp)
            { cport.Add(int.Parse(mcp.Groups[1].Value)); continue; }

            if (RxControl.Match(name) is { Success: true } mc)
            { control.Add(int.Parse(mc.Groups[1].Value)); continue; }

            if (name.StartsWith(ConstantPrefix, StringComparison.Ordinal)
                && int.TryParse(name[ConstantPrefix.Length..], out int cn) && cn >= 1)
                constants = Math.Max(constants, cn);
        }

        // A port states EITHER what its voltage is OR what its current is — the factory refuses
        // both, by name ("port 1 states both V[1] and a current equation"). So each side of that
        // exclusion has to suppress the other here, or the picker offers a slot whose only effect
        // is to make the design refuse to elaborate.
        bool HasCurrentAt(int p) => taken.Any(t => t.Port == p);

        var slots = new List<SddEquationSlot>();

        // ── 1. Current i(v) ──────────────────────────────────────────────────
        for (int p = 1; p <= portCount; p++)
        {
            if (voltage.Contains(p)) continue;
            if (taken.Contains((p, 0))) continue;
            slots.Add(new SddEquationSlot("Current", $"I[{p},0]",
                $"I[{p},0] — current into port {p}",
                $"The conduction current this device draws at port {p}, as a function of the port "
                + $"voltages (_v1 … _v{portCount}). Seeded at 0, which is exactly what leaving the "
                + "slot out means.",
                DefaultExpression: "0"));
        }

        // ── 2. Charge q(v) ───────────────────────────────────────────────────
        for (int p = 1; p <= portCount; p++)
        {
            if (voltage.Contains(p)) continue;
            if (taken.Contains((p, 1))) continue;
            slots.Add(new SddEquationSlot("Charge", $"I[{p},1]",
                $"I[{p},1] — charge at port {p}",
                $"The stored charge at port {p}; the engine differentiates it, so this is how a "
                + "capacitance is written. Q[p] is the same slot under its short spelling — this "
                + "one is used so the row reads alongside I[p,0].",
                DefaultExpression: "0"));
        }

        // ── 3. Branch equation V[p] ──────────────────────────────────────────
        for (int p = 1; p <= portCount; p++)
        {
            if (voltage.Contains(p) || HasCurrentAt(p)) continue;
            slots.Add(new SddEquationSlot("Voltage", $"V[{p}]",
                $"V[{p}] — hold port {p}'s voltage",
                $"Constrains port {p}: its voltage follows this expression and its current becomes a "
                + "branch unknown the rest of the circuit sets. This is how a behavioural voltage "
                + "source is written. Seeded at 0, which holds the port at zero volts until you "
                + "write the expression.",
                DefaultExpression: "0"));
        }

        // ── 4. Weighted current through a weighting this SDD already declares ─
        foreach (int w in weights.Where(w => w >= 2).OrderBy(w => w))
            for (int p = 1; p <= portCount; p++)
            {
                if (voltage.Contains(p)) continue;
                if (taken.Contains((p, w))) continue;
                slots.Add(new SddEquationSlot("Weighted", $"I[{p},{w}]",
                    $"I[{p},{w}] — weighted current at port {p}",
                    $"Weighted by H[{w}], which this SDD already declares. Seeded at 0.",
                    DefaultExpression: "0"));
            }

        // ── 5. Weighted current with a NEW weighting ─────────────────────────
        //
        // The two are offered together because separately neither runs: the factory refuses an
        // I[p,w] whose H[w] is not defined, and an H[w] nothing references does nothing.
        int wNext = 2;
        while (weights.Contains(wNext)) wNext++;
        for (int p = 1; p <= portCount; p++)
        {
            if (voltage.Contains(p)) continue;
            slots.Add(new SddEquationSlot("Weighted", $"I[{p},{wNext}]",
                $"I[{p},{wNext}] + H[{wNext}] — weighted current at port {p}, with a new weighting",
                $"Adds the weighting function too, because an I[p,{wNext}] without its H[{wNext}] is "
                + $"refused at Run. H[{wNext}] is seeded at 1 (plain conduction, the same weight as "
                + "w=0) until you write the frequency-domain expression you want.",
                DefaultExpression: "0",
                CompanionName: $"H[{wNext}]", CompanionExpression: "1"));
        }

        // ── 6. Control-current references ────────────────────────────────────
        //
        // Offered ONLY for an _cn some equation already reads. A C[n] carries an INSTANCE NAME, not
        // an expression, so there is no value it could be seeded with that runs — which is precisely
        // why it is not offered speculatively. Where an equation already reads _cn and no C[n]
        // exists, the run refuses until one is added, so this row is the fix for a stated problem.
        var referenced = new SortedSet<int>();
        foreach (var (_, expr) in have)
        {
            if (string.IsNullOrEmpty(expr)) continue;
            foreach (Match m in RxControlVar.Matches(expr))
                referenced.Add(int.Parse(m.Groups[1].Value));
        }
        foreach (int n in referenced)
        {
            if (control.Contains(n)) continue;
            slots.Add(new SddEquationSlot("Control", $"C[{n}]",
                $"C[{n}] — name the instance _c{n} reads",
                $"An equation on this SDD reads _c{n} and no C[{n}] says whose current that is, so "
                + "the run refuses until one does. The value is a sibling instance's NAME, not an "
                + "expression — type it in the row.",
                DefaultExpression: ""));
        }
        foreach (int n in control.OrderBy(n => n))
        {
            if (cport.Contains(n)) continue;
            slots.Add(new SddEquationSlot("Control", $"Cport[{n}]",
                $"Cport[{n}] — which port of C[{n}]",
                $"Selects the port when the instance C[{n}] names has more than one. Seeded at 1, "
                + "which is what a single-port reference already means.",
                DefaultExpression: "1"));
        }

        // ── 7. A named constant ──────────────────────────────────────────────
        //
        // Not an equation, but the SDD does use it: the elaborator resolves a non-equation parameter
        // to a number and binds it in the scope the equations evaluate in, so an equation can be
        // written in terms of a per-instance value (a device width, a scaling factor). Renamed in
        // the row — the placeholder is a plain identifier so it is valid before it is renamed.
        slots.Add(new SddEquationSlot("Constant", $"{ConstantPrefix}{constants + 1}",
            $"{ConstantPrefix}{constants + 1} — a named value the equations can reference",
            "Resolved once at elaboration and bound in the SDD's own scope, so any equation on this "
            + "device can use it by name. Rename it to whatever the equations call it.",
            DefaultExpression: "0"));

        return slots;
    }

    /// <summary>
    /// What the picker cannot offer, and why — shown beneath the list.
    ///
    /// <para>A suppressed slot with no explanation reads as a missing feature. The one that actually
    /// bites is <c>V[p]</c>: a freshly placed SDD carries an <c>I[p,0]</c> on every port, and a port
    /// states EITHER its voltage or its current, so the branch equation is offered on no port at all
    /// until a current equation is removed. That is a rule of the device, not a gap in the dialog,
    /// and it takes one sentence to say.</para>
    /// </summary>
    public static IReadOnlyList<string> Notes(
        int portCount, IEnumerable<(string Name, string Expression)> existing)
    {
        if (portCount < 1) portCount = 1;
        var offered = Available(portCount, existing)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);

        var have = (existing ?? []).Select(e => e.Name).ToHashSet(StringComparer.Ordinal);

        var blocked = Enumerable.Range(1, portCount)
            .Where(p => !offered.Contains($"V[{p}]") && !have.Contains($"V[{p}]"))
            .ToList();

        if (blocked.Count == 0) return [];

        return [
            $"{string.Join(", ", blocked.Select(p => $"V[{p}]"))} not offered — a port states either "
            + "its VOLTAGE or its current, never both. Remove that port's current equation first."
        ];
    }
}
