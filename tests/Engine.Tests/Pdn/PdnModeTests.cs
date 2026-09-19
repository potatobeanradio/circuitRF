// ================================================================
//  PdnModeTests.cs — brief-railrf-15-modes-and-maps.md §4
//
//  §4.5's eigenproblem, gated on §7's own acceptance: "A uniform rectangular plane pair has
//  closed-form modes and a closed-form input impedance. The mesh must reproduce THE FIRST SIX
//  MODES TO BETTER THAN 2 %, WITH MONOTONE CONVERGENCE IN CELL SIZE."
//
//  ── WHAT IS EXTERNAL HERE ─────────────────────────────────────────────────────────────────────
//
//    f_mn = (c / 2√εᵣ)·√((m/a)² + (n/b)²)     — the rectangular cavity, §4.5's own closed form
//    C    = ε₀εᵣΔ²/h,  L = µ₀·h               — §4.1's per-cell terms, written out below
//    Z    = 1/(jωC)                            — the parallel-plate capacitor, well below mode 1
//
//  None of it is circuitRF's arithmetic and none of it is read out of the solver.
//
//  ── THE CAVITY IS BUILT HERE, WHICH IS THE POINT OF THE SOLVER TAKING ARRAYS ──────────────────
//
//  src/Engine cannot see src/Design, so PdnModeSolver takes adjacency and per-cell terms rather
//  than a netlist — and that is exactly what lets these gates be the TEXTBOOK RECTANGLE with no
//  artwork, no technology and no extractor anywhere near them. That the same solver agrees with a
//  sweep of a real board is a different claim, and it is gated in Ui.Tests where the extractor is.
//
//  One test per CLAIM the brief makes, not one per measured rung.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Engine.Pdn;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Pdn;

public sealed class PdnModeTests(ITestOutputHelper output)
{
    private const double C0 = 299_792_458.0;
    private const double MuZero = 4.0e-7 * Math.PI;
    private const double Eps0 = 1.0 / (MuZero * C0 * C0);

    // A rectangle chosen so that none of the first six modes is degenerate with another and none
    // is within 5 % of its neighbour: 60 x 42 mm puts the seventh (3,0) at 50 and the sixth (0,2)
    // at 47.6 in units of 1/m, which is the closest pair anywhere in the set.
    private const double AMetres = 60e-3;
    private const double BMetres = 42e-3;
    private const double EpsilonR = 4.3;
    private const double HMetres = 200e-6;

    /// <summary>§4.5's closed form, written out rather than read back.</summary>
    private static double ClosedForm(int m, int n) =>
        C0 / (2.0 * Math.Sqrt(EpsilonR)) *
        Math.Sqrt(Math.Pow(m / AMetres, 2) + Math.Pow(n / BMetres, 2));

    /// <summary>The first six (m,n) of this rectangle, in frequency order.</summary>
    private static readonly (int M, int N)[] FirstSix =
        [(1, 0), (0, 1), (1, 1), (2, 0), (2, 1), (0, 2)];

    // ── R-rail15-4 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>§7's acceptance, in both its halves.</b>
    ///
    /// <para>The first six modes of a uniform rectangle against <c>f_mn</c> to better than 2 %, and
    /// the error falling on every refinement of the cell size. <b>The convergence half is the one
    /// that catches a discretisation which happens to be right at one pitch</b> — a single number
    /// inside a tolerance can be a cancellation between two errors, and three pitches cannot.</para>
    ///
    /// <para>What is left at the finest pitch is the unit-cell ladder's own dispersion,
    /// <c>ω = (2v/Δ)·sin(kΔ/2)</c> against the continuum's <c>vk</c> — second order in Δ, so the
    /// error falls by roughly four when the pitch halves, which is what the ladder below reads.</para>
    /// </summary>
    [Fact]
    public void TheFirstSixModesOfARectangleAreTheClosedFormAndConvergeWithTheCellSize()
    {
        var worst = new List<double>();

        foreach (double deltaMm in new[] { 6.0, 3.0, 2.0 })
        {
            var (system, _, _) = Rectangle(deltaMm * 1e-3);
            var modes = PdnModeSolver.Solve(system, new PdnModeOptions { Count = 6 });

            Assert.Null(modes.Refusal);
            Assert.Equal(1, modes.Pieces);                 // one plane pair, one zero mode
            Assert.Equal(6, modes.Modes.Count);

            double here = 0;
            for (int k = 0; k < 6; k++)
            {
                var (m, n) = FirstSix[k];
                double want = ClosedForm(m, n);
                double error = Math.Abs(modes.Modes[k].FrequencyHz / want - 1.0);
                here = Math.Max(here, error);

                output.WriteLine(
                    $"Δ {deltaMm} mm  ({m},{n})  closed form {want / 1e9:0.0000} GHz  " +
                    $"mesh {modes.Modes[k].FrequencyHz / 1e9:0.0000} GHz  {error:P3}");
            }

            worst.Add(here);
        }

        // Monotone in the cell size — every refinement strictly better than the last.
        Assert.True(worst[1] < worst[0] && worst[2] < worst[1],
            $"the error must fall on every refinement — {worst[0]:P2} / {worst[1]:P2} / {worst[2]:P2}");

        // …and §7's own number, at the finest of the three.
        Assert.True(worst[2] < 0.02,
            $"§7 asks for better than 2 % on the first six modes; the worst was {worst[2]:P2}");
    }

