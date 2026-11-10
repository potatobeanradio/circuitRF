using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Engine.Envelope;

/// <summary>How an envelope run places a CW tone source, a P1Tone, among its fundamentals (brief NZ-11), as coded
/// diagnostics: a tone it cannot carry is refused or warned about, never run driving nothing.</summary>
public static class EnvelopeToneDiagnostics
{
    private static string Mhz(double hz) => (hz / 1e6).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A P1Tone inside a carrier's band but off its frequency: as a fundamental of its own its envelope would
    /// overlap the carrier's.</summary>
    public static Diagnostic InsideCarrier(string tone, double toneHz, double carrierHz) => Diagnostic.Create(
        "envelope.tone.inside-carrier", DiagnosticSeverity.Error,
        "envelope: P1Tone '{tone}' at {toneMhz} MHz is {apartMhz} MHz from the carrier at {carrierMhz} MHz, inside its band and " +
        "not on its frequency, so an envelope cannot carry it as a tone of its own. Put the tone on the carrier's frequency, or " +
        "more than three bandwidths from it.",
        ("tone", tone), ("toneMhz", Mhz(toneHz)), ("apartMhz", (Math.Abs(toneHz - carrierHz) / 1e6).ToString("0.###", CultureInfo.InvariantCulture)),
        ("carrierMhz", Mhz(carrierHz)));

    /// <summary>The carriers' groups and the P1Tones off their frequencies need more fundamentals than the engine has.</summary>
    public static Diagnostic TooManyFundamentals(int groups, IEnumerable<(string Name, double Hz)> tones, int needed) => Diagnostic.Create(
        "envelope.tone.too-many-fundamentals", DiagnosticSeverity.Error,
        "envelope: {groups} carrier group(s) and the P1Tone(s) {tones} would need {needed} fundamentals; up to 2 are supported. " +
        "Put a tone on a product of the others (a harmonic of the carrier, or a sum or difference of two fundamentals).",
        ("groups", groups), ("tones", string.Join(", ", tones.Select(t => $"'{t.Name}' at {Mhz(t.Hz)} MHz"))), ("needed", needed));

    /// <summary>Envelope=single and a P1Tone on no harmonic of the one envelope: it drives nothing.</summary>
    public static Diagnostic OffSingleEnvelope(string analysis, string tone, double toneHz, double f0Hz) => Diagnostic.Create(
        "envelope.tone.off-single-envelope", DiagnosticSeverity.Warning,
        "Envelope '{analysis}': P1Tone '{tone}' at {toneMhz} MHz is on no harmonic of the one envelope (Envelope=single, " +
        "{f0Mhz} MHz), so it drives nothing. Leave Envelope auto to give it a fundamental of its own.",
        ("analysis", analysis), ("tone", tone), ("toneMhz", Mhz(toneHz)), ("f0Mhz", Mhz(f0Hz)));

    /// <summary>The key the round-off-held tolerance is reported once under.</summary>
    public const string RoundOffFloorKey = "envelope-tol-roundoff";

    /// <summary>A signal so small beside the circuit's largest current (a bias supply through an ideal choke) that round-off
    /// holds the Newton tolerance above 1 % of it.</summary>
    public static Diagnostic RoundOffFloor(double tolA, double excitationA) => Diagnostic.Create(
        "envelope.tol.roundoff", DiagnosticSeverity.Warning,
        "envelope: the signal's current ({signal} A) is so small beside the circuit's bias currents that round-off holds the " +
        "solve's tolerance at {tol} A, {pct} % of it: EVM, ACLR and distortion are read no better than that. A series " +
        "resistance on an ideal bias choke (R=) lifts the floor.",
        ("signal", excitationA.ToString("G3", CultureInfo.InvariantCulture)), ("tol", tolA.ToString("G3", CultureInfo.InvariantCulture)),
        ("pct", (100 * tolA / excitationA).ToString("0.#", CultureInfo.InvariantCulture)));
}
