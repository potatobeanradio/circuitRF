# Sonnet Brief — SnP Parameter Editor: relative-path Browse + Show (workspace-root based)

Make the SnP component's Parameter-Editor **Browse** and **Show** work with workspace-root-relative
paths, consistent with the engine's relative-path resolution (`brief-snp-relative-path`, landed).

Behavior:
- **Show** ("reveal in OS file manager"): when `File` is relative, resolve it against the **workspace
  root** and reveal the resulting absolute path — exactly as it already does for an absolute path.
- **Browse** (system file picker): after the user picks a file, prefer storing a **relative** path in
  the `File` parameter:
  - file anywhere inside the workspace subtree → always relative;
  - file at most **2** directories above the workspace root → relative;
  - more than 2 directories above the root, or on a different volume/root, or no workspace open →
    absolute.
  - The stored relative path uses forward slashes so it is portable across Windows/macOS/Linux (the
    engine already tolerates `\` and normalizes via `Path.Combine`/`GetFullPath`).

Why workspace root (not the schematic dir): the engine resolves SnP `File` against
`Path.GetDirectoryName(CurrentWorkspacePath)`. Today **Show** resolves against
`EditModel.SchematicDirectory` — inconsistent (a cell-homed schematic's dir is
`<ws>/<cell>/schematic`, not the workspace root). This brief switches Show to the workspace root so
Show reveals the same file the engine loads.

Plumbing: both editor instances — the embedded `PropertiesTool.EditorVm` (via `SetContext`) and the
double-click dialog VM created in `SchematicView.OnComponentDoubleTapped` (via `SetTargetDirect`) —
already hold a `SchematicViewModel` (`_schematicVm`). So carry the workspace root on
`SchematicViewModel` as a lazy provider; neither the dialog launcher nor the editor needs a new
callback. `WorkspaceViewModel` (which owns `CurrentWorkspacePath`) sets the provider.

Scope: new `src/Ui/Schematic/SnpPathPolicy.cs`; `src/Ui/ViewModels/SchematicViewModel.cs`;
`src/Ui/ViewModels/ParameterEditorViewModel.cs`; `src/Ui/ViewModels/WorkspaceViewModel.cs` (wire the
provider) + tests. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green. No engine/Core changes.

Read first: `ParameterEditorViewModel.PickFileAsync` + `RevealSnpFileAsync`;
`SchematicView.OnComponentDoubleTapped` (dialog VM creation); the `SchematicViewModel` fields; and in
`WorkspaceViewModel` the `new SchematicViewModel(` creation sites (there's one in `CheckForRecovery`;
find the others — open/scratch/registry).

## 1. New file `src/Ui/Schematic/SnpPathPolicy.cs` — the relative/absolute decision (pure, testable)

```csharp
using System;
using System.IO;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Decides how to store a picked SnP/Touchstone file path in the `File` parameter, given the
/// workspace root. Prefers a workspace-root-relative path (forward-slash, cross-platform) when the
/// file is inside the workspace subtree or at most <see cref="MaxParentLevels"/> directories above it;
/// otherwise the absolute path. Mirrors the engine's resolution base (the workspace root).
/// </summary>
public static class SnpPathPolicy
{
    /// <summary>Max number of parent levels above the workspace root for which a relative path is kept.</summary>
    public const int MaxParentLevels = 2;

    /// <summary>
    /// Returns the string to store in the `File` parameter for a picked absolute path.
    /// Relative (forward-slash) when within the workspace subtree or ≤ MaxParentLevels above the root;
    /// absolute otherwise. When <paramref name="workspaceRoot"/> is null/empty, or the inputs are not
    /// rootable to a common base, the absolute path is returned unchanged.
    /// </summary>
    public static string ToStored(string absolutePath, string? workspaceRoot)
    {
        if (string.IsNullOrEmpty(absolutePath) || !Path.IsPathRooted(absolutePath))
            return absolutePath;
        if (string.IsNullOrEmpty(workspaceRoot))
            return absolutePath;

        string rel;
        try { rel = Path.GetRelativePath(workspaceRoot, absolutePath); }
        catch { return absolutePath; }

        // Different volume/root → GetRelativePath returns an absolute path → keep absolute.
        if (Path.IsPathRooted(rel)) return absolutePath;
        if (rel == ".") return absolutePath;   // the root dir itself (not a file) — defensive

        // Count leading ".." segments = how far above the workspace root the file sits.
        var segs = rel.Split('/', '\\');
        int up = 0;
        while (up < segs.Length && segs[up] == "..") up++;
        if (up > MaxParentLevels) return absolutePath;

        return rel.Replace('\\', '/');   // portable separators
    }
}
```

## 2. `SchematicViewModel.cs` — carry the workspace root (lazy)

Add (near the other public properties; uses `System`):
```csharp
    /// <summary>
    /// Returns the current workspace root (the directory holding the .cws), or null when no workspace
    /// is open. Supplied by WorkspaceViewModel. Used by the Parameter Editor to resolve/relativize SnP
    /// File paths consistently with the engine (which resolves against the same root).
    /// </summary>
    public Func<string?>? WorkspaceRootProvider { get; set; }

    /// <summary>The current workspace root, or null. Evaluated lazily so it always reflects the
    /// currently-open workspace.</summary>
    public string? WorkspaceRoot => WorkspaceRootProvider?.Invoke();
```

## 3. `ParameterEditorViewModel.cs` — Browse + Show

`PickFileAsync` (Browse): keep the absolute `path` for the on-disk port-count read; store the
policy-chosen path in `File`. Replace:
```csharp
        var fileParam = newParams.FirstOrDefault(p => p.Name == "File");
        if (fileParam is not null) fileParam.Expression = path;
```
with:
```csharp
        // Prefer a workspace-relative path (portable); falls back to absolute per SnpPathPolicy.
        string stored = SnpPathPolicy.ToStored(path, _schematicVm.WorkspaceRoot);
        var fileParam = newParams.FirstOrDefault(p => p.Name == "File");
        if (fileParam is not null) fileParam.Expression = stored;
```
(`using CircuitRF.Ui.Schematic;` is already present. `TouchstoneIO.TryGetPortCount(path, …)` keeps
using the absolute `path` — unchanged.)

`RevealSnpFileAsync` (Show): resolve a relative path against the **workspace root** (was
`SchematicDirectory`). Replace the body's resolve line:
```csharp
        if (!System.IO.Path.IsPathRooted(path) && _schematicVm?.EditModel.SchematicDirectory is { } dir)
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, path));
```
with:
```csharp
        if (!System.IO.Path.IsPathRooted(path) && _schematicVm?.WorkspaceRoot is { Length: > 0 } root)
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path.Replace('\\', '/')));
```
(Absolute paths still reveal directly. When no workspace root is available, a relative path is passed
through as-is — `RevealFileAsync` already swallows failures.)

## 4. `WorkspaceViewModel.cs` — set the provider on every SchematicViewModel

Add a small accessor:
```csharp
    private string? CurrentWorkspaceRoot
        => CurrentWorkspacePath is null ? null : Path.GetDirectoryName(CurrentWorkspacePath);
