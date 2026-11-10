// .cchain file format — rev 1: an RF receive chain as a budget, an ordered list of stages, each a part from the
// System library with that part's own parameters, plus the system inputs a budget is read against.
//
// System.Text.Json, WriteIndented, WhenWritingNull, reject-on-newer-format-version, AtomicFile write — the .csmith
// and .crail spelling of the same thing. The system inputs are numbers whose NAMES carry their unit (FrequencyHz,
// InputPowerDbm); a stage's parameters are TEXT, exactly as the part's own netlist line carries them ("2.35 GHz",
// "Butterworth", "16"), because they are copied onto that line unchanged when the chain is transferred to a schematic.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Chain;

/// <summary>One stage: a name, the part it is, and the part's parameters by their own names.</summary>
public sealed class ChainStage
{
    public string Name { get; set; } = "";

    /// <summary>A <see cref="ChainParts"/> token: a System block's own type (<c>Amp</c>, <c>Mixer</c>, …) or one of
    /// the composed stages (<c>PhaseShifter</c>, <c>Combiner</c>, …).</summary>
    public string Part { get; set; } = "Amp";

    /// <summary>The part's parameters. A key the part does not list is carried onto the schematic as written and
    /// ignored by the budget; an absent key takes the part's default.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The parameter's text, or the part's default for it.</summary>
    public string Get(string key) =>
        Parameters.TryGetValue(key, out var v) ? v : ChainParts.Find(Part)?.Default(key) ?? "";

    public ChainStage Clone() => new() { Name = Name, Part = Part, Parameters = new(Parameters, StringComparer.Ordinal) };
}

/// <summary>A receive chain: the system inputs and the stages, signal path order.</summary>
public sealed class ChainDesign
{
    /// <summary>The input (RF) frequency, Hz.</summary>
    public double FrequencyHz { get; set; } = 2.45e9;

    /// <summary>The noise bandwidth the floor, the MDS and the SNR are read in, Hz.</summary>
    public double BandwidthHz { get; set; } = 1e6;

    /// <summary>The signal at the chain's input, dBm. In an array, the power at EACH element.</summary>
    public double InputPowerDbm { get; set; } = -70;

    /// <summary>The source's (antenna's) noise temperature, K. 290 K is the T0 a noise figure is referred to.</summary>
    public double TemperatureK { get; set; } = 290;

    /// <summary>The SNR the demodulator needs, dB: sensitivity is the MDS plus this.</summary>
    public double RequiredSnrDb { get; set; } = 10;

    public List<ChainStage> Stages { get; set; } = [];

    /// <summary>Why this design cannot be budgeted, or null. Every cause, one sentence each.</summary>
    public IReadOnlyList<string> Refusals()
    {
        var r = new List<string>();
        if (!(FrequencyHz > 0)) r.Add("The frequency must be above 0 Hz.");
        if (!(BandwidthHz > 0)) r.Add("The bandwidth must be above 0 Hz.");
        if (!(TemperatureK >= 0)) r.Add("The source temperature cannot be below 0 K.");
        if (Stages.Count == 0) r.Add("The chain has no stages.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int combiners = 0;
        foreach (var s in Stages)
        {
            if (NameValidatorOk(s.Name) is { } why) r.Add($"Stage '{s.Name}': {why}");
            else if (!names.Add(s.Name)) r.Add($"Two stages are named '{s.Name}'.");
            if (ChainParts.Find(s.Part) is not { } part)
            {
                r.Add($"Stage '{s.Name}': '{s.Part}' is not a chain part. The parts are {string.Join(", ", ChainParts.All.Select(p => p.Token))}.");
                continue;
            }
            foreach (var p in part.Parameters.Where(p => p.Numeric))
                if (!ChainValue.TryParse(s.Get(p.Name), out _) && !(p.BlankAllowed && s.Get(p.Name).Trim().Length == 0))
                    r.Add($"Stage '{s.Name}': {p.Name} = '{s.Get(p.Name)}' is not a number.");
            if (part.Token == ChainParts.Combiner)
            {
                combiners++;
                ChainParts.ReadCombiner(s, r);
            }
        }
        if (combiners > 1) r.Add("A chain has at most one Combiner: the stages before it are one element's branch.");
        return r;
    }

    /// <summary>A stage name is an instance name on the schematic, so it follows the instance-name rule.</summary>
    private static string? NameValidatorOk(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a stage needs a name.";
        if (!char.IsLetter(name[0])) return "a stage name starts with a letter.";
        if (name.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) return "a stage name holds letters, digits and _ only.";
        return null;
    }

    public ChainDesign Clone() => new()
    {
        FrequencyHz = FrequencyHz, BandwidthHz = BandwidthHz, InputPowerDbm = InputPowerDbm,
        TemperatureK = TemperatureK, RequiredSnrDb = RequiredSnrDb,
        Stages = [.. Stages.Select(s => s.Clone())],
    };
}

/// <summary>A stage parameter's text as a number: a bare number, or one with a unit (<c>2.35 GHz</c>, <c>16 dB</c>,
/// <c>350MHz</c>). Frequency prefixes scale; dB, dBm, deg and Ohm do not.</summary>
public static class ChainValue
{
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        string num = t, unit = "";
        int sp = t.IndexOfAny([' ', '\t']);
        if (sp > 0) { num = t[..sp]; unit = t[(sp + 1)..].Trim(); }
        else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                 && Units.TrySplitGluedNumber(t, out var n, out var u)) { num = n; unit = u; }
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (unit.Length == 0 || Units.IsLogarithmic(unit) || unit is "deg" or "Ohm" or "Ω" or "K") return true;
        if (Units.Scale(unit) is { } s) { value *= s; return true; }
        return false;
    }

    /// <summary>The number, or <paramref name="fallback"/> when the text is blank or not one.</summary>
    public static double Or(string? text, double fallback) => TryParse(text, out var v) ? v : fallback;

    /// <summary>A number as a parameter's text: invariant, round-trippable, no exponent for ordinary values.</summary>
    public static string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>Reads and writes <c>.cchain</c>. Framework-free.</summary>
