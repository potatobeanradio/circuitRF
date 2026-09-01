using System;
using System.Collections.Generic;
using System.IO;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The symbol of a placed <see cref="SymbolKind.SpiceModel"/>, generated from the SPICE file the
/// component's <c>File</c> and <c>Name</c> parameters point at.
///
/// <h3>Why this is another virtual symbol source rather than a variadic built-in</h3>
/// <para>Three mechanisms already produce a component's symbol, and this needs what none of them
/// gives on its own:</para>
/// <list type="bullet">
///   <item><b>A fixed <c>SymbolKind</c></b> draws one glyph; a SpiceModel draws a <i>diode</i> for a
///     diode card and a <i>four-pin box</i> for a four-port subcircuit, and which one is a property
///     of the file rather than of the kind.</item>
///   <item><b>A variadic <c>SymbolKind</c> + <c>PortCount</c></b> (SnP, SDD, ZPort) lets the USER
///     set the count. Here the file already knows it, and the pins carry the subcircuit's own port
///     NAMES, which that route has nowhere to put.</item>
///   <item><b>A <c>CellRef</c> to a cell folder</b> needs a <c>.csym</c> on disk, and writing one
///     makes a second copy of the interface that goes stale the moment the file is edited — the
///     staleness <see cref="WBondSymbolProvider"/> exists to avoid. Importing the file AS a cell is
///     already offered (Copy to Workspace as Cell…); this component is the other choice, and its
///     whole point is that there is no copy.</item>
/// </list>
///
/// <h3>What the reference carries, and what it deliberately does not</h3>
/// <para>The reference is <c>pitch | pinconfig | name | file</c> — every input the symbol depends
/// on, in a form derived from the instance's own parameters on each access
/// (<c>EditableComponent.ExternalSymbolRef</c>) and never written to a file. The FILE'S CONTENT is
/// not in it and cannot be: it is read through <see cref="SpiceModelPeek"/>, which keys its own
/// cache on the file's mtime, so editing the <c>.subckt</c> and returning to the schematic redraws
/// the pins without anything here being invalidated.</para>
///
/// <para>It plugs into the seam <see cref="CellSymbolResolver"/> already has for
/// <see cref="PdkKitRegistry"/> and <see cref="WBondSymbolProvider"/>, checked ahead of the path
/// branch and for the same reason: the reference is not a path and must never be reported as a
/// bad one.</para>
/// </summary>
public static class SpiceModelSymbolProvider
{
    /// <summary>Marks a symbol reference as naming a SPICE file's definition rather than a cell folder.</summary>
    public const string Scheme = "spicemodel://";

    /// <summary>Separator between the reference's fields.</summary>
    private const char Separator = '|';

    /// <summary>The instance parameter naming the file.</summary>
    public const string FileParameter = "File";

    /// <summary>The instance parameter naming which definition IN that file to run.</summary>
    public const string NameParameter = "Name";

    /// <summary>
    /// The parameters the SpiceModel's own dialog panel owns — which file, which definition, and how
    /// the box is drawn. <b>One definition, read by three places</b>: the panel (to keep them out of
    /// the generic rows), the extractor (to keep them out of the netlist), and the row adopter (to
    /// keep them from being mistaken for a subcircuit's declared parameters). They used to be three
    /// hand-maintained lists, which is the shape of bug this repository has already paid for twice.
    /// </summary>
    public static bool IsPanelParameter(string? name)
        => name is FileParameter or NameParameter or "PinConfig" or "Pitch";

    // ── The reference form ────────────────────────────────────────────────────

    /// <summary>
    /// The symbol reference for a SpiceModel carrying these parameters. Never null — a blank file
    /// resolves to the generic two-port, which is what an unconfigured instance is.
    ///
    /// <para><b>The file goes LAST, positionally</b>, and the reference is split with a field limit,
    /// so a path containing the separator cannot be mistaken for one of the leading fields. The
    /// leading three are all drawn from closed vocabularies.</para>
    /// </summary>
    public static string RefFor(string? file, string? name, SnpPinConfig cfg, SnpPitch pitch)
        => Scheme + pitch + Separator + cfg + Separator
                  + (name ?? "").Trim() + Separator + (file ?? "").Trim();

    /// <summary>True when this reference names a SPICE file's definition.</summary>
    public static bool IsSpiceModelRef(string? symbolRef)
        => symbolRef is not null && symbolRef.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The four fields of a reference, or null when it is not one.</summary>
    public static (SnpPitch Pitch, SnpPinConfig Config, string Name, string File)? Parse(string? symbolRef)
    {
        if (!IsSpiceModelRef(symbolRef)) return null;

        var fields = symbolRef![Scheme.Length..].Split(Separator, 4);
        if (fields.Length < 4) return null;

        return (Enum.TryParse<SnpPitch>(fields[0], ignoreCase: true, out var pitch) ? pitch : SnpPitch.Loose,
                Enum.TryParse<SnpPinConfig>(fields[1], ignoreCase: true, out var cfg) ? cfg : SnpPinConfig.Standard,
                fields[2], fields[3]);
    }

