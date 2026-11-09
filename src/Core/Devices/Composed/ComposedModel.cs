using CircuitRF.Core.Devices.Composed.Traps;
using RfCore.Data;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The contents of a <c>.cmodel</c> — a composed nonlinear FET model, the file a
/// <c>ComposedFET</c> instance names (brief-modelcomposer-1 R-mc1-1; <c>docs/design/model-composer.md</c>).
///
/// <para>The container is a ZIP holding <c>model.json</c> (this record, minus <see cref="Arrays"/>) and
/// one <c>arrays/&lt;name&gt;.npy</c> per entry of <see cref="Arrays"/>. <see cref="CmodelFile"/> reads,
/// validates and writes it; nothing else touches the bytes.</para>
/// </summary>
public sealed record ComposedModel
{
    /// <summary>The newest <see cref="FormatVersion"/> this build reads and the one it writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The only <see cref="Kind"/> there is so far: a three-terminal FET.</summary>
    public const string FetKind = "Fet";

    public int    FormatVersion { get; init; } = CurrentFormatVersion;
    public string Kind          { get; init; } = FetKind;

    /// <summary>Always <c>G D S</c> for a FET: pins 1, 2 and 3 of the symbol, in that order.</summary>
    public IReadOnlyList<string> Terminals { get; init; } = ["G", "D", "S"];

    /// <summary>The periphery of the measured device, which <c>N</c> scales from (D9).</summary>
    public required BasePeriphery BasePeriphery { get; init; }

    public required ModelTemperature Temperature { get; init; }

    /// <summary>Self-heating (MC-9): the thermal network on the core's thermal node. Null: no thermal node. Only a
    /// <see cref="TemperatureMode.Range"/> model can carry one.</summary>
    public ModelThermal? Thermal { get; init; }

    /// <summary>Traps (MC-10): the trap model and its enabled states, each an internal node of the core whose voltage is the
    /// state. Null: no traps — never a block naming <see cref="ModelTraps.None"/>, which the reader reads as null and the
    /// writer never writes.</summary>
    public ModelTraps? Traps { get; init; }

    public required ExtrinsicNetwork Network { get; init; }

    public required CoreConstituents Core { get; init; }

    /// <summary>Guided extrapolation's settings (R-mc2-3): present when a current constituent is a
    /// table or an ANN, absent for an all-equation model.</summary>
    public ModelExtrapolation? Extrapolation { get; init; }

    /// <summary>A library symbol name, or a <c>.csym</c> path relative to the <c>.cmodel</c>. Null =
    /// the standard three-terminal FET symbol.</summary>
    public string? Symbol { get; init; }

    public ModelProvenance Provenance { get; init; } = new();

    /// <summary>The <c>arrays/*.npy</c> entries, by name. Tables and network weights (MC-2); empty for
    /// an all-equation model.</summary>
    public IReadOnlyDictionary<string, DataCube> Arrays { get; init; } = new Dictionary<string, DataCube>();
}

/// <summary>The measured device's finger count <c>N0</c> and unit gate width <c>W0</c>, the latter as
/// written (<c>"125 um"</c>).</summary>
public sealed record BasePeriphery(int Fingers, string UnitWidth);

public enum TemperatureMode { Single, Range }

/// <summary>
/// The temperature a model is valid at (D8). <see cref="TemperatureMode.Single"/>: one temperature
/// <see cref="T0"/>, and no temperature input. <see cref="TemperatureMode.Range"/>: every constituent
/// takes <c>T</c>, clamped to [<see cref="Tmin"/>, <see cref="Tmax"/>]. All in °C.
/// </summary>
public sealed record ModelTemperature(TemperatureMode Mode, double T0, double Tmin, double Tmax)
{
    public static ModelTemperature Single(double t0)               => new(TemperatureMode.Single, t0, t0, t0);
    public static ModelTemperature Range(double tmin, double tmax) => new(TemperatureMode.Range, double.NaN, tmin, tmax);
}

/// <summary>
/// <c>Thermal</c> (brief-modelcomposer-9 R-mc9-2): the thermal network from the core's thermal node — whose voltage is
/// the temperature rise ΔT in kelvin, and into which the core injects its dissipated power — to thermal ground, at the
/// base periphery. One pole is [P1] Fig. 6.32's <c>Rth ∥ Cth</c>; more is a Foster ladder, a chain of parallel RC
/// sections whose impedance is <c>Σ Ri/(1 + jω·Ri·Ci)</c>. The synthesised cell holds them as ordinary R and C, so
/// <c>Rth</c> scales by <c>N0/N</c> and <c>Cth</c> by <c>N/N0</c> (D9). With <see cref="RthSlope"/> the thermal
/// resistance varies linearly with the ambient ([R11] §2.8): every R is scaled by <c>RthAt(temp)/Rth</c>.
/// </summary>
public sealed record ModelThermal(IReadOnlyList<ThermalPole> Poles)
{
    /// <summary>The most poles a ladder may have.</summary>
    public const int MaxPoles = 5;

