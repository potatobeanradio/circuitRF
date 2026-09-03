namespace CircuitRF.Ui.Schematic;

/// <summary>
/// A loose FILE dragged out of one workspace's Project Tree (MW3 §5).
///
/// <para><b>Why a payload of its own rather than the OS file list the tree already accepts.</b> A
/// drop carrying <c>DataFormat.File</c> can have come from anywhere — Finder, Explorer, a browser —
/// and the tree's existing answer for one is <c>AddKnownFile</c>: a bookmark, not a copy. A file
/// dragged from ANOTHER TREE is a different intent and needs a different answer (R-mw3-11: it is
/// copied into the receiving workspace), and the two are indistinguishable on the wire unless the
/// gesture states its own kind. Same reasoning as <see cref="CellDragPayload"/>'s, and same
/// mechanism: a prefixed text string, so it travels on the native pasteboard.</para>
///
/// <para><b>There is no Reference option for a file</b> — a loose <c>.s2p</c>, <c>.npy</c> or
/// <c>.ctech</c> has no reference semantics in a <c>.cws</c>, and inventing one would be a fourth
/// path convention beside the three <c>DocumentFileRefs.RefBase</c> already carries.</para>
/// </summary>
public sealed record WorkspaceFileDragPayload(string FileAbsPath)
{
    private const string Prefix = "circuitrf-wsfile:";

    /// <summary>Compact wire representation: <c>circuitrf-wsfile:&lt;absolute-file-path&gt;</c>.</summary>
    public string Serialize() => $"{Prefix}{FileAbsPath}";

    /// <summary>Parses a string produced by <see cref="Serialize"/>; false for anything else, which
    /// is the foreign-text guard every one of these payloads carries.</summary>
    public static bool TryParse(string? s, out WorkspaceFileDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var path = s[Prefix.Length..];
        if (string.IsNullOrEmpty(path)) return false;
        result = new WorkspaceFileDragPayload(path);
        return true;
    }
}
