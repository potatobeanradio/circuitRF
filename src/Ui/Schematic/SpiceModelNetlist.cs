using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// What a <see cref="SymbolKind.SpiceModel"/> instance actually emits into the netlist.
/// Exactly one of <see cref="CellName"/> and <see cref="PrimitiveReference"/> is set.
/// </summary>
/// <param name="CellName">
/// The library cell the instance references, for a <c>.subckt</c> — and for the one <c>.model</c>
/// case that needs a circuit around it rather than a bare device (see
/// <see cref="SpiceModelNetlist.Build"/>).
/// </param>
/// <param name="PrimitiveReference">The engine component reference, for an ordinary <c>.model</c> card.</param>
/// <param name="Overrides">Parameters to put on the emitted instance.</param>
/// <param name="Ports">The terminal order the emitted thing binds nets in.</param>
/// <param name="Notes">Decisions worth reporting — never warnings, never empty by accident.</param>
/// <param name="Refusal">Why nothing can be emitted. Null when something can.</param>
public sealed record SpiceModelEmission(
    string?                             CellName,
    string?                             PrimitiveReference,
    IReadOnlyList<ParameterAssignment>  Overrides,
    IReadOnlyList<string>               Ports,
    IReadOnlyList<string>               Notes,
    string?                             Refusal);

/// <summary>
/// Turns a peeked SPICE definition into the design-layer cells and instance the extractor emits.
///
/// <para><b>Why this exists beside <see cref="SubcircuitCellBuilder"/> rather than reusing it.</b>
/// That one writes a cell FOLDER — a <c>.csch</c> the user can open, edit and re-symbol — which is
/// the import gesture's whole deliverable. A placed SpiceModel has no folder and wants none: what
/// it needs is the same translation expressed directly as <see cref="Cell"/> objects, put in the
/// extraction's own library and never written anywhere. Both read the SAME
/// <see cref="SubcircuitTranslation"/>, so the two gestures cannot classify one file differently.</para>
///
/// <para><b>The cells go in under the SPICE file's own names.</b> That is the rule the kit path
/// already follows (<c>NetExtractor.TryEmitNetlistBackedCellInstance</c>) and for the same reason:
/// a subcircuit's calls name their targets, so renaming the targets would mean rewriting every call
/// inside every definition. A name the design itself already uses is never replaced — the design's
/// own cell wins, and the caller reports the collision rather than discovering it as a wrong
/// answer.</para>
/// </summary>
public static class SpiceModelNetlist
{
    /// <summary>
    /// Builds everything one instance needs, adding any cells to <paramref name="library"/>.
    ///
    /// <para><paramref name="claimedCells"/> records which cell names THIS extraction created from
    /// a SPICE file, and which file each came from, so a second instance pointed at the same file
    /// reuses them and a second instance pointed at a DIFFERENT file that happens to define the
    /// same name is reported instead of silently binding to the first one's circuit.</para>
    /// </summary>
    public static SpiceModelEmission Build(
        SpiceModelDefinition definition,
        SpiceCellScan        scan,
        string               sourcePath,
        Library              library,
        Dictionary<string, string> claimedCells)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(claimedCells);

        if (definition.Refusal is { } refusal)
            return Refused(refusal);

