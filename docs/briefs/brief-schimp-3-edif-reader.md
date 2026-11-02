# Brief SI-3 — The EDIF 2.0.0 reader

**Series:** `brief-schimp-0-overview.md` (D7, D8) · **Tag:** `R-si3-<m>`
**Depends on:** SI-1
**Area:** new `src/Design/Schematic/Import/EdifReader.cs`, `EdifSexpr.cs` (if `PcbSexpr` cannot take EDIF's atoms —
measure first, R-si3-1), `EdifText.cs`.

---

## 1. Goal

Read the EDIF 2.0.0 schematic that the commercial editor (overview §0) writes from its own export menu into SI-1's
`ForeignSchematic`, **with its declared nets**, so SI-1's check compares the drawing against the file's own
connectivity. This is the reliable route for that editor's designs: every user of the editor can produce it, and the
format is an open standard.

## 2. Facts about the format this reader must honour
Learned from the standard and from what the MIT reference converter (overview D7 — consulted, not translated)
recorded about real exports. **Each is to be confirmed against the local corpus (§4) before it is relied on**:

- **Units come from the header's `numberDefinition`** — `(scale 1 (e 254 -6) (unit DISTANCE))`, i.e. one unit =
  0.254 mm = 10 mil, in every export seen. Read the scale; do not hard-code it. **Y is UP**, and page content lives at
  y ≤ 0 (origin top-left): sheet coordinates are negated to SI-1's Y-down; symbol coordinates are not.
- **Text escapes:** characters outside printable ASCII are written as runs of `%<decimal>%` byte values. A run is
  decoded as ONE byte string in the exporting machine's ANSI code page (not per byte); CR/LF become one newline; other
  C0 controls are dropped; a trailing newline is dropped. The code page is not stated in the file — try UTF-8, then
  the `--codepage` option (default 1252), and report which decoded it. Never lose a byte silently: an undecodable run
  falls back to Latin-1 with a note.
- **Libraries of `cell`s** are the symbols. A symbol's `interface` lists its `port`s with a `designator` (pin number,
  may be empty), a pin-type string (`PAS`, `IO`, `IN`, `OUT`, `POWER`, `HIZ`, `OC`, `OE`, `3ST`, and `DBO_IN`/`DBO_OUT`
  variants — map to SI-1's types, unknown → passive), and properties including `PackagePortNumbers` (comma-separated
  pin numbers per unit), `INVISIBLEPIN`, and `IMPLICITPORTCLASS` (a hidden power pin's implicit net → `ImplicitNet`).
  Its `contents` hold the figures (path, polygon, rectangle, circle, arc, open shape, text) and one
  `portImplementation` per pin: the connection point and the lead figure; **the lead's vector from the connection dot
  gives the pin direction and length; no lead figure means a hidden pin.**
- **The design's root cell holds `page`s.** A page's `instance`s carry a `designator` (the reference, possibly with
  a unit suffix), `viewRef → cellRef + libraryRef` (symbol keys are `library:cell`, since cell names repeat across
  libraries), a `transform` with `orientation` (`R0 R90 R180 R270 MX MY MXR90 MYR90`) and `origin`, `property`s
  (`Value`, the footprint property, `Designator` — the unit suffix — and `PositionInPackage`, the 0-based unit), and
  `portInstance`s whose own `designator` overrides a pin number.
- **Pin-number fallback chain** (report every use beyond the first): the instance's `portInstance` designator → the
  cell port's designator → `PackagePortNumbers[unit-1]` → the pin name → the port id.
- **Orientation:** mirror is applied before rotation in the format's own matrices — derive the table once from a
  fixture with a known answer and test all eight.
- Instances with no designator are title blocks and page borders — skipped (overview D5).
- **Nets are declared:** each page's `net` has a `joined` list of `portRef`s (instance + port) — `DeclaredNets` — and
  `figure`s that are the wire geometry (`ForeignWire`s). Names matching `N<digits>` are generated and carry no meaning.
  Power and off-page connectors are `portImplementation`s of cells whose names identify them (off-page ones end in a
  fixed `_OFF_PAGE_CONNECTOR` suffix); they become `ForeignLabel(Power|OffPage)` with the connector's net name, and
  page nets are joined across pages by those names when building `DeclaredNets` (confirm on the corpus that this is
  how the editor itself joins them).
- Hierarchical designs: sheet instances are `instance`s of cells whose view is itself a schematic with pages; their
  ports map to the child's hierarchical ports by name.

## 3. Requirements
**R-si3-1** Measure whether `PcbSexpr` tokenises EDIF (quoted strings, `&`-prefixed identifiers, `(rename id "text")`
pairs). If it does, reuse it; if not, a small `EdifSexpr` — and record why in `src/Design/RESOLVED.md`, as
`ComponentPlxSexpr` did.
**R-si3-2** `EdifReader.Read(path, options) → ForeignSchematic` (or a refusal naming the construct). `rename` is
honoured everywhere: the display name is the renamed text, the id is used for references.
**R-si3-3** `DeclaredNets` is populated; SI-1's check runs both comparisons (declared and geometric). A mismatch
between the file's declared nets and its geometry is itself reported — it is the editor's own record disagreeing with
its drawing, and the user needs to hear it.
**R-si3-4** Unknown constructs → a `Notes` sentence with a count, never an exception.

## 4. Fixtures
- **Committed:** `testdata/schematic-import/edif/` — tiny files **written by hand for this repo** following the
  structure above: `divider.edf`, `opamp_units.edf` (a two-unit part, `MX` and `R90`), `two_pages.edf` (off-page
  connector), `escapes.edf` (a `%13%%10%` run and a non-ASCII run).
- **Local only (owner-provided):** real exports in `testdata/schematic-sources/edif/` (git-ignored), each ideally
  with the editor's own exported netlist beside it for a third comparison (`FixtureFact`). **Ask the owner for these
  before starting**; without them §2's facts are unconfirmed and the brief's hand-back must say so.

## 5. Gates
One test per committed fixture (connectivity `Matched` both ways + two named pin positions); the orientation theory
(eight cases); the escape-decoding theory; a `FixtureTheory` over the local corpus.
