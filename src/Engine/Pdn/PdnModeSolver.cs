// The cavity modes, as a generalised eigenproblem on the loss-free system
// (docs/sonnet-briefs/brief-railrf-15-modes-and-maps.md R-rail15-1, R-rail15-4;
//  docs/design/railrf.md §4.5, §2.4, §7).
//
// ── THE MODES COME OUT OF THE SAME DISCRETISATION, AND THAT IS THE WHOLE POINT ─────────────────
//
// §4.5: "The cavity modes come out of the same discretisation as a generalised eigenproblem on the
// loss-free system, SO THE MODE LIST AND THE SWEEP CANNOT DISAGREE ABOUT THE STRUCTURE."
//
// A separate analytic mode calculator would be a dozen lines and would be wrong the moment the
// board is not a rectangle — a cutout, a split, an antipad field, an L-shaped pour. It would agree
// with the sweep on every board where the answer does not matter and disagree on every board where
// it does. So there is no closed form anywhere in this file: the matrices below are assembled from
// the SAME per-cell C and per-edge L brief 14's extraction stamped into the netlist the sweep
// solves, and the rectangle's f_mn is the ACCEPTANCE CHECK rather than the method.
//
// ── THE ARITHMETIC, IN THREE LINES ─────────────────────────────────────────────────────────────
//
// Drop every loss term (§4.5 says loss-free, and that is what makes the eigenvalues real). What is
// left at each cell is Kirchhoff's current law on the plane pair,
//
//     jωC_i·v_i + Σ_j (v_i − v_j)/(jωL_ij) = 0        →        K v = ω²·C v
//
// with K the graph Laplacian weighted by 1/L and C the diagonal of the cells' own capacitances.
// K is real, symmetric and positive SEMI-definite; C is real, diagonal and positive. So the
// generalised problem reduces EXACTLY — not approximately — to a standard symmetric one:
//
//     A = C^(−1/2) K C^(−1/2),   A u = ω² u,   v = C^(−1/2) u
//
// which is why no generalised eigensolver is called. That matters beyond tidiness: LAPACK's
// generalised routine requires the FIRST matrix to be positive definite and K is not — its null
// space is one vector per connected piece of the plane pair, which is the ω = 0 "mode" of a
// capacitor that nothing is driving. Handing K to it would fail, or worse, succeed on a matrix it
// had silently read only the upper triangle of.
//
// ── IT IS DENSE, ON PURPOSE, AND THE CEILING IS STATED RATHER THAN DISCOVERED ──────────────────
//
// The lowest few eigenpairs of a sparse symmetric pencil are classically found by shift-and-invert
// Lanczos, which is cheaper and has convergence knobs that are wrong in ways that look like
// physics: a mode that has not converged is a mode at the wrong frequency, and nothing about the
// number says so. This solve is one LAPACK call with no tolerance, no restart and no starting
// vector — and <see cref="PdnModeOptions.MaxCells"/> REFUSES rather than running a board it cannot
// hold, naming the cell size as the knob that answers it. See src/Engine/RESOLVED.md for what it
// actually costs on the design note's own 90 x 70 mm board.
//
// ── NOTHING HERE IS A DOMAIN TYPE ──────────────────────────────────────────────────────────────
//
// src/Engine cannot see src/Design (railRF overview §1a). A cell is an int, an edge is two ints and
// a henry, and a field is a double per cell. Mapping a cell back to copper, and drawing it, are
// src/Design's and src/Render's — which is also what keeps this file gateable on the textbook
// rectangle with no artwork anywhere near it.
//
// NUMBERS ARE BASE SI. Hertz, ohms, farads, henries, siemens.

using NumFlat;

namespace CircuitRF.Engine.Pdn;

/// <summary>
/// One edge of the plane pair: two cells and what is between them.
/// </summary>
/// <param name="A">The cell the edge leaves, 0-based.</param>
/// <param name="B">The cell it arrives at.</param>
/// <param name="ResistanceOhms">The LOOP resistance of the edge — both conductors, in series
/// (§4.1's <c>R = 2·Rs</c>). <b>Unread by <see cref="PdnModeSolver"/></b>, which is loss-free by
/// §4.5, and read by <see cref="PdnFieldMap"/>, which is not.</param>
/// <param name="InductanceHenries">The LOOP inductance of the edge — §4.1's <c>L = µ₀·h</c> per
/// square, both conductors together. <b>Not one conductor's half</b>: the extraction splits µ₀h
/// across the two meshes so that the loop carries µ₀h, and a caller that passed one half would put
/// every mode a factor of √2 high.</param>
public readonly record struct PdnCavityEdge(
    int A, int B, double ResistanceOhms, double InductanceHenries);

