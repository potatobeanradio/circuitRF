// ================================================================
//  HarmonicaInputs.cs  —  M1 of brief-harmonicarf-h7
//
//  R-h7-3  every input is a VALUE change unless it is a STRUCTURAL one, and CircuitModel.StructuralKey
//          is what decides. Nothing here maintains a second list of "which inputs rebuild".
//  R-h7-4  the model's OWN parameters appear, read FROM the model. A hardcoded list of
//          plausible-looking parameters is worse than none.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Harmonica;

/// <summary>How an input's value is entered.</summary>
public enum HarmonicaInputEntry
{
    /// <summary>A real number, possibly with a scale prefix on the displayed unit.</summary>
    Number,
    /// <summary>An integer.</summary>
    Integer,
    /// <summary>A checkbox.</summary>
    Boolean,
    /// <summary>Free text — an SDD equation, an external model's string parameter.</summary>
    Text,
}

/// <summary>
/// One §7.5 input, as data. The strip renders these; nothing about the list is hardcoded in a view.
/// </summary>
/// <param name="Key">
/// The stable identity <see cref="HarmonicaInputs.Apply"/> writes back through. Model-declared
/// parameters carry the <c>param:</c> prefix so a device parameter called <c>Vds</c> can never be
/// mistaken for the bias input of the same name.
/// </param>
/// <param name="Structural">
/// Whether changing it moves <see cref="CircuitModel.StructuralKey"/> — i.e. whether the context has
/// to be rebuilt and the frame ladder reset. <b>Computed by asking the key, never by a second
/// table</b>: <see cref="HarmonicaInputs.Build"/> derives it by applying a probe value and comparing
/// the key, so an input that is added to the model without being classified here cannot be
/// mis-classified.
/// </param>
/// <param name="Placeholder">
/// R3C §3 — shown in place of an EMPTY <see cref="Text"/>, greyed. Unused today (both Vgs and Idq
/// always carry a real value once <see cref="HarmonicaContext.SolveVgsForIdq"/> landed — see that
/// method's own remarks) but left in place as general strip machinery rather than torn out.
/// </param>
/// <param name="EditText">
/// Owner follow-up (2026-08-13) — "keep it to 3 decimal places in the display; the inline text editor
/// should show the full value." <see cref="Text"/> is what the row DISPLAYS (rounded, for Vgs/Idq —
/// see <see cref="HarmonicaInputs.Build"/>'s own Vgs/Idq rows); this is what an inline editor SEEDS
/// from once opened. Null means "same as <see cref="Text"/>" — every input except Vgs/Idq has no
/// separate rounding, so <see cref="EditValue"/> is what every call site actually reads.
/// </param>
public sealed record HarmonicaInput(
    string              Key,
    string              Label,
    string              Text,
    string              Unit,
    string              Tooltip,
    HarmonicaInputEntry Entry,
    bool                Structural,
    string              Placeholder = "",
    string?             EditText    = null)
{
    /// <summary>The text an inline editor should seed from — <see cref="EditText"/> when the row has
    /// one, else <see cref="Text"/> itself (the common case).</summary>
    public string EditValue => EditText ?? Text;
}

/// <summary>
/// §7.5's input half: bias, frequency, compression, the compute-charge toggle, multiplicity, and
/// <b>every parameter the loaded model declares</b>.
///
/// <para><b>Framework-free on purpose</b> — it takes and returns <see cref="CircuitModel"/> and
/// strings, so the classification and the write-back are testable without a window. The strip is a
/// renderer of this list.</para>
/// </summary>
public static class HarmonicaInputs
{
    /// <summary>The prefix that marks a model-declared parameter's key.</summary>
    public const string ParameterPrefix = "param:";

    // ── the fixed inputs (§7.5) ───────────────────────────────────────────────