    /// <summary>The change of the total <see cref="Rth"/> per kelvin of ambient, K/W per K; 0 = constant.</summary>
    public double RthSlope { get; init; }

    /// <summary>The ambient at which the poles' resistances hold, °C.</summary>
    public double Tref { get; init; } = 25;

    /// <summary>The DC thermal resistance, K/W: the sum of the poles'.</summary>
    public double Rth => Poles.Sum(p => p.Rth);

    /// <summary>The DC thermal resistance at an ambient of <paramref name="ambientC"/>, K/W.</summary>
    public double RthAt(double ambientC) => Rth + RthSlope * (ambientC - Tref);

    /// <summary>One pole: [P1] Fig. 6.32.</summary>
    public static ModelThermal Single(double rth, double cth) => new([new ThermalPole(rth, cth)]);
}

/// <summary>One parallel RC section of the thermal network: K/W and J/K, at the base periphery.</summary>
public sealed record ThermalPole(double Rth, double Cth);

/// <summary>
/// The extrinsic network (D5): <c>.cnl</c> text holding one <c>define</c> block with exactly six
/// pins — extrinsic gate, drain, source, then intrinsic gate, drain, source — and its variables'
/// values; plus the scaling rule of each variable that is not <c>fixed</c> (D9).
/// </summary>
public sealed record ExtrinsicNetwork(string Netlist, IReadOnlyList<ScalingRule> Scaling);

/// <summary>One network variable's periphery rule, by name (<see cref="PeripheryScaling"/>).</summary>
public sealed record ScalingRule(string Variable, string Rule);

/// <summary>The four constituents of D4, and the optional dispersive <c>IdRF</c>.</summary>
public sealed record CoreConstituents(
    Constituent Id, Constituent Ig, Constituent Qg, Constituent Qd, Constituent? IdRF = null)
{
    /// <summary>The drain–source symmetry option (R-mc2-4). Off by default: a field-plated power
    /// device is deliberately asymmetric ([P1] §6.5.5).</summary>
    public DrainSourceSymmetry Symmetry { get; init; } = DrainSourceSymmetry.None;

    /// <summary>The DC/RF dispersion of [P1] eq. 6.52 (R-mc7-4): present exactly when <see cref="IdRF"/>
    /// is, and then <see cref="Id"/> is the DC current <c>IdDC</c>.</summary>
    public ModelDispersion? Dispersion { get; init; }

    /// <summary>The core's noise (brief NZ-4), <c>Core.Noise</c>. Null: no block, and the core is named as noiseless.</summary>
    public ModelNoise? Noise { get; init; }
}

/// <summary>
/// <c>Core.Noise</c> (brief NZ-4): the two-temperature FET noise of the core. <see cref="Td"/>, the drain temperature, puts
/// <c>4k·Td·gds</c> across the intrinsic drain–source; <see cref="Tg"/>, the gate temperature, is the temperature the
/// extrinsic network's element named <c>Ri</c> is thermal at; <see cref="Kf"/>/<see cref="Af"/> a flicker current
/// <c>Kf·|Id|^Af / f</c> on the drain. Temperatures in kelvin, each a constant or a constituent evaluated at the core's own
/// <c>vgs</c>, <c>vds</c> and temperature. Absent ones are off.
/// </summary>
public sealed record ModelNoise(NoiseValue? Tg = null, NoiseValue? Td = null, double Kf = 0.0, double Af = 1.0)
{
    /// <summary>The element of the extrinsic network <see cref="Tg"/> heats.</summary>
    public const string GateResistorName = "Ri";
}

/// <summary>A <c>Core.Noise</c> temperature: a constant (a bare number in the file) or a constituent (an equation or a
/// table, written as the core's currents are). Exactly one is set.</summary>
public sealed record NoiseValue(double? Constant, Constituent? Function)
{
    public static NoiseValue Of(double kelvin) => new(kelvin, null);
    public static NoiseValue Of(Constituent function) => new(null, function);
}

/// <summary>
/// <c>Core.Dispersion</c>: the time constant τ, seconds, of [P1] eq. 6.52's one-pole transition from the
/// DC current <c>IdDC</c> (below 1/τ) to the RF current <c>IdRF</c> (above it). Unscaled by periphery.
/// </summary>
public sealed record ModelDispersion(double Tau);

