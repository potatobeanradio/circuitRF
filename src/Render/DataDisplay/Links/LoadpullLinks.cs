// ================================================================
//  LoadpullLinks.cs  —  a loadpull read at a marker anywhere on the
//  Γ plane (brief-ddl-3)
//
//  A marker on a loadpull's contours, or on its GammaLoad Smith trace,
//  gives a POSITION. A trace on the same loadpull whose gridPoint axis
//  follows it reads:
//    snapped              → the node's index: the measured curve;
//    free, read Nearest   → the nearest node's index, measured;
//    free, read Interp    → a drive-up synthesised by LoadpullSurface.
//  The synthesised curve is a MODEL and is drawn as one (dashed, its
//  label saying "interpolated", the card saying when it is an
//  extrapolation).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using RfCore.Data;
using RfCore.Loadpull;

namespace CircuitRF.Render.DataDisplay;

public static class LoadpullLinks
{
    /// <summary>The loadpull's stored Γ basis — <see cref="LoadpullSurface"/> assumes 50 Ω for its stored grid.</summary>
    public const double StoredZ0 = 50.0;

    // One surface per (DataSet, group), so the RBF stacks a drag builds are there for the next frame. Keyed WEAKLY on
    // the DataSet: a re-run publishes a new DataSet, and its surface is a new one.
    private static readonly ConditionalWeakTable<DataSet, Dictionary<string, LoadpullSurface?>> Surfaces = new();

    /// <summary>The (cached) loadpull surface of <paramref name="group"/> in <paramref name="ds"/>, or null when it has none.</summary>
    public static LoadpullSurface? Surface(DataSet ds, string group)
    {
        var byGroup = Surfaces.GetOrCreateValue(ds);
        lock (byGroup)
        {
            if (byGroup.TryGetValue(group, out var s)) return s;
            try { s = new LoadpullSurface(ds, group); if (s.Frequencies.Count == 0) s = null; }
            catch { s = null; }
            byGroup[group] = s;
            return s;
        }
    }

    // ── the source side ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The loadpull position a marker gives (R-ddl3-1): on a contour trace, wherever it sits; on the GammaLoad trace of a
    /// loadpull on a Smith chart, the load it is snapped to, or — with Snap to Point off — wherever it was put. Null
    /// for any other marker.
    /// </summary>
    public static LinkPosition? PositionOf(LinkDocument.MarkerSite site, IPlotDataSources? sources)
    {
        var (t, m) = (site.Trace, site.Marker);
        if (sources is null || t.SourcePath is not { } path || sources.DataFor(path) is not { } ds) return null;

        if (t.IsContourTrace && t.ContourData is { } cd)
        {
            var views = LoadpullRecognition.FindLoadpullViews(ds);
            if (views.Count == 0) return null;
            string group = ContourResolve.GroupFor(views, cd.LoadpullGroup);
            var at = new Complex(m.PositionStatic.X, m.PositionStatic.Y);
            // A contour on a Smith chart draws in Γ at the trace's reference; on a rectangular plot, in Z.
            Complex z = cd.GammaPlane ? t.Z0 * (1 + at) / (1 - at) : at;
            return new LinkPosition(ToStored(z), StoredZ0, m.ContourSnapped, path, group,
                                    Math.Max(0, cd.FreqIndex), Kernel: cd.InterpKernel, Smooth: cd.Smoothing);
        }

        if (site.Plot.PlotType == PlotType.Smith && IsLoadTrace(t, ds, out var lpGroup))
        {
            bool snapped = !m.FreePosition || m.SnappedToCurve;
            var loc = t.GetMarkerDataLocation(m);
            Complex zRef = t.Z0OverrideEnabled ? t.Z0 : StoredZ0;
            var gamma = new Complex(loc.X, loc.Y);
            int? node = snapped && t.MarkerSampleOf(m) is { } sample ? NodeIndex(t, sample.X) : null;
            return new LinkPosition(ToStored(zRef * (1 + gamma) / (1 - gamma)), StoredZ0, snapped, path, lpGroup,
                                    FreqIndexOf(t), node);
        }
        return null;
    }

