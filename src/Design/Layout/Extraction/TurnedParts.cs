// A two-terminal part placed at 180° to its schematic's pin order, read from the copper it sits on
// (field report, 2026-09-22).
//
// ── WHAT WAS REPORTED ───────────────────────────────────────────────────────────────────────────
//
// A designer drew a schematic for an imported Gerber board, ran Update Layout, and dragged each
// generated footprint onto its lands. Picking `vsmps2` in railRF outlined the whole board; so did
// `Xin`, which is a crystal-local net. The extraction was right about the copper. The NAMES were
// wrong: 30 of the 55 two-pin parts sat at 180° to the schematic's pin order, and each one put a
// schematic net on the ground side of its part — so the net walk seeded the ground pour and took
// everything with it.
//
// Nothing on the screen could have told him. An 0402 at 0° and an 0402 at 180° are the same
// picture: two identical lands, and the only thing that differs is which one the footprint calls
// pin 1 — a number printed nowhere on the board.
//
// ── WHY THE COPPER MAY DECIDE THIS, AND FOR THESE PARTS ONLY ─────────────────────────────────────
//
// A resistor, a capacitor and an inductor have two INTERCHANGEABLE terminals. Which land is "pin 1"
// is a bookkeeping convention with no physical meaning, and LVS already reads them that way
// (`LvsReduce.ParallelKey`: for those three the net PAIR is unordered). What the schematic states
// that does mean something is the pair — "C19 joins Xin to ground" — and the copper says which land
// is on which side of that pair. So for those three kinds, and only those, the pair is bound to the
// lands the way the copper agrees with.
//
// Not a diode, whose reversal is a different circuit, and not a two-terminal CELL, whose symmetry
// nothing here knows — the same line `ParallelKey` draws, for the same reasons.
//
// ── WHAT COUNTS AS EVIDENCE ─────────────────────────────────────────────────────────────────────
//
// The GALVANIC partition — `CopperPieces`, which is `DrcConnectivity`'s, vias included. That is what
// makes a decoupling capacitor decidable: its ground land usually sits alone on a pad with a via
// down to the plane, and it is only through that via that the land shares a net with the forty other
// ground pins on the board. Top copper alone gives it no evidence at all (measured on the reported
// board: read on the top layer alone, 18 parts turn and two nets stay shorted to ground; read
// through the vias, 30 turn and every net the designer named comes back the size he said).
//
// A net's evidence is the names every OTHER pin standing on it carries, plus a name stamped on its
// copper. A part turns only where the turn makes STRICTLY more of those agree; a tie leaves the
// schematic's order alone, because a guess between two readings the copper cannot tell apart is
// exactly the guess this file exists not to make.
//
// ── IT IS A READING, AND IT SAYS SO ─────────────────────────────────────────────────────────────
//
// The turned parts are RETURNED, never silently absorbed: railRF names them and offers to turn them
// in the layout, which is the only fix that makes the document itself agree — LVS, the placement
// table and the bill of materials all read the `.clay`, not this.

using System.Linq;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>
/// One placed part the copper reads as turned 180° against its schematic's pin order.
/// </summary>
/// <param name="Refdes">What the part is called on the board.</param>
/// <param name="InstanceIndex">Its index in the root layout's <see cref="LayoutView.Instances"/> —
/// what <see cref="TurnedParts.HalfTurn"/> is applied to.</param>
/// <param name="Land1">Where the land the footprint calls its FIRST pin sits, as placed.</param>
/// <param name="Land2">Where the other one sits.</param>
/// <param name="Agreeing">How many other pins and stamps on the two nets agree with the turn, less
/// how many agreed with the schematic's order — the margin the decision was made by.</param>
public sealed record TurnedPart(
    string Refdes, int InstanceIndex, (long X, long Y) Land1, (long X, long Y) Land2, int Agreeing);

/// <summary>
/// Reads which symmetric two-terminal parts sit at 180° to their schematic — see the file header.
/// </summary>
public static class TurnedParts
{
    /// <summary>What <see cref="Read"/> found.</summary>
    /// <param name="Pads">The pads, with every turned part's two lands exchanged — so <c>C19.1</c>
    /// still names the terminal the schematic calls pin 1, now at the land that terminal is on.</param>
    /// <param name="Turned">The parts that were turned, in placement order.</param>
    public sealed record Reading(IReadOnlyList<PlacedPin> Pads, IReadOnlyList<TurnedPart> Turned);

