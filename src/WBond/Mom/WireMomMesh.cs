namespace CircuitRF.WBond.Mom;

/// <summary>
/// What one mesh actually is, reported <b>before</b> any matrix is filled (RW2).
///
/// <para>The repository has already paid for a ceiling that predicted, passed, and threw twenty real
/// minutes later (<c>src/Engine/Mom/RESOLVED.md</c>, the de-embed closeout). The prediction here is
/// therefore produced by <see cref="WireMomMesh.Predict"/> without allocating anything, and the
/// arithmetic behind <see cref="PredictedPeakBytes"/> is spelled out in
/// <see cref="MemoryArithmetic"/> rather than asserted.</para>
/// </summary>
/// <param name="Segments">N_s — current unknowns.</param>
/// <param name="Nodes">N_n — charge unknowns before shorting.</param>
/// <param name="ReducedNodes">N_r — charge unknowns after terminal shorting.</param>
/// <param name="Terminals">T = 2M.</param>
/// <param name="Wires">Wire count.</param>
/// <param name="Arrays">Array count M.</param>
/// <param name="ClampedWires">
/// How many wires hit <see cref="WireMomSettings.MaxSegmentsPerWire"/>. Non-zero means the mesh is
/// coarser than the settings asked for, which is reported rather than absorbed.
/// </param>
/// <param name="PredictedPeakBytes">
/// The largest simultaneous residency across the fill, the assembly <i>and</i> WM-2's own complex
/// system. <b>WM-2's M̃ is included deliberately</b> — a report that stops at this brief's own
/// matrices and then watches WM-2 allocate another 16·N_s² bytes is a report that lied.
/// </param>
/// <param name="MemoryArithmetic">The sum, term by term, that produced <paramref name="PredictedPeakBytes"/>.</param>
/// <param name="Warnings">Proximity (RW17) and clamp warnings, each naming the wires it is about.</param>
/// <param name="PredictedSetupSeconds">
/// <see cref="WireMomCost.SetupSeconds"/> for this mesh — everything frequency-independent.
/// </param>
/// <param name="PredictedPerPointSeconds"><see cref="WireMomCost.PerPointSeconds"/> for this mesh.</param>
/// <param name="SolveThreads">
/// How many frequency points a sweep of this mesh will solve at once, under this settings object's
/// memory budget. <b>Part of the report because it is part of the prediction</b>: the same sweep is two
/// and a half times slower at one thread than at ten, and a user looking at a number needs to know
/// which one it is.
/// </param>
public sealed record WireMomMeshReport(
    int Segments,
    int Nodes,
    int ReducedNodes,
    int Terminals,
    int Wires,
    int Arrays,
    int ClampedWires,
    long PredictedPeakBytes,
    string MemoryArithmetic,
    IReadOnlyList<string> Warnings,
    double PredictedSetupSeconds = 0.0,
    double PredictedPerPointSeconds = 0.0,
    int SolveThreads = 1)
{
    /// <summary>
    /// Predicted wall clock for a sweep of <paramref name="points"/> frequencies, at
    /// <see cref="SolveThreads"/> and at the measured parallel efficiency — <b>not</b> at a linear one.
    /// </summary>
    public double PredictedSweepSeconds(int points) =>
        PredictedSetupSeconds +
        points * PredictedPerPointSeconds /
            WireMomCost.ParallelSpeedup(Math.Min(SolveThreads, Math.Max(1, points)));

    /// <summary>
    /// Peak bytes including the <b>per-thread</b> <c>M̃</c> buffers a parallel sweep will hold at once.
    /// <see cref="PredictedPeakBytes"/> counts one of them, because it is also the answer for a caller
    /// that solves a point at a time.
    /// </summary>
    public long PredictedPeakBytesForSweep =>
        PredictedPeakBytes + (SolveThreads - 1) * WireMomCost.BytesPerSolveThread(Segments, Terminals);

    /// <summary>
    /// The one-line cost sentence — what the panel shows before Run, and what the slow-run warning
    /// quotes, so the two can never disagree about the same sweep.
    /// </summary>
    public string CostSummary(int points)
    {
        int threads = Math.Min(SolveThreads, Math.Max(1, points));
        string thread = threads == 1 ? "1 thread" : $"{threads} threads";
        return
            $"Predicted ~{WireMomCost.Duration(PredictedSweepSeconds(points))} for {points} point(s) " +
            $"({thread}): ~{WireMomCost.Duration(PredictedSetupSeconds)} of setup plus " +
            $"~{WireMomCost.Duration(PredictedPerPointSeconds)} per point. Peak " +
            $"{PredictedPeakBytesForSweep / 1048576.0:0.#} MB.";
    }

    /// <summary>The loop count W − M: the nullity of Ãᵀ, and therefore of K̃ (§2.6 item 2).</summary>
    public int LoopCount => Wires - Arrays;

    public double PredictedPeakMegabytes => PredictedPeakBytes / (1024.0 * 1024.0);

    public override string ToString() =>
        $"N_s = {Segments}, N_n = {Nodes}, N_r = {ReducedNodes}, T = {Terminals} " +
        $"({Wires} wires in {Arrays} arrays); predicted peak {PredictedPeakMegabytes:F1} MB.";
}

