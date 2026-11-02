# Brief DFR31-1 — Electrostatic on a board with no nets: connected bodies, a terminal picker, floating metal

**Series:** designer feedback round 31 (2026-10-29). · **Tag:** `R-dfr31-1-<n>`
**Area:** `src/Design/Layout/Em3d/Em3dLayoutSolids.cs` (the object-net map, `ViaLandingNets`),
`src/Design/Layout/Em3d/Em3dGenerator.cs` (`Terminals`), `src/Design/ThreeD/C3dProblemAssembly.cs`,
`src/Engine/Em3d/Em3dProblem.cs` (`FloatingConductors`), `src/Design/Em3d/PalaceConfigWriter.cs` (`StaticRefusal`,
`WriteStatic`), the static result reader; `src/Ui/Layout/Em/EmSetupEditorViewModel.Static.cs`,
`src/Ui/Views/Layout/EmSetupEditorView.axaml(.cs)`, `src/Ui/Views/ThreeD/C3dSetupAnalysesView.axaml.cs`.
Findings go in `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md`.
**Tests:** `tests/Engine.Tests` / `tests/Ui.Tests` classes next to the existing electrostatic tests
(`TestPadRound26Tests`, the em3d-22 static tests). Use synthetic geometry only (DFR31-0).

---

## 0. Why

The round-26 test-pad study had a layout that carried NETS: the import that made it named every copper piece, and
its setup used `Net:` terminals and `Ground3D: GND`, written directly in the file. A Gerber-imported board has no nets:
compositing for polarity drops every net name. Repeating the study on such a board through the GUI hits five gaps,
each a product gap:

1. **The terminal column in a 3D view takes object paths only.** `TerminalConductorHeader` is "Objects" for an
   embedded setup, and `AddTerminal` makes `ByObjects` rows. A net given to a polygon in the 2D layout
   (Properties ▸ Net) and typed into the column is refused as "not a conductor of the 3D problem": the column never
   looks at nets. The round-26 setup used `Net:` keys, which this column cannot write.
2. **Nothing lists the names the column wants.** The real names are paths like `U1/Top Copper (1 oz)/RF_LINE`, which
   appear only in refusals. The setup window opens with `ShowDialog` (modal), so the 3D view cannot be clicked to
   read an object's name while it is open. This breaks the standing rule to prefer a picker to a text box whenever
   the valid values can be listed (`src/Ui/CLAUDE.md`).
3. **Ground by net needs a net.** `Ground3D: GND` is refused when no conductor carries a net.
4. **Then every other piece of metal is "floating".** With full object paths typed, the solve refuses 60-odd
   conductors: plane pieces on every layer, plus the stitching vias. Palace 0.18.1 has no floating-conductor boundary,
   so `StaticRefusal` refuses rather than grounding them silently. That refusal is correct. But on a real board almost
   all of that metal is ground-plane copper joined by vias. It is one or two bodies, not sixty. The solve has no idea
   which metal touches which.
5. **A top pad, its via and a bottom pad would each need naming.** They are one piece of metal, and the tool should
   know that.

## 1. Requirements

**R-dfr31-1-1 — Connected bodies.** For a static problem, compute which conductor objects form one piece of metal.
Two objects join when they share a face or overlap in volume: a via barrel joins the copper it passes on each level it
spans, and copper on adjacent levels joins only through a via, never through dielectric. Merge pieces of one layer
that touch. Use union-find over the objects the builder already emits, with the bounding-box spatial index the layout
code already has. A board of 10⁴ objects must not go quadratic. Each body gets:
- the nets of its members, if any (a body whose members name two different nets is the ViaNetConflict case DFR28-5
  already refuses; keep that refusal and extend it to bodies);
- a stable generated name when it has no net. Use the name of its largest member with a `body:` prefix, so the name
  survives re-runs.

**R-dfr31-1-2 — A terminal is a body.** A terminal naming ANY object, or any net, of a body takes the whole body. Naming
the bottom pad of a test point takes the pad, its barrel, the top pad and every inner landing pad. Two terminals that
resolve to the same body are refused with both names.