    /// <summary>The device kinds whose two terminals are interchangeable — <c>ParallelKey</c>'s own.</summary>
    public static bool IsSymmetric(Lvs.DeviceKind kind) =>
        kind is Lvs.DeviceKind.Resistor or Lvs.DeviceKind.Capacitor or Lvs.DeviceKind.Inductor;

    /// <summary>
    /// Reads the turned parts off <paramref name="pieces"/>.
    /// </summary>
    /// <param name="pads">The artwork's pads, as <c>PlacedPins.Of</c> produced them.</param>
    /// <param name="origins">One per pad, same order — where each came from, and on which layer its
    /// land is. A pad with no origin is read as fixed evidence and never turned.</param>
    /// <param name="view">The root layout — its instances say which placement is an array.</param>
    /// <param name="kindOf">The device kind of a placement, by instance index.</param>
    /// <param name="pieces">The galvanic partition over the FLATTENED copper.</param>
    public static Reading Read(
        IReadOnlyList<PlacedPin> pads,
        IReadOnlyList<PlacedPinOrigin> origins,
        LayoutView view,
        Func<int, Lvs.DeviceKind> kindOf,
        CopperPieces pieces)
    {
        ArgumentNullException.ThrowIfNull(pads);
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(kindOf);
        ArgumentNullException.ThrowIfNull(pieces);

        if (!pieces.Any || origins.Count != pads.Count || pads.Count == 0)
            return new Reading(pads, []);

        // Every pad's net INDEX — the galvanic identity, named or not. On its own land's layer, for
        // R-ab2-2d's reason: a pad over a plane is not on the plane.
        var netOf = new int[pads.Count];
        for (int i = 0; i < pads.Count; i++)
            netOf[i] = pieces.PieceAt(pads[i].X, pads[i].Y, origins[i].Layer);

        // The candidates: one placement, exactly two lands, a symmetric kind, two DIFFERENT stated
        // nets on two DIFFERENT pieces of copper. A part whose lands share a net is shorted and has
        // no orientation to read; a part with one land on nothing has no evidence on that side.
        var byInstance = new Dictionary<int, List<int>>();
        for (int i = 0; i < pads.Count; i++)
        {
            if (!byInstance.TryGetValue(origins[i].Instance, out var list))
                byInstance[origins[i].Instance] = list = [];
            list.Add(i);
        }

        var candidates = new List<(int Instance, int A, int B)>();
        foreach (var (instance, list) in byInstance.OrderBy(kv => kv.Key))
        {
            if (list.Count != 2) continue;
            if (instance < 0 || instance >= view.Instances.Count) continue;
            var inst = view.Instances[instance];
            if (Math.Max(1, inst.Rows) * Math.Max(1, inst.Cols) != 1) continue;
            if (!IsSymmetric(kindOf(instance))) continue;

            int a = list[0], b = list[1];
            if (pads[a].Net is not { Length: > 0 } na || pads[b].Net is not { Length: > 0 } nb) continue;
            if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) continue;
            if (netOf[a] < 0 || netOf[b] < 0 || netOf[a] == netOf[b]) continue;

            candidates.Add((instance, a, b));
        }

        if (candidates.Count == 0) return new Reading(pads, []);

        // ── The evidence: every name standing on every net ──────────────────────────────────────
        var labels = new Dictionary<int, Dictionary<string, int>>();
        void Add(int net, string name, int delta)
        {
            if (net < 0) return;
            if (!labels.TryGetValue(net, out var names))
                labels[net] = names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            names[name] = names.GetValueOrDefault(name) + delta;
        }
        int Count(int net, string name) =>
            labels.TryGetValue(net, out var names) ? names.GetValueOrDefault(name) : 0;

        for (int i = 0; i < pads.Count; i++)
            if (pads[i].Net is { Length: > 0 } net) Add(netOf[i], net, +1);

