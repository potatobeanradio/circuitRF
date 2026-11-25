// ================================================================
//  LinkGraph.cs  —  who follows whom, and in what order (R-ddl1-8)
//
//  One node per trace; an edge from a consumer to the trace its source
//  marker sits on. Resolution runs in topological order, so a marker is
//  read after its own trace has followed whatever IT follows. A cycle —
//  including a marker whose own trace is pinned by that marker — has no
//  order: it is refused when it is made (LinkCommit) and, in a file that
//  already holds one, drawn frozen with the chain named.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircuitRF.Render.DataDisplay;

public sealed partial class LinkGraph
{
    /// <summary>
    /// One linked place in a consumer: a pinned slice entry (<see cref="SliceIndex"/> ≥ 0) or an <c>@name</c> operand of
    /// a multi-cube expression (−1). <see cref="Site"/> is null when the source is not in the document, and then
    /// <see cref="Error"/> says why when there is more to say than "gone" (an unbound name that resolves to nothing).
    /// </summary>
    /// <param name="IsSelection">The slot follows the trial selection (brief-ddl-4 R-ddl4-5), which has no site.</param>
    /// <param name="Control">The display control the slot follows (brief-ddl-5); it sits on no trace.</param>
    public sealed record Slot(int SliceIndex, string? Id, string Name, LinkDocument.MarkerSite? Site, string? Error,
                              bool IsSelection = false, DisplayControl? Control = null);

    /// <summary>The <see cref="Slot.SliceIndex"/> of an <c>@name</c> operand of a multi-cube expression.</summary>
    public const int ExpressionOperandSlot = -1;

    /// <summary>The <see cref="Slot.SliceIndex"/> of a contour's frequency (brief-ddl-4 R-ddl4-3).</summary>
    public const int ContourFreqSlot = -2;

    /// <summary>The <see cref="Slot.SliceIndex"/> of an expression's frequency (brief-ddl-4 R-ddl4-4).</summary>
    public const int ExpressionFreqSlot = -3;

    private readonly Dictionary<Trace, List<Slot>> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Trace, string> _cycles = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every consumer, each after every consumer its sources sit on.</summary>
    public IReadOnlyList<Trace> Order { get; }

    /// <summary>The consumers on a cycle, each with the chain written from itself.</summary>
    public IReadOnlyDictionary<Trace, string> Cycles => _cycles;

    public IReadOnlyList<Slot> SlotsOf(Trace t) => _slots.TryGetValue(t, out var s) ? s : [];

    private LinkGraph(LinkDocument doc)
    {
        foreach (var (ti, _, t) in doc.Traces())
            if (SlotsFor(doc, ti, t) is { Count: > 0 } s) _slots[t] = s;

        var order = new List<Trace>();
        var state = new Dictionary<Trace, int>(ReferenceEqualityComparer.Instance);
        var path  = new List<Trace>();
        var via   = new List<string>();   // via[k]: the marker that takes path[k] to path[k + 1]

        void Visit(Trace t)
        {
            state[t] = 1;
            path.Add(t);
            foreach (var slot in _slots[t])
            {
                if (slot.Site is not { } site) continue;
                var s = site.Trace;
                if (!_slots.ContainsKey(s)) continue;          // a source on a trace that follows nothing
                state.TryGetValue(s, out int st);
                if (st == 1)
                {
                    int p = path.FindIndex(x => ReferenceEquals(x, s));
                    var members = path.Skip(p).ToList();
                    var edges   = via.Skip(p).Append(slot.Name).ToList();
                    for (int k = 0; k < members.Count; k++)
                        if (!_cycles.ContainsKey(members[k]))
                            _cycles[members[k]] = Chain(members, edges, k);
                }
                else if (st == 0)
                {
                    via.Add(slot.Name);
                    Visit(s);
                    via.RemoveAt(via.Count - 1);
                }
            }
            path.RemoveAt(path.Count - 1);
            state[t] = 2;
            order.Add(t);
        }

        foreach (var t in _slots.Keys.ToList())
            if (!state.ContainsKey(t)) Visit(t);
        Order = order;
    }

    public static LinkGraph Build(LinkDocument doc) => new(doc);

