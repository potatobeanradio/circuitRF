# Analysis Authoring — Step 2: `.csch` persistence + the one shared serialization (Claude Code / Sonnet)

Persist analyses (+ measurements) in the `.csch`, via a **single shared serializer** that will ALSO back the
clipboard payload and the `.canl` template file (the locked single-source-of-truth decision). Flip
`SchematicHasAnalyses` real so a schematic carrying analyses marks its cell `IsTestBench`. **This brief is
step 2** — persistence + the shared encoder + the IsTestBench hook. **No UI, no copy/paste, no templates, no
extraction/run wiring** (steps 3+). Read `analysis-authoring.md` §3 + §5.4 first. Sub-gated; **report and stop
between every layer.** Firewall green.

> Read first: `docs/design/analysis-authoring.md` §3 (`.csch` persistence), §5.4 (one serialization for
> `.csch` + clipboard + `.canl` — implement ONCE). Context code: `src/Ui/Schematic/SchematicPersistence.cs`
> (`CschFile` + the per-type `Csch*` DTO pattern, `[JsonConverter(typeof(JsonStringEnumConverter))]`,
> `WhenWritingNull`, format_version reject — **mirror exactly** for analyses), `src/Core/Design/Analysis.cs`
> (step-1 shape: `Analysis` base + `DcAnalysis`/`SParameterAnalysis (Sweeps list)`/`HarmonicBalanceAnalysis`,
> `FrequencySpec` w/ mode + expr fields, `Measurement`), `src/Ui/Schematic/SavePlan.cs`
> (`SchematicHasAnalyses(model)` — currently `return false; // TODO 6e`), `src/Ui/Schematic/CellPersistence.cs`
> (`CcellFile.IsTestBench`), `src/Ui/Schematic/EditableSchematic.cs` (`SchematicEditModel` — where the
> in-memory analyses list lives). Design docs win on any conflict.

## The spine (do not violate)
- **One serialization, three destinations (§5.4):** implement the analyses/measurements serialize+deserialize
  **once** (a `AnalysisSerialization` helper or equivalent). The `.csch` analyses section uses it now;
  clipboard (step 5) and `.canl` templates (step 5) reuse the **same bytes**. Do NOT write a second encoder
  later — build the shared one here.
- **Polymorphic by type** — DC/SParameter/HB are different shapes; serialize with a type discriminator
  (`[JsonPolymorphic]` + `[JsonDerivedType]`, or a `Type` tag), the same idea `SymbolModel` primitives use.
- **Framework-free model, GUI-side persistence** — the `Analysis`/`Measurement` types are in `src/Core`
  (framework-free); the serializer + `.csch` integration live where `SchematicPersistence` is (`src/Ui/
  Schematic`, still Avalonia-free). Headless-testable.
- **Alpha policy** — format_version reject-on-mismatch; `Id` never persisted; graceful within-version load
  (absent analyses list → empty).
- **Scope fence (step 2):** `.csch` analyses+measurements persistence + the shared encoder + IsTestBench hook.
  NO UI, NO clipboard, NO template files, NO extraction/run wiring.

---

## LAYER 1 — the shared analysis serializer (polymorphic, framework-free)

A single serializer for a **list of `Analysis` + list of `Measurement`** (the unit that `.csch`, clipboard,
and `.canl` all use):
1. **DTOs** mirroring `SchematicPersistence`'s `Csch*` pattern — `CschAnalysis` (polymorphic: DC/SP/HB
   variants), `CschFrequencySpec` (start/stop/step/mode/points/kind expr fields, step-1 shape),
   `CschMeasurement` (name/expr/unit). Map `Analysis`↔DTO both directions.
2. **Polymorphic discriminator:** a `Type` tag (`"dc"`/`"sp"`/`"hb"`) so the list round-trips mixed types.
   (Loadpull/pursuit: not authored yet — either omit from the v1 discriminator or carry as a passthrough;
   state which. v1 only needs DC/SP/HB.)
3. **`Serialize(IReadOnlyList<Analysis>, IReadOnlyList<Measurement>) → json`** and the inverse — the **one**
   encoder reused everywhere. System.Text.Json, enum-as-string, `WhenWritingNull`.
4. Round-trip unit test over a mixed list (DC + 2-segment SP + HB with expr fields) — equivalent after
   round-trip; `Id` not present.

**Layer 1 gate:** the shared serializer round-trips a mixed analyses+measurements list losslessly
(polymorphic, expr fields, SP multi-segment), framework-free, headless-tested. Report.

---

## LAYER 2 — integrate into `.csch` + flip `SchematicHasAnalyses`

1. **`CschFile`** gains `List<CschAnalysis> Analyses` + `List<CschMeasurement> Measurements` (absent in old
   files → empty, graceful). The `.csch` load/save maps them via the **Layer-1 shared serializer** (don't
   inline a second encoder). `SchematicEditModel` holds the in-memory analyses+measurements list that
   load/save round-trips.
2. **`SchematicHasAnalyses(model)`** (SavePlan) now returns **whether the model's analyses list is non-empty**
   (replacing `return false; // TODO 6e`). So on save, the cell's `.ccell` `IsTestBench` flips true when its
   primary schematic carries analyses (the save-plan + cell-create paths already consult this hook).
3. Round-trip test: a `.csch` with analyses+measurements saves and reloads to an equivalent model; a schematic
   with analyses drives `SchematicHasAnalyses == true` (→ IsTestBench on save); an old `.csch` with no
   analyses loads as empty (no break).

**Layer 2 gate:** `.csch` round-trips analyses+measurements via the shared serializer; `SchematicHasAnalyses`
reflects the real list; IsTestBench flips on save for an analysis-carrying schematic; old files load. Report.

## Acceptance (step 2)
1. A **single shared serializer** round-trips a polymorphic analyses + measurements list (DC/SP-multi-segment/
   HB, expr fields) — the encoder clipboard + `.canl` will reuse (§5.4); framework-free, headless-tested.
2. `CschFile` persists `Analyses` + `Measurements` via that serializer; old analysis-less `.csch` load as
   empty (graceful, alpha policy; `Id` not persisted; format_version reject).
3. `SchematicHasAnalyses` returns the real non-empty check; a schematic with analyses flips its cell
   `IsTestBench` on save.
4. `dotnet build`/`dotnet test` green; firewall green (Core types framework-free; serializer Avalonia-free);
   **no UI, no clipboard, no templates, no extraction/run wiring** (steps 3+); nothing else regresses.

## Guardrails
- **One encoder (§5.4)** — build the shared analysis serializer now; `.csch` uses it; clipboard + `.canl`
  reuse it in step 5. Never a second encoder.
- **Polymorphic by type tag**; mirror `SchematicPersistence`/`SymbolModel` conventions (enum-as-string,
  WhenWritingNull, format_version reject, Id not persisted).
- **Graceful load** — absent analyses → empty list; old files don't break.
- **Framework-free** serializer; headless-testable.
- **Scope fence:** persistence + shared encoder + IsTestBench hook only.
- Sub-gate the two layers; report and stop between each.
- Update `analysis-authoring.md` §7 status (step 2 done) and `src/Ui/CLAUDE.md` (the shared analysis
  serializer backs `.csch`/clipboard/`.canl`; SchematicHasAnalyses real; IsTestBench flips on save).

*Exit: analyses + measurements persist in the `.csch` through the one shared serializer that copy/paste and
templates will reuse, and a schematic carrying analyses marks its cell as a TestBench — the persistence
foundation the authoring UI (steps 3–4) and reuse (step 5) build on.*
