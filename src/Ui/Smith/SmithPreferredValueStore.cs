using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The discrete ladders in force — the shipped ones, or the user's own
/// (<c>docs/design/smith-chart.md</c> §5.6a).
/// </summary>
/// <remarks>
/// <b>A preference of null IS the shipped ladder, and that is the whole of "revert".</b> Nothing
/// copies <see cref="SmithPreferredValues"/>'s tables into the preferences file on first run, so
/// <see cref="Revert"/> is a write of null and a user who has never touched the list picks up
/// whatever a later circuitRF ships. Storing the shipped values the first time the dialog opened
/// would have frozen every user on the table current the day they first looked at it, silently.
///
/// <para><b>It reads the preference on every call</b> rather than caching. <c>AppPreferencesIo</c>
/// already holds ONE in-process copy (MW1 R-mw1-8), so this is a dictionary lookup, not a file
/// read — and a cache here would be a second copy that <c>AppDataRoot.RedirectTo</c> would have to
/// know about, which is exactly the class of bug that lever exists to prevent.</para>
/// </remarks>
public static class SmithPreferredValueStore
{
    /// <summary>The capacitance ladder, farads, ascending.</summary>
    public static IReadOnlyList<double> Capacitors
        => Sane(AppPreferencesIo.Load().SmithPreferredCapacitorsFarad)
           ?? SmithPreferredValues.ShippedCapacitorsFarad;

    /// <summary>The inductance ladder, henries, ascending.</summary>
    public static IReadOnlyList<double> Inductors
        => Sane(AppPreferencesIo.Load().SmithPreferredInductorsHenry)
           ?? SmithPreferredValues.ShippedInductorsHenry;

    /// <summary>The ladder for one parameter, or null for a parameter that is not on one.</summary>
    public static IReadOnlyList<double>? LadderFor(SmithParameter p)
        => SmithPreferredValues.LadderFor(p, Capacitors, Inductors);

    /// <summary>True when either ladder has been replaced — what greys out <i>Revert</i>.</summary>
    public static bool IsCustomized
        => Sane(AppPreferencesIo.Load().SmithPreferredCapacitorsFarad) is not null
        || Sane(AppPreferencesIo.Load().SmithPreferredInductorsHenry)  is not null;

    /// <summary>
    /// Replaces one ladder — or, when what is handed in <b>IS</b> the shipped ladder, stores
    /// nothing and clears any override.
    /// </summary>
    /// <remarks>
    /// <b>That second case is not an optimization.</b> The editor commits both ladders on every
    /// Apply, because a half-applied pair is worse than a refusal — so a user who edits their
    /// capacitors and presses Apply also re-commits their inductors, untouched. Storing those would
    /// silently mark a list the user never edited as customized and freeze them on today's table
    /// forever after. Compared by RATIO rather than by bits, because the editor's own round trip
    /// through five significant digits of text does not return the same double.
    /// </remarks>
    public static void Set(MatchQuantity quantity, IReadOnlyList<double> values)
    {
        var stored = values.Where(v => v > 0 && double.IsFinite(v)).OrderBy(v => v).ToList();
        if (stored.Count == 0) return;

        var shipped = quantity == MatchQuantity.Capacitance
            ? SmithPreferredValues.ShippedCapacitorsFarad
            : SmithPreferredValues.ShippedInductorsHenry;

        List<double>? write = SameLadder(stored, shipped) ? null : stored;

        AppPreferencesIo.Update(p =>
        {
            if (quantity == MatchQuantity.Capacitance) p.SmithPreferredCapacitorsFarad = write;
            else                                       p.SmithPreferredInductorsHenry  = write;
        });
    }

    /// <summary>Two ladders that hold the same rungs, to the precision five significant digits of
    /// text survives.</summary>
    private static bool SameLadder(IReadOnlyList<double> a, IReadOnlyList<double> b)
        => a.Count == b.Count && !a.Where((v, i) => Math.Abs(v / b[i] - 1.0) > 1e-9).Any();

    /// <summary>Puts one ladder — or, with no argument, both — back to what circuitRF ships.</summary>
    public static void Revert(MatchQuantity? quantity = null)
        => AppPreferencesIo.Update(p =>
        {
            if (quantity != MatchQuantity.Inductance)  p.SmithPreferredCapacitorsFarad = null;
            if (quantity != MatchQuantity.Capacitance) p.SmithPreferredInductorsHenry  = null;
        });

    /// <summary>A stored list that could not be snapped to reads as ABSENT rather than as a refusal:
    /// a preferences file hand-edited to <c>[]</c>, or to nothing but zeros, would otherwise leave
    /// the toggle on and inert with no way to tell from the window why.</summary>
    private static IReadOnlyList<double>? Sane(List<double>? stored)
    {
        if (stored is null) return null;
        var usable = stored.Where(v => v > 0 && double.IsFinite(v)).OrderBy(v => v).ToList();
        return usable.Count > 0 ? usable : null;
    }
}
