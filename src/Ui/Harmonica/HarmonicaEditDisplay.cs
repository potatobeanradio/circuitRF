// ================================================================
//  HarmonicaEditDisplay.cs  —  M3 of brief-harmonicarf-h7
//
//  R-h7-8   unlocking flips CharmLayout.Locked and nothing else changes. R-h45-1 put the layout in
//           fractions FOR THIS MILESTONE — "H7 only has to flip Locked and start writing to the same
//           field" — so this type writes CharmLayout and never replaces it.
//  R-h7-9   the undo stack is .cdd's own (UndoRedoManager / IUndoableCommand), not a second one.
//  R-h7-10  a degenerate placement must not be CREATABLE, because CharmLayout already drops one on
//           read and the next load would silently discard it. CLAMPED at drag time — see MinimumSpan.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.Harmonica;

/// <summary>What an Edit Display pointer-down landed on.</summary>
public enum HarmonicaEditGrab { None, Move, Resize }

/// <summary>
/// The Edit Display hit test — which panel, and whether by its body or its resize grip.
///
/// <para>Separate from <see cref="HarmonicaHitTest"/> because it answers a different question over a
/// different set: every panel in the layout, not the markers on two of them. It shares the one
/// discipline that matters — <b>everything is computed per call</b> and the grip is a DEVICE-pixel
/// size divided by the render scaling each time (R-h6-2's rule, for the same reason).</para>
/// </summary>
public static class HarmonicaEditTarget
{
    /// <summary>The resize grip's size, in DEVICE pixels.</summary>
    public const double GripDevicePixels = 12.0;

    /// <summary>Every panel Edit Display can move: the five §7.1 ones plus one per picked trace.</summary>
    public static IReadOnlyList<string> PanelIds(
        CharmLayout layout, IReadOnlyList<HarmonicaPickedTrace> picked)
    {
        var ids = new List<string>();
        foreach (var p in CharmLayout.DefaultPanels) ids.Add(p.PanelId);
        foreach (var t in picked) if (!ids.Contains(t.PanelId)) ids.Add(t.PanelId);
        // A layout may also carry a panel neither list names — a trace removed from a newer document
        // and reopened in an older one. It is still on screen, so it is still editable.
        foreach (var p in layout.Panels) if (!ids.Contains(p.PanelId)) ids.Add(p.PanelId);
        return ids;
    }

    /// <summary>
    /// Resolves a canvas point to a panel. <b>Topmost first</b> — picked traces are drawn over the
    /// §7.1 panels, so they are tested first, or a trace dropped onto a Smith chart could never be
    /// picked up again.
    /// </summary>
    public static (HarmonicaEditGrab Kind, string PanelId) Resolve(
        CharmLayout layout, IReadOnlyList<HarmonicaPickedTrace> picked,
        double x, double y, double w, double h, double renderScaling = 1.0)
    {
        if (w <= 0 || h <= 0) return (HarmonicaEditGrab.None, "");

        double grip = GripDevicePixels / Math.Max(1e-9, renderScaling);
        var ids = PanelIds(layout, picked);

        for (int i = ids.Count - 1; i >= 0; i--)
        {
            var p = layout.PlacementOf(ids[i]);
            double px = p.X * w, py = p.Y * h, pw = p.W * w, ph = p.H * h;
            if (x < px || x >= px + pw || y < py || y >= py + ph) continue;

            bool onGrip = x >= px + pw - grip && y >= py + ph - grip;
            return (onGrip ? HarmonicaEditGrab.Resize : HarmonicaEditGrab.Move, ids[i]);
        }
        return (HarmonicaEditGrab.None, "");
    }
}

/// <summary>
/// §7.7's Edit Display mode, over <see cref="CharmLayout"/>.
///
/// <para><b>What is reused and what is not, stated plainly.</b> The undo machinery IS <c>.cdd</c>'s —
/// <see cref="UndoRedoManager"/> and <see cref="IUndoableCommand"/> from
/// <c>src/Ui/DataDisplay/Models/UndoRedo.cs</c>, unchanged. The placement model is <b>not</b>
/// <c>PlotContainerViewModel</c>'s: that type positions a plot in CANVAS PIXELS against
/// <c>DataDisplayViewModel</c>'s own zoom/pan, and adopting it would mean replacing
/// <see cref="CharmLayout"/> — exactly what R-h7-8 says has gone wrong. A harmonicaRF panel is a
/// FRACTION of the document area, by R-h45-1's own design, so what moves here is
/// <see cref="CharmPanelPlacement"/>.</para>
///
/// <para><b>One undo entry per gesture, not per pointer move.</b> A drag calls
/// <see cref="BeginGesture"/> once, mutates freely, and calls <see cref="EndGesture"/> once; the
/// single command carries the before/after layout. This is the counter-assertable shape the PCell
/// drags already use.</para>
/// </summary>
public sealed class HarmonicaEditDisplay
{
    private readonly Func<CharmLayout>      _get;
    private readonly Action<CharmLayout>    _set;