    // ── R-rail15-2 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>WHERE the mode lands is the finding.</b> §2.4: <i>"A mode whose maximum sits on the load
    /// pin field is a problem; the same mode with its maximum in a corner is not, and only the map
    /// distinguishes them."</i>
    ///
    /// <para>The (1,0) mode of a rectangle is <c>cos(πx/a)</c>: full amplitude at either end and a
    /// null down the middle. Two ports — one on the end, one on the centre line — and the reported
    /// per-port values differ by the ratio that shape predicts. <b>A mode list that reported only
    /// frequencies would pass nothing here</b>, which is why the per-port reading is the
    /// deliverable rather than a convenience.</para>
    ///
    /// <para>The centre port is not exactly zero and cannot be: the cells nearest the null sit half
    /// a cell off it, so the field there is <c>sin(πΔ/2a)</c> — a number the cell size sets, and one
    /// the gate below states rather than rounds away.</para>
    /// </summary>
    [Fact]
    public void AModesFieldIsReportedAtEachPortAndTheEndAndTheNullDoNotReadTheSame()
    {
        const double delta = 2e-3;
        var (system, nx, ny) = Rectangle(delta);

        var modes = PdnModeSolver.Solve(system, new PdnModeOptions { Count = 1 });
        Assert.Null(modes.Refusal);

        var mode = Assert.Single(modes.Modes);
        Assert.InRange(mode.FrequencyHz / ClosedForm(1, 0), 0.98, 1.02);

        // Two 2 x 2 "pin fields", one on the x = 0 end and one straddling the centre line — a port
        // is a set of cells (§4.3), and the reading is the peak over it.
        int mid = nx / 2;
        var endPort    = Cells(nx, [(0, ny / 2), (0, ny / 2 + 1), (1, ny / 2), (1, ny / 2 + 1)]);
        var centrePort = Cells(nx, [(mid - 1, ny / 2), (mid - 1, ny / 2 + 1), (mid, ny / 2), (mid, ny / 2 + 1)]);

        var end = PdnFieldMap.ValueAtPort(mode.Field, endPort);
        var centre = PdnFieldMap.ValueAtPort(mode.Field, centrePort);

        Assert.NotNull(end);
        Assert.NotNull(centre);

        output.WriteLine($"end {end!.Value.Value:0.0000}   centre {centre!.Value.Value:0.0000}");

        // The end IS where this mode is worst: the field is normalised to its own peak, so a port
        // sitting on it reads 1.
        Assert.InRange(end.Value.Magnitude, 0.99, 1.0);

        // And the centre reads the half-cell offset from the null, sin(πΔ/2a), and nothing more.
        double expected = Math.Sin(Math.PI * delta / (2.0 * AMetres));
        Assert.InRange(centre.Value.Magnitude, expected * 0.9, expected * 1.1);

        // Stated as the ratio the brief asks for, so the claim is not two separate numbers.
        Assert.True(end.Value.Magnitude / centre.Value.Magnitude > 15,
            $"the end and the null must not read the same — {end.Value.Magnitude:0.000} against " +
            $"{centre.Value.Magnitude:0.000}");
    }

