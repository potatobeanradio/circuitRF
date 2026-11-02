# Brief 7 — the Palace backend: `.geo` → Gmsh → Palace → `.sNp`

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d7-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §4.2 (Palace section), §4.5, §5.2, §5.3, §6.1, §6.2 Route A, §6.4, §10 Validation
**Area:** `src/Design/Em3d/GmshGeoWriter.cs`, `PalaceConfigWriter.cs`, `PalaceRun.cs`,
`Em3dRunService.cs` (all new), `src/Design/Layout/Em/EmRunService.cs` (the delegation),
`src/Cli/CliEntry.cs` (`RunEm`), `src/Ui/Layout/Em/EmSetupEditorViewModel.cs` (the solver picker)
**Depends on:** 1, 3, 6 (4 for the wire gates) · **Blocks:** 10

---

## 0. What this brief delivers

```
circuitrf em via.cem                      # Solver3D: Palace
  → Em3dGenerator            (brief 3)    Em3dProblem
  → GmshGeoWriter                         via.palace/model.geo        (deterministic text)
  → gmsh                                  via.palace/model.msh + entities.txt
  → entity check (§3)                     every named object got exactly what it expected
  → PalaceConfigWriter                    via.palace/config.json      (deterministic text)
  → palace                                via.palace/out/port-S.csv
  → read back                             results/via.palace.s2p + via.palace_em.npy
```

The same thing happens from the `.cem` panel's Simulate, because both reach it through
`EmRunService.Run` (overview §5).

---

## 1. `R-em3d7-1` — the door, the verb and D1

**`R-em3d7-1a`** `EmRunService.Run` delegates to `Em3dRunService.Run` when `Solver3D` is `Palace`,
and returns the same `EmRunResult` with the same statuses (`Ok`, `Refused`, `NoLayout`,
`EngineError`, `Cancelled`). No caller branches. A planar setup's path through `EmRunService` is
**unchanged, line for line**.

**`R-em3d7-1b` D1: the verb is `em`** (overview §3), with `--solver palace|openems` overriding the
setup's field for this run only and never writing it back. A sibling verb would split one format's
runs in two (§4.6). If the owner decides otherwise, this is the one requirement that changes.

**`R-em3d7-1c`** Exit codes are `em`'s existing ones: a refusal exits 1 with the run service's own
sentence, and cancellation exits 130 (CLAUDE.md, the `em` verb).

---

## 2. `R-em3d7-2` — the `.geo` writer

**`R-em3d7-2a` Text, written by circuitRF, run by the `gmsh` executable** (§6.1). No Gmsh library,
no API, and no Python. Brief 6's firewall test already forbids them.

**`R-em3d7-2b` Byte-deterministic** (overview rule 2): `"R"`-formatted doubles with the invariant
culture, `\n` line endings on every platform, solids in construction order, and names in the
problem's order. The same problem produces the same bytes.

**`R-em3d7-2c` Conductors are voids; only dielectrics and air are meshed** (overview §1e, F0 Q6):
- `SetFactory("OpenCASCADE")`, then each dielectric/air/body solid as an OCC primitive, extruded
  polygon or cylinder.
- **Make volumes disjoint by construction order before fragmenting**: each solid is cut by every
  higher-order solid it overlaps (`BooleanDifference{...}{... Delete;}`), then each non-sheet
  conductor is cut out of everything, leaving a void whose surface is the conductor's boundary.
- **Sheets** (brief 3 §5f) are `Plane Surface`s embedded in the interface they lie on.
- One `BooleanFragments` over all volumes and sheets, which only imprints shared faces, with
  `Geometry.OCCBooleanPreserveNumbering = 1`.

**If F0's Q5 found this recipe does not keep tags, follow F0's alternative** and record in
`src/Design/RESOLVED.md` what was tried. Do not fall back to face indices under any circumstances
(§6.4).

**`R-em3d7-2d` Physical groups by name.** Every volume becomes `Physical Volume("<solid name>")`
and every conductor surface, sheet, port sheet and air-box face a `Physical Surface("<name>")`.
The groups are selected with tight `BoundingBox` queries around geometry circuitRF itself placed.
The Palace attribute number for each group is assigned by circuitRF, in problem order, and written
to `groups.json` beside the mesh. That file is the mapping §6.1 calls circuitRF's.

