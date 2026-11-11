using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CircuitRF.WBond;

/// <summary>One row of a conductivity-against-temperature table: σ at <see cref="TempC"/>.</summary>
/// <param name="TempC">Temperature, °C.</param>
/// <param name="Sigma">Conductivity at <paramref name="TempC"/>, S/m.</param>
public readonly record struct SigmaPoint(double TempC, double Sigma);

/// <summary>One row of a thermal-conductivity-against-temperature table: k at <see cref="TempC"/>.</summary>
/// <param name="TempC">Temperature, °C.</param>
/// <param name="K">Thermal conductivity at <paramref name="TempC"/>, W/(m·K).</param>
public readonly record struct KPoint(double TempC, double K);

/// <summary>
/// A bond-wire metal (wbond.md §2.3).
///
/// <para><b>Conductivity is stored at the 20 °C reference and evaluated at the operating
/// temperature</b> — the 85 °C figures quoted in the design note are <i>derived</i>, not a second
/// set of constants (WB4c). Storing them as literals would break <c>T = 20 °C</c> recovery, which
/// is the one way to get this wrong (brief-wbond-wba §0.3 item 6).</para>
///
/// <para><b>A σ(T) table, when the metal carries one, wins over the α₂₀ formula</b> — the same
/// order <c>ThermalProperties.SigmaAt</c> uses for a technology material, so a wire and a 3D thermal
/// run read one conductivity for one metal. The table is piecewise-linear and HELD at its end rows
/// beyond them: a temperature outside it is clamped, never extrapolated and never refused, and
/// <see cref="ClampNote"/> is the sentence that says so.</para>
/// </summary>
/// <param name="Name">Display name, and the key a <see cref="Wire"/> refers to.</param>
/// <param name="Sigma20">Conductivity at 20 °C, S/m.</param>
/// <param name="Alpha20">Temperature coefficient of resistance at 20 °C, 1/K.</param>
/// <param name="DensityKgM3">Mass density, kg/m³ — carried for future bond-strength/mass work.</param>
/// <param name="SigmaVsTemp">Conductivity against temperature, strictly increasing in °C, or null for
/// the α₂₀ formula alone.</param>
public sealed record WireMaterial(string Name, double Sigma20, double Alpha20, double DensityKgM3,
                                  IReadOnlyList<SigmaPoint>? SigmaVsTemp = null)
{
    /// <summary>
    /// Conductivity at <paramref name="tempC"/>: the σ(T) table when stated (clamped to its ends),
    /// else σ(T) = σ₂₀ / (1 + α₂₀·(T − 20)). At 85 °C this is 22–25 % below the 20 °C figure (WB4a).
    /// </summary>
    public double SigmaAt(double tempC)
    {
        if (SigmaVsTemp is not { Count: > 0 } t) return Sigma20 / (1.0 + Alpha20 * (tempC - 20.0));

        if (tempC <= t[0].TempC) return t[0].Sigma;
        if (tempC >= t[^1].TempC) return t[^1].Sigma;

        int i = 0;
        while (t[i + 1].TempC <= tempC) i++;   // at a row, the segment STARTING there: its value exactly
        var a = t[i];
        var b = t[i + 1];
        return a.Sigma + (b.Sigma - a.Sigma) * (tempC - a.TempC) / (b.TempC - a.TempC);
    }

    /// <summary>The temperatures the σ(T) table spans, °C, or null when there is no table (the α₂₀
    /// formula has no range).</summary>
    public (double MinC, double MaxC)? ConductivityRangeC
        => SigmaVsTemp is { Count: > 0 } t ? (t[0].TempC, t[^1].TempC) : null;

    /// <summary>
    /// Null when <paramref name="tempC"/> is inside the σ(T) table (or there is no table); otherwise
    /// the sentence saying the conductivity was held at the table's nearest end, and at what value.
    /// </summary>
    public string? ClampNote(double tempC)
    {
        if (ConductivityRangeC is not var (lo, hi) || tempC >= lo && tempC <= hi) return null;

        double end = tempC < lo ? lo : hi;
        return string.Create(CultureInfo.InvariantCulture,
            $"Temp = {tempC:0.##} °C is outside {Name}'s conductivity table ({lo:0.##} to {hi:0.##} °C), " +
            $"so its conductivity is held at the {end:0.##} °C value, {SigmaAt(end):G4} S/m.");
    }

    // ── Thermal conductivity (brief-wbond-wire-temperature R-wbt-1) ──────────────────────────────────────
    //
    // Read from the same .cmat records and keys the 3D thermal run reads. Not positional, so every caller that
    // builds a metal from σ₂₀/α₂₀ alone is unchanged and states no k — legal everywhere except where a wire's
    // temperature is SOLVED, which refuses by name (ComponentModelFactory.CreateWBondModel).

    /// <summary>Thermal conductivity, W/(m·K): the constant, and the value below a <see cref="ThermalKVsTemp"/>
    /// table when there is one. Null when the metal states none.</summary>
    public double? ThermalK { get; init; }

    /// <summary>k against temperature, strictly increasing in °C, or null for the constant <see cref="ThermalK"/>.</summary>
    public IReadOnlyList<KPoint>? ThermalKVsTemp { get; init; }

    /// <summary>Whether the metal states a thermal conductivity at all.</summary>
    public bool HasThermalK => ThermalK is not null || ThermalKVsTemp is { Count: > 0 };

    /// <summary>
    /// k and dk/dT at <paramref name="tempC"/>: the table when stated — linear inside it, HELD at its bottom row
    /// below it and at its top row above it (above it the state is not physical, which <see cref="BeyondTables"/>
    /// says; the value is still defined so a caller can evaluate before it checks) — else the constant, slope 0.
    /// At a row, the segment STARTING there, as <see cref="SigmaAt"/> reads its table.
    /// </summary>
    public (double K, double Slope) ThermalKAt(double tempC)
    {
        if (ThermalKVsTemp is not { Count: > 0 } t)
            return (ThermalK ?? throw new InvalidOperationException($"Wire material '{Name}' states no thermal conductivity."), 0.0);
        return Table(t.Count, i => t[i].TempC, i => t[i].K, tempC);
    }

    /// <summary>
    /// σ and dσ/dT at <paramref name="tempC"/>, the same σ <see cref="SigmaAt"/> gives: the table's segment slope
    /// (0 where it is held), or the α₂₀ formula's derivative −σ₂₀α₂₀/(1 + α₂₀(T − 20))².
    /// </summary>
    public (double Sigma, double Slope) SigmaWithSlopeAt(double tempC)
    {
        if (SigmaVsTemp is not { Count: > 0 } t)
        {
            double u = 1.0 + Alpha20 * (tempC - 20.0);
            return (Sigma20 / u, -Sigma20 * Alpha20 / (u * u));
        }
        return Table(t.Count, i => t[i].TempC, i => t[i].Sigma, tempC);
    }

    /// <summary>
    /// A table's value and slope, by the rule the 3D thermal run's <c>ThermalProperties.FromTable</c> reads one: held (slope
    /// 0) beyond either end, else the segment whose lower row is at or below T — at the top row, the last segment.
    /// </summary>
    private static (double Value, double Slope) Table(int n, Func<int, double> temp, Func<int, double> value, double tempC)
    {
        if (n == 1 || tempC < temp(0)) return (value(0), 0.0);
        if (tempC > temp(n - 1)) return (value(n - 1), 0.0);
        int i = 0;
        while (i < n - 2 && temp(i + 1) <= tempC) i++;
        double slope = (value(i + 1) - value(i)) / (temp(i + 1) - temp(i));
        return (value(i) + slope * (tempC - temp(i)), slope);
    }

    /// <summary>
    /// Whether <paramref name="tempC"/> lies above the top row of either table this metal states — σ(T) or k(T).
    /// A table is held there, and with σ held a wire's heat stops growing with its temperature, so past a runaway
    /// a second, unphysical branch of steady states appears hotter than the metal melts. Such a state is NOT
    /// physical — the meaning <c>ElectricalConductivity.Beyond</c> gives the 3D run's tables. A formula has no top.
    /// </summary>
    public bool BeyondTables(double tempC)
        => SigmaVsTemp is { Count: > 0 } s && tempC > s[^1].TempC
           || ThermalKVsTemp is { Count: > 0 } k && tempC > k[^1].TempC;
}