    /// <summary>"Pout pinned by m1 on Gt, pinned by m2 on Pout", starting at member <paramref name="from"/>.</summary>
    private static string Chain(List<Trace> members, List<string> edges, int from)
    {
        int n = members.Count;
        var parts = new List<string> { NameOf(members[from]) };
        for (int k = 0; k < n; k++)
        {
            int i = (from + k) % n;
            parts.Add($"pinned by {edges[i]} on {NameOf(members[(i + 1) % n])}");
        }
        return parts[0] + " " + string.Join(", ", parts.Skip(1));
    }

    /// <summary>A trace as a person would name it in a sentence: its cube, its expression, or its description.</summary>
    public static string NameOf(Trace t)
        => t.CubeName ?? t.Expression ?? t.ShortDescription;

    /// <summary>Whether a trace is a single-cube spec — its pins live in <see cref="Trace.Slice"/> — rather than a
    /// multi-cube expression, whose <c>@name</c> operands live in its text. The same test <c>TraceResolve</c> makes.</summary>
    internal static bool IsSingleCube(Trace t) => t.CubeName is not null && t.Slice is not null;

    [GeneratedRegex(@"@([A-Za-z0-9_][A-Za-z0-9_.]*)")]
    private static partial Regex LinkOperand();

    /// <summary>The <c>@name</c> operands of an expression, in order, each once.</summary>
    public static IReadOnlyList<string> OperandNames(string expression)
        => LinkOperand().Matches(expression).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    private static List<Slot> SlotsFor(LinkDocument doc, int tab, Trace t)
    {
        var slots = new List<Slot>();
        // A contour's frequency (R-ddl4-3): a contour trace is not cube-bound, so it is read before that test.
        if (t.ContourData is { IsFreqLinked: true } cd)
            slots.Add(MarkerSlot(doc, tab, ContourFreqSlot, cd.FreqLink, cd.FreqLinkName!));
        if (!t.IsCubeBound || t.LinkError is not null) return slots;   // a refused spec follows nothing

        if (IsSingleCube(t))
        {
            for (int i = 0; i < t.Slice!.Length; i++)
            {
                var e = t.Slice[i];
                if (!e.IsLinked) continue;
                if (LinkDocument.IsSelection(e.Link) || (e.Link is null && LinkDocument.IsSelection(e.LinkName)))
                    slots.Add(new Slot(i, LinkDocument.SelectionId, LinkDocument.SelectionId, null, null, IsSelection: true));
                else
                    slots.Add(MarkerSlot(doc, tab, i, e.Link, e.LinkName ?? "the linked marker"));
            }
        }
        else if (t.Expression is { } expr)
        {
            if (expr.Contains('@'))
                foreach (var name in OperandNames(expr))
                {
                    if (LinkDocument.IsSelection(name))
                    {
                        slots.Add(new Slot(ExpressionOperandSlot, LinkDocument.SelectionId, name, null, null, IsSelection: true));
                        continue;
                    }
                    slots.Add(MarkerSlot(doc, tab, ExpressionOperandSlot, null, name));
                }
            // An expression's frequency (R-ddl4-4).
            if (t.IsExpressionFreqLinked)
                slots.Add(MarkerSlot(doc, tab, ExpressionFreqSlot, t.ExpressionFreqLink, t.ExpressionFreqLinkName!));
        }
        return slots;
    }

    /// <summary>
    /// A slot following a marker: by its id when it has one (a marker that is gone leaves the site null, and the
    /// consumer freezes), else by an unbound name — a hand-written file, or a spec typed outside a commit.
    /// </summary>
    private static Slot MarkerSlot(LinkDocument doc, int tab, int sliceIndex, string? id, string name)
    {
        if (id is not null)
        {
            if (doc.FindControl(id) is { } control) return new Slot(sliceIndex, id, control.Name, null, null, Control: control);
            var site = doc.FindMarker(id);
            return new Slot(sliceIndex, id, site?.Marker.Name ?? name, site, null);
        }
        // A control by name (brief-ddl-5 R-ddl5-8) — controls and markers share one name space.
        if (doc.FindControlByName(name, tab) is { } named) return new Slot(sliceIndex, named.Id, named.Name, null, null, Control: named);
        bool found = doc.TryFindByName(name, tab, out var byName, out var err);
        return new Slot(sliceIndex, found ? byName.Marker.Id.ToString("D") : null, name,
                        found ? byName : null, found ? null : err);
    }
}