/// <summary>
/// The plane pair as a network of cells — <b>the one input both the mode solver and the |Z| map
/// read</b>, which is what makes §4.5's "cannot disagree about the structure" true of them too.
/// </summary>
public sealed class PdnCavitySystem
{
    /// <summary>How many cells. Cell indices are 0 … <c>CellCount − 1</c>.</summary>
    public required int CellCount { get; init; }

    /// <summary>The edges. An edge appearing twice is two conductances in parallel, which is what
    /// the netlist says and is never de-duplicated here.</summary>
    public required IReadOnlyList<PdnCavityEdge> Edges { get; init; }

    /// <summary>Each cell's own <c>C = ε₀εᵣΔ²/h</c> to the reference plane, in farads. One entry
    /// per cell, and every entry positive — see <see cref="PdnModeSolver.Solve"/>'s refusals for
    /// why a zero is refused rather than skipped.</summary>
    public required IReadOnlyList<double> CapacitanceFarads { get; init; }

    /// <summary>
    /// Each cell's dielectric loss <c>G = ωC·tan δ</c> AT THE FREQUENCY THIS SYSTEM IS OF, in
    /// siemens, or empty for none.
    /// </summary>
    /// <remarks>
    /// <b>Already evaluated at ω, rather than a tan δ this file would multiply out.</b> The
    /// extraction is for one frequency (§2.8) and re-runs per point, so the caller already holds the
    /// number; taking tan δ here would put one more copy of <c>G = ωC·tan δ</c> in the tool and
    /// invite the two to disagree. Unread by <see cref="PdnModeSolver"/>.
    /// </remarks>
    public IReadOnlyList<double> ConductanceSiemens { get; init; } = [];
}

/// <summary>How many modes, and the ceiling. See <see cref="PdnModeSolver"/>'s header.</summary>
public sealed class PdnModeOptions
{
    /// <summary>How many modes to report, lowest first. Six is §7's own acceptance count.</summary>
    public int Count { get; init; } = 6;

    /// <summary>
    /// The largest system this solver will attempt, in cells.
    /// </summary>
    /// <remarks>
    /// <b>A stated ceiling, set from a measurement rather than from a guess</b> (R-rail15-5). The
    /// solve is dense and cubic, and it was measured on the design note's own board: 800 cells is
    /// 0.7 s and 19 MB, 1,575 is 4.4 s and 74 MB, and §4.1's own 90 × 70 mm at 1.4 mm — 3,200
    /// cells — is <b>36 s and 298 MB</b>. Eight times the time for twice the cells, which is the
    /// n³ the LAPACK call costs.
    ///
    /// <para>4,000 is a little over the note's own board and about 70 s at the top of it. A refusal
    /// that names the cell size is an answer a user can act on; forty minutes and an
    /// <c>OutOfMemoryException</c> is not. src/Engine/RESOLVED.md carries the rest of the finding.</para>
    /// </remarks>
    public int MaxCells { get; init; } = 4_000;
}

/// <summary>
/// One cavity mode: where it resonates, and — <b>the part that makes it actionable</b> — the shape
/// of its field (R-rail15-2).
/// </summary>
/// <param name="Index">0-based, lowest frequency first.</param>
/// <param name="FrequencyHz">Its resonant frequency, <c>√λ / 2π</c>.</param>
/// <param name="Field">
/// The mode's own field, one value per cell, <b>normalised so the largest magnitude is exactly
/// +1</b>.
///
/// <para>Both halves of that normalisation are deliberate and the SIGN half is the one worth
/// stating: an eigensolver's eigenvectors are determined only up to a scale, sign included, so two
/// runs of the same board can legitimately return <c>v</c> and <c>−v</c>. Drawn on a cold-to-hot
/// ramp those are different pictures, and brief 9's clipboard gate and brief 17's figures both
/// require the same input to render the same bytes. So the sign is pinned explicitly, to the cell
/// of largest magnitude (lowest index on a tie), rather than left to LAPACK.</para>
/// </param>
public sealed record PdnMode(int Index, double FrequencyHz, double[] Field);

