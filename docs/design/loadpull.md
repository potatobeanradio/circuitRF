# circuitRF — Loadpull Engine & the `Tuner` Component (Phase 4b-1 Design)

**Status:** Draft for review · **Date:** 2026-06-03
**Reads with:** `docs/design/harmonic-balance.md` (the HB engine this orchestrates; §3.1 commensurability, §10 seed, §11 continuation), `docs/design/linear-engine.md` (§4.4 `Z_Port` + tone sources, §4.3.1 voltage-pinned DC interface), `docs/design/measurements.md` (the FOM library — `Pout`/`Pin`/`Gain`/`Pdc`/`DE`/`PAE`, V/I retention; loadpull implements the subset its stop-logic needs), `docs/design/expressions.md` (`freq`/`Freq`, complex helpers), `docs/design/data-model.md` (§4 analyses, §7 result cubes).
**Defers to:** Phase 4b-2 for the search/automation layer (MXP, MXE, auto-Zsource, frequency loop) — see §8.

This note specifies **Phase 4b-1**: the **core swept-loadpull engine** and the **`Tuner` component**, validated on **Hero 3**. It is the first sweep/automation layer on the proven single-tone HB engine (Phase 4a). It is **sub-gated**: 4b-1 is the engine (this note); 4b-2 (a later design pass) adds the search algorithms that make loadpull a differentiator (§8). No code is written until this is approved.

## 0. What loadpull is, and the 4b sub-gate

**Loadpull** sweeps the impedance (or reflection coefficient Γ) presented to a device under test (DUT) over a grid, and at each grid point drives the DUT in power up to a target gain compression, capturing the performance (Pout, gain, efficiency) as a function of termination. It is how PA designers find the optimum load. **Sourcepull** is the same with the source-side termination swept. circuitRF treats both with one mechanism (a `Tuner` on either side).

