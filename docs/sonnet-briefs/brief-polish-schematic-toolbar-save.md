# Brief: polish-schematic-toolbar-save (B15) — toolbar "Save .csch" = ⌘S parity (Save-to-Cell)

**Goal.** The schematic toolbar's Save button currently does an ad-hoc "Save As" that writes the file
but never materializes the document, registers it, or refreshes the tree — so a scratch schematic saved
this way stays scratch + dirty + invisible to the project tree and re-prompts every time, and a
materialized doc gets a spurious file picker. Make the button do exactly what ⌘S does for the active
document (single-doc scope): materialized → write silently to its known path; **scratch → Save-to-Cell
plan dialog** (creates the cell, registers the session + tree). Decision locked with the owner: option (A),
Save-to-Cell.

Size: **S–M**. Files: `src/Ui/Schematic/IHierarchyHost.cs`, `src/Ui/ViewModels/WorkspaceViewModel.cs`,
`src/Ui/Views/Content/SchematicView.axaml.cs`.

## Root cause (verified)

`SchematicView.OnSaveCsch` (code-behind) opens its own `SaveFilePickerAsync` and calls
`SchematicPersistence.SaveToFile(savePath, doc.ViewModel.EditModel, doc.Id, pan, zoom)` directly, then
posts "Saved". It never calls `doc.Materialize()`, never registers a Known File / session
(`RegisterSession`/`NotifySessionSaved`), never adds to `_openDocsByPath`, and never refreshes the tree.
The proper single-doc save already exists in `WorkspaceViewModel` as `SaveSingleDocument(doc, window)`
(scratch → `SavePlanBuilder`/`SavePlanDialog` → `ExecuteSavePlan`; materialized → write to `FilePath` +
`Materialize` + `NotifySessionSaved`), and ⌘S routes to it via `SaveAllDocuments` (SingleDoc scope).

`SchematicView`'s `DataContext` is the `SchematicDocument`, not `WorkspaceViewModel`. The document
already holds the workspace via the injected `IHierarchyHost Hierarchy`. So expose the save through that
interface — no window plumbing needed (the host resolves its own owner window).

(Aside, not in scope: view-state isn't restored on open anyway — `GetOrCreateSession` does
`var (editModel, _, _) = SchematicPersistence.LoadFromFile(...)`, discarding the stored pan/zoom. So
routing through the 3-arg save path loses nothing the user sees. If we ever want reopen to restore the
view, that's a separate item touching both the save call sites and the open path.)

## Fix

### 1. `src/Ui/Schematic/IHierarchyHost.cs` — add one interface member

The file currently has no `using` directives. Add one for `Task`, and the method:

```csharp
using System.Threading.Tasks;

namespace CircuitRF.Ui.Schematic;
```

Inside the `IHierarchyHost` interface, after `OpenCellInNewTab(...)`:

```csharp
    /// <summary>
    /// Saves <paramref name="doc"/> with the same behaviour as ⌘S single-doc scope:
    /// materialized → writes to its known path; scratch → the Save-to-Cell plan dialog.
    /// Registers the file/session and refreshes the project tree. The host resolves the owner window.
    /// </summary>
    Task SaveSchematicDocumentAsync(SchematicDocument doc);
```

### 2. `src/Ui/ViewModels/WorkspaceViewModel.cs` — implement it

Place next to the other `IHierarchyHost` implementations (immediately after the `OpenCellInNewTab`
method is a good spot). It mirrors the `SaveAllDocuments` SingleDoc-schematic branch exactly:

```csharp
    /// <inheritdoc/>
    public async Task SaveSchematicDocumentAsync(SchematicDocument doc)
    {
        var window = ResolveOwner(null);
        if (window is null) return;

        if (!doc.IsDirty)
        {
            Messages.Info("Nothing to save.");
            return;
        }

        await SaveSingleDocument(doc, window);

        // ⌘S single-doc parity: refresh the .cws open-doc snapshot silently.
        if (CurrentWorkspacePath is not null)
            WriteWorkspaceFile(CurrentWorkspacePath, silent: true);
    }
```

`SaveSingleDocument`, `ResolveOwner`, and `WriteWorkspaceFile` all already exist — no other changes in
this file. (`ResolveOwner(null)` finds the window whose `DataContext` is this VM, i.e. the main
WorkspaceWindow; dialogs parent there even if the doc tab is floated — same as ⌘S.)

### 3. `src/Ui/Views/Content/SchematicView.axaml.cs` — delegate from the button handler

Replace the **entire** `OnSaveCsch` method body (the file-picker + `SchematicPersistence.SaveToFile` +
messages) with a delegation:

```csharp
    private async void OnSaveCsch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SchematicDocument doc) return;
        if (doc.Hierarchy is { } host)
            await host.SaveSchematicDocumentAsync(doc);
    }
```

That removes the only use of `SchematicPersistence` in this file; the `using CircuitRF.Ui.Schematic;`
stays (still needed for `SchematicDocument`, `Vm`, etc.). No other edits.

## What this yields

- **Scratch schematic** + toolbar Save → Save-to-Cell plan dialog → cell created, session registered,
  `_openDocsByPath` updated, tree refreshed, tab materialized (bullet clears). Re-saving now writes
  silently to the known path. Identical to ⌘S.
- **Materialized schematic** + toolbar Save → writes to its known `FilePath` with no picker, marks the
  session saved, refreshes `.cws`. (Was: spurious file picker every time.)
- **Clean doc** + toolbar Save → "Nothing to save." (matches ⌘S).

## Verification (demand runtime proof — don't trust "done")

1. New scratch schematic → add a component → click toolbar **Save** → Save-to-Cell dialog appears →
   confirm → the cell shows in the project tree, the tab loses its `•`, and a "Saved" message points at
   the new `.csch`. Click **Save** again → no dialog, writes silently.
2. Open an existing cell schematic from the tree → edit → toolbar **Save** → **no picker**, "Saved" to
   the same path, tab `•` clears.
3. Clean materialized schematic → toolbar **Save** → "Nothing to save."
4. Confirm on disk the new `.csch` exists under `<cell>/schematic/` and `.cws` lists it after save.

## Acceptance

- Toolbar Save behaves identically to ⌘S for the active schematic (materialized = silent known-path
  write; scratch = Save-to-Cell).
- Saved scratch schematics become visible in the tree and stop re-prompting.
- `OnSaveCsch` no longer contains its own file-picker / persistence call.
