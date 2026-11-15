using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>brief-pch-6 — the Propagation Channel's coded refusals (<c>channel.*</c>), each naming the parameter or the
/// instances to change.</summary>
public static class PropagationChannelDiagnostics
{
    private static Diagnostic E(string id, string template, params (string, object?)[] args)
        => Diagnostic.Create("channel." + id, DiagnosticSeverity.Error, template, args);

    public static Diagnostic NotANumber(string name) => E("not-a-number", "{name} must be a number.", ("name", name));
    public static Diagnostic NotAVector(string name, string text)
        => E("not-a-vector", "{name}={text} is not three numbers x,y,z (metres, or m/s for a velocity).", ("name", name), ("text", text));
    public static Diagnostic NotAnImpedance(string name) => E("not-an-impedance", "{name} must be an impedance in ohms.", ("name", name));
    public static Diagnostic PatternPort(string name, string text)
        => E("pattern-port", "{name}={text}: the port after # is a number from 1.", ("name", name), ("text", text));
    public static Diagnostic UnknownModel(string model, string models)
        => E("unknown-model", "PathLoss={model} is not a propagation model; the models are {models}.", ("model", model), ("models", models));
    public static Diagnostic F0NotPositive() => E("f0", "f0 must be a positive frequency.");
    public static Diagnostic PositionsTogether() => E("positions", "TxPos and RxPos are stated together, or neither (Distance and heights).");
    public static Diagnostic ImpedanceNotPositive() => E("impedance", "TxZ and RxZ must have a positive resistance.");
    public static Diagnostic AntennaTemperature() => E("antenna-temperature", "RxTant is an antenna temperature in kelvin, 0 or more.");
    public static Diagnostic DopplerComp(string word) => E("doppler-comp", "DopplerComp={word} is none or common.", ("word", word));
    public static Diagnostic RayListNeedsFile() => E("ray-list-file", "Multipath=file reads its rays from File=, a ray list (delay, power, phase, doppler).");
    public static Diagnostic Multipath(string word)
        => E("multipath", "Multipath={word} is none (the rays of the geometry) or file (a ray list).", ("word", word));
    public static Diagnostic ModelValue(string name) => E("model-value", "{name} must be a number or a word.", ("name", name));
    public static Diagnostic RainDrawn()
        => E("rain-drawn", "Rain=drawn draws a rain attenuation per drop, and this channel draws nothing; Rain=fixed is the attenuation at the availability.");
    public static Diagnostic NoPlacement() => E("placement", "Distance (metres) or TxPos and RxPos place the two ends.");
    public static Diagnostic NoPatternReader()
        => E("no-pattern-reader", "TxPattern and RxPattern read an EM result, and this process has no reader for one.");
    public static Diagnostic Pattern(string end, string message) => E("pattern", "{end}Pattern: {message}", ("end", end), ("message", message));
    public static Diagnostic ReverseOfReverse(string instance, string name)
        => E("reverse-of-reverse", "'{instance}': Reverse={name} must name another channel, one that is not itself a reverse.",
             ("instance", instance), ("name", name));
    public static Diagnostic ReverseUnknown(string instance, string name, string channels)
        => E("reverse-unknown", "'{instance}': Reverse={name} names no Propagation Channel; the channels are {channels}.",
             ("instance", instance), ("name", name), ("channels", channels));

    public static Diagnostic MetreIsMilli(string instance, string name, string expression)
        => E("metre-is-milli", "'{instance}': {name}={expression} m would be {expression} millimetres: m is milli in every value. Write a bare number "
                             + "of metres ({name}={expression}) or {name}={expression} metre.", ("instance", instance), ("name", name), ("expression", expression));

    public static Diagnostic PatternPortMissing(string record, int ports, int port)
        => E("pattern-port-missing", "'{record}' has {ports} port(s); #{port} is not one of them.", ("record", record), ("ports", ports), ("port", port));
    public static Diagnostic RecordingTooShort(string instance, long sample)
        => E("recording-short", "'{instance}': the recorded transmit wave does not reach record sample {sample}.", ("instance", instance), ("sample", sample));
    public static Diagnostic RecordingProducts(string instance, int recorded, int stepped)
        => E("recording-products", "'{instance}': the recorded transmit wave has {recorded} products; this run steps {stepped}.",
             ("instance", instance), ("recorded", recorded), ("stepped", stepped));

    /// <summary>A cycle of channels in an envelope run: each receive side leads back to a transmit side of the set.</summary>
    public static Diagnostic Cycle(IReadOnlyList<string> instances)
        => E("cycle", "Propagation Channels {list} form a cycle: each one's receive side reaches the next one's transmit side, and the last " +
                      "reaches the first's. The channel is one way; a link that goes back is a separate instance on a separate circuit side.",
             ("instances", instances), ("list", string.Join(", ", instances.Select(i => $"'{i}'"))));

    /// <summary>A receive pin two channels drive.</summary>
    public static Diagnostic SharedReceivePin(string net, IReadOnlyList<string> channels)
        => E("shared-receive-pin", "The receive net '{net}' is driven by {list}: a pin receive end takes one Propagation Channel.",
             ("net", net), ("channels", channels), ("list", string.Join(" and ", channels.Select(i => $"'{i}'"))));
}
