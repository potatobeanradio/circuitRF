using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore.Propagation;

namespace CircuitRF.Core.Devices;

/// <summary>A model told the frequency a run carries its signal at (brief PCH-6): the HB fundamental, an envelope run's
/// band centre. A Propagation Channel evaluates its large-scale model there when <c>f0</c> is not stated.</summary>
public interface ICarrierContext
{
    void SetCarrier(double hz);
}

/// <summary>
/// One port's far field as a Propagation Channel's pin end reads it (brief PCH-6 R-pch6-2), supplied by src/Design through
/// <see cref="PropagationChannelModel.PatternSource"/>: at the record frequency nearest the one asked, that port's
/// terminated pattern per unit incident wave as a REALIZED-GAIN field, |e|² = G(θ, φ) with the port's mismatch in it, in the
/// pattern's own frame; and the port's S11 against the record's reference impedance.
/// </summary>
public sealed class ChannelPattern
{
    public required double FrequencyHz { get; init; }
    public required string Record { get; init; }
    public required Complex S11 { get; init; }
    public required double Z0 { get; init; }
    /// <summary>(Eθ, Eφ) at (θ, φ) in radians, realized-gain normalised.</summary>
    public required Func<double, double, (Complex ETheta, Complex EPhi)> Field { get; init; }
}

/// <summary>
/// The Propagation Channel with pin ends (brief PCH-6): what is between a transmit antenna's terminals and a receive
/// antenna's terminals that is not an antenna. Two nets, <c>tx</c> and <c>rx</c>, each over ground.
///
/// <para><b>One-way</b> (design note §2.2): the receive pin is a source behind its impedance driven by the transmit pin's
/// incident wave through
/// <code>
///   H(f, t) = (λ/4π·d) · 10^(−Lex/20) · Σ_r c_r(f) · A_r · e^(−j2πf·τ_r) · e^(j2π·ν_r(f)·t + jπ·ν̇(f)·t²)
/// </code>
/// and nothing travels back: S12 = 0, S11 and S22 the two ends' impedances. d is the line-of-sight distance the model uses
/// (3D, slant range on a satellite path), Lex the model's loss in excess of free space over d at <c>f0</c> (the stated
/// <c>f0</c>, else the run's carrier, else the frequency asked) with every added loss in it, A_r, τ_r and ν_r each ray's gain,
/// EXCESS delay and Doppler (<see cref="ChannelRay"/>), c_r the two ends' antennas toward the ray, and ν scaled with f. The
/// free-space part follows λ at every frequency, so Friis is exact across a sweep; the excess is the model's at f0. At DC
/// H is 0: an antenna radiates nothing there.</para>
///
/// <para><b>What each analysis sees.</b> S-parameter, noise and HB: H frozen at <c>t = TimeOffset</c>, each tone at its own
/// frequency, no Doppler shift (a shifted tone would leave the HB grid). An envelope run replaces the coupling by
/// <c>EnvelopeChannel</c>: the receive pin is driven by each ray's delayed, rotated, scaled transmit wave, and the Doppler is a
/// phase that turns with time. The bulk delay τ0 = d/c is reported and never simulated.</para>
///
/// <para><b>Noise.</b> The receive pin's impedance at the antenna temperature <c>RxTant</c> (default the ambient), and no
/// other noise of its own.</para>
/// </summary>
public sealed class PropagationChannelModel : ComponentModel, IReportsWarnings, ICarrierContext
{
    public const string TypeName = "PropagationChannel";
    public const string InstancePrefix = "CH";

