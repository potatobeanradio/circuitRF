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
}

/// <summary>Which termination plane a marker belongs to. Mirrors the engine's own
/// <c>TerminationSide</c> without src/Ui taking a dependency on its numbering.</summary>
public enum TerminationSideKind { Source, Load }

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
    /// <summary>Panel title, e.g. "Power @ P-3dB" / "Efficiency @ P-3dB".</summary>
    public string Title { get; init; } = "";

    /// <summary>Iso-lines, already clipped to the support mask by <c>ContourGrid</c>.</summary>
    public IReadOnlyList<RfCore.Loadpull.IsoPolyline> Contours { get; init; } = [];

    /// <summary>The level set the contours came from — the alpha ramp is a function of RANK in this
    /// list (§7.2), so it must travel with them.</summary>
    public IReadOnlyList<double> Levels { get; init; } = [];

    public IReadOnlyList<HarmonicaGridPoint> GridPoints { get; init; } = [];

    /// <summary>The markers this chart shows. The SAME instances the other chart holds (R-h45-3).</summary>
    public IReadOnlyList<HarmonicaMarker> Markers { get; init; } = [];

    /// <summary>D6's argmax over the computed grid — never a search, so the readout beside it can
    /// never disagree with what is drawn.</summary>
    public Complex? Mxp { get; init; }
    public Complex? Mxe { get; init; }

    /// <summary>
    /// R-h6-12 — the region the dragged glyph's intrinsic Γ can actually be put in, shaded during an
    /// INTRINSIC drag and null at every other time. Null and empty are the same thing on screen; both
    /// mean "no shading", which is the ordinary state.
    /// </summary>
    public CircuitRF.Harmonica.ReachableRegion? Reachable { get; init; }
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

    /// <summary>The §7.5 readouts, as label/value pairs. Deliberately flat: §7.5 asks for "small
    /// fonts, no section titles, no decoration", every element tooltipped and every value
    /// selectable.</summary>
    public IReadOnlyList<(string Label, string Value, string Tooltip)> Readouts { get; init; } = [];

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
