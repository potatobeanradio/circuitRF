// THE ASSEMBLY — brief-lvs-13-assemblies.md, docs/design/lvs.md §4.6 and §4.8.
//
//   package/board .clay                     ← the extraction root
//     ├─ instance: die A (.clay, its OWN .ctech)   ─► sub-cell (brief 9), boundary = its bond pads
//     ├─ instance: die B                           ─► sub-cell
//     └─ Assembly.wBond  (beside the root .clay)   ─► wires; feet land on A's, B's and the root's
//
// ── WHY THIS FILE EXISTS AT ALL ───────────────────────────────────────────────────────────────
//
// Everything above except the last line is brief 9 and needs nothing new (R-lvs13-1b): a die is an
// ordinary sub-cell whose bond pads are its boundary pins, and two technologies are reconciled at
// the boundary by the flatten, which already refuses a pending mapping rather than guessing
// (R-lvs13-2c). What has no precedent in either reading is the wBond: one schematic component with
// 2M(+1) pins against a `.wBond` SIDECAR holding polylines in nanometres, with no net on a wire, no
// pad binding on a foot and no LayoutPin anywhere in the picture.
//
// ── IT READS THE WIRES THE ENGINE WOULD READ, WHICH IS NOT ALWAYS A LAYOUT FILE ───────────────
//
// R-lvs13-5b. A placed wBond's `Source` chooses Carried (the `Design` payload on the component) or
// Linked (the `.wBond` the `File` parameter names), and the ENGINE simulates whichever it says —
// `NetExtractor` acts on that parameter and this file makes the same choice by asking the same
// function. Verifying the other one verifies a design nobody runs.
//
// That is why a schematic-side object reaches a layout-side read here, and it is NOT the thing
// `LayoutRead`'s own header forbids. What crosses is GEOMETRY — a polyline someone drew, which
// happens to be stored on a component — and it becomes a net by the same point-in-piece lookup
// every pad uses. No schematic NET NAME, net number or binding reaches any net on this path.
//
// ── THE CONVERSION IS NAMED AT THE CALL SITE, AND THE TEST IS NOT AT THE DEFAULT ──────────────
//
// R-lvs13-3b. A `Wire`'s points are `Point3` in NANOMETRES; the layout is DBU. At the shipped
// 1000 DBU/µm the two numbers coincide, so a wrong conversion — or none at all — is invisible on
// every default-configured design. That is a recorded scar (WB-C), not a hypothetical, which is why
// `LayoutUnits.NmToDbu` is spelled out below rather than folded into a helper, and why the fixture
// uses a non-default resolution and coordinates that are not round micron values.

using System.Linq;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.WBond;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// One placed wBond, with the wires the engine would simulate already chosen and read.
/// </summary>
/// <param name="Path">The instance, as the designer named it — the identity on both sides.</param>
/// <param name="Design">The wires. Never null and never empty of arrays: an instance that has
/// neither contributes no device to EITHER netlist, because <c>NetExtractor</c> skips it too.</param>
/// <param name="HasReferencePin">The instance's own <c>RefPin</c>, which is what decides whether the
/// component has 2M or 2M+1 terminals. Read from the same parameter the symbol generator and the
/// extractor read, so the three cannot disagree about the port count.</param>
/// <param name="Drifted">R-lvs13-4c. Its array list moved under it, so it is reported and NOT
/// compared.</param>
internal sealed record AssemblyWBond(
    string Path, WBondDesign Design, bool HasReferencePin, bool Drifted);

/// <summary>The wBond half of an assembly comparison — R-lvs13-3, R-lvs13-4, R-lvs13-5.</summary>
internal static class AssemblyRead
{
    /// <summary>The parameter that exposes the reference terminal (wbond.md §5.4).</summary>
    private const string RefPinParameter = "RefPin";

