// brief-cosim-pattern-7 R-cp7-1 — what relates a planar block's port to its delta gap, kept with the pattern.
//
// A planar pattern holds its driven gap at 1 V and every other gap at 0 V, so the field of a whole circuit is Σ V_k·E_k over the GAPS' voltages. The block's S
// is at the reference plane, so a circuit's voltage there is not its gap's on a de-embedded port. The way back undoes DeembedAt one step at a time, for each port on its
// own: renormalise to the line's Z_c, put the feed lead back, then cross the error box. An internal gap has an identity box, and all of this leaves its waves alone.
//
// The wave the box gives is also the POWER the raw structure takes at that gap, |a_e|² − |b_e|², so a recombination needs no raw S or Y of its own.
//
// A calibration group's box is an N×N block in a modal basis and is not kept: a group's run says so with GroupCube rather than leaving identities to be misread.

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Engine.Mom;

/// <summary>One port's way from its reference plane to its delta gap, at one frequency.</summary>
/// <param name="A11">The error box's external reflection.</param>
/// <param name="A21">The error box's transmission.</param>
/// <param name="A22">The error box's internal reflection.</param>
/// <param name="Zc">The reference the box's internal side is in.</param>
/// <param name="Peel">γ·ℓ of the feed lead taken off between the box and the reference plane; zero when none was grown.</param>
public readonly record struct PlanarPortBox(Complex A11, Complex A21, Complex A22, Complex Zc, Complex Peel)
{
    /// <summary>A port with nothing between its plane and its gap, in reference <paramref name="z0"/>.</summary>
    public static PlanarPortBox Identity(Complex z0) => new(Complex.Zero, Complex.One, Complex.Zero, z0, Complex.Zero);

    /// <summary>
    /// The waves at the gap, in the raw solve's reference <paramref name="z0"/>, of waves <paramref name="a"/> into and <paramref name="b"/> out of the block at
    /// the plane in that same real reference. <paramref name="pseudoWave"/> says which spelling of a wave the run renormalised in (<see cref="PlanarPortBoxes.PseudoWaveCube"/>).
    /// </summary>
    public (Complex A, Complex B) ToGap(Complex a, Complex b, double z0, bool pseudoWave)
    {
        double root = Math.Sqrt(z0);
        Complex v = root * (a + b), i = (a - b) / root;
        // The line's own reference, as PlanarDeembed.Renormalise or RenormalisePseudoWave put it there.
        Complex scale = pseudoWave ? Complex.Sqrt(Zc) : new Complex(Math.Sqrt(Zc.Real), 0);
        Complex back = pseudoWave ? Zc : Complex.Conjugate(Zc);
        Complex aPlane = (v + Zc * i) / (2 * scale), bPlane = (v - back * i) / (2 * scale);
        // PlanarFeedExtension.Peel took e^{+θ} off each way, so a wave into the block left the box e^{+θ} ago and one out of it arrives there e^{−θ} later.
        Complex aInner = aPlane * Complex.Exp(Peel), bInner = bPlane * Complex.Exp(-Peel);
        // PlanarDeembed.Apply's bookkeeping: a_i = a21·a_e + a22·b_i and b_e = a11·a_e + a21·b_i.
        Complex aGap = (aInner - A22 * bInner) / A21;
        return (aGap, A11 * aGap + A21 * bInner);
    }
}

/// <summary>The cubes that carry <see cref="PlanarPortBox"/> in a result, in the planar diagnostics group: <c>__</c> names, since none is a signal.</summary>
public static class PlanarPortBoxes
{
    public const string A11Cube = "__PortBoxA11", A21Cube = "__PortBoxA21", A22Cube = "__PortBoxA22", ZcCube = "__PortBoxZc", PeelCube = "__PortBoxPeel";

    /// <summary>1 when the run renormalised in pseudo-waves (a grounded edge-port return, or a conducting stack), 0 in power waves.</summary>
    public const string PseudoWaveCube = "__PortBoxPseudoWave";

    /// <summary>Present, in place of the others, when a port of the run is in a calibration group.</summary>
    public const string GroupCube = "__PortCalibrationGroup";

    /// <summary>The boxes at every frequency the pattern was taken, on the pattern's own <c>[freq, port]</c> axes. A sweep with no pattern publishes nothing.</summary>
    internal static void Publish(DataSet ds, PlanarSolveResult sweep, IReadOnlyList<PlanarPortResolution> ports)
    {
        if (sweep.FarField is not { Patterns.Count: > 0 } far) return;
        if (ports.Any(p => p.Group is not null))
        {
            ds.AddToGroup(PlanarKernel.DiagnosticsGroup, GroupCube, DataCube.Scalar(1.0));
            return;
        }

        int nf = far.FrequenciesHz.Count, np = ports.Count;
        var a11 = new Complex[nf * np];
        var a21 = new Complex[nf * np];
        var a22 = new Complex[nf * np];
        var zc = new Complex[nf * np];
        var peel = new Complex[nf * np];
        for (int i = 0; i < nf; i++)
        {
            double hz = far.FrequenciesHz[i];
            var point = sweep.Points.First(p => p.FrequencyHz == hz);
            for (int p = 0; p < np; p++)
            {
                int at = i * np + p;
                var c = point.Calibrations.FirstOrDefault(x => x.PortNumber == ports[p].Number);
                double lead = sweep.PortPeelLengthsM is { } leads ? leads[p] : 0;
                var box = c is null
                    ? PlanarPortBox.Identity(ports[p].Z0)
                    : new PlanarPortBox(c.Box.A11, c.Box.A21, c.Box.A22, c.Zc, c.Gamma.Gamma * lead);
                (a11[at], a21[at], a22[at], zc[at], peel[at]) = (box.A11, box.A21, box.A22, box.Zc, box.Peel);
            }
        }

        Axis[] Axes() =>
        [
            new Axis("freq", far.FrequenciesHz.ToArray(), "Hz"),
            new Axis("port", ports.Select(p => (double)p.Number).ToArray(), ""),
        ];
        ds.AddToGroup(PlanarKernel.DiagnosticsGroup, A11Cube, new DataCube(Axes(), a11));
        ds.AddToGroup(PlanarKernel.DiagnosticsGroup, A21Cube, new DataCube(Axes(), a21));
        ds.AddToGroup(PlanarKernel.DiagnosticsGroup, A22Cube, new DataCube(Axes(), a22));
        ds.AddToGroup(PlanarKernel.DiagnosticsGroup, ZcCube, new DataCube(Axes(), zc));
        ds.AddToGroup(PlanarKernel.DiagnosticsGroup, PeelCube, new DataCube(Axes(), peel));
        ds.AddToGroup(PlanarKernel.DiagnosticsGroup, PseudoWaveCube, DataCube.Scalar(sweep.PseudoWaveReference ? 1.0 : 0.0));
    }
}
