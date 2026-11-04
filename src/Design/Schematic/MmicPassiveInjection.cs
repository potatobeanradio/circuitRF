using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The MMIC passives' side of the substrate seam (brief-agent-authoring-overview.md AA-1): what
/// <see cref="MicrostripSubstrateInjection"/> is to MLIN. At extraction, a <c>MIMCAP</c>, <c>SPIRAL</c>,
/// <c>TFR</c> or <c>AIRBRIDGE</c> on a technology has its materials resolved from the stackup
/// (<see cref="MmicStackResolver"/>, the same resolution the artwork uses) and injected as plain SI
/// overrides, so none of them is typed twice.
///
/// <para><b>What the user states</b> is geometry only — W and L, the turns, the span. Everything a
/// process decides (the film's permittivity and thickness, the plates' metal, the sheet resistance,
/// the bridge height, the substrate under it all) comes from the technology. With no technology the
/// model takes its standalone defaults, which are the shipped GaAs process's; inside a technology that
/// cannot supply what a part needs, the run says which piece is missing and uses the same defaults.</para>
/// </summary>
public static class MmicPassiveInjection
{
    public static bool IsMmicKind(SymbolKind kind)
        => kind is SymbolKind.MimCap or SymbolKind.Spiral or SymbolKind.OctSpiral or SymbolKind.Tfr or SymbolKind.Airbridge;

    /// <summary>The parameters the technology supplies for <paramref name="kind"/> — the ones a line
    /// that states all of them has stated its own process with.</summary>
    public static IReadOnlyList<string> InjectedNames(SymbolKind kind) => kind switch
    {
        SymbolKind.MimCap    => ["Er", "Td", "TanD", "SigmaTop", "Ttop", "SigmaBot", "Tbot", "H", "ErSub"],
        SymbolKind.Spiral or SymbolKind.OctSpiral => ["Sigma", "T", "H", "ErSub", "Hb", "Erb"],
        SymbolKind.Tfr       => ["Rs", "H", "ErSub"],
        SymbolKind.Airbridge => ["Sigma", "T", "Hb", "Erb", "SigmaU", "Tu"],
        _                    => [],
    };

    private static MmicPart PartOf(SymbolKind kind) => kind switch
    {
        SymbolKind.MimCap => MmicPart.MimCap,
        SymbolKind.Spiral or SymbolKind.OctSpiral => MmicPart.Spiral,
        SymbolKind.Tfr    => MmicPart.Tfr,
        _                 => MmicPart.Airbridge,
    };

    /// <summary>
    /// The overrides for one instance, and the sentence to post when the technology could not supply
    /// them. No technology at all is NOT a message — the standalone default is what such a line has
    /// always meant, exactly as for a microstrip.
    /// </summary>
    public static (IReadOnlyList<ParameterAssignment> Overrides, string? Warning) Build(Technology? technology, SymbolKind kind)
    {
        if (technology is null || !IsMmicKind(kind)) return ([], null);

        var stack = MmicStackResolver.Resolve(technology);
        if (MmicStackResolver.Why(stack, PartOf(kind), technology.Name) is { } why)
            return ([], $"{why}; the {ComponentTypeRegistry.EngineReference(kind)} uses its standalone defaults " +
                        "(the shipped GaAs process's).");

        var sub = stack.Substrate!;
        var o = new List<ParameterAssignment>();
        void Add(string name, double value) => o.Add(new ParameterAssignment(name, value.ToString("R", CultureInfo.InvariantCulture)));

        switch (kind)
        {
            case SymbolKind.MimCap:
                var d = stack.CapDielectric!;
                Add("Er",       d.Epsr);
                Add("Td",       MmicStackResolver.Metres(d.ThicknessDbu));
                Add("TanD",     d.TanD);
                Add("SigmaTop", stack.TopPlate!.SigmaSm);
                Add("Ttop",     MmicStackResolver.Metres(stack.TopPlate.ThicknessDbu));
                Add("SigmaBot", stack.BaseMetal!.SigmaSm);
                Add("Tbot",     MmicStackResolver.Metres(stack.BaseMetal.ThicknessDbu));
                Add("H",        sub.HeightMeters);
                Add("ErSub",    sub.RelativePermittivity);
                break;
            case SymbolKind.Spiral:
            case SymbolKind.OctSpiral:
                Add("Sigma", stack.BaseMetal!.SigmaSm);
                Add("T",     MmicStackResolver.Metres(stack.BaseMetal.ThicknessDbu));
                Add("H",     sub.HeightMeters);
                Add("ErSub", sub.RelativePermittivity);
                Add("Hb",    stack.BridgeHeightMeters);
                Add("Erb",   stack.BridgeEpsR);
                break;
            case SymbolKind.Tfr:
                Add("Rs",    stack.ResistorFilm!.SheetResistanceOhmPerSq);
                Add("H",     sub.HeightMeters);
                Add("ErSub", sub.RelativePermittivity);
                break;
            case SymbolKind.Airbridge:
                Add("Sigma", stack.BridgeMetal!.SigmaSm);
                Add("T",     MmicStackResolver.Metres(stack.BridgeMetal.ThicknessDbu));
                Add("Hb",    stack.BridgeHeightMeters);
                Add("Erb",   stack.BridgeEpsR);
                if (stack.BaseMetal is { } crossed)               // the line under the bridge
                {
                    Add("SigmaU", crossed.SigmaSm);
                    Add("Tu",     MmicStackResolver.Metres(crossed.ThicknessDbu));
                }
                break;
        }
        return (o, null);
    }

