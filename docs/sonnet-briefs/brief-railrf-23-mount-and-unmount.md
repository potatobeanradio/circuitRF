# Brief 23 — mount and unmount, and comparing against the run you just did

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail23-n` · **Phase:** feature
**Area:** `src/Design/RailRf/RailPart.cs`, `RailPartResolver.cs`, `RailDocumentIo.cs`,
`src/Design/RailRf/RailComparison.cs`, `src/Ui/RailRf/RailPartRowViewModel.cs`,
`RailRfViewModel.Parts.cs`, `.Compare.cs`, `src/Ui/RailRf/RailLayoutOverlay.cs`
**Depends on:** 1-18 · **Blocks:** [footprint 5](brief-footprint-5-power-rail-example.md)
**Found by:** a first-time designer's pass, 2026-09-20 — the best idea in the report

---

## 0. What this brief delivers

Two requests that are one feature:

> *"how would you remove the placed part on the layout, for example to depopulate and resimulate?"*
> — answered today with *"you'd have to go to the layout file and delete it"*

> *"it could be also of interest to save the result of a given iteration, then unmount part and
> resimulate to compare the Z curve. avoiding deleting the part in the layout make
> mounting/unmounting easier. for 'what if' investigation."*

Depopulating a board is the commonest what-if in power integrity, and today it costs an edit to the
artwork — which is destructive, is not what the designer means, and loses the part's mounting
inductance so it cannot be put back the way it was.

---

## 1. `R-rail23-1` — a part is mounted or it is not

### `R-rail23-1a` — the flag

`RailPart.Mounted`, a `bool` defaulting to **true**, beside `Refdes`, `PartNumber`,
`MountingInductanceHenries` and `Origin` (`src/Design/RailRf/RailPart.cs:40`). Persisted in the
`.crail`. Absent means mounted, so every existing document reads as it does now.

### `R-rail23-1b` — an unmounted part contributes nothing, and is still a row

It is excluded from the frequency model exactly as a deleted row would be, and it **stays in the
parts table**, greyed, with its part number, its position and its computed mounting inductance
intact. That is the whole difference from deleting it: the number the artwork gave it survives, so
putting it back costs one click and gives the same answer as before.

### `R-rail23-1c` — the artwork still draws it

The board shows the part where it is, drawn as unmounted. **The `.clay` is not touched.** railRF
shows the board; the way to change the geometry is to open the layout and edit it there
(the read-only rule from 2026-09-18), and depopulating is not a change to the geometry — it is a
statement about what is fitted to it.

### `R-rail23-1d` — it is not the same as a part with no model

A row whose part number does not resolve is *unresolved* and already reported as such. An unmounted
row resolves perfectly well and is deliberately absent. Two states, two spellings, and the parts
table must not collapse them — an unresolved part is a data problem and an unmounted part is a
design question.

### `R-rail23-1e` — the DC answer too, where it applies

A decoupling capacitor carries no DC current so unmounting one changes no drop. Say nothing about
it rather than printing an unchanged number as though it were a finding. **If
[brief 25](brief-railrf-25-series-element.md) lands**, unmounting a *series* element is a different
matter entirely — it opens the rail — and that case is refused with the reason rather than solved
as an open circuit.

---

## 2. `R-rail23-2` — the gesture, in three places

**`R-rail23-2a`** A checkbox in the parts table's own row. That is where the designer was looking.

**`R-rail23-2b`** The board's context menu on a part: **Unmount** / **Mount**. This is the one he
actually asked for — he was looking at the layout when he asked how to remove a part — and the
board's context menu already exists and is already rebuilt per opening
(`RailRfWindow.axaml`'s `OnBoardContextMenuOpening`).

**`R-rail23-2c`** Multi-select, from both. *"Unmount these four and re-run"* is the real gesture;
four separate clicks and four re-solves is not.

**`R-rail23-2d`** Unmounting is an **edit**, so it goes through `QueueResolve` like every other
committed row edit and the Fast model re-solves. It is not a separate "apply" step.

---

## 3. `R-rail23-3` — compare against the run you just did

### What exists

`RailComparison` compares **two `.crail` documents** (§2.9's Q3, brief 16), and the Compare dialog
asks for a second file. The README says so and says why there is no second board shipped: the
comparison is about the reader's own re-layout.

### What is missing

The designer's loop is *save the result, change one thing, re-run, compare* — **within one
document**. There is nothing to point Compare at, because the other side of his comparison is a run
that no longer exists.

### `R-rail23-3a` — keep the previous run

railRF holds the last completed result for the selected rail as a **baseline**, taken automatically
when a new run starts. One deep, not a history: the question is *what did that change do*, and a
list of twelve past runs is a different feature with a different UI.

### `R-rail23-3b` — Compare takes it as a side

The Compare dialog gains **"the previous run"** beside "another `.crail`". Everything downstream is
`RailComparison`'s and unchanged — the same report, the same per-part table, which the README
already calls the half worth looking at first.

### `R-rail23-3c` — the report says what differed in the INPUTS

A comparison of two runs of one document must name what changed between them, or the reader is left
diffing curves to infer it. *"C10 unmounted"* at the top of the report is the whole point. This is
cheap because the baseline can carry the part list it was run with.

### `R-rail23-3d` — pin it deliberately

A baseline taken on every run is lost as soon as you run twice. One control — **Pin this result** —
holds a baseline across further runs, so the loop *"unmount, run, unmount another, run"* compares
each against the fitted board rather than against the previous cut.

---

## 4. `R-rail23-4` — how this differs from Q2's removal ranking

Worth writing down, because they look alike and are not.

**Q2 already re-solves the whole sweep once per part and reports what deleting it would cost.** It
is a *ranking* — automatic, exhaustive, one part at a time, and it answers *which capacitors are
earning their place*.

This is a *what-if* — manual, arbitrary combinations, kept across runs, and it answers *what
happens if I do this to my board*. Q2 cannot answer that: unmounting three parts together is not
the sum of unmounting each, which is exactly why the designer wants to try it.

**`R-rail23-4a`** They must not be merged, and Q2's ranking is computed on the **mounted** set.
A ranking that included unmounted parts would report what removing an absent part would cost.

---

## 5. Gate

`tests/Ui.Tests/RailRf/MountUnmountTests.cs`,
`tests/Design.Tests/RailRf/RailPartMountedTests.cs`.

1. **Round trip.** `Mounted=false` saves and loads. A `.crail` written before this reads every part
   mounted (R-rail23-1a).
2. **An unmounted part leaves the model.** On the shipped example, unmounting `C10` moves the worst
   margin by the amount Q2's ranking predicts for it — the README publishes 16.3 dB to −15.6 dB, so
   the two mechanisms have to agree. **The cross-check is the test**: two independent paths to one
   number.
3. **Its row survives, with its inductance.** The parts table still holds `C10`, greyed, with its
   computed mounting loop; re-mounting restores the original answer bit for bit (R-rail23-1b).
4. **The `.clay` is untouched.** Byte-compare before and after (R-rail23-1c).
5. **Unmounted and unresolved are different.** Two rows, two states, two spellings (R-rail23-1d).
6. **Multi-select unmounts once.** Four parts, one re-solve (R-rail23-2c).
7. **Compare against the previous run.** Run, unmount, run, compare: a report naming `C10
   unmounted` and a per-part table (R-rail23-3b/c).
8. **A pinned baseline survives further runs** (R-rail23-3d).
9. **Q2 ranks the mounted set.** With `C10` unmounted, it is absent from the ranking rather than
   ranked at zero (R-rail23-4a).

## 6. Scope

- **No run history.** One baseline, plus one pin. R-rail23-3a.
- **No change to the `.clay`, ever.** R-rail23-1c.
- **No merging with Q2.** R-rail23-4a.
- **No DC handling of an unmounted series element** unless brief 25 has landed. R-rail23-1e.
