// ================================================================
//  MarkerLinkSource.cs  —  a marker, asked where it is (R-ddl1-3)
//
//  The coordinates come from the SAMPLE the marker sits on
//  (Trace.MarkerSampleOf) — the curve index of a family, the frequency
//  index of a network trace — never from a value matched with ==.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class MarkerLinkSource
{
    /// <summary>The link value of the marker at <paramref name="site"/>, read from its trace as it now stands.</summary>
    public static LinkValue Resolve(LinkDocument.MarkerSite site, IPlotDataSources? sources)
    {
        var (t, m) = (site.Trace, site.Marker);
        var coords = new List<LinkCoordinate>();

        if (t.MarkerSampleOf(m) is { } sample)
        {
            if (!t.IsCubeBound)
            {
                var f = t.Data.Frequencies;
                coords.Add(new LinkCoordinate(t.EffectiveSourcePath, "", "freq", sample.X, f[sample.X], "Hz", f));
            }
            else
            {
                DataCube? cube = null;
                string group = "";
                if (t.CubeName is { } cn && sources is not null && t.SourcePath is { } sp && sources.DataFor(sp) is { } ds
                    && ds.Contains(cn))
                {
                    cube = ds[cn];
                    int dot = cn.IndexOf('.');
                    if (dot > 0 && ds.Groups.Contains(cn[..dot])) group = cn[..dot];
                }

                if (!t.IsVersus && t.CubeXValues is { Count: > 0 } xs && sample.X < xs.Count)
                    coords.Add(Coordinate(t, cube, group, t.CubeXAxisName, t.CubeXUnit ?? "", sample.X, xs));
                else if (t.IsVersus && t.SampleAxis is { } sa && sample.X < sa.Length)
                    coords.Add(new LinkCoordinate(t.SourcePath, group, sa.Name, sample.X, sa.Values[sample.X], sa.Unit ?? "", sa.Values));

                if (t.IsFamily && sample.Curve >= 0 && sample.Curve < t.FamilyCurves.Count && t.FamilyAxisName is { } fam)
                {
                    var famValues = t.FamilyCurves.Select(c => c.AxisValue).ToArray();
                    coords.Add(Coordinate(t, cube, group, fam, t.FamilyAxisUnit ?? "", sample.Curve, famValues));
                }
            }
        }

        // A loadpull position (brief-ddl-3) carries the file, group and frequency it was read against.
        LinkPosition? position = LoadpullLinks.PositionOf(site, sources);
        if (position is null && site.Plot.PlotType is PlotType.Smith && (t.IsComplexPlanePlot || t.IsContourTrace || m.FreePosition))
        {
            var at = t.GetMarkerDataLocation(m);
            position = new LinkPosition(new Complex(at.X, at.Y), t.MarkerZ0, Snapped: !m.FreePosition || m.SnappedToCurve);
        }

        return new LinkValue(m.Id.ToString("D"), m.Name, coords, position);
    }

    /// <summary>
    /// One coordinate, as an index into the CUBE's axis. The trace's drawn values may be a narrowed range of it
    /// (<c>Pout[17, 3..9]</c>); the source cube's own axis is what a consumer of the same run compares against, so
    /// the drawn index is offset by the range's start and the whole axis is carried.
    /// </summary>
    private static LinkCoordinate Coordinate(Trace t, DataCube? cube, string group, string axisName, string unit,
                                             int drawnIndex, IReadOnlyList<double> drawn)
    {
        int offset = 0;
        if (t.Slice?.FirstOrDefault(s => s.AxisName == axisName) is { } entry && entry.IsNarrowedRange)
            offset = entry.RangeStart;
        var axis = cube?.Axes.FirstOrDefault(a => a.Name == axisName);
        if (axis is not null && drawnIndex + offset < axis.Length)
            return new LinkCoordinate(t.SourcePath, group, axisName, drawnIndex + offset, axis.Values[drawnIndex + offset],
                                      axis.Unit ?? unit, axis.Values);
        return new LinkCoordinate(t.SourcePath, group, axisName, drawnIndex, drawn[drawnIndex], unit, drawn.ToArray());
    }
}
