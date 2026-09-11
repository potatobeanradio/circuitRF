// ================================================================
//  TraceResolve.cs  —  turning a trace's authored SPEC into the points
//  it draws, against a DataSet
//
//  RND-4 (brief-render-4-data-display.md R-rnd4-2). Every line of this
//  file was `PlotInspectorViewModel`'s until this brief, and none of it
//  is view-model code: it takes a Trace and a DataSet and fills the
//  trace's Points, FamilyCurves, axis names and error text. It moved
//  here rather than being re-implemented in `src/Cli`, because a
//  headless plot that resolves its traces *differently* from the window
//  is the single worst output this series can produce — it is a
//  plausible picture, and nobody can see that it is wrong.
//
//  WHAT STAYED IN THE VIEW MODEL is everything that needs a LIBRARY: the
//  `TrySetCubeData` lookup that turns a trace's SourcePath into an entry,
//  the picker lists, the undo stack, the notifications.
//  `SetCubeDataFrom` — a DataSet already in hand — is the seam that was
//  already there, cut for harmonicaRF (R-h7-5), and `src/Cli` resolves
//  through the same one.
//
//  THE `Note` SEAM. This code reports a failed resolve to the crash
//  trail, which lives in src/Ui and cannot be reached from here. It is a
//  delegate, installed by `DataDisplayDiagnosticsInstaller`; unset — in a
//  test, or in the CLI — a failed resolve still ends as "<invalid>" on
//  the trace, which is the behaviour that mattered.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using NumFlat;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;
using SkiaSharp;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Render.DataDisplay;

/// <summary>
/// The resolution half of a Data Display trace: spec → cube → points.
/// See the file header for what deliberately did NOT come here.
/// </summary>
public static class TraceResolve
{
    /// <summary>
    /// Resolves a cube-bound trace against a source provider: finds its DataSet (and, for a "plot
    /// versus" trace, its X side's), stamps the cross-source alias, and calls
    /// <see cref="SetCubeDataFrom"/>.
    ///
    /// <para>This is <c>PlotInspectorViewModel.TrySetCubeData</c> with the library replaced by
    /// <see cref="IPlotDataSources"/> (RND-4 R-rnd4-2) — the view model calls it over the live
    /// library, <c>circuitrf render</c> calls it over the files a caller named, and there is one
    /// lookup rather than two.</para>
    /// </summary>
    public static void ResolveCubeTrace(Trace t, IPlotDataSources sources,
                                        PlotType plotType, FreqUnit freqUnit)
    {
        if (!t.IsCubeBound) return;

        // NOTE the ordering below: DataFor is not a field read. The first touch of a source's
        // DataSet MATERIALIZES the group's virtual Z and Y cubes, which means N matrix inversions —
        // real work that can fail. It used to be evaluated in the argument list of SetCubeDataFrom,
        // OUTSIDE that method's own containment, so a failure there was fatal while the identical
        // failure one line later was not. Resolving a trace is a read either way.
        DataSet? ds = t.SourcePath is { } p ? sources.DataFor(p) : null;

        // "Plot versus" may take its X from a DIFFERENT loaded file (measured Pout against simulated
        // Gain). Null XSourcePath — the ordinary case — means "same source as Y".
        bool crossSource = t.XSourcePath is { } xp
                        && !string.Equals(xp, t.SourcePath, StringComparison.OrdinalIgnoreCase)
                        && sources.Contains(xp);
        DataSet? xDs = t.XSourcePath is { } xpath ? sources.DataFor(xpath) : ds;

        // Display-only: a cross-source X reads as "alias::Pout" in the spec box and table header.
        t.XSourceAlias = t.IsVersus && crossSource
            ? (sources.AliasFor(t.XSourcePath!) is { Length: > 0 } a
                ? a
                : System.IO.Path.GetFileNameWithoutExtension(
                      sources.DisplayNameFor(t.XSourcePath!) ?? t.XSourcePath!))
            : null;

        SetCubeDataFrom(t, ds, plotType, freqUnit, xDs);
    }

    /// <summary>
    /// The same resolution, against a <see cref="DataSet"/> already in hand rather than one looked up
    /// in the library.
    ///
    /// <para><b>Split out for harmonicaRF (R-h7-5).</b> harmonicaRF publishes its own <c>DataSet</c>
    /// on each solved frame and has no data-source library at all — it is a live instrument, not a
    /// document over files on disk. Everything below the lookup is identical, which is the point:
    /// there is one slicing implementation, and the picker over harmonicaRF's cubes is the same code
    /// the <c>.cdd</c> trace card runs.</para>
    /// </summary>
    public static void SetCubeDataFrom(Trace t, DataSet? ds, PlotType plotType, FreqUnit freqUnit,
                                        DataSet? xDs = null)
    {
        var probe = new ResolveProbe();
        try
        {
            SetCubeDataFromCore(t, ds, plotType, freqUnit, xDs, probe);
        }
        catch (Exception ex)
        {
            // Resolving a trace against a data source is a READ. Every foreseeable way it can fail —
            // missing source, missing cube, wrong rank, unparseable spec — already ends as
            // "<invalid>" on the trace card, so an UNforeseen one has no business taking the session
            // down with it: the user loses an unsaved workspace over a curve that could simply have
            // said it could not be drawn.
            //
            // This exists because a reproducible field crash (Windows, 1.0.0-beta.7) landed here as a
            // bare IndexOutOfRangeException out of DataCube's gather, and the report named only the
            // reader — no source, no cube, no slice. The trail note below is what turns the next one
            // into a diagnosis: it records exactly which cube was being sliced and with what.
            //
            // The STACK is the half the first instrumented report (1.0.0-beta.8) still lacked, and it
            // is the half that ends the hunt: every reported failure so far names a well-formed cube
            // and an in-range slice, so the throw is somewhere in this method OTHER than the read the
            // stack was assumed to name. Recording it costs one string on a path that is already
            // failing.
            DataDisplayDiagnostics.Note($"trace resolve FAILED: {DescribeCubeResolve(t, ds, plotType, probe)} — "
                                           + $"{DescribeException(ex)}");
            t.ExpressionError = ex.Message;
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
        }
    }

    /// <summary>
    /// What the failing read was ACTUALLY handed — the one thing the note could not otherwise say.
    ///
    /// <para>Everything else in the note is authored trace state: <c>t.Slice</c> is what the trace
    /// asks for, not what <see cref="DataCube.Slice"/> received, and the shape is re-read from the
    /// DataSet in the catch handler rather than captured at the moment of the throw. The round-5
    /// field report is exactly the case where those two must be compared: the cube it describes is
    /// well formed and the slice is in range, and on that cube the gather provably cannot walk off
    /// its buffer — so the interesting question became whether the cube the READ saw is the same
    /// object, with the same buffer, as the one the note goes on to describe.</para>
    ///
    /// <para>Recorded by reference only. Nothing is formatted on the success path, so a resolve that
    /// works pays two field writes and no allocation.</para>
    /// </summary>
    private sealed class ResolveProbe
    {
        /// <summary>The cube handed to the indexer — after any network-parameter substitution.</summary>
        public DataCube? Cube;

        /// <summary>The positional slice arguments handed with it.</summary>
        public object[]? Args;

