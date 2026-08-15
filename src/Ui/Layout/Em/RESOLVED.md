# `src/Ui/Layout/Em` — resolved briefs (detail, off the CLAUDE.md growth path)

Completed work's detail lands here instead of `CLAUDE.md`, which stays for durable, still-true
conventions only. Same pattern as `src/Ui/DataDisplay/RESOLVED.md`.

## Every EM-run message respects the layout's own display unit (2026-08-15)

Owner request: "all messages from running an EM sim that reference distance/length need to respect
the units of the `.clay` file." Both kernels' notes were SI-only (kernel B's `SurfaceMesher.Eng`,
kernel A's raw `{v:G4} m`) regardless of what unit the layout itself is drawn and displayed in.

**The engine cannot know about mil/µm/DBU** — the UI firewall forbids `src/Engine` from referencing
`LayoutUnits` — so both kernels' length-bearing entry points (`SurfaceMesher.Mesh`,
`PlanarFeedExtension.Extend`, `PlanarSolve.Run` and its two public helpers, `BoundaryMesher.Mesh`,
`QuasiStaticKernel.Mesh`/`SolveDetailed`) take an optional `SurfaceMesher.PlanarLengthFormat`
(`delegate string PlanarLengthFormat(double metres)`) — a plain delegate, never a UI type, so the
firewall holds. `EmLengthFormat.For(displayUnit, dbuPerMicron)` builds the real one, on this side of
it, from `LayoutUnits.Format` + `LayoutUnits.Suffix`.

**`null` (every existing caller, including the CLI, which has no `.clay` at all) reproduces the
exact pre-2026-08-15 text — asserted, not merely arranged.** Kernel B's default is
`SurfaceMesher.Eng(v) + "m"` (SI engineering notation); kernel A's is its own pre-existing
`{v:G4} m` — the two were never the same convention, and giving them one shared fallback would have
been a silent format change for every existing kernel-A test. **This is why one bug already surfaced
and was fixed while wiring this in**:
`EmMeshOverlayTests.EveryNumberInTheReport_ReachesThePanelUnmodified` compared the panel's (now
formatted) notes against an un-formatted direct kernel call and read as "the panel modified the
engine's notes" — the fix was giving the reference computation the same formatter, not reverting the
panel. Any future "the panel's notes don't match a raw kernel call" report should ask this question
first.

**The two panel entry points (`BuildMesh`, kernel A's own Mesh button; `PreparePlanarMesh` /
`ComputePlanarMesh`, kernel B's) capture the display unit as PLAIN VALUES**
(`EmSetupEditorViewModel._pendingDisplayUnit`/`_pendingDbuPerMicron`, both set in `Refresh()` and
again in `PreparePlanarMesh()`) **rather than the live `LayoutView` reference** — `ComputePlanarMesh`
runs off the UI thread (see its own header for why: `Extract`/`Flatten` read the live, editable
`LayoutView` and must stay on the UI thread, but the mesher itself is poolable), and passing the
live view across that boundary would be exactly the data race that split already exists to avoid.
`EmRunService.RunPlanar`/kernel A's own solve path build the formatter directly from `source.View`
instead, since neither of those runs off-thread relative to `source`.

Gate: `tests/Ui.Tests/Em/EmLengthFormatTests.cs` (3 tests) — the mil round trip, a mil-displayed
layout's mesh note actually reading in mil, and the default-display-unit case matching a
formatter-supplied direct kernel call (not the bare 2-arg one — see the note above).
