using CircuitRF.WBond;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Placing a <c>.wBond</c> design into a schematic, and keeping a placed one honest about the design
/// it was wired against (wbond.md §9.2 route 2, brief-wbond-wbb2 M2/M3).
///
/// <para>Framework-free (no Avalonia): every decision that can be wrong lives here rather than in a
/// menu handler, so it is testable without a window.</para>
/// </summary>
public static class WBondPlacement
{
    /// <summary>The parameter that records the array list an instance's wiring was drawn against.</summary>
    public const string ArraysParameter = "Arrays";

    /// <summary>The parameter that CARRIES the design (<see cref="WBondEmbedding"/>).</summary>
    public const string DesignParameter = WBondEmbedding.DesignParameter;

    // ── Building the component ────────────────────────────────────────────────

    /// <summary>What went wrong, phrased for the user, or null when a component was built.</summary>
    public sealed record BuildResult(EditableComponent? Component, string? Error);

    /// <summary>
    /// Builds a placeable wBond component carrying the design read from <paramref name="absolutePath"/>.
    ///
    /// <para><b>The wires are EMBEDDED, not referenced</b> — <see cref="WBondEmbedding.Encode"/>, which
    /// deliberately drops any layout artwork the file carried. A schematic component has nowhere to
    /// put artwork, and a reference to a file that mostly holds artwork is what produced the
    /// "Not Found" placeholder on every freshly-placed wBond. Artwork travels by
    /// <c>AddWBondAsCell</c>, which makes it a real layout view.</para>
    ///
    /// <para><b>M1 — a design with no arrays is refused BY NAME, not placed with no pins.</b>
    /// <c>WBondSymbolGenerator.Build</c> returns null for such a design, so the component would have
    /// nothing to wire and nothing to stamp; a silent placeholder in the middle of a schematic is a
    /// worse answer than saying which file and why.</para>
    /// </summary>
    public static BuildResult TryBuild(string absolutePath, string? workspaceRootDir, string instanceName)
    {
        _ = workspaceRootDir;   // nothing is stored as a path any more; kept so callers need no change

        if (string.IsNullOrWhiteSpace(absolutePath))
            return new BuildResult(null, "No wirebond design was named.");

        if (!File.Exists(absolutePath))
            return new BuildResult(null, $"Wirebond design not found: \"{absolutePath}\".");

        WBondDesign design;
        try
        {
            design = WBondIo.ReadFile(absolutePath);
        }
        catch (Exception ex)
        {
            return new BuildResult(null,
                $"\"{Path.GetFileName(absolutePath)}\" could not be read as a wirebond design: {ex.Message}");
        }

        if (design.Arrays.Count == 0)
            return new BuildResult(null,
                $"\"{Path.GetFileName(absolutePath)}\" declares no wire arrays, so it has no pins and " +
                "cannot be placed. Group its wires into at least one array first.");

        return new BuildResult(BuildCarrying(design, instanceName), null);
    }

