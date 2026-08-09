using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Composes a kit's palette tiles: ONE TILE PER PART, carrying every view that part can be placed as.
///
/// <para><b>A part is not two things because it has two views.</b> A kit's <c>nmos</c> with both a
/// schematic symbol and a layout generator is one component; showing it twice would make a user
/// choose between tiles that mean the same thing, and choose wrongly half the time. So a generator is
/// MERGED onto the part's existing tile and the DROP TARGET decides which view is placed — dropping on
/// a schematic places the symbol, dropping on a layout places the cell. This is how the built-ins have
/// always behaved (one MLIN tile, both canvases); the kit path matches it.</para>
///
/// <para>Framework-free and headless, so the matching rules below are testable directly rather than
/// only through a <c>WorkspaceViewModel</c> that cannot be constructed in a test.</para>
/// </summary>
public static class KitPaletteMerge
{
    /// <summary>
    /// Merges <paramref name="generatorKits"/> (generator id → the kit it came from) into
    /// <paramref name="parts"/> (a kit's schematic parts), returning the tiles to publish.
    ///
    /// <para><b>Matching, and why it is two steps.</b> Same kit and same part id is the confident
    /// case. A name match ACROSS kits is what handles a supplier whose schematic kit and PCell kit
    /// carry different names — but only when exactly one part anywhere has that name, because merging
    /// two different kits' same-named parts would silently give one kit's tile the other's layout,
    /// which draws perfectly and is wrong.</para>
    ///
    /// <para><b>The third step, and the one that makes a kit work.</b> A kit routinely names its
    /// schematic part and its layout cell differently — the symbol is a file, the cell is a class in a
    /// package, and nothing obliges the two to match. What they DO share is the device model each
    /// declares, because that is what a netlist has to say. So a part and a generator that declare the
    /// SAME model are the same component, and matching on it is reading the kit's own statement rather
    /// than guessing. It applies only when exactly one generator claims that model on each side: a kit
    /// whose ordinary and RF variants of one device share a model is genuinely ambiguous, and putting
    /// the wrong one on a design draws perfectly and is wrong.</para>
    ///
    /// <para>A generator matching no part still gets a tile of its own: it is a real, placeable
    /// thing — just layout-only.</para>
    /// </summary>
    /// <param name="generatorModels">
    /// Each generator's own declared model name, when it declares one. Absent (or empty) disables the
    /// model step entirely, which is what keeps every pre-existing caller and test behaving exactly as
    /// before.
    /// </param>
    /// <param name="generatorParameters">
    /// Each generator's own declared parameter names. Enables the FOURTH step (see
    /// <see cref="PairByParameterInterface"/>) and nothing else — absent, every earlier step behaves
    /// exactly as it did.
    /// </param>
    public static IReadOnlyList<PaletteItem> Compose(
        IReadOnlyList<PaletteItem> parts,
        IReadOnlyDictionary<string, string> generatorKits,
        IReadOnlyDictionary<string, string>? generatorModels = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? generatorParameters = null)
    {
        var byKitAndId = new Dictionary<(string Kit, string Part), PaletteItem>();
        var byPartName = new Dictionary<string, List<PaletteItem>>(StringComparer.OrdinalIgnoreCase);
        var byModel    = new Dictionary<string, List<PaletteItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in parts)
        {
            if (item.Pdk is not { } pdk) continue;
            byKitAndId[(pdk.KitName, pdk.PartId)] = item;
            if (!byPartName.TryGetValue(pdk.PartId, out var list))
                byPartName[pdk.PartId] = list = [];
            list.Add(item);

            if (pdk.ModelName is { Length: > 0 } model)
            {
                if (!byModel.TryGetValue(model, out var mlist)) byModel[model] = mlist = [];
                mlist.Add(item);
            }
        }

