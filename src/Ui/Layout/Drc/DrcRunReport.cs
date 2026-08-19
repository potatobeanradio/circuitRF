// What a DRC run says in the Messages panel (docs/design/layout-view.md §9A).
//
// ── Why this is one place and not three ─────────────────────────────────────────────────────────
//
// A check is reachable from Design ▸ Check Design Rules, from a torn-off layout window with no shell
// in reach, and from the DRC panel's own Check button. The first two grew the same six lines twice;
// the third grew NOTHING, so pressing Check in the panel ran a check and posted not a word — which
// matters now that the run has something to say even with no `.wasm` in the workspace (the built-in
// rule set: see WBondBuiltInRules). Three surfaces, one sentence.

using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Layout.Drc;

public static class DrcRunReport
{
    /// <summary>
    /// Posts one run's diagnostics and its one-line verdict. Null sink posts nothing — a torn-off
    /// window or a test is a supported host, not a bug.
    /// </summary>
    public static void Post(IMessageSink? messages, DrcRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (messages is null) return;

        // Diagnostics FIRST and the verdict last: the verdict is the answer, and an answer that
        // scrolls above its own footnotes reads as belonging to the run before it.
        foreach (var d in result.Diagnostics) messages.Warning($"DRC — {d}");

        string tech = result.TechnologyName is { Length: > 0 } n ? $" against \"{n}\"" : "";

        if (result.IsClean)
            messages.Success($"DRC{tech}: no violations — {result.RulesEvaluated} rule(s) over " +
                             $"{result.ShapesChecked:N0} shape(s)" +
                             (result.WaivedCount > 0 ? $", {result.WaivedCount} waived." : "."));
        else
            messages.Warning($"DRC{tech}: {result.ErrorCount} error(s), {result.WarningCount} warning(s)" +
                             (result.WaivedCount > 0 ? $", {result.WaivedCount} waived" : "") +
                             " — see the DRC panel.");
    }
}
