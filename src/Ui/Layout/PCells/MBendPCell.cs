using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// MBend artwork (brief-L5a-pcell-contract-and-microstrip.md §3): two arms of width <c>W</c>
/// meeting at <c>Angle</c> degrees (measured CCW from arm 1's own +X direction, matching this
/// codebase's standing CCW-positive convention — see <c>LayoutArc</c>'s own doc comment), with the
/// outer corner cut when <c>Mitered</c>. R-pc-3: pin 1 at the origin, arm 1 (the input arm) along
/// +X. R-pc-18: mitered and unmitered are DISTINCT discontinuities — this file's own geometry
/// difference between the two (a real corner cut vs. none) is what backs that on the electrical
/// side (<c>MicrostripBendModel</c> in <c>src/Core/Devices/</c>) actually using different models.
/// </summary>
public static class MBendPCell
{
    public const string GeneratorId = "MBEND";

    public static PCellResult Generate(
        IReadOnlyDictionary<string, double> parameters,
        Technology? technology,
        PCellLayerSelection layerSelection)
    {
        double wMeters = parameters.GetValueOrDefault("W", 0.0);
        double angleDeg = parameters.GetValueOrDefault("Angle", 90.0);
        // "Miter" (0=None, 1=Fifty, 2=Optimal — MicrostripBendMiter's own order); "Mitered" (0/1)
        // still read as a fallback for a hand-authored parameter set predating this brief.
        var miter = parameters.ContainsKey("Miter")
            ? (MicrostripBendMiter)(int)Math.Round(parameters["Miter"])
            : (parameters.GetValueOrDefault("Mitered", 0.0) != 0.0 ? MicrostripBendMiter.Optimal : MicrostripBendMiter.None);

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        long w = PCellUnits.MetresToDbu(wMeters, dbuPerMicron);
        long stubLen = (long)Math.Round(PCellGeometryHelpers.StubLengthFactor * w, MidpointRounding.AwayFromZero);

        var signalLayer = SubstrateResolver.ResolveSignalLayerKey(technology, layerSelection, out _);

        long cornerX = stubLen, cornerY = 0;
        var arm1 = PCellGeometryHelpers.BuildArmRect(0, 0, 0.0, stubLen, w, signalLayer);
        var arm2 = PCellGeometryHelpers.BuildArmRect(cornerX, cornerY, angleDeg, stubLen, w, signalLayer);

        double angleRad = angleDeg * Math.PI / 180.0;
        double pin2X = cornerX + stubLen * Math.Cos(angleRad);
        double pin2Y = cornerY + stubLen * Math.Sin(angleRad);
        long pin2XDbu = (long)Math.Round(pin2X, MidpointRounding.AwayFromZero);
        long pin2YDbu = (long)Math.Round(pin2Y, MidpointRounding.AwayFromZero);

        LayoutShape merged = PCellGeometryHelpers.UnionArms([arm1, arm2], signalLayer, technology);

        if (miter != MicrostripBendMiter.None && Math.Abs(Math.Sin(angleRad)) > 1e-9)
        {
            var miterCut = BuildMiterCutTriangle(cornerX, cornerY, angleDeg, w, wMeters, miter, technology, layerSelection, signalLayer, dbuPerMicron);
            if (miterCut is not null)
            {
                var diff = LayoutBooleans.Difference([merged, miterCut], technology);
                if (diff.Shapes.Count > 0) merged = diff.Shapes[0];
            }
        }

        var pins = new[]
        {
            new PCellPin("1", 0, 0, signalLayer, w, 180.0),
            new PCellPin("2", pin2XDbu, pin2YDbu, signalLayer, w, angleDeg),
        };

        return new PCellResult([merged], pins);
    }

