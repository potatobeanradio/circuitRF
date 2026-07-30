# Sonnet Brief — TermG port numbering, layout→schematic units, and shipping default technologies

Four items. **§3 has a prerequisite check that could otherwise regress earlier work** — see §3.3.

Gate command is plain `dotnet test`.

---

## 1. Bug: pasting a `TermG` does not check for port-number collisions

`Term` checks; `TermG` (added as a convenience wrapper) does not, so paste can produce duplicate port numbers.

**R-misc-1. Route `TermG` through the same uniquing path `Term` uses.** Find it and reuse it — do not copy the
logic, or the two will diverge the first time the rule changes.

**R-misc-2. The underlying cause is almost certainly a hardcoded type list, and that will fail again.** A
paste-time renumber that asks *"is this component a `Term`?"* has to be edited for every future port-bearing
component — and `TermG` is the proof, since it was added and missed within the same week.

Replace the type test with a **property of the component**: does it own a port number that must be unique in
the design? Then the next such component inherits the behaviour instead of reintroducing the bug. If that
refactor is larger than it looks, add `TermG` to the list now and **say in the completion note that the list
is a latent trap**, so the next person adding a port-bearing part knows to check.

Check the same question for **duplicate** and **drag-copy**, not only clipboard paste — if the uniquing lives
in one of three paths, the other two have the same hole.

## 2. Bug: technology units ignored by Update Schematic from Layout for layout-first microstrip

Reported specifically for microstrip instances **created directly in the layout** by drag-and-drop.

### 2.1 Cause

The two sides store parameters differently:

| | Storage |
|---|---|
| **Layout PCell** | resolved **SI metres** (PCell contract R-pc-6) |
| **Schematic component** | a **coefficient plus a unit** — the evaluator's var-unit-wins model |

So pushing a layout value into a schematic field means writing **both parts**. Writing the SI number as a bare
coefficient into a field whose unit is `mil` or `mm` is wrong by a factor of 1000 or 25400 — and the result
looks like a plausible number, which is why it reads as "units not respected" rather than as an obvious error.

**Schematic-linked instances are unaffected** because their schematic parameters were authored with units and
never re-derived. That matches the report exactly: only the layout-first path is broken.

### 2.2 Fix

**R-misc-3. The reverse generator writes coefficient *and* unit, converting through the single conversion
helper R-pc-6 mandates.** Not a second conversion — the same one, or the two will disagree.

**R-misc-4. The unit written is the technology's `DefaultDisplayUnit`.** That is what "respect technology
units" means here: a value pushed into a schematic on a mil-based PCB technology should read `40 mil`, not
`0.001016 m`. It is also the answer least likely to surprise, since it matches what the layout was showing.

**R-misc-5. Assert the round trip.** Place a microstrip PCell in a layout, push to schematic, push back to
layout: the geometry must be **identical**. A units bug in either direction fails this, and it is the cheapest
possible guard against the whole class.

## 3. Change: ship four default technologies

Four authored `.ctech` files exist in `example_tech_files/`:

