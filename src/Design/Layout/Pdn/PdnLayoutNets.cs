// What a pad is CONNECTED TO on a board somebody drew — brief-authored-board-2-net-identity.md.
//
// ── THE RULE, IN ONE LINE (the owner's, 2026-09-20) ────────────────────────────────────────────
//
//     net(pad) = the schematic's own binding for that refdes and that pin, where a schematic
//                resolves; else the net stated on the copper the pad lands on
//
// RE-DERIVED, WITH A STAMPED FALLBACK — DisplayRefDes' own shape, chosen for that record's reason: a
// re-derived value cannot go stale, and the stored one is what a board with no schematic behind it
// needs.
//
// ── NOTHING HERE WRITES `Net`, AND THAT IS THE PART THAT SURPRISES ────────────────────────────
//
// Update Layout does not stamp it. There is no forward annotation, no back annotation and no
// migration. A stamp is a USER'S OWN ACT on a board they drew by hand, exactly as RefDes is for a
// hand-placed instance — and if a schematic later resolves, the schematic wins and the stamp is
// simply not consulted.
//
// ── THE PARTITION IS DrcConnectivity'S, AND THERE IS NO SECOND ONE ────────────────────────────
//
// Regions' own header says it and the reasoning carries verbatim: a board whose island
// structure the DRC and railRF disagree about is a bug neither of them reports. So a stated net
// names its WHOLE connected piece through the partition that already exists, which is what buys the
// whole point of the gesture — naming one trace names the pour, the vias and everything they reach,
// and a user does not annotate 80 shapes.
//
// ── IT IS NOT LVS, AND MUST NOT BE NAMED LIKE ONE (R-ab2-5d) ──────────────────────────────────
//
// PdnDivergence below compares TWO FILES THAT BOTH CLAIM TO DESCRIBE ONE BOARD. It extracts no
// device and compares no design. DrcConnectivity's header already draws this line for its own class
// and naming either of them anything LVS-flavoured invites the assumption that they answer a
// question neither asks.

using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>Which of the three claims a board's net names came from — R-ab2-4d.</summary>
/// <remarks>
/// Flags rather than a choice, because a board can legitimately carry an <c>.ipc</c>, resolve a
/// schematic and have copper somebody stamped, all at once. Reporting one of the three as though it
/// were the only one is the false sentence R-ab2-4b is about, in miniature.
/// </remarks>
[Flags]
public enum PdnNetOrigin
{
    /// <summary>Nothing named a net.</summary>
    None = 0,

    /// <summary>The schematic beside the artwork.</summary>
    Schematic = 1,

    /// <summary>A <c>Net</c> a user stamped on the copper itself.</summary>
    Artwork = 2,

    /// <summary>A companion board netlist.</summary>
    BoardNetlist = 4,
}

/// <summary>
/// One divergence between the board netlist and the artwork — R-ab2-5.
///
/// <para><b>Reported, never resolved, never ranked, never a refusal</b> (R-ab2-5c). The run proceeds
/// on R-ab1-3a's precedence. This is a stale export or a hand edit, which are ordinary mid-design
/// states, and a tool that refused to solve on one would be a tool nobody runs mid-design.</para>
/// </summary>
/// <param name="Refdes">The part both files name.</param>
/// <param name="Pin">The pin, for a net disagreement; null where the whole part moved.</param>
/// <param name="Sentence">What to print.</param>
public sealed record PdnDivergence(string Refdes, string? Pin, string Sentence);

/// <summary>
/// What one schematic says about one placed part: its component's ports, in port order, and the net
/// each of them binds.
/// </summary>
/// <param name="PortNames">The ports' own names, in order. Empty entries are ordinary — a primitive
/// declares none — and an empty LIST means nothing named them at all.</param>
/// <param name="Nets">The nets those ports bind, in the same order. <see cref="Instance.NetBindings"/>
/// verbatim.</param>
/// <param name="Value">What the schematic DRAWS as this component's value — its first drawn
/// parameter, expression and unit, exactly as the label reads. Null where it draws none.</param>
/// <param name="Footprint">The stored <c>Footprint</c> reference (footprint brief 2), never
/// normalised or re-derived.</param>
/// <param name="TypeName">What the schematic draws as this component's TYPE — the cell folder name
/// for a cell reference, the registry name for a built-in.</param>
/// <remarks>
/// <b>The last three are here so the schematic is read ONCE</b> (R-ab3-1a). A bill of materials
/// wants a value, a footprint and a type; the net resolution wants ports and bindings; both come
/// off the same <c>.csch</c> beside the same artwork, and a second reader for the second question
/// is a second answer to "which schematic is this board's" the first time somebody moves one.
/// </remarks>
public sealed record PdnSchematicPart(
    IReadOnlyList<string> PortNames,
    IReadOnlyList<string> Nets,
    string? Value = null,
    string? Footprint = null,
    string? TypeName = null);

