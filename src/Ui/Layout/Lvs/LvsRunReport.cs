// What an LVS run says in the Messages panel — brief-lvs-12-gui.md, on DrcRunReport's terms.
//
// ── One place, for DrcRunReport's own reason ────────────────────────────────────────────────────
//
// A comparison is reachable from the LVS panel's Compare button and from a torn-off layout window
// with no shell in reach, and will be reachable from the pre-export gate. Each of those surfaces
// growing its own sentence is how the DRC panel came to run a check and post not a word.
//
// ── The technology is NEVER left implicit (R-lvs12-1b) ──────────────────────────────────────────
//
// A comparison made against the wrong process's stackup reads exactly like one made against the
// right one — the nets come out differently and nothing says why — so the verdict names it.

using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Layout.Lvs;

public static class LvsRunReport
{
    /// <summary>
    /// Posts one comparison's verdict. Null sink posts nothing — a torn-off window or a test is a
    /// supported host, not a bug.
    /// </summary>
    public static void Post(IMessageSink? messages, LvsRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (messages is null) return;

        // What the run could NOT do, first and never dropped (R-lvs8-1a): a comparison can look
        // clean and not be, and these are the lines that say so. The verdict goes last because it
        // is the answer, and an answer that scrolls above its own footnotes reads as belonging to
        // the run before it.
        foreach (var note in result.Incomplete) messages.Warning($"LVS — {note.Render()}");

        string tech = result.TechnologyName is { Length: > 0 } n ? $" against \"{n}\"" : "";
        string waived = result.WaivedCount > 0 ? $", {result.WaivedCount} waived" : "";

        if (result.IsClean)
            messages.Success($"LVS{tech}: the artwork implements the drawing — " +
                             $"{result.Counts.Describe()}{waived}.");
        else
            messages.Warning($"LVS{tech}: {result.ErrorCount} error(s), " +
                             $"{result.WarningCount} warning(s){waived} — see the LVS panel.");
    }
}
