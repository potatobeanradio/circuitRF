using System;
using System.Collections.Generic;
using System.Numerics;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// One termination marker (§4.2). A marker <b>is</b> a band's termination — it is a property of the
/// CIRCUIT, not of a plot, which is what makes R-h45-3's "moving L2 on the power chart moves it on
/// the efficiency chart in the same frame" true by construction: both charts hold the same object.
/// </summary>
/// <param name="Side">Source or load.</param>
/// <param name="Band">Harmonic band. 1 = f₀.</param>
public sealed class HarmonicaMarker(TerminationSideKind side, int band)
{
    public TerminationSideKind Side { get; } = side;
    public int Band { get; } = band;

    /// <summary>"S1", "L2", … — §4.2's naming.</summary>
    public string Name => (Side == TerminationSideKind.Source ? "S" : "L") + Band;

    /// <summary>The termination as Γ against the reference impedance. THIS is what a drag writes.</summary>
    public Complex Gamma { get; set; }

    /// <summary>The intrinsic glyph's Γ for this band (§4.5). Read from the <c>Gamma_intr</c> cube —
    /// never recomputed here (§0.3 item 1).</summary>
    public Complex GammaIntrinsic { get; set; }

    /// <summary>§4.5 consequence 2 — <c>|Γ_intr|</c> can legitimately exceed 1. It is rendered
    /// OUTSIDE the chart boundary on a compressed radial scale, never clamped and never hidden.</summary>
    public bool IntrinsicIsOutsideUnitCircle => GammaIntrinsic.Magnitude > 1.0;

    /// <summary>
    /// R-h6-10 — the EXTRINSIC termination is outside the unit circle, i.e. active. Legitimate and
    /// never clamped: an inverse solve is entitled to discover that the only way to put the intrinsic
    /// glyph where the user dragged it is an active termination, and hiding that would mislead. The
    /// marker is drawn with a hatched outline so the discovery is never silent.
    ///
    /// <para>Distinct from <see cref="IntrinsicIsOutsideUnitCircle"/>, which is about the GLYPH and is
    /// ordinary rather than notable — <c>IntrinsicGlyphScale</c> already handles that case.</para>
    /// </summary>
    public bool ExtrinsicIsOutsideUnitCircle => Gamma.Magnitude > 1.0;

    /// <summary>
    /// R-h9r2-8 — whether this marker's constant-VSWR locus is drawn. Persisted additively in
    /// <c>.charm</c> (<c>CharmIo.VswrToJson</c>/<c>VswrFromJson</c>) — a marker nobody has turned this
    /// on for is the absent state, so an untouched document re-serialises byte-for-byte.
    /// </summary>
    public bool VswrEnabled { get; set; }

    /// <summary>The VSWR the locus is drawn for. Default 2.0, per R-h9r2-8. Persisted alongside
    /// <see cref="VswrEnabled"/> — only when the overlay is actually on.</summary>
    public double VswrValue { get; set; } = 2.0;

    /// <summary>
    /// R-h9r2-9 — "Snap to Grid": when true, releasing a drag on this marker snaps its Γ to the
    /// nearest sample in <c>Frame.SmithPower.GridPoints</c> instead of landing wherever the pointer
    /// was. <b>Session state only, like <see cref="HarmonicaViewModel.TopmostMarker"/></b> — the grid
    /// itself is never persisted (a custom ring set is session state too), so there is nothing for
    /// this to mean once the document is reopened.
    /// </summary>
    public bool SnapToGridEnabled { get; set; }
}

/// <summary>Which termination plane a marker belongs to. Mirrors the engine's own
/// <c>TerminationSide</c> without src/Ui taking a dependency on its numbering.</summary>
public enum TerminationSideKind { Source, Load }

