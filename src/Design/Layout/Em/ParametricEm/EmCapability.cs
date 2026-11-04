// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 §1 — what makes a schematic EM-capable: a layout linked to it (Update Layout from Schematic's placement
// record present) and exactly one .cem whose layout is that layout, or the one an analysis or instance names when there
// are several. Anything missing is a refusal naming it; EM never falls back to circuit models silently.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>An EM-capable cell, resolved: its schematic, the layout made from it and the setup that solves it.</summary>
/// <param name="CellDir">The cell folder.</param>
/// <param name="CellName">Its name (the folder's).</param>
/// <param name="SchematicPath">Its primary <c>.csch</c>.</param>
/// <param name="ClayPath">Its primary <c>.clay</c>, the layout Update Layout from Schematic made from it.</param>
/// <param name="CemPath">The <c>.cem</c> that analyses that layout.</param>
/// <param name="WorkspaceDir">The workspace the cell belongs to — where the EM cache lives.</param>
/// <param name="Setup">The setup, as read.</param>
public sealed record EmCellTarget(
    string CellDir, string CellName, string SchematicPath, string ClayPath, string CemPath, string WorkspaceDir, EmSetup Setup)
{
    /// <summary>The workspace's <c>.cws</c>.</summary>
    public string CwsPath => Path.Combine(WorkspaceDir, ".cws");
}

public static class EmCapability
{
    /// <summary>
    /// <paramref name="cellDir"/> resolved for EM, or null with <paramref name="refusal"/> saying what is missing.
    /// <paramref name="emSetup"/> names the setup when the layout has several: a path (absolute, or relative to
    /// <paramref name="baseDirectory"/>, then to the workspace) or a setup's file stem.
    /// </summary>
    public static EmCellTarget? Resolve(string cellDir, string? emSetup, string? baseDirectory, out string? refusal)
        => Resolve(cellDir, emSetup, baseDirectory, out refusal, out _);

    private static EmCellTarget? Resolve(string cellDir, string? emSetup, string? baseDirectory, out string? refusal,
                                         out bool severalSetups)
    {
        refusal = null;
        severalSetups = false;
        string dir;
        try { dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = $"'{cellDir}' is not a cell folder ({e.Message}).";
            return null;
        }
        string name = Path.GetFileName(dir);
        if (!Directory.Exists(dir))
        {
            refusal = $"'{name}' is not a cell folder: {dir} does not exist.";
            return null;
        }

        if (WorkspaceRootFinder.WorkspaceDirOf(dir) is not { } ws)
        {
            refusal = $"'{name}' belongs to no workspace, so it has no EM setup and nowhere to keep EM results.";
            return null;
        }

        if (PrimaryOf(dir, ViewType.Schematic) is not { } csch)
        {
            refusal = $"'{name}' has no schematic, so there is nothing to make its layout from.";
            return null;
        }
        if (PrimaryOf(dir, ViewType.Layout) is not { } clay)
        {
            refusal = $"'{name}' has no layout. Run Layout ▸ Update Layout from Schematic to make one, then add an EM setup.";
            return null;
        }

        LayoutView view;
        try { view = LayoutPersistence.LoadFromFile(clay); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            refusal = $"{name}'s layout {Path.GetFileName(clay)} could not be read ({e.Message}).";
            return null;
        }
        if (view.GeneratedPlacements.Count == 0)
        {
            refusal = $"{name}'s layout {Path.GetFileName(clay)} was not made from its schematic (Update Layout from " +
                      "Schematic has never placed it), so a change of the schematic's values cannot reach its copper. " +
                      "Run Layout ▸ Update Layout from Schematic.";
            return null;
        }

        string cws = Path.Combine(ws, ".cws");
        var setups = EmSetupResolver.FindSetupsForLayout(cws, clay);
        string? cem;
        if (emSetup is { Length: > 0 } wanted)
        {
            cem = Named(wanted, setups, baseDirectory, ws);
            if (cem is null)
            {
                refusal = $"{name}'s layout has no EM setup '{wanted}'" +
                          (setups.Count == 0 ? "; it has none at all." : $"; it has {List(setups, ws)}.");
                return null;
            }
        }
        else if (setups.Count == 1) cem = setups[0];
        else if (setups.Count == 0)
        {
            refusal = $"No EM setup analyses {name}'s layout {Path.GetFileName(clay)}. Create one (File ▸ New ▸ EM Setup, " +
                      "or Update Layout from Schematic with Create or update EM setup on).";
            return null;
        }
        else
        {
            severalSetups = true;
            refusal = $"{name}'s layout has {setups.Count} EM setups ({List(setups, ws)}); name the one to use with emSetup=.";
            return null;
        }

        EmSetup setup;
        try { setup = EmSetupPersistence.LoadFromFile(cem); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            refusal = $"The EM setup {Path.GetFileName(cem)} could not be read ({e.Message}).";
            return null;
        }
        return new EmCellTarget(dir, name, csch, clay, cem, ws, setup);
    }

