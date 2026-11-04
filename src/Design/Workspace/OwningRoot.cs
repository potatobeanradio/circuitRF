using CircuitRF.Design.VendorLibraries.ModelLibrary;

namespace CircuitRF.Design.Workspace;

/// <summary>Which walk produced an <see cref="OwningRoot"/> answer.</summary>
public enum OwningRootKind
{
    /// <summary>The nearest folder at or above the document holding a <c>part.cpart</c>.</summary>
    ModelFolder,
    /// <summary>The nearest folder at or above the document holding a <c>.cws</c>.</summary>
    Workspace,
}

/// <summary>An owning root and the walk that found it.</summary>
public sealed record OwningRootResult(string Root, OwningRootKind Kind)
{
    /// <summary>The walk's name as <c>explain</c> reports it.</summary>
    public string KindName => Kind == OwningRootKind.ModelFolder ? "model-folder" : "workspace";
}

/// <summary>
/// The ONE answer to "what does a relative file reference in this document resolve against"
/// (brief vendorlib-8 R-vl8-2a): the nearest ancestor model folder, else the workspace root, else
/// nothing.
///
/// <para><b>The nearest wins.</b> A model library kept inside a workspace tree still resolves each
/// model's references against that model's own folder, because the model folder is reached first on
/// the way up. A model is placed from somebody else's workspace, so its references cannot be written
/// against any workspace root.</para>
///
/// <para>Callers that need a fallback for a loose document (no workspace and no model folder) supply
/// it themselves: the run's host workspace, then the document's own folder
/// (<c>SchematicCircuit.ReferenceBaseOf</c>, <c>SnpPathPolicy.Resolve</c>).</para>
/// </summary>
public static class OwningRoot
{
    /// <summary>The owning root of a document in <paramref name="documentDir"/>, or null.</summary>
    public static string? Of(string? documentDir) => Find(documentDir)?.Root;

    // Memoised for the reason WorkspaceRootFinder.WorkspaceDirOf is: a schematic asks per SnP per render.
    // Dropped by WorkspaceRootFinder.InvalidateCache, which is called wherever a .cws appearing or
    // disappearing could change the answer; a part.cpart appearing is the same kind of event.
    // Keyed on the normalised full path, compared as the file system compares it: on Linux /x/Lib and /x/lib are two
    // folders with two answers, and "dir" and "dir/" are one.
    private static readonly Dictionary<string, OwningRootResult?> _memo = new(DocumentFileRefs.PathComparer);
    private static readonly Lock _gate = new();

    /// <summary>Forgets the memoised walks (<see cref="WorkspaceRootFinder.InvalidateCache"/>).</summary>
    public static void InvalidateCache()
    {
        lock (_gate) _memo.Clear();
    }

    /// <summary>The owning root of a document in <paramref name="documentDir"/> and the walk that
    /// found it, or null when neither walk finds one.</summary>
    public static OwningRootResult? Find(string? documentDir)
    {
        if (string.IsNullOrWhiteSpace(documentDir)) return null;
        string key;
        try { key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(documentDir)); }
        catch { return null; }
        lock (_gate)
            if (_memo.TryGetValue(key, out var memo)) return memo;
        var found = Walk(key);
        lock (_gate) _memo[key] = found;
        return found;
    }

    private static OwningRootResult? Walk(string documentDir)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(documentDir)); }
        catch { return null; }

        // The workspace walk is memoised; the model-folder walk is one File.Exists per level and stops
        // at the workspace root, where the workspace is the nearer answer.
        string? workspace = WorkspaceRootFinder.WorkspaceDirOf(full);
        for (string? dir = full; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (workspace is not null && DocumentFileRefs.PathComparer.Equals(dir, Path.TrimEndingDirectorySeparator(workspace))) break;
            if (ModelFolder.Is(dir)) return new OwningRootResult(dir, OwningRootKind.ModelFolder);
        }
        return workspace is null ? null : new OwningRootResult(workspace, OwningRootKind.Workspace);
    }

    /// <summary>True when two roots name the same folder.</summary>
    public static bool Same(string? a, string? b)
        => a is not null && b is not null
           && string.Equals(WorkspaceRootFinder.Normalize(a), WorkspaceRootFinder.Normalize(b), StringComparison.OrdinalIgnoreCase);
}
