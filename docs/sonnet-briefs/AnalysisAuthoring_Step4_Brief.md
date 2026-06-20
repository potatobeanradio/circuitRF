# Analysis Authoring — Step 4: the Add/Edit per-type form (HIG, progressive disclosure) (Claude Code / Sonnet)

The HIG-critical piece: the **Add/Edit dialog** behind step 3's placeholder — a **type picker** (DC ·
S-Parameter · Harmonic Balance) that **reveals only the chosen type's fields**, with **expression fields +
live "≈" preview** (reusing the parameter-editor pattern), the S-param **multi-segment sweep sub-list**
(Start/Stop, Step|Points toggle, Linear|Log), and HB **basic + collapsed-advanced** progressive disclosure.
**This brief is step 4.** Copy/paste + templates are **step 5**. Read `analysis-authoring.md` §4.2/§4.3/§4.4
first. **Pay special attention to HIG — do not intimidate.** Sub-gated; **report and stop between every
layer.** Firewall green.

> Read first: `docs/design/analysis-authoring.md` §4.2 (the form, type picker, per-type bodies), §4.3
> (expression fields + preview — reuse), §4.4 (don't-intimidate: defaults, collapsed advanced, empty/guide).
> Context code: `src/Ui/ViewModels/ParameterRowViewModel.cs` (**the preview pattern to reuse**:
> `DesignScope.Build(editModel, selfName)` + `new Evaluator().Eval(expr, scope)`, swallow-all-errors → empty
> "≈" preview, bare-number/blank gates — copy this approach for analysis fields), `src/Ui/ViewModels/
> AnalysesListViewModel.cs` + `AnalysisRowViewModel.cs` (step 3 — Add/Edit currently open a placeholder; wire
> them to this dialog), `src/Core/Design/Analysis.cs` (DC/SParameter(Sweeps list)/HB fields + `FrequencySpec`
> mode/expr from step 1), `src/Ui/Views/Dialogs/SavePlanDialog.axaml` + `NewWorkspaceDialog.axaml` (HIG dialog
> conventions: centered default button, ShowDialog<T?>, inline validation), `src/Ui/Schematic/
> ComponentTypeRegistry.cs` (`UnitOptions` for any unit ComboBoxes). Design docs win on any conflict.

## The spine (do not violate)
- **Progressive disclosure is the anti-intimidation mechanism** (§4.2/§4.4): the type picker swaps the body to
  **only** the chosen type's fields; HB's many fields hide behind a **collapsed Advanced** group; a novice
  sees a handful of fields, never the full 15.
- **Sensible defaults so OK works immediately** (§4.4): new SP pre-fills one 1–10 GHz / 101-pt segment; new HB
  pre-fills f0 + 7 harmonics; new DC is near-empty. The novice path is **Add → OK → Run**.
- **Expression fields + preview, reused** (§4.3): every numeric field is an expression box with the grey "≈
  resolved" preview, computed by the **same `DesignScope`+`Evaluator` approach** as `ParameterRowViewModel`
  (swallow errors → empty preview; quiet inline hint for unresolved). **Share the evaluator approach, don't
  fork it.**
- **Edits the model on OK** (via the step-3 list, which mutates the `SchematicEditModel` analyses + dirty);
  Cancel discards. The dialog stages, OK commits.
- **Loadpull/pursuit:** shown in the type picker **disabled** with a "coming soon" tooltip (deferred — note
  carried).
- **Scope fence (step 4):** the Add/Edit form (DC/SP/HB) + preview. NO copy/paste, NO templates, NO run
  changes, NO measurements builder (the measurements list is §4.4/step 6).

---

## LAYER 1 — the form shell + type picker + DC + expression-preview helper

1. **`AnalysisEditorViewModel`** staging one analysis: a **Type** (DC/SP/HB) selector, **Name** (defaulted
   `DC1`/`SP1`/`HB1`, validated), **Enabled**, and a per-type body VM swapped by the type selection. Returns
   the staged `Analysis` on OK, null on Cancel.
2. **A shared analysis-field preview helper** (extract the `ParameterRowViewModel` preview essence into
   something reusable, or mirror it): given an expression string + the active `SchematicEditModel`, compute
   the grey "≈" preview via `DesignScope.Build` + `Evaluator`, swallow errors → empty, bare-number/blank
   gates. Used by every expression field in SP/HB.
3. **DC body:** near-empty (name + enabled + maybe a "save operating point" check) — the reassuring novice
   case.
4. **`AnalysisEditorDialog`** (mirror `SavePlanDialog`/`NewWorkspaceDialog`): type picker at top
   (segmented/radio; loadpull/pursuit **disabled + tooltip**), name/enabled, the swappable body, **OK**
   (centered default) / **Cancel** (Esc), inline validation, OK gated on valid.

**Layer 1 gate:** the dialog opens from the step-3 Add/Edit; picking DC shows the minimal DC body; Name
validates; OK returns a DC analysis into the list; the preview helper resolves an expression against the
schematic's VARs (and shows nothing for a bare number / unresolved). Report.

---

## LAYER 2 — the S-Parameter body: multi-segment sweep sub-list

The SP body (§4.2): a **sub-list of frequency-sweep segments** (the step-1 `Sweeps` list), each row:
- **Start · Stop** (expression fields, each with "≈" preview);
- a **Step | Points** segmented toggle → shows the **Step value** field *or* the **# Points** field
  accordingly (the step-1 `FreqSpecMode`);
- a **Linear | Log** toggle (`SweepKind`);
- **Add / Remove** segment (≥1 enforced).
Defaults: one segment pre-filled (e.g. Start 1 GHz, Stop 10 GHz, 101 points). The novice adds one segment and
is done; multi-band is there but not forced. OK builds the `SParameterAnalysis` with its `Sweeps` list.

**Layer 2 gate:** a new SP analysis opens with one sensible default segment; toggling Step|Points swaps the
field; Linear|Log toggles; add/remove segments (≥1); expression Start/Stop show previews; OK produces a
multi-segment `SParameterAnalysis` that round-trips (step-2 persistence) and that the list summary reflects
("SP · …, N segments"). Report.

---

## LAYER 3 — the Harmonic Balance body: basic + collapsed advanced

The HB body (§4.2/§4.4) — **progressive disclosure is essential** (~15 fields):
- **Basic group (always visible):** fundamental **Tone** (expression), **Max harmonics** (expression); a
  **single-tone / multi-tone** toggle that reveals extra tone fields **only** when multi-tone.
- **Advanced group (collapsed by default):** the rest — max mix order, tolerance, λ damping, oversample,
  guard harmonic, max iterations, source/drive stepping, sweep — each an expression field with preview.
- Sensible defaults (f0 + 7 harmonics) so OK works without opening Advanced.

**Layer 3 gate:** a new HB analysis shows only Basic (Tone + Max harmonics + tone-count toggle) with Advanced
collapsed; expanding Advanced reveals the rest; multi-tone reveals extra tone fields; expression fields preview;
OK produces an `HarmonicBalanceAnalysis` that round-trips and whose summary reflects ("HB · f0=…, N harmonics").
A novice can Add HB → OK without touching Advanced. Report.

## Acceptance (step 4)
1. An Add/Edit dialog with a **type picker** that reveals only the chosen type's fields (DC/SP/HB;
   loadpull/pursuit disabled+tooltip); HIG buttons (centered default OK / Cancel); inline validation.
