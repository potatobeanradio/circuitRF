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
    public sealed record Slot(int SliceIndex, string? Id, string Name, LinkDocument.MarkerSite? Site, string? Error);

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

    [GeneratedRegex(@"@([A-Za-z0-9_][A-Za-z0-9_.\-]*)")]
    private static partial Regex LinkOperand();

    /// <summary>The <c>@name</c> operands of an expression, in order, each once.</summary>
    public static IReadOnlyList<string> OperandNames(string expression)
        => LinkOperand().Matches(expression).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    private static List<Slot> SlotsFor(LinkDocument doc, int tab, Trace t)
    {
        var slots = new List<Slot>();
        if (!t.IsCubeBound || t.LinkError is not null) return slots;   // a refused spec follows nothing

        if (IsSingleCube(t))
        {
            for (int i = 0; i < t.Slice!.Length; i++)
            {
                var e = t.Slice[i];
                if (!e.IsLinked) continue;
                if (e.Link is { } id)
                {
                    var site = doc.FindMarker(id);
                    slots.Add(new Slot(i, id, site?.Marker.Name ?? e.LinkName ?? "the linked marker", site, null));
                }
                else
                {
                    // A name not yet bound to an id — a hand-written file, or a spec typed outside a commit.
                    bool found = doc.TryFindByName(e.LinkName!, tab, out var byName, out var err);
                    slots.Add(new Slot(i, found ? byName.Marker.Id.ToString("D") : null, e.LinkName!,
                                       found ? byName : null, found ? null : err));
                }
            }
        }
        else if (t.Expression is { } expr && expr.Contains('@'))
        {
            foreach (var name in OperandNames(expr))
            {
                bool found = doc.TryFindByName(name, tab, out var site, out var err);
                slots.Add(new Slot(-1, found ? site.Marker.Id.ToString("D") : null, name,
                                   found ? site : null, found ? null : err));
            }
        }
        return slots;
    }
}
