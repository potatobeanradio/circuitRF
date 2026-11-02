# Sonnet Brief — Phase L5: schematic→layout, and placing PCells directly in a layout

**Design:** `docs/design/layout-view.md` §9 (schematic→layout, R16 re-run idempotency),
`docs/design/pcell-contract.md` (R1, R6, R9). **Consumes L5a and all of L0–L4.**

Two entry points for the same machinery. §9's schematic→layout is the one already designed; the owner has
asked to add **drag-and-drop from the Library Palette straight into a layout window**, mirroring what already
works for the schematic.

Gate command is plain `dotnet test`.

---

## 1. Read this first — how a PCell actually sits in a layout

The owner asked whether cut/copy/paste works for PCells. **The answer follows from where the parameters
live, and that has consequences for everything else in this brief.**

`PCellOrigin` is a property of **`LayoutView`**, not of `LayoutInstance`:

```csharp
public sealed record PCellOrigin(string GeneratorId, IReadOnlyDictionary<string, double> Parameters);
// LayoutView.PCellOrigin — non-null when this view's Shapes were generated rather than drawn
```

That is contract R1 exactly: *a PCell is a cell whose layout view is generated rather than stored*. So a
PCell placed in a parent layout is **an ordinary `LayoutInstance` pointing at a cell whose `.clay` carries
`PCellOrigin`.** `LayoutInstance` has no parameter field, and should not gain one.

**So cut/copy/paste already works — as instance clipboard, not as a new path.** L3a's gate 11 and the
follow-ups' base-independent `CellRef` (R-fix-2) cover it. **Verify rather than build**, and if a gap turns
up it is a gap in *instance* clipboard, which is where it should be fixed.

### 1.1 The consequence: one generated cell per unique parameter set

Because parameters live on the cell, **two instances of the same generated cell necessarily share one
parameter set.** An MLIN at W = 10 and another at W = 20 cannot be two instances of one cell; they are two
cells.

**R-L5-1. Placing a PCell creates or reuses a generated cell keyed on `(GeneratorId, parameter values,
technology)`.** Identical components therefore share a cell automatically — which is not merely tidy: it is
what lets fifty identical vias become one cell and, eventually, one array.

**R-L5-2. Editing a generated instance's parameters is copy-on-write.** If other instances reference the
same generated cell, changing one forks a new cell rather than silently altering its siblings. Silently
editing every MLIN in a design because the user changed one is the failure this rule exists to prevent.

**R-L5-3. Generated cells must not clutter the project tree.** A design with forty microstrip components
would otherwise show forty machine-named cells beside the user's own. Put them under a reserved subfolder,
or mark them so the tree can group or collapse them — **decide and record which**, because it is the first
thing a user will notice and the hardest thing to change later.

## 2. Schematic → layout (§9)

The command the owner has been unable to find, because it does not exist yet.

- Walk the schematic through `NetExtractor` — do not re-traverse it independently.
- For each component instance, resolve `ViewType.Layout` primacy. **A PCell resolves too** — its layout is
  generated rather than stored (contract R1), which §9's step 2 now notes explicitly.
- Components with no layout view are **reported and skipped**, with a labelled placeholder so the omission
  is visible rather than silent.
- Stamp nets onto the instances' **pins**, which the PCell contract's R3 supplies. Do not infer pin
  locations from geometry.

**R-L5-4. Re-running must be idempotent** (§9 R16). `LayoutInstance.SchematicId` exists for exactly this and
is documented as "unused until L5" — this is when it starts being used. A second run updates the instances it
already placed rather than duplicating them, and **must not disturb the user's manual arrangement**: a user
who spent an hour placing components and then adds one part to the schematic expects one new part, not a
re-shuffle.

**R-L5-5. Report what changed on a re-run** — added, updated, removed, and left alone. A command that
silently rewrites a layout is one users stop trusting.

### 2.1 Which file it writes to, and opening it