        foreach (int net in netOf.Distinct())
            if (pieces.NameOfNet(net) is { Length: > 0 } stamped) Add(net, stamped, +1);

        // ── Turn a part only where the turn STRICTLY improves agreement ─────────────────────────
        //
        // One pass can turn a part whose neighbour turns later and settles it, so passes repeat until
        // nothing moves. Each turn strictly raises the total agreement, which is bounded, so this
        // terminates; the pass cap is a belt over that braces.
        var turned = new bool[candidates.Count];
        for (int pass = 0; pass < 32; pass++)
        {
            bool moved = false;
            for (int c = 0; c < candidates.Count; c++)
            {
                var (_, a, b) = candidates[c];
                string nameAtA = turned[c] ? pads[b].Net! : pads[a].Net!;
                string nameAtB = turned[c] ? pads[a].Net! : pads[b].Net!;

                // Its own two names are taken off first — a part does not vote for itself.
                Add(netOf[a], nameAtA, -1);
                Add(netOf[b], nameAtB, -1);

                int stay = Count(netOf[a], nameAtA) + Count(netOf[b], nameAtB);
                int flip = Count(netOf[a], nameAtB) + Count(netOf[b], nameAtA);

                if (flip > stay)
                {
                    turned[c] = !turned[c];
                    (nameAtA, nameAtB) = (nameAtB, nameAtA);
                    moved = true;
                }

                Add(netOf[a], nameAtA, +1);
                Add(netOf[b], nameAtB, +1);
            }
            if (!moved) break;
        }

        // ── …AND A SYMMETRY THE COPPER CANNOT BREAK IS NOT A READING ────────────────────────────
        //
        // Two capacitors that disagree and nothing else on either net: the search turns whichever it
        // visits first, and turning the OTHER one instead fits the copper exactly as well. Nothing
        // here can say which strip is the supply, so neither may be called turned. In general: a
        // group of parts joined by the nets they share, compared against the same group with every
        // part in it swapped. Where the copper tells them apart the better one already won; where it
        // does not, the reading that turns FEWER parts is kept — the schematic is the author's
        // statement and it wins every tie — and an even split turns none.
        SettleSymmetricGroups(candidates, turned, pads, netOf, labels);

        // ── The answer ──────────────────────────────────────────────────────────────────────────
        var result = pads.ToArray();
        var found = new List<TurnedPart>();
        for (int c = 0; c < candidates.Count; c++)
        {
            if (!turned[c]) continue;
            var (instance, a, b) = candidates[c];

            // The margin, re-measured against the settled evidence: what the turn is worth over the
            // schematic's own order, with this part's vote taken off.
            string atA = pads[b].Net!, atB = pads[a].Net!;
            Add(netOf[a], atA, -1);
            Add(netOf[b], atB, -1);
            int margin = Count(netOf[a], atA) + Count(netOf[b], atB)
                       - Count(netOf[a], pads[a].Net!) - Count(netOf[b], pads[b].Net!);
            Add(netOf[a], atA, +1);
            Add(netOf[b], atB, +1);

            // The lands trade places; the pins keep their names and their nets.
            result[a] = pads[a] with { X = pads[b].X, Y = pads[b].Y };
            result[b] = pads[b] with { X = pads[a].X, Y = pads[a].Y };

            var (first, second) = FirstPinFirst(pads[a], pads[b]);
            found.Add(new TurnedPart(
                pads[a].Refdes ?? view.Instances[instance].DisplayRefDes ?? $"#{instance}",
                instance, (first.X, first.Y), (second.X, second.Y), margin));
        }