**The 4b sub-gate** (mirrors Phase-2's Hero-1-then-Hero-1B and Phase-4a-before-4b discipline):
- **4b-1 (this note) — the core loadpull engine → Hero 3.** The `Tuner` component, the `Loadpull` analysis directive, the 2-D sweep (Γ-grid × adaptive-Pin), the compression-based Pin stop, the live measurements the stop needs, Γ-grid warm-starting, and capture-everything output.
- **4b-2 (later, own design pass) — the search/automation layer.** MXP (max power at compression), MXE (max efficiency at compression), auto-Zsource (conjugate-match the source to the DUT's load-dependent Zin), and the frequency loop. These are search algorithms *on* the proven 4b-1 engine and are a major differentiator vs other RF CAD; they deserve careful design and are out of scope here (§8).

---

## 1. The `Tuner` component

A **`Tuner`** is the user-facing programmable termination at a DUT port — the software analog of a lab impedance tuner (the term of art; an RF engineer reads `Tuner:Load` and knows exactly what it is). It is **role-neutral by declaration but role-specific when stamped**: the same component can serve as a *load* tuner or a *source* tuner, and the **`Loadpull` analysis assigns the role** by naming it `LoadTuner=` or `SourceTuner=` (§2). The two roles **stamp differently** (§1.1): a load tuner is a passive termination; a **source tuner additionally owns its own RF drive** (an internal `V_1Tone` excitation), because on a real loadpull bench the signal source comes *through* the source tuner. Internally a `Tuner` is built from the `Z_Port` + bias-tee machinery already in the engine (linear-engine §4.4), plus (in the source role) an internal drive source — all hidden behind a clean face. `Z_Port` remains the lower-level primitive for power users; `Tuner` is the friendly layer.

### 1.1 What a `Tuner` declares (it is a circuit block — "what it is")
- **Connection** — the DUT-facing net (and a reference net, normally ground). This connection is also how the loadpull engine learns the **measurement node** (§4): the load tuner's DUT-facing net *is* the DUT output node; the source tuner's is the DUT input node.
- **Per-harmonic terminations `Z[1]`, `Z[2]`, `Z[3]`, …** (or `G[1]`, `G[2]`, … in the Γ form) — the impedance/reflection the Tuner presents at harmonic 1 (f0), 2 (2f0), …. The Tuner presents these whether or not a loadpull is running, so it is **self-sufficient**: a complete, well-defined termination on its own, usable in an **S-parameter or plain-HB sim** as a fixed parameterized termination. A loadpull analysis merely *overrides* the one harmonic it is told to tune, per grid point (§2, §3), leaving the others at their declared values.
- **`Z[1]`/`G[1]` is REQUIRED** — at least the fundamental termination must be specified, so a user cannot accidentally create a Tuner that is all `Zdefault` (a near-short on everything).
- **`Zdefault` — the catch-all** for every harmonic not explicitly listed. **Defaults to `1e-6`** (near-short) if omitted, so a minimal Tuner need only declare `Z[1]` (and perhaps `Z[2]`).
- **The Tuner does NOT know which harmonic is "tunable."** Like a real passive slabline tuner — a slug on a line that simply presents whatever impedance its position dictates, with no notion of "harmonic" — the `Tuner` just presents `Z[1]/Z[2]/Z[3]/…`. **The `Loadpull` analysis (the control software) decides which harmonic to tune** (`TuneHarm`, §2) and overrides that one. This makes the same Tuner reusable across experiments that tune different harmonics.
- **Γ form and `Z0`** — each harmonic's entry may be given as an impedance (`Z[1]`) **or** a reflection coefficient (`G[1]`), independently per harmonic (mixing is allowed — e.g. `G[1]` for the fundamental but `Z[2]=1e-6` to short the 2nd). Declaring **both** an impedance and a reflection for the *same* harmonic (e.g. `Z[2]` and `G[2]` together) is an **error** (one harmonic, one specification). An optional **`Z0`** sets the Γ normalization (defaults to **50 Ω**); Γ↔Z conversion is via RfCore.
- **Behavior outside HB (S-parameter / plain linear sims).** The per-harmonic band structure is **HB-specific** — it only means something when a fundamental defines the harmonic bands. In an S-parameter sweep (or any non-HB analysis) there is no fundamental and `freq` takes arbitrary swept values, so the Tuner **ignores its band structure and presents its `Z[1]`/`G[1]` value as a constant over all frequencies**. This is the sensible degeneration: `Z[1]` is the fundamental termination, and a linear characterization around that band sees it flat. (So a Tuner is a valid fixed termination in an S-parameter sim, presenting `Z[1]`.) A Tuner maintains its bias-tee machinery outside HB.
- **Optional internal bias-tee + bias source** — a Tuner may embed its own bias-tee (choke + DC-block) and DC bias source, so the user need not wire chokes/blocks into the schematic. When present, the Tuner carries the bias point (`Vbias` — the gate/drain supply value) for the port it feeds. The embedded ideal choke **re-uses the `InductanceRegularization` path** (linear-engine §4.3.1) for the DC voltage-pinned-interface case — the same auto-regularization that resolved Hero 2's ideal-choke singularity, now triggered automatically inside the Tuner. When absent, the user supplies their own bias-tee externally (the Hero-2 style).
- **Role-specific stamping — load vs source (assigned by the `Loadpull` analysis, §2):**
  - **As a LoadTuner:** stamps as a passive termination (the `Z_Port` + optional bias-tee). No excitation.
  - **As a SourceTuner:** stamps as a termination **plus its own internal RF drive** (an internal `V_1Tone` at the analysis fundamental). The user does **not** write a separate drive source in the netlist — the source Tuner *is* the drive. The source Tuner **computes its own excitation magnitude** `|Vs| = sqrt(8·Pavl_w·real(Z[1]))` from its **own `Z[1]`** (the fundamental source impedance) and the **`Pavl` the Loadpull analysis hands it per drive step** (§3). So available power is referenced to the source Tuner's own `Z[1]`, and the analysis sets `Pavl` each Pin step; the Tuner re-evaluates `|Vs|` internally. This is why `Z[1]` for the source side appears in exactly **one** place (the source Tuner) — no separate `V_1Tone`/`Vs_mag` plumbing in the netlist.
- **Exposed handles for the analysis.** A Tuner exposes, to the `Loadpull` analysis: its **DUT-facing net** (the measurement node, §4), and — when it has an internal bias-tee — the **voltage and current nodes of its internal DC bias supply** (needed for `Pdc = Vdc·Idc`, hence efficiency). Live efficiency is **not** needed by the 4b-1 compression loop (compression keys off gain), but the bias-supply node handles must be exposed so 4b-2's efficiency search and the elaborate measurements can read `Pdc` (§8).  The internal bias-tee + bias source is not an option for the `Loadpull` analysis directive; error if not enabled.

### 1.2 What a `Tuner` does NOT hold
The *procedure* — the Γ-grid, compression target, gain choice, max-Pin, tickle — lives in the `Loadpull` analysis (§2), **not** in the Tuner. The Tuner is a circuit block ("what it is"); the analysis is the behavior ("what it does"). This split keeps the Tuner reusable (standalone termination) and the analysis about behavior, and avoids cramming a procedure's worth of parameters onto a component.

**UI/schematic surface — single pin, implicit-ground reference (deferred 2nd pin).** The general `Tuner` schematic symbol exposes a **single** DUT-facing pin (on the left of the glyph). The engine's `TunerModel` declares **two** nets — `Nodes[0]` DUT-facing, `Nodes[1]` reference — and the same ordering for **all three tiles** (general `Tuner`, `LoadTuner`, `SourceTuner`): the net extractor supplies `Nodes[0]` from the pin and **hard-codes `Nodes[1] = "0"` (ground)**. This matches the lab convention (a tuner connects a single DUT terminal; the reference is implicit ground). The `SourceTuner`'s internal RF-drive node (where the embedded `V_1Tone` drives against the reference) is **minted by the engine** (`__tuner_<inst>_outer`, Nodes[4]), not declared — so the schematic surface and net ordering are symmetric across the family. Exposing the reference as a second pin — for non-ground references such as differential terminations — is **deferred**; it can be added later without changing the net ordering.

In the GUI, the Source/Load/general Tuner tiles place the identical `Tuner` component (same EngineReference, parameters, and net ordering [pin → Nodes[0], "0" → Nodes[1]]); they differ only in glyph and instance prefix. Match the symbol to its analysis role (`LoadTuner=`/`SourceTuner=`); the role (assigned by the analysis) selects the stamp, and the engine mints the source's internal drive node when needed.

### 1.3 `.cnl` surface
```
Tuner:Load  n_drain 0  Z[1]=80+j*10  Z[2]=1  Zdefault=1e-6  BiasTee=on  Vbias=48
Tuner:Src   n_gate  0   Z[1]=25                         BiasTee=on  Vbias=-3.05
```
No `TuneHarm` on the Tuner (the `Loadpull` directive sets it, §2). `Z[1]`/`G[1]` is **required**; `Zdefault` defaults to `1e-6` if omitted. The **source** Tuner needs no separate drive source — when the `Loadpull` analysis names it `SourceTuner=`, it stamps its own internal `V_1Tone` and computes `|Vs|` from its `Z[1]` and the analysis-supplied `Pavl` (§1.1, §3). Γ form: replace `Z[1]`/`Z[2]`/… with `G[1]`/`G[2]`/… and optionally add `Z0=` (default 50 Ω); forms may be mixed per harmonic, but the same harmonic may not be given both an impedance and a reflection (error). Values resolve through the expression engine, so terminations and bias may be parameters/expressions. (Bias-tee sub-parameters — choke/block values vs "ideal" — are an open item, §7.)

---

## 2. The `Loadpull` analysis directive

The `Loadpull` analysis holds the **procedure** and references the Tuners by name. It wraps the Phase-4a HB engine: for each termination grid point, it runs an inner adaptive power sweep (the HB solve at each Pin), applying the swept termination to the named Tuner.

### 2.1 Directive keys (the procedure — "what it does")
| Key | Meaning | Default |
|---|---|---|
| `LoadTuner` | name of the load-side `Tuner` (**required**) | (required) |
| `SourceTuner` | name of the source-side `Tuner` (**required**) — it owns the RF drive (§1.1) | (required) |
| `Sweep` | **which Tuner's termination is varied over the grid**: `Load` (loadpull) or `Source` (sourcepull). Distinct from `LoadTuner`/`SourceTuner`, which only name the Tuners and their roles — `Sweep` selects which one the `.gam` grid drives | Load |
| `Tone` | the analysis fundamental f0 (as in the HB directive, §3.2 of the HB note) | (required) |
| `TuneHarm` | which harmonic **of the swept Tuner** (selected by `Sweep`) to override per grid point (1 = f0, 2 = 2f0, …) | 1 |
| `MaxHarm` | harmonic count K | 5 |
| `Grid` | path to a **`.gam` grid file** (Γ or Z points, one per line — §2.2) | (required) |
| `Compression` | target gain compression x for the P-xdB stop (dB), e.g. 3 | 3 |
| `GainType` | which gain drives compression: `Gt` (transducer, Pout/Pavl) or `Gp` (power, Pout/Pin_delivered) | Gt |
| `PinStart` | starting available power for the drive-up (dBm) | (required) |
| `PinStep` | power step (dB) | 1 |
| `PinMax` | **safety cap** — never drive past this Pavl (dBm), even if compression not reached | (required) |
| `Tickle` | optional single very-low Pin point prepended to anchor small-signal gain (dBm); `off` to disable | on, e.g. −50 |
| `MaxIter` | **max Newton iterations of the underlying HB solve** (per inner solve, before continuation backoff / recording non-convergence) | 100 |
| `PinOverride` | optional explicit Pin list, overriding the adaptive drive-up entirely (advanced) | none |

The directive **assigns roles, and the roles stamp differently** (§1.1): `LoadTuner=` names the load-side Tuner (passive termination); `SourceTuner=` names the source-side Tuner, which **also becomes the RF drive** (it stamps an internal `V_1Tone` and computes `|Vs|` from its `Z[1]` and the per-step `Pavl` the analysis supplies — so no separate drive source is written in the netlist). **Both `LoadTuner` and `SourceTuner` are REQUIRED** — error if either is missing. The requirement is what closes the architecture: the source Tuner gives the engine the drive instance (for `Pavl`/`|Vs|`) *and* the DUT-input net (for `Pin_delivered`, needed by the live `Gp` compression calc); the load Tuner gives the DUT-output net (for `Pout`). Without both, the user would have to hand the analysis a `V_1Tone` instance and the DUT-input/output net names by hand — painful and error-prone. A Tuner declares its terminations role-neutrally; the analysis decides which is load and which is source, and the source assignment is what adds the drive. Either may be the swept one (loadpull vs sourcepull). **`Pavl` is referenced to the source Tuner's `Z[1]`**, and the analysis hands the source Tuner a new `Pavl` at each Pin step of the drive-up (§3).

**Regularization for loadpull:** because every `Tuner` bias-tee creates the voltage-pinned ideal-choke DC interface (linear-engine §4.3.1), the DC `InductanceRegularization` is **known** to be needed at every grid×Pin point. The loadpull engine therefore runs with **`InductanceRegularization=Always`** — assemble the regularized DC interface from the start, skipping the speculative fail-then-retry of `IfNecessary` on each of the hundreds of inner solves. This is a real per-point speedup multiplied across the 2-D sweep.

### 2.2 The grid file (`.gam`) — Γ or Z, one point per line
The termination grid is a **separate `.gam` file**, not inline in the netlist — a loadpull grid is *data* (often hundreds of points, machine- or GUI-generated), not circuit structure, and inlining it would bloat the netlist and mix data with structure. This mirrors a real loadpull system, where the tuner controller reads a *pattern file* of impedance states to visit. (A future circuitRF GUI will generate `.gam` files from a Smith-chart grid picker.)

Format: an **optional header line declares the form**, then one complex point per line. The parser is **forgiving** (these files are meant to be quick-and-dirty, robust to the user):
```
# gamma Z0=50 mag_ang        ; header is optional; tags: re_im | mag_ang | re+j*imag
0.50  30
0.50  60
...
```
- **Form tag:** `gamma` (with optional `Z0=`, default 50 Ω) or `impedance`. If the form tag is absent, **default to `impedance`** (a bare grid of Z points; use the `gamma` tag for Γ).
- **Column/complex format:** `re_im` (two columns: real, imag), `mag_ang` (two columns: magnitude, angle°), or `re+j*imag` (one column, the `.cnl` complex literal like `0.5+j*0.3`).
- **Forgiving inference when the format tag is absent:** if a data line's value contains `j` or `i` (an imaginary marker, e.g. `0.5+j*0.3`), parse it as the `re+j*imag` literal form; otherwise assume **`re imag`** (two columns). So a header-less file of `re imag` pairs, and a header-less file of `re+j*imag` literals, both parse correctly without the user declaring anything.
- The engine converts Γ↔Z via **RfCore** against `Z0` and the `TuneHarm` reference. Each line is one grid point; the engine visits them with warm-start ordering (§3.3). Blank lines and `;`/`#` comment lines are skipped.

**Reading & Γ↔Z conversion (`GamReader`).** Every point is stored as **both** Γ and Z, converted once against the file's `Z0`: `Z = Z0·(1+Γ)/(1−Γ)`, `Γ = (Z−Z0)/(Z+Z0)` — with the degenerate cases guarded (a point on the unit circle maps to a large finite Z; `Z ≈ 0` maps to `Γ = −1`). `mag_ang` angles are **degrees**; the `re+j*imag` form accepts the `.cnl` complex-literal variants (`80+j*10`, `0.5-j*0.3`, suffix `0.3j`, pure-imaginary `j*0.5` / `-j`). Header tags are case-insensitive and order-independent; an inline `;` truncates the rest of a data line.

The `Loadpull` directive references the file by path: `Grid="hero3_load.gam"` — resolved **relative to the netlist directory** (absolute paths used as-is).

### 2.2.1 Multi-frequency grids (frequency-swept loadpull)
A single `.gam` file can carry **one termination grid per frequency**, so a frequency-swept loadpull (a parametric sweep over a tone-frequency variable wrapping the loadpull) reads the right grid at each frequency. Frequency blocks are delimited by a bare **`freq=<value><unit>`** directive line — **not** `#`-prefixed, since `#` is the header/comment token. The unit is one of `Hz` (default), `kHz`, `MHz`, `GHz`, `THz` (case-insensitive); every data line after a `freq=` line belongs to that block until the next `freq=`:
```
# impedance Z0=50 re+j*imag
freq=1.8GHz
80+j*10
60-j*5
freq=2.2GHz
85+j*5
70+j*0
```
- A file with **no** `freq=` line is one **freq-less** block, applied at **any** frequency (the back-compatible single-grid case).
- A frequency-tagged file is read **per analysis frequency**: the engine (`GamReader.ReadFileForFreq`) selects the block whose `freq=` is **nearest** the current frequency by `|Δf|`; freq-less blocks apply at any frequency (they sort last). So one file can hold a measured grid per band, or a single grid reused across all frequencies.

A wholly empty or comment-only file parses to one empty block (no grid points) rather than an error.

### 2.3 How the sweep overrides the Tuner
Per grid point, the engine overrides the swept Tuner's termination **at the `TuneHarm` harmonic** with the grid value and re-runs the inner HB power sweep, leaving the Tuner's other-harmonic terminations (`Z[1]`/`Z[2]`/… except the tuned one, and `Zdefault`) at their declared values. Mechanism: the tuned harmonic's value references a **swept variable** the loadpull engine sets each grid point (the same "set a variable, re-run" mechanism as Hero 2's `Pavl_dbm` power sweep and the HB directive's `Sweep=`), rather than a bespoke "reach in and mutate the component" path. So loadpull is "vary the variable that the Tuner's `TuneHarm` band reads, across the grid; at each grid value, run the adaptive Pin sweep."

---

## 3. The 2-D sweep and the compression stop

Loadpull is a **2-D sweep**: an outer **termination grid** (Γ/Z) × an inner **adaptive power sweep** (Pin), with the inner sweep stopping at compression.

### 3.1 The inner adaptive power sweep (per grid point)
At a fixed termination, drive the DUT up in power and stop at the target compression:
1. **Tickle (optional, on by default).** Prepend a single very-low-Pin point (e.g. −50 dBm) so the drive-up establishes a clean small-signal gain reference before climbing — ensuring "max gain" is the true small-signal gain, not a point already inside compression. (Without it, a sweep starting at a moderate Pin could begin mid-compression and mis-locate the reference.)
2. **Drive up** from `PinStart` by `PinStep`, running the HB solve at each Pin (warm-started, §3.3).
3. **Track gain and its running maximum.** At each Pin step compute the chosen gain (`Gt` or `Gp`, §4) live, and track `Gmax` seen so far.
4. **Detect compression.** The compression point is the higher-Pin point where gain has dropped `x` dB (the `Compression` target) below `Gmax`. **Stop at P-xdB + ~0.1 dB** — i.e. drive a hair past the compression point so a post-processor can numerically bracket/interpolate the exact P-xdB (the engine captures the brackets; it does not itself interpolate).
5. **Hard stops** regardless of compression: reach `PinMax` (the safety cap), or the HB solve fails to converge (driving further is futile). On either, stop and record the reason.

### 3.2 Why compression detection must be live (not post-only)
The stop decision needs gain *now* to know when to quit driving — and the stop point differs per grid point because **gain depends on the termination**. So the engine computes gain (hence Pout, Pin_delivered/Pavl) at each Pin step internally. The *elaborate* analysis (exact P-xdB interpolation, Pout-at-compression, contours) is deferred to a **post-processor** over the captured sweep (§5); only the live gain-tracking and the stop trigger are in the engine. This is the minimal-measurement-in-engine principle: capture everything, compute in-engine only what the control loop requires.

### 3.3 Γ-grid warm-starting (continuation across the grid)
Each grid point's HB solve warm-starts from the **nearest already-converged adjacent grid point** (its converged spectra are a good seed), making the hundreds-of-points loadpull tractable on the proven 4a engine. "Nearest" is measured by **`RFNetwork.VSWR`** (RfCore): the VSWR between two complex Γ (or Z) points — **closer to 1 ⇒ closer points**. So the engine picks, among converged neighbors, the one with VSWR-to-this-point nearest 1 as the seed. (The inner Pin direction warm-starts from the previous Pin point, per the HB note's `DriveStepping`.)

---

## 4. The live measurements (the subset the stop needs)

The loadpull engine computes these **internally in C# from the HB V/I spectra** (it does not depend on a user writing a measurement). Users may *additionally* add their own `.cnl` measurement equations (measurements.md) for reporting; those are separate from the engine's internal control measurements. The measurement *vocabulary* already exists in `measurements.md` — 4b-1 **implements the subset** the stop-logic needs.

**The "which nodes?" answer — the engine learns nodes from the named Tuners:**
- **DUT output node** = `LoadTuner`'s DUT-facing net.
- **DUT input node** = `SourceTuner`'s DUT-facing net.
So no user measurement is needed for the internal control loop; the Tuner connections supply the nodes.

Live quantities, per Pin step, at the fundamental (k = 1):
- **Pavl** (available source power) — the drive variable the analysis hands the source Tuner each step, **referenced to the source Tuner's `Z[1]`**; the source Tuner uses it to set its internal `|Vs|` (§1.1).
- **Pin_delivered** = ½·Re(V·I*) at the **source Tuner's DUT-facing port**, fundamental. (Accounts for input mismatch — e.g. a deliberately mismatched 25 Ω source into a 50 Ω gate.)
- **Pout** = ½·Re(V·I*) at the **load Tuner's DUT-facing port**, fundamental.
- **Gt** = Pout / Pavl ; **Gp** = Pout / Pin_delivered. The `GainType` key selects which drives the compression stop.

These reuse the magnitude/peak phasor convention (linear-engine §2.2: P = ½·Re(V·I*)). The HB engine's I cube (built in Phase 4a) supplies the currents; the Hero-2 regression already exercised currents (the DC current rose with drive and the hand-checked efficiency numbers were sensible), so loadpull builds on a current path that is **already validated**, not first-exercised here.

---

## 5. Output — capture everything

The loadpull engine writes the **full 2-D dataset**: for every (grid point, Pin step) it retains the converged V and I spectra (all harmonics incl. k=0, per the measurements.md retention requirement) plus the live Pavl/Pin_delivered/Pout/Gt/Gp. The result cubes gain a **termination axis** (the grid) on top of the HB cubes' `{node, harmonic, Pin}`.

**The post-processor** (a later, separate concern — possibly 4b-2 or its own utility) computes the elaborate FOMs from this captured data: exact P-xdB by interpolation, Pout-at-compression (gain + Pin at the compression point — the critical PA metric), drain efficiency/PAE at compression, and the loadpull **contours** (constant-Pout / constant-PAE circles on the Smith chart). 4b-1 captures; the post-processor analyzes. The capture-everything principle means no information needed by later analysis is lost during the sweep.

---

## 6. Hero 3 — the 4b-1 acceptance anchor

Hero 3 is a single-device loadpull (the grounded-source GaN HEMT SDD again — the proven device) with a **load `Tuner`** swept over a Γ-grid at the fundamental, each point driven to **3 dB compression** (`GainType` per the owner's choice), with a fixed (or `Tuner`) source. Acceptance:
- The 2-D sweep runs: every grid point's inner Pin sweep drives up and stops correctly (at P-3dB + ~0.1 dB, at `PinMax`, or on convergence failure — recording which).
- The live measurements (Pavl/Pin_delivered/Pout/Gt/Gp) are computed from the spectra and match hand/spot checks.
- Γ-grid warm-starting works (VSWR-nearest neighbor seed) and the sweep is tractable.
- The full 2-D dataset is captured (V, I, the live FOMs, the termination axis).
- (Golden-data approach TBD with the owner — likely self-generated regression like Hero 2, given the external-reference trust issues; the owner will decide the reference.)

---

## 7. Open items (4b-1)
1. **`Tuner` bias-tee sub-parameters** (§1.1) — choke/block values vs an "ideal" flag, and how `Vbias` names the supply. Finalize at bring-up. (The termination grammar — `Z[1]`/`Zdefault`/`G[1]`/`Z0` — is now settled, §1.1/§1.3.)
2. **`.gam` defaults** (§2.2) — settled: optional header (`re_im`/`mag_ang`/`re+j*imag`); absent form tag → `impedance`; absent column tag → infer `re+j*imag` if a `j`/`i` marker is present, else `re imag`. Forgiving by design.
3. **Compression-detection robustness** (§3.1) — confirming "gain dropped x dB below running max AND past the max" is the exact live trigger, and the +0.1 dB overshoot policy, against Hero 3 behavior.
4. **Hero 3 golden reference** — **decided:** self-generated **regression** (à la Hero 2), the owner verifies the results. Labeled self-generated/not-independently-validated.
5. **Bias-by-current option** — the user specifying a bias *current* and the analysis finding the gate voltage for a given Vds (mentioned in brainstorming) — **deferred to 4b-2**; not core to 4b-1.

---

## 8. Deferred to Phase 4b-2 (the search/automation layer — a major differentiator)
These are search algorithms built **on** the proven 4b-1 engine; each calls loadpull repeatedly. They get their own design pass (own conversation) because a sloppy search is worse than none, and they carry their own convergence/robustness concerns. Recorded here so they are on the roadmap, not lost:

- **Live efficiency detection** — `Pdc = Vdc·Idc` from each Tuner's internal bias supply (the Tuner exposes the bias-supply V and I node handles, §1.1), hence DE/PAE. **Not** needed in the 4b-1 compression loop (compression keys off gain), but **required by the efficiency search (MXE)** to decide where to place the next termination, and by the elaborate post-processor measurements. 4b-1 must therefore *expose and capture* the bias-supply node data (so the information exists); 4b-2 *consumes* it for the search.
- **MXP** — find the load termination giving **maximum Pout at compression**, then loadpull a refined grid around it.
- **MXE** — find the load termination giving **maximum efficiency (PAE/DE) at compression**.
- **Auto-Zsource at f0** — find the MXE load, then set the source `Tuner`'s f0 termination to the DUT's **Zin\*** (conjugate of input impedance) at ~5 dB backoff from compression. Note **Zin changes with the load termination** for non-unilateral devices, so this is a coupled load-then-source search, not a one-shot.
- **Frequency loop** — wrap the above across a frequency sweep so a DUT is loadpulled across frequency, capturing MXP, MXE, and Zin vs frequency with minimal user input.

These are the features that differentiate circuitRF from other RF CAD; 4b-1 exists to make them buildable on a trustworthy core.

---

## 9. Summary of decisions
- **`Tuner`** = the user-facing programmable termination (declared role-neutrally; the `Loadpull` analysis assigns load vs source, and **the roles stamp differently** — a load tuner is passive, a **source tuner also owns its internal RF drive** and computes `|Vs|` from its `Z[1]` + the analysis-supplied `Pavl`). Built internally from `Z_Port` + optional bias-tee (+ internal `V_1Tone` in the source role). Self-sufficient as a standalone termination (declares `Z[1]`/`Z[2]`/… or `G[1]`/… per harmonic with `Z[1]`/`G[1]` required, `Zdefault` catch-all defaulting to 1e-6, optional `Z0` for the Γ form; same harmonic may not be both Z and G), usable in non-loadpull sims (presents `Z[1]` flat over frequency outside HB). **Does not know which harmonic is tuned** — like a real slabline tuner; the `Loadpull` directive's `TuneHarm` decides. Exposes its DUT-facing net and (with an internal bias-tee) its bias-supply V/I nodes to the analysis. The embedded ideal choke re-uses the `InductanceRegularization` auto-fix (linear-engine §4.3.1).
- **The `Tuner` holds "what it is"** (connection, tunable harmonic, default + fixed terminations, optional internal bias-tee + bias point); **the `Loadpull` directive holds "what it does"** (grid, compression target, Gt/Gp, Pin start/step/max, tickle), referencing Tuners by name.
- **The engine learns measurement nodes from the named Tuners** (load tuner's net = DUT output; source tuner's net = DUT input) — answering "which nodes?".
- **Loadpull is a 2-D sweep**: outer Γ/Z grid × inner adaptive Pin drive-up, stopping at **P-xdB + ~0.1 dB**, with a **PinMax safety cap**, optional **tickle**, and stop-on-convergence-failure.
- **Compression detection is live** (gain vs running-max), on the chosen **Gt or Gp**; the engine computes Pavl/Pin_delivered/Pout/Gt/Gp internally from the V/I spectra (first load-bearing use of currents); elaborate FOMs (exact P-xdB, Pout-at-compression, contours) are a **post-processor** over the captured data.
- **Γ-grid warm-start** from the VSWR-nearest converged neighbor (RfCore `RFNetwork.VSWR`).
- **Grid accepts Γ or Z** (RfCore conversion).
- **Capture everything**; the post-processor analyzes.
- **Sub-gated**: 4b-1 = this engine + Hero 3; **4b-2** (own design pass) = MXP/MXE/auto-Zsource/frequency-loop search layer, the differentiator.