**R-L5-15. If the cell has no layout, create `<SchematicName>.clay` and open it — unprompted.** No naming
dialog, no "where should this go" question. The command's whole value is that it just runs, and a prompt in
the middle of it is friction for a decision the user has no reason to make differently.

The name follows the **schematic**, which lines up with the cell-first change elsewhere: a new cell already
creates `<CellName>.csch`, so the layout ends up named after the cell too, by the same route.

**R-L5-16. If the layout already exists, open it and make it the active document — do not recreate it and do
not prompt.** Either way the user ends the command looking at the layout, which is the point: today there is
no entry point at all, and one that runs silently in the background would barely be better.

**R-L5-17. The command targets the layout named after the schematic**, creating it if absent. Two cases worth
handling deliberately:

- **The cell has no primary layout view** — the newly created file becomes primary, mirroring how a new
  cell's schematic becomes its primary schematic.
- **A *differently named* layout is already primary** — write to `<SchematicName>.clay` as usual, leave
  primacy alone, and **report it**. Primacy drives `ResolvePrimary`, so silently repointing it would change
  what every other design sees when it instances this cell — a much larger act than the user asked for.

**R-L5-18. A scratch, unsaved schematic has no cell to write into.** Refuse with a message saying the
schematic must be saved first, rather than inventing a location.

### 2.2 Layout parameter edits are overwritten by a re-run — and reported

**R-L5-9. The owner's decision: the schematic wins. A re-run overwrites layout-side parameter edits on
schematic-linked instances, matching legacy tools, and users expect it.** So there is **no** read-only
restriction and **no** detach-on-edit: the layout parameter editor is freely usable on every PCell instance,
linked or not. That is what keeps schematic-first and layout-first equally viable.

What makes it safe is that the overwrite is **visible and reversible**, not that it is prevented.

**R-L5-10. Report each overwritten value, with its direction stated correctly.** The re-run replaces the
*layout's current* value with the *schematic's* value, so a user who set 20 mil over a schematic's 10 mil sees
it go **from 20 mil to 10 mil** — not the reverse. Report the actual before → after of what the command did:

> *`MLIN3` — `W` changed from 20 mil to 10 mil (from schematic)*

**R-L5-11. Distinguish an ordinary update from an overwritten edit — the stored snapshot already tells you
which.** `PCellOrigin.Parameters` records what was generated last time, so:

| snapshot vs schematic | snapshot vs layout | Meaning | Severity |
|---|---|---|---|
| differs | same | **the schematic changed** — the expected case, why the user ran the command | informational |
| same | differs | **the layout was edited** — user work is being discarded | **warning** |
| differs | differs | both moved | **warning** |

Only the second and third rows are a loss. Reporting all three at one severity buries the ones that matter
under the ones the user asked for.

**R-L5-12. The whole re-run is one undoable action.** This is what turns the overwrite from destructive into
recoverable: a user reads the message, realises the edit mattered, presses Ctrl+Z. Legacy behaviour plus undo
is strictly better than legacy behaviour, and it costs nothing — the re-run is already a command.

**R-L5-13. Cap the report and summarise the remainder.** One line per **instance**, not per parameter, with a
sensible cap (suggest 20) and a trailing count:

> *…and 47 more instances updated. 3 had layout edits overwritten.*

Keep the overwritten-edit count in the summary line even when the details are truncated — that number is the
one a user needs, and it is exactly the one a naive truncation would hide.

**R-L5-14. Say nothing when nothing changed.** A re-run that reports "0 instances updated" every time trains
users to skip the message, and then they skip the one that mattered. Same rule the GDSII export dialog
follows.

**Optional, and worth considering:** an instance whose current parameters differ from its snapshot is
*diverged from the schematic*. That state is already computable and could be shown in the Properties
Inspector, so a user sees the divergence while they are working rather than discovering it at the next
re-run. Not required; note it if skipped.

Placement itself can be crude — a row, or a grid near the origin. **Do not attempt auto-routing or
auto-placement quality**; the user arranges. Say so in the completion note so nobody mistakes the simplicity
for an oversight.

