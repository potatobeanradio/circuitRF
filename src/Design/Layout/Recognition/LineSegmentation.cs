// One trace → a chain of line elements — brief-artsch-5-traces-to-line-elements.md R-as5-2 … R-as5-5, overview D13,
// D14; docs/design/artwork-to-schematic.md §6.3.
//
// The trace review hands over a trace as pieces (straight strips, each with a width), the joins between them
// (a straight gap — a jog, a width step, a taper the piece finder cannot see because its edges are not
// parallel — or a TraceCorner), and its cuts. This walks them once, start to end:
//
//   1. every cut is read (LineTypeChoice), and a run of one reading shorter than max(2·W, 0.5 mm) takes its
//      neighbours' — a pad's edge or a via's antipad is not a change of line type;
//   2. a piece shorter than max(W, 100 µm) between straight joins takes its longer neighbour's width and type
//      — its length stays in the line, it is never dropped (D14: a short thin line is an inductor);
//   3. pieces of one type and one width class (TraceImpedanceAnalysis.MergeToleranceMicrons) are ONE line;
//      a width change is a step (the two lines abut) or, in a microstrip region, a taper when the ramp is at
//      least 2·W of its narrow end; a corner is an MBEND in a microstrip region and centre-line length
//      anywhere else.
//
// Lengths follow R-as5-5's reference planes: an MBEND owns its corner square, so each adjoining line stops W/2
// short of the corner point. The ends of the chain are measured to the chain's own end points here; what owns
// them — a pad, a via's land, a junction — is the caller's to apply, because only the caller knows what the
// end landed on.

using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>One element being built. Lengths and widths in DBU until <see cref="LineRecognition"/> writes it.</summary>
internal sealed class LineDraft
{
    public LineElementType Type;
    public LineKind Kind;
    public List<string> Nodes = [];
    public string Source = "";
    public LayerKey Layer;

    public double L;
    public double W, W2;
    public double[] Widths = [];
    public double Angle;
    public MicrostripBendMiter Miter;
    public List<(long X, long Y)> Anchor = [];

    // ── the cuts it covers, length-weighted ────────────────────────────────────────────────────────
    public double WidthLen, WidthSum;
    public double SolvedLen, ZSum, ESum;
    public double GapLLen, GapLSum, GapRLen, GapRSum;
    public readonly Dictionary<string, double> Reasons = [];
    public readonly Dictionary<string, double> Below = [], Above = [];

    public bool Fresh => WidthLen <= 0;
    public bool IsLine => Type is LineElementType.MLIN or LineElementType.CPWG or LineElementType.SLIN or LineElementType.TLIN;

    public void AddWidth(double w, double len)
    {
        double weight = Math.Max(len, 1e-9);
        WidthLen += weight;
        WidthSum += w * weight;
        W = WidthSum / WidthLen;
    }

    public void AddStation(TraceStation s, LineReading reading)
    {
        double len = s.Length;
        if (s.Z0 is { } z) { SolvedLen += len; ZSum += z * len; ESum += (s.Eeff ?? 1) * len; }
        if (s.GapLeft is { } gl) { GapLLen += len; GapLSum += gl * len; }
        if (s.GapRight is { } gr) { GapRLen += len; GapRSum += gr * len; }
        if (reading.Reason is { } r) Reasons[r] = Reasons.GetValueOrDefault(r) + len;
        if (s.ReferenceBelow is { } b) Below[b] = Below.GetValueOrDefault(b) + len;
        if (s.ReferenceAbove is { } a) Above[a] = Above.GetValueOrDefault(a) + len;
    }

    /// <summary>
    /// Takes <paramref name="other"/> in, end to end: its length, its width and its cuts. The caller has
    /// already re-pointed the shared end. <paramref name="atStart"/> says which end of this line met it, and
    /// <paramref name="otherAtStart"/> which of its own — so the anchor still runs start to end.
    /// </summary>
    public void Absorb(LineDraft other, bool atStart, bool otherAtStart)
    {
        L += other.L;
        WidthLen += other.WidthLen;
        WidthSum += other.WidthSum;
        if (WidthLen > 0) W = WidthSum / WidthLen;
        SolvedLen += other.SolvedLen; ZSum += other.ZSum; ESum += other.ESum;
        GapLLen += other.GapLLen; GapLSum += other.GapLSum; GapRLen += other.GapRLen; GapRSum += other.GapRSum;
        foreach (var (src, dst) in new[] { (other.Reasons, Reasons), (other.Below, Below), (other.Above, Above) })
            foreach (var (k, v) in src) dst[k] = dst.GetValueOrDefault(k) + v;
        var theirs = new List<(long X, long Y)>(other.Anchor);
        if (atStart == otherAtStart) theirs.Reverse();
        Anchor = atStart ? [.. theirs, .. Anchor] : [.. Anchor, .. theirs];
    }

