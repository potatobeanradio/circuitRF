namespace CircuitRF.WBond;

/// <summary>How much fidelity a drag frame gets (wbond.md WB15).</summary>
public enum DragQuality
{
    /// <summary>Full geometry, exact incremental fill. The readout is final.</summary>
    Exact,

    /// <summary>
    /// Each moving wire represented by its CHORD — one filament instead of six or seven. The readout
    /// is provisional and the panel says so.
    /// </summary>
    Chord,

    /// <summary>
    /// No refill at all: the last value is held while the canvas keeps up, and the exact answer is
    /// computed on mouse-up.
    /// </summary>
    FreezeAndSnap,
}

/// <summary>
/// Decides how much fidelity a drag frame can afford (WB15), <b>from measured frame times rather than
/// from a cost model</b>.
///
/// <h3>Why feedback and not a formula — this was measured, not assumed</h3>
/// <para>The obvious design is to predict a frame's cost from the number of moving wires and the
/// total, and pick a rung. <b>That formula cannot be calibrated.</b> The two measurements available
/// disagree by roughly 2× on cost per wire-pair block:</para>
/// <list type="bullet">
/// <item>one wire of 600 — 600 blocks, 2.34 ms fill, i.e. ~3.9 µs/block;</item>
/// <item>200 wires of 200 — 20,100 blocks, 159 ms total, i.e. ~7.9 µs/block.</item>
/// </list>
/// <para>The gap is real: past <c>k &gt; N/12</c> the incremental path stops rank-2 updating the
/// Cholesky factor and refactorises instead, and the fill's cache behaviour changes with the shape of
/// the block set. A predictor fitted to either point is wrong at the other by 2–3×, which would put
/// the ladder on the wrong rung exactly where it matters.</para>
///
/// <para>So the ladder <b>observes</b>: it runs a frame, measures it, and steps. That needs no model
/// of the machine, survives a faster or slower one, and is the same mechanism harmonicaRF's frame
/// scheduler already uses.</para>
///
/// <h3>Degrade the GEOMETRY, not the algebra</h3>
/// <para>Representing a moving wire by its chord is a <b>36×</b> reduction in filament pairs (6
/// filaments become 1, and pairs go as the square), and it keeps every wire in the matrix so the
/// array reduction stays meaningful. Dropping wires instead would change which mutuals exist and make
/// the readout jump as the ladder engaged — the one thing a live readout must not do.</para>
/// </summary>
public sealed class QualityLadder
{
    /// <summary>The frame budget, milliseconds — 60 fps.</summary>
    public const double FrameBudgetMs = 1000.0 / 60.0;

    /// <summary>
    /// Below this fraction of the budget the ladder steps back up.
    ///
    /// <para>Hysteresis, and it is not optional: stepping up the moment a frame fits makes the ladder
    /// oscillate between two rungs on every frame, which is far more visible than simply staying one
    /// rung low.</para>
    /// </summary>
    public const double StepUpFraction = 0.5;

    /// <summary>Chord representation collapses ~6 filaments to 1, and pair count goes as the square.</summary>
    public const double ChordSpeedup = 36.0;

    private readonly double _budgetMs;
    private int _consecutiveComfortable;

    public QualityLadder(double budgetMs = FrameBudgetMs)
    {
        if (budgetMs <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(budgetMs), budgetMs, "The frame budget must be positive.");
        _budgetMs = budgetMs;
    }

    /// <summary>The rung the next frame should use.</summary>
    public DragQuality Current { get; private set; } = DragQuality.Exact;

    /// <summary>The last frame time fed in, milliseconds.</summary>
    public double LastFrameMs { get; private set; }

    /// <summary>True when the current rung's readout must be shown as provisional.</summary>
    public bool IsProvisional => Current != DragQuality.Exact;

    /// <summary>
    /// Feeds in a completed frame's measured time and returns the rung for the next one.
    ///
    /// <para>Steps down immediately when a frame overruns — a user feels one slow frame — and steps
    /// up only after <see cref="StepUpAfterComfortableFrames"/> comfortable ones, so the ladder
    /// cannot oscillate.</para>
    /// </summary>
    public DragQuality Observe(double frameMs)
    {
        LastFrameMs = frameMs;

        if (frameMs > _budgetMs)
        {
            _consecutiveComfortable = 0;
            Current = Current switch
            {
                DragQuality.Exact => DragQuality.Chord,
                DragQuality.Chord => DragQuality.FreezeAndSnap,
                _ => DragQuality.FreezeAndSnap,
            };
            return Current;
        }

        if (frameMs <= _budgetMs * StepUpFraction)
        {
            _consecutiveComfortable++;
            if (_consecutiveComfortable >= StepUpAfterComfortableFrames)
            {
                _consecutiveComfortable = 0;
                Current = Current switch
                {
                    DragQuality.FreezeAndSnap => DragQuality.Chord,
                    DragQuality.Chord => DragQuality.Exact,
                    _ => DragQuality.Exact,
                };
            }
            return Current;
        }

        // Inside budget but not comfortably: hold this rung.
        _consecutiveComfortable = 0;
        return Current;
    }

