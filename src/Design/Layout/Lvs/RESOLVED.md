# `src/Design/Layout/Lvs/` — findings

---

## An LVS waiver key must NOT reuse `LvsFinding.Key` (brief-lvs-12-gui.md R-lvs12-4b)

`LvsFinding.Key` is `DrcEngine.KeyFor`'s form: id, objects, **and the marker's exact box**. Its own
doc comment said "brief 12 consumes it"; brief 12 says the opposite, and the difference is the whole
point of R-lvs-53.

- A **DRC** waiver names a *place*. Moving the shape stops it applying, correctly — the place no
  longer exists.
- An **LVS** waiver names a *relationship*: "R7's pin 2 is deliberately not connected". That
  survives moving R7 across the board, and it stops being true the moment the **schematic** changes.

Keying an LVS waiver on a box would be wrong in both directions at once: silently un-waived on every
re-route, and kept alive across the schematic edit that invalidated it. So `LvsWaiverKey.For` is a
second, independent key — `(id, the schematic-side identity, the terminal)` — read off the
diagnostic's **typed arguments**, because the id is the contract and the sentence is not (R-lvs8-2a).
`LvsFinding.Key` is kept and still used, for what it is actually good at: identifying which row's
marker is selected.

**It is an argument PREFERENCE ORDER, not a per-id table.** `LvsReport` already holds the table
saying which argument of which id names an object; a second copy here would be the same fault twice,
drifting silently because nothing compares them. The order is
`schematicPath, path, nets, net, designator`, then the finding's own `Objects`, then the id alone
for a run-level line — the designer's own name for the object, on the schematic side wherever there
is one, and never a coordinate.

## The waiver is applied in `LvsRun`, not in either surface

Both the CLI and the panel honour waivers because `LvsRun.Run` applies `layout.LvsWaivers` to the
findings before returning — the same argument that put the whole comparison behind one entry point.
A sub-cell's findings arrive already marked against **its own** `.clay`, which is right: a waiver is
a statement about the drawing it is stored on.

**The run still writes nothing** (R-lvs12-4g, and `check`'s R-aut4-6). It reads the list and hands
back marked findings; the list on the document is untouched, which is what keeps a comparison usable
on a read-only tree and on a workspace another process has open. There is deliberately no `--waive`:
a waiver is a deliberate, reasoned act performed beside the thing being waived.
