using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Revision;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Plugins;

/// <summary>One placement, asked for. Every input is an argument, so the dialog and <c>plugin run --place</c>
/// (brief 5) call the same function.</summary>
/// <param name="WorkspaceDir">The workspace folder the cell goes in.</param>
/// <param name="Manifest">The plugin that staged it: its name and version are the checkpoint's intent.</param>
/// <param name="Cell">What <c>placed</c> named, files already resolved inside the staging folder.</param>
/// <param name="PluginMessages">The plugin's own messages with <c>placed</c>. An error among them refuses.</param>
/// <param name="CellName">The name to place under — the user's New name. Null takes the plugin's, else the id's tail.</param>
/// <param name="Replace">Write over an existing cell of that name. Without it an existing cell is a question.</param>
public sealed record PluginPlacementRequest(
    string WorkspaceDir,
    PluginManifest Manifest,
    PluginPlacedCell Cell,
    IReadOnlyList<PluginMessage> PluginMessages,
    string? CellName = null,
    bool Replace = false);

/// <summary>How a placement ended.</summary>
public enum PluginPlacementOutcome
{
    Placed,
    /// <summary>A cell of that name exists and the request said neither Replace nor another name — ask.</summary>
    NameTaken,
    /// <summary>Validation, the name, or the checkpoint refused it. Nothing was written.</summary>
    Refused,
}

/// <param name="Refusals">Why it was refused: the plugin's error messages first, then each validator's sentence.</param>
/// <param name="Checkpoint">The restore point taken before writing, or null when the workspace keeps no history.</param>
public sealed record PluginPlacementResult(
    PluginPlacementOutcome Outcome,
    string CellName,
    string? CellDir,
    IReadOnlyList<string> Written,
    RestorePoint? Checkpoint,
    IReadOnlyList<string> Refusals);

