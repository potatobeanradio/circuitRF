# Brief DFR26-3 — Technology files: ship the ten-layer stackup, Save As that renames, libraries that travel

**Series:** designer feedback round 26 (2026-10-24), see DFR26-1 for the list. · **Tag:** `R-dfr26-3-<n>`
**Area:** `src/Design/resources/technologies/` and `src/Design/Layout/ShippedTechnologies.cs`,
`src/Design/Layout/TechnologyCatalog.cs` (`Install` ~232), `src/Design/Layout/MaterialLibraries.cs` (~250-270),
`src/Ui/Views/Dialogs/TechnologySettingsView.axaml.cs` (~110, ~252), the technology editor's Save As. Findings go in
`src/Design/RESOLVED.md`.
**Tests:** the shipped-technology and catalog test classes, plus new `TechnologyFilesRound26Tests`.

---

## 0. The gap

1. **The designer asked for their ten-layer stackup to ship**, because they will use it again soon. The file they
   sent still carries the board it was imported from: its `Name` is the board's Gerber folder name, every drill span
   is named after the board's drill files, one span (`… _1-10`, from the board's routing file) has no `SpanFromLayer`/
   `SpanToLayer`, the stackup's `Bottom` is `Ground` although Bottom Copper is a signal layer, and only Inner 1 is a
   ground reference.
2. **Save As keeps the old identity.** Saving a technology under a new file name, then renaming it, left properties of
   the original in it — the `Name` inside the file at least. The designer's file is an example.
3. **A technology installed into the user library loses its material library.** Every Gerber import mapped onto their
   installed ten-layer technology posted `Material library '<user technologies folder>\generic-materials.cmat', named
   by technology '<…>\pcb-10layer_….ctech', cannot be read: the file does not exist.` `TechnologyCatalog.Install`
   copies the libraries a technology names relatively — but only if the library exists beside the SOURCE, and it says
   nothing when it does not. A technology saved from the editor straight into the user folder may not go through
   `Install` at all.
4. Found while looking: the **shipped six-layer technology has the same `Bottom = Ground` with a signal Bottom Copper**,
   which is where the designer's file inherited it from.

## 1. What to do

### R-dfr26-3-1 — Ship `pcb-10layer_FR-4_1.7mm_1oz`

Build it from the designer's file (the owner has it; take the numbers, not the file):

| Layer | Thickness | Notes |
|---|---|---|
| Top Copper | 35 µm | signal |
| Dielectric 1 | 71 µm | FR-4, εr 4.4, tanδ 0.02 (all dielectrics) |
| Inner 1 | 18 µm | **ground reference** |
| Dielectric 2 | 69 µm | |
| Inner 2 | 18 µm | |
| Dielectric 3 | 460 µm | |
| Inner 3 | 18 µm | |
| Dielectric 4 | 77 µm | |
| Inner 4 | 18 µm | |
| Dielectric 5 | 150 µm | |
| Inner 5 | 18 µm | |
| Dielectric 6 | 77 µm | |
| Inner 6 | 18 µm | |
| Dielectric 7 | 460 µm | |
| Inner 7 | 18 µm | |
| Dielectric 8 | 69 µm | |
| Inner 8 | 18 µm | **ground reference** (the designer's own description: the layer next to the bottom is full ground) |
| Dielectric 9 | 71 µm | |
| Bottom Copper | 35 µm | signal |

Total 1.718 mm. `Top = Open`, **`Bottom = Open`**. The import the designer made earlier read 73 µm for the two outer
dielectrics; the file they asked to ship says 71 µm. Ship 71 and note it in RESOLVED.

**Drill spans — the designer's set, which is a 2+6+2 build:** through 1-10 (Top Copper → Bottom Copper), laser 1-2
(Top Copper → Inner 1), laser 2-3 (Inner 1 → Inner 2), buried 3-8 (Inner 2 → Inner 7), laser 8-9 (Inner 7 → Inner 8),
laser 9-10 (Inner 8 → Bottom Copper). Generic names ("Via 1-10", "Laser 1-2", …), drawing layers numbered like the
other shipped PCB technologies. **Drop** the spanless routing entry. Drawing-layer numbers and colours follow the
six-layer technology's pattern. Default via pad and drill: take the through-via sizes from the other shipped PCB
technologies unless the designer's Gerber drill file states one (it does: read the 1-10 drill file's tool table).

Name: "PCB 10-Layer FR-4 (1.7 mm, 1/0.5 oz)" in the six-layer's style. It names `generic-materials.cmat` like the
others.

### R-dfr26-3-2 — Fix the six-layer's boundary

`Bottom = Open` on `pcb-6layer_FR-4_1.6mm_1oz` (its Bottom Copper is a signal layer). Check all shipped technologies
with one test: a stackup boundary of `Ground` on a side must sit against a ground-reference conductor.

### R-dfr26-3-3 — Save As writes a new identity

Find the technology editor's Save As. It must set the `Name` from the new file name (unless the user typed a name),
and the reference-carrying fields — material libraries, relative paths — must be rewritten for the new location, not
copied verbatim. Reproduce the designer's case first and list in RESOLVED exactly which properties stayed.

### R-dfr26-3-4 — A user-library technology always has its material libraries

Every path that writes a `.ctech` into `TechnologyCatalog.UserDirectory` (Install, and the editor's save there if it
is separate) copies each relatively-named library beside it. When a named library is missing at the source:
- if it is `generic-materials.cmat` (`MaterialLibraries.GenericFileName`), write the built-in generic library
  (`MaterialLibraries.GenericRawJson()`) — that file has a known content and nothing is guessed;
- otherwise refuse the install, naming the missing file.
Then an existing user-library technology that names a missing `generic-materials.cmat` loads with the built-in one and
a single note, rather than failing every import.

## 2. Tests

1. The ten-layer technology is offered, validates with no issue, and has the six spans with resolvable ends.
2. Every shipped technology passes the boundary-against-ground test (R-dfr26-3-2).
3. Save As to a new name gives a new `Name` and resolvable library references.
4. Installing a technology whose `generic-materials.cmat` is missing at the source writes the generic one; a missing
   non-generic library refuses.
5. The workspace-creation tests that enumerate shipped technologies still pass (they may need the new count).

Targeted classes only.

## 3. Text

`docs/user/src/reference/stackup.md`: add the ten-layer technology to the list of shipped technologies. Regenerate
that page only.

## 4. Out of scope

Technology importers from other tools' stackup files.
