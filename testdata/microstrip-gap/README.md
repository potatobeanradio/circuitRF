# Microstrip gap (MGAP) reference

Regression data for brief SDL-9 (R-sdl9-3): the MGAP closed form against an EM solve that shares nothing
with it. `gap-capacitance.txt` holds the series capacitance `Cg` across the gap and the shunt capacitance
`Cp` from each end to ground, over W/h = 0.5, 1, 2 and S/W = 0.1, 0.2, 0.3, 0.5, 1 on the schematic-to-layout
test substrate (h = 508 µm, εr = 3.66). It is gated by `tests/Core.Tests/Devices/Microstrip/MicrostripGapTests.cs`.

## How the numbers were made

The gap is quasi-static at every frequency it is used at, so the reference is an electrostatic solve: the
planar EM engine's own capacitance-matrix solve (`PlanarDeembed.StaticCapacitanceMatrix`, the grounded-slab
static Green's function with its image series) on a meshed pair of zero-thickness strips, driven by a scratch
program that is not part of the repository. For each geometry, with strip length `L = max(8h, 4W)`:

1. Two strips of length `L`, `S` apart, end to end. The Maxwell matrix gives `Cg = −C12` and the first
   strip's capacitance to ground `C11 + C12`.
2. One strip of length `L` alone, and one of `2L`: `C(L) = L·C' + 2·Cend`, so `Cend = C(L) − C(2L)/2`.
3. `Cp = (C11 + C12) − C(L) + Cend` — the strip's ground capacitance with its far end open and its near end
   at the gap, less the same strip with both ends open, plus the one open end it no longer has.

Every strip is meshed by the same rule (cells graded toward each end and each edge from W/40 to W/4 along,
W/6 across), so the line's own capacitance per metre cancels in the differences. The open-end capacitance
this produces (13.4 fF at W/h = 1) agrees with the usual open-end length extension to about 3 %.

**Converged to about 1.6 %.** Refining the mesh 2× moved `Cg` by 0.9-1.6 % and `Cp` by under 1 %; making the
strips 2× longer moved `Cg` by 1 %. Five rows were also solved at 1.5× mesh and 1.5× length (W = 254 µm):
`Cg` 12.762 / 10.354 / 8.883 / 6.974 / 4.381 fF and `Cp` 0.621 / 1.182 / 1.694 / 2.591 / 4.260 fF, against
the committed 12.623 / 10.250 / 8.793 / 6.897 / 4.317 and 0.620 / 1.179 / 1.690 / 2.584 / 4.248.

**Zero-thickness metal, on purpose.** The closed form has no thickness term either. A real 35 µm strip adds
a parallel-plate coupling across a narrow gap (about ε₀·W·t/S, ~1.5 fF at W = 508 µm, S = 100 µm) that
neither side carries.

## Where the closed form departs from it

Read as the gap's own |S21| and phase at 50 Ω, 2-10 GHz:

| W/h | Cg, closed form vs EM | Cp | |S21| | phase |
|---|---|---|---|---|
| 0.5 | 1-6 % low | within 0.21 fF | within 0.5 dB | within 0.3° |
| 1 | 1.4 % high to 5 % low | 0.3-0.9 fF low | within 0.5 dB | within 0.5° |
| 2, S/W ≤ 0.5 | 8-14 % low | 1.5-3.2 fF low | within 1.3 dB | within 1.7° |
| 2, S/W = 1 | **43 % low** (1.70 against 2.99 fF) | 2.5 fF low | **4.9 dB low** | 1.4° |

The last row is the edge of the stated range in both ratios at once (S/h = 2), where the series capacitance is
the small difference of the two fitted mode capacitances and the fit's error in each lands on it whole. The
expressions are left as fitted; the test holds that corner to its own recorded bound rather than to the rest.

## The scratch program