    /// <summary>True for the parts with a readout: a TFR's resistance, a MIMCAP's capacitance and a
    /// spiral's inductance.</summary>
    public static bool HasReadout(SymbolKind kind) => kind is SymbolKind.Tfr or SymbolKind.MimCap or SymbolKind.Spiral or SymbolKind.OctSpiral;

    /// <summary>The readout's name: "R" for a TFR, "C" for a MIMCAP, "L" for a spiral.</summary>
    public static string ReadoutName(SymbolKind kind) => kind switch
    {
        SymbolKind.Tfr    => "R",
        SymbolKind.MimCap => "C",
        _                 => "L",
    };

    /// <summary>The geometry parameter a layout lists the readout row after.</summary>
    public static string ReadoutFollows(SymbolKind kind) => kind is SymbolKind.Tfr ? "Lc" : kind is SymbolKind.MimCap ? "L" : "Din";

    /// <summary>
    /// What a TFR's resistance, a MIMCAP's capacitance or a spiral's inductance comes out to, as the
    /// text a properties panel shows (<c>≈ 100 Ω (2 sq × 50 Ω/sq)</c>, <c>≈ 1.51 pF</c>,
    /// <c>≈ 1.5 nH (estimated)</c>). <paramref name="geometry"/> is the instance's parameters in SI
    /// (metres; N a plain count); a missing one takes the model's own default. Computed by the model a
    /// run builds, from the overrides <see cref="Build"/> injects — never a second copy of the formula
    /// that could disagree with what is simulated, and a closed form in every case, so it is cheap
    /// enough to follow every keystroke. Null for any other kind or a non-positive size, and then
    /// <paramref name="note"/> says why; when there IS a value there is no note — the value is marked
    /// estimated and that is all the panel says.
    /// </summary>
    public static string? Readout(Technology? technology, SymbolKind kind, IReadOnlyDictionary<string, double> geometry,
                                  out string? note)
    {
        note = null;
        if (!HasReadout(kind)) return null;
        foreach (var (name, v) in geometry)
            if (name is "W" or "L" or "Din" or "N" or "Lc" && !(v > 0)) { note = $"{name} must be positive."; return null; }

        var (overrides, _) = Build(technology, kind);
        var values = new Dictionary<string, CircuitRF.Core.Expressions.Value>(StringComparer.Ordinal);
        foreach (var (name, v) in geometry) values[name] = new CircuitRF.Core.Expressions.Value(v);
        foreach (var o in overrides)
            if (double.TryParse(o.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                values[o.Name] = new CircuitRF.Core.Expressions.Value(v);
        var model = ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind), values);

        var ci = CultureInfo.InvariantCulture;

        return model switch
        {
            ThinFilmResistorModel r => string.Format(ci, "≈ {0} ({1:0.###} sq × {2:0.###} Ω/sq)",
                                           FormatOhms(r.Resistance), r.Squares, r.SheetResistance),
            MimCapModel c           => "≈ " + FormatFarads(c.Capacitance),
            SpiralInductorModel l   => "≈ " + (l.Inductance * 1e9).ToString("0.##", ci) + " nH (estimated)",
            _                       => null,
        };
    }

