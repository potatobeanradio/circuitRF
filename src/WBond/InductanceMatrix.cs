using System.Threading.Tasks;

namespace CircuitRF.WBond;

/// <summary>
/// A design flattened into the flat, SI-unit arrays the fill actually walks.
///
/// <para>The object graph (<see cref="WBondDesign"/> → <see cref="WireArray"/> → <see cref="Wire"/>
/// → <see cref="Point3"/>) is the editing model; it is converted to filaments <b>once</b> and the
/// pair loop never touches it again. Every per-filament invariant — direction, length, radius — is
/// computed here (brief-wbond-wba §3).</para>
/// </summary>
public sealed class WireMesh
{
    private WireMesh(
        WBondDesign design,
        Filament[] filaments,
        Filament[] images,
        int[] wireStart,
        int[] wireLength,
        Wire[] wires,
        int[] arrayOfWire,
        string[] arrayNames,
        bool hasImages)
    {
        Design = design;
        Filaments = filaments;
        Images = images;
        WireStart = wireStart;
        WireLength = wireLength;
        Wires = wires;
        ArrayOfWire = arrayOfWire;
        ArrayNames = arrayNames;
        HasImages = hasImages;
    }

    /// <summary>
    /// The design this mesh was flattened from — <b>held live, not copied</b>.
    ///
    /// <para>The geometry in here is a snapshot (see <see cref="RefreshWire"/> for what that costs),
    /// but the design's scalar SETTINGS are not: <see cref="WBondDesign.OvermoldEr"/> is read at fill
    /// time by <see cref="PotentialCoefficients.Fill"/>, so changing the permittivity and refilling
    /// needs no rebuild and cannot go stale. That is the same relationship
    /// <c>Mom.WireMomMesh.Design</c> already has.</para>
    /// </summary>
    public WBondDesign Design { get; }

    /// <summary>All filaments, grouped by wire.</summary>
    public Filament[] Filaments { get; }

    /// <summary>The ground-plane image of each filament, index-parallel to <see cref="Filaments"/>.</summary>
    public Filament[] Images { get; }

    /// <summary>Index of each wire's first filament.</summary>
    public int[] WireStart { get; }

    /// <summary>Number of filaments in each wire.</summary>
    public int[] WireLength { get; }

    public Wire[] Wires { get; }

    /// <summary>The array index each wire belongs to — the mapping matrix <b>A</b>, in compact form.</summary>
    public int[] ArrayOfWire { get; }

    public string[] ArrayNames { get; }

    public bool HasImages { get; }

    public int WireCount => WireStart.Length;

    public int ArrayCount => ArrayNames.Length;

    public int FilamentCount => Filaments.Length;

    /// <summary>
    /// Re-flattens one wire after its points have moved — the mesh half of the drag path.
    ///
    /// <para><b>The mesh is a snapshot, not a view.</b> <see cref="Build"/> copies the polylines into
    /// flat SI arrays and the pair loop never touches the model again, which is what makes the fill
    /// fast. The cost of that is exactly this method: mutating a <see cref="Wire"/> does <i>not</i>
    /// update the mesh, so the incremental path must say which wire changed. Forgetting to is a
    /// silent staleness bug, so <see cref="IncrementalFill"/> calls this itself rather than trusting
    /// callers to.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The wire's point count changed. The flat layout assigns each wire a fixed span of the filament
    /// array, so adding or removing a point needs a full <see cref="Build"/> — which is correct, and
    /// far better than silently reading a neighbouring wire's filaments.
    /// </exception>
    public void RefreshWire(int wire)
    {
        var w = Wires[wire];
        int expected = w.Points.Count - 1;

        if (expected != WireLength[wire])
            throw new InvalidOperationException(
                $"Wire {wire} now has {w.Points.Count} points ({expected} filaments) but the mesh was built " +
                $"for {WireLength[wire]}. A point-count change needs a full WireMesh.Build, not a refresh.");

        double radius = w.RadiusMetres;
        int at = WireStart[wire];

        for (int i = 1; i < w.Points.Count; i++)
        {
            var a = w.Points[i - 1];
            var b = w.Points[i];
            var filament = Filament.FromEndpoints(
                WBondUnits.ToMetres(a.X), WBondUnits.ToMetres(a.Y), WBondUnits.ToMetres(a.Z),
                WBondUnits.ToMetres(b.X), WBondUnits.ToMetres(b.Y), WBondUnits.ToMetres(b.Z),
                radius);

            Filaments[at] = filament;
            if (HasImages) Images[at] = filament.Image();
            at++;
        }
    }

