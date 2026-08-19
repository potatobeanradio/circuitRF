// ================================================================
//  VersusResolver.cs  —  Resolves the X side of a "Y vs X" trace to
//  the real-valued array the plot uses as its X coordinates.
//
//  The X side is an ORDINARY trace spec, resolved with the ordinary
//  parsers:
//    • single-cube shorthand  → CubeTraceSpecParser + the cube slicer
//    • anything else          → TraceExpression (element-wise)
//
//  Role inheritance: an X side written BARE (no brackets) adopts the Y
//  side's axis roles BY AXIS NAME — X, family, and pinned index alike —
//  so "Gain[:, ~] vs Pout" means what it reads as. A bracketed X side is
//  taken exactly as typed and must be congruent with the Y side.
//
//  X must be REAL: a complex slice needs a transform that lands on the
//  real line (dB20/dB10/dB/mag/phase/real/imag). Cube VALUES carry no
//  unit anywhere in the data model (only axes do), so a versus X axis
//  has no unit — its label is the X spec text itself.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RfCore.Data;

namespace CircuitRF.Ui.DataDisplay;

internal static class VersusResolver
{
    /// <summary>Resolves a single (non-family) X side to a rank-1 real array.</summary>
    internal static bool TryResolveX(
        string       xSpec,
        DataSet      ds,
        AxisSlice[]? ySlice,
        out double[] xValues,
        out string   error)
    {
        xValues = Array.Empty<double>();
        error   = "";
        if (UnresolvedAlias(xSpec, out error)) return false;

        if (TryParseSingleCube(xSpec, ds, ySlice, out var cube, out var slice, out var transform, out error))
        {
            var args = BuildArgs(cube!, slice!, familyIndex: -1, out int xDim, out _);
            if (xDim < 0)
            {
                error = $"X side '{xSpec}' has no swept axis — one axis must be ':'.";
                return false;
            }
            var res = cube![args];
            if (!res.IsCube || res.Cube!.Rank != 1)
            {
                error = $"X side '{xSpec}' did not yield a single swept curve.";
                return false;
            }
            return TryToReal(res.Cube!, transform, xSpec, out xValues, out error);
        }
        if (!string.IsNullOrEmpty(error)) return false;

        // Multi-cube expression X side.
        if (!TraceExpression.TryEvaluate(xSpec, ds, PlotType.Rect,
                out var xv, out var cz, out var rv, out _, out _, out _, out var exprErr))
        {
            error = exprErr;
            return false;
        }
        _ = xv;
        if (rv is null)
        {
            if (cz is null) { error = $"X side '{xSpec}' produced no values."; return false; }
            error = ComplexXMessage(xSpec);
            return false;
        }
        xValues = rv;
        return true;
    }

    /// <summary>
    /// Resolves the X side of a FAMILY trace: one X array per curve, because a versus family's X
    /// data genuinely differs per curve (Pout at 2.0 GHz is not Pout at 2.4 GHz).
    /// </summary>
    internal static bool TryResolveXFamily(
        string          xSpec,
        DataSet         ds,
        AxisSlice[]?    ySlice,
        string          familyAxisName,
        int             curveCount,
        out List<double[]> perCurveX,
        out string      error)
    {
        perCurveX = new List<double[]>();
        error     = "";
        if (UnresolvedAlias(xSpec, out error)) return false;

        if (!TryParseSingleCube(xSpec, ds, ySlice, out var cube, out var slice, out var transform, out error))
        {
            if (string.IsNullOrEmpty(error))
                error = $"X side '{xSpec}' must be a single-cube spec to follow a family "
                      + $"(a multi-cube expression cannot iterate '{familyAxisName}').";
            return false;
        }

        // The X side must iterate the SAME family axis, by name.
        var famSlice = Array.Find(slice!, s => s.Role == AxisRole.FamilyIterate);
        if (famSlice.AxisName is null || !string.Equals(famSlice.AxisName, familyAxisName, StringComparison.Ordinal))
        {
            error = $"'{xSpec}': both sides must iterate the same family axis ('{familyAxisName}').";
            return false;
        }

        for (int k = 0; k < curveCount; k++)
        {
            var args = BuildArgs(cube!, slice!, familyIndex: k, out int xDim, out int fDim);
            if (xDim < 0 || fDim < 0)
            {
                error = $"X side '{xSpec}' needs one swept axis (':') alongside the family ('~').";
                return false;
            }
            var res = cube![args];
            if (!res.IsCube || res.Cube!.Rank != 1)
            {
                error = $"X side '{xSpec}' did not yield a single swept curve.";
                return false;
            }
            if (!TryToReal(res.Cube!, transform, xSpec, out var xs, out error)) return false;
            perCurveX.Add(xs);
        }
        return true;
    }