**`R-em3d7-2e` Mesh size** from the `.cem`'s Palace section (§6): a maximum element size per
material in wavelengths at the top frequency, a finer size near sheets' edges and port sheets
(Gmsh `Field`s), and a grading ratio. **An initial mesh only.** Palace's adaptive refinement
converges the answer (§6.1).

**`R-em3d7-2f`** The script ends by writing its own **entity table** (`Printf` to `entities.txt`):
per physical group, the name, dimension and count of entities. It then meshes (`Mesh 3;`) and saves
in the mesh format F0 found the pinned Palace reads.

---

## 3. `R-em3d7-3` — running Gmsh, and checking what it made

**`R-em3d7-3a`** Run `gmsh model.geo -3 -o model.msh` (flags per F0) from `SolverDiscovery.Gmsh`'s
absolute path, in the run directory, with the process tree under `RunHost`'s `RunControl`. It
reports progress by lines read and is **cancelled by killing the tree**.

**`R-em3d7-3b` The entity check, which is §6.4's rule made executable.** circuitRF knows, per named
object, how many entities it expects: one volume per dielectric region, one or more surfaces per
conductor void, exactly one per port sheet, and six air-box faces. After Gmsh runs, it compares
`entities.txt` against the expectation. **Any named object with zero entities is a refusal naming
the object.** A count different from expectation is a refusal that names both counts. This check is
the only thing standing between a changed geometry and a boundary condition silently landing on the
wrong face, so it is never downgraded to a warning.

**`R-em3d7-3c` Mesh reuse.** The run directory keeps `model.geo`'s SHA-256 next to `model.msh`. An
unchanged `.geo` skips Gmsh. That is what determinism buys (overview rule 2). The gate counts Gmsh
invocations.

**`R-em3d7-3d`** A Gmsh failure is reported with **Gmsh's own output verbatim** (the last error
lines), and the full log path. There is no retry and no fallback.

---

## 4. `R-em3d7-4` — the Palace configuration and run

**`R-em3d7-4a` Written against the pinned version's JSON schema** (§5.3; F0 Q10). The schema file
extracted in F0 is committed under `testdata/em3d/palace-schema/<version>.json`, and the writer's
golden output is validated against it in a test. Key names come from that schema, never from
memory or an example on the web. Palace states its configuration is not compatible across versions.

**`R-em3d7-4b`** Content: problem type *driven*; the mesh and its length unit (metres); a material
per dielectric volume attribute (εr or tensor, tanδ, μr); a **finite-conductivity** boundary on each
conductor void surface (σ at the operating temperature) and on each sheet (with its thickness);
PEC and absorbing air-box faces per brief 3 §6; a **lumped port** per port, with its attribute,
direction and resistance/reactance from Z0; the frequency sweep; and the Palace section's solver
settings (element order, adaptive tolerance, sweep tolerance). Deterministic, like §2b.

**`R-em3d7-4c`** Run through `SolverDiscovery.Palace`'s absolute path, with the process count from
the setup's existing `EmSolveCores` setting (planar's), so one knob means one thing. Under
`RunControl`, killed as a tree on cancel. The version and capability checks (brief 6) run **before**
Gmsh, so a refusal costs seconds.

**`R-em3d7-4d` Read back by column name**, never by column position: `port-S.csv`'s header per F0,
converting Palace's magnitude and phase representation to complex S. A missing expected column is
an `EngineError` naming it.

**`R-em3d7-4e` A failed or cancelled run produces no `.sNp`.** A partial `port-S.csv` from a killed
run is never promoted to a result.

---

## 5. `R-em3d7-5` — where results land (§4.5; overview §1j)

**`R-em3d7-5a` 3D results carry the solver in the name**: `<key>.palace.sNp`, and the `.npy`
dataset key `<key>.palace` + `NpyKeySuffix`. The run directory is `<results>/<key>.palace/` and is
**kept**: geometry script, mesh, groups, config, the solver's log, its CSV. A user can re-run it by
hand, and F2's viewer will read the fields from it.