/// <summary>
/// §5 (R1C) — which of the strip's columns a readout belongs to. <c>General</c> is everything that
/// stayed flat (§7.5's original wrapping run, now just the "intrinsic: not located" row — R3C §2/§3
/// moved everything else out of it); <c>Source</c>/<c>Load</c>/<c>Mxp</c>/<c>Mxe</c> are R-h9c-6's
/// columns. <c>OperatingPoint</c> (R3C §2) is Pin/Pout/Gain/DE/PAE/Pdc — named for what the figures
/// ARE (the numbers at the operating point), not for where R3C happens to have put the column.
/// <c>IntrinsicVds</c>/<c>IntrinsicIds</c> (R6C §2) are the intrinsic drain voltage/current spectra,
/// one row per harmonic, magnitude ∠ angle. Screen order (a 2×4 grid — Settings · OperatingPoint ·
/// Mxp · Mxe over Load · Source · IntrinsicVds · IntrinsicIds) lives in
/// <c>ReadoutStripView.axaml</c>'s <c>Columns</c> grid, not here.
/// </summary>
public enum ReadoutColumn { General, Source, Load, Mxp, Mxe, OperatingPoint, IntrinsicVds, IntrinsicIds }

/// <summary>
/// One row of §7.5's strip (R-h9c-9). Replaces the old flat <c>(label, value, tooltip)</c> triple —
/// columns, per-row format and editability do not fit in one, and <c>HarmonicaSolver.BuildReadouts</c>
/// is where the numbers already are (§0.3 item 1: never recompute in a view model).
/// </summary>
/// <param name="Label">The row's own label — "ZL1", "Pout", "MXP 1f0 Load", …</param>
/// <param name="Value">Formatted for display AT SOLVE TIME, in whichever format was current then.
/// <b>R-h9r2-25:</b> for an <see cref="IsComplex"/> row this is a FALLBACK only — the strip renders
/// from <see cref="RawValue"/> at the CURRENT format instead, whenever both are available, which is
/// what makes a right-click format change repaint immediately without a re-solve. This still holds
/// the answer for a row with no raw form (headers, "no optimum", every scalar figure).</param>
/// <param name="Tooltip">§7.5's own concession to newcomers — every row carries one.</param>
/// <param name="Column">Which of the four columns this row renders in.</param>
/// <param name="IsComplex">True for a Z or Γ row — R-h9c-7's right-click format flyout applies to
/// these and only these.</param>
/// <param name="Editable">True for a row R-h9c-8's inline double-click editor may open. Only a
/// termination row (Source/Load column) is ever true — an MXP/MXE row is a CONSEQUENCE of the
/// solve and the owner says directly it "cannot be edited".</param>
/// <param name="Side">The marker's side, for an editable termination row; null otherwise.</param>
/// <param name="Band">The marker's band, for an editable termination row; 0 otherwise.</param>
/// <param name="IsGamma">True when this complex row is the Γ half of a Z/Γ pair rather than the Z
/// half — the two need independent format state and independent write-through calls
/// (<c>SetMarkerImpedance</c> vs <c>SetMarkerGamma</c>).</param>
/// <param name="RawValue">
/// R-h9r2-25 — the UNFORMATTED value behind an <see cref="IsComplex"/> row, carried so
/// <c>ReadoutStripView</c> can format it through <c>HarmonicaReadoutFormatting</c> at RENDER time
/// using whatever format is current then, rather than baking in whatever format was current when
/// <c>HarmonicaSolver.BuildReadouts</c> ran. Null for every row with no complex quantity behind it.
/// </param>
/// <param name="Unit">
/// R7C §1.2 — the row's unit, stated ONCE here from what <c>HarmonicaSolver</c> KNOWS the row is
/// ("dBm" for Pout, "Ω" for a Zin/termination row, …), never recovered by parsing <see cref="Value"/>
/// (<c>HarmonicaReadoutFormatting.SplitUnit</c>'s old job). Parsing the rendered text meant the unit
/// silently vanished — and the shared label column silently narrowed — the instant a value rendered
/// "—" instead of a number (a failed solve mid-drag). <c>ReadoutStripView</c> composes the row's
/// LABEL cell from this ("Pout (dBm):"), never the value cell — see <c>BuildColumnRowShell</c>.
/// </param>
public sealed record HarmonicaReadout(
    string Label, string Value, string Tooltip, ReadoutColumn Column,
    bool IsComplex = false, bool Editable = false,
    TerminationSideKind? Side = null, int Band = 0, bool IsGamma = false,
    Complex? RawValue = null, string Unit = "")
{
    /// <summary>
    /// R-h9c-7's persistence key for this row's format choice, or null for a row with no format
    /// (every non-<see cref="IsComplex"/> row). Stable across a reload — it names the QUANTITY, not
    /// a position in a list, so reordering or adding rows can never silently move a saved format onto
    /// the wrong row.
    /// </summary>
    public string? FormatKey => !IsComplex ? null
        : Side is { } side
            ? $"{(side == TerminationSideKind.Source ? "S" : "L")}{Band}.{(IsGamma ? "Gamma" : "Z")}"
        : Column is ReadoutColumn.Mxp or ReadoutColumn.Mxe
            ? $"{(Column == ReadoutColumn.Mxp ? "MXP" : "MXE")}.Zin"
        : Column is ReadoutColumn.IntrinsicVds or ReadoutColumn.IntrinsicIds
            ? $"{(Column == ReadoutColumn.IntrinsicVds ? "VDSi" : "IDSi")}.{Band}"
        : Column is ReadoutColumn.OperatingPoint
            ? "OP.Zin"
        : null;
}

