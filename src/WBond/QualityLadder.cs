namespace CircuitRF.WBond;

/// <summary>How much fidelity a drag frame gets (wbond.md WB15).</summary>
public enum DragQuality
{
    /// <summary>Full geometry, exact incremental fill. The readout is live and final.</summary>
    Exact,

    /// <summary>
    /// No refill at all: the geometry moves and the canvas redraws, the panel holds its last numbers,
    /// and the exact answer is computed on mouse-up.
    ///
    /// <para><b>This rung is unconditionally cheap</b>, which is what makes "the drag always stays
    /// smooth" a guarantee rather than a hope.</para>
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
/// <h3>The middle rung is gone, and its own argument is why (2026-08-18)</h3>
/// <para>There used to be a <c>Chord</c> rung between the two: each moving wire represented by its
/// chord, a <b>36×</b> reduction in filament pairs, keeping every wire in the matrix so the array
/// reduction "stayed meaningful". <b>It did not stay meaningful.</b> A 20 mil loop flattened to its
/// chord loses most of its loop area and reports its array inductance <b>~70 % low</b> — measured
/// 597 pH exact against 180 pH collapsed. The paragraph that stood here warned that dropping wires
/// "would make the readout jump as the ladder engaged — the one thing a live readout must not do",
/// and then did exactly that by another route: with the ladder stepping down on one slow frame and
/// back up after three comfortable ones, the panel alternated between those two numbers for the whole
/// drag (owner, 2026-08-18).</para>
///
/// <para>It was also the most expensive rung, not the middle one. Collapsing changes each wire's
/// POINT COUNT, so the flat filament layout no longer matches and the mesh has to be rebuilt — and
/// the drag path rebuilt it <b>every frame</b> while collapsed, at full cold-fill cost, rather than
/// once at the step-down. And it mutated geometry the canvas was drawing, so the wires visibly
/// straightened mid-drag.</para>
///
/// <para>So the ladder is now <b>Exact or frozen</b>. A rung that produces a number nobody should
/// look at is not a rung.</para>
///
/// <h3>Frame rate takes priority over the readout — always (owner, 2026-08-18)</h3>
/// <para><i>"Dragging 500 wires must always be fast, it should always take priority. We can give up
/// frame rate on the inductance calculation if necessary."</i> Two rules follow, and they are what
/// <see cref="BeginDrag(int,int)"/> and <see cref="CanAffordExactFill"/> implement.</para>
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

            // A rung that overran BADLY is not tried again for the rest of this drag. Feedback alone
            // would retry it every four frames, and at 500 wires one exact frame is seconds — so the
            // drag would hitch periodically forever. This is memory of a measurement, not a model.
            if (frameMs > _budgetMs * LockoutOverrunFactor) _lockedDown = true;

