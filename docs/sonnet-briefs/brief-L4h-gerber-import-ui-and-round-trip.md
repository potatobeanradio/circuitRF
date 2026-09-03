# Sonnet Brief — Phase L4h: `File ▸ Import ▸ Gerber…`, and the round trip

**Design:** `docs/design/layout-view.md` §8 (interchange), `docs/design/ui-architecture.md`.
**Consumes L4c (Gerber + Excellon export), L4e (the Gerber reader), L4f (drill and vias), L4g
(orchestration).**

**Last of four phases that together add Gerber import.** L4e reads artwork, L4f reads drill files and
rebuilds vias, L4g orchestrates a file set into a cell and a technology, **L4h** puts it on the menu and
proves the loop closes.

**Two deliverables, and the second is the important one.** The menu entry is a morning's work over
L4g's existing entry point. The round trip — export → import → export → import — is what says the four
phases actually agree with each other, and it is the only test in this series that can catch a reader and
a writer being wrong in the same direction.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. The menu entry, and the folder-or-file question

**R-L4h-1. The user may point at either a folder or a single file.** Both are legitimate starting
points and the import must accept either — this is the owner's decision on the entry behaviour, and it
is the reason this section exists rather than a plain file picker.

**R-L4h-2. A folder means: import everything in it that is artwork or drill data.** L4g's content-based
classification (its R-L4g-1) decides what that means, and its summary reports what was skipped.

**R-L4h-3. A single file means: ask whether that really is the intent, or whether the enclosing folder
was.** A single Gerber file is one layer, with no drill data, no other copper and no board outline —
almost never what someone means when they say "import this board", and yet a perfectly reasonable thing
to want when checking one layer. So:

- The prompt states **what the enclosing folder actually holds** — how many artwork files, how many
  drill files, and its name — so the choice is informed rather than blind. A prompt that asks a question
  the user has no basis to answer is worse than no prompt.
- **The whole folder is the default**, because it is the more common intent and because the cost of the
  wrong answer is asymmetric: importing a folder when one file was wanted is a folder the user deletes,
  while importing one file when the board was wanted produces a plausible-looking one-layer board.
- Cancel aborts; nothing is created.
- **Skip the prompt when there is nothing to ask** — when the enclosing folder holds no other artwork or
  drill file, the two answers are the same answer.

**R-L4h-4. Single-file import is a first-class outcome, not a degraded one.** It produces a one-layer
cell and its own technology, with the same messages as any other import, and it says plainly that no
drill data was read and therefore no vias were reconstructed (L4g's R-L4g-4).

**R-L4h-5. Reaching a folder from the file picker.** Avalonia's `StorageProvider` exposes
`OpenFilePickerAsync` and `OpenFolderPickerAsync` as separate calls; one dialog cannot return both. Open
the **file** picker first — it is the common intent, it shows the user the folder's contents so they can
see what they are choosing among, and R-L4h-3's prompt already escalates from a file to its folder. Give
that prompt a third option that opens the folder picker, for the user who wants to point at a different
folder outright.

The considered alternative — a small chooser dialog offering "Files…" and "Folder…" before any picker
opens — costs every user an extra click to serve the rarer case, and is recorded here as considered and
rejected rather than left for someone to re-propose.

**R-L4h-6. The drill-format prompt appears only when L4f's inference is uncertain.** Pre-fill it with
the inference **and the evidence behind it** — which sources were available, what the tool diameters
imply, and how the hits compare against the artwork's own bounding box (L4f's R-L4f-1). Units and zero
suppression are two separate unknowns and get two separate controls. Cancel aborts the whole import. The
precedent is `DxfUnitsPromptDialog`, built for the same reason: a drawing read at the wrong scale is the
worst possible silent failure.

**R-L4h-7. Everything else is `Messages`, not a dialog.** GDSII, DXF and board import all report through
`Messages` and only interrupt for the shared layer-mapping dialog; Gerber import interrupts for that,
for R-L4h-3 and for R-L4h-6, and for nothing else. An import fidelity dialog was considered and is not
warranted: the export side has one because a lossy *write* is about to leave the building, whereas an
import's losses are visible in the cell that just opened and reported in the summary above it.

## 2. What is lossy, permanently, and must be said out loud

Export is lossy **by type**, and no amount of reader work will change that — the format has no such
types:

