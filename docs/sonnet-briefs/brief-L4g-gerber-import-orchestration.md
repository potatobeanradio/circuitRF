# Sonnet Brief — Phase L4g: turning a Gerber file set into a cell and a technology

**Design:** `docs/design/layout-view.md` §8 (interchange), §2.4 (technology and stackup).
**Consumes L4e (the Gerber reader), L4f (drill reading and via reconstruction), and the shared layer
reconciliation built by L4a/L4b and reused by L4d.**

**Third of four phases that together add Gerber import.** L4e reads artwork, L4f reads drill files and
rebuilds vias, **L4g** orchestrates a whole file set into a cell and a technology, L4h adds the menu
entry and the round-trip gate.

**Scope is everything the two readers deliberately refuse to know about**: which files in a folder are
artwork at all, what layer each one is, which technology the result belongs to, where the cell lands, and
what the user is told. This is `PcbImport` to L4e/L4f's `PcbReader` — the only piece of the stack that
touches `CellFolder`, reconciliation, `Technology` and `Messages`.

**R-L4g-0. This is one more consumer of `InterchangeStructure` and the shared layer-mapping dialog.**
If this phase grows a second reconciliation, it has gone wrong. §3's own rule, in L4d's words: reuse the
shared dialog; do not write a second reconciliation.

