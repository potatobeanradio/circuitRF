# circuitRF — Measurements Design

**Status:** Draft (rev 3) for review · **Date:** 2026-06-06
**Reads with:** `docs/design/data-model.md` (§7 result model, §8 expression engine, §9 measurements overview), `docs/PRD.md` (§7 expressions, §4 heroes).
**Defers to:** `docs/design/expressions.md` (base grammar + AD/FD), `docs/design/harmonic-balance.md` (what HB stores), data-cube note (axis/units representation, export).

A **measurement** computes a performance figure — Gain, Pout, Pin, drain efficiency, PAE, IMn, or a user-defined FOM — *after* a simulation, by evaluating an expression over the run's result cubes and adding the answer to the run's `DataSet` as a named cube. This note specifies how measurements resolve their operands, the small set of primitives the language provides, how the standard figures of merit are built as composable macros (not built-ins), and how the results are typed and stored.

This is a v1 capability (PRD §7's "grows later: vectors" line is what measurements cash in). No code is written until this note is approved.

> **Design (rev 3): composable cube algebra, not a closed FOM library.** A measurement is an **expression over result-cube operands**, evaluated by the **Phase-1 expression engine extended to cube-valued operands** (operators and functions broadcast element-wise over a `DataCube`). The figures of merit (`Pout`, `PAE`, `DE`, `IMn`, …) are **not engine built-ins** — they are **pure user-space macros** (ordinary user-defined functions, data-model §8) shipped in a standard library file (`stdlib.cnfunc`) that the user can read and override. The engine provides only: (a) **qualified cube accessors** `Analysis.V/I/S(...)`, (b) **cube-valued arithmetic** (the Phase-1 operators over cubes), and (c) a few **element-wise complex/dB helpers** (`conj`, `log10`, `dB`, `mag`, `phase`, `re`, `im`). Everything else composes from those. This keeps the engine surface tiny, puts every FOM definition (including the current-direction sign and the dBc convention) in plain, inspectable code rather than a hidden built-in, and means any FOM a user can write as math, they can measure.

Example (a single-tone HB power sweep named `HB1`; `X1` a cell; `drain`/`gate` nodes in `X1`):
```
measure Pout_W           = 0.5*HB1.V(X1.drain,1,All)*conj(-1*HB1.I(X1.M1:d,1,All))
measure Pout_dBm         = 10*log10(Pout_W*1000)
measure Pin_delivered_W  = 0.5*HB1.V(X1.gate,1,All)*conj(HB1.I(X1.M1:g,1,All))
measure Pin_delivered_dBm= 10*log10(Pin_delivered_W*1000)
measure Gain_dB          = Pout_dBm - Pin_delivered_dBm
measure IRL_dB           = dB(SP.S(1,1))            ; all of S11 over SP's frequencies
```
Each result is a **cube** carrying the operands' axes (here a trace over the `Pin` sweep, via `All`). `Pout_dBm` and `Gain_dB` are themselves measurements referenced by later measurements — measurements compose. The `-1*` on the drain current is **ordinary scalar×cube multiplication** (the documented current-into-DUT convention written in the open), not special handling.

---

## 1. What a measurement is

```csharp
class Measurement { string Name; string Expression; string Units; }   // declared on the TestBench
```

A `Measurement` is declared on the `TestBench` (data-model §2.1), alongside the analyses. Its result is added to the run's result `DataSet` as a named `DataCube` (the `user_results` group): `ds["Gain_dB"]`, `ds["PAE"]`.

A measurement does **not** carry a single target-analysis field. Instead, **each operand names its analysis inline** as a dotted qualifier — `HB1.V(...)`, `SP.S(1,1)` (§2, §6). This is strictly more expressive than a per-measurement `@ analysis`: a single measurement may reference operands from **more than one analysis** (e.g. compare an HB result against an S-parameter result in one expression). There is exactly one `TestBench` at the top of a run, so a measurement is **evaluated once** — it is not a reusable template and does not fan out.

A measurement differs from an ordinary parameter expression (data-model §8) in two ways: its **operands are cube quantities** (a spectrum, a swept trace) resolved from a named analysis's result, so the whole expression is **cube-valued** (evaluated element-wise); and it can **reference circuit quantities by hierarchical path** (`HB1.V(X1.drain,...)`), which an ordinary parameter expression cannot. Everything else — operators, precedence, `if(cond,…)`, user-defined functions — is the **same Phase-1 engine**, extended to operate on cubes.

---

## 2. Operand resolution

### 2.1 Qualified cube accessors
A measurement names result quantities through **analysis-qualified accessor functions** — `Analysis.accessor(args)`, where `Analysis` is the instance name of an analysis on the TestBench (`HB1`, `SP`, `LP1`) and the accessor is the **same cube accessor the `DataSet` exposes** (data-cube note): the qualifier just selects *which* analysis's `DataSet` to read.

| Accessor | Meaning | Kind |
|---|---|---|
| `A.V(nodePath[, harm, sliceArgs…])` | node-voltage spectrum/trace at a node | Complex |
| `A.I(branchRef[, harm, sliceArgs…])` | branch current into a **named branch** (§2.3) | Complex |
| `A.S(i, j)` | S-parameter trace over frequency (port numbers `i`,`j`) | Complex |
| `freq` / `Pin` / `<sweepvar>` | the value(s) of a sweep axis (a real cube/scalar) | Real |

`A.V`/`A.I` return the cube sliced exactly as the DataSet's `V`/`I` accessors do: node/branch name first, then **positional axis-index** slice of the remaining axes (harmonic index, sweep index), with `All`/`..` keeping an axis (data-cube note). **Every bracket slot after the name is an axis INDEX, never a physical value** — `1` is harmonic *index* 1 (fundamental), `All` is the whole sweep axis. `A.S(i,j)` reads the `S` cube at port numbers `i,j`, traced over `freq`.

### 2.2 Voltage paths — absolute, from the top, downward only
A node path names a specific node **below the top level**, at any depth, separated by `.`:
```
HB1.V(drain)            ; a node at the top level
HB1.V(X1.drain)         ; a node inside instance X1
HB1.V(X1.inner.n3)      ; two levels down
```
The path is **absolute from the single top**, naming a *specific* instance — no per-instance fan-out, no upward (`..`) or sideways reach. To measure both stages of the 2-stage PA hero, write two measurements against `X1.…` and `X2.…`. This matches how bench instrumentation probes a specific point, and keeps resolution unambiguous.

### 2.3 Current is addressed by NAMED BRANCH, never by net
A node has one voltage but is a junction of many terminals, each with its own current (KCL: they sum to zero), so **“the current at a net” is meaningless** and is never allowed. Current is always a property of a specific **branch**, named one of two ways:

- **Component terminal — `instance:terminal`.** The current into a named terminal of a component, regardless of how many other terminals share that net: `HB1.I(X1.M1:d)` (into M1's drain), `HB1.I(X1.M1:g)` (gate), `HB1.I(Lmatch:1)` (terminal 1 of a series inductor). Each `ComponentModel` declares its terminal names (SDD: `d`/`g`/`s`; two-terminal parts: `1`/`2` or `p`/`n`). The elaborated netlist knows every terminal's branch current, so `M1:d` and `M2:g` on the same net are distinct, unambiguous currents.
- **Explicit current probe — `IProbe`.** A named zero-volt series element (an ideal ammeter) the user inserts on any branch they want to sense: `IProbe:IP1 …` in the netlist, read as `HB1.I(IP1)`. This is how the user senses a *specific wire/branch* (not necessarily any single component terminal) and how **many probes on the same net stay distinct** — each probe is its own named branch. Matches the bench mental model (drop an ammeter where you want to measure) and the SPICE 0 V-source-as-ammeter idiom; it adds one branch current to the MNA solution.

**Both forms build now** (the machinery for terminal-current references and for `IProbe`). *Note:* the component-terminal form may later become optional (a memory optimization could retain only probe/named branches); the `IProbe` form is the always-available path. For v1 both work.

### 2.4 Resolution against the elaborated netlist
Paths resolve against the **elaborated netlist** (data-model §3), where the hierarchy is flattened and node names already carry the instance path (`X1.drain` is exactly the elaborated node name) — so resolution reuses the elaboration node/branch map directly (a lookup, not a second traversal), the same name registry the `DataSet` `V`/`I` accessors use. A path that does not resolve (unknown node, unknown `instance:terminal`, unknown probe) is a **user error reported with the offending path**, never a silent zero.

---

## 3. Primitives the engine provides, and the macro standard library

The measurement language is the **Phase-1 expression engine over cube operands** plus a **small set of element-wise primitives**. The figures of merit are **macros** (user-defined functions) in a shipped standard-library file, not engine built-ins.

### 3.1 Engine primitives (the only built-in surface)
- **The qualified cube accessors** `A.V`, `A.I`, `A.S` (§2).
- **Cube-valued arithmetic** — the Phase-1 operators (`+ - * / ^`, comparisons, `if(…)`) broadcast **element-wise** over cubes: cube×cube (same shape), cube×scalar, scalar functions mapped over a cube. Result axes are the operands' axes (broadcasting a scalar against a cube keeps the cube's axes).
- **Element-wise complex/dB helpers:** `conj(z)`, `re(z)`, `im(z)`, `mag(z)`, `phase(z)` (degrees), `log10(x)`, `ln(x)`, `dB(x)` = `20·log10(|x|)` (amplitude dB) and `dB10(x)` = `10·log10(|x|)` (power dB) and `dBm(p)` = `10·log10(|p|/1e-3)`. These map over a cube element-wise, returning a cube of the appropriate `DataKind` (e.g. `mag`/`dB` Complex→Real; `conj` Complex→Complex). (These mirror the `DataCube` transforms in the data-cube note, exposed as expression functions.)
- **Sweep reductions** — `max(x[, axis])`, `min(x[, axis])`, `peak(x[, axis])`, `at(x, axis, index)`: collapse or sample a named sweep axis, dropping it from the result cube (e.g. `max(PAE)` over a `Pin` sweep → a scalar). These are the `DataCube` reductions surfaced as functions.

That is the **entire** engine-provided measurement surface. Notably there is **no built-in `Pout`/`PAE`/`IMn`** — those are macros (§3.2).

### 3.2 The standard-library macros (`stdlib.cnfunc`)
The conventional figures of merit ship as **pure user-defined functions** (data-model §8 `func`s) in a standard-library file with the extension **`.cnfunc`** (circuitRF functions — distinct from `.cnl` netlists to avoid confusion). The user can read every definition, and **override any of them** by defining a same-named `func` in their own `.cnfunc`/netlist. Because they are ordinary user functions, **their parameters bind to cube slices** and their bodies are cube-valued expressions — the same machinery as any user function, now over cubes.

Illustrative definitions (the exact shipped file is finalized at implementation; current-direction sign written in the open):
```
; --- power / gain (single-tone) ---
func Pout_W(v, i)        = 0.5 * v * conj(-1*i)         ; v,i are fundamental slices; i is current INTO the load
func Pout_dBm(v, i)      = dBm( Pout_W(v, i) )
func Pin_del_W(v, i)     = 0.5 * v * conj(i)            ; i INTO the DUT input
func Gain_dB(vo,io,vi,ii)= dBm(Pout_W(vo,io)) - dBm(Pin_del_W(vi,ii))

; --- efficiency: Pdc = sum of V*I over the named DC supply branches ---
func Pdc_supply(vdc, idc)= vdc * idc                   ; user passes the k=0 supply node V and branch I
func DE(vo, io, pdc)     = Pout_W(vo, io) / pdc
func PAE(vo, io, vi, ii, pdc) = (Pout_W(vo,io) - Pin_del_W(vi,ii)) / pdc

; --- two-tone intermod, in dBc relative to a carrier ---
func IM3_dBc(lo, hi, carrier) = dB(lo) - dB(carrier)   ; lo = tone(out,2,-1) slice, carrier = tone(out,1,0)
```
The user then writes terse measurements that call the macros with explicit cube operands:
```
measure Pout_dBm = Pout_dBm( HB1.V(X1.drain,1,All), HB1.I(X1.M1:d,1,All) )
measure PAE      = PAE( HB1.V(X1.drain,1,All), HB1.I(X1.M1:d,1,All),
                        HB1.V(X1.gate,1,All),  HB1.I(X1.M1:g,1,All),
                        Pdc_supply(HB1.V(vdd,0,All), HB1.I(IP_vdd,0,All)) )
```

**Why macros, not built-ins** — three consequences worth stating:
- **No hidden knowledge.** A built-in `Pdc()` would have to *guess* which nodes are the DC supplies; a macro instead has the user pass the supply node-V and probe-current explicitly (`HB1.V(vdd,0,All)`, `HB1.I(IP_vdd,0,All)`). The circuit-specific knowledge (which branch is the supply, the k=0 index for DC) lives in the user's measurement, not in the engine. `Pdc`/`DE`/`PAE` read the **HB DC (k=0) component** because the *user writes index 0*, capturing the drive-dependent self-bias shift (§5) — and they can see that they did.
- **No hidden conventions.** The current-into-DUT sign (`-1*i`) and the dBc reference subtraction are written in the macro body, inspectable and overridable — not buried in a `Pout()` whose definition the user can't see. (This is exactly the class of hidden convention that historically caused sign bugs.)
- **Unbounded FOMs.** Anything expressible as cube math is a measurement; a lab encodes a house FOM once in its own `.cnfunc` and reuses it. No engine change to add a figure of merit.

### 3.3 Two-tone selection
For a two-tone result the spectrum axis is `mixIndex`, addressed by **tone pair** via the accessor's harmonic-slot (the `mixIndex` for `(k₁,k₂)`, per harmonic-balance.md §6.3). The standard-library `IMn` macros select the carrier and intermod `mixIndex` slices and form the dBc ratio (§3.2). The Hero-5 products map as:

| Product | Tone pair `(k₁,k₂)` | Frequency (f₁=1.995, f₂=2.005 GHz) |
|---|---|---|
| carrier f₁ / f₂ | (1,0) / (0,1) | 1.995 / 2.005 GHz |
| IM2 (baseband) | (1,−1) | 0.010 GHz |
| IM3 | (2,−1) / (−1,2) | 1.985 / 2.015 GHz |
| IM5 | (3,−2) / (−2,3) | 1.975 / 2.025 GHz |

Capturing IM5 requires the two-tone truncation to reach **mixing order ≥ 5** (PRD §5; harmonic-balance.md); the macros simply select already-computed `mixIndex` slices.

---

## 4. Result typing and storage

The measurement's result cube takes the **`DataKind` its expression produces** (data-model §7) — the element-wise helpers determine it: `mag`/`re`/`im`/`dB`/`dB10`/`dBm`/`phase` yield **`Real`**; `conj` and raw `V`/`I`/`S` accessors yield **`Complex`**; arithmetic follows the usual promotion (Complex if any operand is Complex). So `Pout_dBm`, `Gain_dB`, `PAE`, `IM3_dBc` → **`Real`**; `HB1.V(…)`, a raw `SP.S(2,1)` → **`Complex`**.

The result is added to the run's `DataSet` as a named cube under the measurement's `Name` (the `user_results` group). It carries the **same axes as its operands** minus any axis removed by a reduction: `Gain_dB` over a Pin sweep is a `Real` cube with a `Pin` axis (a trace); `max(Gain_dB)` is a scalar `Real` cube (no sweep axis). Because measurement cubes live in the DataSet alongside primary cubes, they plot in splotRF and export to `.mat`/`.npy` (the whole DataSet as one packed structured array, PRD §11) with no special handling.

---

## 5. What the engine must retain (requirement on HB)

Measurements need both **node voltages and branch currents**, per harmonic component (including k = 0), per sweep point — `Pout`/`Pdc`/`PAE` are all built from V·I products, and terminal-current paths (`I(X1.M1:d)`) reference currents directly. Therefore:

> The harmonic-balance engine must retain node-voltage *and* branch-current spectra for every node/terminal it solves — **including the DC (k = 0) component** — at every sweep point, in the run's primary cubes (`V`, `I`).

This is stated as a requirement in data-model §7 and repeated here because it is a measurement-driven constraint. Two consequences:

- **`Pdc` comes from the HB DC component, not a separate DC analysis.** Because harmonic balance already solves the k = 0 component self-consistently with the RF, `Pdc` (hence `PAE`/`DE`) is `Σ V·I` over the DC sources evaluated at `harm(…,0)` of the *same* HB result. There is no cross-analysis hand-off: the DC-source currents are simply the k = 0 slice of the HB current cube, so they must be retained like every other harmonic, not discarded after convergence. A measurement is thus a function of exactly one simulation.
- **Memory.** Retaining full internal V and I for a large netlist across a big sweep is the dominant memory cost (data-cube note). Because measurement paths can reach arbitrarily deep, a future "retain only referenced nodes" optimization must first scan all measurements to compute the referenced set. For v1 the engine retains the full internal solution (it already computes it); the prune pass is a later optimization, noted here so it is designed with deep references in mind.

---

## 6. Which analysis a measurement reads

A `TestBench` may run several analyses (S-parameter, single-tone HB, two-tone HB, loadpull), each producing its own `DataSet`. A measurement references quantities that exist only in some of them — `SP.S(2,1)` only in an S-parameter result; `Pout` operands only where HB voltages and currents exist; two-tone `mixIndex` slices only in a two-tone result.

**Each operand names its analysis inline** (the dotted qualifier `HB1.…`, `SP.…`, §2.1). Resolution is therefore unambiguous per operand, and — unlike a single per-measurement `@ analysis` field — a measurement may **mix analyses in one expression** (e.g. `dB(SP.S(2,1)) - something(HB1.V(…))`), which is occasionally useful and costs nothing. Each operand resolves against *its* named analysis's result `DataSet`; the measurement's result cube is added to the run's DataSet under the measurement `Name`.

Validation at elaboration time: if a qualified analysis does not exist on the TestBench, or an operand does not resolve within that analysis's result (e.g. `SP.S(2,1)` named against an HB analysis, or a two-tone `mixIndex` slice against a single-tone run), it is a user error reported with the offending name — not a silent zero.

---

## 7. `.cnl` surface (placeholder)

Measurements appear at the top level (the TestBench), never inside a `define … end` block. The form is `measure Name = expression`, where the expression's operands carry their **analysis qualifier inline** (`HB1.…`, `SP.…`). Standard-library macros are loaded from a `.cnfunc` file (the shipped `stdlib.cnfunc`, plus any user `.cnfunc`):
```
measure Pout_W           = 0.5*HB1.V(X1.drain,1,All)*conj(-1*HB1.I(X1.M1:d,1,All))
measure Pout_dBm         = 10*log10(Pout_W*1000)
measure Pin_delivered_W  = 0.5*HB1.V(X1.gate,1,All)*conj(HB1.I(X1.M1:g,1,All))
measure Pin_delivered_dBm= 10*log10(Pin_delivered_W*1000)
measure Gain_dB          = Pout_dBm - Pin_delivered_dBm
measure MaxGain_dB       = max(Gain_dB)                 ; reduce over the Pin sweep
measure IRL_dB           = dB(SP.S(1,1))                ; S11 over SP's frequencies
measure IMD3_dBc         = IM3_dBc( tone slices via HB2.V(out, (2,-1), All), … )
```
Left of `=` is the measurement `Name` (its cube name in the run's `DataSet`); right is a cube-valued expression whose operands are analysis-qualified accessors and/or earlier measurements. Units may be attached as in parameter expressions. (Exact directive tokenization — e.g. how a two-tone `mixIndex` pair is written in the accessor — is finalized with the rest of the directive grammar, data-model §10; the `measure Name = expr` shape with inline `Analysis.` qualifiers is settled.)

---

## 8. Summary of decisions and open items

**Decided here (for review):**
- Measurements are TestBench-level, `measure Name = expression`, evaluated once, results are named cubes in the run's `DataSet` (the `user_results` group).
- **Composable cube algebra, not a closed FOM library.** The engine provides only: qualified cube accessors (`Analysis.V/I/S`), cube-valued arithmetic (the Phase-1 engine extended to operate element-wise over cubes), and element-wise helpers (`conj`/`re`/`im`/`mag`/`phase`/`log10`/`ln`/`dB`/`dB10`/`dBm`) + sweep reductions (`max`/`min`/`peak`/`at`). **No built-in `Pout`/`PAE`/`IMn`.**
- **Figures of merit are pure user-space macros** (`func`s) shipped in a standard-library **`.cnfunc`** file, readable and user-overridable. Macro parameters bind to cube slices (reusing the Phase-1 user-function machinery over cubes). Circuit-specific knowledge (which branch is a DC supply, the k=0 DC index, the current-into-DUT sign, the dBc reference) lives in the macro/measurement as visible code — never hidden in a built-in.
- **Each operand names its analysis inline** (`HB1.…`, `SP.…`); a measurement may reference **multiple analyses** in one expression. (Replaces the earlier single `@ analysis` field.)
- Operands resolve by **absolute downward path** against the elaborated map; no up/sideways reach. **Current is addressed by NAMED BRANCH, never by net** — either a component terminal (`instance:terminal`) or an explicit named **`IProbe`** ammeter. Both build now; `IProbe` is the always-available form (terminal references may later become an optional, memory-prunable convenience).
- **`Pdc`/`DE`/`PAE` use the HB DC (k=0) component** — because the user writes index 0 explicitly (one simulation; captures self-biasing under drive).
- The result cube's `DataKind` follows from the expression (helpers set Real/Complex).
- The HB engine must retain V and I including the k=0 component, per harmonic/mixIndex per sweep point.

**Open items:**
1. Exact directive tokenization for a two-tone `mixIndex` pair inside the accessor (§7) — deferred with the rest of the directive grammar (data-model §10). The `measure Name = expr` shape with inline `Analysis.` qualifiers is settled.
2. The precise contents of the shipped `stdlib.cnfunc` (§3.2) — the illustrative definitions are finalized at implementation; the *mechanism* (pure macros over the primitives) is settled.
3. `IProbe` component `.cnl` grammar (§2.3) — settle at bring-up with the other component grammars; the zero-volt-series-ammeter semantics are settled.
