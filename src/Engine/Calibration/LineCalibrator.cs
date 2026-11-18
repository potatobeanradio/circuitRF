// PWL-1 — THE CALIBRATION'S BRANCH STATE, STEPPED ACROSS A SWEEP, INDEPENDENT OF THE SOLVER.
//
// What the planar solver's `PlanarPortCalibrator` did per frequency that is not about meshes: predict β, choose the
// separation, extract γ on the predicted 2π branch, carry a₂₁'s sign, and do the same per mode for a calibration group.
// It is handed the standards' raw S; it does not know how they were solved, and it owns no cache. The planar calibrator
// is now its caller, and keeps what is planar (its standards' meshes and solves, the quasi-static path, the conducting
// substrate's estimates); a 3D caller drives it with standards solved by a 3D solver.
//
// IT IS STATEFUL AND MUST BE STEPPED IN INCREASING FREQUENCY ORDER, because both branch resolutions are continuations:
// γ's 2π ambiguity is unwrapped from the previous point, and a₂₁'s sign is carried from it. A per-point independent
// choice has no information with which to make either decision. A caller that inserts a frequency mid-band caches its
// raw matrices, calls Restart, and replays every solved frequency in sorted order.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Calibration;

/// <summary>One calibrated point of a single-mode port: γ, the error box and Z_c.</summary>
public sealed record LineCalibrationPoint(LineCalibration.GammaResult Gamma, LineErrorBox Box, Complex Zc);

/// <summary>
/// The line-standard calibration of one port (or one calibration group), stepped across a sweep in increasing frequency
/// order. See the file header.
/// </summary>
public sealed class LineCalibrator
{
    private readonly double[] _deltas;
    private readonly Func<double, double> _estimatedBetaAt;
    private readonly bool _predictFromHistory;

    private double   _prevBeta = double.NaN;
    private double   _prevF;
    private Complex? _prevA21;
    private Complex[]? _prevGamma;

    /// <param name="shortLengthM">The short standard's plane-to-plane length, as BUILT.</param>
    /// <param name="deltasM">Each long standard's separation from the short one, as built: long standard i is
    /// <paramref name="shortLengthM"/> + <paramref name="deltasM"/>[i].</param>
    /// <param name="estimatedBetaAt">β before any solve, used at the first frequency (and at every frequency when
    /// <paramref name="predictFromHistory"/> is false).</param>
    /// <param name="predictFromHistory">True predicts each point's β from the previous point's measured β scaled by
    /// frequency, which is what keeps the 2π branch; false predicts every point from the estimate, for a line whose β does
    /// not scale with frequency (a slow wave on a conducting substrate).</param>
    public LineCalibrator(double shortLengthM, IReadOnlyList<double> deltasM, Func<double, double> estimatedBetaAt,
                          bool predictFromHistory = true)
    {
        ArgumentNullException.ThrowIfNull(deltasM);
        ArgumentNullException.ThrowIfNull(estimatedBetaAt);
        if (deltasM.Count == 0) throw new ArgumentException("A calibration needs at least one separation.", nameof(deltasM));
        ShortLengthM        = shortLengthM;
        _deltas             = [.. deltasM];
        _estimatedBetaAt    = estimatedBetaAt;
        _predictFromHistory = predictFromHistory;
    }

    /// <summary>The short standard's plane-to-plane length.</summary>
    public double ShortLengthM { get; }

    /// <summary>The separations, short standard to each long one.</summary>
    public IReadOnlyList<double> DeltasM => _deltas;

    /// <summary>The β this frequency's selection and branch continuation are predicted from — the previous point's
    /// measured β scaled by frequency, or the estimate at the first point (or at every point, see the constructor).</summary>
    public double ExpectedBeta(double fHz) =>
        !_predictFromHistory ? _estimatedBetaAt(fHz)
        : double.IsNaN(_prevBeta) ? _estimatedBetaAt(fHz)
                                  : _prevBeta * (fHz / _prevF);

    /// <summary>Which separation this frequency reads, over all of them, from the predicted β. A caller with separations
    /// it does not measure over (a supplied-γ one) selects over its own subset with
    /// <see cref="LineCalibration.SelectSeparation"/> instead.</summary>
    public int SelectedIndexAt(double fHz) => LineCalibration.SelectSeparation(_deltas, ExpectedBeta(fHz));

    /// <summary>γ from the short standard and long standard <paramref name="pick"/>, on the predicted branch. Does not step
    /// the branch state; <see cref="StepBranch"/> does.</summary>
    public LineCalibration.GammaResult MeasureGamma(Mat<Complex> sShort, Mat<Complex> sLong, int pick, double fHz)
    {
        double expect = ExpectedBeta(fHz);
        return LineCalibration.Gamma(sShort, sLong, _deltas[pick], expect * _deltas[pick]);
    }