    public double? Z0 => SolvedLen > 0 ? ZSum / SolvedLen : null;
    public double? Eeff => SolvedLen > 0 ? ESum / SolvedLen : null;
    public double? GapLeft => GapLLen > 0 ? GapLSum / GapLLen : null;
    public double? GapRight => GapRLen > 0 ? GapRSum / GapRLen : null;
    public string? Reason => Reasons.Count == 0 ? null : Reasons.MaxBy(kv => kv.Value).Key;
    public string? ReferenceBelow => Below.Count == 0 ? null : Below.MaxBy(kv => kv.Value).Key;
    public string? ReferenceAbove => Above.Count == 0 ? null : Above.MaxBy(kv => kv.Value).Key;
}

/// <summary>A chain end: where it is, and the direction of travel out of the trace there.</summary>
internal readonly record struct ChainEnd(double X, double Y, double Dx, double Dy, string Kind, string? Junction, double Width);

/// <summary>One trace, segmented.</summary>
/// <param name="Drafts">Its elements in order, start to end.</param>
/// <param name="StartNode">The placeholder node at the trace's start — the first line's first node.</param>
/// <param name="EndNode">The placeholder at its end.</param>
internal sealed record TraceLines(
    TraceRun Run, int Island, List<LineDraft> Drafts, string StartNode, string EndNode, ChainEnd Start, ChainEnd End)
{
    /// <summary>The attachments the trace runs through, each a node between two of its lines.</summary>
    public IReadOnlyList<string> Taps { get; init; } = [];

    /// <summary>Taps at a point another tap took: each is that tap's node.</summary>
    public IReadOnlyList<(string Tap, string SameAs)> TapAliases { get; init; } = [];

    public LineDraft First => Drafts.First(d => d.IsLine);
    public LineDraft Last => Drafts.Last(d => d.IsLine);
}

/// <summary>What segmentation counts for the report.</summary>
internal sealed class SegmentationCounts
{
    public List<(long X, long Y)> Slivers { get; } = [];
    public List<(long X, long Y)> UnmodelledBends { get; } = [];
    public List<(long X, long Y)> UnmodelledSteps { get; } = [];
}

/// <summary>R-as5-2 … R-as5-5 for one trace.</summary>
internal static class LineSegmentation
{
    /// <summary>A run of one reading shorter than this many widths (or <see cref="MinRunMicrons"/>) takes its
    /// neighbours' (R-as5-2).</summary>
    public const double MinRunWidths = 2;
    public const double MinRunMicrons = 500;

    /// <summary>A piece shorter than this many widths (or <see cref="SliverMicrons"/>) is absorbed (R-as5-3).</summary>
    public const double SliverWidths = 1;
    public const double SliverMicrons = 100;

    /// <summary>A width ramp at least this many of its NARROW end's widths long is an MTAPER (R-as5-4).</summary>
    public const double TaperMinWidths = 2;

    private const int Unsolved = 4;

    private sealed class Atom
    {
        public double Len, W;
        public int Cat;
        public int Piece;
        public int FirstStation, EndStation;            // [first, end) into the trace's stations
        public double Ax, Ay, Bx, By;
        public double S0, S1;                           // along the trace
        public string? TapAfter;                        // a part, via or port this atom ends on

        public Atom SplitAt(double s, IReadOnlyList<TraceStation> stations)
        {
            double f = (s - S0) / Math.Max(S1 - S0, 1e-9);
            double mx = Ax + f * (Bx - Ax), my = Ay + f * (By - Ay);
            int cut = FirstStation;
            while (cut < EndStation && stations[cut].S < s) cut++;
            var tail = new Atom
            {
                Len = S1 - s, W = W, Cat = Cat, Piece = Piece, FirstStation = cut, EndStation = EndStation,
                Ax = mx, Ay = my, Bx = Bx, By = By, S0 = s, S1 = S1, TapAfter = TapAfter,
            };
            Len = s - S0; EndStation = cut; Bx = mx; By = my; S1 = s; TapAfter = null;
            return tail;
        }
    }

