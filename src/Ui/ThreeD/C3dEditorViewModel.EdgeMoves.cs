// Expand Edges and Contract Edges on a drawn sheet and on a lumped port. The geometry is C3dEdgeMoves'; this is the menu, the
// refusals and the write. A menu item is enabled only when the move would do something, and its tip is the sentence saying what.

using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>The rectangle an edge move changes: a drawn sheet's or a port's, its plane, and where its own frame sits in the world.</summary>
    private sealed record EdgeTarget(string Label, C3dRect Rect, C3dPlane Plane, long Offset, C3dSheet? Sheet, C3dPort? Port, long ShiftU, long ShiftV, long ShiftN)
    {
        public long WorldOffset => Offset + ShiftN;
        public C3dEdgeRect World => new(Rect.Min.U + ShiftU, Rect.Min.V + ShiftV, Rect.Min.U + Rect.Size.U + ShiftU, Rect.Min.V + Rect.Size.V + ShiftV);
    }

    private (C3dElaboration Elaboration, C3dPlane Plane, long Offset, string? Self, Em3dAirBox? Box, C3dEdgeMoves Moves)? _edgeMoves;

    /// <summary>The cut of what the target's plane passes through. Cut once per elaboration and plane: a menu asks once an item.</summary>
    private C3dEdgeMoves? EdgeMovesFor(EdgeTarget t)
    {
        if (Elaboration is not { } e) return null;
        var box = CurrentPortContext().Box;
        if (_edgeMoves is { } c && ReferenceEquals(c.Elaboration, e) && c.Plane == t.Plane && c.Offset == t.WorldOffset && c.Self == t.Sheet?.Name && Equals(c.Box, box))
            return c.Moves;
        var moves = new C3dEdgeMoves(e, t.Plane, t.WorldOffset, Document.DbuPerMicron, box, t.Sheet?.Name, Document.DisplayUnit);
        _edgeMoves = (e, t.Plane, t.WorldOffset, t.Sheet?.Name, box, moves);
        return moves;
    }

    private EdgeTarget? SheetEdgeTarget(C3dSheet s, out string? why)
    {
        why = null;
        if (s.Ellipse is not null) { why = CircuitRF.Design.ThreeD.Kernel.C3dFaceEditor.EllipseVertexMove; return null; }   // brief-hzn-13
        if (s.Rect is null) { why = $"'{s.Name}' is not a rectangle: its edges are moved by its corners."; return null; }
        if (s.Locked) { why = LockedRefusal(s.Name); return null; }
        if (s.Placement.Rotate.Count > 0 || s.Placement.MirrorX) { why = $"'{s.Name}' is rotated, so its edges do not lie along its plane's axes."; return null; }
        _ = Resolution;                                       // the numbers beside any expressions
        var o = s.Placement.Origin;
        var (n, u, v) = C3dEdgeMoves.Axes(s.Plane);
        long[] at = [o.X, o.Y, o.Z];
        return new EdgeTarget($"'{s.Name}'", s.Rect, s.Plane, s.Offset, s, null, at[u], at[v], at[n]);
    }

    private EdgeTarget? PortEdgeTarget(C3dPort p, out string? why)
    {
        why = null;
        if (p.Annulus is not null) { why = $"{C3dPorts.Label(p)} is a coaxial port's ring: set its radii in the Inspector."; return null; }
        if (p.Angle != 0) { why = $"{C3dPorts.Label(p)} is turned {C3dTurnedPorts.Degrees(p.Angle)}°: its edges do not lie along its plane's axes. Set its Angle to 0 first."; return null; }
        _ = Resolution;
        return new EdgeTarget(C3dPorts.Label(p), p.Rect, p.Plane, p.Offset, null, p, 0, 0, 0);
    }

    /// <summary>Expand Edges and Contract Edges for a target, or disabled saying why.</summary>
    private IEnumerable<Viewer3DMenuItem> EdgeMenuItems(EdgeTarget? target, string? why)
    {
        var moves = target is null ? null : EdgeMovesFor(target);
        why ??= target is not null && moves is null ? "The scene is still being built." : null;
        foreach (var (header, kind) in new[] { ("Expand Edges", C3dEdgeMoveKind.Expand), ("Contract Edges", C3dEdgeMoveKind.Contract) })
        {
            if (target is null || moves is null) { yield return new Viewer3DMenuItem(header, Enabled: false, Tip: why); continue; }
            C3dEdge[] all = [new(0, false), new(0, true), new(1, false), new(1, true)];
            Viewer3DMenuItem Item(string text, params C3dEdge[] edges)
            {
                var r = moves.Move(kind, target.World, edges);
                return new Viewer3DMenuItem(text, () => Report(MoveEdges(target, kind, edges)), Enabled: r.Moved.Count > 0, Tip: moves.Describe(r, Document.DbuPerMicron));
            }
            var children = new List<Viewer3DMenuItem> { Item("All", all) };
            for (int axis = 0; axis < 2; axis++)
            {
                C3dEdge low = new(axis, false), high = new(axis, true);
                children.Add(Viewer3DMenuItem.Separator);
                children.Add(Item($"{moves.EdgeName(low)} Only", low));
                children.Add(Item($"{moves.EdgeName(high)} Only", high));
                children.Add(Item(moves.EdgeName(high)[1..], low, high));
            }
            yield return new Viewer3DMenuItem(header, Children: children, Tip: kind == C3dEdgeMoveKind.Expand
                ? "Grow an edge until it meets the next solid or sheet."
                : "Shrink an edge back to the next boundary inside the rectangle, never past the opposite edge.");
        }
    }

    /// <summary>Moves the edges of a sheet or a lumped port as one undo entry. Null on success, else why nothing moved.</summary>
    private string? MoveEdges(EdgeTarget t, C3dEdgeMoveKind kind, IReadOnlyList<C3dEdge> edges)
    {
        if (EdgeMovesFor(t) is not { } moves) return "The scene is still being built.";
        var r = moves.Move(kind, t.World, edges);
        string said = moves.Describe(r, Document.DbuPerMicron);
        if (r.Moved.Count == 0) return said;

        var was = (t.Rect.Min, t.Rect.Size);
        var min = new C3dPoint2(r.Rect.U0 - t.ShiftU, r.Rect.V0 - t.ShiftV);
        var size = new C3dPoint2(r.Rect.U1 - r.Rect.U0, r.Rect.V1 - r.Rect.V0);
        var minSpec = C3dBindings.SpecOf(typeof(C3dRect), nameof(C3dRect.Min))!;
        var sizeSpec = C3dBindings.SpecOf(typeof(C3dRect), nameof(C3dRect.Size))!;
        // The components that move, by what each now holds.
        var moved = new (C3dFieldSpec Spec, int K, long From, long To)[]
        {
            (minSpec, 0, was.Min.U, min.U), (minSpec, 1, was.Min.V, min.V), (sizeSpec, 0, was.Size.U, size.U), (sizeSpec, 1, was.Size.V, size.V),
        }.Where(c => c.From != c.To).ToList();
        string title = $"{(kind == C3dEdgeMoveKind.Expand ? "Expand" : "Contract")} {t.Label} edges";

        if (t.Sheet is { } sheet)
        {
            // A sheet takes the drag rule every other gesture takes (ThroughNames): an expression naming a variable writes that
            // variable, or the edit is refused with Replace with Number on offer. An expression of numbers alone holds nothing
            // to follow and no link to lose, so it simply becomes the number, which is all a drag could want of it.
            var arithmetic = moved.Where(c => C3dBindings.GetExpr(t.Rect, c.Spec, c.K) is { } e && C3dExpressionText.Names(e.Expr).Count == 0).ToList();
            int refusals = ExpressionRefusals;
            BeginGroup(title);
            try
            {
                if (arithmetic.Count > 0)
                    EditNames(title, (doc, _) =>
                    {
                        C3dResolver.Resolve(doc, Cell);
                        var rect = ((C3dSheet)doc.Objects.Single(o => o.Name == sheet.Name)).Rect!;
                        foreach (var c in arithmetic) C3dBindings.SetExpr(rect, c.Spec, c.K, null);
                        return null;
                    });
                // By name: the edit above has replaced the object the menu saw.
                ChangeObjects(title, [Document.Objects.FindIndex(o => o.Name == sheet.Name)], o =>
                {
                    var rect = ((C3dSheet)o).Rect!;
                    rect.Min = min;
                    rect.Size = size;
                });
            }
            finally { EndGroup(); }
            if (ExpressionRefusals == refusals) StatusMessage = $"{t.Label}: {said}";
            return null;
        }

        // A port has no drag rule: its place is typed or drawn, and setting it states the numbers (SetPortGeometry).
        var port = t.Port!;
        bool lost = false;
        ChangeRecords(title, _ =>
        {
            foreach (var c in moved)
            {
                lost |= C3dBindings.GetExpr(port.Rect, c.Spec, c.K) is not null;
                C3dBindings.SetExpr(port.Rect, c.Spec, c.K, null);
            }
            port.Rect.Min = min;
            port.Rect.Size = size;
        });
        var resolved = C3dPorts.Resolve(port, Document.DbuPerMicron, CurrentPortContext());
        StatusMessage = $"{t.Label}: {said} " + (resolved.Refusal ?? C3dPortReports.Describe(resolved)) +
                        (lost ? " An expression on a moved edge is a number now." : "");
        return null;
    }
}
