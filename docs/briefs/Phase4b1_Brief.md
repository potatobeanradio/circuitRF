# Phase 4b-1 — Implementation Brief: Core Loadpull Engine + the `Tuner` Component → Hero 3 (Claude Code / Sonnet)

**Goal:** the **core swept-loadpull engine** and the **`Tuner` component**, validated on **Hero 3**. This is the first sweep/automation layer on the proven single-tone HB engine (Phase 4a). It is sub-gated: 4b-1 is the engine (this brief); the search/automation layer (MXP/MXE/auto-Zsource/frequency loop) is **Phase 4b-2**, a later design pass — OUT of scope here.

> Read first, in order: root `CLAUDE.md`, `src/Engine/CLAUDE.md`, `src/Engine/HarmonicBalance/CLAUDE.md`, then `docs/design/loadpull.md` (the whole note — authoritative), and the docs it builds on: `docs/design/harmonic-balance.md` (the HB engine this orchestrates), `docs/design/linear-engine.md` (§4.4 `Z_Port`/tone sources, §4.3.1 the voltage-pinned-DC `InductanceRegularization`), `docs/design/measurements.md` (the FOM library — 4b-1 implements the subset the stop-logic needs). Where this brief and a design note disagree, the design note wins — flag, don't guess.

## Prerequisite (done)
Phases 1–4a complete and passing: linear engine, nonlinear DC + AD + SDD, and the single-tone HB engine (Hero 2 — self-generated regression golden, DC-in-Newton correct, the `Z_Port`/`V_1Tone`/bias-tee/`InductanceRegularization` machinery all working). **4b-1 CONSUMES the HB engine — it orchestrates HB solves over a 2-D sweep; it does not modify the HB inner solve.**

## Working style (same discipline as Phase 4a)
**Diagnostics over deep convergence problem-solving.** The loadpull engine runs hundreds of HB solves; if some grid points or Pin steps don't converge, that's expected (high-VSWR loads, deep compression) — the engine should **record the failure reason and move on**, not grind. Build the convergence trace into the per-point logging. Small fixes OK; large re-architecture → flag for the owner. Don't burn large context on a convergence rabbit-hole.

## Scope — build these, in this order (each gated)

