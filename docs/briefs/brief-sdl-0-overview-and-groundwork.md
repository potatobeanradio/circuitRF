# Brief SDL-0 — Schematic-driven layout: series overview and groundwork

**Series:** Schematic-driven layout, SDL-0 to SDL-10 · **Tag:** `R-sdl0-n`
**Origin:** GitHub discussion #40 (an idea request: a parameterised microstrip schematic whose layout
connects, takes its ports from the schematic, and can be EM-optimised as one block).
**Area:** `src/Ui/Layout/SchematicToLayoutGenerator.cs` (split), new `src/Design/Layout/SchematicSync/`,
`src/Design/Layout/LayoutModel.cs` + `LayoutPersistence.cs` (placement record),
`docs/design/layout-view.md` §9, new `testdata/schematic-to-layout/`, `tests/Ui.Tests/SchematicSync/`
**Depends on:** nothing. Every later SDL brief depends on this one.

---

## 0. What the series adds

Update Layout from Schematic places every part with a layout in packed rows and draws a ratsnest between
pins that share a net (`layout-view.md` §9 step 3). For a schematic built from distributed parts (MLIN,
MTEE, MBEND, MCURVE, MCROSS, coupled lines) and footprints, that leaves a layout in which nothing touches:
each joint has to be dragged into place by hand, and redone whenever a length changes. Nothing turns the
schematic's ports into EM ports, and nothing lets an EM'd layout stand in a test bench with its
parameters open, so copper geometry cannot be swept, tuned or optimised.

The series closes all three gaps:

| Brief | Adds |
|---|---|
| **SDL-0** | This groundwork: the planner moves to `src/Design`, the placement record, the test-design set and its checker |
| **SDL-1** | Connected placement for distributed parts: chains, mirroring from the schematic, loop-gap report |
| **SDL-2** | Footprints in lines: pad directions, series / shunt / bridging parts, the pad-edge joint, ground vias |
| **SDL-3** | Remaining hard cases: width steps, coupled lines, layer changes, any-angle chains, unjoinable nets, overlaps |
| **SDL-4** | Re-runs that respect hand edits: the layout as left plus the schematic's change, propagated so copper stays joined; Lock Position, Re-snap, Restore Removed Parts |
| **SDL-5** | Schematic ports become EM ports on the layout; a seeded `.cem` |
| **SDL-6** | Simulating a schematic by EM of its own layout, at any values of its VARs (and the same cell reused as a part elsewhere) |
| **SDL-7** | Sweeps, tuning and optimisation by EM in the designer's own schematic, with connectivity held fixed; Apply writes the result back |
| **SDL-8** | The EM cell in 3D: a `.c3d` made from the schematic-driven layout, solved by Palace, EMerge or openEMS |
| **SDL-9** | A series gap part (MGAP), so a designed end-to-end gap is in the schematic and can be optimised |
| **SDL-10** | Placed EM components: which can be varied, each instance's geometry in the parent's layout, several instances, where optimised values are written |

## 1. Series rules (every SDL brief holds these)

- **S1 — Never distort to close.** Copper is exactly what the schematic's values say. A joint that cannot
  be made is REPORTED with its size and location; nothing is stretched, bent, shortened or nudged to hide
  it. A layout that looks connected but has the wrong lengths is worse than one with a visible gap.
- **S2 — No auto-routing.** §9 step 3 stands. Connected placement is *placement*: parts are moved and
  turned so their pins meet. No trace, tee or bend is ever invented. A net the chain cannot join stays as
  a ratsnest line and gets a report line saying why.
- **S3 — Hand edits win, and copper stays joined.** A re-run starts from the layout as the user left it
  and applies only what the schematic changed. When a part changes size, everything joined beyond it
  moves as one rigid body by exactly the change, so the user's arrangement is kept and the copper stays
  connected (SDL-4). A parameter edited in the layout is never overwritten. This replaces R-L5-11, under
  which the schematic's parameter value overwrote a layout edit with a warning.
- **S4 — One function, two callers.** The planner lives in `src/Design`; the GUI command wraps its
  plan in undoable commands, and SDL-6's EM evaluation calls the same planner headlessly. Per
  `src/Design/CLAUDE.md`, no second copy in a view model.
- **S5 — Deterministic.** The same schematic, layout and technology give the same plan, byte for byte.
  Iteration orders are by schematic id, never by hash-set order. SDL-6's cache depends on this.
- **S6 — Robustness is measured, not asserted.** Every behaviour is gated on the test-design set (§4)
  through one checker: LVS clean, no ratsnest line on a joinable net, no overlap of unconnected copper,
  and exactly the expected report lines.
