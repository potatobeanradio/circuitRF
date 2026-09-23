# Brief 16 — parts placed end for end: read them, name them once, turn them all

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs16-n` · **Phase:** comparison change, feature
**Area:** `src/Design/Layout/Lvs/LvsCompare.cs` (edges, terminal check, `NetCorrespondence`),
`LvsDiagnostics.cs`, `LvsFindings.cs`, `LvsRunResult.cs`, `LvsReport.cs`, `LayoutRead.cs`;
`src/Design/Layout/Extraction/TurnedParts.cs` (reused, not changed in substance);
`src/Ui/Layout/Lvs/LayoutEditorViewModel.Lvs.cs`, `src/Ui/Views/Lvs/LvsToolView.axaml(.cs)`;
`src/Cli` (the `lvs` verb's `--json`)
**Depends on:** 7, 8, 11, 12 · **Blocks:** nothing
**Found by:** the owner, 2026-09-23 — a designer places a two-pin part 180° against its schematic,
and on a real board it is 30-50 of them at once (railRF field report 4 had 30 of 55). The owner
asked whether LVS catches it and whether it can fix them in bulk, from either side.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What LVS does today, measured

On the shipped `examples/LVS/Attenuator` (clean: 0 findings), turning the layout's resistors 180°
in memory and running `LvsRun.Run`:

| Turned | Findings |
|---|---|
| none | 0 |
| R2 | 3 — `lvs.anchor.contradicted` (warning), `lvs.device.unmatched-layout` + `lvs.device.unmatched-schematic` (errors) |
| R1, R2, R3 | 9 — the same three per part |

So LVS *notices*, and says the wrong thing three times:

1. **The diagnosis is wrong for an R, a C or an L.** Turning one end for end changes no circuit: the
   copper is identical and only the number the footprint calls "pin 1" moves. LVS reports two
   unmatched parts and a contradicted name, and the contradiction sentence reads as a mis-wiring.
   Nothing in the report says the word *turned*.
2. **The comparison contradicts the rest of the codebase.** `LvsCompare` checks every device port
   for port (`LvsCompare.cs:441-468`), and its refinement keys on terminal position for every device
   (R-lvs7-3c, `:879`). Only reduction's `ParallelKey` treats an R/C/L's net pair as unordered — and
   `TurnedParts.cs`'s header justifies itself with *"LVS already reads them that way"*, which is true
   of that one step and false of the comparison.
3. **It does not scale.** 30 turned parts is ~90 findings, and `LvsReport.MaxPerId = 20` caps each id
   at 20 — so a bulk fix built on the FINDINGS would silently miss a third of the parts.

For a two-terminal part that is NOT symmetric — a diode, an LED, a polarised capacitor — the same
three findings appear, and there it IS a real fault (the board assembles backwards). It deserves one
finding that says so, not three that describe it indirectly.

## 1. Decisions taken in this brief, and why

- **The fix is in the LAYOUT, never the schematic.** The schematic is the statement of intent LVS
  checks the artwork against; editing it to agree with a placement accident inverts the tool. For a
  polarised part it would bury a real assembly fault behind a clean report. And it costs more: a
  symbol turned 180° needs its wires re-attached and its labels flipped, where a symmetric footprint
  turned about its two lands touches no copper at all. For R/C/L the schematic edit is unnecessary
  anyway — R-lvs16-1 stops them being findings. **Out of scope, stated so it is not re-proposed:**
  any schematic-side "rotate to match" gesture.
- **Symmetric = the schematic device is R, C or L with two terminals**, and the layout device
  `CouldBe` it. Not the layout's kind: an ordinary board's placement has a `SchematicId` and no
  `PartKind`, so `DeviceTypes.OfLayout` answers `Cell` with the land pattern's directory
  (`DeviceType.cs:90-107`) — keying on the layout kind would exempt nothing on a real board. Not a
  two-terminal cell or a diode: `TurnedParts.IsSymmetric`'s line and `ParallelKey`'s, for their reasons.
- **Warning, not info and not error.** The circuit is right, so it must not fail `--severity error`.
  But the placement states the wrong pin order, and railRF, cross-probing, the placement table and
  the bill of materials all read pin 1 literally — that is how field report 4's whole board became
  one net. LVS is the one place the designer is guaranteed to look, so it must be visible.
- **One finding per part**, not one aggregate: each part cross-probes and waives through the
  existing per-finding machinery (R-lvs12-4). The panel groups them (R-lvs16-3).
- **ONE reader decides which parts are turned, and it is railRF's.** `TurnedParts.Read` already
  answers exactly this question, and LVS's `LayoutRead` already builds every input it takes — the
  same `PlacedPins.Of` pads and origins, the same `CopperPieces` partition, the same `LayoutView`,
  and the schematic's kind by `SchematicId` (`RailArtwork.cs:453`'s `KindOf`). LVS calls it; it does
  not grow a second detector. Two detectors with two tie rules WILL disagree on some board, and a
  designer told "30 turned" by railRF and "28 turned" by LVS — or pressing Turn in one window and
  seeing the other still complain — has no way to know which to believe. §1A says what each window
  owns.

## 1A. Relation to railRF — one reading, one edit, two windows

| | railRF (shipped, field report 4) | LVS (this brief) |
|---|---|---|
| Which parts are turned | `TurnedParts.Read` | **the same call**, on the same inputs |
| Which kinds | R, C, L by the schematic's kind | the same (`TurnedParts.IsSymmetric`) |
| A tie | not turned | the same — it is the same function |
| The edit | `TurnedParts.HalfTurn`, one undo step | **the same function**, extracted so both panels call it (R-lvs16-3b) |
| The button | *Turn these N in the layout* | the same words |
| The words | the user docs' *placed end for end* / *turned* | the same |
| What it does with the reading | solves from the copper's pin order and says so | compares with the pins unordered and warns |
| A reversed diode | not its question — it reads the schematic's order | `lvs.device.reversed`, an error (LVS only, by design) |

**Either window's Turn clears the other.** Both read the `.clay`: after a Turn in the LVS panel,
railRF's debounced pad read (brief railRF-28, pads follow the layout) re-reads and its note goes;
after a Turn in railRF, the next LVS run has nothing to report. Gate 5 holds this.

## 2. `R-lvs16-1` — the comparison reads a symmetric part's two terminals as unordered

- **`R-lvs16-1a`** In refinement (`Refine`'s edge build, `LvsCompare.cs:815-840`), both edges of a
  symmetric device (§1) carry the SAME port value, so its colour depends on its neighbours and not
  on which end is which. A FET, a diode and a two-terminal cell are unchanged.
- **`R-lvs16-1b`** A symmetric device's **orientation** — straight or crossed — is the one
  `TurnedParts.Read` gave its part (§1): crossed if the reading turned it, straight otherwise. The
  comparison takes it as input and decides nothing about it.
- **`R-lvs16-1c`** A crossed device votes its nets crossed in `NetCorrespondence`, and the terminal
  check (`:441-468`) accepts a symmetric device **either way round**. A turned part then produces
  **no** `terminal.wrong-net`, **no** `anchor.contradicted` and **no** unmatched pair — and neither
  does one the reading left straight on a tie, because the circuit is right whichever end is pin 1.
- **`R-lvs16-1d`** `--no-reduce` changes nothing here; reduction already treats these as unordered.

## 3. `R-lvs16-2` — what is reported

- **`R-lvs16-2a`** `lvs.device.turned`, **warning**, one per part `TurnedParts.Read` turned (which
  are, by that function's construction, placements carrying a `SchematicId`). Suggested text, in
  the railRF docs' own words:
  *"'{refdes}' is placed end for end: its pin 1 sits on the copper the schematic gives its pin 2.
  The circuit is the same either way round, but anything that reads the placement's pin order —
  railRF, the placement table, cross-probing — reads it backwards. Turn it in the layout."*
- **`R-lvs16-2b`** `lvs.device.reversed`, **error**, for an anchored TWO-terminal device that is not
  symmetric (diode, LED, polarised C, a two-terminal cell) whose terminals are all wrong straight and
  all right crossed. It **replaces** the contradicted-plus-two-unmatched trio: the anchor is kept, the
  pair stays paired, and it casts **no** net votes (a reversed diode must not drag the correspondence
  toward itself). It names both nets. Three or more terminals are out of scope (§6).
- **`R-lvs16-2c`** Call `TurnedParts.Read` in `LayoutRead`, **before reduction**, on the pads it has
  just built. That placement is what keeps the list per **placed part**: with reduction on, ten
  decoupling caps between one rail and ground merge into one device, and a turned member is
  invisible to anything that reads after the merge. Pass the reading's orientation to the comparison
  (R-lvs16-1b) through the device, not through a side table keyed by path.
- **`R-lvs16-2d`** `LvsRunResult` carries the reading's **full** `TurnedPart` list unchanged (refdes,
  root instance index, both land positions), so `TurnedParts.HalfTurn` applies to it directly. It is
  exempt from `LvsReport.MaxPerId` by construction: the cap trims findings, and nothing may read the
  list from the findings.
- **`R-lvs16-2e`** Register both ids in `LvsFindings.cs`' catalogue (the test that holds that list
  shut will insist).

## 4. `R-lvs16-3` — the fix, one gesture for all of them

- **`R-lvs16-3a`** The LVS panel shows the turned parts as ONE group with its full count (not the
  capped count) and a button **Turn these N in the layout** (**Turn R7 in the layout** for one).
  A checkbox list lets the designer leave parts out. All selected parts are turned in **one undoable
  step** through `LayoutEditorViewModel.ReplaceInstances` — the route railRF's Turn already uses when
  a layout window is open, and the LVS panel always has one. Then LVS re-runs.
- **`R-lvs16-3b`** Reuse, do not re-derive — the railRF column of §1A: the edit is `TurnedParts.HalfTurn` (it turns about the
  midpoint of the two lands, `T' = P1 + P2 − T` — `src/Design/RESOLVED.md` records why the footprint
  origin is the wrong centre). Extract railRF's edit-list build (`RailRfViewModel.TurnedParts.cs`,
  `TurnParts`) into one function both panels call, rather than copying it; a stale instance index is
  refused exactly as railRF refuses it.
