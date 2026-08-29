// brief-em-p1-honest-memory-accounting.md — what one frequency point of the planar solver actually
// holds, measured, against what the code said it held.
//
// The three ceiling refusals and the AIM report all quoted counts that were RIGHT about the thing
// they named and SILENT about everything beside it: 16·N² is exactly the dense matrix, and the dense
// matrix is about a third of a dense frequency point; the AIM report counted its own arrays and not
// the sparse LU it builds from them. P1 changes no arithmetic anywhere — it measures, and it re-words.
//
// TWO INSTRUMENTS, and they answer different questions, which is why both are here:
//
//   GC.GetTotalMemory(true)     — LIVE managed bytes at a phase boundary. Exact for what is still
//                                 reachable, blind to a transient that has already been released.
//   Process.PeakWorkingSet64    — what the OS committed at the high-water mark of the whole run. Sees
//                                 the transients, and also the JIT, the runtime and every earlier
//                                 test in the process, so it is a CEILING on the point's cost and a
//                                 cross-check on the arithmetic — never a second measurement of it.
//
// A trap this file is written around, and it cost an afternoon: the LARGE OBJECT HEAP does not
// compact by default, so a freed n×n matrix leaves committed space that the next GetTotalMemory
// reads back as still-allocated OR silently absorbs the next allocation into. A ladder that measures
// several N in one process therefore reports the SECOND rung's matrix at half its size and the
// third's factorisation at four times its live cost. Every measurement below takes its own baseline
// immediately before the phase it measures and asks for LOH compaction at every boundary.