/// <summary>
/// <see cref="Exchange"/>: the constituents are defined for <c>vds ≥ 0</c>, and a point with
/// <c>vds &lt; 0</c> is evaluated at its image <c>(vgs − vds, −vds)</c> with drain and source exchanged
/// ([P1] §6.5, eqs. 6.61–6.64).
/// </summary>
public enum DrainSourceSymmetry { None, Exchange }

/// <summary>
/// <c>Extrapolation</c>: the added conductance <c>G_x</c> of each current payload that is a table or
/// an ANN, by payload — <c>Id</c>, <c>IgGs</c>, <c>IgGd</c> (and <c>IdRF</c>). In siemens, at the
/// base periphery. <see cref="Mode"/> is the law it is added by (<c>Extrapolation.Mode</c>; absent is
/// <see cref="ExtrapolationMode.Global"/>).
/// </summary>
public sealed record ModelExtrapolation(IReadOnlyDictionary<string, double> Gx)
{
    /// <summary>The order the writer puts them in.</summary>
    public static readonly IReadOnlyList<string> Keys = ["Id", "IgGs", "IgGd", "IdRF"];

    public ExtrapolationMode Mode { get; init; } = ExtrapolationMode.Global;

    /// <summary>The local law (brief-modelcomposer-29), each <c>G_x</c> its cap: what the composer exports.</summary>
    public static ModelExtrapolation Local(IReadOnlyDictionary<string, double> gx) => new(gx) { Mode = ExtrapolationMode.Local };
}

/// <summary>
/// The law guided extrapolation adds its conductance by (<see cref="GuidedExtrapolation"/>). <see cref="Global"/>: <c>G_x</c>
/// at every edge (MC-2). <see cref="Local"/>: ten times the constituent's own-branch conductance at the edge it leaves, with
/// a floor, capped by <c>G_x</c> (brief-modelcomposer-29).
/// </summary>
public enum ExtrapolationMode { Global, Local }

/// <summary>
/// One constituent: its method, the inputs it may read, and the method's payload, held in
/// <see cref="Expressions"/> by key — one entry for <c>Id</c>/<c>Qg</c>/<c>Qd</c>/<c>IdRF</c>, keyed by the
/// method (<see cref="PayloadKey"/>); two, <c>IgGs</c> and <c>IgGd</c>, for <c>Ig</c>.
/// <list type="bullet">
/// <item><c>Equation</c>: each entry is an expression; <see cref="Parameters"/> are the named
/// constants it reads.</item>
/// <item><c>Table</c>: each entry names arrays <c>&lt;name&gt;.values</c> and
/// <c>&lt;name&gt;.coefficients</c>; <see cref="Smoothing"/> is the λ the coefficients were fitted with.</item>
/// <item><c>ANN</c>: each entry names a network's arrays (<see cref="ComposedArrays.Ann"/>);
/// <see cref="Activation"/> is its hidden layers' activation.</item>
/// </list>
/// A table or ANN carries its training <see cref="Domain"/>; an equation is global and has none.
/// </summary>
public sealed record Constituent
{
    public const string EquationMethod = "Equation";
    public const string TableMethod    = "Table";
    public const string AnnMethod      = "ANN";
    /// <summary>A charge by [I11]'s drift decomposition (MC-8, <see cref="DriftCharges"/>): <c>Qg</c> and <c>Qd</c>
    /// together, their payloads the pieces' networks.</summary>
    public const string DriftMethod    = "Drift";