**Test loop:**
```
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

---

## 1. The file set

A Gerber "file" is not the unit of work. **A board is a folder**, and the folder's contents are a mix of
artwork, drill data, and a scattering of companion files that are not either.

**R-L4g-1. Classify by CONTENT first, extension second.** Extensions in this format are conventional at
best and collide at worst: artwork extensions vary widely between toolchains, a drill file and its
human-readable listing routinely share a stem, and a plain `.txt` may be artwork's companion report or a
drill file depending on who wrote it. Sniff the head of each file:

- **Artwork** — a `%FS…%` / `%MO…%` pair, or a recognizable stream of `D`-operation blocks.
- **Drill** — an `M48` header, or `T<n>C<diameter>` tool definitions over a coordinate stream.
- **A job file** (`.gbrjob`) — JSON, and the single most valuable file in the set. See R-L4g-5 rung 0.
- **Anything else** — reports, listings, placement files, netlists, images, PDFs — is a sibling to skip.

**R-L4g-2. Report every skipped file by name, once.** A folder scan that silently ignores half of what it
found is the same failure as a reader that silently ignores a token, and it is more alarming because the
user can see the files sitting there.

**R-L4g-3. Drill files frequently do NOT live with the artwork.** A production output set commonly puts
artwork in one folder and drill data in a sibling. After scanning the chosen folder, look one level up
and one level down for drill files whose stem matches the artwork's, and **offer** them —
`ImportResult` carries the candidates and L4h asks. **Never pull them in silently:** an import that
quietly reached outside the folder the user pointed at is a surprise, and a surprise in a file importer
is a support question forever.

**R-L4g-4. An artwork set with no drill data imports, and says so.** It is a perfectly ordinary thing to
want — a single layer, a check of one file — and it must not read as a failure. State that no drill data
was read and therefore no vias were reconstructed.

## 2. Layer identity — a ranked cascade, not a guess

Nothing in a Gerber file reliably says which layer it is. Four sources, strongest first:

**R-L4g-5. The cascade:**

0. **The `.gbrjob` job file**, when the set has one. It lists every artwork file by path with its own
   `FileFunction` and `FilePolarity`, so it settles **set membership and layer identity together** — it
   says which files belong to this board at all, which is something no individual file can say. It also
   carries the board's layer count, its overall thickness, and a full material stackup (R-L4g-9). Read it
   first; it is JSON, and L4c already writes one.
1. **`%TF.FileFunction`** from L4e (X2), for a set with no job file or a file the job file omits. An
   explicit statement of what the file is, and what L4c's own writer emits — so a file circuitRF produced
   is identified exactly, with no heuristic involved. It also names a copper file's **position in the
   stack** (`Copper,L1,Top` … `Copper,L4,Bot`), which R-L4g-10 depends on.
2. **A `.ctech` layer whose `InterchangeMapping.GerberSuffix` matches the file's extension.** Also ours,
   also exact, and it closes the loop against a technology the user already has: export wrote
   `<cell>.<GerberSuffix>`, so import reads the suffix back to the same layer.
3. **A generic name/extension heuristic.** Copper top / bottom / inner, solder mask, silkscreen, paste,
   outline or mechanical, drill. **This table must be data, not a chain of `if`s, and it must be
   generic** — patterns for what the layer *is*, never a table keyed to a particular tool's or vendor's
   private naming, which root `CLAUDE.md` §"Commercial Vendor References" forbids outright.
4. **The shared layer-mapping dialog** for everything left. The same one GDSII, DXF and board import all
   use.

Report, per file, **which rung identified it** — and specifically flag anything identified by rung 3,
because that is the one that can be confidently wrong.

**R-L4g-6. An unmatched row defaults to "Add to technology"**, following L4b's and L4d's own divergence
from the paste path: a file set's layer names are the author's deliberate intent, not an accident of a
paste.

**R-L4g-7. Record the source extension back onto the minted layer's `GerberSuffix`.** This is L4d's
R-L4d-4 lesson pointed in the Gerber direction, and it is not decoration: without it, a re-export names
its files from a synthetic fallback suffix instead of the names the import read, and L4h's byte-identity
gate cannot pass. L4d measured the equivalent omission on a real board — every layer landing on a general
drawing layer, tracks becoming graphics — so this is a known failure, not a hypothetical one.

## 3. The technology — a new `.ctech`, and an empty stackup

**R-L4g-8. Write a NEW `.ctech` inside the import folder and point the new `.clay` at it. Do not graft
the layers onto the workspace's technology.**

This is a deliberate divergence from board import, and the reason is **what is being grafted onto what**,
not merely how much data arrives. A board file's per-layer permittivity and loss tangent make a live
override on the workspace technology worth having; a Gerber set brings a layer table and, when a job file
is present, thicknesses and an order (R-L4g-9) — real, but still a whole board's worth of drawing layers
being pushed into a file that is possibly shared by every cell in the workspace and quite possibly
describes an entirely different process. That is a permanent cost for a temporary convenience.

A file, not a live override, for the same reason in reverse: the technology this import mints belongs to
this import, has no prior state to preserve, and nothing about it is a pending edit to something the user
already had.

**R-L4g-9. Build the stackup from the job file when there is one; leave it EMPTY when there is not.
Never fabricate one either way.**

A `.gbrjob` carries a `MaterialStackup` — an ordered, top-to-bottom list of copper, dielectric, mask,
paste and legend entries, each with a **thickness** and a material name, plus the board's overall
thickness and layer count. That is a real fraction of a `.ctech`, and it must be taken: build
`StackupLayer`s from it in the file's own order, mapping copper → `StackupKind.Conductor` and dielectric
→ `StackupKind.Dielectric`, skipping the non-electrical entries, and link each conductor to the drawing
layer the cascade resolved for it.

**What the job file still does not carry is the electrical part**: relative permittivity, loss tangent,
conductivity, permeability, and the top/bottom boundary conditions. The format has optional fields for
the first two and a real export may omit them entirely. So:

- Read `DielectricConstant` and `LossTangent` **if present**; otherwise leave them unset.
- Default conductivity and `Mur` exactly as `PcbStackupMapping` already does, and **name them as
  defaults** in the same one paragraph — three separate caveats read as three small notes, and the point
  is that these are the values a simulation will silently use.
- **Never infer permittivity from the material name.** It is a lookup table of laminate trade names, it
  is out of scope, and it would put third-party product names into this repo (root `CLAUDE.md`
  §"Commercial Vendor References"). `PcbStackupMapping` refuses this by name already; refuse it here for
  the same reason.

**With no job file, the stackup stays empty and one message says so** — an individual Gerber file carries
no substrate data whatsoever. L4d's R-L4d-6 rule holds in both branches: **do not fabricate a plausible
substrate.** An invented stackup is worse than none, because nothing downstream will ever question it and
it *will* be simulated. Say what is still needed before the EM path can run.

**R-L4g-10. Layer ORDER is declared by X2 and by the job file; guess it only when neither is there, and
report the guess.** `Copper,L1,Top` … `Copper,L4,Bot` ranks the copper exactly, and the job file's
stackup order ranks everything else. Take it. Only a set with neither needs a heuristic — copper top and
bottom from a name, inner ordering usually unrecoverable — and then say, by name, which layers were
ordered by guess. A silently wrong stack order produces a simulation that runs cleanly and answers a
different question (L4d's R-L4d-5), which is why the guess must never be indistinguishable from the
declaration.

**R-L4g-11. Colours come from `FallbackPalette.For(key)`** — the same deterministic gap-fill the
renderer, DXF import and board import already use. **Never from a `G04 Layer_Color=` comment**: those are
one tool's private annotation, they are not portable, and honouring them would make two imports of the
same board look different depending on who generated the files.

## 4. The cell — one, flat, and deliberately so

**R-L4g-12. One cell folder, one `LayoutView`, no sub-cells.** Gerber has no hierarchy. The only
construct that resembles one is step-and-repeat, which is panelization and which L4e already flattens
(its R-L4e-15).

**Component membership can be DECLARED, and the answer is still no.** `%TO.C` names a component
reference and `%TO.P` its pad, so grouping pads by component is a lookup rather than the threshold-driven
clustering it would otherwise be — that part of the objection does not survive contact with a modern file
set, and the code must not repeat it. The conclusion survives on three others:

- **There is no cell definition to build.** Two placements of the same part carry different references
  and their geometry is in absolute board coordinates; recovering a shared cell plus two transforms means
  inferring the transform, which is the clustering problem again by another name.
- **It would break the round trip.** L4c's writer emits no `%TO.C`, so hierarchy built from it could not
  survive a re-export, and L4h's byte-identity gate would fail on the first cycle.
- **It is not needed.** The pads are on the right layers with the right nets either way.

**Carry `%TO.C` and `%TO.P` onto the shapes as metadata** — they cost nothing, they are the natural key
for a later "group by component" editor action, and discarding declared data is not a neutral act. Say
all of this in the code, at the site where someone would otherwise add footprint inference.

**R-L4g-13. The cell lands under an `ImportFolder.Create` directory named after the source folder (or
the single file).** Unchanged from board import, for the reason recorded there: a file set generates
enough cells and layers to bury everything the user actually authored, `ImportFolder.UniqueName` is what
stops a second import of the same board merging into the first one's folder, and a cell in a sub-folder
already works everywhere.

**R-L4g-14. A cancelled or failed import leaves nothing behind** — `ImportFolder.RemoveIfEmpty` on every
exit path, exactly as board import does. "Nothing was created" has to stay literally true.

## 5. What the user is told

**R-L4g-15. One honest summary, per layer and then overall.** Per layer: the file it came from, which
rung of the cascade identified it (and a flag if that was the heuristic), shape counts split into
flashes / strokes / regions, and **whether the layer was composited for polarity** (L4e's R-L4e-13) —
because a composited layer has lost its shape identities and the user is entitled to know which ones
did. Overall: drill tools and hits, vias reconstructed, unpaired hits, slots, files skipped, and every
construct L4e or L4f refused or counted.

**R-L4g-16. The stroke-count line is actionable, not decorative.** A copper pour that arrived as N
parallel strokes (L4e's R-L4e-19) is correct artwork that is neither editable copper nor meshable, and
the fix already exists in the editor — the Merge action. Name the layer, name the count, name the
action. A user cannot act on what they are not told, and a bare number they cannot act on is noise.

**R-L4g-17. Say what comes next, once.** L4d's R-L4d-19 rule holds here unchanged: the whole set comes
in, unfiltered, because cropping is an edit and the editor already has one. A real board is not a MoM
problem as a whole — the summary should say to crop the region of interest before setting up EM ports.

## 6. Scope guardrails

- **No menu item, no picker, no prompt** — L4h. This phase's entry point takes a resolved list of file
  paths and returns a result; it asks no one anything except through the callbacks it is handed (the
  shared layer dialog, and L4f's format resolution).
- **No second reconciliation** (R-L4g-0).
- No footprint inference, no net inference beyond `%TO.N`, no design rules, no board outline
  interpretation beyond putting outline artwork on its own layer.
- No change to L4c's writer.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 7. Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); documented gate green; no existing test regresses.
2. **Classification (R-L4g-1)** — a folder holding artwork, drill data, a drill listing, a placement file
   and a PDF imports the first two and skips the rest, **by content**; renaming a file to a misleading
   extension does not change the outcome.
3. **Skips reported (R-L4g-2)** — every skipped file is named once in the summary.
4. **Sibling drill data (R-L4g-3)** — drill files one level away are offered, not imported silently; the
   result carries them as candidates and importing without accepting them produces no vias.
5. **No drill data (R-L4g-4)** — an artwork-only set imports cleanly and says no vias were reconstructed.
6. **Cascade rung 1 (R-L4g-5)** — a set carrying `%TF.FileFunction` is identified with no heuristic and
   no dialog.
7. **Cascade rung 2 (R-L4g-5)** — a set exported by L4c against a technology with `GerberSuffix` aliases
   re-identifies every layer exactly, with no dialog.
8. **Cascade rung 3 flagged (R-L4g-5)** — a heuristic identification is reported as a guess, by name.
9. **Suffix recorded (R-L4g-7)** — an imported layer carries the source extension as its `GerberSuffix`;
   deleting that behaviour makes L4h's byte-identity gate fail, which is asserted as the reason.
10. **A new technology (R-L4g-8)** — the import writes its own `.ctech` in the import folder, the `.clay`
    resolves against it, and the **workspace's own technology is unmodified** — asserted directly, since
    that is the divergence from board import.
11. **Stackup from a job file (R-L4g-9)** — a set with a `.gbrjob` yields conductor and dielectric
    layers with the file's own thicknesses, in the file's own top-to-bottom order; permittivity and loss
    tangent are left unset when absent; conductivity and `Mur` are named as defaults; **nothing is
    inferred from a material name**.
12. **Empty stackup (R-L4g-9)** — a set with no job file yields no stackup layers, one message says so,
    and **no substrate is fabricated**. A test that asserts a plausible default would be asserting the bug.
13. **Order declared (R-L4g-10)** — a four-copper set with `Copper,L1..L4` ranks exactly and reports no
    guess; the same set stripped of its X2 attributes and job file reports which layers were guessed.
14. **Colours (R-L4g-11)** — a file carrying a colour comment gets `FallbackPalette` colours; two imports
    of the same set produce the same colours.
15. **One flat cell (R-L4g-12)** — no `LayoutInstance` is created, whatever the input.
16. **Nothing left behind (R-L4g-14)** — a cancelled import and a failed import both leave no folder.
17. **Composited layers reported (R-L4g-15)** — a set with a `%LPC` layer names that layer as composited.
18. **Counters only** — no wall-clock assertion anywhere.

## 8. On completion

Write a **"Phase L4g — COMPLETE"** entry at the top of `src/Ui/RESOLVED.md` — **not** `CLAUDE.md`.
Call out:

1. **How well the identity cascade actually performed**, per rung, across the fixtures available: how
   many files were identified exactly, how many by heuristic, how many needed the dialog. That ratio is
   the real measure of whether this import is pleasant to use.
2. **What the heuristic table ended up containing**, and whether keeping it generic (R-L4g-5) cost
   accuracy on real sets — stated as a number, not an impression.
3. **The new-`.ctech` decision as it landed** (R-L4g-8), including anything found that argues for the
   board-import live-override model instead.
4. **How much of a stackup a job file actually delivered** (R-L4g-9) — which fields were present, which
   were absent, and therefore what an imported set still needs before it can be simulated. The concrete
   list, so the next phase or the user is not left to work it out.
5. **The largest set imported**: files, layers, shapes, and the resulting `.clay` size, as measured
   numbers.
