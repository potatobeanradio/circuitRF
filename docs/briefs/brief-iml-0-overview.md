# Brief series — Schematic pictures read by trained models (IML-1 … IML-11)

**Status:** written 2026-10-12, not started · **Date:** 2026-10-12 · **Decisions:** §6 locked by this brief; §7 settled by the owner 2026-10-12
**Requirement tag:** `R-iml<n>-<m>` (phase n, requirement m).
**Design note:** `docs/design/schematic-vision.md` — new, opened by IML-1. `docs/design/image-to-circuit.md` keeps the
layout-picture and plot sections; IML-10 marks its schematic-reader sections (§9–§13, §17–§26) superseded.
**Developer handbook:** `tools/schematic-ml/README.md` — new, opened by IML-1, finished by IML-11.
**Builds on:** the Create from Image series (`brief-img-0-overview.md`) and the datasheet/web series
(`brief-imf-0-overview.md`). Their **back half stays**: `SchematicImageEmit` (reading → TestBench and parts table),
`SchematicImageDrawing` (the picture's own wire graph drawn as drawn), `NetlistSchematic.Build(exact:)`,
`SchematicImageRecognition.PasteDrawing` / `PasteInto` / `IntoSchematic` / `Run`, the dialog (IM-5), the entry points
(IM-6), `recognize` / MCP (IM-11), Esc and the one-line outcome (IMF-9). Their **front half is replaced**: everything
that turns pixels into wires, symbols and words.

---

## 0. What the owner asked for (paraphrased)

The rule-based schematic reader built by the IM and IMF series does not read real pictures well enough. Replace it with
a machine-learning reader — possibly several networks working together — that recognises schematic pictures properly.

- **Training data stays outside the repository.** Only circuitRF developers train; the trained weights ship inside
  circuitRF so every user has them with no setup. Users never train.
- **The training code is in the repository**, usable by any circuitRF developer, because more training pictures will
  arrive later and the models will be retrained.
- **The new reader replaces the old one** for schematic pictures. The Create Schematic from Image dialog stays.
- **Edit ▸ Paste Image as Schematic must just work**: one command, and the schematic the user expected appears.
- **Very important — size.** The pasted parts must match the size of the parts in the picture wherever possible. The
  picture placed under the result is scaled so circuitRF's parts sit over the picture's parts. circuitRF's own symbol
  glyphs and their sizes are **not** changed to do it.
- **Training pictures:** the owner's starting set (outside the repo; ~72 screenshots — a few at the top level and four
  sub-folders, each holding screenshots of one other schematic editor). Every sub-folder is used.
- **Added mid-request:** the starting set holds **no** pictures of circuitRF's own schematics. Generate many, from
  circuitRF's own components, and train on them too.
- **Out of scope:** machine learning for Image to Touchstone (plots) and Image to Layout. They keep their current
  readers. If this works, the owner may move them to learned models later.

---

## 1. Frank assessment before any code (the owner asked for this at every series start)

**Learned models are the right tool here.** Recognising symbols, wires and words drawn in dozens of editors' styles,
at any resolution, after JPEG compression, is a perception problem. Rule-based reading plateaued for the reason the
IMF series recorded: each fix exposed the next interaction. A trained detector learns all the styles in one set of
weights, and adding a style is adding pictures, not adding rules.

**But 72 unlabelled pictures cannot train a network on their own** — not even close. A detector of this kind wants tens
of thousands of labelled examples. This series therefore rests on **generated training pictures with exact labels**:

1. **circuitRF's own schematics** (IML-2), drawn by circuitRF's own renderer from random circuits built out of the real
   component catalogue, in every colour theme, at every zoom, with and without the grid. The labels are exact because
   the generator knows every glyph, pin, wire and word it drew.
2. **Other editors' drawing conventions** (IML-3), drawn by a style-pack renderer: different resistor, capacitor,
   inductor, ground and port shapes, different pens, fonts, colours, label stacks and screenshot chrome. Style packs
   are data files, written by looking at the real pictures; adding a convention is adding a file.
3. **The real pictures** (IML-4), labelled once by hand (drafted by an agent, reviewed by the owner), used to fine-tune
   the models and, above all, **to measure them**. A fixed share of them is never trained on.

**The risks, stated now:**
- **The gap between generated and real pictures** is the main one. A model that scores 99 % on generated pictures can
  score far less on screenshots. The mitigations are wide style randomisation, style packs written from the real
  families, fine-tuning on the labelled real pictures, and a **stop-and-report checkpoint** at the end of IML-6: if the
  held-out real numbers are far from §4, the owner sees them before the reader is wired into circuitRF.
