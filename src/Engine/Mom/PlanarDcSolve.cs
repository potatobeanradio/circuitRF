// LF1 — THE DC POINT, AND WHY IT IS NOT A FREQUENCY OF THE FULL-WAVE SWEEP.
//
// A user biasing a FET off an EM result needs the 0 Hz row of the `.sNp`, and needs it to be RIGHT
// about the one thing DC decides: whether the port is connected to anything. A series MIM cap is an
// OPEN at DC and a trace is very nearly a SHORT, and a file that guesses either way sends a bias
// current somewhere it does not go.
//
// **The full-wave kernel cannot produce it, and not because of a tolerance.** The MPIE splits into a
// vector-potential term that scales with ω and a scalar-potential term that scales with 1/ω, so at
// ω = 0 the system is not ill-conditioned, it is undefined — and every ingredient underneath is
// undefined with it: the Green's function is written in k₀, the DCIM path is a multiple of k₀, and
// the radial table is sized against a wavelength that does not exist. Dcim.CanFitAtFrequency refuses
// the neighbourhood for exactly these reasons.
//
// **What DC actually is, is a conduction problem.** At ω = 0 the charge term enforces ∇·J = 0 and the
// vector potential contributes nothing, so what is left is E = ZsJ on the metal with a divergence-free
// current — which is Ohm's law on a resistor network, and has an exact answer with no fit in it. The
// network is not a second discretisation of the structure: **it IS the rooftop basis**, one resistor
// per basis function, which is the same statement PlanarConductors already makes ("the basis list
// already IS the conduction graph of the meshed structure"). A cell pair that carries a rooftop
// conducts; one that does not, does not; a via basis crosses levels; a ground-attachment basis
// reaches the plane. Nothing here re-derives connectivity from the drawn polygons, because that
// second answer would disagree with the solve's wherever the mesher staircased, cut or dropped a
// sliver.
//
// **The ports are terminal PAIRS here, not delta gaps.** That is the one place this departs from the
// AC port model and it has to: a delta gap at a conductor's end face drives the structure against the
// plane through the FIELD, and at DC there is no field to drive it through — modelled as a source in
// series with its own rooftop it would see an open circuit at both ends of every line and publish
// S = I for a solid piece of copper. What an edge port IS at DC is the end of that conductor against
// its reference, which is the plane; so the terminals are the conductor's end cells and the ground
// node, and the answer comes out as the trace's own resistance. The kinds that genuinely ARE a cut in
// metal — the internal delta gap, and the via-to-plane port whose gap is at the foot of the via — cut
// the network, because that is what they are.
//
// **And a pair is a PAIR: neither terminal of a cut port is the ground node.** An edge port's − IS
// the plane, so the two statements coincide there and it is easy to write a solve that only works
// for them — which is what this file did until 2026-09-14, by holding every terminal at an absolute
// potential. That is the short-circuit condition ONLY under a common reference; on a delta gap it
// welds the far lip to ground and the port stops conserving current across itself. So each port is
// driven by a source across its own terminal pair (`Admittance`), which says nothing about where
// either terminal sits, and a port is treated as a CONNECTION when asking what can reach what —
// a cut's two lips are in different conduction components by construction, so branch connectivity
// alone declares everything past a gap unreachable.

using System.Numerics;
using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;
using CSparse.Ordering;
using CSparse.Storage;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Mom;

/// <summary>What the DC point cost and produced. <b>No calibration and no de-embedding</b> — see
/// <see cref="PlanarDcSolve"/>'s header for why there is nothing at DC for a two-line standard to
/// remove, and <see cref="PlanarDcSolve.Solve"/> for the one thing that IS peeled.</summary>
/// <param name="S">The scattering matrix at 0 Hz, referenced to each port's own declared Z₀. Real
/// by construction (it is an <see cref="Y"/> of real conductances) and carried as
/// <see cref="Complex"/> so it is the same type every other point of the sweep is.</param>
/// <param name="Y">The port admittance matrix, siemens. A row and column of exact zeros is a port
/// with no DC path to anything — the MIM-cap answer, and the one this whole file exists to get
/// right.</param>
/// <param name="Notes">What the run should say about it, already worded.</param>
public sealed record PlanarDcResult(Mat<Complex> S, Mat<Complex> Y, IReadOnlyList<string> Notes,
                                    double ElapsedMs);

/// <summary>
/// <b>0 Hz, solved as a conduction network on the rooftop basis.</b> See the file header for the
/// physics and for why the port model differs from the AC one.
/// </summary>
public static class PlanarDcSolve
{
    /// <summary>
    /// The sheet resistance a layer with no usable conductivity or thickness is given, Ω/sq. A layer
    /// declared with σ ≤ 0 or t ≤ 0 is a PEC as far as everything else in this kernel is concerned
    /// (L8c's loss model reads the same two numbers and produces no loss from them), so the DC point
    /// says the same thing: connected metal is a short. <b>Small rather than zero</b>, because a
    /// zero-resistance branch makes the nodal matrix singular and there is no answer on the far side
    /// of that.
    ///
    /// <para><b>1 µΩ/sq rather than something smaller, and the limit is the S CONVERSION rather than
    /// the network.</b> The nodal solve is happy at any value; <c>Y → S</c> inverts <c>I + Z₀·Y</c>,
    /// and at 1 nΩ/sq that matrix has entries of ~7e9 on a 50 Ω port, which costs ten digits and
    /// publishes |S₂₁| = 0.99997 — a worse answer than the larger resistance gives. At 1 µΩ/sq the
    /// same trace is 7 µΩ end to end (a thousand times better than the copper it stands in for) and
    /// |S₂₁| comes back 1 to eight digits.</para>
    /// </summary>
    public const double PecSheetResistance = 1e-6;

