using System.Numerics;
using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>
/// A <see cref="FilterPrototype"/> placed on the real frequency axis: the lowpass/highpass/bandpass
/// transformation, the flat insertion loss, and the two limits an ordinary sweep actually reaches.
/// Frames the whole electrical answer of the ideal filter; the model on top of it only decides
/// which nets the numbers are stamped onto, which is why the duplexer can hold two of these and add
/// no mathematics of its own.
///
/// <para><b>The transformations</b>, applied to the prototype before evaluation:</para>
/// <code>
///   Lowpass    s → s/ω_c                                   Ω = ω/ω_c
///   Highpass   s → ω_c/s                                    Ω = −ω_c/ω
///   Bandpass   s → (s² + ω_0²)/(BW·s)                       Ω = (ω² − ω_0²)/(BW·ω)
///                  ω_0 = 2π·sqrt(F1·F2)   BW = 2π(F2 − F1)
/// </code>
///
/// <para><b>The bandpass transformation DOUBLES the degree.</b> A user's <c>Order = 3</c> bandpass
/// is a sixth-degree network, and <c>Order</c> here means the PROTOTYPE order. Both conventions
/// exist in the wild, so the parameter description says which — a user comparing a plot against a
/// datasheet needs to know which number the datasheet meant. Nothing in this class doubles: the
/// prototype is evaluated at the transformed Ω, so the degree doubling is in the map rather than in
/// a bigger polynomial.</para>
///
/// <para><b>Ω is real for every real ω</b> under all three maps, which is the property that lets the
/// prototype's own <c>S22</c> derivation survive the transformation unchanged.</para>
/// </summary>
public sealed class FilterNetwork
{
    private readonly double _wc;        // lowpass / highpass corner, rad/s
    private readonly double _w0;        // bandpass centre, rad/s
    private readonly double _bw;        // bandpass width, rad/s
    private readonly double _ilAmp;     // amplitude factor of the flat insertion loss

    private FilterNetwork(FilterPrototype prototype, NetworkForm form,
                          double wc, double w0, double bw, double ilAmp)
    {
        Prototype = prototype;
        Form      = form;
        _wc = wc; _w0 = w0; _bw = bw; _ilAmp = ilAmp;
    }

    /// <summary>The prototype this transforms.</summary>
    public FilterPrototype Prototype { get; }

    /// <summary>Lowpass, highpass or bandpass — <c>Match</c>'s own spelling, and its own glyph.</summary>
    public NetworkForm Form { get; }

    /// <summary>
    /// Builds one filter's whole response.
    ///
    /// <para><b>Which frequencies are read depends on <paramref name="form"/>, and the unread ones
    /// are ignored rather than refused</b> — the same rule the response families follow for
    /// <paramref name="rippleDb"/> and <paramref name="astopDb"/>. A user moving a filter from
    /// bandpass to lowpass should not have to clear the band edges.</para>
    /// </summary>
    /// <param name="response">The prototype family.</param>
    /// <param name="form">Lowpass, highpass or bandpass.</param>
    /// <param name="order">Prototype order.</param>
    /// <param name="fcHz">Cutoff, Hz. Read by lowpass and highpass.</param>
    /// <param name="f1Hz">Lower band edge, Hz. Read by bandpass.</param>
    /// <param name="f2Hz">Upper band edge, Hz. Read by bandpass.</param>
    /// <param name="rippleDb">Passband ripple, dB.</param>
    /// <param name="astopDb">Stopband floor, dB.</param>
    /// <param name="ilDb">A flat insertion loss laid on top of the ideal response, dB.</param>
    public static FilterNetwork Create(FilterResponse response, NetworkForm form, int order,
                                       double fcHz, double f1Hz, double f2Hz,
                                       double rippleDb, double astopDb, double ilDb)
    {
        var proto = FilterPrototype.Create(response, order, rippleDb, astopDb);
        double ilAmp = Math.Pow(10.0, -ilDb / 20.0);

        if (form == NetworkForm.Bandpass)
        {
            if (!(f1Hz > 0.0) || !(f2Hz > f1Hz) || !double.IsFinite(f2Hz))
                throw new ArgumentOutOfRangeException(nameof(f1Hz),
                    $"A bandpass filter needs 0 < F1 < F2; got F1 = {f1Hz:G6} Hz, F2 = {f2Hz:G6} Hz.");

            double w1 = 2.0 * Math.PI * f1Hz, w2 = 2.0 * Math.PI * f2Hz;
            return new FilterNetwork(proto, form, 0.0, Math.Sqrt(w1 * w2), w2 - w1, ilAmp);
        }

        if (!(fcHz > 0.0) || !double.IsFinite(fcHz))
            throw new ArgumentOutOfRangeException(nameof(fcHz),
                $"A lowpass or highpass filter needs a positive cutoff; got Fc = {fcHz:G6} Hz.");

        return new FilterNetwork(proto, form, 2.0 * Math.PI * fcHz, 0.0, 0.0, ilAmp);
    }

    /// <summary>
    /// The prototype frequency this network maps <paramref name="omega"/> onto.
    /// <see cref="double.NegativeInfinity"/> at <c>ω = 0</c> for the two forms that block DC — an
    /// honest answer rather than a division that produces one by accident.
    /// </summary>
    public double PrototypeOmega(double omega) => Form switch
    {
        NetworkForm.Lowpass  => omega / _wc,
        NetworkForm.Highpass => omega == 0.0 ? double.NegativeInfinity : -_wc / omega,
        _                    => omega == 0.0 ? double.NegativeInfinity
                                             : (omega * omega - _w0 * _w0) / (_bw * omega),
    };

    /// <summary>
    /// The two-port S at <paramref name="omega"/> ≥ 0, referenced to whatever impedances the caller
    /// stamps it against. <c>S12 = S21</c> — this block is reciprocal, and an ideal filter that was
    /// not would be an isolator.
    /// </summary>
    public (Complex S11, Complex S21, Complex S22) At(double omega)
    {
        var (s11, s21, s22) = Prototype.At(PrototypeOmega(omega));
        return (s11, s21 * _ilAmp, s22);
    }
}