        return new Reading(result, found);
    }

    private static void SettleSymmetricGroups(
        List<(int Instance, int A, int B)> candidates, bool[] turned,
        IReadOnlyList<PlacedPin> pads, int[] netOf,
        Dictionary<int, Dictionary<string, int>> labels)
    {
        // Union-find over the nets the candidates join.
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
            if (p == x) return x;
            int root = Find(p);
            parent[x] = root;
            return root;
        }
        foreach (var (_, a, b) in candidates)
        {
            int ra = Find(netOf[a]), rb = Find(netOf[b]);
            if (ra != rb) parent[ra] = rb;
        }

        foreach (var group in Enumerable.Range(0, candidates.Count).GroupBy(c => Find(netOf[candidates[c].A])))
        {
            var members = group.ToList();
            int turnedNow = members.Count(c => turned[c]);
            if (turnedNow == 0) continue;

            // The group's score as it stands, and with every member swapped — Σ c² over the names on
            // each of its nets, which counts agreeing PAIRS. The labels hold the settled state.
            var nets = members.SelectMany(c => new[] { netOf[candidates[c].A], netOf[candidates[c].B] })
                              .Distinct().ToList();
            long Score() => nets.Sum(n => labels.TryGetValue(n, out var names)
                ? names.Values.Sum(v => (long)v * v) : 0L);

            long asSettled = Score();
            foreach (int c in members) Swap(c);
            long swapped = Score();
            int turnedSwapped = members.Count - turnedNow;

            bool keepSwapped = swapped > asSettled
                            || (swapped == asSettled && turnedSwapped < turnedNow);
            bool tie = swapped == asSettled && turnedSwapped == turnedNow;

            if (tie)
            {
                // Back to the schematic's own order for every member: an even split is no reading.
                foreach (int c in members) if (turned[c]) Swap(c);
            }
            else if (!keepSwapped)
            {
                foreach (int c in members) Swap(c);   // undo the trial
            }
        }

        void Swap(int c)
        {
            var (_, a, b) = candidates[c];
            string atA = turned[c] ? pads[b].Net! : pads[a].Net!;
            string atB = turned[c] ? pads[a].Net! : pads[b].Net!;
            Bump(netOf[a], atA, -1); Bump(netOf[b], atB, -1);
            Bump(netOf[a], atB, +1); Bump(netOf[b], atA, +1);
            turned[c] = !turned[c];
        }

        void Bump(int net, string name, int delta)
        {
            if (!labels.TryGetValue(net, out var names))
                labels[net] = names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            names[name] = names.GetValueOrDefault(name) + delta;
        }
    }

    /// <summary>
    /// <paramref name="before"/> turned 180° about the midpoint of its two lands — so each land ends
    /// up exactly where the other one was, whatever the footprint's own origin.
    /// </summary>
    /// <remarks>
    /// A land's world position is <c>T + R(θ)·p</c>. With θ advanced by 180° the rotated term changes
    /// sign, so asking for land 1 to land on land 2 (and the other way round) gives
    /// <c>T' = P1 + P2 − T</c> — exact integer arithmetic, and independent of the mirror flag, which
    /// the transform applies before the rotation. Rotating about the footprint's own origin instead
    /// would only be right for a footprint drawn centred on it.
    /// </remarks>
    public static LayoutInstance HalfTurn(LayoutInstance before, TurnedPart part)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(part);

        var after = LayoutGeometry.Clone(before);
        after.X = part.Land1.X + part.Land2.X - before.X;
        after.Y = part.Land1.Y + part.Land2.Y - before.Y;
        after.RotationDegrees = before.RotationDegrees + 180.0;   // normalized by the setter
        return after;
    }

    /// <summary>The sentence a report prints, or empty where nothing was turned.</summary>
    public static string Sentence(IReadOnlyList<TurnedPart> turned)
    {
        ArgumentNullException.ThrowIfNull(turned);
        if (turned.Count == 0) return "";

        string names = string.Join(", ", turned.Select(t => t.Refdes));
        return turned.Count == 1
            ? $"{names} is placed at 180° to its schematic: its pin 1 sits on the copper its pin 2 " +
              "belongs to. A two-terminal part looks the same either way round, so railRF reads its " +
              "nets from the copper it sits on. Turn it in the layout to make the document agree."
            : $"{turned.Count} parts are placed at 180° to their schematic — {names}: each one's pin 1 " +
              "sits on the copper its pin 2 belongs to. A two-terminal part looks the same either way " +
              "round, so railRF reads their nets from the copper they sit on. Turn them in the layout " +
              "to make the document agree.";
    }

    private static (PlacedPin First, PlacedPin Second) FirstPinFirst(PlacedPin a, PlacedPin b) =>
        string.CompareOrdinal(a.Pin ?? "", b.Pin ?? "") <= 0 ? (a, b) : (b, a);
}
