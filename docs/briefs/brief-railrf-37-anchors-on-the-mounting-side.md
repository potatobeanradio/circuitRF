# Brief 37 — anchors on the mounting side, and a board that brings its own netlist

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail37-n` · **Phase:** behaviour change (owner decision 2026-09-28)
**Area:** `src/Design/RailRf/RailPortAnchor.cs`, `src/Design/Layout/Pdn/PdnAttachments.cs` (`Resolve`),
`src/Design/Layout/Extraction/Regions.cs` (`Walk`'s anchor seeds, the ambiguity refusal from brief 34),
`src/Design/RailRf/RailDcRun.cs` + `RailSeriesPartition.cs` (the "names no terminals" refusal),
`src/Ui/RailRf/RailRfViewModel.Board.cs` (`PlaceSource`/`PlaceLoad`), `RailRfViewModel.Import.cs`,
`src/Design/Layout/Interchange/BoardNetlistFile.cs`
**Depends on:** 34, 35, 36 (the gates need the board to open) · **Blocks:** nothing
**Evidence:** the round-9 field-report workspace, held by the owner.
**Rule:** the board, its customer and the reporter must not be named anywhere in the repo.

---

## 0. What was reported

1. Both anchors of the designer's rail were refused by brief 34's rule: the source over `Top Copper`
   (net named) and an unnamed inner plane; the load over Top, the inner plane and a third inner
   layer. His request: resolve a source or load to the TOP or BOTTOM copper, where parts are mounted.
2. After re-importing the Gerbers, his two series parts (FB8, R168, added by refdes) were refused with
   "Series element FB8 names no terminals" and the parts table showed both as *not placed*.
3. The `.crail`'s `BoardNetlistRef` names a CAD tool's **expanded part list**, which is not a netlist;
   railRF reports it as unreadable. **The IPC-D-356A netlist the same CAD tool wrote sits in the Gerber
   folder he imported from** (`<job>.ipc`), and it carries a net name, refdes and pin for every pad.
   With it, the inner planes would have had net names, both anchors would have resolved by pad, and
   the series parts' terminals would have been known — most of this report would not have happened.

## 1. `R-rail37-1` — anchors resolve on outer copper (owner decision)

The rule, in this order:

1. An anchor that names a layer (`RailPortAnchor.Layer`) uses it — unchanged.
2. A pad anchor, or a coordinate that lands on a PLACED PART'S PAD, uses that pad's land layer — the
   part's mounting side (`RailPartSides`, `PlacedPin.Layer`). A through-hole pad still reaches every
   layer through its own barrel, which the connectivity walk follows.
3. A bare coordinate considers ONLY the outer conductor layers (the stackup's first and last conductor).
   Inner layers are never candidates unless the anchor names one.
4. Copper on BOTH outer layers under a bare coordinate (a through-via pad, a part on each side): take
   **Top** and say so in a note on the anchor ("resolved to Top Copper; Bottom Copper also carries
   copper here — set the anchor's layer to use it").
5. Copper on neither outer layer: refused, naming the inner layers that do carry copper there and the
   `.crail` spelling that selects one.

Brief 34's two-nets refusal survives only for the case rule 4 does not settle by itself — it never
fires for inner-vs-outer. The window's right-click placement and pour-click route follow the same rule
(the top-most SHOWN outer layer), so hiding Top still means "not that one".

## 2. `R-rail37-2` — find the netlist that is sitting beside the Gerbers

- On Import Board (and when a `.crail` names a netlist that does not read), look in the Gerber source
  folder for a file `BoardNetlistFile` reads (by content, not by extension) and OFFER it with its path.
- The "did not read" refusal for a file that is recognisably a part list or BOM, not a netlist, says
  that in its first line and names the found IPC-D-356 file when there is one. No CAD-tool names in the
  text or in the code (repo rule) — "a CAD tool's part list" is enough.

## 3. `R-rail37-3` — series terminals after a re-import

Reproduce on the field-report workspace: re-import the Gerbers, then run. Find why FB8/R168 became
*not placed* (the placement file still lists both at their coordinates) and why a refdes-only series
element no longer derives its two terminals. Expected: a two-terminal part whose placement and
footprint are known resolves its own two pads as the element's terminals (brief 35's partition), with
no pin typed. Where a part really is not placed, the refusal says THAT ("FB8 is not placed on the
board — …"), not that it names no terminals.

## 4. `R-rail37-4` — the turned-part notice after a manual rotate

He rotated FB8 by hand and the "placed at 180°" notice stayed. With brief 36 the re-read that clears it
is fast; confirm the notice is recomputed from the rotated placement (live session AND file), and that
Turn is not offered for a part the pending re-read may already have fixed (`CanTurnParts`).

## 5. Gates

1. The field-report rail solves after rules 1–4 with no layer typed, source and load on Top.
2. Every existing fixture and the shipped Power Rail example: netlists identical where no anchor was
   inner-vs-outer ambiguous (brief 30's `fx` runner).
3. Brief 34's two-supply fixture still refuses where both candidates are OUTER layers of different nets
   and rule 4 does not apply (it does: Top wins, with the note) — decide which, write it down, and have
   the test say it.

## 6. Tests (minimal)

Anchor over Top + inner plane → Top, no refusal; over Top + Bottom → Top with the note; pad anchor on a
bottom-side part → Bottom; inner-only copper → refused naming the inner layers; the netlist offer finds
an IPC-D-356 file beside the Gerbers; a refdes-only series part on a placed two-pin footprint resolves
both terminals. `--filter` on the classes touched, never the whole of `Ui.Tests`.

## 7. On completion

`src/Design/RESOLVED.md` (window half in `src/Ui/RESOLVED.md`), never any `CLAUDE.md`. Update the
railRF user doc's anchor section in its source (no DocGen run — the owner regenerates at the end of the
series).
