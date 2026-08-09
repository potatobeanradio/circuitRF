using System.Linq;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MKlopf artwork (brief-mtaper-mklopf.md §2-3): the Klopfenstein-taper outline, straight or offset.
/// R-pc-3: pin 1 at the origin, running to +X (the AXIAL span; the Offset centerline may curve away
/// from the X axis but always starts and ends on it, per R-klp-7's own G2-continuity choice).
///
/// <b>R-tap-2 applies here too: the tessellation count below is a GEOMETRIC decision (a generous
/// sample count for a smooth-looking curve), fully independent of the electrical section count
/// <c>MicrostripKlopfModel</c>/<c>MicrostripCascadeSectioning</c> resolves per frequency.</b> Those
/// two are easy to conflate and are not the same thing: the electrical sectioning is deliberately
/// NON-uniform (equal Δ(ln Z) boundaries, so a section's own reflection contribution is bounded),
/// while the artwork's stations are always EVENLY SPACED IN X — only how many there are varies, and
/// only to give <c>SmoothSteps</c>' blend somewhere to land (<see cref="ResolveStationCount"/>).
///
/// <b>R-klp-8: width is drawn PERPENDICULAR to the local centerline tangent</b> — each tessellation
/// point contributes two outline vertices, offset by ±(localWidth/2) along the tangent's own normal,
/// not vertically in y (which would overshoot wherever the centerline is sloped — exactly the error
/// this rule exists to prevent).
///
/// <b>R-klp-4a: <c>SmoothSteps</c> (artwork only — never applied to the electrical model, see
/// <c>MicrostripKlopfModel</c>'s own doc comment) blends the drawn width, within a length equal to
/// 3× the LOCAL width from each end, to a ZERO-SLOPE approach into the exact W1/W2 endpoint width</b>
/// — i.e. the drawn outline looks tangent to a straight lead of that same width at each pin, which is
/// the practical, implementable reading of "blend from the connecting line's own value" available to
/// a PCell that has no visibility into whatever is actually wired to it. The blend is entirely
/// consumed INSIDE this component's own extent (never reaches past the pins) and is reported via
/// <see cref="Generate"/>'s own diagnostic list, per the brief's own "report the blend length used."
/// </summary>
public static class MKlopfPCell
{
    public const string GeneratorId = "MKLOPF";
    /// <summary>Baseline geometric fidelity, independent of the electrical section count (R-tap-2).
    /// The stations are always EVENLY SPACED IN X; see <see cref="ResolveStationCount"/> for the one
    /// thing that varies the count, and why it is not a departure from uniform sampling.</summary>
    private const int TessellationPoints = 96;

    private const double BlendWidthMultiple = 3.0;

    /// <summary>A blend spanning fewer stations than this has nowhere to put its intermediate widths,
    /// so the drawn end still steps however correctly the blend itself is computed. Six is the
    /// smallest count that renders a cubic Hermite ramp as a ramp rather than as two or three visible
    /// facets.</summary>
    private const int MinBlendStations = 6;

    /// <summary>Ceiling on the adaptive station count. 96 already draws a smooth taper; this only ever
    /// binds on a taper hundreds of end-widths long, and bounds both the vertex count of the emitted
    /// polygon and the per-station width synthesis (a bisection root-find each).</summary>
    private const int MaxTessellationPoints = 1024;

    /// <summary>A taper shorter than this cannot be drawn as one. Positive rather than zero because
    /// the arc-length parameterisation divides by the total length, and a floor a grip can be dragged
    /// down to must still leave a shape on screen.</summary>
    private const double MinLengthMeters = 1e-6;