    // ── determinism ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The same cavity produces the same field, bit for bit.</b>
    ///
    /// <para>An eigenvector is determined only up to a scale — sign included — so an eigensolver may
    /// legitimately hand back <c>v</c> on one run and <c>−v</c> on the next. On a cold-to-hot ramp
    /// those are different pictures, and brief 9's clipboard gate and brief 17's figures both
    /// require one result to render as one set of bytes. <see cref="PdnMode.Field"/> pins the sign
    /// to the cell of largest magnitude rather than leaving it to LAPACK, and this is what says so.</para>
    /// </summary>
    [Fact]
    public void TheSameCavityGivesTheSameFieldEveryTime()
    {
        var (a, _, _) = Rectangle(3e-3);
        var (b, _, _) = Rectangle(3e-3);

        var first = PdnModeSolver.Solve(a, new PdnModeOptions { Count = 3 });
        var second = PdnModeSolver.Solve(b, new PdnModeOptions { Count = 3 });

        Assert.Null(first.Refusal);
        Assert.Equal(3, first.Modes.Count);

        for (int k = 0; k < first.Modes.Count; k++)
        {
            Assert.Equal(first.Modes[k].FrequencyHz, second.Modes[k].FrequencyHz);
            Assert.Equal(first.Modes[k].Field, second.Modes[k].Field);

            // The normalisation is exact at the peak, which is what a ramp is read against.
            Assert.Equal(1.0, first.Modes[k].Field.Max());
        }
    }

    // ── the |Z| map, against the one closed form it has below its first mode ───────────────────

    /// <summary>
    /// <b>Well below the first mode the impedance map is the parallel-plate capacitor, everywhere.</b>
    ///
    /// <para>§7's lumped limit applied to the field rather than to one port: with no phase across
    /// the board every cell is at the same potential, so every cell of the map reads
    /// <c>1/(jωC_total)</c> with <c>C_total</c> the sum of the cells' own capacitances. That pins
    /// three things at once that nothing else here does — the one amp of injection, the ohms the map
    /// is in, and the sign of the reactance.</para>
    /// </summary>
    [Fact]
    public void BelowItsFirstModeEveryCellOfTheMapReadsTheLumpedCapacitor()
    {
        var (system, _, _) = Rectangle(3e-3);

        double f = ClosedForm(1, 0) / 100.0;
        double total = system.CapacitanceFarads.Sum();
        double lumped = 1.0 / (2.0 * Math.PI * f * total);

        var z = PdnFieldMap.Impedance(system, [0], f, out string? refusal);

        Assert.Null(refusal);
        Assert.NotNull(z);

        foreach (var cell in z!)
        {
            Assert.InRange(cell.Magnitude / lumped, 0.99, 1.01);

            // Capacitive, which is what separates ε₀εᵣA/h from an inductance of the same size at
            // one frequency.
            Assert.True(cell.Imaginary < 0, $"a plane pair below its first mode is capacitive: {cell}");
        }
    }

    // ── the fixture ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A uniform rectangular plane pair on a grid of square cells — <b>§4.1's own terms, written out
    /// here rather than taken from the extractor.</b>
    /// </summary>
    /// <remarks>
    /// <c>C = ε₀εᵣΔ²/h</c> per cell and <c>L = µ₀·h</c> per edge, which for square cells is the
    /// loop inductance of one square. The edge resistance is §4.1's <c>2·Rs</c> at one ounce of
    /// copper — unread by the mode solver, which is loss-free, and read by the map.
    /// </remarks>
    private static (PdnCavitySystem System, int Nx, int Ny) Rectangle(double deltaMetres)
    {
        int nx = (int)Math.Round(AMetres / deltaMetres);
        int ny = (int)Math.Round(BMetres / deltaMetres);

        double c = Eps0 * EpsilonR * deltaMetres * deltaMetres / HMetres;
        double l = MuZero * HMetres;
        double r = 2.0 / (5.8e7 * 34.8e-6);          // 2·Rs, both planes, one ounce

        var cap = new double[nx * ny];
        Array.Fill(cap, c);

        var edges = new List<PdnCavityEdge>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                if (i + 1 < nx) edges.Add(new PdnCavityEdge(k, k + 1, r, l));
                if (j + 1 < ny) edges.Add(new PdnCavityEdge(k, k + nx, r, l));
            }

        return (new PdnCavitySystem
        {
            CellCount = nx * ny,
            Edges = edges,
            CapacitanceFarads = cap,
        }, nx, ny);
    }

    private static int[] Cells(int nx, (int I, int J)[] at) =>
        [.. at.Select(p => p.J * nx + p.I)];
}
