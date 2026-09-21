# Brief 12 — the panel, the markers, cross-probing, and waivers

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs12-n` · **Design note:** [`lvs.md`](../design/lvs.md) §8.2, §8.3
**Area:** `src/Ui/Views/Lvs/` (new), `src/Ui/Layout/Lvs/` (new),
`src/Ui/ViewModels/Dock/LvsTool.cs` (new), `src/Design/Layout/Lvs/LvsWaiver.cs` (new),
`src/Design/Layout/LayoutModel.cs`, `src/Render/`
**Depends on:** 8, 11 · **Blocks:** 15

---

## 0. What this brief delivers

The results panel, markers on the canvas, **cross-probing between the two views**, and waivers.

Cross-probing is the feature that makes LVS usable rather than merely correct, and the
correspondence brief 7 returns is what makes it possible: once LVS has matched `R7` to `R7`,
selecting one selects the other.

---

## 1. `R-lvs12-1` — the panel is the DRC panel's pattern, not a new one

**`R-lvs12-1a`** A dock tool beside `DrcTool`, the same shape: a list, click to zoom to the
marker, severity grouping, a run button, a summary strip.

**`R-lvs12-1b`** The strip states what the run actually did — device counts per side before and
after reduction, net counts, the **technology by name**, and the reduction mode. A workspace with
two processes has a default that may not be the one the designer has in mind; naming it costs
nothing and is the whole mitigation.

**`R-lvs12-1c` The panel calls `LvsRun.Run`, the same function the verb calls, with the same
arguments.** Brief 11 `R-lvs11-1d` is the gate. No view model computes a finding.

**`R-lvs12-1d` LVS never blocks editing.** It runs on demand, reports, and gets out of the way —
R16b's rule for DRC, for the same reason: an editor that fights the user while they work is worse
than one that never checks. No live-as-you-type LVS, in this brief or any other.

**`R-lvs12-1e`** Export offers to run it, as DRC's export gate does — a checkbox, default **off**
for LVS rather than on, because LVS needs a schematic and an artwork-only export is a legitimate
thing to do. Off with the box visible is not the same as absent.

---

## 2. `R-lvs12-2` — markers

**`R-lvs12-2a`** Drawn on a system layer above the geometry, the same superimposed mechanism DRC
markers and the mesh viewer use. Built once, used three times.

**`R-lvs12-2b`** The marker rings come from the finding (brief 8 `R-lvs8-2c`); nothing here
recomputes geometry.

**`R-lvs12-2c`** A short's marker is the **join**, not the net (brief 8 `R-lvs8-4c`). A marker
covering a board-wide pour points at nothing.

**`R-lvs12-2d`** An open draws **one marker per island**, and selecting the finding zooms to fit
all of them — the whole point of an island report is seeing the pieces relative to each other.

---

## 3. `R-lvs12-3` — cross-probing, both directions

**`R-lvs12-3a`** Selecting a finding selects its objects **in both open views**: the layout
instance and the schematic component.

**`R-lvs12-3b`** Selecting a part in either view highlights its counterpart, where the last run
matched them. **Where it did not, the panel says why** — unmatched, or matched by symmetry and
therefore arbitrary — rather than silently highlighting nothing.

**`R-lvs12-3c` `lvs.match.by-symmetry` must be visible at the point of cross-probe**, not only in
the report (brief 7 `R-lvs7-4c`). A user clicking `C7` in the schematic and being shown a
capacitor the drawing calls `C9` will believe the tool is wrong unless it says the pairing was
arbitrary.

**`R-lvs12-3d`** The correspondence is a **snapshot of the last run** and goes stale on the next
edit. The panel marks itself stale on any document change and says so; it does not re-run, and it
does not silently highlight against a result that no longer describes the design.

---

## 4. `R-lvs12-4` — waivers

**`R-lvs12-4a`** Per finding, persisted on the **`.clay` that was checked**, visible, with a
reason the UI asks for. `DrcWaiver`'s rules verbatim, including that a waived finding is **still
reported** and merely not counted.

**`R-lvs12-4b` The key is the CORRESPONDENCE, not a bounding box** — the one place this departs
from DRC, deliberately. A DRC waiver keys on the marker's exact bbox so that moving the shape
stops the waiver applying: a DRC waiver names a *place*. An LVS waiver names a *relationship* —
*"R7's pin 2 is deliberately not connected"* — which survives moving R7 and **should stop applying
when the schematic changes**.

**`R-lvs12-4c`** The key is (finding id, the **schematic-side** identity, the terminal). Stable
under every layout edit; correctly invalidated by a schematic edit.

**`R-lvs12-4d`** A waiver whose key no longer matches anything is **listed and removable by a
human who recognises it**, carrying the finding's text at the time of waiving — `DrcWaiver`'s
`RuleName` field and its reasoning, unchanged.

**`R-lvs12-4e`** Waivers persist additively on `LayoutView` beside `DrcWaivers`, nullable, omitted
when empty, **no `FormatVersion` bump**.

**`R-lvs12-4f`** The CLI **honours** waivers and does not create them (brief 11 §7). A waiver is a
deliberate, reasoned act performed beside the thing being waived.

**`R-lvs12-4g` Writing a waiver is not the run writing** — and the distinction has to be explicit,
because note §1.3 says LVS writes nothing and this section persists something. A waiver is a
**user's edit of their own document**, made through the ordinary undo stack and saved when they
save, exactly as a DRC waiver is. The **run** still writes nothing: it reads waivers from the
`.clay` and never writes them back, which is `check`'s own rule (R-aut4-6) and is what keeps a run
usable on a read-only tree and on a workspace another process has open.

---

## 5. `R-lvs12-5` — the firewall

**`R-lvs12-5a`** Everything in `src/Ui/` here consumes a finished `LvsRunResult`. No extraction,
no comparison, no geometry above the wall.

**`R-lvs12-5b`** Marker drawing is `src/Render`'s, with the overlay types the editors already
fill in. `src/Render` draws; it does not edit, and it does not decide what a finding is.

**`R-lvs12-5c`** `tests/Firewall.Tests` is unchanged and must stay passing: nothing in
`src/Design/Layout/Lvs/` references a UI framework.

---

## 6. Gate

`tests/Ui.Tests/Lvs/LvsPanelTests.cs`.

1. **The panel's result equals the verb's**, object for object, on the six-fault board
   (`R-lvs12-1c`). The same gate brief 11 states, asserted from this side.
2. **Click a finding, the canvas zooms to its marker**; an open zooms to fit **all** its islands
   (`R-lvs12-2d`).
3. **Cross-probe both directions** on a matched part; **an unmatched part says why**
   (`R-lvs12-3b`).
4. **A by-symmetry pairing is announced at the cross-probe**, not only in the list
   (`R-lvs12-3c`).
5. **The panel goes stale on an edit** and says so, and does not highlight against the old result
   (`R-lvs12-3d`).
6. **Waiver round trip.** Waive a finding with a reason, save, reload: still waived, still listed,
   still reported, not counted (`R-lvs12-4a`).
7. **A waiver survives moving the part** and **stops applying when the schematic changes**
   (`R-lvs12-4b`, `c`). Two tests, and they are the reason this key differs from DRC's.
8. **An orphaned waiver is listed with its original text and is removable** (`R-lvs12-4d`).
9. **A `.clay` with no waivers re-serializes byte for byte** (`R-lvs12-4e`).
9b. **A run writes nothing, waivers or not.** Run the panel over a read-only workspace holding
    waivers; they are honoured and no file mtime changes (`R-lvs12-4g`).
10. **The CLI honours a waiver written in the GUI** and reports it as waived (`R-lvs12-4f`).
11. **Firewall** unchanged (`R-lvs12-5c`).
12. **Export gate checkbox defaults off and is present** (`R-lvs12-1e`).

## 7. Scope

- **No live checking** (`R-lvs12-1d`).
- **No auto-repair.** The panel reports; it does not offer to fix. Note §11.
- **No waiver creation from the CLI.**
- **No second result type.** The panel binds `LvsRunResult`.
