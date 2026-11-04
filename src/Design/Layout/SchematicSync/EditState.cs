using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// brief-sdl-4 §2: what a re-run of Update Layout from Schematic makes of each parameter of a linked PCell part, read
/// against the baseline the last run wrote (<see cref="LayoutView.SchematicPCellSnapshots"/>). The layout as it is
/// stays the starting point; only what the SCHEMATIC changed is applied.
///
/// <list type="table">
/// <item><b>Schematic changed, layout did not</b> — the schematic's value is applied, and the change of geometry it
/// makes is propagated (<see cref="DeltaPropagation"/>).</item>
/// <item><b>Layout changed, schematic did not</b> — the layout's value is KEPT, said as Info on every run.</item>
/// <item><b>Both changed</b> — the layout's value is kept, said as a Warning naming both values. The baseline then
/// takes the schematic's new value, so the next run reads the pair as the row above.</item>
/// </list>
///
/// <para>This replaces R-L5-11, under which the schematic's value overwrote an edit made in the layout with a warning.
/// A layout edit is a decision about the board — a line widened to meet a connector's pad — and the schematic is not
/// the place that decision was made.</para>
/// </summary>
public static class EditState
{
    /// <summary>Which side moved since the baseline.</summary>
    public enum ParamState { Same, Schematic, Layout, Conflict }

    /// <summary>One parameter's state, with the three values it was judged on (<paramref name="Baseline"/> null when
    /// the last run recorded none — a layout written before baselines were kept).</summary>
    public sealed record Param(string Name, ParamState State, PCellValue Layout, PCellValue Schematic, PCellValue? Baseline)
    {
        /// <summary>The value the run leaves the part with: the layout's whenever the layout moved.</summary>
        public PCellValue Kept => State is ParamState.Layout or ParamState.Conflict ? Layout : Schematic;
    }

    /// <summary>
    /// Every parameter the schematic and the layout both state, classified. <paramref name="layoutNow"/> is the
    /// parameter set of the cell the placement references now (null: nothing to compare against, and every parameter
    /// is the schematic's). With no baseline for a parameter, a difference is the schematic's — nothing says the layout
    /// ever chose otherwise.
    /// </summary>
    public static IReadOnlyList<Param> Classify(IReadOnlyDictionary<string, PCellValue> schematicNow,
                                                IReadOnlyDictionary<string, PCellValue>? layoutNow,
                                                IReadOnlyDictionary<string, PCellValue>? baseline)
    {
        var result = new List<Param>();
        if (layoutNow is null) return result;
        foreach (var (name, schematic) in schematicNow)
        {
            if (!layoutNow.TryGetValue(name, out var layout)) continue;
            PCellValue? base_ = baseline is not null && baseline.TryGetValue(name, out var b) ? b : (PCellValue?)null;
            bool layoutMoved = base_ is { } lb && !SchematicLayoutPlanner.SameParamValue(lb, layout);
            bool schematicMoved = base_ is not { } sb ? !SchematicLayoutPlanner.SameParamValue(layout, schematic)
                                                      : !SchematicLayoutPlanner.SameParamValue(sb, schematic);
            var state = SchematicLayoutPlanner.SameParamValue(layout, schematic) ? ParamState.Same
                      : layoutMoved && schematicMoved ? ParamState.Conflict
                      : layoutMoved ? ParamState.Layout
                      : ParamState.Schematic;
            result.Add(new Param(name, state, layout, schematic, base_));
        }
        return result;
    }

    /// <summary>The parameter set to generate the part from: the schematic's, with the layout's value wherever the
    /// layout moved (<see cref="Param.Kept"/>). Null when that is the schematic's own set unchanged.</summary>
    public static Dictionary<string, PCellValue>? Merge(IReadOnlyDictionary<string, PCellValue> schematicNow,
                                                       IReadOnlyList<Param> classified)
    {
        Dictionary<string, PCellValue>? merged = null;
        foreach (var p in classified)
            if (p.State is ParamState.Layout or ParamState.Conflict)
                (merged ??= new Dictionary<string, PCellValue>(schematicNow, StringComparer.Ordinal))[p.Name] = p.Layout;
        return merged;
    }

    /// <summary>
    /// A linked part the user deleted from the layout: the last run placed it (its record is in the placement table)
    /// and nothing in the layout carries its schematic id now. It is not put back (§2) until Restore Removed Parts asks.
    /// </summary>
    public static bool WasRemoved(LayoutView target, string schematicId) =>
        target.GeneratedPlacements.TryGetValue(schematicId, out var record)
        && (record.Removed || !target.Instances.Any(i => i.SchematicId == schematicId));
}
