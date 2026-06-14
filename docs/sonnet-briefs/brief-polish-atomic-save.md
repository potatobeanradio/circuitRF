# Brief: polish-atomic-save — atomic writes for .csch / .csym / .ccell

**Goal.** Make schematic, symbol, and cell file saves atomic (temp-write + rename), matching the
existing `.cws` behaviour, so a crash mid-write never leaves a half-written/corrupt file. This is
the document the user spends the most time in, so it matters most for `.csch`.

Authority: laundry-list "atomic save" item. Size: **S**.

## Background (confirmed)

- `.cws` is already atomic: `WorkspacePersistence.SaveToFileAtomic` = write `path + ".tmp"` then
  `File.Move(tmp, path, overwrite: true)`.
- `.csch` (`SchematicPersistence.SaveToFile`), `.csym` (`SymbolPersistence.SaveToFile`), and `.ccell`
  (`CellPersistence.SaveToFile`) all use plain `File.WriteAllText` — **not** atomic.

No cell-membership bookkeeping is needed: the temp file is written in the **same directory** as the
target and renamed in place, so it always lands in the correct cell subfolder. (The "recover to the
right directory" concern the user raised is a separate RecoveryManager matter — out of scope here.)

## Step 1 — shared atomic-write helper

Add a tiny framework-free helper (new file `src/Ui/Schematic/AtomicFile.cs`):

```csharp
namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Crash-safe text file write: serialize to a sibling temp file, then atomically rename over the
/// target. A crash mid-write leaves the previous file intact. Temp lives in the SAME directory as
/// the target so the rename stays on one volume (atomic) and lands in the right place.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
```

## Step 2 — route the three persistence classes through it

**`SchematicPersistence.SaveToFile`** — replace the `File.WriteAllText` call; **keep** the
`SchematicDirectory` assignment that follows it:

```csharp
public static void SaveToFile(string path, SchematicEditModel model, string cellName = "",
                              double panX = 0, double panY = 0, double zoom = 1.0)
{
    AtomicFile.WriteAllText(path, Serialize(model, cellName, panX, panY, zoom));
    // (unchanged) record the on-disk directory so CellRef relative-path resolution works.
    model.SchematicDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
}
```

**`SymbolPersistence.SaveToFile`** — replace its `File.WriteAllText(path, Serialize(...))` with
`AtomicFile.WriteAllText(path, Serialize(...))`.

**`CellPersistence.SaveToFile`** — replace `File.WriteAllText(path, Serialize(cell))` with
`AtomicFile.WriteAllText(path, Serialize(cell))`.

Optionally refactor `WorkspacePersistence.SaveToFileAtomic` to call `AtomicFile.WriteAllText` too, so
all four formats share one implementation (keep the public `SaveToFileAtomic` name — callers use it).

## Notes / edge cases

- `File.Move(overwrite: true)` replaces the target; on the same directory this is a same-volume rename
  (atomic on Windows/macOS/Linux).
- A stale `.tmp` left by a crash is harmless — the next save truncates and overwrites it.
- The clipboard/serialize-only paths (`Serialize`, `SerializeSelection`) don't touch disk — leave them.
- These writes already (or should) log to the Messages panel via their callers; that coverage is
  handled in the Messages briefs, not here.

## Acceptance

- Saving a `.csch`, `.csym`, and `.ccell` writes via temp+rename (verify a `*.tmp` appears transiently
  / no `.tmp` remains after a normal save; the target updates).
- Save → reload round-trips identically (no format change).
- Build clean; existing persistence tests pass.

## Out of scope

- Recovery-to-cell-directory bookkeeping (separate RecoveryManager concern).
- Logging save messages (Messages briefs).