- `pcb-2layer_RO4350B_20mil_1oz.ctech` — **the default** (owner's choice)
- `pcb-2layer_RO4350B_30mil_1oz.ctech`
- `pcb-2layer_FR-4_70mil_1oz.ctech`
- `mmic-GaAs_2LM_100um.ctech`

They are real `FormatVersion: 1` JSON written by the application's own writer.

### 3.1 Where they should live — embedded resources, not transcribed C#

**R-misc-6. Ship them as `EmbeddedResource` assets, parsed at runtime by the normal `TechPersistence` reader.**

The owner asked whether to hard-code their content. **I would not**, for three reasons:

- The `.ctech` files are the **authored artifacts**. Transcribing them into C# object initializers creates a
  second representation of the same thing, and the two will drift — the first time a stackup is tweaked, one
  of them will be forgotten.
- Embedded resources are compiled into the assembly, so there is **no deployment risk** and no file to go
  missing, while remaining exactly the authored bytes — diffable, and editable with the technology editor.
- The existing `StarterTechnologies` code-generation approach means **every future technology change is a code
  change**. That is the cost being avoided.

**R-misc-7. Parse them through the same reader user files use, and test it.** A test must load all four,
assert each round-trips, and assert each passes `TechValidation`. A malformed shipped technology has to fail
in CI, not on a new user's first run.

**R-misc-8. On workspace creation, write the chosen technology into the workspace's own `tech/` folder as a
real file.** Do not have the workspace reference the embedded copy at runtime — a workspace must stay
self-contained and its technology must be editable, which is the whole premise of `.ctech` living in the
workspace.

Move the files from `example_tech_files/` to wherever the csproj embeds them (suggest `resources/technologies/`)
so there is one location, not two.

### 3.2 Check the display names before wiring the combobox

The default file's internal `"Name"` is **`"PCB 2-Layer"`** — generic. **If all three PCB files carry the same
`Name`, the combobox will show three identical entries.**

**R-misc-9. Verify each file's `Name`, and make the combobox entries distinguishable.** Either correct the
`Name` fields to include the substrate and thickness, or display something derived from the filename. Prefer
fixing the `Name` fields, since that string appears elsewhere in the UI too — but check all four first and
report what they actually say.

### 3.3 Prerequisite: do these files contain the stackup additions earlier work specified?

**R-misc-10. Before replacing `StarterTechnologies`, confirm these four files carry everything the starters
gained.** Two pieces of earlier work extended the starter stackups, and shipping files authored without them
would silently regress both:

- **R-via-2/3/4** — a `StackupKind.Via` entry with a **fill model** (`Plated` + wall thickness, or `Solid`)
  and a recorded **span** naming the two conductor layers it joins. Neither starter had one originally.
- **The two-metal MMIC stackup** — `Metal2 / air (εr = 1) / Metal1 / GaAs / backside ground`, with a
  Metal1→Metal2 via post, which is what makes airbridges expressible.

The default file above does carry `DefaultViaPadDbu` and `DefaultViaDrillDbu`, which is encouraging but is not
the same thing as a stackup via entry. **Check, report, and add what is missing to the files rather than
keeping `StarterTechnologies` alive alongside them.**

Then find and update everything still referencing `StarterTechnologies` — including tests, which likely assert
against the old starters.

## 4. Change: New Workspace dialog — combobox with a `None` option

**R-misc-11. Replace the radio selector with a combobox listing all four shipped technologies, defaulting to
`pcb-2layer_RO4350B_20mil_1oz`.**

**R-misc-12. `None` needs defined semantics, not just an entry.** A workspace with no technology means no
workspace default, so:

- Layouts in it resolve **no** technology. Geometry still draws (dimensions are parameters), layers fall back
  to the generated `FallbackPalette`, and microstrip components **generate artwork but cannot stamp** — they
  have no `εr` or `h`. That is the behaviour `pcell-contract.md` §5 already specifies for the no-technology
  case, so this is a **supported state**, not an error path.
- The user can add a technology later via the Technology Editor, and everything then resolves.

State that in the dialog — a one-line hint under the combobox — so `None` reads as a deliberate choice rather
than a mistake.

## 5. Guardrails

- Do not duplicate `Term`'s uniquing logic into `TermG` (§1).
- Do not add a second unit-conversion helper (§2).
- Do not hand-transcribe the `.ctech` contents into C# (§3.1).
- Do not delete `StarterTechnologies` until §3.3's check passes.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **TermG paste (R-misc-1)** — pasting a `TermG` beside an existing one assigns a fresh port number; same for
   duplicate and drag-copy; same for a mixed `Term` + `TermG` selection.
3. **Units round-trip (R-misc-3/4/5)** — a layout-first MLIN pushed to schematic shows its width in the
   technology's display unit with the correct magnitude; pushing back to layout reproduces **identical**
   geometry.
4. **Shipped technologies load (R-misc-7)** — all four parse, round-trip, and pass `TechValidation`.
5. **Distinguishable names (R-misc-9)** — the combobox shows four entries no two of which read the same.
6. **Stackup completeness (R-misc-10)** — each shipped technology has the via stackup entry with fill model
   and span; the MMIC file has the two-metal stack with an air layer at `εr = 1`. Report anything that had to
   be added.
7. **Workspace creation (R-misc-8)** — creating a workspace writes the chosen `.ctech` into its `tech/`
   folder; editing it there affects only that workspace.
8. **Default selection (R-misc-11)** — the dialog opens on `pcb-2layer_RO4350B_20mil_1oz`.
9. **`None` (R-misc-12)** — a workspace created with `None` opens, a layout in it draws geometry with fallback
   colours, a microstrip component generates artwork, and an attempt to simulate reports the missing
   technology by name rather than failing obscurely.

## 7. On completion

Record in `src/Ui/CLAUDE.md`: whether §1's uniquing was a hardcoded type list and whether it was refactored or
merely extended (and if extended, **that it remains a trap**); **§2's SI-vs-coefficient-and-unit boundary** as
the cause, since it is the third appearance of that class of bug; that shipped technologies are **embedded
resources parsed by the normal reader**, with the reasoning against transcribing them; **what §3.3's check
found** and what had to be added to the files; and `None`'s defined semantics.