    /// <summary>
    /// Every wBond the schematic places, with its wires resolved — and everything that had to be
    /// said about how they were found.
    /// </summary>
    /// <remarks>
    /// <b>The enumeration is the SCHEMATIC's</b>, because the choice of wire source is per instance
    /// (R-lvs13-5a) and only an instance can make it. A `.wBond` beside the artwork that no
    /// instance names is the ordinary mid-design state of the layout-driven flow (wbond.md §9.5):
    /// the wires exist and the component has not been created yet, which is what
    /// "Update Schematic from wBond Layout" is for.
    /// </remarks>
    /// <param name="model">The schematic, already read.</param>
    /// <param name="cschPath">Where it was read from — what a stored <c>File</c> is relative to.</param>
    /// <param name="clayPath">The root artwork — what the cell's own <c>.wBond</c> is found beside.</param>
    public static (IReadOnlyList<AssemblyWBond> WBonds, IReadOnlyList<Diagnostic> Notes) Resolve(
        SchematicEditModel model, string cschPath, string clayPath)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The base a Linked `File` resolves against, derived HERE rather than taken off the model:
        // this runs BEFORE `SchematicRead`, which is where `SchematicDirectory` is otherwise filled
        // in, and a null one would silently answer "nothing linked" and compare the carried wires
        // instead — the one substitution R-lvs13-5b forbids. Same rule SchematicRead applies, said
        // in the place that needs it first.
        string? schematicDir = model.SchematicDirectory
            ?? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cschPath));

        var notes = new List<Diagnostic>();
        var found = new List<AssemblyWBond>();

        // The cell's own wires, resolved by the ONE rule that pairs a `.wBond` to a `.clay` —
        // shared stem, then the legacy cell-root sidecar (WBondCell, whose header says why). A
        // second "same folder, same stem" here would be the same rule twice.
        string? sidecar = WBondCell.FindFor(clayPath);

        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.WBond) continue;
            if (SchematicExclusions.IsExcluded(comp)) continue;

            var source = WBondPlacement.SourceOf(comp);
            string? linked = source == WBondPlacement.WireSource.Linked
                ? WBondPlacement.ResolveLinkedPath(comp, schematicDir)
                : null;

            // The fallback `NetExtractor` already takes, mirrored rather than invented: Linked with
            // nothing resolvable simulates the carried payload, so that is what is compared.
            var design = linked is not null ? TryRead(linked) : null;
            string from = design is not null && linked is not null
                ? System.IO.Path.GetFileName(linked)
                : "carried in the schematic";
            string spelling = design is not null
                ? nameof(WBondPlacement.WireSource.Linked)
                : nameof(WBondPlacement.WireSource.Carried);

            string payload = comp.Parameters
                .FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression ?? "";

            if (design is null)
            {
                if (!WBondEmbedding.TryDecode(payload, out var carried) || carried is null) continue;
                design = carried;

                // R-lvs13-5c. The carried copy against the cell's own file — the state §9.6 calls
                // normal and recoverable, which must never be quiet.
                if (sidecar is not null && TryRead(sidecar) is { } onDisk
                    && !string.Equals(WBondEmbedding.Encode(design), WBondEmbedding.Encode(onDisk),
                                      StringComparison.Ordinal))
                    notes.Add(LvsDiagnostics.WBondPayloadDrift(
                        comp.InstanceName, System.IO.Path.GetFileName(sidecar)));
            }

            // A design with no arrays has no pins, and `NetExtractor` emits no instance for it.
            // Emitting one here would put a device on the layout side that the schematic cannot
            // have — an unmatched part whose cause is in neither document.
            if (design.Arrays.Count == 0) continue;

            // R-lvs13-4c, BEFORE anything is compared. Consumed, never re-derived.
            var drift = WBondPlacement.DriftBetween(comp, design, from);
            if (drift is not null)
            {
                notes.Add(LvsDiagnostics.WBondArrayDrift(
                    comp.InstanceName, drift.Recorded, drift.Current));
                found.Add(new AssemblyWBond(comp.InstanceName, design, false, Drifted: true));
                continue;
            }

            notes.Add(LvsDiagnostics.WBondWiresRead(
                comp.InstanceName, spelling, from, design.Arrays.Count, design.WireCount));

            found.Add(new AssemblyWBond(
                comp.InstanceName, design, RefPinOf(comp), Drifted: false));
        }

        return (found, notes);
    }

    /// <summary>
    /// One <see cref="LvsDevice"/> per compared wBond, its terminals resolved from wire FEET —
    /// R-lvs13-3, R-lvs13-4a, R-lvs13-6a.
    /// </summary>
    /// <remarks>
    /// <b>Terminal order is array order and terminal NAMES are array names</b>, which is exactly
    /// what <c>WBondModel.TerminalNames</c> gives the schematic side (<c>G1.i</c>, <c>G1.o</c>, …,
    /// then <c>REF</c>). The two sides therefore agree by construction on the same design — and
    /// where they would not, the instance already drifted and is not here (R-lvs13-4b/4c).
    ///
    /// <para><b>A foot declares no layer, so the lookup does not ask for one.</b> A wire endpoint
    /// carries a z HEIGHT, which is not a drawing layer and cannot be turned into one without a
    /// stackup model this brief explicitly does not build. Any-layer is the honest question for a
    /// bond foot, and it is the only place in the extraction that asks it.</para>
    /// </remarks>
    /// <param name="wbonds">What <see cref="Resolve"/> found.</param>
    /// <param name="pieces">The root's partition — a die's INTERNALS are already out of it
    /// (R-lvs9-2d), so a foot on a die's bond pad lands on the pad that IS its boundary pin and
    /// reaches whatever net that pin reaches inside the die (R-lvs13-3d).</param>
    /// <param name="dbuPerMicron">The ROOT layout's resolution — the one the partition is in.</param>
    public static void Emit(
        IReadOnlyList<AssemblyWBond> wbonds, CopperPieces pieces, int dbuPerMicron,
        string document, LayoutRead.NetTable nets, List<LvsDevice> devices,
        List<LvsPadGeometry> padGeometry, List<Diagnostic> notes, LvsGeometryNaming naming)
    {
        foreach (var wbond in wbonds)
        {
            // R-lvs13-4c. Reported already; comparing its nets would produce 2M real findings about
            // the wrong thing, so the device is not emitted on EITHER side (SchematicRead is given
            // the same exclusion).
            if (wbond.Drifted) continue;

            int deviceIndex = devices.Count;
            var terminals = new List<LvsTerminal>(2 * wbond.Design.Arrays.Count + 1);
            long anchorX = 0, anchorY = 0;
            bool anchored = false;

            for (int k = 0; k < wbond.Design.Arrays.Count; k++)
            {
                var array = wbond.Design.Arrays[k];

                // R-lvs13-4d. An array with no wires is an ordinary mid-design state: both its
                // terminals are open, the opens name the array, and nothing is an error.
                foreach (bool input in new[] { true, false })
                {
                    var reached = new List<int>();

                    for (int w = 0; w < array.Wires.Count; w++)
                    {
                        var points = array.Wires[w].Points;
                        if (points.Count < 2) continue;
                        var foot = input ? points[0] : points[^1];

                        // ── R-lvs13-3a/3b: nanometres → DBU, HERE, said out loud ───────────────
                        long x = LayoutUnits.NmToDbu(foot.X, dbuPerMicron);
                        long y = LayoutUnits.NmToDbu(foot.Y, dbuPerMicron);

                        if (!anchored) { (anchorX, anchorY, anchored) = (x, y, true); }

                        int index = pieces.IndexAt(x, y, layer: null);
                        int piece = index < 0 ? -1 : pieces.NetOfPiece(index);

                        // Recorded either way, exactly as a pad is: a foot on nothing needs a
                        // marker as much as one that landed.
                        padGeometry.Add(new LvsPadGeometry(
                            deviceIndex, terminals.Count, x, y,
                            index < 0 ? default : pieces.LayerOfPiece(index),
                            LayoutUnits.NmToDbu(array.Wires[w].DiameterNm, dbuPerMicron), index));

                        if (piece < 0)
                        {
                            notes.Add(LvsDiagnostics.WBondFootOnNothing(
                                wbond.Path, array.Name, w + 1, input ? "start" : "end",
                                naming.Format.Point(x, y), x, y));
                            continue;
                        }

                        int net = nets.Of(piece);
                        if (!reached.Contains(net)) reached.Add(net);
                    }

                    // Brief 3's own rule for a terminal that is several pads, reused rather than
                    // restated (R-lvs13-3a): several wires of one array landing on several nets is
                    // either an unbonded wire or a short, and nothing here can tell which.
                    if (reached.Count > 1)
                    {
                        notes.Add(LvsDiagnostics.TerminalSplitAcrossNets(
                            wbond.Path, $"{array.Name}.{(input ? "i" : "o")}", reached.Count));
                        reached.Clear();
                    }

                    int chosen = reached.Count == 1 ? reached[0] : nets.Open();
                    terminals.Add(new LvsTerminal(
                        terminals.Count + 1, $"{array.Name}.{(input ? "i" : "o")}", chosen));
                    nets.Attach(chosen, deviceIndex, terminals.Count - 1);
                }
            }

            // ── R-lvs13-6a: the reference conductor is not optional ────────────────────────────
            //
            // The wBond's z origin IS its ground reference (GroundPlane's own note: the plane is at
            // z = 0 by construction), so on the artwork side the terminal resolves to whatever the
            // STACKUP calls the ground reference — the same answer every ground terminal in the
            // extraction gets, arrived at the same way. With no such conductor it is an ordinary
            // open, which is R-lvs13-6b's die: grounded through bondwires to a package, warned
            // about by `lvs.ground.no-reference-conductor`, and still compared correctly.
            if (wbond.HasReferencePin)
            {
                int net = wbond.Design.GroundPlane.Enabled && pieces.Ground.Nets.Count > 0
                    ? nets.Of(pieces.Ground.Nets.First())
                    : nets.Open();
                terminals.Add(new LvsTerminal(terminals.Count + 1, "REF", net));
                nets.Attach(net, deviceIndex, terminals.Count - 1);
            }

            devices.Add(new LvsDevice(
                wbond.Path, wbond.Path,
                // No coarse kind, exactly as the schematic side types it: a wBond is a BOX whose
                // file names its contents, and DeviceTypes.Of(SymbolKind.WBond) answers Unknown.
                new DeviceType(DeviceKind.Unknown, null, nameof(SymbolKind.WBond)),
                terminals, EmptyParameters,
                new LvsProvenance(document, wbond.Path, anchorX, anchorY))
            {
                // A claim, and brief 7 verifies it like any other (R-lvs7-2b). It is a strong one:
                // these wires were read BECAUSE that instance names them, so if the comparison
                // cannot make the pairing work the anchor contradiction is the finding to see.
                AnchorId = wbond.Path,
            });
        }
    }

    /// <summary>The instance names whose devices are left out of BOTH netlists — R-lvs13-4c.</summary>
    public static IReadOnlySet<string> Excluded(IReadOnlyList<AssemblyWBond> wbonds)
        => wbonds.Where(w => w.Drifted).Select(w => w.Path).ToHashSet(StringComparer.Ordinal);

    private static bool RefPinOf(EditableComponent comp)
        => comp.Parameters.FirstOrDefault(p => p.Name == RefPinParameter)?.Expression
               .Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A <c>.wBond</c> off disk, or null. <b>Never throws</b>: an unreadable bond list is a
    /// reported, repairable state and not a reason to refuse the whole comparison — the same
    /// bargain <c>WBondCell</c> strikes when the layout editor opens one.
    /// </summary>
    private static WBondDesign? TryRead(string path)
    {
        try { return System.IO.File.Exists(path) ? WBondIo.ReadFile(path) : null; }
        catch (Exception) { return null; }
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