/// <summary>R-h9c-7's per-row format — real/imaginary or magnitude/angle. Display-only; never
/// touches the model.</summary>
public enum ReadoutFormat { RealImaginary, MagnitudeAngle }

/// <summary>A Γ grid point as the Smith panel draws it (§6.3 / R-h45-5).</summary>
/// <param name="Gamma">Where it is.</param>
/// <param name="IsHole">
/// True when the point did not reach the compression target before <c>PinMax</c>. It is drawn as a
/// small HOLLOW dot so the hole reads as <i>measured</i> rather than as a rendering gap — §0.3 item 4
/// measured 7 of them on a realistic 61-point grid, so this is the common case, not a corner.
/// </param>
public readonly record struct HarmonicaGridPoint(Complex Gamma, bool IsHole);

/// <summary>
/// One Smith panel's contents. Everything here is READ from a solved result — §0.3 item 1: "the
/// engine is DONE and every number the panels need already exists. Do not recompute any of it in a
/// view-model."
/// </summary>
public sealed record SmithPanelData
{
    /// <summary>R-h9b-4's row 1 — the metric, e.g. "P-3dB Power (dBm)" / "P-3dB Efficiency (%)".</summary>
    public string Title { get; init; } = "";

    /// <summary>R-h9b-4's row 2 — the swept plane and band, e.g. "Fundamental Load Plane, Z0=50Ω".
    /// Drawn beneath <see cref="Title"/>, centred with the chart; both rows are built by
    /// <c>HarmonicaTitles</c> so the two charts cannot disagree about how a setting is spelled.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Iso-lines, already clipped to the support mask by <c>ContourGrid</c>.</summary>
    public IReadOnlyList<RfCore.Loadpull.IsoPolyline> Contours { get; init; } = [];

    /// <summary>The level set the contours came from — the alpha ramp is a function of RANK in this
    /// list (§7.2), so it must travel with them.</summary>
    public IReadOnlyList<double> Levels { get; init; } = [];

    public IReadOnlyList<HarmonicaGridPoint> GridPoints { get; init; } = [];

    /// <summary>The markers this chart shows. The SAME instances the other chart holds (R-h45-3).</summary>
    public IReadOnlyList<HarmonicaMarker> Markers { get; init; } = [];

