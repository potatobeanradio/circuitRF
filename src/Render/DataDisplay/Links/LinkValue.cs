// ================================================================
//  LinkValue.cs  —  what a link source resolves to (brief-ddl-1 R-ddl1-3)
//
//  A link SOURCE (in DDL-1, a marker) is asked where it is, and answers
//  with one of these. A link CONSUMER (a pinned slice axis) takes an index
//  out of it by the matching rules in LinkMatch. Everything a consumer
//  needs is in the value, so a consumer never reaches back into the
//  source's trace — which is what lets a control (DDL-5) be a source
//  without any consumer learning what a control is.
// ================================================================

using System.Collections.Generic;
using System.Numerics;

namespace CircuitRF.Render.DataDisplay;

/// <summary>
/// One axis the source's trace keeps, and the sample the source sits on along it. <see cref="Index"/> is an index
/// into the source cube's own axis (<see cref="AxisValues"/>), not into a narrowed X range drawn from it.
/// </summary>
/// <param name="SourcePath">The file the source's trace reads, or null when it has none.</param>
/// <param name="Group">The DataSet group of the source's cube — "" for the default group or a network trace.</param>
/// <param name="Axis">The axis name — <c>freq</c>, <c>gridPoint</c>, <c>pinStep</c>.</param>
/// <param name="Unit">The axis unit as the source stores it — "Hz", or "" for a unitless index axis.</param>
/// <param name="AxisValues">The whole axis, so "the same axis" (R-ddl1-6 rule 1) can be decided by comparing grids.</param>
public sealed record LinkCoordinate(
    string? SourcePath, string Group, string Axis, int Index, double Value, string Unit,
    IReadOnlyList<double> AxisValues);

/// <summary>Where a marker on a Smith or contour plot sits, as a point on the Γ plane.</summary>
/// <param name="Gamma">The reflection coefficient the marker stands on. For a LOADPULL position (<paramref name="SourcePath"/>
/// set) it is in the loadpull's own stored basis, which is what its surface is fitted in.</param>
/// <param name="ReferenceZ0">The impedance <paramref name="Gamma"/> is referred to.</param>
/// <param name="Snapped">True when the marker is on a sample (a measured load); false when it was placed freely.</param>
/// <param name="SourcePath">The loadpull file the position was read against — null for a position on any other Smith
/// trace (brief-ddl-3 R-ddl3-1).</param>
/// <param name="Group">That file's loadpull group ("" for the top level).</param>
/// <param name="FreqIndex">The frequency (leading-axis) index it was read at (R-ddl3-6).</param>
/// <param name="Node">The <c>gridPoint</c> a snapped marker sits on, when its trace says so directly.</param>
/// <param name="Kernel">The RBF kernel the source's contours fit with — a synthesised read uses the same.</param>
/// <param name="Smooth">That fit's smoothing.</param>
public sealed record LinkPosition(
    Complex Gamma, Complex ReferenceZ0, bool Snapped,
    string? SourcePath = null, string? Group = null, int FreqIndex = 0, int? Node = null,
    RfCore.Loadpull.RbfKernel Kernel = RfCore.Loadpull.RbfKernel.Multiquadric, double Smooth = 1e-3)
{
    /// <summary>The load as an impedance.</summary>
    public Complex Z => ReferenceZ0 * (1 + Gamma) / (1 - Gamma);

    public bool IsLoadpull => SourcePath is not null;
}

/// <summary>A plain value with its unit — what a display control supplies (DDL-5).</summary>
public sealed record LinkScalar(double Value, string Unit);

/// <summary>
/// What a link source gave, the last time it was asked. <see cref="Frozen"/> is set when the source is gone and this
/// is what remains of it (R-ddl1-7) — in DDL-1 that is only ever the stored index of the consumer, so a frozen value
/// carries no coordinates.
/// </summary>
public sealed record LinkValue(
    string SourceId,
    string Name,
    IReadOnlyList<LinkCoordinate> Coordinates,
    LinkPosition? Position = null,
    LinkScalar? Scalar = null,
    bool Frozen = false);

/// <summary>How a linked pin came by its index.</summary>
public enum LinkMatchKind
{
    /// <summary>The same axis — its name, length and values — so the source's own index (rule 1).</summary>
    SameAxis,
    /// <summary>A coordinate in the same unit: the nearest sample to its value (rule 2), or to a control's (rule 3).</summary>
    Nearest,
    /// <summary>As <see cref="Nearest"/>, but the value is off the end of the axis, so the end sample (rule 2).</summary>
    Clamped,
    /// <summary>The axis is a list of cases (<c>point</c>) with 1-D companion cubes over it (<c>vgs[point]</c>,
    /// <c>vds[point]</c>): the case whose companion values are nearest the source's (brief-ddl-4 R-ddl4-1).</summary>
    Companion,
    /// <summary>Nothing the source gives fits the axis; drawn at the stored index (rule 4).</summary>
    Unresolved,
    /// <summary>The source is gone, or is part of a cycle; drawn at the stored index (R-ddl1-7, R-ddl1-8).</summary>
    Frozen,
}

/// <summary>The index a linked pin draws at, how it got it, and the sentence the trace's note carries (null when
/// there is nothing worth saying, which is the exact and the nearest-in-range cases).</summary>
public readonly record struct LinkMatchResult(int Index, LinkMatchKind Kind, string? Note)
{
    public bool IsLive => Kind is LinkMatchKind.SameAxis or LinkMatchKind.Nearest or LinkMatchKind.Clamped
                                 or LinkMatchKind.Companion;
}