/// <summary>
/// Everything one mode solve produced. <see cref="Refusal"/> non-null means NOTHING was solved —
/// the contract the rest of this series already states.
/// </summary>
/// <param name="Refusal">Why nothing was solved, or null.</param>
/// <param name="Modes">The modes, lowest frequency first. <b>The ω = 0 family is not among
/// them</b>: a plane pair with nothing driving it has one zero eigenvalue per connected piece —
/// that is a capacitor, not a resonance.</param>
/// <param name="Pieces">How many galvanically separate pieces the plane pair is, which is the
/// number of zero modes that were dropped. More than one is a split plane, and it is stated rather
/// than folded away.</param>
/// <param name="Notes">What the solve established that the caller did not state.</param>
public sealed record PdnModeSet(
    string? Refusal,
    IReadOnlyList<PdnMode> Modes,
    int Pieces,
    IReadOnlyList<string> Notes)
{
    internal static PdnModeSet Refused(string why) => new(why, [], 0, []);
}

/// <summary>§4.5's eigenproblem. Numerics only — see this file's header.</summary>
public static class PdnModeSolver
{
    /// <summary>
    /// Solves <paramref name="system"/>'s loss-free modes, or refuses and says why.
    /// </summary>
    public static PdnModeSet Solve(PdnCavitySystem system, PdnModeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        var opt = options ?? new PdnModeOptions();

        int n = system.CellCount;
        if (n <= 0)
            return PdnModeSet.Refused(
                "This plane pair meshed to no cells at all, so it has no modes. The two conductors " +
                "of the rail and its reference nowhere overlap — check the reference layer.");

        if (system.CapacitanceFarads.Count != n)
            return PdnModeSet.Refused(
                $"The cavity states {n} cells and {system.CapacitanceFarads.Count} capacitances. " +
                "Every cell carries its own ε₀εᵣΔ²/h and the two lists are the same list.");

        if (n > opt.MaxCells)
            return PdnModeSet.Refused(
                $"This plane pair is {n:N0} cells and the mode solve is dense — cubic in the cell " +
                $"count — so it stops at {opt.MaxCells:N0}, which is already about a minute. The " +
                "knob is the CELL SIZE: state a coarser one, or find the modes against a lower band " +
                "top, since λ/20 at half the frequency is twice the cell and a quarter of the cells.");

        // A cell with no capacitance has no mass in K v = ω²C v, so its own mode is at infinity and
        // C^(−1/2) is not a number. It is refused rather than dropped: a cell the extraction
        // produced and this solver silently removed is a hole in the cavity nobody drew.
        var c = new double[n];
        for (int i = 0; i < n; i++)
        {
            c[i] = system.CapacitanceFarads[i];
            if (!(c[i] > 0) || double.IsInfinity(c[i]))
                return PdnModeSet.Refused(
                    $"Cell {i} of this plane pair carries no capacitance to the reference plane, so " +
                    "the loss-free system has no finite mode there. Every meshed cell is a piece of " +
                    "plane pair and every piece of plane pair has ε₀εᵣΔ²/h — this is an extraction " +
                    "defect rather than a board one.");
        }

        // ── K, and the pieces its null space counts ────────────────────────────────────────────
        var a = new Mat<double>(n, n);
        var sqrtC = new double[n];
        for (int i = 0; i < n; i++) sqrtC[i] = Math.Sqrt(c[i]);

        var pieces = new UnionFind(n);
        int edges = 0;

        foreach (var e in system.Edges)
        {
            if (e.A == e.B) continue;
            if (e.A < 0 || e.A >= n || e.B < 0 || e.B >= n)
                return PdnModeSet.Refused(
                    $"An edge of this plane pair joins cells {e.A} and {e.B}, and the cavity has " +
                    $"{n}. The edge list and the cell list came from different meshes.");

            if (!(e.InductanceHenries > 0) || double.IsInfinity(e.InductanceHenries)) continue;

            double w = 1.0 / e.InductanceHenries;

            // The Laplacian, scaled as it is built: A = C^(−1/2) K C^(−1/2) entry by entry, so the
            // n × n K is never materialised beside the n × n A.
            a[e.A, e.A] += w / c[e.A];
            a[e.B, e.B] += w / c[e.B];
            double off = -w / (sqrtC[e.A] * sqrtC[e.B]);
            a[e.A, e.B] += off;
            a[e.B, e.A] += off;

            pieces.Union(e.A, e.B);
            edges++;
        }

        if (edges == 0)
            return PdnModeSet.Refused(
                "This plane pair has no inductive path between any two cells, so there is nothing " +
                "for a wave to travel on and no mode to find. That is what a DC extraction looks " +
                "like — §4.1's L vanishes at ω = 0 — so solve the modes at the top of the band " +
                "instead.");

        int pieceCount = pieces.Count();

        // ── the one LAPACK call ────────────────────────────────────────────────────────────────
        Vec<double> d;
        Mat<double> v;
        try
        {
            var evd = MatrixDecompositions.Evd(a);
            d = evd.D;
            v = evd.V;
        }
        catch (MatrixFactorizationException ex)
        {
            return PdnModeSet.Refused(
                "The cavity eigenproblem did not factorise: " + ex.Message);
        }

        // Ascending is what LAPACK's symmetric driver returns, and sorting is what makes that a
        // property of this file rather than of a library version.
        var order = Enumerable.Range(0, n).OrderBy(i => d[i]).ToArray();

        var modes = new List<PdnMode>(Math.Max(0, opt.Count));
        var notes = new List<string>();

        for (int k = pieceCount; k < n && modes.Count < opt.Count; k++)
        {
            double lambda = d[order[k]];
            if (!(lambda > 0)) continue;           // numerical dust at the bottom of the null space

            double f = Math.Sqrt(lambda) / (2.0 * Math.PI);
            modes.Add(new PdnMode(modes.Count, f, FieldOf(v, order[k], sqrtC, n)));
        }

        if (pieceCount > 1)
            notes.Add(
                $"This plane pair is {pieceCount} galvanically separate pieces, so it has " +
                $"{pieceCount} modes at zero rather than one. They are the pieces' own static " +
                "capacitances and are not resonances; the frequencies below are everything above " +
                "them.");

        if (modes.Count < opt.Count)
            notes.Add(
                $"{modes.Count} mode(s) were asked for out of {opt.Count} and that is all this mesh " +
                "has: a cavity of N cells has N − pieces of them, and a coarse mesh simply does not " +
                "carry the higher ones.");

        return new PdnModeSet(null, modes, pieceCount, notes);
    }

