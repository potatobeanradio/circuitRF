# circuitRF — Measurements Design

**Status:** Draft (rev 2) for review · **Date:** 2026-05-29
**Reads with:** `docs/design/data-model.md` (§7 result model, §8 expression engine, §9 measurements overview), `docs/PRD.md` (§7 expressions, §4 heroes).
**Defers to:** `docs/design/expressions.md` (base grammar + AD/FD), `docs/design/harmonic-balance.md` (what HB stores), data-cube note (axis/units representation, export).

A **measurement** computes a performance figure — Gain, Pout, Pin, drain efficiency, PAE, IMn, or a user-defined FOM — *after* a simulation, by evaluating an expression over the run's result cubes and adding the answer to the run's `DataSet` as a named cube. This note specifies how measurements resolve their operands, what functions the language provides, how IMn is extracted from two-tone results, and how the results are typed and stored.

This is a v1 capability (PRD §7's "grows later: vectors" line is what measurements cash in). No code is written until this note is approved.

---

## 1. What a measurement is

```csharp
class Measurement { string Name; string Analysis; string Expression; string Units; }   // declared on the TestBench
```

A `Measurement` is declared on the `TestBench` (data-model §2.1), alongside the analyses, and **names the analysis it is measured on** (`Analysis`). There is exactly one TestBench at the top of a run, so a measurement is **evaluated once** — it is not a reusable template and does not fan out. Its result is added to the named analysis's result `DataSet` as a named `DataCube` (the `user_results` group): `ds["Gain"]`, `ds["PAE"]`.

A measurement differs from an ordinary expression (data-model §8) in two ways: its **operands can be cube quantities** (a spectrum, a swept trace), not just scalars; and it can **reference circuit quantities by hierarchical path** (`V(X1.drain)`), which an ordinary parameter expression cannot. Everything else — operators, precedence, `if(cond,…)`, user-defined functions — is the same engine.

---

## 2. Operand resolution

### 2.1 Quantity references
A measurement names circuit quantities through a small set of **accessor functions** whose arguments are *paths*, not numbers:

| Accessor | Meaning | Kind |
|---|---|---|
| `V(path)` | node voltage spectrum at a node | Complex |
| `I(path)` | branch current spectrum into a component terminal | Complex |
| `S(i, j)` | S-parameter (from an S-parameter result) | Complex |
| `P_delivered(path)` | power delivered into a port/node (derived from V·I*) | Real |
| `freq` / `Pin` / `<sweepvar>` | the value of a sweep axis at the current point | Real |

`V`/`I`/`P_delivered` return a value **per harmonic per sweep point** (a slice of the corresponding result cube); harmonic selection is a further function (§3). `S(i,j)` is meaningful only against an S-parameter result.

### 2.2 Paths are absolute, from the top, downward only
A path names a specific node, port, or component terminal **below the top level**, at any depth, separated by `.`:

```
V(drain)            ; a node at the top level
V(X1.drain)         ; a node inside instance X1
I(X1.inner.M1:d)    ; current into terminal d of M1, two levels down
```

Because the path is **absolute from the single top**, it names a *specific* instance — there is no per-instance fan-out (that machinery was removed when measurements returned to the TestBench). To measure both stages of the 2-stage PA hero, write two measurements:

```
Gain_stage1 = dB( Pout(X1.drain) / Pin )
Gain_stage2 = dB( Pout(X2.drain) / Pin )
```

There is **no upward (`..`) or sideways reach** — every path is anchored at the top and walks inward. This keeps resolution unambiguous and matches how bench instrumentation probes a specific point.

### 2.3 Resolution against the elaborated netlist
Paths resolve against the **elaborated netlist** (data-model §3), where the hierarchy has been flattened and node names already carry the instance path (`X1.drain` is exactly the elaborated node name). So measurement-path resolution reuses the elaboration node map directly — it is a lookup, not a second traversal. A path that does not resolve is a user error reported with the offending path (not a silent zero).

A terminal-current reference (`I(X1.M1:d)`) needs the **branch current** at a device terminal; this is why the engine must retain currents as well as voltages (§5).

---

## 3. The measurement function library

Measurements reuse the base expression engine (data-model §8) and add a library of **measurement functions**. Each function declares the `DataKind` it returns, which sets the kind of the cube the measurement writes (§4).

### 3.1 Harmonic / spectral selection
- `harm(x, k)` — the k-th harmonic component of spectrum `x` (e.g. `harm(V(drain), 1)` = fundamental). Complex.
- `dc(x)` — the DC (k = 0) component. For two-tone results, the harmonic index is a tone pair (§3.4).

### 3.2 Power, gain, efficiency (the PA FOMs)
Defined so they read like the textbook quantities; all real unless noted.

| Function | Definition (informal) | Kind |
|---|---|---|
| `Pout(path[, k])` | available/delivered RF power at `path`, harmonic `k` (default fundamental); from ½·Re(V·I*) | Real |
| `Pin` | available source power (the drive level / sweep variable) | Real |
| `Gain` | `Pout / Pin` (use `dB(...)` for dB) | Real |
| `Pdc` | DC power drawn from the bias supplies: Σ over DC sources of `V·I` at the **HB DC (k = 0) component** | Real |
| `DE` | drain efficiency = `Pout_fund / Pdc` | Real |
| `PAE` | power-added efficiency = `(Pout_fund − Pin) / Pdc` | Real |

Where `Pin` is the available source power (the drive level / sweep variable), `Pout` defaults to the fundamental, and **`Pdc` is taken from the harmonic-balance DC (k = 0) solution** — i.e. `harm(V(…),0)` and `harm(I(…),0)` of the same HB result, not from a separate DC analysis. This is both simpler (one simulation, one result) and *more accurate*: under drive, a PA self-biases, so the DC current at compression differs from the small-signal DC operating point. Using the HB k = 0 component captures that drive-dependent DC shift, which is exactly where PAE matters. `DE`/`PAE` are ratios → real; stored as a fraction or `%` per the measurement's declared `Units`.

### 3.3 Scalar helpers
- `dB(x)` = `20·log10(|x|)` for a wave/ratio quantity; `dBm(p)` = `10·log10(p/1mW)` for a power. Real.
- `mag(x)`, `phase(x)` (degrees), `re(x)`, `im(x)`. `mag`/`phase`/`re`/`im` are real; they are the bridge from a Complex operand to a Real measurement.
- Sweep **reductions**: `max(x)`, `min(x)`, `peak(x)`, `at(x, sweepvar, value)` — collapse or sample a swept trace (e.g. `max(PAE)` over a Pin sweep → a scalar). A reduction removes the named sweep axis from the result cube.

### 3.4 Intermodulation (two-tone)
For a two-tone HB result the spectrum is indexed by a **tone pair** `(k₁, k₂)` at frequency `k₁f₁ + k₂f₂` (data-model §4, harmonic-balance note). The library exposes:

- `tone(x, k1, k2)` — the component at `k₁f₁ + k₂f₂`. Complex.
- `IMn(path, n[, side])` — the n-th-order intermodulation product near the carriers, as a convenience over `tone`. `side` selects which of the symmetric pair (default both/worst-case). Real (returned in dBc relative to the carrier unless wrapped otherwise).

The standard close-in products the Hero-5 validation checks map as:

| Product | Tone pair `(k₁,k₂)` | Frequency (f₁=1.995, f₂=2.005 GHz) |
|---|---|---|
| carrier f₁ / f₂ | (1,0) / (0,1) | 1.995 / 2.005 GHz |
| IM2 (baseband) | (1,−1) | 0.010 GHz |
| IM3 | (2,−1) / (−1,2) | 1.985 / 2.015 GHz |
| IM4 | (3,−2)… (low) / (2,1) sum side | depends on side |
| IM5 | (3,−2) / (−2,3) | 1.975 / 2.025 GHz |

Capturing IM5 requires the two-tone truncation to reach **mixing order ≥ 5** (PRD §5; harmonic-balance note); `IMn` simply selects the already-computed tone-pair components. `IMn` reported in **dBc** is relative to the carrier: `IM3_dBc = dB(tone(V(out),2,-1)) − dB(tone(V(out),1,0))` — the library wraps this so the user writes `IMn(out, 3)`.

### 3.5 User-defined measurements
A user writes a measurement in the same language, combining the above:

```
; custom: ratio of 2nd to 3rd harmonic at the drain, in dB
H2_over_H3 = dB( harm(V(X1.drain), 2) / harm(V(X1.drain), 3) )
```

User-defined *functions* (data-model §8) compose with measurement functions, so a lab can encode a house FOM once and reuse it.

---

## 4. Result typing and storage

Each measurement function declares whether it returns **`Real`** or **`Complex`** (§3 tables). The measurement's result cube takes that `DataKind` (data-model §7):

- `PAE`, `DE`, `Pout`, `Gain` (as a ratio or dB), `IMn` (dBc) → **`Real`**.
- `Gamma_load`, `Zin`, `harm(V(...), k)` → **`Complex`**.

The result is added to the run's `DataSet` as a named cube under the measurement's `Name` (the `user_results` group). It carries the **same sweep axes** as its operands minus any axis removed by a reduction: a `PAE` measurement over a Pin sweep is a `Real` cube with a `Pin` axis (a trace); `max(PAE)` over that sweep is a scalar `Real` cube (no sweep axis). Because measurement cubes live in the DataSet alongside primary cubes, they plot in splotRF and export to `.mat`/`.npy` (the whole DataSet as one packed structured array, PRD §11) with no special handling.

---

## 5. What the engine must retain (requirement on HB)

Measurements need both **node voltages and branch currents**, per harmonic component (including k = 0), per sweep point — `Pout`/`Pdc`/`PAE` are all built from V·I products, and terminal-current paths (`I(X1.M1:d)`) reference currents directly. Therefore:

> The harmonic-balance engine must retain node-voltage *and* branch-current spectra for every node/terminal it solves — **including the DC (k = 0) component** — at every sweep point, in the run's primary cubes (`V`, `I`).

This is stated as a requirement in data-model §7 and repeated here because it is a measurement-driven constraint. Two consequences:

- **`Pdc` comes from the HB DC component, not a separate DC analysis.** Because harmonic balance already solves the k = 0 component self-consistently with the RF, `Pdc` (hence `PAE`/`DE`) is `Σ V·I` over the DC sources evaluated at `harm(…,0)` of the *same* HB result. There is no cross-analysis hand-off: the DC-source currents are simply the k = 0 slice of the HB current cube, so they must be retained like every other harmonic, not discarded after convergence. A measurement is thus a function of exactly one simulation.
- **Memory.** Retaining full internal V and I for a large netlist across a big sweep is the dominant memory cost (data-cube note). Because measurement paths can reach arbitrarily deep, a future "retain only referenced nodes" optimization must first scan all measurements to compute the referenced set. For v1 the engine retains the full internal solution (it already computes it); the prune pass is a later optimization, noted here so it is designed with deep references in mind.

---

## 6. Which analysis a measurement reads

A `TestBench` may run several analyses (S-parameter, single-tone HB, two-tone HB, loadpull), each producing its own `DataSet`. A measurement references quantities that exist only in some of them — `S(2,1)` only in an S-parameter result; `PAE` only where HB voltages and currents exist; `IMn` only in a two-tone result.

**A measurement names its target analysis** (the `Analysis` field, §1). Resolution is therefore unambiguous by construction: operands resolve against *that* analysis's result `DataSet`, and the measurement is added to *that* `DataSet`. A `TestBench` carrying both an S-parameter run `SP` and a two-tone run `HB2` can hold `InsertionLoss @ SP` and `IMD3 @ HB2` with no collision and no guessing.

Validation at elaboration time: if a named analysis does not exist on the TestBench, or an operand does not resolve within that analysis's result (e.g. `S(2,1)` named against an HB analysis, or `IMn` against single-tone), it is a user error reported with the offending name — not a silent zero.

---

## 7. `.cnl` surface (placeholder)

Measurements appear at the top level (the TestBench), never inside a `define … end` block, and **name their analysis with `@`**. The exact directive grammar is deferred (data-model §10), but the shape is:

```
measure Gain     @ HB1  = dB( Pout(out) / Pin )
measure PAE      @ HB1  = PAE
measure IMD3_dBc @ HB2  = IMn(out, 3)
measure MaxPAE   @ HB1  = max(PAE)
measure InsLoss  @ SP   = dB( S(2,1) )
```

Left of `=` is the measurement `Name` (its cube name in the analysis's `DataSet`) and, after `@`, the target analysis; right is the expression. Units may be attached as in parameter expressions.

---

## 8. Summary of decisions and open items

**Decided here (for review):**
- Measurements are TestBench-level, **named to a target analysis** (`@`), evaluated once, results are named cubes in that analysis's `DataSet`.
- Operands resolve by **absolute downward path** against the elaborated node map; no up/sideways reach.
- The function library: spectral selection (`harm`, `tone`, `dc`), PA FOMs (`Pout`, `Pin`, `Gain`, `Pdc`, `DE`, `PAE`), scalar helpers (`dB`, `dBm`, `mag`, `phase`, `re`, `im`, reductions), and `IMn` for two-tone.
- **`Pdc`/`DE`/`PAE` use the harmonic-balance DC (k = 0) component** — one simulation, and physically correct under drive (captures self-biasing).
- Each function declares `Real` or `Complex`; the result cube takes that `DataKind`.
- The HB engine must retain V and I including the k = 0 component, per harmonic per sweep point.

**Open items:**
1. The `.cnl` measurement directive grammar, incl. the `@ analysis` form (§7) — deferred with the rest of the directive grammar (data-model §10).
2. Exact `IMn` side/convention defaults (§3.4) — confirmed; revisit only if the Hero-5 reference reveals a mismatch.