    private CharmLayout? _gestureStart;

    public HarmonicaEditDisplay(Func<CharmLayout> get, Action<CharmLayout> set)
    {
        _get = get ?? throw new ArgumentNullException(nameof(get));
        _set = set ?? throw new ArgumentNullException(nameof(set));
    }

    /// <summary>
    /// The smallest fraction of the document a panel may occupy in either axis. R-h7-10's clamp:
    /// <b>a drag cannot produce a placement the next load would drop.</b> Chosen over refusing the
    /// drop because a refused drop leaves the pointer holding something with nowhere to put it, and
    /// the user has no way to tell a refusal from a frozen UI.
    /// </summary>
    public const double MinimumSpan = 0.05;

    /// <summary><c>.cdd</c>'s own undo manager (R-h7-9). Not a second implementation.</summary>
    public UndoRedoManager Undo { get; } = new();

    /// <summary>§7.7's lock. Flipping it writes <see cref="CharmLayout.Locked"/> and does nothing
    /// else — the placements are untouched by locking and unlocking alone.</summary>
    public bool Unlocked
    {
        get => !_get().Locked;
        set
        {
            var layout = _get();
            if (!layout.Locked == value) return;                 // already in the wanted state

            // Re-locking restores §7.1's default arrangement: "locked" is the shipped layout, and a
            // lock that kept a user's arrangement would leave no way back to the default at all.
            _set(value
                ? layout with { Locked = false }
                : CharmLayout.Default);

            Undo.Clear();      // the placements the stack described no longer exist
            UnlockedChanged?.Invoke();
        }
    }

    public event Action? UnlockedChanged;

    /// <summary>Raised after any layout mutation, including an undo or redo.</summary>
    public event Action? LayoutChanged;

    // ── gestures ──────────────────────────────────────────────────────────────

    /// <summary>Starts a gesture. Every mutation until <see cref="EndGesture"/> collapses into ONE
    /// undo entry.</summary>
    public void BeginGesture() => _gestureStart ??= _get();

    /// <summary>
    /// Ends a gesture, pushing one entry if anything actually moved. A gesture that put everything
    /// back where it started pushes nothing — an undo entry that undoes nothing is worse than none.
    /// </summary>
    public bool EndGesture()
    {
        var before = _gestureStart;
        _gestureStart = null;
        if (before is null) return false;

        var after = _get();
        if (SameLayout(before, after)) return false;

        Undo.Push(new LayoutChange(this, before, after));
        return true;
    }

    /// <summary>Abandons a gesture, restoring the layout it started from.</summary>
    public void CancelGesture()
    {
        if (_gestureStart is not { } before) return;
        _gestureStart = null;
        _set(before);
        LayoutChanged?.Invoke();
    }

    // ── the mutations §7.7 offers ─────────────────────────────────────────────

    /// <summary>Moves a panel by a fraction of the document, keeping it wholly inside it.</summary>
    public bool MovePanel(string panelId, double dx, double dy)
        => Write(panelId, p =>
        {
            double x = Math.Clamp(p.X + dx, 0.0, 1.0 - p.W);
            double y = Math.Clamp(p.Y + dy, 0.0, 1.0 - p.H);
            return p with { X = x, Y = y };
        });

    /// <summary>
    /// Resizes a panel to a fraction of the document. R-h7-10 — the span is CLAMPED to
    /// <see cref="MinimumSpan"/> and to the document edge, so a drag toward zero width stops at the
    /// minimum instead of committing something the next load would discard.
    /// </summary>
    public bool ResizePanel(string panelId, double w, double h)
        => Write(panelId, p => p with
        {
            W = Math.Clamp(w, MinimumSpan, 1.0 - p.X),
            H = Math.Clamp(h, MinimumSpan, 1.0 - p.Y),
        });

