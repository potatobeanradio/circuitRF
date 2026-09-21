// Naming a net is a GESTURE, not a text field — brief-authored-board-2-net-identity.md R-ab2-3.
//
// LayoutShapePropertiesViewModel.CommitNetText already set `Net` per shape and across a
// multi-select. It was correct and nobody found it: the Properties Inspector's Net row is a free
// text box on a panel a user opens for something else, and the thing they actually want to say —
// "this pour is +3V3" — is a right-click on the pour.
//
// ── ONE WRITER, AND THE GESTURE IS THE SELECTION ──────────────────────────────────────────────
//
// R-ab2-3a: it writes the same field through the same setter, one undo entry. So the gesture does
// not grow a writer of its own. It EXTENDS THE SELECTION over the connected piece and then writes,
// which is the multi-select path the panel has always used — and extending the selection is also
// how the user sees what they just named.
//
// ── IT SAYS WHAT IT WILL REACH, BEFORE IT REACHES IT ──────────────────────────────────────────
//
// R-ab2-3c. A gesture whose blast radius is invisible until afterwards is a gesture users stop
// trusting, and this one's is deliberately large: naming one trace names the pour, the vias and
// everything they reach, which is the whole point (R-ab2-2b) and is exactly why it has to be stated
// first. The count comes from the SAME partition the pads resolve through — there is no second
// walk, per this brief's own scope.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;

namespace CircuitRF.Ui.Layout;

/// <summary>What Name Net… would do, counted before anything is committed.</summary>
/// <param name="ShapeIndices">Every shape the commit will write, the clicked one included.</param>
/// <param name="ExistingNames">The names those pieces already carry, distinct. More than zero and
/// different from what is being applied is R-ab2-3d's "asks, naming the existing one".</param>
/// <param name="Sentence">What to show — <i>"names this piece and the 34 shapes joined to it"</i>.</param>
public sealed record LayoutNetNameReach(
    IReadOnlyList<int> ShapeIndices,
    IReadOnlyList<string> ExistingNames,
    string Sentence);

public sealed partial class LayoutEditorViewModel
{
    /// <summary>
    /// The board's copper partitioned into electrically-joined pieces, rebuilt each time it is
    /// asked for.
    /// </summary>
    /// <remarks>
    /// <b>Not cached.</b> Every edit to this document changes it, and a stale partition would name
    /// the wrong shapes — which is silent, because the commit that follows looks entirely ordinary.
    /// It is built only on a right-click and on the commit that follows, so the cost is paid twice
    /// per gesture rather than per frame.
    ///
    /// <para><b>The ROOT's own shapes, both as the copper and as what may be stamped</b> — the
    /// layout editor selects nothing else, and R-ab2-2a is explicit that a <c>Net</c> inside a
    /// shared land pattern would put every placement of it on one net. railRF partitions the
    /// FLATTENED artwork instead, because a pad has to land on something; the two differ in what
    /// they are asked, not in how they answer.</para>
    /// </remarks>
    private PdnCopperPieces CopperPieces() =>
        PdnCopperPieces.Build(Model.Shapes, Technology);

    /// <summary>
    /// Every net name already on this board — what the dialog offers, before whatever the user
    /// types.
    /// </summary>
    /// <remarks>
    /// <b>R-ab2-3b: a free-text field over a board that already says <c>+3V3</c> is how a board
    /// acquires <c>+3v3</c>.</b> The two are different nets to everything downstream and identical
    /// to a reader, which is the worst pair of properties a pair of strings can have.
    /// </remarks>
    public IReadOnlyList<string> NetNamesOnBoard()
    {
        var names = new SortedSet<string>(System.StringComparer.Ordinal);
        foreach (var shape in Model.Shapes)
            if (shape.Net is { Length: > 0 } net) names.Add(net);
        return [.. names];
    }

