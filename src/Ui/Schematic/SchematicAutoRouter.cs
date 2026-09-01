using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Draws the wires for a schematic nobody laid out by hand — an imported netlist.
///
/// <para><b>The whole difficulty is that geometry IS connectivity here.</b> circuitRF reads a
/// schematic's nets off the drawing (<c>SchematicEditModel.ComputeConnectivityGeometry</c>), so a
/// router that merely avoids drawing something ugly is not enough: a wire laid across another net's
/// wire in the wrong way does not look wrong, it JOINS the two nets, and the imported cell then
/// simulates as a different circuit. The three rules below are that connectivity contract restated
/// as routing constraints, and they are why this is an obstacle-aware router rather than a few
/// straight lines.</para>
///
/// <list type="number">
/// <item><b>A pure crossing is safe.</b> Two wires passing through a point with neither having a
/// vertex there is NOT a connection — it joins only through a user-placed dot. So a route may cross
/// another net at right angles, and that is what makes routing possible at all.</item>
/// <item><b>A vertex on another net's wire is a connection.</b> Three or more incident segments with
/// a vertex among them auto-dots. So a route may never BEND or END on a cell any other net's wire
/// touches.</item>
/// <item><b>Collinear overlap is not a connection but is a lie.</b> Two nets' wires running along
/// the same line read as one wire. Forbidden for the same reason, one step weaker.</item>
/// </list>
///
/// <para><b>Everything sits on the connection grid P = 100.</b> Cells are integer (i,j) with world
/// (i·P, j·P) — not an approximation of the grid but the grid itself, because a point off it does
/// not connect at all.</para>
///
/// <para><b>A route that cannot be found is reported, never approximated.</b> The caller's fallback
/// is a net label, which is a real connection; silently leaving a terminal open would produce a cell
/// that elaborates into a different circuit with nothing said.</para>
/// </summary>
internal static class SchematicAutoRouter
{
    /// <summary>The connection grid. Everything electrical is a multiple of this.</summary>
    public const double P = 100.0;

    /// <summary>Cost of a step; a bend costs this much more, so routes prefer few corners.</summary>
    private const double TurnCost = 6.0;

    /// <summary>How far outside the placed content the router may wander, in cells.</summary>
    private const int Margin = 8;

    // ─────────────────────────────────────────────────────────────────────────
    //  Input
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One placed thing the router must wire to and route around.
    /// </summary>
    /// <param name="Terminals">
    /// World positions of this block's connection points, each with the net it belongs to. A null or
    /// empty net means "leave it alone" — a terminal the netlist does not connect.
    /// </param>
    /// <param name="KeepOut">
    /// World cells no wire may enter. <b>The terminals are removed from it by the router</b>, along
    /// with the one cell immediately outside each terminal, so every terminal stays reachable from
    /// exactly the direction its own lead points. That is what keeps a wire from creeping along a
    /// device body to reach a pin from the side.
    /// </param>
    public sealed record Block(
        IReadOnlyList<(double X, double Y, string? Net)> Terminals,
        IReadOnlyList<(double X, double Y)>              KeepOut);

    /// <summary>What the router drew, and what it could not.</summary>
    /// <param name="Wires">Each wire as its ordered vertex list, in world coordinates.</param>
    /// <param name="Unrouted">
    /// Terminals left unconnected, as (world position, net). <b>Never dropped silently</b> — the
    /// caller connects these another way and says so.
    /// </param>
    public sealed record Result(
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> Wires,
        IReadOnlyList<(double X, double Y, string Net)>    Unrouted);

