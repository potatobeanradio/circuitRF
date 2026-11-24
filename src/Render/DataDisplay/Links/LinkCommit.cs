// ================================================================
//  LinkCommit.cs  —  a typed `@name`, checked when it is committed
//  (brief-ddl-1 R-ddl1-5, R-ddl1-8; overview D4, D5)
//
//  The spec parser can only see a name. Committing it binds the name to
//  the marker's ID — so renaming the marker later keeps the link, and a
//  later marker that reuses the name never takes it over — and refuses
//  what cannot be followed: an unknown name, an ambiguous one, a cycle.
// ================================================================

using System;
using System.Linq;

namespace CircuitRF.Render.DataDisplay;

public static class LinkCommit
{
    /// <summary>
    /// Binds every <c>@name</c> pin of <paramref name="consumer"/>'s <see cref="Trace.Slice"/> to its marker's id, and
    /// checks every name of a multi-cube expression resolves. False, with the reason, for an unknown or ambiguous name
    /// or a link that would close a cycle; the consumer's slice is then left as it was given. A trace with no link is
    /// always accepted.
    /// </summary>
    public static bool TryCommit(LinkDocument doc, Trace consumer, out string error)
    {
        error = "";
        int tab = Math.Max(0, doc.TabOf(consumer));
        var original = consumer.Slice;

        if (LinkGraph.IsSingleCube(consumer))
        {
            if (!consumer.Slice!.Any(s => s.IsLinked)) return true;
            var bound = (AxisSlice[])consumer.Slice!.Clone();
            for (int i = 0; i < bound.Length; i++)
            {
                var e = bound[i];
                if (!e.IsLinked || e.Link is not null) continue;
                // `@selected` is the trial selection, never a marker (brief-ddl-4 R-ddl4-5).
                if (LinkDocument.IsSelection(e.LinkName))
                {
                    bound[i] = e with { Link = LinkDocument.SelectionId };
                    continue;
                }
                if (!doc.TryFindByName(e.LinkName!, tab, out var site, out error)) return false;
                bound[i] = e with { Link = site.Marker.Id.ToString("D"), LinkName = site.Marker.Name };
            }
            consumer.Slice = bound;
        }
        else if (consumer.Expression is { } expr && expr.Contains('@'))
        {
            foreach (var name in LinkGraph.OperandNames(expr))
                if (!LinkDocument.IsSelection(name) && !doc.TryFindByName(name, tab, out _, out error)) return false;
        }
        else return true;

        if (LinkGraph.Build(doc).Cycles.TryGetValue(consumer, out var chain))
        {
            consumer.Slice = original;
            error = $"That would make a cycle: {chain}.";
            return false;
        }
        return true;
    }
}