    /// <summary>
    /// <b>Which metal a VERTICAL basis is made of</b>, S/m — the via artwork covering its footprint,
    /// falling back to any via declared between the same two levels and then to the upper level's own
    /// conductivity, which is what <c>PlanarGroundPath</c>'s synthesised cells are.
    ///
    /// <para><b>Public because CL1's surface-impedance term asks the same question of the same
    /// basis.</b> A second copy of this resolution in the AC fill would disagree with the DC point on
    /// exactly the cases this walk exists for — a ground attachment, whose lower index is
    /// <see cref="PlanarVia.GroundTerminal"/>, and a cell of via the user never drew — and the
    /// disagreement would be a smooth, plausible loss figure rather than an error. See
    /// <see cref="PlanarConductorLoss.BarrelAt"/>.</para>
    /// </summary>
    public static double ViaSigmaFor(PlanarProblem problem, PlanarBasis b, PlanarCell cell)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(cell);

        int lower = b.AttachesToGround ? PlanarVia.GroundTerminal : b.LayerIndex;
        int upper = b.AttachesToGround ? b.LayerIndex : b.LayerIndex + 1;
        double fallback = 0;
        foreach (var v in problem.ViaList)
        {
            if (v.LowerLayerIndex != lower || v.UpperLayerIndex != upper) continue;
            if (fallback <= 0) fallback = v.SigmaSm;
            foreach (var poly in v.Polygons)
                if (poly.Contains(cell.CentroidX, cell.CentroidY)) return v.SigmaSm;
        }
        // No via artwork here — PlanarGroundPath builds one cell of via where the user drew
        // none, and it is that level's own metal.
        if (fallback > 0) return fallback;
        int layer = Math.Clamp(upper, 0, problem.Layers.Count - 1);
        return problem.Layers[layer].SigmaSm;
    }

    /// <summary>
    /// <b>The DC point.</b> Exact for the meshed structure, up to the two things it is explicit
    /// about: the reference plane sits at the port's outermost cell rather than on the gridline one
    /// half-cell in, and a grown feed lead (R-fed-1) is peeled as the series resistance it is at DC.
    /// </summary>
    /// <param name="problem">The artwork the sweep is solving — the SAME object, after
    /// <c>PlanarGroundPath</c> and <c>PlanarFeedExtension</c>, so the network is the network that was
    /// meshed.</param>
    /// <param name="mesh">The DUT mesh. Its <see cref="PlanarMesh.Bases"/> are the branches.</param>
    /// <param name="ports">Resolved ports, in published order.</param>
    /// <param name="leads">R-fed-1's grown feeds, or null. Each one's own DC resistance is peeled off
    /// its port, so the DC row is referenced to the same plane every other row of the file is.</param>
    public static PlanarDcResult Solve(PlanarProblem problem, PlanarMesh mesh,
                                       IReadOnlyList<PlanarPortResolution> ports,
                                       IReadOnlyList<PlanarFeedLead>? leads = null)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(ports);
        if (mesh.BlockSource is { } source) mesh = source;   // PMS-6 — the network is the mesh the ports index
        if (ports.Count == 0)
            throw new ArgumentException("A DC solve needs at least one port.", nameof(ports));

        var sw    = System.Diagnostics.Stopwatch.StartNew();
        var notes = new List<string>();
        var net   = Network.Build(problem, mesh, ports, notes);
        var y     = net.Admittance(notes);

        // ── The two series terms, both exact at DC and both in the same place ────────────────────
        //
        // A via-to-plane port's gap is at the FOOT of the via, so the via's own metal is between the
        // port and the structure and belongs in the answer (positive). A grown feed lead is metal the
        // user never drew, added so the calibration has something uniform to measure, and every other
        // row of the file has it peeled (positive would publish a DC row referenced to a different
        // plane from its neighbours) — so it comes off (negative).
        var series = new double[ports.Count];
        for (int p = 0; p < ports.Count; p++) series[p] = net.SeriesOhms[p];
        if (leads is not null)
            foreach (var lead in leads)
            {
                int p = IndexOfPort(ports, lead.PortNumber);
                if (p < 0 || !(lead.LengthM > 0)) continue;
                double r = net.LeadResistanceOhms(problem, ports[p], lead);
                series[p] -= r;
                if (r > 0)
                    notes.Add($"DC: port {ports[p].Number}'s grown feed lead " +
                              $"({SurfaceMesher.Eng(lead.LengthM)}m of it) is {Ohms(r)} at DC and is " +
                              "peeled, so the 0 Hz row references the same plane the rest of the file " +
                              "does.");
            }

        var yPort = ApplySeries(y, series);
        var s     = RFNetwork.YToS(yPort, PlanarExcitation.ReferenceImpedances(ports));

        notes.Insert(0, Summary(ports, yPort));
        return new PlanarDcResult(s, yPort, notes, sw.Elapsed.TotalMilliseconds);
    }

    private static int IndexOfPort(IReadOnlyList<PlanarPortResolution> ports, int number)
    {
        for (int i = 0; i < ports.Count; i++) if (ports[i].Number == number) return i;
        return -1;
    }

    /// <summary>
    /// <c>Z′ = Z + R</c> without ever forming Z — <c>Y′ = (I + Y·R)⁻¹·Y</c>, which is defined for a
    /// SINGULAR Y and therefore for the case this file exists for: a port with no DC path to
    /// anything, whose Z does not exist and whose Y is a row of zeros.
    /// </summary>
    private static Mat<Complex> ApplySeries(Mat<Complex> y, double[] series)
    {
        int n = series.Length;
        bool any = false;
        foreach (double r in series) if (r != 0) { any = true; break; }
        if (!any) return y;

        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
        {
            m[i, i] = Complex.One;
            for (int j = 0; j < n; j++) m[i, j] += y[i, j] * series[j];
        }

        var lu = m.Lu();
        var x  = new Mat<Complex>(n, n);
        for (int j = 0; j < n; j++)
        {
            var col = lu.Solve(y.Cols[j].Copy());
            for (int i = 0; i < n; i++) x[i, j] = col[i];
        }
        return x;
    }

    private static string Ohms(double r) =>
        r >= 1 ? $"{r:0.###} Ω" : r >= 1e-3 ? $"{r * 1e3:0.###} mΩ" : $"{r * 1e6:0.###} µΩ";

    /// <summary>
    /// The one line the Messages panel gets. <b>It names what DC decided, not how</b>: which ports
    /// are open, and what the through resistance is where there is one — the two facts a bias
    /// calculation turns on.
    /// </summary>
    private static string Summary(IReadOnlyList<PlanarPortResolution> ports, Mat<Complex> y)
    {
        int n = ports.Count;
        var open = new List<int>();
        for (int i = 0; i < n; i++)
        {
            double row = 0;
            for (int j = 0; j < n; j++) row += y[i, j].Magnitude;
            if (row <= 0) open.Add(ports[i].Number);
        }

        // The through resistance of the first connected pair, which is what "is this trace really
        // continuous" looks like as a number. Off-diagonal Y is −1/R for a two-node resistor.
        string through = "";
        for (int i = 0; i < n && through.Length == 0; i++)
            for (int j = i + 1; j < n; j++)
            {
                double g = -y[i, j].Real;
                if (!(g > 0)) continue;
                through = $" Ports {ports[i].Number}-{ports[j].Number} are " +
                          $"{Ohms(1.0 / g)} apart at DC.";
                break;
            }

        string openText = open.Count == 0
            ? "every port has a DC path"
            : open.Count == n
                ? "every port is a DC open"
                : $"port{(open.Count == 1 ? "" : "s")} {string.Join(", ", open)} " +
                  $"{(open.Count == 1 ? "is" : "are")} a DC open";

        return $"0 Hz is solved as a conduction network rather than fitted — {openText}.{through}";
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE NETWORK
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private sealed class Network
    {
        private const int NoNode = -1;

        private int[]      _parent = [];               // union-find over node ids
        private int        _nodeCount;
        private int        _groundNode = NoNode;
        private readonly List<(int A, int B, double G)> _branches = [];
        private int[]      _plus  = [];                // per port: super-node of its + terminal
        private int[]      _minus = [];
        private int[]      _numbers = [];               // per port: the number the design gave it
        public  double[]   SeriesOhms = [];

        private int Find(int i) { while (_parent[i] != i) i = _parent[i] = _parent[_parent[i]]; return i; }
        private void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) _parent[b] = a;
        }

        private Expansion? _x;

        public static Network Build(PlanarProblem problem, PlanarMesh mesh,
                                    IReadOnlyList<PlanarPortResolution> ports, List<string> notes)
        {
            var net = new Network();
            if (mesh.HasBlocks) net._x = new Expansion(mesh);

            // ── Nodes: one per cell that carries current, plus the plane ────────────────────────
            var nodeOf = new int[mesh.Cells.Count];
            Array.Fill(nodeOf, NoNode);
            foreach (var b in mesh.Bases)
            {
                if ((uint)b.CellA < (uint)nodeOf.Length && nodeOf[b.CellA] == NoNode) nodeOf[b.CellA] = 0;
                if ((uint)b.CellB < (uint)nodeOf.Length && nodeOf[b.CellB] == NoNode) nodeOf[b.CellB] = 0;
            }
            int next = 0;
            // PMS-6 — on a block mesh a block is ONE NODE PER POSITION of the shared grid it spans (Expansion); every
            // other cell is one node, as on a tensor grid.
            for (int c = 0; c < nodeOf.Length; c++)
                if (nodeOf[c] == 0) { nodeOf[c] = next; next += net._x?.NodesOf(c) ?? 1; }
            net._groundNode = next++;              // always present: it is every edge port's reference
            net._nodeCount  = next;
            net._parent = new int[net._nodeCount];
            for (int i = 0; i < net._nodeCount; i++) net._parent[i] = i;

            // ── Which bases are a PORT'S OWN GAP, and therefore not conductors ──────────────────
            var cut = new bool[mesh.Bases.Count];
            foreach (var p in ports)
            {
                if (p.Kind is not (PlanarPortKind.Internal or PlanarPortKind.InternalDeltaGap)) continue;
                foreach (int bi in p.BasisIndices) if ((uint)bi < (uint)cut.Length) cut[bi] = true;
                if (p.Negative is { } neg)
                    foreach (int bi in neg.BasisIndices) if ((uint)bi < (uint)cut.Length) cut[bi] = true;
            }

            // ── Branches: one resistor per surviving basis ──────────────────────────────────────
            var levels  = PlanarLevels.From(problem);
            bool anyPec = false;
            if (net._x is { } x)
                x.AddInternal(problem, mesh, nodeOf, net._branches, ref anyPec);
            for (int bi = 0; bi < mesh.Bases.Count; bi++)
            {
                if (cut[bi]) continue;
                var b = mesh.Bases[bi];
                if (net._x is { } xb && xb.Touches(b))
                {
                    xb.AddBasis(problem, mesh, levels, b, nodeOf, net._groundNode, net._branches, ref anyPec);
                    continue;
                }
                double r = Resistance(problem, mesh, levels, b, ref anyPec);
                if (!(r > 0)) r = PecSheetResistance;

                int a = nodeOf[b.CellA];
                int z = b.AttachesToGround ? net._groundNode : nodeOf[b.CellB];
                if (a == NoNode || z == NoNode || a == z) continue;
                net._branches.Add((a, z, 1.0 / r));
            }
            if (anyPec)
                notes.Add("DC: at least one conductor level declares no conductivity or no thickness, " +
                          "so it is taken as a perfect conductor — its DC resistance reads as zero " +
                          "rather than as a number.");

            // ── Port terminals ─────────────────────────────────────────────────────────────────
            net._plus       = new int[ports.Count];
            net._minus      = new int[ports.Count];
            net._numbers    = new int[ports.Count];
            net.SeriesOhms  = new double[ports.Count];
            for (int p = 0; p < ports.Count; p++)
            {
                var port = ports[p];
                net._numbers[p] = port.Number;
                bool low = port.IncidenceSign > 0;

                switch (port.Kind)
                {
                    case PlanarPortKind.Internal:
                        // The gap is at the FOOT of the via: + is the metal it lands on, − is the
                        // plane, and the via's own metal is in series between them.
                        net._plus[p]  = net.Tie(mesh, nodeOf, port.BasisIndices, wantLow: true);
                        net._minus[p] = net._groundNode;
                        net.SeriesOhms[p] = ViaSeries(problem, mesh, levels, port);
                        break;

                    case PlanarPortKind.InternalDeltaGap when port.Negative is { } negGap:
                        // Two cuts in one loop: the far lip of each is the near lip of the other, so
                        // the pair is one terminal pair. See the file header's note on this kind.
                        net._plus[p]  = net.Tie(mesh, nodeOf, port.BasisIndices, low,
                                                negGap.BasisIndices, !low);
                        net._minus[p] = net.Tie(mesh, nodeOf, port.BasisIndices, !low,
                                                negGap.BasisIndices, low);
                        break;

                    case PlanarPortKind.InternalDeltaGap:
                        net._plus[p]  = net.Tie(mesh, nodeOf, port.BasisIndices, wantLow: low);
                        net._minus[p] = net.Tie(mesh, nodeOf, port.BasisIndices, wantLow: !low);
                        break;

                    default:
                        // An EDGE port: the end of its conductor, against its reference. The
                        // reference is the plane unless the user named a return conductor.
                        net._plus[p] = net.Tie(mesh, nodeOf, port.BasisIndices, wantLow: low);
                        net._minus[p] = port.Negative is { } negEdge
                            ? net.Tie(mesh, nodeOf, negEdge.BasisIndices, wantLow: low)
                            : net._groundNode;
                        break;
                }
            }
            return net;
        }

        /// <summary>Tie one terminal's cells into a single node and return it. A port terminal is an
        /// equipotential: the AC excitation impresses the same gap voltage on every cut of it.</summary>
        private int Tie(PlanarMesh mesh, int[] nodeOf, IReadOnlyList<int> bases, bool wantLow)
        {
            int first = NoNode;
            foreach (int bi in bases)
            {
                if ((uint)bi >= (uint)mesh.Bases.Count) continue;
                var b = mesh.Bases[bi];
                int cell = b.AttachesToGround ? b.CellA : (wantLow ? b.CellA : b.CellB);
                if ((uint)cell >= (uint)nodeOf.Length || nodeOf[cell] == NoNode) continue;
                if (_x is { } x && x.Touches(b))
                {
                    // PMS-6 — a block terminal is every position of it the basis's face (or foot) touches.
                    foreach (int node in x.FaceNodes(mesh, b, cell, nodeOf))
                        if (first == NoNode) first = node; else Union(first, node);
                    continue;
                }
                if (first == NoNode) first = nodeOf[cell]; else Union(first, nodeOf[cell]);
            }
            return first == NoNode ? _groundNode : Find(first);
        }

        private int Tie(PlanarMesh mesh, int[] nodeOf, IReadOnlyList<int> a, bool aLow,
                        IReadOnlyList<int> b, bool bLow)
        {
            int x = Tie(mesh, nodeOf, a, aLow), y = Tie(mesh, nodeOf, b, bLow);
            Union(x, y);
            return Find(x);
        }

        /// <summary>
        /// <b>PMS-6 — the DC network of a local-grid mesh is the TENSOR grid's network, rebuilt from the blocks.</b> The
        /// resistor-per-basis model is a two-point flux: exact on a conforming rectangle grid, and inconsistent across a
        /// refinement step, where a fine cell's centre is offset ACROSS the current from the block's and the flux between
        /// them is still taken as purely along it. Measured on A1: local 1.3–1.7 % below tensor at every density, the gap
        /// not closing with refinement. A block is a union of the tensor grid's own uncut cells (PMS-4's homogeneity
        /// rule), so each is given one node per position it spans, the elementary resistors between them, and one
        /// resistor per position across each face it shares with a neighbour; a vertical basis on a block is one
        /// elementary via per position. That is the tensor grid's network resistor for resistor, built at DC only —
        /// the AC solve keeps the coarse basis.
        /// </summary>
        private sealed class Expansion(PlanarMesh mesh)
        {
            private readonly IReadOnlyList<double> _gx = mesh.GridX, _gy = mesh.GridY;

            public int NodesOf(int c)
            {
                var cell = mesh.Cells[c];
                return cell.IsBlock ? (cell.IX1 - cell.IX) * (cell.IY1 - cell.IY) : 1;
            }

            public bool Touches(PlanarBasis b) => b.IsSegment || mesh.Cells[b.CellA].IsBlock || mesh.Cells[b.CellB].IsBlock;

            private static int At(PlanarCell cell, int baseNode, int ix, int iy)
                => cell.IsBlock ? baseNode + (iy - cell.IY) * (cell.IX1 - cell.IX) + (ix - cell.IX) : baseNode;

            private double Half(PlanarProblem problem, PlanarMesh m, PlanarCell cell, int ix, int iy, bool alongX,
                                ref bool anyPec)
            {
                double rs = CellSheetResistance(problem, m, cell, ref anyPec);
                if (!cell.IsBlock) return rs * HalfSquares(cell, alongX ? PlanarBasisDirection.X : PlanarBasisDirection.Y);
                double dx = _gx[ix + 1] - _gx[ix], dy = _gy[iy + 1] - _gy[iy];
                return rs * 0.5 * (alongX ? dx / dy : dy / dx);
            }

            /// <summary>The elementary resistors inside every block.</summary>
            public void AddInternal(PlanarProblem problem, PlanarMesh m, int[] nodeOf,
                                    List<(int A, int B, double G)> branches, ref bool anyPec)
            {
                for (int c = 0; c < m.Cells.Count; c++)
                {
                    var cell = m.Cells[c];
                    if (!cell.IsBlock || nodeOf[c] == NoNode) continue;
                    int b0 = nodeOf[c];
                    for (int iy = cell.IY; iy < cell.IY1; iy++)
                        for (int ix = cell.IX; ix < cell.IX1; ix++)
                        {
                            if (ix + 1 < cell.IX1)
                            {
                                double r = Half(problem, m, cell, ix, iy, true, ref anyPec)
                                         + Half(problem, m, cell, ix + 1, iy, true, ref anyPec);
                                if (!(r > 0)) r = PecSheetResistance;
                                branches.Add((At(cell, b0, ix, iy), At(cell, b0, ix + 1, iy), 1.0 / r));
                            }
                            if (iy + 1 < cell.IY1)
                            {
                                double r = Half(problem, m, cell, ix, iy, false, ref anyPec)
                                         + Half(problem, m, cell, ix, iy + 1, false, ref anyPec);
                                if (!(r > 0)) r = PecSheetResistance;
                                branches.Add((At(cell, b0, ix, iy), At(cell, b0, ix, iy + 1), 1.0 / r));
                            }
                        }
                }
            }

            /// <summary>The positions of a basis's face: rows (x basis) or columns (y basis) it spans, or the foot's
            /// positions for a vertical one.</summary>
            private IEnumerable<(int IX, int IY)> Face(PlanarBasis b, bool sideA)
            {
                var a = mesh.Cells[b.CellA]; var c = mesh.Cells[b.CellB];
                var cell = sideA ? a : c;
                if (b.Direction == PlanarBasisDirection.Z)
                {
                    for (int iy = cell.IY; iy < cell.IY1; iy++)
                        for (int ix = cell.IX; ix < cell.IX1; ix++) yield return (ix, iy);
                    yield break;
                }
                bool x = b.Direction == PlanarBasisDirection.X;
                int lo = b.IsSegment ? b.SegmentLo : Math.Max(x ? a.IY : a.IX, x ? c.IY : c.IX);
                int hi = b.IsSegment ? b.SegmentHi : Math.Min(x ? a.IY1 : a.IX1, x ? c.IY1 : c.IX1);
                int along = x ? (sideA ? a.IX1 - 1 : c.IX) : (sideA ? a.IY1 - 1 : c.IY);
                for (int t = lo; t < hi; t++) yield return x ? (along, t) : (t, along);
            }

            public IEnumerable<int> FaceNodes(PlanarMesh m, PlanarBasis b, int cell, int[] nodeOf)
            {
                var ce = m.Cells[cell];
                foreach (var (ix, iy) in Face(b, sideA: cell == b.CellA))
                    yield return At(ce, nodeOf[cell], ix, iy);
            }

            /// <summary>One basis that touches a block: a resistor per position along its face, or an elementary via
            /// per position of its foot.</summary>
            public void AddBasis(PlanarProblem problem, PlanarMesh m, PlanarLevels levels, PlanarBasis b, int[] nodeOf,
                                 int ground, List<(int A, int B, double G)> branches, ref bool anyPec)
            {
                var a = m.Cells[b.CellA]; var c = m.Cells[b.CellB];
                int na = nodeOf[b.CellA], nc = nodeOf[b.CellB];
                if (na == NoNode || nc == NoNode) return;
                if (b.Direction == PlanarBasisDirection.Z || b.AttachesToGround)
                {
                    double length = b.AttachesToGround ? levels.AttachmentLengthOf(b.LayerIndex) : levels.LengthOf(b.LayerIndex);
                    double sigma = ViaSigma(problem, b, a);
                    foreach (var (ix, iy) in Face(b, sideA: true))
                    {
                        double area = (_gx[ix + 1] - _gx[ix]) * (_gy[iy + 1] - _gy[iy]);
                        double r = sigma > 0 && area > 0 && length > 0 ? length / (sigma * area) : 0;
                        if (!(sigma > 0)) anyPec = true;
                        if (!(r > 0)) r = PecSheetResistance;
                        int u = At(a, na, ix, iy);
                        int v = b.AttachesToGround ? ground : At(c, nc, ix, iy);
                        if (u != v) branches.Add((u, v, 1.0 / r));
                    }
                    return;
                }
                bool x = b.Direction == PlanarBasisDirection.X;
                using var fa = Face(b, true).GetEnumerator();
                using var fc = Face(b, false).GetEnumerator();
                while (fa.MoveNext() && fc.MoveNext())
                {
                    var (ax, ay) = fa.Current; var (cx, cy) = fc.Current;
                    double r = Half(problem, m, a, ax, ay, x, ref anyPec) + Half(problem, m, c, cx, cy, x, ref anyPec);
                    if (!(r > 0)) r = PecSheetResistance;
                    branches.Add((At(a, na, ax, ay), At(c, nc, cx, cy), 1.0 / r));
                }
            }
        }

        // ── The resistance of one basis ────────────────────────────────────────────────────────
        //
        // A rooftop is two half-cells in series. The half a cell contributes along the current is
        // Rs·(w/2)/h_eff with w its extent along and h_eff its extent ACROSS — and for a conformally
        // cut cell the metal is not the rectangle, so h_eff is taken as Area/w. That reduces to h
        // exactly on an uncut cell, which is what keeps a Manhattan mesh's answer the closed form.
        private static double Resistance(PlanarProblem problem, PlanarMesh mesh, PlanarLevels levels,
                                         PlanarBasis b, ref bool anyPec)
        {
            if (b.Direction == PlanarBasisDirection.Z || b.AttachesToGround)
                return ViaResistance(problem, mesh, levels, b, ref anyPec);

            var ca = mesh.Cells[b.CellA];
            var cb = mesh.Cells[b.CellB];

            // EM-TFR — each half takes its own sheet resistance. Two halves of one material keep the
            // single expression they always had, so a mesh with no film gives today's bits.
            if (ca.Film == cb.Film)
            {
                double rs = CellSheetResistance(problem, mesh, ca, ref anyPec);
                return rs * (HalfSquares(ca, b.Direction) + HalfSquares(cb, b.Direction));
            }
            return CellSheetResistance(problem, mesh, ca, ref anyPec) * HalfSquares(ca, b.Direction)
                 + CellSheetResistance(problem, mesh, cb, ref anyPec) * HalfSquares(cb, b.Direction);
        }

        private static double HalfSquares(PlanarCell c, PlanarBasisDirection d)
        {
            double along = d == PlanarBasisDirection.X ? c.Width : c.Height;
            double area  = c.Area;
            return area > 0 ? 0.5 * along * along / area : 0;
        }

        /// <summary>A vertical basis: ρ·ℓ/A through the meshed footprint it stands on.</summary>
        private static double ViaResistance(PlanarProblem problem, PlanarMesh mesh, PlanarLevels levels,
                                            PlanarBasis b, ref bool anyPec)
        {
            var cell = mesh.Cells[b.CellA];
            double length = b.AttachesToGround
                ? levels.AttachmentLengthOf(b.LayerIndex)
                : levels.LengthOf(b.LayerIndex);
            double sigma = ViaSigma(problem, b, cell);
            if (!(sigma > 0)) { anyPec = true; return 0; }
            double area = cell.Area;
            return area > 0 && length > 0 ? length / (sigma * area) : 0;
        }

        private static double ViaSigma(PlanarProblem problem, PlanarBasis b, PlanarCell cell)
            => ViaSigmaFor(problem, b, cell);

        private static double CellSheetResistance(PlanarProblem problem, PlanarMesh mesh, PlanarCell cell, ref bool anyPec)
        {
            if (mesh.FilmSheetResistanceOf(cell) is { } film) return film;
            if (cell.Stack < 0) return SheetResistance(problem, cell.LayerIndex, ref anyPec);
            // PMS-3 — a stacked region's interior cell conducts through every level of its stack in parallel.
            var st = mesh.Stacks[cell.Stack];
            double g = 0;
            for (int l = st.LowerLayerIndex; l <= st.UpperLayerIndex; l++)
                g += 1.0 / SheetResistance(problem, l, ref anyPec);
            return 1.0 / g;
        }

        private static double SheetResistance(PlanarProblem problem, int layerIndex, ref bool anyPec)
        {
            var layer = problem.Layers[Math.Clamp(layerIndex, 0, problem.Layers.Count - 1)];
            double denom = layer.SigmaSm * layer.ThicknessM;
            if (denom > 0) return 1.0 / denom;
            anyPec = true;
            return PecSheetResistance;
        }

        private static double ViaSeries(PlanarProblem problem, PlanarMesh mesh, PlanarLevels levels,
                                        PlanarPortResolution port)
        {
            // The port's own bases are its gap; their metal sits between the port and the structure,
            // in parallel across the footprint.
            double g = 0;
            bool pec = false;
            foreach (int bi in port.BasisIndices)
            {
                if ((uint)bi >= (uint)mesh.Bases.Count) continue;
                double r = ViaResistance(problem, mesh, levels, mesh.Bases[bi], ref pec);
                if (r > 0) g += 1.0 / r;
            }
            return g > 0 ? 1.0 / g : 0;
        }

        /// <summary>R-fed-1's lead, as the series resistor it is at DC: Rs·ℓ/W through the port's own
        /// meshed width.</summary>
        public double LeadResistanceOhms(PlanarProblem problem, PlanarPortResolution port,
                                         PlanarFeedLead lead)
        {
            bool pec = false;
            double rs = SheetResistance(problem, port.LayerIndex, ref pec);
            double w  = port.WidthM > 0 ? port.WidthM : port.GridWidthM;
            return w > 0 ? rs * lead.LengthM / w : 0;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // THE SOLVE
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <b>Y by driving each port with an ideal voltage source, solved as one MNA system.</b>
        /// Nothing is inverted and no current is driven: a source across each port's terminal PAIR
        /// gives the short-circuit condition every other port needs (V = 0 across it) without
        /// asserting anything about where those terminals sit relative to the plane, which is what
        /// makes the formulation survive a FLOATING conductor. Driving currents instead would need a
        /// Z that does not exist for one — a trace over a plane it never touches has a perfectly
        /// good two-port Y and no Z at all.
        ///
        /// <para><b>It used to hold every terminal at an absolute potential, and that is only the
        /// short-circuit condition when every port's − terminal IS the common node.</b> True for an
        /// edge port and for a via-to-plane port, both of which reference the ground node. False for
        /// an internal delta gap, whose − terminal is the far lip of the cut — signal metal. Pinning
        /// that lip to zero does not set V = 0 across the port; it welds the trace to ground on the
        /// far side of the gap, and the two lips stop being one port at all: current entering the
        /// near lip is under no obligation to leave the far one. Owner report, 2026-09-14 — a 50 Ω
        /// line cut by a gap port published S₁₃ = 1 and S₂₂ = −1 at the low end of a sweep, where
        /// the right answer is the 1/3-2/3 split of three ports meeting at one series cut, and the
        /// gap sim was unusable below the fit floor. The rank is the tell: the true network has ONE
        /// independent current (I₁ = I₃, I₂ = −I₃) so Y is rank 1, and the Dirichlet answer was
        /// rank 2 — the extra rank is exactly the current path that should not exist.</para>
        ///
        /// <para>Ports whose − terminal is the ground node are unaffected by the change: their
        /// source rows reduce to the same Dirichlet condition the old code wrote by hand.</para>
        /// </summary>
        public Mat<Complex> Admittance(List<string> notes)
        {
            int p = _plus.Length;
            var y = new Mat<Complex>(p, p);

            // Super-nodes, after every terminal tie.
            var rep = new int[_nodeCount];
            for (int i = 0; i < _nodeCount; i++) rep[i] = Find(i);

            // Branches, in super-node terms. A branch inside one terminal is a short across itself.
            var br = new List<(int A, int B, double G)>(_branches.Count);
            foreach (var (a, b, g) in _branches)
            {
                int ra = rep[a], rb = rep[b];
                if (ra != rb) br.Add((ra, rb, g));
            }

            // The terminals, THROUGH rep — for the same reason the branches are. Build wrote a
            // Find() result into _plus/_minus, and a LATER port's Tie can union that root under a
            // new one, which leaves the stored id a non-root that the branch list never mentions.
            // The ground node was already read this way here; the port terminals were not.
            int gnd   = rep[_groundNode];
            var plus  = new int[p];
            var minus = new int[p];
            for (int i = 0; i < p; i++) { plus[i] = rep[_plus[i]]; minus[i] = rep[_minus[i]]; }

            // ── WHICH TERMINALS CAN REACH EACH OTHER AT ALL — AND IT IS ASKED STRUCTURALLY ───────
            //
            // A port with no DC path to anything is the case this file exists for (the series MIM
            // cap), and its answer has to be a row of EXACT zeros rather than a small number. It does
            // not come out of the solve that way: the island's free nodes settle at the driven
            // potential to ~1e-16, and the current is that residual times a conductance of some
            // thousands of siemens, so it reads ~1e-12 S — indistinguishable from a real, very large
            // leakage resistance. Whether two terminals are connected is a question about the GRAPH,
            // so it is answered on the graph, and the numerical solve is then only asked about the
            // entries it can actually determine.
            var comp = new int[_nodeCount];
            for (int i = 0; i < _nodeCount; i++) comp[i] = i;
            int CompOf(int i) { while (comp[i] != i) i = comp[i] = comp[comp[i]]; return i; }
            foreach (var (a, b, _) in br)
            {
                int ra = CompOf(a), rb = CompOf(b);
                if (ra != rb) comp[rb] = ra;
            }

            // A conduction component carries current only if TWO port terminals sit in it: one alone
            // makes the whole component sit at that terminal's potential and conduct nothing, whatever
            // the excitation. Counted over terminals rather than over held nodes, because a delta gap
            // contributes two of them and neither is the ground node.
            var terminals = new Dictionary<int, int>();
            for (int i = 0; i < p; i++)
            {
                int cp = CompOf(plus[i]),  cm = CompOf(minus[i]);
                terminals[cp] = terminals.GetValueOrDefault(cp) + 1;
                terminals[cm] = terminals.GetValueOrDefault(cm) + 1;
            }

            // ── WHICH PORTS GET A SOURCE ─────────────────────────────────────────────────────────
            //
            // A source LOOP has no solution (two ports sharing one terminal pair assert two different
            // voltages across the same metal), so the one that closes the loop is refused rather than
            // handed to a factorisation that would return whatever the pivoting happened to produce.
            // No port kind this kernel builds can close one — each cut is its own — so this is a
            // guard, not a path.
            var active    = new bool[p];
            var srcParent = new int[_nodeCount];
            for (int i = 0; i < _nodeCount; i++) srcParent[i] = i;
            int SrcOf(int i) { while (srcParent[i] != i) i = srcParent[i] = srcParent[srcParent[i]]; return i; }

            for (int i = 0; i < p; i++)
            {
                if (plus[i] == minus[i])
                {
                    notes.Add($"DC: port {Number(i)}'s two terminals are the same piece of metal, so " +
                              "it is a dead short at DC and has no admittance to publish. Its row and " +
                              "column are left at zero.");
                    continue;
                }
                if (terminals.GetValueOrDefault(CompOf(plus[i]))  < 2 ||
                    terminals.GetValueOrDefault(CompOf(minus[i])) < 2)
                    continue;                                  // no path — exact zero row and column

                int sa = SrcOf(plus[i]), sb = SrcOf(minus[i]);
                if (sa == sb)
                {
                    notes.Add($"DC: port {Number(i)} shares both of its terminals with another port, " +
                              "so the two would have to impose different voltages on the same metal. " +
                              "Its row and column are left at zero.");
                    continue;
                }
                srcParent[sb] = sa;
                active[i] = true;
            }

            // ── ISLANDS: BRANCHES **AND** SOURCES, BECAUSE A PORT IS A CONNECTION ────────────────
            //
            // The old reachability was over branches alone, which is exactly backwards for a cut
            // port: a gap's two lips are in different conduction components BY CONSTRUCTION — that is
            // what a cut is — so every port on the far side was declared unreachable and hard-zeroed.
            // A non-driven port is a short, so it joins what it touches.
            var isle = new int[_nodeCount];
            for (int i = 0; i < _nodeCount; i++) isle[i] = i;
            int IsleOf(int i) { while (isle[i] != i) i = isle[i] = isle[isle[i]]; return i; }
            void JoinIsle(int a, int b) { a = IsleOf(a); b = IsleOf(b); if (a != b) isle[b] = a; }
            foreach (var (a, b, _) in br) JoinIsle(a, b);
            for (int i = 0; i < p; i++) if (active[i]) JoinIsle(plus[i], minus[i]);

            var live = new HashSet<int>();
            for (int i = 0; i < p; i++) if (active[i]) live.Add(IsleOf(plus[i]));

            // ── A DATUM PER ISLAND ──────────────────────────────────────────────────────────────
            //
            // The ground node is the datum of its own island. An island that never reaches it — a
            // trace over a plane it does not touch, driven only through gap ports — is floating, and
            // its common-mode potential is genuinely arbitrary: it appears in no current. One node of
            // it is pinned so the system has a solution at all; without that the constant vector is a
            // null vector and the factorisation is singular.
            var touched = new bool[_nodeCount];
            foreach (var (a, b, _) in br) { touched[a] = true; touched[b] = true; }
            for (int i = 0; i < p; i++)
                if (active[i]) { touched[plus[i]] = true; touched[minus[i]] = true; }

            var datum = new Dictionary<int, int> { [IsleOf(gnd)] = gnd };
            for (int i = 0; i < _nodeCount; i++)
            {
                if (rep[i] != i || !touched[i]) continue;
                int isl = IsleOf(i);
                if (live.Contains(isl)) datum.TryAdd(isl, i);
            }

            // ── UNKNOWNS: one per live non-datum node, plus one current per active port ──────────
            var nodeIndex = new int[_nodeCount];
            Array.Fill(nodeIndex, -1);
            int nNode = 0;
            for (int i = 0; i < _nodeCount; i++)
            {
                if (rep[i] != i || !touched[i]) continue;
                int isl = IsleOf(i);
                if (!live.Contains(isl) || datum[isl] == i) continue;
                nodeIndex[i] = nNode++;
            }

            var portIndex = new int[p];
            Array.Fill(portIndex, -1);
            int nSrc = 0;
            for (int i = 0; i < p; i++) if (active[i]) portIndex[i] = nNode + nSrc++;

            if (nSrc == 0) return y;

            // The conductances here run to ~1e6 S (PecSheetResistance is 1 µΩ/sq) while a source row
            // is ±1, and a pivot search comparing those across one matrix costs digits for nothing.
            // Scaling the unknown current by a typical conductance puts both blocks on one scale; it
            // is a change of variable, so the answer is unchanged.
            double scale = 0;
            foreach (var (_, _, g) in br) scale += g;
            scale = br.Count > 0 && scale > 0 ? scale / br.Count : 1.0;

            int n = nNode + nSrc;
            var tri = new CoordinateStorage<double>(n, n, br.Count * 4 + nSrc * 4 + n);
            foreach (var (a, b, g) in br)
            {
                int ia = nodeIndex[a], ib = nodeIndex[b];
                if (ia >= 0) tri.At(ia, ia, g);
                if (ib >= 0) tri.At(ib, ib, g);
                if (ia >= 0 && ib >= 0) { tri.At(ia, ib, -g); tri.At(ib, ia, -g); }
            }
            for (int k = 0; k < p; k++)
            {
                if (!active[k]) continue;
                int c = portIndex[k], ip = nodeIndex[plus[k]], im = nodeIndex[minus[k]];
                // KCL: the source delivers its current into + and takes it out of −.
                if (ip >= 0) { tri.At(ip, c, -scale); tri.At(c, ip,  scale); }
                if (im >= 0) { tri.At(im, c,  scale); tri.At(c, im, -scale); }
            }

            var csc  = SparseMatrix.OfIndexed(tri);
            var perm = AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtPlusA);
            var lu   = SparseLU.Create(csc, perm, 1.0);

            var rhs = new double[n];
            var sol = new double[n];

            for (int j = 0; j < p; j++)
            {
                if (!active[j]) continue;

                Array.Clear(rhs);
                rhs[portIndex[j]] = scale;                     // V_j = 1, every other port shorted
                lu.Solve(rhs, sol);

                int driven = IsleOf(plus[j]);
                for (int i = 0; i < p; i++)
                    if (active[i] && IsleOf(plus[i]) == driven)
                        y[i, j] = new Complex(scale * sol[portIndex[i]], 0);
            }

            return y;
        }

        /// <summary>The port NUMBER behind a row index, for the notes. The two differ the moment a
        /// design numbers its ports anything but 1..n, and a message naming the wrong one sends a
        /// reader to the wrong port.</summary>
        private int Number(int i) => (uint)i < (uint)_numbers.Length ? _numbers[i] : i + 1;

    }
}
