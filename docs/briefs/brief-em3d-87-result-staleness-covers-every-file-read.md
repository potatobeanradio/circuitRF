# Brief 87 — A 3D result is stale when ANY file it was solved from has changed, not only the `.c3d`

**Tag:** `R-em3d87-n` · **Series:** found by the thermal series' second review. It is not thermal-specific: every 3D run
(Palace, openEMS, thermal) shares the mechanism.
**Area:** `src/Design/ThreeD/C3dRunDocument.cs`, `src/Design/ThreeD/C3dElaborator.cs`, `src/Design/Em3d/Em3dRunService.cs`,
`src/Design/Thermal/ThermalRunService.cs` (submodel reuse), `src/Design/Thermal/ThermalCircuitLink.cs`,
`src/Ui/ThreeD/C3dEditorViewModel.Simulate.cs`, `src/Cli/RenderEm3dField.cs`, `src/Cli/` (`em`), `tests/`
**Depends on:** 49 (the stale banner, R-em3d49-5b), 79 (a circuit-driven thermal run's stamp), 84 (`render --field`'s note)
· **Blocks:** —

---

## 0. The short answer

"Has the model changed since this run?" is answered today by comparing **the `.c3d`'s own text** (`C3dRunDocument`,
through `SerializeForRun`) with a copy kept in the run's directory. A 3D view is rarely one file. It places layouts
(`.clay`, and through them `.wBond` wire designs and PCells), nests other 3D views, and resolves a technology (`.ctech`)
and material libraries (`.cmat`). A circuit-driven thermal run also reads a schematic and its sub-cells. **An edit to any
of those leaves the result looking current.** The fields are drawn on geometry the model no longer has, with no banner.
`render --field` prints no note, and a thermal **submodel** reuses a whole-model solution that is out of date (below).

## 1. What is wrong, precisely

| Path | What it compares | What it misses |
|---|---|---|
| Editor stale banner (`RefreshFieldsStale`, per plot since the review) | the kept `document.c3d` vs the open `.c3d` | every placed `.clay` / `.wBond`, a nested `.c3d`, the `.ctech`, the `.cmat` libraries |
| `render --field` (`C3dRunDocument.Check`) | the same | the same |
| Circuit-driven thermal (`ThermalCircuitLink.Staleness`) | the top schematic **as extracted** (its `.cnl` text) and the S-parameter file | an edit inside a **sub-cell** of that schematic, which changes the elaborated circuit and not the top `.cnl` text |
| Submodel reuse (`ThermalRunService`, "reused when newer than the document") | the stored global field's file time vs the `.c3d`'s | everything above. A layout edit after the whole-model run leaves the submodel's cut faces fixed to the old answer, **silently** |
| `circuitrf em` | writes no `document.c3d` at all | so a CLI-made result can never be called stale by anything |

## 2. What to build

1. **The elaboration names what it read.** `C3dElaboration` gains `FilesRead`: every file the elaborator opened, as full
   paths. That means the `.c3d`, each placed `.clay` and its referenced cells, each `.wBond` design, each nested `.c3d`,
   the technology and each material library it looked through (`ThermalMaterials` looks through libraries at run time;
   those reads count too). `SpiceCellImport.FilesRead` is the precedent for the shape.
2. **A run keeps a manifest, not just the document.** Beside `document.c3d`, `inputs.json` holds
   `{ path (relative to the workspace when inside it), SHA-256 }` for every file in `FilesRead`, plus (circuit-driven
   thermal) every file the schematic's elaboration read. Replace the top-level-only schematic hash with this. Both the GUI's
   Simulate and `circuitrf em` write it, **from `src/Design`**, so the two cannot diverge. Moving the `document.c3d` write
   out of the view model is part of this.
3. **One staleness function.** `C3dRunDocument.Check(runDir, doc)` returns *which* inputs changed:
   "`Board.clay` changed since this run", not just "the model changed". The banner, the per-plot text, `render --field`'s
   note and `explain` all read it. A file is re-hashed only when its time or size moved (the cache
   `ThermalCircuitLink.Staleness` already has).
4. **Submodel reuse reads the manifest.** A stored whole-model solution is reused only when its manifest matches the
   current inputs. Otherwise the From setup is solved first and the notes say why.
5. A run made before the manifest existed has none, and nothing is claimed. That is today's rule for a missing
   `document.c3d`.

## 3. Gates

- Place a layout in a `.c3d`, run, then edit the `.clay` (move a pad). The banner names the `.clay`; `render --field`'s
  note names it; `circuitrf em` followed by the same edit reports the same.
- Edit a material's k in the workspace's `.cmat` library after a thermal run: stale, naming the library.
- A circuit-driven thermal run, then edit a sub-cell of its schematic: stale, naming the sub-cell.
- A submodel with a stored whole-model result, then edit the placed layout. The submodel run re-solves the From setup
  first (a note says so) and does not reuse the old field.
- An edit to a field plot only (display) still makes nothing stale (R-em3d83-2).

## 4. Scope

Not here: making results *follow* a change (they are re-run, never re-mapped); staleness for planar (`.cem`) runs, whose
provenance is SnP-side and already hashes the layout. Check that claim before relying on it.

On completion, record findings in `src/Design/RESOLVED.md` — not in any CLAUDE.md.