| Leaves as | Comes back as |
|---|---|
| `RectShape`, `RoundedRectShape`, `CurveShape` | `PolygonShape` (a region) |
| `LabelShape` | polygons (already true on export — L4c converts labels to geometry) |
| `PathShape` with a non-round end style | one or more regions |
| `ViaShape` | a flash **plus** a drill hit — rejoined by L4f, and only if the drill file came too |
| a layer using clear polarity | composited polygons; individual shape identities gone |
| a net name containing `*`, `%` or `,` | those characters as `_` — see R-L4h-9 |

**R-L4h-9. The one loss on that list that is ours rather than the format's.** `GerberWriter`'s
`EscapeAttribute` replaces `*`, `%` and `,` in an attribute value with `_`, because those characters
terminate or delimit a block. The format does not require that: it defines `\uXXXX` escapes for exactly
this, which real exports use. So a net named with a comma survives a round trip through a third-party
tool and does **not** survive one through ours.

This is a writer change, and it is the kind §4 guards against — files people already hold would differ
from files we write next. Do not make it opportunistically. **Prove it with a failing round-trip cycle
first** (a fixture whose net names carry those characters), then change both sides together, and say so
loudly in the completion note.

**R-L4h-8. Name these in the import summary, not only in this brief.** A user who exports a design,
re-imports it, and finds their rounded rectangles are now polygons should have been told, once, at the
moment it happened.

## 3. The round trip — the phase's real gate

The claim worth proving is not "lossless", which is false, but **closed after one pass**: whatever the
first cycle collapses, every later cycle preserves exactly.

**R-L4h-9. Cycle 1 — geometric closure.** Per layer, the Clipper XOR of the re-imported design against
the original is **empty, in DBU. Exact, not toleranced.** This is the test that the reader read what the
writer wrote, and a tolerance would hide precisely the systematic errors worth catching — a unit scale
off by a factor, an arc centre resolved to the wrong candidate, a hole subtracted from the wrong outline.

**R-L4h-10. Cycle 2 onward — byte identity.** Export₂ and export₃ are **byte-for-byte identical**,
excluding only the creation-date attribute the files carry by design. This is the same comparison the
CLI's EM verb gate already makes against `EmRunService`, for the same reason: byte identity is the
strongest available statement, and the one field that legitimately differs is named rather than
tolerated by a loose comparison.

This is also what makes L4g's R-L4g-7 (record the source extension as `GerberSuffix`) load-bearing —
without it, export₂ names its files differently from export₁ and the file set stops being comparable at
all.

**R-L4h-11. Drill data is exact at EVERY cycle.** Tool count, tool diameters and the full hit set are
identical at cycle 1, 2 and 3. **The fixture must carry more than one tool diameter** — a single-tool
fixture cannot fail this test, which is the whole reason multi-size drilling gets its own gate line.

**R-L4h-12. Vias survive as vias.** A design containing vias exports, imports and comes back as
`ViaShape`s with the original `PadSize` and `DrillSize` — not as a circle plus an orphaned hole.
**Proven by re-export comparison, not by reading the fields back** (L4d's R-L4d-10 discipline: barrel and
landing are exactly the pair that reads correctly while rendering wrong).

**R-L4h-13. Shape identity is preserved wherever the format allows it.** A circle flash returns as a
`CircleShape`, a rect flash as a `RectShape`, a round-capped stroke as a `PathShape` of the right
`Width`. **Assert this on the shape types, directly.** L4e's R-L4e-9 and R-L4e-13 exist to protect it,
and without a test that names it, both will eventually be "simplified" into a uniform polygonize-and-
composite reader that passes every geometric check and quietly destroys the round trip.

**R-L4h-14. The fixture exercises both polarity paths.** One layer with no clear polarity (primitives
preserved, R-L4h-13 asserted on it) and one layer with clear polarity (composited, and the summary names
it). A fixture with only the first kind cannot distinguish "composites only where needed" from "never
composites", and one with only the second cannot distinguish it from "always composites".

**R-L4h-15. The round-trip fixture is a design, not a file.** Build it from `LayoutShape`s covering
every row of §2's table plus arcs, holes, nets and at least two drill diameters, export it with L4c, and
run the cycles. That way the gate tests our two sides against each other with no third-party file in the
loop — which is exactly its purpose, and exactly its limitation.

**R-L4h-16. Say so in the completion note: a round trip proves the two sides agree, and nothing more.**
It cannot prove the reader handles a dialect neither side emits. Whatever third-party dialect coverage
exists comes from L4e's and L4f's own fixtures, and the completion note must state plainly which parts of
the format are proven **only** by self-agreement.

