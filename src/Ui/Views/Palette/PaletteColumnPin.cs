using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Dock.Controls.ProportionalStackPanel;

namespace CircuitRF.Ui.Views.Palette;

/// <summary>
/// Keeps a docked Library palette showing the number of component-glyph columns it is showing now,
/// whatever the user has dragged that to, whatever happens to the workspace window around it.
///
/// <para><b>Why this is not just a better default number.</b> Dock sizes a column as a FRACTION of
/// the window, so a palette that is two glyphs wide at 1200 px is two and a half at 1500 and three
/// at 1800 — the glyphs reflow at whole slots, so the extra width is not a wider palette, it is a
/// strip of empty space that grows and then jumps to another column. The fraction has to be
/// recomputed from the pixel width every time the pool it divides changes, which is what this
/// does.</para>
///
/// <para><b>The count is read off the palette, never assumed</b> — whatever whole number of glyph
/// columns the tile area is showing (<see cref="PaletteColumnWidth.GlyphColumnsIn"/>), so dragging
/// the splitter to a three- or five-glyph palette is all it takes to make the window keep it at
/// three or five. Only a palette too narrow to show one whole column has no count to preserve, and
/// that one is left to scale as it always did.</para>
///
/// <para><b>The count changes only while the SPLITTER IS UNDER THE POINTER, and that is the whole of
/// it</b> (owner, 2026-09-21: resizing the workspace window could still change how many columns the
/// docked Library showed). The count is a LATCH — once it reads one, the pin *enforces* one, and
/// widening the window back does not bring the second column back — so the question "has the user
/// chosen a new width?" must be answered by the pointer, not inferred from the layout. It used to be
/// inferred: a layout pass in which the pool had not changed was taken for a splitter drag, and any
/// pass that moved the tile area for some other reason was therefore read as a narrower palette the
/// user had asked for. Reproduced headlessly with a scrollbar that reserves its column rather than
/// floating: shortening the window makes the bar appear, the tile area loses the bar's width on a
/// pass the window's own width did not move, and a three-column palette latches two. Every other
/// pass that can move that width — a theme or density change, a font change, a scroller appearing —
/// is the same bug wearing different clothes, which is why the fix is the gate and not a special
/// case for scrollbars.</para>
///
/// <para><b>Outside a drag the pin simply holds the width, on every pass.</b> It does not ask whether
/// the window moved: it asks whether the column is the width the count calls for, and re-proportions
/// it when it is not. That is what makes it robust to a pass nobody predicted — including one where
/// the chrome around the tile area changes, since <see cref="PaletteColumnWidth.TargetWidth"/> is
/// measured chrome plus whole glyph slots, so the tile area comes back to the same N slots and the
/// count is still the count. <b>Nothing is applied DURING a drag</b>, which is the one thing this
/// must not break: a pin that snapped back mid-drag would peg the splitter to whole glyph slots and
/// make every width between them unreachable. The consequence, and it is deliberate: a palette let
/// go of at four columns and a bit tightens to exactly four, because the bit is a strip no glyph was
/// in.</para>
///
/// <para><b>The window's ClientSize is a trigger as well as the layout that follows it.</b> Avalonia
/// publishes the new client size and then runs the layout pass, so re-proportioning there lands
/// before anything is arranged and the palette never flashes at the scaled width — which it would if
/// the correction were only made after the fact, most visibly on a maximise, where one frame's worth
/// of scaling is the whole jump. The prediction it acts on is kept apart from <c>_pool</c>, which is
/// only ever a MEASUREMENT: several ClientSize changes can arrive between two layout passes (a fast
/// drag, and a Debug build where layout is the slower half), so the deltas accumulate against the
/// prediction rather than each being added to a measurement that is by then several steps stale.
/// <see cref="OnPanelLayoutUpdated"/> then re-applies against the width the pass actually
/// produced.</para>
/// </summary>
public sealed class PaletteColumnPin
{
    // One pin per workspace window. A tear-off host window holds a PaletteToolView too and has no
    // pin registered, which is exactly right: a floated palette has no column to size.
    private static readonly ConditionalWeakTable<TopLevel, PaletteColumnPin> Pins = new();

    private readonly Window _window;

    private ProportionalStackPanel? _panel;   // the row of columns
    private ContentPresenter?       _column;  // the palette's column within it
    private PaletteToolView?        _view;    // what measures the chrome

    private bool   _settled;                  // a usable measurement has been seen for this arrangement
    private int    _columns;                  // glyph columns the palette is showing; 0 = nothing to hold
    private double _chrome;                   // column width less tile-area width, as last measured
    private double _pool;                     // the panel width proportions divide up — MEASURED, only ever
    private double _predicted;                // that width as predicted since the last pass; 0 = none pending
    private bool   _dragging;                 // a splitter is under the pointer: this width is the user's
    private bool   _readAfterDrag;            // take the width they let go of, once
    private double _clientWidth;