    /// <summary>
    /// The miter cut length, applied symmetrically along each arm's outer edge from the sharp
    /// outer corner: <c>Optimal</c> uses Douville &amp; James 1978's W/h-dependent optimum
    /// (<c>MicrostripDiscontinuities.MiterCutLength</c>, <c>src/Core/Devices/Microstrip/</c> —
    /// shared with the electrical model's own chamfer-geometry reasoning per R-pc-12/R7, though the
    /// L-C-L electrical model itself no longer consumes this length directly, see
    /// <c>MicrostripBendModel</c>'s own doc comment); <c>Fifty</c> uses a FIXED 50% chamfer
    /// (per-edge leg = 0.5·W, i.e. M/100=0.5 in the same diagonal-cut convention) — the artwork
    /// analogue of the Fifty electrical coefficients (brief-mtaper-mklopf.md §1A.3).
    /// </summary>
    private static LayoutShape? BuildMiterCutTriangle(
        long cornerX, long cornerY, double angleDeg, long wDbu, double wMeters, MicrostripBendMiter miter,
        Technology? technology, PCellLayerSelection layerSelection, LayerKey signalLayer, int dbuPerMicron)
    {
        double angleRad = angleDeg * Math.PI / 180.0;
        double d1X = 1.0, d1Y = 0.0;
        double d2X = Math.Cos(angleRad), d2Y = Math.Sin(angleRad);

        double turnSign = Math.Sign(d1X * d2Y - d1Y * d2X);
        if (turnSign == 0) return null;

        // Outer side = right of travel for a CCW (left) turn, left of travel for a CW (right) turn.
        double n1X = turnSign > 0 ? d1Y : -d1Y, n1Y = turnSign > 0 ? -d1X : d1X;
        double n2X = turnSign > 0 ? d2Y : -d2Y, n2Y = turnSign > 0 ? -d2X : d2X;

        double halfW = wDbu / 2.0;
        double a1X = cornerX + n1X * halfW, a1Y = cornerY + n1Y * halfW; // point on arm1's outer edge line
        double a2X = cornerX + n2X * halfW, a2Y = cornerY + n2Y * halfW; // point on arm2's outer edge line

        // arm1's outer edge is always horizontal (d1 = (1,0) by R-pc-3) — solve the intersection
        // with arm2's outer edge line (a2 + s*d2) directly rather than a general 2-line solver.
        if (Math.Abs(d2Y) < 1e-12) return null; // degenerate (arm2 also horizontal)
        double s = (a1Y - a2Y) / d2Y;
        double outerX = a2X + s * d2X, outerY = a1Y; // = a2Y + s*d2Y by construction

        double mMeters;
        if (miter == MicrostripBendMiter.Fifty)
        {
            mMeters = 0.5 * wMeters; // fixed 50% chamfer, per-edge leg = 0.5*W
        }
        else
        {
            // Optimal: h = the resolved substrate height; falls back to the W/h→∞ asymptote of the
            // Douville-James formula (cut → 0.52·W) when no technology/substrate resolves, per §2
            // of the brief ("the geometry is still generatable" even with nothing resolved).
            var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, layerSelection);
            mMeters = substrate is not null
                ? MicrostripDiscontinuities.MiterCutLength(wMeters, substrate.HeightMeters)
                : MicrostripDiscontinuities.MiterCutLengthAsymptotic(wMeters);
        }
        long mDbu = PCellUnits.MetresToDbu(mMeters, dbuPerMicron);
        if (mDbu <= 0) return null;

        long cut1X = (long)Math.Round(outerX - d1X * mDbu, MidpointRounding.AwayFromZero);
        long cut1Y = (long)Math.Round(outerY - d1Y * mDbu, MidpointRounding.AwayFromZero);
        long cut2X = (long)Math.Round(outerX - d2X * mDbu, MidpointRounding.AwayFromZero);
        long cut2Y = (long)Math.Round(outerY - d2Y * mDbu, MidpointRounding.AwayFromZero);
        long outerXDbu = (long)Math.Round(outerX, MidpointRounding.AwayFromZero);
        long outerYDbu = (long)Math.Round(outerY, MidpointRounding.AwayFromZero);

        return new PolygonShape
        {
            Layer = signalLayer,
            Xy = [outerXDbu, outerYDbu, cut1X, cut1Y, cut2X, cut2Y],
        };
    }
}