    /// <summary>The reference impedance this frame was solved against — R-h9r2-8's VSWR locus needs
    /// it to turn a marker's Γ into the impedance the circle is really drawn around. Stamped by
    /// <c>HarmonicaSolver</c> from <c>Model.Settings.Z0</c> on every frame (R-h9b-6: Z0 is a live
    /// setting, never frozen).</summary>
    public double Z0 { get; init; } = 50.0;

    /// <summary>D6's argmax over the computed grid — never a search, so the readout beside it can
    /// never disagree with what is drawn. Kept as the honest SAMPLE-based seed (R-h9b-15); the glyph
    /// itself is drawn from <see cref="MxpOptimum"/>/<see cref="MxeOptimum"/> below.</summary>
    public Complex? Mxp { get; init; }
    public Complex? Mxe { get; init; }

    /// <summary>
    /// R-h9b-15/16/17 — THIS panel's resolved optimum (the Power panel's is its PoutDbm optimum; the
    /// Efficiency panel's is its DE/PAE one): the interpolated argmax of the FITTED surface (never a
    /// grid sample), and the figures of merit from ONE SOLVE at that state rather than from N
    /// separately-interpolated surfaces. <see cref="Solved"/>/<see cref="Published"/> are null on a
    /// degraded (dragging) rung or a <c>SkipContours</c> frame — the glyph still tracks
    /// <see cref="Gamma"/> every frame (cheap, no HB solve), but its FOMs are only ever from a real
    /// one. <b>The glyph and 1C's readout column read this SAME record</b> — the invariant R-h9b-17
    /// exists to create.
    /// </summary>
    /// <param name="Gamma">The interpolated argmax.</param>
    /// <param name="MetricValue">The fitted surface's value there.</param>
    /// <param name="Solved">The Pin drive-up at this termination, or null (not solved this frame).</param>
    /// <param name="Published">Zin/AM-PM and the rest of §5's cubes at that solve, or null.</param>
    public sealed record SmithOptimum(
        Complex Gamma, double MetricValue,
        CircuitRF.Harmonica.PinStep? Solved, RfCore.Data.DataSet? Published)
    {
        /// <summary>
        /// R9C §2.1 — why this optimum has no <see cref="Solved"/>/<see cref="Published"/> from a
        /// full-quality frame's OWN drive-up: the search itself ran and failed (read off its
        /// <c>PinStopReason</c> — <c>PinMax</c> and <c>NonConvergence</c> are different stories and
        /// stay distinguishable rather than merged into one refusal), never a fabricated fallback like
        /// the search's last surviving probe. Null when <see cref="Solved"/> is non-null, and also null
        /// on a degraded/<c>SkipContours</c> frame — that case never ran a drive-up at all this frame,
        /// which is a different story from one that ran and failed.
        /// </summary>
        public string? UnsolvedReason { get; init; }

        /// <summary>
        /// R9C §2.3 — the compression point's own SCALAR figures (Pout/Gain/DE/PAE/Pdc), read from the
        /// ladder's <c>SweepCompression</c> — the interpolated (or, with <c>ExactCompressionSolve</c>,
        /// one-real-solve) reading AT the target — rather than from <see cref="Solved"/>'s own (nearest
        /// ladder RUNG) numbers, exactly the <c>AtCompression</c>-vs-<c>SweepCompression</c> split every
        /// other reader of a <c>PinSearchResult</c> already follows (<c>HarmonicaSolver</c>'s own
        /// operating-point column). Null for a <c>Run()</c>-based caller, whose own
        /// <c>AtCompression</c> already sits exactly on target.
        /// </summary>
        public CircuitRF.Harmonica.CompressionReadout? SolvedCompression { get; init; }
    }

    /// <summary>This panel's own resolved optimum (R-h9b-15/16/17). Null means "no optimum" —
    /// every grid point a hole, or a <c>SkipContours</c> frame — never a cross at the origin.</summary>
    public SmithOptimum? Optimum { get; init; }