    /// <summary>
    /// What Name Net… would reach from a right-click at
    /// (<paramref name="wx"/>, <paramref name="wy"/>), or null where the click landed on nothing.
    /// </summary>
    /// <remarks>
    /// <b>The SELECTION wins where the click is inside it</b> (R-ab2-3d: "a selection spanning two
    /// pieces names both, and the sentence says so"). Right-clicking one of several selected shapes
    /// is how every other item on this menu behaves, and naming only the one under the pointer after
    /// a user had deliberately selected six would be the surprise.
    /// </remarks>
    public LayoutNetNameReach? NetNameReachAt(double wx, double wy, long tolDbu)
    {
        long px = (long)System.Math.Round(wx), py = (long)System.Math.Round(wy);

        var seeds = new List<int>();
        var hits = LayoutHitTest.HitStack(Model, Technology, px, py, tolDbu);

        if (hits.Count > 0 && SelectedIndices.Contains(hits[0]))
            seeds.AddRange(SelectedIndices.Where(i => i >= 0 && i < Model.Shapes.Count));
        else if (hits.Count > 0)
            seeds.Add(hits[0]);

        if (seeds.Count == 0) return null;

        var pieces = CopperPieces();
        var reached = pieces.Any ? pieces.ShapesJoinedTo(seeds) : [.. seeds.Distinct().OrderBy(i => i)];
        var existing = pieces.Any
            ? pieces.NamesOn(seeds)
            : (IReadOnlyList<string>)[.. seeds.Select(i => Model.Shapes[i].Net)
                                             .Where(n => n is { Length: > 0 })
                                             .Select(n => n!)
                                             .Distinct(System.StringComparer.Ordinal)];

        return new LayoutNetNameReach(reached, existing, DescribeReach(seeds.Count, reached.Count));
    }

    /// <summary>
    /// <i>"Names this piece and the 34 shapes joined to it"</i> — counted BEFORE the commit
    /// (R-ab2-3c).
    /// </summary>
    internal static string DescribeReach(int seedCount, int totalCount)
    {
        int joined = System.Math.Max(0, totalCount - seedCount);
        string subject = seedCount == 1 ? "this piece" : $"these {seedCount} shapes";
        return joined == 0
            ? $"Names {subject}."
            : joined == 1
                ? $"Names {subject} and the 1 shape joined to it."
                : $"Names {subject} and the {joined} shapes joined to it.";
    }

    /// <summary>
    /// Commits <paramref name="net"/> across <paramref name="reach"/> — one undo entry, through the
    /// one writer.
    /// </summary>
    /// <param name="net">The name, or null/blank to clear it.</param>
    public void ApplyNetName(LayoutNetNameReach reach, string? net)
    {
        System.ArgumentNullException.ThrowIfNull(reach);

        // The selection follows the write, so the user SEES what the sentence said. Done first, so
        // a Properties Inspector refresh triggered by the commit already reads the new selection.
        SetSelection(reach.ShapeIndices, clearOtherKind: false);

        SetNetOnShapes(
            [.. reach.ShapeIndices.Where(i => i >= 0 && i < Model.Shapes.Count).Select(i => Model.Shapes[i])],
            string.IsNullOrWhiteSpace(net) ? null : net.Trim());
    }

    /// <summary>
    /// <b>The one writer of <see cref="LayoutShape.Net"/></b> — R-ab2-3a, and the reason the gesture
    /// needed no second one.
    /// </summary>
    /// <remarks>
    /// This IS <c>LayoutShapePropertiesViewModel.ApplyToEach</c>'s body for the Net field, moved
    /// here so the panel and the canvas gesture cannot come apart: one
    /// <see cref="SetShapeFieldCommand{T}"/> per shape, folded into one
    /// <see cref="CompositeCommand"/> chain, so a multi-select commit is ONE undo entry. A shape
    /// already carrying the value contributes nothing, which is what keeps an undo stack from
    /// growing an entry that changes nothing.
    /// </remarks>
    public void SetNetOnShapes(IReadOnlyList<LayoutShape> shapes, string? net)
    {
        System.ArgumentNullException.ThrowIfNull(shapes);

        IUiCommand? combined = null;
        foreach (var shape in shapes)
        {
            if (shape.Net == net) continue;
            var captured = shape;
            string? old = shape.Net;
            IUiCommand cmd = new SetShapeFieldCommand<string?>(
                Model, "Net", old, net, v => captured.Net = v);
            combined = combined is null ? cmd : new CompositeCommand(combined, cmd);
        }

        if (combined is not null) Execute(combined);
    }
}