/// <summary>
/// The schematic beside a <c>.clay</c>, extracted — R-ab2-1.
/// </summary>
/// <param name="SchematicPath">Where it was found, or null where the cell holds none.</param>
/// <param name="ByInstance">Keyed on <see cref="Instance.InstanceName"/>, which is what
/// <see cref="LayoutInstance.SchematicId"/> holds. Empty whenever this path contributes nothing.</param>
/// <param name="Notes">What went wrong, if anything. <b>Returned, never posted</b>.</param>
public sealed record PdnSchematicNets(
    string? SchematicPath,
    IReadOnlyDictionary<string, PdnSchematicPart> ByInstance,
    IReadOnlyList<string> Notes)
{
    /// <summary>The answer for a board with no schematic behind it.</summary>
    public static readonly PdnSchematicNets None =
        new(null, new Dictionary<string, PdnSchematicPart>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>True once at least one placed part can be looked up here.</summary>
    public bool Any => ByInstance.Count > 0;

    /// <summary>What the schematic says about <paramref name="schematicId"/>, or null.</summary>
    public PdnSchematicPart? For(string? schematicId) =>
        schematicId is { Length: > 0 } id && ByInstance.TryGetValue(id, out var part) ? part : null;

    /// <summary>Every net the schematic named, in a stable order.</summary>
    public IReadOnlyList<string> Nets
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var part in ByInstance.Values)
                foreach (string net in part.Nets)
                    if (net is { Length: > 0 }) set.Add(net);
            return [.. set];
        }
    }

    /// <summary>
    /// The schematic that is this artwork's own sibling, read and extracted.
    /// </summary>
    /// <param name="clayPath">The artwork. <b>The walk starts from here and not from whatever
    /// workspace is open</b> (R-ab2-1a) — the <c>.clay</c> lives in a cell's <c>layout/</c> folder
    /// and its sibling is the same cell's primary schematic. That is the walk <c>RailArtwork</c>
    /// already performs for the technology, and it is the same rule for the same reason: a board
    /// drawn in one workspace answers against the design that drew it, not against whichever
    /// workspace happens to be open.</param>
    /// <remarks>
    /// <b>A <c>.csch</c> reaches this through <see cref="SchematicPersistence"/>, NOT through the
    /// <c>.cnl</c> round trip</b> (R-ab2-1f). That rule — CLAUDE.md's, for <c>check</c> and
    /// <c>explain</c> — is about the ELABORATOR, whose reader disagrees with the schematic's about
    /// bare words. <see cref="NetExtractor"/> takes the edit model directly; it is the front half of
    /// the very round trip in question, and routing it through <c>CnlWriter</c> would be a second
    /// extraction of the thing being extracted.
    ///
    /// <para><b>Conflicts suppress the WHOLE path, not part of it</b> (R-ab2-1e).
    /// <c>ExtractionResult.Conflicts</c> means the extraction disagreed with itself somewhere, and
    /// taking the uncontested half of a contested extraction is how a board ends up half-named with
    /// nothing to say which half.</para>
    /// </remarks>
    public static PdnSchematicNets Resolve(string? clayPath)
    {
        if (clayPath is not { Length: > 0 }) return None;

        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath)) ?? "";
        string cellDir = Path.GetDirectoryName(layoutDir) ?? layoutDir;
        if (!Directory.Exists(cellDir)) return None;

        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        if (primary.ResolvedName is not { Length: > 0 } name) return None;

        string schPath = Path.Combine(
            CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name);
        if (!File.Exists(schPath)) return None;

        SchematicEditModel model;
        try { (model, _, _) = SchematicPersistence.LoadFromFile(schPath); }
        catch (Exception ex)
        {
            return new PdnSchematicNets(schPath, None.ByInstance,
                [$"The schematic beside this artwork, '{Path.GetFileName(schPath)}', did not read: " +
                 $"{ex.Message}. Its pads take whatever net is stated on the copper they land on."]);
        }

        NetExtractor.ExtractionResult extracted;
        // DiskCellResolver, never null — Check.cs' own note says why: a null resolver is how
        // NetExtractor is told the caller is flat, and it then skips every cell instance WITHOUT a
        // conflict note. A hierarchical design would come back clean with its parts silently removed.
        try { extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance); }
        catch (Exception ex)
        {
            return new PdnSchematicNets(schPath, None.ByInstance,
                [$"The schematic beside this artwork, '{Path.GetFileName(schPath)}', did not " +
                 $"extract: {ex.Message}. Its pads take whatever net is stated on the copper they " +
                 "land on."]);
        }

        if (extracted.Conflicts.Count > 0)
        {
            var notes = new List<string>
            {
                $"'{Path.GetFileName(schPath)}' extracted with {extracted.Conflicts.Count} naming " +
                "conflict(s), so no pad on this board took a net from it — the uncontested half of " +
                "a contested extraction would leave the board half-named with nothing to say which " +
                "half. Its pads take whatever net is stated on the copper they land on.",
            };
            notes.AddRange(extracted.Conflicts);
            return new PdnSchematicNets(schPath, None.ByInstance, notes);
        }

        // The EDIT model's own components, by name — where the value, the footprint and the type
        // label live (R-ab3-1e). The extraction's Instance carries overrides and bindings; what a
        // bill of materials wants is what the schematic DRAWS, and that is this side of it.
        var drawn = new Dictionary<string, EditableComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in model.Components)
            if (c.InstanceName is { Length: > 0 } cname) drawn.TryAdd(cname, c);

        var byInstance = new Dictionary<string, PdnSchematicPart>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in extracted.TestBench.Instances)
        {
            if (inst.InstanceName is not { Length: > 0 } who) continue;

            // A CELL's ports are named and a primitive's are not. Both cases are joined by INDEX at
            // the far end (R-ab1-4b), so an empty name list is an answer and not a failure: what
            // matters is that PortNames and Nets are the same length and in the same order.
            var ports = extracted.Library.Find(inst.Reference)?.Ports;
            drawn.TryGetValue(who, out var component);
            byInstance[who] = new PdnSchematicPart(
                ports is { Count: > 0 } p && p.Count == inst.NetBindings.Count ? [.. p] : [],
                inst.NetBindings,
                ValueOf(component),
                component?.Footprint,
                component?.TypeLabelText());
        }

        return new PdnSchematicNets(schPath, byInstance, []);
    }

    /// <summary>
    /// What one component's value LABEL reads — its first drawn parameter, expression and unit, in
    /// the schematic's own spelling (<c>EditableComponent.ToRenderComponent</c>'s own format, minus
    /// the <c>Name =</c> prefix a bill of materials has no column for).
    ///
    /// <para><b>The first DRAWN one, not the first one.</b> A component's drawn parameters are the
    /// ones its author chose to show, and the first of those is the value on every built-in family
    /// — <c>C</c> on a capacitor, <c>R</c> on a resistor. Taking the first of ALL of them would put
    /// a temperature coefficient in the value column of a part somebody typed one on.</para>
    /// </summary>
    private static string? ValueOf(EditableComponent? component)
    {
        if (component is null) return null;
        foreach (var p in component.LabelParameters())
        {
            if (p.Expression is not { Length: > 0 } expression) continue;
            return p.Unit is { Length: > 0 } unit ? $"{expression} {unit}" : expression;
        }
        return null;
    }
}

