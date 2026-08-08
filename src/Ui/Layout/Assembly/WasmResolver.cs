namespace CircuitRF.Ui.Layout.Assembly;

/// <summary>Where a resolved <see cref="WasmFile"/> came from, or that none was found.</summary>
public enum WasmResolutionSource
{
    /// <summary>The document's own `.wasm` reference.</summary>
    DocumentRef,

    /// <summary>The workspace default (`.cws`'s <c>DefaultAssemblyRef</c>).</summary>
    WorkspaceDefault,

    /// <summary>
    /// Nothing was referenced. <b>This is not an error</b> — it means the design states no assembly
    /// house, so there are no assembly rules to check. Die-side checking runs normally.
    /// </summary>
    None,
}

/// <summary>
/// Result of resolving a design's effective assembly rule set. <see cref="Rules"/> is null when
/// nothing resolved, which is a normal, fully-supported state.
/// </summary>
public sealed record WasmResolution(
    WasmFile?             Rules,
    string?               ResolvedPath,
    WasmResolutionSource  Source,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>The empty answer: no reference anywhere, nothing to say about it.</summary>
    public static readonly WasmResolution None = new(null, null, WasmResolutionSource.None, []);

    /// <summary>
    /// The one sentence a surface shows about which rules ran. Reported for the same reason
    /// <c>DrcRunResult.TechnologyName</c> is: a clean result checked against no assembly rules at
    /// all looks exactly like a clean result checked against the right house's, unless it says.
    /// </summary>
    public string Describe() => Rules is null
        ? "No assembly rules."
        : $"Assembly rules: \"{Rules.Name}\" ({Rules.RuleCount} rule(s)).";
}

/// <summary>
/// Resolves a design's effective assembly rule set, mirroring <see cref="TechnologyResolver"/>
/// exactly — framework-free, side-effect-free, returns diagnostics but never posts them.
///
/// <b>Resolution order (§5 open question 1, answered BOTH — document overrides workspace, the
/// `.ctech` precedent):</b>
/// <list type="number">
/// <item>A non-null document `.wasm` reference resolves relative to the document's own directory.</item>
/// <item>Otherwise the workspace default resolves relative to the workspace root.</item>
/// <item>Otherwise none — and <b>none is not an error</b>. It is reported ONCE, as
/// "no assembly rules", not once per wire and not as a failure.</item>
/// </list>
///
/// <para><b>Why both and not one.</b> A workspace default is what makes the ordinary case free: a
/// shop bonds at one house, states it once, and every design in the workspace picks it up. A
/// per-document reference is what makes the exception expressible: one product qualified at a
/// second house does not force a second workspace. Either alone leaves a real case unsayable, and
/// `.clay`'s <c>TechRef</c> already proves the pair works — a document only stores a reference when
/// it deliberately deviates, so moves and Save-As never have to rewrite a relative path.</para>
///
/// <para><b>All failure modes are non-fatal</b> — a missing file, corrupt JSON or a newer
/// <c>format_version</c> each produce a null rule set plus one diagnostic; the design still opens,
/// still edits, and its die-side rules still check.</para>
/// </summary>
public static class WasmResolver
{
    /// <param name="documentAssemblyRef">The document's own reference (relative path, or null).</param>
    /// <param name="documentDir">Absolute directory containing the document, or null for a
    /// not-yet-saved one — a non-null <paramref name="documentAssemblyRef"/> cannot be resolved
    /// without it.</param>
    /// <param name="workspaceRootDir">Absolute workspace root, or null when none is open.</param>
    /// <param name="workspaceDefaultRef">The workspace's own default reference, or null.</param>
    /// <param name="cache">The cache to load through — never bypassed.</param>
    public static WasmResolution Resolve(
        string?    documentAssemblyRef,
        string?    documentDir,
        string?    workspaceRootDir,
        string?    workspaceDefaultRef,
        WasmCache  cache)
    {
        if (!string.IsNullOrWhiteSpace(documentAssemblyRef) && documentDir is not null)
            return Load(Path.GetFullPath(Path.Combine(documentDir, documentAssemblyRef)),
                        WasmResolutionSource.DocumentRef, cache);

        if (!string.IsNullOrWhiteSpace(workspaceDefaultRef) && workspaceRootDir is not null)
            return Load(Path.GetFullPath(Path.Combine(workspaceRootDir, workspaceDefaultRef)),
                        WasmResolutionSource.WorkspaceDefault, cache);

        return WasmResolution.None;
    }

    /// <summary>Loads and validates directly from a known absolute path — the same logic both
    /// branches of <see cref="Resolve"/> use, exposed for a caller that already knows the file.</summary>
    public static WasmResolution LoadDirect(string path, WasmResolutionSource source, WasmCache cache)
        => Load(path, source, cache);

    private static WasmResolution Load(string path, WasmResolutionSource source, WasmCache cache)
    {
        WasmFile? rules;
        try
        {
            rules = cache.Get(path);
        }
        catch (Exception ex)
        {
            return new WasmResolution(null, path, source,
                [$"Could not load assembly rules '{path}': {ex.Message}"]);
        }

        if (rules is null)
            return new WasmResolution(null, path, source, [$"Assembly rule file not found: {path}"]);

        // A rule set with problems still RESOLVES and is still used — the same rule
        // TechnologyResolver follows. A malformed envelope loses one rule, not the file.
        return new WasmResolution(rules, path, source, WasmValidation.Validate(rules));
    }
}
