// What a row arrives CARRYING — and the one place the bend in §2.2's rule is argued.
//
// ── THE RULE, AND WHY IT IS BENT HERE AND NOWHERE ELSE ──────────────────────────────────────────
//
// RailLoad.DcCurrentA and RailSource.OpenCircuitVoltageV are nullable BY DESIGN (§2.2, Q-16, brief
// 1): "a defaulted zero and a stated zero are the same number and mean different things", and the DC
// report has to list an observation port AS observed. None of that changes. The MODEL still
// represents the absence, clearing either cell still restores it, and nothing downstream has learned
// a default.
//
// What changed is what the ADD GESTURE hands you. Reported from the field, 2026-09-21: a dropped
// load should arrive drawing a small current — a milliamp — rather than reading "observe", and a
// source should arrive holding a voltage rather than needing one typed. A source and a load placed
// on a real board gave, correctly and uselessly, a rail with a source at no stated voltage and a
// port drawing nothing: every drop zero, by construction, with no sign that the document was the
// reason. A tool whose first answer to a complete-looking setup is a column of zeros is a tool that
// reads as broken.
//
// ── SO THE SEED IS VISIBLE, EDITABLE AND COUNTED ────────────────────────────────────────────────
//
// The failure the original rule exists to prevent is a number nobody typed being READ as a number
// somebody typed. A seed is therefore not enough on its own, and this file is two halves:
//
//   1. the values, named once, so the window and its tests cannot disagree about them, and
//   2. SeededRowCount — which rows still carry one, by REFERENCE IDENTITY of the record.
//
// The identity trick is the whole of the bookkeeping and it is free: every committed edit on these
// rows goes through `Commit(_load with { … })`, which builds a NEW record. So a row the user has
// touched is a different object and falls out of the set by itself; there is no "dirty" flag to set,
// to clear, or to forget to clear. Removing a row drops it for the same reason — the count is
// derived from the document's own lists, never from the set alone.
//
// ── WHAT IT DELIBERATELY DOES NOT DO ────────────────────────────────────────────────────────────
//
// It does not survive a SAVE, and that is the right boundary rather than a shortcut. Nothing is
// written to the `.crail` — a seeded 1 mA is stored as 1 mA, indistinguishable from a typed one,
// because by the time somebody saved the document they adopted the number. Persisting "railRF chose
// this" would put a provenance field in a document format to carry a warning about a session that
// has ended, and would then have to answer what it means after a hand edit.

using System.Collections.Generic;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// What a freshly dropped load draws — <b>1 mA, the figure the report asked for</b>. Small enough that
    /// nobody mistakes it for a measured operating point, large enough that every drop, every
    /// current density and every breakdown row on the report is non-zero and the picture has
    /// something in it.
    /// </summary>
    public const double SeededLoadCurrentA = 1e-3;

    /// <summary>
    /// What a freshly added source holds a rail at where the rail itself gives no better answer.
    /// <b>The rail's own <see cref="RailSpec.NominalVoltageV"/> is preferred</b> — a second source on
    /// a 1V8 rail is a second branch of the same rail, and seeding it at 3.3 V would make the two
    /// branches fight in a way that looks like a result.
    /// </summary>
    public const double SeededSourceVoltageV = 3.3;

    /// <summary>The rows railRF filled in, by reference identity — see this file's header.</summary>
    private readonly HashSet<object> _seededRows = new(ReferenceEqualityComparer.Instance);

    /// <summary>A source row for <paramref name="rail"/>, carrying a voltage and remembered as seeded.</summary>
    private RailSource NewSeededSource(RailSpec rail, RailPortAnchor? anchor = null)
    {
        var source = new RailSource
        {
            Anchor = anchor ?? new RailPortAnchor(),
            OpenCircuitVoltageV = rail.NominalVoltageV ?? SeededSourceVoltageV,
        };
        _seededRows.Add(source);
        return source;
    }

    /// <summary>A load row carrying a current and remembered as seeded.</summary>
    private RailLoad NewSeededLoad(RailPortAnchor? anchor = null)
    {
        var load = new RailLoad
        {
            Anchor = anchor ?? new RailPortAnchor(),
            DcCurrentA = SeededLoadCurrentA,
        };
        _seededRows.Add(load);
        return load;
    }

    /// <summary>
    /// How many rows across the whole document still hold the value railRF put there — <b>what the
    /// status strip says</b>, so a run built entirely on seeded numbers can never be silent about it.
    /// </summary>
    /// <remarks>
    /// Counted off the DOCUMENT and not off the set, so a row that was edited (a new record) or
    /// removed (not in any list) stops counting with no bookkeeping at the edit site.
    /// </remarks>
    public int SeededRowCount
    {
        get
        {
            int n = 0;
            foreach (var rail in _document.Rails)
            {
                foreach (var source in rail.Sources) if (_seededRows.Contains(source)) n++;
                foreach (var load in rail.Loads)     if (_seededRows.Contains(load))   n++;
            }
            return n;
        }
    }

    /// <summary>The strip's own phrase, or "" where nothing is seeded.</summary>
    internal string SeededRowsText => SeededRowCount switch
    {
        0 => "",
        1 => "1 row still holds railRF's starting value",
        var n => $"{n} rows still hold railRF's starting values",
    };
}