    /// <summary>Record this point's β as the next point's prediction.</summary>
    public void StepBranch(double beta, double fHz)
    {
        _prevBeta = beta;
        _prevF    = fHz;
    }

    /// <summary>The general error box from the short standard and long standard <paramref name="pick"/>, its a₂₁ sign
    /// continued from the previous point, which it then becomes.</summary>
    public LineErrorBox SolveBox(Mat<Complex> sShort, Mat<Complex> sLong, int pick, Complex gamma)
    {
        var box = LineDeembed.SolveErrorBox(sShort, sLong, ShortLengthM, ShortLengthM + _deltas[pick], gamma, _prevA21);
        _prevA21 = box.A21;
        return box;
    }

    /// <summary>
    /// <b>One single-mode point, whole:</b> γ, the branch step, Z_c from <paramref name="zcOf"/> (typically
    /// <c>γ/(jωC′)</c> through <see cref="LineDeembed.CharacteristicImpedance(Complex, double, double)"/>), and the box.
    /// </summary>
    public LineCalibrationPoint At(double fHz, Mat<Complex> sShort, Mat<Complex> sLong, int pick, Func<Complex, Complex> zcOf)
    {
        ArgumentNullException.ThrowIfNull(zcOf);
        var g = MeasureGamma(sShort, sLong, pick, fHz);
        StepBranch(g.Beta, fHz);
        var zc  = zcOf(g.Gamma);
        var box = SolveBox(sShort, sLong, pick, g.Gamma);
        return new LineCalibrationPoint(g, box, zc);
    }

    /// <summary>
    /// <b>A calibration GROUP's point:</b> γ per mode and the N×N modal error box from the 2N-port short standard and long
    /// standard <paramref name="pick"/>. Each mode's 2π branch is continued from the previous point's own γ, or predicted
    /// from <paramref name="quasiStaticBeta"/> (β of mode m at this frequency, from the cross-section's electrostatics) at
    /// the first; that prediction is the ONLY thing that assigns a measured mode to a mode of the cross-section, so it is
    /// per mode and never an average. The mean β over modes becomes the scalar prediction.
    /// </summary>
    /// <param name="tv">The cross-section's voltage modal matrix, N×N: column m is mode m's terminal voltage pattern.</param>
    public ModalErrorBox ModalAt(double fHz, Mat<Complex> sShort, Mat<Complex> sLong, int pick,
                                 Func<int, double> quasiStaticBeta, Mat<double> tv)
    {
        ArgumentNullException.ThrowIfNull(quasiStaticBeta);
        int n = tv.ColCount;
        double dl = _deltas[pick];

        var expect = new double[n];
        for (int m = 0; m < n; m++)
            expect[m] = (_prevGamma is null ? quasiStaticBeta(m)
                                            : _prevGamma[m].Imaginary * (fHz / _prevF)) * dl;

        var box = ModalLineCalibration.Solve(sShort, sLong, ShortLengthM, ShortLengthM + dl, expect, tv);

        _prevGamma = [.. box.Gamma];
        _prevF     = fHz;

        // The separation choice and the scalar branch prediction both need ONE β for the group; the mean over modes is
        // what they get, and it only has to be right to ~20% for either.
        double mean = 0;
        for (int m = 0; m < n; m++) mean += box.Gamma[m].Imaginary;
        _prevBeta = mean / n;

        return box;
    }

    /// <summary>Whether every mode of a group's point is inside the usable interval: one mode outside it makes the whole
    /// box unusable, because the box is solved from all of them at once.</summary>
    public static bool Usable(ModalErrorBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        bool usable = true;
        foreach (double deg in box.ElectricalDegrees)
            usable &= deg >= LineCalibration.UsableLoDegrees && deg <= LineCalibration.UsableHiDegrees;
        return usable;
    }

    /// <summary>Whether a group's modes were far enough apart, over the separation this point read, for the cascade to
    /// tell them apart. Below the floor the modal basis is decided by round-off and the point must be refused.</summary>
    public static bool Separable(ModalErrorBox box, double floorDegrees)
    {
        ArgumentNullException.ThrowIfNull(box);
        return !(box.ModeSeparationDegrees < floorDegrees);
    }

    /// <summary>Drop the branch state so the next point starts a fresh continuation.</summary>
    public void Restart()
    {
        _prevBeta  = double.NaN;
        _prevF     = 0;
        _prevA21   = null;
        _prevGamma = null;
    }
}