    /// <summary>Segments <paramref name="run"/>, on <paramref name="island"/>.</summary>
    /// <param name="onIsland">The parts' terminals, vias' ends and ports on the trace's island: one the trace
    /// runs THROUGH — within a line end's attach reach of its centre line, more than a width from either end — splits the line there
    /// (a shunt part's pad standing on a line, a via in the middle of one).</param>
    public static TraceLines Segment(TraceRun run, int island, RecognitionOptions options, LineBinder binder,
                                     int dbuPerMicron, SegmentationCounts counts,
                                     IReadOnlyList<(string Name, long X, long Y)>? onIsland = null)
    {
        var pieces = run.Pieces;
        var stations = run.Stations;
        var readings = stations.Select(s => LineTypeChoice.Of(s, options, binder)).ToArray();

        // ── 1. each cut's category, short runs absorbed ─────────────────────────────────────────────
        var cat = readings.Select(r => r.Solved ? (int)r.Kind : Unsolved).ToArray();
        double meanW = stations.Count > 0 ? stations.Average(s => s.Width) : pieces.Average(p => p.Width);
        Smooth(cat, stations, Math.Max(MinRunWidths * meanW, MinRunMicrons * dbuPerMicron));

        // ── the pieces' places along the trace, and the cuts on each ────────────────────────────────
        int n = pieces.Count;
        var dir = new (double X, double Y)[n];
        var len = new double[n];
        var s0 = new double[n];
        double at = 0;
        for (int i = 0; i < n; i++)
        {
            var p = pieces[i];
            len[i] = Math.Sqrt(Sq(p.X1 - p.X0) + Sq(p.Y1 - p.Y0));
            dir[i] = len[i] > 0 ? ((p.X1 - p.X0) / len[i], (p.Y1 - p.Y0) / len[i]) : (1, 0);
            if (i > 0) at += Math.Sqrt(Sq(p.X0 - pieces[i - 1].X1) + Sq(p.Y0 - pieces[i - 1].Y1));
            s0[i] = at;
            at += len[i];
        }

        // ── atoms: each piece cut where the category changes inside it ──────────────────────────────
        var atoms = new List<Atom>();
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var p = pieces[i];
            double sEnd = s0[i] + len[i];
            int first = k;
            while (k < stations.Count && stations[k].S <= sEnd + 1e-6) k++;
            if (i == n - 1) k = stations.Count;
            if (first == k)
            {
                // A piece with no cut of its own takes the category of the nearest one.
                int near = Math.Clamp(first == 0 ? 0 : first - 1, 0, Math.Max(0, stations.Count - 1));
                atoms.Add(new Atom { Len = len[i], W = p.Width, Cat = cat.Length > 0 ? cat[near] : (int)LineKind.Tlin, Piece = i,
                                     FirstStation = first, EndStation = first, Ax = p.X0, Ay = p.Y0, Bx = p.X1, By = p.Y1,
                                     S0 = s0[i], S1 = sEnd });
                continue;
            }
            int a = first;
            double from = s0[i];
            while (a < k)
            {
                int b = a + 1;
                while (b < k && cat[b] == cat[a]) b++;
                double to = b < k ? 0.5 * (stations[b - 1].S + 0.5 * stations[b - 1].Length + stations[b].S - 0.5 * stations[b].Length) : sEnd;
                to = Math.Clamp(to, from, sEnd);
                double f0 = (from - s0[i]) / Math.Max(len[i], 1e-9), f1 = (to - s0[i]) / Math.Max(len[i], 1e-9);
                atoms.Add(new Atom
                {
                    Len = to - from, W = p.Width, Cat = cat[a], Piece = i, FirstStation = a, EndStation = b,
                    Ax = p.X0 + f0 * (p.X1 - p.X0), Ay = p.Y0 + f0 * (p.Y1 - p.Y0),
                    Bx = p.X0 + f1 * (p.X1 - p.X0), By = p.Y0 + f1 * (p.Y1 - p.Y0),
                    S0 = from, S1 = to,
                });
                from = to;
                a = b;
            }
        }

