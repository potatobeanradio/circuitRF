// Component import orchestrator (docs/sonnet-briefs/brief-PL1-component-library-import.md §§3-4, 9).
//
// The ONLY piece of the component stack that touches CellFolder / layer reconciliation / Technology /
// Messages — every reader under src/Design/Layout/Interchange stays pure bytes-and-geometry, exactly
// the split PcbImport and PcbReader already draw.
//
// This half lives in src/Ui because a `.csym` is written by SymbolPersistence and a Symbol is built
// from SymbolPrimitive, both of which sit beside the renderer here (R-PL1-16). The readers stay on the
// far side of the firewall in src/Design; the neutral symbol model between them is Core's
// KitSymbolPin/KitSymbolShape, and the conversion is KitTemplateSymbol.BuildFromDrawing, used unchanged.
//
// Import only (§13). There is no writer of any of these formats.

using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

public static class ComponentImport
{
    public sealed record ImportResult(
        bool Cancelled,
        string? CellDir,
        IReadOnlyList<LayerDef> LayersToAdd,
        IReadOnlyList<string> Messages)
    {
        public static ImportResult Nothing(IReadOnlyList<string> messages) => new(true, null, [], messages);
    }

    /// <summary>
    /// R-PL1-17: one symbol-editor local unit is one mil, exactly.
    ///
    /// <para><c>SymbolModel.cs</c> states 100 local units per connection-grid square P, and
    /// <c>DsnSymbolReader.PinGrid</c> is 100. The readers emit mils, so a pin at 100 mil and the same
    /// pin at 2.54 mm both land on local 100, on the connection grid, with no rounding. The scale is not
    /// chosen, fitted or clamped: it is 1.</para>
    /// </summary>
    public const double SymbolScale = 1.0;

