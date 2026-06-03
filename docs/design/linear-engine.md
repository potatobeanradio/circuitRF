# circuitRF — Linear Engine Design (MNA, DC, S-parameters)

**Status:** Draft (rev 5) for review · **Date:** 2026-06-01
**Reads with:** `docs/design/data-model.md` (§3 elaboration, §5 `ComponentModel`, §6 RfCore, §7 result model), `docs/PRD.md` (§4 Hero 1, §5 simulation scope, §14 NFRs).
**Defers to:** `docs/design/harmonic-balance.md` (the nonlinear partition + conversion-matrix Jacobian, which builds on this), `docs/design/expressions.md` (parameter resolution).

This note specifies the **linear engine**: how an `ElaboratedNetlist` becomes a sparse modified-nodal-analysis (MNA) system, how each `ComponentModel` contributes to it, how DC and S-parameter analyses are formulated and solved, and how the port S-parameters are extracted. It gates **Phase 2** and **Hero 1**, and it provides the linear machinery the harmonic-balance engine reuses. It defines *method and contracts*, not full derivations or C#. No code is written until this is approved.

---

## 1. What this engine produces

- **DC analysis** (linear) — the operating point of a linear circuit; also the linear scaffolding for the nonlinear DC solve and the HB seed (harmonic-balance note).
- **S-parameter analysis** — the multiport S-parameters over a frequency sweep (Hero 1): build the MNA at each frequency, extract the port network, renormalize, convert to S via RfCore.
- **Linear characterization for harmonic balance** (an internal service, not a user analysis) — the linear subnetwork as a frequency-domain N-port at the nonlinear-facing nodes, per harmonic, plus the excitation the independent sources present there (§2.1, §10).

The two analyses run headless from the CLI and write a `DataSet` (§8). The S-parameter `DataSet` carries the `S` cube `{freq, outPort, inPort}` (data-model §7), which RfCore writes to Touchstone and splotRF plots. The three uses share one MNA assembly but differ in frequency set, excitation, and output — see §2.1.

---

## 2. MNA formulation

Modified Nodal Analysis solves `A x = b` at each frequency, where `x` stacks **node voltages** (one unknown per non-ground node; ground is node 0, data-model §3) and **branch currents** for elements that cannot be written as a voltage-controlled admittance. Elements fall into two groups:

- **Group 1 — admittance-stampable** (no extra unknown): resistor, capacitor, current source, and any frequency-domain N-port **whose matrix is available as a finite `Y(ω)`** (§4.1). A 2-terminal admittance `y` between nodes `a,b` stamps `+y` at `(a,a)` and `(b,b)`, `−y` at `(a,b)` and `(b,a)` (ground rows/cols dropped).
- **Group 2 — branch-current unknowns** (one extra row/col each): inductor, ideal voltage source (DC/AC/RF source), current probe, **frequency-domain N-ports given as `Z(ω)`** (the default for Touchstone/SNP, impedance block, TLIN — §4.1), and anything constrained by a voltage relation. Each adds an unknown branch current `i` and a constraint row.

This split is the standard MNA structure. The reason it matters for circuitRF is **DC correctness** (§5): treating inductors as Group-2 makes a DC short *exact* rather than a large-number fudge.

