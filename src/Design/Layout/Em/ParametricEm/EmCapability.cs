// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 §1 — what makes a schematic EM-capable: a layout linked to it (Update Layout from Schematic's placement
// record present) and exactly one .cem whose layout is that layout, or the one an analysis or instance names when there
// are several. Anything missing is a refusal naming it; EM never falls back to circuit models silently.

using CircuitRF.Design.Cells;
using CircuitRF.Design.ThreeD;
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
/// <remarks>brief-sdl-8 D1 — the EM view is a <c>.cem</c> (planar) or a <c>.c3d</c> and one of its embedded setups. For the
/// second, <see cref="CemPath"/> is the spelling an analysis or instance names it by, <c>&lt;c3d path&gt;#&lt;setup&gt;</c>, and
/// <see cref="Setup"/> is the embedded setup as its run uses it (<see cref="C3dSetups.ForRun"/>).</remarks>
public sealed record EmCellTarget(
    string CellDir, string CellName, string SchematicPath, string ClayPath, string CemPath, string WorkspaceDir, EmSetup Setup)
{
    /// <summary>The workspace's <c>.cws</c>.</summary>
    public string CwsPath => Path.Combine(WorkspaceDir, ".cws");

    /// <summary>brief-sdl-8 — the cell's 3D view, when the EM view is one of its setups; null for a <c>.cem</c>.</summary>
    public string? C3dPath { get; init; }

    /// <summary>brief-sdl-8 — the embedded setup's own name, when <see cref="C3dPath"/> is set.</summary>
    public string? C3dSetup { get; init; }

    /// <summary>True when the EM view is a 3D view's setup.</summary>
    public bool Is3D => C3dPath is not null;

    /// <summary>How a report names the setup: a <c>.cem</c>'s file stem, or <c>&lt;c3d stem&gt;#&lt;setup&gt;</c>.</summary>
    public string SetupName => C3dPath is { } c ? $"{Path.GetFileNameWithoutExtension(c)}#{C3dSetup}" : Path.GetFileNameWithoutExtension(CemPath);
}

/// <summary>brief-sdl-8 D1 — one EM view a cell's layout can be solved by: a <c>.cem</c>, or a setup embedded in its 3D view.</summary>
internal sealed record EmViewChoice(string Path, string? SetupName)
{
    public bool Is3D => SetupName is not null;
    public string Spelling(string ws) =>
        System.IO.Path.GetRelativePath(ws, Path).Replace('\\', '/') + (SetupName is { } n ? "#" + n : "");
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
        var setups = Choices(cws, clay, dir, out string? threeDNote);
        EmViewChoice? chosen;
        if (emSetup is { Length: > 0 } wanted)
        {
            chosen = Named(wanted, setups, baseDirectory, ws);
            if (chosen is null)
            {
                refusal = $"{name}'s layout has no EM setup '{wanted}'" +
                          (setups.Count == 0 ? "; it has none at all." : $"; it has {List(setups, ws)}.") +
                          (threeDNote is null ? "" : " " + threeDNote);
                return null;
            }
        }
        else if (setups.Count == 1) chosen = setups[0];
        else if (setups.Count == 0)
        {
            refusal = $"No EM setup analyses {name}'s layout {Path.GetFileName(clay)}. Create one (File ▸ New ▸ EM Setup, " +
                      "or Update Layout from Schematic with Create or update EM setup on), or add a driven setup to its 3D view." +
                      (threeDNote is null ? "" : " " + threeDNote);
            return null;
        }
        else
        {
            severalSetups = true;
            refusal = $"{name}'s layout has {setups.Count} EM setups ({List(setups, ws)}); name the one to use with emSetup=.";
            return null;
        }

