# Brief IML-9 — Paste, the dialog, `recognize` and MCP on the new reader

**Series:** `brief-iml-0-overview.md` (§0, L5, O5) · **Tag:** `R-iml9-<m>`
**Depends on:** IML-8
**Area:** `src/Ui/ViewModels/WorkspaceViewModel.ImageRecognition.cs`, `src/Ui/ViewModels/SchematicViewModel.ImagePaste.cs`,
the dialog (`ImageSourceViewModel`, `ImageReadingPanel`, `ImageOverlay(+Canvas)`, options row),
`src/Design/Imaging/ImageOverlay*.cs`, `src/Cli/PictureInput.cs`, the `recognize` verb and MCP adapter,
`docs/design/cli.md` §26.8/§27, design note §11

---

## 1. Goal

The owner's UX requirement: **Edit ▸ Paste Image as Schematic is one command and gives the schematic the user
expected**, with parts the picture's size over the picture. The dialog stays as the place to correct a reading. The
CLI and MCP read the same way and report the same things.

## 2. Requirements

**R-iml9-1 — Paste.** Unchanged in shape (no dialog, into the focused schematic, one undo step, Esc cancels, one
outcome line, Correct… opens the dialog seeded), with these differences:
- **First use is warm:** the models load on a background thread the first time a picture reaches the clipboard check
  (`PasteImageAvailability`), so the first paste does not pay the load;
- **the picture goes under the drawing at the fitted size** (IML-8), at the underlay opacity, as now;
- **the selection after paste is the review set:** parts below the review confidence, parts with an unread value, and
  parts IML-8 could not place within ½ step — not every pasted item. When the review set is empty, everything pasted
  is selected as today. The outcome line counts the review set ("3 parts to check, selected");
- the progress row names the two stages ("Reading the picture…", "Reading the words…") rather than a bare spinner.

**R-iml9-2 — The dialog.** The source pane, frame, overlay and parts table stay. Changes:
- the overlay draws the new reading — part boxes coloured by class with the confidence as the box's opacity, pins,
  wires, junction dots, text quads with the string read — and hovering a part shows its class, confidence and the
  alternatives the model ranked next (the top three class scores at that peak), so a correction is one click from the
  parts table;
- the options row loses the controls that belonged to the rule-based reader (the candidate-reading switch, pen and text
  height — O5); it keeps the crossing rule, the size override, Arrange (As drawn / Tidy) and the kind;
- the schematic mode's **Learn** button (per-user glyph lessons for the stroke reader) is removed — the text net does
  not learn per user (overview §8); its stored lessons are left on disk untouched and unread.

**R-iml9-3 — `recognize` and MCP.** Same verb, same flags but O5's: `--pen` and `--text-height` are refused with one
sentence ("recognize: --pen is no longer used; the schematic reader measures the picture itself."), with a
`picture.*` diagnostic id; `--symbol-scale` sets IML-8's frame scale. `--json` gains, per part, `confidence` and
`alternatives`, and for the reading `model` (name and version), `frame` (s, s′, residual median and max) and
`review` (the review set's ids). `--overlay` draws what R-iml9-2 draws. The MCP `recognize` tool returns the same
JSON and overlay; its description says the reader is a trained model and that low-confidence parts are listed under
`review`. `cli.md` updated.

**R-iml9-4 — One picture, one answer.** GUI paste, dialog Make, `recognize` and MCP on the same picture give the same
circuit and the same frame — byte identity on the written `.csch` between the CLI and the in-process call, as IM-11's
gate already holds for the rule-based reader.

## 3. Gates

- `PasteOutcomeTests`, `PasteCancelTests`, `PasteDatasheetEndToEndTests`, `ImageScreenshotPasteTests` updated: the
  review-set selection, the warm load (a counter: the model loaded once across two pastes), Esc during each stage.
- `ImageRecognizeCliVerbTests` / `McpImageToolTests`: the new JSON fields, the O5 refusals, CLI-vs-in-process byte
  identity on a committed fixture.
- The dialog's view-model tests for the removed options and the hover alternatives.
- Run only the image-related test classes (the owner's targeted-tests rule), not all of `Ui.Tests`.

## 4. Not in this phase

Deleting the old reader's code (IML-10). New entry points. Learned correction from user edits.

## 5. Traps

- **The clipboard check runs often** (IM-6: only on window activation and menu opening); warming the models there
  must be a one-time background start, never a blocking load on the UI thread.
- **Removed options in saved presets / provenance**: a stored option the reader no longer has is ignored on read and
  not written again — never an error opening an older document.