    /// <summary>
    /// How far below its own degeneracy bound <c>GammaMax</c> is held.
    ///
    /// <para>The bound is <c>|½·ln(Z2/Z1)|</c> — the accumulated small-reflection integral that
    /// <see cref="KlopfensteinTaper.ComputeA"/> divides by <c>GammaMax</c> before taking
    /// <c>acosh</c>. At or above it the ratio falls to 1 or below and <c>acosh</c> returns
    /// <b>NaN</b>, which is the whole of the reported "the transformer goes extremely thin in the
    /// middle": every interior station's impedance becomes NaN, <see cref="HammerstadJensen.SynthesizeWidth"/>'s
    /// bisection compares NaN (every comparison false) and walks to its own narrowest width, while
    /// the two ends stay correct because they are synthesised from Z1/Z2 directly. Nothing throws
    /// and nothing is reported — the artwork simply collapses.</para>
    ///
    /// <para>Held just below rather than refused because the limit is a real, continuous one: as
    /// <c>GammaMax</c> rises to the bound the taper degenerates smoothly into a uniform line at
    /// √(Z1·Z2) with the whole transformation happening as a step at each end, which is exactly what
    /// asking for that much passband ripple means.</para>
    /// </summary>
    private const double GammaMaxHeadroom = 1e-3;