    /// <summary>
    /// One eigenvector, back out of the <c>C^(−1/2)</c> scaling and normalised — <b>sign
    /// included</b>, see <see cref="PdnMode.Field"/>.
    /// </summary>
    private static double[] FieldOf(Mat<double> v, int column, double[] sqrtC, int n)
    {
        var field = new double[n];
        for (int i = 0; i < n; i++) field[i] = v[i, column] / sqrtC[i];

        int peak = 0;
        double best = Math.Abs(field[0]);
        for (int i = 1; i < n; i++)
        {
            double m = Math.Abs(field[i]);
            if (m > best) { best = m; peak = i; }
        }

        if (!(best > 0)) return field;

        double scale = 1.0 / (field[peak] < 0 ? -best : best);
        for (int i = 0; i < n; i++) field[i] *= scale;

        // Exactly +1 at the peak, rather than 1 ± an ulp: the map's ramp and every per-port ratio
        // are read against it.
        field[peak] = 1.0;
        return field;
    }

    /// <summary>Connected pieces of the edge graph — the dimension of K's null space, which is
    /// exactly how many zero modes there are to skip.</summary>
    private sealed class UnionFind(int n)
    {
        private readonly int[] _parent = [.. Enumerable.Range(0, n)];

        public int Find(int x)
        {
            while (_parent[x] != x) x = _parent[x] = _parent[_parent[x]];
            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) _parent[ra] = rb;
        }

        public int Count()
        {
            int roots = 0;
            for (int i = 0; i < _parent.Length; i++) if (Find(i) == i) roots++;
            return roots;
        }
    }
}
