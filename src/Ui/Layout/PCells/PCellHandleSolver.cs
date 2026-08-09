using System.Globalization;

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>Why a handle cannot be dragged. Every member degrades to "no grip, parameter still
/// editable in the Properties Inspector" — none of them blocks editing (R-pch-6, design §8).</summary>
public enum PCellHandleRejection
{
    None = 0,
    /// <summary>The handle names a parameter the generator was not given — R2's one list is the
    /// contract, and a handle naming something outside it is a defect in the cell, not a new
    /// parameter to invent.</summary>
    UnknownParameter,
    /// <summary>The parameter is a String or a Bool. There is no continuum for a drag to move
    /// along; those belong in the dialog.</summary>
    NotNumeric,
    /// <summary>A handle kind this build does not implement. None currently — both Linear and
    /// Angular are live — but the member and its whole path stay, because the drop-and-report
    /// behaviour is what lets a FURTHER kind be added later without the next wire bump becoming a
    /// cliff for anyone.</summary>
    UnsupportedKind,
    /// <summary>The grip does not move for any perturbation the probe tried — the declaration says
    /// this parameter drives this grip and the geometry disagrees.</summary>
    Unmeasurable,
    /// <summary>The generator threw while being probed.</summary>
    GeneratorFailed,
}

/// <summary>The outcome of one solve. <see cref="Ok"/> false always carries a
/// <see cref="Problem"/>.</summary>
/// <param name="Achieved">The handle as the generator last emitted it — its real position AND its
/// real anchor, at the value being returned. Free: the solver regenerated to get here anyway, so a
/// caller that needs to know where the grip (or its anchor) actually ended up never has to ask for
/// another generate. Null only when the solve failed before any regeneration succeeded.</param>
public sealed record PCellHandleSolveResult(
    bool Ok,
    PCellValue Value,
    double AchievedProjection,
    bool Converged,
    string? Problem,
    PCellHandle? Achieved = null)
{
    public static PCellHandleSolveResult Failed(string problem)
        => new(false, PCellValue.Real(0), 0, false, problem);
}

/// <summary>
/// pcell-parameter-handles.md R-pch-2/R-pch-3/R-pch-11 — the half of parameter handles that is not
/// UI.
///
/// <para><b>The generator declares WHAT is editable and WHERE the grip is; this measures HOW
/// MUCH.</b> It perturbs the parameter, regenerates, and reads how far the declared grip moved. The
/// alternative — an affine <c>scale</c> stated by the author — was rejected because it differs
/// between an in-process generator (lengths in SI metres) and a script one (lengths already in DBU)
/// for the same cell, and because it cannot describe a non-linear relationship at all. Measuring
/// puts no unit in the declaration and handles both.</para>
///
/// <para><b>R-pch-11 — deterministic, and this is not stylistic.</b> The committed value is fed to
/// <see cref="PCellValue.ToString"/>, which IS the content hash naming the generated cell
/// (<see cref="GeneratedCellStore"/>). A value differing in its seventeenth digit between two
/// identical drags mints two cell folders for one design intent, silently defeating R6's sharing and
/// churning <c>.generated-cells/</c>. So: a fixed probe schedule, a fixed iteration cap, a fixed
/// tolerance, no wall-clock anywhere, and every candidate value snapped to a
/// <see cref="SignificantDigits"/>-digit lattice before it is ever used — which makes "same start
/// and same target ⇒ same value" true by construction rather than by luck.</para>
///
/// <para>Framework-free by design: it takes a plain <c>Func</c>, so every path below is testable
/// against synthetic generators (linear, quadratic, quantized, clamped, dead) with no editor, no
/// canvas and no workspace.</para>
/// </summary>
public static class PCellHandleSolver
{
    /// <summary>Every candidate value is snapped to this many significant digits. Twelve is far
    /// beyond any real geometric precision and well inside a double's 15–17, so it rounds away
    /// iteration noise without ever rounding away intent.</summary>
    public const int SignificantDigits = 12;

    /// <summary>First probe step, as a fraction of the current value.</summary>
    private const double RelativeProbe = 1e-3;

    /// <summary>Probe step used when the current value is zero (or too small to scale from). Chosen
    /// small enough to be inside any parameter's own scale and grown geometrically from there —
    /// deliberately NOT derived from a unit, because knowing the unit is exactly what R-pch-2
    /// promises the host never needs.</summary>
    private const double AbsoluteProbe = 1e-9;

    private const double ProbeGrowth = 10.0;
    private const int MaxProbeAttempts = 12;

    /// <summary>How far the grip must move for a probe to count as measured. Grip coordinates are
    /// integer DBU, so a one-DBU move would make the finite difference mostly quantization noise;
    /// four gives a usable slope while still being reached on the first attempt for any sane cell.</summary>
    private const double MinProbeProjection = 4.0;