/// <summary>
/// The segment/node mesh kernel W1 is filled over: one axial-current unknown per <b>segment</b> and
/// one charge unknown per <b>node</b> (the standard Ruehli PEEC pairing).
///
/// <h3>This is a finer <see cref="WireMesh"/>, not a different one</h3>
/// <para>Every cell is a <see cref="Filament"/>, every image is <see cref="Filament.Image"/>, and the
/// kernels evaluated over them are <see cref="Grover.Mutual"/> and
/// <see cref="PotentialCoefficients.Kernel"/> — the same four pieces of physics the wire-basis model is
/// validated on. <b>Nothing about the physics is new here.</b> What is new is the discretisation, the
/// incidence bookkeeping, and the frequency-independent assembly.</para>
///
/// <h3>Subdivide, never merge</h3>
/// <para>Each polyline segment is split into <c>ceil(len / maxSegmentLength)</c> equal parts and no
/// two polyline segments are ever merged. Three things follow, and each is worth keeping: the authored
/// geometry survives exactly (every original vertex is still a node), subdivision invariance becomes
/// testable, and the mesh is deterministic — so a cached fill is safe.</para>
///
/// <h3>Incidence is stored as two int arrays, not as a matrix</h3>
/// <para><b>A</b> has exactly two non-zeros per row. Every product this kernel needs (<c>AR</c>,
/// <c>ÃG⁻¹Ãᵀ</c>, <c>ÃG⁻¹E</c>) is an O(1)-per-entry index operation against
/// <see cref="StartNode"/>/<see cref="EndNode"/>, while a dense N_s × N_n at the 200-wire size would be
/// 288 MB of almost entirely zeros.</para>
/// </summary>
public sealed class WireMomMesh
{
    private WireMomMesh(
        WBondDesign design,
        WireMomSettings settings,
        Filament[] segments, Filament[] segmentImages,
        int[] wireSegStart, int[] wireSegCount,
        int[] wireNodeStart,
        int[] startNode, int[] endNode,
        int nodeCount,
        int[] reducedOfNode, int reducedCount, int terminalCount,
        Filament[] halves, Filament[] halfImages,
        int[] nodeCellStart, int[] nodeCellIndex,
        double[] nodeCellLength,
        double[] segmentLength, double[] segmentRadius, double[] segmentSigma,
        int[] wireOfSegment, int[] arrayOfWire,
        Wire[] wires, string[] arrayNames, string[] terminalNames,
        bool hasImages,
        WireMomMeshReport report)
    {
        Design = design;
        Settings = settings;
        Segments = segments;
        SegmentImages = segmentImages;
        WireSegStart = wireSegStart;
        WireSegCount = wireSegCount;
        WireNodeStart = wireNodeStart;
        StartNode = startNode;
        EndNode = endNode;
        NodeCount = nodeCount;
        ReducedOfNode = reducedOfNode;
        ReducedCount = reducedCount;
        TerminalCount = terminalCount;
        Halves = halves;
        HalfImages = halfImages;
        NodeCellStart = nodeCellStart;
        NodeCellIndex = nodeCellIndex;
        NodeCellLength = nodeCellLength;
        SegmentLength = segmentLength;
        SegmentRadius = segmentRadius;
        SegmentSigma = segmentSigma;
        WireOfSegment = wireOfSegment;
        ArrayOfWire = arrayOfWire;
        Wires = wires;
        ArrayNames = arrayNames;
        TerminalNames = terminalNames;
        HasImages = hasImages;
        Report = report;
    }

    public WBondDesign Design { get; }

