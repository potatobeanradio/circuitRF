// ================================================================
//  SurfaceResolve.cs  —  ANT-10: a cube with two free ANGLE axes, as the
//  (theta, phi) grid a 3D pattern surface is drawn from
//
//  This is the surface's half of TraceResolve, and it is separate for the
//  same reason ContourResolve is: everything here is arithmetic on a cube,
//  and it is reached from the ONE resolve both the window and `Cli render`
//  go through, so a headless surface cannot be a different surface.
//
//  THE TWO AXES ARE FOUND BY NAME, not by slice role, and the rule is
//  ANT-7's own: PolarPatternAngle.TryDegreesPerUnit already decides
//  whether an axis can be an angle at all, and the polar cut already
//  refuses a frequency sweep on that test rather than drawing a spiral of
//  nothing. Using the same test here means one rule, and it means a trace
//  authored as `farfield.U[0, :, :, 1]` resolves without the author having
//  to know which of the two ':' the positional family convention would
//  have made the X axis.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Render.DataDisplay;

public static class SurfaceResolve
{
    /// <summary>What a 3D plot says when the trace it was given is not a pattern over two angles.
    /// Surfaced on the card as the trace's own error, exactly as a bad slice is.</summary>
    internal const string NotAPatternRefusal =
        "A 3D pattern surface needs TWO angle axes left open — a polar angle (theta or el) and an " +
        "azimuth (phi or az) — and this trace has none it can use. Open both with ':' and pin the " +
        "rest, as in farfield.U[0, :, :, 1].";

    /// <summary>
    /// Fills <paramref name="t"/>'s <see cref="Trace.SurfaceGrid"/> from a cube, or clears it and
    /// sets the trace's error. Called from <see cref="TraceResolve"/> when — and only when — the
    /// parent plot is a <see cref="PlotType.Surface3D"/>.
    /// </summary>
    public static void Resolve(Trace t, DataCube cube, AxisSlice[] slice)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(cube);
        t.SurfaceGrid = null;

        if (!TryFindAngleAxes(cube, out int thetaDim, out int phiDim))
        {
            t.ExpressionError = NotAPatternRefusal;
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        // Both angle axes whole; every other axis pinned exactly where the slice pins it. A slice
        // entry that is missing, or that asked to keep an axis this surface has no room for, pins at
        // its own index rather than failing — the picker's default slice opens axis 0 (freq), and a
        // trace dragged onto a 3D plot must draw rather than report a slice the author never chose.
        var args = new object[cube.Rank];
        for (int d = 0; d < cube.Rank; d++)
        {
            if (d == thetaDim || d == phiDim) { args[d] = System.Range.All; continue; }
            AxisSlice? found = null;
            foreach (var s in slice) if (s.AxisName == cube.Axes[d].Name) { found = s; break; }
            args[d] = Math.Clamp(found?.Index ?? 0, 0, Math.Max(0, cube.Axes[d].Length - 1));
        }

        var res = cube[args];
        if (!res.IsCube || res.Cube!.Rank != 2)
        {
            t.ExpressionError = NotAPatternRefusal;
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        var sliced = res.Cube!;
        // The slice keeps the cube's own axis ORDER, which need not be theta-then-phi. Read the
        // result through its axis names rather than assuming: a cube written [phi, theta] would
        // otherwise come out transposed, which is a plausible surface of the wrong antenna.
        bool thetaFirst = string.Equals(sliced.Axes[0].Name, cube.Axes[thetaDim].Name, StringComparison.Ordinal);
        var  thAxis = thetaFirst ? sliced.Axes[0] : sliced.Axes[1];
        var  phAxis = thetaFirst ? sliced.Axes[1] : sliced.Axes[0];

        PolarPatternAngle.TryDegreesPerUnit(thAxis.Name, thAxis.Unit, out double thDpu);
        PolarPatternAngle.TryDegreesPerUnit(phAxis.Name, phAxis.Unit, out double phDpu);

        int nt = thAxis.Length, np = phAxis.Length;
        var complex = sliced.DataKind == DataKind.Complex ? sliced.ComplexValues : null;
        var real    = sliced.DataKind == DataKind.Real    ? sliced.RealValues    : null;
        if (complex is null && real is null) { t.ExpressionError = NotAPatternRefusal; return; }

        // ── THE RADIUS IS IN DECIBELS, SO THE VALUES MUST BE (2026-09-11) ─────────────────────
        //
        //  Reported: Etheta on a surface drew a uniform pink hemisphere. It is complex, and with no
        //  transform RectY returns the LINEAR magnitude — ~0.01 V on a real patch — which against a
        //  peak reference spans 0.01 "dB" and puts every direction on the outer radius in the top
        //  colour. A hemisphere is not an error shape; it is what an isotropic radiator over a
        //  ground plane looks like, so nothing in the picture could have told anyone.
        //
        //  The kind is recorded before the values are flattened to double, because after that line
        //  the trace cannot tell a complex cube from a real one.
        t.SurfaceComplexSource = complex is not null;
        if (!t.PatternValuesCanBeDb)
        {
            t.ExpressionError = Trace.PatternValueRefusal;
            t.InvalidSpecText = t.Expression ?? t.CubeName;
            t.SurfaceGrid     = null;
            t.Points.Clear();
            t.FamilyCurves.Clear();
            return;
        }

        // The VALUE goes through the trace's own transform — the same RectY a rect cut and a polar
        // cut read, so `db10(U)` means one thing on all three and a surface can never be a different
        // quantity from the cut beside it.
        var db = new double[nt * np];
        for (int a = 0; a < nt; a++)
            for (int b = 0; b < np; b++)
            {
                int src = thetaFirst ? a * np + b : b * nt + a;
                double? v = complex is not null
                    ? t.RectY(complex[src], null)
                    : t.RectY(null, real![src]);
                db[a * np + b] = v ?? double.NaN;
            }

        var theta = new double[nt];
        for (int a = 0; a < nt; a++) theta[a] = thAxis.Values[a] * thDpu;
        var phi = new double[np];
        for (int b = 0; b < np; b++) phi[b] = phAxis.Values[b] * phDpu;

        t.ExpressionError = null;
        t.InvalidSpecText = null;
        t.SurfaceGrid = new PatternSurfaceGrid
        {
            ThetaDeg      = theta,
            PhiDeg        = phi,
            Db            = db,
            ThetaAxisName = thAxis.Name,
            PhiAxisName   = phAxis.Name,
        };
    }

    /// <summary>
    /// The polar axis and the azimuth, by NAME. Both must be angles on
    /// <see cref="PolarPatternAngle.TryDegreesPerUnit"/>'s own test, and they must be DIFFERENT
    /// axes — a cube carrying only "theta" has a cut in it, not a surface.
    /// </summary>
    internal static bool TryFindAngleAxes(DataCube cube, out int thetaDim, out int phiDim)
    {
        thetaDim = phiDim = -1;
        for (int d = 0; d < cube.Rank; d++)
        {
            var ax = cube.Axes[d];
            if (!PolarPatternAngle.TryDegreesPerUnit(ax.Name, ax.Unit, out _)) continue;
            if (ax.Length < 2) continue;
            if (thetaDim < 0 && ax.Name is "theta" or "el") thetaDim = d;
            else if (phiDim < 0 && ax.Name is "phi" or "az") phiDim = d;
        }
        return thetaDim >= 0 && phiDim >= 0;
    }
}