    /// <summary>
    /// R-h6-12 — the region the dragged glyph's intrinsic Γ can actually be put in, shaded during an
    /// INTRINSIC drag and null at every other time. Null and empty are the same thing on screen; both
    /// mean "no shading", which is the ordinary state.
    /// </summary>
    public CircuitRF.Harmonica.ReachableRegion? Reachable { get; init; }

    /// <summary>
    /// R-h9r2-1 — which plane/band this panel's <see cref="Contours"/>/<see cref="GridPoints"/>/
    /// <see cref="Optimum"/> were actually SOLVED for. Null means this panel carries no contour layer
    /// at all (never solved yet, or a grid-less frame that refused to carry one forward because the
    /// identity did not match). Compared field-for-field against a frame's own
    /// <c>Options.GridSide</c>/<c>GridHarmonic</c> before a grid-less frame is allowed to reuse a
    /// PREDECESSOR's layer — carrying a Load-plane contour set into a frame the user has since
    /// switched to the Source plane would draw a confident wrong picture.
    /// </summary>
    public CircuitRF.Harmonica.TerminationSide? ContourGridSide { get; init; }

    /// <summary>The harmonic band half of <see cref="ContourGridSide"/>'s identity check.</summary>
    public int? ContourGridHarmonic { get; init; }
}

/// <summary>§7.3 — the DCIV family with the time-domain loadline over it.</summary>
public sealed record LoadlinePanelData
{
    /// <summary>One DCIV curve: a Vgs value and its Ids(Vds) trace.</summary>
    public sealed record Curve(double Vgs, double[] Vds, double[] Ids);

    public IReadOnlyList<Curve> Dciv { get; init; } = [];

    /// <summary>The loadline itself, closed over one RF cycle.</summary>
    public double[] LoadlineVds { get; init; } = [];
    public double[] LoadlineIds { get; init; } = [];

    /// <summary>
    /// §7.3's plane toggle. <b>One toggle, not two</b> — the DCIV family and the loadline move
    /// together, so the two curves are always in the same plane and cannot be misleadingly
    /// superimposed. The indicator that states which plane is shown is <b>persistent, never
    /// absent</b>.
    /// </summary>
    public bool Intrinsic { get; init; } = true;

    public string PlaneLabel => Intrinsic ? "intrinsic" : "extrinsic";

    /// <summary>
    /// brief-harmonicarf-r6d §5 — the fundamental this loadline was closed over one cycle of, carried
    /// alongside <see cref="LoadlineVds"/>/<see cref="LoadlineIds"/> so the Time Domain panel can build
    /// its own time axis (<c>i / N × 1/f₀</c>, <c>N = LoadlineVds.Length − 1</c>) WITHOUT a second read
    /// of <c>Model.Settings</c> — the same "read from the solved frame, never recompute" rule §0.3
    /// item 1 states for every other panel value.
    /// </summary>
    public double FrequencyHz { get; init; }
}

/// <summary>§7.4 — gain on the left axis, efficiency on the right, against output power.</summary>
public sealed record PowerSweepPanelData
{
    public double[] PoutDbm { get; init; } = [];
    public double[] PinAvailDbm { get; init; } = [];
    public double[] GainDb { get; init; } = [];
    public double[] EfficiencyPct { get; init; } = [];

    /// <summary>The operating-point cursor's index into the arrays, or -1 for none. §7.4: the Pin at
    /// which the glyphs, the loadline and (H6) the inverse solve are evaluated.</summary>
    public int CursorIndex { get; init; } = -1;

    /// <summary>§6.3 — a Γ point that never reached the target still shows its full drive-up, and
    /// says so.</summary>
    public bool ReachedCompression { get; init; } = true;

    /// <summary>§7.4 — the X-axis unit is CLICK-TO-CYCLE on the axis itself.</summary>
    public PowerSweepXUnit XUnit { get; init; } = PowerSweepXUnit.PoutDbm;