    public WireMomSettings Settings { get; }

    /// <summary>The current cells, grouped by wire and ordered from each wire's input end.</summary>
    public Filament[] Segments { get; }

    /// <summary>Each segment's ground-plane image, index-parallel to <see cref="Segments"/>. Empty when the plane is off.</summary>
    public Filament[] SegmentImages { get; }

    public int[] WireSegStart { get; }

    public int[] WireSegCount { get; }

    /// <summary>Index of each wire's first node. Wire <i>w</i> owns <c>WireSegCount[w] + 1</c> of them.</summary>
    public int[] WireNodeStart { get; }

    /// <summary><b>A</b>'s <c>+1</c> column for each segment: current leaves this node.</summary>
    public int[] StartNode { get; }

    /// <summary><b>A</b>'s <c>−1</c> column for each segment.</summary>
    public int[] EndNode { get; }

    public int NodeCount { get; }

    /// <summary><b>R</b> in compact form: the reduced index every node collapses onto. Terminals take 0 … T−1.</summary>
    public int[] ReducedOfNode { get; }

    public int ReducedCount { get; }

    /// <summary>T = 2M. <b>E</b> is the leading T rows of the identity, so it is a slice and never a matrix.</summary>
    public int TerminalCount { get; }

    /// <summary>Two per segment: the first half belongs to its start node, the second to its end node.</summary>
    public Filament[] Halves { get; }

    /// <summary>Images of <see cref="Halves"/>. Empty when the plane is off.</summary>
    public Filament[] HalfImages { get; }

    /// <summary>CSR offsets into <see cref="NodeCellIndex"/> — node <i>n</i>'s charge cell.</summary>
    public int[] NodeCellStart { get; }

    /// <summary>Half-filament indices, grouped by owning node. 1 or 2 per node.</summary>
    public int[] NodeCellIndex { get; }

    /// <summary>l_m — each charge cell's total length, metres.</summary>
    public double[] NodeCellLength { get; }

    public double[] SegmentLength { get; }

    public double[] SegmentRadius { get; }

    /// <summary>Conductivity at the design's operating temperature, per segment, S/m.</summary>
    public double[] SegmentSigma { get; }

    public int[] WireOfSegment { get; }

    public int[] ArrayOfWire { get; }

    public Wire[] Wires { get; }

    public string[] ArrayNames { get; }

    /// <summary><c>G1.i, G1.o, G2.i, …</c> — the same order the exported Touchstone's ports use.</summary>
    public string[] TerminalNames { get; }

    public bool HasImages { get; }

    public WireMomMeshReport Report { get; }

    public int SegmentCount => Segments.Length;

    public int WireCount => WireSegStart.Length;

    public int ArrayCount => ArrayNames.Length;

    /// <summary>The reduced index of segment <i>k</i>'s start node — <b>Ã</b>'s <c>+1</c> column.</summary>
    public int ReducedStart(int segment) => ReducedOfNode[StartNode[segment]];

    /// <summary>The reduced index of segment <i>k</i>'s end node — <b>Ã</b>'s <c>−1</c> column.</summary>
    public int ReducedEnd(int segment) => ReducedOfNode[EndNode[segment]];

    /// <summary>
    /// The terminal names a design's ports carry, in the design's own array order.
    ///
    /// <para><b>This must agree with <c>WBondTouchstoneExport.PortNames(design, Terminals)</c> element
    /// for element.</b> A file and a schematic symbol that disagree about which port is which is a
    /// failure mode this repository has already paid for once; the order is documented here and
    /// asserted in <c>PortNamingTests</c>.</para>
    /// </summary>
    public static string[] TerminalNamesFor(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        var names = new string[design.Arrays.Count * 2];
        for (int a = 0; a < design.Arrays.Count; a++)
        {
            names[2 * a] = design.Arrays[a].Name + ".i";
            names[2 * a + 1] = design.Arrays[a].Name + ".o";
        }
        return names;
    }

    // ------------------------------------------------------------------ prediction

