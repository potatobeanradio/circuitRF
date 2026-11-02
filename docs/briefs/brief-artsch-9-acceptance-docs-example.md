# Brief AS-9 — Round-trip acceptance, field fixtures, user pages, the example workspace

**Series:** `brief-artsch-0-overview.md` (D17) · **Tag:** `R-as9-<m>`
**Depends on:** AS-7, AS-8 (AS-11 if it has landed: its page section is added here)
**Area:** `tests/Ui.Tests/Recognition/` (`ArtworkRoundTripTests.cs`, `ArtworkFieldBoardTests.cs`), `.gitignore`,
`testdata/artwork-boards/README.md` (committed; the boards are not), `examples/Artwork to Schematic/`,
`examples/examples.json`, `docs/user/src`, `docs/design/artwork-to-schematic.md` (final state)

---

## 1. Goal

Prove the feature end to end on artwork whose answer is known, keep the field boards that shaped it running as
skip-on-absent fixtures, and ship it with an example and its user pages.

## 2. Requirements

**R-as9-1 — The round trip (the acceptance gate).** Starting from a committed synthetic schematic — two ports, an
MLIN path with a 90° MBEND and an MTEE to a shunt stub, a series 0402 C, a shunt 0402 L to a VIAGND pad, a series
0603 R, all with footprints — on a two-layer synthetic technology:
1. **Update Layout from Schematic** (the generator the menu calls) generates the layout;
2. **Gerber + Excellon export** (`GerberExport`, the function File ▸ Export calls) and the **companion writers**
   (`BoardCompanions` — placement and BOM) write the board out;
3. a fresh workspace **imports** it (`GerberImport`, the Board import's path), reading its own `.ctech`;
4. **recognize** (the CLI verb as a process, `--placement`, `--bom`, `--into new:m`);
5. compare with the original:
   - **topology**: element counts by type equal; every part's refdes, kind, connection and value equal;
   - **geometry**: every line's W within 1 µm, every L within max(1 %, 10 µm);
   - **response**: both simulated 0.1–3 GHz; |ΔS| ≤ 0.02 on every entry at every point (small entries judged by
     absolute error, not dB), and S21 phase within ±3°.
A second variant runs step 4 **without** the companion files: topology and geometry as above, every value a
variable with its `tune` entry. Tolerances are stated in the test with their reason (pad-edge reference planes and
the absence of a step model account for the response tolerance).

**R-as9-2 — The coplanar and stripline round trips.** No PCell draws CPWG or SLIN (D18), so each is a layout built
in memory — a GCPW line with side grounds and a via fence on the two-layer technology; a stripline on an inner
layer of a four-layer one — exported and re-imported as in R-as9-1. Recognised as CPWG (W, G, L within the
geometry tolerances) and SLIN (W, L; H1/H2 from the technology); the fence vias all dropped as stitching.
Under `--coplanar microstrip` the GCPW line is MLIN with its gaps recorded (R-as5-2).

**R-as9-3 — Field fixtures (D17).** `.gitignore` gains `testdata/artwork-boards/*/` with a committed
`testdata/artwork-boards/README.md` stating: what goes there (a workspace per board, under a **generic folder
name**, `board-a`, `board-b`, …), the `expected.json` schema (port count, part count, series/shunt split, element
count ranges by type), and that nothing in the folder is ever committed. `ArtworkFieldBoardTests` is a
`FixtureTheory` over the folders present: each recognises with 0 refusals, its `.cnl` passes `check` with 0 errors,
and its counts fall in `expected.json`'s ranges. On a fresh clone every case skips with the reason.

**R-as9-4 — The example workspace.** `examples/Artwork to Schematic/`: a small synthetic board **authored in
circuitRF** (never third-party artwork), exported to Gerber + Excellon + placement + BOM inside the example's own
folder, then imported as the workspace's artwork cell — so the example is a real flattened board, as a user's
would be. Its README walks: open the artwork, Design ▸ Create Schematic from Artwork, review the parts table (one
part deliberately has no BOM row, so a variable appears), Create, simulate, tune the unknown part. A row in
`examples/examples.json`. No results committed. `ExampleWorkspacesTests` keeps passing (index ↔ disk, every
schematic extracts).

**R-as9-5 — User pages.** The page AS-8 began gains: a worked walk-through on the example; "what is recognised and
what is not" as one table (D13, D14, D18); the coplanar choice and the swap (AS-11); for a board with an IC, the follow-up of importing its symbol with
Import Component and wiring it to the recognised ports (or an SnP in its place); the CLI equivalent
(`recognize`, then `import part`). Edit
sources only; **do not run DocGen** — the owner regenerates at the end of the series.

**R-as9-6 — The design note's final state.** `docs/design/artwork-to-schematic.md` with every phase's section, the
round-trip tolerances and their reasons, and the known limits.

## 3. Gates (minimal tests, run only these classes)
`ArtworkRoundTripTests` (R-as9-1 both variants, R-as9-2), `ArtworkFieldBoardTests` (skips on a fresh clone),
`ExampleWorkspacesTests` (existing).
