# Phase 2 — Implementation Brief (for Claude Code / Sonnet)

**Goal:** the **linear engine** — turn an `ElaboratedNetlist` into a sparse MNA system, solve it, and
produce trustworthy S-parameters. Then add a VendorA netlist importer and validate at scale. Exit
gates: **Hero 1** (4-port S-params vs reference < 1e-6), then **Hero 1B** (~10k components within the
performance budget, via the VendorA importer). First numerically-validated engine; also builds the
linear machinery harmonic balance (Phase 4) reuses.

> Read first, in order: root `CLAUDE.md`, `src/Engine/CLAUDE.md`, `src/Core/CLAUDE.md`,
> `src/Core/Data/CLAUDE.md`, then `docs/design/linear-engine.md` (rev 3, whole doc) and
> `docs/design/data-model.md` §3 + §7. Authoritative; design notes win over this brief — flag, don't guess.

## Prerequisite (done)
RfCore is extracted and tested (SNP, RFNetwork, TouchstoneIO, RfHelpers, DataSet/DataCube, frequency
interpolation). circuitRF references it via `ProjectReference`. Phase 2 **consumes** RfCore — never
reimplements network math, Touchstone I/O, renormalization, or interpolation.

---

## Sequencing — three steps, in order. Do NOT start a step until the prior one's gate passes.

### STEP 1 — SnP/Touchstone block, end-to-end (the one new primitive)
Hero 1 embeds a Touchstone block, and the `.cnl` reader does not yet support it. Build it as a
vertical slice before the rest of the engine:
- **`.cnl` reader:** parse the `SnP:` line. Syntax (VendorA-compatible; native `.cnl` uses our vocabulary):
  `SnP:X1  n1 n2 [...]  NumPorts=N File="rel/or/abs/path.sNp" Type="touchstone" InterpMode="spline" ExtrapMode="clamp"`
  - **Honored fields:** node list is N nets (ports ground-referenced) or N+1 nets (last net = common reference node for all ports — the floating-block case); validate count == NumPorts or NumPorts+1, else error. Record the reference on the component; the model stamps it per linear-engine §4.1 (it is NOT special-cased by the engine); `NumPorts` (must equal node count — error if not); `File`
    (relative resolved against the `.cnl` file's directory, absolute as-is); `Type` (v1: only
    `"touchstone"` — **hard-error on any other value**; designed as an extensible data-source
    discriminator, future `"datacube"`); `InterpMode` (`"spline"` default | `"linear"`); `InterpDom`
    (default if empty); `ExtrapMode` (`"clamp"` default | `"extrapolate"`).
  - **Completely ignore:** `Temp` (parse and discard, no warning). **Silently ignore:**
    `CheckPassivity`, `Noise=`, `SaveCurrent=`, and similar flags.
- **SnP `ComponentModel`:** holds an RfCore `SNP` loaded from the file. **`Type` selects a loader**
  (file vs, later, datacube); the loaded network is what gets stamped — design so the future
  datacube source slots in without touching the stamp path. Load via RfCore's TouchstoneIO; handle
  file-not-found cleanly.