    public const string KeyVgs           = "bias.vgs";
    public const string KeyIdq           = "bias.idq";
    public const string KeyVds           = "bias.vds";
    public const string KeyFrequency     = "settings.f0";
    public const string KeyHarmonicCount = "settings.k";
    public const string KeyCompression   = "settings.compression";
    public const string KeyZ0            = "settings.z0";
    public const string KeyLoadlineSamples = "settings.loadline_samples";
    public const string KeyComputeCharge = "settings.charge";
    public const string KeyFftOverSample = "settings.fftoversample";
    public const string KeyMultiplicity  = "dut.m";

    /// <summary>
    /// The whole §7.5 input list for a model, in strip order: bias, drive, then the model's own.
    /// </summary>
    /// <param name="liveIdqAmpsWhenVgsDriven">
    /// R3C follow-up (2026-08-13) — the DC drain current the CURRENT Vgs actually draws, amps, read
    /// live from a <see cref="HarmonicaContext"/> the caller already owns (<c>HarmonicaContext.
    /// DcDrainCurrentAmps</c>). Shown in the Idq row ONLY when <c>model.Bias.Idq</c> is null (Vgs is
    /// the driver) — otherwise the row shows the user's own STATED target, which is what
    /// <see cref="HarmonicaContext.SolveVgsForIdq"/> was actually asked to hit. This stays a plain
    /// <c>double?</c> parameter rather than a <see cref="HarmonicaContext"/> reference so this method
    /// keeps its "pure function of a model" contract — see the class's own remark.
    /// </param>
    public static IReadOnlyList<HarmonicaInput> Build(CircuitModel model,
                                                       double? liveIdqAmpsWhenVgsDriven = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        // R3C follow-up — Idq is user-facing in mA (owner request); BiasSpec.Idq itself stays amps
        // (Num/TryReal below convert at this one boundary, same as every other unit-bearing input in
        // this file). model.Bias.Idq set ⇒ the user's own stated target; else the LIVE, computed
        // current the CURRENT Vgs draws (Idq is then informational, not an editable "answer" — typing
        // into it still switches the document to Idq-driven, exactly like typing into Vgs switches it
        // back, per HarmonicaInputs.Apply's own KeyVgs/KeyIdq cases).
        //
        // Owner follow-up — the STRIP shows Vgs to 3 places and Idq to 1 (a live-updating mA readout
        // at full precision is noise, not information); the inline EDITOR still seeds from the full
        // value (HarmonicaInput.EditText), so committing without retyping every digit round-trips
        // exactly rather than truncating to whatever the display happened to round to.
        double? idqAmps = model.Bias.Idq ?? (liveIdqAmpsWhenVgsDriven is { } liveA && double.IsFinite(liveA) ? liveA : null);
        string idqText     = idqAmps is { } a ? Num1(a * 1000.0) : "";
        string idqEditText = idqAmps is { } a2 ? Num(a2 * 1000.0) : "";

        var list = new List<HarmonicaInput>
        {
            Make(model, KeyVgs, "Vgs", model.Bias.Vgs is { } vg ? Num3(vg) : "", "V",
                 model.Bias.Idq is not null
                     ? "Gate bias, solved for the stated Idq (1-D secant on the DC operating point)."
                     : "Gate bias. Set Idq instead to solve Vgs for a target current.",
                 HarmonicaInputEntry.Number,
                 editText: model.Bias.Vgs is { } vge ? Num(vge) : null),
            Make(model, KeyIdq, "Idq", idqText, "mA",
                 model.Bias.Idq is not null
                     ? "Quiescent drain current target. Vgs is solved for it at the stated Vds."
                     : "Quiescent drain current — read live from the current Vgs. Type a value to " +
                       "drive the bias from Idq instead (Vgs is then solved for it).",
                 HarmonicaInputEntry.Number,
                 editText: idqEditText),
            Make(model, KeyVds, "Vds", Num(model.Bias.Vds), "V", "Drain supply.",
                 HarmonicaInputEntry.Number),

            Make(model, KeyFrequency, "Freq:", Num(model.Settings.FrequencyHz / 1e9), "GHz",
                 "Fundamental drive frequency. Changing it rebuilds the context and resets the frame ladder.",
                 HarmonicaInputEntry.Number),
            Make(model, KeyHarmonicCount, "Harmonic Order:", model.Settings.HarmonicCount.ToString(CultureInfo.InvariantCulture), "",
                 "Harmonic order. Changing it rebuilds the context and resets the frame ladder; " +
                 "marker bands above the new K are dropped.",
                 HarmonicaInputEntry.Integer),
            Make(model, KeyCompression, "Compression:", Num(model.Settings.CompressionDb), "dB",
                 "Compression target the contour grid is taken at.",
                 HarmonicaInputEntry.Number),
            Make(model, KeyZ0, "Z0", Num(model.Settings.Z0), "Ω",
                 "Smith-chart normalisation reference impedance. Terminations do not move — only " +
                 "their Γ (and the grid) do. For best visualization, set Z0 = Ropt.",
                 HarmonicaInputEntry.Number),
            Make(model, KeyLoadlineSamples, "loadline pts",
                 model.Settings.LoadlineSamples.ToString(CultureInfo.InvariantCulture), "",
                 "Time samples the loadline is drawn at. Exact at any count — the spectrum carries " +
                 $"every harmonic — not a solve setting. Clamped to " +
                 $"{HarmonicaSettings.LoadlineSamplesMin}..{HarmonicaSettings.LoadlineSamplesMax}.",
                 HarmonicaInputEntry.Integer),
            Make(model, KeyFftOverSample, "FFT×", model.Settings.FftOverSample.ToString(CultureInfo.InvariantCulture), "",
                 "FFT oversampling factor. Structural — the time grid changes size.",
                 HarmonicaInputEntry.Integer),
            Make(model, KeyComputeCharge, "charge", model.Settings.ComputeCharge ? "1" : "0", "",
                 "Whether the DUT's charge terms are evaluated. With charge OFF the load glyph " +
                 "coincides with its marker on a bare device (§4.5 consequence 1).",
                 HarmonicaInputEntry.Boolean),
            Make(model, KeyMultiplicity, "M", Num(model.Dut.Multiplicity), "",
                 "Device multiplier — how many identical devices in parallel.",
                 HarmonicaInputEntry.Number),
        };

        list.AddRange(DeclaredModelParameters(model));
        return list;
    }