## 3. Drag-and-drop from the Library Palette into a layout

New, and mirroring the existing palette→schematic path.

**R-L5-6. Reuse the palette's existing drag payload and the layout canvas's existing drop wiring.** The
schematic side already defines the payload; the layout canvas gained `DragDrop` handlers for cell drops in
the L3a follow-ups. This is a third payload kind on an established mechanism, not a new mechanism —
`SchematicCanvas` registers separate handler pairs per payload kind, and that pattern is the one to follow.

**R-L5-7. The ghost renders the component's real generated artwork at its default parameters**, per the
owner. That means invoking the PCell generator with defaults during the drag — so it must be **evaluated
once and cached**, not re-run per pointer move. Contract R6 already requires exactly this ("once per unique
parameter set"), and a drag is the case that would violate it most visibly.

Fall back to a labelled bounding box if the generator fails or the technology cannot resolve, matching
R-L3a-1's placeholder behaviour rather than inventing a second failure visual.

**R-L5-8. Only components that *have* a layout generator are droppable.** A `Term` or a `Var` has no artwork.
Set `DragEffects = None` for those so the cursor says no before release — this is the case where a silent
refusal is correct, because the user is dragging a thing that has no layout existence at all, not making a
choice being denied.

**On drop**, route through the **same placement path** §2 uses — one path, so the cell-creation policy
(R-L5-1), the naming and the tree treatment (R-L5-3) cannot diverge between the two entry points.

**A palette-dropped PCell has no `SchematicId`.** It was never in a schematic, so §2's idempotent re-run must
leave it alone rather than treating it as an orphan to clean up. State that explicitly; the natural
implementation of "remove instances no longer in the schematic" would delete it.

## 3A. Update Schematic from Layout — the reverse command

A layout-first user designs visually, then pushes the result into a schematic with one command. **Scope: place
or update component instances. No wiring.**

**R-L5-19. The update half is the mechanical inverse of §2 and is where most of the value is.** For instances
already carrying a `SchematicId`, push their PCell parameters back to the linked schematic component. This
also gives §2.2's overwrite an escape hatch that is not "don't edit in the layout": edit freely, then push
back *before* the next schematic→layout run, and nothing is lost.

**R-L5-20. The create half writes `SchematicId` as it goes.** A layout-first instance has none — it was never
in a schematic. Creating its schematic component and **stamping the link** is what makes a second run update
rather than duplicate. `LayoutInstance.SchematicId` already exists; this command is what populates it for
layout-first work.

**R-L5-21. Target, create and focus the schematic exactly as §2.1 does in reverse.** Write to the schematic
named after the layout, create it unprompted if absent, open it and make it active. The same primacy caution
applies: if a *differently named* schematic is already the cell's primary, leave primacy alone and report it.

**R-L5-22. Overwrites in this direction are reported on the same terms as §2.2.** The snapshot logic of
R-L5-11 works symmetrically — a schematic value differing from the snapshot means the schematic was edited
and is now being discarded, which is a warning; a value moving because the layout changed is the expected
case and is informational. Same per-instance line, same cap, same silence when nothing changed, same single
undoable action.

**R-L5-23. Neither command ever runs automatically.** Both are explicitly invoked, always. With two commands
pushing opposite directions, anything implicit means values ping-pong with no clear owner — and that is far
easier to prevent now than to unpick later. This is the one guardrail in this section that is not negotiable.

**Not generated: wiring, terminations, ports.** Placement is crude, as in the other direction. The owner's
point stands — nobody expects terminations from a layout that never had any — so state it once in the
completion note and do not belabour it in the UI.

**Worth knowing for a later increment, not for this one:** the contract's R3 pins carry position, layer and
direction, so **coincident pins are connected**. For a microstrip cascade that is the entire connectivity, so
abutment-based wiring would cover the case this whole arc is about. Only hand-drawn copper between pins needs
real extraction (§9A's LVS territory). Note it; do not build it.

## 3B. Menus and keyboard shortcuts — no toolbar buttons

**R-L5-24. Both commands get a menu item and a keyboard shortcut. Neither gets a toolbar button**, per the
owner.

**Naming — make the pair visibly symmetric**, because the one thing a user must never be unsure of is which
direction they are about to run:

> **Update Layout from Schematic**  ·  **Update Schematic from Layout**

"Update" covers create-if-absent naturally, so one name serves the first run and every re-run.

**No ellipsis on either** — R-L5-15 makes them unprompted, and the file-menu brief's **R-menu-1** rule is that
`…` means "this command needs more input from you," not "something might happen."

**R-L5-25. Add a new top-level `Design` menu, placed between `View` and `Simulate`, and put both commands
there.**

The existing bar is **File · Edit · View · Simulate · Help**, and none of them fits:

- **Edit** is Undo/Redo/Copy/Paste — operations on the *current document's* content. These generate or update
  a **different** document.
- **View** is Hide/Show Dockers — presentation. These mutate data, so putting them there is a category error.
- **Simulate** is Generate Netlist / Run / Stop. Tempting, because `Generate Netlist` is the same *shape* of
  command (derive an artifact from the schematic) — but "Simulate" is the wrong word for "update my layout,"
  and it would make Simulate the place where non-simulation derivations accumulate.

**The decisive argument for creating the menu now rather than borrowing Simulate: L5b's DRC needs a home
too**, and DRC under "Simulate" would be plainly wrong. "Add Library" will want one eventually as well. Two
commands is a thin menu today; it will not stay thin, and moving them later is worse than placing them now.

**Why `Design` over `Tools`:** these are design-flow operations, not utilities, and DRC reads naturally as a
design command. `Tools` is an EDA precedent — its *Update PCB from Schematic* / *Update Schematic from PCB*
pair is the closest analogue in any tool, and it validates both the naming and the pairing above — but `Tools`
tends to become a junk drawer. Other EDA tools use `Design`, and their users will look for a design or
layout menu rather than a utilities one. **If the owner prefers `Tools`, it is a one-word change; the
placement and contents stand either way.**

**R-L5-26. Audit every existing accelerator before choosing the two bindings, and pick a visibly related
pair.** Do not invent bindings and discover the collision later — `Cmd+Shift+N` has already moved once, and
the menu restructure touched several. The pair should read as related (a shared modifier, differing in one
key) so the symmetry of the commands is visible in the shortcuts too. **Propose the pair in the completion
note rather than treating it as settled.**

**Enablement per R13a — disabled with a reason, never hidden:**

| Command | Enabled when |
|---|---|
| Update Layout from Schematic | a **schematic** document is active |
| Update Schematic from Layout | a **layout** document is active |

Both appear in both menu surfaces — the in-window menu and the macOS native menu — which must stay
structurally identical, watching the `$parent[Window]` binding gotcha already documented in
`src/Ui/CLAUDE.md`.

## 4. Guardrails

- **Do not add parameters to `LayoutInstance`** (§1). It would break the instance-is-a-transform-of-a-cell
  model and defeat the L3a geometry cache.
- Do not build a second clipboard path for PCells — verify the instance path (§1).
- No auto-routing, no placement optimisation (§2).
- Do not modify the PCell generators themselves; this brief places them, it does not change them.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 5. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Clipboard (§1)** — cut/copy/paste of a placed PCell works within a layout and across two layouts;
   the generated cell resolves in the destination, or is reported broken and rendered as a placeholder.
   Assert it routes through the **instance** clipboard, not a new path.
3. **One cell per parameter set (R-L5-1)** — placing two MLINs with identical parameters yields **one**
   generated cell and two instances; differing parameters yield two cells.
4. **Copy-on-write (R-L5-2)** — with two instances sharing a cell, editing one's parameters forks a new cell
   and leaves the other unchanged.
5. **Tree treatment (R-L5-3)** — forty generated cells do not appear as forty peers of the user's own cells.
   Record which approach was taken.
6. **Schematic→layout** — every component with a layout view is placed; those without are reported and
   placeholdered; nets are stamped onto contract-R3 pins.
7. **Idempotency (R-L5-4)** — running twice on an unchanged schematic changes nothing, including positions.
   Move a placed instance by hand, add one component to the schematic, re-run: the moved instance stays
   where the user put it and exactly one instance is added.
8. **Re-run report (R-L5-5)** — added/updated/removed/unchanged counts are reported.
9. **Palette drop (R-L5-6/7)** — dragging MLIN into a layout shows a ghost of its **real artwork at default
   parameters**; assert the generator is invoked **once** for the drag, not per pointer move.
10. **Non-droppable components (R-L5-8)** — dragging `Term` or `Var` into a layout shows a "no" cursor and
    places nothing.
11. **Palette-placed survives re-run** — a PCell dropped from the palette, with no `SchematicId`, is not
    removed or altered by a subsequent schematic→layout run.
12. **One placement path** — assert the palette drop and schematic→layout produce an identical instance and
    generated cell for the same component and parameters.
13. **Overwrite is reported correctly (R-L5-10/11)** — edit `W` to 20 mil in the layout on an instance whose
    schematic says 10 mil, re-run: the value becomes 10 mil, and the message reads **from 20 to 10**, at
    **warning** severity. A change driven purely by the schematic reports at informational severity.
14. **Undo (R-L5-12)** — one Ctrl+Z after a re-run restores every overwritten value and every added instance.
15. **Report is capped (R-L5-13/14)** — 100 changed instances produce a bounded message whose summary still
    states the overwritten-edit count; a re-run that changes nothing produces **no** message.
16. **File creation and focus (R-L5-15/16)** — with no layout present, the command creates
    `<SchematicName>.clay`, opens it, and leaves it as the **active** document, with no prompt at any point.
    Run again: the same file is opened and focused, not recreated, and no second file appears.
17. **Primacy (R-L5-17)** — a cell with no primary layout gains one; a cell whose primary layout is named
    differently keeps that primary, and the situation is **reported**.
18. **Unsaved schematic (R-L5-18)** — running on a scratch schematic refuses with a message naming the
    reason, and creates nothing.
19. **Reverse update (R-L5-19)** — editing a linked instance's `W` in the layout and running Update Schematic
    from Layout pushes the new value into the schematic; a subsequent schematic→layout run then leaves it
    alone, because the two now agree.
20. **Reverse create (R-L5-20)** — a layout-first PCell with no `SchematicId` produces a schematic component
    **and gains a `SchematicId`**; running again updates that component rather than creating a second.
21. **Reverse file handling (R-L5-21)** — with no schematic present, one is created unprompted, opened and
    made active; a differently-named primary schematic is left primary and the situation reported.
22. **Symmetric reporting (R-L5-22)** — a discarded schematic edit reports at **warning** severity; a value
    moving because the layout changed reports as informational; nothing changed → no message; one Ctrl+Z
    reverts the whole run.
23. **Nothing is automatic (R-L5-23)** — assert neither command is invoked by any save, open, edit or
    document-activation path.
24. **Menus and shortcuts (R-L5-24/25/26)** — a new top-level **`Design`** menu exists between `View` and
    `Simulate` in **both** menu surfaces, holding both commands with symmetric names and **no ellipsis**;
    neither appears on a toolbar; each is disabled with a reason when the wrong document type is active; the
    two accelerators collide with nothing existing.

## 6. On completion

Record in `src/Ui/CLAUDE.md`: **that `PCellOrigin` lives on `LayoutView`, so a placed PCell is an ordinary
instance and clipboard support came for free** — with the parameters-live-on-the-cell consequence spelled
out, because it is the fact every future PCell question turns on; **R-L5-1's cell-keying and R-L5-2's
copy-on-write**; **R-L5-3's decision** about the project tree; that placement is deliberately crude and
routing is out of scope; and that a palette-placed PCell has no `SchematicId` and is deliberately exempt from
re-run cleanup.
