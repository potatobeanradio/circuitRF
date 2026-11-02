# Brief — 3D EM, eighth series: wave ports on both solvers, terminal wave ports, and two examples

**Status:** Briefed, not built · **Date:** 2026-10-05 · **Decisions: open, each with a recommended default** (§3); [113-a](brief-em3d-113-a-terminal-port-redo.md) measured 2026-10-05: terminal ports ship on openEMS only (D14), Palace revisited by brief 119; openEMS coax on a cylindrical grid (D15, brief 120)
**Design note:** [`docs/design/em-3d.md`](../design/em-3d.md) §2, §4.4 (reference planes), §5.3 (the FDTD port transform).
**Previous briefs:** 1–111. This series is numbered **112–124**.
**Precedent:** [brief 23](brief-em3d-23-wave-ports-and-eigenmodes.md) (Palace wave ports, mode 1 only, eigenmode),
its findings in `src/Design/RESOLVED.md` § "Wave ports and eigenmodes — brief-em3d-23", and
[brief 70](brief-em3d-70-showcase.md) (the `3D Connector` example).
**Area:** `src/Design/ThreeD/` (`C3dDocument`, `C3dPorts`, `C3dProblemAssembly`), `src/Engine/Em3d/` (`Em3dProblem`,
`FdtdPortTransform`, a new terminal transform), `src/Design/Em3d/` (`PalaceConfigWriter`, `PalaceRun`, `GmshGeoWriter`,
`CsxcadWriter`, `OpenEmsRun`, `Em3dRunService`, `Em3dPortMap`), `src/Ui/ThreeD/` (the port tool, the port rows),
`src/Cli/` (`check`, `explain`, `em`), `examples/`, `docs/user/src/reference/`
**Requirement tag:** `R-em3d<n>-<m>` as before.

---

## 0. The short answer

The owner asked for three things:

1. An example that teaches a **wave port**, on **Palace and openEMS**. Every shipped 3D example uses lumped ports.
2. A wave port where **two conductors end on one face** and **each conductor gets its own port** in the `.sNp`.
3. An example of Palace's **eigenmode** solve: a microstrip in a lidded cavity whose resonance is moved out of band by a
   grounded pillar.

Where those stand today:

| Ask | Today | What this series does |
|---|---|---|
| Wave port, Palace | Works (brief 23). Not in any example. | Nothing new in the engine; brief 117 uses it |
| Wave port, openEMS | **Refused** (`Em3dRunService.PalaceOnlyRefusal`) | Brief 116 builds it |
| Two conductors, one face, one port each | **Refused** (`C3dPorts.Wave`: "a wave port's mode runs between two conductors"); `Mode` is hard-coded to 1 (`PalaceConfigWriter.cs` ~270) | Briefs 114 (model), 115 (Palace), 116 (openEMS) |
| Eigenmode | Works from a `.c3d` (brief 23, used by `3D Package`'s *Lid modes*) | Brief 118: an example only |

When the series is done:

- A wave port on a `.c3d` or `.cem` runs on **Palace, openEMS or Both**.
- A wave port whose face is met by **N signal conductors and one reference** (a ground, a shield) is **one port with N
  terminals**. Each terminal is its own numbered port in the Touchstone file, referenced to its own Z0. These are
  **terminal** S-parameters (voltage and current on each conductor), not modal ones. **They run on openEMS only**
  (D14); Palace refuses a multi-terminal port by name until brief 119's spike says how it can join.
- Two new example workspaces: **3D Wave Ports** (the connector launch with its back end open and a coax wave port, plus
  a coupled pair with a two-terminal wave port at each end; the launch on both solvers, the pair on openEMS) and **3D Eigenmode** (the cavity, with
  and without the pillar).

### The rules this series adds

> **1. A terminal is a port.** Everywhere a port number is used (the `.sNp`, the port map, field drives, `explain`), a
> terminal of a multi-terminal wave port is exactly an ordinary port. Nothing downstream of the solver learns that
> terminals exist.
>
> **2. One definition of a wave.** Every S-parameter this series produces comes out of one formula, the one
> `FdtdPortTransform` already uses: `S = √y·(U − Z₀·I)·(U + Z₀·I)⁻¹·√z`. Palace's terminal ports get their `U` and `I` from
> modal data (brief 115); openEMS's from probes (brief 116). No second wave definition is written.
>
> **3. A single-conductor wave port is unchanged.** Every existing `.c3d`, `.cem`, Palace configuration, Gmsh script and
> golden stays byte-identical. Terminals are written only when a port has two or more of them.

---

## 1. Things that are not obvious, resolved here once

**a. Palace's wave-port S is modal, and Palace has no terminal S at all** (schema 1-7-0; `waveportoperator.cpp`
`GetSParameter` projects onto a unit-power mode; `port-I.csv` covers lumped ports only, `postoperatorcsv.cpp` ~1022).
So a per-conductor result cannot be read from Palace. It has to be **computed** from what Palace does write.

