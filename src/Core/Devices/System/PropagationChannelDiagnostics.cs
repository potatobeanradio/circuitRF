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
        => E("multipath", "Multipath={word} is none (the rays of the geometry), tdl (a tapped delay line, Profile=), file (a ray list or "
                          + "power-delay profile), cdl (a clustered delay line, Profile=), rays (a directional ray list, File=) or scenario (a "
                          + "TR 38.901 drop, PathLoss=38901).", ("word", word));

    // brief-pch-7 — fading and tapped delay lines
    public static Diagnostic Fading(string word) => E("fading", "Fading={word} is rayleigh, rician or nakagami.", ("word", word));
    public static Diagnostic NakagamiM(double m) => E("nakagami-m", "NakagamiM={m} must be at least 0.5.", ("m", m));
    public static Diagnostic DopplerSpectrum(string word)
        => E("doppler-spectrum", "Doppler={word} is classical, flat, gaussian or los.", ("word", word));
    public static Diagnostic DopplerNegative() => E("doppler-negative", "Velocity and fD must not be negative.");
    public static Diagnostic Sinusoids(double n) => E("sinusoids", "Sinusoids={n} must be a whole number, 4 or more.", ("n", n));
    public static Diagnostic ProfileNeedsTdl(string profile)
        => E("profile-needs-tdl", "Profile={profile} names a tapped delay line, which Multipath=tdl uses; a terrain profile is Terrain=.",
             ("profile", profile));
    public static Diagnostic TdlNeedsProfile(string profiles)
        => E("tdl-profile", "Multipath=tdl takes Profile=, one of {profiles}.", ("profiles", profiles));
    public static Diagnostic UnknownProfile(string profile, string profiles)
        => E("unknown-profile", "Profile={profile} is not a tapped delay line; the profiles are {profiles}.", ("profile", profile), ("profiles", profiles));
    public static Diagnostic RicianNeedsLosProfile(string profile)
        => E("rician-profile", "Fading=rician: {profile} has no line-of-sight tap (TDL-D, TDL-E, NTN-TDL-C and NTN-TDL-D have one).",
             ("profile", profile));
    // brief-pch-8 — instance ends, polarization, clustered delay lines and ray lists
    public static Diagnostic InstanceEndOwns(string end, string instance, string name)
        => E("instance-end-owns", "{end}={instance} makes that end the instance's own antennas, so {name} does not apply to it.",
             ("end", end), ("instance", instance), ("name", name));
    public static Diagnostic ReverseWithInstance()
        => E("reverse-instance", "Reverse= transposes a pin-to-pin realization; a channel with an instance end is not one. A reverse link to an array is a second channel.");
    public static Diagnostic Polarization(string name, string word)
        => E("polarization", "{name}={word} is theta, phi, rhcp, lhcp or an angle in degrees.", ("name", name), ("word", word));
    public static Diagnostic EndNotBound(string end, string instance)
        => E("end-not-bound", "{end}={instance} was not bound to an instance before the run.", ("end", end), ("instance", instance));
    public static Diagnostic EndUnknown(string channel, string end, string instance, string arrays)
        => E("end-unknown", "'{channel}': {end}={instance} names no Phased Antenna Array or EM Field Data block; the arrays are {arrays}.",
             ("channel", channel), ("end", end), ("instance", instance), ("arrays", arrays));
    public static Diagnostic EndFieldData(string channel, string end, string instance)
        => E("end-field-data", "'{channel}': {end}={instance} is an EM Field Data block. A channel end on one is not built yet; a Phased Antenna Array "
                               + "on the same record (Rows=1 Cols=1, or its own lattice) is.", ("channel", channel), ("end", end), ("instance", instance));
    public static Diagnostic FieldDataInEnvelope(string channel)
        => E("field-data-envelope", "'{channel}': an EM Field Data block as a channel end is coupled in S-parameter and harmonic-balance runs, not yet in an "
                                    + "envelope run. A Phased Antenna Array on the same record is an end the envelope steps.", ("channel", channel));

    public static Diagnostic RaysNeedFile() => E("rays-file", "Multipath=rays reads its rays from File=, a ray list with departure and arrival angles.");
    public static Diagnostic CdlNeedsProfile(string profiles)
        => E("cdl-profile", "Multipath=cdl takes Profile=, one of {profiles}.", ("profiles", profiles));
    public static Diagnostic CdlNeedsSpread()
        => E("cdl-spread", "Multipath=cdl scales its normalized delays to DelaySpread, which it needs (TR 38.901 Table 7.7.3-1 gives 10 to 1000 ns).");
    public static Diagnostic RayFrame(string word) => E("ray-frame", "RayFrame={word} is global or ends.", ("word", word));
    public static Diagnostic ClusterDopplerFromVelocity()
        => E("cluster-doppler", "Velocity and fD set a fading tap's Doppler; the rays of a clustered delay line or a ray list take theirs from "
                                + "TxVel and RxVel and their own directions.");

    public static Diagnostic FadingWithDirections()
        => E("fading-directions", "Fading= makes a tap without directions fade; the rays of a clustered delay line or a ray list fade by their own "
                                  + "phases and Dopplers beating, and take none.");

    public static Diagnostic KFactorNeedsRician()
        => E("kfactor", "KFactor is a Rician tap's line-of-sight to diffuse power ratio, dB: it goes with Fading=rician, a line-of-sight "
                        + "profile or a file's k column, and Fading=rician needs it.");
    public static Diagnostic ModelValue(string name) => E("model-value", "{name} must be a number or a word.", ("name", name));
    // brief-pch-10 — what a drop draws
    public static Diagnostic Shadowing(string word) => E("shadowing", "Shadowing={word} is on (a draw of the model's σ per drop) or off.", ("word", word));
    public static Diagnostic ScenarioNeeds38901(string model)
        => E("scenario-model", "Multipath=scenario draws a TR 38.901 drop for the model's scenario, so it takes PathLoss=38901; this channel's is {model}.",
             ("model", model));
    public static Diagnostic ScenarioOwns(string key)
        => E("scenario-owns", "Multipath=scenario draws its own spreads, K, Dopplers and fading from the scenario's statistics; {key} does not apply to it.",
             ("key", key));
    public static Diagnostic States(string word) => E("states", "States={word} is off or p681 (ITU-R P.681's land mobile-satellite states).", ("word", word));
    public static Diagnostic StatesNeedSatellite(string model)
        => E("states-model", "States=p681 is the land mobile-satellite state model, on a satellite path: it takes PathLoss=satellite; this channel's is {model}.",
             ("model", model));
    public static Diagnostic StatesEnvironment(string word, string words)
        => E("states-environment", "States=p681 takes StateEnvironment=, one of {words}{word}.", ("word", word), ("words", words));
    public static Diagnostic StatesOwns(string key)
        => E("states-owns", "States=p681 is the link's fading and shadowing (its states' Loo statistics); {key} does not apply with it.", ("key", key));
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

    // brief-pch-9 — picking the beam
    public static Diagnostic BeamSelect(string word)
        => E("beam-select", "BeamSelect={word} is auto (the strongest pair), rank (BeamRank=), index (TxBeam=, RxBeam=) or manual (the arrays' own weights).",
             ("word", word));
    public static Diagnostic BeamRank(double n) => E("beam-rank", "BeamRank={n} is a whole number from 1, the strongest distinct pair.", ("n", n));
    public static Diagnostic BeamKeyNeedsMode(string key, string mode)
        => E("beam-key-mode", "{key} is read only with BeamSelect={mode}.", ("key", key), ("mode", mode));
    public static Diagnostic BeamWithoutArray(string word)
        => E("beam-no-array", "BeamSelect={word} picks a beam on a Phased Antenna Array end, and both ends of this channel are pins.", ("word", word));
    public static Diagnostic BeamSearch(string word) => E("beam-search", "BeamSearch={word} is exhaustive or alternating.", ("word", word));
    public static Diagnostic ScanLimit(double deg)
        => E("scan-limit", "ScanLimit={deg} is an angle from boresight above 0 and at most 90 degrees.", ("deg", deg));
    public static Diagnostic BeamChoice(string key, string text)
        => E("beam-choice", "{key}={text} is a codebook entry (a whole number from 1) or a direction (θ, φ) in degrees.", ("key", key), ("text", text));
    public static Diagnostic Codebook(string path, string why) => E("codebook", "Codebook '{path}': {why}", ("path", path), ("why", why));
    // brief-pch-9 — the selection's refusals at run start (src/Engine BeamSelection)
    public static Diagnostic BeamEndNotFeed(string channel, string mode, string instance, string kind)
        => E("beam-end-not-feed", "Propagation Channel '{channel}': BeamSelect={mode} steers a Feed-mode Phased Antenna Array end; '{instance}' is {kind}, " +
                                  "with no beamformer to weigh. BeamSelect=manual uses the end as it is.",
             ("channel", channel), ("mode", mode), ("instance", instance), ("kind", kind));
    public static Diagnostic BeamCannotSteer(string channel, string mode, string instance, string why)
        => E("beam-cannot-steer", "Propagation Channel '{channel}': BeamSelect={mode} cannot steer '{instance}'. {why}",
             ("channel", channel), ("mode", mode), ("instance", instance), ("why", why));
    public static Diagnostic CodebookNoEnd(string channel, string path, string end, string instance)
        => E("codebook-no-end", "Propagation Channel '{channel}': Codebook '{path}' lists no direction for the {end} end ('{instance}').",
             ("channel", channel), ("path", path), ("end", end), ("instance", instance));
    public static Diagnostic BeamIndexOnFixedEnd(string channel, string key, string choice, string end, string what)
        => E("beam-index-fixed-end", "Propagation Channel '{channel}': {key}={choice} names a beam of the {end} end, which {what}.",
             ("channel", channel), ("key", key), ("choice", choice), ("end", end), ("what", what));
    public static Diagnostic BeamIndexNeeded(string channel, string key, int count, string instance)
        => E("beam-index-needed", "Propagation Channel '{channel}': BeamSelect=index needs {key}, a codebook entry (1 to {count}) or a direction (θ, φ) " +
                                  "for '{instance}'.", ("channel", channel), ("key", key), ("count", count), ("instance", instance));
    public static Diagnostic BeamIndexBeyond(string channel, string key, int index, string end, int count)
        => E("beam-index-beyond", "Propagation Channel '{channel}': {key}={index}, and the {end} end's codebook has {count} entries.",
             ("channel", channel), ("key", key), ("index", index), ("end", end), ("count", count));
    public static Diagnostic BeamRankBeyond(string channel, int rank, string search, int count)
        => E("beam-rank-beyond", "Propagation Channel '{channel}': BeamRank={rank}, and the {search} gives {count} distinct pair(s).",
             ("channel", channel), ("rank", rank), ("search", search), ("count", count));
}
