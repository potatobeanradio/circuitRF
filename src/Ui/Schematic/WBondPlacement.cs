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

    /// <summary>The parameter that names the design.</summary>
    public const string FileParameter = "File";

    // ── Building the component ────────────────────────────────────────────────

    /// <summary>What went wrong, phrased for the user, or null when a component was built.</summary>
    public sealed record BuildResult(EditableComponent? Component, string? Error);

    /// <summary>
    /// Builds a placeable wBond component for the design at <paramref name="absolutePath"/>.
    ///
    /// <para><b>M1 — a design with no arrays is refused BY NAME, not placed with no pins.</b>
    /// <c>WBondSymbolGenerator.Build</c> returns null for such a design, so the component would have
    /// nothing to wire and nothing to stamp; a silent placeholder in the middle of a schematic is a
    /// worse answer than saying which file and why.</para>
    ///
    /// <para>The stored <c>File</c> value follows §5 question 1 —
    /// <see cref="WBondSymbolProvider.StoredFileValueFor"/>: workspace-relative inside, absolute
    /// outside, and it resolves against the WORKSPACE ROOT, not the schematic's own directory
    /// (R-wbb2-3).</para>
    /// </summary>
    public static BuildResult TryBuild(string absolutePath, string? workspaceRootDir, string instanceName)
    {
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

        SetParameter(comp, FileParameter,
            WBondSymbolProvider.StoredFileValueFor(absolutePath, workspaceRootDir));
        SetParameter(comp, ArraysParameter, WBondSymbolProvider.ArraysKeyOf(design));

        return new BuildResult(comp, null);
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
    /// <param name="File">The stored <c>File</c> value, as written on the component.</param>
    /// <param name="Recorded">The array list the wiring was drawn against.</param>
    /// <param name="Current">The array list the design now declares.</param>
    public sealed record ArrayDrift(string InstanceName, string File, string Recorded, string Current)
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
                ? $"wBond '{InstanceName}': the arrays in \"{File}\" have been REORDERED "
                  + $"({Recorded} → {Current}). Every pin keeps its position while its name moves, so "
                  + "the wires now connect to different arrays. Check the wiring, then update this "
                  + "instance's 'Arrays' parameter to dismiss."
                : $"wBond '{InstanceName}': the array list in \"{File}\" has changed "
                  + $"({Recorded} → {Current}), so its pins have moved. Check the wiring, then update "
                  + "this instance's 'Arrays' parameter to dismiss.";
    }

    /// <summary>
    /// Compares every placed wBond's recorded array list against the design it references.
    ///
    /// <para><b>This is the whole of M2's silent-failure guard, and reporting is the answer rather
    /// than repair.</b> A wBond's pin ORDER is its array order — that is R-wbb2-2's contract with
    /// <c>WBondModel</c>'s stamp — so a reorder genuinely moves pins and there is no re-mapping that
    /// keeps existing wires correct without moving the artwork the user drew. What must not happen
    /// is that it happens quietly.</para>
    ///
    /// <para>An instance with no recorded list (placed before this existed, or hand-authored) is NOT
    /// reported: nothing is known about what it was wired against, and a warning that cannot be
    /// acted on is noise.</para>
    /// </summary>
    public static IReadOnlyList<ArrayDrift> CheckArrayDrift(SchematicEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        List<ArrayDrift>? found = null;
        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.WBond) continue;

            string file     = comp.Parameters.FirstOrDefault(p => p.Name == FileParameter)?.Expression ?? "";
            string recorded = comp.Parameters.FirstOrDefault(p => p.Name == ArraysParameter)?.Expression ?? "";
            if (string.IsNullOrWhiteSpace(recorded)) continue;

            string? abs = WBondSymbolProvider.ResolveFilePath(file, model.SchematicDirectory);
            if (abs is null) continue;

            var loaded = WBondSymbolProvider.Load(abs);
            if (loaded is null) continue;              // unreadable is a different problem, reported elsewhere
            if (string.Equals(loaded.ArraysKey, recorded, StringComparison.Ordinal)) continue;

            (found ??= []).Add(new ArrayDrift(comp.InstanceName, file, recorded, loaded.ArraysKey));
        }
        return (IReadOnlyList<ArrayDrift>?)found ?? [];
    }
}