    /// <summary>Point-count gate — the rule that keeps a versus trace honest (and is what makes a
    /// cross-source X safe). Returns false with the message the card shows under the spec box.</summary>
    internal static bool CountsAgree(string xSpec, string ySpec, int xN, int yN, out string error)
    {
        if (xN == yN) { error = ""; return true; }
        error = $"'{ySpec} vs {xSpec}': X has {xN} point(s), Y has {yN} — "
              + "both sides must slice the same swept axis.";
        return false;
    }

    // ---------------------------------------------------------------
    //  Internals
    // ---------------------------------------------------------------

    /// <summary>
    /// Parses the X side as a single-cube spec, inheriting the Y side's roles when it is written
    /// bare. Returns false with an EMPTY error when the text is not a single-cube spec at all
    /// (the caller then tries the expression path), and false with a message on a real failure.
    /// </summary>
    private static bool TryParseSingleCube(
        string           xSpec,
        DataSet          ds,
        AxisSlice[]?     ySlice,
        out DataCube?    cube,
        out AxisSlice[]? slice,
        out CubeTransform transform,
        out string       error)
    {
        cube = null; slice = null; transform = CubeTransform.None; error = "";

        if (!CubeTraceSpecParser.TryParse(xSpec, ds, out var cubeName, out var parsed, out transform, out var parseErr))
        {
            // A bracketed spec that failed to parse is a genuine error; a bare name that is simply
            // not a cube may still be a valid expression, so leave the error empty for the caller.
            if (xSpec.Contains('[')) error = parseErr;
            return false;
        }
        cube = ds[cubeName];
        slice = parsed ?? Array.Empty<AxisSlice>();

        // Bare X side (no brackets) → inherit the Y side's roles by axis name.
        if (!xSpec.Contains('[') && ySlice is { Length: > 0 })
            slice = InheritRoles(cube, ySlice);

        if (slice.Length != cube.Rank)
        {
            error = $"X side '{xSpec}': expected {cube.Rank} axis token(s), got {slice.Length}.";
            return false;
        }
        return true;
    }

    /// <summary>Copies the Y side's role (and pinned index) onto every X axis of the same NAME;
    /// an axis the Y side does not have is pinned at its first entry.</summary>
    private static AxisSlice[] InheritRoles(DataCube cube, AxisSlice[] ySlice)
    {
        var slice = new AxisSlice[cube.Rank];
        for (int d = 0; d < cube.Rank; d++)
        {
            var ax = cube.Axes[d];
            AxisSlice? match = null;
            foreach (var s in ySlice)
                if (string.Equals(s.AxisName, ax.Name, StringComparison.Ordinal)) { match = s; break; }

            if (match is { } m && m.Role != AxisRole.PinToIndex)
                slice[d] = new AxisSlice(ax.Name, m.Role, 0);
            else
            {
                int idx = Math.Clamp(match?.Index ?? 0, 0, Math.Max(0, ax.Length - 1));
                string lbl = ax.Labels is { Length: > 0 } L && idx < L.Length ? L[idx] : "";
                slice[d] = new AxisSlice(ax.Name, AxisRole.PinToIndex, idx, Label: lbl);
            }
        }
        return slice;
    }