**R-dfr31-1-3 — The column accepts what a person would type, in this order:** an exact object path; a net name carried
by any conductor (now including nets given to 2D shapes, which `ObjectNetMap` already receives); a unique object-path
suffix (`TESTPT` matching `…/Bottom Copper (1 oz)/TESTPT`). An ambiguous suffix is refused, and the refusal lists the
candidates. The same resolution applies to `Ground3D`, so a ground can be named by an object as well as by a net.
Keep the existing refusals for a name that matches nothing, but have them list up to five near names.

**R-dfr31-1-4 — A picker, and a 3D view you can click.** Each terminal row gets a dropdown listing the problem's
bodies, each shown by its net (or generated name), its layers and its object count. Type-to-filter must still work.
The ground box gets the same dropdown, plus an **"Every body not in a terminal"** entry (R-dfr31-1-5). Make the setup
window non-modal, or give each row a pick button that arms a one-shot pick in the 3D view. Picking an object fills the
row with its body. The 3D view highlights the bodies of the selected row. Building the list needs the elaborated
problem: build it once when the window opens, off the UI thread, and say "listing conductors…" until it is ready.

**R-dfr31-1-5 — Floating metal, said once and answered.** Replace the bare refusal with a choice the setup stores,
`UnassignedConductors3D`, with these values:
- `Refuse` — today's behaviour. Keep it as the default only if R-dfr31-1-6 is not built.
- `Ground` — every body in no terminal is ground. The Messages line names how many bodies were grounded and the
  largest of them. This is the right answer for plane copper and stitching vias, and it is what the round-26 setup did
  by naming GND.
- `Float` (R-dfr31-1-6).

**R-dfr31-1-6 — Floating bodies solved exactly.** Floating metal carries zero net charge at an unknown potential.
Solve each floating body as an extra terminal, then eliminate those terminals from the Maxwell matrix:
`C_red = C_tt − C_tf · C_ff⁻¹ · C_ft`. That is exact for zero-charge conductors, not an approximation. It costs one
extra right-hand side per floating body on the same operator. Report the count before the run ("3 floating bodies add
3 solves"), and refuse above a limit (start at 50) with the counts, so a whole-board run does not quietly become 400
solves. Once this is in, make `Float` the default. Record the derivation in `src/Design/RESOLVED.md` in our own
words, with no citation (owner rule). The `.npy` stores both matrices: the reduced one as the result, and the full one
in the diagnostics group.

**R-dfr31-1-7 — Notes, not noise.** For a static run, "An electrostatic solve has no ports: the 3D view's N port(s)
are not in the problem" is a NOTE. When a run is refused, it must not be printed directly under the refusal, as if it
were part of the failure. Directly under a refusal it reads as a second error.

## 2. Gates

1. A synthetic three-layer board: a top line, a test pad on the bottom joined to the line through a via with an inner
   landing pad, two ground planes stitched by vias, and no nets anywhere. Name the bottom pad alone as the terminal.
   The terminal resolves to {top pad, barrel, inner pad, bottom pad}, and the planes and stitching vias are one body.
2. On the same board with `Ground`, the capacitance equals, within 0.5 %, the capacitance of the same geometry with
   GND nets assigned by hand and `Ground3D: GND` (today's working path).
3. Kron check, with no Palace: a 3×3 Maxwell matrix built by hand, one body floating, matches the closed form. Then one
   Palace run: two plates with a floating plate between them. The reduced C matches two series capacitors within the
   mesh tolerance the existing static tests use.
4. Resolution: a net, a unique suffix and an exact path each resolve. An ambiguous suffix is refused, listing the
   candidates.
5. UI: headless view test. The picker lists the bodies, choosing one writes the terminal, and the ground dropdown
   offers "Every body not in a terminal". Render it with `tools/UiSnapshot` and look at it before reporting.
6. Performance: body grouping on a 20,000-object generated board finishes in under a second (counter-style assertion,
   no timing benchmark test).