    /// <summary>Whether <paramref name="t"/> draws a loadpull's own loads — its <c>GammaLoad</c> cube — and in which group.</summary>
    public static bool IsLoadTrace(Trace t, DataSet ds, out string group)
    {
        group = "";
        if (!t.IsCubeBound || t.CubeName is not { } cn) return false;
        int dot = cn.LastIndexOf('.');
        string bare = dot >= 0 ? cn[(dot + 1)..] : cn;
        if (bare != "GammaLoad") return false;
        group = dot >= 0 ? cn[..dot] : "";
        string g = group;
        return LoadpullRecognition.FindLoadpullViews(ds).Any(v => (v.Group ?? "") == g);
    }

    private static Complex ToStored(Complex z) => (z - StoredZ0) / (z + StoredZ0);

    private static int NodeIndex(Trace t, int drawn)
    {
        var entry = t.Slice?.FirstOrDefault(s => s.AxisName == "gridPoint");
        return entry is { IsNarrowedRange: true } e ? e.RangeStart + drawn : drawn;
    }

    /// <summary>The leading-axis (frequency) pin of a loadpull trace: its index, or 0 for a single-frequency one.</summary>
    private static int FreqIndexOf(Trace t)
        => t.Slice?.FirstOrDefault(s => s.Role == AxisRole.PinToIndex && s.AxisName is not ("gridPoint" or "pinStep"))
               is { AxisName.Length: > 0 } pin ? pin.Index : 0;

    // ── the consumer side ─────────────────────────────────────────────────────────────────────────

    /// <summary>How one loadpull read came out.</summary>
    internal enum ReadKind { NotLoadpull, Node, Synthesised, Deferred }

    /// <summary>
    /// Reads a consumer's <c>gridPoint</c> pin at a loadpull position (R-ddl3-2). Returns the index the pin takes (the
    /// node for a snapped or Nearest read, the stored index for a synthesised one), whether the curve is synthesised —
    /// <paramref name="synthesis"/> then holds what replaces it — or that it waits for the drag to end
    /// (<paramref name="released"/> false and the surface's stacks not yet built, R-ddl3-7).
    /// </summary>
    internal static ReadKind Read(
        Trace t, Plot plot, AxisSlice[] slice, int pinIndex, LinkPosition pos, string sourceName, DataSet ds,
        bool released, List<string> notes, out int index, out Action<Trace, PlotType, FreqUnit>? synthesis)
    {
        index = slice[pinIndex].Index;
        synthesis = null;
        if (!SameLoadpull(t, pos) || LoadpullLinks.Surface(ds, pos.Group ?? "") is not { } surface) return ReadKind.NotLoadpull;
        int freq = Math.Clamp(pos.FreqIndex, 0, surface.Frequencies.Count - 1);

        // R-ddl3-6: the consumer reads at the position's frequency, unless its own frequency pin follows a link.
        for (int i = 0; i < slice.Length; i++)
            if (i != pinIndex && slice[i].Role == AxisRole.PinToIndex && !slice[i].IsLinked
                && slice[i].AxisName is not ("gridPoint" or "pinStep"))
                slice[i] = slice[i] with { Index = freq };

        int nearest = pos.Node ?? surface.NearestGridPoint(freq, pos.Gamma);
        if (nearest < 0) return ReadKind.NotLoadpull;
        if (pos.Snapped) { index = nearest; return ReadKind.Node; }

        string bare = Bare(t.CubeName!);
        if (plot.TableReadMode == TableReadMode.Nearest)
            return Nearest(surface, freq, nearest, pos, sourceName, notes, null, out index);

        string metricY = Canonical(bare);
        string metricX = t.IsVersus && t.XSpec is { } xs ? Canonical(Bare(xs)) : LoadpullSurface.SweepKeyName;
        if (!surface.HasMetric(freq, metricY))
            return Nearest(surface, freq, nearest, pos, sourceName, notes,
                           $"{bare} is not a fitted metric; this trace takes the nearest node.", out index);
        if (!surface.HasMetric(freq, metricX))
            return Nearest(surface, freq, nearest, pos, sourceName, notes,
                           $"{Bare(t.XSpec ?? metricX)} is not a fitted metric; this trace takes the nearest node.", out index);

        double comp = plot.TableCompression;
        if (!released && !surface.HasPowerSweepStacks(freq, metricX, metricY, comp, SurfacePlane.Gamma, null,
                                                       pos.Kernel, pos.Smooth, plot.TableCompressionReference,
                                                       plot.TableCompressionType))
        {
            notes.Add("Updates on release.");
            return ReadKind.Deferred;
        }

        var ps = surface.GetPowerSweep(freq, pos.Gamma, metricX, metricY, comp, SurfacePlane.Gamma, null,
                                       pos.Kernel, pos.Smooth, plot.TableCompressionReference, plot.TableCompressionType);
        if (ps is null)
            return Nearest(surface, freq, nearest, pos, sourceName, notes,
                           $"Too few loads reach {comp:0.#} dB compression to interpolate; this trace takes the nearest node.",
                           out index);

        // Only the samples both halves have.
        var xsOut = new List<double>(ps.X.Length);
        var ysOut = new List<double>(ps.Y.Length);
        for (int i = 0; i < ps.X.Length; i++)
            if (double.IsFinite(ps.X[i]) && double.IsFinite(ps.Y[i])) { xsOut.Add(ps.X[i]); ysOut.Add(ps.Y[i]); }
        double[] x = [.. xsOut], y = [.. ysOut];
        string xName = metricX == LoadpullSurface.SweepKeyName ? "available input power" : Bare(t.XSpec ?? metricX);
        string xUnit = UnitOf(metricX);
        string label = $"interpolated, Γ {pos.Gamma.Magnitude:0.##}∠{pos.Gamma.Phase * 180 / Math.PI:0}°";

        double outside = surface.DistanceOutsideMeasured(freq, pos.Gamma);
        if (outside > 1e-9)
            notes.Add(double.IsPositiveInfinity(outside)
                ? $"Extrapolated: too few loads were measured to say where {sourceName} is."
                : $"Extrapolated: {sourceName} is {outside:0.###} (|ΔΓ|) outside the measured loads.");

        synthesis = (tr, plotType, freqUnit) =>
        {
            tr.SetCubeData(x, null, y, xName, xUnit, plotType, freqUnit, transformBaked: true);
            tr.ExpressionError  = null;
            tr.InvalidSpecText  = null;
            tr.IsSynthesized    = true;
            tr.SynthesisLabel   = label;
        };
        return ReadKind.Synthesised;
    }