    /// <summary>Builds the cube indexer args for one slice, optionally pinning the family axis to
    /// <paramref name="familyIndex"/>. Reports which dims carried the X and family roles.</summary>
    private static object[] BuildArgs(DataCube cube, AxisSlice[] slice, int familyIndex,
                                      out int xDim, out int fDim)
    {
        xDim = -1; fDim = -1;
        var args = new object[cube.Rank];
        for (int d = 0; d < cube.Rank; d++)
        {
            var ax = cube.Axes[d];
            AxisSlice s = default;
            bool found = false;
            foreach (var sl in slice)
                if (string.Equals(sl.AxisName, ax.Name, StringComparison.Ordinal)) { s = sl; found = true; break; }

            if (found && s.Role == AxisRole.KeepAsX)
            {
                args[d] = s.IsNarrowedRange ? new Range(s.RangeStart, s.RangeEndExclusive) : Range.All;
                xDim = d;
            }
            else if (found && s.Role == AxisRole.FamilyIterate)
            {
                fDim = d;
                args[d] = familyIndex >= 0 ? Math.Clamp(familyIndex, 0, Math.Max(0, ax.Length - 1)) : 0;
            }
            else
            {
                args[d] = Math.Clamp(found ? s.Index : 0, 0, Math.Max(0, ax.Length - 1));
            }
        }
        return args;
    }

    /// <summary>Maps a rank-1 slice to real X values through the X side's own transform.</summary>
    private static bool TryToReal(DataCube sliced, CubeTransform transform, string xSpec,
                                  out double[] values, out string error)
    {
        error = "";
        if (sliced.DataKind == DataKind.Real)
        {
            var raw = sliced.RealValues;
            values = transform switch
            {
                CubeTransform.dB20 => raw.Select(v => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300))).ToArray(),
                CubeTransform.dB10 or CubeTransform.dB
                                   => raw.Select(v => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300))).ToArray(),
                CubeTransform.Mag  => raw.Select(Math.Abs).ToArray(),
                CubeTransform.Real => raw,
                CubeTransform.Imag => raw.Select(_ => 0.0).ToArray(),
                _                  => raw,
            };
            return true;
        }

        var cz = sliced.ComplexValues;
        switch (transform)
        {
            case CubeTransform.dB20:
                values = cz.Select(z => 20.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300))).ToArray(); return true;
            case CubeTransform.dB10:
            case CubeTransform.dB:
                values = cz.Select(z => 10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300))).ToArray(); return true;
            case CubeTransform.Mag:
                values = cz.Select(z => z.Magnitude).ToArray(); return true;
            case CubeTransform.Phase:
                values = cz.Select(z => z.Phase * 180.0 / Math.PI).ToArray(); return true;
            case CubeTransform.Real:
                values = cz.Select(z => z.Real).ToArray(); return true;
            case CubeTransform.Imag:
                values = cz.Select(z => z.Imaginary).ToArray(); return true;
            default:
                values = Array.Empty<double>();
                error  = ComplexXMessage(xSpec);
                return false;
        }
    }

    /// <summary>
    /// An <c>alias::Cube</c> prefix that survives to the resolver means the card could NOT match the
    /// alias to a loaded dataset (it strips the prefix when it can). Reporting it here — rather than
    /// at the point of typing — keeps ONE error path: the card's own message would otherwise be
    /// overwritten by the resolve that follows every edit.
    /// </summary>
    private static bool UnresolvedAlias(string xSpec, out string error)
    {
        int sep = xSpec.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) { error = ""; return false; }
        error = $"No loaded data source named '{xSpec[..sep].Trim()}'.";
        return true;
    }

    private static string ComplexXMessage(string xSpec) =>
        $"X side '{xSpec}' is complex — the X axis must be real. "
      + "Wrap it in mag(), real(), dB20(), …";
}