    private PaletteColumnPin(Window window)
    {
        _window      = window;
        _clientWidth = window.ClientSize.Width;
        window.PropertyChanged += OnWindowPropertyChanged;
    }

    /// <summary>Arms the pin for one workspace window. Idempotent.</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Pins.TryGetValue(window, out _)) return;
        Pins.Add(window, new PaletteColumnPin(window));
    }

    // ── What the palette tells us ─────────────────────────────────────────────
    //
    //  The view reports itself rather than being searched for. A search would have to run on every
    //  layout pass to notice a rebuilt dock tree, and walking the whole dock's visual tree that often
    //  — in the arrangements where the palette is a background tab and the search finds nothing at
    //  all — is a real cost for no result.

    internal static void NotifyPaletteAttached(PaletteToolView view)
    {
        if (TopLevel.GetTopLevel(view) is { } top && Pins.TryGetValue(top, out var pin)) pin.Adopt(view);
    }

    internal static void NotifyPaletteDetached(PaletteToolView view)
    {
        // The view is usually already off its root by now, so the window lookup often misses — the
        // sweep is what actually drops it. Cheap: one pin per open workspace window.
        if (TopLevel.GetTopLevel(view) is { } top && Pins.TryGetValue(top, out var owner)) owner.Release(view);
        else foreach (var (_, pin) in Pins) pin.Release(view);
    }

    private void Adopt(PaletteToolView view)
    {
        Release(_view);
        _view = view;

        // The column is the nearest ancestor that is a child of a row of columns. Found by walking UP
        // from the palette, which is a handful of steps, and which also settles the two arrangements
        // the palette can be in: its own right-hand column, or sharing the left column with the
        // Project Tree. In the second it will simply never be two glyphs wide, and never arm.
        for (Visual? v = view; v is not null; v = v.GetVisualParent())
        {
            if (v is ContentPresenter presenter
                && presenter.GetVisualParent() is ProportionalStackPanel { Orientation: Orientation.Horizontal } row)
            {
                _column = presenter;
                _panel  = row;
                _panel.LayoutUpdated += OnPanelLayoutUpdated;

                // The pointer, not the layout, is what says the user is choosing a width. Tunnel AND
                // bubble with handledEventsToo, because the splitter handles its own press and marks
                // it; a press that never reaches a handler is a drag the pin would apply over.
                _panel.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
                                  RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                _panel.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
                                  RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

                // A pointer capture lost without a release — the pointer leaves the window mid-drag —
                // is raised DIRECTLY on the splitter and never routes through the panel, so it is
                // taken there. Without it a drag that ended that way would leave the pin suspended
                // until the next press anywhere in the row.
                foreach (var splitter in SplittersIn(row))
                    splitter.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost,
                                        RoutingStrategies.Direct, handledEventsToo: true);
                break;
            }
        }

        _settled       = false;
        _columns       = 0;
        _pool          = 0.0;
        _predicted     = 0.0;
        _dragging      = false;
        _readAfterDrag = false;
    }

    private void Release(PaletteToolView? view)
    {
        if (view is not null && !ReferenceEquals(view, _view)) return;
        if (_panel is not null)
        {
            _panel.LayoutUpdated -= OnPanelLayoutUpdated;
            _panel.RemoveHandler(InputElement.PointerPressedEvent,  OnPointerPressed);
            _panel.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
            foreach (var splitter in SplittersIn(_panel))
                splitter.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
        }
        _panel         = null;
        _column        = null;
        _view          = null;
        _settled       = false;
        _columns       = 0;
        _predicted     = 0.0;
        _dragging      = false;
        _readAfterDrag = false;
    }

    // ── Is the user choosing a width? ─────────────────────────────────────────

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        => _dragging = IsOnSplitter(e.Source as Visual);

    // Any release ends the drag, not only one on the splitter: the pointer can be let go anywhere
    // once the splitter has captured it, and a latch left standing would suspend the pin.
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _dragging = false;

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _dragging = false;

    private static bool IsOnSplitter(Visual? source)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
            if (v is ProportionalStackPanelSplitter) return true;
        return false;
    }

    private static IEnumerable<ProportionalStackPanelSplitter> SplittersIn(ProportionalStackPanel panel)
        => panel.Children
                .Select(c => c as ProportionalStackPanelSplitter
                          ?? (c as ContentPresenter)?.Child as ProportionalStackPanelSplitter)
                .Where(s => s is not null)
                .Select(s => s!);

    // ── The two triggers ──────────────────────────────────────────────────────

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TopLevel.ClientSizeProperty) return;

        double width = _window.ClientSize.Width;
        double delta = width - _clientWidth;
        _clientWidth = width;

        if (_columns <= 0 || !_settled || _dragging || _panel is null || Math.Abs(delta) < 0.5) return;

        // The pool has not been re-measured yet — the layout pass this change is about to start is
        // what does that. Every column in the row is inside the window and none of the chrome around
        // them is elastic, so the pool moves with the window pixel for pixel; OnPanelLayoutUpdated
        // re-applies against the answer once the pass has run.
        //
        // The prediction is kept apart from _pool, which is only ever a MEASUREMENT, and the deltas
        // accumulate here because several ClientSize changes can arrive between two layout passes.
        double pool = (_predicted > 0.0 ? _predicted : _pool) + delta;
        if (pool <= 0.0) return;

        _predicted = pool;
        Apply(pool);
    }

    private void OnPanelLayoutUpdated(object? sender, EventArgs e)
    {
        if (_panel is null || _column is null || _view is null) return;

        double pool        = PoolOf(_panel);
        double columnWidth = _column.Bounds.Width;
        double tileArea    = _view.TileAreaWidth;
        if (!(pool > 0.0) || !(columnWidth > 0.0) || !(tileArea > 0.0)) return;

        _chrome    = columnWidth - tileArea;
        _pool      = pool;
        _predicted = 0.0;

        if (!_settled)
        {
            // First measurable layout of a NEW arrangement, and the only place the pending count is
            // taken. Two things land here:
            //
            //  - Reset Layout, and choosing a Window Layout, mean "the shipped arrangement, now", and
            //    the shipped palette is DefaultGlyphColumns wide whatever size the window has been
            //    dragged to since. The layout itself carries only a FRACTION, which is that many
            //    glyphs at the window's OPENING size and one more on a window since widened — so the
            //    count travels separately and is NOT read off the panel here.
            //
            //  - Everything else: read the count off the panel. A brand-new workspace becomes exactly
            //    two glyphs wide this way, the shipped proportion having opened within a pixel of it.
            //
            // **The request is taken only in this branch, and that is load-bearing.** It is raised
            // while the OUTGOING tree is still up, and a settled pin gets one more layout pass before
            // its panel is replaced — consuming there honoured it on the arrangement about to be
            // thrown away, and the new one then read three columns off its own fresh fraction. The
            // latch belongs to the arrangement that has not measured yet.
            _settled = true;
            _columns = _view.ConsumeDefaultWidthRequest()
                     ? PaletteColumnWidth.DefaultGlyphColumns
                     : PaletteColumnWidth.GlyphColumnsIn(tileArea);
            if (_columns > 0) Apply(pool);
            return;
        }

        if (_dragging)
        {
            // The user has the splitter. Whatever they are landing on is the count to keep, and
            // nothing is applied — see the class note on why a pin that snapped back mid-drag would
            // be a bug.
            _columns       = PaletteColumnWidth.GlyphColumnsIn(tileArea);
            _readAfterDrag = true;
            return;
        }

        if (_readAfterDrag)
        {
            // The first pass after they let go: the width they left it at is the one to keep.
            _readAfterDrag = false;
            _columns       = PaletteColumnWidth.GlyphColumnsIn(tileArea);
        }

        // Not a drag, so this width is not a choice — hold the count, whatever moved. Apply writes
        // nothing when the column is already within half a pixel of its target, so an untouched
        // window costs one comparison per layout pass and no layout of its own.
        if (_columns > 0) Apply(pool);
    }

    // ── Doing it ──────────────────────────────────────────────────────────────

    private void Apply(double pool)
    {
        if (_panel is null || _column is null) return;

        var columns = _panel.Children.Where(c => !IsSplitter(c)).ToList();
        int index   = columns.IndexOf(_column);
        if (index < 0) return;

        double target  = PaletteColumnWidth.TargetWidth(_chrome, _columns);
        var    current = columns.Select(ProportionalStackPanel.GetProportion).ToArray();

        // TryPin refuses a row with no room for the target as well as a column already at it: the
        // palette then stays at its scaled share of a too-narrow window, which is not a count anybody
        // chose — and is not read as one either, since nothing is read outside a drag.
        if (!PaletteColumnWidth.TryPin(current, index, pool, target, out var pinned)) return;

        // Set on the PRESENTER, at local value, which is where Dock itself writes a resize: the
        // theme binds the presenter's proportion two-way to the dockable's, so this still reaches the
        // model and is still what gets saved into the .cws. Writing the dockable instead would not
        // move the layout at all — Dock's own arrange has already written a local value here, and a
        // local value outranks the style-priority binding that would carry a model change back.
        for (int i = 0; i < columns.Count; i++)
            if (Math.Abs(pinned[i] - current[i]) > 0.0)
                ProportionalStackPanel.SetProportion(columns[i], pinned[i]);
    }

    /// <summary>The width proportions divide up: the row less the splitters between its columns.</summary>
    private static double PoolOf(ProportionalStackPanel panel)
        => panel.Bounds.Width - panel.Children.Where(IsSplitter).Sum(c => c.Bounds.Width);

    /// <summary>
    /// Dock wraps every child of the row in a <see cref="ContentPresenter"/>, splitters included, so
    /// a splitter is recognised by what is inside one. (Dock's own predicate is internal.)
    /// </summary>
    private static bool IsSplitter(Control control)
        => control is ProportionalStackPanelSplitter
        || control is ContentPresenter { Child: ProportionalStackPanelSplitter };
}