    private static ReadKind Nearest(LoadpullSurface surface, int freq, int node, LinkPosition pos, string sourceName,
                                    List<string> notes, string? why, out int index)
    {
        index = node;
        if (why is not null) notes.Add(why);
        double d = Complex.Abs(surface.GridCoord(freq, node) - pos.Gamma);
        notes.Add($"{sourceName} reads the nearest measured load, gridPoint {node} (|ΔΓ| {d:0.###}).");
        return ReadKind.Node;
    }

    /// <summary>Whether the consumer reads the same loadpull — the same file and the same group.</summary>
    private static bool SameLoadpull(Trace t, LinkPosition pos)
        => pos.IsLoadpull && t.CubeName is { } cn
           && string.Equals(t.SourcePath, pos.SourcePath, StringComparison.OrdinalIgnoreCase)
           && (cn.LastIndexOf('.') is var dot && dot >= 0 ? cn[..dot] : "") == (pos.Group ?? "");

    private static string Bare(string spec)
    {
        int b = spec.IndexOf('[');
        string name = (b >= 0 ? spec[..b] : spec).Trim();
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    /// <summary>A cube's name as the surface knows it — through the dialect table the contour and summary paths use.</summary>
    private static string Canonical(string bare)
        => LoadpullFomDialect.Map.TryGetValue(bare, out var e) ? e.CanonicalName : bare;

    private static string UnitOf(string metric)
        => metric == LoadpullSurface.SweepKeyName || metric.EndsWith("dBm", StringComparison.Ordinal) ? "dBm"
         : metric.EndsWith("_dB", StringComparison.Ordinal) ? "dB"
         : metric is "Efficiency" or "PAE" ? "%" : "";
}