    // ─────────────────────────────────────────────────────────────────────────
    //  Routing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes every net that has two or more terminals.
    ///
    /// <para>Nets are taken in ascending terminal count, so the two-terminal nets — which are most of
    /// them, and which have the fewest ways to be drawn — claim their short direct paths before a
    /// many-terminal net spreads a tree across the sheet. Within a net, each further terminal is
    /// routed to the NEAREST point of what the net has already been given, which is what makes the
    /// result a trunk with branches rather than a star.</para>
    /// </summary>
    public static Result Route(IReadOnlyList<Block> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var grid = new Grid(blocks);
        var wires    = new List<IReadOnlyList<(double, double)>>();
        var unrouted = new List<(double, double, string)>();

        var nets = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        foreach (var b in blocks)
            foreach (var (x, y, net) in b.Terminals)
            {
                if (string.IsNullOrEmpty(net)) continue;
                if (!nets.TryGetValue(net, out var list)) nets[net] = list = [];
                list.Add(Cell.Of(x, y));
            }

        foreach (var (net, terminals) in nets.OrderBy(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (terminals.Count < 2) continue;

            // The tree so far: every cell this net already owns. Seeded with the first terminal,
            // grown by each route that lands.
            var tree = new HashSet<Cell> { terminals[0] };
            bool anyFailed = false;

            foreach (var target in terminals.Skip(1))
            {
                if (tree.Contains(target)) continue;

                var path = FindPath(grid, net, tree, target);
                if (path is null)
                {
                    unrouted.Add((target.X, target.Y, net));
                    anyFailed = true;
                    continue;
                }

                grid.Commit(net, path);
                wires.Add([.. path.Select(c => (c.X, c.Y))]);

                // EVERY cell the route covers joins the tree, not just its corners — a later branch
                // of the same net should be able to T onto the middle of a trunk, which is how a
                // person draws it and what turns a star into a trunk with branches. `path` is the
                // simplified VERTEX list by this point, so the run between two vertices has to be
                // filled back in here.
                foreach (var c in Cells(path)) tree.Add(c);
            }

            // **The seed has to join the fallback too.** A terminal the router gave up on is
            // connected by name, and the name has to reach the wired part of the net as well — the
            // seed is the one terminal that is never a routing TARGET, so without this the labelled
            // terminals form one net and everything that did get wired forms a second, which is a
            // different circuit that elaborates.
            if (anyFailed) unrouted.Add((terminals[0].X, terminals[0].Y, net));
        }

        return new Result(wires, unrouted);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The grid
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Every grid cell a vertex list passes through, corners included.</summary>
    private static IEnumerable<Cell> Cells(IReadOnlyList<Cell> path)
    {
        yield return path[0];
        for (int k = 1; k < path.Count; k++)
        {
            var a = path[k - 1];
            var b = path[k];
            int di = Math.Sign(b.I - a.I), dj = Math.Sign(b.J - a.J);
            var c = a;
            while (c != b) { c = new Cell(c.I + di, c.J + dj); yield return c; }
        }
    }

    /// <summary>A point on the connection grid, in grid units. World = (I·P, J·P).</summary>
    private readonly record struct Cell(int I, int J)
    {
        public static Cell Of(double x, double y)
            => new((int)Math.Round(x / P), (int)Math.Round(y / P));

        public double X => I * P;
        public double Y => J * P;
    }

    /// <summary>
    /// What every grid cell already carries. Four separate maps rather than one flags word because
    /// each answers a different question the connectivity rules ask, and collapsing them would make
    /// "another net has a vertex here" and "another net runs through here horizontally"
    /// indistinguishable — which is exactly the distinction that decides whether a crossing is a
    /// connection.
    /// </summary>
    private sealed class Grid
    {
        /// <summary>Cells no wire may enter at all: component bodies.</summary>
        private readonly HashSet<Cell> _blocked = [];

        /// <summary>A terminal's cell, and the net that owns it. No other net may enter.</summary>
        private readonly Dictionary<Cell, string> _owner = [];

        private readonly Dictionary<Cell, HashSet<string>> _usedH  = [];
        private readonly Dictionary<Cell, HashSet<string>> _usedV  = [];
        private readonly Dictionary<Cell, HashSet<string>> _vertex = [];

        public int MinI { get; }
        public int MaxI { get; }
        public int MinJ { get; }
        public int MaxJ { get; }

        public Grid(IReadOnlyList<Block> blocks)
        {
            int minI = int.MaxValue, maxI = int.MinValue, minJ = int.MaxValue, maxJ = int.MinValue;

            void See(Cell c)
            {
                if (c.I < minI) minI = c.I;
                if (c.I > maxI) maxI = c.I;
                if (c.J < minJ) minJ = c.J;
                if (c.J > maxJ) maxJ = c.J;
            }

            foreach (var b in blocks)
            {
                foreach (var (x, y) in b.KeepOut) { var c = Cell.Of(x, y); _blocked.Add(c); See(c); }
                foreach (var (x, y, net) in b.Terminals)
                {
                    var c = Cell.Of(x, y);
                    See(c);
                    // A terminal is never an obstacle to its own net, and is always one to every
                    // other. Both halves matter: without the first the pin is unreachable, without
                    // the second a foreign wire ends on it and joins two nets.
                    _blocked.Remove(c);
                    if (!string.IsNullOrEmpty(net)) _owner[c] = net;
                    else _blocked.Add(c);   // an unconnected terminal is just an obstacle
                }
            }

            if (minI > maxI) { minI = maxI = minJ = maxJ = 0; }

            MinI = minI - Margin; MaxI = maxI + Margin;
            MinJ = minJ - Margin; MaxJ = maxJ + Margin;
        }

        public bool InBounds(Cell c) => c.I >= MinI && c.I <= MaxI && c.J >= MinJ && c.J <= MaxJ;

        private static bool OnlyOther(Dictionary<Cell, HashSet<string>> map, Cell c, string net)
            => map.TryGetValue(c, out var s) && (s.Count > 1 || !s.Contains(net));

        /// <summary>May a wire of <paramref name="net"/> occupy <paramref name="c"/> at all?</summary>
        public bool CanEnter(Cell c, string net)
        {
            if (!InBounds(c)) return false;
            if (_owner.TryGetValue(c, out var o)) return string.Equals(o, net, StringComparison.Ordinal);
            if (_blocked.Contains(c)) return false;
            // Rule 2, the entry half: another net's VERTEX here would gain a third incident segment
            // from us, whichever way we pass through. That is an auto-dot and a false connection.
            return !OnlyOther(_vertex, c, net);
        }

        /// <summary>May a wire of <paramref name="net"/> pass through <paramref name="c"/> travelling
        /// horizontally (<paramref name="horizontal"/>) without turning?</summary>
        public bool CanPass(Cell c, string net, bool horizontal)
            => CanEnter(c, net)
            // Rule 3: collinear overlap with another net reads as one wire.
            && !OnlyOther(horizontal ? _usedH : _usedV, c, net);

        /// <summary>May a wire of <paramref name="net"/> put a VERTEX — a bend or an end — at
        /// <paramref name="c"/>?</summary>
        public bool CanStop(Cell c, string net)
            => CanEnter(c, net)
            // Rule 2, the vertex half: our corner landing anywhere on another net's wire, in either
            // orientation, is the auto-dot again — this is the case a crossing test alone misses.
            && !OnlyOther(_usedH, c, net)
            && !OnlyOther(_usedV, c, net);

        private static void Mark(Dictionary<Cell, HashSet<string>> map, Cell c, string net)
        {
            if (!map.TryGetValue(c, out var s)) map[c] = s = new HashSet<string>(StringComparer.Ordinal);
            s.Add(net);
        }

        /// <summary>Records a routed path so later routes must respect it.</summary>
        public void Commit(string net, IReadOnlyList<Cell> path)
        {
            Mark(_vertex, path[0], net);
            Mark(_vertex, path[^1], net);

            for (int k = 1; k < path.Count; k++)
            {
                var a = path[k - 1];
                var b = path[k];
                bool horizontal = a.J == b.J;
                var map = horizontal ? _usedH : _usedV;

                int lo = horizontal ? Math.Min(a.I, b.I) : Math.Min(a.J, b.J);
                int hi = horizontal ? Math.Max(a.I, b.I) : Math.Max(a.J, b.J);
                for (int t = lo; t <= hi; t++)
                    Mark(map, horizontal ? new Cell(t, a.J) : new Cell(a.I, t), net);

                if (k < path.Count - 1) Mark(_vertex, b, net);   // a bend
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  A*
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Which way a step went. <see cref="None"/> is the start, which has no direction yet.</summary>
    private enum Dir { None = 0, Left, Right, Up, Down }

    private static bool IsHorizontal(Dir d) => d is Dir.Left or Dir.Right;

    private static readonly (Dir D, int Di, int Dj)[] Steps =
    [
        (Dir.Right, 1, 0), (Dir.Left, -1, 0), (Dir.Down, 0, 1), (Dir.Up, 0, -1),
    ];

    /// <summary>
    /// Shortest turn-averse path from ANY cell of <paramref name="sources"/> to
    /// <paramref name="target"/>, or null when the obstacles leave none.
    ///
    /// <para><b>The search state is (cell, arrival direction), not (cell).</b> Whether a cell may be
    /// used at all depends on how the wire passes through it — straight through is legal where a
    /// corner is not — so a plain per-cell search would either forbid legal crossings or permit
    /// illegal corners. This is the whole reason the router is A* over a doubled state space rather
    /// than a breadth-first flood.</para>
    /// </summary>
    private static List<Cell>? FindPath(Grid grid, string net, HashSet<Cell> sources, Cell target)
    {
        if (!grid.CanStop(target, net)) return null;

        var best   = new Dictionary<(Cell, Dir), double>();
        var from   = new Dictionary<(Cell, Dir), (Cell, Dir)>();
        var closed = new HashSet<(Cell, Dir)>();
        var open   = new PriorityQueue<(Cell C, Dir D), double>();

        double H(Cell c) => Math.Abs(c.I - target.I) + Math.Abs(c.J - target.J);

        // A route STARTS at a vertex — the terminal it leaves, or the T it makes on the net's own
        // existing wire — so a source has to satisfy the corner rule, not merely the entry one.
        foreach (var s in sources)
        {
            if (!grid.CanStop(s, net)) continue;
            var key = (s, Dir.None);
            best[key] = 0;
            open.Enqueue(key, H(s));
        }

        while (open.TryDequeue(out var cur, out _))
        {
            var (c, d) = cur;
            if (!closed.Add(cur)) continue;
            if (!best.TryGetValue(cur, out double g)) continue;

            if (c == target)
            {
                var path = new List<Cell>();
                var node = cur;
                while (true)
                {
                    path.Add(node.Item1);
                    if (!from.TryGetValue(node, out var prev)) break;
                    node = prev;
                }
                path.Reverse();
                return Simplify(path);
            }

            foreach (var (nd, di, dj) in Steps)
            {
                var n = new Cell(c.I + di, c.J + dj);

                bool turning = d != Dir.None && d != nd;

                // Leaving `c` in a new direction puts a corner AT c. A corner is the case rule 2
                // forbids on another net's wire, so it is checked here rather than on arrival.
                if (turning && !grid.CanStop(c, net)) continue;

                // Arriving at n: it must at least be enterable travelling this way. Whether n also
                // needs to survive CanStop depends on what happens next, which the successor state
                // decides — except at the target, where the wire ends and a vertex is certain.
                if (!grid.CanPass(n, net, IsHorizontal(nd))) continue;
                if (n == target && !grid.CanStop(n, net)) continue;

                double ng = g + 1 + (turning ? TurnCost : 0);
                var key = (n, nd);
                if (best.TryGetValue(key, out double old) && old <= ng) continue;

                best[key] = ng;
                from[key] = cur;
                open.Enqueue(key, ng + H(n));
            }
        }

        return null;
    }

    /// <summary>
    /// Drops the cells in the middle of a straight run, leaving only the corners — the vertex list a
    /// wire is actually made of. Keeping every cell would put a VERTEX on every grid point of the
    /// run, and a vertex is a connection, so this is correctness rather than tidiness.
    /// </summary>
    private static List<Cell> Simplify(List<Cell> path)
    {
        if (path.Count < 3) return path;

        var outp = new List<Cell> { path[0] };
        for (int k = 1; k < path.Count - 1; k++)
        {
            bool straight = (path[k - 1].I == path[k].I && path[k].I == path[k + 1].I)
                         || (path[k - 1].J == path[k].J && path[k].J == path[k + 1].J);
            if (!straight) outp.Add(path[k]);
        }
        outp.Add(path[^1]);
        return outp;
    }
}