    /// <summary>Whether <paramref name="cellDir"/> resolves for EM — the question an extraction asks of every cell it
    /// places, answered from the same function so the two cannot disagree. A cell whose layout has several setups is capable too: an instance or analysis names one.</summary>
    public static bool IsCapable(string cellDir)
    {
        // Asked of every cell instance an extraction meets, and an extraction runs on every Simulate: a cell with no
        // layout answers from two directory reads, and a linked one's answer is held for a moment so a schematic placing
        // it many times reads its layout and walks its workspace once.
        string key;
        try { key = Path.GetFullPath(cellDir); } catch (Exception) { return false; }
        long now = Environment.TickCount64;
        lock (_memoGate)
            if (_memo.TryGetValue(key, out var m) && now - m.At < MemoMilliseconds) return m.Capable;
        bool capable = PrimaryOf(key, ViewType.Layout) is not null
                       && (Resolve(key, null, null, out _, out bool several) is not null || several);
        lock (_memoGate) _memo[key] = (now, capable);
        return capable;
    }

    private const long MemoMilliseconds = 2000;
    private static readonly Dictionary<string, (long At, bool Capable)> _memo = new(StringComparer.Ordinal);
    private static readonly Lock _memoGate = new();

    /// <summary>Every <c>.cem</c> that analyses <paramref name="cellDir"/>'s layout, workspace-relative and in the resolver's
    /// order — what an analysis's EM setup picker lists. Empty when the cell has no layout or no workspace.</summary>
    public static IReadOnlyList<string> SetupsFor(string cellDir)
    {
        try
        {
            string dir = Path.GetFullPath(cellDir);
            if (WorkspaceRootFinder.WorkspaceDirOf(dir) is not { } ws || PrimaryOf(dir, ViewType.Layout) is not { } clay) return [];
            return EmSetupResolver.FindSetupsForLayout(Path.Combine(ws, ".cws"), clay);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>The cell folder a schematic belongs to — the folder above its <c>schematic/</c> sub-folder — or null when
    /// the file is not inside a cell folder's schematic view.</summary>
    public static string? CellDirOfSchematic(string cschPath)
    {
        string? schematicDir = Path.GetDirectoryName(Path.GetFullPath(cschPath));
        string? cell = schematicDir is null ? null : Path.GetDirectoryName(schematicDir);
        if (cell is null) return null;
        return string.Equals(Path.GetFullPath(CellFolder.SubFolderPath(cell, ViewType.Schematic)).TrimEnd(Path.DirectorySeparatorChar),
                             schematicDir!.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
            ? cell : null;
    }

    /// <summary>The cell folder a <c>.cem</c>'s layout belongs to, or null when it names none.</summary>
    public static string? CellDirOfSetup(string cemPath)
    {
        try
        {
            var setup = EmSetupPersistence.LoadFromFile(cemPath);
            string? cws = WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(Path.GetFullPath(cemPath))) is { } ws
                ? Path.Combine(ws, ".cws") : null;
            if (EmSetupResolver.ResolveLayoutPath(cemPath, setup.LayoutRef, cws) is not { } clay) return null;
            return Path.GetDirectoryName(Path.GetDirectoryName(clay));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? PrimaryOf(string cellDir, ViewType type)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, type);
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null) return null;
        return Path.Combine(CellFolder.SubFolderPath(cellDir, type), primary.ResolvedName);
    }

    private static string? Named(string wanted, IReadOnlyList<string> setups, string? baseDirectory, string ws)
    {
        foreach (string root in new[] { baseDirectory, ws }.Where(r => r is { Length: > 0 }).Cast<string>())
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.IsPathRooted(wanted) ? wanted : Path.Combine(root, wanted)); }
            catch (Exception) { continue; }
            if (setups.FirstOrDefault(s => string.Equals(Path.GetFullPath(s), candidate, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return hit;
        }
        string stem = Path.GetFileNameWithoutExtension(wanted);
        return setups.FirstOrDefault(s => string.Equals(Path.GetFileNameWithoutExtension(s), stem, StringComparison.OrdinalIgnoreCase));
    }

    private static string List(IReadOnlyList<string> setups, string ws) =>
        string.Join(", ", setups.Select(s => Path.GetRelativePath(ws, s).Replace('\\', '/')));
}