    /// <summary>The words the elaborator keeps verbatim (D12): structural words, paths and the list-valued positions.</summary>
    public static readonly IReadOnlySet<string> WordParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PathLoss", "Multipath", "File", "TxPattern", "RxPattern", "DopplerComp", "Reverse", "TxPos", "RxPos", "TxVel", "RxVel",
        "Fading", "Doppler", "Profile", "Terrain",
    };

    /// <summary>The parameters stated in degrees (a bare number is degrees).</summary>
    public static readonly IReadOnlySet<string> AngleParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TxAz", "TxEl", "TxRoll", "RxAz", "RxEl", "RxRoll",
    };

    /// <summary>The channel's own parameters; every other one is the large-scale model's, by the registry's name.</summary>
    public static readonly IReadOnlySet<string> OwnParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PathLoss", "Multipath", "File", "TxPattern", "RxPattern", "DopplerComp", "Reverse", "TxPos", "RxPos", "TxVel", "RxVel",
        "Distance", "TxHeight", "RxHeight", "TxAz", "TxEl", "TxRoll", "RxAz", "RxEl", "RxRoll",
        "TxGain", "RxGain", "TxZ", "RxZ", "RxTant", "TimeOffset", "f0",
        // brief-pch-7 — small-scale fading and the realization's draws
        "Fading", "KFactor", "NakagamiM", "Doppler", "Velocity", "fD", "Sinusoids", "Profile", "DelaySpread", "Seed", "Drop", "Terrain",
    };

    /// <summary>Parameters every component may carry that belong to no model.</summary>
    private static readonly IReadOnlySet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Noise", Temperature.AbsoluteParamName, Temperature.DeltaParamName, "Tnom", "m",
    };

    /// <summary>
    /// brief-pch-6 R-pch6-2 — a port's far field from an EM result, supplied by src/Design (which reads results; src/Core reads
    /// none): (record path, 0-based port, frequency) → the pattern at the record frequency nearest it. Throws
    /// <see cref="InvalidOperationException"/> with the sentence refusing it. Null in a process that never loaded the design
    /// layer, and then a pattern end is refused.
    /// </summary>
    public static Func<string, int, double, ChannelPattern>? PatternSource { get; set; }

    // ── resolved parameters ──────────────────────────────────────────────────────────────────────

    private readonly string _model;
    private readonly IReadOnlyDictionary<string, string> _modelValues;
    private readonly double? _f0;
    private readonly Vec3? _txPos, _rxPos;
    private readonly double _distance, _txHeight, _rxHeight;
    private readonly Vec3 _txVel, _rxVel;
    private readonly (double Az, double El)? _txAim, _rxAim;
    private readonly double _txRoll, _rxRoll;
    private readonly double _txGainDb, _rxGainDb;
    private readonly Complex _txZ, _rxZ;
    private readonly (string Path, int Port)? _txPattern, _rxPattern;
    private readonly double _rxTantK;
    private readonly double _timeOffset;
    private readonly bool _dopplerComp;
    private readonly IReadOnlyList<RayListRow>? _rayList;
    private readonly string? _rayListPath;

    // brief-pch-7 — small-scale fading
    private readonly FadingKind? _fading;               // stated; null keeps a profile's own (static for none and a plain file)
    private readonly double? _kFactorDb;
    private readonly double _nakagamiM;
    private readonly DopplerShape _spectrum;
    private readonly double? _velocity, _fd;
    private readonly int _sinusoids;
    private readonly TdlProfile? _profile;
    private readonly double? _delaySpread;
    private readonly ulong _seed;
    private readonly long _drop;

    /// <summary>The instance path the fading draws' streams are named by (design note §8.1), bound after elaboration.</summary>
    public string InstancePath
    {
        get => _instancePath;
        set { _instancePath = value; lock (_realizations) _realizations.Clear(); }
    }
    private string _instancePath = "";

    /// <summary>The instance this one is the transposed realization of (<c>Reverse=</c>), bound after elaboration.</summary>
    public string? ReverseOf { get; }
    private PropagationChannelModel? _forward;

    private double? _carrier;
    private readonly int[] _branch = [-1, -1];
    private readonly List<(string Key, string Message)> _pending = [];
    private readonly HashSet<string> _said = [];

    /// <summary>The run time t = 0 of a stamp corresponds to: <c>TimeOffset</c>, plus an envelope segment's start.</summary>
    public double FrozenTimeS { get; set; }

    /// <summary>While an envelope run steps its products, the coupling is the run's (EnvelopeChannel), and the stamp leaves
    /// S21 out: the two ends alone.</summary>
    public bool Stepping { get; set; }

    public PropagationChannelModel(IReadOnlyDictionary<string, Value> p, double ambientC, string? baseDirectory = null)
    {
        string Word(string name, string def) => p.TryGetValue(name, out var v) && v.Kind == ValueKind.String && v.AsString().Trim().Length > 0
            ? v.AsString().Trim() : def;
        double Num(string name, double def)
        {
            if (!p.TryGetValue(name, out var v)) return def;
            if (v.Kind == ValueKind.Real) return v.AsReal();
            throw new ParameterRefusalException(PropagationChannelDiagnostics.NotANumber(name));
        }
        Vec3? Vector(string name)
        {
            string w = Word(name, "");
            if (w.Length == 0) return null;
            var parts = w.Trim('"').Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !parts.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                throw new ParameterRefusalException(PropagationChannelDiagnostics.NotAVector(name, w));
            var n = parts.Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            return new Vec3(n[0], n[1], n[2]);
        }
        Complex Imp(string name, double def) => p.TryGetValue(name, out var v)
            ? v.Kind switch
            {
                ValueKind.Real => new Complex(v.AsReal(), 0),
                ValueKind.Complex => v.AsComplex(),
                _ => throw new ParameterRefusalException(PropagationChannelDiagnostics.NotAnImpedance(name)),
            }
            : new Complex(def, 0);
        (string, int)? Pattern(string name)
        {
            string w = Word(name, "").Trim('"');
            if (w.Length == 0) return null;
            int hash = w.LastIndexOf('#');
            int port = 1;
            string path = w;
            if (hash > 0)
            {
                if (!int.TryParse(w[(hash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1)
                    throw new ParameterRefusalException(PropagationChannelDiagnostics.PatternPort(name, w));
                path = w[..hash];
            }
            if (baseDirectory is { Length: > 0 } root && !Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(root, path));
            return (path, port - 1);
        }

        ReverseOf = Word("Reverse", "") is { Length: > 0 } rev ? rev : null;
        _model = Word("PathLoss", "free");
        if (PropagationModels.Find(_model) is null)
            throw new ParameterRefusalException(PropagationChannelDiagnostics.UnknownModel(_model, string.Join(", ", PropagationModels.All.Select(m => m.Name))));
        _f0 = p.ContainsKey("f0") ? Num("f0", double.NaN) : null;
        if (_f0 is double f0v && !(f0v > 0)) throw new ParameterRefusalException(PropagationChannelDiagnostics.F0NotPositive());

        _txPos = Vector("TxPos");
        _rxPos = Vector("RxPos");
        if ((_txPos is null) != (_rxPos is null)) throw new ParameterRefusalException(PropagationChannelDiagnostics.PositionsTogether());
        _distance = Num("Distance", double.NaN);
        _txHeight = Num("TxHeight", 1.5);
        _rxHeight = Num("RxHeight", 1.5);
        _txVel = Vector("TxVel") ?? default;
        _rxVel = Vector("RxVel") ?? default;
        _txAim = p.ContainsKey("TxAz") || p.ContainsKey("TxEl") ? (Num("TxAz", 0), Num("TxEl", 0)) : null;
        _rxAim = p.ContainsKey("RxAz") || p.ContainsKey("RxEl") ? (Num("RxAz", 0), Num("RxEl", 0)) : null;
        _txRoll = Num("TxRoll", 0);
        _rxRoll = Num("RxRoll", 0);
        _txGainDb = Num("TxGain", 0);
        _rxGainDb = Num("RxGain", 0);
        _txZ = Imp("TxZ", 50);
        _rxZ = Imp("RxZ", 50);
        if (!(_txZ.Real > 0) || !(_rxZ.Real > 0)) throw new ParameterRefusalException(PropagationChannelDiagnostics.ImpedanceNotPositive());
        _txPattern = Pattern("TxPattern");
        _rxPattern = Pattern("RxPattern");
        _rxTantK = Num("RxTant", ambientC + Temperature.KelvinOffset);
        if (_rxTantK < 0) throw new ParameterRefusalException(PropagationChannelDiagnostics.AntennaTemperature());
        _timeOffset = Num("TimeOffset", 0);
        FrozenTimeS = _timeOffset;
        _dopplerComp = Word("DopplerComp", "none").ToLowerInvariant() switch
        {
            "none" => false,
            "common" => true,
            var w => throw new ParameterRefusalException(PropagationChannelDiagnostics.DopplerComp(w)),
        };
        // brief-pch-7 — fading, its Doppler, a tapped delay line's profile, the draws
        string fadingWord = Word("Fading", "");
        if (fadingWord.Length > 0)
            _fading = TapFading.TryParseKind(fadingWord, out var fk) ? fk
                      : throw new ParameterRefusalException(PropagationChannelDiagnostics.Fading(fadingWord));
        _kFactorDb = p.ContainsKey("KFactor") ? Num("KFactor", 0) : null;
        _nakagamiM = Num("NakagamiM", 1);
        if (!(_nakagamiM >= 0.5)) throw new ParameterRefusalException(PropagationChannelDiagnostics.NakagamiM(_nakagamiM));
        string dopplerWord = Word("Doppler", "classical");
        if (!TapFading.TryParseSpectrum(dopplerWord, out _spectrum))
            throw new ParameterRefusalException(PropagationChannelDiagnostics.DopplerSpectrum(dopplerWord));
        _velocity = p.ContainsKey("Velocity") ? Num("Velocity", 0) : null;
        _fd = p.ContainsKey("fD") ? Num("fD", 0) : null;
        if (_velocity < 0 || _fd < 0) throw new ParameterRefusalException(PropagationChannelDiagnostics.DopplerNegative());
        double sins = Num("Sinusoids", FadingProcess.DefaultSinusoids);
        if (!(sins >= 4) || sins != Math.Floor(sins)) throw new ParameterRefusalException(PropagationChannelDiagnostics.Sinusoids(sins));
        _sinusoids = (int)sins;
        _delaySpread = p.ContainsKey("DelaySpread") ? Num("DelaySpread", 0) : null;
        _seed = unchecked((ulong)(long)Math.Round(Num("Seed", 1)));
        _drop = (long)Math.Round(Num("Drop", 1));
        string multipath = Word("Multipath", "none").ToLowerInvariant();
        string profileWord = Word("Profile", "").Trim('"');
        if (profileWord.Length > 0 && multipath != "tdl")
            throw new ParameterRefusalException(PropagationChannelDiagnostics.ProfileNeedsTdl(profileWord));
        if (_kFactorDb is not null && _fading is null && multipath != "tdl" && !(multipath == "file"))
            throw new ParameterRefusalException(PropagationChannelDiagnostics.KFactorNeedsRician());
        switch (multipath)
        {
            case "none": break;
            case "tdl":
                if (profileWord.Length == 0) throw new ParameterRefusalException(PropagationChannelDiagnostics.TdlNeedsProfile(TdlProfiles.Names));
                _profile = TdlProfiles.Find(profileWord)
                           ?? throw new ParameterRefusalException(PropagationChannelDiagnostics.UnknownProfile(profileWord, TdlProfiles.Names));
                if (_fading == FadingKind.Rician && !_profile.HasLos)
                    throw new ParameterRefusalException(PropagationChannelDiagnostics.RicianNeedsLosProfile(_profile.Name));
                try { _ = TdlProfiles.Resolve(_profile, _delaySpread, _kFactorDb); }
                catch (PropagationRefusal ex) { throw new ParameterRefusalException(ex.Message); }
                break;
            case "file":
                _rayListPath = Word("File", "").Trim('"');
                if (_rayListPath.Length == 0) throw new ParameterRefusalException(PropagationChannelDiagnostics.RayListNeedsFile());
                if (baseDirectory is { Length: > 0 } dir && !Path.IsPathRooted(_rayListPath)) _rayListPath = Path.GetFullPath(Path.Combine(dir, _rayListPath));
                try { _rayList = RayListFile.Read(_rayListPath); }
                catch (PropagationRefusal ex) { throw new ParameterRefusalException(ex.Message); }
                break;
            case var w:
                throw new ParameterRefusalException(PropagationChannelDiagnostics.Multipath(w));
        }
        if (_fading == FadingKind.Rician && _kFactorDb is null && _profile is null && !(_rayList?.Any(r => r.KDb is not null) ?? false))
            throw new ParameterRefusalException(PropagationChannelDiagnostics.KFactorNeedsRician());
        // every parameter the channel does not own is the large-scale model's, as text in the registry's unit
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in p)
        {
            if (k.StartsWith("__", StringComparison.Ordinal) || OwnParameters.Contains(k) || Generic.Contains(k)) continue;
            values[k] = v.Kind switch
            {
                ValueKind.Real => v.AsReal().ToString("R", CultureInfo.InvariantCulture),
                ValueKind.String => v.AsString().Trim().Trim('"'),
                ValueKind.Bool => v.AsBool() ? "yes" : "no",
                _ => throw new ParameterRefusalException(PropagationChannelDiagnostics.ModelValue(k)),
            };
        }
        if (values.TryGetValue("Rain", out var rain) && rain.Equals("drawn", StringComparison.OrdinalIgnoreCase))
            throw new ParameterRefusalException(PropagationChannelDiagnostics.RainDrawn());
        // a terrain profile is Terrain= on the channel (Profile= names a tapped delay line), the model's own Profile
        if (Word("Terrain", "").Trim('"') is { Length: > 0 } terrain)
            values["Profile"] = baseDirectory is { Length: > 0 } pr && !Path.IsPathRooted(terrain) ? Path.GetFullPath(Path.Combine(pr, terrain)) : terrain;
        _modelValues = values;
        if (ReverseOf is null && _txPos is null && _model != "satellite" && !(_distance > 0))
            throw new ParameterRefusalException(PropagationChannelDiagnostics.NoPlacement());
        if (_txPattern is not null || _rxPattern is not null)
            if (PatternSource is null) throw new ParameterRefusalException(PropagationChannelDiagnostics.NoPatternReader());
        // the model's validity, refused here rather than at the first stamp, where only the carrier can be missing
        if (ReverseOf is null && _f0 is double at) _ = At(at);
    }

    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;
    public override string[] TerminalNames => ["tx", "rx"];
    public IReadOnlyList<int> PortBranches => _branch;

    /// <summary>The large-scale model's registry name.</summary>
    public string Model => (_forward ?? this)._model;

    public double TimeOffsetS => _timeOffset;
    public double RxTantKelvin => _rxTantK;

    public void SetCarrier(double hz) { if (hz > 0) _carrier = hz; }

    /// <summary>The frequency the large-scale model is evaluated at for <paramref name="hz"/>: f0, the carrier, or hz itself.</summary>
    public double ReferenceHz(double hz) => _f0 ?? _carrier ?? Math.Abs(hz);

    /// <summary><c>Reverse=</c>: this channel becomes <paramref name="forward"/>'s realization, transposed.</summary>
    public void BindReverse(PropagationChannelModel forward)
    {
        if (ReferenceEquals(forward, this) || forward.ReverseOf is not null)
            throw new ParameterRefusalException(PropagationChannelDiagnostics.ReverseOfReverse("", ReverseOf ?? ""));
        _forward = forward;
    }

    // ── the realization ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the channel is at one reference frequency: the large-scale result, the line-of-sight distance d the free-space
    /// part is over, the loss in excess of free space over d (dB, added losses in it), the rays (Doppler at that frequency),
    /// the common Doppler and its drift, the ends' geometry and frames.
    /// </summary>
    /// <para>brief-pch-7: <c>Fades</c> is each ray's fading (a static ray's gain is 1 at every instant), <c>FdHz</c> the maximum
    /// Doppler they were drawn at, <c>Profile</c> the tapped delay line's resolution when there is one.</para>
    public sealed record Realization(double ReferenceHz, PathLossResult LargeScale, double DistanceM, double ExcessDb,
                                     IReadOnlyList<ChannelRay> Rays, double CommonDopplerHz, double DriftHzps, ChannelGeometry Geometry,
                                     IReadOnlyList<string> Notes, IReadOnlyList<FadingProcess> Fades, double FdHz, TdlRealization? Profile)
    {
        /// <summary>True when any ray fades.</summary>
        public bool Fading => Fades.Any(f => f.Spec.Kind != FadingKind.Static);

        /// <summary>The coherence time: the lag at which the power-weighted correlation of the rays' gains first falls to one
        /// half (note §2.4; 0.24/f_D for the classical spectrum), infinite when it never does.</summary>
        public double CoherenceTimeS => SmallScale.CoherenceTime(Rays, Fades, FdHz);

        /// <summary>The bulk delay τ0 = d/c, reported and not simulated.</summary>
        public double BulkDelayS => DistanceM / PropagationMath.C;
        /// <summary>The largest excess delay.</summary>
        public double MaxExcessDelayS => Rays.Count == 0 ? 0 : Rays.Max(r => r.ExcessDelayS);
    }

    private readonly Dictionary<double, Realization> _realizations = [];

    /// <summary>The realization at reference frequency <paramref name="refHz"/>, refused with the model's own sentence.</summary>
    public Realization At(double refHz)
    {
        if (_forward is { } fwd)
        {
            var r = fwd.At(refHz);
            return r with { Rays = [.. r.Rays.Select(x => x.Transposed)],
                            Geometry = new ChannelGeometry(r.Geometry.RxPos, r.Geometry.TxPos, r.Geometry.RxVel, r.Geometry.TxVel) };
        }
        lock (_realizations)
        {
            if (_realizations.TryGetValue(refHz, out var cached)) return cached;
            var made = Realize(refHz);
            _realizations[refHz] = made;
            return made;
        }
    }

    private Realization Realize(double f)
    {
        try
        {
            ChannelGeometry geo;
            double hTx, hRx, dHorizontal;
            if (_txPos is { } tp && _rxPos is { } rp)
            {
                geo = new ChannelGeometry(tp, rp, _txVel, _rxVel);
                hTx = tp.Z; hRx = rp.Z;
                dHorizontal = Math.Sqrt(Math.Pow(rp.X - tp.X, 2) + Math.Pow(rp.Y - tp.Y, 2));
            }
            else
            {
                hTx = _txHeight; hRx = _rxHeight; dHorizontal = _distance;
                geo = new ChannelGeometry(new Vec3(0, 0, hTx), new Vec3(dHorizontal, 0, hRx), _txVel, _rxVel);
            }
            var inputs = new ModelInputs
            {
                Model = _model, FrequencyHz = f, DistanceM = _model == "satellite" ? double.NaN : dHorizontal,
                TxHeightM = hTx, RxHeightM = hRx, Values = _modelValues,
            };
            var ls = PropagationModels.Evaluate(inputs);
            var notes = new List<string>(ls.NoteList);
            double common = 0, drift = 0, d;
            List<ChannelRay> rays;
            if (ls.Geometry is { } sat)
            {
                // the satellite at its slant range, seen from the transmit end at the stated elevation
                d = sat.SlantRangeKm * 1000;
                double el = sat.ElevationDeg * Math.PI / 180;
                geo = geo with { RxPos = geo.TxPos + new Vec3(d * Math.Cos(el), 0, d * Math.Sin(el)) };
                common = sat.DopplerHz;
                drift = sat.DopplerRateHzps;
            }
            else d = geo.Distance;
            double excess = ls.LossDb - PropagationMath.FreeSpaceDb(f, d);
            // brief-pch-7 — the maximum Doppler: fD, else Velocity (or the ends' relative speed) at the reference frequency
            double fd = _fd ?? (_velocity ?? (geo.RxVel - geo.TxVel).Length) * f / PropagationMath.C;
            var fades = new List<FadingProcess>();
            TdlRealization? tdl = null;
            FadingProcess Fade(int i, FadingKind kind, double? kLin, DopplerShape shape)
                => new(new TapFading(kind, shape, fd, kLin ?? 0, _nakagamiM), _seed, _drop, $"{InstancePath}:tap{i + 1}", _sinusoids);
            static double? KLin(double? db) => db is double x ? Math.Pow(10, x / 10) : null;
            if (_profile is { } prof)
            {
                tdl = TdlProfiles.Resolve(prof, _delaySpread, prof.HasLos ? _kFactorDb : null);
                var los = geo.LineOfSight(f);
                rays = [];
                for (int i = 0; i < tdl.Taps.Count; i++)
                {
                    var tap = tdl.Taps[i];
                    rays.Add(new ChannelRay($"tap {i + 1}", Math.Sqrt(tap.Power), tap.DelayS, 0, los.Departure, los.ArrivalFrom));
                    var kind = _fading switch
                    {
                        FadingKind.Nakagami => FadingKind.Nakagami,
                        FadingKind.Rayleigh => FadingKind.Rayleigh,
                        _ => tap.K is null ? FadingKind.Rayleigh : FadingKind.Rician,
                    };
                    fades.Add(Fade(i, kind, tap.K, _spectrum));
                }
                notes.AddRange(tdl.Notes);
            }
            else if (_rayList is { } list)
            {
                // a profile that does not sum to one is normalized (the large-scale loss carries the mean), and says so
                double sum = list.Sum(r => Math.Pow(10, r.PowerDb / 10));
                double norm = 1 / Math.Sqrt(sum);
                var listed = geo.FromList(list, f);
                rays = [];
                for (int i = 0; i < list.Count; i++)
                {
                    var row = list[i];
                    var kind = row.KDb is not null ? FadingKind.Rician
                               : _fading is { } fk ? (fk == FadingKind.Rician && _kFactorDb is null ? FadingKind.Rayleigh : fk)
                               : row.Spectrum is not null ? FadingKind.Rayleigh : FadingKind.Static;
                    var shape = row.Spectrum is { } sp && TapFading.TryParseSpectrum(sp, out var rs) ? rs : _spectrum;
                    var ray = listed[i] with { Gain = listed[i].Gain * norm };
                    // a fading tap's motion is its fading: no static Doppler unless the file states one
                    if (kind != FadingKind.Static && row.DopplerHz is null) ray = ray with { DopplerHz = 0 };
                    rays.Add(ray);
                    fades.Add(Fade(i, kind, KLin(row.KDb ?? _kFactorDb), shape));
                }
                notes.Add($"{list.Count} tap(s) from {Path.GetFileName(_rayListPath)}, each along the line of sight at both ends"
                          + (fades.Any(x => x.Spec.Kind != FadingKind.Static) ? "." : ", held static."));
                if (Math.Abs(10 * Math.Log10(sum)) > 0.005)
                    notes.Add(string.Format(CultureInfo.InvariantCulture, "{0}: its powers sum to {1:0.###} dB and are normalized to 0 dB "
                                                + "(the large-scale loss carries the mean).", Path.GetFileName(_rayListPath), 10 * Math.Log10(sum)));
            }
            else if (_model.Equals("tworay", StringComparison.OrdinalIgnoreCase))
            {
                // the model's ground term becomes a ray: the free space is over the direct path, the reflection is summed as fields
                var ground = ls.Terms.FirstOrDefault(t => t.Name == "ground reflection");
                excess -= ground?.Db ?? 0;
                var pol = Text("Polarization", "vertical") == "horizontal" ? TwoRayModel.Polarization.Horizontal : TwoRayModel.Polarization.Vertical;
                var gamma = TwoRayModel.Reflection(f, geo.GrazingRad, Number("GroundEr", 15), Number("GroundSigma", 0.005), pol);
                rays = [geo.LineOfSight(f), geo.GroundRay(f, gamma)];
            }
            else rays = [geo.LineOfSight(f)];
            if (fades.Count == 0)
            {
                // Multipath=none: each ray of the geometry fades as Fading= says (a moving end's Doppler is then its fading's)
                var kind = _fading ?? FadingKind.Static;
                for (int i = 0; i < rays.Count; i++) fades.Add(Fade(i, kind, KLin(_kFactorDb), _spectrum));
                if (kind != FadingKind.Static) rays = [.. rays.Select(r => r with { DopplerHz = 0 })];
            }
            if (fades.Any(x => x.Spec.Kind != FadingKind.Static))
            {
                var inv = CultureInfo.InvariantCulture;
                int n = fades.Count(x => x.Spec.Kind != FadingKind.Static);
                notes.Add(string.Format(inv, "{0} of {1} tap(s) fade ({2}), {3} Doppler spectrum, f_D = {4:G4} Hz at {5}, {6} sinusoids each; "
                                             + "Seed {7}, Drop {8}.", n, fades.Count,
                    string.Join(", ", fades.Where(x => x.Spec.Kind != FadingKind.Static).Select(x => x.Spec.Kind.ToString().ToLowerInvariant()).Distinct()),
                    _spectrum.ToString().ToLowerInvariant(), fd, Ghz(f), _sinusoids, unchecked((long)_seed), _drop));
                if (fd == 0)
                    notes.Add("The fading has no Doppler (Velocity and fD are 0 or unstated, the ends still): each drop is one frozen draw.");
            }
            if (common != 0 || drift != 0)
                rays = [.. rays.Select(r => r with { DopplerHz = r.DopplerHz + common })];
            if (_dopplerComp)
            {
                double nu0 = Strongest(rays).DopplerHz;
                rays = [.. rays.Select(r => r with { DopplerHz = r.DopplerHz - nu0 })];
                drift = 0;
                notes.Add($"DopplerComp=common: the common Doppler {PropagationMath.F(nu0)} Hz is compensated at the transmit end.");
            }
            return new Realization(f, ls, d, excess, rays, Strongest(rays).DopplerHz, drift, geo, notes, fades, fd, tdl);
        }
        catch (PropagationRefusal ex) { throw new ParameterRefusalException(ex.Message); }
    }

    private string Text(string name, string def)
        => _modelValues.TryGetValue(name, out var v) && v.Length > 0 ? v.ToLowerInvariant() : def;

    private double Number(string name, double def)
        => _modelValues.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : def;

    private static ChannelRay Strongest(IReadOnlyList<ChannelRay> rays) => rays.MaxBy(r => r.Gain.Magnitude)!;

    // ── the ends ─────────────────────────────────────────────────────────────────────────────────

    private ChannelPattern? PatternAt((string Path, int Port)? which, double hz, string end)
    {
        if (which is not { } w) return null;
        ChannelPattern pat;
        try { pat = PatternSource!(w.Path, w.Port, hz); }
        catch (InvalidOperationException ex) { throw new ParameterRefusalException(PropagationChannelDiagnostics.Pattern(end, ex.Message)); }
        if (Math.Abs(pat.FrequencyHz - hz) > 1e-9 * Math.Max(hz, 1) && _said.Add($"{end}:{pat.FrequencyHz}:{hz}"))
            _pending.Add(($"channel-pattern-nearest:{end}:{hz}",
                $"{end}Pattern: '{pat.Record}' has no far field at {Ghz(hz)}; its nearest, {Ghz(pat.FrequencyHz)}, is used."));
        return pat;
    }

    private static string Ghz(double hz) => (hz / 1e9).ToString("0.######", CultureInfo.InvariantCulture) + " GHz";

    /// <summary>Each end's reference impedance and reflection at <paramref name="hz"/> ≥ 0: a pattern's record, or the
    /// stated impedance presented (the reference its conjugate, S11 = 0).</summary>
    public (Complex Z0, Complex S) End(int port, double hz)
    {
        bool tx = port == 0;
        if (PatternAt(tx ? _txPattern : _rxPattern, hz, tx ? "Tx" : "Rx") is { } pat) return (new Complex(pat.Z0, 0), pat.S11);
        return (Complex.Conjugate(tx ? _txZ : _rxZ), Complex.Zero);
    }

    /// <summary>The impedance a port presents at <paramref name="hz"/>.</summary>
    public Complex PresentedZ(int port, double hz)
    {
        var (z0, s) = End(port, hz);
        return s == Complex.Zero ? Complex.Conjugate(z0) : z0 * (1 + s) / (1 - s);
    }

    private AntennaFrame Frame(bool tx, Realization r)
    {
        var aim = tx ? _txAim : _rxAim;
        double roll = tx ? _txRoll : _rxRoll;
        if (aim is { } a) return AntennaFrame.Of(a.Az, a.El, roll);
        var toward = tx ? r.Geometry.RxPos - r.Geometry.TxPos : r.Geometry.TxPos - r.Geometry.RxPos;
        return AntennaFrame.Toward(toward, roll);
    }

    private static (Complex X, Complex Y, Complex Z) Field(ChannelPattern pat, AntennaFrame frame, Vec3 u)
    {
        var (th, ph) = frame.Angles(u);
        var (et, ep) = pat.Field(th, ph);
        return frame.ToGlobal(th, ph, et, ep);
    }

    private static Complex Dot((Complex X, Complex Y, Complex Z) a, (Complex X, Complex Y, Complex Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static (Complex, Complex, Complex) UnitConj((Complex X, Complex Y, Complex Z) a)
    {
        double n = Math.Sqrt(a.X.Magnitude * a.X.Magnitude + a.Y.Magnitude * a.Y.Magnitude + a.Z.Magnitude * a.Z.Magnitude);
        return n > 0 ? (Complex.Conjugate(a.X) / n, Complex.Conjugate(a.Y) / n, Complex.Conjugate(a.Z) / n) : (0, 0, 0);
    }

    /// <summary>
    /// The two antennas' coupling along each ray at <paramref name="hz"/>: √(G_tx·G_rx) for two gains; e_tx·e_rx for two
    /// patterns, both realized-gain fields in the global frame (so polarization mismatch is in it); a gain and a pattern,
    /// √G times the pattern's field projected on its own polarization along the line of sight (the gain end co-polar).
    /// </summary>
    public Complex[] Couplings(Realization r, double hz)
    {
        var tp = PatternAt(_txPattern, hz, "Tx");
        var rp = PatternAt(_rxPattern, hz, "Rx");
        double gt = Math.Pow(10, _txGainDb / 20), gr = Math.Pow(10, _rxGainDb / 20);
        var los = r.Geometry.LineOfSight(r.ReferenceHz);
        var ft = tp is null ? null : Frame(true, r);
        var fr = rp is null ? null : Frame(false, r);
        var polT = tp is null ? default : UnitConj(Field(tp, ft!, los.Departure));
        var polR = rp is null ? default : UnitConj(Field(rp, fr!, los.ArrivalFrom));
        var c = new Complex[r.Rays.Count];
        for (int i = 0; i < c.Length; i++)
        {
            var ray = r.Rays[i];
            if (tp is null && rp is null) c[i] = gt * gr;
            else if (rp is null) c[i] = gr * Dot(Field(tp!, ft!, ray.Departure), polT);
            else if (tp is null) c[i] = gt * Dot(Field(rp, fr!, ray.ArrivalFrom), polR);
            else c[i] = Dot(Field(tp, ft!, ray.Departure), Field(rp, fr!, ray.ArrivalFrom));
        }
        return c;
    }

    // ── H(f, t) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One ray's share of H at one frequency: its amplitude (large scale, antennas, ray gain and the rotation
    /// e^(−j2πf·τ)), its excess delay, its Doppler and drift at that frequency, and (brief PCH-7) its fading, whose gain at the
    /// channel time multiplies the amplitude (conjugated on the conjugate side, <see cref="Conjugate"/>).</summary>
    public readonly record struct RayTerm(Complex Amplitude, double DelayS, double DopplerHz, double DriftHzps, FadingProcess? Fade = null,
                                          bool Conjugate = false)
    {
        /// <summary>The fading gain at channel time <paramref name="t"/> (s): 1 for a static ray.</summary>
        public Complex FadeAt(double t)
        {
            if (Fade is null || Fade.Sinusoids == 0) return Complex.One;
            var g = Fade.Gain(t);
            return Conjugate ? Complex.Conjugate(g) : g;
        }
    }

    /// <summary>Every ray's term at the signed frequency <paramref name="hz"/> (a negative one is the conjugate side:
    /// amplitudes conjugated, Doppler negated); empty at DC.</summary>
    public RayTerm[] Terms(double hz)
    {
        if (hz == 0) return [];
        double f = Math.Abs(hz);
        var r = At(ReferenceHz(f));
        var c = Couplings(r, f);
        double amp = Math.Pow(10, -r.ExcessDb / 20) * PropagationMath.C / (4 * Math.PI * f * r.DistanceM);
        double scale = f / r.ReferenceHz;
        var terms = new RayTerm[r.Rays.Count];
        for (int i = 0; i < terms.Length; i++)
        {
            var ray = r.Rays[i];
            var a = amp * c[i] * ray.Gain * Complex.FromPolarCoordinates(1, -2 * Math.PI * f * ray.ExcessDelayS);
            double nu = ray.DopplerHz * scale, dr = r.DriftHzps * scale;
            var fade = i < r.Fades.Count ? r.Fades[i] : null;
            terms[i] = hz > 0 ? new RayTerm(a, ray.ExcessDelayS, nu, dr, fade)
                              : new RayTerm(Complex.Conjugate(a), ray.ExcessDelayS, -nu, -dr, fade, Conjugate: true);
        }
        return terms;
    }

    /// <summary>H at the signed frequency <paramref name="hz"/> and run time <paramref name="t"/> (s, after <see cref="FrozenTimeS"/>).</summary>
    public Complex H(double hz, double t = 0)
    {
        double tt = FrozenTimeS + t;
        Complex sum = 0;
        foreach (var term in Terms(hz))
            sum += term.Amplitude * term.FadeAt(tt) * Complex.FromPolarCoordinates(1, 2 * Math.PI * term.DopplerHz * tt + Math.PI * term.DriftHzps * tt * tt);
        return sum;
    }

    // ── the stamp ────────────────────────────────────────────────────────────────────────────────

    private int[] Nodes4(ElaboratedComponent c) => [c.Nodes[0], 0, c.Nodes.Length > 1 ? c.Nodes[1] : 0, 0];

    /// <summary>S at the signed angular frequency <paramref name="omega"/>: [[S11, 0], [H, S22]], H frozen (left out while
    /// <see cref="Stepping"/>).</summary>
    public Complex[,] SAt(double omega, out Complex[] z0)
    {
        double hz = omega / (2 * Math.PI);
        double f = Math.Abs(hz);
        var (z0t, st) = End(0, f);
        var (z0r, sr) = End(1, f);
        if (omega < 0) { z0t = Complex.Conjugate(z0t); z0r = Complex.Conjugate(z0r); st = Complex.Conjugate(st); sr = Complex.Conjugate(sr); }
        z0 = [z0t, z0r];
        return new Complex[,] { { st, 0 }, { Stepping ? Complex.Zero : H(hz), sr } };
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var s = SAt(omega, out var z0);
        IdealSBlockModel.StampWaveConstraints(mna, Nodes4(c), s, z0, _branch);
    }

    public override Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz)
    {
        _activityNote = null;
        foreach (double hz in freqsHz)
        {
            var s = SAt(2 * Math.PI * hz, out _);
            // σ_max of [[a, 0], [h, b]]: the largest eigenvalue of SᴴS
            double p = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude, q = s[1, 1].Magnitude * s[1, 1].Magnitude;
            double x = (Complex.Conjugate(s[1, 0]) * s[1, 1]).Magnitude;
            double sigma2 = 0.5 * (p + q + Math.Sqrt((p - q) * (p - q) + 4 * x * x));
            if (sigma2 > 1 + 1e-12)
            {
                _activityNote = $"|S21| = {Math.Sqrt(sigma2):G4} > 1 at {hz / 1e9:G6} GHz: the antennas' gains exceed the path's loss";
                return Activity.BlackBox;
            }
        }
        return Activity.Passive;
    }

    public override string? PassivationNote => _activityNote;
    private string? _activityNote;

    // ── noise ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The receive pin's impedance at the antenna temperature, from the rx net to ground.</summary>
    public override IReadOnlyList<NoiseSourceDecl> NoiseSources { get; } = [new NoiseSourceDecl("RxTant", 1, -1)];

    public override void EvaluateNoise(in PortVoltages v, double kelvin, Span<NoiseDensity> into)
        => into[0] = NoiseDensity.Thermal((1 / PresentedZ(1, _carrier ?? 1e9)).Real, _rxTantK);

    public override double[,]? NoiseDensitiesAt(ElaboratedComponent c, in PortVoltages bias, double kelvin, IReadOnlyList<double> freqsHz)
    {
        var d = new double[freqsHz.Count, 1];
        for (int k = 0; k < freqsHz.Count; k++)
            d[k, 0] = NoiseDensity.Thermal((1 / PresentedZ(1, Math.Max(Math.Abs(freqsHz[k]), 1))).Real, _rxTantK).White;
        return d;
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings()
    {
        if (_pending.Count == 0) return [];
        var drained = _pending.ToArray();
        _pending.Clear();
        return drained;
    }

    // ── what a run reports (note §5.6) ───────────────────────────────────────────────────────────

    /// <summary>The realization at the reference frequency for <paramref name="hz"/>, in sentences: the model and its document,
    /// the breakdown, τ0, ν0, the rays.</summary>
    public IReadOnlyList<string> Describe(double hz)
    {
        var inv = CultureInfo.InvariantCulture;
        var r = At(ReferenceHz(hz));
        var lines = new List<string>
        {
            $"model {r.LargeScale.Model} ({r.LargeScale.Document}) at {Ghz(r.ReferenceHz)}: path loss {r.LargeScale.LossDb.ToString("0.###", inv)} dB",
        };
        lines.AddRange(r.LargeScale.Terms.Select(t => $"  {t.Name}: {t.Db.ToString("0.###", inv)} dB"));
        if (r.LargeScale.SigmaDb > 0) lines.Add($"  shadowing σ {r.LargeScale.SigmaDb.ToString("0.##", inv)} dB, held at 0 (a draw belongs to a drop)");
        lines.Add(string.Format(inv, "bulk delay τ0 = {0:G6} s over {1:G6} m (reported, not simulated); common Doppler ν0 = {2:G6} Hz{3}",
            r.BulkDelayS, r.DistanceM, r.CommonDopplerHz, r.DriftHzps != 0 ? string.Format(inv, ", drifting {0:G6} Hz/s", r.DriftHzps) : ""));
        lines.Add(string.Format(inv, "{0} ray(s); RMS delay spread {1:G4} s, coherence bandwidth {2}", r.Rays.Count,
            RayStatistics.RmsDelaySpread(r.Rays), double.IsInfinity(RayStatistics.CoherenceBandwidthHz(r.Rays)) ? "unbounded (one ray)"
                : RayStatistics.CoherenceBandwidthHz(r.Rays).ToString("G4", inv) + " Hz"));
        if (r.Fading)
        {
            // brief-pch-7 — the small scale's regime (note §2.4): f_D and the coherence time
            double tc = r.CoherenceTimeS;
            lines.Add(string.Format(inv, "fading: f_D = {0:G4} Hz, coherence time {1}", r.FdHz,
                double.IsInfinity(tc) ? "unbounded (the correlation never falls to one half)" : tc.ToString("G4", inv) + " s"));
        }
        lines.AddRange(r.Notes);
        return lines;
    }
}