**`R-em3d7-5b` A planar result's path does not change by one byte.** Gate 8.

**`R-em3d7-5c`** `-o` (`SnpOutputPathOverride`) moves the Touchstone only, exactly as for planar.

**`R-em3d7-5d`** The `.sNp` carries `EmSnpProvenance`'s header with the solver, its version, the
mesh's element count (initial and final), adaptive iterations, and the operating temperature. That
is the planar provenance extended, not a second header format.

**`R-em3d7-5e`** Whichever backend brief is built **first** (this or brief 9) owns §5a. The other
reuses it.

---

## 6. `R-em3d7-6` — the `.cem`'s Palace section, and the panel

**`R-em3d7-6a`** The `Palace` object brief 3 declared empty gains: `MaxElementWavelengths` (per
material default), `EdgeRefinement`, `Grading`, `ElementOrder` (default 2), `AdaptiveTol`,
`AdaptiveMaxIterations`, `SweepAdaptiveTol`. Every field is nullable with defaults in one place,
omitted at default, and described by the generated reference page.

**`R-em3d7-6b` The panel** (`EmSetupEditorViewModel`): a *Solver* picker (Planar / Palace / openEMS)
and, when Palace is picked, its section's fields. **Nothing that affects the answer lives only in the
panel** (R-em-11). **Pixels are not verified from this session**; say so in the completion note and
list what the owner should look at.

---

## 7. Gate

`tests/Ui.Tests/Em3d/PalaceBackendTests.cs`. Writer tests need nothing installed. Gmsh tests skip
without Gmsh, and Palace tests skip without Palace (overview §1i).

1. **Writer goldens.** `model.geo`, `groups.json` and `config.json` for brief 3 gate 1's microstrip
   and gate 3's via transition, byte for byte against committed goldens, on every platform.
2. **Schema.** Both `config.json` goldens validate against the committed Palace schema.
3. **Entity check, planted.** Hand-edit a generated `.geo` so that one port sheet selects nothing:
   the run refuses and names the port. *(Gmsh.)*
4. **Entity check, real.** The via transition meshes and every named object gets its expected count.
   *(Gmsh.)*
5. **Mesh reuse.** Two runs with no change invoke Gmsh once (counter). *(Gmsh.)*
6. **Closed form** (§10 Validation). A **homogeneous stripline** generated from a `.clay`: the
   group delay of S21 equals ℓ·√εr/c within 1 %, and |S11| is consistent with Cohn's closed-form Z0
   for the strip against the 50 Ω ports. Compute both expected values in the test from the
   formulas, never from a circuitRF run. *(Palace; tag Benchmark if > ~5 s.)*
7. **F0 reference.** circuitRF's generated via transition against F0's hand-written Palace result
   (`testdata/em3d/f0/B-via/palace/`): |S21| within 0.05 dB and ∠S21 within 1° over the band, or the
   tolerance F0's own adaptive convergence spread justifies, citing it. *(Palace; Benchmark.)*
   *(When brief 4 lands: the same for case A against F0's hexagon-with-foot run.)*
8. **Planar path unchanged.** `ResolveSnpPath`/`ResolveNpyKey` for every `.cem` in the repo is
   identical to before.
9. **CLI == in-process.** `em` as a process produces the same `.sNp` as `EmRunService.Run`, apart
   from the provenance timestamp (the `EmCliVerbTests` pattern). *(Palace.)*
10. **Cancellation.** Cancel during Gmsh and during Palace: exit 130, no `.sNp`, and no solver
    process left running (check the process table for the child PID).
11. **Refusals before work.** Unvalidated Palace, or Gmsh missing: refused with brief 6's text, and
    Gmsh is invoked 0 times.
12. **One door.** Source scan: nothing but `EmRunService` calls `Em3dRunService`.

## 8. Scope

- **Driven S-parameters with lumped ports only.** Electrostatic/magnetostatic, eigenmode and wave
  ports come in the follow-on series.
- **No field display.** The field files stay in the run directory for F2.
- **No local location other than native.** The Linux subsystem, containers and remote runs come in
  the next series (overview §4).
