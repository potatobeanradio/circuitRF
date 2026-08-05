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
    /// <para>A generator matching no part still gets a tile of its own: it is a real, placeable
    /// thing — just layout-only.</para>
    /// </summary>
    public static IReadOnlyList<PaletteItem> Compose(
        IReadOnlyList<PaletteItem> parts,
        IReadOnlyDictionary<string, string> generatorKits)
    {
        var byKitAndId = new Dictionary<(string Kit, string Part), PaletteItem>();
        var byPartName = new Dictionary<string, List<PaletteItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in parts)
        {
            if (item.Pdk is not { } pdk) continue;
            byKitAndId[(pdk.KitName, pdk.PartId)] = item;
            if (!byPartName.TryGetValue(pdk.PartId, out var list))
                byPartName[pdk.PartId] = list = [];
            list.Add(item);
        }

        var attached   = new Dictionary<PaletteItem, string>();
        var layoutOnly = new List<PaletteItem>();

        foreach (var (generatorId, kitName) in generatorKits.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            PaletteItem? part = byKitAndId.GetValueOrDefault((kitName, generatorId));
            if (part is null && byPartName.TryGetValue(generatorId, out var candidates) && candidates.Count == 1)
                part = candidates[0];

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
}
