namespace CircuitRF.Ui.Commands;

/// <summary>
/// A dock document that is, or can become, backed by a file on disk.
///
/// Every document type already declared its own <c>FilePath</c> (plus <c>IsScratch =&gt; FilePath
/// is null</c>) independently; this states the shape once so a surface that works on "the document
/// behind this tab" — the tab context menu's Reveal item — can be written against one type rather
/// than against a switch over every concrete document class, which is the form that silently
/// misses the next one added.
/// </summary>
public interface IFileBackedDocument
{
    /// <summary>Absolute path of the file this document was loaded from / last saved to,
    /// or null while the document is a scratch (in-memory only) document.</summary>
    string? FilePath { get; }
}
