// What each source part becomes — brief-schimp-1 R-si1-3, overview D3.
//
// NATIVE ONLY WHERE THE SOURCE'S OWN EVIDENCE MAKES IT CERTAIN: a rule matches a symbol name AND a reference prefix AND a
// pin count, every condition it states. Anything no rule matches becomes a symbol cell — the source's own drawing, no
// model — so an import never invents behaviour for a part it does not know. A user may push a native row back to a
// symbol cell; never the other way.
//
// The table is DATA: SchematicImportMap.Rules. A new two-pin kind is a row, not a branch per format.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>What a part becomes.</summary>
public enum ImportPartKind
{
    /// <summary>A circuitRF component of <see cref="ImportPartDecision.Native"/>.</summary>
    Native,
    /// <summary>A cell holding the source's own symbol and no model (overview D3).</summary>
    SymbolCell,
    /// <summary>A ground symbol: every pin on it is node 0.</summary>
    Ground,
}

/// <summary>
/// One row of the map (R-si1-3). Every non-null condition must hold: <paramref name="SymbolPattern"/> on the symbol's
/// display name (whole name, case-insensitive), <paramref name="ReferencePrefix"/> as the reference's leading letters
/// (case-insensitive), <paramref name="PinCount"/> on the unit's pins, <paramref name="ValuePattern"/> on the value text.
/// </summary>
/// <param name="Native">The component a match becomes; null for a ground rule.</param>
/// <param name="Parameter">The parameter its value is read into (<c>R</c>, <c>C</c>, <c>L</c>).</param>
/// <param name="CaseFromName">The symbol is named after its land pattern, <c>R-0603</c>: it matches only when the part
/// after the first <c>-</c> is a case size circuitRF knows, and the part takes that footprint.</param>
public sealed record SchematicImportRule(
    string Name, string? SymbolPattern, string? ReferencePrefix, int? PinCount, string? ValuePattern,
    ImportPartKind Becomes, SymbolKind? Native = null, string? Parameter = null, bool CaseFromName = false)
{
    public bool Matches(string symbolName, string reference, int pinCount, string? value)
    {
        if (CaseFromName && SchematicImportMap.CaseOf(symbolName) is null) return false;
        if (SymbolPattern is not null && !Regex.IsMatch(symbolName, $"^(?:{SymbolPattern})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        if (ReferencePrefix is not null
            && !new string([.. reference.TakeWhile(char.IsLetter)]).Equals(ReferencePrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (PinCount is int n && n != pinCount) return false;
        if (ValuePattern is not null && !Regex.IsMatch(value ?? "", ValuePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        return true;
    }
}

/// <summary>What one part (one reference, every unit) becomes, and why — a row of the import dialog's parts table.</summary>
/// <param name="Expression">A native part's value: the number (or the variable standing for it).</param>
/// <param name="Unit">The number's unit (<c>kOhm</c>, <c>nF</c>), null with a variable.</param>
/// <param name="Variable">The global variable a value that does not parse became (<c>R3_R</c>), with its default.</param>
/// <param name="CellName">A symbol cell's name, before any collision suffix.</param>
/// <param name="Footprint">A native part's land pattern, as its <c>Footprint</c> parameter spells it (<c>smt:1210@N</c>),
/// when the source states one circuitRF has built in.</param>
/// <param name="PinOrder">The source's pin numbers in the native part's terminal order, when that is not the order the
/// symbol declares them in: a supply connector's voltage pin is the source's <c>+</c> whichever it lists first.</param>
public sealed record ImportPartDecision(
    string Reference, string SymbolKey, string SymbolName, string? Value, ImportPartKind Becomes, string? Rule,
    SymbolKind? Native = null, string? Parameter = null, string? Expression = null, string? Unit = null,
    (string Name, string Expression, string Unit)? Variable = null, string? CellName = null, string? Note = null,
    string? Footprint = null, IReadOnlyList<string>? PinOrder = null)
{
    /// <summary>The parts table's "becomes" column: <c>R → R=10 kOhm</c>, <c>U3 → symbol cell LM358</c>.</summary>
    public string Describe() => Becomes switch
    {
        ImportPartKind.Native => $"{Reference} → {Parameter}={Expression}{(Unit is { Length: > 0 } u ? " " + u : "")}"
                                 + (Footprint is { } f ? $", {f}" : ""),
        ImportPartKind.Ground => $"{Reference} → ground",
        _ => $"{Reference} → symbol cell {CellName}",
    };
}

public static class SchematicImportMap
{
    /// <summary>The rules, in the order they are tried (R-si1-3). Data, not code: one row per certain mapping.</summary>
    public static readonly IReadOnlyList<SchematicImportRule> Rules =
    [
        // A symbol named after its land pattern — "R-0603", "C-1210" — is that part in that case size (owner, 2026-10-19).
        new("resistor in a case",  @"R-.+", "R", 2, null, ImportPartKind.Native, SymbolKind.Resistor,  "R", CaseFromName: true),
        new("capacitor in a case", @"C-.+", "C", 2, null, ImportPartKind.Native, SymbolKind.Capacitor, "C", CaseFromName: true),
        new("inductor in a case",  @"L-.+", "L", 2, null, ImportPartKind.Native, SymbolKind.Inductor,  "L", CaseFromName: true),
        new("resistor",  @"R|RES|RESISTOR|R[_ ].*|RES[_ ].*|RESISTOR[_ ].*",          "R", 2, null, ImportPartKind.Native, SymbolKind.Resistor,  "R"),
        new("capacitor", @"C|CAP|CAPACITOR|CP\d?|C[_ ].*|CAP[_ ].*|CAPACITOR[_ ].*|CP\d?[_ ].*", "C", 2, null, ImportPartKind.Native, SymbolKind.Capacitor, "C"),
        new("inductor",  @"L|IND|INDUCTOR|L[_ ].*|IND[_ ].*|INDUCTOR[_ ].*",          "L", 2, null, ImportPartKind.Native, SymbolKind.Inductor,  "L"),
        // A ferrite bead is circuitRF's Bead: the reference says what the part is and the symbol agrees. Its value
        // ("220R/1A") is the impedance it is sold by, read as Rp, the peak of the Bead's impedance.
        new("ferrite bead", @"FB|BEAD|FERRITE|FERRITE[_ ]?BEAD.*|BEAD[_ ].*|FB[_ ].*", "FB", 2, null, ImportPartKind.Native, SymbolKind.Bead, "Rp"),
        new("ground",    @"GND|GROUND|EARTH|0|GND[_ ].*|GROUND[_ ].*",                null, 1, null, ImportPartKind.Ground),
    ];

    /// <summary>The built-in case size a symbol named after its land pattern states — the part after the first <c>-</c>
    /// of <c>R-0603</c> — or null when that is not a case circuitRF knows.</summary>
    public static SmtCase? CaseOf(string symbolName)
    {
        int dash = symbolName.IndexOf('-');
        return dash > 0 && dash < symbolName.Length - 1 ? SmtCaseTable.Find(symbolName[(dash + 1)..]) : null;
    }

    /// <summary>
    /// The land pattern a part's own footprint property names (<c>PCB Footprint</c>, <c>Footprint</c> — any property whose
    /// name says footprint), when it is a chip case circuitRF builds: or null.
    /// </summary>
    public static FootprintRef? FootprintOfProperties(IReadOnlyDictionary<string, string>? properties) =>
        properties?.Where(kv => kv.Key.Contains("footprint", StringComparison.OrdinalIgnoreCase))
                   .Select(kv => FootprintOfName(kv.Value)).FirstOrDefault(f => f is not null);

    /// <summary>
    /// A chip case read off a footprint's name — an IPC-7351 name (<c>RESC1005X038L</c>, <c>CAPC1608X080N</c>,
    /// <c>INDC1005X060L</c>, any prefix or suffix around it), whose size is METRIC and whose last letter is its density
    /// level; or a name stating both sizes, imperial first (<c>R_0603_1608Metric</c>). Null for anything else.
    /// <para><b>The size in an IPC name is metric, always.</b> <c>1005</c> there is 1.0 x 0.5 mm, the 0402 case — read as
    /// an imperial code it would be the 01005, a part 2.5 times smaller, and nothing downstream would notice.</para>
    /// </summary>
    public static FootprintRef? FootprintOfName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var ipc = Regex.Match(name, @"(?:RES|CAP|IND)C[A-Z]?(\d{4})X\d+([LNM])?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (ipc.Success
            && SmtCaseTable.All.FirstOrDefault(c => c.Family == SmtCaseFamily.Chip && c.MetricTwin == ipc.Groups[1].Value) is { } metric)
            return FootprintRef.For(metric, char.ToUpperInvariant(ipc.Groups[2].Value.FirstOrDefault()) switch
            {
                'L' => DensityLevel.Least,
                'M' => DensityLevel.Most,
                _ => FootprintRef.DefaultDensity,
            });
        var both = Regex.Match(name, @"(?<![0-9])(\d{4})_(\d{4})Metric", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return both.Success && SmtCaseTable.Find(both.Groups[1].Value) is { Family: SmtCaseFamily.Chip } imperial
                            && imperial.MetricTwin == both.Groups[2].Value
            ? FootprintRef.For(imperial) : null;
    }

    /// <summary>A net name the source uses for ground — a power label of one of these is node 0 (overview D3).</summary>
    public static bool IsGroundName(string name) =>
        Regex.IsMatch(name.Trim(), "^(GND|GROUND|EARTH|0)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every part's decision, by reference, in source order. <paramref name="forceSymbolCell"/> names references the user
    /// pushed back to a symbol cell; a reference that is already one is unaffected.
    /// </summary>
    public static IReadOnlyList<ImportPartDecision> Decide(ForeignSchematic source, IReadOnlyCollection<string>? forceSymbolCell = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var force = new HashSet<string>(forceSymbolCell ?? [], StringComparer.Ordinal);
        var decisions = new List<ImportPartDecision>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in source.Pages)
            foreach (var inst in sheet.Instances)
            {
                if (!seen.Add(inst.Reference)) continue;
                var symbol = source.Symbol(inst.SymbolKey);
                string name = symbol?.DisplayName ?? inst.SymbolKey;
                int pins = symbol?.Unit(inst.Unit)?.Pins.Count ?? 0;
                // A multi-unit part is never a two-pin passive, whatever its unit draws.
                bool single = symbol is null || symbol.UnitCount <= 1;
                var rule = single && !force.Contains(inst.Reference)
                    ? Rules.FirstOrDefault(r => r.Matches(name, inst.Reference, pins, inst.Value))
                    : null;
                var supply = rule is null && single && !force.Contains(inst.Reference)
                    ? SupplyOf(inst, name, symbol?.Unit(inst.Unit)?.Pins) : null;
                decisions.Add(supply ?? (rule is null
                    ? new ImportPartDecision(inst.Reference, inst.SymbolKey, name, inst.Value, ImportPartKind.SymbolCell, null,
                                             CellName: CellNameFor(name))
                    : Native(inst, name, rule)));
            }
        return decisions;
    }

    /// <summary>
    /// A supply connector: two pins, one named for ground and the other for a voltage (<c>5V</c>, <c>+12V</c>,
    /// <c>3V3</c>). It is where the supply enters the circuit, so it becomes a DC source of that voltage, its <c>+</c>
    /// on the voltage pin — the pin names are the source's own evidence. Null for anything else; a rail drawn only as a
    /// net (a <c>+5V</c> label) stays a net.
    /// </summary>
    private static ImportPartDecision? SupplyOf(ForeignInstance inst, string name, IReadOnlyList<ForeignPin>? pins)
    {
        if (pins is not { Count: 2 }) return null;
        int ground = pins[0].Name is { } a && IsGroundName(a) ? 0 : pins[1].Name is { } b && IsGroundName(b) ? 1 : -1;
        if (ground < 0 || VoltageOf(pins[1 - ground].Name) is not { } volts) return null;
        return new ImportPartDecision(inst.Reference, inst.SymbolKey, name, inst.Value, ImportPartKind.Native, SupplyRuleName,
                                      SymbolKind.Vdc, "Vdc", volts, "V",
                                      Note: $"{inst.Reference} is a supply connector (pins {pins[1 - ground].Name} and {pins[ground].Name}): "
                                          + $"it is a DC source of {volts} V.",
                                      PinOrder: [pins[1 - ground].Number, pins[ground].Number]);
    }

    /// <summary>The rule a supply connector's decision names (<see cref="SupplyOf"/>).</summary>
    public const string SupplyRuleName = "supply connector";

    /// <summary>The voltage a pin name states — <c>5V</c>, <c>+5V</c>, <c>+3.3V</c>, <c>3V3</c>, <c>12V0</c> — or null.</summary>
    internal static string? VoltageOf(string? pinName)
    {
        var m = Regex.Match((pinName ?? "").Trim(), @"^\+?(\d+)(?:\.(\d+))?V(\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success || m.Groups[2].Success && m.Groups[3].Success) return null;
        string frac = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        double v = double.Parse(m.Groups[1].Value + (frac.Length > 0 ? "." + frac : ""), CultureInfo.InvariantCulture);
        return v > 0 ? Invariant(v) : null;
    }

    private static ImportPartDecision Native(ForeignInstance inst, string name, SchematicImportRule rule) =>
        NativeValue(inst, name, rule) with
        {
            Footprint = (rule.CaseFromName && CaseOf(name) is { } c ? FootprintRef.For(c) : FootprintOfProperties(inst.Properties))?.ToString(),
        };

    private static ImportPartDecision NativeValue(ForeignInstance inst, string name, SchematicImportRule rule)
    {
        if (rule.Becomes == ImportPartKind.Ground)
            return new ImportPartDecision(inst.Reference, inst.SymbolKey, name, inst.Value, ImportPartKind.Ground, rule.Name);

        var kind = rule.Native!.Value;
        string param = rule.Parameter!;
        if (kind == SymbolKind.Bead) return BeadValue(inst, name, rule);
        if (ReadValue(inst.Value, kind) is { } read)
            return new ImportPartDecision(inst.Reference, inst.SymbolKey, name, inst.Value, ImportPartKind.Native, rule.Name,
                                          kind, param, read.Number, read.Unit);

        // The AS rule: the native part, its value a global variable, the source text kept as a note.
        bool notFitted = BomTablePaste.IsNotFittedMarker(inst.Value);
        string variable = Identifier($"{inst.Reference}_{param}");
        var (number, unit) = DefaultFor(kind, notFitted);
        string note = inst.Value is { Length: > 0 } v
            ? $"{inst.Reference}'s value \"{v}\" is not {Noun(kind)}" + (notFitted ? " (the part is not fitted)" : "")
              + $"; it is the variable {variable} = {number} {unit}."
            : $"{inst.Reference} has no value; it is the variable {variable} = {number} {unit}.";
        return new ImportPartDecision(inst.Reference, inst.SymbolKey, name, inst.Value, ImportPartKind.Native, rule.Name,
                                      kind, param, variable, null, (variable, number, unit), Note: note);
    }

    /// <summary>
    /// A ferrite bead's value is the impedance it is sold by (<c>220R</c>, <c>1k/200mA</c>), which is Rp, the peak of the
    /// Bead's impedance. Nothing in it states L or Cp, and the Bead takes zero as "not modelled": until they are set the
    /// part is its Rdc, which the note says. A value that is not an impedance leaves every parameter at its default.
    /// </summary>
    private static ImportPartDecision BeadValue(ForeignInstance inst, string name, SchematicImportRule rule)
    {
        if (ReadValue(inst.Value, SymbolKind.Resistor) is { } read)
            return new ImportPartDecision(inst.Reference, inst.SymbolKey, name, inst.Value, ImportPartKind.Native, rule.Name,
                                          SymbolKind.Bead, "Rp", read.Number, read.Unit,
                                          Note: $"{inst.Reference} is a Bead with Rp = {read.Number} {read.Unit}, read from \"{inst.Value}\"; "
                                              + "its L and Cp are not in the source, so it is its Rdc until they are set.");
        return new ImportPartDecision(inst.Reference, inst.SymbolKey, name, inst.Value, ImportPartKind.Native, rule.Name,
                                      SymbolKind.Bead, "Rdc", "0.01", "Ohm",
                                      Note: (inst.Value is { Length: > 0 } v ? $"{inst.Reference}'s value \"{v}\" is not an impedance; " : $"{inst.Reference} has no value; ")
                                          + "it is a Bead with its default parameters. Set Rdc, L, Rp and Cp from the part's data.");
    }

    /// <summary>
    /// A two-pin passive's value as the source spells it — <c>10k</c>, <c>4k7</c>, <c>100nF</c>, <c>4u7</c>, <c>2R2</c>,
    /// <c>1.5 nH</c> — read by the bill-of-materials reader, as a number and the unit it is written with; null when it
    /// does not read as this kind's dimension.
    /// <para>A value stated with its rating — <c>100nF/10V</c>, <c>10k/1%</c> — is the first <c>/</c>-separated part that
    /// reads as this kind's dimension: the rating is never taken for the value, since it has another dimension.</para>
    /// </summary>
    public static (string Number, string Unit)? ReadValue(string? text, SymbolKind kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (Read(t) is { } whole) return whole;
        if (!t.Contains('/')) return null;
        foreach (string part in t.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Read(part) is { } value) return value;
        return null;

        (string, string)? Read(string part)
        {
            if (!BomTablePaste.TryReadValue(part, kind, false, out double si, out var dim)
                && !BomTablePaste.TryReadValue(part.Replace(" ", ""), kind, false, out si, out dim)) return null;
            if (dim != DimensionOf(kind) || !double.IsFinite(si) || si < 0) return null;
            string spelled = PartsTable.ValueText(si, PartKindOf(kind));
            int sp = spelled.LastIndexOf(' ');
            return (spelled[..sp], spelled[(sp + 1)..]);
        }
    }

    /// <summary>A symbol cell's name from the source symbol's name: what <see cref="NameValidator"/> refuses, replaced.</summary>
    public static string CellNameFor(string symbolName)
    {
        var chars = symbolName.Trim().Select(c => c <= 0x1F || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or ' ' ? '_' : c);
        string s = new string([.. chars]).TrimEnd('.', ' ');
        if (s.Length == 0) s = "part";
        return NameValidator.IsValid(s) ? s : "_" + s;
    }

    /// <summary>An identifier for a variable: letters, digits and underscores, never starting with a digit.</summary>
    internal static string Identifier(string text)
    {
        var s = new string([.. text.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
        return s.Length == 0 || char.IsAsciiDigit(s[0]) ? "_" + s : s;
    }

    private static (string Number, string Unit) DefaultFor(SymbolKind kind, bool notFitted) => (kind, notFitted) switch
    {
        (SymbolKind.Resistor, true)  => ("1", "GOhm"),
        (SymbolKind.Capacitor, true) => ("0.01", "pF"),
        (SymbolKind.Inductor, true)  => ("1", "H"),
        (SymbolKind.Resistor, _)     => ("1", "kOhm"),
        (SymbolKind.Capacitor, _)    => ("1", "nF"),
        _                            => ("1", "nH"),
    };

    private static UnitDimension DimensionOf(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor => UnitDimension.Resistance,
        SymbolKind.Capacitor => UnitDimension.Capacitance,
        _ => UnitDimension.Inductance,
    };

    private static PartKind PartKindOf(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor => PartKind.R,
        SymbolKind.Capacitor => PartKind.C,
        _ => PartKind.L,
    };

    private static string Noun(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor => "a resistance",
        SymbolKind.Capacitor => "a capacitance",
        _ => "an inductance",
    };

    internal static string Invariant(double v) => v.ToString("0.############", CultureInfo.InvariantCulture);
}
