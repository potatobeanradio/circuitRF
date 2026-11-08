// ================================================================
//  Evaluator.PortBlock.cs — a sub-matrix of a port matrix, by port number
//
//  SP1.S(1:2, 1:2) is ports 1 to 2 of SP1's S matrix as a matrix of its own, ports renumbered from 1, so a
//  bench with two 2-ports on Terms 1-4 gives each its own S block: DUT_2 = SP1.S(3:4, 3:4), and DUT_2(2,1) is
//  the bench's S43. Every other axis (freq, a sweep) is kept. It reads any cube whose axes i and j are ports —
//  S, Y, Z, a TRL fixture's SA — and a measurement that holds one, which is how DUT_2(2,1) reads back.
//
//  A range is port NUMBERS, both ends included, and takes the ports the result has inside it: Terms 1, 2, 9,
//  10 give S(9:10, 9:10) as a 2-port. A single number on one side pins that port, as in S(2, 1).
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Core.Expressions;

public sealed partial class Evaluator
{
    /// <summary>Whether either argument of a two-argument call is a port range, <c>lo:hi</c>.</summary>
    private static bool HasPortRange(CallExpr cl)
        => cl.Args.Length == 2 && cl.Args.Any(a => a is PortRangeExpr);

    /// <summary>Whether <paramref name="cube"/> has both port axes, <c>i</c> and <c>j</c>.</summary>
    private static bool HasPortAxes(DataCube cube)
        => cube.Axes.Any(a => a.Name == "i") && cube.Axes.Any(a => a.Name == "j");

    /// <summary>
    /// <c>name(i, j)</c> on <paramref name="cube"/>: each argument a port number, which pins that axis, or a port
    /// range <c>lo:hi</c>, which keeps the ports inside it renumbered 1…k. Every other axis is kept.
    /// </summary>
    private Value EvalPortSelection(DataCube cube, CallExpr cl, string name, Scope scope)
    {
        if (cl.Args.Length != 2) throw new ArityException(name, 2, cl.Args.Length);
        int iDim = AxisDim(cube, "i"), jDim = AxisDim(cube, "j");
        var iSel = PortSelection(cube.Axes[iDim], cl.Args[0], name, scope);
        var jSel = PortSelection(cube.Axes[jDim], cl.Args[1], name, scope);

        // Gather: the result keeps every axis but a pinned port axis; a kept port axis holds the selected
        // positions, renumbered. Row-major, the last axis fastest — DataCube's own layout.
        var keep = new int[cube.Rank][];
        var outAxes = new System.Collections.Generic.List<Axis>();
        for (int d = 0; d < cube.Rank; d++)
        {
            PortSel? sel = d == iDim ? iSel : d == jDim ? jSel : null;
            var ax  = cube.Axes[d];
            if (sel is null) { keep[d] = Enumerable.Range(0, ax.Length).ToArray(); outAxes.Add(ax); continue; }
            keep[d] = sel.Value.Positions;
            if (sel.Value.Pinned) continue;
            var labels = ax.Labels is { } l ? sel.Value.Positions.Select(p => l[p]).ToArray() : null;
            outAxes.Add(new Axis(ax.Name, [.. Enumerable.Range(1, keep[d].Length).Select(k => (double)k)], ax.Unit, labels));
        }

        var strides = new int[cube.Rank];
        for (int d = cube.Rank - 1, s = 1; d >= 0; d--) { strides[d] = s; s *= cube.Axes[d].Length; }
        int total = keep.Aggregate(1, (n, k) => n * k.Length);
        var src = new int[total];
        var idx = new int[cube.Rank];
        for (int o = 0; o < total; o++)
        {
            int flat = 0;
            for (int d = 0; d < cube.Rank; d++) flat += keep[d][idx[d]] * strides[d];
            src[o] = flat;
            for (int d = cube.Rank - 1; d >= 0; d--)
            {
                if (++idx[d] < keep[d].Length) break;
                idx[d] = 0;
            }
        }

        DataCube result;
        if (cube.DataKind == DataKind.Complex)
        {
            var v = cube.ComplexValues;
            result = outAxes.Count == 0 ? DataCube.Scalar(v[src[0]]) : new DataCube([.. outAxes], src.Select(k => v[k]).ToArray());
        }
        else
        {
            var v = cube.RealValues;
            result = outAxes.Count == 0 ? DataCube.Scalar(v[src[0]]) : new DataCube([.. outAxes], src.Select(k => v[k]).ToArray());
        }
        result.Unit = cube.Unit;
        return new Value(result);
    }