    /// <summary>
    /// brief-harmonicarf-r4 §1.2 — the sweep's own configured range, carried alongside the data so a
    /// Pin-domain X axis can be PINNED to it rather than autofit to whatever Pin the ladder actually
    /// stopped at. With §1's early stop, the last solved Pin moves with the termination; autofitting
    /// the axis to that would make it visibly breathe frame to frame during a drag.
    /// </summary>
    public double PinStartDbm { get; init; }
    public double PinMaxDbm   { get; init; }

    /// <summary>
    /// R-h9b-8 — which metric the right axis is, so its label reads "Efficiency (%)" or "PAE (%)"
    /// rather than falling back to the trace's own auto-derived label. The solver already has
    /// <c>opt.EfficiencyMetric</c> in hand when it builds this panel.
    /// </summary>
    public CircuitRF.Harmonica.GridMetric EfficiencyMetric { get; init; } = CircuitRF.Harmonica.GridMetric.DrainEfficiency;
}

/// <summary>§7.4's click-to-cycle X-axis unit, in cycle order.</summary>
public enum PowerSweepXUnit { PoutDbm, PoutW, PinAvailDbm, PinAvailW }

/// <summary>The next unit in §7.4's cycle: Pout (dBm) → Pout (W) → Pin available (dBm) → Pin available (W).</summary>
public static class PowerSweepXUnitExtensions
{
    public static PowerSweepXUnit Next(this PowerSweepXUnit u)
        => (PowerSweepXUnit)(((int)u + 1) % 4);

    public static string Label(this PowerSweepXUnit u) => u switch
    {
        PowerSweepXUnit.PoutDbm     => "Pout (dBm)",
        PowerSweepXUnit.PoutW       => "Pout (W)",
        PowerSweepXUnit.PinAvailDbm => "Pin available (dBm)",
        _                           => "Pin available (W)",
    };

    /// <summary>The X values for this unit, from the sweep's own dBm arrays.</summary>
    public static double[] Values(this PowerSweepXUnit u, PowerSweepPanelData d) => u switch
    {
        PowerSweepXUnit.PoutDbm     => d.PoutDbm,
        PowerSweepXUnit.PoutW       => ToWatts(d.PoutDbm),
        PowerSweepXUnit.PinAvailDbm => d.PinAvailDbm,
        _                           => ToWatts(d.PinAvailDbm),
    };

    private static double[] ToWatts(double[] dbm)
    {
        var w = new double[dbm.Length];
        for (int i = 0; i < dbm.Length; i++) w[i] = Math.Pow(10.0, (dbm[i] - 30.0) / 10.0);
        return w;
    }
}

/// <summary>
/// One solved frame: everything the four panels draw, and nothing they have to compute.
///
/// <para>M3 fills this from a STATIC solved result. M5/M6 replace the producer with the solve pool
/// and the frame scheduler; nothing on this side changes, which is the point of the split.</para>
/// </summary>
public sealed record HarmonicaFrame
{
    public SmithPanelData      SmithPower      { get; init; } = new();
    public SmithPanelData      SmithEfficiency { get; init; } = new();
    public LoadlinePanelData   Loadline        { get; init; } = new();
    public PowerSweepPanelData PowerSweep      { get; init; } = new();

    /// <summary>
    /// Which rung of §6.8's ladder this frame was actually SOLVED at. Carried on the frame rather than
    /// inferred from the scheduler's present state, because by the time a frame is published the
    /// ladder may already have moved — and "what did the user just see" is a property of the frame.
    /// </summary>
    public CircuitRF.Harmonica.FrameQuality Quality { get; init; } = CircuitRF.Harmonica.FrameQuality.Full;

    /// <summary>
    /// R-h6-4 / D6 — what this frame cost, by stage, with <c>RenderMs</c> left at zero: the solver
    /// cannot know it. The view fills that in from its own last draw before feeding the whole thing
    /// back to <c>FrameScheduler.RecordFrame</c>.
    ///
    /// <para><b>The stages are timed apart on purpose.</b> A scheduler that lumps fit and solve
    /// together cannot tell "the solver is slow" from "the fit is slow" and will degrade the wrong
    /// one.</para>
    /// </summary>
    public CircuitRF.Harmonica.FrameTiming Timing { get; init; }

