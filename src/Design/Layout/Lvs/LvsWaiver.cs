// A deliberate, reasoned exception to one LVS finding — brief-lvs-12-gui.md §4,
// docs/design/lvs.md §8.2 (R-lvs-53).
//
// ── WHY THIS IS NOT `DrcWaiver` WITH A DIFFERENT NAME ────────────────────────────────────────
//
// Everything about the two is the same except the one thing that matters: the KEY.
//
// A DRC waiver keys on the violation's exact bounding box, deliberately — a DRC waiver names a
// PLACE, so moving the shape stops it applying, and that is the correct outcome because the place
// no longer exists. An LVS waiver names a RELATIONSHIP: "R7's pin 2 is deliberately not
// connected". That relationship survives moving R7 across the board, and it stops being true the
// moment the SCHEMATIC changes. Keying an LVS waiver on a box would silently un-waive it on every
// re-route and keep it alive across the schematic edit that invalidated it — both directions
// wrong (R-lvs12-4b).
//
// ── THE RUN DOES NOT WRITE WAIVERS AND MUST NOT START (R-lvs12-4g) ──────────────────────────
//
// A waiver is a user's edit of their own document, saved when they save it. `LvsRun` READS this
// list off the `.clay` it was handed and never writes it back, which is `check`'s own rule
// (R-aut4-6) and is what keeps a run usable on a read-only tree and on a workspace another
// process has open. The CLI honours waivers and cannot create one (R-lvs12-4f): there is no
// `--waive`, because a waiver is a deliberate act performed beside the thing being waived.

using System.Linq;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// A deliberate, persisted exception to one finding — <see cref="Drc.DrcWaiver"/>'s rules verbatim
/// (R-lvs12-4a), including that <b>a waived finding is still REPORTED</b> and merely not counted.
/// </summary>
/// <remarks>
/// Stored on the <see cref="LayoutView"/> that was checked, for <c>DrcWaiver</c>'s own reason: the
/// finding belongs to this artwork, and a technology shared by twenty cells must not accumulate one
/// cell's exceptions.
/// </remarks>
public sealed class LvsWaiver
{
    /// <summary><see cref="LvsWaiverKey.For(LvsFinding)"/> of the finding this waives — the
    /// CORRESPONDENCE, never a box (R-lvs12-4b).</summary>
    public string Key { get; set; } = "";

    /// <summary>Why. Free text, may be empty — but the UI asks for it, because a waiver with no
    /// reason is indistinguishable from a mistake six months later.</summary>
    public string Reason { get; set; } = "";

    /// <summary>
    /// The finding's own sentence at the time of waiving — <c>DrcWaiver.RuleName</c>'s field and its
    /// reasoning, unchanged (R-lvs12-4d): a waiver whose key no longer matches anything can still be
    /// listed and removed by a human who recognises it.
    /// </summary>
    public string FindingText { get; set; } = "";
}

/// <summary>
/// The identity a waiver names: <b>(finding id, the schematic-side identity, the terminal)</b>
/// — R-lvs12-4c.
/// </summary>
/// <remarks>
/// <b>Read off the diagnostic's TYPED ARGUMENTS, because the id is the contract and the sentence is
/// not</b> (R-lvs8-2a). Reading a name out of a rendered sentence would break the first time
/// somebody reworded a template, which brief 8 explicitly allows.
///
/// <para><b>Not one entry per id.</b> <c>LvsReport</c> already holds the table saying which argument
/// of which id names an object, and a second copy of it here would be the same fault twice — one
/// that drifts silently, since nothing compares them. What is here instead is the ORDER the
/// arguments are preferred in, which is the whole of the rule: the designer's own name for the
/// object the finding is about, on the schematic side wherever the finding has one, and never a
/// coordinate.</para>
/// </remarks>
public static class LvsWaiverKey
{
    /// <summary>
    /// Which typed argument holds the identity, most-preferred first.
    /// </summary>
    /// <remarks>
    /// <c>schematicPath</c> leads because a finding naming BOTH sides (a type mismatch, every
    /// property divergence) is about the pairing, and the schematic half is the one a schematic edit
    /// must invalidate. <c>path</c> follows for the single-device findings — schematic-side for
    /// <c>lvs.device.unmatched-schematic</c> and <c>lvs.terminal.wrong-net</c>, layout-side for the
    /// rest, and layout-side is right there too: it is the designator, which survives every move and
    /// changes when the part is renamed. Then the net-level findings by the schematic net names they
    /// carry, and the designator of the duplicate-designator line.
    /// </remarks>
    private static readonly string[] Identity =
        ["schematicPath", "path", "nets", "net", "designator"];

    /// <summary>Which argument names the terminal, where a finding is about one.</summary>
    private const string Terminal = "port";

    /// <summary>The key <paramref name="finding"/> is waived by.</summary>
    public static string For(LvsFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        string identity = "";
        foreach (string name in Identity)
            if (finding.Diagnostic.Arguments.TryGetValue(name, out object? value) &&
                value?.ToString() is { Length: > 0 } text)
            { identity = text; break; }

        // A finding whose id carries no named object still names its OBJECTS, which are the
        // designer's own un-reduced names and carry no coordinate either. A run-level line has
        // neither and keys on its id alone — which is correct: there is one of it.
        if (identity.Length == 0) identity = string.Join(",", finding.Objects);

        string terminal = finding.Diagnostic.Arguments.TryGetValue(Terminal, out object? port)
            ? port?.ToString() ?? "" : "";

        return $"{finding.Id}|{identity}|{(terminal.Length > 0 ? terminal : "-")}";
    }
}

/// <summary>Applying a document's waivers to a run's findings, and finding the ones that no longer
/// match anything.</summary>
public static class LvsWaivers
{
    /// <summary>
    /// <paramref name="findings"/> with every waived one MARKED — <c>DrcEngine.ApplyWaivers</c>'
    /// shape, and the same rule: a waived finding stays in the list in its own place and is merely
    /// not counted (<see cref="LvsRunResult.ErrorCount"/> and its siblings do the not-counting).
    /// </summary>
    public static IReadOnlyList<LvsFinding> Apply(
        IReadOnlyList<LvsFinding> findings, IEnumerable<LvsWaiver>? waivers)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (waivers is null) return findings;

        var byKey = new Dictionary<string, LvsWaiver>(StringComparer.Ordinal);
        foreach (var w in waivers) byKey[w.Key] = w;
        if (byKey.Count == 0) return findings;

        return [.. findings.Select(f => byKey.TryGetValue(LvsWaiverKey.For(f), out var w)
            ? f with { Waived = true, WaiverReason = w.Reason }
            : f with { Waived = false, WaiverReason = null })];
    }

    /// <summary>
    /// Every waiver that matched nothing in this run — <b>listed rather than dropped</b>
    /// (R-lvs12-4d), so a human who recognises the text it carries can remove it.
    /// </summary>
    /// <remarks>
    /// An orphan is the ordinary outcome of the schematic edit that fixed the thing being waived,
    /// and it is also the outcome of the schematic edit that made the waiver a lie. Neither can be
    /// told from the other by machine, which is exactly why they are listed for a person.
    /// </remarks>
    public static IReadOnlyList<LvsWaiver> Orphans(
        IEnumerable<LvsWaiver>? waivers, IEnumerable<LvsFinding> findings)
    {
        if (waivers is null) return [];
        ArgumentNullException.ThrowIfNull(findings);

        var live = new HashSet<string>(findings.Select(LvsWaiverKey.For), StringComparer.Ordinal);
        return [.. waivers.Where(w => !live.Contains(w.Key))];
    }
}
