# Brief MC-20 — Capacitance tables feed the charges

**Series:** `brief-modelcomposer-0-overview.md` (D4, D6, D11) · **Tag:** `R-mc20-<m>` · **Depends on:** MC-8, MC-16
**Area:** `src/Design/Modeling/DataImport.cs`, `CmcFile.cs` (`TableQuantities`), `IntrinsicExtraction.cs`,
`ChargeFit.cs`, `DataHygiene.cs`; `docs/user/src/reference/model-composer.md`, `file-formats.md`;
`docs/design/model-composer.md`
**Read first:** `DataImport.Arrays` (the `C` array is read, checked for shape and cached); `NormalisedData.C`;
`ChargeFit.Gather` (reads only the intrinsic `C11 … C22` that `IntrinsicExtraction` derives from S)

---

## 1. Goal

A simulator's operating-point capacitances are the most direct data the charge step could have, and many tools print
them per bias. The importer already accepts a `SimulatedTables` capacitance table `C[point, 2, 2]`, and the pages say
so. But **no step reads it**: it is cached and ignored, and a set holding only currents and capacitances is refused by
the charge step as having no S data. This brief makes a capacitance table a full source for the charges.

## 2. Requirements

**R-mc20-1 — Two spellings.** A `SimulatedTables` set's capacitances can be:
- the matrix `C[point, 2, 2]`, `Cij = ∂Qi/∂vj` (gate 1, drain 2; the convention of `Y = jωC`, so `C12 = −Cgd`) — as
  today; or
- the **circuit set**: new per-point array quantities `cgs`, `cgd`, `cds` and optionally `cm`. They are converted with
  `IntrinsicQuantities`' relations: `C11 = Cgs + Cgd`, `C12 = −Cgd`, `C21 = Cm − Cgd`, `C22 = Cds + Cgd`. An absent
  `cm` is 0, with one line saying the model has no transcapacitance.

Both spellings in one set is a refusal. `cgs`, `cgd` and `cds` come together or not at all.

**R-mc20-2 — The plane.** A capacitance table is the core's own, so it is accepted at `ReferencePlane=intrinsic`
only. At another plane it is refused in one sentence: capacitances cannot be de-embedded through an extrinsic network
without the frequency response, so the tool must export the core's, or Y. When the set also holds Y or S at the
intrinsic plane, Y feeds the charges and the table is cross-checked against `Im Y / ω`. The largest relative
difference is reported in the form of D6's voltage cross-check, e.g. `max |ΔC| 0.02 % between its capacitance table
and Im Y/ω`.

**R-mc20-3 — Into the charge step.** For a set with capacitances and no Y or S, `IntrinsicExtraction` gives the
charge step what it reads from S: `C11 … C22` per point, at the set's intrinsic voltages, with flatness 0 (weight 1).
`ChargeFit.Gather` needs no special case beyond the data's origin. Every quantity that needs an admittance (`Gm`,
`Gds`, `Tau`, the network fit, dispersion, τth) treats the set as having none, and says so where it names its data. The
MC-17 minimums apply as for S points.

**R-mc20-4 — The sign check.** Hygiene flags a matrix-spelled table whose `C12` is positive at most of its points:
`C12 > 0 at N of M points; the matrix convention has C12 = −Cgd — was the circuit set (cgd) meant?` This is a warning,
not a refusal.

**R-mc20-5 — Explain.** `model explain` gives a set's capacitance source (`table`, `circuit set` or `Y`), and the
Data step's set list shows it.

**R-mc20-6 — Docs** (sources only; the owner runs DocGen): in `model-composer.md`, "What a model needs" reads *S, Y or
a capacitance table*. The "File formats" array list gains `C` and the circuit set, with the convention in one line. The
"From another simulator" table's S-or-Y row names the operating-point capacitances as an alternative. `file-formats.md`'s
`Import` array list documents both spellings and the plane rule. The design note records the change.

Owner rules: no change history in the pages; no vendor names; no "the design's own".

## 3. Not in this phase
Capacitance tables at a non-intrinsic plane; capacitance tables for `PassiveSParams`.

## 4. Gates
- `CapacitanceTableTests`:
  - A `.mat` holding `vgs`, `vds`, `id`, `ig` and a matrix `C` for a known core, at the intrinsic plane, fits the
    charges. The exported model's `C11 … C22` at the table's points are within 1 % of the table.
  - The same core as the circuit set gives the same intrinsic `C11 … C22` as the matrix spelling, to 1e-12.
  - A table at `device` is refused with the R-mc20-2 sentence. A set with both Y and C at `intrinsic` reports the
    cross-check line, below 1e-6 for consistent data.
  - A matrix table written with positive `C12` raises the R-mc20-4 warning.
- Run only these classes and `ModelComposerDocumentedPageTests`.
