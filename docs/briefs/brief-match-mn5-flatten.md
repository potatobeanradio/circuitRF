# Sonnet Brief — Match MN-5: Flatten to Cell

**Design:** `docs/design/match.md` §11. **Depends on MN-1, MN-2, MN-3.** This brief implements
**Flatten to Cell**: turning a designed `Match` into an ordinary cell whose schematic is the LC network
it synthesised, with pins, a symbol, and the design's terminations carried along **disabled**.

**Where findings go: `src/Ui/RESOLVED.md`.** **Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet test tests/Ui.Tests       --no-build
dotnet test tests/Engine.Tests   --no-build
dotnet test tests/Firewall.Tests --no-build
```

Separate commands (`MSB1008`). `Engine.Tests` is needed only for §5's equivalence test; `--filter` to
that class while iterating.

---

## 0. Why this exists

Two reasons, and the second is the one people will actually use.

1. **It hands the design over.** After flattening, every L and C is an ordinary parameter that can be
   swept, expressed, optimised, replaced with a microstrip equivalent, or given a finite Q. That is the
   escape hatch for `match.md` §7.5's deliberate refusal to let a `Match` participate in sweeps.
2. **It keeps the design's memory.** A flattened cell that has forgotten what it was designed for is a
   dead end six months later. The terminations travel with it, disabled; so does the design record and
   the `Design` blob itself.

---

## 1. What is produced

A new cell folder, through the existing machinery (`src/Ui/Schematic/CellFolder.cs`,
`CellPersistence.cs`, and the save-plan path — read `SavePlan.cs`/`SavePlanExecutor.cs` before writing
a single file, so a half-written cell is not left behind on failure):

```
  <name>/
    .ccell
    schematic/<name>.csch
    symbol/<name>.csym
```

**`schematic/<name>.csch` contains:**

- **Two `Pin` components**, left and right, matching the `Match` symbol's pins so the cell is
  pin-compatible with the component it replaces.
- **The matching network** as ordinary `L` and `C` instances on a grid: series arms along the spine,
  shunt arms dropping to `Ground`, with wires. **Write a series arm as two components** — an `L` and a
  `C` in series — not as one `L` with a `C=` parameter, because the user's next action is to edit,
  sweep or replace individual elements.
- **Both terminations**: a `Term` carrying `R`, plus the absorbed reactive element, **all with
  `DisableState.Open`** (`src/Ui/Schematic/DisableState.cs`).
- **A text annotation** listing the design: band, order, response, both terminations, achieved worst
  return loss, insertion loss and ripple, `Π N²`, and the date.
- **The `Design` blob**, carried onto the cell so a later `Re-open in Match Designer…` can reconstruct
  the original design.

**`symbol/<name>.csym`** is a **copy of the `Match` symbol** — the bandpass glyph, same two pins, same
positions. That is what makes §3's in-place replacement keep the wires.

Element names are MN-1's (`L1`, `C1`, `L2`, …, `CFano`, `MN1_N1_2`, …). They are already unique and
already meaningful; do not renumber them.

---

## 2. Why the terminations are disabled rather than omitted

Omitted, the design intent is lost the moment the cell is opened. Enabled, the cell short-circuits its
own ports when placed. **Disabled**, the cell simulates correctly against the real circuit *and* a user
who wants to reproduce the Designer's plot can enable the two `Term`s and run an S-parameter analysis
on the cell alone — which is the first thing anyone will want to do after flattening. Say this in the
annotation text, in one line, so the user knows the option is there.

---

## 3. Replacing in place

A checkbox in the dialog, **on by default**: *Replace MN1 with an instance of the new cell.*

Since the symbol and the pin positions are identical, the wires stay connected and the schematic is
immediately runnable. The whole operation — create the cell, write the files, replace the instance — is
**one composite undoable command** on the owning schematic's stack (`CompositeCommand.cs`), and the file
writes go through the save-plan machinery.

The dialog asks for the cell name and destination, defaulting to `<InstanceName>_match`. Follow an
existing name-prompt dialog rather than inventing one; `RenameCellDialog` and `InputNameDialog` are both
in `src/Ui/Views/Dialogs/`.

---

## 4. Where it is invoked from

- `[Flatten to Cell…]` in the Match Designer's footer (MN-3 wired the button and left it disabled —
  enable it here).
- The schematic context menu for a selected `Match` instance.

Both routes go through one command object.

---

## 5. Tests

| test | project | what it protects |
|---|---|---|
| **Component ≡ flattened cell** — a `Match` and the cell its Flatten produces give **identical S-parameters to 1e-12** | Engine.Tests | the whole point, and MN-2 §0.2's justification for elementwise stamping |
| **Terminations are disabled** — the generated cell's netlist ignores them; enabling both `Term`s and running the cell alone reproduces the Designer's own response | Engine.Tests | §2 |
| **In-place replacement keeps the wires** — every net that touched the `Match` touches the new instance | Ui.Tests | §3 |
| **One undo reverses everything** — the instance, the cell reference, the files | Ui.Tests | §3 |
| **Round-trip** — `Re-open in Match Designer…` on the generated cell reconstructs the original design (same ladder, same transforms, same N's) | Ui.Tests | §1's `Design` blob |
| **Symbol copied, not referenced** — editing the original `Match` glyph later does not change the generated cell's symbol | Ui.Tests | §1 |
| **Series arms are two components** | Ui.Tests | §1 — someone will "simplify" this |
| **Failure leaves nothing behind** — a write failure part-way leaves no partial cell folder | Ui.Tests | the save-plan path |
| **Name collision** — flattening twice prompts rather than overwriting | Ui.Tests | ordinary care |

### 5.1 Cost

All small. The equivalence test runs two short S-parameter sweeps — well under a second.

---

## 6. Report

State: the measured agreement between the component and the flattened cell; how the annotation reads
(paste it); what you reused from the save-plan machinery and what, if anything, you had to add.
Findings to `src/Ui/RESOLVED.md`.