    /// <summary>
    /// What an inverse-drag frame's solve produced, or null on an ordinary frame. Carried on the
    /// frame because the answer is computed on a WORKER and the terminations it writes are UI-visible
    /// state — so the value crosses the thread boundary the same way every other frame value does,
    /// rather than through a field two threads share.
    /// </summary>
    public InverseOutcome? Inverse { get; init; }

    /// <summary>
    /// R9D §2 — the S1 "Match to Zin*" command's answer, or null on every ordinary frame. Carried on
    /// the frame for the same reason <see cref="Inverse"/> is: the answer is computed on a WORKER and
    /// the termination it writes is UI-visible state.
    /// </summary>
    public ConjugateMatchOutcome? ConjugateMatch { get; init; }

    /// <summary>Every marker, once. Both Smith panels hold references INTO this list (R-h45-3).</summary>
    public IReadOnlyList<HarmonicaMarker> Markers { get; init; } = [];

    /// <summary>
    /// R-h7-6 — the §5 <c>DataSet</c> this frame was built from, at the frame's own operating point.
    ///
    /// <para><b>It rides on the frame for the same reason the inverse-solve outcome does.</b> It is
    /// produced on a solve worker and consumed on the UI thread, and the trace picker must see the
    /// numbers the glyphs beside it were drawn at — a picker that re-solved to populate itself would
    /// show a different operating point. Null only on a frame with no converged step (nothing was
    /// solved, so there is nothing to publish).</para>
    /// </summary>
    public RfCore.Data.DataSet? Published { get; init; }

    /// <summary>
    /// §7.5's readouts (R-h9c-9). Four columns now (General/Source/Load/MXP/MXE) rather than one
    /// flat run — §7.5's own density constraint ("small fonts, no section titles, no decoration",
    /// every element tooltipped and every value selectable) is unchanged, it just applies per
    /// column instead of to one long wrap.
    /// </summary>
    public IReadOnlyList<HarmonicaReadout> Readouts { get; init; } = [];

    public static readonly HarmonicaFrame Empty = new();
}

/// <summary>
/// One inverse-drag frame's answer, as it crosses back to the UI thread.
/// </summary>
/// <param name="Converged">Whether the solve landed. R-h6-9: false means NOTHING moves.</param>
/// <param name="Failure">Why not, when it did not.</param>
/// <param name="Bands">The marked bands, in unknown order.</param>
/// <param name="Gammas">
/// The extrinsic Γ per band. On a failure this is the UNCHANGED vector, so applying it is a no-op by
/// construction rather than by a branch someone has to remember to write.
/// </param>
/// <param name="Residual">‖F‖ where the iteration stopped.</param>
public sealed record InverseOutcome(
    bool Converged,
    CircuitRF.Harmonica.InverseFailure Failure,
    CircuitRF.Harmonica.InverseBand[] Bands,
    Complex[] Gammas,
    double Residual);

/// <summary>
/// R9D §2 — the S1 "Match to Zin*" command's answer, as it crosses back to the UI thread.
/// </summary>
/// <param name="Found">False means NOTHING is written — R-h6-9's rule, applied here.</param>
/// <param name="Reason">Why not, when it was not found. Shown on the message line, never thrown.</param>
/// <param name="RequestedBackoffDb">What was asked for (5 dB).</param>
/// <param name="ActualBackoffDb">What the nearest ALREADY-SOLVED ladder rung actually is, which is
/// what "approximately" in the owner's request means and what the message line must state.</param>
/// <param name="PinDbm">That rung's own Pin.</param>
/// <param name="Zin">Zin there, at the extrinsic source plane, fundamental.</param>
public sealed record ConjugateMatchOutcome(
    bool Found, string? Reason, double RequestedBackoffDb, double ActualBackoffDb,
    double PinDbm, Complex Zin);
