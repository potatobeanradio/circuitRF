// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-7 R-sdl7-8 — Apply. Until applied, an optimisation's best values exist only in its result; the schematic's
// VAR block and its saved layout still hold the starting geometry. Apply writes each best value where its name is
// defined (the VAR row's expression, spelled as Push spells it, so the design keeps its own structure), runs Update
// Layout from Schematic (the planner, SDL-4's rules, so hand edits are kept), and checks that the saved layout's EM
// cache key is the key of the EM-verified best point: the layout on disk is exactly the geometry that was solved. A
// mismatch is reported, never ignored.
//
// This is the headless half — the CLI's and MCP's --apply. The GUI's write-back is Push through each document's own
// session (one undo step per document), then the same Update Layout and the same check (KeyOfLayout).

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Optimization;

/// <summary>What Apply did.</summary>
/// <param name="Written">The value keys written into the schematic.</param>
/// <param name="NotWritten">Keys with no VAR row to write to, each with why.</param>
/// <param name="LayoutKey">The saved layout's geometry key afterwards; null when the layout was not updated.</param>
/// <param name="ExpectedKey">The best point's key, as the run reported it; null when it reported none.</param>
/// <param name="Refusal">Why nothing (or not everything) was done; null when it was.</param>
/// <param name="LayoutLines">Update Layout's warnings — a loop that does not close at the applied values, say.</param>
public sealed record EmApplyResult(
    IReadOnlyList<string> Written, IReadOnlyList<string> NotWritten, string? LayoutKey, string? ExpectedKey, string? Refusal,
    IReadOnlyList<string> LayoutLines)
{
    /// <summary>Whether the saved layout is the geometry that was solved.</summary>
    public bool KeyMatches => Refusal is null && LayoutKey is not null &&
                              (ExpectedKey is null || ExpectedKey.Split('+').Contains(LayoutKey, StringComparer.Ordinal));

    /// <summary>One sentence for a status line or the CLI.</summary>
    public string Sentence => Refusal is { } why ? why
        : $"Applied {Written.Count} value{(Written.Count == 1 ? "" : "s")} and updated the layout; " +
          (KeyMatches ? $"the saved layout is the geometry EM verified ({LayoutKey})."
                      : $"the saved layout's geometry {LayoutKey} is NOT the one EM verified ({ExpectedKey}): what is on disk was " +
                        "not solved. Re-run the optimisation from these values, or simulate the analysis by EM to solve it.");
}