    /// <summary>
    /// The mesh report a design <i>would</i> produce, <b>without allocating a single matrix</b> —
    /// RW2's "the predicted N is reported before the solve".
    ///
    /// <para>It does not refuse. <see cref="Build"/> refuses; this exists so a caller can show the
    /// number, and the ceiling, before anyone waits for anything.</para>
    /// </summary>
    public static WireMomMeshReport Predict(WBondDesign design, WireMomSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        settings = settings ?? WireMomSettings.Default;
        design.Validate();

        var wires = CollectWires(design, out var arrayOfWire);
        var perWire = new int[wires.Count];
        int clamped = 0;

        for (int w = 0; w < wires.Count; w++)
        {
            perWire[w] = SegmentsForWire(wires[w], settings, out bool clampBit);
            if (clampBit) clamped++;
        }

        int ns = 0;
        foreach (int c in perWire) ns += c;
        int nn = ns + wires.Count;
        int terminals = 2 * design.Arrays.Count;
        int nr = terminals + ns - wires.Count;

        var warnings = new List<string>();
        if (clamped > 0)
            warnings.Add(
                $"{clamped} wire(s) hit the {settings.MaxSegmentsPerWire}-segment cap and are meshed " +
                "coarser than 'Segments per wire' asked for.");
        warnings.AddRange(ProximityWarnings(wires, settings));

        long peak = PredictPeakBytes(ns, nn, nr, out string arithmetic);

        return new WireMomMeshReport(
            ns, nn, nr, terminals, wires.Count, design.Arrays.Count,
            clamped, peak, arithmetic, warnings,
            WireMomCost.SetupSeconds(ns), WireMomCost.PerPointSeconds(ns),
            WireMomCost.SolveThreadCount(ns, terminals, settings));
    }