        // ── taps: what the trace runs through rather than ends on ───────────────────────────────────
        var taps = new List<string>();
        var aliases = new List<(string Tap, string SameAs)>();
        double total = at;
        double onePoint = Math.Max(1, 0.5 * dbuPerMicron);   // two taps half a micron apart are at one point
        foreach (var (name, x, y) in onIsland ?? [])
        {
            for (int i = 0; i < n; i++)
            {
                var p = pieces[i];
                double t = (x - p.X0) * dir[i].X + (y - p.Y0) * dir[i].Y;
                double off = Math.Abs(-(x - p.X0) * dir[i].Y + (y - p.Y0) * dir[i].X);
                double s = s0[i] + t;
                // A terminal is a pad's CENTRE, and a large pad standing on a narrow line puts it well off the centre
                // line: FB2's 2512 pad on a 1.2 mm supply rail, 1.5 mm off it, was not tapped and joined the line's
                // start node instead, beside FB1 (designer report, round 15). So the reach is the one a line's END
                // attaches within (LineRecognition.AttachReach*), and not one width.
                double reach = Math.Max(p.Width, LineRecognition.AttachReachWidths * p.Width + LineRecognition.AttachReachMicrons * dbuPerMicron);
                if (t < 0 || t > len[i] || off > reach || s < p.Width || s > total - p.Width) continue;
                // The atom that ends where the tap is. A tap within half a micron of an atom's end is AT that end, whichever
                // side rounding put it: two terminals at one point either side of a diagonal line project a few ulps apart,
                // and the second, a hair past the first's split, went to the end of the atom after it, a whole atom late —
                // on a trace's last atom the walk never closes a line there, so the terminal floated while the line ran on to
                // its end. Which side of the split the hair falls on is the direction the trace is walked. A second tap at the
                // point another has taken is that tap's node, not an overwrite of it, which left the first floating. (The
                // guard above keeps every tap a width short of the trace's end, so the atom found is never its last.)
                int a = atoms.FindIndex(o => o.S1 >= s - onePoint);
                if (a < 0) break;
                if (s < atoms[a].S1 - onePoint) atoms.Insert(a + 1, atoms[a].SplitAt(s, stations));
                if (atoms[a].TapAfter is { } taken) aliases.Add((name, taken));
                else atoms[a].TapAfter = name;
                taps.Add(name);
                break;
            }
        }

        var corners = run.Corners.ToDictionary(c => c.After);
        bool Straight(int a) => a + 1 < atoms.Count && !(atoms[a].Piece != atoms[a + 1].Piece && corners.ContainsKey(atoms[a].Piece));

        // ── 2. slivers: a short piece between straight joins takes its longer neighbour's width ─────
        double sliverFloor = SliverMicrons * dbuPerMicron;
        for (int a = 0; a < atoms.Count; a++)
        {
            var atom = atoms[a];
            if (atom.Len >= Math.Max(SliverWidths * atom.W, sliverFloor)) continue;
            Atom? before = a > 0 && Straight(a - 1) ? atoms[a - 1] : null;
            Atom? after = Straight(a) ? atoms[a + 1] : null;
            var into = (before, after) switch
            {
                (null, null) => null,
                (null, { } x) => x,
                ({ } x, null) => x,
                ({ } x, { } y) => x.Len >= y.Len ? x : y,
            };
            if (into is null) continue;
            bool changes = !SameClass(atom.W, into.W, dbuPerMicron) || atom.Cat != into.Cat;
            atom.W = into.W;
            atom.Cat = into.Cat;
            if (changes) counts.Slivers.Add(((long)Math.Round(0.5 * (atom.Ax + atom.Bx)), (long)Math.Round(0.5 * (atom.Ay + atom.By))));
        }

        // ── 3. the walk ─────────────────────────────────────────────────────────────────────────────
        var drafts = new List<LineDraft>();
        int between = 0;
        string Node() => $"{run.Id}_{++between}";
        string startNode = $"{run.Id}<", endNode = $"{run.Id}>";
        LineDraft NewLine(string start) => new() { Nodes = [start], Source = run.Id, Layer = run.Layer, Type = LineElementType.TLIN };
        void Close(LineDraft line, string node)
        {
            line.Nodes.Add(node);
            line.Type = line.Kind switch
            {
                LineKind.Mlin => LineElementType.MLIN,
                LineKind.Cpwg => LineElementType.CPWG,
                LineKind.Slin => LineElementType.SLIN,
                _ => LineElementType.TLIN,
            };
            drafts.Add(line);
        }
        void Take(LineDraft line, Atom atom)
        {
            if (line.Fresh) line.Kind = KindOf(atom.Cat);
            line.L += atom.Len;
            line.AddWidth(atom.W, atom.Len);
            for (int s = atom.FirstStation; s < atom.EndStation; s++) line.AddStation(stations[s], readings[s]);
            AddPoint(line, atom.Ax, atom.Ay);
            AddPoint(line, atom.Bx, atom.By);
        }