- **Stamp:** per linear-engine §4.1 — **`Z(ω)` branch-current expansion by default**, interpolating
  the stored network to ω via RfCore; native-`Y` admittance stamp only if a block is natively finite-Y
  (an `.sNp` block is not — it's the Z path).
- **Gate:** a unit test loads `potentially_unstable_amp.s2p`, interpolates to an off-grid frequency,
  and stamps correctly into a small MNA (hand-verified).

### STEP 2 — the linear engine + Hero 1
Build per `linear-engine.md` rev 3:
- **`MnaSystem`** + stamping API (§3); engine owns the matrix, models contribute stamps; Group-1/
  Group-2 split (§2).
- **Linear `ComponentModel.Stamp` for:** R, C (Group 1); L, voltage sources, current probe,
  `Short` (0V branch), mutual inductance (Group 2, couples two inductor branches per §7); AC current
  source; Port/Term; impedance block; TLIN. (RF power source per §4.2 — wire + unit-test the
  `|Vs|=sqrt(8·Pavl·Re(Zs))` mapping, but it's exercised in Phase 4, not Hero 1.)
- **DC analysis** (§5): exact ω→0; inductor-short, capacitor-open, **gmin=1e-12 S default, advanced
  setting**. No value fudges.
- **S-parameter analysis** (§9): MNA per frequency; **extract port Y-matrix by default**, Z as
  conditioning fallback; hand Y to RfCore for Y→S + renormalization to per-port (complex) Z0. Output
  the `S` cube `{freq, i, j}` (Complex) in a `DataSet`; write Touchstone via RfCore.
- **Sparse solve** (§6): CSparse.NET complex LU; **symbolic-once/numeric-per-frequency**,
  **factor-once/multi-RHS**; AMD ordering owned here.
- **Conventions (fixed, §2.2 / `src/Engine/CLAUDE.md`):** magnitude(=peak) phasor; branch current
  first→second node; current source injects into first node. Named constants; never silently varied.
- **CLI:** `circuitrf sparam hero1.cnl --freq <sweep> -o out.s4p`, headless; plus a DC command.
- **Gate — Hero 1:** the supplied `hero1.cnl` (4-port: RLC + embedded `potentially_unstable_amp.s2p`
  + 4 Terms) matches the owner-supplied 4-port reference `.s4p` with
  `max |S_sim(i,j,f) − S_ref(i,j,f)| < 1e-6` for all 16 S-params, swept **1–3 GHz** (choose sweep
  points that do NOT all coincide with the file's 0.1 GHz grid, so interpolation is genuinely
  exercised). If it lands just over tolerance, **gmin is the first suspect** (try smaller).

### STEP 3 — VendorA importer + Hero 1B (only after Hero 1 passes)
A **second front-end** that emits the **identical design-layer model** the `.cnl` reader emits. No
elaboration or engine code may know which reader produced the model. Call it **`VendorA`** everywhere
(class names, comments, messages) — never the vendor's real name.
- **Translate:** `R:`/`L:`/`C:` (inline `expr unit`), `Mutual:` (`M=<expr> <unit> Inductor1="X"
  Inductor2="Y"` → our MutualInductance, references resolved post-flatten), `Short:` (→ 0V branch),
  `Port:`/`Term:`, `SnP:` (same line as Step 1) preserve the N+1 reference-net convention when translating SnP lines (VendorA emits it the same way), `define...end` subcircuits (→ `Cell` + port list),
  subcircuit instances (`CellName:Inst net...`), variable assignments (global + cell-scoped, with
  expressions).
- **Strip + ignore annotations:** `opt{...}`, `tune{...}`, `notune{...}` (keep the value before them);
  flags `Noise=`, `SaveCurrent=`, `Mode=`.
- **Skip header/tooling:** `Options ...`, `#load ...`, `Component Module=...`.
- **Raw-directive (deferred grammar):** `S_Param:`, `SweepPlan:`, `OutputPlan:` → opaque
  `RawDirective` records (same treatment the `.cnl` reader gives directives).
- **Vocabulary translation:** map VendorA interp/extrap words to ours (`constant`→`clamp`, etc.).
- **Error loudly on:** duplicate instance names (a valid VendorA netlist has none); any unrecognized
  construct — `"unsupported VendorA construct at line N: <text>"`. Never silent-skip or guess.
- **Gate — Hero 1B:** import `testdata/Hero1B/hero1b_netlist.cnl` (VendorA format), solve the
  ~10k-component network, compare against the supplied `.s5p`. Acceptance is **performance (< 10 s,
  few-hundred freqs, typical laptop) + internal consistency**, NOT a 1e-6 match.

---

## Test fixtures
- **Hero 1** (owner will supply the reference): `hero1.cnl` (provided — see below), a **copy of
  `potentially_unstable_amp.s2p`** placed in the Hero 1 fixture folder so the test is **self-contained**
  (do not reach into the RfCore repo), and the owner-generated 4-port reference `.s4p`.
- **Series-open / shunt-short** small fixture to exercise the Y→Z extraction fallback.
- Small hand-verifiable DC and single-frequency MNA fixtures for stamp unit tests.
- **Hero 1B:** `testdata/Hero1B/` already holds `hero1b_netlist.cnl` (VendorA format) + golden `.s5p`.

## Guardrails
- Consume RfCore; never reimplement its math.
- No code outside the current step's scope; if it seems needed, stop and flag (esp. the HB side of
  linear-engine §10 — that's Phase 4; build the *extraction* reusably but don't build HB).
- Fixed conventions as named constants.
- Swift prototype is reference only — its central stamping is the *opposite* of this engine's
  model-contributes-stamps design; do not transliterate.
- Flag design questions to Opus/Chat. Update `src/Engine/CLAUDE.md` / `src/Core/CLAUDE.md` if reality diverges.

*Exit unblocks Phase 3 (nonlinear DC + devices' Evaluate path).*