public static class EmApply
{
    /// <summary>
    /// <paramref name="values"/> (value key → value text, as a run's best values) written into
    /// <paramref name="model"/>'s VAR rows — the number in the row, the unit in its column, as Push writes them. Returns
    /// the keys written; a value equal to the row's is not rewritten, and a key with no VAR row is in
    /// <paramref name="notWritten"/>.
    /// </summary>
    public static IReadOnlyList<string> WriteValues(SchematicEditModel model, IReadOnlyDictionary<string, string> values,
                                                   out List<string> notWritten)
    {
        notWritten = [];
        var written = new List<string>();
        var rows = model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters).ToList();
        foreach (var (key, text) in values)
        {
            var row = rows.FirstOrDefault(p => p.Name.Trim() == key);
            if (row is null) { notWritten.Add($"{key}: no VAR row defines it in this schematic"); continue; }
            if (!TunableValue.TryParse(text, out double number, out string unit, out _))
            {
                notWritten.Add($"{key}: '{text}' is not a value");
                continue;
            }
            string expr = number.ToString("G15", CultureInfo.InvariantCulture);
            string rowUnit = UnitFor(row, unit);
            if (row.Expression.Trim() == expr && row.Unit == rowUnit) continue;
            row.Expression = expr;
            row.Unit = rowUnit;
            written.Add(key);
        }
        return written;
    }

    /// <summary>The row's own unit spelling when it means the same unit, else the value's — Push's rule.</summary>
    private static string UnitFor(EditableParameter row, string unit)
    {
        if (row.Unit.Length > 0 && UnitNormalizer.ToEngineUnit(row.Unit) == unit) return row.Unit;
        return row.Unit.Contains('Ω') && unit.EndsWith("Ohm", StringComparison.Ordinal) ? unit[..^3] + "Ω" : unit;
    }

    /// <summary>
    /// Apply on disk: <paramref name="values"/> into <paramref name="schematicPath"/>'s VAR block, Update Layout from
    /// Schematic on its cell's saved layout, and the key check against <paramref name="expectedKey"/>. Every other byte of
    /// the <c>.csch</c> is what its own persistence writes for it.
    /// </summary>
    public static EmApplyResult Apply(string schematicPath, IReadOnlyDictionary<string, string> values, string? expectedKey,
                                      string? emSetup = null)
    {
        string full = Path.GetFullPath(schematicPath);
        if (EmCapability.CellDirOfSchematic(full) is not { } cellDir)
            return Refused($"{Path.GetFileName(full)} is not a cell's schematic, so it has no layout to update.");
        if (EmCapability.Resolve(cellDir, emSetup, Path.GetDirectoryName(full), out var notCapable) is null)
            return Refused(notCapable ?? "the cell has no layout and EM setup to update.");

        var (model, view, cellName) = SchematicPersistence.LoadFromFile(full);
        var written = WriteValues(model, values, out var notWritten);
        if (written.Count > 0) SchematicPersistence.SaveToFile(full, model, cellName, view.PanX, view.PanY, view.Zoom);

        var (layoutKey, lines, refusal) = UpdateLayout(cellDir, emSetup, Path.GetDirectoryName(full));
        return new EmApplyResult(written, notWritten, layoutKey, expectedKey, refusal, lines);
    }

    /// <summary>
    /// Update Layout from Schematic, headless, on <paramref name="cellDir"/>'s saved layout at its schematic's saved
    /// values — the plan applied and the <c>.clay</c> saved — then the saved layout's key.
    /// </summary>
    public static (string? Key, IReadOnlyList<string> Lines, string? Refusal) UpdateLayout(string cellDir, string? emSetup, string? baseDir)
    {
        if (EmCapability.Resolve(cellDir, emSetup, baseDir, out var why) is not { } target) return (null, [], why);
        try
        {
            var model = SchematicPersistence.LoadFromFile(target.SchematicPath).model;
            model.SchematicDirectory = Path.GetDirectoryName(target.SchematicPath);
            var layout = LayoutPersistence.LoadFromFile(target.ClayPath);
            var (tech, _) = TechnologyResolver.ResolveForDocument(layout.TechRef, target.ClayPath, target.CwsPath, new TechnologyCache());
            if (tech.Tech is null) return (null, [], $"{target.CellName}'s layout resolves no technology: {string.Join(" ", tech.Diagnostics)}");
            var plan = SchematicLayoutPlanner.Plan(model, layout, model.SchematicDirectory!, target.WorkspaceDir,
                                                   Path.GetDirectoryName(target.ClayPath)!, tech.Tech, tech.ResolvedPath,
                                                   DiskCellResolver.Instance, targetLayoutPath: target.ClayPath, connectParts: true);
            plan.ApplyTo(layout);
            LayoutPersistence.SaveToFile(target.ClayPath, layout);
            var lines = plan.Lines.Where(l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning)
                                  .Select(l => l.Text).ToList();
            return (KeyOfSaved(target), lines, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return (null, [], $"the layout could not be updated: {e.Message}");
        }
    }

    /// <summary>The EM cache key of <paramref name="target"/>'s layout AS SAVED — read back from disk, not re-planned.</summary>
    public static string KeyOfSaved(EmCellTarget target)
    {
        var saved = LayoutPersistence.LoadFromFile(target.ClayPath);
        var (tech, _) = TechnologyResolver.ResolveForDocument(saved.TechRef, target.ClayPath, target.CwsPath, new TechnologyCache());
        return KeyOfLayout(target, saved, tech.Tech);
    }

    /// <summary>The EM cache key of <paramref name="layout"/> (an open layout, saved or not) as <paramref name="target"/>'s
    /// setup would solve it — the GUI's check after its own Update Layout.</summary>
    public static string KeyOfLayout(EmCellTarget target, LayoutView layout, Technology? tech) =>
        EmCellCache.KeyOf(new CircuitRF.Design.Layout.Em.EmLayoutSource(target.ClayPath, layout, tech, layout.DbuPerMicron), target.Setup);

    private static EmApplyResult Refused(string why) => new([], [], null, null, why, []);
}