/// <summary>
/// Moves a plugin's staged documents into a workspace as a cell (brief-plg-1 §7, <c>plugins.md</c> §6.3, P-5):
/// validate every file with its kind's own validator, settle the name, take a history checkpoint, then write through
/// <see cref="CellCreate"/>. Nothing is written unless every step before the write succeeded, so a plugin cannot
/// leave a half-placed cell, and a placed cell is one restore point away from not existing.
/// </summary>
public static class PluginPlacement
{
    /// <summary>The documents a plugin may place, and where each lands inside the cell folder.</summary>
    private static readonly IReadOnlyDictionary<string, string> Homes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".csch"] = CellFolder.SchematicSubFolder,
        [".csym"] = CellFolder.SymbolSubFolder,
        [".clay"] = CellFolder.LayoutSubFolder,
        [".c3d"]  = CellFolder.ThreeDSubFolder,
        [".cem"]  = "em",
        // A netlist is its schematic's: beside it, where primacy (which counts .csch only) never sees it.
        [".cnl"]  = CellFolder.SchematicSubFolder,
    };

    /// <summary>The name a cell is placed under when the user has not typed one: the plugin's, else the id's tail.</summary>
    public static string SuggestedName(PluginManifest manifest, PluginPlacedCell cell) => cell.Name ?? manifest.IdTail;

    /// <summary>True when <paramref name="name"/> is already taken in <paramref name="workspaceDir"/>.</summary>
    public static bool Exists(string workspaceDir, string name)
        => Directory.Exists(Path.Combine(workspaceDir, name)) || File.Exists(Path.Combine(workspaceDir, name));

    /// <summary>Every reason the staged files cannot be placed, each naming its file; empty when they can.</summary>
    public static IReadOnlyList<string> Validate(PluginPlacedCell cell)
    {
        var why = new List<string>();
        if (cell.Files.Count == 0) why.Add("The plugin placed a cell with no files.");

        foreach (var group in cell.Files.GroupBy(f => Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1)
                why.Add($"The plugin staged {group.Count()} {group.Key} files for one cell; a placed cell has one of each.");

        var cache = new TechnologyCache();
        foreach (string file in cell.Files)
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file);
            if (!Homes.ContainsKey(ext)) { why.Add($"{name}: a {ext} file is not a document circuitRF places in a cell."); continue; }

            if (CellViewFileValidator.ViewTypeFor(file) is { } view)
            {
                if (CellViewFileValidator.DescribeDefect(file, view) is { } defect) why.Add($"{name}: {defect}");
                continue;
            }
            if (ext.Equals(".cnl", StringComparison.OrdinalIgnoreCase)) { if (NetlistDefect(file) is { } d) why.Add($"{name}: {d}"); continue; }
            if (ext.Equals(".cem", StringComparison.OrdinalIgnoreCase)) { if (EmSetupDefect(file, cache) is { } d) why.Add($"{name}: {d}"); }
        }
        return why;
    }

    public static PluginPlacementResult Place(PluginPlacementRequest request)
    {
        var cell = request.Cell;
        string name = (request.CellName ?? SuggestedName(request.Manifest, cell)).Trim();

        // 1. Validate. The plugin's own errors first: it knows best why its output is wrong.
        var refusals = request.PluginMessages.Where(m => m.IsError).Select(m => m.Text).ToList();
        refusals.AddRange(Validate(cell));
        if (NameValidator.Validate(name) is { } badName) refusals.Add($"The cell name '{name}' cannot be used: {badName}");
        if (refusals.Count > 0) return Refused(refusals);

        // 2. The name.
        string cellDir = Path.Combine(request.WorkspaceDir, name);
        bool exists = Exists(request.WorkspaceDir, name);
        if (exists && !request.Replace) return new PluginPlacementResult(PluginPlacementOutcome.NameTaken, name, cellDir, [], null, []);
        if (exists && !Directory.Exists(cellDir)) return Refused([$"{name} is a file in the workspace, not a cell, so it cannot be replaced."]);

        // 3. A restore point before anything is written — skipped, as every boundary is, when there is no history.
        RestorePoint? point = null;
        if (WorkspaceRevisionSetting.Read(WorkspaceRevisionSetting.CwsPathFor(request.WorkspaceDir)) != false
            && GitCommand.For(request.WorkspaceDir) is { } git && git.IsRepositoryRoot())
        {
            var taken = WorkspaceCheckpoints.Take(git, CheckpointOrigin.BeforeBatch,
                $"Place {request.Manifest.Name} {request.Manifest.Version}", attended: false);
            if (taken.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } failed)
                return Refused([$"Nothing was placed: a restore point could not be taken first ({failed.Render()})."]);
            point = taken.Point ?? RestorePoints.Newest(git);
        }

        // 4. Write.
        var written = new List<string>();
        try
        {
            if (!exists) CellCreate.Create(request.WorkspaceDir, name, CellViews.None);

            if (exists)
                foreach (string ext in cell.Files.Select(f => Path.GetExtension(f) ?? "").Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string home = Path.Combine(cellDir, Homes[ext]);
                    if (Directory.Exists(home))
                        foreach (string old in Directory.GetFiles(home, "*" + ext)) File.Delete(old);
                }

            string? placedLayout = cell.Files.FirstOrDefault(f => f.EndsWith(".clay", StringComparison.OrdinalIgnoreCase));
            foreach (string staged in cell.Files)
            {
                string ext = Path.GetExtension(staged) ?? "";
                string stem = Path.GetFileNameWithoutExtension(staged);
                string home = Path.Combine(cellDir, Homes[ext]);
                Directory.CreateDirectory(home);

                if (ext.Equals(".csch", StringComparison.OrdinalIgnoreCase))
                {
                    // Through CellCreate's own writer, so the schematic records the cell it now belongs to.
                    written.Add(CellCreate.WriteSchematicView(cellDir, name, stem, SchematicPersistence.LoadFromFile(staged).model));
                    continue;
                }

                string target = Path.Combine(home, Path.GetFileName(staged));
                if (ext.Equals(".cem", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(target, RepointLayout(staged, placedLayout, name));
                else
                    File.Copy(staged, target, overwrite: true);
                written.Add(target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return new PluginPlacementResult(PluginPlacementOutcome.Refused, name, cellDir, written, point,
                [$"Placing {name} stopped part way: {e.Message}" + (point is null ? "" : " Restore the point taken just before it to undo what was written.")]);
        }

        return new PluginPlacementResult(PluginPlacementOutcome.Placed, name, cellDir, written, point, []);

        PluginPlacementResult Refused(IReadOnlyList<string> why) => new(PluginPlacementOutcome.Refused, name, null, [], null, why);
    }

    /// <summary>
    /// A staged <c>.cem</c> names its layout relative to the staging folder (it has no workspace there). Placed, its
    /// <c>LayoutRef</c> is workspace-relative, so a reference to the staged layout becomes the placed one's. Any other
    /// reference is left as the plugin wrote it.
    /// </summary>
    private static string RepointLayout(string cemPath, string? stagedLayout, string cellName)
    {
        string text = File.ReadAllText(cemPath);
        if (stagedLayout is null || JsonNode.Parse(text) is not JsonObject root) return text;
        if (root["LayoutRef"] is not JsonValue v || !v.TryGetValue<string>(out var reference) || reference.Length == 0) return text;

        string resolved = Path.GetFullPath(reference, Path.GetDirectoryName(cemPath)!);
        if (!string.Equals(resolved, Path.GetFullPath(stagedLayout), StringComparison.OrdinalIgnoreCase)) return text;

        root["LayoutRef"] = $"{cellName}/{CellFolder.LayoutSubFolder}/{Path.GetFileName(stagedLayout)}";
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A <c>.cnl</c> must read and elaborate — <c>check</c>'s two questions of a netlist, in counting mode so
    /// nothing is solved.</summary>
    private static string? NetlistDefect(string path)
    {
        try
        {
            var (lib, tb) = CnlTechnologyBinding.ReadFile(path);
            using (Layout.Em.ParametricEm.EmEvaluationScope.Begin(countOnly: true))
            using (new CircuitRF.Core.Elaboration.Elaborator(lib) { BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) }.Elaborate(tb)) { }
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"it does not elaborate ({e.Message}).";
        }
    }

    /// <summary>A <c>.cem</c> must read, and its layout must resolve — what <c>check</c> asks before it meshes.</summary>
    private static string? EmSetupDefect(string path, TechnologyCache cache)
    {
        EmSetup setup;
        try { setup = EmSetupPersistence.LoadFromFile(path); }
        catch (Exception e) when (e is not OutOfMemoryException) { return $"it could not be read ({e.Message})."; }

        var resolution = EmSetupResolver.Resolve(Path.GetFullPath(path), setup.LayoutRef, null, cache);
        if (resolution.Source is not null) return null;
        return resolution.Diagnostics.Count > 0 ? string.Join(" ", resolution.Diagnostics) : "its layout reference resolves to nothing.";
    }
}
