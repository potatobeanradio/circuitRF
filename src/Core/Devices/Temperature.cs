using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The one place temperature is defined for every component model — the nominal, the two physical
/// constants that go with it, and the conversions.
///
/// <para><b>Degrees Celsius is the unit at every boundary a user or a netlist touches</b>, and
/// kelvin is used only where the physics wants it (kT/q, and any interface specified in kelvin).
/// A model whose equations need kelvin converts at its own construction boundary — see
/// <see cref="DiodeModel"/>, whose constructor takes kelvin while its <c>Temp</c> parameter is
/// Celsius. Two components in one palette must never read the same parameter name in different
/// units, which is why the rule is stated once here rather than per model.</para>
///
/// <para><b>Why this is not on <c>FetModelBase</c>, where it started.</b> It is no longer FET
/// plumbing: <see cref="DiodeModel"/> already reaches for the same nominal, and anything evaluating
/// an externally-supplied compact model needs the same conversion at its own boundary. A FET base
/// class holding the definition means every later consumer depends on the FET family for a constant
/// that has nothing to do with FETs. <c>FetModelBase.NominalTemperatureC</c> is kept as a forwarding
/// alias so no existing model, factory call or test changes.</para>
/// </summary>
public static class Temperature
{
    /// <summary>Boltzmann's constant, J/K (SI 2019 exact).</summary>
    public const double Boltzmann = 1.380649e-23;

    /// <summary>Elementary charge, C (SI 2019 exact).</summary>
    public const double ElemCharge = 1.602176634e-19;

    /// <summary>Offset between the Celsius and Kelvin scales.</summary>
    public const double KelvinOffset = 273.15;

    /// <summary>
    /// Nominal (parameter-extraction) temperature, °C — the default a model is evaluated at when
    /// neither it nor the design states one.
    ///
    /// <para><b>26.85 °C is 300.00 K exactly</b>, and that is the reason for the odd-looking value
    /// rather than a round 27 °C. Verified as an exact IEEE-754 double sum, not assumed —
    /// <c>26.85 + 273.15 == 300.0</c> is bit-exact, which is what lets a device stating no
    /// temperature collapse every temperature relation to the identity with no residual drift.</para>
    ///
    /// <para><b>This is circuitRF's own default, NOT a claim about any model card.</b> A parameter
    /// set that was extracted at some other temperature says so in its own <c>Tnom</c>, and that
    /// value wins. Do not "correct" this constant to match a particular card.</para>
    /// </summary>
    public const double NominalC = 26.85;

    /// <summary>Nominal temperature in kelvin — exactly 300 K, by construction of <see cref="NominalC"/>.</summary>
    public const double NominalK = NominalC + KelvinOffset;

    /// <summary>
    /// Above this, a thermal resistance is not a thermal resistance. Real junction-to-ambient values
    /// run from well under 1 to a few hundred °C/W; a few thousand is already beyond anything
    /// physical. Four orders of magnitude of headroom above that is deliberate — this exists to
    /// catch a node left on a keep-alive leak resistor, which is typically 10^7 or more, not to
    /// second-guess an unusual but real design.
    ///
    /// <para>Lives here rather than in the engine because two layers need the same line: the
    /// elaborator, deciding whether a device's own thermal conductance is a real path back, and the
    /// engine, reporting a node that reaches its reference through no real path. Two copies of a
    /// judgement like this drift.</para>
    /// </summary>
    public const double ImplausibleThermalResistanceCPerW = 1e4;

    /// <summary>°C → K.</summary>
    public static double ToKelvin(double celsius) => celsius + KelvinOffset;

    /// <summary>K → °C.</summary>
    public static double ToCelsius(double kelvin) => kelvin - KelvinOffset;

    /// <summary>
    /// Device temperature minus its parameter-extraction temperature, in degrees — the argument
    /// every temperature relation is written in. Scale-free: the difference is the same number in
    /// Celsius or kelvin, which is exactly why the relations take a delta rather than two absolutes.
    /// </summary>
    public static double DeltaT(double tempC, double tnomC) => tempC - tnomC;