    /// <summary>
    /// <c>DUT_1(2, 1)</c>: a call whose name is not a function but a value in scope holding a port matrix — an
    /// earlier measurement such as <c>DUT_1 = SP1.S(1:2, 1:2)</c> — reads it by port number as <c>SP1.S(2, 1)</c>
    /// reads S. Anything else stays an unknown function.
    /// </summary>
    private Value EvalCallOnPortMatrix(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length == 2 && scope.Lookup(cl.Name) is not null
            && Resolve(cl.Name, scope) is { Kind: ValueKind.Cube } v && HasPortAxes(v.AsCube()))
            return EvalPortSelection(v.AsCube(), cl, cl.Name, scope);
        throw new UnknownFunctionException(cl.Name);
    }

    private readonly record struct PortSel(int[] Positions, bool Pinned);

    /// <summary>The positions one argument selects on a port axis: a number pins its port, a range keeps the
    /// ports numbered inside it. A port or range the axis does not have is refused, naming the ports it has.</summary>
    private PortSel PortSelection(Axis axis, Expr arg, string name, Scope scope)
    {
        var ports = axis.Values.Select(v => (int)Math.Round(v)).ToArray();
        string have = string.Join(", ", ports);
        if (arg is PortRangeExpr pr)
        {
            int lo = PortNumber(EvalExpr(pr.Lo, scope), name, axis.Name);
            int hi = PortNumber(EvalExpr(pr.Hi, scope), name, axis.Name);
            if (hi < lo)
                throw new ExpressionException(PortBlockDiagnostics.RangeBackwards(name, lo, hi, axis.Name));
            var pos = Enumerable.Range(0, ports.Length).Where(k => ports[k] >= lo && ports[k] <= hi).ToArray();
            if (pos.Length == 0)
                throw new ExpressionException(PortBlockDiagnostics.RangeEmpty(name, lo, hi, axis.Name, have));
            return new PortSel(pos, false);
        }
        int n = PortNumber(EvalExpr(arg, scope), name, axis.Name);
        int at = Array.IndexOf(ports, n);
        if (at < 0)
            throw new ExpressionException(PortBlockDiagnostics.PortMissing(name, n, axis.Name, have));
        return new PortSel([at], true);
    }

    private static int PortNumber(Value v, string name, string axisName)
    {
        if (v.Kind != ValueKind.Real)
            throw new ExpressionException(PortBlockDiagnostics.PortNotWhole(name, axisName, v.Kind.ToString()));
        double x = v.AsReal();
        if (Math.Abs(x - Math.Round(x)) > 1e-9)
            throw new ExpressionException(PortBlockDiagnostics.PortNotWhole(name, axisName, x.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return (int)Math.Round(x);
    }

    private static int AxisDim(DataCube cube, string axisName)
    {
        for (int d = 0; d < cube.Rank; d++)
            if (cube.Axes[d].Name == axisName) return d;
        throw new InvalidOperationException(axisName);   // unreachable: callers check HasPortAxes first
    }

    /// <summary>A port range evaluated anywhere but a port matrix's arguments.</summary>
    private static Value RefusePortRange()
        => throw new ExpressionException(PortBlockDiagnostics.RangeOutsidePorts());

    /// <summary>
    /// The bracket form's range on a port axis: brackets count positions from 0 with the end left out, so
    /// <c>SP1.S[:, 1:2, 1:2]</c> would be port 2 alone. Refused, pointing at the form that takes port numbers.
    /// </summary>
    private static ExpressionException BracketPortRange(IndexExpr ix, Axis axis)
    {
        string target = ix.Target switch
        {
            CallExpr { Args.Length: 0 } c => c.Name,
            RefExpr r                     => r.Name,
            _                             => "SP1.S",
        };
        return new ExpressionException(PortBlockDiagnostics.BracketRange(axis.Name, target));
    }
}