        var cur = NewLine(startNode);
        for (int a = 0; a < atoms.Count; a++)
        {
            var atom = atoms[a];
            if (!cur.Fresh && (KindOf(atom.Cat) != cur.Kind || !SameClass(cur.W, atom.W, dbuPerMicron)))
            {
                // Abutting: a change of type or width inside a piece, or after a join that did not close it.
                string node = Node();
                Close(cur, node);
                cur = NewLine(node);
            }
            Take(cur, atom);
            if (atom.TapAfter is { } tap && a + 1 < atoms.Count)
            {
                Close(cur, tap);
                cur = NewLine(tap);
            }
            if (a + 1 >= atoms.Count) break;

            var next = atoms[a + 1];
            if (next.Piece == atom.Piece) continue;   // a split inside one piece: nothing between

            var p = pieces[atom.Piece];
            var q = pieces[next.Piece];
            var (d1x, d1y) = dir[atom.Piece];
            var (d2x, d2y) = dir[next.Piece];
            bool mlin = KindOf(atom.Cat) == LineKind.Mlin && KindOf(next.Cat) == LineKind.Mlin;
            bool same = atom.Cat == next.Cat && SameClass(atom.W, next.W, dbuPerMicron);

            if (corners.TryGetValue(atom.Piece, out var c))
            {
                if (mlin)
                {
                    // An MBEND owns its corner square: each adjoining line stops W/2 short of the corner point.
                    double half = 0.5 * c.Width;
                    cur.L += (c.X - p.X1) * d1x + (c.Y - p.Y1) * d1y - half;
                    string n1 = Node(), n2 = Node();
                    Close(cur, n1);
                    var bend = new LineDraft
                    {
                        Type = LineElementType.MBEND, Kind = LineKind.Mlin, Nodes = [n1, n2], Source = run.Id, Layer = run.Layer,
                        W = c.Width, Angle = Math.Abs(c.TurnDeg), Miter = MiterOf(c, binder, cur.ReferenceBelow, dbuPerMicron),
                        Anchor = [(c.X, c.Y)],
                    };
                    drafts.Add(bend);
                    cur = NewLine(n2);
                    cur.Kind = LineKind.Mlin;
                    cur.L += (q.X0 - c.X) * d2x + (q.Y0 - c.Y) * d2y - half;
                }
                else
                {
                    // Anywhere but microstrip a bend is its centre-line length (D14), said once per class.
                    counts.UnmodelledBends.Add((c.X, c.Y));
                    cur.L += Math.Sqrt(Sq(c.X - p.X1) + Sq(c.Y - p.Y1));
                    AddPoint(cur, c.X, c.Y);
                    if (!same)
                    {
                        string node = Node();
                        Close(cur, node);
                        cur = NewLine(node);
                        AddPoint(cur, c.X, c.Y);
                    }
                    cur.L += Math.Sqrt(Sq(q.X0 - c.X) + Sq(q.Y0 - c.Y));
                }
                continue;
            }

            // A straight join: a jog, a step, or a taper the piece finder saw as a gap.
            double gap = Math.Sqrt(Sq(q.X0 - p.X1) + Sq(q.Y0 - p.Y1));
            if (same)
            {
                cur.L += gap;
                continue;
            }
            bool widthChanges = !SameClass(atom.W, next.W, dbuPerMicron);
            double tol = Math.Max(1, dbuPerMicron);
            if (mlin && widthChanges && gap >= TaperMinWidths * Math.Min(atom.W, next.W) - tol)
            {
                string n1 = Node(), n2 = Node();
                Close(cur, n1);
                drafts.Add(new LineDraft
                {
                    Type = LineElementType.MTAPER, Kind = LineKind.Mlin, Nodes = [n1, n2], Source = run.Id, Layer = run.Layer,
                    W = atom.W, W2 = next.W, L = gap,
                    Anchor = [(p.X1, p.Y1), (q.X0, q.Y0)],
                });
                cur = NewLine(n2);
                cur.Kind = KindOf(next.Cat);
                continue;
            }
            // A step abuts at the middle of the join (no step model in v1, D18).
            if (widthChanges && !mlin) counts.UnmodelledSteps.Add(((long)Math.Round(0.5 * (p.X1 + q.X0)), (long)Math.Round(0.5 * (p.Y1 + q.Y0))));
            cur.L += 0.5 * gap;
            string at2 = Node();
            Close(cur, at2);
            cur = NewLine(at2);
            cur.Kind = KindOf(next.Cat);
            cur.L += 0.5 * gap;
        }
        if (cur.Fresh && drafts.Count > 0 && drafts[^1].IsLine)
        {
            // Nothing after the last join: the length it carried joins the line before.
            var last = drafts[^1];
            last.L += cur.L;
            last.Nodes[^1] = endNode;
        }
        else Close(cur, endNode);