    // ── Path resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// The absolute path a stored <c>File</c> value names, or null when it cannot be made absolute.
    ///
    /// <para><b>One rule, shared with SnP</b> — <see cref="SnpPathPolicy.Resolve"/>: absolute passes
    /// through, relative resolves against the WORKSPACE ROOT (which is what
    /// <see cref="SnpPathPolicy.ToStored"/> wrote it against and what makes a design portable), and
    /// the schematic's own directory serves only when there is no workspace at all.</para>
    ///
    /// <para><b>The root is derived by walking up to the nearest <c>.cws</c></b>, not taken from
    /// <c>WorkspaceViewModel.CurrentWorkspaceRoot</c> the way <c>SetSnpFileCommand</c> takes it,
    /// because this is reached from <see cref="CellSymbolResolver"/> and from
    /// <c>NetExtractor</c> — both framework-free, neither holding a view model, and both given only
    /// the schematic's directory. For a document inside the open workspace the two agree; for a
    /// FOREIGN document the walk-up is the one that is right, and it is the same rule the technology
    /// and layout references already follow. Editor and extractor call THIS, so the drawn pins and
    /// the simulated circuit cannot disagree about which file was read.</para>
    /// </summary>
    public static string? ResolvePath(string? file, string? schematicDir)
        => SnpPathPolicy.Resolve(file, WBondSymbolProvider.WorkspaceRootOf(schematicDir), schematicDir);

    // ── The CellSymbolResolver seam ───────────────────────────────────────────

    /// <summary>
    /// Resolves a <c>spicemodel://</c> reference to the three-state result every other symbol source
    /// produces, so the renderer, the hit-test and the extractor need no SpiceModel-specific branch.
    ///
    /// <list type="bullet">
    ///   <item><b>Resolved</b> — no file yet (the generic two-port every freshly placed instance
    ///     shows, which is wirable on purpose), or a definition circuitRF can build.</item>
    ///   <item><b>NotFound</b> — the file does not exist, or holds nothing importable.</item>
    ///   <item><b>PrimaryMissing</b> — the file reads, but the named definition is absent or is one
    ///     circuitRF refuses. The refusal ITSELF is not carried here: a resolution is three states
    ///     wide, and the sentence belongs where there is room to read it — the parameter dialog and
    ///     the extraction report, both of which call <see cref="SpiceModelPeek"/> directly.</item>
    /// </list>
    /// </summary>
    public static CellSymbolResolution Resolve(string symbolRef, string? schematicDir)
    {
        if (Parse(symbolRef) is not { } r) return CellSymbolResolution.NotFoundResult;

        if (r.File.Length == 0)
            return new CellSymbolResolution
                { State = CellSymbolState.Resolved, Symbol = UnconfiguredSymbol(r.Config, r.Pitch) };

        string? path = ResolvePath(r.File, schematicDir);
        if (path is null) return CellSymbolResolution.NotFoundResult;

        var file = SpiceModelPeek.Read(path);
        if (file.Error is not null || file.Definitions.Count == 0)
            return CellSymbolResolution.NotFoundResult;

        var def = SpiceModelPeek.Select(file, r.Name);
        if (def is null || !def.IsSupported || def.PortNames.Count == 0)
            return CellSymbolResolution.PrimaryMissingResult;

        return new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = SymbolFor(def, r.Config, r.Pitch) };
    }

    /// <summary>
    /// The glyph for one resolved definition: the device's own artwork for a <c>.model</c> card, an
    /// N-port box carrying the subcircuit's port names for a <c>.subckt</c>.
    ///
    /// <para><b>A card draws as the DEVICE and not as a box</b> — that is the whole of what the
    /// dynamic symbol buys. A diode card placed as a generic rectangle tells a reader nothing the
    /// instance name did not; the diode glyph tells them which lead is the anode without their
    /// opening anything.</para>
    /// </summary>
    public static Symbol SymbolFor(SpiceModelDefinition definition, SnpPinConfig cfg, SnpPitch pitch)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.DeviceSymbol is { } kind
            ? BuiltInSymbols.Primitives(kind, definition.PortNames.Count)
            : BuiltInSymbols.PrimitivesForNamedPortBox(definition.PortNames, cfg, pitch);
    }

    /// <summary>
    /// What an instance with no file yet draws: the generic two-port the palette tile shows, with
    /// its pins already where a wire can reach them.
    ///
    /// <para><b>Resolved, not NotFound.</b> An unconfigured instance is not a broken reference — it
    /// is one the user has not filled in yet, and it has to be placeable, wirable and movable while
    /// they do. The broken-reference placeholder has no pins at all, so a component drawn as one
    /// cannot be wired up first and pointed at a file second.</para>
    /// </summary>
    public static Symbol UnconfiguredSymbol(SnpPinConfig cfg, SnpPitch pitch)
        => BuiltInSymbols.PrimitivesForNamedPortBox(["1", "2"], cfg, pitch);
}