- **S7 — Decisions taken for the series:**
  - **D-joint:** where an MLIN meets a footprint pad, the line's end sits on the middle of the pad's
    OUTER edge, so the MLIN's L is the exposed line and matches what the circuit simulation counts.
  - **D-conflict:** when the schematic and the layout both changed the same parameter since the last
    run, the layout's value is kept and the conflict is reported as a warning.

## 2. `R-sdl0-1` — the planner moves to `src/Design`

`SchematicToLayoutGenerator.Run` (`src/Ui/Layout/SchematicToLayoutGenerator.cs`, ~1,800 lines) mixes two
things: deciding what to place where, and building the `IUiCommand` chain that applies it.

- **`R-sdl0-1a`** Create `src/Design/Layout/SchematicSync/SchematicLayoutPlanner.cs`. It takes what `Run`
  takes today (the schematic model, target `LayoutView`, directories, technology, cell resolver, the
  orientation-link and pour-margin settings) and returns a **`SchematicLayoutPlan`**: instances to add,
  instances to replace (index, before, after), instances to delete, shapes to add (ground ties, pour),
  placement-record updates (§3) and the report lines. Pure: it does not mutate `target`.
- **`R-sdl0-1b`** `SchematicToLayoutGenerator.Run` becomes a thin wrapper that calls the planner and turns
  the plan into the same command chain it builds today, in the same order (§9's comment on why deletes
  go LAST still applies). `GenerationResult` is unchanged for its callers.
- **`R-sdl0-1c`** No behaviour change in this brief. Gate: every existing test in
  `SchematicToLayoutGeneratorTests`, `SchematicToLayoutOverwriteReportTests`, `SchematicToLayoutUndoTests`
  and `SchematicToLayoutDrawnArtworkOverlapTests` passes unchanged, and a new test proves the planner's
  plan applied to a copy equals the wrapper's result applied through the command chain (same `.clay`
  bytes) for each of their fixtures.
- **`R-sdl0-1d`** A comment-stripped source scan (the `AuthoringCliVerbTests` pattern) proves
  `src/Ui/Layout/SchematicToLayoutGenerator.cs` keeps no placement arithmetic: no call to the row packer,
  no transform composition — only plan-to-command translation.
- Anything the planner needs that lives in `src/Ui` (`SchematicLayoutOrientation`, the ViaGnd partner
  logic, `GroundArtworkPlan`) moves with it if it draws nothing; otherwise stop and report. Moved
  namespaces go into `src/Ui/GlobalUsings.cs`. Run `tests/Firewall.Tests`.

## 3. `R-sdl0-2` — the placement record

A re-run has to know which linked parts the generator placed, which one anchored each chain, and which
ones the user deleted. Nothing records that today: `SchematicPCellSnapshots` holds parameters and
`OrientationLink` holds rotation, but nothing records placement, because the generator has never moved
an existing part. SDL-4 moves parts when lengths change, and needs this to decide which side of a
change stays put and to tell a deleted part from a new one.

- **`R-sdl0-2a`** Add to `LayoutView` a table keyed by schematic id, beside `SchematicPCellSnapshots`:
  `GeneratedPlacements : Dictionary<string, GeneratedPlacement>`, where `GeneratedPlacement` holds the
  origin (DBU), rotation in degrees, mirror, placement side, and two flags: `ChainAnchor` (this part
  anchored its chain on the last run) and `Removed` (the user deleted it, set by SDL-4).
- **`R-sdl0-2b`** Persisted in `.clay` with the additive convention `RotDeg` and `CellInterfaceHash`
  follow: `WhenWritingNull`/omitted when empty, no `FormatVersion` bump. A file without the table reads
  as an empty table.
- **`R-sdl0-2c`** In this brief the planner WRITES the record for every part it adds and every part whose
  transform it changes, and reads nothing from it. SDL-4 defines what reading it means. Gate: round-trip
  test; a `.clay` written before this brief loads, saves and reloads unchanged except for the new table.
- **`R-sdl0-2d`** Undo restores the table: the record update rides inside the same command as the
  placement it describes.

## 4. `R-sdl0-3` — the test-design set and its checker

- **`R-sdl0-3a`** `testdata/schematic-to-layout/`, one folder per design: a cell folder with `.csch`, a
  technology (one shared microstrip `.ctech`, 0.508 mm substrate, so widths around 1 mm read naturally),
  footprints imported through `ComponentImport` where needed, and `expect.txt`: the report lines the run
  must produce, one per line, as `<severity> <schematic id> <text fragment>`. No owner or field files;
  every design is drawn for this set.
