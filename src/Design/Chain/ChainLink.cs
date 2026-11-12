// A link in front of the receive chain: a transmit array, a line-of-sight path, and the chain's own receive array, whose
// size Mrx is its Combiner's N (1 with none). The transmitter and the path are not stages: a stage before the combiner is
// one receive element's branch, and the chain's noise is referred to that element's input. Every input but the element
// count and the distance may be a number, an expression in freq or a table (ChainScaling), read at the chain's frequency.
//
//   Pout   = Psat − Backoff                                each transmit element's PA output, dBm
//   EIRP   = Pout − Loss + AntennaGain + 20·log10(Mtx)    Mtx PAs in phase: Mtx times the power, Mtx times the directivity
//   Lfs    = 20·log10(4π·d·f / c)                          free space, dB
//   A      = Attenuation · d / 1000                        extra or atmospheric loss over the path, its dB/km at f
//   Prx    = EIRP − Lfs − A + RxAntennaGain                at EACH receive element
//   Pin    = min(Prx, IP1dB − RxBackoffDb)                 the chain's input: held at the ceiling, as power control would
//
// The DC power, with or without a link:
//
//   P_PA   = (Pout − Pout/G) / PAE                         each PA at its back-off, unless its Pdc is stated
//   P_TX   = Mtx · P_PA ;   P_RX = Mrx · Σ P_branch + Σ P_after   the branch's stages once per element, the combiner's
//                                                          and every stage after it once
//   P      = S · (P_TX + P_RX) ;   E_b = P / rate          S streams, each with its own hardware: E_b does not move with S
//
// Nothing here is a bench: EIRP and the path have no circuit to simulate. The chain's benches run at Pin, so what they
// verify is the receiver at the power the link delivers; the closed forms above are checked against an independent
// calculation (testdata/chain/link_reference.py).

using System.Globalization;
using System.Text.Json.Serialization;
using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>The link's numbers at the carrier, dBm and dB.</summary>
public sealed record ChainLinkBudget(double PoutDbm, double EirpDbm, double LfsDb, double AtmDb, double PrxDbm, int Mtx);

/// <summary>The DC power of a chain and its link, W, and the energy per bit, J.</summary>
public sealed record ChainPower(double PaW, double TxW, double RxW, double TotalW, double EnergyPerBitJ, int Mrx);

/// <summary>A transmitter and a path in front of the chain, each a set of inputs by name, as a stage's are.</summary>
public sealed class ChainLink
{
    public const double C = 299_792_458;