        var f = pieces[0];
        var l = pieces[^1];
        var start = new ChainEnd(f.X0, f.Y0, -dir[0].X, -dir[0].Y, run.StartsAt, run.StartJunction, f.Width);
        var end = new ChainEnd(l.X1, l.Y1, dir[n - 1].X, dir[n - 1].Y, run.EndsAt, run.EndJunction, l.Width);
        return new TraceLines(run, island, drafts, startNode, endNode, start, end) { Taps = taps, TapAliases = aliases };
    }

    /// <summary>The model's miter option nearest the measured chamfer (R-as5-4): none, half a width, or the
    /// optimum <see cref="MicrostripDiscontinuities.MiterCutLength"/> gives on this substrate.</summary>
    internal static MicrostripBendMiter MiterOf(TraceCorner corner, LineBinder binder, string? reference, int dbuPerMicron)
    {
        if (corner.CutLeg <= 0) return MicrostripBendMiter.None;
        double w = corner.Width / dbuPerMicron * 1e-6;
        double optimal = binder.HeightMeters(reference) is { } h
            ? MicrostripDiscontinuities.MiterCutLength(w, h)
            : MicrostripDiscontinuities.MiterCutLengthAsymptotic(w);
        double cut = corner.CutLeg / dbuPerMicron * 1e-6;
        return new[] { (MicrostripBendMiter.None, 0.0), (MicrostripBendMiter.Fifty, 0.5 * w), (MicrostripBendMiter.Optimal, optimal) }
            .MinBy(o => Math.Abs(o.Item2 - cut)).Item1;
    }

    /// <summary>Runs of one category shorter than <paramref name="minRun"/> take a neighbour's: both
    /// neighbours' where they agree, else the longer one's.</summary>
    private static void Smooth(int[] cat, IReadOnlyList<TraceStation> stations, double minRun)
    {
        while (true)
        {
            var runs = new List<(int From, int To, double Len)>();
            for (int i = 0; i < cat.Length;)
            {
                int j = i;
                double sum = 0;
                while (j < cat.Length && cat[j] == cat[i]) sum += stations[j++].Length;
                runs.Add((i, j, sum));
                i = j;
            }
            if (runs.Count <= 1) return;
            int shortest = -1;
            for (int r = 0; r < runs.Count; r++)
                if (runs[r].Len < minRun && (shortest < 0 || runs[r].Len < runs[shortest].Len)) shortest = r;
            if (shortest < 0) return;

            int into = shortest == 0 ? 1
                     : shortest == runs.Count - 1 ? shortest - 1
                     : cat[runs[shortest - 1].From] == cat[runs[shortest + 1].From] || runs[shortest - 1].Len >= runs[shortest + 1].Len
                        ? shortest - 1 : shortest + 1;
            int value = cat[runs[into].From];
            for (int i = runs[shortest].From; i < runs[shortest].To; i++) cat[i] = value;
        }
    }

    private static LineKind KindOf(int cat) => cat == Unsolved ? LineKind.Tlin : (LineKind)cat;

    /// <summary>Two widths in one class — the trace review's own merge tolerance.</summary>
    internal static bool SameClass(double a, double b, int dbuPerMicron) =>
        Math.Abs(a - b) <= TraceImpedanceAnalysis.MergeToleranceMicrons(Math.Min(a, b) / dbuPerMicron) * dbuPerMicron;

    private static void AddPoint(LineDraft line, double x, double y)
    {
        var p = ((long)Math.Round(x), (long)Math.Round(y));
        if (line.Anchor.Count == 0 || line.Anchor[^1] != p) line.Anchor.Add(p);
    }

    private static double Sq(double v) => v * v;
}
