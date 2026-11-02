# Brief MC-18 — A model from DC I–V alone

**Series:** `brief-modelcomposer-0-overview.md` (D4, D12) · **Tag:** `R-mc18-<m>` · **Depends on:** MC-17
**Area:** `src/Design/Modeling/ChargeFit.cs`, `ChargeFitSettings.cs`, `ModelCommands.cs` (`AllSteps`),
`CmodelExport.cs`, `ConservationCheck.cs`; the Charge step in `src/Ui/ModelComposer/`;
`docs/user/src/reference/model-composer.md`, `file-formats.md`, `cli.md`; `docs/design/model-composer.md`
**Read first:** MC-17; `CmodelExport.Assemble` (which refuses until `Qg` and `Qd` are fitted); `ModelCommands.AllSteps`

---

## 1. Goal

DC I–V is the only data that is truly required. Today export needs fitted charges, and the charges need S data, so a
user with only DC I–V — or a datasheet's capacitances beside it — cannot build a `.cmodel`. With this brief, the I–V
gives the currents, and the charges are **typed** constant capacitances, which may be zero.

## 2. Requirements

**R-mc18-1 — Typed linear charges.** With no S, Y or capacitance data, the charge step writes `Linear` constituents
(MC-17) from typed values instead of fitting them. The values are stored in the Charge step's settings as the circuit
set `Cgs`, `Cgd`, `Cds` and `Cm` (default 0), and converted to `C11 … C22` by R-mc17-1's relations. The circuit set is
what a datasheet gives: `Cgd = Crss`, `Cgs = Ciss − Crss`, `Cds = Coss − Crss`. Say so on the step and in the page.
Typed values with S data present are refused, naming both: typing would discard the data.

**R-mc18-2 — Zero is allowed, and said.** All four at zero builds a model, with one line on the step, at export and
in `model explain`: `the charges are zero: S-parameters and harmonic balance see the currents alone.` A model that is
only meant for DC is a legitimate model.

**R-mc18-3 — Fit all with no S data.** `AllSteps` keeps its order. With no S data:
- the **network** step is skipped, holding its values, with one line (`network: held at its values — no S data to
  fit to`); an explicit `fit --step network` still refuses as today;
- the **charge** step writes the typed charges;
- dispersion and the τth fit are unavailable, as they already say.

**R-mc18-4 — Export, provenance, validation.** Export proceeds. The provenance and `model explain` say
`Qg, Qd: typed` with the four values. Validation runs the I–V and large-signal items and has no S items. The
conservation check reports (a) as not run (no data), (b) as zero, and runs (c).

**R-mc18-5 — The window.** On a document with no S data, the Charge step shows the four typed fields (with units)
in place of the method picker, with the datasheet relation as their tooltip; Run writes them.

**R-mc18-6 — The format.** `file-formats.md`'s `.cmc` Charge settings section documents the typed values. The
`.cmodel` format does not change: the result is two equation constituents.

**R-mc18-7 — Docs** (sources only; the owner runs DocGen): in `model-composer.md`'s "What a model needs", only the
I–V row is **Required**. The S row becomes *Optional*, and its text gives the three levels: none (typed or zero
capacitances), one bias or more (fitted linear charges, MC-17), across the I–V plane (nonlinear charges). Update the
checklist, and `cli.md`'s `model` section where it says what a fit needs. The design note records, under D12, that export
no longer needs fitted charges.

Owner rules: no change history in the pages; no vendor names; no "the design's own".

## 3. Not in this phase
Bias-dependent charges from datasheet curves (Ciss/Coss/Crss against Vds) — a natural follow-up, and the reason the
circuit set is the stored form.

## 4. Gates
- `DcOnlyModelTests`: a document with one DC I–V set and no S, fitted with `--step all` and exported, gives a
  `.cmodel` that `circuitrf check` passes. Its DC run is within 1 % of range of the data. Its intrinsic Y at 1 GHz gives
  `Im Y11 / ω = Cgs + Cgd` and `Im Y12 / ω = −Cgd` to 1e-6 relative for the typed values.
- The zero-charge line appears at export; typed values beside S data are refused.
- Fit all on that document skips the network with its line, and an explicit network fit still refuses.
- Run only these classes and `ModelComposerDocumentedPageTests`.