    /// <summary>
    /// Writes <paramref name="part"/> as ONE cell folder under <paramref name="parentDir"/>.
    /// </summary>
    /// <param name="resolveLayerMapping">The shared L1g layer-mapping dialog, exactly as
    /// <c>PcbImport</c>/<c>GdsiiImport</c>/<c>DxfImport</c> take it. Returning null aborts the whole
    /// import and creates nothing.</param>
    public static ImportResult Import(
        ComponentPart part,
        string parentDir,
        Technology? destTech,
        int destDbuPerMicron,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null)
    {
        var messages = new List<string>(part.Messages);

        // ── The map, and the numbering both views share ─────────────────────────────────────────
        var padOrder = part.Footprints.FirstOrDefault()?.PadNames ?? [];
        var terminals = ComponentTerminals.Build(part, padOrder);

        // ── Layers ──────────────────────────────────────────────────────────────────────────────
        var allNames = new List<string>();
        var mergedTable = new List<PcbLayerTableEntry>();
        var tableSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var footprint in part.Footprints)
        {
            foreach (var s in footprint.Cell.Shapes)
            {
                allNames.Add(s.LayerName);
                if (s.LandingLayerName is { Length: > 0 } landing) allNames.Add(landing);
            }
            foreach (var p in footprint.Cell.Pins) allNames.Add(p.LayerName);
            foreach (var entry in footprint.LayerTable)
                if (tableSeen.Add(entry.CanonicalName)) mergedTable.Add(entry);
        }
        allNames = [.. allNames.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

        var (sourceLayers, keyByName) = PcbLayerReconciliation.BuildSourceLayers(allNames, mergedTable, destTech);
        foreach (var footprint in part.Footprints)
            foreach (var s in footprint.Cell.Shapes)
                s.Shape.Layer = keyByName.TryGetValue(s.LayerName, out var key) ? key : default;

        var allShapes = part.Footprints.SelectMany(f => f.Cell.Shapes.Select(s => s.Shape)).ToList();
        var rows = LayoutLayerMapping.Propose(allShapes, sourceLayers, destTech);

        // R-PL1-26, as L4b and L4d do: an unmatched row defaults to "Add to technology" rather than
        // Keep-as-unknown.
        rows = [.. rows.Select(r => r.Match == LayerMatchKind.NoMatch
            ? r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.AddToTechnology) }
            : r)];

        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices = null;
        if (rows.Count > 0 && LayoutLayerMapping.RequiresConfirmation(rows) && resolveLayerMapping is not null)
        {
            choices = resolveLayerMapping(rows);
            if (choices is null) return ImportResult.Nothing(messages);
        }
        choices ??= LayoutLayerMapping.BuildChoices(rows);
        if (rows.Count > 0) messages.Add(LayoutLayerMapping.SummarizeMapping(rows, destTech));

        // ── The cell folder ─────────────────────────────────────────────────────────────────────
        //
        // R-PL1-5: named by ImportFolder.UniqueName, the same rule a board import uses — importing the
        // same part twice yields PartName_2 rather than overwriting.
        string cellName = ImportFolder.UniqueName(parentDir, part.Name);
        string cellDir = CellFolder.CreateCellFolder(parentDir, cellName);

        var layersToAdd = new List<LayerDef>();
        var addedKeys = new HashSet<LayerKey>();
        string? primaryLayout = null;

        foreach (var footprint in part.Footprints)
        {
            var reconciled = LayoutFragment.ApplyReconciliation(
                [.. footprint.Cell.Shapes.Select(s => s.Shape)], sourceLayers, choices);
            foreach (var def in reconciled.LayersToAdd)
                if (addedKeys.Add(def.Key)) layersToAdd.Add(def);

            var view = new LayoutView { DbuPerMicron = destDbuPerMicron };
            view.Shapes.AddRange(reconciled.Shapes);
            AddPins(view, footprint, terminals.Terminals, keyByName, choices);

            // R-PL1-25: every density variant is written as a sibling view of ONE cell, with the
            // nominal pattern as the primary. Separate cells would represent them as separate parts.
            string fileName = cellName + footprint.Variant + ".clay";
            LayoutPersistence.SaveToFile(
                Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), fileName), view);
            primaryLayout ??= fileName;
        }

        // ── The symbol ──────────────────────────────────────────────────────────────────────────
        string? primarySymbol = null;
        if (BuildSymbol(part, terminals.Terminals, messages) is { } symbol)
        {
            primarySymbol = cellName + ".csym";
            SymbolPersistence.SaveToFile(
                Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), primarySymbol), symbol);
        }

        // ── The source files, kept ──────────────────────────────────────────────────────────────
        //
        // R-PL1-2: the bytes each view was built from are kept beside the cell, and named in .ccell's
        // ImportedFrom. Nothing resolves through them at runtime — this is provenance, not a live
        // reference.
        var copied = new List<string>();
        foreach (var source in part.SourceFiles)
        {
            try
            {
                string target = Path.Combine(cellDir, Path.GetFileName(source));
                File.Copy(source, target, overwrite: true);
                copied.Add(Path.GetFileName(source));
            }
            catch (IOException ex) { messages.Add($"Could not keep a copy of {Path.GetFileName(source)}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { messages.Add($"Could not keep a copy of {Path.GetFileName(source)}: {ex.Message}"); }
        }

        // ── The .ccell ──────────────────────────────────────────────────────────────────────────
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimarySymbol = primarySymbol;
        ccell.PrimaryLayout = primaryLayout;
        ccell.NumPorts = terminals.Terminals.Count;
        ccell.Parameters = [.. MetadataParameters(part)];
        ccell.ImportedFrom = new CcellImportProvenance
        {
            Source = Path.GetFileName(part.SourceFiles.FirstOrDefault() ?? ""),
            Definition = part.Name,
            ContentHash = ComponentProvenance.HashOf(part, terminals.Terminals),
        };
        CellPersistence.SaveToFile(ccellPath, ccell);

        // ── What came in, and what did not ──────────────────────────────────────────────────────
        Report(part, terminals, copied, messages);

        return new ImportResult(false, cellDir, layersToAdd, messages);
    }

    // ── Pins ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-PL1-8 and R-PL1-15: one <see cref="LayoutPin"/> per terminal that has a pad, in
    /// <b>terminal order</b>, so <c>Pins[i]</c> is the terminal whose <c>PortIndex</c> is
    /// <c>i + 1</c> — the same terminal the symbol pin of that index names.
    /// </summary>
    private static void AddPins(
        LayoutView view,
        ComponentFootprint footprint,
        IReadOnlyList<ComponentTerminal> terminals,
        IReadOnlyDictionary<string, LayerKey> keyByName,
        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices)
    {
        var byPad = new Dictionary<string, PcbImportedPin>(StringComparer.Ordinal);
        foreach (var p in footprint.Cell.Pins)
            if (p.Pin.Name.Length > 0) byPad.TryAdd(p.Pin.Name, p);

        // R-PL1-15: OutwardDeg is the direction AWAY from the land pattern's centroid, snapped to the
        // nearest 90°. Not PcbReader's board-side rule (the pad's own long axis), because a package pad
        // faces out of the body while a board pad is reached from any direction.
        double cx = 0, cy = 0;
        if (footprint.Cell.Pins.Count > 0)
        {
            cx = footprint.Cell.Pins.Average(p => (double)p.Pin.X);
            cy = footprint.Cell.Pins.Average(p => (double)p.Pin.Y);
        }

        foreach (var terminal in terminals)
        {
            if (terminal.PadName is not { Length: > 0 } pad) continue;
            if (!byPad.TryGetValue(pad, out var source)) continue;

            var pin = source.Pin;
            if (ResolveKey(keyByName, choices, source.LayerName) is { } key) pin.Layer = key;
            pin.OutwardDeg = FacingOf(pin.X - cx, pin.Y - cy, pin.OutwardDeg);
            view.Pins.Add(pin);
        }
    }

    /// <summary>The nearest 90° to the direction away from the centroid; the geometry's own answer when
    /// the pad sits ON the centroid and there is no outward direction to read.</summary>
    private static double FacingOf(double dx, double dy, double fallback)
    {
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return fallback;
        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        return LayoutAngle.Normalize(Math.Round(deg / 90.0, MidpointRounding.AwayFromZero) * 90.0);
    }

    private static LayerKey? ResolveKey(
        IReadOnlyDictionary<string, LayerKey> keyByName,
        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices,
        string name)
    {
        if (name.Length == 0 || !keyByName.TryGetValue(name, out var source)) return null;
        if (choices is not null && choices.TryGetValue(source, out var choice)
            && choice.Action == LayoutFragment.LayerReconciliationAction.MapToExisting
            && choice.MapTarget is { } target)
            return target;
        return source;
    }

    // ── The symbol ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The drawn symbol, at scale 1 and with <b>Y negated</b> (R-PL1-18).
    ///
    /// <para>Both halves of this import negate Y, for different reasons. The FOOTPRINT flips because the
    /// board format is +y down and <c>.clay</c> is +y up — <c>PcbUnits.Y</c> does it, before a shape
    /// reaches this file. The SYMBOL flips because the source symbol formats are +y UP and <c>.csym</c>
    /// is +y DOWN (<c>SymbolModel.cs</c>: "+x right, +y down (screen convention)"). The two are
    /// independent, so they are gated independently.</para>
    ///
    /// <para>Arc ANGLES are NOT negated here. <see cref="KitSymbolArc"/> states its angles
    /// counter-clockwise while circuitRF's arc primitive measures them clockwise, and
    /// <c>KitTemplateSymbol.Convert</c> flips both fields — which is already the sign change negating Y
    /// calls for. Flipping them here as well would cancel it out.</para>
    /// </summary>
    private static Symbol? BuildSymbol(
        ComponentPart part, IReadOnlyList<ComponentTerminal> terminals, List<string> messages)
    {
        if (part.Symbol is not { Pins.Count: > 0 } drawing) return null;

        var pins = drawing.Pins.Select(p => new KitSymbolPin(p.Name, p.XMil, -p.YMil)).ToList();
        var shapes = drawing.Shapes.Select(FlipY).ToList();

        var symbol = KitTemplateSymbol.BuildFromDrawing(pins, shapes, SymbolScale);
        if (symbol is null) return null;

        // The numbering comes from the terminal table, never from the declaration order the pins
        // arrived in (R-PL1-10). Both strings are kept: SymbolPin.Name is the symbol's pin name, and
        // the layout pin carries the pad identifier.
        var indexByPin = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in terminals)
            if (t.PinName is { Length: > 0 } name) indexByPin.TryAdd(name, t.PortIndex);

        for (int i = 0; i < symbol.Pins.Count && i < drawing.Pins.Count; i++)
        {
            symbol.Pins[i].Name = drawing.Pins[i].Name;
            if (indexByPin.TryGetValue(drawing.Pins[i].Name, out int index))
                symbol.Pins[i].PortIndex = index;
        }

        // A pin displaced by the connection-grid snap is reported. A pin drawn on a 100-mil grid does
        // not move at all, so any displacement means this symbol is drawn on a finer one and its pins
        // have been rounded onto circuitRF's.
        int moved = 0;
        for (int i = 0; i < symbol.Pins.Count && i < pins.Count; i++)
            if (Math.Abs(symbol.Pins[i].LocalX - pins[i].X) > 0.5 || Math.Abs(symbol.Pins[i].LocalY - pins[i].Y) > 0.5)
                moved++;
        if (moved > 0)
            messages.Add(
                $"{moved} of {pins.Count} symbol pin(s) were not on circuitRF's 100-mil connection grid " +
                "and were snapped onto it. A lead was drawn from each back to where the file put it.");

        return new Symbol(symbol.Primitives, symbol.Pins, terminals.Count);
    }

    private static KitSymbolShape FlipY(KitSymbolShape shape) => shape switch
    {
        KitSymbolLine l => new KitSymbolLine(l.X1, -l.Y1, l.X2, -l.Y2),
        KitSymbolRectangle r => new KitSymbolRectangle(r.X1, -r.Y1, r.X2, -r.Y2, r.Filled),
        KitSymbolPath p => new KitSymbolPath(FlipYs(p.Xy), p.Closed, p.Filled),
        KitSymbolArc a => new KitSymbolArc(a.Cx, -a.Cy, a.Radius, a.StartDeg, a.SweepDeg),
        _ => shape,
    };

    private static List<double> FlipYs(IReadOnlyList<double> xy)
    {
        var pts = new List<double>(xy.Count);
        for (int i = 0; i + 1 < xy.Count; i += 2) { pts.Add(xy[i]); pts.Add(-xy[i + 1]); }
        return pts;
    }

    // ── Metadata ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-PL1-7: the free text the file carries, as read-only cell parameters.
    ///
    /// <para>QUOTED, because a declared parameter's default is an EXPRESSION and is evaluated as one:
    /// a bare URL is a parse error, and a bare part number that parses as a variable reference resolves
    /// to something else again. <c>ShowOnSchematic</c> is false throughout — this is reference text, not
    /// a value to print beside the part.</para>
    /// </summary>
    private static IEnumerable<CcellParameter> MetadataParameters(ComponentPart part)
    {
        foreach (var (name, value) in part.Metadata.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string identifier = SanitizeParameterName(name);
            if (identifier.Length == 0) continue;
            yield return new CcellParameter
            {
                Name = identifier,
                DefaultExpression = $"\"{value.Replace("\"", "'")}\"",
                ShowOnSchematic = false,
                Description = name,
            };
        }
    }

    /// <summary>A property name from a foreign file is free text; a parameter name is an identifier.
    /// The original spelling survives on the parameter's own description, so nothing is lost.</summary>
    internal static string SanitizeParameterName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        string s = sb.ToString().Trim('_');
        if (s.Length > 0 && char.IsDigit(s[0])) s = "_" + s;
        return s;
    }

    // ── Reporting ───────────────────────────────────────────────────────────────────────────────

    private static void Report(
        ComponentPart part, ComponentTerminals.Result terminals, IReadOnlyList<string> copied, List<string> messages)
    {
        int joined = terminals.Terminals.Count(t => t.PadName is not null && t.PinName is not null);
        messages.Add(
            $"Imported {terminals.Terminals.Count:N0} terminal(s) — {joined:N0} joined pin to pad — " +
            $"across {part.Footprints.Count:N0} land pattern(s)" +
            (part.Symbol is null ? " and no symbol." : " and one symbol."));

        // R-PL1-11: both sides in full, joined where they join, and the leftovers named rather than
        // dropped or invented.
        if (terminals.PinsWithNoPad > 0 || terminals.PadsWithNoPin > 0)
            messages.Add(
                $"{terminals.PinsWithNoPad:N0} symbol pin(s) reference no pad and " +
                $"{terminals.PadsWithNoPin:N0} pad(s) are referenced by no symbol pin. Both were imported " +
                "in full — a pin bonded to two pads and a mounting or shield pad both look like this.");

        foreach (var section in part.UnimportedSections)
            messages.Add($"Not imported: section \"{section}\" — this phase reads one section per part.");
        foreach (var variant in part.UnimportedDeviceVariants)
            messages.Add($"Not imported: package variant \"{variant}\" — this phase reads one package variant.");

        if (part.Footprints.Count > 1)
            messages.Add(
                $"{part.Footprints.Count:N0} land patterns were imported as sibling layout views of one " +
                "cell; the nominal one is primary. Open the cell's layout views to switch between them.");

        if (copied.Count > 0)
            messages.Add($"Kept a copy of {string.Join(", ", copied)} in the cell folder.");

        // R-PL1-27 / R-L4d-6: no substrate is invented, and the absence is stated rather than left for
        // an EM run to discover.
        messages.Add(
            "A component file carries no stackup — no permittivity, no thickness, no substrate. Nothing " +
            "was invented in its place: an EM run on this footprint still needs a technology whose " +
            "stackup describes the board it will sit on.");
    }
}

/// <summary>R-PL1-2's content hash over what the import wrote — the terminal table, the land patterns
/// and the metadata — recorded in <c>CcellImportProvenance.ContentHash</c> so a later import can tell an
/// identical definition from a different one that shares a name.</summary>
public static class ComponentProvenance
{
    public static string HashOf(ComponentPart part, IReadOnlyList<ComponentTerminal> terminals)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(part.Name).Append('\n');
        foreach (var t in terminals)
            sb.Append(t.PortIndex).Append('\t').Append(t.PadName).Append('\t').Append(t.PinName).Append('\n');
        foreach (var f in part.Footprints)
            sb.Append(f.Name).Append(f.Variant).Append('\t').Append(f.Cell.Shapes.Count).Append('\n');
        foreach (var (k, v) in part.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v).Append('\n');

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return System.Convert.ToHexString(hash).ToLowerInvariant();
    }
}