**b. Measured (113-a): Palace cannot give a coupled pair terminal ports, neither of the two documented ways.**
Palace's only multi-line example (`examples/cpw`) gives each line its own port rectangle, the two meeting at the
midline, and its guide says touching ports treat each other's edges as PEC in their mode solves. So each port's mode is
half of the ODD mode. On the air stripline pair (W 1.2, S 0.4 mm, b 2 mm) that setup is off by 1.17 dB on the thru and
13.4 dB on the far-end coupling (|ΔS| 0.12), and the error does not move across three meshes (46 k → 2.07 M
unknowns). It falls to 0.05 dB only between S = 2W and 4W, where the true near-end coupling is already below −46 dB; the
microstrip pair fails the same way (thru 1.5–1.9 dB). Several modes on ONE shared face (`Mode` 2, `"Active": false`, as
the config reference describes them) returns the degenerate TEM pair in a different mixture at each end, with σ_max 1.27.
That is upstream issue #328, still open. The maintainers' suggested route (#251, #328) is a symmetry split with a PEC
edge for the odd mode and a PMC edge for the even mode; Palace has no documented key for a PMC shared edge.
`src/Design/RESOLVED.md` § "Terminal wave ports — brief-em3d-113-a". **Owner (2026-10-05): terminal ports on openEMS only (D14); brief 119 revisits Palace.**

**c. What still holds on Palace (re-measured).** `V_wp` in `port-V.csv` is the line integral of the total field and
ignores `Offset`, while S moves by kₙd; |V_wp|² = Z_PV to 9e-9 (peak power convention, factor 1). These made the §1c
transform of the first draft possible, but it needs a mode basis that spans the terminals, and neither documented setup
gives one for a coupled pair.