### STEP 1 — The `Tuner` component (the new vocabulary)
Per loadpull.md §1. A `Tuner` is the user-facing programmable termination, built internally from the existing `Z_Port` + bias-tee machinery (do NOT reinvent — wrap what Hero 2 used).
- **Declaration:** DUT-facing net + reference net; per-harmonic terminations `Z[1]`/`Z[2]`/`Z[3]`/… (or `G[1]`/`G[2]`/… in Γ form, with optional `Z0` default 50 Ω); `Zdefault` catch-all (default `1e-6`). **`Z[1]`/`G[1]` required.** Same harmonic given both Z and G → error. Forms may be mixed across harmonics.
- **Internal bias-tee:** `BiasTee=on` + `Vbias=<supply value>` embeds a bias-tee (ideal choke + DC-block) and DC supply, so the user wires no chokes/blocks. **The ideal choke triggers the DC voltage-pinned-interface case — run `InductanceRegularization=Always` for loadpull** (loadpull.md §2.1: we KNOW it's needed at every point, so `Always` skips the speculative retry — a per-point speedup). Re-use the exact `InductanceRegularization` auto-fix from Hero 2 (linear-engine §4.3.1); do not write a new mechanism.
- **Role-specific stamping (the role is assigned by the `Loadpull` analysis, not the Tuner):**
  - **LoadTuner:** stamps as a passive termination (`Z_Port` + bias-tee).
  - **SourceTuner:** stamps as a termination **PLUS its own internal `V_1Tone` RF drive at the analysis fundamental** (`Freq` = the analysis `Tone`). It computes its own `|Vs| = sqrt(8·Pavl_w·real(Z[1]))` from its own `Z[1]` and the `Pavl` the analysis hands it per drive step. **No separate drive source exists in the netlist** — the source Tuner IS the drive.
- **Outside HB (S-parameter / plain linear sims):** the Tuner ignores its harmonic-band structure and presents `Z[1]`/`G[1]` **constant over frequency** (the band logic is HB-specific). A Tuner is thus a valid fixed termination in an S-param sim.
- **Exposed handles** (for the analysis): the DUT-facing net, and — with an internal bias-tee — the **bias-supply voltage and current nodes** (for `Pdc`; needed by 4b-2 and the post-processor, captured but not used by the 4b-1 stop loop).
- Tests: a Tuner parses (Z and Γ forms, mixed, the Z-and-G-same-harmonic error, the required-`Z[1]` error); a Tuner used as a fixed termination in an S-param sim presents `Z[1]` flat; a SourceTuner stamps a drive computing the right `|Vs|` from `Z[1]`+`Pavl`.

### STEP 2 — The `.gam` grid file reader
Per loadpull.md §2.2. A forgiving parser for the termination grid file:
- Optional header line `# gamma Z0=50 mag_ang` (tags: form = `gamma`/`impedance`; complex format = `re_im`/`mag_ang`/`re+j*imag`).
- Absent form tag → default `impedance`. Absent complex-format tag → infer `re+j*imag` if a data value contains `j`/`i`, else `re imag` (two columns).
- One point per line; skip blank lines and `;`/`#` comments.
- Convert Γ↔Z via **RfCore** against `Z0` and the `TuneHarm` reference. Each line → one grid point.
- Tests: parse `mag_ang`-with-header, header-less `re imag`, header-less `re+j*imag` literals, an `impedance` file; verify Γ↔Z conversion against RfCore.

### STEP 3 — The `Loadpull` analysis directive + the 2-D sweep engine
Per loadpull.md §2–§5. This is the core deliverable.
- **Directive** (`analysis Name type=loadpull …`): keys `LoadTuner` (required), `SourceTuner` (required — error if either missing), `Sweep` (`Load`/`Source`, default `Load` — which Tuner the grid varies; distinct from naming the Tuners), `Tone`, `TuneHarm` (default 1 — which harmonic of the swept Tuner), `MaxHarm`, `Grid` (the `.gam` path), `Compression` (default 3), `GainType` (`Gt`/`Gp`, default `Gt`), `PinStart`, `PinStep`, `PinMax` (required safety cap), `Tickle` (default on, e.g. −50 dBm), **`MaxIter` (max Newton iterations of the underlying HB solve, default 100)**, plus the HB knobs (`FFTOverSample`/`Tol`/`DriveStepping`/`GuardHarmonic`). All values resolve through the expression engine. This is the second concrete analysis directive (after `type=hb`); other analysis types stay `RawDirective`.
- **The engine learns nodes from the named Tuners:** DUT output = LoadTuner's DUT-facing net; DUT input = SourceTuner's DUT-facing net. No user measurement needed for the control loop.
- **2-D sweep:** outer termination grid (the `.gam` points, applied to the swept Tuner at `TuneHarm` via the swept-variable mechanism — §2.3) × inner adaptive Pin drive-up.
- **Inner adaptive power sweep (per grid point), §3.1:**
  1. Optional **tickle** point (single very-low Pin) prepended to anchor the small-signal gain reference.
  2. Drive up `PinStart` by `PinStep`, running an HB solve at each Pin (warm-started — §3.3).
  3. Compute the chosen gain (`Gt`/`Gp`) **live** each step; track running `Gmax`.
  4. **Compression stop:** the point where gain dropped `Compression` dB below `Gmax`; **stop at P-xdB + ~0.1 dB** (overshoot a hair so a post-processor can bracket it).
  5. **Hard stops:** `PinMax` reached, or HB non-convergence — record the reason and move on.
- **Per-step live measurements (§4), computed in C# from the HB V/I spectra at the fundamental:** `Pavl` (referenced to SourceTuner `Z[1]`, set by the engine each step), `Pin_delivered = ½·Re(V·I*)` at the SourceTuner DUT-facing port, `Pout = ½·Re(V·I*)` at the LoadTuner DUT-facing port, `Gt = Pout/Pavl`, `Gp = Pout/Pin_delivered`. (Magnitude/peak phasor convention, linear-engine §2.2.)
- **Γ-grid warm-start (§3.3):** each grid point's HB solve seeds from the **nearest already-converged grid point**, "nearest" = `RFNetwork.VSWR` (RfCore) between the two complex Γ/Z points closest to 1. Inner Pin direction warm-starts from the previous Pin point.
- **Capture everything (§5):** for every (grid point, Pin step) retain the converged V and I spectra (all harmonics incl. k=0) plus the live FOMs; result cube gains a termination axis. Also capture the bias-supply V/I (for later efficiency/post-processing). The elaborate FOMs (exact P-xdB, Pout-at-compression, contours) are a **post-processor — OUT of scope for 4b-1.**

### STEP 4 — Diagnostics
- Per-point convergence trace (reuse the HB trace): which grid points/Pin steps converged, iterations, and the stop reason per inner sweep (compression / PinMax / non-convergence).

## Acceptance gate — Hero 3 (self-generated regression, owner-verified)
- `testdata/Hero3/hero3.cnl` (two Tuners + SDD + Loadpull directive) and `testdata/Hero3/hero3_load.gam` (21-point Γ grid) are written.
- The 2-D sweep runs: each grid point's inner Pin sweep drives up and stops correctly (P-3dB+0.1, PinMax, or non-convergence — recording which).
- Live `Pavl`/`Pin_delivered`/`Pout`/`Gt`/`Gp` computed from spectra; spot-checks sensible (the owner will verify — e.g. small-signal gain, the gain dropping into compression, Pout-at-compression varying across the load grid).
- Γ-grid warm-start (VSWR-nearest) works; the sweep is tractable.
- Full 2-D dataset captured (V, I, FOMs, bias-supply V/I, termination axis).
- **Self-generate the Hero 3 regression golden** (à la Hero 2): run the sweep, export the captured data to CSV, label it **self-generated regression, not independently validated**, place in `testdata/Hero3/`. Wire a CI regression test with the **<1e-5-is-noise** tolerance rule. (Generate only once the sweep + measurements are sane; the owner verifies before it's frozen.)
- `dotnet build`/`dotnet test` green; Phases 1–4a still pass.

## Also for this brief — expose `MaxIter` on the HB analysis too
The `type=hb` analysis directive (harmonic-balance.md §3.2) now also exposes **`MaxIter` (default 100)** — the max Newton iterations per HB solve before continuation backoff. This was previously "(engine default)"; make it an explicit user-settable key defaulting to **100** on *both* the `hb` and `loadpull` analyses (loadpull's inner solves are HB solves, so they share the same knob). A small addition while implementing the loadpull directive.

## Guardrails
- CONSUME the HB engine (Phase 4a) and `Z_Port`/`V_1Tone`/bias-tee/`InductanceRegularization` (Phase 4a + linear-engine) — the Tuner WRAPS them; do not reinvent. Do not modify the HB inner solve.
- Run loadpull with **`InductanceRegularization=Always`** (we know the Tuner bias-tee needs it — skip the speculative retry).
- Both `LoadTuner` and `SourceTuner` are required — error clearly if either is missing.
- **Diagnostics over grinding:** non-convergence at some grid/Pin points is expected — record and move on; do not launch a large investigation or burn context. Small fixes OK; large re-architecture → flag.
- The MXP/MXE/auto-Zsource/frequency-loop search layer is **Phase 4b-2 — do NOT build it.** (4b-1 must *capture* the bias-supply V/I so 4b-2 can later compute efficiency, but 4b-1 does not do efficiency search or live efficiency detection.)
- Update `src/Engine/CLAUDE.md` (and a loadpull CLAUDE.md if warranted) with the Tuner, the `.gam` reader, and the loadpull engine.
- Flag design questions to Opus/Chat; don't improvise.

*Phase 4b-1 exit (Hero 3 sweep runs + measurements sane + regression golden frozen) unblocks 4b-2 (the search/automation layer, its own design pass) and the later multi-tone (4c) / multi-device (4d) sub-gates.*