        // Generators grouped by the model they claim, so "exactly one on each side" is checkable
        // rather than assumed. Built once, not per generator.
        var generatorsByModel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gid, model) in generatorModels ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(model)) continue;
            if (!generatorsByModel.TryGetValue(model, out var gl)) generatorsByModel[model] = gl = [];
            gl.Add(gid);
        }

        var attached   = new Dictionary<PaletteItem, string>();
        var layoutOnly = new List<PaletteItem>();

        var ordered = generatorKits.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();

        // PASS ONE — the three rules that read an identity: same kit and id, a unique name anywhere,
        // a model exactly one of each claims.
        //
        // Run to completion BEFORE the fourth, and that ordering is load-bearing rather than tidy.
        // The fourth reasons over a whole model GROUP at once, so it would otherwise hand a cell to a
        // part that step one was about to claim by id — and the part step one meant it for would end
        // up with nothing while the artwork looked perfectly plausible. Caught by a test written to
        // pin the precedence, not by reading the code.
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (generatorId, kitName) in ordered)
        {
            PaletteItem? part = byKitAndId.GetValueOrDefault((kitName, generatorId));
            if (part is null && byPartName.TryGetValue(generatorId, out var candidates) && candidates.Count == 1)
                part = candidates[0];

            if (part is null
                && (generatorModels?.TryGetValue(generatorId, out var genModel) ?? false)
                && !string.IsNullOrWhiteSpace(genModel)
                && generatorsByModel.TryGetValue(genModel, out var claimants) && claimants.Count == 1
                && byModel.TryGetValue(genModel, out var modelParts) && modelParts.Count == 1)
                part = modelParts[0];

            if (part is null || attached.ContainsKey(part)) continue;
            attached[part] = generatorId;
            settled.Add(generatorId);
        }

        // PASS TWO — the fourth rule, over only what pass one left, and then the leftovers.
        var byInterface = PairByParameterInterface(
            byModel, generatorsByModel, generatorParameters, attached.Keys, settled);

        foreach (var (generatorId, kitName) in ordered)
        {
            if (settled.Contains(generatorId)) continue;

            var part = byInterface.GetValueOrDefault(generatorId);

            if (part is not null && !attached.ContainsKey(part))
            {
                attached[part] = generatorId;
                continue;
            }

            if (part is null)
                layoutOnly.Add(new PaletteItem(
                    Kind:             SymbolKind.Generic,
                    PortCount:        0,
                    DisplayName:      generatorId,
                    Category:         ComponentCategory.Other,
                    SearchTerms:      [generatorId, kitName],
                    IsCommon:         false,
                    ExtraCategories:  null,
                    // Filed under the KIT's own name — the same heading its schematic parts use, so a
                    // user browsing a kit finds everything that kit offers in one place.
                    Pdk:              new PdkPartRef(kitName, generatorId),
                    PCellGeneratorId: generatorId));
        }

        return
        [
            .. parts.Select(i => attached.TryGetValue(i, out var gen) ? i with { PCellGeneratorId = gen } : i),
            .. layoutOnly,
        ];
    }

    /// <summary>
    /// The fourth step: several parts and several layout cells all naming ONE model.
    ///
    /// <para><b>This is the ordinary shape of a device a kit offers twice</b> — an RF form and a plain
    /// one — and the model step above has to refuse it, because "exactly one on each side" is what
    /// makes that step a reading rather than a guess. Measured, all four of its MOS
    /// devices land here: two parts and two cells per model, four models, eight parts with no
    /// artwork.</para>
    ///
    /// <para><b>The kit does state which is which, in what each side ACCEPTS.</b> The RF part carries a
    /// parameter the plain one does not, and so does the RF cell. And this is not a similarity score:
    /// a cell that does not declare a parameter the part HAS cannot be that part's layout view at all,
    /// because the artwork could never follow the schematic — the value would have nowhere to land.
    /// So coverage is a hard requirement, and the pairing is what that requirement leaves.</para>
    ///
    /// <para><b>Propagated, not scored.</b> Coverage alone settles the RF part (only the RF cell takes
    /// its extra parameter) but not the plain one (both cells accept everything it has). Assigning the
    /// forced one first and re-asking is what settles the rest — and it converges or stops, so a group
    /// it cannot decide is left for the palette rather than guessed at. Nothing here reads a NAME:
    /// pairing "rf" against "rf" would be inventing a convention the kit never stated, and the kit
    /// that spells it the other way round would silently get its artwork swapped.</para>
    /// </summary>
    /// <returns>Generator id → the part it draws, for the pairs this step could settle. Empty when no
    /// parameter declarations were supplied, which leaves every earlier step exactly as it was.</returns>
    /// <param name="alreadyAttached">Parts an earlier rule has claimed; never re-offered here.</param>
    /// <param name="usedGenerators">Cells an earlier rule has spent; never re-offered here.</param>
    private static Dictionary<string, PaletteItem> PairByParameterInterface(
        Dictionary<string, List<PaletteItem>> partsByModel,
        Dictionary<string, List<string>> generatorsByModel,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? generatorParameters,
        IEnumerable<PaletteItem> alreadyAttached,
        IReadOnlySet<string> usedGenerators)
    {
        var paired = new Dictionary<string, PaletteItem>(StringComparer.OrdinalIgnoreCase);
        if (generatorParameters is null || generatorParameters.Count == 0) return paired;

        var taken = new HashSet<PaletteItem>(alreadyAttached);

        foreach (var (model, generators) in generatorsByModel)
        {
            if (!partsByModel.TryGetValue(model, out var modelParts)) continue;
            // Exactly one on each side is the model step's own case, already settled above.
            if (generators.Count == 1 && modelParts.Count == 1) continue;

            var freeParts = modelParts.Where(p => !taken.Contains(p)).ToList();
            var freeGens  = generators.Where(g => !usedGenerators.Contains(g)).ToList();

            bool progress = true;
            while (progress && freeParts.Count > 0 && freeGens.Count > 0)
            {
                progress = false;

                foreach (var part in freeParts.ToList())
                {
                    var wanted = PartParameterNames(part);
                    if (wanted.Count == 0) continue;   // states nothing to pair on

                    var fits = freeGens
                        .Where(g => generatorParameters.TryGetValue(g, out var declared)
                                    && wanted.IsSubsetOf(declared))
                        .ToList();

                    if (fits.Count != 1) continue;

                    paired[fits[0]] = part;
                    freeGens.Remove(fits[0]);
                    freeParts.Remove(part);
                    progress = true;
                }
            }
        }

        return paired;
    }

    /// <summary>The parameters a part declares, as the KIT names them. circuitRF's own
    /// <c>ModelLibrary</c> row is excluded because no kit has ever heard of it, so requiring a cell to
    /// declare it would rule out every cell for every part.</summary>
    private static HashSet<string> PartParameterNames(PaletteItem part)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in part.Pdk?.ParameterNames ?? [])
            if (!string.IsNullOrWhiteSpace(n) &&
                !n.Equals(PdkPartInstaller.ModelLibraryParameter, StringComparison.Ordinal))
                names.Add(n);
        return names;
    }
}
