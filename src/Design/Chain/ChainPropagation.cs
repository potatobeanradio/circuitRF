// The link's propagation (brief PCH-3; docs/design/propagation-channel.md §9.1). The path in front of the chain is
// closed against one of the large-scale models of RfCore.Propagation (PropagationModels, the one registry the
// Propagation Channel component reads too), and its received power is stated with the margins an availability needs:
//
//   L      = Lfs + (L_model − Lfs) + Σ added losses + Attenuation·d/1000     the breakdown, each a row and a cube
//   Prx    = EIRP − L + RxAntennaGain                                        rain at the availability is IN L
//
//   M_shadow = σ · Q⁻¹(1 − p)                                  lognormal shadowing, σ from the model or stated
//   M_fade   = −10·log10(x),  F(x) = 1 − p                     F the fading power's CDF at mean 1:
//                Rayleigh 1 − e^(−x)  (so M_fade = −10·log10(−ln p), 19.98 dB at 99 %, 30.00 dB at 99.9 %)
//                Rician   Σ_n e^(−K) Kⁿ/n! · P(n+1, (K+1)x)    (1 − Q₁(√(2K), √(2(K+1)x)), a Poisson mixture)
//                Nakagami P(m, m·x)                             (P the regularized lower incomplete gamma)
//   M_comp   = −10·log10(x),  ∫ φ(z) F(x·10^(−σz/10)) dz = 1 − p     lognormal-shadowed fading, the default
//   M_sum    = M_shadow + M_fade                               the conservative bound, Margin=sum
//
//   Eb/N0    closed form, uncoded, Gray coded, at the target BER:
//                BPSK, QPSK  Q(√(2γ)) ;  M-PSK (2/k)·Q(√(2kγ)·sin(π/M)) ;  square M-QAM (4/k)(1 − 1/√M)·Q(√(3kγ/(M − 1)))
//   SNR_req  = Eb/N0 + 10·log10(k)                             the symbol rate equal to the noise bandwidth
//   margin   = Prx − M − sensitivity,  sensitivity = MDS + SNR_req      met when margin ≥ 0
//
// Model=free with Distance and Attenuation (and nothing else) is the link as it was before PCH-3, number for number:
// the free-space term over the 3D distance with equal heights is today's 20·log10(4π·d·f/c) to the last bit (√(d²)
// is d in IEEE arithmetic), and every other term is a 0 that subtracts nothing.

using System.Globalization;
using RfCore.Data;
using RfCore.Propagation;
using RfCore.Statistics;

namespace CircuitRF.Design.Chain;

/// <summary>One row of the link's loss breakdown: what it is, the cube it is written as, and its value, dB.</summary>
public sealed record ChainLossRow(string Name, string Cube, double Db);

/// <summary>The margins an availability needs, dB. <paramref name="AppliedDb"/> is the one the link margin takes:
/// the composite, or the sum with <c>Margin=sum</c>. <paramref name="RainDb"/> is the rain at the availability, which
/// is already in the received power.</summary>
public sealed record ChainMargins(double AvailabilityPct, string Fading, double SigmaDb, double KFactorDb, double M,
                                  double ShadowDb, double FadeDb, double CompositeDb, double SumDb, string Method,
                                  double AppliedDb, double RainDb);

/// <summary>The SNR a modulation needs at a target BER, uncoded: its Eb/N0 and the SNR in the noise bandwidth.</summary>
public sealed record ChainRequiredSnr(string Modulation, double TargetBer, double EbN0Db, double SnrDb, bool Stated);

/// <summary>The link's propagation at one frequency.</summary>
public sealed record ChainPropagationResult(
    string Model, string Document, double TotalDb, double FreeSpaceDb, double AttenuationDb,
    IReadOnlyList<ChainLossRow> Rows, double SigmaDb, double? LosProbability, SlantGeometry? Geometry,
    ChainMargins Margins, ChainRequiredSnr? RequiredSnr, IReadOnlyList<string> Notes);

/// <summary>What the link margin comes to, after the budget: the margin, whether the availability is met, and the
/// distance at which the margin is zero (NaN where it cannot be found; <paramref name="DistanceNote"/> says why).</summary>
public sealed record ChainLinkMargin(double PrxDbm, double MarginsDb, double SensitivityDbm, double LinkMarginDb,
                                     bool Met, double ZeroMarginDistanceM, string? DistanceNote);

