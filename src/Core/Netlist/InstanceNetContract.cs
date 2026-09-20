using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;

namespace CircuitRF.Core.Netlist;

/// <summary>
/// How many nets a primitive's <c>.cnl</c> instance line binds — <c>R:R1 n1 n2 R=1k</c> is two, and
/// <c>Tuner:T1 n_drain 0 …</c> is two even though <c>TunerModel.PortCount</c> is one.
///
/// <para><b>Why this is not <see cref="ComponentModel.PortCount"/> (AUT-8 R-aut8-4, AUT-10 R-aut10-2).</b>
/// "Port" means three different things across this codebase and none of them is "net". A resistor's
/// two ports are its two terminals; a FET's two ports are (gate,source) and (drain,source), which is
/// three nets; an ideal S-block's ports each take a signal net and a reference net, which is two nets
/// each; a Tuner declares one port and takes two nets, because its reference terminal is implicit on
/// the glyph. Reading a net count off <c>PortCount</c> is how the generated catalogue came to
/// describe <c>Tuner</c> as a one-net part — and a client that wrote it that way got a circuit whose
/// bias tee delivered nothing, <c>status: ok</c>, and Pout at the engine's floor sentinel at every
/// drive point. It reported the model as broken. The model is correct.</para>
///
/// <para><b>What used to happen with the wrong count.</b> Nothing, loudly. A one-net resistor reached
/// <c>ResistorModel.Stamp</c>, which indexes <c>c.Nodes[1]</c>, and surfaced as
/// <c>Index was outside the bounds of the array</c> — naming neither the line, the instance nor the
/// count. A one-net <c>Tuner</c> did not even do that: the elaborator's own family expansions are
/// guarded by <c>resolvedNodes.Length == 3</c> and friends, so a short line SKIPS the expansion in
/// silence and a wired-wrong circuit simulates to completion.</para>
///
/// <para><b>A null is a deliberate blank, not an omission.</b> Every type in
/// <c>ComponentModelFactory</c>'s registry is accounted for here; the ones that return null return it
/// because the count genuinely is not knowable from the model alone, and each says why.
/// <c>InstanceNetContractTests</c> holds the table exhaustive over the registry, so a new primitive
/// cannot quietly become a third category — which is the shape of defect this whole brief removes.</para>
/// </summary>
public static class InstanceNetContract
{
    /// <summary>
    /// The number of nets this model's instance line must bind, or null when the model cannot state
    /// one. Asked of the CONSTRUCTED model, so the parameterised types answer from their own resolved
    /// parameters rather than from a number written down twice.
    /// </summary>
    public static int? Expected(ComponentModel model) => model switch
    {
        // ── Two-terminal linear parts. PortCount is the net count for these, and only these. ──
        // The whole RLC family answers through its two BASE types, not member by member — six more
        // names here would be six chances to forget one, and a forgotten one is a part whose net
        // count nothing states.
        ResistorModel or CapacitorModel or InductorModel
            or SeriesRlcBranchModel or ParallelRlcBranchModel
            or BeadModel or ShortModel or SemiCapacitorModel or MatchModel                    => 2,

        // Port and Term take a signal net and a reference net. The schematic draws one pin and the
        // extractor appends "0", which is why the symbol's pin count is not this number.
        PortModel or TermModel                                                                => 2,

        // ── Distributed and transmission-line parts ──
        TLineModel or MicrostripLineModel or MicrostripBendModel
            or MicrostripTaperModel or MicrostripKlopfModel                                   => 2,
        MicrostripTeeModel                                                                    => 3,
        MicrostripCrossModel                                                                  => 4,

        // The mutual-inductance element names two inductors by parameter, not two nets by position.
        MutualInductanceModel                                                                 => 0,

        // ── Sources, probes and terminations whose reference terminal is implicit on the glyph ──
        // Each declares ONE port and takes TWO nets. This is the family the catalogue got wrong.
        // SeriesProbeModelBase covers IProbe and WSProbe alike — both bind two nets, G/+ then L/−.
        VdcModel or SeriesProbeModelBase or TunerModel or ToneSourceModelBase
            or P1ToneModel or PnToneModel or NonlinearCModel                                  => 2,

        // ── Controlled sources: an output pair and a control pair, two ports of two nets each ──
        VccsModel or VcvsModel                                                                => 4,

        // The cascade block is a two-port with an explicit reference at each end.
        ChainModel                                                                            => 4,

        // ── Ideal system blocks: every RF port is a signal net and a reference net ──
        // Atten is 2 ports → 4 nets; Balun and Circulator 3 → 6; Coupler 4 → 8.
        IdealSBlockModel sb                                                                   => 2 * sb.PortCount,
        DuplexerModel dx                                                                      => 2 * dx.PortCount,
        MixerModel mx                                                                         => 2 * mx.PortCount,

        // ── Semiconductor families. The count is what the USER DRAWS, before the elaborator expands
        // it into the model's internal port pairs — which is the only count an instance line states.
        DiodeModel                                                                            => 2,
        Devices.Fet.FetModelBase                                                              => 3,   // gate, drain, source
        BjtModel                                                                              => 3,   // collector, base, emitter
        Devices.Igbt.IgbtModel                                                                => 3,   // collector, gate, emitter
        Devices.Mos.VdmosModel                                                                => 3,   // drain, gate, source
        Devices.Jfet.JfetModel                                                                => 3,   // drain, gate, source
        Devices.Mos.MosfetModelBase                                                           => 4,   // drain, gate, source, bulk

        // ── Parameterised counts, answered from the model's own resolved state ──
        // Two nets per port, both differential: this is the case CLAUDE.md already calls out as
        // differing from the symbol's pin count by construction.
        SddModel sdd                                                                          => 2 * sdd.PortCount,
        ZPortModel zp                                                                         => 2 * zp.PortCount,
        // ── Deliberately unstated, because a better statement of the rule already exists ──
        // wBond is an N-or-N+1 part like SnP: 2M nets for an M-wire array, with an OPTIONAL trailing
        // reference net that the model reads when RefPin says so and ignores otherwise. PortCount
        // looks like the net count and is not — it counts the reference pin only when the parameter
        // turned it on, while the netlist may supply the net either way. WBondModel's own refusals
        // (the ground-plane return path among them) name the array and the missing return.
        WBondModel                                                                            => null,
        // SnP takes N nets or N+1 (the trailing one being a floating reference). CnlReader's own
        // ValidateSnpNets names the instance and both legal counts; a number here would be a second,
        // wrong statement of a rule that is not a number.
        SnpModel                                                                              => null,
        // ExtDevice's count is the provider's EXTERNAL pin count, which is not PortCount — that
        // includes the internal nodes the model asked for. It is also not fixed per type: a component
        // may state a smaller ConnectedPinCount to place a five-terminal model as a four-pin part.
        // Elaborator.BuildExternalDeviceNodes refuses a mismatch already, naming the type id, the
        // pin count and which of the two rules it was measured against.
        ExternalDeviceModel                                                                   => null,

        // Anything not listed. Not silence: InstanceNetContractTests fails on a registered type that
        // lands here, so a new primitive is either given a count or given a reason in this file.
        _                                                                                     => null,
    };