- **`R-lvs16-3c`** A `device.reversed` part gets a per-part **Turn** only, never the bulk button and
  never checked by default: either the part or the copper may be the wrong half, and only the
  designer knows which. Offer it only where `HalfTurn` maps each land onto the other's position
  (within one DBU); otherwise say that a turn would not land it and show no button.
- **`R-lvs16-3d`** A part inside a sub-cell (not a root placement) is reported but not turned from
  here — `TurnedPart.InstanceIndex` is a root index. The finding says to turn it in that cell.
- **`R-lvs16-3e`** The CLI stays read-only (cli.md §19's rule for `lvs`): `--json` carries the list
  from R-lvs16-2d, which is everything an agent needs to write the `.clay` itself. The MCP `lvs`
  tool returns the same.

## 5. Gates

1. **The measurement in §0, inverted**: the Attenuator with R1, R2, R3 turned has **no error**, and
   exactly three `lvs.device.turned` warnings naming them.
2. **Every existing LVS test is unchanged**, including `AContradictedAnchorIsOneFindingAndThenACleanMatch`
   (a renamed part is still a contradiction — the turn must not swallow it) and the FET
   drain/source test (a three-terminal device is untouched).
3. **The cap does not reach the fix**: 25 turned parts on a synthetic board → 25 in
   `LvsRunResult`'s list and in the panel's count, although the findings are capped at 20.
4. **Reduction does not hide one**: three caps in parallel, one turned → that one is named.
5. **One reader, and the windows agree**: a comment-stripped scan finds no turned-part decision in
   `src/Design/Layout/Lvs` outside its call to `TurnedParts.Read`; and on a synthetic board (no private
   data), after the LVS panel's Turn, `RailArtwork`'s reading of the same `.clay` turns nothing —
   and after railRF's Turn, LVS reports no `device.turned`.
6. **A reversed diode** is one `lvs.device.reversed` error and nothing else.

## 6. Tests (minimal — one per claim)

1. The Attenuator, three resistors turned: no errors, three `device.turned` (gate 1).
2. A turned R the reading leaves straight (a tie) is neither a warning nor an error.
3. 25 turned parts: full list despite the cap (gate 3).
4. Parallel caps with one turned member: named (gate 4).
5. The source scan, and one Turn in each window clearing the other (gate 5).
6. A reversed diode: one error, the pair kept, no unmatched (gate 6).
7. The panel's Turn: one undo step; undo restores every instance; the re-run is clean.

## 7. Scope

- **No schematic-side rotation**, bulk or single (§1).
- **No parts with three or more pins.** A 180° turn of a SOT-23 does not put its pads back on its
  own lands; that is a pin permutation, and a different question.
- No new pin-equivalence declaration for cells or parts; the symmetric set is R, C, L exactly as
  `ParallelKey` and `TurnedParts.IsSymmetric` have it. A declared-equivalence field would be its own brief.

## 8. On completion

Findings in `src/Design/RESOLVED.md` (the panel half in `src/Ui/RESOLVED.md`), never in any
`CLAUDE.md`. Correct the sentence in `TurnedParts.cs`' header that says LVS already reads R/C/L as
unordered, so it names the comparison as well as `ParallelKey`. Add an as-built note to
`docs/design/lvs.md` §6.2 step 3 (the terminal position now holds for every device but the symmetric
two-terminal ones). Add both ids to the findings table in `docs/user/src/reference/lvs.md`, and the
`turned` array to cli.md §19's `--json` description. Do not regenerate `docs/user`; that happens at
the end of the series.