using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PlanarMemoryAccountingTests
{
    private readonly ITestOutputHelper _out;
    public PlanarMemoryAccountingTests(ITestOutputHelper output) => _out = output;

    private static string Mb(long bytes) => $"{bytes / 1048576.0:N1}";

    /// <summary>Live managed bytes, with the LOH compacted first — see the file header for why the
    /// compaction is not optional here.</summary>
    private static long Live()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    // =========================================================================================
    // Milestone 1 — one DENSE frequency point, split by phase
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]   // three fills and three factorisations, the largest at N ≈ 4,800
    public void P1_1_OneDenseFrequencyPoint_ResidentPeak_SplitByPhase()
    {
        var slab = GroundedSlab.Fr4Starter;

        // 20 / 80 / 200 mm — the hero, the brief's own middle rung, and the largest line that still
        // fits under R17's ceiling. N grows at a flat 23.8 unknowns per mm on this cross-section at the
        // shipping mesh, so the three land where the brief asked for them.
        var counted = new List<(int N, int Cells, long Cores, long Matrix, long Lu, long P)>();

        _out.WriteLine("TABLE 1 — COUNTED. Every term is an array this code allocates, at its element");
        _out.WriteLine("size; nothing here is a profile or an estimate. 'quoted' is the 16·N² the three");
        _out.WriteLine("ceiling refusals printed until P1.");
        _out.WriteLine("");
        _out.WriteLine("P7 (2026-08-29) changed one column and one total: the factorisation is now IN");
        _out.WriteLine("PLACE, so 'L+U' is what the pre-P7 general LU held BESIDE the matrix and is no");
        _out.WriteLine("longer part of the peak — what is, is 16n of diagonal.");
        _out.WriteLine("");
        _out.WriteLine("      N  cells    cores      matrix   L+U (pre-P7)   P (transient)      quoted   " +
                       "RESIDENT PEAK   x quoted");

        var meshes = new List<PlanarMesh>();
        foreach (double lengthM in new[] { 20e-3, 80e-3, 200e-3 })
        {
            var report = SurfaceMesher.Mesh(PlanarLineFixtures.Fr4Line(lengthM, 10e9),
                                            PlanarLineFixtures.Shipping);
            Assert.Null(report.Refusal);
            meshes.Add(report.Mesh);

            int n = report.Mesh.Bases.Count, cells = report.Mesh.Cells.Count;
            long cores  = PlanarSystem.CoreBytes(n, cells);
            long matrix = PlanarSystem.MatrixBytes(n);
            long lu     = PlanarSystem.LuFactorBytes(n);   // pre-P7, for the column above
            long pMat   = 16L * cells * cells;
            counted.Add((n, cells, cores, matrix, lu, pMat));

            long peak = PlanarSystem.ResidentBytes(n, cells);
            _out.WriteLine($"  {n,5}  {cells,5}  {Mb(cores),7}  {Mb(matrix),10}  {Mb(lu),8}  " +
                           $"{Mb(pMat),14}  {Mb(matrix),10}  {Mb(peak),13}  {(double)peak / matrix,9:F2}x");
        }

        // ── TABLE 2, the measurement ─────────────────────────────────────────────────────────
        //
        // CUMULATIVE from one baseline, and paired with GC.GetTotalAllocatedBytes, because a
        // per-phase live DELTA is not trustworthy here and the reason is structural rather than
        // statistical: the fill allocates the m×m scalar-potential matrix P and releases it, and the
        // LU allocates about 0.6 of a matrix of scratch and releases that, and the large object heap
        // hands the freed space to the NEXT phase instead of returning it. A per-phase delta therefore
        // reads the matrix LOW (it lands in P's grave) and the factorisation HIGH. The cumulative
        // total and the allocation counter are both exact, and between them they say what was kept
        // and what merely passed through.
        _out.WriteLine("");
        _out.WriteLine("TABLE 2 — MEASURED, cumulative live from one baseline, with the allocation");
        _out.WriteLine("counter beside it. 'allocated' includes every transient the phase released.");
        _out.WriteLine("");
        _out.WriteLine("      N     live after      live after      live after      alloc by   " +
                       "alloc by    counted peak   live/counted");
        _out.WriteLine("                 cores            fill              LU          fill         LU");

        foreach (var (mesh, c) in meshes.Zip(counted))
        {
            var kernel = PlanarLineFixtures.Kernel(slab, 10e9);
            double omega = 2.0 * Math.PI * 10e9;

            long b0 = Live();
            var cores = PlanarFill.BuildCores(mesh);
            long afterCores = Live() - b0;

            var pair = kernel.For(cores, PlanarFillSettings.Default.Order);

            long allocBeforeFill = GC.GetTotalAllocatedBytes(true);
            var system = PlanarSystem.Build(cores, pair.VectorPotential, pair.Scalar, omega);
            long allocFill = GC.GetTotalAllocatedBytes(true) - allocBeforeFill;
            long afterFill = Live() - b0;

            long allocBeforeLu = GC.GetTotalAllocatedBytes(true);
            system.Factor();                                   // P7's in-place LDLᵀ, the shipped path
            long allocLu = GC.GetTotalAllocatedBytes(true) - allocBeforeLu;
            long afterLu = Live() - b0;

            GC.KeepAlive(system);
            GC.KeepAlive(cores);

            long peak = PlanarSystem.ResidentBytes(c.N, c.Cells);
            _out.WriteLine($"  {c.N,5}  {Mb(afterCores),13}  {Mb(afterFill),14}  {Mb(afterLu),14}  " +
                           $"{Mb(allocFill),12}  {Mb(allocLu),9}  {Mb(peak),14}  " +
                           $"{(double)afterLu / peak,12:F2}");
        }

        // ── the transient P, measured on its own ─────────────────────────────────────────────
        // At the smallest rung only, in a clean sub-sequence with nothing freed before it, which is
        // the one place a per-phase live delta IS trustworthy.
        {
            var mesh = meshes[0];
            var cores = PlanarFill.BuildCores(mesh);
            var pair = PlanarLineFixtures.Kernel(slab, 10e9).For(cores, PlanarFillSettings.Default.Order);
            long b0 = Live();
            var p = PlanarFill.ScalarPotentialMatrix(cores, pair.Scalar);
            long livedP = Live() - b0;
            GC.KeepAlive(p);
            _out.WriteLine("");
            _out.WriteLine($"The transient m×m P at N = {mesh.Bases.Count} " +
                           $"(m = {mesh.Cells.Count}): counted {Mb(counted[0].P)} MB, measured " +
                           $"{Mb(livedP)} MB. It is allocated by the fill and released before the " +
                           "factorisation, so it is real and it is NOT the peak.");
        }

        var proc = Process.GetCurrentProcess();
        _out.WriteLine("");
        _out.WriteLine($"Process working set at the end: {Mb(proc.WorkingSet64)} MB; peak " +
                       $"{Mb(proc.PeakWorkingSet64)} MB (0 means the platform does not track it — " +
                       "macOS does not). Either way it carries the runtime, the JIT and every earlier " +
                       "test in this process, so it bounds the table above rather than reproducing it.");
        _out.WriteLine("");
        _out.WriteLine("WHAT THIS SETTLES. The '4×' in the scratch measurement the brief opens with is");
        _out.WriteLine("CONFIRMED in kind and pinned at 3.52× in number, flat across the three sizes");
        _out.WriteLine("(3.39× since P2 took one of the three vector core triangles out):");
        _out.WriteLine("the resident peak of one dense point is one matrix, TWO further full matrices");
        _out.WriteLine("for the factors (NumFlat's L and U are separate Mat<Complex> of stride n — this");
        _out.WriteLine("is not a packed in-place LU) and the cached cores at just over half a matrix.");
        _out.WriteLine("The scratch program's own 530 MB for a 137 MB matrix — 3.87× — was the LIVE");
        _out.WriteLine("figure with the factorisation's released scratch still committed on the LOH;");
        _out.WriteLine("what is genuinely RETAINED is 2× the matrix, and the extra ~0.6× is real but");
        _out.WriteLine("transient. Both matter to a machine; only the first belongs in a refusal.");

        int ceiling = SurfaceMesher.UnknownCeiling;
        long shipped = PlanarSystem.ResidentBytes(ceiling);
        long preP7   = PlanarSystem.MatrixBytes(ceiling) + PlanarSystem.LuFactorBytes(ceiling)
                     + PlanarSystem.CoreBytes(ceiling);

        // The cores are real and must still be counted — 16·N² alone was never the honest number.
        Assert.True(shipped > PlanarSystem.MatrixBytes(ceiling),
            "the cached cores are part of a frequency point and the peak has to say so");

        // And P7's own claim: taking both factor matrices out more than halves the peak.
        Assert.True(2 * shipped < preP7,
            $"P7 removed L and U from the peak; if {Mb(shipped)} MB is not less than half of the " +
            $"{Mb(preP7)} MB the general LU held, it did not");
    }

    // =========================================================================================
    // Milestone 2 — one ACCELERATED frequency point, split by part
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]   // two AIM builds, the larger at the accelerated ceiling
    public void P1_2_OneAcceleratedFrequencyPoint_ResidentPeak_SplitByPart()
    {
        _out.WriteLine("One ACCELERATED frequency point, FR-4 hero cross-section at 6 GHz, shipping");
        _out.WriteLine("mesh — M5's own top rung (256 mm, N = 3,731) and the accelerated ceiling");
        _out.WriteLine("(832 mm). Nothing built here is released until the test ends: the large object");
        _out.WriteLine("heap hands a freed operator's space to the next one, and a second rung measured");
        _out.WriteLine("after a first was collected reads ~40% low.");
        _out.WriteLine("");
        _out.WriteLine("MEASURE THIS ONE ALONE. GC.GetTotalMemory is PROCESS-wide and xUnit runs test");
        _out.WriteLine("classes concurrently, so another class's collection landing inside the window");
        _out.WriteLine("moves the ratio without bound — it read -0.245 once, in a full-suite run. The");
        _out.WriteLine("load-immune form of the same gate is P1_3, which walks the operator's own");
        _out.WriteLine("object graph instead of asking the process how much memory it has.");
        _out.WriteLine("");
        _out.WriteLine("   label       N  near/row   stencils   grid+FFT   near CSR    L+U MB   " +
                       "REPORTED   measured   ratio");

        // Every operator (and the cores behind it) stays reachable — see the note above.
        var kept = new List<object>();
        var measuredBytes = new List<long>();

        foreach (double lenMm in new[] { 256.0, 832.0 })
        {
            var problem = PlanarLineFixtures.Fr4Line(lenMm * 1e-3, 6e9);
            var report  = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping, accelerated: true);
            var mesh    = report.Mesh;
            int n       = mesh.Bases.Count;

            var geom = PlanarFill.BuildGeometryOnlyCores(mesh);
            var pair = PlanarLineFixtures.Kernel(problem.Slab, 6e9)
                                         .For(geom, PlanarFillSettings.Default.Order);
            kept.Add(mesh); kept.Add(geom);

            long b0 = Live();
            var aim = PlanarAimOperator.Build(geom, pair.VectorPotential, pair.Scalar,
                                              2.0 * Math.PI * 6e9);
            long measured = Live() - b0;
            kept.Add(aim);
            measuredBytes.Add(measured);

            var r = aim.Report;
            // P6: 16 B per stencil node (double), and the whole geometry — stencils, CSR index, mirror
            // and the near cores' store — is one term, built once per mesh.
            long stencils = r.GeometryBytes;
            long gridFft  = 16L * r.GridNodesX * r.GridNodesY * 2 + 16L * r.PaddedGridNodes * 5;
            long nearCsr  = 20L * r.NearEntries + 4L * (r.UnknownCount + 1);
            long luBytes  = 20L * r.FactorNonZeros + 8L * (r.UnknownCount + 1);

            _out.WriteLine($"  {lenMm,5:F0} mm {n,7}  {r.NearEntriesPerRow,8:F0}   " +
                           $"{Mb(stencils),8}   {Mb(gridFft),8}   {Mb(nearCsr),8}   {Mb(luBytes),7}   " +
                           $"{Mb(r.ResidentBytes),8}   {Mb(measured),8}   " +
                           $"{(double)r.ResidentBytes / measured,5:F2}");
        }

        // ── what the OLD accounting said, against what was actually there ────────────────────
        _out.WriteLine("");
        _out.WriteLine("   label       N   near nnz    L+U nnz   old ApproximateBytes   honest, " +
                       "_nearExact KEPT   honest, RELEASED (ships)");
        foreach (var o in kept.OfType<PlanarAimOperator>())
        {
            var r = o.Report;
            long old = 36L * r.NearEntries
                     + 16L * r.GridNodesX * r.GridNodesY * 2
                     + 16L * r.PaddedGridNodes * 5
                     + 32L * (r.ProjectionOrder + 1) * (r.ProjectionOrder + 1) * r.UnknownCount;
            long keptExact = r.ResidentBytes + 16L * r.NearEntries;
            _out.WriteLine($"  {"",5}    {r.UnknownCount,7}  {r.PreconditionerNonZeros,9:N0}  " +
                           $"{r.FactorNonZeros,9:N0}   {Mb(old),20}   {Mb(keptExact),21}   " +
                           $"{Mb(r.ResidentBytes),22}");
        }

        _out.WriteLine("");
        _out.WriteLine("WHAT THIS SETTLES.");
        _out.WriteLine("");
        _out.WriteLine("1. The report's omission was the SPARSE LU, not the near field. Its fill-in is");
        _out.WriteLine("   the near matrix's own nnz again and a bit more, at 20 B an entry, and at the");
        _out.WriteLine("   ceiling that is nearly half of everything the accelerator holds.");
        _out.WriteLine("2. 'PreconditionerNonZeros' was NEVER the fill-in. It is csc.NonZerosCount —");
        _out.WriteLine("   the near ENTRY count again — under a name that reads like the factor's.");
        _out.WriteLine("   FactorNonZeros (SparseLU.NonZerosCount, L and U together) is the new one.");
        _out.WriteLine("3. Releasing _nearExact after the factorisation is what keeps CLAUDE.md §8's");
        _out.WriteLine("   'the accelerator's own working set stays under 200 MB even at that ceiling'");
        _out.WriteLine("   TRUE. Counted honestly and with the exact entries still held, it was not:");
        _out.WriteLine("   the two columns above are the same operator with and without P1's free.");
        _out.WriteLine("4. AIM still wins the memory comparison it was built to win, and against the");
        _out.WriteLine("   honest dense number it wins by MORE — the dense side grew 3.52× at P1 (3.39×");
        _out.WriteLine("   after P2) and");
        _out.WriteLine("   the accelerated side grew far less.");

        var top = kept.OfType<PlanarAimOperator>().Last();
        Assert.True(top.Report.FactorNonZeros > top.Report.PreconditionerNonZeros,
            "the factor's fill-in must exceed the matrix it is factored from");
        Assert.True(top.Report.ResidentBytes < PlanarSystem.ResidentBytes(top.Size),
            "the accelerator must still be the smaller of the two, honestly counted on both sides");

        // The brief's own milestone-3 gate, at the N it names — asserted here rather than in the
        // routine tier for the reason printed above.
        foreach (var (o, m) in kept.OfType<PlanarAimOperator>().Zip(measuredBytes))
            Assert.InRange((double)o.Report.ResidentBytes / m, 0.80, 1.20);
    }

    // =========================================================================================
    // Routine gates — counters, not clocks
    // =========================================================================================

    [Fact]
    public void P1_3_TheAimReportIsWithin20PercentOfEveryArrayTheOperatorActuallyHolds()
    {
        // MILESTONE 3'S GATE, IN A FORM A PARALLEL TEST SUITE CANNOT BREAK.
        //
        // The brief asks for the reported bytes to be within 20% of "the measured resident delta".
        // A GC.GetTotalMemory delta is PROCESS-wide, and xUnit runs test classes concurrently in one
        // process, so under full-suite load another class's collection lands inside the measurement:
        // the first version of this test read a ratio of -0.245 in a full run and 0.925 alone. That is
        // a fact about the instrument, not about the code, and it is not fixable by re-running.
        //
        // So the RESIDENT measurement lives in P1_2 (Category=Benchmark, measured alone, asserted
        // there), and the routine gate counts the same thing by a route that is immune to load:
        // WALK THE OPERATOR'S OWN OBJECT GRAPH and add up every array it holds. That is independent
        // of the report's arithmetic in the way that matters — it counts the arrays that exist rather
        // than re-deriving them from the report's own fields — so it is exactly the check that would
        // have caught the omission P1 fixed.
        var problem = PlanarLineFixtures.Fr4Line(20e-3, 10e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping).Mesh;
        int n       = mesh.Bases.Count;
        Assert.InRange(n, 400, 900);

        var geom = PlanarFill.BuildGeometryOnlyCores(mesh);
        var pair = PlanarLineFixtures.Kernel(problem.Slab, 10e9)
                                     .For(geom, PlanarFillSettings.Default.Order);
        var aim = PlanarAimOperator.Build(geom, pair.VectorPotential, pair.Scalar, 2.0 * Math.PI * 10e9);

        var r = aim.Report;
        // P6: the operator holds its geometry, and the geometry holds the geometry-only cores it was
        // built from. Those cores are PlanarFillCores.CoreBytes' own accounting (P1_5 gates it) and
        // are reported beside the accelerator in every table rather than inside ResidentBytes, so the
        // walk stops at them.
        long walked = ArrayBytesReachableFrom(aim) - ArrayBytesReachableFrom(aim.Geometry.Cores);
        double ratio = (double)r.ResidentBytes / walked;

        _out.WriteLine($"N = {n}: reported {Mb(r.ResidentBytes)} MB, every array the operator holds " +
                       $"{Mb(walked)} MB, ratio {ratio:F3}. Near entries {r.NearEntries:N0}, factor " +
                       $"nnz {r.FactorNonZeros:N0}. The BUILD's own peak — this plus the transient " +
                       $"CSC copy CSparse factors from — is {Mb(r.PeakBuildBytes)} MB.");

        Assert.InRange(ratio, 0.80, 1.20);

        // …AND THE GATE IS NOT VACUOUS. Without the sparse LU's own fill-in — the term P1 added — the
        // same comparison misses by more than the 20% it allows. If it did not, milestone 3 would have
        // been a rename and nothing else, and this test would be asserting that a bug was never there.
        long withoutFactor = r.ResidentBytes - (20L * r.FactorNonZeros + 8L * (r.UnknownCount + 1));
        _out.WriteLine($"the same count WITHOUT the sparse LU is {Mb(withoutFactor)} MB — " +
                       $"{(double)r.ResidentBytes / withoutFactor:F2}× less, and " +
                       $"{(double)withoutFactor / walked:F3} of what the operator holds.");

        Assert.True(r.FactorNonZeros > r.PreconditionerNonZeros,
            "the factor's fill-in must exceed the near matrix it is factored from, or " +
            "PreconditionerNonZeros was an adequate stand-in after all");
        Assert.True((double)withoutFactor / walked < 0.80,
            "the term P1 added has to be worth more than the 20% the gate above allows, or the gate " +
            "would pass with the old accounting");
    }

    [Fact]
    public void P1_4_TheExactNearEntriesAreReleasedByDefault_AndTheReportSaysSo()
    {
        // The other half of milestone 3. The accelerated PRODUCT reads the CORRECTION (exact − AIM),
        // never the exact entries; holding both for the life of the operator was 16 B per near entry
        // of pure diagnostic weight. Released by default, kept on request, and the report says which.
        var problem = PlanarLineFixtures.Fr4Line(8e-3, 10e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var geom    = PlanarFill.BuildGeometryOnlyCores(mesh);
        var pair    = PlanarLineFixtures.Kernel(problem.Slab, 10e9)
                                        .For(geom, PlanarFillSettings.Default.Order);
        double omega = 2.0 * Math.PI * 10e9;

        var shipped = PlanarAimOperator.Build(geom, pair.VectorPotential, pair.Scalar, omega);
        Assert.False(shipped.Report.NearExactRetained);

        // It THROWS rather than returning zero: a silent zero is indistinguishable from "not near",
        // which is the question the caller is asking.
        var ex = Assert.Throws<InvalidOperationException>(() => shipped.NearExactAt(0, 0));
        Assert.Contains("KeepNearExact", ex.Message, StringComparison.Ordinal);

        var kept = PlanarAimOperator.Build(geom, pair.VectorPotential, pair.Scalar, omega,
                                           PlanarAimSettings.Default with { KeepNearExact = true });
        Assert.True(kept.Report.NearExactRetained);
        Assert.NotEqual(Complex.Zero, kept.NearExactAt(0, 0));

        // The report charges for them exactly when they are held, and for nothing else: the two
        // operators differ by 16 B per near entry and by nothing at all otherwise.
        long delta = kept.Report.ResidentBytes - shipped.Report.ResidentBytes;
        Assert.Equal(16L * shipped.Report.NearEntries, delta);
        _out.WriteLine($"N = {mesh.Bases.Count}, {shipped.Report.NearEntries:N0} near entries: " +
                       $"released {Mb(shipped.Report.ResidentBytes)} MB, kept " +
                       $"{Mb(kept.Report.ResidentBytes)} MB, difference {Mb(delta)} MB.");

        // …and the answer is unaffected either way. The flag moves memory, never arithmetic.
        var rhs = PlanarExcitation.RightHandSide(mesh.Bases.Count,
                                                 PlanarPorts.ResolveAll(mesh, PlanarLineFixtures.EndPorts(problem))[0]);
        var a = shipped.Solve(rhs);
        var b = kept.Solve(rhs);
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
    }

    /// <summary>
    /// Every array reachable from <paramref name="root"/>, in bytes — an independent count of what an
    /// object actually holds, walking fields rather than trusting anything the object says about
    /// itself. Element sizes are the CLR's own for the blittable types this graph contains; an array
    /// of some other value type would be counted at zero, which is why the ratio this feeds is
    /// two-sided rather than an upper bound.
    /// </summary>
    private static long ArrayBytesReachableFrom(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long total = 0;
        var stack = new Stack<object>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            object o = stack.Pop();
            if (o is null || !seen.Add(o)) continue;

            var t = o.GetType();
            if (t.IsArray)
            {
                var arr = (Array)o;
                var el = t.GetElementType()!;
                total += arr.LongLength * ElementBytes(el);
                if (!el.IsPrimitive && !el.IsEnum && el != typeof(Complex))
                    foreach (var item in arr)
                        if (item is not null && !item.GetType().IsValueType) stack.Push(item);
                continue;
            }

            for (var walk = t; walk is not null && walk != typeof(object); walk = walk.BaseType)
                foreach (var f in walk.GetFields(System.Reflection.BindingFlags.Instance
                                               | System.Reflection.BindingFlags.Public
                                               | System.Reflection.BindingFlags.NonPublic))
                {
                    if (f.FieldType.IsPrimitive || f.FieldType.IsEnum) continue;
                    object? v = f.GetValue(o);
                    if (v is not null && !f.FieldType.IsPrimitive) stack.Push(v);
                }
        }
        return total;

        // P6: a struct element is sized as the runtime lays it out (Unsafe.SizeOf), not as 0 — the
        // near cores' store is an array of 168-byte structs, and a walk that sized it at nothing
        // would let the report claim them unchecked.
        static int ElementBytes(Type el)
            => el == typeof(Complex) ? 16
             : el == typeof(double) || el == typeof(long) ? 8
             : el == typeof(int) || el == typeof(float) ? 4
             : el == typeof(bool) || el == typeof(byte) ? 1
             : el.IsValueType
                 ? (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!
                       .MakeGenericMethod(el).Invoke(null, null)!
                 : IntPtr.Size;
    }

    [Fact]
    public void P1_5_ResidentBytesReproducesTheCoresTheFillActuallyBuilt()
    {
        // PlanarSystem.CoreBytes reconstructs PlanarFillCores.CoreBytes from N and the cell count,
        // because a refusal has to quote the number BEFORE anything is cored. This is the gate on that
        // reconstruction — and on its two stated assumptions (the shipped extraction order, and an
        // even x̂/ŷ split, which minimises the vector term and therefore makes the estimate a floor).
        (double LengthM, PlanarMeshSettings Settings)[] rungs =
        [
            (20e-3, PlanarLineFixtures.Coarse),
            (60e-3, PlanarLineFixtures.Coarse),
            (20e-3, PlanarLineFixtures.Shipping),
        ];

        foreach (var (lengthM, settings) in rungs)
        {
            var problem = PlanarLineFixtures.Fr4Line(lengthM, 10e9);
            var mesh    = SurfaceMesher.Mesh(problem, settings).Mesh;
            var cores   = PlanarFill.BuildCores(mesh);

            long actual   = cores.CoreBytes;
            long estimate = PlanarSystem.CoreBytes(mesh.Bases.Count, mesh.Cells.Count);
            double ratio  = (double)estimate / actual;

            _out.WriteLine($"N = {mesh.Bases.Count,5}, cells = {mesh.Cells.Count,5}: " +
                           $"cores actually {Mb(actual)} MB, reconstructed {Mb(estimate)} MB " +
                           $"({ratio:F3}×); {cores.ClassCount:N0} classes for {cores.BandPairs:N0} band pairs");

            // P5: the reconstruction is P4's triangle layout, kept as the a-priori figure a refusal
            // quotes before anything is cored. The class layout's actual bytes are geometry-dependent
            // — a 4-byte index per band pair plus 112 bytes per class — so they meet the figure at a
            // translation reuse of ≈ 3× and fall well below it above that. These three rungs span
            // that range (the coarse meshes have no edge fan and reuse heavily; the shipping hero's
            // 2.5× sits just under break-even), which is why the band is what it is.
            Assert.InRange(ratio, 0.6, 20.0);
        }
    }

    [Fact]
    public void P1_6_AllThreeCeilingRefusalsQuoteTheSameNumber()
    {
        // Milestone 4's whole point: the three had drifted apart in wording while agreeing on a number
        // that was wrong, and the fix is that they share one function rather than three copies of one
        // formula. N is past the dense ceiling and the mesh is a real one, so the cell count is real.
        var problem = PlanarLineFixtures.Fr4Line(400e-3, 10e9);
        var report  = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Shipping);
        Assert.Equal(PlanarBudgetVerdict.Refused, report.Verdict);
        int n = report.UnknownCount, cells = report.CellCount;

        string phrase = PlanarSystem.ResidentPhrase(n, cells);
        _out.WriteLine(phrase);
        _out.WriteLine("");
        _out.WriteLine(report.Refusal!);

        Assert.Contains(phrase, report.Refusal!, StringComparison.Ordinal);

        var fromSystem = Assert.Throws<InvalidOperationException>(
            () => PlanarSystem.GuardCeiling(n, cells));
        Assert.Contains(phrase, fromSystem.Message, StringComparison.Ordinal);

        var fromFill = Assert.Throws<InvalidOperationException>(
            () => PlanarFill.BuildCores(report.Mesh));
        Assert.Contains(phrase, fromFill.Message, StringComparison.Ordinal);

        var fromSolveContext = Assert.Throws<InvalidOperationException>(
            () => SurfaceMesher.GuardCeiling(n, accelerated: false, cellCount: cells));
        Assert.Contains(PlanarSystem.ResidentPhrase(n, cells), fromSolveContext.Message,
                        StringComparison.Ordinal);

        // …and it says what the number IS. A bare megabyte figure beside a ceiling reads as a machine
        // limit, which is the defect the 2026-08-14 owner report already caught once.
        Assert.Contains("resident at the peak of one frequency point", report.Refusal!,
                        StringComparison.Ordinal);
        Assert.Contains("cached cores", report.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain("MB of dense complex matrix", report.Refusal!, StringComparison.Ordinal);
    }
}