    // ── What the catalogue publishes (AUT-10 R-aut10-2) ──────────────────────

    /// <summary>
    /// What one <c>.cnl</c> type token's instance line binds, as the generated catalogue states it.
    ///
    /// <para>Exactly one of the three carries the answer, and the gate
    /// (<c>NetlistContractTests</c>) holds that true for every registered token: a fixed
    /// <paramref name="Count"/>, a <paramref name="DeterminedBy"/> parameter with the rule beside
    /// it, or a <paramref name="Rule"/> alone where nothing about the count is a number.</para>
    /// </summary>
    /// <param name="Count">The number of nets the line binds, when it is the same for every legal
    /// instance of the type.</param>
    /// <param name="DeterminedBy">The <c>.cnl</c> PARAMETER that sets the count — which is not
    /// always the symbol's own port-count parameter: an <c>SDD</c> instance line spells it
    /// <c>SddPortCount</c> where the schematic tile calls it <c>NumPorts</c>.</param>
    /// <param name="Rule">One sentence stating the rule, in the caller's own spelling. Empty
    /// wherever <paramref name="Count"/> alone is the whole answer.</param>
    public sealed record NetContract(int? Count, string? DeterminedBy, string Rule);

    /// <summary>
    /// The net contract for a <c>.cnl</c> type token.
    ///
    /// <para><b>MEASURED wherever a model can be built</b>, exactly as
    /// <c>ComponentCatalog.PortsOf</c> measures whether a symbol's pin count is fixed. The type is
    /// constructed at three port counts and <see cref="Expected"/> asked of each: three equal
    /// answers is a fixed count, an affine progression is a rule with the multiplier and the
    /// intercept read off the measurement, and anything else is reported as the three numbers it
    /// actually is. Nothing here writes a count down, so a component whose wiring changes cannot
    /// leave a stale number behind in a catalogue.</para>
    ///
    /// <para><b>The minimal parameters are chosen not to change the answer</b>, and the three-point
    /// probe is what proves it: a value that moved the count would show up as a progression rather
    /// than as a constant. <c>Match</c>'s <c>Design</c> is the EMPTY design, not a real one.</para>
    ///
    /// <para><b><see cref="_ruleOnly"/> is the rest</b> — the four tokens no measurement can reach,
    /// each because the rule genuinely is not a number. Three of them are <see cref="Expected"/>'s
    /// own null arms; the fourth (<c>VerilogA</c>) is a second token over the same model.</para>
    /// </summary>
    public static NetContract ForToken(string token)
    {
        if (_ruleOnly.TryGetValue(token, out string? stated))
            return new NetContract(null, PortCountKey(token), stated);

        var measured = new int?[_probes.Length];
        for (int i = 0; i < _probes.Length; i++)
            measured[i] = TryMinimalModel(token, _probes[i], out var model) ? Expected(model!) : null;

        // A token nothing can build and no rule names. Not silence: NetlistContractTests fails on a
        // registered token that lands here, so it is either measurable or given a sentence.
        if (measured.Any(m => m is null)) return new NetContract(null, null, "");

        int[] n = [.. measured.Select(m => m!.Value)];
        if (n.All(v => v == n[0])) return new NetContract(n[0], null, "");

        string key = PortCountKey(token) ?? "a parameter";
        int    per = n[1] - n[0];
        int    c   = n[0] - per * _probes[0];

        // Affine, and CHECKED at the third point rather than assumed from two — a count that is not
        // a straight line would otherwise be published as one.
        if (per > 0 && n.Select((v, i) => v == per * _probes[i] + c).All(ok => ok))
        {
            string tail = c == 0 ? "" : c > 0 ? $" + {c}" : $" - {-c}";
            return new NetContract(null, key,
                $"{key}=N binds {(per == 1 ? "N" : per + "N")}{tail} nets.");
        }

        return new NetContract(null, key,
            $"Set by {key}: " +
            string.Join(", ", _probes.Select((p, i) => $"{p} → {n[i]} nets")) + ".");
    }