    /// <summary>
    /// R-h7-4 — the parameters <b>the loaded model itself declares</b>, never a table of plausible
    /// ones. Where they come from depends on what the DUT is, and each route is the model's own:
    ///
    /// <list type="bullet">
    /// <item><b>SDD</b> — its equation strings, keyed as the <c>.cnl</c> spells them
    /// (<c>I[1,0]</c>, <c>Q[2]</c>, …). An SDD's parameters ARE its equations.</item>
    /// <item><b>Native FET</b> — <see cref="ComponentTypeRegistry.DefaultParameters"/> for that law,
    /// which is the same declaration the schematic parameter editor renders. Each of the five laws
    /// has its OWN set, so periphery-like parameters appear when and only when the law has them.</item>
    /// <item><b>Diode</b> — the same registry route.</item>
    /// <item><b>External</b> — whatever the document actually carries, because the descriptor lives
    /// with the provider and a <c>.charm</c> that cannot resolve its model must still show what it
    /// was set to rather than an invented list.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<HarmonicaInput> DeclaredModelParameters(CircuitModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dut = model.Dut;

        var declared = dut.Kind switch
        {
            // R-h9c-5 (R1C §5) — an SDD's parameters ARE its equations, hundreds of characters of
            // expression text in a 160 px box, and §6's Set DUT dialog now edits them properly. So
            // the strip stops surfacing them — the owner's own words: "Stop surfacing SDD equation
            // parameters in the strip now that Set DUT's dialog edits them properly." The line is
            // drawn on the DUT KIND, not the parameter name: a native FET's Ipk/Vpk (below) is
            // exactly what this whole pass is for, and stays.
            DutKind.Sdd => [],

            DutKind.NativeFet or DutKind.Diode => RegistryParameters(dut),

            // R-h8-2 — an external model declares its parameters in its own descriptor, so ASK IT.
            // The descriptor is the provider's and is only reachable when the provider resolves (a
            // kit that is not installed, an .osdi that has moved), which is why this falls back to
            // what the document itself carries rather than to an invented list: a .charm whose model
            // reference cannot be resolved must still show what it was set to (§8.1).
            DutKind.External => ExternalParameters(dut),

            _ => dut.Parameters.Select(p =>
                (Name: p.Key, Text: p.Value, Unit: "",
                 Tip: $"{dut.TypeName} parameter {p.Key}, as this document carries it.",
                 Entry: HarmonicaInputEntry.Text)).ToList(),
        };

