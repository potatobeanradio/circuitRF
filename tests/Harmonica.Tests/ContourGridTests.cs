using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Harmonica;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// M5's gate: <b>Tier 6</b> (the RBF factorization cache is bit-identical to a full rebuild and
/// invalidates on a NaN-mask change) and <b>Tier 7</b> (the <c>excludeHoleDiscs: true</c> mask still
/// produces no iso-line inside a hole — R8A §6 reversed this from <c>Contours()</c>'s own DEFAULT to
/// an explicit opt-in; see <c>ContourGridHoleSpanTests</c> for the new default's own gate), plus
/// R-hrf-7's solves-per-Γ-point and D6's argmax.
/// </summary>
public sealed class ContourGridTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    /// <summary>Hero 2's GaN HEMT, coefficients folded in so the fixture needs no globals.</summary>
    private static CircuitModel Model(double pinMax = 34) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
            },
        },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = pinMax,
        },
    };

    private static TerminationSet Terms(CircuitModel m)
    {
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return t;
    }

    // ── R-hrf-7 / D4 — the secant search ──────────────────────────────────────

    [Fact]
    public void D4_TheSecantReachesCompressionInFarFewerSolvesThanTheLadder()
    {
        var model = Model();
        var ctx = HarmonicaContext.Create(model, Settings);
        var terms = Terms(model);

        var r = PinSearch.Run(ctx, terms);

        output.WriteLine($"stop = {r.Reason}, {r.Solves} HB solves (tickle included), " +
                         $"Gss = {r.SmallSignalGainDb:F2} dB");
        foreach (var s in r.Steps)
            output.WriteLine($"  Pin {s.PavlDbm,7:F3} dBm  Gt {s.GainDb,6:F2} dB  " +
                             $"compr {s.Compression,5:F2} dB  Pout {10 * Math.Log10(s.PoutW) + 30,6:F2} dBm");

        Assert.True(r.Compressed, $"the fixture must compress: stopped on {r.Reason}");
        Assert.NotNull(r.AtCompression);
        Assert.InRange(r.AtCompression!.Compression, 3.0 - 0.02, 3.0 + 0.02);

        // §6.3: "~5–8 solves against the ladder's ~30". The ladder for this fixture would be the
        // tickle plus (PinMax − PinStart)/PinStep steps — 37 at the batch engine's 1 dB.
        int ladderWouldBe = 1 + (int)(model.Settings.PinMaxDbm - model.Settings.PinStartDbm) + 1;
        output.WriteLine($"a uniform 1 dB ladder over the same span would be up to {ladderWouldBe} solves");
        Assert.True(r.Solves <= 12,
            $"the secant took {r.Solves} solves; D4's whole claim is that this is single digits");
    }

    [Fact]
    public void D4_ANonCompressingPointStopsAtPinMaxAndSaysSo()
    {
        // The hole case, in isolation: a PinMax so low the device cannot reach 3 dB.
        var model = Model(pinMax: -4);
        var ctx = HarmonicaContext.Create(model, Settings);

        var r = PinSearch.Run(ctx, Terms(model));

        output.WriteLine($"stop = {r.Reason} after {r.Solves} solves");
        Assert.Equal(PinStopReason.PinMax, r.Reason);
        Assert.False(r.Compressed);
        Assert.Null(r.AtCompression);
    }

    // ── TIER 6 — the factorization cache ──────────────────────────────────────

    [Fact]
    public void Tier6_ARebuiltFitAndACachedResolveAreBitIdentical()
    {
        // Not "within a tolerance": the factored path runs the same LDLᵀ factors through the same
        // solve, so anything other than equality is a defect. A tolerance here would hide exactly the
        // re-association that would be one.
        var rng = new Random(20260806);
        int n = 61;
        var re = new double[n];
        var im = new double[n];
        var power = new double[n];
        var efficiency = new double[n];

        for (int i = 0; i < n; i++)
        {
            double mag = 0.8 * Math.Sqrt(rng.NextDouble());
            double ang = 2 * Math.PI * rng.NextDouble();
            re[i] = mag * Math.Cos(ang);
            im[i] = mag * Math.Sin(ang);
            power[i]      = 30 + 5 * re[i] - 3 * im[i] * im[i];
            efficiency[i] = 55 - 20 * (re[i] - 0.2) * (re[i] - 0.2) - 15 * im[i] * im[i];
        }

        var factor = Rbf2D.Factorize(re, im, power);
        Assert.True(factor.IsUsable);

        // Two metrics, ONE factorization — the case §6.4.1 names.
        foreach (var (name, values) in new[] { ("power", power), ("efficiency", efficiency) })
        {
            var rebuilt = new Rbf2D(re, im, values);
            var cached  = factor.Solve(values);

            Assert.Equal(rebuilt.NodeCount, cached.NodeCount);
            Assert.Equal(rebuilt.Epsilon,   cached.Epsilon);

            for (int i = 0; i < rebuilt.NodeCount; i++)
                Assert.Equal(rebuilt.NodeValues[i], cached.NodeValues[i]);

            // The weights are the whole point, and they are compared exactly.
            var a = new double[n];
            var b = new double[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = rebuilt.Evaluate(re[i] * 0.5 + 0.03, im[i] * 0.5 - 0.02);
                b[i] = cached .Evaluate(re[i] * 0.5 + 0.03, im[i] * 0.5 - 0.02);
                Assert.Equal(a[i], b[i]);
            }

            output.WriteLine($"{name}: {n} nodes, rebuilt and cached agree bit for bit " +
                             $"(sample {a[0]:G17})");
        }
    }

    [Fact]
    public void Tier6_TheCacheInvalidatesWhenTheNaNMaskChanges()
    {
        // §0.3 item 6: the constructor DROPS NaN nodes, so which nodes exist depends on the values
        // after all. A point crossing in or out of a compression hole is exactly that, and re-solving
        // a stale factor against it would give a surface fitted to the wrong node set.
        var re = new double[] { 0.0, 0.3, -0.2, 0.1, -0.4, 0.25 };
        var im = new double[] { 0.0, 0.1,  0.4, -0.3, -0.1, 0.35 };
        var full = new double[] { 30, 31, 29, 32, 28, 30.5 };

        var factor = Rbf2D.Factorize(re, im, full);
        Assert.True(factor.MatchesNaNMask(full));
        Assert.Equal(6, factor.NodeCount);

        // One point falls into a hole.
        var holed = (double[])full.Clone();
        holed[2] = double.NaN;

        Assert.False(factor.MatchesNaNMask(holed));
        var ex = Assert.Throws<ArgumentException>(() => factor.Solve(holed));
        output.WriteLine(ex.Message);
        Assert.Contains("NaN", ex.Message, StringComparison.Ordinal);

        // Re-factorized against the new mask, it agrees bit for bit with a full rebuild.
        var refactored = Rbf2D.Factorize(re, im, holed);
        Assert.Equal(5, refactored.NodeCount);

        var rebuilt = new Rbf2D(re, im, holed);
        var cached  = refactored.Solve(holed);
        Assert.Equal(rebuilt.Epsilon, cached.Epsilon);
        Assert.Equal(rebuilt.Evaluate(0.05, 0.05), cached.Evaluate(0.05, 0.05));
        Assert.Equal([0, 1, 3, 4, 5], cached.UsedIndices);
    }

    // ── brief-harmonicarf-r6a §3 — the contour surface's own kernel/smooth/epsilon ──────────────────

    [Fact]
    public void ContourKernel_ChangesTheFittedSurface_ForTheIdenticalGrid()
    {
        // Two builds of the SAME model/terminations/Γ grid, differing only in ContourKernel — the
        // brief's own gate: the interpolated value must differ, or the setting does nothing.
        var mqModel = Model() with { Settings = Model().Settings with { ContourKernel = RbfKernel.Multiquadric } };
        var tpModel = Model() with { Settings = Model().Settings with { ContourKernel = RbfKernel.ThinPlate } };

        var gammaGrid = ContourGrid.RingGrid(rings: 3, spokes: 10, maxGamma: 0.85);

        var mqCtx = HarmonicaContext.Create(mqModel, Settings);
        var mqGrid = new ContourGrid();
        mqGrid.Build(mqCtx, Terms(mqModel), gammaGrid);
        var mqFit = mqGrid.Fit(GridMetric.PoutDbm);

        var tpCtx = HarmonicaContext.Create(tpModel, Settings);
        var tpGrid = new ContourGrid();
        tpGrid.Build(tpCtx, Terms(tpModel), gammaGrid);
        var tpFit = tpGrid.Fit(GridMetric.PoutDbm);

        Assert.Equal(RbfKernel.Multiquadric, mqGrid.ContourKernel);
        Assert.Equal(RbfKernel.ThinPlate,    tpGrid.ContourKernel);

        // A query point BETWEEN samples — at a sample node itself an RBF interpolant is exact for
        // every kernel and would agree trivially.
        double mqValue = mqFit.Evaluate(0.15, -0.1);
        double tpValue = tpFit.Evaluate(0.15, -0.1);
        output.WriteLine($"Multiquadric: {mqValue:F6} dBm, ThinPlate: {tpValue:F6} dBm");

        Assert.NotEqual(mqValue, tpValue, precision: 6);
    }

    [Fact]
    public void ContourSmooth_ChangeAlone_InvalidatesTheCachedFactor_EvenWithUnchangedPositions()
    {
        // §3's own correctness trap: _factor/_factorMask are keyed on (positions, NaN mask) — a
        // kernel/smooth/epsilon change with UNCHANGED positions must still force a re-factorization,
        // or the user changes the setting and the contours do not move.
        var model = Model();
        var gammaGrid = ContourGrid.RingGrid(rings: 3, spokes: 10, maxGamma: 0.85);
        var terms = Terms(model);

        var ctx = HarmonicaContext.Create(model, Settings);
        var grid = new ContourGrid();
        grid.Build(ctx, terms, gammaGrid);

        var before = grid.Fit(GridMetric.PoutDbm);
        int factorizationsBefore = grid.FactorizationCount;
        Assert.Equal(1, factorizationsBefore);
        double beforeValue = before.Evaluate(0.15, -0.1);

        // Same positions, same terminations, same NaN mask — ONLY ContourSmooth moves. Reuse the grid
        // points (no re-solve needed — see HarmonicaViewModel.ApplyContourSettings' own remarks) via
        // reuseUnchanged, exactly the path a live settings change takes.
        var smoothedModel = model with { Settings = model.Settings with { ContourSmooth = 0.5 } };
        var smoothedCtx = HarmonicaContext.Create(smoothedModel, Settings);
        grid.Build(smoothedCtx, terms, gammaGrid, reuseUnchanged: true);

        var after = grid.Fit(GridMetric.PoutDbm);
        double afterValue = after.Evaluate(0.15, -0.1);

        output.WriteLine($"smooth=1e-3: {beforeValue:F6} dBm, smooth=0.5: {afterValue:F6} dBm, " +
                         $"factorizations before={factorizationsBefore} after={grid.FactorizationCount}");

        // A NEW factorization happened (the cache was not stale-reused)...
        Assert.Equal(factorizationsBefore + 1, grid.FactorizationCount);
        // ...and it actually moved the surface — proving this is real invalidation, not just a counter.
        Assert.NotEqual(beforeValue, afterValue, precision: 6);
    }

    [Fact]
    public void ContourSettings_RoundTripThroughCharm_AndAnOlderCharmOpensAtHarmonicaSettingsOwnDefaults()
    {
        var model = Model() with
        {
            Settings = Model().Settings with
            {
                ContourKernel = RbfKernel.Gaussian, ContourSmooth = 0.25, ContourEpsilon = 0.7,
            },
        };
        var terms = Terms(model);

        string json = CharmIo.Write(model, terms);
        var (back, _) = CharmIo.Read(json, null, out var unresolved, withMarkers: true);
        Assert.Empty(unresolved);

        Assert.Equal(RbfKernel.Gaussian, back.Settings.ContourKernel);
        Assert.Equal(0.25, back.Settings.ContourSmooth);
        Assert.Equal(0.7,  back.Settings.ContourEpsilon);

        // R8A §5 — absent (an older .charm, or a hand-written one) takes HarmonicaSettings's own
        // defaults for ALL THREE now, ContourEpsilon included: it is no longer null-means-auto BY
        // DEFAULT (CharmIo:334's `?? defaults.ContourEpsilon`), so an old file lands on 0.5 exactly
        // like every neighbouring field here, not on Rbf2D's own auto null.
        var (older, _) = CharmIo.Read("""{ "FormatVersion": 1 }""", null, out _, withMarkers: true);
        Assert.Equal(new HarmonicaSettings().ContourKernel,  older.Settings.ContourKernel);
        Assert.Equal(new HarmonicaSettings().ContourSmooth,  older.Settings.ContourSmooth);
        Assert.Equal(new HarmonicaSettings().ContourEpsilon, older.Settings.ContourEpsilon);
    }

    [Fact]
    public void Tier6_TheExistingConstructorIsUntouched()
    {
        // R-hrf-9 is ADDITIVE, and Rbf2D is on the critical path of the shipping loadpull contour
        // display, so the old path is pinned independently of the new one.
        //
        // The oracle is scipy's own documented epsilon formula, computed here from the node bounding
        // box rather than read back off the object — that is the one number the factored path had to
        // reproduce and the one a refactor of the constructor would most plausibly move.
        double[] re = [0.0, 0.5, -0.5, 0.0];
        double[] im = [0.0, 0.0,  0.0, 0.5];
        double[] v  = [1.0, 2.0,  3.0, 4.0];

        var fit = new Rbf2D(re, im, v);
        Assert.Equal(4, fit.NodeCount);
        Assert.Equal(Math.Sqrt(1.0 * 0.5 / 4), fit.Epsilon, 15);

        // It is a SMOOTHING spline, not an exact interpolant — the constructor applies
        // `A[i,i] -= smooth` with smooth = 1e-3, scipy's convention — so it passes NEAR its nodes
        // rather than through them. Asserting equality here would be asserting against the
        // smoothing, and would go red the moment anyone changed a default they are entitled to.
        for (int i = 0; i < 4; i++)
        {
            double got = fit.Evaluate(re[i], im[i]);
            output.WriteLine($"node {i}: value {v[i]}, fit {got:F6}, deviation {got - v[i]:+0.000000;-0.000000}");
            Assert.True(Math.Abs(got - v[i]) < 0.05,
                $"the smoothing spline should still pass close to node {i}: {got:F6} vs {v[i]}");
        }
    }

    // ── TIER 7 — the excludeHoleDiscs:true MASK still works, opt-in ────────────
    //
    // R8A §6 REVERSED the doctrine these two tests originally pinned: `Contours()`'s own DEFAULT now
    // SPANS a hole rather than breaking at it (see `ContourGridHoleSpanTests` for that new-default
    // gate). What survives here is the MECHANISM itself — `Raster(..., excludeHoleDiscs: true)`,
    // which the optimum-search path (`InterpolatedArgmax`) still depends on unconditionally — so
    // these are rewritten to exercise that explicit opt-in rather than deleted.

    [Fact]
    public void Tier7_ExcludeHoleDiscsTrue_StillProducesNoIsoLineInsideTheHole()
    {
        // A synthetic grid, because the claim is about the MASK rather than about the physics: a
        // ring set with one interior point removed, and a metric shaped so an unmasked RBF would
        // certainly draw a contour through the gap.
        var gammas = ContourGrid.RingGrid(rings: 3, spokes: 12, maxGamma: 0.75);
        var grid = new ContourGrid();

        // The hole is one of the middle-ring points — an INTERIOR hole, which is the case that
        // matters. An edge hole would be excluded by the convex hull anyway.
        int holeIndex = 1 + 12 + 3;
        Complex holeGamma = gammas[holeIndex];

        SeedSyntheticFor(grid, gammas, holeIndex);

        Assert.Equal(1, grid.HoleCount);
        Assert.Equal(gammas.Length - 1, grid.ConvergedCount);

        double radius = grid.HoleRadius;
        // R8A §6 — `Contours()` no longer exposes the exclude switch (its own default is to span),
        // so the opt-in path is built the same way `Contours()` builds its own default: raster then
        // extract, just with excludeHoleDiscs explicit.
        var raster = grid.Raster(GridMetric.PoutDbm, 201, excludeHoleDiscs: true);
        var set    = ContourExtractor.LevelsBetween(raster, 12);
        var polylines = ContourExtractor.Extract(raster, set);

        output.WriteLine($"hole at Γ = {holeGamma:G4}, excluded disc radius {radius:F4}, " +
                         $"{polylines.Count} iso-polylines extracted");

        int inside = 0;
        foreach (var poly in polylines)
            foreach (var (x, y) in poly.Points)
            {
                double dr = x - holeGamma.Real, di = y - holeGamma.Imaginary;
                if (dr * dr + di * di < radius * radius) inside++;
            }

        Assert.Equal(0, inside);

        // And the check that stops this passing vacuously: contours must actually EXIST, and they
        // must pass near the hole — otherwise a mask that blanked everything would score perfectly.
        Assert.True(polylines.Count > 0, "no contours were drawn at all");
        int nearby = polylines.SelectMany(p => p.Points).Count(pt =>
        {
            double dr = pt.X - holeGamma.Real, di = pt.Y - holeGamma.Imaginary;
            double d  = Math.Sqrt(dr * dr + di * di);
            return d >= radius && d < 2.5 * radius;
        });
        output.WriteLine($"{nearby} vertices sit in the annulus just outside the excluded disc");
        Assert.True(nearby > 0,
            "the contours must reach the hole's edge, or the mask is not what excluded them");
    }

    [Fact]
    public void Tier7_WithoutTheMaskTheRbfDoesInventASurfaceInTheHole()
    {
        // The companion that gives Tier 7 its teeth. If the raw fit had no value inside the hole
        // either, the mask would be doing nothing and the test above would prove nothing.
        var gammas = ContourGrid.RingGrid(rings: 3, spokes: 12, maxGamma: 0.75);
        var grid = new ContourGrid();
        int holeIndex = 1 + 12 + 3;
        Complex holeGamma = gammas[holeIndex];

        SeedSyntheticFor(grid, gammas, holeIndex);

        var fit = grid.Fit(GridMetric.PoutDbm);
        double invented = fit.Evaluate(holeGamma.Real, holeGamma.Imaginary);

        output.WriteLine($"the unmasked RBF evaluates to {invented:F3} dBm inside the hole — " +
                         "a perfectly plausible number with no measurement behind it");
        Assert.True(double.IsFinite(invented) && invented > 0,
            "the raw fit must produce something inside the hole, or the mask has nothing to suppress");

        // R8A §6 — excludeHoleDiscs: true, explicitly: Raster's own DEFAULT now spans the hole (the
        // point of this brief), so the masked behaviour this test is about is opt-in from here on.
        var raster = grid.Raster(GridMetric.PoutDbm, resolution: 201, excludeHoleDiscs: true);
        int xi = NearestIndex(raster.XSpace, holeGamma.Real);
        int yi = NearestIndex(raster.YSpace, holeGamma.Imaginary);
        Assert.True(double.IsNaN(raster.Values[yi * raster.XSpace.Length + xi]));
    }

    // ── D6 — MXP / MXE are the argmax over the computed grid ──────────────────

    [Fact]
    public void D6_MxpAndMxeAreTheArgmaxOverTheGridAndNeverAHole()
    {
        var gammas = ContourGrid.RingGrid(rings: 2, spokes: 8, maxGamma: 0.6);
        var grid = new ContourGrid();
        SeedSyntheticFor(grid, gammas, holeIndex: 5);

        var mxp = grid.Mxp;
        var mxe = grid.Mxe;

        Assert.NotNull(mxp);
        Assert.NotNull(mxe);
        Assert.False(mxp!.Point.IsHole);
        Assert.False(mxe!.Point.IsHole);

        // Argmax over the COMPUTED points, so the readout can never disagree with what is drawn.
        double bestPout = grid.Points.Select(p => p.Metric(GridMetric.PoutDbm))
                                     .Where(v => !double.IsNaN(v)).Max();
        Assert.Equal(bestPout, mxp.Value);

        output.WriteLine($"MXP at Γ = {mxp.Point.Gamma:G4}, {mxp.Value:F2} dBm  " +
                         $"(index {mxp.Index} of {grid.Points.Count})");
        output.WriteLine($"MXE at Γ = {mxe.Point.Gamma:G4}, {mxe.Value:F2} %");
    }

    [Fact]
    public void R9_TwoMetricsOnOneGridShareOneFactorization()
    {
        var gammas = ContourGrid.RingGrid(rings: 3, spokes: 12, maxGamma: 0.75);
        var grid = new ContourGrid();
        SeedSyntheticFor(grid, gammas, holeIndex: 7);

        grid.Fit(GridMetric.PoutDbm);
        grid.Fit(GridMetric.DrainEfficiency);
        grid.Fit(GridMetric.Pae);

        // All three metrics share the grid's NaN mask (a hole is a hole for every metric), so one
        // factorization serves all three — the §6.4.1 claim, counted rather than assumed.
        Assert.Equal(1, grid.FactorizationCount);

        grid.InvalidateValues();
        grid.Fit(GridMetric.PoutDbm);
        Assert.Equal(1, grid.FactorizationCount);
        output.WriteLine("three metrics and a values-only invalidation: 1 factorization");
    }

    // ── §3 (R1C) — the grid-build progress callback ────────────────────────────

    [Fact]
    public void Build_ReportsOneTickPerPoint_AndTheFinalTickReachesTheTotal()
    {
        var model = Model();
        var ctx   = HarmonicaContext.Create(model, Settings);
        var terms = Terms(model);
        var scatter = ContourGrid.RingGrid(rings: 2, spokes: 6);   // 13 points

        var ticks = new List<(int Done, int Total)>();
        var grid = new ContourGrid();
        grid.Build(ctx, terms, scatter, onProgress: (done, total) => ticks.Add((done, total)));

        Assert.Equal(scatter.Length, ticks.Count);
        for (int i = 0; i < ticks.Count; i++)
        {
            Assert.Equal(i + 1, ticks[i].Done);
            Assert.Equal(scatter.Length, ticks[i].Total);
        }

        // The bar can never land short: the LAST point always ticks, unthrottled.
        Assert.Equal((scatter.Length, scatter.Length), ticks[^1]);
    }

    [Fact]
    public void Build_TicksAReusedPointToo_SoTheFractionReflectsTotalCompletionNotJustFreshSolves()
    {
        var model = Model();
        var ctx   = HarmonicaContext.Create(model, Settings);
        var terms = Terms(model);
        var scatter = ContourGrid.RingGrid(rings: 2, spokes: 6).ToArray();

        var grid = new ContourGrid();
        grid.Build(ctx, terms, scatter, reuseUnchanged: true);   // seeds _reusableAgainst
        grid.Build(ctx, terms, scatter, reuseUnchanged: true);   // every point now reusable

        int ticks = 0;
        grid.Build(ctx, terms, scatter, reuseUnchanged: true, onProgress: (_, _) => ticks++);

        Assert.Equal(scatter.Length, grid.ReusedPointCount);
        Assert.Equal(scatter.Length, ticks);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int NearestIndex(double[] axis, double v)
    {
        int best = 0;
        for (int i = 1; i < axis.Length; i++)
            if (Math.Abs(axis[i] - v) < Math.Abs(axis[best] - v)) best = i;
        return best;
    }

    /// <summary>
    /// Fills a grid with synthetic Pin-search results — a smooth power/efficiency dome, and one
    /// point marked as a hole. Synthetic on purpose: Tier 7 is a claim about the MASK, and driving
    /// real HB solves would put the physics in the way of the thing being tested.
    /// </summary>
    internal static void SeedSyntheticFor(ContourGrid grid, IReadOnlyList<Complex> gammas, int holeIndex)
    {
        var points = (List<GridPoint>)typeof(ContourGrid)
            .GetField("_points", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(grid)!;

        for (int i = 0; i < gammas.Count; i++)
        {
            Complex g = gammas[i];
            Complex z = 50 * (Complex.One + g) / (Complex.One - g);

            if (i == holeIndex)
            {
                points.Add(new GridPoint(g, z,
                    new PinSearchResult(PinStopReason.PinMax, 6) { Steps = [] }));
                continue;
            }

            // A dome offset from the origin, so the contours are real curves rather than circles
            // centred on the hole.
            double poutW = 8.0 * Math.Exp(-4.0 * ((g.Real - 0.25) * (g.Real - 0.25)
                                                + (g.Imaginary + 0.15) * (g.Imaginary + 0.15)));
            double pdc   = 20.0;
            var pt = new OperatingPoint(new Complex[1, 1], new Complex[1, 1], true, 3, 0)
            {
                YNN = [], ISrc = [], INlTotal = new Complex[1, 1], Residual = 0,
            };
            var step = new PinStep(0, 3.0, pt)
            {
                Foms = new CircuitRF.Engine.Loadpull.LoadpullEngine.FomResult(1, 0.5, poutW, 0, 0),
                PdcW = pdc,
            };

            points.Add(new GridPoint(g, z,
                new PinSearchResult(PinStopReason.Compression, 7)
                {
                    Steps = [step], AtCompression = step, SmallSignalGainDb = 15,
                }));
        }
    }
}
