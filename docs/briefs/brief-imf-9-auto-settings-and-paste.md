# Brief IMF-9 — Settings chosen per picture, "not a schematic", and what Paste says

**Series:** `brief-imf-0-overview.md` (F12, L1, L5, O8 — settled 2026-10-11) · **Tag:** `R-imf9-<m>`
**Depends on:** IMF-2 … IMF-8
**Area:** `src/Design/Schematic/Recognition/` (`SchematicImageRecognition.cs`, `ReadingPlausibility.cs` new,
`SchematicImageOptions.cs`), `src/Design/Imaging/ImageKind.cs`, `src/Ui/ViewModels/WorkspaceViewModel.ImageRecognition.cs`
(Paste's outcome, its live row and Esc), `src/Design/Layout/Recognition/Image/ImageTrace.cs` (`PasteTrace`), the dialog's options row, `src/Cli/PictureInput.cs`, design note §25

---

## 1. Goal

Paste reads once, with one option set, and takes whatever comes back. After IMF-2…IMF-8 the reader measures its own
scales; this phase lets it **try a few readings and keep the most plausible**, refuse a picture that is not a
schematic in one sentence, and tell the user in one line what the paste did — all with no dialog (L1).

## 2. Requirements

**R-imf9-1 — Plausibility, a score of a reading.** `ReadingPlausibility.Of(SchematicImageResult)` → a number in
[0, 1] and its terms, each a property every real schematic has and a bad reading lacks:
- **ink explained** — the share of ink pixels that ended as a wire, a recognised symbol, a word or an annotation;
- **parts named** — recognised symbols over all symbol regions; an S-parameter block counts as named only when it is
  a closed box or a triangle (IMF-8), so a runaway region scores zero;
- **pins honest** — every two-pin part has two wired pins; no region has more attachments than its outline allows;
- **words read** — words read over word regions, with designators associated over parts;
- **graph sane** — no net shorted by a part's own pins, every net but a port's or a named end's reaching two pins.
The weights are fitted once on IMF-1's fixtures (a few lines of least squares in a test-only helper, the weights
committed as constants with the fit's table in §25) — not hand-tuned per picture.

**R-imf9-2 — A few readings, the best kept (O8).** With `Profile = Auto`, `Read` runs **up to four** candidate
readings in parallel and keeps the most plausible: the measured profile (IMF-2); the profile resampled one step up
(`Low` pictures only); the crossing rule *Always* instead of *With a dot* (a picture with no junction dots but many
T-junctions); and the heavy-type weight normalisation off (IMF-7, when it changed any word). Candidates that cannot
differ for this picture are not run. The report names the reading chosen, its score and the runner-up's. The dialog's
options row shows the candidates and switches between them with one click (no re-read).

**R-imf9-3 — Not a schematic.** Before reading, and again after: a picture whose kind (IMF-2's line-art evidence) is
**none** — a photograph, a shaded illustration (g), a chart — or whose best plausibility is under a floor (set from
the fixtures: every committed schematic above it, `illustration` and IM-12's nothing-here pictures below it) is
**refused in one sentence**: "This picture does not look like a schematic: <the evidence>." Paste **forces** the
schematic kind today (it never reads a picture as a layout); keep that, but force only away from *layout*, never
past *none*. Refusal is fast: the kind check runs first and alone, under 0.5 s on a 1000 px picture.

**R-imf9-4 — Paste's outcome, one line.** After Paste places the drawing, the Messages panel gets **one** line:
"Pasted 7 parts, 5 nets, 2 ports from the picture — 1 part not recognised (selected): open it in Create Schematic
from Image to correct." The counts come from the report; the unrecognised parts (IMF-6's `?<n>`), any IC or System
block with default parameters and any unread value are the ones listed. The link opens the dialog **on the same
picture with the chosen reading loaded**, so correcting is one step, not a re-read. When nothing needs correcting the
line says only what was pasted. A refusal is the line instead, and nothing is pasted.

**R-imf9-5 — Time budget (O8).** Two budgets, on the owner's Debug build, all candidate readings included:
- a **simple** diagram — up to 20 symbol regions and 2,500 px on its longer side after IMF-2's resampling (a 1× datasheet crop resampled ×2, a 2× crop as it is), which
  every committed datasheet fixture and every datasheet crop but ds-g is — pastes in **under 3 s**;
- a **complex** one — anything larger (ds-g, a full evaluation-board sheet) — in **under 10 s**.
The kind check runs first and alone (under 0.5 s), and decides how many candidates a picture gets: a complex picture
runs the measured profile first and the others only while its budget allows. The budgets are held by **counters**,
not timers (owner rule: no new timing tests): candidates run, resampled pixel count, regions matched; the measured
times go in §25 once, from a scratch harness, per field picture.

**R-imf9-6 — Esc cancels a paste (O8).** The read runs off the UI thread under a `RunControl` whose token is the
paste's own (`SchematicImageRecognition.PasteDrawing` already takes one; Paste passes none today), with a
**`RunCancellation`** handle bound to a **live Messages row**, as Simulate's run is: "Reading the picture… (Esc to
cancel)", its progress from the `RunControl` (kind check, each candidate reading, drawing). The paste is cancelled
by any of: **Esc** in the window that started it, the row's right-click ▸ Cancel, or closing the document it pastes
into — one request through one object.
- **Esc goes to the paste first.** While a paste is in flight, the window's Esc cancels it and is marked handled, so
  the editor does not also deselect or end a tool; once no paste is running, Esc means what it meant before.
- **A cancelled paste changes nothing**: no shapes, no picture kept beside the document (the kept copy is written
  only after the read succeeds), no undo step. The row ends "Paste cancelled."
- **The reader checks the token often enough to stop within 0.5 s** — between candidates, between IM-1's passes
  (threshold, skeleton, components), inside `Skeleton.Thin`'s pass loop and the template matcher's region loop. A
  cancelled read throws nothing to the user; it returns cancelled.
- **Paste Image as Layout gets the same** (`ImageTrace.PasteTrace`): one row, Esc, nothing changed on cancel.
- The commands stay disabled for a second paste while one is in flight.

**R-imf9-7 — One reader.** The candidates run inside `SchematicImageRecognition.Read`, so the dialog's Auto, `recognize`
(`--profile auto`, the default; a stated profile flag runs that one reading only) and MCP improve with Paste. `--json`
carries the chosen reading, the candidates and their scores.

## 3. Not in this phase
A correction loop inside Paste (the dialog's job); learning weights from user corrections.

## 4. Gates (minimal tests, run only these classes)
- `ReadingPlausibilityTests` — every committed fixture's correct reading scores above the floor; a reading with a
  runaway 20-pin region (the pre-IMF-4 reader's, kept as a recorded `SchematicImageResult` JSON fixture) scores below.
- `ImageNothingHereTests` — `illustration` and IM-12's pictures are refused with the not-a-schematic sentence; the
  kind check alone decides them (counter: zero candidate readings run).
- `PasteOutcomeTests` (Ui.Tests, view-model level) — the Messages line for `ds-gain-block` names its `Amp` with default
  gain; the dialog opened from the line holds the same reading.
- `PasteCancelTests` (Ui.Tests, view-model level) — a paste cancelled through its `RunCancellation` mid-read leaves
  the schematic's undo stack, shapes and folder unchanged and posts "Paste cancelled."; Esc while a paste is in
  flight cancels it and does not deselect; the same for Paste Image as Layout. The read is made slow by a test
  `RunControl` that blocks at the first checkpoint, never by a large picture.
- `ReadCancellationTests` — the reader returns cancelled at each named checkpoint (counter: passes run after the
  token was cancelled is zero).
- `ImageDatasheetStyleTests`, `ImageWebStyleTests` — every fixture that IMF-2…IMF-8 turned on still passes through
  Auto (the candidates never pick a worse reading on a fixture).