        return [.. declared.Select(d => Make(
            model, ParameterPrefix + d.Name, d.Name,
            dut.Parameters.TryGetValue(d.Name, out string? v) ? v : d.Text,
            d.Unit, d.Tip, d.Entry))];
    }

    /// <summary>
    /// An external model's own declared parameters, from its descriptor. Falls back to whatever the
    /// document carries when the provider cannot be reached — never to a made-up list.
    /// </summary>
    private static List<(string Name, string Text, string Unit, string Tip, HarmonicaInputEntry Entry)>
        ExternalParameters(DutSpec dut)
    {
        var declared = HarmonicaDutCatalog.TryDescribe(dut, out _);

        if (declared is null)
            return [.. dut.Parameters.Select(p =>
                (Name: p.Key, Text: p.Value, Unit: "",
                 Tip: $"{dut.TypeName} parameter {p.Key}, as this document carries it. The model " +
                      "itself could not be reached, so its own declaration is not being shown.",
                 Entry: HarmonicaInputEntry.Text))];

        // The parameter the document carries but the model does not declare is kept and shown too —
        // dropping it would silently discard a setting the user made against an earlier revision.
        var known = new HashSet<string>(declared.Parameters.Select(p => p.Name), StringComparer.Ordinal);

        var rows = declared.Parameters.Select(p =>
            (Name: p.Name,
             Text: p.DefaultText ?? "",
             Unit: p.Units,
             Tip:  $"{dut.TypeName} parameter {p.Name}" +
                   (p.Units.Length > 0 ? $" ({p.Units})" : "") + $", declared by the model as {p.Kind}.",
             Entry: p.Kind == ExternalParamKind.Double || p.Kind == ExternalParamKind.Int
                 ? HarmonicaInputEntry.Number : HarmonicaInputEntry.Text)).ToList();

        rows.AddRange(dut.Parameters
            .Where(p => !known.Contains(p.Key))
            .Select(p => (Name: p.Key, Text: p.Value, Unit: "",
                          Tip: $"'{p.Key}' is carried by this document but is not declared by " +
                               $"{dut.TypeName}. It is shown so it can be corrected rather than lost.",
                          Entry: HarmonicaInputEntry.Text)));

        return rows;
    }

    private static List<(string Name, string Text, string Unit, string Tip, HarmonicaInputEntry Entry)>
        RegistryParameters(DutSpec dut)
    {
        var kind = SymbolKindFor(dut.TypeName);
        if (kind is null) return [];

        return [.. ComponentTypeRegistry.DefaultParameters(kind.Value, portCount: 2)
            .Select(p => (p.Name, p.Expression, p.Unit,
                          $"{dut.TypeName} parameter {p.Name}" + (p.Unit.Length > 0 ? $" ({p.Unit})" : "") + ".",
                          HarmonicaInputEntry.Number))];
    }

    /// <summary>
    /// Engine type name → the schematic symbol that declares it. Built by INVERTING
    /// <see cref="ComponentTypeRegistry.EngineReference"/> rather than by a second literal table, so a
    /// renamed engine type cannot leave the two disagreeing. Several symbols map to one engine name
    /// (the three tuner tiles); first wins, which is harmless — they declare the same parameters.
    /// </summary>
    private static SymbolKind? SymbolKindFor(string engineTypeName)
    {
        foreach (SymbolKind kind in Enum.GetValues<SymbolKind>())
            if (string.Equals(ComponentTypeRegistry.EngineReference(kind), engineTypeName,
                              StringComparison.OrdinalIgnoreCase))
                return kind;
        return null;
    }

    // ── classification: ask the key, do not keep a list ───────────────────────

    private static HarmonicaInput Make(CircuitModel model, string key, string label, string text,
                                       string unit, string tooltip, HarmonicaInputEntry entry,
                                       string placeholder = "", string? editText = null)
        => new(key, label, text, unit, tooltip, entry, IsStructural(model, key), placeholder, editText);

    /// <summary>
    /// Whether writing <paramref name="key"/> moves <see cref="CircuitModel.StructuralKey"/>.
    ///
    /// <para><b>Measured, not tabulated.</b> A probe value is applied and the two keys compared, so
    /// this cannot drift from what <c>HarmonicaContext.Apply</c> will actually do — which is the
    /// whole of R-h7-3. The probe is discarded; the model is a record and nothing is mutated.</para>
    /// </summary>
    public static bool IsStructural(CircuitModel model, string key)
    {
        var probed = Apply(model, key, ProbeText(model, key), out string? error);
        return error is null && probed is not null
            && probed.StructuralKey != model.StructuralKey;
    }

    /// <summary>A value the given input will certainly accept and that certainly differs from the
    /// current one — so the probe cannot report "not structural" merely because nothing moved.</summary>
    private static string ProbeText(CircuitModel model, string key)
    {
        if (key.StartsWith(ParameterPrefix, StringComparison.Ordinal))
        {
            string name = key[ParameterPrefix.Length..];
            string cur  = model.Dut.Parameters.TryGetValue(name, out string? v) ? v : "";
            // NOT a trailing space: Apply trims, so a whitespace-only probe stores the identical
            // value and the key does not move — which would report an SDD equation as a VALUE change.
            // Caught by the gate that applies each probe and compares the key, not by review.
            return cur + "_";
        }

        return key switch
        {
            KeyComputeCharge => model.Settings.ComputeCharge ? "0" : "1",
            KeyHarmonicCount => Num(model.Settings.HarmonicCount + 1),
            KeyFftOverSample => Num(model.Settings.FftOverSample + 1),
            KeyFrequency     => Num(model.Settings.FrequencyHz / 1e9 + 1.0),
            KeyCompression   => Num(model.Settings.CompressionDb + 1.0),
            KeyZ0            => Num(model.Settings.Z0 + 1.0),
            KeyLoadlineSamples => Num(model.Settings.LoadlineSamples + 1),
            KeyVgs           => Num((model.Bias.Vgs ?? 0.0) - 0.1),
            // R3C follow-up — the field (and so the probe text Apply parses) is mA now; Bias.Idq
            // itself is still amps, hence the ×1000 before adding the probe's own mA nudge.
            KeyIdq           => Num((model.Bias.Idq ?? 0.0) * 1000.0 + 0.01),
            KeyVds           => Num(model.Bias.Vds + 1.0),
            KeyMultiplicity  => Num(model.Dut.Multiplicity + 1.0),
            _                => "",
        };
    }

    // ── write-back ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the model with one input written, or null with <paramref name="error"/> set. The
    /// caller compares <see cref="CircuitModel.StructuralKey"/> to decide whether to reset the frame
    /// ladder (§6.8) — this method deliberately does not decide that, because the comparison is the
    /// definition and a second opinion could disagree with it.
    /// </summary>
    public static CircuitModel? Apply(CircuitModel model, string key, string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(model);
        error = null;
        text  = text?.Trim() ?? "";

        if (key.StartsWith(ParameterPrefix, StringComparison.Ordinal))
        {
            string name = key[ParameterPrefix.Length..];
            var map = new Dictionary<string, string>(model.Dut.Parameters, StringComparer.Ordinal)
            {
                [name] = text,
            };
            return model with { Dut = model.Dut with { Parameters = map } };
        }

        switch (key)
        {
            case KeyVgs:
                if (text.Length == 0)
                    return model with { Bias = model.Bias with { Vgs = null } };
                if (!TryReal(text, out double vgs)) { error = Bad("Vgs", text); return null; }
                // Setting Vgs directly clears Idq: §7.5 offers "Vgs OR Idq", and a model carrying
                // both would leave the secant silently overwriting what the user just typed.
                return model with { Bias = model.Bias with { Vgs = vgs, Idq = null } };

            case KeyIdq:
                if (text.Length == 0)
                    return model with { Bias = model.Bias with { Idq = null } };
                if (!TryReal(text, out double idqMa)) { error = Bad("Idq", text); return null; }
                // R3C follow-up — the field is mA (owner request); BiasSpec.Idq itself stays amps,
                // the unit every solver-side consumer (HarmonicaContext.SolveVgsForIdq) expects.
                return model with { Bias = model.Bias with { Idq = idqMa / 1000.0 } };

            case KeyVds:
                if (!TryReal(text, out double vds)) { error = Bad("Vds", text); return null; }
                return model with { Bias = model.Bias with { Vds = vds } };

            case KeyFrequency:
                if (!TryReal(text, out double ghz) || ghz <= 0)
                { error = "f₀ must be a positive frequency in GHz."; return null; }
                return model with { Settings = model.Settings with { FrequencyHz = ghz * 1e9 } };

            case KeyHarmonicCount:
                if (!TryInt(text, out int k) || k < 1 || k > 32)
                { error = "K must be a whole number between 1 and 32."; return null; }
                return model with { Settings = model.Settings with { HarmonicCount = k } };

            case KeyFftOverSample:
                if (!TryInt(text, out int os) || os < 1 || os > 8)
                { error = "The FFT oversampling factor must be a whole number between 1 and 8."; return null; }
                return model with { Settings = model.Settings with { FftOverSample = os } };

            case KeyCompression:
                if (!TryReal(text, out double cdb) || cdb <= 0)
                { error = "The compression target must be a positive number of dB."; return null; }
                return model with { Settings = model.Settings with { CompressionDb = cdb } };

            case KeyZ0:
                if (!TryReal(text, out double z0) || z0 <= 0)
                { error = "Z0 must be a positive number of ohms."; return null; }
                return model with { Settings = model.Settings with { Z0 = z0 } };

            case KeyLoadlineSamples:
                if (!TryInt(text, out int ls) ||
                    ls < HarmonicaSettings.LoadlineSamplesMin || ls > HarmonicaSettings.LoadlineSamplesMax)
                {
                    error = $"Loadline sample count must be a whole number between " +
                            $"{HarmonicaSettings.LoadlineSamplesMin} and {HarmonicaSettings.LoadlineSamplesMax}.";
                    return null;
                }
                return model with { Settings = model.Settings with { LoadlineSamples = ls } };

            case KeyComputeCharge:
                return model with
                {
                    Settings = model.Settings with { ComputeCharge = ParseBool(text) },
                };

            case KeyMultiplicity:
                if (!TryReal(text, out double m) || m <= 0)
                { error = "The device multiplier must be a positive number."; return null; }
                return model with { Dut = model.Dut with { Multiplicity = m } };

            default:
                error = $"'{key}' is not an input of this document.";
                return null;
        }
    }

    private static string Bad(string what, string text)
        => $"{what}: '{text}' is not a number.";

    private static bool ParseBool(string text)
        => text.Length > 0
        && (text[0] is 't' or 'T' or 'y' or 'Y' || (text[0] != '0' && char.IsDigit(text[0])));

    private static bool TryReal(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool TryInt(string s, out int v)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static string Num(double v)
        => v.ToString("0.#######", CultureInfo.InvariantCulture);

    /// <summary>Owner follow-up — the Vgs row's DISPLAY precision (3 places); <see cref="Num"/> stays
    /// what an inline editor seeds from (<see cref="HarmonicaInput.EditText"/>).</summary>
    private static string Num3(double v)
        => v.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>Owner follow-up — the Idq row's DISPLAY precision, mA (1 place): "49.1 mA, not
    /// 49.113mA". Same split from <see cref="Num"/> as <see cref="Num3"/>.</summary>
    private static string Num1(double v)
        => v.ToString("0.0", CultureInfo.InvariantCulture);
}