    /// <summary>
    /// Flattens a design. Validates first, so structural problems are reported against the model
    /// rather than surfacing later as a linear-algebra failure.
    /// </summary>
    public static WireMesh Build(WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        design.Validate();

        var wires = new List<Wire>();
        var arrayOf = new List<int>();
        var arrayNames = new string[design.Arrays.Count];

        for (int ai = 0; ai < design.Arrays.Count; ai++)
        {
            arrayNames[ai] = design.Arrays[ai].Name;
            foreach (var wire in design.Arrays[ai].Wires)
            {
                wires.Add(wire);
                arrayOf.Add(ai);
            }
        }

        int wireCount = wires.Count;
        var wireStart = new int[wireCount];
        var wireLength = new int[wireCount];

        int total = 0;
        for (int w = 0; w < wireCount; w++)
        {
            wireStart[w] = total;
            wireLength[w] = wires[w].Points.Count - 1;
            total += wireLength[w];
        }

        var filaments = new Filament[total];
        for (int w = 0; w < wireCount; w++)
        {
            var wire = wires[w];
            double radius = wire.RadiusMetres;
            int at = wireStart[w];

            for (int i = 1; i < wire.Points.Count; i++)
            {
                var a = wire.Points[i - 1];
                var b = wire.Points[i];
                filaments[at++] = Filament.FromEndpoints(
                    WBondUnits.ToMetres(a.X), WBondUnits.ToMetres(a.Y), WBondUnits.ToMetres(a.Z),
                    WBondUnits.ToMetres(b.X), WBondUnits.ToMetres(b.Y), WBondUnits.ToMetres(b.Z),
                    radius);
            }
        }

        bool hasImages = design.GroundPlane.Enabled;
        var images = hasImages ? new Filament[total] : [];
        if (hasImages)
        {
            for (int i = 0; i < total; i++)
                images[i] = filaments[i].Image();
        }

        return new WireMesh(
            design, filaments, images, wireStart, wireLength,
            [.. wires], [.. arrayOf], arrayNames, hasImages);
    }
}

/// <summary>
/// The wire-basis inductance matrix <b>L</b> (wbond.md §3), assembled from Grover filament pairs
/// with the ground-plane image folded in.
///
/// <para><b>The fill is the bottleneck, not the solve (WB13).</b> Measured on this machine: a cold
/// 600-wire fill is ~0.54 s while a Cholesky factorisation plus twelve solves at N = 600 is 22.9 ms.
/// That inverts the usual intuition about a 600 × 600 matrix problem, and it is why the caching
/// effort belongs here.</para>
/// </summary>
public sealed class InductanceMatrix
{
    private readonly double[] _l;

    private InductanceMatrix(double[] l, int n)
    {
        _l = l;
        Order = n;
    }

    /// <summary>N, the number of wires.</summary>
    public int Order { get; }

    /// <summary>L[i,j] in henries. Symmetric.</summary>
    public double this[int i, int j] => _l[i * Order + j];

    /// <summary>The backing row-major store, for the linear-algebra layer.</summary>
    public double[] Values => _l;

    /// <summary>
    /// Wraps an already-assembled symmetric matrix, row-major.
    ///
    /// <para>The array is taken by reference, not copied — this is the entry point for a matrix that
    /// came from somewhere other than <see cref="Fill"/> (a cached fill, a reduction test, or a
    /// higher-fidelity kernel), so the caller keeps ownership of the storage the incremental path
    /// will keep updating.</para>
    /// </summary>
    public static InductanceMatrix FromDense(double[] rowMajor, int n)
    {
        ArgumentNullException.ThrowIfNull(rowMajor);
        if (rowMajor.Length < n * n)
            throw new ArgumentException(
                $"Expected at least {n * n} values for a {n} x {n} matrix, got {rowMajor.Length}.",
                nameof(rowMajor));

        return new InductanceMatrix(rowMajor, n);
    }

    /// <summary>
    /// The mutual inductance between two wires, summed over every ordered filament pair and each
    /// filament's image.
    ///
    /// <para><c>L_ij = Σ_p Σ_q [ M(p, q) + M(p, Image(q)) ]</c> — the image contribution is
    /// <b>added</b> because the reversal is already carried by the image filament's direction
    /// (<see cref="Filament.Image"/>).</para>
    /// </summary>
    public static double Block(WireMesh mesh, int wi, int wj)
    {
        // CANONICAL ORDER. Mutual inductance is symmetric, but the double sum over filament pairs is
        // NOT bit-symmetric: Block(i,j) and Block(j,i) accumulate the same terms in a different order
        // and differ in the last bits. Fill computes the upper triangle, so every other caller must
        // too, or the incremental path stops being bit-identical to a rebuild (tier 7).
        if (wi > wj) (wi, wj) = (wj, wi);

        var filaments = mesh.Filaments;
        var images = mesh.Images;
        bool hasImages = mesh.HasImages;

        int pStart = mesh.WireStart[wi], pEnd = pStart + mesh.WireLength[wi];
        int qStart = mesh.WireStart[wj], qEnd = qStart + mesh.WireLength[wj];

        double acc = 0.0;
        for (int p = pStart; p < pEnd; p++)
        {
            ref readonly var fp = ref filaments[p];
            for (int q = qStart; q < qEnd; q++)
            {
                acc += Grover.Mutual(in fp, in filaments[q]);
                if (hasImages)
                    acc += Grover.Mutual(in fp, in images[q]);
            }
        }

        return acc;
    }

