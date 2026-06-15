# Sonnet Brief — Net naming priority: Pin beats coincident net label

**Bug:** A cell interface **Pin** sharing a net with a user **net label** is silently mis-extracted. The net
gets named after the *label*, but `Cell.Ports` is built from the *Pin* name, so the elaborator (which binds
parent nets to the body **by port name**) can't match the body net → the port is silently disconnected inside
the cell → floating nodes → singular MNA → "Regularization engaged" at every frequency in S-param analysis.

**Root cause:** `NetExtractor.AssignNetNames` (`src/Ui/Schematic/NetExtractor.cs`) applies names in the order
**ground → net labels → Pin port names (only if unnamed) → auto**, so a label shadows the Pin's port name.

**Fix (owner-specified rule):** net-name priority must be **ground → Pin → label → auto**. Net labels are a
cell-local wiring convenience; a **Pin owns the interface identity**, so any net carrying a Pin is named after
the Pin (for the whole label-unioned node), a label names a net only when **no** Pin is present, and otherwise
an auto name is assigned. A Pin coincident with a differently-named label is **not** a conflict — the Pin
silently wins (the user shouldn't have to keep Pin and label names in sync). Scope-locality is already correct
(`ResolveNet` prefixes non-port body nets with the instance path), so this is purely a naming-priority fix; no
elaborator or editor change.

## Change — `AssignNetNames` in `NetExtractor.cs`
Keep the **net-label loop exactly as-is** (it still performs label↔label conflict detection and the ground
guard), but **move the Pin-name application to AFTER the net-label loop and make it OVERRIDE** label names
(except ground). Minimal diff:

- Today the Pin block runs *before* auto-names with `if (!rootToName.ContainsKey(root))` (fills only unnamed).
  **Replace that guard** so Pins override labels but never override ground:
  ```csharp
  // Pin port names — a Pin OWNS its net's name (beats a coincident label); ground still wins.
  if (pinNetNameMap is not null)
  {
      foreach (var (key, portName) in pinNetNameMap)
      {
          if (!uf.Contains(key)) continue;
          var root = uf.Find(key);
          if (rootToName.TryGetValue(root, out var existing) && existing == "0")
          {
              // Pin sits on the ground net — interface can't bind to the parent. Warn, keep "0".
              conflicts.Add($"Pin net '{portName}' is tied to ground inside the cell; " +
                            $"its interface will not connect to the parent.");
              continue;
          }
          rootToName[root] = portName;   // override any label/auto name
      }
  }
  ```
- Ensure ordering in the method body is: ground → **net-label loop (unchanged)** → **Pin block (above)** →
  auto-name loop (unchanged). The label-vs-label conflict warning still fires (it runs in the label loop,
  before the Pin override), which is correct: two differing labels on one net is independently suspect. Do
  **not** add any pin-vs-label conflict warning.

No change to `BuildPinNetNameMap`, `BuildCellPorts` (already uses the Pin name → `Cell.Ports`), the elaborator,
or the editor's one-label-per-node rule (a single label on a Pin node stays allowed — that's the convenience
being supported).

## Tests
Add to `tests/Ui.Tests/NetExtractorPinTests.cs` (reuse `MakePin`/`MakeResistor`/`Wire`; add net labels via
`model.NetLabels.Add(new EditableNetLabel { Name = "...", X = px, Y = py })` placed at the shared net coord):
1. **`Pin_WithCoincidentLabel_PinNameWins`**: a Pin(Num=1, Name="in") and a net label "mylabel" on the same net
   as a resistor terminal → `R1.NetBindings[0] == "in"` (not "mylabel"); `CellPorts[0] == "in"`; **no**
   conflict added.
2. **`Label_WithoutPin_NamesNet`**: same topology minus the Pin → the resistor net is named "mylabel" (label
   wins when no Pin). (Locks the ground→…→label→auto fallthrough.)
3. **`TwoDifferentLabels_StillConflict`**: two differently-named labels on one net (no Pin) → label-vs-label
   conflict still reported (regression guard that the reorder didn't drop it).
4. *(optional edge)* **`Pin_OnGroundNet_Warns`**: a Pin on a grounded net → net stays "0" and a "tied to
   ground" conflict is added.

Add to `tests/Ui.Tests/NetExtractorHierarchyTests.cs` the **actual regression** (the reported bug):
5. **`CellPinWithCoincidentLabel_BindsThroughToParent`**: build a cell whose interface Pin shares its net with
   a differently-named label (and an internal component on that net); instantiate it at the top with the symbol
   port wired to a parent net (e.g. a Term net). After `Extract` (+ `Elaborate` if the harness does the
   round-trip), assert the cell's internal component connects to the **parent** net (port bound), i.e. the
   body net at the Pin equals the Pin's port name and matches `Cell.Ports` — no floating internal node.

Update any existing Layer-2/Pin tests whose expected net names assumed label-over-pin (there shouldn't be many;
`Pin_NetNamedAfterPort` already expects the Pin name and stays green).

## Gate
- A cell with a Pin + coincident, differently-named label extracts with the **Pin name** on that net; the cell
  instance binds through to the parent; S-param analysis of such a cell no longer reports per-frequency
  regularization (the floating-port singularity is gone). Build 0W/0E; full suite green.

## Not in scope (flag separately)
- **Geometric connectivity** misses (e.g. a cell pin that shows an auto-name like `n2` because its wire isn't
  unioned onto the intended node) are upstream of naming and unaffected by this fix — separate investigation.
- The per-frequency regularization log truncating to the first line of the singular-matrix message (hides which
  nodes float) — separate small brief if wanted.

## On completion
Note the rule in `src/Ui/CLAUDE.md` (and `docs/design/project-file-formats.md` if it documents net naming):
**net-name priority is ground → Pin → label → auto; a Pin owns its net's name; labels are cell-local.**
