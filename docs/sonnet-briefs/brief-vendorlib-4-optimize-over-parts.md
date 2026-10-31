# Brief 4 — optimizing over real parts: the optimizer picks the vendor part

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md) · **Tag:** `R-vl4-n` · **Phase:** format, optimizer, UI
**Area:** `src/Core/Design/TuningSetup.cs` (the tune entry), `src/Core/Netlist/CnlReader.cs` / `CnlWriter.cs` (the
`tune` line), `src/Design/Schematic/NetExtractor.cs` (candidate cells), `src/Design/Optimization/OptimizationVariables.cs`,
`TunableCatalog.cs`, `TuningValidator.cs`, `src/Engine/Optimization/Discrete.cs`, the Tuning and Optimizer windows
(`src/Ui/Views/Tuning/`, `src/Ui/Views/Optimization/`), `src/Cli/Optimize.cs`, `src/Cli/Explain.cs` (`--tunables`)
**Depends on:** 1–3, and the tuning/optimization series (TO-1…TO-12) as built
**Rule:** overview §0 and §3. Overview §4 named this brief: "an optimizer variable over parts in this series".

---

## 0. What exists, and what is missing

Brief 3's **Value ▲ / ▼** steps a placed part through the parts of the same kind — same file, same Description
fields but the value — ordered by value. That is the manual form of this brief.

The optimizer already searches discrete spaces: `discrete=integer|preferred|E6…E96` on a tune line, the **Discrete**
algorithm (exhaustive grid up to a cap, coordinate descent with restarts above it), **Snap and polish** after a
continuous run, and **Auto**, which ends in snap-and-polish when any variable is discrete (TO-8).

What none of that can do is choose a PART. Every tunable today is a number written into an instance parameter or a
variable (`TunableOverrides`). A vendor part is not a number: changing it changes which subcircuit the instance
binds to — a different cell in the netlist, with its own parasitics, and often a different self-resonance. A real
10 pF part is not "C = 10 pF". The ESR, ESL and resonances the static model carries are the reason a designer
reaches for a real part at all.

## 1. `R-vl4-1` — the tunable: a placed vendor part

- **`R-vl4-1a`** A placed vendor part offers ONE tunable in the catalog (`TunableCatalog`): **`<Instance>.Part`**. In
  the Tuning window and `explain --tunables` it reads as the part number, its value and its Description. It never
  offers the hidden `File`/`Name`/`Glyph` parameters as text tunables.
- **`R-vl4-1b`** Its **candidates** are a list of parts, chosen one of two ways:
  - **`kind=same`** (the default) — brief 3's same-kind list (`VendorPartChange.SameKind`), so the size,
    dielectric, voltage and series stay fixed and only the value moves.
  - **`parts="<query>"`** — the shared search grammar (`VendorLibrarySearch`) over the part's own library: case
    code, value tokens with units, the bracket-free Description text. `parts="0603 C0G 50V"` lets the search
    cross series but not size.
  - Either may be narrowed by **`min=` / `max=`** on the stated value (`min=1 pF max=47 pF`), in the part's unit.
- **`R-vl4-1c`** The candidate list is resolved from the index (no file read) when the setup is checked. It is
  reported as a count, the value span and the series it spans. It is capped (default 200 candidates per entry; the
  cap is a `check` error naming the count, never a silent truncation), because each candidate is a cell the run
  must hold (§2).
