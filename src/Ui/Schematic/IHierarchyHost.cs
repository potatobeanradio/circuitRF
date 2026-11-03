using System.Threading.Tasks;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Allows a <see cref="SchematicDocument"/> (and its view) to call into the workspace-level
/// hierarchy service without a direct reference to <c>WorkspaceViewModel</c>.
/// Injected at document creation time via <see cref="SchematicDocument.Hierarchy"/>.
/// </summary>
public interface IHierarchyHost
{
    bool CanPushInto(EditableComponent? comp, SchematicEditModel? parentModel, out string? reason);
    void PushIntoCell(SchematicDocument doc, EditableComponent comp);
    void PopOutOf(SchematicDocument doc);
    void PopToLevel(SchematicDocument doc, int frameIndex);
    void OpenCellInNewTab(SchematicDocument fromDoc, EditableComponent comp);

    /// <summary>
    /// Saves <paramref name="doc"/> with the same behaviour as ⌘S single-doc scope:
    /// materialized → writes to its known path; scratch → the Save-to-Cell plan dialog.
    /// Registers the file/session and refreshes the project tree. The host resolves the owner window.
    /// </summary>
    Task SaveSchematicDocumentAsync(SchematicDocument doc);

    /// <summary>
    /// <b>Re-reference Cell…</b> — put a broken cell reference back together: look for the cell, and
    /// ask the user to point at it only when the search comes up empty or ambiguous. Records whatever
    /// reference the answer needs (an alias, a row in the Project Tree) and rewrites the document only
    /// when the reference itself actually changed.
    /// </summary>
    Task ReReferenceCellAsync(SchematicDocument doc, EditableComponent comp);

    /// <summary>
    /// The <b>Reference Cell…</b> escape hatch in the cell picker: pick a cell folder anywhere on
    /// disk and take it into THIS workspace — by reference or by copy, through the same prompt and the
    /// same code as File ▸ Add Cell to Workspace… and the cross-workspace drag. Returns the absolute
    /// cell folder the chosen cell now occupies (unchanged for a reference, the new copy for a copy),
    /// or null when the user cancelled anywhere along the way.
    ///
    /// <para>Run by the CALLER after the picker has closed, never from inside it: the flow shows
    /// modal dialogs of its own on the same owner window.</para>
    /// </summary>
    Task<string?> ReferenceExternalCellAsync();

    /// <summary>False when there is no workspace to take a cell into — the picker then offers its
    /// plain folder-browse instead of the reference flow.</summary>
    bool CanReferenceExternalCell { get; }

    /// <summary>brief-artsch-8 R-as8-5: Show in Artwork for a <c>FromArtwork</c> component — opens the layout its
    /// schematic was created from and marks the component's anchor there. A host with no layouts does nothing.</summary>
    void ShowInArtwork(SchematicDocument doc, EditableComponent comp) { }

    /// <summary>brief vendorlib-10 R-vl10-1a: the schematic toolbar's Export to Library… — File ▸ Export to Library… on
    /// this document. A host with no workspace does nothing.</summary>
    Task ExportToLibraryAsync(SchematicDocument doc) => Task.CompletedTask;

    /// <summary>brief vendorlib-12 R-vl12-2a: Edit Vendor Model… — the placed model's own schematic opened as its own
    /// document tab, never pushed into. A host with no workspace does nothing.</summary>
    void EditVendorModel(SchematicDocument doc, EditableComponent comp) { }

    /// <summary>brief vendorlib-12 R-vl12-3: Copy to Workspace… — the placed model copied into the open workspace as an
    /// ordinary cell, then the question whether to point this instance, every instance of the part in this schematic,
    /// or none at the copy. A host with no workspace does nothing.</summary>
    Task CopyVendorModelToWorkspaceAsync(SchematicDocument doc, EditableComponent comp) => Task.CompletedTask;

    /// <summary>One line in the status line — a key that does nothing says why there (brief vendorlib-12 R-vl12-1).</summary>
    void ShowStatus(string text) { }
}