public static class ChainDesignIo
{
    public const string Extension = ".cchain";
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CchainFile
    {
        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public double? FrequencyHz { get; set; }
        public double? BandwidthHz { get; set; }
        public double? InputPowerDbm { get; set; }
        public double? TemperatureK { get; set; }
        public double? RequiredSnrDb { get; set; }
        public List<ChainStage>? Stages { get; set; }
    }

    public static string Serialize(ChainDesign d) => JsonSerializer.Serialize(new CchainFile
    {
        FrequencyHz = d.FrequencyHz, BandwidthHz = d.BandwidthHz, InputPowerDbm = d.InputPowerDbm,
        TemperatureK = d.TemperatureK, RequiredSnrDb = d.RequiredSnrDb, Stages = d.Stages,
    }, JsonOpts);

    /// <summary>The design as written, unvalidated: a half-built chain still opens, and
    /// <see cref="ChainDesign.Refusals"/> says what is missing.</summary>
    /// <exception cref="InvalidDataException">The JSON is empty, malformed, or from a newer circuitRF.</exception>
    public static ChainDesign Deserialize(string json)
    {
        CchainFile? f;
        try { f = JsonSerializer.Deserialize<CchainFile>(json, JsonOpts); }
        catch (JsonException ex) { throw new InvalidDataException(ex.Message, ex); }
        if (f is null) throw new InvalidDataException(Empty);
        if (f.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, Newer, f.FormatVersion, CurrentFormatVersion));
        var d = new ChainDesign();
        d.FrequencyHz   = f.FrequencyHz   ?? d.FrequencyHz;
        d.BandwidthHz   = f.BandwidthHz   ?? d.BandwidthHz;
        d.InputPowerDbm = f.InputPowerDbm ?? d.InputPowerDbm;
        d.TemperatureK  = f.TemperatureK  ?? d.TemperatureK;
        d.RequiredSnrDb = f.RequiredSnrDb ?? d.RequiredSnrDb;
        foreach (var s in f.Stages ?? [])
            d.Stages.Add(new ChainStage
            {
                Name = s.Name ?? "", Part = s.Part ?? "Amp",
                Parameters = new Dictionary<string, string>(s.Parameters ?? [], StringComparer.Ordinal),
            });
        return d;
    }

    private const string Empty = "The .cchain file is empty.";
    private const string Newer = ".cchain FormatVersion {0} is newer than this circuitRF reads ({1}). Update the application.";

    public static ChainDesign LoadFromFile(string path) => Deserialize(File.ReadAllText(path));

    public static void SaveToFile(string path, ChainDesign design) => AtomicFile.WriteAllText(path, Serialize(design));
}