    /// <summary>
    /// Thermal voltage kT/q at an absolute temperature in KELVIN. Takes kelvin deliberately: a
    /// Celsius overload would be one call site away from silently computing kT/q at 300 degrees
    /// below where it was meant to, which produces a finite, plausible, wrong answer.
    /// </summary>
    public static double ThermalVoltage(double kelvin) => Boltzmann * kelvin / ElemCharge;

    // ── The junction relations, shared by every device that has a junction ────
    //
    //  These began inside FetModelBase, which needed them for its gate diode. DiodeModel needs the
    //  same three relations for the same physics, and a second copy would be a second set of answers
    //  to one question — the failure this file exists to prevent, and the reason ResolveDeviceC is
    //  here rather than per model. The FET family now calls these; its own tests are the proof the
    //  move changed no number.

    /// <summary>
    /// Bandgap at 0 K, eV, as the Varshni relation is written.
    ///
    /// <para><b>Not 1.11, which is the bandgap at room temperature</b> and is what several published
    /// parameter tables quote as their default. The two are not interchangeable: this constant is the
    /// <c>Eg(0)</c> that <see cref="BandgapAt"/> subtracts from, and feeding a room-temperature value
    /// into it shifts every junction relation by the difference. One value, used one way.</para>
    /// </summary>
    public const double SiliconBandgapEv = 1.16;

    /// <summary>
    /// Varshni bandgap narrowing, eV, at an absolute temperature in KELVIN.
    ///
    /// <para><b><c>Eg ≤ 0</c> means "the bandgap term is not modelled", and returns zero</b> — the
    /// same rule the diode's <c>Bv = 0</c> already follows. Without it there is no way to state a
    /// device whose saturation current does not move with temperature, because the narrowing term
    /// is non-zero even at <c>Eg(0) = 0</c> and would go on scaling the current on its own.</para>
    /// </summary>
    public static double BandgapAt(double kelvin, double bandgapAtZeroK = SiliconBandgapEv)
        => bandgapAtZeroK <= 0
            ? 0.0
            : bandgapAtZeroK - 7.02e-4 * kelvin * kelvin / (1108.0 + kelvin);

    /// <summary>
    /// Junction (built-in) potential at the device temperature, in volts — the standard relation:
    /// <c>Vj(T) = tr·Vj − 3·Vt(T)·ln(tr) − (tr·Eg(Tnom) − Eg(T))</c>, with <c>tr = T/Tnom</c>.
    /// Returns <paramref name="vj"/> unchanged when there is no temperature difference, so a device
    /// at its own extraction point is bit-identical to one with no temperature model at all.
    /// </summary>
    public static double JunctionPotentialAt(
        double vj, double tempC, double tnomC, double bandgapAtZeroK = SiliconBandgapEv)
    {
        if (vj <= 0 || tempC == tnomC) return vj;

        double tK1 = ToKelvin(tnomC), tK2 = ToKelvin(tempC);
        double tr  = tK2 / tK1;

        return tr * vj
             - 2.0 * ThermalVoltage(tK2) * System.Math.Log(tr * System.Math.Sqrt(tr))   // = 3·Vt·ln(tr)
             - (tr * BandgapAt(tK1, bandgapAtZeroK) - BandgapAt(tK2, bandgapAtZeroK));
    }

    /// <summary>
    /// Multiplier on a zero-bias depletion capacitance, given the junction potential before and
    /// after. The linear term is the usual 4e-4/°C expansion; the rest follows the junction
    /// potential, which is where nearly all of the movement is.
    /// </summary>
    public static double DepletionCapacitanceScale(double vj, double vjAtT, double gradingCoefficient, double dT)
        => vj <= 0 ? 1.0 : 1.0 + gradingCoefficient * (400e-6 * dT - (vjAtT - vj) / vj);