    /// <summary>
    /// §8's arithmetic, stated rather than hand-waved. The peak is the largest of three residencies:
    /// the two fills together, the assembly, and WM-2's own solve.
    /// </summary>
    private static long PredictPeakBytes(int ns, int nn, int nr, out string arithmetic)
    {
        long l = 8L * ns * ns;          // L, real, lives for the whole run
        long p = 8L * nn * nn;          // P, real, released after G is formed
        long g = 8L * nr * nr;          // G, real
        long y = 8L * (long)nr * ns;    // Y = G^-1 A~^T
        long k = 8L * ns * ns;          // K~, real
        long m = 16L * ns * ns;         // WM-2's M~, complex

        // THE CHOLESKY FACTOR IS A SECOND MATRIX, NOT AN IN-PLACE ONE. CholeskyFactor.Factor does not
        // modify its input, so P and its factor are alive together, and so are G and its factor.
        // Leaving those out of the arithmetic understated the 200-wire peak by 190 MB — measured, not
        // reasoned: predicted 825 MB against a 1,305 MB working set.
        long fill = l + p;
        long reduce = l + 2 * p + g;            // step 1: P, chol(P), G
        long assemble = l + 2 * g + y + k;      // steps 3-4: G, chol(G), Y, K~
        long solve = l + k + m;                 // WM-2
        long peak = Math.Max(Math.Max(fill, reduce), Math.Max(assemble, solve));

        static string Mb(long b) => $"{b / (1024.0 * 1024.0):F1} MB";

        arithmetic =
            $"fill  L(8·{ns}²={Mb(l)}) + P(8·{nn}²={Mb(p)}) = {Mb(fill)}; " +
            $"reduce  L + P + chol(P) + G(8·{nr}²={Mb(g)}) = {Mb(reduce)}; " +
            $"assemble  L + G + chol(G) + Y(8·{nr}·{ns}={Mb(y)}) + K~(8·{ns}²={Mb(k)}) = {Mb(assemble)}; " +
            $"WM-2 solve  L + K~ + M~(16·{ns}²={Mb(m)}) = {Mb(solve)}; " +
            $"peak = {Mb(peak)}.";

        return peak;
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Meshes a design. Refuses <b>here</b>, before any fill, when there is no reference conductor or
    /// when the segment count is above <see cref="WireMomSettings.UnknownCeiling"/>.
    /// </summary>
    public static WireMomMesh Build(WBondDesign design, WireMomSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        settings = settings ?? WireMomSettings.Default;

        var report = Predict(design, settings);

        RefuseIfReturnPathUndeclared(design);
        RefuseIfAboveCeiling(design, settings, report);

        var wires = CollectWires(design, out var arrayOfWire);
        int wireCount = wires.Count;

        var wireSegStart = new int[wireCount];
        var wireSegCount = new int[wireCount];
        var wireNodeStart = new int[wireCount];

        int ns = 0, nn = 0;
        for (int w = 0; w < wireCount; w++)
        {
            wireSegStart[w] = ns;
            wireNodeStart[w] = nn;
            wireSegCount[w] = SegmentsForWire(wires[w], settings, out _);
            ns += wireSegCount[w];
            nn += wireSegCount[w] + 1;
        }

        bool hasImages = design.GroundPlane.Enabled;

        var segments = new Filament[ns];
        var segmentImages = hasImages ? new Filament[ns] : [];
        var halves = new Filament[2 * ns];
        var halfImages = hasImages ? new Filament[2 * ns] : [];
        var startNode = new int[ns];
        var endNode = new int[ns];
        var segmentLength = new double[ns];
        var segmentRadius = new double[ns];
        var segmentSigma = new double[ns];
        var wireOfSegment = new int[ns];

        for (int w = 0; w < wireCount; w++)
        {
            var wire = wires[w];
            double radius = wire.RadiusMetres;
            double sigma = design.MaterialFor(wire).SigmaAt(design.OperatingTempC);
            double maxLen = MaxSegmentLength(wire, settings);

            int at = wireSegStart[w];
            int node = wireNodeStart[w];

            for (int i = 1; i < wire.Points.Count; i++)
            {
                ToMetres(wire.Points[i - 1], out double ax, out double ay, out double az);
                ToMetres(wire.Points[i], out double bx, out double by, out double bz);

                int pieces = PiecesFor(ax, ay, az, bx, by, bz, maxLen);
                for (int j = 0; j < pieces; j++)
                {
                    Lerp(ax, ay, az, bx, by, bz, j, pieces, out double sx, out double sy, out double sz);
                    Lerp(ax, ay, az, bx, by, bz, j + 1, pieces, out double ex, out double ey, out double ez);

                    var f = Filament.FromEndpoints(sx, sy, sz, ex, ey, ez, radius);
                    segments[at] = f;
                    if (hasImages) segmentImages[at] = f.Image();

                    // THE CHARGE CELL IS A HALF-CELL. Segment k contributes its first half to its
                    // start node and its second half to its end node; a node's cell is the union of
                    // the halves of its incident segments nearest to it. Ruehli's pairing, exactly.
                    double mx = 0.5 * (sx + ex), my = 0.5 * (sy + ey), mz = 0.5 * (sz + ez);
                    var h0 = Filament.FromEndpoints(sx, sy, sz, mx, my, mz, radius);
                    var h1 = Filament.FromEndpoints(mx, my, mz, ex, ey, ez, radius);
                    halves[2 * at] = h0;
                    halves[2 * at + 1] = h1;
                    if (hasImages)
                    {
                        halfImages[2 * at] = h0.Image();
                        halfImages[2 * at + 1] = h1.Image();
                    }

                    startNode[at] = node;
                    endNode[at] = node + 1;
                    segmentLength[at] = f.Length;
                    segmentRadius[at] = radius;
                    segmentSigma[at] = sigma;
                    wireOfSegment[at] = w;

                    at++;
                    node++;
                }
            }
        }

        // ---- terminal shorting. Terminals first, so E = [I_T ; 0] is a slice.
        int terminals = 2 * design.Arrays.Count;
        var reducedOfNode = new int[nn];
        Array.Fill(reducedOfNode, -1);

        for (int w = 0; w < wireCount; w++)
        {
            int a = arrayOfWire[w];
            reducedOfNode[wireNodeStart[w]] = 2 * a;
            reducedOfNode[wireNodeStart[w] + wireSegCount[w]] = 2 * a + 1;
        }

        int next = terminals;
        for (int n = 0; n < nn; n++)
            if (reducedOfNode[n] < 0) reducedOfNode[n] = next++;

        // ---- the charge cells, as CSR over the half filaments.
        var nodeCellStart = new int[nn + 1];
        for (int k = 0; k < ns; k++)
        {
            nodeCellStart[startNode[k] + 1]++;
            nodeCellStart[endNode[k] + 1]++;
        }
        for (int n = 0; n < nn; n++) nodeCellStart[n + 1] += nodeCellStart[n];

        var cursor = (int[])nodeCellStart.Clone();
        var nodeCellIndex = new int[2 * ns];
        for (int k = 0; k < ns; k++)
        {
            nodeCellIndex[cursor[startNode[k]]++] = 2 * k;
            nodeCellIndex[cursor[endNode[k]]++] = 2 * k + 1;
        }

        var nodeCellLength = new double[nn];
        for (int n = 0; n < nn; n++)
        {
            double total = 0.0;
            for (int c = nodeCellStart[n]; c < nodeCellStart[n + 1]; c++)
                total += halves[nodeCellIndex[c]].Length;
            nodeCellLength[n] = total;
        }

        var arrayNames = new string[design.Arrays.Count];
        for (int a = 0; a < arrayNames.Length; a++) arrayNames[a] = design.Arrays[a].Name;

        return new WireMomMesh(
            design, settings,
            segments, segmentImages,
            wireSegStart, wireSegCount, wireNodeStart,
            startNode, endNode, nn,
            reducedOfNode, next, terminals,
            halves, halfImages,
            nodeCellStart, nodeCellIndex, nodeCellLength,
            segmentLength, segmentRadius, segmentSigma,
            wireOfSegment, arrayOfWire,
            [.. wires], arrayNames, TerminalNamesFor(design),
            hasImages,
            report);
    }

    /// <summary>
    /// The one-line warning for a sweep that is predicted to take longer than
    /// <paramref name="thresholdSeconds"/>, or <c>null</c> when it is not — <b>a warning, never a
    /// refusal</b>. A fourteen-minute run someone chose is a legitimate run; a fourteen-minute run
    /// nobody was warned about is a bug report.
    ///
    /// <para>It names the coarser rung's cost as well, because "this will be slow" without a cheaper
    /// number beside it is a message the reader can do nothing with —
    /// <c>em-refusal-must-name-a-binding-remedy</c>, applied to a warning.</para>
    /// </summary>
    public static string? SlowRunWarning(WBondDesign design, int points, WireMomSettings? settings = null,
                                         double thresholdSeconds = 60.0)
    {
        ArgumentNullException.ThrowIfNull(design);
        settings ??= WireMomSettings.Default;

        var report = Predict(design, settings);
        double predicted = report.PredictedSweepSeconds(points);
        if (predicted <= thresholdSeconds) return null;

        var message =
            $"This sweep is predicted to take about {WireMomCost.Duration(predicted)} " +
            $"({report.Segments:N0} unknowns x {points} point(s), " +
            $"{Math.Min(report.SolveThreads, Math.Max(1, points))} thread(s)).";

        int fits = WireMomCost.SegmentsForBudget(design, points, thresholdSeconds, settings);
        if (fits > 0 && fits < settings.TargetSegmentsPerWire)
        {
            var probe = settings with { TargetSegmentsPerWire = fits };
            var coarser = Predict(design, probe);
            message +=
                $" At {fits} segments per wire it would be about " +
                $"{WireMomCost.Duration(coarser.PredictedSweepSeconds(points))} " +
                $"({coarser.Segments:N0} unknowns).";
        }
        else if (fits == 0)
        {
            message +=
                " No segments-per-wire value fits that budget for this design — the wire count itself " +
                "is the lever, and solving one array at a time is the way to reach it.";
        }

        return message;
    }

    /// <summary>
    /// The refusal <see cref="Build"/> would make for this design, or <c>null</c> when it would build.
    ///
    /// <para><b>A caller that shows <see cref="Predict"/>'s report has to be able to show the refusal
    /// too</b>, or the panel reports 1,000 unknowns and a 40 ms setup for a design that cannot be
    /// solved at all, and the user finds out only after pressing Run. Predict deliberately does not
    /// refuse — it exists so a number can be shown — so the two are offered separately and
    /// <see cref="Build"/> is still the one place that throws.</para>
    /// </summary>
    public static string? RefusalFor(WBondDesign design, WireMomSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        settings ??= WireMomSettings.Default;

        try
        {
            var report = Predict(design, settings);
            RefuseIfReturnPathUndeclared(design);
            RefuseIfAboveCeiling(design, settings, report);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ refusals

    /// <summary>
    /// Mirrors <c>WBondTouchstoneExport.RefuseIfReturnPathUndeclared</c>. RW13: a port in kernel W
    /// carries an explicit reference conductor, so a design with no plane has no terminal basis and
    /// there is nothing honest to return.
    /// </summary>
    private static void RefuseIfReturnPathUndeclared(WBondDesign design)
    {
        if (design.GroundPlane.Enabled) return;

        throw new InvalidOperationException(
            "This design has its ground plane disabled and no array nominated as the return " +
            "conductor, so the distributed model's ports have no reference conductor to be referenced " +
            "to and its inductance has no defined return path. Re-enable the ground plane (the image " +
            "plane at z = 0 then IS the return), or add ground bond wires and nominate their array as " +
            "the reference.");
    }

    /// <summary>
    /// The ceiling, refused at mesh time with three <b>binding</b> remedies and their real numbers.
    ///
    /// <para><c>em-refusal-must-name-a-binding-remedy</c>: a refusal that names knobs which do not
    /// change the outcome is worse than no refusal, because it sends the reader to a panel setting that
    /// cannot help. Each of the three below is computed against <i>this</i> design, so each is known to
    /// move the number it quotes.</para>
    /// </summary>
    private static void RefuseIfAboveCeiling(WBondDesign design, WireMomSettings settings, WireMomMeshReport report)
    {
        if (report.Segments <= settings.UnknownCeiling) return;

        var wires = CollectWires(design, out var arrayOfWire);

        // Remedy 1 — the largest 'segments per wire' that fits, and what it actually gives.
        int bestTarget = 0, bestCount = 0;
        for (int t = settings.TargetSegmentsPerWire - 1; t >= 1; t--)
        {
            int count = 0;
            var probe = settings with { TargetSegmentsPerWire = t };
            foreach (var wire in wires) count += SegmentsForWire(wire, probe, out _);
            if (count <= settings.UnknownCeiling) { bestTarget = t; bestCount = count; break; }
        }

        // Remedy 2 — the worst single array, which is what "one array at a time" would cost.
        var perArray = new int[design.Arrays.Count];
        for (int w = 0; w < wires.Count; w++)
            perArray[arrayOfWire[w]] += SegmentsForWire(wires[w], settings, out _);
        int worstArray = perArray.Length == 0 ? 0 : perArray.Max();

        string remedy1 = bestTarget > 0
            ? $"Lowering 'Segments per wire' from {settings.TargetSegmentsPerWire} to {bestTarget} gives {bestCount:N0} " +
              $"(~{WireMomCost.Duration(WireMomCost.SetupSeconds(bestCount))} of setup and " +
              $"~{WireMomCost.Duration(WireMomCost.PerPointSeconds(bestCount))} per frequency point). "
            : $"No 'Segments per wire' value down to 1 fits — even one segment per polyline vertex is " +
              $"{report.Segments:N0} here, so this lever is exhausted. ";

        throw new InvalidOperationException(
            $"This design meshes to {report.Segments:N0} segments (~{report.PredictedPeakMegabytes / 1024.0:F1} GB peak) " +
            $"— above the {settings.UnknownCeiling:N0}-segment ceiling. " +
            remedy1 +
            $"Solving one array at a time gives <= {worstArray:N0}. " +
            $"The wire count itself is the other lever: {wires.Count:N0} wires cannot be solved at " +
            $"{settings.TargetSegmentsPerWire} segments each on this build.");
    }

    // ------------------------------------------------------------------ segmentation

    private static List<Wire> CollectWires(WBondDesign design, out int[] arrayOfWire)
    {
        var wires = new List<Wire>();
        var owner = new List<int>();
        for (int a = 0; a < design.Arrays.Count; a++)
            foreach (var wire in design.Arrays[a].Wires)
            {
                wires.Add(wire);
                owner.Add(a);
            }
        arrayOfWire = [.. owner];
        return wires;
    }

    /// <summary>
    /// <c>maxSegmentLength = pathLength / target</c>, with the target walked down until the wire fits
    /// under <see cref="WireMomSettings.MaxSegmentsPerWire"/>.
    ///
    /// <para>The walk is needed because the count is <c>Σ_i ceil(len_i / maxLen)</c> over the polyline,
    /// not <c>target</c>: a wire whose vertices are unevenly spaced rounds up on every one of them.</para>
    /// </summary>
    private static double MaxSegmentLength(Wire wire, WireMomSettings settings)
    {
        SegmentsForWire(wire, settings, out _, out double maxLen);
        return maxLen;
    }

    private static int SegmentsForWire(Wire wire, WireMomSettings settings, out bool clamped) =>
        SegmentsForWire(wire, settings, out clamped, out _);

    private static int SegmentsForWire(Wire wire, WireMomSettings settings, out bool clamped, out double maxLen)
    {
        double path = wire.PathLengthMetres();
        int target = Math.Max(1, settings.TargetSegmentsPerWire);
        int cap = Math.Max(1, settings.MaxSegmentsPerWire);

        clamped = false;
        maxLen = path / target;

        int count = CountAt(wire, maxLen);
        if (count <= cap) return count;

        clamped = true;
        for (int t = target - 1; t >= 1; t--)
        {
            double probe = path / t;
            int c = CountAt(wire, probe);
            if (c <= cap)
            {
                maxLen = probe;
                return c;
            }
        }

        // One piece per polyline segment is the floor — the mesher never merges two of them, so this
        // is genuinely as coarse as the authored geometry allows.
        maxLen = double.PositiveInfinity;
        return wire.Points.Count - 1;
    }

    private static int CountAt(Wire wire, double maxLen)
    {
        int count = 0;
        for (int i = 1; i < wire.Points.Count; i++)
        {
            ToMetres(wire.Points[i - 1], out double ax, out double ay, out double az);
            ToMetres(wire.Points[i], out double bx, out double by, out double bz);
            count += PiecesFor(ax, ay, az, bx, by, bz, maxLen);
        }
        return count;
    }

    private static int PiecesFor(double ax, double ay, double az, double bx, double by, double bz, double maxLen)
    {
        if (double.IsInfinity(maxLen) || maxLen <= 0.0) return 1;
        double dx = bx - ax, dy = by - ay, dz = bz - az;
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        int pieces = (int)Math.Ceiling(len / maxLen);
        return pieces < 1 ? 1 : pieces;
    }

    /// <summary>
    /// The <i>j</i>-th subdivision point of A → B, with <b>both endpoints reproduced exactly</b>.
    ///
    /// <para>A plain <c>a + (b − a)·t</c> does not return <c>b</c> bit-for-bit at <c>t = 1</c>, and the
    /// subdivision-invariance gate compares against a mesh whose filaments end exactly at the authored
    /// vertices. Special-casing the two ends costs one branch and keeps that gate meaningful.</para>
    /// </summary>
    private static void Lerp(double ax, double ay, double az, double bx, double by, double bz,
                             int j, int pieces, out double x, out double y, out double z)
    {
        if (j == 0) { x = ax; y = ay; z = az; return; }
        if (j == pieces) { x = bx; y = by; z = bz; return; }

        double t = (double)j / pieces;
        x = ax + (bx - ax) * t;
        y = ay + (by - ay) * t;
        z = az + (bz - az) * t;
    }

    private static void ToMetres(in Point3 p, out double x, out double y, out double z)
    {
        x = WBondUnits.ToMetres(p.X);
        y = WBondUnits.ToMetres(p.Y);
        z = WBondUnits.ToMetres(p.Z);
    }

    // ------------------------------------------------------------------ RW17

    /// <summary>
    /// RW17 — warn, do not refuse, when a wire pair's closest centreline approach falls below
    /// <c>s/a = 6</c>. Below that the thin-wire reduced kernel is a few percent optimistic, which is a
    /// stated accuracy limit of the model rather than a fault in the design.
    /// </summary>
    private static List<string> ProximityWarnings(List<Wire> wires, WireMomSettings settings)
    {
        var warnings = new List<string>();
        if (wires.Count < 2) return warnings;

        double maxRadiusNm = 0.0;
        foreach (var wire in wires) maxRadiusNm = Math.Max(maxRadiusNm, wire.DiameterNm / 2.0);

        // Clearance is surface-to-surface, s/a is centreline-to-radius: a pair at s = 6a has a
        // clearance of 4a when the radii match, so 6a is a safe (over-inclusive) broad-phase limit.
        double limitNm = settings.ProximityWarnRatio * maxRadiusNm;

        var sweep = new WirePairSweep(wires, limitNm);
        foreach (var pair in sweep.FindCloserThan(limitNm))
        {
            double approachNm = WireGeometry3D.ClosestApproach(wires[pair.A], wires[pair.B], out _, out _);
            double aNm = Math.Sqrt((wires[pair.A].DiameterNm / 2.0) * (wires[pair.B].DiameterNm / 2.0));
            if (aNm <= 0.0) continue;

            double ratio = approachNm / aNm;
            if (ratio >= settings.ProximityWarnRatio) continue;

            warnings.Add(
                $"wires {pair.A} and {pair.B} approach to {ratio:F1} a; the thin-wire reduced kernel is " +
                $"a few percent optimistic below {settings.ProximityWarnRatio:F0} a.");
        }

        return warnings;
    }
}
