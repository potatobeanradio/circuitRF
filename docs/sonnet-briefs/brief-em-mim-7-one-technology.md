# Brief MIM-7 — one GaAs technology: the capacitor dielectric participates only when its plate does

**Problem.** circuitRF ships two MMIC technologies — `mmic-GaAs_2LM_100um.ctech` and
`mmic-GaAs_2LM_100um_MIM.ctech` — identical apart from the MIM module (three stackup entries, two
drawing layers, `Metal1.SheetAt = Top`). The owner wants ONE technology that simulates both MIM
capacitors and ordinary interconnect on the upper metals. The split was never filing: MIM-2 measured
two real costs of stating the capacitor dielectric (`src/Ui/RESOLVED.md` §MIM-2), and neither may
land silently on the technology every existing MMIC workspace copied:

1. **Every Metal1–Metal2 airbridge post stops solving.** The post spans two regions of the medium
   (the capacitor dielectric's band, then air), and `PlanarKernel.CanSolve`'s
   `EveryViaLiesInOneMediumRegion` refuses a via that crosses a dielectric interface — the closed-form
   via z-integral (`ViaZIntegral`) is written in ONE region's asymptotic coefficients. **A whole-run
   refusal, not a dropped shape.**
2. **A Metal1 microstrip moves.** The 0.2 µm εᵣ 6.8 sheet is laterally infinite by the 2.5D premise,
   so it sits on every run's Metal1 as superstrate: Z₀ −2.8%, ε_eff past an acceptance test's bound.

## The observation that makes unification cheap

Both costs exist only because the capacitor dielectric is placed in the laterally-infinite medium of
EVERY run — including runs that contain no capacitor. Physically the nitride is patterned: it exists
at the capacitors and nowhere else. The 2.5D premise forces "laterally infinite per RUN"; it does not
force "present in every run". The honest per-run proxy for "this run has capacitors in it" already
exists: **is the plate conductor among the run's analysis levels?** — and the extractor's DEFAULT
level selection is exactly "the non-ground conductor bands that carry artwork"
(`PlanarExtractor.Extract`, the `signalBands` else-branch), so an interconnect-only layout
deactivates the module with no configuration at all, and a layout with plate artwork activates it.

This is also the kernel's own suggested remedy, verbatim in the refusal text: *"…or remove the
interface if it carries no physics."*

Read first: `src/Design/Layout/TechModel.cs` (`StackupLayer`, `SheetAt` — the additive-nullable-field
precedent, no `FormatVersion` bump); `src/Design/Layout/Em/PlanarExtractor.cs` (level selection
~line 228, `BuildMediumStack` ~line 678, the SheetAt absorption branch); `src/Design/RESOLVED.md`
§MIM-6 (why sheet surface and absorption direction are ONE choice);
`tests/Ui.Tests/Em/MimCapacitorTests.cs` (the tests this brief flips);
`src/Ui/RESOLVED.md` §MIM-2; `docs/user/src/reference/stackup.md` (#mim-separate, #mim-import).

## The design

**`StackupLayer` gains one optional field on Dielectric entries** — working name `PlateOf` (pick a
better one if you have it; a conductor-entry NAME, e.g. `"MIM Metal"`), meaning: *this dielectric is
a patterned thin film that physically exists only at that conductor's artwork.* Additive, nullable,
meaningless on non-Dielectric entries, no `FormatVersion` bump — the `SheetAt`/`SpanFromLayer`
pattern exactly.

**Extraction rule.** When the named plate conductor is NOT among the run's analysis levels:

- the dielectric's band enters the medium as **air** (εᵣ 1, tanδ 0, thickness preserved — every
  other band's height stays put);
- **`SheetAt = Top` on the conductor whose band sits directly beneath this dielectric is treated as
  unset for this run.** This is deliberate, not a convenience: MIM-6 built `SheetAt` expressly so
  the plate gap reads 0.2 µm; absent the plate, the pre-MIM-6 sheet placement is the established
  baseline, and reverting it is what makes the degeneracy gate below BIT-identical rather than
  "close". State both halves of the rule in one place and emit ONE run note naming them
  (e.g. *"'MIM Dielectric' treated as air and 'Metal1' sheet at band bottom: plate level
  'MIM Metal' is not an analysis level in this run"*).

When the plate conductor IS an analysis level, nothing changes from today's MIM technology.

The kernel keeps its refusal untouched — it still guards hand-authored stacks whose dielectric
genuinely is everywhere. This brief is extraction-side only; **`src/Engine` must not change.**

## Milestones

1. **Schema + editor.** The field on `StackupLayer`; `TechPersistence` round-trip;
   `TechValidation` — it must name an existing, non-ground Conductor entry, and (recommend, state
   it) the conductor directly ABOVE the dielectric; invalid on a non-Dielectric entry.
   `TechnologyMerge`, `StackupLayerRowViewModel` + `TechEditorView.axaml` (follow `SheetAt`'s own
   editor pattern).

2. **The extraction rule, with a bit-identity gate.** The same airbridge artwork (post + Metal1 +
   Metal2, no MIM artwork) extracted on the merged technology and on today's plain starter must
   produce IDENTICAL `PlanarProblem`s — level z's, medium stack regions, vias — and
   `PlanarKernel.CanSolve` must accept it. `MimCapacitorTests.AnAirbridgePost_IsRefusedByTheKernel…`
   flips from asserting the refusal to asserting the solve on the one technology. The ACTIVE case
   (plate level in the analysis) must extract bit-identically to today's `MmicGaAsMim()` — pin the
   expected stack BEFORE deleting the old builder. If bit-identity fails for a reason not covered
   here, report the reason rather than forcing it.

3. **One shipped file.** Fold the module into `mmic-GaAs_2LM_100um.ctech` (the byte diff against the
   current plain file must read as pure additions — nothing renamed or renumbered, MIM-2's own
   rule), retire `mmic-GaAs_2LM_100um_MIM.ctech`, fold `StarterTechnologies.MmicGaAsMim()` into
   `MmicGaAs()` and update call sites. Update the technology's Name/description note. Sweep the
   ship-gate test, workspace-picker, foreign-document and DRC/palette consumers. Existing
   workspaces are untouched by construction — they hold their own copies (`ShippedTechnologies`,
   R-misc-8) — say so in the write-up rather than leaving it to be wondered about.

4. **User docs + the stackup rendering (owner request).** Rewrite
   `docs/user/src/reference/stackup.md`: #mim-separate ("It is a separate technology, and that is
   not filing") becomes the explanation of the tied dielectric — one technology, the capacitor
   dielectric rides along only when its plate is analyzed; #mim-import drops the "do it on a copy"
   warning in favour of "add the rows and set the tie on the same technology", keeping an honest
   note of what ACTIVATION changes (a run that analyzes the plate sees the lower metal's line move
   in ε_eff/Z₀ — that is the capacitor run's real physics). **Add a rendered stackup cross-section
   figure to the #mim section** using the existing DocGen figure machinery
   (`{{ui: …}}` → `FigureCatalog` → `DocStackupFixtures`, which draws from the REAL shipped
   `Technology` object so the picture cannot outlive the file): the existing `stackup-mmic` figure
   will now show the module rows automatically once `MmicGaAs()` gains them; add a focused variant
   for the MIM section if the single figure gets crowded — the plate metal, the tied dielectric
   (mark the tie), and the plate via's span are what the reader needs to see. Regenerate the docs;
   classify SVG id-churn vs real change before reporting (the id counter is HEX).

5. **Gates.** `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` green (run as two commands);
   no `src/Engine` diff; the run-note wording asserted (a deactivated tie must be reported, never
   silent — the same discipline as the extractor's dropped-artwork note). Write-up split:
   `src/Design/RESOLVED.md` (the extraction rule and its gates) and `src/Ui/RESOLVED.md` (the
   shipped-file merge, docs, editor). Correct any sentence this makes false in
   `docs/user` or `docs/design/mom-engine.md` in place with a dated "Built at MIM-7" note. Do not
   write to any CLAUDE.md. Add the MIM-7 row to `brief-em-mim-series.md`'s table.

## What this deliberately does NOT do

- **Airbridge posts and capacitor plates in ONE EM setup** are still bounded: with the plate level
  in the analysis, a Metal1–Metal2 post skips a level and is dropped with the `notAdjacent` note
  (asserted today in `MimCapacitorTests`). The principled fix — the extractor splits a solid via
  crossing an intermediate ANALYSIS level into a chain and paints its cross-section as artwork on
  that level (physically faithful: the post's metal exists at that height) — is named here as its
  own future brief, not smuggled in. Separate EM setups per structure remain the documented answer.
- **The engine's via-across-an-interface capability** (splitting `ViaZIntegral` at the interfaces a
  via crosses, per-region closed forms plus bounded cross-region pairs — the remedy
  `PlanarKernel.EveryViaLiesInOneMediumRegion`'s own doc names as "real work… not built") is NOT
  this brief. Even built, it would not unify the technologies alone: the always-present dielectric
  would still move every interconnect line, which is the cost the owner refused at MIM-2.
- **Always-on with re-baselined numbers** — rejected for the same reason it was at MIM-2: a silent
  change to every plain interconnect result.

## Must NOT

- Touch `src/Engine` — the refusal, the z-integral and the kernel stay exactly as they are.
- Rename or renumber any existing layer, stackup entry or DRC rule.
- Loosen any existing gate; if one seems to need it, say which and stop.
- Name any foundry, process or vendor kit anywhere (root `CLAUDE.md` §Commercial Vendor References).
- Read any small element off a RAW solve (series convention — MIM-2's retraction).

The conventions block of `brief-em-mim-series.md` binds this brief.
