# Brief 2 — the nets have names, and nobody had to type them twice

**Series:** [authored board](brief-authored-board-0-overview.md) · **Tag:** `R-ab2-n` · **Phase:** P1
**Area:** `src/Design/Layout/Pdn/PdnLayoutNets.cs` (new), `PdnLayoutPads.cs`,
`src/Design/RailRf/RailArtwork.cs`, `src/Ui/RailRf/RailRfViewModel.Import.cs`,
`src/Ui/Layout/LayoutEditorViewModel.*`, `src/Ui/ViewModels/LayoutShapePropertiesViewModel.cs`
**Depends on:** [1](brief-authored-board-1-layout-pads.md) · **Blocks:** brief 3

---

## 0. What this brief delivers

Brief 1 produced pads with no net names. This one names them, by the owner's rule of 2026-09-20:

```
net(pad)  =  the schematic's own binding for that refdes and that pin,  where a schematic resolves
             else the net stated on the copper the pad lands on
```

**Re-derived, with a stamped fallback** — `DisplayRefDes`' own shape (`LayoutModel.cs:875`), chosen
for that record's reason: a re-derived value cannot go stale, and the stored one is what a board with
no schematic behind it needs.

The consequence to state before anything else, because it is the part that surprises: **nothing in
this brief writes `Net` automatically.** Update Layout does not stamp it. There is no forward
annotation, no back annotation and no migration. The stamp is a user's own act on a board they drew
by hand, exactly as `RefDes` is for a hand-placed instance — and if a schematic later resolves, the
schematic wins and the stamp is simply not consulted.

---

## 1. `R-ab2-1` — re-derived: the walk to the schematic

