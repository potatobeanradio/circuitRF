using CircuitRF.Design.Layout;
using CircuitRF.Design.Revision;

namespace CircuitRF.Design.Workspace;

// ──────────────────────────────────────────────────────────────────────────────
//  WorkspaceCreate — making a workspace, once.
//
//  brief-automation-3-authoring-verbs.md R-aut3-1: `circuitrf new workspace` and File ▸ New
//  Workspace call THIS, and after this file there is exactly one implementation. An operation that
//  exists only inside a view model is not a capability (R-aut-2), and the failure the whole brief
//  exists to prevent is a headless verb that re-implements what the view model does: the two diverge
//  silently, and the first symptom is a workspace created headlessly that the GUI treats as subtly
//  malformed.
//
//  WHAT IS HERE AND WHAT IS NOT (R-aut3-2). `WorkspaceViewModel.NewWorkspace` is a dialog, a
//  dirty-work prompt, then four operations. Only the four are here — create the directory, build a
//  CwsFile, optionally copy a shipped technology's own bytes into tech/ and point DefaultTechRef at
//  it, save the .cws atomically. The dialog, the prompt, and everything the view model does AFTER
//  the save — cache resets, tree refresh, dock layout, launch action — are shell concerns and stay
//  in the shell.
//
//  THE TECHNOLOGY COPY IS PART OF THE CAPABILITY, NOT THE DIALOG (R-aut3-4). The chosen entry's own
//  RAW BYTES are written; they are not re-serialized through TechPersistence on the way, which would
//  produce a different file for no gain (R-misc-8's own reasoning: the shipped bytes ARE already
//  exactly what should land on disk).
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>What a created workspace is, in paths — which is what a headless caller's next step
/// needs, since the usual next step is to read or rewrite one of them.</summary>
/// <param name="WorkspaceDir">The workspace folder.</param>
/// <param name="CwsPath">Its <c>.cws</c>.</param>
/// <param name="TechPath">The copied technology, or null when none was chosen (a perfectly valid
/// workspace that resolves to the fallback palette — <c>pcell-contract.md</c> §5's own supported
/// no-technology state).</param>
public sealed record WorkspaceCreateResult(string WorkspaceDir, string CwsPath, string? TechPath);

public static class WorkspaceCreate
{
    /// <summary>
    /// The technology a caller that says nothing gets — <see cref="TechnologyCatalog.DefaultId"/>,
    /// which is what the New Workspace dialog's combobox opens pre-selected on.
    ///
    /// <para>R-aut3-3: the headless default IS the GUI's default. A headless default that differs
    /// from the dialog's is a second product. <b>It stopped being a <c>const</c> when the default
    /// became something the user chooses</b> (Settings ▸ Technology): a constant folded at compile
    /// time would have gone on naming the shipped one in every caller that read it, silently.</para>
    /// </summary>
    public static string DefaultTechnologyId => TechnologyCatalog.DefaultId;

    /// <summary>
    /// R-sl2-13's rule, in one place: the refusal sentence for creating into a directory that cannot
    /// be written, or null when it can. Three GUI sites and the <c>new workspace</c> verb share it —
    /// the directory is named because "somewhere you picked" is not something a caller can act on.
    /// </summary>
    public static string? UnwritableParentRefusal(string? parentDir, string whatWasBeingCreated)
        => WorkspaceWritability.IsReadOnly(parentDir)
            ? $"{whatWasBeingCreated} — '{parentDir}' is read-only on this machine. Choose a location you can write to."
            : null;

    /// <summary>
    /// R-aut3-5: resolves a technology id — shipped or user-installed — or throws naming the ones
    /// that exist.
    ///
    /// <para>Not a fallback to the default. A caller that asked for a specific process and silently
    /// got another has a wrong design and no way to know.</para>
    /// </summary>
    public static TechnologyCatalogEntry ResolveTechnology(string id)
        => TechnologyCatalog.Find(id)
           ?? throw new ArgumentException(
               $"No technology named '{id}'. The available technologies are: "
               + string.Join(", ", TechnologyCatalog.All.Select(e => e.Id)) + ".",
               nameof(id));

    /// <summary>
    /// Creates the workspace: the directory, the optional technology copy, and the <c>.cws</c>.
    /// </summary>
    /// <param name="technologyId">
    /// A <see cref="TechnologyCatalog"/> id — shipped or user-installed — or null for a workspace
    /// with no technology. Unknown ids throw (R-aut3-5).
    /// </param>
    /// <exception cref="IOException">
    /// R-aut3-6: the target already exists. <see cref="WorkspaceLock"/>'s own header explains what is
    /// at stake when two things believe they own one <c>.cws</c> — so this neither overwrites nor
    /// merges, and the guard is here rather than only in each caller's pre-flight so that no route
    /// into workspace creation can skip it.
    /// </exception>
    public static WorkspaceCreateResult Create(string parentDir, string name, string? technologyId)
    {
        string workspaceDir = Path.Combine(parentDir, name);
        if (Directory.Exists(workspaceDir))
            throw new IOException($"A folder named '{name}' already exists at that location.");

        // Resolved BEFORE anything is created: an unknown id must not leave a half-made workspace
        // behind, and the four steps below have no rollback (the same reasoning R-sl2-13's
        // unwritable-parent refusal is placed on).
        var entry = technologyId is { Length: > 0 } id ? ResolveTechnology(id) : null;

        string cwsPath = Path.Combine(workspaceDir, ".cws");
        Directory.CreateDirectory(workspaceDir);

        var cws = new CwsFile();
        string? techPath = null;
        if (entry is not null)
        {
            var techDir = Path.Combine(workspaceDir, "tech");
            Directory.CreateDirectory(techDir);
            techPath = Path.Combine(techDir, entry.Id + ".ctech");
            File.WriteAllText(techPath, TechnologyCatalog.LoadRawJson(entry));
            cws.DefaultTechRef = Path.GetRelativePath(workspaceDir, techPath);
        }

        WorkspacePersistence.SaveToFileAtomic(cwsPath, cws);

        // RC-3 R-rc3-11: the .gitignore and .gitattributes are written HERE, whether or not this
        // workspace ever becomes a repository. This is the one function both File > New Workspace and
        // `circuitrf new workspace` call, so writing them here means a headlessly-created workspace is
        // not a second, subtly different product. Both files are inert until there is a repository and
        // correct the moment there is — and they are NOT a substitute for R-rc3-11a's other half:
        // every workspace that already exists was created before this line, and gets them when it
        // first gains a repository instead.
        WorkspacePolicyFiles.Ensure(workspaceDir);

        return new WorkspaceCreateResult(workspaceDir, cwsPath, techPath);
    }
}