    /// <summary>
    /// Builds a wBond component carrying <paramref name="design"/>. With no design supplied the
    /// registry's own default (one array, one wire) is what a dropped component arrives with.
    /// </summary>
    public static EditableComponent BuildCarrying(WBondDesign? design, string instanceName)
    {
        var comp = new EditableComponent
        {
            InstanceName = instanceName,
            Symbol       = SymbolKind.WBond,
        };

        var info = ComponentTypeRegistry.Get(SymbolKind.WBond);
        comp.ShowTypeLabel    = info.DefaultShowTypeLabel;
        comp.ShowInstanceName = info.DefaultShowInstanceName;

        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.WBond, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });

        if (design is not null) ApplyDesign(comp, design);
        return comp;
    }

    /// <summary>
    /// Writes a design onto an existing component — the one place an import lands.
    ///
    /// <para><c>Arrays</c> is updated in step, so the recorded identity always describes the payload
    /// that is actually there. Whether the arrays MOVED is the caller's question, asked BEFORE this
    /// runs (<see cref="DriftBetween"/>).</para>
    /// </summary>
    public static void ApplyDesign(EditableComponent comp, WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(comp);
        ArgumentNullException.ThrowIfNull(design);

        SetParameter(comp, DesignParameter, WBondEmbedding.Encode(design));
        SetParameter(comp, ArraysParameter, WBondSymbolProvider.ArraysKeyOf(design));
    }

    private static void SetParameter(EditableComponent comp, string name, string value)
    {
        var p = comp.Parameters.FirstOrDefault(q => q.Name == name);
        if (p is not null) p.Expression = value;
        else comp.Parameters.Add(new EditableParameter { Name = name, Expression = value });
    }

    /// <summary>
    /// A free spot to drop a component into: clear of everything already placed, snapped to the
    /// connection grid. Deliberately crude — a placement gesture that starts from a menu has no
    /// cursor to follow, and landing on top of an existing component would be worse than landing
    /// somewhere the user has to drag it from.
    /// </summary>
    public static (double X, double Y) SuggestPlacementPoint(SchematicEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Components.Count == 0) return (0, 0);

        double maxX = model.Components.Max(c => c.X);
        double minY = model.Components.Min(c => c.Y);
        return (model.SnapToGrid(maxX + 800), model.SnapToGrid(minY));
    }

    // ── §5 question 3: the array list an instance was wired against ───────────

    /// <summary>
    /// One placed wBond whose referenced design's array list has changed since the instance was
    /// placed (or since the user last acknowledged a change).
    /// </summary>
    /// <param name="InstanceName">The placed component, so the user can find it.</param>
    /// <param name="Source">What the new arrays came from, for the message — usually a file name.</param>
    /// <param name="Recorded">The array list the wiring was drawn against.</param>
    /// <param name="Current">The array list the incoming design declares.</param>
    public sealed record ArrayDrift(string InstanceName, string Source, string Recorded, string Current)
    {
        /// <summary>
        /// True when the arrays are the same set in a different order — the case that silently
        /// re-points wiring, because every pin keeps its position while its NAME moves to a
        /// different row.
        /// </summary>
        public bool IsReorder =>
            Recorded.Split('|').OrderBy(s => s, StringComparer.Ordinal)
                .SequenceEqual(Current.Split('|').OrderBy(s => s, StringComparer.Ordinal));

        /// <summary>The message, naming the remedy rather than only the problem.</summary>
        public string Message =>
            IsReorder
                ? $"wBond '{InstanceName}': the arrays in \"{Source}\" are REORDERED relative to what "
                  + $"this instance was wired against ({Recorded} → {Current}). Every pin keeps its "
                  + "position while its name moves, so the wires now connect to different arrays. "
                  + "Check the wiring."
                : $"wBond '{InstanceName}': the array list changed on import from \"{Source}\" "
                  + $"({Recorded} → {Current}), so its pins have moved. Check the wiring.";
    }

    /// <summary>
    /// Whether an incoming design's arrays differ from what a placed instance was wired against.
    ///
    /// <para><b>This is the whole of M2's silent-failure guard, and reporting is the answer rather
    /// than repair.</b> A wBond's pin ORDER is its array order — that is R-wbb2-2's contract with
    /// <c>WBondModel</c>'s stamp — so a reorder genuinely moves pins and there is no re-mapping that
    /// keeps existing wires correct without moving the artwork the user drew. What must not happen
    /// is that it happens quietly.</para>
    ///
    /// <para>Now that the design travels INSIDE the component, this is an import-time question rather
    /// than a load-time one: nothing external can change under a placed instance, so the only moment
    /// its pins can move is the moment new wires are imported over them. Call it BEFORE
    /// <see cref="ApplyDesign"/>.</para>
    ///
    /// <para>An instance with no recorded list (hand-authored, or placed before this existed) yields
    /// null: nothing is known about what it was wired against, and a warning that cannot be acted on
    /// is noise.</para>
    /// </summary>
    public static ArrayDrift? DriftBetween(EditableComponent comp, WBondDesign incoming, string source)
    {
        ArgumentNullException.ThrowIfNull(comp);
        ArgumentNullException.ThrowIfNull(incoming);

        string recorded = comp.Parameters.FirstOrDefault(p => p.Name == ArraysParameter)?.Expression ?? "";
        if (string.IsNullOrWhiteSpace(recorded)) return null;

        string current = WBondSymbolProvider.ArraysKeyOf(incoming);
        if (string.Equals(current, recorded, StringComparison.Ordinal)) return null;

        return new ArrayDrift(comp.InstanceName, source, recorded, current);
    }
}
