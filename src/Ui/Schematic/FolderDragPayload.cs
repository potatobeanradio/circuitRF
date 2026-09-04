namespace CircuitRF.Ui.Schematic;

/// <summary>
/// A user FOLDER dragged out of the Project Tree (TM1 R-tm1-7). The fourth of the tree's drag
/// payloads and the only one MW3 had no use for — a folder is not a cell and has no cross-workspace
/// meaning, so it becomes draggable only now that a drop inside the tree's own workspace does
/// something.
///
/// <para>Carries an ABSOLUTE path for the same reason every other one does: the payload travels on
/// the platform pasteboard, so the receiving tree has to be able to say precisely which folder in
/// which workspace it came from without anything else on the wire.</para>
/// </summary>
public sealed record FolderDragPayload(string FolderAbsPath)
{
    private const string Prefix = "circuitrf-wsfolder:";

    public string Serialize() => $"{Prefix}{FolderAbsPath}";

    /// <summary>Parses a string produced by <see cref="Serialize"/>; false for anything else, which
    /// is the foreign-text guard every one of these payloads carries.</summary>
    public static bool TryParse(string? s, out FolderDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var path = s[Prefix.Length..];
        if (string.IsNullOrEmpty(path)) return false;
        result = new FolderDragPayload(path);
        return true;
    }
}