            Current = DragQuality.FreezeAndSnap;
            return Current;
        }

        if (frameMs <= _budgetMs * StepUpFraction)
        {
            _consecutiveComfortable++;
            if (_consecutiveComfortable >= StepUpAfterComfortableFrames && !_lockedDown)
            {
                _consecutiveComfortable = 0;
                Current = DragQuality.Exact;
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
    /// Resets for a new drag, with no idea what it will cost. Starts at the top rung.
    ///
    /// <para><b>Prefer <see cref="BeginDrag(int,int)"/>.</b> This overload starts optimistic, which
    /// means the first frame of a 500-wire drag is an exact fill costing seconds. It survives for
    /// callers that genuinely have no selection size to offer.</para>
    /// </summary>
    public void BeginDrag()
    {
        Current = DragQuality.Exact;
        _consecutiveComfortable = 0;
        _lockedDown = false;
        LastFrameMs = 0.0;
    }

    /// <summary>
    /// Resets for a new drag whose size is known, and <b>refuses to attempt a fill that obviously
    /// cannot fit</b>.
    ///
    /// <para><b>This is a BOUND, not the cost model WB15 rejected</b>, and the difference is the whole
    /// justification. WB15 rejected fitting a predictor to <i>choose among rungs</i>, because the two
    /// available measurements disagree by 2× on cost per wire-pair block (3.9 µs/block for one wire of
    /// 600, 7.9 µs/block for 200 of 200) and a predictor fitted to either is wrong at the other by
    /// 2–3×. That objection is fatal to a 2× decision and irrelevant to a <b>100×</b> one: dragging
    /// 500 wires of 500 is 250,000 blocks, i.e. ~2 s against a 16.7 ms budget, and no factor-of-two
    /// uncertainty changes that verdict. So this asks only the question a 2×-accurate bound can
    /// answer — <i>is this obviously hopeless?</i> — and hands everything else back to feedback.</para>
    ///
    /// <para>Without it, feedback has to <i>pay</i> one catastrophic frame to learn what the block
    /// count already says.</para>
    /// </summary>
    /// <param name="movingWires">How many wires this drag moves.</param>
    /// <param name="totalWires">How many wires there are in total.</param>
    public void BeginDrag(int movingWires, int totalWires)
    {
        BeginDrag();

        double estimate = EstimatedFillMs(movingWires, totalWires);
        if (estimate <= _budgetMs) return;

        // Over budget: start frozen rather than paying to find out.
        Current = DragQuality.FreezeAndSnap;

        // ...and only LOCK when it is hopeless by a wide margin. The bound is deliberately 2×
        // pessimistic (see MicrosecondsPerBlockUpperBound), so a drag that merely looks marginal must
        // still be allowed to prove itself — three comfortable frozen frames and the ladder tries it,
        // which costs ~50 ms of frozen readout and nothing else. A drag 60× over the budget gets no
        // such courtesy.
        if (estimate > _budgetMs * LockoutOverrunFactor) _lockedDown = true;
    }

    /// <summary>
    /// The wire-pair blocks an incremental fill recomputes when <paramref name="movingWires"/> of
    /// <paramref name="totalWires"/> move: <c>k·N − k(k−1)/2</c>.
    ///
    /// <para>One row per moved wire, less the intra-selection pairs already covered when the outer
    /// loop reached the other member — which is exactly what <c>IncrementalFill.MoveWires</c> skips.
    /// It reduces to N for one wire, matching the measured 600 blocks at N = 600.</para>
    /// </summary>
    public static long FillBlocks(int movingWires, int totalWires)
    {
        long k = Math.Clamp(movingWires, 0, Math.Max(totalWires, 0));
        long n = Math.Max(totalWires, 0);
        return k * n - k * (k - 1) / 2;
    }

    /// <summary>
    /// The <b>upper</b> bound on a wire-pair block, in microseconds.
    ///
    /// <para>Measured 3.9 µs/block (one wire of 600) and 7.9 µs/block (200 of 200) — see
    /// <see cref="BeginDrag(int,int)"/>. 8 is the pessimistic end, chosen deliberately: this constant
    /// only ever decides whether to ATTEMPT the exact fill, so overestimating costs a frozen readout
    /// on a drag that might have kept up, while underestimating costs a multi-second stall.</para>
    /// </summary>
    public const double MicrosecondsPerBlockUpperBound = 8.0;

    /// <summary>An upper bound on what an exact drag frame would cost, milliseconds.</summary>
    public static double EstimatedFillMs(int movingWires, int totalWires) =>
        FillBlocks(movingWires, totalWires) * MicrosecondsPerBlockUpperBound / 1000.0;

    /// <summary>Whether the exact fill is worth attempting at all for a drag of this size.</summary>
    public bool CanAffordExactFill(int movingWires, int totalWires) =>
        FitsInOneFrame(movingWires, totalWires, _budgetMs);

    /// <summary>
    /// Whether a fill of this size fits in one frame — the same bound
    /// <see cref="CanAffordExactFill"/> asks, reachable without a ladder.
    ///
    /// <para>The drag path is not the only thing that must not stall on a fill: <b>an undo has to put
    /// the wires back instantly too</b> (owner, 2026-08-18), and it has no ladder because it is not a
    /// gesture. Both ask the same question, so both ask it here.</para>
    /// </summary>
    public static bool FitsInOneFrame(int movingWires, int totalWires, double budgetMs = FrameBudgetMs) =>
        EstimatedFillMs(movingWires, totalWires) <= budgetMs;

    /// <summary>
    /// How far past the budget a frame has to land before its rung is abandoned for the rest of the
    /// drag. Four times: a frame that merely misses is worth retrying, one that misses by 4× is not.
    /// </summary>
    public const double LockoutOverrunFactor = 4.0;

    /// <summary>The frame budget this ladder measures against, milliseconds.</summary>
    public double BudgetMs => _budgetMs;

    /// <summary>
    /// True when the last frame left at least half the budget unused — the test the drag path uses
    /// before spending anything OPTIONAL, such as refreshing the capacitance.
    /// </summary>
    public bool HasHeadroom => LastFrameMs > 0.0 && LastFrameMs <= _budgetMs * StepUpFraction;

    /// <summary>True once a rung has overrun badly enough not to be retried in this drag.</summary>
    public bool IsLockedDown => _lockedDown;

    private bool _lockedDown;

    // ---------------------------------------------------------------- the degraded representation

    /// <summary>
    /// Replaces a wire's polyline with its chord.
    ///
    /// <para><b>No longer used by the drag path (2026-08-18)</b> — see the class note on why the Chord
    /// rung was removed. Kept, with <see cref="RestoreFromChord"/>, because the pair encodes a
    /// hard-won rule about carrying interior points onto moved feet that any future
    /// simplify-then-restore feature would otherwise rediscover the hard way.</para>
    ///
    /// <para>Returns the original points so the caller can restore exact geometry — this is a
    /// reversible shortcut, never a destructive edit.</para>
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