    /// <summary>
    /// A spiral's series resistance as a properties panel's second readout line — at DC and at
    /// <paramref name="freqHz"/>, where the skin effect has raised it — from the same model as its L.
    /// Null for anything that is not a spiral.
    /// </summary>
    public static string? ResistanceReadout(Technology? technology, SymbolKind kind, IReadOnlyDictionary<string, double> geometry,
                                            double freqHz = 10e9)
    {
        if (kind is not (SymbolKind.Spiral or SymbolKind.OctSpiral)) return null;
        var values = new Dictionary<string, CircuitRF.Core.Expressions.Value>(StringComparer.Ordinal);
        foreach (var (name, v) in geometry) values[name] = new CircuitRF.Core.Expressions.Value(v);
        foreach (var o in Build(technology, kind).Overrides)
            if (double.TryParse(o.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                values[o.Name] = new CircuitRF.Core.Expressions.Value(v);
        if (ComponentModelFactory.TryCreate(ComponentTypeRegistry.EngineReference(kind), values) is not SpiralInductorModel coil)
            return null;
        var ci = CultureInfo.InvariantCulture;
        return string.Format(ci, "≈ {0} at DC, {1} at {2:0.##} GHz (estimated)",
            FormatOhms(coil.Resistance(0)), FormatOhms(coil.Resistance(freqHz)), freqHz / 1e9);
    }

    /// <summary><see cref="Readout(Technology?, SymbolKind, IReadOnlyDictionary{string, double}, out string?)"/>
    /// from a schematic instance's own rows, evaluated in their units. An expression the readout cannot
    /// evaluate (a VAR) shows nothing and says why; the run uses the real value.</summary>
    public static string? Readout(Technology? technology, SymbolKind kind, IEnumerable<EditableParameter> parameters, out string? note)
    {
        note = null;
        if (!HasReadout(kind)) return null;
        var geometry = new Dictionary<string, double>(StringComparer.Ordinal);
        var unevaluated = new List<string>();
        foreach (var p in parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Expression)) continue;
            try
            {
                string unit = CircuitRF.Core.Expressions.UnitNormalizer.ToEngineUnit(p.Unit);
                geometry[p.Name] = new CircuitRF.Core.Expressions.Evaluator()
                    .Eval(p.Expression, new CircuitRF.Core.Expressions.Scope("global"), unit.Length > 0 ? unit : null)
                    .AsReal();
            }
            catch { unevaluated.Add(p.Name); }
        }
        if (unevaluated.Count > 0)
        {
            note = $"{string.Join(", ", unevaluated)} {(unevaluated.Count > 1 ? "are expressions" : "is an expression")} "
                 + "the readout cannot evaluate here; the run uses the real value.";
            return null;
        }
        return Readout(technology, kind, geometry, out note);
    }

    /// <summary>A schematic instance's rows in SI, evaluated in their units; null when any cannot be
    /// evaluated here (a VAR) — the readouts then show nothing rather than a value for the wrong size.</summary>
    public static IReadOnlyDictionary<string, double>? GeometryOf(IEnumerable<EditableParameter> parameters)
    {
        var geometry = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Expression)) continue;
            try
            {
                string unit = CircuitRF.Core.Expressions.UnitNormalizer.ToEngineUnit(p.Unit);
                geometry[p.Name] = new CircuitRF.Core.Expressions.Evaluator()
                    .Eval(p.Expression, new CircuitRF.Core.Expressions.Scope("global"), unit.Length > 0 ? unit : null)
                    .AsReal();
            }
            catch { return null; }
        }
        return geometry;
    }

    private static string FormatOhms(double r) => r >= 1e3
        ? (r / 1e3).ToString("0.###", CultureInfo.InvariantCulture) + " kΩ"
        : r.ToString("0.###", CultureInfo.InvariantCulture) + " Ω";

    private static string FormatFarads(double c) => c < 1e-13
        ? (c * 1e15).ToString("0.##", CultureInfo.InvariantCulture) + " fF"
        : (c * 1e12).ToString("0.###", CultureInfo.InvariantCulture) + " pF";
}
