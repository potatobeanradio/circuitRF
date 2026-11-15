using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore.Signals;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Modulated RF source (<c>MOD_Source</c>): an LTE / NR / CW / imported-I/Q signal of one or more
/// carriers around the centre frequency <c>Fc</c>, with total average available power <c>Pavg</c>
/// (dBm) behind the source impedance <c>Z</c> (default 50 Ω).
///
/// Netlist:
/// <code>
/// MOD_Source:S1 n_in 0 Fc=2 GHz Pavg=14 Z=50 Record=1e-3 \
///     Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=-200MHz;P=0" Carrier[2]="LTE;BW=20MHz;Off=200MHz;P=0"
/// </code>
/// Optional: <c>Clip=8</c> (target PAPR, dB; absent = unclipped), <c>Shaping=-80</c> (dBc floor; <c>Shaping=off</c>).
///
/// A pulsed-CW carrier (<c>Carrier[1]="PCW;PRI=100us;PW=50us;..."</c>) must be the source's only carrier, and its
/// record is normalised to unit power on the flat top, so <c>Pavg</c> is then the PULSE-TOP available power and
/// harmonic balance and loadpull see the top: the right steady state for a pulse.
///
/// A single carrier of PSK / QAM / APSK symbols (<c>Carrier[1]="SC;Rs=10Msps;Mod=16APSK;RollOff=0.2;..."</c>, brief
/// PCH-5) is the source's only carrier too, with a record of its own symbols; unlike FSK, DBPSK and CSS it may be
/// clipped (<c>Clip=</c>), since a filtered QAM or APSK carrier has peaks.
///
/// Node layout (as P1Tone): [0] external, [1] reference, [2] minted <c>__modsrc_&lt;inst&gt;_drv</c>.
///
/// What each analysis sees:
///   S-parameter: a passive port impedance Z (no drive).
///   HB / loadpull: a CW tone at <c>Fc</c> carrying the signal's average power — the operating point a
///     modulated run is built around.
///   Envelope: per-carrier complex envelopes. The envelope engine sets <see cref="EnvelopeDrive"/> to stamp a
///     unit (or zero) drive phasor at whatever spectral product it is assembling, and reads the carriers'
///     baseband records from <see cref="Signal"/>.
/// |Vs| = sqrt(8 · Re(Z) · Pavg_W) is the peak phasor of a unit-mean-power envelope, as for P1Tone.
/// </summary>
public sealed class ModSourceModel : ComponentModel, IDriveScalable
{
    private const double OmegaTolRad = 1.0;

    public override int PortCount => 1;
    public override ModelKind Kind => ModelKind.Linear;

    public string InstanceName { get; }
    public Complex Z { get; }
    public double PavgDbm { get; }
    public double FcHz { get; }
    public SignalSpec Spec { get; }

    /// <inheritdoc/>
    public double DriveScale { get; set; } = 1.0;

    /// <summary>
    /// Envelope assembly hook: when non-null the drive branch is stamped at EVERY frequency with this
    /// phasor (volts, peak) instead of the CW tone. Null restores the HB behaviour.
    /// </summary>
    public Complex? EnvelopeDrive { get; set; }

    private double _fc;   // 0 = S-parameter mode (no drive branch)

    /// <summary>Peak phasor amplitude of the unit-mean-power envelope.</summary>
    public double VsPeak
    {
        get
        {
            double p = Math.Pow(10.0, (PavgDbm - 30.0) / 10.0);
            return Z.Real > 0 ? Math.Sqrt(8.0 * p * Z.Real) : 0.0;
        }
    }

    private ModulatedSignal? _signal;

    /// <summary>The generated signal (cached by spec across instances and runs; generated on first use).</summary>
    public ModulatedSignal Signal => _signal ??= SignalCache.Get(Spec);

    /// <summary>Starts background generation without waiting (the UI calls this when a design is opened).</summary>
    public Task<ModulatedSignal> SignalAsync() => SignalCache.GetAsync(Spec);

    public ModSourceModel(string instanceName, Complex z, double pavgDbm, double fcHz, SignalSpec spec)
    {
        InstanceName = instanceName;
        Z = z;
        PavgDbm = pavgDbm;
        FcHz = fcHz;
        Spec = spec;
    }

    /// <summary>HB context: <paramref name="fc"/> &gt; 0 enables the drive branch (0 = S-parameter mode).</summary>
    public void SetToneContext(double fc) => _fc = fc;