2. **Expression fields with the reused "≈" preview** (DesignScope+Evaluator, swallow-errors) throughout SP/HB.
3. **SP** = a multi-segment sweep sub-list (Start/Stop expr · Step|Points toggle · Linear|Log · add/remove,
   ≥1); **HB** = basic + collapsed-advanced + single/multi-tone; **DC** = minimal. Sensible defaults so
   Add→OK works.
4. OK commits into the step-3 list (model + dirty), round-trips via step-2 persistence; Cancel discards.
5. `dotnet build`/`dotnet test` green; firewall green; **no copy/paste, no templates, no run changes, no
   measurements builder** (steps 5/6); nothing else regresses.

## Guardrails
- **Progressive disclosure** — type picker reveals only that type; HB advanced collapsed; never show all
  fields at once.
- **Sensible defaults** — Add→OK→Run works without touching anything; novice path protected.
- **Reuse the preview** — `DesignScope`+`Evaluator`, swallow-errors→empty, like `ParameterRowViewModel`; don't
  fork the evaluator.
- **Loadpull/pursuit disabled + tooltip** (deferred).
- **Mirror `SavePlanDialog`/`NewWorkspaceDialog`** HIG conventions; centered default button.
- **Scope fence:** the form only — no copy/paste, templates, run, or measurements builder.
- Sub-gate the three layers; report and stop between each.
- Update `analysis-authoring.md` §7 status (step 4 done) and `src/Ui/CLAUDE.md` (the analysis editor: type
  picker progressive disclosure, reused expression preview, SP segments, HB basic/advanced).

*Exit: a calm, progressive Add/Edit form — pick a type, see only its fields, with expression previews, SP
multi-segment sweeps, and HB basic/advanced — so a novice can add an analysis and Run without intimidation;
copy/paste + templates (step 5) plug into the now-complete list+form.*
