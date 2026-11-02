# Brief 27 — the rest of the path: an imported Gerber set to a curve, without expert knowledge

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail27-n` · **Phase:** defects + one import question
**Area:** `src/Design/Layout/Interchange/GerberImport.cs`, `GerberStackupMapping.cs`,
`src/Design/Layout/TechValidation.cs`, `src/Design/Layout/Pdn/PdnGraphExtractor.cs`,
`src/Design/RailRf/RailPartDiscovery.cs` (brief 26's), `src/Ui/RailRf/RailRfViewModel.{Import,Parts,Open}.cs`,
`src/Ui/Views/RailRf/RailRfWindow.axaml`, `src/Ui/Views/Dialogs/LayerMappingDialog.axaml`
**Depends on:** [26](brief-railrf-26-parts-from-the-board.md) — this closes that brief's own §10 ·
**Blocks:** nothing
**Found by:** answering "once I run brief 26, will it all work for the user?" — 2026-09-21. It will
not. Brief 26 removes the step that is currently impossible; four more stand behind it, and none of
them is discoverable from the window.

---

## 0. The scenario this brief is measured against

A designer with a fabrication output set and no circuitRF experience:

1. imports the Gerber set into a new workspace,
2. places their own footprints in the `.clay`,
3. opens the board in railRF,
4. picks the rail, confirms the reference,
5. gets a |Z| curve with their decoupling in it.

**Step 5 is the deliverable of this brief**, and the gate in §6 is one test that performs all five
with no display. Brief 26 delivers the parts rows; on the reported board the path still breaks at
step 4 for two independent reasons, and at step 5 for a third.

---

## 1. `R-rail27-1` — copper that never reached the stackup, and is named nowhere

**The defect, exactly.** `GerberImport` step 9 builds the stackup from `copperKeys` —
the files the cascade classified as conductors. Everything else is artwork. That is correct
behaviour and `GerberLayerIdentity`'s own header defends it: *"only conductors enter the stackup and
the copper order"*, so guessing a conductor from a name is the costly wrong guess.

What is not correct is what happens next:

- **The set's inner plane matched no copper pattern.** The copper rows are `copper`, `top/bottom
  layer`, `inner`, and a numbered `layer n`; a file named for the NET its plane carries matches none
  of them. It became a `drawing` layer with 328 shapes on it.
- **Nothing said so.** The one message that names layers left out of the stackup is gated on
  `IsMaskPasteOrLegend` (`GerberImport.cs:1761`), so a file that is neither copper nor mask, paste,
  legend nor drill is reported by neither branch. On the mint path there is not even a layer summary:
  `"Layers: " + SummarizeMapping` is written only when `rows.Count > 0`, and `rows` come from
  reconciliation against a DESTINATION technology, which a fresh workspace does not have.
- **Nothing asked either.** `resolveLayerMapping` — the layer-mapping dialog — runs only when
  `unidentified.Count > 0 && rows.Count > 0`. Same `rows`. **A Gerber set imported into a workspace
  with no technology to reconcile against is asked nothing at all**, which is precisely the
  first-import case this brief is about.

So the import silently produced a technology that is missing a copper layer, and every consequence
of that appears somewhere else: no reference to confirm in railRF, an EM run with a plane missing,
and a DRC that prices nothing on it.

### `R-rail27-1a` — the import NAMES every layer it did not put in the stackup

One message, always, on every path, listing each imported drawing layer that is not in the stackup,
with the count of shapes on it, split into the two cases that mean different things:

> *Imported as artwork and not in the stackup: Soldermask Top, Soldermask Bottom, Silk Top, Paste
> Top, Paste Bottom (mask, paste and legend — see above).*
> **Not classified at all: `gnd` (328 shapes), `…-FAB` (741 shapes). If either is copper it must be
> a conductor in the stackup, or nothing on it is priced, extracted or usable as a reference.**

**The shape count is the actionable part.** "Unclassified" says nothing; *328 shapes* is what
distinguishes a plane from a stray drawing, and it is free — the import already holds the artwork.

### `R-rail27-1b` — and it ASKS, on the path where it currently cannot

The layer-mapping dialog is the right place and it already exists; what is missing is that it never
runs when there is no destination technology — `LayoutLayerMapping.Propose` *"returns an empty list
when destTech is null"*, and everything downstream is gated on that list being non-empty. **The rows
it would build already carry what this question needs**: `LayerMappingRow.ShapeCount` (sorted
descending, so a plane is at the top) and `SourceDetail`, the FILE, which is the only thing that
tells a Gerber set's layers apart when every file shares the board's stem.

It gains **one column, "in the stackup as"**, with two values:

- **artwork** — today's behaviour, and the default for every row, so an import nobody reads
  behaves exactly as it does now;
- **copper, after \<conductor\>** — the file joins `copperKeys` at the stated position.

**The position has to be asked and cannot be derived.** The import orders copper by the side and
index the cascade read off each name (`copperTopToBottom`); a file whose name states neither has no
place in that order, and inventing one puts a plane at the wrong z — which changes every plane
separation, every mounting loop and every cavity mode, all silently. The combo lists the conductors
already ordered, plus *above the top*; that is a question with a small, complete answer set.

**Rows the cascade already identified are not asked about** — that is rung 4's existing rule
(*"whatever rungs 0-3 identified is settled, and asking about it would make an exactly-identified
set interrupt for nothing"*) and it is unchanged. Only unclassified files get a row.

### `R-rail27-1c` — `TechValidation` reports a conductor with no drawing layer

Pure technology, so it belongs there and reaches the technology editor, `circuitrf check` and every
other validation surface at once:

> *Conductor 'GND' claims no drawing layer, so no artwork sits on it: it is priced by nothing,
> extracted by nothing, and cannot be named as a reference return. Attach the drawing layer that
> carries this plane's copper on the Stackup tab.*

This is the other half of the window fix of 2026-09-21 (`src/Ui/RESOLVED.md`): the combo now LISTS
such a conductor and says what is missing; this makes the same fact reach someone who never opens
railRF. **A conductor with no drawing layer is not an error** — a stackup skeleton legitimately has
them before the artwork arrives — so it is a warning, and `circuitrf check` still exits 0.

### `R-rail27-1d` — railRF says it at OPEN, without a run

The extraction already reports drawing layers carrying geometry no conductor claims, and it is
useless here: it needs a run, a run needs a reference, and the missing conductor is why there is no
reference. **Circular, and the user is inside the circle.** railRF holds the flattened shapes and
the technology the moment the board opens, so it can answer it there, as a note on the
specification panel beside the combo — the same sentence, before the first run rather than after a
run that cannot happen.

---

## 2. `R-rail27-2` — a rail that is its own reference is refused, by name

On the reported board the rail was made by clicking a pour, and the pick landed on the ground pour.
What came back was a solved result: three notes reading *"The rail has copper on layer 3/0, which is
also its reference layer… a conductor cannot be its own return"*, one breakdown row of
**526,314,394.9 squares** of reference copper, and a drop of 0 mV at 0 %. **It looks like an
answer.** Nothing on it says the rail is not a rail.

**The predicate is cheap and certain:** the rail's seed — the anchor `PdnRailRegions.Walk` is seeded
from — lands inside one of `regions.Reference`'s islands. That is a refusal, flagged at the control
that answers it (`RailRefusalControl.ReferenceLayer` or the pick button):

> *This rail is anchored on the copper of its own reference return ('gnd'), so there is nothing for
> current to return through. Pick the supply pour instead, or name a different reference layer.*

A refusal rather than a note, because the three notes it replaces are already printed today and were
read past — a result that exists is evidence that the tool understood the question.

**Not a name check.** A board may legitimately have several returns, and which net the reference is
was MEASURED from the copper on the confirmed layer (R-rail19-1d). This is region membership, like
everything else in this series.

---

## 3. `R-rail27-3` — a discovered row still has no value, and the way to give it one is per SELECTION

After brief 26 the reported board yields rows with a refdes, a position and a computed mounting
loop, and **no part number**, because R-rail26-3 refuses to invent one from a land pattern. With no
BOM in the workspace that is every row, all of them listed as unresolved, and the |Z| curve has no
decoupling in it. The table is right and the answer is still empty.

The missing gesture is the one a designer would expect: **select the rows that are the same part and
say which part they are.**

### `R-rail27-3a` — assign a part number to the selected rows

The parts list is already `SelectionMode="Multiple"` for brief 23's batch unmount, so the gesture
exists; what it gains is *Assign part number…*, on the row's context menu and the pane's own button,
taking either a row of the resolved part library or free text.

**This does not make the parts table editable**, and the distinction is the whole argument.
`RailPartRowViewModel` is read-only *"deliberately… a row that could be edited here would be a
second place the same number lives"* — and that rule is about the MODEL: capacitance, ESR, f₀, which
belong to the part library. A part NUMBER is not a model value. It is the row's own field on the
document (`RailPart.PartNumber`), it is what the library is keyed BY, and choosing it is choosing
which library row applies. Every electrical column stays read-only and stays the library's.

### `R-rail27-3b` — the board's own footprint fills the column the BOM would have

`RailPartRowViewModel.FootprintToken` reads the BOM row, then the library row, and stops — so a
discovered row on a board with neither shows nothing in the one column that would let a user group
the rows they are about to assign. The board knows: each placed instance names its land-pattern
cell, and brief-footprint-6's `PartKind` says what the placement IS.

Brief 26's discovery already walks those instances, so it returns a refdes → footprint map with its
candidates and `RebuildParts` uses it **only where the BOM and the library are both silent**. No
document field is added: the artwork is still there on the next open, and the map is rebuilt with
the rows.

### `R-rail27-3c` — and the library is one gesture from the table

*Create part library…* already exists on the project tree and seeds a `.crlib` from the document's
own part numbers (`PartLibrarySeed`, `WorkspaceViewModel`). From the parts pane it is the obvious
next step once part numbers are assigned, and brief 24's editor is where C and f₀ get typed. **This
brief adds no second route** — it puts the existing command where the work is.

---

## 4. `R-rail27-4` — the pour pick seeds its source like every other add gesture

`PickRailAt` adds `new RailSource { Anchor = … }` with no voltage, while the window's own add-source
gesture goes through `NewSeededSource` and hands it the rail's nominal or 3.3 V
(`RailRfViewModel.Seeds.cs`). So a rail made by clicking a pour — the only route available on a
Gerber-only board — produces the exact report that file was written to prevent: *"states no
open-circuit voltage, so it contributes its impedance and no DC level"*, and a column of zeros with
nothing saying the document is the reason.

One line: `PickRailAt` uses `NewSeededSource`, and the seeded-row count on the status strip covers
it exactly as it covers a dropped source.

---

## 5. What this brief deliberately does NOT do

- **It does not teach the cascade net names.** `gnd`, `pwr`, `vcc` and `plane` stay unrecognised
  (`GerberLayerIdentity`'s header gives the reason and it is still right). R-rail27-1b asks instead.
- **It does not create loads or sources beyond the seed.** §2.3 step 4 is the user's: an observation
  port is a place on the board, and no file in a Gerber set says where the IC that matters is.
- **It does not guess a capacitance.** R-rail26-3 stands; R-rail27-3 is a way to STATE one quickly.
- **It does not touch the `.clay`.** Nothing here writes artwork.

---

## 6. Tests

The per-claim tests are ordinary, and one of them is the point of the brief.

**`tests/Ui.Tests/RailRf/GerberSetToACurveTests.cs` — the end-to-end gate.** One test, no display,
performing the whole scenario on a fixture Gerber set built for it (two outer copper files, an inner
plane file named for its net, a drill file, mask and silk):

1. import the set → the technology is minted, and **the inner plane is named in the report as
   unclassified, with its shape count** (R-rail27-1a);
2. answer the layer question with *copper, after Top Copper* → **the stackup has three conductors,
   in that order, each bound to its drawing layer** (R-rail27-1b);
3. place two capacitors between the supply pour and the plane, in the `.clay`;
4. open the board in railRF → the combo offers three conductors, none disabled (the 2026-09-21 fix);
5. pick the GROUND pour → **refused by name** (R-rail27-2); pick the supply pour → a rail;
6. confirm the reference → solve → **brief 26 offers exactly the two capacitors**;
7. assign a part number to both (R-rail27-3a), seed a library, give it C and f₀;
8. **the |Z| curve has a series resonance at the part's own f₀ and is not the bare-copper curve.**

Step 8 is the assertion that says the scenario works: a curve, from a fabrication output set,
without anybody editing JSON. Everything before it is a step someone reported being unable to take.

Per-claim, beside it:

- The import's unclassified line is present with the count, and ABSENT when every file classified
  (R-rail27-1a) — a message that is always there is one nobody reads.
- `TechValidation` reports the drawing-layer-less conductor as a WARNING, and `circuitrf check`
  still exits 0 (R-rail27-1c).
- railRF says it at open, with no run performed (R-rail27-1d) — assert on a window that has never
  solved.
- The seed-on-the-reference refusal fires, and does NOT fire for a rail that merely has a via to the
  reference (R-rail27-2). **This second half is the one that would be missed**, and it is the
  difference between a refusal and a tool that refuses every real board.
- Assigning writes `PartNumber` on exactly the selected rows, and every electrical column stays the
  library's (R-rail27-3a).
- The footprint column falls back to the artwork only when the BOM and library are silent, and the
  BOM still wins where it speaks (R-rail27-3b).
- A pour-picked rail's source carries a voltage and is COUNTED as seeded (R-rail27-4).

---

**On completion:** record findings in `src/Design/RESOLVED.md` (the import, the validation, the
refusal) and `src/Ui/RESOLVED.md` (the dialog column, the parts-pane gestures, the open-time note).
**Never write findings into a CLAUDE.md.**