**`R-ab2-1a`** From the artwork's own path, not from whatever workspace is open. The `.clay` lives in
a cell's `layout/` folder; its sibling is `CellFolder.ResolvePrimary(cellDir, ViewType.Schematic)`.
That is the walk `RailArtwork` already performs for the technology (*"a board drawn in one workspace
opens here against the technology that priced it, not against whichever workspace happens to be
open"*, `RailRfWindow.Open.cs:117`), and it is the same rule for the same reason.

**`R-ab2-1b`** Read it with `SchematicPersistence.LoadFromFile` and extract with
`NetExtractor.Extract(model, "tb", DiskCellResolver.Instance)`. Both are in `src/Design/Schematic/`,
below the firewall, and `src/Cli/Check.cs:383` already does exactly this headlessly — so the CLI
gets net names with no display and no second reader.

**`R-ab2-1c`** The mapping is `Instance.InstanceName` → `Instance.NetBindings`, **in port order**
(`src/Core/Design/Instance.cs:14,23`). Joined to the artwork by **`LayoutInstance.SchematicId`**, not
by `DisplayRefDes`: `SchematicId` is what Update Layout writes and what v2's LVS matches on
(R-fp3-3b), and for a schematic-owned instance the two strings are equal anyway. An instance carrying
only a hand-typed `RefDes` has no schematic counterpart and takes R-ab2-2's path even when a
schematic resolves.

**`R-ab2-1d`** Port index → pin is brief 1's `R-ab1-4` join, unchanged and not re-derived here. The
name branch, the index branch and the partial-match refusal are that brief's; this one consumes the
answer.

**`R-ab2-1e`** **No schematic, an unreadable one, or one `NetExtractor` reports conflicts on: this
path contributes nothing** and R-ab2-2's takes over for the whole board. Not per part —
`ExtractionResult.Conflicts` means the extraction disagreed with itself somewhere, and taking the
uncontested half of a contested extraction is how a board ends up half-named with nothing to say
which half. Reported, with the conflicts.

**`R-ab2-1f`** A `.csch` reaches this through `SchematicPersistence`, **not** through the `.cnl`
round trip. That rule (CLAUDE.md, `check`/`explain`) is about the **elaborator**, whose reader
disagrees with the schematic's about bare words. `NetExtractor` takes the edit model directly — it is
the front half of the very round trip in question — and routing it through `CnlWriter` would be a
second extraction of the thing being extracted.

---

## 2. `R-ab2-2` — stamped: the net a user wrote on the copper

**`R-ab2-2a`** The fallback reads `LayoutShape.Net` on the **root's own shapes**, never on a sub-cell's.
This is the trap and it is worth being explicit: a land pattern is **one cell shared by every
placement of it**, so a `Net` stamped inside `C0402`'s `.clay` would put thirteen capacitors on one
net. The sub-cell's pad shapes carry `Pin` (`ChipLandPatternGenerator.cs:181`) and that is all they
may carry — pin is a property of the pattern, net is a property of the board.

**`R-ab2-2b`** **A stated net names its whole connected piece**, through the partition that already
exists. `DrcConnectivity.Extract` answers *which shapes are electrically joined* from geometry and
the stackup, with integer indices and no names — its header says so and says it is not LVS — and
`PdnRailRegions` already joins that partition to a name. So: partition the root copper, and a piece
holding any shape with a stated `Net` takes that name.

The UX this buys is the whole point: **naming one trace names the pour, the vias and everything they
reach.** A user does not annotate 80 shapes.

**`R-ab2-2c`** **A piece carrying two different stated names is a refusal, naming both and where
they are.** Two names on one piece of metal means either the artwork shorts two nets or one of the
labels is wrong, and both readings are things a user must see. Picking one — first, longest, most
frequent — produces a plausible board and buries a short. Same class as the Excellon suppression
refusal.

**`R-ab2-2d`** A pad takes the name of the piece it lands on, on its own layer. A pad on unnamed
copper stays unnamed, which is representable (`PdnPad.Net` is `string?`) and is what a board with
placement and no netlist produces today.

**`R-ab2-2e`** Net points follow: brief 1 R-ab1-1e's via points take their piece's name too, which is
what lets `PdnRailRegions` recognise an inner-layer pour it reached.

---

## 3. `R-ab2-3` — naming a net is a gesture, not a text field

`LayoutShapePropertiesViewModel.CommitNetText` (`:311`) already sets `Net` per-shape and across a
multi-select. It is correct and nobody finds it.

**`R-ab2-3a`** **Name Net…** on the layout canvas context menu, on a selected shape. It writes the
same field through the same setter — one undo entry, the existing `ApplyToEach` path, no second
writer.

**`R-ab2-3b`** The dialog **offers the names already on the board** (every distinct stated `Net`,
plus every net the schematic named where one resolved) and accepts a new one. A free-text field over
a board that already says `+3V3` is how a board acquires `+3v3`.

**`R-ab2-3c`** It says what it will reach: *"names this piece and the 34 shapes joined to it"*,
counted from R-ab2-2b's partition **before** the commit. A gesture whose blast radius is invisible
until afterwards is a gesture users stop trusting.

**`R-ab2-3d`** A selection spanning two pieces names both, and the sentence says so. A selection that
would violate R-ab2-2c — naming a piece that already carries a different name — asks, naming the
existing one.

**`R-ab2-3e`** No new persisted field, no new layer, no new shape kind. `Net` has been on
`LayoutShape` since L5 and is serialised already.

---

## 4. `R-ab2-4` — the window stops saying the wrong thing

**`R-ab2-4a`** `AvailableNets` is rebuilt from **the resolved net set**, not from `BoardNetlist`
alone (`RailRfViewModel.Import.cs:157`). A drawn board with a schematic now offers `+3V3` and `GND`
in the pick list, which is the whole reason a user drew it.

**`R-ab2-4b`** `HasNoPickableNets`' sentence — *no board netlist named any nets, click the pour
instead* — becomes **conditional**. It stays exactly as it is for a board that names nothing. It must
not be printed over a board whose nets came from a schematic, because it is then a false statement
about the model, and **nothing fails when a false sentence is printed**. This is [railRF brief 25](brief-railrf-25-series-element.md)'s R-rail25-4b
trap in its second instance, and it will be missed unless a test asserts the sentence's absence.

**`R-ab2-4c`** Click-the-pour keeps working, unchanged, on a board with names and on one without. It
is the gesture for *this copper here*, and a named board does not make it redundant.

**`R-ab2-4d`** The strip says where the names came from, in the shape brief 1 R-ab1-6c established —
*"nets from the schematic"*, *"nets stated on the artwork"*, *"nets from the board netlist"*.

---

## 5. `R-ab2-5` — a disagreement is reported, and it is not LVS

A board can carry an `.ipc` **and** resolve a schematic. Brief 1 R-ab1-3a gives the netlist
precedence for pads and R-ab1-3d defers the disagreement to here.

**`R-ab2-5a`** Where both name a refdes and pin and the **net differs**, that is reported: the
refdes, the pin, both names, both sources. Once per pair, not once per pad.

**`R-ab2-5b`** Where both name a refdes and the **position** differs by more than the pad's own
extent, likewise. Inside the pad is a rounding difference between two coordinate systems and is not
worth a line.

**`R-ab2-5c`** **Reported, never resolved, never ranked, never a refusal.** The run proceeds on
R-ab1-3a's precedence. This is a stale export or a hand edit, which are ordinary mid-design states,
and a tool that refused to solve on one would be a tool nobody runs mid-design.

**`R-ab2-5d`** It is **not LVS** and must not be named like it. `DrcConnectivity`'s header already
makes this distinction for its own class and the reasoning carries: naming this anything
LVS-flavoured invites the assumption that it extracts devices and compares a whole design, and it
does neither. It compares two files that both claim to describe one board.

---

## 6. Gate

`tests/Ui.Tests/RailRf/LayoutNetsTests.cs`.

1. **A schematic beside the artwork names the pads.** Two capacitors between `+3V3` and `GND`,
   placed by Update Layout; every pad comes back with the schematic's own name (R-ab2-1c).
2. **Port order, not pad order.** A part whose pin `"2"` binds the FIRST net — assert the pad on
   pin 2 carries it. Reversing this is the R-ab1-4 swap and it is invisible on a symmetric fixture,
   so **the fixture must be asymmetric**.
3. **A hand-placed instance takes the stamped path even with a schematic present** (R-ab2-1c) — it
   has no `SchematicId`.
4. **Conflicts suppress the whole schematic path, not part of it** (R-ab2-1e).
5. **One stamp names a whole pour.** A trace, a pour and three vias; stamp the trace, assert all
   five shapes' pads resolve (R-ab2-2b). **This is the test that says the partition is being used at
   all** — an implementation that reads `Net` off each shape individually passes every other row
   here.
6. **A sub-cell stamp does NOT leak.** Put a `Net` inside the shared `C0402` cell and assert it names
   nothing — thirteen instances, thirteen unnamed pads (R-ab2-2a). The one defect that would look
   correct on a board with one capacitor on it.
7. **Two names on one piece is a refusal naming both** (R-ab2-2c).
8. **The gesture is one undo, and it reports its reach before committing** (R-ab2-3a, R-ab2-3c).
9. **The pick list offers a drawn board's nets** (R-ab2-4a).
10. **The wrong sentence is ABSENT.** Assert `HasNoPickableNets`' text does not appear on a board
    whose nets came from a schematic, and that it still does on a board that names nothing
    (R-ab2-4b).
11. **Divergence is reported and the run still completes** (R-ab2-5a, R-ab2-5c).
12. **The shipped example is unchanged, bit for bit** — the same gate brief 1 ends on, for the same
    reason. It has an `.ipc`, and its answers must not move.

## 7. Scope

- **No automatic stamping.** §0. Update Layout writes no `Net`, now or later.
- **No back annotation.** The schematic is never written from the artwork.
- **No net on a sub-cell.** R-ab2-2a.
- **No renaming across a board.** Name Net… names a piece. A project-wide rename is a different
  feature and needs a different gesture.
- **No LVS.** R-ab2-5d.
- **No second partition.** `DrcConnectivity`/`PdnRailRegions`' walk, reused.