public static class ChainPropagation
{
    // ── the Propagation section's own parameters ──────────────────────────────────────────────────────────────────

    public const string Free = "free";
    public const string GroupPath = "Path", GroupMargins = "Margins";

    /// <summary>The words a fading distribution is chosen by.</summary>
    public static readonly string[] Fadings = ["none", "rayleigh", "rician", "nakagami"];

    /// <summary>The modulations a required SNR is worked out for, with their bits per symbol.</summary>
    public static readonly (string Name, int Bits, bool Qam)[] Modulations =
        [("bpsk", 1, false), ("qpsk", 2, false), ("8psk", 3, false), ("16qam", 4, true), ("64qam", 6, true), ("256qam", 8, true)];

    /// <summary>The modulation a blank <c>Modulation</c> means: the chain's signal, the Demo bench's NR 64-QAM.</summary>
    public const string ChainSignal = "64qam";

    /// <summary>The section's own parameters beyond the three the link always had (Distance, Attenuation,
    /// RxAntennaGain): the model, the heights, and the margins. The model's parameters and the added losses come from
    /// the registry (<see cref="PropagationModels"/>).</summary>
    public static readonly IReadOnlyList<ChainParam> OwnParameters =
    [
        new("Model", "", "", Numeric: false, BlankAllowed: true, Options: [.. PropagationModels.All.Select(m => m.Name)],
            Help: "the large-scale model, by its ITU-R recommendation or 3GPP report; blank: free space", Placeholder: Free),
        new("TxHeight", "", "m", BlankAllowed: true, Help: "the transmit antenna's height; blank: 1.5 m", Placeholder: "1.5", ShowUnit: true),
        new("RxHeight", "", "m", BlankAllowed: true, Help: "the receive antenna's height; blank: 1.5 m", Placeholder: "1.5", ShowUnit: true),
        new("Fading", "", "", Numeric: false, BlankAllowed: true, Options: Fadings,
            Help: "the small-scale fading the margin covers; blank: none", Placeholder: "none"),
        new("KFactor", "", "dB", BlankAllowed: true, Help: "Rician: the K-factor; blank: the model's, where it gives one", Placeholder: "model"),
        new("m", "", "", BlankAllowed: true, Help: "Nakagami: the shape m, 0.5 or more; blank: 1 (Rayleigh)", Placeholder: "1"),
        new("Sigma", "", "dB", BlankAllowed: true, Help: "the shadowing standard deviation; blank: the model's", Placeholder: "model"),
        new("Margin", "", "", Numeric: false, BlankAllowed: true, Options: ["composite", "sum"],
            Help: "composite: the margin of lognormal-shadowed fading; sum: the shadowing and fading margins added, the conservative bound; blank: composite",
            Placeholder: "composite"),
        new("TargetBer", "", "", BlankAllowed: true, Help: "the bit error rate the link must reach, uncoded: it sets the required SNR; blank: the Required SNR as stated", Placeholder: "none"),
        new("Modulation", "", "", Numeric: false, BlankAllowed: true, Options: [.. Modulations.Select(m => m.Name)],
            Help: "with a TargetBer, the modulation; blank: the chain's signal, 64-QAM as the Demo bench sends", Placeholder: ChainSignal),
    ];

    /// <summary>The names the chain reads itself and does not hand to the model.</summary>
    private static readonly HashSet<string> ChainNames = new(StringComparer.OrdinalIgnoreCase)
        { "Distance", "Attenuation", "RxAntennaGain", "Model", "TxHeight", "RxHeight", "Fading", "KFactor", "m", "Sigma", "Margin", "TargetBer", "Modulation" };

    /// <summary>The link's keys as version 2 knew them; any other (but <c>Model=free</c>) needs version 3.</summary>
    internal static bool UsesNewFields(ChainLink link) =>
        link.Path.Any(kv => kv.Key is not ("Distance" or "Attenuation" or "RxAntennaGain")
                            && !(kv.Key == "Model" && (kv.Value.Trim().Length == 0 || kv.Value.Trim().Equals(Free, StringComparison.OrdinalIgnoreCase))));