    /// <summary>Absolute carrier frequencies (Hz).</summary>
    public IEnumerable<double> CarrierFrequenciesHz => Spec.Carriers.Select(c => FcHz + c.OffsetHz);

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int nExt = c.Nodes.Length > 0 ? c.Nodes[0] : 0;
        int nRef = c.Nodes.Length > 1 ? c.Nodes[1] : 0;
        int nDrv = c.Nodes.Length > 2 ? c.Nodes[2] : 0;

        if (_fc <= 0 && EnvelopeDrive is null)
        {
            StampZ(mna, nExt, nRef, Z);
            return;
        }

        Complex v;
        if (EnvelopeDrive is { } ed) v = ed;
        else v = Math.Abs(omega - 2 * Math.PI * FcHz) < OmegaTolRad ? new Complex(DriveScale * VsPeak, 0) : Complex.Zero;

        int br = mna.AddBranch();
        mna.AddBranchCurrent(br, nDrv, nRef);
        mna.AddConstraint(br, nDrv, Complex.One);
        if (nRef > 0) mna.AddConstraint(br, nRef, -Complex.One);
        mna.AddSourceValue(br, v);
        StampZ(mna, nExt, nDrv, Z);
    }

    /// <summary>S-parameter mode helper (mirrors P1Tone): tie the unused drive node to the reference.</summary>
    public void StampSParamDriveTie(IMnaContext mna, ElaboratedComponent c)
    {
        int nRef = c.Nodes.Length > 1 ? c.Nodes[1] : 0;
        int nDrv = c.Nodes.Length > 2 ? c.Nodes[2] : 0;
        if (nDrv > 0) mna.AddAdmittance(nDrv, nRef, Complex.One);
    }

    private static void StampZ(IMnaContext mna, int na, int nb, Complex z)
    {
        if (z.Magnitude < 1e-15) z = new Complex(1e-6, 0);
        mna.AddAdmittance(na, nb, Complex.One / z);
    }

    // ── construction from resolved parameters ────────────────────────────────

    private static readonly Regex RxCarrier = new(@"^Carrier\[(\d+)\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ModSourceModel Create(IReadOnlyDictionary<string, Value> p)
    {
        string name = p.TryGetValue("ModSourceName", out var nm) && nm.Kind == ValueKind.String ? nm.AsString() : "MOD_Source";
        double Real(string k, double def) => p.TryGetValue(k, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : def;
        // Fc and Pavg have no default: a source that silently played 1 GHz at 0 dBm is a run nobody asked for.
        double Required(string k, string what) => p.TryGetValue(k, out var v) && v.Kind == ValueKind.Real
            ? v.AsReal()
            : throw new ParameterRefusalException($"MOD_Source needs {k}, its {what}");
        Complex z = p.TryGetValue("Z", out var zv)
            ? (zv.Kind == ValueKind.Complex ? zv.AsComplex() : new Complex(zv.AsReal(), 0))
            : new Complex(50, 0);

        var carriers = new SortedDictionary<int, CarrierSpec>();
        foreach (var kv in p)
        {
            var m = RxCarrier.Match(kv.Key);
            if (!m.Success) continue;
            if (kv.Value.Kind != ValueKind.String)
                throw new InvalidOperationException($"MOD_Source '{name}': {kv.Key} must be a quoted carrier description");
            try { carriers[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = SignalSpec.ParseCarrier(kv.Value.AsString()); }
            catch (FormatException ex) { throw new InvalidOperationException($"MOD_Source '{name}': {kv.Key}: {ex.Message}"); }
        }
        if (carriers.Count == 0)
            carriers[1] = new CarrierSpec { Standard = CarrierStandard.Lte, BandwidthHz = 20e6 };

        double? shaping = -80;
        if (p.TryGetValue("Shaping", out var sv))
        {
            if (sv.Kind == ValueKind.String && sv.AsString().Equals("off", StringComparison.OrdinalIgnoreCase)) shaping = null;
            else if (sv.Kind == ValueKind.Real) shaping = sv.AsReal();
        }
        double? clip = p.TryGetValue("Clip", out var cv) && cv.Kind == ValueKind.Real ? cv.AsReal() : null;

        var spec = new SignalSpec
        {
            Carriers = carriers.Values.ToList(),
            RecordSeconds = Real("Record", 1e-3),
            ClipPaprDb = clip,
            ShapingFloorDbc = shaping,
        };
        try { SignalComposer.HasOwnRecord(spec); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException($"MOD_Source '{name}': {ex.Message}");
        }
        return new ModSourceModel(name, z, Required("Pavg", "average power in dBm"), Required("Fc", "centre frequency"), spec);
    }
}
