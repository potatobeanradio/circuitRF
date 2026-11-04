// Framework-free. No Avalonia, no SkiaSharp.

using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// brief-sdl-5 R-sdl5-2a: the one place a new <c>.cem</c> is written — File ▸ New ▸ EM Setup…, the Layout Editor's EM
/// button and Update Layout from Schematic's Create or update EM setup all call it (<c>src/Design/CLAUDE.md</c>: a
/// make-an-artifact operation lives here, with a shell wrapper in <c>src/Ui</c>, never in a view model).
///
/// <para>A new setup goes in <c>&lt;workspace&gt;/em/</c>, never beside the layout: a cell's <c>layout/</c> folder lists
/// <c>*.clay</c> only, so a <c>.cem</c> there would have no row in the project tree. It arrives at
/// <see cref="EmSetup.CreateNew"/>'s defaults, which is what makes "pick a layout, run" work with no other step.</para>
/// </summary>
public static class EmSetupCreate
{
    /// <summary>The folder new setups go in, under the workspace root.</summary>
    public static string DirectoryOf(string workspaceDir) => Path.Combine(workspaceDir, "em");

    /// <summary>Where a setup called <paramref name="name"/> is written.</summary>
    public static string PathOf(string workspaceDir, string name) =>
        Path.Combine(DirectoryOf(workspaceDir), name + EmSetupPersistence.Extension);

    /// <summary><paramref name="clayPath"/> as a setup stores it: relative to the workspace root, forward slashes.</summary>
    public static string LayoutRefOf(string workspaceDir, string clayPath) =>
        Path.GetRelativePath(workspaceDir, clayPath).Replace('\\', '/');

    /// <summary><paramref name="stem"/>, or the first of <c>stem2</c>, <c>stem3</c>, … not already taken in the
    /// workspace's <c>em/</c> folder.</summary>
    public static string NextFreeName(string workspaceDir, string stem)
    {
        string candidate = stem;
        for (int n = 2; File.Exists(PathOf(workspaceDir, candidate)); n++)
            candidate = $"{stem}{n}";
        return candidate;
    }

    /// <summary>What <see cref="Create"/> did: the path written, or why nothing was.</summary>
    public sealed record Result(string? Path, string? Refusal)
    {
        public bool Ok => Path is not null;
    }

    /// <summary>
    /// Writes <c>&lt;workspace&gt;/em/&lt;name&gt;.cem</c>, a new setup analysing <paramref name="layoutRef"/>.
    /// <paramref name="seed"/>, when given, fills in what the caller knows beyond the defaults (Update Layout from
    /// Schematic's port impedances and frequency plan) before it is written. Refuses a name that is not usable and a
    /// setup that already exists; never overwrites.
    /// </summary>
    public static Result Create(string workspaceDir, string name, string layoutRef, Action<EmSetup>? seed = null)
    {
        if (!NameValidator.IsValid(name))
            return new Result(null, $"'{name}' is not a usable name. {NameValidator.Validate(name)}");
        string path = PathOf(workspaceDir, name);
        if (File.Exists(path))
            return new Result(null, $"An EM setup named '{name}' already exists.");

        var setup = EmSetup.CreateNew(name, layoutRef);
        seed?.Invoke(setup);
        Directory.CreateDirectory(DirectoryOf(workspaceDir));
        EmSetupPersistence.SaveToFile(path, setup);
        return new Result(path, null);
    }
}