- **`R-sdl0-3b`** `tests/Ui.Tests/SchematicSync/SdlChecker.cs`: runs the planner on an empty layout,
  applies the plan to a copy, then reports:
  1. **LVS** — `LvsRun.Run` on the result: zero errors, except the ones `expect.txt` names;
  2. **open nets** — ratsnest lines from `Ratsnest` on nets the design marks joinable: zero;
  3. **overlaps** — copper of two instances on different nets overlapping: zero, unless expected;
  4. **report lines** — exactly the `expect.txt` set (no extra, none missing).
  Edge contact counts as connection (`DrcConnectivity`: "overlap or share an edge"), which is what lets
  abutted parts join without overlapping.
- **`R-sdl0-3c`** One `[Theory]` over the designs, each design tagged in a manifest
  (`testdata/schematic-to-layout/designs.txt`: `<folder> <brief>`) with the brief that makes it pass. A
  brief adds its designs to the manifest. SDL-0 adds two designs that pass on today's behaviour in the
  "everything reported, nothing crashes" sense: `C0-rows` (three unconnected resistors) and
  `C0-unresolved` (a part with no layout view).
- **`R-sdl0-3e`** C2 keeps the discussion's own spellings, including the two lines that put a unit after
  a variable (`W = w50 mm`). Before SDL-1 starts, establish what the elaborator does with a unit after
  an expression whose names already carry units, and record it in `src/Design/RESOLVED.md`. If `w50 mm`
  does not give the same width as `w50`, that is a defect in its own right: report it to the owner
  rather than editing C2 to avoid it.
- **`R-sdl0-3d`** The full list, so every brief knows its gate:

  | Design | What it holds | Brief |
  |---|---|---|
  | C1 through-line | Port, MLIN, MLIN, MLIN, Port | 1 |
  | C2 branch-line | The schematic from discussion #40, redrawn verbatim: a VAR block `w50 = 280.152 um`, `L90 = 4.6604 mm`, `Llaunch = 1 mm`; four MTEEs with `W1-W3 = w50`, `L1-L3 = w50/2`; ML1/ML2 `L = L90-w50/2`; ML3/ML4 written `W = w50 mm`, `L = L90 mm`; four launches `L = Llaunch`; ports 1-4 | 1 |
  | C3 stub filter | MTEEs with open-ended stubs off a through line | 1 |
  | C4 bends | MBEND, MMITER and MCURVE at 30°, 45° and 90°, left- and right-handed via mirrored symbols | 1 |
  | C5 cross | MCROSS with four arms | 1 |
  | C6 broken loop | C2 with one arm 50 µm too long — one expected loop-gap line | 1 |
  | C7 turned | C2 drawn with every symbol rotated or mirrored — same copper up to a rigid transform | 1 |
  | F1 DC block | 0603 capacitor in series in a 50 Ω line | 2 |
  | F2 shunt cap | Two MLINs and a 0603 capacitor on one net (no tee), cap to ground | 2 |
  | F3 pi attenuator | Three 0603 resistors, two shunt to ground | 2 |
  | F4 Wilkinson | Two MCURVE arms and a 0402 resistor bridging their ends | 2 |
  | F5 bias tee | Series capacitor, shunt inductor to a bias pad | 2 |
  | F6 board-read footprint | F1 with the footprint read straight from a board file (axis-only pad direction) | 2 |
  | F7 three-pad part | A three-pad footprint inline, third pad to ground | 2 |
  | H1 stepped lowpass | Alternating-width MLINs, no junction parts | 3 |
  | H2 coupled bandpass | Three coupled-line sections in a staircase | 3 |
  | H3 layer change | Line, VIA, line on the second metal | 3 |
  | H4 taper | MTAPER between two widths | 3 |
  | H5 three ends | Three MLIN ends on one net, no tee — reported, not joined | 3 |
  | H6 long curve chain | 20 MCURVEs at 17° — end point within 1 DBU of the exact value | 3 |
  | H7 self-crossing | A meander whose lengths make it cross itself — overlap reported | 3 |
  | C8 gap-coupled filter | Three resonators separated by two MGAPs | 9 |
| E1-E14 | Hand-edit scenarios over C1, C2 and F1 (SDL-4 §5) | 4 |
  | P1-P5 | Port scenarios (SDL-5 §4) | 5 |

## 5. `R-sdl0-4` — the design note

Rewrite `docs/design/layout-view.md` §9 step 3 to say parts are placed so their pins meet where the
schematic joins them (pointing at a new §9.1), and that unjoinable nets keep the ratsnest. Add §9.1
*Connected placement* holding §1's rules S1-S7 in prose. Later briefs extend §9.1 with their own
decisions. Record R-L5-11 as superseded by S3, with the date and the series tag.

## 6. Gate

`dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~SchematicToLayout|FullyQualifiedName~SchematicSync|FullyQualifiedName~LayoutPersistence"`
and `dotnet test tests/Firewall.Tests --no-build`. Not the full suite.

Findings go in `src/Design/RESOLVED.md`.