- **Text is the hardest part.** Small, blurred, JPEG-compressed values (`C=0.77 pF` at 9 px) will sometimes read
  wrong. The value grammar (`ValueGrammar`) still guards what is read, and an unread value is still a tunable global.
- **Pictures nobody has drawn yet** — a convention no style pack covers — will read worse. The handbook (IML-11) is how
  a developer fixes that: add pictures or a style pack, retrain, ship new weights.
- **Training cost.** Hours, not minutes, per training run on an Apple-silicon Mac (§7 O4). Not part of any build or test.

Not a risk, by construction: nothing here adds a native dependency (§6 L3, O1),
nothing here makes users download anything, and the data never enters the repository.

---

## 2. The system — two networks and the code around them

```
picture (file │ clipboard │ placed bitmap)          IM-2 ImageSource, unchanged
  └─ pass 1  perception net at low resolution  ──► "is this a schematic?" + the picture's part scale (px per part)
       └─ resample so a typical part body is the size the net was trained at (and tile a very large picture)
            └─ pass 2  perception net at that scale ──► per pixel and per part:
                 • parts:     class, centre, body box, orientation (4 turns × mirror), confidence
                 • pins:      a pin-point heat map (+ sub-pixel offset)
                 • masks:     wire · symbol ink · text · annotation (arrows, frames, chrome) · background
                 • junctions: a connecting-dot heat map; crossings that do NOT connect
                 • text:      line regions, with their reading direction
       └─ text net on every text line crop ──► a string (+ per-character confidence)
  └─ assembly (classical, on clean model outputs — IML-7):
       wire mask → IM-1 skeleton graph → wires; pins matched to each part's class pin layout;
       wire ends to pins; junctions decide crossings; strings → ValueGrammar / KnownWords;
       words → parts (designator, value), nets, ports
  └─ SchematicImageResult  ──► unchanged back half: emit → drawing → paste / dialog / recognize / MCP
  └─ size frame (IML-8): parts' body sizes → px per schematic unit; parts snapped; picture fitted under them
```

**Why two networks and not one, or five.** One *perception* network (one shared backbone, several output heads)
sees the whole picture once and answers everything that is a question about *where* — parts, pins, wires, dots,
text regions. A separate small *text* network reads each text line, because reading characters wants a different
input (a narrow, tall-normalised crop) and a different output (a sequence). Two passes of the same perception net
handle scale: pictures in the starting set run from 294 to 7,016 px wide, and a part can be 20 px or 400 px long.
The first pass at low resolution measures the part scale; the second runs at the scale the net was trained for.

**Why the assembly stays classical.** Turning a clean wire mask into a graph, matching pins and applying the
junction-dot rule are exact geometry, already written and gated (IM-1's skeleton graph, IM-10's emit). What failed
was deciding *which ink is what*; that is what the networks now do.

**Where things live.**

