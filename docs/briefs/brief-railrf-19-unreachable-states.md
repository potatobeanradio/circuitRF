# Brief 19 — nothing in this window may become unreachable

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail19-n` · **Phase:** corrective
**Area:** `src/Ui/RailRf/RailRfViewModel.Import.cs`, `.Solve.cs`, `.Selection.cs`,
`src/Ui/RailRf/RailLayoutOverlay.cs`, `src/Ui/Views/RailRf/RailRfWindow.axaml`
**Depends on:** 1-18 · **Blocks:** nothing
**Found by:** a first-time designer's pass, 2026-09-20

---

## 0. What this brief delivers

Three defects from one pass, and the first of them is the worst thing in the window: **one mis-click
disables Run, Compare and Export permanently, with no way back.** The other two are the same
complaint from two directions — a list you can select in that shows you nothing.

| | Defect | Reported as |
|---|---|---|
| `R-rail19-1` | a rail can be added and **never removed** | picked GND, could not run, could not undo it, project stuck |
| `R-rail19-2` | selecting a net in the pick list highlights nothing | *"when select pick a net, it would be expected to highlight the net in the layout too"* |
| `R-rail19-3` | selecting a breakdown row shows nothing on the board | *"a locator on the board when searching the breakdown list would be useful"* |

**Each ships with a test that fails at HEAD.**

---

## 1. `R-rail19-1` — a rail can be added and never removed

### What is wrong

`RailRfViewModel.PickRail` (`src/Ui/RailRf/RailRfViewModel.Import.cs:211`) does
`_document.Rails.Add(rail)`. **Nothing anywhere removes one.** There is no command, no menu row, no
context menu, no keyboard route. `AvailableNets` is every net in the board netlist, so `GND` is in
the list and one click makes it a rail.

Then `RefreshRunGate` (`Solve.cs:325`) evaluates `UnreferencedRail()`, which walks every rail other
than the selected one and returns the first with no reference layer. A rail added by mistake has
none. The gate resolves to:

> *"Rail 'GND' states no reference layer, so there is nothing to return current through. The rail set
> is solved together, so this one blocks the run as well — pick it in the rail selector above and
> confirm its reference."*

`CanRun` goes false, and with it `RunCommand`, `AccuracyCommand`, `PlaneResonancesCommand`,
Compare and Export. The refusal names a remedy — confirm its reference — that the user does not
want and that would leave a meaningless rail in the document forever.

### Why it matters more than it looks

The gate is *correct*. The rail set genuinely is solved together, and an unreferenced rail genuinely
does block it. What is missing is the other door. **A window that can enter a state it cannot leave
is not a window with a bug in one control; it is a window that can lose a session's work**, and the
designer hit it within minutes of opening the shipped example.

### `R-rail19-1a` — rails are removable

A **Remove rail** control beside the rail selector, mirroring the Sources and Loads lists, which
already carry add and remove buttons with exactly this shape
(`RailRfWindow.axaml:833`). Removing a rail removes its sources, loads, targets, aggressors and
parts with it, because they are its and nothing else references them.

### `R-rail19-1b` — the refusal names BOTH doors

The `UnreferencedRail` sentence gains its second remedy: confirm its reference, **or remove it**.
A refusal that names one of two exits is a refusal that traps whoever wanted the other one.

### `R-rail19-1c` — the return is IDENTIFIED from the artwork, and never from a name

The obvious fix is to drop `GND` from the list. Two forms of that were considered and both are
guesses; the third is not.

**Matching the NET's name** — `GND`, `VSS`, `AGND`, `0V`, `VSSA` — is a guess about a string the
user owns. A board may have several returns, a split analogue and digital return, or a rail named
`GND` that is genuinely the subject.

**Matching the LAYER's name in the `.ctech`** (owner's question, 2026-09-20) is the same guess
wearing different clothes, and it is worse in one specific way: the pick list holds **nets**, built
from the board netlist (`RebuildAvailableNets`, `Import.cs:134`), while layer names live in the
technology. The two share a string in the shipped example — layer 2 is named `GND` and so is a net —
and are otherwise unrelated objects. It also does not work on the technologies we ship:
`pcb-4layer_FR-4_62mil_1oz` calls its planes **"Inner 1"** and **"Inner 2"**, which no ground-name
rule catches. And the `.ctech` layer table declares nothing that would help — `Purpose` reads
`"drawing"` on every shipped layer, and `Interchange.PcbLayerName` is a Gerber mapping, not a role.

**What does work: the net on the confirmed reference layer.** railRF already makes the user affirm a
reference layer rather than inferring one (Q-8), so by the time the question matters the answer is
available *from the artwork*:

1. walk the copper on the confirmed reference layer — `PdnRailRegions.Walk`, which exists and is
   the same walk R-rail19-2a's preview uses and caches;
2. read which net the board netlist's pads on that copper belong to.

That is a measurement, not a heuristic. It returns `GND` on the shipped example, and it returns
`Inner 2`'s actual net on a board where the plane is not called anything suggestive.

### `R-rail19-1d` — flagged, not hidden, and only once the reference is confirmed

**Before the reference is confirmed, nothing is filtered or flagged.** railRF does not know yet, and
acting as if it did is the guess this requirement exists to avoid.

**After it is confirmed**, the reference net's row in the pick list is **marked** — *"the reference
return"* — and is still selectable. Picking it is refused with that sentence and the remedy.

Marked rather than removed, for the reason the pick card's own absence already teaches: a user who
cannot find `GND` in the list and is told nothing is in exactly the position the dead Run button put
him in. A row that says why is an answer; a missing row is a second mystery.

### `R-rail19-1e` — an explicit reference role in the `.ctech` is NOT in scope

Worth recording as the durable answer, and worth not doing here. A layer could carry a declared
**reference-plane role** beside `Interchange.PcbLayerName`, which would make this *declared* data
rather than inferred and would also let railRF propose the reference layer with a better reason
than it has today. That is a change to the technology model, it touches every `.ctech` reader and
writer and the technology editor, and it is not what a stuck Run button needs.

### Gate

`tests/Ui.Tests/RailRf/RailRemovalTests.cs`.

1. Load the shipped example, pick `GND`, assert `CanRun` false — **this is the trap, reproduced.**
2. Remove the rail, assert `CanRun` true again and the document holds exactly the rails it started
   with. **Fails at HEAD: there is nothing to call.**
3. Removing a rail removes its sources, loads, targets and aggressors.
4. The unreferenced-rail sentence contains both remedies.
5. **The reference net is identified from the artwork.** On the shipped example, walking the
   confirmed reference layer's copper and reading the netlist returns `GND` (R-rail19-1c). On a
   synthetic board whose plane layer is named `Inner 2` and whose return net is named `RTN`, it
   returns `RTN` — the case every name rule fails.
6. **Flagged only after confirmation.** The row is unmarked before the reference is confirmed and
   marked after (R-rail19-1d).
7. **Picking it is refused, with the sentence and the remedy** — and the rail is not added.
8. `AvailableNets` still contains `GND`, before and after. The filter that must not exist.

---

## 2. `R-rail19-2` — selecting a net highlights nothing

### What is wrong

`OnSelectedNetChanged` (`Import.cs:151`) notifies the pick command and the button caption, and
nothing else. The board does not move, nothing lights up, and the only way to see what a net IS is
to commit it as a rail and look at the result.

That is backwards. **The pick is the moment a user needs to check they picked the right thing**, and
on a board with `+3V3`, `+3V3_A` and `VDD_IO` in the list, the name is not enough.

### `R-rail19-2a` — a preview highlight, on selection

Selecting a row highlights that net's copper on the board, through the walk that already exists:
`PdnRailRegions` walks everything galvanically connected to a pick, through vias and across layers,
and brief 8's overlay already draws a rail's own copper. The preview uses both; it does not write a
second walk.

### `R-rail19-2b` — a PREVIEW, and it says so

Drawn in the overlay's preview role, distinct from a committed rail's highlight, and cleared when
the selection clears or the card goes away. A preview that looks identical to a committed rail is a
preview that makes a user think they already pressed the button.

### `R-rail19-2c` — the walk is not paid for twice

The walk is not free on a large board. It is computed once per selection change, cached by net
name for the lifetime of the loaded board, and cleared when the artwork changes
(`NotifyArtworkChanged` already exists for exactly this). Arrow-keying down a list of two hundred
nets must not re-walk two hundred times.

### Gate

`tests/Ui.Tests/RailRf/PickNetHighlightTests.cs`.

1. Selecting a net publishes a preview region set that is non-empty and matches
   `PdnRailRegions.Walk`'s own answer for that net. **Fails at HEAD: nothing is published.**
2. The preview role differs from the committed-rail role (R-rail19-2b).
3. Clearing the selection clears the preview.
4. Selecting the same net twice walks once (R-rail19-2c).

---

## 3. `R-rail19-3` — the breakdown list has no locator

### What is wrong

The drop breakdown is the answer Q0 exists for — *"22.7 mV, 45 %, 26.5 mm of 0.209 mm BOT copper"* —
and there is no way to find out **where on the board that copper is.** The parts table already
highlights a selected row's part; the breakdown list, which is the more valuable of the two, does
not.

### Why it is cheap

`PdnBreakdownRow.GroupKey` exists for this and says so:

> *"The group key this row came from, so a caller can map a row back to the copper it names (the
> drop map, brief 8)."* — `src/Engine/Pdn/PdnBreakdown.cs:55`

The mechanism is written, published and already used by the drop map. What is missing is the
selection wiring.

### `R-rail19-3a` — select a row, find the copper

Selecting a breakdown row highlights the copper of its group and brings it on screen. **Brings it on
screen**, not merely highlights: a 0.2 mm run on a 30 x 20 mm board at fit zoom is three pixels, and
a highlight the user cannot find has answered nothing. Same camera move Update Layout makes when it
places instances off screen.

### `R-rail19-3b` — a row with no copper says so

Some rows are not copper: the source's own series resistance, a part's ESR, the reference return.
Selecting one clears the highlight and says what it is, rather than leaving the previous row's
copper lit — which would be a locator pointing at the wrong thing, worse than none.

### Gate

`tests/Ui.Tests/RailRf/BreakdownLocatorTests.cs`.

1. On the shipped example, selecting the top breakdown row publishes a highlight whose cells are
   the group's own. **Fails at HEAD.**
2. The viewport moves to contain it (R-rail19-3a).
3. Selecting the source's series-resistance row clears the highlight and states its kind
   (R-rail19-3b).
4. Selecting a row, then another, leaves no residue of the first.

---

## 4. Scope

- **No new refusal vocabulary.** Every sentence here is an existing one gaining a clause.
- **No second connectivity walk.** R-rail19-2a.
- **No name-based net filtering.** R-rail19-1c, and it is the point rather than an omission.
