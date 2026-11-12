// HZN-3 R-hzn3-2 — where the 3D editor's document comes from and goes back to.
//
// A `.c3d` on disk and a design held inside a Hertzon project are two implementations of this one seam. The editor
// loads, reloads, saves and stamps through it and asks it two things nothing else knows: what the tab is called, and
// whether the document may be written back. The editor's own state (undo, the scene, the setups) is the same either way.

using CircuitRF.Design.ThreeD;

namespace CircuitRF.Ui.ThreeD;

public interface IC3dDocumentSource
{
    /// <summary>What the open-document table and the tab are keyed by. A file's is its full path.</summary>
    string Key { get; }

    /// <summary>The path the document resolves its relative references and its technology from: the file itself, or the
    /// file a held document lives in.</summary>
    string FilePath { get; }

    /// <summary>The tab's title.</summary>
    string Title { get; }

    /// <summary>Null when the document can be saved to where it came from; otherwise the sentence saying why not.</summary>
    string? ReadOnlyReason { get; }

    /// <summary>Reads the document afresh. Throws <see cref="C3dReadException"/>, <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when it cannot.</summary>
    C3dDocument Load();

    /// <summary>Writes the document back; null on success, else why not.</summary>
    string? Save(C3dDocument document);

    /// <summary>A value that changes when the stored document does, or null when there is none to compare.</summary>
    string? Stamp();
}

/// <summary>A <c>.c3d</c> on disk.</summary>
public sealed class C3dFileSource(string path) : IC3dDocumentSource
{
    public string FilePath { get; } = Path.GetFullPath(path);
    public string Key => FilePath;
    public string Title => Path.GetFileName(FilePath);
    public string? ReadOnlyReason => null;

    public C3dDocument Load() => C3dPersistence.LoadFromFile(FilePath);

    public string? Save(C3dDocument document)
    {
        try { C3dPersistence.SaveToFile(FilePath, document); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
        return null;
    }

    public string? Stamp()
    {
        try
        {
            var f = new FileInfo(FilePath);
            return f.Exists ? $"{f.LastWriteTimeUtc.Ticks}:{f.Length}" : null;
        }
        catch (Exception) { return null; }
    }
}