/// <summary>
/// The shipped metals and the default operating temperature.
///
/// <para><b>Why the default is well above room temperature (WB4a).</b> A wire that carries current is never at
/// room temperature. The default was 85 °C and is 125 °C from 2026-10-05 — closer to where a power part's bond
/// wires actually run, and a default that is close beats one that is wrong by a quarter.</para>
///
/// <para><b>The RF penalty is about half the DC penalty (WB4b).</b> Deep in the skin regime
/// R_ac ∝ 1/√σ rather than 1/σ, so gold's 22 % DC rise becomes ~10.5 % once the current is confined
/// to a skin. <see cref="InternalImpedance"/> traverses the whole transition, so nothing here needs
/// special-casing — but a user comparing against a room-temperature hand calculation should expect
/// two different numbers depending on where they look.</para>
///
/// <para><b>Every metal here is read from the shipped material library</b>,
/// <c>generic-materials.cmat</c>, linked into this assembly as a resource — so a wire's σ(T) is the
/// table the Materials dialog shows, not a second copy of its numbers. Every conductor in that
/// library (a material stating <c>Sigma20</c>) is a metal a wire may be made of; one stating no
/// <c>Alpha20</c> and no table has a conductivity that does not move with temperature.</para>
/// </summary>
public static class WireMaterials
{
    /// <summary>
    /// The default operating temperature of a bond wire, °C — 125 throughout circuitRF (owner, 2026-10-05): a new
    /// design, the wBond editor and a newly placed component all start here. It was 85 °C (WB4a, 2026-08-07).
    /// </summary>
    public const double DefaultOperatingTempC = 125.0;