- **`R-vl4-1d`** The `.cnl` spelling rides the existing `tune` line, so the format is one grammar:

  ```
  tune C1.Part kind=same min=1 pF max=47 pF opt=1
  tune C2.Part parts="0603 C0G 50V" opt=1
  ```

  `CnlWriter` must be able to say it (`src/Core/CLAUDE.md`'s standing check). An unknown `kind=` or an unparsable
  `parts=` is a reader error with a position, not a dropped key.

## 2. `R-vl4-2` — how one evaluation switches parts

The design decision this brief rests on: **a part change is a change of the instance's CELL, not of a value.**

- **`R-vl4-2a`** At extraction, when the tuning setup names `<Instance>.Part` with `opt=1`, the extractor emits a
  cell for **every candidate** — each read from its own byte block, exactly as brief 3 reads the placed part
  (`VendorPartSource`), through `SpiceModelNetlist.Build`. The emitted cells go under the parts' own names, so the
  claimed-cell rule is unchanged. A candidate that does not read is dropped from the list and REPORTED (one line per
  entry, naming the parts). It is never a failure of the whole run.
- **`R-vl4-2b`** The instance's own emitted reference is its placed part. An evaluation's override for
  `<Instance>.Part` is a **cell name**, and the override path (`TunableOverrides`) learns one new kind: rebinding an
  instance's cell reference before elaboration. The elaborator sees an ordinary netlist with a different cell on
  one instance. Nothing in the numeric layer knows a part was chosen.
- **`R-vl4-2c`** A candidate whose port count differs from the placed part's is not a candidate: it is removed when
  the list is resolved, and the count says so. Every candidate of `kind=same` has the same port count by
  construction; `parts=` can cross it.
- **`R-vl4-2d`** Counter, not clock: an optimization over k candidates reads exactly k blocks, once, whatever the
  number of evaluations. A gate holds `VendorLibraryIndex.FileReads` and `SpiceNetlistReader` calls to that.

## 3. `R-vl4-3` — the search

- **`R-vl4-3a`** In the optimizer's unit box a part variable is a **discrete coordinate whose levels are its
  candidates, ordered by stated value** (ties by part name, brief 3's order). This is the shape the **Discrete**
  algorithm already searches (`Discrete`'s `levels`). Exhaustive under its cap, coordinate descent above it. A
  problem of part variables alone runs Discrete with no new algorithm.
- **`R-vl4-3b`** **Mixed problems** (part variables plus continuous ones) follow TO-8's snap-and-polish, with the
  part variable relaxed to a continuous proxy for the continuous phase. The proxy is the part's stated value, mapped
  onto the candidates' value span: the continuous phase optimizes an IDEAL component of that value in the part's
  place (a C or L the glyph already names; a part with no glyph has no proxy and is searched discretely only).
  Snap then takes the nearest candidates by value, evaluates the 2^k neighbours (k ≤ 6, else nearest only) as REAL
  parts, keeps the best, and re-polishes the continuous variables. The report states the cost with the ideal
  proxy, and the cost after snapping to real parts. The difference is the parasitics the ideal model hid, and the
  user should see it.
- **`R-vl4-3c`** **Auto** picks Discrete for a part-only problem and snap-and-polish for a mixed one, and its progress
  names the stage, as TO-8's Auto does.
- **`R-vl4-3d`** Corners and yield are unchanged: a part variable is an `opt` variable, never a statistical one. A
  vendor part's tolerance is not in its static model; brief 5 (`brief-vendorlib-5-tolerance.md` R-vl5-3) reads it
  from the part number and draws the part's VALUE (`C1.C`) with it.

## 4. `R-vl4-4` — writing the answer back

- **`R-vl4-4a`** The result is the part number per entry, with its value. **Apply** (the Optimizer window, and the CLI's
  "values as text to write back") writes the chosen part through `SchematicViewModel.ChangeVendorPart`, the one
  part-change function brief 3 added. The footprint follows only when it came from the old part, as R-vl3-1d
  states, and the whole Apply is one undo entry.
- **`R-vl4-4b`** The frequency-range warning (R-vl3-3) is judged per evaluated part. The final report lists, once,
  any CHOSEN part whose range the analyses leave. A candidate that was only tried says nothing.

## 5. `R-vl4-5` — UI

- **`R-vl4-5a`** **Tuning window:** a part row shows the part number and value. Its slider steps through the
  candidates in value order (the live session re-elaborates per step, as a value change does). Its range shows the
  candidate count and the value span. The row's editor chooses `kind=same` or a query, and the min/max.
- **`R-vl4-5b`** **Optimizer window:** the variable list shows part rows with their candidate count. The algorithm
  menu greys Bayesian, CMA-ES and the other continuous-only entries for a part-only problem, with the reason, as it
  does for a parts-of-complex-value problem today (TO-8 R-to8-7).
- **`R-vl4-5c`** The **Parameter Editor's** vendor panel gains **Tune** beside Value ▲/▼: it adds the
  `<Instance>.Part kind=same` entry with `opt=1`, which is the one-click path from "this part" to "the best part like
  this one".

## 6. `R-vl4-6` — CLI and agents

- **`R-vl4-6a`** `circuitrf explain <design> --tunables` lists part tunables with their candidate count and span.
  `circuitrf check` reports an empty candidate list, a cap overrun and an unreadable candidate, each through
  `TuningValidator`. No rule may live in the CLI alone (`src/Design/CLAUDE.md`).
- **`R-vl4-6b`** `circuitrf run analysis=optimize` (and its MCP twin) returns the chosen part per entry in
  `result.optimize`, beside the values it returns today, plus the before/after-snap costs of R-vl4-3b.

## 7. Tests (minimal — counters and structure)

1. **Discrete over parts finds the grid optimum:** a two-part matching network over two same-kind lists from the
   generated fixture (overview §3), small enough to be exhaustive. The chosen pair equals the best pair found by
   evaluating every pair directly, and k blocks are read for k candidates (§2d).
2. **Mixed snap reports both costs:** one part variable plus one continuous variable. The report carries the
   ideal-proxy cost and the real-part cost, and the chosen part is one of the snapped neighbours.
3. **Apply is one undo entry** and moves the footprint only when it came from the part (reuses brief 3's case).
4. **The `.cnl` round trip** keeps `kind=`, `parts=`, `min=`, `max=` byte for byte.
5. **`check`** reports an empty list and a cap overrun as errors.

Run only the classes you touch.

## 8. Decisions for the owner (proposed defaults in bold)

- **D1 — Candidate default:** **`kind=same`** (value moves; size, dielectric, voltage, series fixed), or the whole file.
- **D2 — Cap:** **200 candidates per entry**, or another number. Each candidate is one block read and one cell.
- **D3 — Mixed problems:** **relax to an ideal proxy, then snap to real parts (R-vl4-3b)**, or search parts
  discretely throughout (slower, never wrong about parasitics, no proxy).
- **D4 — Footprint during the search:** **not evaluated** (no layout is in a circuit run). Only Apply moves it.

## 9. Not in this brief

- Optimizing across DIFFERENT libraries in one entry.
- A part's tolerance or temperature behaviour (static models carry neither; overview §4).
- Dynamic models (overview D1).
- Choosing a footprint by optimization.

## 10. On completion

Findings in `src/Design/RESOLVED.md` (and `src/Ui/RESOLVED.md` for the windows), never a `CLAUDE.md`. Update
`docs/user/src/reference/pdk-integration.md` (Vendor part libraries), `docs/design/yield.md` / the tuning reference
for the `tune … .Part` line, and `cli.md` for `--tunables` and the optimize result. Do not run DocGen.