**d. Neither self-check sees the failure.** Reciprocity was ≤ 2e-6 on every wrong Palace pair, and so was passivity:
σ_max = 1.0000. The per-line split LOSES the even mode's power, so the failure shows only as singular values below one
(0.75 at the example's spacing) in a structure that has no loss. On a lossy structure it is indistinguishable from loss.

**e. Mode ranking can pick an evanescent mode.** Palace ranks port modes by |Re kₙ − target| and ignores Im kₙ (upstream
issue #996, open, fix #1019 open). Brief 23's propagation test (|Im kₙ| ≤ 0.1·Re kₙ) already exists in `PalaceRun`. A
terminal port **requires** every one of its modes to be propagating, so for it a failure is an **error**, not a note.

**f. openEMS needs no modes: one transmission-line port per terminal works (measured, 113-a).** Each terminal is
an upstream-style line port: its excitation, three voltage planes and two current planes, the current averaged onto
the middle voltage plane (`calcTLPort`, `calc_ypar.m`). Without that average the current sits half a cell off, which is
0.75° at 5 GHz on the pair. **S must be assembled from all excitations at once**, S = (U − Z₀I)(U + Z₀I)⁻¹, as upstream's
own four-port example does (`directional_coupler.m`). Upstream's per-port `calcPort` columns are valid only with
terminations equal to the reference, which a PML-ended coupled pair never has. `FdtdPortTransform.Solve` already
assembles the matrix and reproduces upstream's S to every printed digit. On the stripline pair the result's error does
not grow with coupling: 0.057 dB / 0.00° on the thru at every spacing from S = 4W down to W/3, with the remainder the
grid's 0.7 % low Z. The coax meets 0.5 Ω / 1° only on a cylindrical grid (141 azimuth lines), which `CsxcadWriter`
cannot write: on a Cartesian grid Z is within 0.4 Ω but the line propagates 1–2.4 % slow.

**g. Where openEMS's source goes, and the side walls.** With the examples' PMC (or absorbing) sides, the result moves by
≤ 0.001 dB / 0.01° over feed shifts of 1.5b–6b and feed-to-plane distances of 2.25b–9b. The long feed brief 113 needed
came from its PEC side walls, whose first box mode was near the band. Use the tutorials' proportions and never PEC side
walls close to a line. Two silent traps for `CsxcadWriter`: a zero-thickness source off its grid line is dropped with
only a warning; and a current-loop edge on an exact half-cell is a tie, which on one strip snapped into the metal and
read 55–68 % of the current.

**h. A hollow waveguide has no terminal.** A port region met by one conductor (WR-90) has no voltage between conductors.
Palace's modal wave port handles it as today. openEMS would need a mode-matching port (analytic TE₁₀), which is **deferred**
(§4), so it stays refused, by name.

---

## 2. The briefs

| # | Title | Depends on | Delivers |
|---|---|---|---|
| 113 | Measure first: the terminal-port spike | — | Numbers that settle §1b–g on the pinned Palace 0.18.1 and openEMS 0.37.0-rc3; go/no-go for 115 and 116. No shipped code |
| 114 | The terminal wave port: document, problem, editor | 113-a | `C3dPort.Terminals`, `Reference`; N-conductor inference; `Em3dPort.FaceGroup`; port rows, overlay, `check`, `explain`. openEMS refuses it until 116; Palace refuses it (D14) |
| 115 | Palace: terminal ports on a shared face, and the example | 114, 124 | **Rewritten 2026-10-06 from 124's go (option 1, the derived modal transform); route B is not built.** A two-terminal wave port on Palace, any cross-section, symmetric or not: one port face per end, both terminals' modes on it, Palace's modal S, `V_wp`, Z_PV and kₙ converted to terminal S (Gram fit on degenerate faces only, Robin K from Re kₙ). Measures Palace's adaptive sweep against Point samples first. Three terminals and a shifted reference plane are refused, naming openEMS. Updates `examples/3D Wave Ports/`: Pair gains a Palace setup, and a new asymmetric *Coupled Microstrip* cell runs on both solvers. Writes the follow-up brief on openEMS's lidded microstrip. **Built 2026-10-06.** Measured on the way: an adaptive sweep is used, with kₙ from Palace's `BoundaryMode` solve of each face; a face whose modes travel at one speed (a stripline) comes back from Palace in an arbitrary, re-drawn mixture and is REFUSED, naming openEMS (owner's choice after one-run-per-frequency also failed); each face's M is a least-squares fit. Pair stays openEMS-only; the new *Coupled Microstrip* runs on both |
| 116 | openEMS: wave ports by probes | 113-a (114 for N > 1) | Feed extension, a source shaped to the line, three voltage and two current planes per terminal, S from all runs at once; single and multi-terminal; lifts the Palace-only refusal for any port with two or more conductors |
| 117 | Example: 3D Wave Ports | 116 (114 for Pair) | The connector with its back end open (coax wave port, both solvers) and a coupled pair (two-terminal wave ports, openEMS only); numbers quoted once |
| 118 | Example: 3D Eigenmode | — | Microstrip in a lidded cavity; the same cavity with a grounded pillar; eigenmode plus a driven sweep showing the notch move |
| [119](brief-em3d-119-palace-magnetic-wall-spike.md) | Spike: Palace terminal ports by symmetry (the magnetic-wall edge) | 113-a (nothing else) | Go/no-go for Palace terminal ports: a PMC strip between per-line ports (even mode) combined with 113-a's touching ports (odd), or a PMC/PEC symmetry half-model. No shipped code. Not urgent |
| [120](brief-em3d-120-openems-cylindrical-grid.md) | openEMS: a cylindrical grid, exact cylinders and a weighted source | 116 | `OpenEms.Grid: Cylindrical` on a `.c3d` setup; booleans of primitives written as prioritised operands; a coax terminal fed in its own 1/ρ profile. Meets 116's coax criterion for problems round about one axis |
| [121](brief-em3d-121-wave-ports-by-hand.md) | Wave ports by hand: a coax, and terminals against the air box's ground | 114, 116, 117 | A single wave port infers a coax's voltage path (114's `RayPath` fallback, enclosure only); *Make Port ▸ Wave* writes terminals when the ground is the air box's PEC faces, which count as one reference. 117's two ports re-made by the gesture |
| [122](brief-em3d-122-thirds-pair-grading.md) | openEMS grid: no line inside a thirds pair | — | Measure, then repair the grading that splits a thirds pair when two edges face across a gap just wider than the clamp allows (117's S 0.3 mm pair: max \|ΔS\| 0.26 against Cohn). No churn for clean grids |
| [123](brief-em3d-123-thirds-pair-required-line.md) | openEMS grid: a required line on a thirds edge | 122 | **Done 2026-10-06: measured a gain (strip Z₀ 6-19 % low → within 2.5 %), so a port's width, a material face and a kernel solid's own face now snap; D4 reversed (a pair with an unmovable line stays as it was); the 3D Connector's openEMS numbers re-recorded and 3D Wave Ports' Launch given grading 1.15 (src/Engine/RESOLVED.md).** Measure first whether a port extent, a material face or a kernel solid's own face lying inside a thirds pair costs accuracy (three variants against closed forms); fix upstream's way (the extent snaps) only if it does, else change nothing |
| [124](brief-em3d-124-palace-modal-transform-spike.md) | Spike: terminal S from Palace's modal S on a shared face, any cross-section | 119 | **Run 2026-10-06: go, option 1 (the derived transform alone).** Rebuilt: G real and symmetric, fitted from Z_PV, on degenerate faces only; Robin K = Re k₁ / Re k_m (Palace builds the Robin term and every mode's n×H from Re kₙ; 113's complex form fails on loss). At equal mesh it equals route B to max \|ΔS\| 0.0003 (A) and 0.0003 / 0.0007 (B); the asymmetric air pair (10 %, 3 %) is within 0.0025–0.0031 of the exact line, the symmetric pair's mesh error; the asymmetric microstrip lands at openEMS's own measured error (0.0095 / 0.024 / 0.0048 at 2 / 4 / 6 GHz), and what the asymmetry does agrees with openEMS to 0.002–0.005; lossy, at 4 GHz (openEMS's loss is exact there only), what the loss does agrees to 0.0004. Not met literally: (d)'s 0.1 dB / 1° against openEMS, which no microstrip case meets, the symmetric one included. Costs 3.5–4× route B's wall clock and ~2× its memory; N ≥ 3 on a degenerate face unverified. `src/Design/RESOLVED.md` § brief-em3d-124 |
| [125](brief-em3d-125-openems-lidded-microstrip.md) | openEMS: a microstrip terminal under a PEC lid | 115 | **Done 2026-10-06: strip → ground is closer to Palace than the mean at every lid height (4 GHz: 0.009 against 0.024), and the lid's distance changes nothing (the halves differ because of the interface), so a strip between two planes ON AN INTERFACE reads the half toward its reference; one medium keeps the mean (Pair byte for byte). Classification, source and model XML unchanged. Coupled Microstrip re-run: 0.008–0.027 against Palace over 2–12 GHz.** Written by brief 115 (D-115c). |

**Build order** (after 113-a): **118** any time (no engine work). **114 → 116 → 117** is the main line; 116's
single-terminal half (the coax) needs no 114 and can start first. **120 after 116**: it extends 116's coax source and
probes, and its gates run wave ports. **119 any time**: a scratch spike that changes no code and blocks nothing; run it
when solver time is free, and before anyone builds Palace terminal ports. **121 and 122 after 117**, independent of each other: 121 makes 117's ports by hand, 122
fixes the grid defect 117 worked around. **123 after 122**: it measures the pairs 122's check leaves alone, and may change nothing. **124 before 115**: its
go/no-go (2026-10-06) chose the derived modal transform, and 115 builds it.

### 2a. UI and user docs: where each lands

**Each brief documents its own capability**, in the same change that builds it, so a reference page never describes a
refusal that has already been lifted (today four pages say wave ports are "Palace only"). The two example briefs add only
the walk-throughs. Edit doc **sources** only (`docs/user/src/`). **No brief runs DocGen or regenerates `docs/user`**: the owner does that,
once, after the series. A brief's diff never touches a generated file.

| Brief | UI | User docs (sources) |
|---|---|---|
| 113 | — | — (findings go to `src/Design/RESOLVED.md`) |
| 114 | *Make Port ▸ Wave* makes terminals; port rows with terminal children; overlay arrows; *Set Reference…*; the menu tip | `drawing-in-3d.md` (#simulate's port paragraph, and the port entries in #reference); `em-setup.md` #wave-ports gains a *Several conductors on one face* subsection (the idea, the numbering, the file keys); `cli.md` (`explain`'s terminal lines) |
| 115 | Field-plot *Solution* choices labelled by port and mode (`Left mode 2`); the route note and the fit/power warnings as Messages-panel rows | `em-setup.md` #wave-port-terminals gains *On Palace*: two terminals per face, any cross-section, what is refused and why, the warnings; `em-3d.md` diagnostics keys; `cli.md` `em` and `explain` lines; the 3D Wave Ports example and the new-user guide §14 |
| 116 | The port-kind tip and the Offset tip lose "Palace"; the 3D view's FDTD grid overlay draws the openEMS feed extension | `em-setup.md` #wave-ports (the "Palace only" sentence goes, and an *On openEMS* paragraph is added); `em-3d.md` (~129, "Palace only"); `em-solvers.md` (the capability table, ~169, and the prose around it) |
| 117 | — | `new-user-guide/index.md`: a new walk-through after §13; `em-setup.md` #wave-ports links it |
| 118 | — | `drawing-in-3d.md` #lid and `em-setup.md` #eigenmodes link it, with the post's explanation; `em-solvers.md` (~267) links it |

---

## 3. Decisions — open, each with the brief's recommended default

| # | Decision | Recommended default | Blocks |
|---|---|---|---|
| D1 | Terminal or modal S for a multi-conductor port | **Terminal** (one port per conductor, its own Z0), which is what the owner asked for. Modal S (even/odd) is not exported in this series | 115, 116 |
| D2 | Where a terminal port lives | **`.c3d` only.** A `.cem`'s ports come from layout edge ports, one conductor each. Single-terminal openEMS wave ports (116) work from both | 114 |
| D3 | File shape of a multi-terminal port | **A `Terminals` list** (`Number`, `Name`, `Conductor`, `Z0`, optional `VoltagePath`, `Flip`) plus a port-level **`Reference`**; the port-level `Number`/`Name`/`Z0`/`Positive`/`Negative`/`VoltagePath` are **not written** when `Terminals` is. A one-terminal port keeps today's spelling (rule 3). Alternative: terminal 1 stays in the port-level fields and `Terminals` holds 2…N, which touches less code but is asymmetric | 114 |
| D4 | Numbers for an inferred terminal | **Never invented by the reader.** A file whose wave port meets 3+ conductors and states no `Terminals` stays refused, with a sentence naming the fix. The editor's *Make Port ▸ Wave* writes the terminals with the next free numbers | 114 |
| D5 | Turning a terminal off (brief 93's `Model`) | **Per port, not per terminal.** A face's modes are solved together, so a half-modelled face is not a smaller problem | 114 |
| D6 | Palace: a terminal port's `Offset` | **Measured (113-a):** `V_wp` ignores `Offset` and S moves by kₙd, so any Palace terminal transform must be written with `Offset` 0 and shift itself. **Moot until the owner decides §1b**: no documented Palace setup yields terminal ports for a coupled pair. | 115 |
| D7 | Palace: the run's quality check | **Measured (113-a):** a Robin-mismatch threshold is the wrong check. The documented setup's failure is LOST power, invisible to reciprocity and to σ_max (1.0000 on every wrong run). Any Palace multi-conductor check must look at the smallest singular value on a lossless structure (0.75 at the example's spacing). Moot until §1b is decided. | 115 |
| D8 | Palace: field plots of a terminal port's run | **Shown per solved mode**, labelled `P3 mode 2`. Superposing the steps into "terminal k driven" (via T_V) is deferred. openEMS runs drive terminals, so its plots are per terminal | 115 |
| D9 | openEMS: the face a wave port lies on | **Lowered absorbing (PML)** whatever the setup states, said in the run's notes when it states otherwise. openEMS has one boundary per face, and a source behind a PEC face has nowhere to go | 116 |
| D10 | openEMS: hollow waveguide (one conductor) | **Still refused**, naming Palace and the deferral (§1h) | 116 |
| D11 | The wave-port example's home | **A new workspace, `3D Wave Ports`**, self-contained (examples are copied whole by *Tools ▸ Examples*), with `Launch` (the connector, back end open) and `Pair`. `3D Connector` is left as it is | 117 |
| D12 | The pair's cross-section | **Measured (113-a):** the edge-coupled air stripline pair works on openEMS (thru 0.057 dB / 0.00° at every spacing; PMC sides). Its reference is a 2D field solve of the drawn section, not Cohn (Cohn is the t → 0 limit). On Palace neither the stripline nor the microstrip pair yields terminal S (§1b), so no Palace fallback exists. | 117 |
| D13 | Mixed-mode (Sdd/Scc) | **Deferred.** RfCore has no mixed-mode conversion today; the example plots terminal S (thru, near-end and far-end coupling) | 117 |
| D14 | Which solver gives terminal ports | **Decided (owner, 2026-10-05): openEMS only.** Palace refuses a multi-terminal wave port by name, pointing to openEMS. **To be revisited** with brief 119, which tries the magnetic-wall edge (a PMC strip between per-line ports) and a symmetry half-model, both with documented Palace keys only. **Brief 119 measured (2026-10-06): GO, route B** (the symmetry half-model, `PMC` / `PEC` cut, two runs, §3 combination): on A it meets 0.05 dB / 0.5°, max \|ΔS\| 0.0012, σ within 0.0001 of 1, at ~100 s a run with 0.02 mm strip-edge refinement. Route A works but costs twice as much and hides a wrong half from σ. Mirror-symmetric two-conductor faces only; a 1.3 % width difference already breaks 0.05 dB with nothing to detect it, so 115 refuses asymmetry. B's route is exact against Palace's own port modes but misses 0.1 dB / 1° against its quasi-static reference (full-wave dispersion), and the even half loses 5–11 % to 113-a's absorbing walls. The owner chose route B: brief 115 is rewritten around it, and held for brief 124, which measures brief 113's derived modal transform (it would also cover asymmetric pairs). If Palace fixes #328 upstream, see `src/Design/RESOLVED.md` § brief-em3d-119, "If upstream fixes this". **Brief 124 measured (2026-10-06): go for the derived transform alone** (one shared face per end, N entries, equal `MaxSize`, the Gram fit on degenerate faces, K from Re kₙ): symmetric and asymmetric two-conductor faces, lossless and lossy, equal to route B at equal mesh and at openEMS's own error on microstrip; the owner chose it, and brief 115 is rewritten around it (route B is not built) | 114, 115, 116, 117 |
| D15 | openEMS: a coax's accuracy | **Decided (owner, 2026-10-05): a cylindrical grid** (brief 120), with exact cylinders and a weighted source. It serves problems round about one axis; the 3D Connector's Launch, a coax meeting a board, stays Cartesian and its run notes the coax's phase-velocity error (113-a: 1–2.4 %) | 116, 117, 120 |

---

## 4. What is deferred, and why

- **openEMS hollow-waveguide ports** (TE₁₀ mode-matching, ProbeBox types 10/11). Upstream has the elements, but it is a
  different port (modal, no terminal), and nothing in this series needs it.
- **Palace terminal ports** (D14): openEMS gives them now. Brief 119 measured a go for mirror-symmetric pairs by the
  symmetry half-model (route B); brief 124 then measured the derived modal transform on a shared face as a go for
  symmetric and asymmetric pairs alike (option 1), and 115 is rewritten around it. A native Palace fix (#328)
  would replace it: `src/Design/RESOLVED.md` § brief-em3d-119 says what to re-run and what then changes.
- **Mixed-mode S-parameters** in RfCore (D13). It is an RfCore feature on its own, useful far beyond 3D EM.
- **Palace field plots as terminal drives** (D8).
- **Terminal ports from a `.cem`** (D2): it needs layout ports that name several edges as one port.
- **openEMS numeric mode files** (§1f): no need while V/I probes give terminal S exactly.

## 5. Owner walk-through, at the end of the series

Open **3D Wave Ports ▸ Launch**, run *Palace* and *openEMS*, and compare |S11| with `3D Connector`'s lumped-port result:
the gap capacitance the lumped port carried is gone. Open **Pair**, look at its two wave ports with two numbered terminals
each, run both setups, and plot S21 (thru), S31 (near-end) and S41 (far-end) from both solvers on one Data Display. Open
**3D Eigenmode**, run *Modes* on both cells, and see the first mode move from inside the line's band to above it.
