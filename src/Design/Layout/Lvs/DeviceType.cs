// What kind of device something is, named once for both sides — brief-lvs-4-schematic-netlist.md
// R-lvs4-5, started here because brief 3 cannot emit an LvsDevice without it.
//
// ── WHAT IS HERE AND WHAT BRIEF 4 STILL OWES ──────────────────────────────────────────────────
//
// HERE: the type, the kinds, the generator map, and OfLayout — the layout side, which is what
// brief 3 needs. Brief 4 adds OfSchematic, completes the generator map against the whole
// PCellRegistry with the build-time completeness test R-lvs4-5c asks for, and folds PartKind and
// the built-in kinds in behind it.
//
// THE MAP IS NOT COPIED. LayoutToSchematicGenerator's ReverseGeneratorMap was the seed and it now
// CALLS this one rather than keeping its own: two maps with one meaning drift, which is this
// repository's recurring scar (three copies of a version number). R-lvs4-5d's deletion is that,
// done early, because doing it late would have meant writing the second copy first.

using System.Linq;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// What a device IS, at the granularity the comparison matches on.
/// </summary>
/// <remarks>
/// <b>Deliberately coarse.</b> Four namespaces name a device type in this repository today
/// (<see cref="SymbolKind"/>, a <c>CellRef</c>, a <c>PCellOrigin.GeneratorId</c> and a
/// <c>PartKind</c>), and the comparison needs exactly one question answered — <i>could these two
/// be the same part?</i> A kind finer than that turns a correct design into a type mismatch; a
/// kind coarser than that lets a resistor match a capacitor.
/// </remarks>
public enum DeviceKind
{
    /// <summary>Nothing said. <b>Two Unknowns still match</b> — the kind is a veto, not evidence.</summary>
    Unknown,

    /// <summary>A cell instance, matched by its resolved directory rather than by a kind.</summary>
    Cell,

    Resistor,
    Capacitor,
    Inductor,

    /// <summary>Every microstrip element — a line, a bend, a tee, a cross, a taper. They are one
    /// kind here because the artwork does not distinguish them either: what tells a tee from a
    /// cross is its terminal COUNT, which the comparison already has.</summary>
    TransmissionLine,

    /// <summary>A FET, a HEMT, a bipolar — anything with a control terminal.</summary>
    Transistor,

    Diode,

    /// <summary>A source, a port, a termination — the fixture. Excluded from the default
    /// comparison (R-lvs4-4c) and named so the exclusion can be stated rather than guessed.</summary>
    Fixture,
}

/// <summary>
/// The canonical type of one device — <b>the same function answers for a schematic component and
/// for a layout placement</b> (R-lvs4-5a).
/// </summary>
/// <param name="Kind">The coarse kind. A veto, not evidence: see <see cref="DeviceKind"/>.</param>
/// <param name="CellDir">The resolved cell folder, absolute, where the device IS a cell. <b>The
/// strongest identity there is</b> (R-lvs4-5b) — an absolute path is unambiguous and needs no name
/// matching, which is what makes a user-authored PDK part, an imported component and a hand-drawn
/// cell all work identically with nothing registered anywhere.</param>
/// <param name="Name">What to CALL it in a report. Never matched on.</param>
public readonly record struct DeviceType(DeviceKind Kind, string? CellDir, string? Name)
{
    /// <summary>Nothing is known about this device's type.</summary>
    public static readonly DeviceType Unknown = new(DeviceKind.Unknown, null, null);

    /// <summary>
    /// Whether two devices could be the same part — R-lvs4-5e's veto, asked in one place.
    /// </summary>
    /// <remarks>
    /// <b>A cell directory beats a kind.</b> Where both sides resolve one, that path IS the
    /// answer; where neither does, the kinds decide, and <see cref="DeviceKind.Unknown"/> matches
    /// anything because "nothing said" is not evidence of difference.
    /// </remarks>
    public bool CouldBe(DeviceType other)
    {
        if (CellDir is { Length: > 0 } mine && other.CellDir is { Length: > 0 } theirs)
            return string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);

        return Kind == DeviceKind.Unknown || other.Kind == DeviceKind.Unknown || Kind == other.Kind;
    }
}