    /// <summary>The model the link names; null when it names none there is.</summary>
    public static ModelDescriptor? ModelOf(ChainLink link)
        => PropagationModels.Find(link.GetPath("Model").Trim() is { Length: > 0 } m ? m : Free);

    /// <summary>The registry's parameters for the link's model, Sigma left to the chain where the model has none of
    /// its own: the model's (group "Large scale"), then the added losses (group "Added losses"), Availability moved to
    /// the Margins it also sets.</summary>
    public static IReadOnlyList<ModelParameter> RegistryParameters(ModelDescriptor model) => [.. model.Parameters, .. AddedLosses.Parameters];

    // ── the editor's groups ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A registry parameter as an editor row: blank by default, its registry default shown in the field.</summary>
    public static ChainParam AsChainParam(ModelParameter p) =>
        new(p.Name, "", p.Unit, Numeric: p.Kind == ParameterKind.Number, BlankAllowed: true, Options: p.Words,
            Help: p.Help, IsFile: p.Kind == ParameterKind.File, Placeholder: p.Default.Length > 0 ? p.Default : "auto", ShowUnit: true);

    /// <summary>The Path group: the distance, the attenuation, the receive gain, the model and the heights.</summary>
    public static IReadOnlyList<ChainParam> PathGroup =>
        [.. ChainLink.PathParameters, .. OwnParameters.Where(p => p.Name is "Model" or "TxHeight" or "RxHeight")];

    /// <summary>The Large scale group: the model's own parameters, as the registry groups them.</summary>
    public static IReadOnlyList<ChainParam> LargeScaleGroup(ModelDescriptor model) => [.. model.Parameters.Select(AsChainParam)];

    /// <summary>The Added losses group, as the registry groups it, but for the Availability the Margins also read.</summary>
    public static IReadOnlyList<ChainParam> AddedLossesGroup =>
        [.. AddedLosses.Parameters.Where(p => p.Name != "Availability").Select(AsChainParam)];

    /// <summary>The Margins group: the availability, the fading and shadowing, the margin taken and the required SNR's
    /// target. Sigma is the model's own row where the model reads it.</summary>
    public static IReadOnlyList<ChainParam> MarginsGroup(ModelDescriptor model) =>
        [AsChainParam(AddedLosses.Parameters.First(p => p.Name == "Availability")),
         .. OwnParameters.Where(p => p.Name is "Fading" or "KFactor" or "m" or "Margin" or "TargetBer" or "Modulation"
                                     || p.Name == "Sigma" && !ModelOwnsSigma(model))];

    /// <summary>True when the model reads <c>Sigma</c> itself (log-distance), so the chain's Sigma is the model's.</summary>
    private static bool ModelOwnsSigma(ModelDescriptor model) => model.Parameters.Any(p => p.Name == "Sigma");