    /// <summary>Angular projections are in degrees, where the quantization argument above does not
    /// apply — the grip position is still integer DBU but the angle it subtends is continuous.</summary>
    private const double MinProbeProjectionAngular = 1e-3;

    /// <summary>
    /// Measured, not guessed. Three was the first choice and is not enough: a quadratic cell run
    /// with a fixed slope oscillates around the target and was still 12% out after three
    /// corrections. With the secant update in <see cref="Solve"/> a quadratic converges on the
    /// fourth, and a linear cell converges on the first regardless — so six is generous headroom for
    /// the non-linear case without ever being spent on the ordinary one.
    /// </summary>
    private const int MaxSolveIterations = 6;

    /// <summary>Convergence tolerance, in the handle's own projection units (DBU for Linear).</summary>
    public const double LinearTolerance = 1.0;
    public const double AngularTolerance = 1e-2;

    /// <summary>
    /// Can this handle be dragged at all? Used by the editor to decide which grips to draw and by
    /// <see cref="MeasureSensitivity"/> before it spends a regeneration.
    /// </summary>
    public static PCellHandleRejection Validate(
        PCellHandle handle,
        IReadOnlyDictionary<string, PCellValue> parameters)
    {
        if (!parameters.TryGetValue(handle.Parameter, out var v)) return PCellHandleRejection.UnknownParameter;
        return v.Kind is PCellValueKind.Real or PCellValueKind.Int
            ? PCellHandleRejection.None
            : PCellHandleRejection.NotNumeric;
    }

    /// <summary>Human-readable, and it names the generator and the handle so a cell author can find
    /// the declaration that is wrong.</summary>
    public static string Explain(PCellHandleRejection why, string generatorId, PCellHandle handle) => why switch
    {
        PCellHandleRejection.UnknownParameter =>
            $"'{generatorId}' declares a drag handle for '{handle.Parameter}', which is not one of its parameters. " +
            "The handle is ignored; the cell's other parameters are unaffected.",
        PCellHandleRejection.NotNumeric =>
            $"'{generatorId}' declares a drag handle for '{handle.Parameter}', which is not a number. " +
            "Text and flag parameters are edited in the Properties Inspector, not by dragging.",
        PCellHandleRejection.UnsupportedKind =>
            $"'{generatorId}' declares a '{handle.Kind}' drag handle for '{handle.Parameter}', which this build " +
            "does not support. The handle is ignored; every other handle on this cell still works.",
        PCellHandleRejection.Unmeasurable =>
            $"'{generatorId}': dragging '{handle.Parameter}' does not move its own handle, so there is nothing to " +
            "drag it along. Edit the parameter in the Properties Inspector instead.",
        PCellHandleRejection.GeneratorFailed =>
            $"'{generatorId}' failed while working out how '{handle.Parameter}' responds to being dragged, " +
            "so that handle is unavailable. The design is unchanged.",
        _ => "",
    };

    /// <summary>
    /// R-pch-2's probe. Perturbs <paramref name="handle"/>'s parameter and reads how far its grip
    /// moved, growing the perturbation geometrically until the movement is measurable.
    ///
    /// <para>Returns parameter-units per projection-unit — the reciprocal of a derivative, because
    /// that is the direction a drag actually needs it in.</para>
    /// </summary>
    public static bool MeasureSensitivity(
        Func<IReadOnlyDictionary<string, PCellValue>, PCellResult> generate,
        IReadOnlyDictionary<string, PCellValue> baseParameters,
        PCellHandle handle,
        int handleIndex,
        out double valuePerProjection,
        out PCellHandleRejection rejection,
        string? matchParameter = null)
    {
        valuePerProjection = 0;
        rejection = Validate(handle, baseParameters);
        if (rejection != PCellHandleRejection.None) return false;

        var start = baseParameters[handle.Parameter];
        bool isInt = start.Kind == PCellValueKind.Int;
        double startValue = start.AsReal();
        double baseProjection = handle.ProjectedPosition;
        double minMove = handle.Kind == PCellHandleKind.Angular ? MinProbeProjectionAngular : MinProbeProjection;

        // Unit-free: relative to the value when there is one to be relative to, absolute otherwise.
        // For an Int the smallest meaningful step is 1, by definition of the kind.
        double delta = isInt
            ? 1.0
            : Math.Max(Math.Abs(startValue) * RelativeProbe, AbsoluteProbe);

        for (int attempt = 0; attempt < MaxProbeAttempts; attempt++)
        {
            // Probe in the POSITIVE direction first and fall back to negative, so a parameter sitting
            // exactly on its own upper clamp is still measurable rather than reading as dead.
            foreach (double signed in (double[])[delta, -delta])
            {
                double probed = Snap(startValue + signed, isInt);
                if (probed == startValue) continue;

                if (!TryProject(generate, baseParameters, handle, handleIndex, probed, isInt,
                        out double projection, out _, matchParameter))
                {
                    rejection = PCellHandleRejection.GeneratorFailed;
                    return false;
                }

                double moved = projection - baseProjection;
                if (Math.Abs(moved) >= minMove)
                {
                    valuePerProjection = (probed - startValue) / moved;
                    return true;
                }
            }
            delta *= ProbeGrowth;
        }

        rejection = PCellHandleRejection.Unmeasurable;
        return false;
    }