| | Where | What |
|---|---|---|
| Inference | `src/Vision` (new project, `CircuitRF.Vision`) | managed tensor runtime, model loader, the two shipped models as embedded resources |
| Reader | `src/Design/Schematic/Recognition/` | the new front half on `src/Vision`, behind the same `SchematicImageRecognition` API |
| Generator | `tools/SchematicSynth` (C#, in `circuitrf.slnx` so an API change breaks its build, not its next run) | IML-2 and IML-3 training pictures, with labels |
| Training kit | `tools/schematic-ml/` (Python) | import, label tools, training, evaluation, export |
| Data | **outside the repo**, `$CRF_ML_DATA` | real pictures and their labels, generated pictures, runs, exports |
| Shipped weights | `src/Vision/Models/*.crfnn` + `MODEL_CARD.md` | the only training product the repo holds |

---

## 3. The phases

| Phase | Brief | Delivers | Depends on |
|---|---|---|---|
| IML-1 | `brief-iml-1-data-home-and-label-format.md` | `$CRF_ML_DATA`, the importer with neutral names and fixed splits, the label format, the class list, the validator, the design note and handbook opened | — |
| IML-2 | `brief-iml-2-synthetic-circuitrf-schematics.md` | Many generated pictures of **circuitRF's own** schematics, exact labels | IML-1 |
| IML-3 | `brief-iml-3-synthetic-foreign-styles.md` | Generated pictures in other editors' conventions from style packs; distractors and chrome | IML-2 |
| IML-4 | `brief-iml-4-real-picture-labels.md` | The review/annotation tool; every real picture labelled and reviewed | IML-1 |
| IML-5 | `brief-iml-5-managed-inference-runtime.md` | `src/Vision`: the `.crfnn` format, the operators, parity tests, Debug-build speed | IML-1 |
| IML-6 | `brief-iml-6-training-kit-and-first-models.md` | The Python training kit, both networks trained, exported, measured — **checkpoint: stop and report** | IML-2, IML-3, IML-4, IML-5 |
| IML-7 | `brief-iml-7-reader-on-the-models.md` | The new front half: model outputs → `SchematicImageResult`, one reader for every entry point | IML-6 |
| IML-8 | `brief-iml-8-size-matched-placement.md` | Parts sized like the picture's; the picture scaled and placed under them | IML-7 |
| IML-9 | `brief-iml-9-paste-dialog-cli.md` | Paste, the dialog, `recognize`, MCP on the new reader; confidence shown; old options retired | IML-8 |
| IML-10 | `brief-iml-10-retire-the-classical-reader.md` | The rule-based schematic reader removed; shared raster code kept; tests migrated | IML-9 |
| IML-11 | `brief-iml-11-acceptance-docs-handbook.md` | The numbers against §4, user page, design note, handbook, RESOLVED.md | all |

IML-2…IML-5 can run in parallel once IML-1 is in. **IML-4 is the only phase with human time in it** (§7 O3).

---

## 4. Acceptance — what "good" means

Measured end to end through `SchematicImageRecognition` (the same call Paste makes), by IMF-1's `FieldScore` extended
in IML-6, on pictures **no model was trained on**:

- **Held-out real pictures** (IML-1's fixed test split of the owner's set, plus the git-ignored datasheet/web corpus
  under `testdata/image-sources/`, which is never trained on):
  - parts found with the right kind: **F1 ≥ 0.90**;
  - topology exact (netlist graph isomorphic to the truth): **≥ 70 % of pictures**;
  - designators read exactly: **≥ 85 %**; values read exactly (by the grammar, in base units): **≥ 75 %**;
  - "not a schematic" pictures refused; no schematic refused.
- **Size (the owner's very-important requirement, IML-8):** on the same pictures, **≥ 90 % of placed parts** have a
  body long side within **±20 %** of the picture's part, and the median distance between a placed part's body centre
  and the picture's part under it is **≤ ¼ grid step** (25 schematic units), 95th percentile ≤ 60.
- **Generated circuitRF pictures** (fresh seeds, never trained on): **≥ 98 %** exact.
- **IMF-1's committed style pictures** (17): every one at least as right as the rule-based reader read it, and the
  target is all exact.
- **Time (IMF O8, unchanged):** a simple diagram pastes in **under 3 s** and a complex one **under 10 s** on the owner's
  **Debug** build, both passes and the text net included. Held by counters, not timers (§6 L9).
- **Size of what ships (O2):** both models together **≤ 6 MB** targeted. The budget is flexible, but anything over
  6 MB ships only with the owner's approval, asked for with the accuracy it buys.

These are the targets IML-6's checkpoint measures against. If the first models are far short, the owner decides
there whether to continue, add data, or change course (§6 L7).

---

## 5. What stays, what goes

**Stays, unchanged in behaviour:** the IM-1 raster core (`src/Design/Imaging/` — decode, colour, skeleton graph, used by
layout and plot readers too); IM-2's source, kind and provenance; the layout-picture and plot pipelines entirely; the
back half named above; `ValueGrammar`, `KnownWords`, `ConnectionOverride`, the parts table and its CSV; the dialog's
frame, overlay and parts table; `recognize` and the MCP tool's shape; Esc; the outcome line; the time budgets.

**Goes (IML-10), once the new reader passes:** the rule-based schematic front half — `SchematicImageReading`'s
normalise/thicken/deskew chain where the model makes it unnecessary, `WireGraph`, `SymbolRegions`, `SymbolTemplates`
and the schematic `.stroke` templates, `SymbolMatch`, `StrokeMarks`, `Hatching`, `GlyphLift`, `TextRegions`, the
schematic use of the stroke text reader, `PinSquares`, `PictureTerminals`' pixel rules, `DatasheetSymbols`' pixel rules,
`PictureProfile`'s tolerance machinery, `ReadingPlausibility`'s hand-fitted weights and the candidate readings. Each is
removed only after a reference scan shows nothing outside the schematic reader uses it (the silkscreen reader shares
the stroke text code; the layout tracer shares IM-1).

---

## 6. Decisions locked by this brief

- **L1 — Data never enters the repository.** Not the real pictures, not their labels, not generated pictures, not
  checkpoints. `$CRF_ML_DATA` (default `~/circuitRF-ml-data`) holds them. The repo holds code, the class list, the
  label schema, tiny synthetic test fixtures drawn by our own code, and the shipped weights.
- **L2 — No vendor or editor name, anywhere.** The starting set's sub-folders and many of its file names name
  commercial tools. The importer gives every source picture a neutral id (`family-a`… and a content hash); the mapping
  from a folder to a family exists only in `$CRF_ML_DATA/manifest.json`. No brief, style pack, class name, test, model
  card or comment names an editor, a vendor or a part maker. Style packs are named by what they look like
  (`blue-on-cream`, `zigzag-dark`). The CLAUDE.md pre-commit grep applies to `tools/schematic-ml/` too.
- **L3 — No native dependency** (CLAUDE.md "ask before"; O1). Inference is
  managed C# in `src/Vision`; the training kit's Python dependencies are developer-only and never ship.
- **L4 — MIT-clean.** The training kit uses PyTorch (BSD) and NumPy/Pillow; **no AGPL or GPL detection framework**, no
  copied model code from one. Models are trained **from scratch** — no pretrained backbone whose weights carry their
  own terms. Fonts used to draw generated text are open-licensed and fetched into `$CRF_ML_DATA/fonts` with their
  licences, never committed; circuitRF's own shipped fonts are used as they are.
- **L5 — One reader** (IMF L5, unchanged). Paste, the dialog, `recognize`, MCP and the CLI all reach the models through
  `SchematicImageRecognition`; nothing in `src/Ui` decides anything.
- **L6 — Replace, don't stack.** No fallback to the rule-based reader at run time. One reader is one answer.
- **L7 — Checkpoint before wiring.** IML-6 ends by reporting the held-out numbers to the owner and waiting. IML-7 does
  not start on a model the owner has not seen measured.
- **L8 — The held-out pictures stay held out.** IML-1 fixes the test split by content hash; the trainer refuses to
  load a test picture. IMF-1's committed style pictures and the `testdata/image-sources/` corpus are never trained on.
- **L9 — Tests in the routine gate are fast and need no data.** No test needs `$CRF_ML_DATA`, a GPU or Python. Field
  scores over the data run in a git-ignored-data test that skips with a reason when the data is absent (as
  `ImageFieldTests` does). No new timing test; time is held by counters (the owner's rule).
- **L10 — circuitRF's glyphs are not changed** to match a picture. Size matching moves and scales the **picture** and
  places parts; it never edits a symbol.
- **L11 — Weights are versioned with a model card.** Every shipped `.crfnn` carries its version, the training-code
  commit, the data manifest's hash, the class list and its measured numbers; `MODEL_CARD.md` says the same in prose,
  with families by id and counts only.

## 7. Owner decisions (2026-10-12)

All five settled by the owner on 2026-10-12; each phase applies its own.

- **O1 — Inference runtime: our own small managed C# engine** (IML-5) — an interpreter for exactly the operator subset
  our two networks use, no native code, identical on all nine shipped platform builds, compiled optimised even in the
  Debug build. ONNX Runtime is not used.
- **O2 — Weights in plain git, kept small and rarely changed.** Stored as 16-bit floats. **Target ≤ 6 MB for both
  models together** (the owner found 16 MB too big): the perception net ≤ 2.5 M parameters (≤ 5 MB), the text net
  ≤ 0.75 M (≤ 1.5 MB). **The budget is flexible, but a larger model is the owner's call:** when a model over 6 MB
  is needed to reach §4, stop and ask, with the size, the accuracy at that size and at ≤ 6 MB, and the history cost
  per version. The approved budget is recorded in `MODEL_CARD.md` with its date, and the size gate reads it. IML-6
  reports the accuracy at half the shipped width too, so the owner can see what the size costs. **Weights change
  rarely**: a new version is committed only when its held-out numbers beat the shipped version's by a clear margin
  (topology-exact share up ≥ 5 points, or a new family or class read that was not), batching training rounds rather
  than shipping each one — every version is permanent in the history. No Git LFS.
- **O3 — Labelling: the implementing agent drafts every label** (pre-label with the current reader, then corrects by
  looking at the picture); **the owner reviews contact sheets** of overlays, about a minute a picture, flagging the
  wrong ones for another pass.
- **O4 — Training runs on the owner's Apple-silicon Mac** (PyTorch's Metal backend), overnight. No data leaves the
  machine.
- **O5 — The old reader's CLI flags, as recommended:** `--symbol-scale` stays and means "override the measured part
  size"; `--crossings` stays; `--pen` and `--text-height` are refused with one sentence saying the reader now measures
  them itself. The dialog's Advanced fields follow the same rule.

## 8. Not in this series

Image to Touchstone and Image to Layout (their readers are unchanged); hand-drawn schematics and photographs of paper;
multi-page sheets; on-device training or any user-side learning (the IM-9 "Learn" button's per-user glyph lessons do
not carry over to the text net — IML-9 removes the button from the schematic mode); cloud inference; a model
download at run time.