    /// <summary>How many comfortable frames it takes to earn a step back up.</summary>
    public const int StepUpAfterComfortableFrames = 3;

    /// <summary>
    /// Resets to the top rung. Called when a drag begins: every drag starts optimistic and finds its
    /// own level within a frame or two, rather than inheriting the last drag's verdict about a
    /// possibly quite different selection.
    /// </summary>
    public void BeginDrag()
    {
        Current = DragQuality.Exact;
        _consecutiveComfortable = 0;
        LastFrameMs = 0.0;
    }

    // ---------------------------------------------------------------- the degraded representation

    /// <summary>
    /// Replaces a wire's polyline with its chord, for the degraded rung.
    ///
    /// <para>Returns the original points so the caller can restore exact geometry on mouse-up — the
    /// degraded representation is a rendering-and-solving shortcut, never a destructive edit.</para>
    /// </summary>
    public static Point3[] CollapseToChord(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        var original = wire.Points.ToArray();
        if (original.Length <= 2) return original;

        wire.Points.Clear();
        wire.Points.Add(original[0]);
        wire.Points.Add(original[^1]);
        return original;
    }

    /// <summary>
    /// Restores the geometry captured by <see cref="CollapseToChord"/>, <b>carried onto wherever the
    /// chord has since been dragged to</b>.
    ///
    /// <h3>Restoring the captured points verbatim was the drag-slip bug</h3>
    /// <para>While the wire is collapsed the drag moves the only two points it has — its feet. Putting
    /// the captured array back unchanged therefore threw away every frame of motion that happened at
    /// the degraded rung: the wire sprang back to where it stood at the instant the ladder stepped
    /// down, while the cursor was somewhere else entirely. That is the owner's report (2026-08-16) —
    /// "drag them around the screen very fast, eventually the cursor will slip and my mouse will no
    /// longer be over the wire vertex I originally clicked on" — and the profile view's "glitch" is
    /// the same event seen from the other canvas, because the wire it draws jumps too.</para>
    ///
    /// <para>Fast dragging is exactly what makes it appear: the ladder is fed measured frame times, so
    /// it only degrades when frames overrun, and it only overruns when the pointer is generating more
    /// work than 60 fps allows.</para>
    ///
    /// <para>The interior points are re-placed by their own <b>chord parameter and height above the
    /// chord</b> — the same parameterisation <see cref="WireEdits.ScaleSpan"/> preserves — so a
    /// translate, a span scale and a rotate performed while collapsed all carry through with one
    /// rule. When the feet have NOT moved the captured array is put back byte-for-byte, so a collapse
    /// with no drag in between is still exactly reversible.</para>
    /// </summary>
    public static void RestoreFromChord(Wire wire, Point3[] original)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(original);

        // Nothing to map onto (the collapse was a no-op, or the wire has been restructured under us).
        if (original.Length <= 2 || wire.Points.Count < 2)
        {
            wire.Points.Clear();
            wire.Points.AddRange(original);
            return;
        }

        Point3 oldStart = original[0], oldEnd = original[^1];
        Point3 newStart = wire.Points[0], newEnd = wire.Points[^1];

        if (oldStart == newStart && oldEnd == newEnd)
        {
            wire.Points.Clear();
            wire.Points.AddRange(original);
            return;
        }

        wire.Points.Clear();
        wire.Points.Add(newStart);

        for (int i = 1; i < original.Length - 1; i++)
        {
            double s = WireEdits.ChordParameter(oldStart, oldEnd, original[i]);
            long oldChordZ = oldStart.Z + (long)Math.Round((oldEnd.Z - oldStart.Z) * s);
            long height = original[i].Z - oldChordZ;

            long x = newStart.X + (long)Math.Round((newEnd.X - newStart.X) * s);
            long y = newStart.Y + (long)Math.Round((newEnd.Y - newStart.Y) * s);
            long chordZ = newStart.Z + (long)Math.Round((newEnd.Z - newStart.Z) * s);

            wire.Points.Add(new Point3(x, y, chordZ + height));
        }

        wire.Points.Add(newEnd);
    }
}