    /// <summary>
    /// R-pch-3. Proposes a value from <paramref name="valuePerProjection"/>, regenerates, and
    /// corrects until the grip lands on <paramref name="targetProjection"/> or the iteration cap is
    /// reached — at which point the best achieved value is returned with
    /// <see cref="PCellHandleSolveResult.Converged"/> false, never an error. The grip is drawn where
    /// the generator actually put it, so a clamped, quantized or non-linear parameter shows the user
    /// the legal answer instead of a drag that lies until release.
    /// </summary>
    public static PCellHandleSolveResult Solve(
        Func<IReadOnlyDictionary<string, PCellValue>, PCellResult> generate,
        IReadOnlyDictionary<string, PCellValue> baseParameters,
        PCellHandle handle,
        int handleIndex,
        double targetProjection,
        double valuePerProjection,
        string? matchParameter = null,
        Func<double, double>? quantize = null)
    {
        if (!baseParameters.TryGetValue(handle.Parameter, out var start))
            return PCellHandleSolveResult.Failed($"'{handle.Parameter}' is not a parameter of this cell.");

        bool isInt = start.Kind == PCellValueKind.Int;
        double tolerance = handle.Kind == PCellHandleKind.Angular ? AngularTolerance : LinearTolerance;

        // The probe's slope is only the STARTING estimate. From the second regeneration on, the
        // secant through the last two (value, projection) pairs is a far better one — which is what
        // makes a non-linear cell converge at all: measured, a quadratic run with the fixed probe
        // slope oscillates around the target and never settles, while the secant reaches it on the
        // fourth iteration. Fully deterministic either way: the sequence depends only on the start
        // and the target.
        double previousValue = start.AsReal();
        double previousProjection = handle.ProjectedPosition;
        double value = Propose(previousValue, targetProjection - previousProjection,
                               valuePerProjection, handle, isInt, quantize);
        double achieved = previousProjection;
        PCellHandle? achievedHandle = null;

        for (int i = 0; i < MaxSolveIterations; i++)
        {
            if (!TryProject(generate, baseParameters, handle, handleIndex, value, isInt,
                            out achieved, out achievedHandle, matchParameter))
                return PCellHandleSolveResult.Failed(
                    $"'{handle.Parameter}' could not be regenerated at this value, so the design is unchanged.");

            double error = targetProjection - achieved;
            if (Math.Abs(error) <= tolerance)
                return new PCellHandleSolveResult(true, Rebuild(value, isInt), achieved, true, null, achievedHandle);

            double slope = valuePerProjection;
            if (achieved != previousProjection && value != previousValue)
                slope = (value - previousValue) / (achieved - previousProjection);

            double next = Propose(value, error, slope, handle, isInt, quantize);
            previousValue = value;
            previousProjection = achieved;
            if (next == value) break;   // on the lattice, or pinned at a bound — no progress to make
            value = next;
        }

        // Not converged is a normal outcome, not a failure: a quantized or clamped parameter simply
        // cannot land on an arbitrary target, and the honest answer is the value it did reach.
        return new PCellHandleSolveResult(true, Rebuild(value, isInt), achieved, false, null, achievedHandle);
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private static double Propose(double from, double error, double valuePerProjection,
                                  PCellHandle handle, bool isInt, Func<double, double>? quantize = null)
    {
        double v = from + error * valuePerProjection;
        if (handle.Min is { } lo) v = Math.Max(v, lo);
        if (handle.Max is { } hi) v = Math.Min(v, hi);
        // The caller's own lattice (a snap grid) FIRST, then R-pch-11's determinism lattice — the
        // second is far finer than any snap step, so it never moves an already-quantized value off
        // the grid, it only rounds away the double noise the multiplication left behind.
        if (quantize is not null) v = quantize(v);
        return Snap(v, isInt);
    }

    /// <summary>Regenerates with one parameter replaced and returns where the handle landed, in its
    /// own projection. False when the generator threw or the handle vanished from the result.</summary>
    private static bool TryProject(
        Func<IReadOnlyDictionary<string, PCellValue>, PCellResult> generate,
        IReadOnlyDictionary<string, PCellValue> baseParameters,
        PCellHandle handle, int handleIndex, double value, bool isInt,
        out double projection, out PCellHandle? achieved, string? matchParameter)
    {
        projection = 0;
        achieved = null;
        var trial = new Dictionary<string, PCellValue>(baseParameters, StringComparer.Ordinal)
        {
            [handle.Parameter] = Rebuild(value, isInt),
        };

        PCellResult result;
        try { result = generate(trial); }
        catch { return false; }

        var moved = Find(result.Handles, matchParameter ?? handle.Parameter, handleIndex);
        if (moved is null) return false;
        achieved = moved;

        // Projected through the ORIGINAL handle's anchor and axis, deliberately. A generator may move
        // its own anchor as a side effect (MKlopf's Offset moves the whole centreline), and measuring
        // against a moving frame would mix the parameter's effect with the frame's.
        //
        // R-pch-4b INVERTS that, and the inversion is the whole reason a pinned grip works at all.
        // When the anchor is held still in WORLD space, the anchor is the stable frame and the grip's
        // CELL coordinates are what drift — MLIN's left-edge grip sits at (0,0) for every value of L
        // and only its anchor moves. Measuring that against the original cell-space anchor reads as a
        // grip that never moves, i.e. Unmeasurable, and the drag would be refused outright. Measuring
        // from the REGENERATED anchor along the SAME declared axis is the quantity the declaration
        // actually names: how far the grip is from what it measures from.
        projection = handle.KeepAnchorFixed
            ? (handle with { AnchorX = moved.AnchorX, AnchorY = moved.AnchorY }).Project(moved.X, moved.Y)
            : handle.Project(moved.X, moved.Y);
        return true;
    }

    /// <summary>
    /// Prefers the same slot, falls back to the first handle naming <paramref name="matchParameter"/>
    /// — a cell may legitimately declare several handles for one parameter (a centred width's two
    /// edges), and the list may legitimately change length between parameter values.
    ///
    /// <para><b>The name searched for is not always the handle's own.</b> A two-axis grip's cross
    /// side is solved through a synthetic handle whose <c>Parameter</c> is the CROSS parameter, while
    /// the generator's returned list still identifies that grip by its PRIMARY one — so the caller
    /// supplies the name the list actually uses. Searching by the synthetic name instead finds
    /// nothing, and "nothing" reads as a generator failure, which is how this first showed up.</para>
    /// </summary>
    private static PCellHandle? Find(IReadOnlyList<PCellHandle>? handles, string matchParameter, int index)
    {
        if (handles is null || handles.Count == 0) return null;
        if ((uint)index < (uint)handles.Count &&
            string.Equals(handles[index].Parameter, matchParameter, StringComparison.Ordinal))
            return handles[index];
        foreach (var h in handles)
            if (string.Equals(h.Parameter, matchParameter, StringComparison.Ordinal)) return h;
        return null;
    }

    /// <summary>
    /// R-pch-11's lattice. Applied to EVERY candidate, not only the committed one, so the whole
    /// iteration runs on the lattice and the outcome cannot depend on how many corrections it happened
    /// to take.
    /// </summary>
    internal static double Snap(double v, bool isInt)
    {
        if (isInt) return Math.Round(v, MidpointRounding.AwayFromZero);
        return RoundSignificant(v, SignificantDigits);
    }

    internal static double RoundSignificant(double v, int digits)
    {
        if (v == 0.0 || !double.IsFinite(v)) return v;
        int exponent = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        int scale = digits - 1 - exponent;
        // Beyond this the scaling itself overflows or underflows to zero, and the value already
        // carries fewer significant digits than the lattice would impose.
        if (scale is > 300 or < -300) return v;
        double factor = Math.Pow(10, scale);
        return Math.Round(v * factor, MidpointRounding.AwayFromZero) / factor;
    }

    /// <summary>
    /// B0's rule — which kind a parameter is belongs to the cell that declares it. An Int stays an
    /// Int, never a Real that happens to be whole: the kind is part of
    /// <see cref="PCellValue.ToString"/> and therefore part of the content hash, so flipping it
    /// silently repoints the instance at a different generated cell.
    /// </summary>
    private static PCellValue Rebuild(double value, bool isInt)
        => isInt ? PCellValue.Int((long)Math.Round(value, MidpointRounding.AwayFromZero))
                 : PCellValue.Real(value);

    /// <summary>Formats a solved value for the drag readout, in the parameter's own terms.</summary>
    public static string FormatForReadout(PCellValue value)
        => value.Kind == PCellValueKind.Int
            ? value.AsInt().ToString(CultureInfo.InvariantCulture)
            : value.AsReal().ToString("G6", CultureInfo.InvariantCulture);
}