    /// <summary>
    /// Multiplier on a saturation current: <c>tr^(Xti/N) · exp(−q·Eg(Tnom)·(1 − tr) / (k·T))</c>.
    ///
    /// <para>This is the term that moves furthest with temperature by orders of magnitude, and it is
    /// exponential in the bandgap — so an <c>Eg</c> that means something slightly different from what
    /// this expects does not produce a slightly different current.</para>
    /// </summary>
    public static double SaturationCurrentScale(
        double tempC, double tnomC, double emissionCoefficient,
        double xti, double bandgapAtZeroK = SiliconBandgapEv)
    {
        if (tempC == tnomC) return 1.0;

        double tK1 = ToKelvin(tnomC), tK2 = ToKelvin(tempC);
        double tr  = tK2 / tK1;
        double n   = emissionCoefficient > 0 ? emissionCoefficient : 1.0;

        return System.Math.Pow(tr, xti / n)
             * System.Math.Exp(-ElemCharge * BandgapAt(tK1, bandgapAtZeroK) * (1.0 - tr) / (Boltzmann * tK2));
    }

    /// <summary>
    /// A plain polynomial temperature coefficient: <c>value·(1 + tc1·ΔT + tc2·ΔT²)</c>. This is how
    /// a resistor's and a capacitor's temperature dependence is stated, and it is a different shape
    /// from the junction relations above — a fitted polynomial rather than device physics.
    /// </summary>
    public static double PolynomialScale(double tc1, double tc2, double dT)
        => 1.0 + tc1 * dT + tc2 * dT * dT;

    // ── Where a device's own temperature comes from ───────────────────────────

    /// <summary>The global variable a design uses to state its ambient temperature, in °C.</summary>
    public const string AmbientGlobalName = "temp";

    /// <summary>The instance parameter naming an ABSOLUTE device temperature, °C.</summary>
    public const string AbsoluteParamName = "Temp";

    /// <summary>The instance parameter naming a device's RISE above ambient, in degrees.</summary>
    public const string DeltaParamName = "Dtemp";

    /// <summary>
    /// The one definition of what temperature a device is evaluated at, in °C. Every
    /// temperature-aware model resolves through here so two of them cannot answer differently.
    ///
    /// <list type="number">
    /// <item><c>Temp</c> given — that is the device's temperature, absolutely. It overrides ambient
    /// entirely, which is what makes it usable for a part held at a stated temperature.</item>
    /// <item>else <c>Dtemp</c> given — <c>ambient + Dtemp</c>. This is the form a hierarchical kit
    /// uses, because a rise above whatever the design is running at is the thing a subcircuit can
    /// meaningfully state about itself.</item>
    /// <item>else the ambient.</item>
    /// </list>
    ///
    /// <para><b>With no ambient stated the ambient IS <see cref="NominalC"/></b>, so a design that
    /// says nothing about temperature evaluates every device exactly at its extraction point and
    /// every relation collapses to the identity — the behaviour before any of this existed.</para>
    /// </summary>
    public static double ResolveDeviceC(IReadOnlyDictionary<string, Value> parameters, double ambientC)
    {
        if (TryReadReal(parameters, AbsoluteParamName, out double abs))   return abs;
        if (TryReadReal(parameters, DeltaParamName,    out double delta)) return ambientC + delta;
        return ambientC;
    }

    /// <summary>
    /// True when an instance states BOTH an absolute temperature and a rise above ambient. Rule 1
    /// above resolves it — <c>Temp</c> wins — but the two together cannot both be what the author
    /// meant, and silently discarding one of them is the kind of thing found months later. The
    /// caller reports; this stays pure so the rule and its detection sit in one file.
    /// </summary>
    public static bool HasContradictoryOverride(IReadOnlyDictionary<string, Value> parameters)
        => TryReadReal(parameters, AbsoluteParamName, out _)
        && TryReadReal(parameters, DeltaParamName,    out _);

    /// <summary>
    /// Reads a real-valued parameter, treating anything else as absent. A device's temperature must
    /// never come from a value that is not a number: a String parameter reaching here would
    /// otherwise throw from inside model construction, naming neither the instance nor the
    /// parameter.
    /// </summary>
    private static bool TryReadReal(IReadOnlyDictionary<string, Value> parameters, string name, out double value)
    {
        value = 0.0;
        if (!parameters.TryGetValue(name, out var v) || v.Kind != ValueKind.Real) return false;
        value = v.AsReal();
        return true;
    }
}