        /// <summary>
        /// WHICH indexer call site was reached last, and how many reads the resolve had performed.
        ///
        /// <para>A resolve is not one slice. The main read is followed, on a spectral source, by
        /// <see cref="ResolveFundamentalByX"/> slicing <c>ToneFreqs</c> once per X point, and a family
        /// trace slices once per curve — none of which were recorded, so a throw in any of them was
        /// reported under the main read's cube and arguments. On an S-parameter source those later
        /// paths do not run (there is no <c>ToneFreqs</c> cube), which is why the two 2026-09-03
        /// trails are unambiguous; on an HB or loadpull source they do, and the note has to say so
        /// rather than leave it to be inferred from the group inventory.</para>
        /// </summary>
        public string? Site;

        /// <summary>Reads attempted, so a failure in a loop says which iteration.</summary>
        public int Slices;

        /// <summary>Records a read about to happen. Two field writes and an increment.</summary>
        public void Reading(string site, DataCube cube, object[] args)
        {
            Site = site; Cube = cube; Args = args; Slices++;
        }
    }

    /// <summary>
    /// One line naming everything the crash trail needs to identify a failed resolve: the cube spec,
    /// the shape the DataSet actually holds for it, and the slice the trace asked for. Every step is
    /// individually guarded — this runs while something is already wrong, so it must not throw.
    /// </summary>
    private static string DescribeCubeResolve(Trace t, DataSet? ds, PlotType plotType, ResolveProbe probe)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"plot={plotType} cube='{t.CubeName ?? "(null)"}' expr='{t.Expression ?? "(null)"}'");

        try
        {
            if (ds is not null && t.CubeName is { } name && ds.Contains(name))
            {
                var c = ds[name];
                sb.Append($" shape=[{DescribeShape(c)}]");
                sb.Append($" kind={c.DataKind}");
            }
            else sb.Append(" shape=(cube not in source)");
        }
        catch (Exception e) { sb.Append($" shape=(unreadable: {e.GetType().Name})"); }

        // The slice's RANGE fields are printed too. A narrowed X axis is stored beside the pin index,
        // not in it, so the first instrumented reports could not tell a whole-axis X from a narrowed
        // one — and a narrowing is exactly the kind of carried-over state that outlives the shape it
        // was authored against.
        try
        {
            sb.Append(t.Slice is { } s
                ? $" slice=[{string.Join(", ", s.Select(DescribeAxisSlice))}]"
                : " slice=(null)");
        }
        catch (Exception e) { sb.Append($" slice=(unreadable: {e.GetType().Name})"); }

        // What the READ was handed, as opposed to what the trace asked for. `buf` vs `expect` is the
        // pair that matters: every slice validates the buffer against the axes product before it
        // gathers, so a report where these two disagree is a report where that validation and the
        // gather are looking at different state. `same` says whether the cube the read saw is still
        // the object this note's own `shape=` field describes.
        try
        {
            if (probe.Cube is { } rc)
            {
                long expect = 1;
                foreach (var a in rc.Axes) expect *= a.Length;
                sb.Append($" read=[{DescribeShape(rc)}] buf={rc.BufferLength} expect={expect}");
                sb.Append($" same={(ds is not null && t.CubeName is { } cn && ds.Contains(cn)
                                     && ReferenceEquals(ds[cn], rc) ? "yes" : "no")}");
                // The identity DataCube's own gather message also prints. S, Z and Y in one group
                // share a shape, so this is the only field that says which cube actually threw.
                sb.Append($" id=0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(rc):x8}");
                sb.Append($" site={probe.Site ?? "(none)"} slices={probe.Slices}");
            }
            else sb.Append(" read=(no slice reached)");
        }
        catch (Exception e) { sb.Append($" read=(unreadable: {e.GetType().Name})"); }

        try
        {
            sb.Append(probe.Args is { } pa
                ? $" args=[{string.Join(", ", pa.Select(DescribeSliceArg))}]"
                : " args=(none)");
        }
        catch (Exception e) { sb.Append($" args=(unreadable: {e.GetType().Name})"); }

        // The rest of the trace state this resolve actually branches on. Every one of these selects a
        // different path through SetCubeDataFromCore, and none of them was in the first report.
        try
        {
            sb.Append($" transform={t.Transform} z0={t.Z0.Real:G6}{(t.Z0.Imaginary >= 0 ? "+" : "-")}j{Math.Abs(t.Z0.Imaginary):G6}");
            sb.Append($" override={(t.Z0OverrideEnabled ? "on" : "off")}");
            sb.Append($" srcZ0={(t.SourceZ0PerPort is { } z ? z.Length.ToString() : "(null)")}");
            sb.Append($" versus={(t.XSpec is { } xs ? $"'{xs}'" : "(none)")}");
            sb.Append($" family={t.FamilyCurves.Count} markers={t.Markers.Count}");
            sb.Append($" src='{SafeFileName(t.SourcePath)}'");
        }
        catch (Exception e) { sb.Append($" state=(unreadable: {e.GetType().Name})"); }

        // What the group actually holds. "The cube resolved" and "the group is well-formed" are
        // different claims, and the S/Z0 pair is what every network-parameter branch reads.
        try
        {
            if (ds is not null && t.CubeName is { } n2)
            {
                int dot = n2.LastIndexOf('.');
                string group = dot < 0 ? RfCore.Data.DataSet.DefaultGroup : n2[..dot];
                if (ds.ContainsGroup(group))
                    sb.Append($" group[{group}]={{{string.Join(", ",
                        ds.CubesIn(group).Select(kv => $"{kv.Key}:[{DescribeShape(kv.Value)}]:{kv.Value.DataKind}"))}}}");
            }
        }
        catch (Exception e) { sb.Append($" group=(unreadable: {e.GetType().Name})"); }

        return sb.ToString();
    }

    private static string DescribeShape(DataCube c)
        => string.Join(" x ", c.Axes.Select(a => $"{a.Name}[{a.Length}]"));

    /// <summary>One positional slice argument as the indexer sees it: an int pins, a Range keeps.</summary>
    private static string DescribeSliceArg(object? a) => a switch
    {
        int i     => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Range r   => r.Equals(Range.All) ? "All" : $"{r.Start}..{r.End}",
        null      => "(null)",
        _         => $"({a.GetType().Name})",
    };

    private static string DescribeAxisSlice(AxisSlice a)
    {
        string s = $"{a.AxisName}:{a.Role}:{a.Index}";
        if (a.IsNarrowedRange) s += $"({a.RangeStart}..{a.RangeEndExclusive})";
        if (!string.IsNullOrEmpty(a.Label)) s += $"\"{a.Label}\"";
        return s;
    }

    internal static string SafeFileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "(null)";
        try { return System.IO.Path.GetFileName(path); } catch { return "(unreadable)"; }
    }

    /// <summary>
    /// The exception, its inner chain, and its stack — the one thing the previous round's trail note
    /// left out, and the only thing that can name the throwing line when (as here) every value the
    /// note DOES print is in range. Frames are capped so one failure cannot flood the ring-buffered
    /// trail, and every step is guarded because this runs while something is already wrong.
    /// </summary>
    internal static string DescribeException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                sb.Append(ReferenceEquals(e, ex) ? "" : " <- ");
                sb.Append($"{e.GetType().Name}: {e.Message}");
            }

            // The stack of the DEEPEST exception, not the outermost. A wrapper that adds context —
            // DataCube's gather now rethrows with the bounds arithmetic attached — carries the stack
            // of the rethrow site, which is the one frame nobody needs. The throwing line lives on
            // the innermost.
            Exception deepest = ex;
            while (deepest.InnerException is { } inner) deepest = inner;

            var frames = (deepest.StackTrace ?? ex.StackTrace ?? "").Split('\n')
                .Select(f => f.TrimEnd('\r').Trim())
                .Where(f => f.Length > 0)
                .Take(16);
            foreach (var f in frames) sb.Append("\n    ").Append(f);
        }
        catch (Exception e) { sb.Append($" (exception unreadable: {e.GetType().Name})"); }
        return sb.ToString();
    }

    private static void SetCubeDataFromCore(Trace t, DataSet? ds, PlotType plotType, FreqUnit freqUnit,
                                            DataSet? xDs, ResolveProbe probe)
    {
        // BEFORE the guard, and before any data is bound. Before the guard because a trace that has
        // just STOPPED being cube-bound must not keep the previous cube's reference; before the bind
        // because the reference shifts the displayed VALUE, and SetCubeData builds the path — a
        // reference stamped afterwards would not be in the geometry until the next rebuild.
        ApplyReferenceLevel(t, ds);

        if (!t.IsCubeBound) return;

        // Derived from the cube, so it cannot outlive the resolve that produced it — the same
        // clear-first contract SetPinnedAxisDisplay has. Re-stamped below once a cube is in hand.
        t.CubeValueUnit = null;

        // "Plot versus": the X side resolves against its own source when one is set, else against
        // the Y side's. Everything below reads xDataSet, so cross-source is not a second code path.
        DataSet? xDataSet = xDs ?? ds;

        // A malformed separator ("A vs B vs C", "Gain vs") is reported HERE as well as at the point
        // of typing, because every edit is followed by a resolve that would otherwise replace the
        // card's message with a confusing downstream parse error.
        if (t.Expression is { } exprText
            && !VersusSpec.TrySplit(exprText, out _, out _, out var splitErr)
            && splitErr.Length > 0)
        {
            t.ExpressionError = splitErr;
            t.InvalidSpecText = exprText;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        // Versus is a rectangular idea: a Γ-plane locus has no X axis to redirect.
        if (t.IsVersus && plotType.IsComplex())
        {
            t.ExpressionError = "'vs' is available on Rect and Table plots only.";
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        // Single-cube specs (picker or typed Name[...]) resolve via the slice path (family-aware).
        // Only multi-cube element-wise expressions go through TraceExpression.
        bool singleCube = t.CubeName is not null && t.Slice is not null;

        // ── Expression path (element-wise multi-cube expressions) ─────────────
        if (t.Expression is not null && !singleCube)
        {
            if (ds is null)
            {
                t.Points.Clear();
                return;
            }
            // Only the Y half goes to the evaluator; the X half is resolved separately below.
            string yExpr = VersusSpec.TrySplit(t.Expression, out var ySide, out _, out _)
                ? ySide : t.Expression;
            if (TraceExpression.TryEvaluate(yExpr, ds, plotType,
                    out var xVals, out var cz, out var rz,
                    out var xName, out var xUnit, out var xLabels, out var exprErr))
            {
                if (t.IsVersus)
                {
                    int yN = cz?.Length ?? rz?.Length ?? 0;
                    if (!TryVersusX(t, xDataSet, ySlice: null, yExpr, yN, out var vx, out var vErr))
                    {
                        t.ExpressionError = vErr;
                        t.InvalidSpecText = t.Expression;
                        t.Points.Clear();
                        return;
                    }
                    xVals = vx; xName = t.XSpec!; xUnit = null; xLabels = null;
                }
                t.ExpressionError = null;
                t.InvalidSpecText = null;
                t.SetSpectrumFundamentals(null);
                // The multi-cube expression text already encodes any transform — mark the values baked so a
                // real result renders as-is (the transform combo must not double-apply on top of it).
                t.SetCubeData(xVals, cz, rz, xName, xUnit, plotType, freqUnit, xLabels, transformBaked: true);
                if (!t.IsVersus)
                {
                    ApplyPinnedSpectral(t, ds);
                    ApplyPinnedAxisDisplay(t, ds, freqUnit);
                }
            }
            else
            {
                t.ExpressionError = exprErr;
                t.InvalidSpecText = t.Expression;
                t.Points.Clear();
            }
            return;
        }

        // ── Single-slice path (picker-authored, no Expression) ────────────────
        if (ds is null || t.CubeName is null || !ds.Contains(t.CubeName))
        {
            // Cube unavailable in the (new) source → surface "<spec> <invalid>" on the label rather
            // than a silently-empty trace. Cleared below once the cube resolves again.
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();   // family geometry must vanish too (and stop feeding Autoscale)
            return;
        }

        var cube  = ds[t.CubeName];
        var slice = t.Slice;
        if (slice is null)
        {
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        // brief-dd-z0-renormalization.md §1: a network-parameter cube trace (S/Z/Y element) renders
        // at the trace's OWN reference Z0 rather than the source's. This is the single interception
        // point that feeds Rect/Smith/Polar/Table AND every marker/table readout downstream (via
        // _cubeComplexValues) — do not duplicate this renorm anywhere else in the cube path.
        if (RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, t.CubeName))
            cube = ResolveNetworkParamCube(ds, t, t.CubeName, cube);

        // WSP-4: a WSProbe trace's CubeName is the run's raw `wsp` matrix, and its VALUES are a
        // metric of it — one library call per leading-axis point, at the SAME interception point,
        // and for the same reason: everything below reads `cube`, so the slice, the family
        // mechanism, the markers, the Table, the export and `render --data` need no probe-specific
        // path at all. A probe that is not in this run says so on the card rather than drawing an
        // empty curve (R-rnd4-4's rule).
        if (t.IsWspTrace)
        {
            if (!WspSource.TryEvaluate(ds, t.CubeName, t.Wsp!, out var wspCube, out string wspErr))
            {
                t.ExpressionError = wspErr;
                t.InvalidSpecText = t.CubeName;
                t.Points.Clear();
                t.FamilyCurves.Clear();
                return;
            }
            cube = wspCube!;

            // The run's own MarginThreshold, for the rect plot's dashed reference line. A source
            // written before the engine reported it simply has no cube, and the trace keeps NaN —
            // the −12 dB floor is still drawn, since that one is arithmetic and not a setting.
            string grp = WspSource.GroupOf(t.CubeName);
            string thrSpec = grp == RfCore.Data.DataSet.DefaultGroup
                ? "__WspMarginThreshold" : $"{grp}.__WspMarginThreshold";
            t.WspMarginThresholdDb = ds.Contains(thrSpec) && ds[thrSpec].BufferLength > 0
                ? ds[thrSpec].RealValues[0]
                : double.NaN;
        }

        // From here down, `cube` is what the indexer will read. Record it now so the crash note
        // describes the object the gather saw rather than re-deriving one from the DataSet later.
        probe.Cube = cube;

        // ANT-7 §2 — the cube's own VALUE unit, which is what a pattern plot's radial numbers are in.
        // Stamped here rather than passed through SetCubeData beside the AXIS unit, because it is a
        // property of the cube and not of the slice, and because every SetCubeData call site below
        // (family, versus, scalar) would otherwise have to carry it and one of them would forget.
        t.CubeValueUnit = string.IsNullOrWhiteSpace(cube.Unit) ? null : cube.Unit;

        // Cube + slice resolved → clear any stale invalid flag left by a prior bad source
        // (covers the scalar, family, all-pinned, and rank-1 success paths below).
        t.InvalidSpecText = null;
        t.ExpressionError = null;

        // Scalar cube (rank 0): operating-point value — valid only on a Table (Part A).
        if (cube.Rank == 0)
        {
            if (RejectVersusOnScalar(t)) return;
            probe.Reading("scalar-rank0", cube, Array.Empty<object>());
            var sr = cube[probe.Args!];
            t.InvalidSpecText = null;
            t.ExpressionError = null;
            t.SetScalarCubeData(
                sr.IsComplex ? sr.ComplexValue : (System.Numerics.Complex?)null,
                sr.IsReal    ? sr.RealValue    : (double?)null,
                plotType, freqUnit);
            return;
        }

        // ── ANT-10: the 3D surface, BEFORE the family path ────────────────────
        //
        //  Before, because a two-':' spec already resolves as a family under the positional
        //  convention (the earlier kept axis becomes the family) and would therefore draw 101 polar
        //  cuts stacked on a plot that has no disc to draw them on. The surface reads the same cube
        //  through its own rank-2 slice, and it finds its two axes by NAME so the convention never
        //  decides which of θ and φ is which.
        if (plotType == PlotType.Surface3D)
        {
            SurfaceResolve.Resolve(t, cube, slice);
            t.Points.Clear();
            t.FamilyCurves.Clear();
            ApplyPinnedAxisDisplay(t, ds, freqUnit);
            return;
        }

        // ── Family path (Phase 7.3b) ──────────────────────────────────────────
        if (Array.Exists(slice, s => s.Role == AxisRole.FamilyIterate))
        {
            ResolveFamily(t, cube, slice, plotType, freqUnit, ds, xDataSet, probe);
            return;
        }

        // Build indexer args matched by axis NAME (Phase 7.3a: order-independent).
        // Missing slice entries default to PinToIndex/0; extra slice entries are ignored.
        var args = new object[cube.Rank];
        int xDim = -1;
        for (int d = 0; d < cube.Rank; d++)
        {
            var axName = cube.Axes[d].Name;
            AxisSlice? found = null;
            foreach (var s in slice)
            {
                if (s.AxisName == axName) { found = s; break; }
            }
            if (found?.Role == AxisRole.KeepAsX)
            {
                args[d] = found.Value.IsNarrowedRange
                    ? new Range(found.Value.RangeStart, found.Value.RangeEndExclusive)
                    : Range.All;
                xDim = d;
            }
            else
            {
                int idx = Math.Clamp(found?.Index ?? 0, 0, Math.Max(0, cube.Axes[d].Length - 1));
                args[d] = idx;
            }
        }

        // No axis is X → every axis is pinned → scalar (operating-point value).
        // Renders on a Table; <invalid> on Rect/Smith/Polar (handled by SetScalarCubeData).
        if (xDim < 0)
        {
            if (RejectVersusOnScalar(t)) return;
            probe.Reading("scalar-all-pinned", cube, args);
            var sr = cube[args];
            t.InvalidSpecText = null;
            t.ExpressionError = null;
            t.SetScalarCubeData(
                sr.IsComplex ? sr.ComplexValue : (System.Numerics.Complex?)null,
                sr.IsReal    ? sr.RealValue    : (double?)null,
                plotType, freqUnit);
            return;
        }

        probe.Reading("main", cube, args);
        var result = cube[args];
        if (!result.IsCube)
        {
            t.Points.Clear();
            return;
        }

        var sliced = result.Cube!;
        if (sliced.Rank != 1)
        {
            t.Points.Clear();
            return;
        }

        var xAxis = sliced.Axes[0];
        Complex[]? complexValues = sliced.DataKind == DataKind.Complex ? sliced.ComplexValues : null;
        double[]?  realValues    = sliced.DataKind == DataKind.Real    ? sliced.RealValues    : null;

        // ── Plot versus: X comes from the X spec, not from this cube's swept axis ──
        if (t.IsVersus)
        {
            int yN = complexValues?.Length ?? realValues?.Length ?? 0;
            if (!TryVersusX(t, xDataSet, slice, t.CubeName!, yN, out var vx, out var vErr))
            {
                t.ExpressionError = vErr;
                t.InvalidSpecText = t.Expression ?? t.CubeName;
                t.Points.Clear();
                t.FamilyCurves.Clear();
                return;
            }
            t.SetSpectrumFundamentals(null);
            // No unit: cube VALUES carry none anywhere in the data model (only axes do), so a versus
            // X axis is labelled by its spec text alone — same as every Y label already is.
            t.SetCubeData(vx, complexValues, realValues, t.XSpec!, null, plotType, freqUnit);
            ApplyPinnedAxisDisplay(t, ds, freqUnit);
            return;
        }

        // ── THE WHOLE PLANE, GATHERED AS ONE TRACE (owner, 2026-09-11) ────────────────────────
        //
        //  A cut is a PLANE and a plane crosses the disc, so the picture wants both azimuths. The
        //  companion is an ordinary second rank-1 read of the SAME cube with one index moved, and
        //  it is taken here — beside the gather it belongs to — rather than by a second resolve
        //  path (ANT-7 §3's rule, which this does not break: there is still no single slice that
        //  carries both, and there is still no derived cube).
        Complex[]? backComplex = null;
        double[]?  backReal    = null;
        double     backPhi     = double.NaN;
        if (t.PatternWholePlane && plotType == PlotType.Polar)
        {
            if (TryWholePlaneCompanion(cube, args, xDim, probe, out backComplex, out backReal,
                                       out backPhi, out string? wpErr))
            {
                // Nothing to do — the values are out.
            }
            else
            {
                t.ExpressionError = wpErr;
                t.InvalidSpecText = t.Expression ?? t.CubeName;
                t.Points.Clear();
                t.FamilyCurves.Clear();
                return;
            }
        }

        var toneFreqs1 = GetToneFreqsCube(ds, t.CubeName);
        t.SetSpectrumFundamentals(ResolveFundamentalByX(toneFreqs1, slice, xAxis.Values.Length, probe));
        t.SetCubeData(xAxis.Values, complexValues, realValues,
                      xAxis.Name, xAxis.Unit, plotType, freqUnit, xAxis.Labels,
                      backComplex: backComplex, backReal: backReal, backPhiDeg: backPhi);
        ApplyPinnedSpectral(t, ds);
        ApplyPinnedAxisDisplay(t, ds, freqUnit);
    }

    /// <summary>
    /// The &#966;&#160;+&#160;180&#176; half of a whole-plane cut: the same indexer args with the
    /// pinned azimuth moved to the antipodal grid index.
    ///
    /// <para><b>The azimuth is found by NAME and its companion by VALUE</b>, never by index
    /// arithmetic. A &#966; axis is not obliged to be uniform, to start at zero or to be 360 long —
    /// the far-field grid is all three today and none of that is a property the data model
    /// promises — so the companion is the index whose &#966; is nearest &#966;&#160;+&#160;180
    /// modulo 360, and it is refused when the nearest is not actually within half a grid step. A
    /// silently-wrong companion here draws a smooth, plausible, wrong pattern.</para>
    /// </summary>
    private static bool TryWholePlaneCompanion(
        DataCube cube, object[] args, int xDim, ResolveProbe probe,
        out Complex[]? backComplex, out double[]? backReal, out double backPhiDeg, out string? error)
    {
        backComplex = null; backReal = null; backPhiDeg = double.NaN; error = null;

        int phiDim = -1;
        for (int d = 0; d < cube.Rank; d++)
            if (d != xDim && cube.Axes[d].Name is "phi" or "az") { phiDim = d; break; }

        if (phiDim < 0 || args[phiDim] is not int phiIdx)
        {
            error = WholePlaneNoAzimuthRefusal(cube, xDim);
            return false;
        }

        var phi = cube.Axes[phiDim];
        if (phi.Length < 2) { error = WholePlaneNoAzimuthRefusal(cube, xDim); return false; }

        double want = Mod360(phi.Values[phiIdx] + 180.0);
        int    best = -1;
        double bestGap = double.MaxValue;
        for (int k = 0; k < phi.Length; k++)
        {
            double gap = Math.Abs(Mod360(phi.Values[k] - want + 180.0) - 180.0);
            if (gap < bestGap) { bestGap = gap; best = k; }
        }

        // Half a grid step: the widest miss that is still "this IS the antipodal sample, quantised".
        double step = Math.Abs(phi.Values[^1] - phi.Values[0]) / Math.Max(1, phi.Length - 1);
        if (best < 0 || bestGap > Math.Max(step * 0.5, 1e-9))
        {
            error =
                $"The whole-plane cut cannot be drawn: this trace is pinned to {phi.Name} = "
              + $"{phi.Values[phiIdx].ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}, and the other "
              + $"half of that plane is at {want.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}, "
              + $"which the cube's own '{phi.Name}' axis does not sample — the nearest it has is "
              + $"{(best >= 0 ? phi.Values[best].ToString("G6", System.Globalization.CultureInfo.InvariantCulture) : "none")}"
              + $", {bestGap.ToString("G3", System.Globalization.CultureInfo.InvariantCulture)}° away against a "
              + $"{step.ToString("G3", System.Globalization.CultureInfo.InvariantCulture)}° grid. Drawing the nearest "
              + "sample instead would put a smooth and entirely plausible curve on the wrong half "
              + "of the disc. Turn the whole-plane option off and the front half draws on its own, "
              + "or take the pattern on a grid that covers the full azimuth.";
            return false;
        }

        var backArgs = (object[])args.Clone();
        backArgs[phiDim] = best;
        probe.Reading("whole-plane-back", cube, backArgs);
        var r = cube[backArgs];
        if (!r.IsCube || r.Cube!.Rank != 1)
        {
            error = WholePlaneNoAzimuthRefusal(cube, xDim);
            return false;
        }

        backComplex = r.Cube.DataKind == DataKind.Complex ? r.Cube.ComplexValues : null;
        backReal    = r.Cube.DataKind == DataKind.Real    ? r.Cube.RealValues    : null;
        backPhiDeg  = phi.Values[best];
        return true;
    }

    private static double Mod360(double d) => ((d % 360.0) + 360.0) % 360.0;

    private static string WholePlaneNoAzimuthRefusal(DataCube cube, int xDim) =>
        "The whole-plane cut needs an AZIMUTH axis pinned to one value, and this trace has none: "
      + "the other half of a cut is the same sweep taken at φ + 180°, so there has to be a φ to "
      + "move. This cube's axes are "
      + string.Join(", ", cube.Axes.Select((a, d) => d == xDim ? $"{a.Name} (the swept one)" : a.Name))
      + ". A cut through a pattern is θ swept with φ pinned; turn the whole-plane option off for "
      + "any other shape of trace.";

    /// <summary>
    /// Resolves the X side of a versus trace and gates it on the point count — the rule that makes a
    /// cross-source X safe (two files whose sweeps disagree can never be silently paired).
    /// </summary>
    private static bool TryVersusX(Trace t, DataSet? xDs, AxisSlice[]? ySlice, string ySpec, int yN,
                                   out double[] xValues, out string error)
    {
        xValues = Array.Empty<double>();
        if (xDs is null)
        {
            error = $"X source for '{t.XSpec}' is not loaded.";
            return false;
        }
        if (!VersusResolver.TryResolveX(t.XSpec!, xDs, ySlice, out xValues, out error))
        {
            if (string.IsNullOrEmpty(error)) error = $"Cannot resolve X side '{t.XSpec}'.";
            return false;
        }
        return VersusResolver.CountsAgree(t.XSpec!, ySpec, xValues.Length, yN, out error);
    }

    /// <summary>A fully-pinned (scalar) Y has no swept axis to plot against. Reports it rather than
    /// rendering a one-point curve at a meaningless X.</summary>
    private static bool RejectVersusOnScalar(Trace t)
    {
        if (!t.IsVersus) return false;
        t.ExpressionError = $"'vs {t.XSpec}' needs a swept Y — this selection is a single value.";
        t.InvalidSpecText = t.Expression ?? t.CubeName;
        t.Points.Clear();
        t.FamilyCurves.Clear();
        return true;
    }

    /// <summary>
    /// Stamps <see cref="Trace.SourceZ0PerPort"/>/<see cref="Trace.SourceZ0IsUnusual"/> on a
    /// cube-bound network-parameter trace from its OWN group's Z0 cube. Returns false — leaving the
    /// trace untouched — when <paramref name="cubeSpec"/> is not a network-parameter cube, which is
    /// the caller's cue that there are no per-port references to carry.
    ///
    /// <para><b>The single stamping site, and it must stay single.</b> The per-port array is the
    /// only faithful record of a run's port references — <c>Data.Z0</c> is one uniform value and
    /// <see cref="Trace.MarkerReferenceZ0"/> falls back to the trace's own (default 50 Ω) <c>Z0</c>
    /// when the array is null, so a trace that loses it silently reports every reflection against
    /// 50 Ω. That is exactly what happened when <c>TraceRowViewModel.RebuildSignals</c> cleared the
    /// array for every cube-bound trace: it runs AFTER <see cref="TrySetCubeData"/> on both the
    /// <c>.cdd</c> load path and the post-run library refresh, so a conjugately-matched port pair
    /// (Term Z = 5+j100 against 5−j100) plotted at the Smith centre — correctly — while its marker
    /// read "impedance=50+j0 Ω" instead of the 5−j100 the port actually sees. Re-stamp here rather
    /// than clearing; never add a second copy of this logic.</para>
    /// </summary>
    public static bool StampSourceZ0FromCube(DataSet ds, Trace t, string cubeSpec)
    {
        if (!RfCore.Data.NetworkMetrics.IsNetworkParamCubeSpec(ds, cubeSpec)) return false;

        int dot = cubeSpec.LastIndexOf('.');
        string group  = dot < 0 ? "" : cubeSpec[..dot];
        string z0Spec = group.Length == 0 ? RfCore.Data.NetworkMetrics.Z0CubeName
                                          : $"{group}.{RfCore.Data.NetworkMetrics.Z0CubeName}";

        var z0Cube = ds[z0Spec];
        t.SourceZ0PerPort   = z0Cube.ComplexValues;
        t.SourceZ0IsUnusual = RfCore.Data.DataSetBuilder.ClassifyZ0(z0Cube) != RfCore.Data.Z0Kind.UniformReal;
        return true;
    }

    /// <summary>
    /// Resolves the effective DataCube for a network-parameter cube trace (S/Z/Y element) at the
    /// trace's OWN reference <see cref="Trace.Z0"/> — brief-dd-z0-renormalization.md §1. Also stamps
    /// <see cref="Trace.SourceZ0PerPort"/>/<see cref="Trace.SourceZ0IsUnusual"/> from the group's Z0
    /// cube, reusing the exact two fields the network/SNP path already uses (§3's Y-label token and
    /// §2's badge read them regardless of which path populated them).
    ///
    /// <para>Order with Z/Y conversion (§1): renormalize S first, then convert to Z or Y — so Z/Y
    /// come out mathematically INVARIANT to the trace's Z0 for a REAL target (Z/Y are reference-
    /// independent quantities; pinned by <c>Z0RenormalizationTests</c>'s "order commutes" gate).
    /// <b>Known limitation for a COMPLEX target:</b> <c>RFNetwork.SToS</c> is the power-wave
    /// (Kurokawa) form (uses <c>Conjugate(z0)</c>), while <c>SToZ</c>/<c>SToY</c> are the ORDINARY
    /// (non-power-wave) √Z0 form — no conjugate. The two conventions coincide when Z0 is real but
    /// genuinely diverge for a complex reference, so a Z/Y cube trace's displayed values can shift
    /// slightly under a COMPLEX Z0 override (pinned by
    /// <c>Z0RenormalizationTests.RenormalizeSCube_ThenConvert_DivergesFromDirect_ComplexTarget</c>).
    /// Not introduced by this brief — <c>NetworkMetrics.TwoPortUniformReal</c>/<c>FullUniformReal</c>
    /// (R-stb-1..6) already restrict their own renormalization target to REAL for exactly this
    /// reason. Fixing the underlying convention gap (making SToZ/SToY power-wave-aware, or SToS
    /// ordinary-aware) is out of scope here — it would touch every S/Z/Y conversion call site in the
    /// engine and UI, not just this brief's cube-trace Z0 field.</para>
    /// </summary>
    private static DataCube ResolveNetworkParamCube(DataSet ds, Trace t, string cubeSpec, DataCube cube)
    {
        int dot = cubeSpec.LastIndexOf('.');
        string bare  = dot < 0 ? cubeSpec : cubeSpec[(dot + 1)..];
        string group = dot < 0 ? "" : cubeSpec[..dot];
        string sSpec  = group.Length == 0 ? RfCore.Data.NetworkMetrics.SCubeName  : $"{group}.{RfCore.Data.NetworkMetrics.SCubeName}";
        string z0Spec = group.Length == 0 ? RfCore.Data.NetworkMetrics.Z0CubeName : $"{group}.{RfCore.Data.NetworkMetrics.Z0CubeName}";

        var sCube  = ds[sSpec];
        var z0Cube = ds[z0Spec];
        int nPorts = sCube.Axes[sCube.Rank - 1].Length;
        var z0Src  = z0Cube.ComplexValues;

        StampSourceZ0FromCube(ds, t, cubeSpec);

        // Override OFF ⇒ absolutely no renormalization, whatever the source's per-port references
        // are (brief-dd-z0-nonuniform-override). The cube is returned exactly as simulated/loaded:
        // an S-parameter run with per-port Term impedances renders the match it actually has, not
        // the match it would have if every port were re-terminated at the port-1 reference.
        if (!t.Z0OverrideEnabled) return cube;

        bool identity = true;
        for (int p = 0; p < nPorts; p++)
            if (z0Src[p] != t.Z0) { identity = false; break; }
        if (identity) return cube;

        var z0New   = Enumerable.Repeat(t.Z0, nPorts).ToArray();
        var renormS = RfCore.Data.NetworkMetrics.RenormalizeSCube(sCube, z0Src, z0New);
        if (bare == RfCore.Data.NetworkMetrics.SCubeName) return renormS;

        var matrixType = bare == "Z" ? MatrixType.Z : MatrixType.Y;
        return RfCore.Data.NetworkMetrics.ConvertSCube(renormS, z0New, matrixType);
    }

    private static void ResolveFamily(Trace t, DataCube cube, AxisSlice[] slice,
                                      PlotType plotType, FreqUnit freqUnit, DataSet? ds = null,
                                      DataSet? xDs = null, ResolveProbe? probe = null)
    {
        // Find family and X axes by name (slice is name-keyed, order-independent).
        int fDim = -1, xDim = -1;
        for (int d = 0; d < cube.Axes.Count; d++)
        {
            var axName = cube.Axes[d].Name;
            foreach (var s in slice)
            {
                if (s.AxisName == axName)
                {
                    if (s.Role == AxisRole.FamilyIterate) fDim = d;
                    else if (s.Role == AxisRole.KeepAsX)  xDim = d;
                    break;
                }
            }
        }
        if (fDim < 0 || xDim < 0) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }

        var fAxis = cube.Axes[fDim];
        int count = Math.Min(fAxis.Length, Trace.MaxFamilyCurves);

        double[]? xVals = null; string xName = ""; string? xUnit = null;
        var curves = new List<(double, string?, System.Numerics.Complex[]?, double[]?)>(count);

        for (int k = 0; k < count; k++)
        {
            var args = new object[cube.Rank];
            for (int d = 0; d < cube.Rank; d++)
            {
                var ax = cube.Axes[d];
                AxisSlice s = default;
                foreach (var sl in slice) { if (sl.AxisName == ax.Name) { s = sl; break; } }
                if (s.Role == AxisRole.FamilyIterate)     args[d] = k;
                else if (s.Role == AxisRole.KeepAsX)
                    args[d] = s.IsNarrowedRange ? new Range(s.RangeStart, s.RangeEndExclusive) : Range.All;
                else args[d] = Math.Clamp(s.Index, 0, Math.Max(0, ax.Length - 1));
            }
            probe?.Reading($"family[{k}]", cube, args);
            var res = cube[args];
            if (!res.IsCube || res.Cube!.Rank != 1) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }
            var sliced = res.Cube!;
            if (xVals is null)
            {
                var xa = sliced.Axes[0];
                xVals = xa.Values;
                xName = xa.Name;
                xUnit = string.IsNullOrEmpty(xa.Unit) ? null : xa.Unit;
            }
            curves.Add((fAxis.Values[k],
                        fAxis.Labels is { } L && k < L.Length ? L[k] : null,
                        sliced.DataKind == DataKind.Complex ? sliced.ComplexValues : null,
                        sliced.DataKind == DataKind.Real    ? sliced.RealValues    : null));
        }
        if (xVals is null) { t.Points.Clear(); t.FamilyCurves.Clear(); return; }

        // ── Plot versus, family form: each curve gets its OWN X (Pout at 2.0 GHz is not Pout at
        //    2.4 GHz), so the X side iterates the same family axis and is resolved per curve.
        List<double[]>? perCurveX = null;
        if (t.IsVersus)
        {
            var xSource = xDs ?? ds;
            if (xSource is null)
            {
                t.ExpressionError = $"X source for '{t.XSpec}' is not loaded.";
                t.InvalidSpecText = t.Expression ?? t.CubeName;
                t.Points.Clear(); t.FamilyCurves.Clear();
                return;
            }
            if (!VersusResolver.TryResolveXFamily(t.XSpec!, xSource, slice, fAxis.Name, curves.Count,
                                                  out perCurveX, out var vErr))
            {
                t.ExpressionError = vErr;
                t.InvalidSpecText = t.Expression ?? t.CubeName;
                t.Points.Clear(); t.FamilyCurves.Clear();
                return;
            }
            for (int k = 0; k < curves.Count; k++)
            {
                int yN = curves[k].Item3?.Length ?? curves[k].Item4?.Length ?? 0;
                if (!VersusResolver.CountsAgree(t.XSpec!, t.CubeName ?? "", perCurveX[k].Length, yN, out var cErr))
                {
                    t.ExpressionError = cErr;
                    t.InvalidSpecText = t.Expression ?? t.CubeName;
                    t.Points.Clear(); t.FamilyCurves.Clear();
                    return;
                }
            }
            xVals = perCurveX[0];       // trace-level anchor; every curve carries its own below
            xName = t.XSpec!;
            xUnit = null;
        }

        var toneFreqs2 = GetToneFreqsCube(ds, t.CubeName);
        t.SetSpectrumFundamentals(t.IsVersus ? null : ResolveFundamentalByX(toneFreqs2, slice, xVals.Length));
        t.SetFamilyData(xVals, xName, xUnit, fAxis.Name, curves, plotType, freqUnit,
                        familyAxisUnit: string.IsNullOrEmpty(fAxis.Unit) ? null : fAxis.Unit,
                        perCurveX: perCurveX);
        // A family trace has no pinned SPECTRAL line (each curve carries its own tag), but it can
        // still carry ordinary pinned axes, and those appear in its label like any other trace's.
        ApplyPinnedAxisDisplay(t, ds, freqUnit);
    }

    private static DataCube? GetToneFreqsCube(DataSet? ds, string? cubeName)
    {
        if (ds is null || cubeName is null) return null;
        int dot = cubeName.IndexOf('.');
        string toneFreqsName = dot < 0 ? "ToneFreqs" : cubeName[..dot] + ".ToneFreqs";
        return ds.Contains(toneFreqsName) ? ds[toneFreqsName] : null;
    }

    // When a spectral axis ("harmonic"/"mixIndex") is PINNED in the slice (X is a sweep, e.g. Pin),
    // surface which spectral line the trace shows + its frequency so the marker box reads the same
    // two rows the spectral-axis-X plot gives. Clears it when no spectral axis is pinned (incl. when
    // the spectral axis is the X axis — then the X-axis marker rows already report it).
    private static void ApplyPinnedSpectral(Trace t, DataSet? ds)
    {
        t.SetPinnedSpectral(null, null, double.NaN);
        if (ds is null || t.CubeName is null || t.Slice is null || !ds.Contains(t.CubeName)) return;

        AxisSlice? pin = null;
        foreach (var s in t.Slice)
            if (s.Role == AxisRole.PinToIndex &&
                (s.AxisName == Trace.HarmonicAxisName || s.AxisName == Trace.MixIndexAxisName))
            { pin = s; break; }
        if (pin is null) return;

        var cube = ds[t.CubeName];
        Axis? axis = null;
        foreach (var a in cube.Axes) if (a.Name == pin.Value.AxisName) { axis = a; break; }
        if (axis is null || axis.Length == 0) return;
        int idx = Math.Clamp(pin.Value.Index, 0, axis.Length - 1);

        string label = axis.Labels is not null && idx < axis.Labels.Length
            ? axis.Labels[idx]
            : axis.Values[idx].ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

        double freqHz;
        if (pin.Value.AxisName == Trace.MixIndexAxisName)
        {
            // The mixIndex value IS the signed product frequency → fold to the single-sided |f|.
            freqHz = Math.Abs(axis.Values[idx]);
        }
        else
        {
            // harmonic: order × f0 (representative fundamental; exact for a non-frequency sweep).
            var tf = GetToneFreqsCube(ds, t.CubeName);
            double f0 = tf is not null && tf.RealValues.Length > 0 ? tf.RealValues[0] : double.NaN;
            freqHz = double.IsNaN(f0) ? double.NaN : Math.Round(axis.Values[idx]) * f0;
        }

        t.SetPinnedSpectral(pin.Value.AxisName, label, freqHz);
    }

    // A pinned axis used to read as its raw INDEX — "DC1.I(VDS=240, branch=0)" — which names neither
    // the value the user swept to nor the quantity they picked. Both answers are on the cube, so the
    // owner resolves them here and hands the finished tokens to the Trace (which never holds a
    // DataSet). Mirrors ApplyPinnedSpectral exactly, including its clear-first contract.
    //
    // Two forms, and the difference is deliberate: a LABELLED axis reads as its label alone ("IDS")
    // because the label already names the quantity, while a swept axis keeps its name and gains its
    // value and unit ("VDS=3.5 V"). The S/Y/Z port axes are excluded — they are written positionally
    // as "S(1,2)" by TraceLabeler and must stay that way.
    /// <param name="freqUnit">
    /// <b>The PLOT's frequency unit, which a pinned frequency axis is read in.</b> Reported
    /// 2026-09-11: a far-field cut plot set to GHz labelled its traces <c>freq=1.74e+09 Hz</c>,
    /// because this resolver formatted every pinned axis out of the cube's own raw values and
    /// nothing here had ever needed to know what the plot displayed frequencies in. Null keeps the
    /// raw-Hz form, which is what a caller with no plot in hand should get.
    /// </param>
    /// <summary>
    /// <b>The reference a dBm LEVEL was published at</b> — read from the cube's own group and handed
    /// to the trace, because a <c>Trace</c> deliberately never holds a <see cref="DataSet"/>. The
    /// same owner-resolves-it contract <see cref="ApplyPinnedAxisDisplay"/> and
    /// <c>ApplyPinnedSpectral</c> follow, including clearing first: a reference left over from a
    /// previous cube would silently re-reference the new one.
    ///
    /// <para>This is what makes <see cref="Trace.ReferenceInputPowerDbmOverride"/> EXACT rather than
    /// a guess. The override says what reference to read the level against; this says what it is
    /// currently against, and the difference is the shift. Without the run publishing its own
    /// reference there would be nothing to subtract.</para>
    /// </summary>
    public static void ApplyReferenceLevel(Trace t, DataSet? ds)
    {
        t.SetBakedReferenceInputPowerDbm(null);
        if (!t.IsCubeBound || t.CubeName is null) return;
        if (LevelReference.IsReferencedLevel(ds, t.CubeName, out double refDbm))
            t.SetBakedReferenceInputPowerDbm(refDbm);
    }

    public static void ApplyPinnedAxisDisplay(Trace t, DataSet? ds, FreqUnit? freqUnit = null)
    {
        t.SetPinnedAxisDisplay(null);
        t.SetPinnedAxisSpecTokens(null);
        if (ds is null || t.CubeName is null || t.Slice is null || !ds.Contains(t.CubeName)) return;

        var cube = ds[t.CubeName];
        Dictionary<string, string>? map  = null;
        Dictionary<string, string>? spec = null;

        foreach (var s in t.Slice)
        {
            // The PICTURE's idea of pinned, which on a 3D surface is not the slice's — see
            // SurfaceResolve.PinsAxis. The labeller asks the same question, so the two cannot
            // disagree about which axes have a token to show.
            if (!SurfaceResolve.PinsAxis(t, s)) continue;
            // Positional pairs — TraceLabeler owns them. row/col are the WSProbe matrix's own and
            // read positionally too, so a display token built here would never be used.
            if (s.AxisName is "i" or "j" or "row" or "col") continue;

            Axis? axis = null;
            foreach (var a in cube.Axes) if (a.Name == s.AxisName) { axis = a; break; }
            if (axis is null || axis.Length == 0) continue;

            // A ONE-VALUE PORT AXIS SAYS NOTHING, so it is not said (owner, 2026-09-11: a
            // single-port antenna's every trace read "…, port=1"). The test is the axis LENGTH
            // rather than the value: "port=1" on a two-port run is the whole point of the token,
            // and "port=2" on a one-port run cannot occur. Confined to `port` deliberately — a
            // single-frequency sweep still names its frequency, because WHICH frequency a pattern
            // was taken at is the first thing a reader needs and a run of one is still a choice.
            int idx = Math.Clamp(s.Index, 0, axis.Length - 1);

            // WHAT A SPEC HAS TO SAY, which for a `port` axis is not its index — SliceTokenParser
            // matches an integer there against the axis's own VALUES. Resolved here because this is
            // the one place holding both the slice and the cube, and stamped BEFORE the
            // one-value suppression below, which is about what a LABEL says and not about what a
            // spec must parse. See Trace.PinnedAxisSpecToken.
            if (axis.Name == "port")
                (spec ??= new Dictionary<string, string>(StringComparer.Ordinal))[s.AxisName] =
                    axis.Values[idx].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

            // An EMPTY token rather than a missing one: the labeller reads a missing entry as "the
            // owner resolved nothing" and falls back to the raw INDEX, which would turn the
            // suppression into "port=0". Empty means "say nothing", which is what is meant.
            if (axis.Name == "port" && axis.Length == 1)
            {
                (map ??= new Dictionary<string, string>(StringComparer.Ordinal))[s.AxisName] = "";
                continue;
            }

            // θ and φ rather than "theta" and "phi" — AxisSymbols says why, and says it once so the
            // trace card's own axis rows cannot spell them differently.
            string shown = AxisSymbols.Display(axis.Name);

            string token;
            if (axis.Labels is not null && idx < axis.Labels.Length &&
                !string.IsNullOrWhiteSpace(axis.Labels[idx]))
            {
                token = axis.Labels[idx];
            }
            else if (freqUnit is { } fu && IsHzAxis(axis))
            {
                // A FREQUENCY reads in the plot's own unit, and the ONE axis actually named "freq"
                // drops the prefix entirely (owner, 2026-09-11): "1.74 GHz" already says it is a
                // frequency, so "freq=" is a word that carries nothing. A second Hz-unit axis — an
                // LO beside an RF — keeps its name, because there the name is the only thing
                // separating two tokens that would otherwise read identically.
                // A PLAIN decimal, not "G6": G6 turns 5e9 Hz into "5E+09 Hz", which is the same
                // unreadable thing the report was about, one unit further along. A frequency in its
                // own display unit is O(1) to O(1000) by construction, so a fixed-point form with a
                // trailing-zero trim reads correctly in every unit the plot offers.
                string val = (axis.Values[idx] * fu.Scale())
                    .ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                token = axis.Name == "freq"
                    ? $"{val} {fu.Description()}"
                    : $"{shown}={val} {fu.Description()}";
            }
            else
            {
                string val = axis.Values[idx].ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
                token = string.IsNullOrWhiteSpace(axis.Unit)
                    ? $"{shown}={val}"
                    : $"{shown}={val} {axis.Unit}";
            }

            // A whole-plane cut carries BOTH azimuths, so its azimuth token says both — "φ=0/180
            // deg". Written here rather than in the labeller because this is where the axis, its
            // unit and the resolved companion are all in hand at once, and because the labeller
            // deliberately holds no DataSet.
            if (t.HasPatternBackBranch && double.IsFinite(t.PatternBackPhiDeg)
                && s.AxisName == axis.Name && axis.Name is "phi" or "az")
            {
                string back = t.PatternBackPhiDeg.ToString(
                    "G6", System.Globalization.CultureInfo.InvariantCulture);
                string fwd = axis.Values[idx].ToString(
                    "G6", System.Globalization.CultureInfo.InvariantCulture);
                token = string.IsNullOrWhiteSpace(axis.Unit)
                    ? $"{shown}={fwd}/{back}"
                    : $"{shown}={fwd}/{back} {axis.Unit}";
            }

            (map ??= new Dictionary<string, string>(StringComparer.Ordinal))[s.AxisName] = token;
        }

        t.SetPinnedAxisDisplay(map);
        t.SetPinnedAxisSpecTokens(spec);
    }

    /// <summary>Whether an axis carries frequency, by its UNIT — the same test the rest of the
    /// resolve uses, so an axis named something other than "freq" is still a frequency.</summary>
    private static bool IsHzAxis(Axis axis) =>
        string.Equals(axis.Unit, "Hz", StringComparison.OrdinalIgnoreCase);

    private static double[]? ResolveFundamentalByX(DataCube? toneFreqs, AxisSlice[]? slice,
                                                   int xAxisLength, ResolveProbe? probe = null)
    {
        if (toneFreqs is null || slice is null) return null;
        var result = new double[xAxisLength];
        for (int xi = 0; xi < xAxisLength; xi++)
        {
            var args = new object[toneFreqs.Rank];
            for (int d = 0; d < toneFreqs.Rank; d++)
            {
                string axName = toneFreqs.Axes[d].Name;
                if (axName == "tone") { args[d] = 0; continue; }
                AxisSlice? found = null;
                foreach (var s in slice) { if (s.AxisName == axName) { found = s; break; } }
                if (found?.Role == AxisRole.KeepAsX)
                    args[d] = Math.Clamp(xi, 0, Math.Max(0, toneFreqs.Axes[d].Length - 1));
                else
                    args[d] = Math.Clamp(found?.Index ?? 0, 0, Math.Max(0, toneFreqs.Axes[d].Length - 1));
            }
            probe?.Reading($"tonefreqs[{xi}]", toneFreqs, args);
            var r = toneFreqs[args];
            result[xi] = r.IsReal ? r.RealValue!.Value : 0.0;
        }
        return result;
    }

}