```csharp
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Core.Devices.Microstrip;

// Quasi-static gap capacitances from the planar engine's own electrostatic MoM (zero-thickness strips on a
// grounded slab). Usage: gapcap h_um er refine  then lines of "W_um S_um" on stdin.
double h = double.Parse(args[0]) * 1e-6, er = double.Parse(args[1]); double refine = args.Length > 2 ? double.Parse(args[2]) : 1;
var slab = new GroundedSlab(h, new EmMaterial(er, 0.0));
var terms = PlanarKernelTerms.StaticScalar(slab);

static List<double> Graded(double a, double b, double c0, double hmax, double r)
{
    // symmetric grading from both ends toward the middle
    var left = new List<double> { 0 }; double s = c0, x = 0; double half = (b - a) / 2;
    while (x + s < half) { x += s; left.Add(x); s = Math.Min(s * r, hmax); }
    var pts = new List<double>();
    foreach (var p in left) pts.Add(a + p);
    for (int i = left.Count - 1; i >= 0; i--) { double q = b - left[i]; if (q - pts[^1] > 1e-12 * (b - a)) pts.Add(q); }
    // merge a too-thin middle cell
    return pts;
}

(Complex[,] C, int m) Solve(List<(double x0, double x1)> strips, double w)
{
    var ys = Graded(-w / 2, w / 2, w / (40 * refine), w / (6 * refine), 1.35);
    var xsAll = new SortedSet<double>();
    var stripXs = new List<List<double>>();
    foreach (var (x0, x1) in strips) { var g = Graded(x0, x1, w / (40 * refine), w / (4 * refine), 1.35); stripXs.Add(g); foreach (var v in g) xsAll.Add(v); }
    var gx = xsAll.ToList(); var gy = ys;
    var cells = new List<PlanarCell>(); var cond = new List<int>();
    for (int k = 0; k < strips.Count; k++)
    {
        var g = stripXs[k];
        for (int i = 0; i + 1 < g.Count; i++)
            for (int j = 0; j + 1 < gy.Count; j++)
            {
                int ix = gx.IndexOf(g[i]);
                cells.Add(new PlanarCell(0, ix, j, g[i], gy[j], g[i + 1], gy[j + 1])); cond.Add(k);
            }
    }
    var mesh = new PlanarMesh(cells, new List<PlanarBasis>(), new[] { "Metal" }, gx, gy);
    var mat = PlanarDeembed.StaticCapacitanceMatrix(mesh, terms, cond, strips.Count);
    var C = new Complex[strips.Count, strips.Count];
    for (int i = 0; i < strips.Count; i++) for (int j = 0; j < strips.Count; j++) C[i, j] = mat[i, j];
    return (C, cells.Count);
}

string? line;
while ((line = Console.ReadLine()) != null)
{
    var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (t.Length < 2) continue;
    double w = double.Parse(t[0]) * 1e-6, s = double.Parse(t[1]) * 1e-6;
    double L = Math.Max(8 * h, 4 * w) * (args.Length > 3 ? double.Parse(args[3]) : 1);
    var (ci1, _) = Solve([(0, L)], w);
    var (ci2, _) = Solve([(0, 2 * L)], w);
    var (cg, m) = Solve([(-L, 0), (s, s + L)], w);
    double cIso = ci1[0, 0].Real, cIso2 = ci2[0, 0].Real;
    double cEnd = (2 * cIso - cIso2) / 2;                 // one open end
    double cGround = (cg[0, 0] + cg[0, 1]).Real;          // strip 1 to ground with strip 2 also at 0 V... (Maxwell: C11 + C12)
    double cSeries = -cg[0, 1].Real;
    double cp = cGround - cIso + cEnd;
    var (mg, mp) = MicrostripGap.Capacitances(w, s, h, er);
    Console.WriteLine($"{t[0]} {t[1]} Cg={cSeries * 1e15:F3} Cp={cp * 1e15:F3} Cend={cEnd * 1e15:F3} cells={m}   closed Cg={mg * 1e15:F3} Cp={mp * 1e15:F3}");
}
```
