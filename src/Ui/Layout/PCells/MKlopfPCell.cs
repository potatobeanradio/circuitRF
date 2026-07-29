using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MKlopf artwork (brief-mtaper-mklopf.md §2-3): the Klopfenstein-taper outline, straight or offset.
/// R-pc-3: pin 1 at the origin, running to +X (the AXIAL span; the Offset centerline may curve away
/// from the X axis but always starts and ends on it, per R-klp-7's own G2-continuity choice).
///
/// <b>R-tap-2 applies here too: the tessellation count below is a GEOMETRIC decision (a fixed,
/// generous sample count for a smooth-looking curve), fully independent of the electrical section
/// count <c>MicrostripKlopfModel</c>/<c>MicrostripCascadeSectioning</c> resolves per frequency.</b>
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
    private const int TessellationPoints = 96; // fixed geometric fidelity, independent of electrical N
    private const double BlendWidthMultiple = 3.0;

    public static PCellResult Generate(
        IReadOnlyDictionary<string, double> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double z1 = parameters.GetValueOrDefault("Z1", 50.0);
        double z2 = parameters.GetValueOrDefault("Z2", 100.0);
        double gammaMax = parameters.GetValueOrDefault("GammaMax", 0.05);
        double lMeters = parameters.GetValueOrDefault("L", 0.02);
        double offsetMeters = parameters.GetValueOrDefault("Offset", 0.0);
        bool smoothSteps = parameters.GetValueOrDefault("SmoothSteps", 1.0) != 0.0;

        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
        double hMeters = substrate?.HeightMeters ?? 1.6e-3;
        double tMeters = substrate?.ThicknessMeters ?? 35e-6;
        double epsR = substrate?.RelativePermittivity ?? 4.4;

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);
        var reporter = new MicrostripValidityReporter(GeneratorId);

        double totalArc = MicrostripOffsetCenterline.TotalArcLength(lMeters, offsetMeters);
        double w1 = HammerstadJensen.SynthesizeWidth(z1, hMeters, tMeters, epsR, reporter);
        double w2 = HammerstadJensen.SynthesizeWidth(z2, hMeters, tMeters, epsR, reporter);
        double blendLen1 = BlendWidthMultiple * w1;
        double blendLen2 = BlendWidthMultiple * w2;

        // Sample centerline position, tangent, and (pre-blend) width at each tessellation point.
        var xs = new double[TessellationPoints + 1];
        var ys = new double[TessellationPoints + 1];
        var tangentAngles = new double[TessellationPoints + 1];
        var widths = new double[TessellationPoints + 1];
        var arcPositions = new double[TessellationPoints + 1];

        for (int i = 0; i <= TessellationPoints; i++)
        {
            double x = lMeters * i / TessellationPoints;
            double y = MicrostripOffsetCenterline.Y(x, lMeters, offsetMeters);
            double slope = MicrostripOffsetCenterline.DyDx(x, lMeters, offsetMeters);
            double arc = MicrostripOffsetCenterline.ArcLength(0.0, x, lMeters, offsetMeters);
            double sFrac = totalArc > 0 ? arc / totalArc : (double)i / TessellationPoints;

            double z = KlopfensteinTaper.ImpedanceAt(sFrac, z1, z2, gammaMax);
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
            ApplyEndBlend(xs, arcPositions, widths, TessellationPoints, blendLen2, w2, fromStart: false);
        }

        var leftEdge = new (double X, double Y)[TessellationPoints + 1];
        var rightEdge = new (double X, double Y)[TessellationPoints + 1];
        for (int i = 0; i <= TessellationPoints; i++)
        {
            double nx = -Math.Sin(tangentAngles[i]);
            double ny = Math.Cos(tangentAngles[i]);
            double half = widths[i] / 2.0;
            leftEdge[i] = (xs[i] + nx * half, ys[i] + ny * half);
            rightEdge[i] = (xs[i] - nx * half, ys[i] - ny * half);
        }

        var xy = new List<long>((TessellationPoints + 1) * 4);
        foreach (var p in leftEdge)
        {
            xy.Add(PCellUnits.MetresToDbu(p.X, dbuPerMicron));
            xy.Add(PCellUnits.MetresToDbu(p.Y, dbuPerMicron));
        }
        for (int i = TessellationPoints; i >= 0; i--)
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

        return new PCellResult([outline], pins);
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