    /// <summary>
    /// A solved wire's default output-end temperature, °C (brief-wbond-wire-temperature D4) — a package lead, where the input end
    /// (<see cref="DefaultOperatingTempC"/>) is a die pad. Different from it so the two ends read as independent.
    /// </summary>
    public const double DefaultEndTempC = 85.0;

    /// <summary>
    /// The default before 2026-10-05. Every <c>.wBond</c> and carried payload written until then states it, because
    /// the writer always wrote the operating temperature out and nothing in the application ever set another
    /// value — so a stored 85 is the old default, not a choice, and is read as <see cref="DefaultOperatingTempC"/>
    /// (<see cref="StoredOperatingTempC"/>). A hand-typed 85 in a file is indistinguishable and reads the same way.
    /// </summary>
    public const double PreviousDefaultOperatingTempC = 85.0;

    /// <summary>The operating temperature a FILE's stated value means: absent, or the previous default, is today's
    /// default; anything else is kept.</summary>
    public static double StoredOperatingTempC(double? stated)
        => stated is not { } t || t == PreviousDefaultOperatingTempC ? DefaultOperatingTempC : t;

    private static readonly IReadOnlyList<WireMaterial> _library = LoadLibrary();

    /// <summary>4N gold — the RF packaging norm, and the metal of kernel W's validation set.</summary>
    public static readonly WireMaterial Gold = Shipped("Gold");

    /// <summary>Al-1%Si in practice, so σ runs ~5–8 % below pure aluminium.</summary>
    public static readonly WireMaterial Aluminium = Shipped("Aluminium");

    /// <summary>Bare or Pd-coated; the coating is thin against δ above ~1 GHz.</summary>
    public static readonly WireMaterial Copper = Shipped("Copper");

    public static readonly WireMaterial Silver = Shipped("Silver");

    /// <summary>The default wire metal (WB4a / D7).</summary>
    public static WireMaterial Default => Gold;

    /// <summary>The four bond-wire metals — the list a new design declares.</summary>
    public static IReadOnlyList<WireMaterial> All { get; } =
        [Gold, Aluminium, Copper, Silver];

    /// <summary>Every conductor circuitRF ships, the four bond-wire metals first. A wire may name any
    /// of them, whether or not its design declares it.</summary>
    public static IReadOnlyList<WireMaterial> Library => _library;

    /// <summary>Looks a shipped metal up by name, case-insensitively. Returns null if unknown.</summary>
    public static WireMaterial? ByName(string name) =>
        _library.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The shipped record for a material a FILE stated as plain σ₂₀/α₂₀ — the shape every
    /// <c>.wBond</c> written before σ(T) tables existed carries its metals in. When the name is a
    /// shipped metal and both numbers are its own, the file's copy is the shipped metal and takes its
    /// table; anything else is the file's own metal and is kept exactly as stated.
    /// </summary>
    public static WireMaterial Adopt(WireMaterial stated)
    {
        ArgumentNullException.ThrowIfNull(stated);
        if (stated.SigmaVsTemp is not null) return stated;
        return ByName(stated.Name) is { } shipped
               && shipped.Sigma20 == stated.Sigma20 && shipped.Alpha20 == stated.Alpha20
            ? shipped
            : stated;
    }

    /// <summary>True when <paramref name="m"/>'s σ(T) table is the shipped one for its name — which a
    /// file need not repeat, because <see cref="Adopt"/> restores it.</summary>
    public static bool HasShippedTable(WireMaterial m)
        => m.SigmaVsTemp is not null && ByName(m.Name) is { } s && ReferenceEquals(s.SigmaVsTemp, m.SigmaVsTemp);

    private static WireMaterial Shipped(string name)
        => _library.FirstOrDefault(m => m.Name == name)
           ?? throw new InvalidOperationException($"The shipped material library has no '{name}'.");

    /// <summary>The library's own resource name — see the <c>.csproj</c>'s <c>LogicalName</c>.</summary>
    internal const string LibraryResource = "CircuitRF.WBond.generic-materials.cmat";

    private static List<WireMaterial> LoadLibrary()
    {
        using var stream = typeof(WireMaterials).Assembly.GetManifestResourceStream(LibraryResource)
            ?? throw new InvalidOperationException($"The shipped material library '{LibraryResource}' is not embedded.");
        return [.. ReadLibrary(stream).Conductors];
    }