```
At **every** site that constructs a `SchematicViewModel` (search `new SchematicViewModel(` — includes
`CheckForRecovery`; also the open-document / scratch-new / session paths), set the provider right where
the other post-construction wiring happens (next to `SetPlacementService` / `ComponentPlaced +=`):
```csharp
        vm.WorkspaceRootProvider = () => CurrentWorkspaceRoot;
```
The closure is lazy, so it always reflects the current workspace — correct even when a scratch
schematic is later saved into a workspace, or a workspace is opened after the document. If a single
shared "new SVM" helper exists, set it there once instead of at each call site.

## Tests (`tests/Ui.Tests` or wherever Ui-layer pure helpers are tested — `SnpPathPolicy` is framework-free)
Use platform-appropriate roots (e.g. build with `Path.Combine` so the test is OS-agnostic).
1. **InsideSubtree_Relative:** root `<ws>`, file `<ws>/touchstone/amp.s2p` → `"touchstone/amp.s2p"`
   (forward slashes, no `..`).
2. **RootItself_File_Relative:** file `<ws>/amp.s2p` → `"amp.s2p"`.
3. **OneUp_Relative / TwoUp_Relative:** file one and two dirs above the root →
   `"../amp.s2p"` / `"../../amp.s2p"`.
4. **ThreeUp_Absolute:** file three dirs above the root → the absolute path unchanged.
5. **NullRoot_Absolute:** `workspaceRoot = null` → absolute unchanged.
6. **NotRooted_Input_Unchanged:** a non-rooted `absolutePath` → returned unchanged (defensive).
7. **DifferentVolume_Absolute** (Windows-only or skipped elsewhere): root on `C:\`, file on `D:\` →
   absolute unchanged.
8. **ForwardSlashes:** on Windows, a relative result contains `/` not `\`.

## Gate (manual)
With a workspace open: Browse to a `.s2p` inside the workspace → `File` shows a relative path
(e.g. `models/amp.s2p`); Show reveals it in the OS file manager. Browse to a file 3+ levels above the
workspace → `File` is absolute; Show still reveals it. Move/relativize across the 2-level boundary and
confirm the relative/absolute switch. Confirm a relative `File` set here runs in S-param analysis
(engine resolves the same root).

## On completion
Note in the SnP/relative-path design doc (where the engine resolution is documented): the Parameter
Editor stores workspace-relative `File` paths when the picked file is within the workspace subtree or
≤ 2 directories above the root (else absolute), forward-slash for portability; **Show** resolves
relative `File` against the workspace root — the same base the engine uses.
