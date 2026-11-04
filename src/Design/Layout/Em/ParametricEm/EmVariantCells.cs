// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-10 D3 — the parent's layout shows each instance at its own values. A schematic-driven cell has one saved
// layout; an instance whose VAR or parameter overrides give it different copper is placed as a VARIANT: the cell's saved
// layout run through the planner at the instance's values (SDL-6 D2, so the cell's hand edits are in every variant),
// stored through GeneratedCellStore and named by the SDL-6 geometry key, so instances with the same copper share one
// variant. The cell's own layout is never written. Variants are generated: an edit is made in the cell and reaches every
// variant on the next Update Layout from Schematic of the parent.

using System.Globalization;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>A variant an instance is placed as.</summary>
/// <param name="CellDir">The variant's cell folder, under the workspace's generated-cells folder.</param>
/// <param name="Key">Its geometry key.</param>
/// <param name="At">The values that made it differ from the cell's own, as <c>L90 = 0.0052</c>.</param>
public sealed record EmVariant(string CellDir, string Key, string At);

public static class EmVariantCells
{
    private static readonly AsyncLocal<HeldScope?> _held = new();

    /// <summary>
    /// brief-sdl-10 D5 — within the returned scope a variant is held in memory (<see cref="GeneratedCellOverlay"/>) rather
    /// than written to the workspace, and dropped when the scope ends: what an optimisation's probe of a candidate uses to
    /// plan the parent's layout, which it never saves. Scopes nest; each drops what it held.
    /// </summary>
    public static IDisposable HeldInMemory()
    {
        var scope = new HeldScope(_held.Value);
        _held.Value = scope;
        return scope;
    }

    private sealed class HeldScope(HeldScope? outer) : IDisposable
    {
        private readonly List<string> _dirs = [];
        private readonly Lock _gate = new();

        public void Add(string dir) { lock (_gate) _dirs.Add(dir); }

        public void Dispose()
        {
            lock (_gate) GeneratedCellOverlay.Remove(_dirs);
            _held.Value = outer;
        }
    }

    /// <summary>
    /// What <paramref name="comp"/>, an instance of <paramref name="cellDir"/>, is placed as in its parent's layout: a
    /// variant when its overrides, evaluated in the parent's <paramref name="scope"/>, give the cell other copper; null when
    /// it shows the cell's own layout. <paramref name="note"/> says why an instance with overrides shows the cell's own
    /// layout anyway (a 3D-parametric cell, an override that does not evaluate, a plan that fails).
    /// </summary>
    public static EmVariant? For(EditableComponent comp, string cellDir, string workspaceRootDir, Scope scope, Evaluator evaluator,
                                 out string? note)
    {
        note = null;
        var names = EmCapability.CellValueNames(cellDir).ToHashSet(StringComparer.Ordinal);
        var overrides = comp.Parameters
            .Where(p => names.Contains(p.Name) && !string.IsNullOrWhiteSpace(p.Expression)
                        && p.Name is not (ParametricModelRegistry.ModelParameter or ParametricModelRegistry.SetupParameter
                                          or ParametricModelRegistry.CellParameter))
            .ToList();
        if (overrides.Count == 0) return null;

        var kind = EmComponentKinds.Of(cellDir);
        if (kind.Kind == EmComponentKind.ThreeDParametric)
        {
            note = $"{comp.InstanceName} — shows {Path.GetFileName(cellDir)}'s own layout: a 3D-parametric cell's layout does not " +
                   "follow its variables, so this placement's own values reach its EM view only";
            return null;
        }
        if (kind.Kind != EmComponentKind.SchematicDriven) return null;

        string? emSetup = comp.Parameters.FirstOrDefault(p => p.Name == ParametricModelRegistry.SetupParameter)?.Expression.Trim('"');
        var target = EmCapability.Resolve(cellDir, emSetup is { Length: > 0 } ? emSetup : null, null, out _)
                     ?? (EmCapability.SetupsFor(cellDir).FirstOrDefault() is { } first ? EmCapability.Resolve(cellDir, first, null, out _) : null);
        if (target is null || target.ThreeDOnly) return null;

        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var p in overrides)
        {
            string unit = UnitNormalizer.ToEngineUnit(p.Unit);
            try { values[p.Name] = evaluator.Eval(p.Expression, scope, unit.Length > 0 ? unit : null); }
            catch (Exception e) when (e is ExpressionException or InvalidCastException or InvalidOperationException or ArgumentException)
            {
                note = $"{comp.InstanceName} — shows {target.CellName}'s own layout: its {p.Name} = {p.Expression} does not evaluate " +
                       $"here ({e.Message})";
                return null;
            }
        }

        if (EmEvaluation.Geometry(target, values, out var refusal) is not { } g)
        {
            note = $"{comp.InstanceName} — shows {target.CellName}'s own layout: its values could not be laid out ({refusal})";
            return null;
        }
        if (g.Applied.Count == 0) return null;   // the instance's values are the cell's own

        var held = _held.Value;
        string variant = GeneratedCellStore.GetOrCreateVariant(workspaceRootDir, target.CellName, g.Key, g.Source.View,
                                                               Path.GetDirectoryName(target.ClayPath)!, g.Schematic,
                                                               Path.GetDirectoryName(target.SchematicPath),
                                                               held is null ? GeneratedCellTarget.Disk : GeneratedCellTarget.Memory);
        held?.Add(variant);
        string at = string.Join(", ", g.Applied.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                               .Select(kv => $"{kv.Key} = {Shown(kv.Key, kv.Value, overrides)}"));
        return new EmVariant(variant, g.Key, at);
    }

    /// <summary>A value in the unit its override states, else base SI.</summary>
    private static string Shown(string name, Value v, List<EditableParameter> overrides)
    {
        double x = v.AsReal();
        string unit = overrides.FirstOrDefault(p => p.Name == name)?.Unit ?? "";
        if (unit.Length > 0 && Units.Scale(UnitNormalizer.ToEngineUnit(unit)) is { } scale && scale != 0)
            return (x / scale).ToString("G6", CultureInfo.InvariantCulture) + " " + unit;
        return EmCellCache.Format(v);
    }
}
