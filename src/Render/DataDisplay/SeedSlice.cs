// ================================================================
//  SeedSlice.cs  —  The axis roles a cube trace opens with.
//
//  One rule, used wherever circuitRF has to choose roles the user has
//  not: "+ Trace", a signal picked on a card, a re-run that added a
//  sweep, and a bare cube name typed on a cube of three or more axes.
//
//    • a cube with a freq axis: freq is X, everything else pinned; with one frequency and a sweep around it, the sweep is X
//    • two or more swept axes: the INNERMOST is X, the next one out is
//      the family, the rest pinned
//    • otherwise the first plottable axis is X, the rest pinned
//
//  Lives below the firewall so the spec parser (and so `circuitrf plot`)
//  reads a bare name the way the trace card seeds one.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class SeedSlice
{
    /// <summary>
    /// Whether an axis indexes a STRUCTURAL element — which node, which branch, which harmonic, which
    /// port, which matrix row/column — rather than a condition the run swept.
    ///
    /// <para>The distinction is what lets a seeded trace tell "the sweep" from "the circuit": a sweep
    /// axis is something to plot ALONG or iterate a family over, a structural axis is something to pin
    /// and let the user repick. <c>freq</c> is deliberately absent — it is a swept condition, and the
    /// preferred X when a cube has one.</para>
    /// </summary>
    public static bool IsStructuralAxis(string name) =>
        name is "node" or "branch" or "harmonic" or "tone" or "port" or "probe" or "mixIndex"
             or "opvar" or "i" or "j";

    /// <summary>
    /// Index of the default X axis: the "freq" axis when present (S/Y/Z parameter cubes and any
    /// freq-swept cube), else the first non-label (node/branch/opvar) axis. Returns -1 when only label
    /// axes exist (→ no X → scalar, valid for no-sweep DC).
    /// </summary>
    public static int DefaultXAxis(IReadOnlyList<Axis> axes)
    {
        for (int d = 0; d < axes.Count; d++)
            if (axes[d].Name == "freq") return d;
        for (int d = 0; d < axes.Count; d++)
            // `opvar` joins node/branch as a LABEL axis, not a plottable one: its entries are
            // different quantities (a transconductance beside a capacitance beside a temperature),
            // so plotting along it draws one line through numbers that share no unit.
            if (axes[d].Name is not "node" and not "branch" and not "opvar") return d;
        return -1;
    }

    /// <summary>
    /// <paramref name="xIdx"/> → KeepAsX, every other axis pinned at index 0 (carrying its first label
    /// for quoted net names). Rank-0 → empty slice.
    /// </summary>
    public static AxisSlice[] Default(IReadOnlyList<Axis> axes, int xIdx)
    {
        var slice = new AxisSlice[axes.Count];
        for (int d = 0; d < axes.Count; d++)
            slice[d] = d == xIdx ? new AxisSlice(axes[d].Name, AxisRole.KeepAsX, 0) : Pinned(axes[d]);
        return slice;
    }

    /// <summary>
    /// The slice a cube trace opens with — <see cref="Default"/>, except for a cube with TWO OR MORE
    /// swept axes and no frequency.
    ///
    /// <para><b>The case (owner, 2026-08-18): a curve tracer.</b> A DC analysis swept over VDS and then
    /// VGS produces <c>DC1.I [VGS × VDS × branch]</c> — each <c>parametric_sweep</c> nesting level
    /// PREPENDS its axis, so the OUTERMOST sweep is axis 0 and the innermost is last. The default slice
    /// therefore makes VGS the X axis and pins VDS at its first value: drain current against the GATE
    /// voltage at VDS = 0, which is a flat line. What every one of those runs exists to produce is
    /// <c>I[~, :, "IDS"]</c> — current against VDS, one curve per gate step. The same holds for a
    /// harmonic-balance drive sweep wrapped in a frequency sweep: gain against Pin, one curve per
    /// frequency.</para>
    ///
    /// <para><b>A cube with a <c>freq</c> axis is left alone</b>, deliberately. Frequency is always the
    /// natural X, so an S-parameter run opens on S(1,1) over frequency with the sweep pinned.</para>
    /// </summary>
    public static AxisSlice[] Build(DataCube cube)
    {
        var axes = cube.Axes;
        // One frequency under a sweep is one point along freq: a value per sweep point reads along the sweep (a far field's metrics, brief-cosim-pattern-5 R-cp5-5).
        // Not a pattern, whose X is an angle the seed has no way to choose.
        if (axes.Any(a => a.Name == "freq" && a.Length == 1) && !axes.Any(a => a.Name is "theta" or "phi"))
            for (int d = axes.Count - 1; d >= 0; d--)
                if (axes[d].Length > 1 && axes[d].Labels is null && !IsStructuralAxis(axes[d].Name)) return Default(axes, d);
        if (axes.Any(a => a.Name == "freq")) return Default(axes, DefaultXAxis(axes));

        var sweeps = new List<int>();
        for (int d = 0; d < axes.Count; d++)
            if (!IsStructuralAxis(axes[d].Name)) sweeps.Add(d);

        if (sweeps.Count < 2) return Default(axes, DefaultXAxis(axes));

        int xIdx      = sweeps[^1];
        int familyIdx = sweeps[^2];

        // Too many members to draw: the renderer clamps a family at Trace.MaxFamilyCurves and says so,
        // but a SEEDED trace that silently showed the first 101 of a 500-point sweep would be claiming
        // to be the whole picture. Past the cap the axis is pinned instead; the family is one click away.
        if (axes[familyIdx].Length > Trace.MaxFamilyCurves) familyIdx = -1;

        var slice = new AxisSlice[axes.Count];
        for (int d = 0; d < axes.Count; d++)
            slice[d] = d == xIdx      ? new AxisSlice(axes[d].Name, AxisRole.KeepAsX, 0)
                     : d == familyIdx ? new AxisSlice(axes[d].Name, AxisRole.FamilyIterate, 0)
                     : Pinned(axes[d]);
        return slice;
    }

    private static AxisSlice Pinned(Axis ax)
        => new(ax.Name, AxisRole.PinToIndex, 0, Label: ax.Labels is { Length: > 0 } l ? l[0] : "");
}