    // ── reading the section at the chain's frequency ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The section's numbers read at <paramref name="fHz"/>: every number-valued registry parameter written as an
    /// expression in freq or a table becomes its number (a value may carry its own unit, "40 mm/h"), and a profile
    /// path becomes a full one. Refusals name the parameter.
    /// </summary>
    internal static void Resolve(ChainLink link, double fHz, string? baseDirectory, List<string> refusals)
    {
        if (ModelOf(link) is not { } model) return;   // the refusal says which models there are
        foreach (var p in RegistryParameters(model))
        {
            if (!link.Path.TryGetValue(p.Name, out var text) || text.Trim().Length == 0) continue;
            if (p.Kind == ParameterKind.File)
            {
                if (baseDirectory is not null && !Path.IsPathRooted(text.Trim())) link.Path[p.Name] = Path.GetFullPath(Path.Combine(baseDirectory, text.Trim()));
                continue;
            }
            if (p.Kind != ParameterKind.Number) continue;
            string t = WithoutUnit(text, p.Unit);
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) { link.Path[p.Name] = t; continue; }
            if (!ChainScaling.IsScaled(t)) continue;   // left to the model's own refusal
            if (ChainScaling.At(t, fHz, baseDirectory, out string why) is { } v) link.Path[p.Name] = v.ToString("R", CultureInfo.InvariantCulture);
            else refusals.Add($"Propagation: {p.Name} = '{text.Trim()}' {why}.");
        }
        foreach (var p in OwnParameters.Where(p => p.Numeric && p.Name is not ("TxHeight" or "RxHeight")))
        {
            string text = link.GetPath(p.Name);
            if (!ChainScaling.IsScaled(text)) continue;
            if (ChainScaling.At(text, fHz, baseDirectory, out string why) is { } v) link.Path[p.Name] = v.ToString("R", CultureInfo.InvariantCulture);
            else refusals.Add($"Propagation: {p.Name} = '{text.Trim()}' {why}.");
        }
    }

    /// <summary>The text with its parameter's own unit taken off the end ("40 mm/h" → "40").</summary>
    private static string WithoutUnit(string text, string unit)
    {
        string t = text.Trim();
        return unit.Length > 0 && t.EndsWith(unit, StringComparison.Ordinal) && t.Length > unit.Length ? t[..^unit.Length].Trim() : t;
    }

    /// <summary>Why the section (already read) cannot be evaluated, one sentence each.</summary>
    internal static void Refusals(ChainLink link, double fHz, List<string> r)
    {
        int before = r.Count;
        if (ModelOf(link) is not { } model)
        {
            r.Add($"Propagation: there is no model '{link.GetPath("Model").Trim()}'; the models are {string.Join(", ", PropagationModels.All.Select(m => m.Name))}.");
            return;
        }
        var known = RegistryParameters(model).Select(p => p.Name).Concat(ChainNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in link.Path.Keys.Where(k => !known.Contains(k)))
            r.Add($"Propagation: {model.Name} has no parameter '{key}'.");
        foreach (var (name, words) in new[] { ("Fading", Fadings), ("Margin", ["composite", "sum"]), ("Modulation", Modulations.Select(m => m.Name).ToArray()) })
            if (link.GetPath(name).Trim() is { Length: > 0 } w && !words.Contains(w, StringComparer.OrdinalIgnoreCase))
                r.Add($"Propagation: {name} is one of {string.Join(", ", words)}; it is '{w}'.");
        foreach (var name in new[] { "TxHeight", "RxHeight" })
            if (link.GetPath(name).Trim() is { Length: > 0 } h && !(ChainLink.TryMetres(h, out double hm) && hm > 0))
                r.Add($"Propagation: {name} = '{h}' is not a height above 0 m (metres, or with m or km).");
        foreach (var name in new[] { "KFactor", "m", "Sigma", "TargetBer" })
            if (link.GetPath(name).Trim() is { Length: > 0 } v && !ChainValue.TryParse(v, out _))
                r.Add($"Propagation: {name} = '{v}' is not a number{ChainScaling.WhyNotANumber(v)}.");
        if (Num(link, "m") is var m && double.IsFinite(m) && !(m >= 0.5)) r.Add("Propagation: Nakagami m is 0.5 or more.");
        if (Num(link, "Sigma") is var s && double.IsFinite(s) && !(s >= 0)) r.Add("Propagation: Sigma cannot be below 0 dB.");
        if (Num(link, "TargetBer") is var ber && double.IsFinite(ber) && !(ber > 0 && ber < 0.5)) r.Add("Propagation: TargetBer is above 0 and below 0.5.");
        if (r.Count > before) return;
        try { Evaluate(link, fHz); }
        catch (PropagationRefusal ex) { r.Add($"Propagation: {ex.Message}"); }
    }

    private static double Num(ChainLink link, string key) => ChainValue.Or(link.GetPath(key), double.NaN);

    // ── the evaluation ────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly (string Prefix, string Name, string Cube)[] AddedKinds =
    [
        ("partition", "partitions", "Partitions_dB"), ("P.2108", "clutter", "Clutter_dB"), ("P.2109", "building entry", "BuildingEntry_dB"),
        ("P.833", "vegetation", "Vegetation_dB"), ("polarization", "polarization", "Polarization_dB"), ("pointing", "pointing", "Pointing_dB"),
        ("P.676", "gas", "Gas_dB"), ("P.530", "rain", "Rain_dB"), ("P.618", "rain", "Rain_dB"), ("P.531", "ionosphere", "Ionosphere_dB"),
    ];

    /// <summary>The breakdown's rows in the order a link budget lists them; each is written as its cube, 0 when absent.</summary>
    public static readonly (string Name, string Cube)[] RowOrder =
    [
        ("free space", "Lfs_dB"), ("model excess", "Excess_dB"), ("attenuation", "Atm_dB"), ("gas", "Gas_dB"), ("rain", "Rain_dB"),
        ("clutter", "Clutter_dB"), ("partitions", "Partitions_dB"), ("building entry", "BuildingEntry_dB"), ("vegetation", "Vegetation_dB"),
        ("polarization", "Polarization_dB"), ("pointing", "Pointing_dB"), ("ionosphere", "Ionosphere_dB"),
    ];

    /// <summary>
    /// The link's propagation at <paramref name="fHz"/>, its inputs already read as numbers. Throws
    /// <see cref="PropagationRefusal"/> where the model refuses; <see cref="Refusals"/> has turned that into a sentence
    /// before any budget asks.
    /// </summary>
    public static ChainPropagationResult Evaluate(ChainLink link, double fHz)
    {
        var model = ModelOf(link) ?? throw new PropagationRefusal($"There is no propagation model '{link.GetPath("Model")}'.");
        bool satellite = model.Name == "satellite";
        ChainLink.TryMetres(link.GetPath("Distance"), out double d);
        double htx = Height(link, "TxHeight"), hrx = Height(link, "RxHeight");
        var registry = RegistryParameters(model).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = link.Path.Where(kv => registry.Contains(kv.Key) && !ChainNames.Contains(kv.Key) || kv.Key == "Sigma" && ModelOwnsSigma(model))
                              .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        // brief-pch-11 — Rain=drawn is the Link bench's: it draws the rain's time percentage per drop. A budget is one evaluation, so
        // it takes the rain at the availability, as Rain=fixed, which the bench's drop sweep then checks.
        bool drawnRain = values.TryGetValue("Rain", out var rainWord) && rainWord.Trim().Equals("drawn", StringComparison.OrdinalIgnoreCase);
        if (drawnRain) values["Rain"] = "fixed";
        var res = PropagationModels.Evaluate(new ModelInputs
        {
            Model = model.Name, FrequencyHz = fHz, DistanceM = satellite ? double.NaN : d,
            TxHeightM = htx, RxHeightM = hrx, Values = values,
        });

        // Free space over the model's own distance: the slant range on a satellite path, the 3D distance otherwise.
        double dModel = res.Geometry is { } g ? g.SlantRangeKm * 1000 : Math.Sqrt(d * d + (htx - hrx) * (htx - hrx));
        double fs = PropagationMath.FreeSpaceDb(fHz, dModel);
        double atm = ChainValue.Or(link.GetPath("Attenuation"), 0) * (satellite ? dModel : d) / 1000;
        var sums = RowOrder.ToDictionary(x => x.Cube, _ => 0.0);
        double model0 = 0;
        foreach (var t in res.Terms)
        {
            var kind = AddedKinds.FirstOrDefault(k => t.Name.StartsWith(k.Prefix, StringComparison.Ordinal));
            if (kind.Cube is not null) sums[kind.Cube] += t.Db;
            else if (t.Name.Contains("clutter", StringComparison.Ordinal)) sums["Clutter_dB"] += t.Db;   // TR 38.811's, part of the model
            else model0 += t.Db;
        }
        sums["Lfs_dB"] = fs;
        sums["Excess_dB"] = model0 - fs;
        sums["Atm_dB"] = atm;
        // P.618 combines rain, clouds and scintillation into one figure (§2.5), which its row is; the note says so.
        bool p618 = res.Terms.Any(t => t.Name.StartsWith("P.618", StringComparison.Ordinal));
        var rows = RowOrder.Select(x => new ChainLossRow(x.Cube == "Rain_dB" && p618 ? "rain (P.618)" : x.Name, x.Cube, sums[x.Cube])).ToList();
        // Free space, the attenuation, then the rest: with free space and nothing else the rest is a 0 that subtracts
        // nothing, and the total is today's Lfs + A to the bit.
        double rest = rows.Where(x => x.Cube is not ("Lfs_dB" or "Atm_dB")).Sum(x => x.Db);
        double total = fs + atm + rest;

        var notes = new List<string>(res.NoteList);
        if (drawnRain)
            notes.Add("Rain=drawn: the budget takes the rain at the availability; the Link bench draws it drop by drop.");
        double sigma =ModelOwnsSigma(model) || link.GetPath("Sigma").Trim().Length == 0 ? res.SigmaDb : Num(link, "Sigma");
        double rain = sums["Rain_dB"];
        var margins = Margins(link, res, sigma, rain, notes);
        return new ChainPropagationResult(model.Name, res.Document, total, fs, atm, rows, sigma, res.LosProbability, res.Geometry,
                                          margins, RequiredSnr(link), notes);
    }

    private static double Height(ChainLink link, string key)
        => link.GetPath(key).Trim().Length > 0 && ChainLink.TryMetres(link.GetPath(key), out double h) ? h : 1.5;

    /// <summary>The availability the link states: the registry's own Availability, % (its default 99).</summary>
    public static double AvailabilityPct(ChainLink link)
    {
        string t = link.GetPath("Availability").Trim();
        return t.Length > 0 && double.TryParse(WithoutUnit(t, "%"), NumberStyles.Float, CultureInfo.InvariantCulture, out double a) ? a
             : double.Parse(AddedLosses.Parameters.First(p => p.Name == "Availability").Default, CultureInfo.InvariantCulture);
    }

    private static ChainMargins Margins(ChainLink link, PathLossResult res, double sigma, double rain, List<string> notes)
    {
        double a = AvailabilityPct(link), p = a / 100;
        string fading = link.GetPath("Fading").Trim().ToLowerInvariant() is { Length: > 0 } f ? f : "none";
        double k = double.NaN, m = double.NaN;
        if (fading == "rician")
        {
            if (link.GetPath("KFactor").Trim().Length > 0) k = Num(link, "KFactor");
            else if (res.Spreads?.KFactorDb is { } ks) { k = ks.Mean; notes.Add($"Rician K = {PropagationMath.F(k)} dB, the model's mean K-factor."); }
            else throw new PropagationRefusal($"Fading=rician needs a KFactor: {res.Model} gives none.");
        }
        if (fading == "nakagami") m = link.GetPath("m").Trim().Length > 0 ? Num(link, "m") : 1;
        double shadow = sigma * Normal.InverseQ(1 - p);
        // Each (fading, K, m, σ, p) is integrated once: a budget reads its link many times, and a distance search more.
        var (fade, comp) = MarginCache.GetOrAdd((fading, k, m, sigma, p), key =>
        {
            var cdf = FadingCdf(key.Fading, key.K, key.M);
            double f = cdf is null ? 0 : key.Fading == "rayleigh" ? -10 * Math.Log10(-Math.Log(key.P)) : MarginOf(cdf, 0, key.P);
            return (f, cdf is null ? key.Sigma * Normal.InverseQ(1 - key.P) : key.Sigma == 0 ? f : MarginOf(cdf, key.Sigma, key.P));
        });
        string method = link.GetPath("Margin").Trim().ToLowerInvariant() is "sum" ? "sum" : "composite";
        return new ChainMargins(a, fading, sigma, k, m, shadow, fade, comp, shadow + fade, method,
                                method == "sum" ? shadow + fade : comp, rain);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Fading, double K, double M, double Sigma, double P), (double, double)> MarginCache = new();

    /// <summary>The CDF of a fading power of mean 1; null for no fading.</summary>
    public static Func<double, double>? FadingCdf(string fading, double kDb, double m) => fading switch
    {
        "rayleigh" => x => x <= 0 ? 0 : 1 - Math.Exp(-x),
        "rician" => RicianCdf(Math.Pow(10, kDb / 10)),
        "nakagami" => x => x <= 0 ? 0 : GammaFunction.P(m, m * x),
        _ => null,
    };

    /// <summary>1 − Q₁(√(2K), √(2(K+1)x)), as the Poisson mixture of gamma CDFs it is: the Rician power is a sum of
    /// K/(K+1) of line of sight and a scattered part, which given n Poisson(K) terms is Gamma(n + 1) at scale 1/(K+1).</summary>
    private static Func<double, double> RicianCdf(double k) => x =>
    {
        if (x <= 0) return 0;
        double y = (k + 1) * x;
        if (k == 0) return 1 - Math.Exp(-y);
        // P(n+1, y) = Σ_{j>n} e^(−y)·y^j/j!: the top one from the incomplete gamma, then down by adding positive terms,
        // so a small outage keeps its relative accuracy.
        int top = (int)Math.Ceiling(k + 12 * Math.Sqrt(k + 1) + 30);
        double ly = Math.Log(y), lk = Math.Log(k);
        double pn = GammaFunction.P(top + 2, y), sum = 0;
        for (int n = top; n >= 0; n--)
        {
            pn += Math.Exp(-y + (n + 1) * ly - GammaFunction.LogGamma(n + 2));
            sum += Math.Exp(-k + n * lk - GammaFunction.LogGamma(n + 1)) * pn;
        }
        return Math.Min(1, sum);
    };

    /// <summary>
    /// The margin −10·log10(x) at which the outage ∫ φ(z) F(x·10^(−σz/10)) dz is 1 − p: shadowing of σ dB (0: none)
    /// over the fading CDF F. The integral is the trapezoid rule over z in [−10, 10] at h = 0.02, which for a smooth
    /// integrand decaying as φ is accurate far below the 1e-6 dB the root is found to; the root by bisection in dB.
    /// </summary>
    public static double MarginOf(Func<double, double> cdf, double sigmaDb, double p)
    {
        double q = 1 - p;
        double Outage(double marginDb)
        {
            double x = Math.Pow(10, -marginDb / 10);
            if (sigmaDb == 0) return cdf(x);
            const double h = 0.02;
            double s = 0;
            for (int i = -500; i <= 500; i++)
            {
                double z = i * h, w = i is -500 or 500 ? 0.5 : 1;
                s += w * Normal.Pdf(z) * cdf(x * Math.Pow(10, -sigmaDb * z / 10));
            }
            return s * h;
        }
        double lo = -40, hi = 120;   // dB: an outage of nearly 1 below, of nearly 0 above
        for (int i = 0; i < 200 && hi - lo > 1e-11; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Outage(mid) > q) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    // ── the required SNR ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The required SNR the link states through a TargetBer; null when it states none.</summary>
    public static ChainRequiredSnr? RequiredSnr(ChainLink link)
    {
        if (link.GetPath("TargetBer").Trim().Length == 0) return null;
        string mod = link.GetPath("Modulation").Trim().ToLowerInvariant();
        bool stated = mod.Length > 0;
        if (!stated) mod = ChainSignal;
        double ber = Num(link, "TargetBer");
        if (Modulations.All(x => x.Name != mod) || !(ber > 0 && ber < 0.5)) return null;   // its refusal says why
        double ebn0 = EbN0Db(mod, ber);
        int k = Modulations.First(x => x.Name == mod).Bits;
        return new ChainRequiredSnr(mod, ber, ebn0, ebn0 + 10 * Math.Log10(k), stated);
    }

    /// <summary>The Eb/N0 a modulation needs for a bit error rate, dB, from the closed forms above (inverted exactly:
    /// each is one Q function of √γ).</summary>
    public static double EbN0Db(string modulation, double ber)
    {
        var (_, k, qam) = Modulations.First(x => x.Name == modulation);
        double mm = Math.Pow(2, k), gamma;
        if (k <= 2) gamma = Math.Pow(Normal.InverseQ(ber), 2) / 2;
        else if (!qam) gamma = Math.Pow(Normal.InverseQ(ber * k / 2) / Math.Sin(Math.PI / mm), 2) / (2 * k);
        else gamma = (mm - 1) / (3.0 * k) * Math.Pow(Normal.InverseQ(ber * k / (4 * (1 - 1 / Math.Sqrt(mm)))), 2);
        return 10 * Math.Log10(gamma);
    }

    // ── after the budget ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The link margin of a budget, and the distance at which it is zero; null without a link.</summary>
    public static ChainLinkMargin? LinkMargin(ChainBudgetResult r)
    {
        if (r.Design is not { Link: { } link } d || r.Totals is not { } t) return null;
        var l = link.Budget(d.FrequencyHz);
        if (l.Propagation is not { } p) return null;
        double sens = t.SensitivityDbm, margin = l.PrxDbm - p.Margins.AppliedDb - sens;
        var (dist, note) = ZeroMarginDistance(link, d.FrequencyHz, sens, p);
        return new ChainLinkMargin(l.PrxDbm, p.Margins.AppliedDb, sens, margin, margin >= 0, dist, note);
    }

    /// <summary>The distance at which Prx − M − sensitivity is zero, by bisection in log distance after bracketing by
    /// doubling or halving; NaN with a reason on a satellite path, or where the model refuses before the margin
    /// reaches zero.</summary>
    private static (double, string?) ZeroMarginDistance(ChainLink link, double fHz, double sens, ChainPropagationResult at)
    {
        if (at.Geometry is not null) return (double.NaN, "A satellite path's distance is its slant range from the altitude and elevation; there is no distance to solve for.");
        ChainLink.TryMetres(link.GetPath("Distance"), out double d0);
        var probe = link.Clone();
        double Margin(double dm)
        {
            probe.Path["Distance"] = dm.ToString("R", CultureInfo.InvariantCulture);
            var b = probe.Budget(fHz);
            return b.Propagation is { } pp ? b.PrxDbm - pp.Margins.AppliedDb - sens : double.NaN;
        }
        double m0 = Margin(d0);
        if (!double.IsFinite(m0)) return (double.NaN, null);
        double lo = d0, hi = d0, f = m0 > 0 ? 2 : 0.5;
        double prev = m0;
        for (int i = 0; ; i++)
        {
            double next = (m0 > 0 ? hi : lo) * f;
            double mn = Margin(next);
            if (!double.IsFinite(mn))
                return (double.NaN, $"The link margin does not reach 0 dB within the model's range: it is {D(prev)} dB at " +
                                    $"{D(m0 > 0 ? hi : lo)} m, and the model refuses {D(next)} m.");
            if (m0 > 0) { lo = hi; hi = next; } else { hi = lo; lo = next; }
            if (m0 > 0 ? mn <= 0 : mn > 0) break;
            prev = mn;
            if (i == 80) return (double.NaN, "The link margin does not reach 0 dB at any distance tried.");
        }
        for (int i = 0; i < 200 && hi / lo - 1 > 1e-13; i++)
        {
            double mid = Math.Sqrt(lo * hi);
            if (Margin(mid) > 0) lo = mid; else hi = mid;
        }
        return (Math.Sqrt(lo * hi), null);
    }

    private static string D(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The breakdown, the margins, the required SNR, the link margin and a satellite path's geometry, added to
    /// a budget's DataSet: the rows as scalars and as one cube over a labelled <c>term</c> axis.</summary>
    internal static void AddTo(DataSet ds, ChainBudgetResult r, ChainPropagationResult p)
    {
        static double Fin(double v) => double.IsFinite(v) ? v : double.NaN;
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [Fin(v)]) { Unit = unit });
        foreach (var row in p.Rows.Where(x => x.Cube is not ("Lfs_dB" or "Atm_dB")))   // those two the link always wrote
            One(row.Cube, "dB", row.Db);
        One("PathLoss_dB", "dB", p.TotalDb);
        var term = new Axis("term", [.. Enumerable.Range(0, p.Rows.Count).Select(i => (double)i)], "", [.. p.Rows.Select(x => x.Name)]);
        ds.Add("Loss_dB", new DataCube([term], [.. p.Rows.Select(x => Fin(x.Db))]) { Unit = "dB" });

        var m = p.Margins;
        One("Availability_pct", "%", m.AvailabilityPct);
        One("Sigma_dB", "dB", m.SigmaDb);
        One("Mshadow_dB", "dB", m.ShadowDb);
        One("Mfade_dB", "dB", m.FadeDb);
        One("Mcomp_dB", "dB", m.CompositeDb);
        One("Msum_dB", "dB", m.SumDb);
        One("Margins_dB", "dB", m.AppliedDb);
        if (p.RequiredSnr is { } q)
        {
            One("TargetBER", "", q.TargetBer);
            One("EbN0req_dB", "dB", q.EbN0Db);
            One("SNRreq_dB", "dB", q.SnrDb);
        }
        if (LinkMargin(r) is { } lm)
        {
            One("LinkMargin_dB", "dB", lm.LinkMarginDb);
            One("AvailabilityMet", "", lm.Met ? 1 : 0);
            One("ZeroMarginDistance_m", "m", lm.ZeroMarginDistanceM);
        }
        if (p.Geometry is { } g)
        {
            One("SlantRange_km", "km", g.SlantRangeKm);
            One("Latency_ms", "ms", g.LatencyS * 1e3);
            One("Doppler_Hz", "Hz", g.DopplerHz);
            One("DopplerRate_Hzps", "Hz/s", g.DopplerRateHzps);
        }
    }
}
