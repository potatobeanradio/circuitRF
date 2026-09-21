// What kind of device something is, named once for both sides — brief-lvs-4-schematic-netlist.md
// R-lvs4-5, started here because brief 3 cannot emit an LvsDevice without it.
//
// ── BOTH SIDES ARE HERE ───────────────────────────────────────────────────────────────────────
//
// Brief 3 landed the type, the kinds, the generator map and OfLayout. Brief 4 adds OfSchematic and
// names the remaining SymbolKinds, so the two sides answer through ONE switch: a layout placement
// declares its kind as a SymbolKind (LayoutPartKind) and a schematic component IS one, which is
// why the classifier takes a SymbolKind and there is no second table to keep in step.
//
// R-lvs4-5c's completeness is a TEST over PCellRegistry's own registration list, in
// tests/Ui.Tests/Lvs/SchematicNetlistTests.cs — it lives there because PCellRegistry is in src/Ui
// and this file is below the firewall. It FAILS on a new generator with no entry here rather than
// letting one fall back at runtime: a silent fallback produces two devices of "unknown" type that
// then match each other.
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
    ///
    /// <para><b><see cref="DeviceKind.Cell"/> is the second thing that means "nothing said", and
    /// leaving it out of that clause vetoed the ordinary board</b> (brief 7, from brief 5's own
    /// fixture). A designer draws a resistor carrying a <c>Footprint</c> and runs Update Layout.
    /// The placement it writes has a <c>SchematicId</c> and NO <c>PartKind</c> — "the schematic
    /// knows" — so <see cref="DeviceTypes.OfLayout"/> reaches its last clause and answers
    /// <c>Cell</c> with the land pattern's directory, while the schematic component answers
    /// <c>Resistor</c> with no directory at all. Neither is <c>Unknown</c>, <c>Cell</c> is not
    /// <c>Resistor</c>, and every part on every ordinary board was type-incompatible with its own
    /// schematic component — the one pairing that must never be refused.</para>
    ///
    /// <para><b>The land pattern is deliberately NOT folded into the type to close that gap.</b>
    /// A board layout's only statement of a value IS its land pattern (brief 5's
    /// <c>R0402-294R</c>), so a part re-pointed at the wrong one is a PROPERTY error — brief 10's
    /// <c>lvs.property.mismatch</c> — and making the footprint part of the identity would turn it
    /// into a type mismatch and lose the value that was actually wrong.</para>
    ///
    /// <para>Where BOTH sides resolve a directory the directory still decides, which is the rule
    /// this method is built on and the reason the change is confined to the asymmetric case.</para>
    /// </remarks>
    public bool CouldBe(DeviceType other)
    {
        if (CellDir is { Length: > 0 } mine && other.CellDir is { Length: > 0 } theirs)
            return string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);

        return SaysNothing(Kind) || SaysNothing(other.Kind) || Kind == other.Kind;
    }

    /// <summary>The kinds that are an absence of a statement rather than a statement.</summary>
    private static bool SaysNothing(DeviceKind kind)
        => kind is DeviceKind.Unknown or DeviceKind.Cell;
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
    /// The canonical type of a schematic component — R-lvs4-5b's precedence, schematic side
    /// (R-lvs4-5a).
    /// </summary>
    /// <param name="comp">The placed component.</param>
    /// <param name="schematicDir">The folder the `.csch` holding it lives in. A <c>CellRef</c> is
    /// relative to that folder, so with no directory nothing resolves and the kind alone answers.</param>
    /// <remarks>
    /// <b>The resolved cell DIRECTORY comes first</b>, and it is resolved through
    /// <c>ExternalCellRef.ResolveCellDir</c> — the same function <c>CellLayoutResolver</c> calls on
    /// the layout side, so the two produce the same spelling of the same folder and an absolute
    /// path can be compared as an identity. Resolving it a second way here would be a path that
    /// differs by a separator or a <c>..</c> and matches nothing, on designs that are correct.
    ///
    /// <para>There is no generator id and no <c>PartKind</c> on this side: the component's
    /// <see cref="SymbolKind"/> IS the declaration those two are trying to recover, so the
    /// precedence collapses to "directory, then kind".</para>
    /// </remarks>
    public static DeviceType OfSchematic(EditableComponent comp, string? schematicDir)
    {
        ArgumentNullException.ThrowIfNull(comp);

        string? cellDir = comp.CellRef is { Length: > 0 } cellRef
            ? CircuitRF.Design.Workspace.ExternalCellRef.ResolveCellDir(cellRef, schematicDir)
            : null;

        string? name = cellDir is { Length: > 0 } dir
            ? new System.IO.DirectoryInfo(dir).Name
            : comp.CellRef is { Length: > 0 } r ? r.Split('/', '\\').LastOrDefault()
            : comp.Symbol.ToString();

        // A cell instance whose kind says nothing more specific is a CELL — the layout side reaches
        // the same answer through the same last clause, which is what makes an imported component,
        // a kit part and a hand-drawn cell all work with nothing registered anywhere.
        var kind = Of(comp.Symbol);
        if (kind == DeviceKind.Unknown && comp.CellRef is { Length: > 0 }) kind = DeviceKind.Cell;

        return new DeviceType(kind, cellDir, name);
    }

    /// <summary>
    /// The coarse kind of a <see cref="SymbolKind"/> — <b>the one classifier both sides use</b>.
    /// </summary>
    /// <remarks>
    /// A layout placement states its kind as a <see cref="SymbolKind"/> too
    /// (<c>LayoutPartKind</c>), so there is no second table and no way for the two to disagree.
    ///
    /// <para><b>A kind this does not name is <see cref="DeviceKind.Unknown"/>, not an error.</b>
    /// Unknown matches anything, so a symbol kind nobody has classified costs a missed TYPE veto
    /// and never a wrong match — where refusing would have made the whole design unreadable. What
    /// is deliberately left Unknown is what has no coarse kind to have: an <c>SnP</c>, an
    /// <c>SDD</c>, a Verilog-A or SPICE model, a <c>Match</c>, a <c>wBond</c>, the composite RLCs
    /// and the system blocks are each a BOX whose contents the file names, and calling a two-pin
    /// <c>Srlc</c> a "Resistor" would veto a correct pairing.</para>
    /// </remarks>
    public static DeviceKind Of(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor  => DeviceKind.Resistor,
        SymbolKind.Capacitor => DeviceKind.Capacitor,
        SymbolKind.Inductor  => DeviceKind.Inductor,

        // Every microstrip element, plus the ideal line: one kind, because the artwork does not
        // distinguish them either — what tells a tee from a cross is its terminal COUNT, which the
        // comparison already has.
        SymbolKind.Mlin or SymbolKind.MBend or SymbolKind.MTee
            or SymbolKind.MCross or SymbolKind.Mtaper or SymbolKind.Mklopf
            or SymbolKind.Tline
            => DeviceKind.TransmissionLine,

        SymbolKind.Diode => DeviceKind.Diode,

        // Anything with a control terminal. The five MESFET laws, the three p-channel ones, the
        // MOS and JFET pairs, the IGBTs, the vertical power MOSFETs and both BJT polarities are
        // one kind here for the reason the microstrip family is: the artwork of a transistor does
        // not say which drain-current law was fitted to it, so a finer kind would turn a correct
        // design into a type mismatch.
        SymbolKind.FetCurtice or SymbolKind.FetCurticeCubic or SymbolKind.FetStatz
            or SymbolKind.FetMaterka or SymbolKind.FetAngelov
            or SymbolKind.PFetCurtice or SymbolKind.PFetStatz or SymbolKind.PFetMaterka
            or SymbolKind.Mos1N or SymbolKind.Mos1P or SymbolKind.Mos3N or SymbolKind.Mos3P
            or SymbolKind.VdmosN or SymbolKind.VdmosP
            or SymbolKind.JfetN or SymbolKind.JfetP
            or SymbolKind.IgbtN or SymbolKind.IgbtP
            or SymbolKind.BjtNpn or SymbolKind.BjtPnp
            => DeviceKind.Transistor,

        // The FIXTURE (R-lvs4-4c): sources, terminations, ports and the tuner family. Excluded
        // from the default comparison and named here so the exclusion can be STATED rather than
        // guessed at by a second list somewhere else.
        SymbolKind.Vdc or SymbolKind.ToneSource or SymbolKind.CurrentToneSource
            or SymbolKind.Term or SymbolKind.TermG or SymbolKind.ZPort
            or SymbolKind.P1Tone or SymbolKind.PnTone
            or SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner
            => DeviceKind.Fixture,

        _ => DeviceKind.Unknown,
    };
}