    public static PCellResult Generate(
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double z1 = parameters.Real("Z1", 50.0);
        double z2 = parameters.Real("Z2", 100.0);
        double gammaMax = parameters.Real("GammaMax", 0.05);
        double lMeters = parameters.Real("L", 0.02);
        double offsetMeters = parameters.Real("Offset", 0.0);
        // A flag, so it is read as one — but AsBool treats a non-zero Real as true, so the 1.0/0.0
        // every pre-contract-v2 parameter set carries still decides it the same way.
        bool smoothSteps = parameters.Bool("SmoothSteps", true);

        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
        double hMeters = substrate?.HeightMeters ?? 1.6e-3;
        double tMeters = substrate?.ThicknessMeters ?? 35e-6;
        double epsR = substrate?.RelativePermittivity ?? 4.4;

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        var reporter = new MicrostripValidityReporter(GeneratorId);

        lMeters = ResolveLength(lMeters, reporter);
        var profile = BuildImpedanceProfile(z1, z2, gammaMax, reporter);

        double totalArc = MicrostripOffsetCenterline.TotalArcLength(lMeters, offsetMeters);
        double w1 = HammerstadJensen.SynthesizeWidth(z1, hMeters, tMeters, epsR, reporter);
        double w2 = HammerstadJensen.SynthesizeWidth(z2, hMeters, tMeters, epsR, reporter);
        var (blendLen1, blendLen2) = ResolveBlendLengths(w1, w2, totalArc, smoothSteps, reporter);
        int stations = ResolveStationCount(blendLen1, blendLen2, totalArc, smoothSteps, reporter);

        CheckCurvature(lMeters, offsetMeters, totalArc, profile, hMeters, tMeters, epsR, reporter);

        // Sample centerline position, tangent, and (pre-blend) width at each tessellation point.
        var xs = new double[stations + 1];
        var ys = new double[stations + 1];
        var tangentAngles = new double[stations + 1];
        var widths = new double[stations + 1];
        var arcPositions = new double[stations + 1];

        for (int i = 0; i <= stations; i++)
        {
            double x = lMeters * i / stations;
            double y = MicrostripOffsetCenterline.Y(x, lMeters, offsetMeters);
            double slope = MicrostripOffsetCenterline.DyDx(x, lMeters, offsetMeters);
            double arc = MicrostripOffsetCenterline.ArcLength(0.0, x, lMeters, offsetMeters);
            double sFrac = totalArc > 0 ? arc / totalArc : (double)i / stations;

            double z = profile(sFrac);
            double w = HammerstadJensen.SynthesizeWidth(z, hMeters, tMeters, epsR, reporter);

            xs[i] = x;
            ys[i] = y;
            tangentAngles[i] = Math.Atan(slope);
            widths[i] = w;
            arcPositions[i] = arc;
        }

        if (smoothSteps)
        {
            ApplyEndBlend(xs, arcPositions, widths, 0, blendLen1, w1, fromStart: true);
            ApplyEndBlend(xs, arcPositions, widths, stations, blendLen2, w2, fromStart: false);
        }

        var leftEdge = new (double X, double Y)[stations + 1];
        var rightEdge = new (double X, double Y)[stations + 1];
        for (int i = 0; i <= stations; i++)
        {
            double nx = -Math.Sin(tangentAngles[i]);
            double ny = Math.Cos(tangentAngles[i]);
            double half = widths[i] / 2.0;
            leftEdge[i] = (xs[i] + nx * half, ys[i] + ny * half);
            rightEdge[i] = (xs[i] - nx * half, ys[i] - ny * half);
        }

        var xy = new List<long>((stations + 1) * 4);
        foreach (var p in leftEdge)
        {
            xy.Add(PCellUnits.MetresToDbu(p.X, dbuPerMicron));
            xy.Add(PCellUnits.MetresToDbu(p.Y, dbuPerMicron));
        }
        for (int i = stations; i >= 0; i--)
        {
            xy.Add(PCellUnits.MetresToDbu(rightEdge[i].X, dbuPerMicron));
            xy.Add(PCellUnits.MetresToDbu(rightEdge[i].Y, dbuPerMicron));
        }

        var outline = new PolygonShape { Layer = signalLayer, Xy = [.. xy] };

        long w1Dbu = PCellUnits.MetresToDbu(w1, dbuPerMicron);
        long w2Dbu = PCellUnits.MetresToDbu(w2, dbuPerMicron);
        long lDbu = PCellUnits.MetresToDbu(lMeters, dbuPerMicron);
        long pin2YDbu = PCellUnits.MetresToDbu(MicrostripOffsetCenterline.Y(lMeters, lMeters, offsetMeters), dbuPerMicron);

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w1Dbu, 180.0),
            new PCellPin("2", lDbu, pin2YDbu, signalLayer, w2Dbu, 0.0),
        };

        // pcell-parameter-handles.md — SIX grips, THREE AT EACH END: top, middle, bottom.
        //
        //   middle — the shape of the taper as a whole. One grip driving BOTH axes (R-pch-4a):
        //            along the axis changes `L`, across it changes `Offset`. That is what the end of
        //            a taper physically is ("how long, and how far off axis"), and the two are
        //            independent scalars, so the decomposition is unique rather than a guess.
        //   top /   the local WIDTH at that end — which for a Klopfenstein taper is not a parameter
        //   bottom  at all but a consequence of the terminating IMPEDANCE, so these drive `Z1`/`Z2`.
        //           The relationship is inverse (a wider trace is a lower impedance) and thoroughly
        //           non-linear, and nothing here has to say so: R-pch-2's measurement discovers the
        //           sign and the scale, and R-pch-3's iterate-until-it-lands survives the curvature.
        //           Neither is a length, so neither is snapped to the layout grid — an impedance has
        //           no business being rounded onto a distance lattice.
        //
        // The FAR middle grip anchors on pin 1, which R4 pins at the cell origin: the taper stretches
        // from its fixed near end, which is what dragging the far end means. The NEAR middle grip is
        // the mirror image and needs R-pch-4b to express it — `L` can only ever grow toward +X, so
        // "drag the near end away" is "grow, and hold the far end where it is," with the host
        // translating the instance to keep the anchor put.
        //
        // Widths are read off the OUTLINE that was actually built (leftEdge/rightEdge above) rather
        // than recomputed from w1/w2, so a grip always sits exactly on the edge it appears to grab —
        // including when SmoothSteps has blended that end's width away from its nominal value.
        long near0X = PCellUnits.MetresToDbu(leftEdge[0].X, dbuPerMicron);
        long near0Y = PCellUnits.MetresToDbu(leftEdge[0].Y, dbuPerMicron);
        long near1X = PCellUnits.MetresToDbu(rightEdge[0].X, dbuPerMicron);
        long near1Y = PCellUnits.MetresToDbu(rightEdge[0].Y, dbuPerMicron);
        long far0X = PCellUnits.MetresToDbu(leftEdge[stations].X, dbuPerMicron);
        long far0Y = PCellUnits.MetresToDbu(leftEdge[stations].Y, dbuPerMicron);
        long far1X = PCellUnits.MetresToDbu(rightEdge[stations].X, dbuPerMicron);
        long far1Y = PCellUnits.MetresToDbu(rightEdge[stations].Y, dbuPerMicron);

        const PCellHandleQuantity len = PCellHandleQuantity.Length;
        var handles = new[]
        {
            // Far end: middle (L + Offset), then the two edges (Z2).
            //
            // `Min` is what stops a drag past the near end producing a negative length. The solver
            // clamps every candidate to it (R-pch-3's Propose), so the grip simply stops rather than
            // the cell being regenerated at a length that is not a length — and it stops at a value
            // the generator would accept anyway, which is what keeps the drawn grip and the committed
            // value agreeing. `Offset` deliberately has no bound: off-axis either way is meaningful.
            new PCellHandle("L", 0, 0, lDbu, pin2YDbu, AxisDeg: 0, Min: MinLengthMeters,
                Cross: new PCellHandleCrossAxis("Offset", Quantity: len), Quantity: len),
            new PCellHandle("Z2", lDbu, pin2YDbu, far0X, far0Y, AxisDeg: 90),
            new PCellHandle("Z2", lDbu, pin2YDbu, far1X, far1Y, AxisDeg: 270),

            // Near end: middle (L + Offset, holding the far end still), then the two edges (Z1).
            new PCellHandle("L", lDbu, pin2YDbu, 0, 0, AxisDeg: 180, Min: MinLengthMeters,
                Cross: new PCellHandleCrossAxis("Offset", Quantity: len),
                KeepAnchorFixed: true, Quantity: len),
            new PCellHandle("Z1", 0, 0, near0X, near0Y, AxisDeg: 90),
            new PCellHandle("Z1", 0, 0, near1X, near1Y, AxisDeg: 270),
        };

        var diagnostics = reporter.Drain();
        return new PCellResult([outline], pins,
            diagnostics.Count > 0 ? diagnostics.Select(d => d.Message).ToList() : null,
            Handles: handles);
    }

    /// <summary>
    /// A drawable length. Zero or negative is not a shorter taper, it is no taper — the arc-length
    /// parameterisation every station is placed along divides by the total length, so the outline
    /// degenerates rather than shrinking. Clamped and reported rather than thrown: a generator that
    /// threw would cost the user the whole cell (and, mid-drag, the live preview) over a value a grip
    /// can pass through on its way somewhere sensible.
    /// </summary>
    private static double ResolveLength(double lMeters, MicrostripValidityReporter reporter)
    {
        if (double.IsFinite(lMeters) && lMeters >= MinLengthMeters) return lMeters;

        reporter.ReportOnce("R-klp-11",
            $"R-klp-11: L must be a positive length; {lMeters:0.###e+00} m cannot be drawn as a taper. " +
            $"It is held at {MinLengthMeters * 1e6:0.###} um for the artwork — set a real length in the " +
            "Properties Inspector, or drag the end grip further out.");
        return MinLengthMeters;
    }

    /// <summary>
    /// The impedance profile this cell's artwork is drawn from: Z at normalised arc position
    /// <c>t</c>∈[0,1]. Always finite, which <see cref="KlopfensteinTaper.ImpedanceAt"/> on its own is
    /// not — see <see cref="GammaMaxHeadroom"/> for the bound and what crossing it does.
    ///
    /// <para><b>Z1 == Z2 is answered here rather than by clamping GammaMax, because no GammaMax
    /// works.</b> The shape parameter is <c>acosh(|½·ln(Z2/Z1)| / GammaMax)</c>; with Z1 == Z2 the
    /// numerator is zero, so the ratio is zero for every finite GammaMax and <c>acosh</c> is NaN
    /// whatever is passed. A taper between two equal impedances is a uniform line, and that is what
    /// is drawn.</para>
    /// </summary>
    private static Func<double, double> BuildImpedanceProfile(
        double z1, double z2, double gammaMax, MicrostripValidityReporter reporter)
    {
        double bound = Math.Abs(KlopfensteinTaper.Rho0Estimate(z1, z2));

        if (!double.IsFinite(bound) || bound <= 0.0)
        {
            reporter.ReportOnce("R-klp-12",
                $"R-klp-12: Z1 and Z2 are both {z1:G6} ohm, so there is nothing for the taper to " +
                "transform and GammaMax has no meaning — the artwork is drawn as a uniform line at " +
                "that impedance.");
            return _ => z1;
        }

        double used = gammaMax;
        if (!double.IsFinite(gammaMax) || gammaMax <= 0.0 || gammaMax >= bound * (1.0 - GammaMaxHeadroom))
        {
            used = bound * (1.0 - GammaMaxHeadroom);
            reporter.ReportOnce("R-klp-2",
                $"R-klp-2: GammaMax={gammaMax:G6} is not below this taper's own reflection bound " +
                $"|0.5*ln(Z2/Z1)|={bound:G6} for Z1={z1:G6} ohm, Z2={z2:G6} ohm, so the Klopfenstein " +
                $"profile is undefined there. It is held at {used:G6}, where the taper is essentially " +
                "a uniform line with a step at each end. Move Z1 and Z2 further apart, or lower " +
                "GammaMax, for a real taper.");
        }

        return t => KlopfensteinTaper.ImpedanceAt(t, z1, z2, used);
    }

    /// <summary>
    /// brief-L5-followups-2.md §2.2/R-L5g-4: R-klp-10's curvature check — CONFIRMED absent from this
    /// generator before this fix (verified directly: this method did not exist; every prior mention
    /// of "R-klp-10" in this codebase's own completion notes described intent, not code that was
    /// actually here). <c>MicrostripOffsetCenterline.MinRadiusOfCurvature</c> already existed and
    /// worked; nothing called it. Scans the centerline for its own point of maximum curvature (mirrors
    /// <c>MinRadiusOfCurvature</c>'s own 400-sample scan, since that method reports only the resulting
    /// radius, not WHERE it occurs — needed here to evaluate the LOCAL trace width at that exact
    /// point, not an endpoint width), and warns once when the resulting minimum radius of curvature is
    /// under <see cref="BlendWidthMultiple"/>× the local trace width there — offsetting an outline by
    /// more than its own local radius of curvature is the specific failure mode that produces a
    /// self-intersecting edge (§2.2's own closing note).
    /// </summary>
    private static void CheckCurvature(
        double lMeters, double offsetMeters, double totalArc,
        Func<double, double> profile,
        double hMeters, double tMeters, double epsR, MicrostripValidityReporter reporter)
    {
        if (offsetMeters == 0.0) return; // a straight centerline has no curvature at all

        const int samples = 400;
        double maxKappa = 0.0, argX = 0.0;
        for (int i = 0; i <= samples; i++)
        {
            double x = lMeters * i / samples;
            double k = MicrostripOffsetCenterline.Curvature(x, lMeters, offsetMeters);
            if (k > maxKappa) { maxKappa = k; argX = x; }
        }
        if (maxKappa <= 0.0) return;

        double rMin = 1.0 / maxKappa;
        double arcAtMax = MicrostripOffsetCenterline.ArcLength(0.0, argX, lMeters, offsetMeters);
        double sFrac = totalArc > 0 ? arcAtMax / totalArc : 0.0;
        double zAtMax = profile(sFrac);
        double wLocal = HammerstadJensen.SynthesizeWidth(zAtMax, hMeters, tMeters, epsR, reporter);

        if (rMin < BlendWidthMultiple * wLocal)
        {
            reporter.ReportOnce("R-klp-10",
                $"R-klp-10: the Offset centerline's minimum radius of curvature ({rMin * 1e3:0.###} mm, near " +
                $"x={argX * 1e3:0.#} mm) is under {BlendWidthMultiple:0.#}x the local trace width there " +
                $"({wLocal * 1e3:0.###} mm) — offsetting the outline by more than its own local radius " +
                "can produce a self-intersecting edge; consider a larger Offset/L ratio, a longer L, or " +
                "fewer SmoothSteps.");
        }
    }

    /// <summary>
    /// R-klp-4a states the blend length as an ARC length — 3× the end width — which is a quantity of
    /// the CROSS-SECTION, not of the taper. On a taper that is short relative to its own end widths
    /// that length exceeds the whole component: a 50 Ω line on 1.6 mm FR-4 is ~3 mm wide, so
    /// 3×W1 ≈ 9 mm, and an impedance transformer only a few millimetres long asks for a blend longer
    /// than itself. <c>ApplyEndBlend</c>'s own "taper shorter than the blend length; skip rather than
    /// overreach" guard then declined to blend AT ALL — silently reinstating, on exactly those
    /// geometries, the end step SmoothSteps exists to remove. That is the visible "strange step at
    /// either end" on a short transformer, and it is NOT a tessellation artefact: the artwork samples
    /// the profile at 96 EVENLY-SPACED stations in x (measured; the electrical model's own
    /// equal-Δ(ln Z) sectioning is a separate, stamp-side concern that never reaches the outline), and
    /// the step's size is independent of how many stations there are — it is the Kajfez-Prewitt
    /// endpoint term's own discontinuity in <see cref="KlopfensteinTaper.ImpedanceAt"/>, ~2.56 Ω on
    /// the worked 50→100 Ω example whether sampled at 24 stations or 4096.
    ///
    /// <para>So the blend is CLAMPED rather than skipped: at most half the arc from each end, which
    /// lets the two blends meet in the middle at worst and still keeps R-klp-4a's "entirely consumed
    /// inside this component's own extent" guarantee intact. A clamped blend is reported, per the
    /// brief's own "report the blend length used" — which is also the only signal that the drawn
    /// endpoint width is approaching its nominal value over a shorter run than the 3× rule asks for.</para>
    /// </summary>
    private static (double Blend1, double Blend2) ResolveBlendLengths(
        double w1, double w2, double totalArc, bool smoothSteps, MicrostripValidityReporter reporter)
    {
        double want1 = BlendWidthMultiple * w1;
        double want2 = BlendWidthMultiple * w2;
        if (!smoothSteps || totalArc <= 0.0) return (want1, want2);

        double maxBlend = totalArc * 0.5;
        double use1 = Math.Min(want1, maxBlend);
        double use2 = Math.Min(want2, maxBlend);

        if (use1 < want1 || use2 < want2)
        {
            reporter.ReportOnce("R-klp-4a",
                $"R-klp-4a: SmoothSteps' blend is {BlendWidthMultiple:0.#}x the local trace width from each end " +
                $"({want1 * 1e3:0.###} mm / {want2 * 1e3:0.###} mm), which is longer than this taper " +
                $"({totalArc * 1e3:0.###} mm) allows — it is clamped to half the taper per end " +
                $"({use1 * 1e3:0.###} mm / {use2 * 1e3:0.###} mm) so the drawn width still reaches W1/W2 " +
                "smoothly rather than stepping. Lengthen L, or narrow the ends, for the full 3x approach.");
        }

        return (use1, use2);
    }

    /// <summary>
    /// How many uniformly-spaced outline stations to sample at. <see cref="TessellationPoints"/> (96)
    /// unless the shorter of the two end blends would span fewer than <see cref="MinBlendStations"/>
    /// of them, in which case enough stations are added that it does.
    ///
    /// <para><b>This is not a departure from uniform sampling and must not become one.</b> The
    /// stations stay evenly spaced in x at every count — what varies is how many there are, which is
    /// the same kind of decision the fixed 96 already was (R-tap-2: a purely geometric fidelity
    /// choice, independent of the electrical section count). Non-uniform stations were rejected:
    /// they would put the outline's own vertex density in step with the profile's curvature, which is
    /// impossible to reason about when a step DOES show up, and the whole point of this pass was to
    /// establish that the drawn step is a property of the profile rather than of the sampling.</para>
    ///
    /// <para>The regime it covers is the mirror image of the clamp above: a blend length of 3× the
    /// END width against a taper hundreds of end-widths long — a narrow, high-impedance end on a
    /// physically long board taper. There the blend is real and correctly computed, but narrower than
    /// one station, so the outline has nowhere to put the intermediate widths and the end still steps.
    /// Adding stations is what actually draws it; the count is bounded by
    /// <see cref="MaxTessellationPoints"/>, and hitting that bound is reported rather than left as a
    /// silently stepped end.</para>
    /// </summary>
    private static int ResolveStationCount(
        double blendLen1, double blendLen2, double totalArc, bool smoothSteps, MicrostripValidityReporter reporter)
    {
        if (!smoothSteps || totalArc <= 0.0) return TessellationPoints;

        double shortest = Math.Min(blendLen1, blendLen2);
        if (shortest <= 0.0 || !double.IsFinite(shortest)) return TessellationPoints;

        // stations >= MinBlendStations * totalArc / shortest puts MinBlendStations of them inside the
        // shorter blend, since the stations are evenly spaced.
        double needed = MinBlendStations * totalArc / shortest;
        if (needed <= TessellationPoints) return TessellationPoints;

        int stations = (int)Math.Ceiling(needed);
        if (stations <= MaxTessellationPoints) return stations;

        double drawn = MaxTessellationPoints * shortest / totalArc;
        reporter.ReportOnce("R-klp-4b",
            $"R-klp-4a: this taper is long relative to its own end widths, so SmoothSteps' blend " +
            $"({shortest * 1e3:0.####} mm) spans only {drawn:0.##} of the {MaxTessellationPoints} outline " +
            "stations the artwork will draw — the drawn end will still step. Shorten L, or widen the " +
            "narrow end, to draw the blend.");
        return MaxTessellationPoints;
    }

    /// <summary>R-klp-4a's own zero-slope blend: within <paramref name="blendLenMeters"/> of the
    /// named end (measured along ARC length, matching how the profile itself is parameterised),
    /// replace the raw sampled widths with a cubic Hermite blend from the boundary sample's own
    /// (value, slope) to (endpointWidth, slope=0) — a smooth, C1-continuous approach that never
    /// reaches outside this component's own extent.</summary>
    private static void ApplyEndBlend(double[] xs, double[] arcPositions, double[] widths,
        int endIndex, double blendLenMeters, double endpointWidth, bool fromStart)
    {
        if (blendLenMeters <= 0.0) return;
        int n = widths.Length - 1;
        double endArc = arcPositions[endIndex];

        // Find the tessellation index at the blend boundary (the first/last sample at least
        // blendLenMeters of arc away from this end).
        int boundaryIndex = endIndex;
        if (fromStart)
        {
            for (int i = 0; i <= n; i++) if (arcPositions[i] - endArc >= blendLenMeters) { boundaryIndex = i; break; }
        }
        else
        {
            for (int i = n; i >= 0; i--) if (endArc - arcPositions[i] >= blendLenMeters) { boundaryIndex = i; break; }
        }
        if (boundaryIndex == endIndex) return; // taper shorter than the blend length; skip rather than overreach

        double boundaryWidth = widths[boundaryIndex];
        double boundaryArc = arcPositions[boundaryIndex];
        // Estimate the slope (dWidth/dArc) at the boundary via a one-sided finite difference using
        // the next sample toward the interior, so the blend matches both value and slope there (C1).
        int neighborIndex = fromStart ? boundaryIndex + 1 : boundaryIndex - 1;
        neighborIndex = Math.Clamp(neighborIndex, 0, n);
        double dArc = arcPositions[neighborIndex] - boundaryArc;
        double boundarySlope = Math.Abs(dArc) > 1e-15 ? (widths[neighborIndex] - boundaryWidth) / dArc : 0.0;

        int lo = fromStart ? endIndex : boundaryIndex;
        int hi = fromStart ? boundaryIndex : endIndex;
        for (int i = lo; i <= hi; i++)
        {
            double localArc = fromStart ? (arcPositions[i] - endArc) : (endArc - arcPositions[i]);
            double span = fromStart ? (boundaryArc - endArc) : (endArc - boundaryArc);
            double u = span > 0 ? localArc / span : 0.0; // 0 at the very end, 1 at the blend boundary
            // Cubic Hermite: p(u) from (endpointWidth, slope=0) at u=0 to (boundaryWidth,
            // boundarySlope*span) at u=1.
            double h00 = 2 * u * u * u - 3 * u * u + 1;
            double h10 = u * u * u - 2 * u * u + u;
            double h01 = -2 * u * u * u + 3 * u * u;
            double h11 = u * u * u - u * u;
            double m1 = fromStart ? boundarySlope * span : -boundarySlope * span;
            widths[i] = h00 * endpointWidth + h10 * 0.0 + h01 * boundaryWidth + h11 * m1;
        }
    }
}