/// <summary>
/// The two files that both claim to describe one board, compared — R-ab2-5. <b>Not LVS</b>; see this
/// file's header.
/// </summary>
public static class PdnBoardDivergence
{
    /// <summary>
    /// Where the board netlist and the artwork name the same refdes and pin and say different
    /// things.
    /// </summary>
    /// <param name="netlistPads">What the <c>.ipc</c> stated.</param>
    /// <param name="artworkPads">What the artwork measures, INCLUDING the parts the netlist also
    /// names — R-ab1-3a discards those pads and this comparison is the reason they are computed at
    /// all.</param>
    /// <param name="extents">Each artwork pad's own extent, DBU. A position difference smaller than
    /// this is a rounding difference between two coordinate systems and is not worth a line
    /// (R-ab2-5b); where the footprint states no extent there is nothing to compare against and no
    /// line is printed.</param>
    public static IReadOnlyList<PdnDivergence> Compare(
        IReadOnlyList<PlacedPin> netlistPads,
        IReadOnlyList<PlacedPin> artworkPads,
        IReadOnlyDictionary<PlacedPin, long>? extents = null,
        RailRf.RailLengthFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(netlistPads);
        ArgumentNullException.ThrowIfNull(artworkPads);

        if (netlistPads.Count == 0 || artworkPads.Count == 0) return [];

        var byKey = new Dictionary<(string, string), PlacedPin>();
        foreach (var pad in netlistPads)
            if (pad.Refdes is { Length: > 0 } r && pad.Pin is { Length: > 0 } p)
                byKey.TryAdd((r.ToUpperInvariant(), p.ToUpperInvariant()), pad);

        var found = new List<PdnDivergence>();
        var reported = new HashSet<(string, string)>();

        foreach (var art in artworkPads)
        {
            if (art.Refdes is not { Length: > 0 } refdes || art.Pin is not { Length: > 0 } pin) continue;
            var key = (refdes.ToUpperInvariant(), pin.ToUpperInvariant());
            if (!byKey.TryGetValue(key, out var stated)) continue;

            // R-ab2-5a: once per PAIR, not once per pad. An array placement puts several pads on one
            // (refdes, pin) and the netlist states it once; three identical lines say nothing the
            // first did not.
            if (!reported.Add(key)) continue;

            if (art.Net is { Length: > 0 } artNet && stated.Net is { Length: > 0 } statedNet
                && !string.Equals(artNet, statedNet, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new PdnDivergence(refdes, pin,
                    $"{refdes}.{pin} is on '{statedNet}' according to the board netlist and on " +
                    $"'{artNet}' according to the artwork. The run used the board netlist's, which " +
                    "is the statement of record; this is a stale export or a hand edit, not an error."));
            }

            long extent = extents is not null && extents.TryGetValue(art, out long e) ? e : 0;
            if (extent <= 0) continue;

            double dx = art.X - stated.X, dy = art.Y - stated.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= extent) continue;

            var fmt = format ?? RailRf.RailLengthFormat.Dbu;
            found.Add(new PdnDivergence(refdes, pin,
                $"{refdes}.{pin} stands at {fmt.Point(stated.X, stated.Y)} according to the board " +
                $"netlist and at {fmt.Point(art.X, art.Y)} according to the artwork — "
              + $"{fmt.Length((long)Math.Round(d))} apart, which is more than the pad's own "
              + $"{fmt.Length(extent)} extent. The run used the board netlist's."));
        }

        return found;
    }
}

/// <summary>How a board's net names read on a status strip — R-ab2-4d.</summary>
/// <remarks>
/// <b>Said once, here</b>, on <c>PlacedPinSummary</c>'s terms: the window's strip and the verb's banner
/// report the same board, and two spellings of one claim is the divergence nobody notices until they
/// are compared.
/// </remarks>
public static class PdnNetSummary
{
    /// <summary>The sentence, or empty where nothing named a net.</summary>
    public static string Describe(PdnNetOrigin origin)
    {
        var parts = new List<string>(3);
        if (origin.HasFlag(PdnNetOrigin.BoardNetlist)) parts.Add("from the board netlist");
        if (origin.HasFlag(PdnNetOrigin.Schematic))    parts.Add("from the schematic");
        if (origin.HasFlag(PdnNetOrigin.Artwork))      parts.Add("stated on the artwork");

        return parts.Count switch
        {
            0 => "",
            1 => $"nets {parts[0]}",
            2 => $"nets {parts[0]} and {parts[1]}",
            _ => $"nets {parts[0]}, {parts[1]} and {parts[2]}",
        };
    }
}