**R-L4h-17. Counters only. No wall-clock assertion anywhere.**

## 4. Scope guardrails

- **No new reader work.** If a round-trip cycle fails, the fix belongs in L4e, L4f or L4g — this phase
  reports and routes, it does not grow a second parser.
- **No change to L4c's writer unless the round trip proves one is needed**, and then only with the
  failing cycle named as the evidence. R-L4h-9 is the one candidate already identified; it is a
  candidate, not an approval.
- No import fidelity dialog (R-L4h-7).
- No batch or scripted import, and no CLI verb — if a headless Gerber import is wanted it is its own
  brief, and it will want L4e–L4g moved to `src/Design` first, which is a firewall question and not a
  UI one.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 5. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Menu wiring** — `File ▸ Import ▸ Gerber…` exists in **both** menu surfaces (the native menu and the
   in-window menu), enabled on the same condition as the other import commands, and covered by the
   existing file-menu structure test.
3. **Folder entry (R-L4h-2)** — choosing a folder imports every artwork and drill file in it.
4. **File entry, folder chosen (R-L4h-3)** — choosing one file and accepting the prompt imports the whole
   enclosing folder; the prompt states the folder's artwork and drill counts.
5. **File entry, file chosen (R-L4h-3/4)** — declining the prompt imports exactly that file, produces a
   one-layer cell, and says no drill data was read.
6. **Prompt suppressed (R-L4h-3)** — a file alone in its folder imports with no prompt.
7. **Cancel creates nothing (R-L4h-3)** — asserted on the filesystem, not on a return value.
8. **Drill prompt (R-L4h-6)** — a headerless drill file raises the prompt pre-filled with the inference
   and its evidence; a fully-headered one does not raise it at all; cancelling aborts the whole import
   and leaves nothing behind.
9. **Losses reported (R-L4h-8)** — a design containing a rounded rect, a non-round-capped path and a via
   reports all three collapses in the summary.
10. **Cycle 1 geometric closure (R-L4h-9)** — per-layer XOR is empty in DBU, exactly.
11. **Cycle 2/3 byte identity (R-L4h-10)** — identical but for the creation date; the diff, when it
    fails, names the first differing line.
12. **Drill exactness (R-L4h-11)** — a fixture with at least three tool diameters keeps every tool and
    every hit across all three cycles.
13. **Vias (R-L4h-12)** — `PadSize` and `DrillSize` survive; barrel/landing proven by re-export.
14. **Shape identity (R-L4h-13)** — circle flash → `CircleShape`, rect flash → `RectShape`, round stroke
    → `PathShape`, asserted on types.
15. **Both polarity paths (R-L4h-14)** — the non-clear layer keeps its primitives; the clear layer
    composites and is named as composited.
16. **Nets (L4e R-L4e-16)** — a shape's net name survives a full cycle; a net name containing `*`, `%`
    or `,` is covered by its own test, failing or passing per whatever R-L4h-9 concluded — never absent.
17. **The job file (L4g R-L4g-5 rung 0)** — L4c writes a `.gbrjob`, so the round trip must read its own:
    file-set membership and layer identity come from the job file with no heuristic and no dialog, and
    export₂'s job file matches export₁'s.
18. **Counters only (R-L4h-17)** — no wall-clock assertion anywhere.

## 6. On completion

Write a **"Phase L4h — COMPLETE"** entry at the top of `src/Ui/RESOLVED.md` — **not** `CLAUDE.md`.
Call out:

1. **Which cycle each property actually stabilizes at.** If anything needs two cycles rather than one to
   become stable, that is the single most useful finding this phase can produce, and it must be stated as
   a measurement rather than smoothed over by asserting from cycle 2 onward.
2. **Every round-trip failure found and where it was actually fixed** — reader, writer or orchestrator.
   A writer change made to satisfy the round trip is worth naming loudly, because it means the exported
   files people already hold differ from the ones we now write.
3. **R-L4h-16's honest statement**: which parts of the format are proven only by our two sides agreeing
   with each other, and which are proven against files we did not write.
4. **What the entry flow felt like in practice** — whether the file-picker-first choice (R-L4h-5) was
   right, and whether the enclosing-folder prompt fired as often as expected.
5. **The measured cost of a full cycle** on the largest available set, as one number, so anyone adding a
   fifth phase can size against it.
6. Whether a headless import verb is now worth a follow-up brief, and what moving L4e–L4g to
   `src/Design` would cost.