    /// <summary>
    /// The wire metals a <c>.cmat</c> holds — every record stating <c>Sigma20</c> and no <c>Epsr</c>, with its <c>Alpha20</c>
    /// (0 when unstated) and its σ(T) table — and the names of the records that state no conductivity, so a
    /// caller asked for one of those can say WHY it is not a metal rather than that it does not exist.
    /// Reads the format the Materials editor writes, gzipped or not. Throws <see cref="JsonException"/> or
    /// <see cref="InvalidDataException"/> for a file that is not one.
    /// </summary>
    public static WireMaterialLibrary ReadLibrary(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffered = new MemoryStream();
        stream.CopyTo(buffered);
        buffered.Position = 0;
        Stream json = buffered.Length >= 2 && buffered.GetBuffer()[0] == 0x1f && buffered.GetBuffer()[1] == 0x8b
            ? new System.IO.Compression.GZipStream(buffered, System.IO.Compression.CompressionMode.Decompress)
            : buffered;

        using var doc = JsonDocument.Parse(json);
        if (Prop(doc.RootElement, "Materials") is not { ValueKind: JsonValueKind.Array } list)
            throw new InvalidDataException("it has no Materials list, so it is not a material library.");

        var metals = new List<WireMaterial>();
        var others = new List<string>();
        foreach (var m in list.EnumerateArray())
        {
            if (Prop(m, "Name") is not { ValueKind: JsonValueKind.String } nameElement) continue;
            string name = nameElement.GetString()!;
            if (Prop(m, "Sigma20") is not { ValueKind: JsonValueKind.Number } s20) { others.Add(name); continue; }
            // A record stating a permittivity is a DIELECTRIC with a conductivity — silicon by resistivity (SI-SIG) — not
            // a metal, whatever its σ: the rule the 3D build applies to the same record (Em3dLayoutSolids: no εr and σ > 0
            // is a conductor). Without it the four silicon grades were offered as bond-wire metals.
            if (Prop(m, "Epsr") is { ValueKind: JsonValueKind.Number }) { others.Add(name); continue; }

            List<SigmaPoint>? table = null;
            if (Prop(m, "SigmaVsTemp") is { ValueKind: JsonValueKind.Array } rows)
                table = [.. rows.EnumerateArray()
                    .Where(r => Prop(r, "TempC") is { ValueKind: JsonValueKind.Number } && Prop(r, "Value") is { ValueKind: JsonValueKind.Number })
                    .Select(r => new SigmaPoint(Prop(r, "TempC")!.Value.GetDouble(), Prop(r, "Value")!.Value.GetDouble()))];

            // k and k(T) under the keys the 3D thermal run reads (ThermalMaterials) — R-wbt-1a.
            List<KPoint>? kTable = null;
            if (Prop(m, "ThermalKVsTemp") is { ValueKind: JsonValueKind.Array } kRows)
                kTable = [.. kRows.EnumerateArray()
                    .Where(r => Prop(r, "TempC") is { ValueKind: JsonValueKind.Number } && Prop(r, "Value") is { ValueKind: JsonValueKind.Number })
                    .Select(r => new KPoint(Prop(r, "TempC")!.Value.GetDouble(), Prop(r, "Value")!.Value.GetDouble()))];

            metals.Add(new WireMaterial(
                name,
                s20.GetDouble(),
                Prop(m, "Alpha20") is { ValueKind: JsonValueKind.Number } a ? a.GetDouble() : 0.0,
                Prop(m, "DensityKgM3") is { ValueKind: JsonValueKind.Number } d ? d.GetDouble() : 0.0,
                table is { Count: > 0 } ? table.AsReadOnly() : null)
            {
                ThermalK = Prop(m, "ThermalK") is { ValueKind: JsonValueKind.Number } k ? k.GetDouble() : null,
                ThermalKVsTemp = kTable is { Count: > 0 } ? kTable.AsReadOnly() : null,
            });
        }
        return new WireMaterialLibrary(metals, others);
    }

    /// <summary>A property by name, case-insensitively — the Materials editor's reader is.</summary>
    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }
}

/// <summary>What a <c>.cmat</c> offers a wire: its conductors, and the names of its records that are not
/// conductors (no σ₂₀, or a dielectric that states εr beside its σ₂₀).</summary>
public sealed record WireMaterialLibrary(IReadOnlyList<WireMaterial> Conductors, IReadOnlyList<string> NonConductors)
{
    /// <summary>Reads a <c>.cmat</c> file. See <see cref="WireMaterials.ReadLibrary"/>.</summary>
    public static WireMaterialLibrary ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return WireMaterials.ReadLibrary(stream);
    }
}
