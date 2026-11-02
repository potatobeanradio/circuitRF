# Brief EMW-1 — Spike: what EMerge's wave ports give, and whether `ModalTerminalTransform` applies

**Tag:** `R-emw1-n` · **Series:** EMW (see `brief-emw-0-overview.md`) · **Depends on:** EMI-5
**Holds:** EMW-2, EMW-3 (not briefed in full until this spike's go/no-go names what they build)
**Area:** `tools/emerge-wave-spike/` (a Python harness run in circuitRF's installed EMerge environment, plus a small C#
or Python check against `ModalTerminalTransform`'s fixtures); scratch output under its git-ignored `runs/`; fixtures in
`testdata/em3d/terminal/emerge-modal/`; findings in `src/Design/RESOLVED.md` § "EMerge wave ports — EMW-1".

---

## 0. Rules for the spike

- **Short runs.** Each case at one coarse and one finer mesh, a handful of frequencies. No 20-minute sweeps; probe one
  frequency first.
- **References are independent of circuitRF**: closed forms (microstrip/stripline/coax Z₀ and ε_eff, Cohn's coupled
  stripline), then Palace's and openEMS's committed results in `examples/3D Wave Ports/expected-numbers.json`.
- Every number recorded with the mesh, element order, EMerge version, and the sparse solver it used.

## 1. The cases

| | Case | Why | Reference |
|---|---|---|---|
| A | a single 50 Ω microstrip thru with a wave port at each end | the one-mode path, renormalisation to Z₀, reference-plane shift | closed-form Z₀, ε_eff; electrical length |
| B | **Coupled Microstrip** (the example's two strips of different widths, two-terminal wave port per end) | non-degenerate modes on a shared face — what Palace does with the K correction | Palace's and openEMS's committed terminal S |
| C | **Pair** (two strips in a homogeneous fill between ground planes) | **degenerate** modes — what Palace cannot do | Cohn's closed form; openEMS's committed S |
| D | **Launch**'s coax wave port | coax: EMerge's analytic coax port vs its modal port | 50.02 Ω closed form; Palace's committed result |

## 2. The questions (answer each in `RESOLVED.md`, with the evidence)

**R-emw1-1 — multimode indexing.** How EMerge numbers N modes of one port in its S matrix and port properties; how to
excite each mode on its own; how Z₀ and β per mode are read back. Write the harness so it reads them by the documented
calls, not by the internal layout.

**R-emw1-2 — the boundary term per mode.** Does every mode of a multimode port get its own launch/absorb term (then the
transform's **K = 1**), or one shared term like Palace's (then K as for Palace)? Decide from the case-B numbers: run the
transform with K = 1 and with K = Re k_active / Re k_m, and see which matches the references.

**R-emw1-3 — degenerate modes.** On case C: with the alignment call (align to the strips' axes, or to each terminal's
own line), is the modal basis **stable across a sweep and across meshes**? Measure reciprocity of the modal S over a
sweep (Palace's failure mode) and the transform's Gram fit residual. If stable, does the transform with G fitted give
Cohn's answer? If the basis is stable *and* power-orthogonal, G = 1 and the fit is unnecessary.

**R-emw1-4 — V.** The driver computes each terminal path's line integral of the **total** E for each excitation, at the
port face (or the reference plane). Check it on case A against the mode's own voltage (V² = 2·P·Z_PV for the PV
definition) to the transform's `PowerConventionLimit` (1e-3), and decide the quadrature (points per path, order).

**R-emw1-5 — Z_PV convention.** Confirm EMerge's PV impedance is the same definition `ModalTerminalTransform` assumes
(|T_V,ii|² against Z_PV after the fit, the check `PalaceTerminalS` makes). A mismatch here is a factor-of-two class
error that looks plausible.

**R-emw1-6 — frequency behaviour.** Multimode ports with the adaptive (rational-fit) sweep: does the fit hold across
mode changes, or does a terminal-group setup need the plain point sweep? And does a face with mixed dielectrics (case
B's microstrip) need EMerge's per-frequency mode recalculation?

**R-emw1-7 — case D.** Analytic coax port vs modal port vs circuitRF's lumped annulus: which matches 50.02 Ω and
Palace's committed Launch result, and at what cost.

## 3. Deliverable: go/no-go per case

A table in `RESOLVED.md`:

| Case | Go? | Transform as used (K, G) | Agreement with reference | Cost (tets, time, memory) |
|---|---|---|---|---|

with EMW-2's and EMW-3's scope stated from it: which port kinds EMerge gets, whether `ModalTerminalTransform` is reused
unchanged, reused with a K/G option (add the option as a parameter, Palace's call site unchanged and its tests
byte-identical), or not needed. **A no-go is a complete answer**: EMerge then keeps refusing that port kind by name, and
the docs say which solvers run it.

## 4. Fixtures

For each go case: the per-frequency inputs to the transform (modal S, V, Z_PV, kₙ) as committed `.npy`, so EMW-3's
tests run the transform on EMerge-shaped data with nothing installed — as `palace-modal/` does for Palace.