        if (chosen.Is3D)
        {
            // brief-sdl-8 R-sdl8-1 — a setup embedded in the cell's 3D view: read as its run reads it.
            C3dDocument doc;
            try { doc = C3dPersistence.LoadFromFile(chosen.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                           or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                refusal = $"{name}'s 3D view {Path.GetFileName(chosen.Path)} could not be read ({e.Message}).";
                return null;
            }
            var (embedded, why) = C3dSetups.Select(doc, chosen.SetupName);
            if (embedded is null)
            {
                refusal = why;
                return null;
            }
            return new EmCellTarget(dir, name, csch, clay, chosen.Path + "#" + chosen.SetupName, ws, C3dSetups.ForRun(embedded, chosen.Path))
            {
                C3dPath = chosen.Path, C3dSetup = chosen.SetupName,
            };
        }

        string cem = chosen.Path;
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

    /// <summary>
    /// brief-sdl-8 D1 — every EM view of the layout: each <c>.cem</c> that analyses it (the resolver's order), then each DRIVEN
    /// setup embedded in the cell's primary 3D view, in file order. An eigenmode, static or thermal setup makes no N-port and
    /// is not a choice; <paramref name="note"/> says when the 3D view holds only such setups, so a refusal can say why.
    /// </summary>
    internal static List<EmViewChoice> Choices(string cws, string clay, string cellDir, out string? note)
    {
        note = null;
        var list = EmSetupResolver.FindSetupsForLayout(cws, clay).Select(c => new EmViewChoice(c, null)).ToList();
        if (PrimaryOf(cellDir, ViewType.ThreeD) is not { } c3d) return list;
        try
        {
            var doc = C3dPersistence.LoadFromFile(c3d);
            var read = C3dSetups.Read(doc);
            var driven = read.Where(r => r.Setup is { Is3D: true, IsThermal: false, Problem3D: CircuitRF.Engine.Em3d.Em3dProblemType.Driven })
                             .Select(r => new EmViewChoice(c3d, r.Name)).ToList();
            if (driven.Count == 0 && read.Count > 0)
                note = $"Its 3D view {Path.GetFileName(c3d)} has no driven setup ({string.Join(", ", read.Select(r => $"'{r.Name}'"))}), " +
                       "and only a driven setup makes the N-port an EM model is.";
            list.AddRange(driven);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            note = $"Its 3D view {Path.GetFileName(c3d)} could not be read ({e.Message}).";
        }
        return list;
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
            // brief-sdl-8 D1 — a 3D view's setup is listed as <c3d>#<setup>, the spelling emSetup= takes.
            return Choices(Path.Combine(ws, ".cws"), clay, dir, out _).Select(c => c.SetupName is { } n ? c.Path + "#" + n : c.Path).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>brief-sdl-8 D1 — how a picker and <c>emSetup=</c> name one of <see cref="SetupsFor"/>'s entries: a <c>.cem</c>'s
    /// file stem, or <c>&lt;view stem&gt;#&lt;setup&gt;</c> for a setup of the 3D view. Both resolve back to the same entry.</summary>
    public static string SetupLabel(string setup)
    {
        int hash = setup.LastIndexOf('#');
        return hash > 0 ? Path.GetFileNameWithoutExtension(setup[..hash]) + setup[hash..] : Path.GetFileNameWithoutExtension(setup);
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

    /// <summary>
    /// The choice <paramref name="wanted"/> names: a path or file stem (a <c>.cem</c>, or a <c>.c3d</c> with one driven setup),
    /// <c>&lt;c3d&gt;#&lt;setup&gt;</c> (brief-sdl-8 D1; the left side may be empty for the cell's own 3D view), or an embedded
    /// setup's bare name when exactly one choice has it.
    /// </summary>
    private static EmViewChoice? Named(string wanted, IReadOnlyList<EmViewChoice> setups, string? baseDirectory, string ws)
    {
        string file = wanted;
        string? setupName = null;
        int hash = wanted.LastIndexOf('#');
        if (hash >= 0) { file = wanted[..hash].Trim(); setupName = wanted[(hash + 1)..].Trim(); }

        IEnumerable<EmViewChoice> pool = setupName is null ? setups : setups.Where(s => s.SetupName == setupName);
        if (setupName is not null && file.Length == 0) return pool.Count() == 1 ? pool.First() : null;

        var byFile = pool.Where(s => SameFile(s.Path, file, baseDirectory, ws)).ToList();
        if (byFile.Count == 0)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            byFile = pool.Where(s => string.Equals(Path.GetFileNameWithoutExtension(s.Path), stem, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (setupName is null)
        {
            // A .cem by its path or stem; a .c3d only when it has exactly one driven setup.
            if (byFile.FirstOrDefault(s => !s.Is3D) is { } cem) return cem;
            if (byFile.Count == 1) return byFile[0];
            if (byFile.Count == 0)
            {
                var byName = setups.Where(s => s.SetupName == wanted.Trim()).ToList();
                if (byName.Count == 1) return byName[0];
            }
            return null;
        }
        return byFile.Count == 1 ? byFile[0] : null;
    }

    private static bool SameFile(string path, string wanted, string? baseDirectory, string ws)
    {
        foreach (string root in new[] { baseDirectory, ws }.Where(r => r is { Length: > 0 }).Cast<string>())
        {
            try
            {
                string candidate = Path.GetFullPath(Path.IsPathRooted(wanted) ? wanted : Path.Combine(root, wanted));
                if (string.Equals(Path.GetFullPath(path), candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    private static string List(IReadOnlyList<EmViewChoice> setups, string ws) =>
        string.Join(", ", setups.Select(s => s.Spelling(ws)));
}
