# Brief DFR27-1 — TESTPAD: a cut-out that behaves like one, and rows a designer can pick

**Series:** designer feedback round 27 (2026-10-25), follow-up to DFR26-7/DFR26-8. · **Tag:** `R-dfr27-1-<n>`
**Area:** `src/Core/Devices/TestPadModel.cs`, `TestPadFormulas`, `ComponentModelFactory.ReadTestPadGeometry`;
`src/Design/Schematic/TestPadInjection.cs`, `SubstrateResolver.ResolveTestPad`; the parameter dialog
(`src/Ui/ViewModels/ParameterRowViewModel.cs`, `src/Ui/Views/Dialogs/ParameterEditorDialog.axaml`).
Findings go in `src/Core/RESOLVED.md` and `src/Ui/RESOLVED.md`.
**Tests:** `tests/Core.Tests/Devices/TestPadModelTests.cs`, `tests/Ui.Tests/Layout/TestPadRound26Tests.cs`.

Already done in round 27 (do not redo): `PadLayer`/`ViaFrom` are conductor pickers; an emptied row whose default is
empty now commits (`ComponentTypeRegistry.HasEmptyDefault`); a side-fed pad stamps its capacitance.

---

## 0. What the designer saw

Sweeping `ClearD` in 1 mm steps on a pad with `ClearLayers = Inner 8` (D 1.5 mm, drill 0.3 mm, a 1.1 nH series L in
front of it), |S21| at 2.44 GHz got WORSE from 0 to 1 mm and then better. Reproduced from the CLI:

| ClearD (mm) | 0 | 0.5 | 1 | 1.5 | 2 | 3 | 6 |
|---|---|---|---|---|---|---|---|
| Re S11 | −0.2138 | −0.2324 | −0.2216 | −0.2196 | −0.2187 | −0.2178 | −0.2169 |

The cause is structural, not a constant:

- **`ClearD` never reaches the pad's own capacitance.** `Hpad` is the dielectric to the nearest conductor NOT in
  `ClearLayers`, chosen at extraction from the stackup alone. Naming Inner 8 moves the pad's plate to Inner 7 for any
  `ClearD`, including one smaller than the pad.
- **`ClearD` only sets the barrel's clearance** in a cleared plane (`Clp`). At `ClearD = 0` that clearance is smaller
  than the drill, `ln(b/a)` is negative, and the barrel term is nonsense — the 0 → 0.5 mm step above.
- So `ClearD = 0` means "no hole" to a designer and "a hole of any size" to the model.

## 1. What to do

### R-dfr27-1-1 — The pad's capacitance depends on `ClearD`

For each cleared plane on a pad's side, the pad faces it over the annulus outside the hole and faces the next plane
through the hole: area-weight the two plates (`(D² − ClearD²)/D²` at the cleared plane's distance, the rest at the
deeper plane's), plus the rim term to the hole's edge where `ClearD` is near `D`. The injection must therefore pass the
cleared plane's distance as well as the deeper one (a new indexed parameter beside `Hpad`, as `Zp`/`Tp` are). With
`ClearD ≥ D` the result must join today's value continuously.

- `ClearD = 0` (or below the via's antipad) on a named plane means **that plane is not cleared**: the model treats it
  as an ordinary plane, and the run says so in one Messages line naming the plane.
- The barrel's clearance in a cleared plane is `max(ClearD, AntipadD)`.

Gate: on the shipped ten-layer stackup, D 1.5 mm, `ClearLayers = Inner 8`, the pad's capacitance is non-increasing in
`ClearD` over 0…3·D in 0.1 mm steps; at `ClearD = 0` it equals the uncleared pad's to 1 %; at `ClearD = D + 0.5 mm` it
is within 25 % of an axisymmetric field solve of the same geometry (the method DFR26-8 used, `src/Core/RESOLVED.md`).

### R-dfr27-1-2 — `ClearLayers` is a checklist of the stackup's conductors

It names several conductors, so a single picker does not fit: a drop-down of the technology's conductors with a
check box each, writing the same comma-separated text the row holds today (old files read unchanged). A name in the
text that is no longer a conductor shows checked and flagged, as the layer picker flags a missing name
(`LayerChoiceMissingWarning`). Unchecking every box writes empty. `src/Ui/CLAUDE.md`'s picker rule applies.

### R-dfr27-1-3 — Rows that do not apply to the chosen `Feed` are disabled

With `Feed = Via`, `FeedW` does nothing; with N/E/S/W, `ViaFrom`, `Drill`, `InnerPadD` and `AntipadD` do nothing. The
designer could not tell what `ViaFrom` was for. Grey out the rows that do not apply (keep their values, so switching
back restores them) with a tooltip saying why, and reword `ViaFrom`'s description to start from what a designer knows:
the layer the trace arrives on, where the via starts.

## 2. Not in scope

The 3D solves' own ±0.15 pF scatter between hole sizes (the report's Ø2.4 vs Ø2.0) is mesh scatter, not this.