The **FFT/sign and current-direction conventions** are fixed once and documented in the engine (a branch current is defined flowing from the element's first node to its second; a current source `J` injects into its first node). These conventions are stated in `src/Engine/CLAUDE.md` so they are never silently changed — the same discipline the HB note applies to FFT conventions.

### 2.1 Three uses, three formulations

The same MNA assembly and stamping serve three callers, but they differ in **frequency set, excitation, and output**. Stating the differences here keeps them from being conflated in code.

| Aspect | DC analysis | S-parameter analysis | HB linear partition |
|---|---|---|---|
| Frequency | single ω = 0 | swept grid `{ω₁ … ω_M}` | discrete harmonics `{0, ω₀, 2ω₀, … Hω₀}` |
| What is assembled | full netlist | full netlist | **linear partition only** (nonlinear devices removed) |
| Independent sources | **on** (real DC values) | **off** (zeroed — the network's own S) | **on** (bias + RF drive), as an excitation at the interface |
| "Ports" | none | user `Port`/`Term` | the **nonlinear-facing nodes** |
| Excitation | the sources themselves | unit stimulus, one user port at a time | (a) unit stimulus at each nonlinear-facing node to extract the interface network; (b) the independent sources, for their contribution at the interface |
| Output | one operating point: node V + branch I | S-matrix cube `{freq, out, in}` | per-harmonic interface **Y (or Z) N-port** + the **source-excitation vector** at the interface |
| Solve pattern | single solve | factor/freq, multi-RHS over ports | factor/harmonic, multi-RHS over nonlinear-facing nodes |

Three points are easy to miss and worth stating outright:

- **Sources off for S-parameters, on for DC and HB.** S-parameters are a property of the network, so the independent sources are zeroed and the excitation is the port stimulus. DC and the HB partition are operating-point calculations — the real sources (bias, RF drive) must be present. *Zeroing* a source has a precise meaning: a voltage source → short (its constraint becomes `Va − Vb = 0`); a current source → open (its injection is dropped).
- **The HB "ports" are not the user ports.** They are the nodes where nonlinear devices attach. The linear engine extracts the network the devices *see* at those nodes — this is the reference's "extra port added at each nonlinear node" (its `NodalAnalysis` appends a port per nonlinear connection to build the admittance matrix HB needs). The user `Port`/`Term` components play no role in that extraction.
- **HB needs two things from this engine, not one.** Besides the interface N-port (the network the devices see), HB needs the **excitation those devices see from the independent sources** — the bias and drive transformed to the interface (a Norton/Thévenin equivalent at the nonlinear nodes, per harmonic). The S-parameter path needs only the first; HB needs both.

The **DC (k = 0) member of the HB harmonic set is formulated exactly like the standalone DC analysis** (inductor short, capacitor open, `gmin` — §5). There is one DC formulation, used both standalone and as the k = 0 slice of HB — which is why `Pdc`/PAE are read from the HB DC component rather than a separate DC run (measurements §5).

### 2.2 Engine-wide conventions (fixed once)
Two conventions are fixed across the whole engine and recorded in `src/Engine/CLAUDE.md` so they are never silently changed:

- **Phasor amplitude = magnitude (= peak), not RMS.** A phasor `V = |V|·e^(jθ)` represents the time signal `|V|·cos(ωt + θ)`, whose peak is `|V|`. This sets the constant in every power relation — e.g. available power `Pavl = |Vs|^2 / (8·Re(Zs))` (§4.2) and `Pout = ½·Re(V·I*)`. A silent switch to RMS is a 3 dB error in every power number, so the convention is stated, not assumed.
- **Current-direction / source-sign and the time↔frequency FFT sign** (the latter detailed in the HB note): a branch current flows from the element's first node to its second; a current source `J` injects into its first node.

---

## 3. `MnaSystem` and the stamping API

The **engine owns `MnaSystem`**; each `ComponentModel` only *contributes* stamps through a controlled API (data-model §5). The model never sees the raw matrix, never allocates rows, and never knows the global indexing — it is handed node indices and asks the system to accumulate contributions. This is what makes adding a component type local (PRD §6).

```csharp
class MnaSystem
{
    // Group 1: accumulate an admittance y between two nodes (ground-aware).
    void AddAdmittance(int nodeA, int nodeB, Complex y);

    // Group 1: accumulate one entry of an N-port admittance block across a node set.
    void AddBlockAdmittance(int rowNode, int colNode, Complex y);

    // Group 2: allocate a branch-current unknown for this element and return its index.
    int AddBranch();

    // Group 2: KCL coupling — branch current i flows nodeFrom -> nodeTo.
    void AddBranchCurrent(int branch, int nodeFrom, int nodeTo);

    // Group 2: one term of a constraint row (e.g. Va - Vb - jωL·i = 0).
    void AddConstraint(int branch, int node, Complex coeff);
    void AddBranchConstraint(int branch, int otherBranch, Complex coeff); // off-diagonal (mutual)

    // Right-hand side (sources).
    void AddCurrentInjection(int node, Complex j);     // current source
    void AddSourceValue(int branch, Complex value);    // voltage-source RHS
}
```

A model's `Stamp(mna, component, omega)` (data-model §5) calls these. The engine has already resolved the component's node indices and parameter values (kinded `Real`/`Complex`, data-model §3/§8) before calling `Stamp`.

---

## 4. How each category stamps

| Category | Group | Stamp |
|---|---|---|
| Resistor | 1 | `AddAdmittance(a, b, 1/R)` (R is Real; a complex R is rejected at elaboration) |
| Capacitor | 1 | `AddAdmittance(a, b, jωC)` — at DC (ω=0) this is `0` = open, exactly |
| Inductor | 2 | branch `i`; KCL `AddBranchCurrent(i, a, b)`; constraint `Va − Vb − jωL·i = 0` — at DC reduces to `Va = Vb` = short, exactly |
| Mutual inductance | 2 | couples two inductor branches (§7) |
| Frequency-domain N-port (Touchstone/SNP, impedance block, TLIN, user freq model) | 2 default, 1 if native Y | **default**: `Z(ω)` branch-current expansion — one branch per port, constraint `V = Z·I`; **if the block is natively `Y(ω)`**: stamp the admittance block directly with `AddBlockAdmittance` (no extra unknowns). See §4.1 |
| **`Z_Port`** (general N-port, impedance entries as `freq`-expressions) | 2 | the N-port whose `Z[i,j]` matrix entries are **expressions in the reserved keyword `freq`** (§4.4), evaluated per stamping frequency; stamped by the same `Z(ω)` branch expansion as the other freq-domain N-ports. Subsumes the impedance block. Hero 2 uses the 1-port (2-net) case. |
| DC / AC voltage source | 2 | branch `i`; constraint `Va − Vb = E`; `AddSourceValue(i, E)`. The tone-carrying source is **`V_1Tone` / `V_nTone`** (§4.4) |
| RF power source | 2 | an AC voltage source behind a series internal impedance `Zs` (default 50 Ω) — see §4.2. The user specifies **available power** `Pavl`; the source back-solves its voltage. (A convenience component; the Hero-2 drive instead composes `V_1Tone` + `Z_Port`, §4.4.) |
| AC current source | 1-ish | `AddCurrentInjection(a, +J)`, `AddCurrentInjection(b, −J)` |
| Port / Term | — | defines a port: records the port number and its reference impedance `Z0_k` for extraction (§9). Not stamped as a physical resistor for S-parameters (§9) |
| Current probe | 2 | a 0 V branch whose current is the recorded quantity |

### 4.1 Frequency-domain N-ports — `Z(ω)` by default, `Y(ω)` when available
Touchstone/SNP blocks, the impedance block, the ideal transmission line, and user frequency-domain models all reduce to the same thing: at the swept frequency `ω`, the element produces an `n×n` network matrix that is stamped across its `n` nodes (referenced to its reference node). **Two stamping forms, and the choice is by how the block is naturally defined:**

- **Default — `Z(ω)` branch-current expansion (Group 2).** Add one branch-current unknown per port and the constraint `V_port − V_ref − Σ_j Z[port,j]·i_j = 0`. This is **always well-defined**: every passive network has a finite `Z` (you can always write `V = Z·I`), so the Z-form never hits an infinity. It is also what the reference did (it stamped the block's `Z` matrix via nodal expansion, adding a port row per connection) and it is the form that naturally exposes the extra port HB needs (§2.1, §10). Cost: `n` extra unknowns per block.

  **Reference node (the `V_ref` above) is not necessarily ground.** A frequency-domain N-port carries a **single shared reference node** against which all its port voltages are defined. By convention the `.cnl`/VendorA node list gives **N nets for an N-port** (each port referenced to **ground**, node 0) **or N+1 nets**, in which case the **last net is the common reference node** for all ports. So an N-port SnP line with N+1 nets is the floating-block case: `V_ref` in every port constraint is that reference net's voltage (an unknown), not zero, and the port return currents sum back into the reference node (its KCL row gains the `−` side of each port branch). When the list has exactly N nets, `V_ref = 0` and the reference terms vanish — identical to a ground-referenced block. This matches the reference Swift (`ReferenceNet`, with the cross-term `−1` entries emitted only when the reference is not ground) and the VendorA convention. The model receives the reference node as just another node index and stamps it itself (engine-owns-matrix / model-contributes-stamps) — the engine does not special-case it.

  This N-or-(N+1) reference-node convention applies to **all frequency-domain N-port components** (SnP block, impedance block, TLIN, user freq model) — they share this stamp. It does **not** apply to the 2-terminal primitives (R, L, C), whose two nets are simply their terminals.
- **Optimization — `Y(ω)` admittance stamp (Group 1), when the block is natively `Y`.** If a block's matrix is available as a finite `Y(ω)` (a user model that hands us `Y` directly, or a network whose `Y` is known well-conditioned), stamp the admittance block directly with `AddBlockAdmittance` and add **no** extra unknowns — the lighter, sparser form. This is the v1 opportunistic path: cheap when applicable, and only a little extra code.

**Why not Y-by-default?** A `Y` block requires a *finite* admittance matrix, but a network with a series-through path or any open-circuit port has a **singular `Y`** (infinite entries) — and Touchstone files routinely contain such networks, including the embedded block Hero 1 exercises. Defaulting to `Y` would fail or lose accuracy on exactly those cases. So `Z`-expansion is the robust default and `Y` is taken only when the block is genuinely given as a finite `Y`.

Sourcing of the matrix is unchanged by the choice of form:
- **Touchstone/SNP** — interpolate the stored network to `ω` and obtain `Z(ω)` (or `Y(ω)`) via **RfCore** (which holds the `SNP` and does S/Z/Y conversion and interpolation, data-model §6). The embedded-SNP case Hero 1 exercises.
- **Impedance block** — evaluate the `Z[i,j](ω)` expressions (the expression engine, with `freq` in scope) and assemble `Z(ω)` directly — the native-Z case, stamped by expansion.
- **Transmission line (TLIN)** — closed-form 2-port from its characteristic impedance, electrical length, and reference frequency, in whichever of `Z`/`Y` is natural.
- **User frequency model** — its evaluated `Z(ω)` or `Y(ω)`; a native-`Y` model takes the admittance stamp.

### 4.2 RF power source — available power to source voltage
An RF power source is an **AC voltage source `Vs` behind a series complex source impedance `Zs`** (default 50 Ω). The user specifies the **available power** `Pavl` (the maximum power the source delivers into a conjugate-matched load) rather than a raw voltage; the component back-solves `Vs`.

With **magnitude (= peak) phasor amplitudes** (the engine-wide convention, §2.2), available power relates to source voltage by

```
Pavl = |Vs|^2 / (8 * Re(Zs))      =>      |Vs| = sqrt(8 * Pavl * Re(Zs))
```

The factor of **8** (not 4) follows from the magnitude/peak convention with the conjugate match dropping half the source voltage across `Zs`; under an RMS convention it would be 4. This is why the magnitude convention must be fixed engine-wide — a mismatch here is a silent 3 dB error in every power number.

- **Phase** defaults to **0°**; advanced users may set it anywhere in **−360° .. +360°**.
- An RF power source may carry an **arbitrary number of frequencies**; each frequency entry independently specifies its own `Zs`, `Pavl`, and phase. (This is exactly the per-tone drive the multi-tone HB analysis needs — one entry per tone.)
- **Stamping:** the source is a Group-2 AC voltage source (branch `i`, constraint `Va − Vb = Vs`) in series with `Zs`. `Zs` enters as a series impedance on that branch (or, equivalently, a small two-node sub-stamp); the back-solved `|Vs|` and phase set the source RHS via `AddSourceValue`.

Though specified here, the RF power source is exercised by the **HB drive** (Phase 4), not by Hero 1; it is documented now while the formulation is fresh. The linear engine only needs the stamping; the `Pavl → |Vs|` mapping is shared with the HB drive setup.

### 4.3 Non-physical inputs — warn and continue; regularization settings
circuitRF is a research/experimentation tool: it lets users build deliberately non-physical circuits, **warns**, and keeps solving — it does **not** hard-error on a non-physical-but-mathematically-handleable input. (This is a deliberate reversal of an earlier "reject k≥1" rule.) The cases:

- **Negative R** (active element): stamp `1/R` with its sign; **warn** (`R<0 on R1: non-physical/active; proceeding`). No singularity risk from the sign.
- **R = 0**: a true short would be infinite admittance. Stamp **`Gmax`** (a near-short conductance, default **1e12 S**, exposed as an engine setting alongside gmin) and **warn**. Gmax is the conductance ceiling, the dual of gmin's conductance floor.
- **Inductor optional series R / C**: an `L:` line may carry `R=` and/or `C=` (e.g. a VendorA or native series-RLC branch). Both optional, both stamped in series on the inductor's single branch: `Va − Vb − (R + jωL + 1/(jωC))·i = 0`. Absent term = omitted (no R → lossless; no C → no capacitive term). **DC care:** a present series `C` makes the branch an open at DC (the `1/(jωC)` term diverges as ω→0), so at DC such a branch carries no current — the opposite of a bare inductor's DC short. (A *physical* inductor's series R, when present, is also what keeps an otherwise purely-reactive coupled block non-singular — stamp it; do not drop it.)
- **Mutual k ≥ 1** (over-coupling, `M² ≥ L1·L2`): non-physical but **allowed — warn**, do not reject. The coupled inductance matrix may be singular; the `InductanceRegularization` setting (below) rescues the solve.
- **Mixed-sign mutuals** (both +M and −M in one circuit): fully physical (coupling sign is geometry), **supported, no warning**. Stamp M with its sign; never negate or reject it.

**Two regularization settings** (each a tri-state `{ IfNecessary, Always, Never }`, default `IfNecessary`):
- **`ConductanceRegularization`** — this is `gmin` (1e-12 S, every node→ground); cures floating-node singularities.
- **`InductanceRegularization`** — the inductive dual (a tiny series resistance / diagonal loading on the inductor block); cures a near-singular or rank-deficient coupled-inductance matrix (e.g. a degenerate EM-extracted coupled-coil block, or k≥1), **and** the DC voltage-pinned-interface singularity (§4.3.1).

#### 4.3.1 Voltage-pinned DC interface (the ideal-choke singularity)
When the HB engine extracts the linear-partition interface admittance at **DC** (`ω=0`, harmonic-balance §10), an interface node tied through an **ideal inductor** (a bias-tee choke with no series R) to an **ideal voltage source** has a **zero-impedance DC path** to that source. Its DC voltage is then *hard-pinned* (`Z(0)=0` at that node), so the port `Z`-matrix is singular and cannot be inverted to a finite `Y(0)`. This is not a numerical defect — it is the math correctly reporting that an *idealized* bias network fixes that node's DC voltage exactly, with no freedom to shift.

**The mathematically exact treatment** is **constrained-system reduction ("Option 2"):** a voltage-pinned interface node is *not a free DC unknown* — its DC voltage is known (the bias value), so it is removed from the DC unknown set and substituted into the right-hand side as a boundary condition, and only the genuinely-free DC unknowns are solved. This is the standard handling of a Dirichlet/voltage constraint and introduces no fudge element. **It is the principled upgrade and is deferred** (a hardening-pass item), because it is a real formulation change to the DC extraction and its Jacobian block.

**The v1 mechanism ("Path A")** routes this through `InductanceRegularization`: under **`IfNecessary`** (default), when the DC interface extraction hits this singularity, the engine **auto-applies a tiny series resistance to the offending ideal inductor(s)** so `Z(0)` becomes finite (a large but invertible `Y(0)`), and **warns** (naming the node), exactly as `ConductanceRegularization`/gmin auto-cures a floating node. This is **regularization, not a circuit change** — it is honestly labeled, automatic, and **converges to the exact constrained-reduction answer as R→0** (a milliohm of choke DCR is physically real and, at RF, utterly swamped by `jωL`). It is the inductive dual of gmin: gmin is a conductance *floor* to ground; this is a series-resistance *floor* on an ideal inductor that would otherwise create a zero-impedance DC path. `Always`/`Never` behave as for the other regularizations (`Never` → fail with the singular-node diagnostic naming the pinned interface node and its `V_oc(0)`). The default series-R floor value is small (e.g. `1e-6 Ω`) and exposed as a setting.

> A *physically realistic* bias network (a choke with real DCR, or a bias resistor) has finite DC path impedance and **never hits this singularity** — `Z(0)` is finite, self-biasing appears naturally, and no regularization engages. The singularity is specific to *ideal* bias-tees; Path A makes ideal-choke circuits "just work" the way gmin makes floating-node circuits just work.

Semantics (identical for both): **`IfNecessary`** assembles *without* the regularization, attempts the factorization, and on failure adds it and retries (clean circuits pay nothing and get the unperturbed result; if a solve fails with both at `IfNecessary`, add **both** on retry — they're cheap and orthogonal). **`Always`** assembles *with* it from the start (skips the speculative failed solve — useful for large circuits known to need it; slightly perturbs all results). **`Never`** assembles without and, if singular, **fails with the singular-node diagnostic** (§6 / engine CLAUDE.md) — a validation mode for users who want to *know* a circuit is degenerate. **Warn only when a regularization actually engages**, never merely for a clean solve or for negative M.

### 4.4 `Z_Port` and the tone sources (`V_1Tone` / `V_nTone`)
These three components are the **RF source/termination vocabulary** the HB drive composes from. `Z_Port` provides a frequency-dependent impedance *environment*; the tone sources provide the *excitation*. They are deliberately separate so a drive is `tone source` + `Z_Port` in series — the impedance and the excitation are independent.

#### `Z_Port` — a frequency-controlled impedance N-port
A new general **N-port** `ComponentModel`, **Group 2** (branch-current unknowns), whose impedance-matrix entries `Z[i,j]` are **expressions in the reserved keyword `freq`** (the engine-injected stamping frequency, expressions.md §3). At each frequency the engine stamps, it evaluates the `Z[i,j](freq)` expressions to a complex `n×n` matrix and stamps it by the **same `Z(ω)` branch expansion** as every other frequency-domain N-port (§4.1) — including the **N-or-(N+1) reference-node convention**. So `Z_Port` *is* the impedance block of §4.1, generalized: instead of a fixed `Z[i,j](ω)` closed form, the entries are arbitrary user `freq`-expressions. Hero 2 uses the **1-port (2-net)** case (`Z[1,1]=<freq expression>`).

Because it stamps at whatever frequency it is given, `Z_Port` works in **any** frequency-stamping analysis — a swept linear S-parameter sweep *and* the HB per-harmonic solve. Its headline use is **per-harmonic termination**: a piecewise `if`-ladder over `freq` that returns a different impedance in each harmonic band, e.g. a load that is `80+j10` at the fundamental, `1` at the 2nd harmonic, and a near-short above. (The `if`/`elseif` ladder is evaluated **in order with short-circuit**, expressions.md §6, so the first true band wins.)

**Exact-harmonic-frequency guarantee (engine).** A `Z_Port` termination expression compares `freq` against integer multiples of the fundamental (`freq <= 2*RFfreq`, etc.). For this to be robust, **HB computes each harmonic's stamping frequency as the exact double `k · f0`** (the same arithmetic the user's band edges use), so `freq` at harmonic `k` is **bit-identical** to `k*RFfreq` in the expression and band comparisons against fundamental multiples are exact — no floating-point drift at a band edge. This holds because integer multiples of an exactly-representable `f0` (e.g. `2e9`) stay exact in IEEE-754 double well within `2^53`. The guarantee covers band edges that are integer multiples of the fundamental (the only sensible harmonic-termination edges); a non-harmonic edge (`2.5*RFfreq`) is outside it. **Consistency note:** the `freq` band edges should reference the *same* fundamental the HB analysis drives at — if a user's `RFfreq` variable disagrees with the analysis fundamental, the bands misalign with the actual harmonics (a setup error, not drift; a future nicety may warn when a `Z_Port`'s apparent edges do not align with the harmonic grid).

At **DC** (`freq = 0`) a `Z_Port` evaluates its expression at `freq = 0` (typically the sub-fundamental branch — e.g. a tiny `1e-5 Ω`); the bias-tee in a real drive network keeps that DC value from loading the bias supply, so it does not disturb the operating point.

#### `V_1Tone` and `V_nTone` — the tone voltage sources
One internal model, two netlist spellings. It is an ideal voltage source (Group 2, constraint `Va − Vb = E`) that contributes a **DC term at `freq = 0`** and a **phasor at each of its tone frequencies**, and **zero at every other stamped frequency** (an AC source is a short at frequencies it does not excite — like S-parameter sources being zeroed, §2.1). Parameters:

- **`Freq`** (capital F) — the tone frequency the source drives at (Hz). *User-set*, distinct from the injected `freq` keyword (expressions.md §3). The **commensurability check** (harmonic-balance §3) validates every `Freq` against the analysis tone grid.
- **`V`** — the tone's complex phasor amplitude (magnitude convention, §2.2). May be written as a real magnitude or as a complex phasor via `polar(mag, deg)`. For the RF drive the user computes `|Vs| = sqrt(8·Pavl·Re(Zs))` (§4.2) and supplies it here — e.g. `V = sqrt(8 * Pavl_w * real(Zs_f))`, with `Pavl_w` a watts expression and `Zs_f` the fundamental source impedance.
- **`Phase`** (optional, **degrees**) — a phase added to the excitation. If `V` is itself a complex phasor, `Phase` **adds** to its angle.
- **`Vdc`** (optional, volts) — a DC bias the same source carries; stamped **only at `freq = 0`** (the k=0 MNA). Lets one component be both bias and drive.

**`V_1Tone`** is the single-tone spelling — scalar `Freq`/`V`/`Phase`/`Vdc`:
```
V_1Tone:Vdrive  N__gate 0  Freq=2 GHz  V=Vs_mag  Phase=0
```
**`V_nTone`** is the multi-tone spelling — parallel indexed lists for an arbitrary number of tones, **1-based in the netlist** (designer convenience), 0-based in C# storage:
```
V_nTone:SRC1  Net_In 0  NumFreqs=2 \
   Freq[1]=1.900 GHz  V[1]=polar(0.1, 0) \
   Freq[2]=1.901 GHz  V[2]=polar(0.1, 0)
```
(Either form may be single- or multi-line via `\` continuation.) `V_1Tone` is exactly the `NumFreqs=1` case; **the reader expands both spellings into one internal model**, so the stamping and commensurability logic is written once. `V_nTone` also accepts an optional `Vdc`. Each tone `i` stamps its phasor `V[i]` (plus `Phase` if present) at the stamped frequency equal to `Freq[i]`, and zero elsewhere; `Vdc` stamps at `freq = 0`.

**The RF drive is a composition, not a bundled component.** The Hero-2 source side is `V_1Tone` (the excitation: `V`, `Freq`) **in series with** `Z_Port:Zsource` (the per-harmonic source impedance environment), feeding the gate through a bias-tee. Together they are the Thévenin equivalent of an RF power source with a frequency-dependent `Zs` — strictly more flexible than the bundled **RF power source** of §4.2 (which carries a single `Zs` per tone internally). The bundled RF power source is retained as a designer convenience for the common single-`Zs` case; the `V_1Tone` + `Z_Port` composition is what the heroes use and what the per-harmonic terminations require.

---

## 5. DC formulation — no value fudges

The prototype forced DC behavior with ad-hoc element-value fudges (an inductor as `1e-9 Ω`, large/small `MinValue`/`MaxValue` clamps). circuitRF does not. DC is the **ω → 0** case of the same MNA, made exact by the Group-1/Group-2 split:

- **Inductor → exact short.** As a Group-2 branch, its constraint `Va − Vb − jωL·i = 0` becomes `Va = Vb` at ω = 0. No large admittance, no fudge.
- **Capacitor → exact open.** Its admittance `jωC` is exactly `0` at ω = 0. It simply contributes nothing.
- **Floating nodes → `gmin`, not value hacks.** A node reachable only through open capacitors is unconstrained, making `A` singular. circuitRF adds a uniform, documented **`gmin`** conductance (default ~1e-12 S) from every node to ground — the standard SPICE technique — guaranteeing a non-singular system. `gmin` is a single principled knob (controllable, reportable), not a scatter of per-element magic numbers, and it is the same mechanism the nonlinear DC solve uses for continuity (harmonic-balance note).

This formulation serves both the standalone linear DC analysis and the DC (k = 0) seed the HB engine needs.

---

## 6. Sparse solve

The MNA matrix for a 10,000-component netlist is large and very sparse (each element touches 2–3 nodes), so a dense solve is out (PRD §14: 10k components, few-hundred frequencies, < 10 s). The engine uses **CSparse.NET** — managed complex sparse LU with a fill-reducing ordering — preserving the clean cross-platform story (no native dependency; native KLU/SuiteSparse stays a profiled, optional future optimization, data-model §6).

The key efficiency structure, because the sweep solves the *same sparsity pattern* at many frequencies:

1. **Symbolic analysis once.** The matrix *structure* (nonzero pattern) is fixed by topology and does not change across frequency. Compute the fill-reducing ordering (AMD) and symbolic factorization **once per topology**.
2. **Numeric factorization per frequency.** Only the *values* change with ω; refactorize numerically at each frequency reusing the symbolic structure.
3. **Reuse the factorization across right-hand sides.** Port extraction (§9) solves the same `A` against `N` excitation vectors (one per port) — factor once at that frequency, back-substitute `N` times.

Node ordering for the solve (the AMD permutation) is therefore a **numeric-layer** concern owned here, as the data model anticipated (§3); the elaborator only provides a stable, unique numbering, and this engine permutes it for fill reduction.

---

## 7. Mutual inductance — coupling two inductor branches

This resolves the data-model §12 open item. A `MutualInductanceModel` references its two inductors by name in the design (the prototype's `Inductor1="L1" Inductor2="L2"`). After flattening, those names are resolved to the two inductors' **elaborated instance paths**, and through them to their **branch-current unknowns** `i1`, `i2` (both inductors are Group-2, §4).

Mutual coupling `M` then modifies the two inductors' constraint rows into the coupled pair:

```
Va1 − Vb1 − jωL1·i1 − jωM·i2 = 0
Va2 − Vb2 − jωM·i1 − jωL2·i2 = 0
```

So the `MutualInductanceModel` stamps the off-diagonal terms `−jωM` into `(row i1, col i2)` and `(row i2, col i1)` via `AddBranchConstraint`. This is exactly why inductors must be Group-2 (a mutual term couples branch *currents*, which only exist as unknowns in that group) and why the elaborator must resolve the mutual's inductor references to concrete branches before stamping. The `MutualInductanceModel` is a 0-port coupling element (PRD §6): it adds no nodes, only the cross-coupling between two existing branches.

At DC the coupled rows reduce to `Va1 = Vb1`, `Va2 = Vb2` (two independent shorts) — correct, since mutual coupling carries no DC.

---

## 8. Producing the result

Each analysis writes a `DataSet` (data-model §7):

- **DC** → a small `DataSet` with node voltages (and branch currents) at ω = 0.
- **S-parameter** → a `DataSet` whose primary cube is `S` with axes `{freq, outPort, inPort}`, `DataKind = Complex`. RfCore writes it to Touchstone (`.sNp`); splotRF plots it; measurements (e.g. `dB(S(2,1))`) read it.

A parametric sweep wrapping the analysis (data-model §4) adds its sweep axis across the cubes.

---

## 9. S-parameter extraction & renormalization

Ports are declared by `Port`/`Term` components, each carrying a port number and a reference impedance `Z0_k` (optionally complex). Extraction, at each frequency:

1. **Identify the port nodes** from the `Port`/`Term` components.
2. **Extract the port network matrix.** With the MNA factorized at this frequency (§6), excite each port in turn and read the response, giving the port **Y-matrix** — **Y is the default extraction** (the prototype's `CalculateAdmittanceMatrix` approach), chosen because the HB engine reuses the *same* extraction for its Newton-update admittance (§10); one routine serves both paths. The **Z-matrix** (dual excitation) is coded as the conditioning fallback for topologies where Y is singular/ill-conditioned (a series-open network has finite Y but a shunt-short network has finite Z). The factorization is reused across the `N` port excitations.
3. **Convert and renormalize via RfCore.** Hand the port Y- (or Z-) matrix to RfCore, which converts to **S** and renormalizes to each port's reference impedance `Z0_k`, using the **power-wave formula for complex `Z0`** (data-model §6). circuitRF does not reimplement this; it is the splotRF/RfCore network math.

The port reference impedance `Z0_k` is the **normalization impedance for the S-definition**, not a physical resistor stamped into the network — so the extracted S-parameters are the network's own, defined against `Z0_k`. (Terminated/loaded responses, when wanted, are obtained by including explicit termination components in the circuit.)

For Hero 1 this is the whole path: a 4-port RLC network with an embedded SNP block → MNA per frequency → port Y-matrix → RfCore Y→S at the port `Z0`s → `S` cube → Touchstone, compared against the 4-port reference to `< 1e-6` (PRD §4).

---

## 10. Reuse by harmonic balance

The HB engine partitions the circuit into a linear subnetwork and nonlinear devices, and needs **two** things from this engine at each harmonic (§2.1): the linear subnetwork as a frequency-domain N-port at the nonlinear-facing nodes, **and** the excitation the independent sources (bias + drive) present at those nodes. This engine provides both: build the MNA of the linear partition, extract its interface **Y- or Z-matrix** at each harmonic frequency (the same extraction as §9, applied to the nonlinear-facing nodes rather than the user ports) and **wrap it as an RfCore `SNP`** (the construct-from-computed-Y/Z path, data-model §6); and solve the source-driven response to get the interface excitation vector. So the linear engine is not S-parameter-only; it is the linear characterization machine the whole simulator leans on. Details of how that N-port and excitation feed the conversion-matrix Jacobian are in the harmonic-balance note.

---

## 11. Performance

- **Sparse throughout** (CSparse.NET); never a dense `n×n` solve for the full netlist.
- **Symbolic-once / numeric-per-frequency** (§6) is the main lever: the expensive ordering/symbolic step is paid once per topology, not per frequency.
- **Factor-once / multi-RHS** for port extraction (§6, §9).
- Target (PRD §14): 10,000 components × few-hundred frequencies in < 10 s on a laptop; this structure is what makes that reachable in managed code.
- Native KLU/SuiteSparse is held in reserve as a profiled optimization if a hero-scale benchmark demands it — never a v1 dependency.

---

## 12. Resolved decisions & remaining checks

The major formulation choices are **decided** (this section was “open items” in rev 2):

1. **N-port block stamping** (§4.1) — **decided:** `Z(ω)` branch-current expansion by default; direct `Y(ω)` admittance stamp when the block is natively a finite `Y`.
2. **Port-matrix extraction** (§9) — **decided:** **Y by default** (HB reuses the same extraction for its Newton updates, §10), Z coded as the conditioning fallback. Both are implemented; Y is the path the heroes exercise.
3. **`gmin`** (§5) — **decided:** default **1e-12 S**, exposed as a user-visible **advanced setting** (tweakable). Verify it does not perturb Hero 1 beyond `1e-6`; if Hero 1 lands just over tolerance, `gmin` is the first suspect (drop it further).
4. **RF power source** (§4.2) — **decided:** AC source behind series `Zs` (default 50 Ω), user gives available power `Pavl`, voltage back-solved as `|Vs| = sqrt(8·Pavl·Re(Zs))` under the magnitude convention; phase default 0° (range ±360°); arbitrary frequencies, each with its own `Zs`/`Pavl`/phase. (Exercised by the Phase-4 HB drive, not Hero 1.)

**Remaining checks (validation, not open design):**

5. **Scale / branch-unknown count** — making every inductor (and every Z-form N-port) Group-2 grows the matrix. **Hero 1B** (a ~10k-component mechanically-generated linear network — the performance/scale anchor, distinct from the correctness heroes) validates the 10k-component / few-hundred-frequency / < 10 s NFR in **Phase 2**. If it misses budget, lean harder on the native-`Y` stamp for `ω > 0` sweeps (DC still needs the branch form). Hero 1B's acceptance is **performance + internal consistency** (e.g. reciprocity), not a `1e-6` reference match.
6. **Y-vs-Z extraction conditioning trigger** — the exact conditioning test that switches Y→Z is an implementation detail to tune against Hero 1 plus a deliberately series-open / shunt-short fixture.

---

## 13. Summary of decisions

- MNA with the standard Group-1 (admittance) / Group-2 (branch-current) split; ground = node 0; fixed, documented current/sign conventions.
- **Engine owns `MnaSystem`; models contribute stamps** through a controlled API (`AddAdmittance`, branch/constraint helpers) — extensibility is per-class.
- Frequency-domain N-ports (Touchstone/SNP, impedance block, TLIN, user) **default to `Z(ω)` branch-current expansion** (always well-defined, matches the reference), with a **direct `Y(ω)` admittance stamp when the block is natively a finite `Y`** (the lighter form) — sourced from RfCore / the expression engine.
- **Three uses, one assembly** (§2.1): DC (ω=0, sources on, no ports), S-parameter (swept, sources off, user ports), and the HB linear partition (per-harmonic, sources on, nonlinear-facing nodes as ports, returning an N-port *and* a source-excitation vector).
- **DC is the exact ω → 0 case** — inductor-as-short via Group-2, capacitor-as-open via zero admittance, **`gmin`** for floating nodes — replacing the prototype's value fudges.
- **CSparse.NET** sparse complex LU; **symbolic-once / numeric-per-frequency**; factor-once / multi-RHS for ports; AMD fill-reducing ordering owned by this layer.
- **Mutual inductance** couples two inductor branch-current unknowns with `−jωM` off-diagonals, after the elaborator resolves its inductor references to branches.
- **S-parameters** by port Y/Z extraction → **RfCore** Y/Z→S renormalization (power-wave, complex `Z0`); port `Z0_k` is the normalization impedance, not a stamped resistor.
- The same extraction **wraps the linear partition as an RfCore `Network`** for the HB engine.