/// <summary>Where a device's canonical type is decided.</summary>
public static class DeviceTypes
{
    /// <summary>
    /// Which <see cref="SymbolKind"/> a PCell generator id produces — the map
    /// <c>LayoutToSchematicGenerator</c> seeded and now calls.
    /// </summary>
    /// <remarks>
    /// Six entries, which is every generator that has a schematic counterpart today. R-lvs4-5c
    /// completes it against <c>PCellRegistry</c> with a test that FAILS THE BUILD on a generator
    /// with no entry, rather than falling back at runtime: a silent fallback here produces two
    /// devices of "unknown" type that then match each other.
    /// </remarks>
    private static readonly Dictionary<string, SymbolKind> GeneratorKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MLIN"]   = SymbolKind.Mlin,
            ["MBEND"]  = SymbolKind.MBend,
            ["MTEE"]   = SymbolKind.MTee,
            ["MCROSS"] = SymbolKind.MCross,
            ["MTAPER"] = SymbolKind.Mtaper,
            ["MKLOPF"] = SymbolKind.Mklopf,
        };

    /// <summary>The <see cref="SymbolKind"/> <paramref name="generatorId"/> produces, if any.</summary>
    public static bool TryGetSymbolKind(string? generatorId, out SymbolKind kind)
    {
        kind = default;
        return generatorId is { Length: > 0 } id && GeneratorKinds.TryGetValue(id, out kind);
    }

    /// <summary>
    /// The canonical type of a layout placement — R-lvs4-5b's precedence, layout side.
    /// </summary>
    /// <param name="inst">The placement.</param>
    /// <param name="resolvedCellDir">Where its <c>CellRef</c> resolved to, or null when it did
    /// not.</param>
    /// <param name="resolvedView">The resolved cell's primary layout, or null — read only for its
    /// <c>PCellOrigin</c>, which is the generator that drew it.</param>
    public static DeviceType OfLayout(LayoutInstance inst, string? resolvedCellDir, LayoutView? resolvedView)
    {
        ArgumentNullException.ThrowIfNull(inst);

        string? name = resolvedCellDir is { Length: > 0 } dir
            ? new System.IO.DirectoryInfo(dir).Name
            : inst.CellRef.Split('/', '\\').LastOrDefault();

        // The generator, then the declared part kind. Both are statements about what the placement
        // IS; the cell directory beside them is where it LIVES, and both travel together because
        // DeviceType.CouldBe reads the directory first and falls back to the kind.
        var kind = DeviceKind.Unknown;

        if (TryGetSymbolKind(resolvedView?.PCellOrigin?.GeneratorId, out var generated))
            kind = Of(generated);
        else if (LayoutPartKind.Of(inst) is { } declared)
            kind = Of(declared);
        else if (resolvedCellDir is { Length: > 0 })
            kind = DeviceKind.Cell;

        return new DeviceType(kind, resolvedCellDir, name);
    }

    /// <summary>
    /// The coarse kind of a <see cref="SymbolKind"/>.
    /// </summary>
    /// <remarks>
    /// <b>A kind this does not name is <see cref="DeviceKind.Unknown"/>, not an error.</b> Unknown
    /// matches anything, so a symbol kind nobody has classified costs a missed TYPE veto and never
    /// a wrong match — where refusing would have made the whole design unreadable. Brief 4 is
    /// where the remaining kinds are named.
    /// </remarks>
    public static DeviceKind Of(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor  => DeviceKind.Resistor,
        SymbolKind.Capacitor => DeviceKind.Capacitor,
        SymbolKind.Inductor  => DeviceKind.Inductor,

        SymbolKind.Mlin or SymbolKind.MBend or SymbolKind.MTee
            or SymbolKind.MCross or SymbolKind.Mtaper or SymbolKind.Mklopf
            => DeviceKind.TransmissionLine,

        SymbolKind.Vdc or SymbolKind.ToneSource or SymbolKind.Term
            or SymbolKind.P1Tone or SymbolKind.ZPort
            => DeviceKind.Fixture,

        _ => DeviceKind.Unknown,
    };
}