    /// <summary>Places a panel outright — what a drop commits.</summary>
    public bool PlacePanel(string panelId, double x, double y, double w, double h)
        => Write(panelId, _ =>
        {
            double cw = Math.Clamp(w, MinimumSpan, 1.0);
            double ch = Math.Clamp(h, MinimumSpan, 1.0);
            return new CharmPanelPlacement(panelId,
                Math.Clamp(x, 0.0, 1.0 - cw), Math.Clamp(y, 0.0, 1.0 - ch), cw, ch);
        });

    /// <summary>Removes a panel from the layout. The §7.1 default is what
    /// <see cref="CharmLayout.PlacementOf"/> falls back to, so a removed panel is genuinely absent
    /// from the file rather than present at a degenerate size.</summary>
    public bool RemovePanel(string panelId)
    {
        if (!Unlocked) return false;
        var layout = _get();
        var kept = layout.Panels.Where(p => p.PanelId != panelId).ToList();
        if (kept.Count == layout.Panels.Count) return false;

        _set(layout with { Panels = kept });
        LayoutChanged?.Invoke();
        return true;
    }

    /// <summary>Adds a panel back at §7.1's own default placement.</summary>
    public bool AddPanel(string panelId)
    {
        if (!Unlocked) return false;
        var layout = _get();
        if (layout.Panels.Any(p => p.PanelId == panelId)) return false;

        var placement = CharmLayout.DefaultPanels.FirstOrDefault(p => p.PanelId == panelId);
        if (placement.PanelId is null) return false;

        _set(layout with { Panels = [.. layout.Panels, placement] });
        LayoutChanged?.Invoke();
        return true;
    }

    private bool Write(string panelId, Func<CharmPanelPlacement, CharmPanelPlacement> mutate)
    {
        if (!Unlocked) return false;

        var layout = _get();
        var panels = layout.Panels.ToList();
        int idx = panels.FindIndex(p => p.PanelId == panelId);

        // A panel the file never placed is still editable: it is drawn at §7.1's default, so that is
        // what an edit starts from. Without this, moving an untouched panel would silently do nothing.
        if (idx < 0)
        {
            var seed = CharmLayout.DefaultPanels.FirstOrDefault(p => p.PanelId == panelId);
            if (seed.PanelId is null) return false;
            panels.Add(seed);
            idx = panels.Count - 1;
        }

        var next = mutate(panels[idx]);
        if (next.W < MinimumSpan || next.H < MinimumSpan) return false;   // belt to the clamp's braces
        if (next == panels[idx]) return false;

        panels[idx] = next;
        _set(layout with { Panels = panels });
        LayoutChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Half a millionth of the document — sub-pixel on any display, and far below anything a pointer
    /// can express.
    ///
    /// <para><b>An epsilon rather than exact equality, and that is not laziness.</b> A drag out and
    /// back accumulates float error: <c>0.65 + 0.05 − 0.05</c> is <c>0.6500000000000001</c>, so exact
    /// comparison would push an undo entry for a move nobody made and nobody can see. Undoing an
    /// invisible change is worse than not offering to.</para>
    /// </summary>
    private const double PlacementEpsilon = 5e-7;

    private static bool SameLayout(CharmLayout a, CharmLayout b)
    {
        if (a.Locked != b.Locked || a.Panels.Count != b.Panels.Count) return false;
        for (int i = 0; i < a.Panels.Count; i++)
        {
            var x = a.Panels[i];
            var y = b.Panels[i];
            if (x.PanelId != y.PanelId) return false;
            if (Math.Abs(x.X - y.X) > PlacementEpsilon) return false;
            if (Math.Abs(x.Y - y.Y) > PlacementEpsilon) return false;
            if (Math.Abs(x.W - y.W) > PlacementEpsilon) return false;
            if (Math.Abs(x.H - y.H) > PlacementEpsilon) return false;
        }
        return true;
    }

    /// <summary>One gesture's worth of layout change, as a <c>.cdd</c> undoable command.</summary>
    private sealed class LayoutChange(HarmonicaEditDisplay owner, CharmLayout before, CharmLayout after)
        : IUndoableCommand
    {
        public void Execute() { owner._set(after);  owner.LayoutChanged?.Invoke(); }
        public void Undo()    { owner._set(before); owner.LayoutChanged?.Invoke(); }
    }
}