    /// <summary>
    /// The direct (non-image) half of a wire-pair block.
    ///
    /// <para>Split out for <see cref="IncrementalFill"/>'s rigid-motion invariance (R-wb-10): under a
    /// rigid translation the direct mutuals within the moving selection are unchanged, but the image
    /// mutuals are only unchanged if the translation is <b>horizontal</b> — the images move with the
    /// selection then, and do not when z changes. Exploiting that needs the two halves
    /// separable.</para>
    /// </summary>
    public static double BlockDirect(WireMesh mesh, int wi, int wj)
    {
        if (wi > wj) (wi, wj) = (wj, wi);   // canonical order — see Block

        var filaments = mesh.Filaments;
        int pStart = mesh.WireStart[wi], pEnd = pStart + mesh.WireLength[wi];
        int qStart = mesh.WireStart[wj], qEnd = qStart + mesh.WireLength[wj];

        double acc = 0.0;
        for (int p = pStart; p < pEnd; p++)
        {
            ref readonly var fp = ref filaments[p];
            for (int q = qStart; q < qEnd; q++)
                acc += Grover.Mutual(in fp, in filaments[q]);
        }

        return acc;
    }

    /// <summary>The ground-plane image half of a wire-pair block. Zero when the plane is disabled.</summary>
    public static double BlockImage(WireMesh mesh, int wi, int wj)
    {
        if (!mesh.HasImages) return 0.0;
        if (wi > wj) (wi, wj) = (wj, wi);   // canonical order — see Block

        var filaments = mesh.Filaments;
        var images = mesh.Images;
        int pStart = mesh.WireStart[wi], pEnd = pStart + mesh.WireLength[wi];
        int qStart = mesh.WireStart[wj], qEnd = qStart + mesh.WireLength[wj];

        double acc = 0.0;
        for (int p = pStart; p < pEnd; p++)
        {
            ref readonly var fp = ref filaments[p];
            for (int q = qStart; q < qEnd; q++)
                acc += Grover.Mutual(in fp, in images[q]);
        }

        return acc;
    }

    /// <summary>Overwrites one entry and its symmetric partner.</summary>
    internal void Set(int i, int j, double value)
    {
        _l[i * Order + j] = value;
        _l[j * Order + i] = value;
    }

    /// <summary>
    /// Assembles the full matrix. Only the upper triangle is computed; <b>L</b> is symmetric because
    /// mutual inductance is.
    /// </summary>
    /// <param name="parallel">
    /// Fill blocks concurrently. The pair loop is a pure map over independent wire pairs, so this is
    /// safe by construction and is the largest single win available at large N.
    /// </param>
    public static InductanceMatrix Fill(WireMesh mesh, bool parallel = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int n = mesh.WireCount;
        var l = new double[n * n];

        if (parallel)
        {
            Parallel.For(0, n, wi =>
            {
                for (int wj = wi; wj < n; wj++)
                {
                    double v = Block(mesh, wi, wj);
                    l[wi * n + wj] = v;
                    l[wj * n + wi] = v;
                }
            });
        }
        else
        {
            for (int wi = 0; wi < n; wi++)
            {
                for (int wj = wi; wj < n; wj++)
                {
                    double v = Block(mesh, wi, wj);
                    l[wi * n + wj] = v;
                    l[wj * n + wi] = v;
                }
            }
        }

        return new InductanceMatrix(l, n);
    }

    /// <summary>
    /// Recomputes the row and column of one wire in place — the incremental drag path (R-wb-9).
    ///
    /// <para>Moving wire <i>k</i> changes exactly row <i>k</i> and column <i>k</i>: 2N−1 blocks
    /// instead of N², and the change is <b>rank 2 whatever N is</b>. Measured at N = 600 this is
    /// ~3.6 ms against ~0.54 s for a cold fill.</para>
    /// </summary>
    public void RefreshWire(WireMesh mesh, int k)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        int n = Order;
        for (int j = 0; j < n; j++)
        {
            double v = Block(mesh, k, j);
            _l[k * n + j] = v;
            _l[j * n + k] = v;
        }
    }
}