    public required string Method { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Expressions { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, double>> Parameters  { get; init; } = [];

    /// <summary>Where a table or ANN was trained (R-mc2-3). Null for an equation.</summary>
    public TrainingDomain? Domain { get; init; }

    /// <summary>A table's smoothing weight λ; 0 is interpolation (R-mc2-1).</summary>
    public double? Smoothing { get; init; }

    /// <summary>An ANN's hidden-layer activation (R-mc2-2).</summary>
    public AnnActivation? Activation { get; init; }

    /// <summary>A table or ANN current's output transform (MC-7): the payload holds <c>u</c> and the current is
    /// <c>Scale·sinh(u)</c>. Null: the payload is the current itself.</summary>
    public OutputTransform? Transform { get; init; }

    /// <summary>The single-payload key of this method: <c>Expression</c>, <c>Table</c> or <c>Network</c>.</summary>
    public string PayloadKey => PayloadKeyOf(Method);

    public static string PayloadKeyOf(string method) => method switch
    {
        TableMethod => "Table",
        AnnMethod   => "Network",
        _           => "Expression",
    };

    /// <summary>One equation by its payload key, or null.</summary>
    public string? Expression(string key)
        => Expressions.FirstOrDefault(e => e.Key == key) is { Key: not null } kv ? kv.Value : null;

    public static Constituent Equation(string expression, IReadOnlyList<string> inputs,
                                       params (string Name, double Value)[] parameters)
        => new()
        {
            Method      = EquationMethod,
            Inputs      = inputs,
            Expressions = [new("Expression", expression)],
            Parameters  = [.. parameters.Select(p => new KeyValuePair<string, double>(p.Name, p.Value))],
        };

    /// <summary>A table constituent naming the arrays under <paramref name="name"/>.</summary>
    public static Constituent Table(string name, IReadOnlyList<string> inputs, TrainingDomain domain,
                                    double smoothing = 0.0)
        => new() { Method = TableMethod, Inputs = inputs, Expressions = [new("Table", name)],
                   Domain = domain, Smoothing = smoothing };

    /// <summary>An ANN constituent naming the arrays under <paramref name="name"/>.</summary>
    public static Constituent Ann(string name, IReadOnlyList<string> inputs, TrainingDomain domain,
                                  AnnActivation activation = AnnActivation.Tanh)
        => new() { Method = AnnMethod, Inputs = inputs, Expressions = [new("Network", name)],
                   Domain = domain, Activation = activation };

    /// <summary>One drift charge (<see cref="DriftCharges"/>): <c>Qg</c> names QGS, QGD and Qdrift, <c>Qd</c> names QGD,
    /// Qdrift and QDS and carries <c>Lambda</c>. Each piece's network is named after its key.</summary>
    public static Constituent Drift(bool gate, TrainingDomain domain, double lambda = 0.0)
        => new()
        {
            Method      = DriftMethod,
            Inputs      = [ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds],
            Expressions = [.. (gate ? DriftCharges.QgKeys : DriftCharges.QdKeys).Select(k => new KeyValuePair<string, string>(k, k))],
            Parameters  = gate ? [] : [new(DriftCharges.Lambda, lambda)],
            Domain      = domain,
            Activation  = AnnActivation.Tanh,
        };

    /// <summary>A gate-conduction pair by table or ANN: the two branches' array names.</summary>
    public static Constituent Gate(string method, string igGs, string igGd, IReadOnlyList<string> inputs,
                                   TrainingDomain domain)
        => new()
        {
            Method = method, Inputs = inputs, Expressions = [new("IgGs", igGs), new("IgGd", igGd)],
            Domain = domain,
            Smoothing  = method == TableMethod ? 0.0 : null,
            Activation = method == AnnMethod ? AnnActivation.Tanh : null,
        };

    /// <summary>A gate-conduction equation pair: the gate–source and gate–drain branch currents.</summary>
    public static Constituent GateEquations(string igGs, string igGd, IReadOnlyList<string> inputs,
                                            params (string Name, double Value)[] parameters)
        => new()
        {
            Method      = EquationMethod,
            Inputs      = inputs,
            Expressions = [new("IgGs", igGs), new("IgGd", igGd)],
            Parameters  = [.. parameters.Select(p => new KeyValuePair<string, double>(p.Name, p.Value))],
        };
}

/// <summary>
/// <c>Transform</c> on a table or ANN current (brief-modelcomposer-7): the payload computes <c>u</c>, and the current is
/// <c>Scale·sinh(u)</c> — linear through zero within ±<c>Scale</c> and logarithmic beyond, so a payload trained in
/// <c>u</c> fits a gate current spanning many decades to an equal RATIO everywhere above <c>Scale</c>, which a linear
/// output weighted by <c>1/|I|²</c> cannot ([P1]'s log-magnitude fit of conduction currents). <c>Scale</c> in amps.
/// </summary>
public sealed record OutputTransform(string Kind, double Scale)
{
    public const string Asinh = "Asinh";
}

/// <summary>Where a model came from. Written as given — the writer stamps nothing itself, which is
/// what makes the same model write the same bytes.</summary>
public sealed record ModelProvenance
{
    public string Created          { get; init; } = "";
    public string CircuitRfVersion { get; init; } = "";
    public IReadOnlyList<ModelSource> Sources { get; init; } = [];
    public string SettingsDigest   { get; init; } = "";
    /// <summary>What the charges were built from when a model's constituents do not show it — <c>Qg, Qd: Linear from 1
    /// bias point(s)</c> (brief-modelcomposer-17) — so a model whose charges are constant capacitances is never taken for a
    /// nonlinear one. Empty, and not written, otherwise.</summary>
    public string Charges          { get; init; } = "";
}

public sealed record ModelSource(string Path, string Sha256);