        return definition.Candidate.Subcircuit is { } sub
            ? BuildSubcircuit(sub, scan, sourcePath, library, claimedCells)
            : BuildCard(definition, sourcePath, library, claimedCells);
    }

    private static SpiceModelEmission Refused(string why)
        => new(null, null, [], [], [], why);

    // ─────────────────────────────────────────────────────────────────────────
    //  .subckt
    // ─────────────────────────────────────────────────────────────────────────

    private static SpiceModelEmission BuildSubcircuit(
        SubcircuitTranslation sub,
        SpiceCellScan         scan,
        string                sourcePath,
        Library               library,
        Dictionary<string, string> claimedCells)
    {
        var byName = scan.Subcircuits.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var notes  = new List<string>();

        // Dependencies are already transitive and leaf-first, so adding them in order and the
        // definition last means every call resolves against a cell that is already there.
        foreach (var depName in sub.Dependencies)
        {
            if (!byName.TryGetValue(depName, out var dep))
                return Refused($"'{sub.Name}' calls '{depName}', which this file does not define.");
            if (AddCell(dep, sourcePath, library, claimedCells, notes) is { } why) return Refused(why);
        }

        if (AddCell(sub, sourcePath, library, claimedCells, notes) is { } topWhy) return Refused(topWhy);

        return new SpiceModelEmission(
            CellName:           sub.Name,
            PrimitiveReference: null,
            Overrides:          [],
            Ports:              [.. sub.Definition.Ports],
            Notes:              notes,
            Refusal:            null);
    }

    /// <summary>Adds one translated definition to the library. Returns a refusal, or null.</summary>
    private static string? AddCell(
        SubcircuitTranslation sub, string sourcePath, Library library,
        Dictionary<string, string> claimedCells, List<string> notes)
    {
        if (claimedCells.TryGetValue(sub.Name, out string? owner))
        {
            if (string.Equals(owner, sourcePath, StringComparison.OrdinalIgnoreCase)) return null;
            return $"'{sub.Name}' is defined by two different SPICE files in this design "
                 + $"({owner} and {sourcePath}). Binding one instance to the other's circuit would "
                 + "be a wrong answer that simulates, so neither is used — rename one definition.";
        }

        if (library.Find(sub.Name) is not null)
        {
            // The design's own cell of that name was already built. Same rule the kit path keeps:
            // a design's cell is never replaced. Reported, because it is not what the user asked for.
            return $"'{sub.Name}' is already a cell in this design, so the SPICE file's definition "
                 + "of that name cannot also be used. Rename one of the two.";
        }

        var cell = new Cell(sub.Name);
        cell.Ports.AddRange(sub.Definition.Ports);
        foreach (var d in sub.Definition.Parameters) cell.Parameters.Add(d);
        foreach (var v in sub.Definition.Variables)  cell.Variables.Add(v);

        foreach (var el in sub.Elements)
        {
            // Already screened: a refused element refuses the whole definition, which the peek
            // reports before anything reaches here. Belt and braces — an emitted cell with a
            // missing element is a DIFFERENT circuit that simulates.
            if (el.Refusal is { } why) return $"'{sub.Name}': {why}";

            string reference = el.SubcircuitName
                ?? (el.Symbol is { } kind
                        ? ComponentTypeRegistry.EngineReference(kind)
                        : el.Reference);

            cell.Instances.Add(new Instance(el.InstanceName, reference, el.Nets, Assignments(el.Parameters)));

            foreach (var n in el.Notes)    notes.Add($"{sub.Name}.{el.InstanceName}: {n}");
            foreach (var u in el.Unmapped) notes.Add(
                $"{sub.Name}.{el.InstanceName}: '{u}' is stated by the model card and is NOT carried "
                + "into circuitRF's model.");
        }

        library.Cells.Add(cell);
        claimedCells[sub.Name] = sourcePath;
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  .model
    // ─────────────────────────────────────────────────────────────────────────

    private static SpiceModelEmission BuildCard(
        SpiceModelDefinition definition, string sourcePath, Library library,
        Dictionary<string, string> claimedCells)
    {
        if (definition.Candidate.Card?.Binding is not { } binding || definition.DeviceSymbol is not { } kind)
            return Refused($"'{definition.Name}' is not a model card circuitRF can run.");

        var notes = new List<string>();
        foreach (var n in binding.Notes) notes.Add($"{definition.Name}: {n}");
        foreach (var u in binding.Unmapped)
            notes.Add($"{definition.Name}: '{u}' is stated by the card and is NOT carried into "
                    + "circuitRF's model.");

        var overrides = binding.Parameters
            .Select(p => Assignment(ModelCardCellBuilder.ImportedParameter(kind, p.Name, p.Expression)))
            .ToList();

        // A MESFET card's RD/RS are REAL SERIES RESISTORS — circuitRF's MESFET has no parameter for
        // them, unlike the diode's Rs and the BJT's Rb/Re/Rc, which the elaborator mints internal
        // nodes for. Dropping them is not an option (they are ohms in the source and drain leads,
        // and a power device's are not small), and there is nowhere on a bare device instance to put
        // them — so this one card family emits a CELL holding the device and its two resistors,
        // which is exactly what importing the same card as a cell already builds.
        bool isMesfet = kind is SymbolKind.FetStatz or SymbolKind.FetCurtice
                             or SymbolKind.PFetStatz or SymbolKind.PFetCurtice;
        var (rd, rs) = isMesfet && definition.Candidate.Card is { } card
            ? SpiceModelCardTranslation.MesfetLeadResistance(card.Card)
            : (null, null);

        if (rd is null && rs is null)
            return new SpiceModelEmission(
                CellName:           null,
                PrimitiveReference: ComponentTypeRegistry.EngineReference(kind),
                Overrides:          overrides,
                Ports:              definition.PortNames,
                Notes:              notes,
                Refusal:            null);

        return BuildLeadResistanceCell(
            definition, kind, overrides, rd, rs, sourcePath, library, claimedCells, notes);
    }

    /// <summary>
    /// The device plus its lead resistors, as one cell. Its ports are the DEVICE's terminals in the
    /// device's own order, so the symbol drawn from the same card binds pin k to port k with nothing
    /// to line up by hand.
    /// </summary>
    private static SpiceModelEmission BuildLeadResistanceCell(
        SpiceModelDefinition definition, SymbolKind kind,
        IReadOnlyList<ParameterAssignment> deviceParams, string? rd, string? rs,
        string sourcePath, Library library, Dictionary<string, string> claimedCells,
        List<string> notes)
    {
        string cellName = CellNameFor(sourcePath, definition.Name);

        if (!claimedCells.TryGetValue(cellName, out string? owner))
        {
            if (library.Find(cellName) is not null)
                return Refused($"'{cellName}' is already a cell in this design.");

            var cell = new Cell(cellName);
            cell.Ports.AddRange(definition.PortNames);

            // Each terminal binds to its own port net, except one carrying a lead resistor, which
            // binds to an internal node the resistor bridges to the port.
            var deviceNets = new List<string>(definition.PortNames.Count);
            var resistors  = new List<Instance>();

            for (int i = 0; i < definition.PortNames.Count; i++)
            {
                string terminal = definition.PortNames[i];
                string? value = terminal switch { "d" => rd, "s" => rs, _ => null };
                if (value is null) { deviceNets.Add(terminal); continue; }

                string inner = $"{terminal}_int";
                deviceNets.Add(inner);
                resistors.Add(new Instance(
                    $"R{terminal.ToUpperInvariant()}", "R", [inner, terminal],
                    // No unit: the card's value arrives from the reader already in base SI.
                    [new ParameterAssignment("R", value)]));
            }

            cell.Instances.Add(new Instance("X1", ComponentTypeRegistry.EngineReference(kind),
                                            deviceNets, deviceParams));
            cell.Instances.AddRange(resistors);

            library.Cells.Add(cell);
            claimedCells[cellName] = sourcePath;

            notes.Add($"{definition.Name}: RD/RS are real series resistors, not model parameters — "
                    + "placed in series with the drain and source leads.");
        }
        else if (!string.Equals(owner, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return Refused($"'{cellName}' was already built from {owner}.");
        }

        return new SpiceModelEmission(
            CellName:           cellName,
            PrimitiveReference: null,
            Overrides:          [],
            Ports:              definition.PortNames,
            Notes:              notes,
            Refusal:            null);
    }

    /// <summary>
    /// The library name for a cell circuitRF mints around a card (as opposed to one the FILE names).
    /// The file stem is in it because a card name is short and generic — <c>nch</c>, <c>d1</c> —
    /// and two files in one design very plausibly both state one.
    /// </summary>
    internal static string CellNameFor(string sourcePath, string definitionName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        return Sanitize(stem.Length > 0 ? $"{stem}_{definitionName}" : definitionName);
    }

    private static string Sanitize(string raw)
        => new([.. raw.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_')]);

    // ─────────────────────────────────────────────────────────────────────────

    private static List<ParameterAssignment> Assignments(IReadOnlyList<EditableParameter> parameters)
        => [.. parameters.Select(Assignment)];

    private static ParameterAssignment Assignment(EditableParameter p)
    {
        string unit = UnitNormalizer.ToEngineUnit(p.Unit);
        return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
    }
}