    /// <summary>The port counts <see cref="ForToken"/> probes at. Three, because two points cannot
    /// tell an affine rule from a coincidence.</summary>
    private static readonly int[] _probes = [2, 3, 4];

    /// <summary>
    /// The <c>.cnl</c> parameter that sets a variadic type's net count.
    ///
    /// <para><b>It is the NETLIST's spelling, which is not always the symbol's.</b>
    /// <c>ComponentTypeRegistry.PortCountParameter</c> answers <c>NumPorts</c> for all three
    /// variadic boxes because that is what the parameter panel calls it; a <c>.cnl</c> line spells
    /// it <c>SddPortCount</c>, <c>ZPortCount</c> and <c>NumPorts</c> respectively, and the catalogue
    /// is read by something that is about to write a <c>.cnl</c>.</para>
    /// </summary>
    public static string? PortCountKey(string token) => token.ToUpperInvariant() switch
    {
        "SDD"      => "SddPortCount",
        "Z_PORT"   => "ZPortCount",
        "SNP"      => "NumPorts",
        "SWITCH"   => "Throws",
        "WBOND"    => "Arrays",
        "VERILOGA" => "Pins",
        _          => null,
    };

    /// <summary>
    /// The tokens whose contract is a sentence rather than a number, with the sentence.
    ///
    /// <para>Three of the four are <see cref="Expected"/>'s own <c>null</c> arms, which is the same
    /// judgement stated for the same reason: a number here would be a second, wrong statement of a
    /// rule that is not a number. <c>VerilogA</c> is a second token over
    /// <see cref="ExternalDeviceModel"/> and cannot be constructed without the file it names.</para>
    /// </summary>
    private static readonly Dictionary<string, string> _ruleOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SnP"] =
            "NumPorts=N binds N nets, or N+1 where the extra trailing net is a floating reference. " +
            "Both counts are legal and the reader tells them apart by how many were written.",
        ["wBond"] =
            "2 nets per wire array — Arrays=M binds 2M — plus one trailing reference net when " +
            "RefPin is on. The arrays come from the design the instance carries or names, so there " +
            "is no count at all until one is chosen.",
        ["ExtDevice"] =
            "Set by the model this instance names: the provider's EXTERNAL pin count, which is not " +
            "its port count and does not include the internal nodes the model asked for. An " +
            "instance may state a smaller ConnectedPinCount to place a five-terminal model as a " +
            "four-pin part.",
        ["VerilogA"] =
            "Set by the compiled model the File parameter names: one net per declared terminal, " +
            "which the module's own source states and no registry here can.",
    };

    /// <summary>
    /// A model of this type built from the fewest parameters that will construct one, at
    /// <paramref name="portCount"/> where the type has a port-count parameter.
    ///
    /// <para>Public because the gate writes its <c>.cnl</c> line from the same parameters: a test
    /// that invented its own minimal set would be comparing the catalogue against a second guess
    /// rather than against the reader.</para>
    /// </summary>
    public static bool TryMinimalModel(string token, int portCount, out ComponentModel? model)
    {
        try
        {
            model = ComponentModelFactory.TryCreate(token, MinimalParameters(token, portCount), null, 27.0);
            return model is not null;
        }
        catch
        {
            // A type that cannot be constructed without a file, a kit or a worker. Never silence:
            // it is either in _ruleOnly or the gate fails on it.
            model = null;
            return false;
        }
    }

    /// <summary>
    /// The fewest parameters a type needs before it will construct at all — a port count where one
    /// is read at construction, and otherwise the one or two values whose ABSENCE is a refusal.
    /// None of them can change the net count, which is what the three-point probe establishes.
    /// </summary>
    public static IReadOnlyDictionary<string, Expressions.Value> MinimalParameters(string token, int portCount)
    {
        var p = new Dictionary<string, Expressions.Value>(StringComparer.Ordinal);

        if (PortCountKey(token) is { } key) p[key] = new Expressions.Value((double)portCount);

        switch (token.ToUpperInvariant())
        {
            // A tuner with no termination at all is a refusal, and the fundamental's is the one it
            // asks for by name. 50 is the reference impedance, not a chosen value.
            case "TUNER":  p["Z[1]"] = new Expressions.Value(50.0); break;

            // The mutual element names two INDUCTORS rather than two nets, so these are the nets'
            // stand-ins: without them there is no element, and with them there are still no nets.
            case "MUTUAL":
                p["Inductor1"] = new Expressions.Value("L1");
                p["Inductor2"] = new Expressions.Value("L2");
                break;

            // The EMPTY design, base64 of "{}" — a Match with no network in it. A real one would be
            // a chosen circuit; this is the absence of one, and it still has the two ends every
            // matching network has.
            case "MATCH":  p["Design"] = new Expressions.Value(Convert.ToBase64String("{}"u8)); break;
        }
        return p;
    }

    /// <summary>
    /// The refusal text for a line whose net count is wrong. Names the type, the instance, what was
    /// written and what is needed — all four, because the caller of a headless verb has no line to
    /// look at and the count alone does not say which end is wrong.
    /// </summary>
    public static string Refusal(string typeName, string instanceName, int given, int expected)
    {
        int shortBy = expected - given;
        string remedy = shortBy > 0
            ? (shortBy == 1 ? "Add the missing net" : $"Add the {shortBy} missing nets") +
              " before the first 'name=value' on the line."
            : "Nets come before the first 'name=value' on the line; anything after one is read as a unit.";

        return $"{typeName}:{instanceName} is wired to {given} {(given == 1 ? "net" : "nets")}, " +
               $"and a {typeName} takes {expected}. {remedy}";
    }
}