    public Dictionary<string, string> Tx { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Path { get; set; } = new(StringComparer.Ordinal);

    public static readonly IReadOnlyList<ChainParam> TxParameters =
    [
        new("Psat", "20", "dBm", Help: "each transmit element's PA saturated output power"),
        new("Backoff", "8", "dB", Help: "the PA's output back-off from Psat: Pout = Psat − Backoff"),
        new("Loss", "0", "dB", Help: "from each PA to its antenna element: a switch, a line"),
        new("AntennaGain", "0", "dBi", Help: "each transmit element's gain"),
        new("Elements", "1", "", Help: "Mtx, the transmit elements, in phase toward the receiver"),
        new("PaGain", "15", "dB", Help: "the PA's gain, for its DC power"),
        new("PAE", "", "%", BlankAllowed: true, Help: "the PA's power-added efficiency at the back-off; blank: Pdc"),
        new("Pdc", "", "W", BlankAllowed: true, Help: "each transmit element's DC power; blank: from PAE"),
    ];

    public static readonly IReadOnlyList<ChainParam> PathParameters =
    [
        new("Distance", "100", "m", Help: "the line-of-sight path: metres, or with m or km"),
        new("Attenuation", "0", "dB/km", Help: "extra or atmospheric attenuation along the path"),
        new("RxAntennaGain", "0", "dBi", Help: "each receive element's gain; the receive array is the chain's Combiner"),
    ];

    private static string Get(Dictionary<string, string> set, IReadOnlyList<ChainParam> ps, string key)
        => set.TryGetValue(key, out var v) ? v : ps.FirstOrDefault(p => p.Name == key)?.Default ?? "";

    public string GetTx(string key) => Get(Tx, TxParameters, key);
    public string GetPath(string key) => Get(Path, PathParameters, key);

    public ChainLink Clone() => new() { Tx = new(Tx, StringComparer.Ordinal), Path = new(Path, StringComparer.Ordinal) };

    private double TxNum(string key) => ChainValue.Or(GetTx(key), double.NaN);

    /// <summary>A distance in metres: bare, or with m or km ("30", "30 m", "0.03 km"). Not <see cref="ChainValue"/>,
    /// where a lone "m" is the milli prefix.</summary>
    public static bool TryMetres(string? text, out double metres)
    {
        metres = 0;
        string t = (text ?? "").Trim();
        double scale = 1;
        if (t.EndsWith("km", StringComparison.Ordinal)) { scale = 1000; t = t[..^2]; }
        else if (t.EndsWith('m')) t = t[..^1];
        if (!double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;
        metres = v * scale;
        return true;
    }

    /// <summary>The elements, Mtx: a whole number of them, 1 or more; null otherwise. Read from <see cref="Tx"/>, so
    /// not a key of its own in the file.</summary>
    [JsonIgnore]
    public int? Elements => ChainValue.TryParse(GetTx("Elements"), out double m) && m >= 1 && m == Math.Round(m) && m < int.MaxValue ? (int)m : null;

    // ── in a design being budgeted ────────────────────────────────────────────

    /// <summary>
    /// The link read at the chain's frequency (<see cref="ChainScaling.Resolve"/>): its expressions and tables written
    /// back as numbers, and the power each element receives made the chain's input power. Refusals name the input.
    /// </summary>
    internal static void Apply(ChainDesign r, List<string> refusals)
    {
        if (r.Link is not { } link) return;
        int before = refusals.Count;
        void Read(Dictionary<string, string> set, IReadOnlyList<ChainParam> ps, string what)
        {
            foreach (var p in ps.Where(p => p.Name is not ("Elements" or "Distance")))
            {
                string text = Get(set, ps, p.Name);
                if (!ChainScaling.IsScaled(text)) continue;
                if (ChainScaling.At(text, r.FrequencyHz, r.BaseDirectory, out string why) is { } v) set[p.Name] = v.ToString("R", CultureInfo.InvariantCulture);
                else refusals.Add($"{what}: {p.Name} = '{text.Trim()}' {why}.");
            }
        }
        Read(link.Tx, TxParameters, "Transmitter");
        Read(link.Path, PathParameters, "Path");
        if (refusals.Count == before) r.InputPowerDbm = link.Budget(r.FrequencyHz).PrxDbm;
    }

    /// <summary>Why the link (already read) cannot be budgeted, one sentence each.</summary>
    internal void Refusals(List<string> r)
    {
        foreach (var (set, ps, what) in new[] { (Tx, TxParameters, "Transmitter"), (Path, PathParameters, "Path") })
            foreach (var p in ps.Where(p => p.Name is not ("Elements" or "Distance")))
            {
                string text = Get(set, ps, p.Name);
                if (!ChainValue.TryParse(text, out _) && !(p.BlankAllowed && text.Trim().Length == 0))
                    r.Add($"{what}: {p.Name} = '{text}' is not a number.");
            }
        if (Elements is null) r.Add($"Transmitter: Elements = '{GetTx("Elements")}' is not a whole number of elements, 1 or more.");
        if (!TryMetres(GetPath("Distance"), out double d) || !(d > 0))
            r.Add($"Path: Distance = '{GetPath("Distance")}' is not a distance above 0 m (metres, or with m or km).");
        if (TxNum("PAE") is var pae && double.IsFinite(pae) && !(pae > 0 && pae <= 100))
            r.Add("Transmitter: PAE is a percentage above 0 and at most 100.");
        if (TxNum("Pdc") < 0) r.Add("Transmitter: Pdc cannot be below 0 W.");
    }

    /// <summary>The link's numbers at <paramref name="fHz"/>, its inputs already read as numbers.</summary>
    public ChainLinkBudget Budget(double fHz)
    {
        int mtx = Elements ?? 1;
        double pout = TxNum("Psat") - TxNum("Backoff");
        double eirp = pout - TxNum("Loss") + TxNum("AntennaGain") + 20 * Math.Log10(mtx);
        TryMetres(GetPath("Distance"), out double d);
        double lfs = 20 * Math.Log10(4 * Math.PI * d * fHz / C);
        double atm = ChainValue.Or(GetPath("Attenuation"), 0) * d / 1000;
        return new ChainLinkBudget(pout, eirp, lfs, atm, eirp - lfs - atm + ChainValue.Or(GetPath("RxAntennaGain"), 0), mtx);
    }

    /// <summary>
    /// In a link, the received power held at the receiver's ceiling, its input P1dB (from the stages' own budgets) less
    /// RxBackoffDb: a copy at the ceiling, with a note, when the link delivers more; the design itself otherwise. And a
    /// note when the transmitter's DC power is neither stated nor follows from a PAE.
    /// </summary>
    internal static ChainDesign Cap(ChainDesign d, IReadOnlyList<StageBudget> budgets, List<string> notes)
    {
        if (d.Link is not { } link) return d;
        if (link.GetTx("PAE").Trim().Length == 0 && link.GetTx("Pdc").Trim().Length == 0)
            notes.Add("The transmitter states neither a PAE nor a Pdc, so its DC power is taken as none.");
        double g = 1, inv = 0;
        foreach (var b in budgets)
        {
            if (double.IsFinite(b.Ip1dbDbm)) inv += g / Math.Pow(10, b.Ip1dbDbm / 10);
            g *= Math.Pow(10, b.GainDb / 10);
        }
        double cap = inv > 0 ? -10 * Math.Log10(inv) - d.RxBackoffDb : double.PositiveInfinity;
        if (!(d.InputPowerDbm > cap)) return d;
        static string D(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        notes.Add($"The link delivers {D(d.InputPowerDbm)} dBm to each element, above the receiver's ceiling of {D(cap)} dBm (its " +
                  $"input P1dB less {D(d.RxBackoffDb)} dB): the budget takes {D(cap)} dBm, as power control or a variable gain would hold it.");
        var c = d.Clone();
        c.InputPowerDbm = cap;
        return c;
    }

    // ── the readouts ──────────────────────────────────────────────────────────

    /// <summary>The DC power of a budget; null when it was refused.</summary>
    public static ChainPower? Power(ChainBudgetResult r)
    {
        if (r.Design is not { } d || ChainCapacity.Read(r) is not { } o) return null;
        int cmb = d.Stages.FindIndex(s => s.Part == ChainParts.Combiner);
        int mrx = cmb >= 0 ? r.Stages[cmb].Stage.ArrayN : 1;
        double branch = 0, after = 0;
        for (int i = 0; i < d.Stages.Count; i++)
            if (i < cmb) branch += ChainParts.StagePdcW(d.Stages[i]); else after += ChainParts.StagePdcW(d.Stages[i]);
        double pa = 0;
        int mtx = 0;
        if (d.Link is { } link)
        {
            var l = link.Budget(d.FrequencyHz);
            mtx = l.Mtx;
            if (ChainValue.TryParse(link.GetTx("Pdc"), out double stated)) pa = stated;
            else if (ChainValue.TryParse(link.GetTx("PAE"), out double pae))
            {
                double pout = Math.Pow(10, (l.PoutDbm - 30) / 10), gpa = Math.Pow(10, link.TxNum("PaGain") / 10);
                pa = (pout - pout / gpa) / (pae / 100);
            }
        }
        double tx = mtx * pa, rx = mrx * branch + after, total = d.Streams * (tx + rx);
        return new ChainPower(pa, tx, rx, total, o.RateBps > 0 && double.IsFinite(total / o.RateBps) ? total / o.RateBps : double.NaN, mrx);
    }

    /// <summary>The link's numbers and the DC power added to a budget's DataSet, as scalars.</summary>
    public static void AddTo(DataSet ds, ChainBudgetResult r)
    {
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [double.IsFinite(v) ? v : double.NaN]) { Unit = unit });
        if (Power(r) is { } p)
        {
            One("Mrx", "", p.Mrx);
            One("PdcPA_W", "W", p.PaW);
            One("PdcTx_W", "W", p.TxW);
            One("PdcRx_W", "W", p.RxW);
            One("Pdctot_W", "W", p.TotalW);
            One("Ebit_J", "J", p.EnergyPerBitJ);
            One("Ebit_pJ", "pJ", p.EnergyPerBitJ * 1e12);
        }
        if (r.Design is not { Link: { } link } d) return;
        var l = link.Budget(d.FrequencyHz);
        One("Mtx", "", l.Mtx);
        One("Pout_PA_dBm", "dBm", l.PoutDbm);
        One("EIRP_dBm", "dBm", l.EirpDbm);
        One("Lfs_dB", "dB", l.LfsDb);
        One("Atm_dB", "dB", l.AtmDb);
        One("Prx_dBm", "dBm", l.PrxDbm);
        One("Pin_dBm", "dBm", d.InputPowerDbm);
    }

    /// <summary>A power as a person reads it: "1.23 W", "45.6 mW".</summary>
    public static string Watts(double w)
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return w >= 1 ? $"{N(w)} W" : w >= 1e-3 ? $"{N(w * 1e3)} mW" : w > 0 ? $"{N(w * 1e6)} µW" : "0 W";
    }
}
