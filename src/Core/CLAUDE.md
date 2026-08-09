A compiled model stopped being reported as "unknown" (2026-08-09) — owner-reported, from the open-PDK
bring-up: *"there's 503 unrecognized files in the PDK listed when I import it — could any of those files
help us get the right ModelLibrary?"*

**Two of them were circuitRF's OWN inputs, being called unrecognised.** `PdkFormatRegistry` now names
both:

- **`.osdi` is a compiled Verilog-A model** — the loader's own shared-object format under another
  extension, and the thing circuitRF actually evaluates a kit of this shape's devices with
  (`OsdiModelDiscovery` already finds them and routes a `.model` card to whichever one declares its
  module; `DeviceWorkerProviderResolver` already substitutes one as a model library). Reporting it as
  *unknown (.osdi)* told the user their models were unreadable at the moment those models were what
  made the kit simulate. Now `Supported`, `ModelData`.
- **`.spiceinit` is the other simulator's start-up file**, matched by NAME because it has no extension
  at all. It is genuinely not circuitRF's to run — it names search paths and which compiled models to
  load, both of which circuitRF works out for itself — but "unknown" invites the reader to go and make
  it work. It now says what it is and that nothing there needs running.

**The rest of the 503 are not circuitRF's business and should stay listed**: the bulk is
four other tools' own setup trees (124, 107, 74 and 57 files) — in a directory layout the open-PDK convention
puts there on purpose (one subdirectory per tool). Eight `.lib` files in the kit's model folder do land in
the list — the `_stat`/`_mismatch` statistical variants, which hold only `.param` sections and match no
netlist marker. They are alternatives to files already read, not gaps.

**On the kit's `install.py`, which the owner asked whether the importer should run: no, and the reason
is concrete.** It does exactly two things — compile the kit's Verilog-A sources to `.osdi` with a
Verilog-A compiler, and symlink `.spiceinit` into `$HOME` for the simulator it was written for. circuitRF needs neither: it searches for
the compiled artefacts itself (kit tree first, then the folders the workspace was told about), and it
does not read `.spiceinit`. Running a kit's scripts on import is also the thing the PCell trust gate
exists to prevent. The one genuinely useful fact in that file is that `.osdi` artefacts are BUILD
OUTPUT — which is already why they are re-derived per session rather than recorded.

Gate: `PdkImporterTests.ACompiledModelAndTheOtherSimulatorsSetup_AreNamedRatherThanCalledUnknown`.

Which CORNERS a kit offers is now discoverable (2026-08-08) — the foundation only; the choice, the
panel and the elaboration half are NOT built and are named at the end.

**A corner is a named set of global variable bindings — nothing else.** Verified across a kit's
capacitor, resistor and MOS corner files: every one is `.LIB <name>` / a handful of `.param` / an
`.include` of the SAME shared model file / `.ENDL`. The subcircuits and model cards are identical
across corners. That is what makes corner selection a substitution into `TestBench.GlobalVariables`
rather than a different netlist, a re-import, or a variant of the parts.

**It was not discoverable at all, and the reason was two capped windows.**

- **The classifier keyed on `.subckt|.model`, and a corner file has NEITHER.** It is nothing but
  sections binding parameters and including the shared file, so it classified as unrecognised and its
  corners were invisible. `.lib` is now a third marker — deliberately not `.param` or `.include`,
  which would also match and are far likelier to head a line in something that is not a netlist at
  all. `.lib` is the one that means "this file participates in this dialect's section mechanism",
  which is precisely the thing being recognised.
- **`PeekChars` was 4,096 and two of that kit's corner files declare their first `.lib` at byte 4,114
  and 4,184** — eighteen bytes past the window, behind a license header and a long parameter block.
  Raised to 64 KB. **The saving was never real**: `PeekLimitBytes` (512 KB) is what bounds cost by
  refusing to open a large file at all, so this only widened the window on files already deemed cheap,
  and the read stops at end of file, which nearly all of them reach first. This is the same shape as
  the note already in this file about a network's port labels: *a format's marker is wherever the file
  puts it, and a cap chosen for cost quietly becomes a correctness rule.*

**`SpiceNetlistResult.Sections` exposes what the reader already parsed and threw away as note text.**
Grouped by FILE, which is structural rather than a naming convention: a kit states its corners one
file per device family, so the file IS the axis — a capacitor corner and a resistor corner are two
independent choices, and flattening them into one list would offer a single pick where the kit offers
several. **Collected during the pass that deliberately SKIPS every section** (a file read whole
chooses none, because choosing one nobody asked for is a guess) — anywhere downstream of that skip
collects nothing.

**No section is filtered for not looking like a corner.** The kit declaring them as alternatives is
the whole semantic; matching names against `_typ`/`_wcs` encodes one supplier's habits and goes blank
on the next kit.

**Measured: 6 axes, 47 options**, all six corner files now recognised —
`capCorners` (7), `dioCorners` (4), `hbtCorners` (7), `mosHvCorners` (11), `mosLvCorners` (11),
`resCorners` (7).

Gate: `SpiceCornerDiscoveryTests` (7, synthetic) — order preserved, no sections means no axis, the
collected-while-skipped rule, no name filtering, a sections-only file recognised, a marker past the
old 4,096-byte window found (with the fixture asserting it genuinely exceeds it, so the test cannot
pass vacuously), and prose that merely mentions a directive still not read as a netlist.

**`PdkCorners` is the model on top of that discovery** — `Discover` turns a set of netlists into one
`PdkCornerAxis` per file that declares sections, and `BindingsFor(axisFile, section)` answers what
choosing one actually binds.

- **A corner is requested the way the dialect itself requests one** — `.lib <file> <section>` — never
  by reaching into the file and reading its parameters directly. That is the format's own mechanism
  for "read this one alternative", so the section's conditionals, nested includes and parameter forms
  are handled by the one reader that already handles them rather than by a second grammar.
- **What comes back is the section AND whatever it includes, deliberately.** A corner file's section
  IS the entry point to the model library, so a caller uses this INSTEAD of reading that library
  separately, never in addition, or the two reads bind the same names twice. **Measured: the overlap
  is empty in practice** — the kit's model files declare every parameter inside a subcircuit, so a
  corner's bindings are exactly its own process constants.
- **A file that will not read declares no corners we can trust, and costs only itself.**
- **A recorded selection outlives the kit it was made against**, so `Offers` exists to catch a stale
  one. Silently binding nothing would leave a design at a corner nobody chose with every number still
  plausible.

**A real gap the gate caught, fixed in the reader rather than papered over:** requesting a section a
file does not declare read nothing and reported nothing. That is the worst available outcome — the
design elaborates with none of its process constants bound, which is not an error anywhere, just a set
of plausible numbers computed from defaults nobody chose. It is now a note that names the section, says
nothing was read, and lists what the file DOES offer; the cell is marked incomplete.

**Measured: 6 axes, 47 options, bindings that genuinely differ per corner** — e.g. the
Schottky saturation current is 1.28 at typical and 0.85 at slow; capacitor area capacitance is
`1.5E-15` at typical against `0.9×` and `1.1×` at the two extremes. Binding counts run from 2
(capacitor) to 125 (high-voltage MOS statistical).

Gate: `PdkCornersTests` (10, synthetic but written in the SHAPE a kit uses) — one axis per
declaring file and none for the rest, two families as two independent axes, an unreadable file costing
only itself, a chosen section binding its own values **and the other corner binding genuinely
different ones** (a picker that binds the same thing whatever you choose looks exactly like success),
alternatives not bleeding into each other, the included library contributing no top-level binding, an
unoffered section reported, a blank choice binding nothing, and a stale selection detectable.

> **Superseded 2026-08-08, same day.** The "NOT BUILT" boundary that stood here — no recorded axes,
> no selection, no picker, no elaboration — is closed. See `src/Ui/CLAUDE.md`'s own corner entry for
> what was built on top of this. The only piece still outstanding is the `Design ▸ PDK…` menu item,
> which is a second door onto a panel that already exists rather than a capability.

**Discovery is now driven from the IMPORT, and it is cheap because of one pre-filter.**
`PdkImporter` records what each netlist declares onto `PdkImportReport.CornerAxes`, identified by the
file's own KIT-RELATIVE path — never an absolute one, because a design's recorded corner has to
survive the kit being moved, re-cloned, or arriving on another machine.

**`PdkCorners.Discover` scans before it parses.** A kit's netlists are mostly megabytes of model
cards that declare no section at all; parsing every one to learn that would cost the whole import.
The scan looks for the directive that OPENS a section — a `.lib` with exactly one word after it,
since two words is a REQUEST for a section rather than a declaration of one — which is the same
distinction the reader's own section handling already makes, so a file this skips is one the reader
would have reported no sections for. **Measured: the entire import, corner discovery
included, is 260 ms for 1,266 assets and 110 parts.**

**A kit ships TWO axes per device family, not one, and that shaped the UI.** It carries one
corner file per family per SIMULATOR FLAVOUR — 12 axes over 6 families — so the file stem alone lists
`capCorners` twice with different option sets and no way to tell them apart. circuitRF states no
preference between the flavours (it has no principle for one, and the constants agree), so both are
offered and the LABEL is qualified where it is ambiguous — see `WorkspaceCorners.From`.

A kit's passives, read from the SPICE dialect and SOLVED (2026-08-08) — COMPLETE for the reader; the
part→circuit binding is deliberately NOT built, and the reason is a decision rather than a shortfall
(bottom of this entry).

**What was actually missing, measured rather than assumed.** `SpiceNetlistReader` already read a real
kit's capacitor library completely — three subcircuits, zero skipped lines, nothing marked incomplete,
including the series resistance and an eleven-element RF network. What stood between that and a part
that runs was three things, each of which leaves a design that **simulates and is wrong**.

- **A subcircuit-local `.param` was a cell VARIABLE, so the geometry was sealed shut.** This dialect
  lets a call site override one, and a kit relies on it: the MIM capacitor states its width and length
  exactly this way and has no other way to set them. It is a `ParameterDeclaration` now — the
  `.subckt` line's own binding still wins, and a name declared twice is the file's own contradiction
  and is said out loud. Every parameter default is unchanged, so nothing that read before reads
  differently; only the interface widened.
- **A `.model` card of a passive type reached nothing.** `SpicePassiveModelBinding` maps a `C` card
  onto circuitRF's own `SemiC` — which already computes
  `(Cfixed + Cj·area + Cjsw·perimeter)·(1 + TC1·ΔT + TC2·ΔT²)` — and an `R` card onto the sheet-
  resistance ratio, with the coefficients handed to the resistor that already carries them. **This is
  a mapping of names, not a second implementation of the arithmetic**, which is the whole reason it is
  worth doing at all.
- **The dialect is case-insensitive and circuitRF compares parameter names ordinally.** `R1 a b r=55m`
  is how the kit writes its series resistance, and passed through verbatim it gives a resistor with no
  value — every part built on it failed to elaborate. `NormalisePassiveParameter` spells a passive's
  own value the way circuitRF does; `AlignSubcircuitParameterCase` does the same one level up, by
  rewriting a call's parameter names into the spelling its DEFINITION declared. That direction is the
  only one that cannot invent a parameter: an unmatched name is left exactly as written, so a genuine
  typo is still refused by name.

**Both new passes run AFTER the whole read, and that is not tidiness.** A card may be declared after —
or in a different file from — the subcircuit that uses it; a definition may be read after the call. At
the element line the result would depend on the order two files happen to be read in, which is the
worst possible property for something whose failure is a component with no value.

**Nothing is guessed.** A capacitor card carrying an area coefficient and no geometry to apply it to is
REPORTED and left unbound — the alternative is a capacitance of zero, which simulates perfectly. A
resistor card with no sheet resistance likewise. A card of any other type is left **entirely** alone:
it is the parameter block of a device something else supplies, and marking it incomplete would report
the working case as broken.

**Two judgement calls, stated so they are not re-derived.** `scale` is folded into the COEFFICIENTS
rather than the geometry, because it is documented to multiply the capacitance and folding it into W
and L would square it — the two are indistinguishable at the `scale=1` every real card uses, which is
exactly why it is pinned. And the units are the file's own and are never converted: a card's `CJ` is
per unit area in whatever unit the instance states its geometry in, and rescaling either side would
break the pairing the file itself set up.

**Measured end to end.** Its capacitor library reads with **zero notes and zero
incomplete cells**, and both the MIM capacitor and its RF sibling bind their plate to `SemiC` with the
process coefficients and the instance geometry. Elaborated and solved as a two-port at 1 GHz:

| geometry | solved | the kit's own arithmetic | ESR |
|---|---|---|---|
| 7 × 7 µm (the card's default) | 74.62 fF | 74.62 fF | 55 mΩ |
| 20 × 30 µm (overridden) | 904 fF | 904 fF | 55 mΩ |

The second row only exists because `.param` became a declaration; the ESR column is the series
resistance a previous, reverted attempt at this dropped silently.

**Gate.** `SpicePassiveModelBindingTests` (17, synthetic — this is format work and the repository
commits no third-party kit data) covers the card mapping, `NARROW`, `scale`, instance-outranks-card,
both refusals, the semiconductor card being untouched, a letter/type disagreement, both case-alignment
rules including a definition read after the call that uses it, and the `.param` rule both ways.
`SpiceModelCardSolveTests` (3, Engine) is the one that matters: it reads, elaborates and SOLVES, and
recovers the capacitance from the solved two-port against the card's own arithmetic — because a part
can be read perfectly and still be a different circuit by the time it reaches the matrix. Its second
test guards the first against passing for the wrong reason, by requiring the two geometries to
genuinely differ. One pre-existing test was UPDATED, not loosened: it asserted the `.param`-is-a-
variable behaviour this deliberately makes false.

**NOT BUILT, deliberately: pointing a kit part at its own subcircuit.** It was built, measured against
the kit, and reverted. Automatic exact-name binding attached **70 of 110 parts to LVS test-case
netlists** — files that share a name with a symbol and are regression data, not the kit's device
models — and did not attach the capacitor at all. And even bound, it would not run: that subcircuit's
area coefficient lives behind a **corner section** (`.LIB cap_typ` / `cap_bcs` / `cap_wcs`, with
mismatch and statistical variants beside them), so the value is undefined until someone says which
corner. **That is a user's decision the kit deliberately does not make**, and it needs a UI answer
before any of the plumbing means anything. Shipping the heuristic would have bound most of a kit's
palette to test data, silently — the same class of mistake as reading a display annotation as a model.

A kit's record symbol file yields its DRAWN body, not just its terminals (2026-08-08) — COMPLETE.
`KitSymbolFileReader` read terminals and a parameter template and skipped the artwork, so every part
of such a kit reached the palette as the same box-with-pins. It now reports the drawing as well, in a
neutral shape vocabulary (`src/Core/Pdk/KitSymbolShape.cs`: line, rectangle, path, arc) carrying the
file's OWN coordinates and angles — no scale and no axis convention applied here, because both are
rendering decisions and burying them in a format reader is how they stop being reviewable. The
consumer's half, and the three ways it goes silently wrong, are in `src/Ui/Schematic/CLAUDE.md`.

- **A rectangle is either a terminal or artwork, never both.** One naming a terminal is a pin; one
  naming none used to be dropped on the floor and is a body box.
- **Closure is stated by REPEATING the first point**, so a run whose ends coincide is closed and the
  repeat is dropped — a consumer never has to know that convention, and a bent lead stays a polyline
  rather than becoming a triangle.
- **`KitSymbolFile.Body` is empty-but-never-null** for a file this reader recognised. Null means "no
  drawing at all" (the terminals came from a symbol library), and the two are placed under different
  axis conventions — conflating them mirrors a part vertically.
- **TEXT is deliberately not read**: in this format it is almost entirely substitution placeholders
  that circuitRF already draws itself from the placed instance.
- A malformed record costs ITSELF and nothing else; a kit carries the occasional damaged line.

Gate: `KitSymbolFileReaderTests` K13-K19 (7 new, synthetic — the repository commits no third-party
kit data). Core.Tests 1,182.

# Core — local conventions

A metre is representable (brief-core-length-units.md, 2026-08-07) — COMPLETE (M1–M4).
`src/Core/Expressions/Units.cs` could not represent a length. Not "had an awkward corner" — of the six
length units the parameter editor offered, three evaluated to the wrong number silently, by factors of
100, 1000 and 1,000,000,000, and `mil` had no base symbol at all.

## The measured before/after table — the whole phase in one artifact

`Eval("1", unit)`, measured against the shipped code on 2026-08-07 with a disposable probe (not derived
by reading the table), and again after:

| unit | before | after | correct SI | what was wrong |
|---|---|---|---|---|
| `nm` | **1** | 1e-9 | 1e-9 | identity unit ⇒ multiplier 1, **1e9 high** |
| `um` | 1e-6 | 1e-6 | 1e-6 | ✓ |
| `mm` | 1e-3 | 1e-3 | 1e-3 | ✓ |
| `cm` | **1** | 1e-2 | 1e-2 | identity unit ⇒ multiplier 1, **100 high** |
| `m` | 1e-3 | 1e-3 | — | **still milli, deliberately** — see the decision below |
| `metre` | *did not exist* | 1.0 | 1.0 | the new base symbol |
| `mil` | 2.54e-5 | 2.54e-5 | 2.54e-5 | ✓ in the table, **absent from the base-unit map** |
| `in` / `inch` | *throws* | 2.54e-2 | 2.54e-2 | not recognised at all |

And `Scale(BaseUnit(u))` — the property `ParametricSweepEngine`'s own comment claims ("BaseUnit reduces
it to scale-1 … so injecting it leaves the value unchanged") and that length had **never** satisfied:

| unit | `BaseUnit` before → after | `Scale(base)` before → after |
|---|---|---|
| `mm`/`um`/`nm`/`cm` | `"m"` → `"metre"` | **1e-3** → **1.0** |
| `mil` | `"mil"` (unmapped) → `"metre"` | **2.54e-5** → **1.0** |
| `in`/`inch` | `"in"`/`"inch"` → `"metre"` | *null* → **1.0** |
| `GHz` (the control) | `"Hz"` → `"Hz"` | 1.0 → 1.0 (untouched) |

## The base-symbol decision, and the shape that was rejected

**`"m"` stays MILLI; the metre's symbol is `"metre"`** — §5 q1 shape (b), the owner's call.

The rejected shape (a) was re-pointing `_scales["m"]` at 1.0 and dropping the bare-prefix reading. It
is cheaper and reads better on an axis label, and the measurement supports its precondition almost
entirely: **bare-prefix `m` appears exactly once in the whole committed corpus** — `C=1m` in
`testdata/PhantomNodes/phantom_nodes.cnl` (meaning 1 mF) and its mirror assertion in
`BareTokenAfterParameterTests`. Every other bare prefix in use (`n`, `p`, `k`, `u` — `L=1n`, `C=1p`,
`C=4p`) collides with nothing. It was still rejected: **`m` is milli in every netlist dialect there
is**, and a hand-authored `C=1m` silently becoming a farad is a wrong answer that parses, stamps and
converges. The metre is what gets the new spelling, because a length is the thing that had no symbol.

**The consequence, and it is the one user-visible cost:** `ComponentTypeRegistry.UnitOptions[Length]`
now offers **`"metre"`** in place of `"m"`. Leaving `"m"` in that list under decision (b) would have
kept the exact bug this brief closes — a user picking `m` for a length would still get milli. One
spelling for the metre, everywhere. `MicrostripSubstrateInjection`'s two `"m"` switch arms were renamed
to `"metre"` for the same reason; both are currently unreachable (`SchematicLengthUnit` never returns
either), and are kept so the next person to add a metre row does not land on a silent mm fallback.

**R-len-3, decided rather than discovered afterwards:** the base symbol becomes the sweep axis's unit in
the published `DataSet` (`ParametricSweepEngine` line ~141), so it is rendered as an axis label in the
Data Display and written into the `.npy`. **"metre" reads acceptably there and gets NO display map** —
it is a real unit word, not an internal token. `SweptLengthUnitTests.TheSweepAxisCarriesTheBaseSymbol`
pins it.

## R-len-2 — nm/cm moving into `_scales` DID fix a second latent bug, and not the one expected

Moving them flips `IsKnown` false → true, which changes the token gates that read `IsKnown` rather than
`IsRecognizedUnit`. **The instance-line path was never affected** — `CnlReader` line ~1491 already used
`IsRecognizedUnit`, so `R:R1 a b R=1 nm` never minted a phantom net. The two that WERE broken are the
ones nobody thinks of:

- **A top-level variable assignment** (`SplitExprUnit`, `CnlReader` ~1706): `W = 5 nm` kept `"5 nm"` as
  the whole *expression*, which the expression parser then had to make sense of.
- **A cell parameter declaration** (`ParseParameterDeclarations`, `CnlReader` ~316): `parameters W=5 nm`
  simply lost the unit, so the default silently became a bare number.

`VendorAReader` (~222/312/475) reads through the same gate and is fixed by the same change.
`tests/Core.Tests/Netlist/LengthUnitTokenTests.cs` is what says so rather than assumes it —
**confirmed to fail against the pre-fix code (11 of 18 red), with the instance-line control passing in
both directions**, so the test distinguishes the two paths rather than merely exercising them.

## §1.3's consumer list, re-verified — which sites changed numbers

| site | changed? |
|---|---|
| `SweepAxisRowViewModel.BuildValues` (`?? 1.0`) | **yes** — an `nm`/`cm` axis was silently unscaled; `metre`/`in` are new |
| `ParametricSweepAnalysis` spec ctor (`Analysis.cs:245`, `?? 1.0`) | **yes** — same |
| `ParametricSweepEngine` re-attach | **yes, and this is the fix** — `Scale(BaseUnit(u))` is now 1.0, so the re-attach is the no-op its comment claims. **The engine itself needed NO change** (M2's own stated hope, confirmed by test rather than assumed) |
| `ParameterEditorViewModel:708` MKlopf entry-mode read (`?? 1.0`) | **yes** — `nm`/`cm` now resolve |
| `ParameterEditorViewModel:747` `FormatLengthInUnit` (`?? 1e-3`) | **yes** — same |
| `SchematicToLayoutGenerator:530` `ToDisplayValue` | **yes** — a PCell length parameter in `nm`/`cm` |
| `LayoutShapePropertiesViewModel:1398` | **yes** — same, in the inverse direction |
| `FreqDeferral:247` (`?? 1.0`) | **yes** — a kit netlist writing `nm`/`cm` |
| Everything frequency/resistance/capacitance/inductance | **no** — untouched, and `AGigahertzSweep_IsUnchanged` is the control |

## R-len-6 — every `Scale(...)` fallback in `src/`, listed rather than changed

Each is a place a future missing row hides the same way this one did. **Changing them was not in scope;
listing them is.**

- `src/Core/Design/Analysis.cs:245` — `?? 1.0`
- `src/Core/Expressions/FreqDeferral.cs:247` — `?? 1.0`
- `src/Ui/ViewModels/SweepAxisRowViewModel.cs:174` — `?? 1.0`
- `src/Ui/ViewModels/ParameterEditorViewModel.cs:708` — `?? 1.0`
- `src/Ui/ViewModels/ParameterEditorViewModel.cs:747` — `?? 1e-3`
- `src/Ui/ViewModels/LayoutShapePropertiesViewModel.cs:1398` — `double? scale; if (scale is > 0)` (same class, different spelling)
- `src/Ui/Layout/SchematicToLayoutGenerator.cs:530` — same spelling

## §5 questions 2 and 4, as decisions

- **q2 — should opening a design that used `nm`/`cm`/`m` or a length sweep say so? NO.** Silent
  correction, on the owner's own instruction that legacy workspaces and designs do not need to be
  supported. The old values were wrong by construction; there is nothing to migrate and nothing to warn
  about. No report was built.
- **q4 — `in`/`inch`: ADDED to the expression engine** (2.54e-2, both spellings, both mapping to
  `"metre"`), on the owner's call. `LayoutUnits` already accepted both, so the asymmetry is closed at
  the engine level. **Deliberately NOT added to `UnitOptions[Length]`** — that is a separate UI-list
  decision, and wiring it properly also needs an inch row in `MicrostripSubstrateInjection`'s
  `ConvertMmTo`/`RoundStepFor`/`NiceLengthFor` tables, which currently fall through to a *wrong* mm
  value for an unrecognised unit. A hand-authored `.cnl` may use `in`/`inch` today; the schematic
  parameter dropdown does not offer it. Stated rather than left to be discovered.

## Tests that had to be updated — none, and that is the blast-radius measurement

**Not one pre-existing test pinned the wrong behaviour.** `BaseUnitTests` covers frequency,
capacitance, voltage and resistance and never had a length case; nothing anywhere asserted
`Eval("1","nm") == 1` or `BaseUnit("mm") == "m"`. `BareTokenAfterParameterTests`' `C=1m` still means
1 mF and is untouched, because `m` stayed milli. One COMMENT was corrected —
`WBondSchematicPlacementTests.M3_AParametricSweepOverLoopHeight_WorksFromAPlacedComponent` carried a
paragraph describing this defect as live; it now points at the M4 gate instead.

## The gates

- **M1** `tests/Core.Tests/Expressions/LengthUnitsTests.cs` (39) — the table above, plus
  `Scale(BaseUnit(u)) == 1.0` for every length unit, plus the pin that `"m"` is still milli and is NOT
  mapped to the length base. **Confirmed red pre-fix: 18 of 39.**
- **R-len-2** `tests/Core.Tests/Netlist/LengthUnitTokenTests.cs` (18) — above. **Red pre-fix: 11 of 18.**
- **M2** `tests/Engine.Tests/Parametric/SweptLengthUnitTests.cs` (12) — a `mm` and a `mil` sweep driven
  through the real `ParametricSweepEngine`, read straight back out through the elaborator (`Vdc=Lvar`
  at a unit-less site, so var-unit-wins makes the node voltage numerically the injected value), plus
  the `GHz` control and the axis-symbol theory. **Red pre-fix: 10 of 12.** *Note on the fixture:* the
  `nm` row sweeps at 1e6 nm rather than 1 nm — a 1 nV source is below the DC engine's own `AbsTol` and
  reads back as 0. That is the readout's resolution, not the units'; the axis theory covers `nm` at any
  magnitude.
- **M4 (the phase gate)** `WBondSchematicPlacementTests.M4_ASweptLoopHeightInMil_AgreesWithTheHandWrittenNetlist`
  — a wBond loop-height sweep at 10 mil and 45 mil from a **placed** component with a `mil`-declared
  global, against the same two heights run from a hand-written netlist with `LoopHeight` a literal in
  metres and no sweep anywhere, so the two paths share no unit resolution at all. **Red pre-fix.**
  Measured: **1086.2 pH at 10 mil, 2206.7 pH at 45 mil** (series inductance recovered from the run's own
  `S21` via `Z = 2·Z0·(1/S21 − 1)`), against WB-B2's own hand-`.cnl` pair of 1073.8 / 2189.9 pH — the
  ~1% gap is this extraction being a 5 GHz series-Z read that carries the wire's own 0.4–0.6 Ω. **The
  assertion is that the two paths AGREE**, not that they reproduce a stored number. Not tagged
  `Category=Benchmark`: the whole class runs in 187 ms.

**Full solution green** (Firewall 6, Core 1,175, RfCore 281, WBond 237, Ui 5,610, Engine 1,034 + 1
pre-existing skip), with **one pre-existing failure**:
`Harmonica.Tests.ContextAndPersistenceTests.R11_AMissingReferencedModelIsNamedRatherThanSubstituted`,
which `src/Ui/CLAUDE.md`'s WB-E entry already records as failing deterministically at commit `20dcadf`
in a clean worktree — **re-confirmed here by stashing this brief's three production files and watching
it still fail.** Separately, `LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect(Mixed)`
failed **once** on the first full-solution run and did not recur in three subsequent runs (one full
solution, two full `Ui.Tests`) or in isolation with or without this brief's changes; nothing in the
layout-perf path reads `Units` or `UnitOptions`. Per this repo's own standing rule, isolated repetition
does not disprove a load-dependent race — **recorded by name rather than called clean.**

A user's OWN Verilog-A model is a placeable component (2026-08-03) — COMPLETE, palette included.
Type `VerilogA`; `src/Core/Devices/External/VerilogAFileResolver.cs` plus a factory path. **No kit, no
manifest, nothing to install**: a user compiles their model with their own compiler, places the
component, points its `File` at the result, and runs.

**The difference from `ExtDevice` is who supplies the model, and it is why this is a second type.**
`ExtDevice` names a PROVIDER — a kit that was installed, with a manifest saying which program
evaluates its devices. This names a FILE. Everything below the provider seam is identical, which is
the point: an externally-supplied device is an ordinary nonlinear component either way, and node
expansion keys on `ExternalDeviceModel` so it was inherited with no change.

- **The provider name CARRIES the path** (`VerilogA|/abs/path.osdi`), and that is what makes the
  caching correct: the registry keys providers by name, so two instances naming one file share a
  worker and two naming different files get one each. Anything coarser evaluates one user's model
  with another's.
- **`VerilogAFileResolver` is BUILT IN and survives `ClearResolvers`.** That call exists to drop the
  resolvers belonging to a workspace being closed; this one belongs to no workspace, because placing
  a model file must work on a fresh install with nothing configured. It is asked LAST, so a host or
  a kit can still override anything it would answer.
- **A missing file is a REFUSAL, not a null.** Null means "this resolver has no opinion", which would
  send the caller on to report that no provider answered to the name — and the name is a file path,
  so the useful message is that the file is not there.
- **`File` and `Model` are always verbatim, never tried as an expression first.** The existing
  fall-back-on-throw rule is not enough for a path: one that happens to parse as arithmetic would be
  silently turned into a number.
- **`Pins` is circuitRF's, not the model's, and is NOT forwarded.** The symbol has to know how many
  terminals to draw before anything has opened the file. Forwarding it asks the model to accept a
  name it never declared, which a strict model refuses — failing every device it serves. **Found by
  the sanity test, not by review.**
- **The type is optional when the file declares one device.** Asking a user to name it as well as
  find it is a step that answers itself; two or more, and it has to be stated.

**Palette and dialog** (`src/Ui`): a tile under **Devices**, findable by *VerilogA*, *Verilog-A*,
*OSDI*, *compact model*, *custom*, *PSP*, *BSIM*, *HICUM*. The `File` row offers a **Browse…** picker
filtered to `*.osdi` — built-ins had no way to declare a file-valued parameter (only kit cells did),
so `ComponentTypeRegistry.IsFilePathParameter` states it. The glyph is a plain box with numbered
leads: circuitRF does not know what the model IS, so drawing a transistor would assert something
untrue on the schematic. Variadic on its own `Pins` parameter rather than `NumPorts`, because a
four-terminal MOSFET is not a four-port.

Gate: 16 in `tests/Ui.Tests/VerilogAComponentTests.cs` (palette, search terms, the registry's names
checked against the factory's constants rather than by eye, the file picker, and pin counts 2–8 all
landing on the connection grid at distinct positions), plus 5 in
`tests/Engine.Tests/External/VerilogATransistorSanityTests.cs`.

**Transistor sanity check, on a real BiCMOS kit's compiled MOSFET.** A common-source stage solved by
circuitRF's own DC engine — which is a much stronger claim than the worker tests make, because
elaboration has to expand a four-terminal device, resolve the seven nodes the model collapses, mint
the rest, and Newton has to converge on derivatives that arrived over a pipe. Asserted as what a
transistor DOES, never as stored numbers: it conducts into the circuit, Id rises monotonically with
Vgs over four decades, Id saturates with Vdd (under 1.35× for a 50 % supply rise, where a resistor
would give 1.5×), gm is positive, and every collapsed TERMINAL still carries the user's net. Skips
with a reason without a compiled model or a built worker.

Validated against REAL compiled compact models (2026-08-03) — COMPLETE, and it found two defects that
no synthetic fixture could. `tests/Core.Tests/Devices/External/CompiledModelValidationTests.cs`, 5
tests, **Skipped with a reason** unless `CIRCUITRF_OSDI_MODELS` names a directory of compiled models.

**circuitRF stays MIT and nothing GPL is anywhere near it.** The Verilog-A compiler is GPL-3.0 and
lives on the USER's machine — never invoked by circuitRF, never shipped. The `.va` sources are the
kit's and are never vendored. The `.osdi` is a build output of those two and is never committed,
which is exactly why the tests locate models through an environment variable: committing a binary
would put someone else's build product in an MIT repository. `osdi.h` remains MPL-2.0 in its own
file, file-scoped, touching nothing. See `tools/osdi-worker/README.md` for the table.

**Two defects, both invisible to the fixture by construction:**

1. **`OsdiSimParas` must be NON-NULL and NULL-TERMINATED.** The worker passed four null pointers,
   meaning "no simulator parameters". A model resolves `$simparam("gmin", …)` by SCANNING `names`,
   so null there is a null dereference *inside the model*, during `setup_instance` — presenting as
   the worker dying with no output at all. The fixture never asks for a simulator parameter.
2. **A collapsed TERMINAL must keep the USER'S net, and this one is the serious one.** A real MOSFET
   collapses its drain terminal onto its internal drain, and its bulk terminal plus three internal
   bulk nodes onto a single master. Reading `SlavedTo` literally gave the *terminal* the internal
   node's index, so the net the user wired to that pin was dropped: **a device that solves perfectly
   while disconnected from the circuit around it**, with nothing on screen saying so.

   The fix is a GROUP resolution in `Elaborator.BuildExternalDeviceNodes`, and it needs two passes
   for a reason: one master may have several slaves of which only one is external, and assigning as
   each is encountered copies the internal index into the others before the terminal is reached. The
   groups are also resolved BEFORE minting, because minting a master that is then absorbed into a
   terminal's net leaves an orphan unknown — an all-zero row and column, which DC hides entirely and
   the S-parameter assembly reports as a singularity naming a node the user cannot find. **Two
   terminals collapsed onto each other is refused**: the device is shorting two of the user's nets,
   which circuitRF cannot carry, and stamping at one silently drops the other.

**Every oracle is the model's own output under a different operation** — the reported Jacobian against
a central difference of the currents it returned, the reported capacitance against a difference of its
charges, collapse reports against structural coherence. No reference simulator is involved, which is
what makes this runnable at all: the kit ships no measured data.

**The gate has teeth, verified rather than assumed.** A sign flip introduced deliberately into the
worker's Jacobian scatter (`G[r*n+c] += jr[e]` → `-=`) turns `V2` red immediately and names the
offending entry with both values. Reverted; the suite is green again.

**A vacuity guard on both counting tests**, because that is the failure mode here: `V2` asserts that
at least one Jacobian entry was large enough to compare (a bias that puts the device somewhere it does
nothing would pass every check), and `V4` asserts at least one model reported a collapsed node.

Measured on real models: a 4-terminal resistor with a thermal node (129 parameters, 4 internal nodes)
and an industrial MOSFET (809 parameters, 9 internal, **7 collapsed nodes**). Suite 6,223 pass with no
kit present; the compiled tier lights up when one is.

Symbols and part discovery for a one-symbol-per-file kit (2026-08-03) — COMPLETE, palette included.
`src/Core/Pdk/KitSymbolFileReader.cs` plus two recognisers, and a fourth shape in
`PdkImporter.DiscoverParts`.

**The gap this closes, and it was total.** Part discovery knew three shapes: a catalog, a
`<cell>/<view>/<file>` database tree, or subcircuits the kit also draws. A fourth exists and is what
an openly-licensed kit looks like — symbols together in one folder, ONE FILE PER PART; the behaviour
behind them in a netlist in a different folder; a compiled model library in a third; and no catalog
anywhere. Such a kit matched none of the three and imported as a pile of recognised files with an
**empty palette**.

- **A terminal is a rectangle record whose attributes declare a NAME, not one on a particular
  layer.** The layer number is the format's display convention — it decides what colour the editor
  draws the box — and a kit may renumber it. Keying on the layer would read such a kit as a symbol
  with no pins at all: it still imports, still appears in the palette, and cannot be wired to
  anything.
- **Recognised STRUCTURALLY, never by extension and never by the tool named in the first line.** The
  extension is shared with unrelated formats, and keying off the writing tool would put a particular
  editor's identity into circuitRF and stop recognising the format the moment a kit generated it with
  something else. Same rule the component catalog already follows.
- **An attribute block SPANS LINES**, and a template routinely does. A line-at-a-time reader takes the
  first line as the whole block and loses every parameter after it, which leaves a part that looks
  read and has half an interface.
- **The symbol's own template outranks a name-matched subcircuit** for the parameter interface: it is
  the kit stating this part's parameters WITH its own defaults. The subcircuit stands in when the
  symbol declares no template.
- **The instance's own name is not a parameter.** Offering it as one puts a "what is this instance
  called" box in the parameter editor beside the real parameters.
- **The part is NOT pointed at the file it came from.** Its terminals are already read and attached;
  handing the path to the installer would send a DIFFERENT record-based text reader at it, and two
  such formats quietly reading each other's files produce a symbol that is drawn, placeable and wrong.
- **The kit's own type word becomes the palette category**, so browsing groups the way the kit's
  documentation does rather than the way circuitRF would guess.

**A netlist is recognised by its DIRECTIVES, not its extension** (`SpiceNetlistRecognizer`). Kits of
this shape spell the same content `.lib`, `.sp`, `.spice`, `.mod`, `.cir` and `.net`, sometimes
several within one kit, and `.lib` is claimed by unrelated formats elsewhere. **This was found by a
test, not by inspection** — the netlist in the gate's own fixture was classified as nothing at all,
so the parameter interface silently fell back to the symbol's template and the fallback path was
never exercised. Directives are matched at the START of a line: a mention inside prose or a path is
not a declaration, and reading documentation as a netlist puts phantom parts in a palette.

**`SubcircuitsIn` now reaches for the SPICE reader** rather than growing a second grammar — that
reader already turns `.subckt` into ordinary cells with ports and a parameter interface, which is
exactly what part discovery wants. This is the join between the netlist phase and this one.

**The palette needed NO new UI code.** `PdkPartInstaller` already tries a per-part drawing first and
falls back to terminals the importer attached — the path a symbol LIBRARY established — so attaching
`Pins` was the whole of it. Same property the device-worker manifest had: the seam was already the
right shape.

**`DsnSymbolReader.TranslationVersion` now governs this path too**, because every source of terminals
goes through `KitTemplateSymbol`. NOT bumped: nothing here moves an existing pin, and no workspace has
a version recorded for a path that did not exist. A future change to that class's scale, snap, axis
flip or ordering moves pins on all three sources and does need the bump.

Gate: 9 in `tests/Core.Tests/Pdk/KitSymbolFileReaderTests.cs`, 6 in
`tests/Ui.Tests/OpenKitPaletteTests.cs` — the latter drives a synthetic kit of this shape through
`PdkImporter` AND `PdkPartInstaller`, because "the importer returned parts" was already true for kits
whose palette was empty. Suite 6,211 pass.

**VERIFIED against a real openly-licensed kit (2026-08-03), and it changed three things.** The kit was
cloned outside the repository (sparse, tech files only) and driven through by disposable probe,
deleted after running. **Result: 41 primitive symbols → 38 read with terminals,
0 refused**; the 3 without pins genuinely have none (two annotation widgets, one gallery). End to end:
**110 parts → 110 palette items → 110 symbols installed, 0 omitted, 0 diagnostics**, with a pin-count
histogram (1–8) matching the device families. What the probe found:

- **A malformed symbol lost all its pins, and the kit's own file is the malformed one.** One device's
  `template="…` has no closing quote. Quote tracking then makes every following brace look quoted, the
  attribute block runs on and swallows the terminals, and the device imports with NO pins — listed, in
  the palette, impossible to wire. Fixed by bounding a block at the next RECORD, since a record always
  begins a line: the terminals survive and only the malformed attribute is lost. **The kit is wrong
  and nothing had noticed**, because nothing else reads that attribute.
- **Pin ORDER is stated after all** — `sim_pinnumber`, on 21 of the 38. Used where every pin of a
  symbol has one; declaration order remains the fallback, because a partial set would interleave
  numbered and unnumbered pins arbitrarily. This replaces the earlier "unverified, names are carried
  so something can bind by name later" caveat.
- **A template states netlisting as well as device**: `name` and `spiceprefix` appear on every device
  and are how the instance is WRITTEN, not what it is. Excluded — otherwise every part gets two boxes
  in its parameter editor for things the user cannot usefully change.

Still deliberate: the drawn BODY is not read — the pins are the kit's and the body is circuitRF's own,
the same bargain the symbol-library path strikes.

Native model top-ups (2026-08-03) — COMPLETE. The diode completed, a semiconductor capacitor added,
temperature coefficients on the resistor, and the device multiplier.

**The junction temperature relations now live in `Temperature`, shared, and that is the point of the
change rather than a side effect.** They began inside `FetModelBase` for its gate diode; `DiodeModel`
needs the same three for the same physics, and a second copy is a second answer to one question —
which is exactly what `ResolveDeviceC` already exists to prevent. `JunctionPotentialAt`,
`DepletionCapacitanceScale`, `SaturationCurrentScale` and `BandgapAt` are the shared four. **The FET
family's own 27 tests are the proof the extraction changed no number** — that is what makes it a
refactor rather than a rewrite.

- **`Eg ≤ 0` means "the bandgap term is not modelled" and `BandgapAt` returns zero.** Without it there
  is no way to state a device whose saturation current does not move with temperature: the Varshni
  narrowing term is non-zero even at `Eg(0) = 0` and would go on scaling the current by itself. Same
  rule as the diode's `Bv = 0`. **This is what keeps the A0 ambient tests honest** — they recover the
  device temperature by inverting the diode's own conduction current, and with `Is(T)` live that
  inversion would have to undo `Is(T)` first, using the model's temperature code to check the model's
  temperature code. The fixtures state `Xti=0 Eg=0`; `Is(T)` is gated separately against a closed form.
- **`SiliconBandgapEv = 1.16` is Eg at 0 K, NOT the 1.11 several tables quote**, which is Eg at room
  temperature. The two are not interchangeable — this one is what `BandgapAt` subtracts from.

**Diode — what was added, and one deliberate behaviour change.** `Area` (scales `Is`/`Isr`/`Cj0`/`Ibv`
up and `Rs` down), `Isr`/`Nr` (recombination), `Nbv` (breakdown emission), `Tnom`/`Xti`/`Eg`, and the
temperature relations on `Is`, `Isr`, `Vj` and `Cj0`.

- **Recombination is a SECOND exponential, not a correction to the first.** Its own saturation current
  AND its own ideality factor — near 2 where the diffusion term's is near 1 — which is what lets it
  dominate at low bias and vanish at high. Folding it into `Is` fits one decade and misses the rest.
  The gate asserts the CROSSOVER, because a single-bias check passes for a model that ignores one term.
- **`Nbv` defaults to the published 1, NOT to `N` — a deliberate change.** Before the parameter
  existed the breakdown branch reused `N`, which made the reverse knee follow the forward ideality.
  Nothing physical requires that and no parameter table states it. A design with `Bv > 0` and `N ≠ 1`
  gets a different reverse knee than it did.
- **A junction potential driven past zero falls back to the card's own value BEFORE it is used**, so
  the capacitance scale is not computed from it either. A relation leaving its range says nothing
  about the device.

**`SemiC` — a capacitor whose value comes from a process and a geometry**, `Cfixed + Cj·area +
Cjsw·perimeter`, times `1 + TC1·ΔT + TC2·ΔT²`. `W`/`L` give the area and perimeter of a rectangle;
explicit `Area`/`Perim` win, for a shape that is not one.

- **A separate type from `CapacitorModel`, not extra parameters on it.** An ideal capacitor takes a
  capacitance; this one takes a process and a shape and works one out. Merged, `C` becomes ambiguous —
  the value, or a parasitic to add to the geometric term? Apart, both questions have one answer.
- **It is LINEAR, and that is physics rather than simplification.** A capacitance that varies with bias
  is a junction, and a junction is a diode — `DiodeModel` already carries that charge and its
  derivative in the form HB needs. A bias-dependent `SemiC` would be a second, worse copy of it under
  a name that does not say so.
- **`Temperature.PolynomialScale` is a different shape from the junction relations** and is named
  separately for that reason: a resistor's and a capacitor's temperature dependence is a fitted curve,
  not device physics. Reaching for the junction relations there would be borrowing physics from
  something that has none.

**Resistor `TC1`/`TC2` is a MULTIPLIER resolved at construction, not a parameter read at stamp time.**
The ambient a device is evaluated at is known at elaboration and nowhere else, so a resistor reading
`c.Parameters` inside `Stamp` cannot see it. `ParametricSweepEngine` re-elaborates every point, so
resolving early loses nothing. `R` stays in the parameterless registry, so `TryCreate("R")` still
returns a factor-1 resistor and the whole path is additive.

**The device multiplier `m` has ONE seam, and creating it was most of the work.** `ElaboratedComponent`
now owns `Stamp` / `StampLinearized` / `Evaluate`, and the 16 engine call sites go through the
component instead of through `ec.Model`. **Do not call `ec.Model.Stamp(...)` directly** — it bypasses
the multiplier and silently simulates one device where the netlist asked for several.

- **A decorator around `ComponentModel` was considered and rejected.** There are 47 `ec.Model is X`
  checks across the engines — sources, probes, ports, terms, SDD-with-control-refs, mutual inductance
  — and a wrapper stops matching every one of them, silently. The component-level seam has no such
  failure mode, and after the move there is exactly one place the multiplier is applied.
- **What scales: admittance contributions and current injections, plus all four nonlinear blocks.**
  That is the whole of what "the same thing again, in parallel" does, and stating it that way needs no
  list of which model is which. Scaling the currents and forgetting the Jacobian would converge slowly
  to the right DC answer and be wrong outright at AC.
- **What is REFUSED: anything that allocates a branch-current unknown.** Two ideal voltage sources in
  parallel is not a circuit — it is the same constraint written twice. Refusing at the moment
  `AddBranch` (or any Group-2 method) is called catches every such model, present and future, by name
  and without a list.
- **`m ≤ 0` is refused rather than obeyed.** Some dialects read `m = 0` as "this device is not there";
  deleting a component the user placed, in silence, is the worse answer.
- **Lower-case `m` is the multiplier; upper-case `M` is the diode's grading coefficient** — on a
  component that can carry both, meaning nothing like each other. Resolved parameters compare
  ordinally so the two are genuinely different keys, and there is a test pinning it, because a diode
  reading its grading coefficient as a device count gives a circuit with 0.4 diodes in it that
  simulates perfectly. **The SPICE reader normalises the instance spelling** (`M=4` → `m`), because
  that dialect is case-insensitive and would otherwise walk straight into this; a model CARD is left
  alone, where `M` means what circuitRF means by it.

Gate: 33 in `DiodeModelTests`, 14 in `tests/Engine.Tests/Devices/NativeModelTopUpTests.cs` over
`testdata/A3/*.cnl` — the whole path, elaborated and solved by the real engines, because every one of
these features leaves the model class correct and the answer wrong if it fails to survive elaboration.
Oracles are closed forms or a second netlist, never a stored number. Suite 6,196 pass.

**NOT done here, deliberately:** the bipolar model stays on the compiled path (A1), not native — the
kit's own authors are migrating there, and a native implementation is permanent maintenance of someone
else's physics. `Rs` and `Bv` carry no temperature coefficients of their own.

Reading the SPICE dialect (2026-08-03) — COMPLETE. `src/Core/Netlist/Spice/` reads a netlist and its
model cards into the same `Library` of `Cell`s circuitRF's own `.cnl` produces, so a subcircuit read
from one is treated exactly like a cell the user drew.

**A sibling of `KitNetlistReader`, not an extension of it.** That reader takes a different format;
what carries across is its SHAPE — an honest note per line it could not use, an explicit set of cells
whose definition was only partly read, and no supplier named anywhere. Bending one reader to cover
two formats puts two grammars in one state machine and loses exactly those properties.

**`M` is MILLI here and circuitRF's own table says MEGA — this is the load-bearing fact.** The SI
table is case-sensitive (`M` mega, `m` milli); this dialect is case-INsensitive and `M` is milli in
either case, with mega spelled `MEG`. A capacitance written `1M` read through the SI table is 10⁹
times too large, and it parses, stamps and converges. So **every literal is resolved in
`SpiceNumber` and handed on as a plain decimal**, and the SI table is never consulted for a suffix
that came out of this dialect. `MEG`/`MIL` are matched before `M` for the same reason — one-character
matching first reads a megohm as a milliohm and carries on.

- **A power-of-ten prefix is applied by RE-READING the literal with the exponent appended**, not by
  multiplying: `3.0 * 1e-9` is `3.0000000000000004e-9` while `3e-9` is the nearest double to what the
  file wrote. Numerically irrelevant, and it reads back out as noise, which is what a user sees.
- **After an explicit exponent no prefix is applied** — `1e-12F` is one picofarad, not 1e-27. Found by
  the test, not by review: read the other way it scales twice, quietly, in the one notation a careful
  author reaches for precisely to be unambiguous.
- **The trailer after a prefix is LETTERS ONLY.** Admitting `/` so a unit like `F/m` could be
  swallowed whole eats the division in `1/2` and yields `12` — a wrong number out of a valid
  expression.
- **`A` for atto is deliberately absent**: no card uses it, a bare `A` meaning amperes is plausible,
  so recognising it can only ever turn a value into 1e-18 times itself.
- **`1F` is one FEMTO-unit, not one farad.** The dialect's own sharp edge, kept rather than smoothed —
  a reader that "fixed" it would disagree with every file it is meant to read.

**A rewritten value carries NO whitespace, and that is a requirement rather than tidying.** circuitRF's
generic instance-line parser splits on whitespace and reads bare words as nets, so `if(a, b, c)` comes
back from a `.cnl` round trip as a value plus two phantom nets — every later node index shifted, and it
still runs. This is the trap already recorded further down this file, reached from a new direction.

**`tests/Core.Tests/Netlist/SpiceNetlistRoundTripTests.cs` exists because reading is not the run path.**
The run path is `CnlWriter → .cnl → CnlReader → elaborate`, so anything the writer cannot say is gone
before the elaborator sees it — the same shape as the three losses already recorded below, each of
which looked like an extractor bug and was not. Assertions are made after RE-READING.

**A passive's third word: value or model name?** Nothing in the word settles it and both spellings are
ordinary. Brackets and numbers are values beyond doubt; a bare identifier is decided by a question the
reader can actually answer — **is a parameter of that name in scope?** Reading `R1 in out rtop` as a
model reference gives a resistor with no value pointing at a card that does not exist.

**Everything else is taken from the END of the bare words** — one rule covering a three- and a
four-terminal transistor and a subcircuit call of any width, with a trailing bare NUMBER popped first
as the positional `area`.

**A model card is NOT given a design-layer type.** It is the parameter block of whatever device
implements its type — a built-in, a compiled model behind a provider, or nothing yet — so it is carried
on the result and bound by whoever supplies that device. A `D`/`Q`/`M` instance's `Reference` is the
card's name, so it fails loudly until something answers to it rather than silently elaborating to a
default. The type and its bracket are read off the RAW text, because the bracket is routinely glued
(`nmos(level=54)`) and the tokeniser keeps a bracketed run whole — off the word list the type is
spelled `nmos(level` on every such card.

> **Corrected 2026-08-08.** That rule was implemented by searching the WHOLE card for its first `(`,
> and a bracket-less card routinely carries one inside a parameter VALUE hundreds of characters in
> (`dlq='5.2e-08-((1-pre_layout)*0.0)'`). Everything before it became the "type" and the card was left
> with **zero parameters** — a model reference resolving to nothing and a parameter set that vanished,
> neither of which reports itself. Measured's MOS card: `ModelType` was the whole rest of
> the line and `Parameters.Count` was 0; it is now `mdla_va` and **377**. The bracket only opens the
> block when it is part of the type's OWN word, and a detached `type (a=1 b=2)` is unwrapped in the
> other arm. Gate `S13b`. **Any earlier real-kit measurement in this file that counts cards is still
> right about the count and was wrong about the type of every card carrying a bracketed value.**

**"Incomplete" means the DEFINITION was damaged, not that a type is unfamiliar.** A skipped analysis
directive leaves the circuit exactly as written and does not mark anything; an unreadable line of the
definition does, because what is left is a plausible-looking different circuit. An unfamiliar device
type marks nothing either — it is very often a device a provider supplies, and marking it reports the
working case as broken. Simulator directives are listed and named rather than merely unknown: a file
full of them must read as understood.

**A conditional that cannot be evaluated takes NO branch.** Reading it as false silently deletes the
guarded block and leaves a cell that builds and is wrong; taking no branch and marking the cell
incomplete says "circuitRF could not read this", which is true, rather than "the file said no", which
is a claim nothing here can make.

**Sections are ALTERNATIVES, so an unrequested one is skipped AND named.** Reading a library file whole
defines the same parameters several times over; choosing a corner nobody asked for is a guess.
`.lib <file> <section>` reads one; `.lib <section>` … `.endl` marks one.

**Statistical distributions are reduced to their nominal value and REPORTED** (`SpiceNetlistResult.Statistics`).
circuitRF does not sample distributions and this does not add that. Doing the reduction silently is the
bad outcome — the number that comes out is indistinguishable from one that carried no distribution at
all. Bracketed only when the nominal is compound, so a card's plain `0.4` does not become `(0.4)`.

**Inclusion is guarded by file IDENTITY, and the ROOT file is registered before it is read** — otherwise
the root is the one file that can be entered twice, and a cycle back to the top recurses to the depth
limit instead of being reported as what it is. Notes carry the file as well as the line: an included
file's line 12 and the including file's line 12 are different lines.

**A directive's name is the leading dot plus letters, NOT the first whitespace-separated word.** A
condition is written `.if(x==1)` as readily as `.if (x==1)`, and the tokeniser keeps a bracketed run
whole — so the glued spelling's first "word" is the whole directive, matches no case, and falls
through to the switch's last arm. That arm is `.endif`, so every conditional in a file written that
way unwinds the wrong construct.

**VERIFIED against a kit's model libraries (2026-08-03) — 32 of them, and it found four
defects.** Measured after the fixes: 57 subcircuits, 75 model cards, **notes down 241 → 98**, and the
remaining 98 are all legitimate reports (a corner section redefining a model, an unrequested section
skipped). Statistical uses rose 32 → 130, which is itself the proof the first fix landed.

- **A mismatched `.ends <name>` was a HARD ERROR and should never have been.** `.ends` closes the
  innermost open subcircuit whatever name follows it — the dialect's own rule, and every simulator
  reads it that way — so the nesting is never in doubt and the name is decoration. The kit has one
  stray suffix (`.ends diodevdd_4kv_mod` closing `diodevdd_4kv`) and refusing threw away an ENTIRE
  model library over it. Now a note.
- **`name =value`** — the `=` glued to the VALUE rather than the name. Two words, one binding. The kit
  writes all 75 of its statistical parameters that way; both halves were falling through as bare
  words, so the file read cleanly, reported nothing, and declared none of them.
- **`.params` is the plural spelling of the same directive**, and the kit uses both.
- **`N` is how a device backed by a COMPILED model is instantiated** — the join to the OSDI worker.
  Its terminal count is the model's, not the letter's, which is exactly the case the
  take-the-name-from-the-END rule was written for; observed as a four-terminal resistor with a
  thermal node.

Gate: 91 tests — `SpiceNumberTests` (34), `SpiceExpressionTests` (19), `SpiceNetlistReaderTests` (32),
`SpiceNetlistRoundTripTests` (6). All fixtures synthetic; the repo commits no third-party kit data.

**NOT done here, and deliberately.** Nothing yet turns a read subcircuit into a placeable PART — that
needs symbols and part discovery, which is its own piece of work. `V`/`I` sources and the remaining
element letters are reported and skipped rather than read: their value forms are a small language of
their own (`PULSE`, `SIN`, `DC … AC …`) and a device definition rarely contains one. And the `m=`
multiplier is carried faithfully but nothing applies it — it is cross-cutting and belongs in
elaboration, not in each model.

Hosting an openly-specified compiled model ABI (2026-08-03) — COMPLETE for a native host platform.
`tools/osdi-worker/` is a **third** worker, beside `senior-worker` (a proprietary model ABI) and
`netlist-worker` (a library that describes a circuit). It shares no ABI with either — same
relationship those two already have — and it reaches circuitRF through the existing provider seam
with **no engine change and no new component type**.

**Why this ABI is worth hosting, and it is not a preference.** Its four load functions map onto
`ComponentModel.Evaluate`'s `(i, q, dg, dc)` essentially one to one, because the interface separates
**resistive** from **reactive** natively — the charge formulation HB wants, rather than a transient
derivative to undo. One integration therefore reaches an entire ecosystem of compact models instead
of one supplier's.

Three ABI facts established by READING a reference host, not by inference — each would have been
silent:

- **Residual offsets are byte offsets into the INSTANCE struct**, read as
  `*(double *)((char *)inst + off)`, with `UINT32_MAX` meaning the node has none. Read as indices
  into an output array they would have produced plausible numbers out of the wrong memory.
- **The `load_spice_rhs_*` pair must NOT be used.** Those return a *linearized* right-hand side in
  SPICE's own convention — which is exactly why the reference host's DC path uses them. circuitRF
  wants raw `i` and `q`, which the residual offsets give directly. The SPICE pair converges, to a
  different formulation's answer.
- **The Jacobian is written through HOST-INSTALLED pointers and it accumulates.** Scratch doubles are
  installed at the declared offsets and scattered into `G`/`C` by each entry's own node pair; the
  scratch is zeroed per point rather than assumed overwritten, because in a real host several
  instances share one matrix entry.

**Node collapsing is DECLARED by the model, so a slaved node costs nothing here.** The other worker
needs a run-time alias map precisely because its ABI cannot say *which* node a degenerate one
follows — the measured difference there was 279,127 iterations at residual 35.6 versus 5 at 7.6e-12.
That whole problem does not recur on this path, and `probe` correctly answers with nothing, leaving
the create-time report standing.

**It is answered at `create`, not at `describe`, and the reporting was DEAD until 2026-08-03.** Which
nodes collapse depends on the parameters the instance was given — a zero series resistance degenerates
a node a nonzero one leaves free — so it cannot be a property of the type. The worker emitted the
array from the beginning; `DeviceWorkerProvider.Create` never read it, so a collapsed node was still
minted a free unknown and the whole "it comes for free" claim was untrue in code. `ReadCollapsedNodes`
+ `ApplyCollapsedNodes` fold it onto a **new** descriptor record (the cached type descriptor is shared
by every instance, so writing into it would let one instance's collapse degenerate a node on all the
others), and it is applied *before* the probe so a probing worker refines the collapsed shape rather
than erasing it.

**"Grounded" is a SEPARATE claim from "slaved", not `SlavedTo = 0`.** `node_2 == UINT32_MAX` on the
wire (`"to": -1`) means tied to the ground reference; node 0 is an ordinary pin, usually wired to
something interesting, so conflating the two would ground a device's own first terminal.
`ExternalNodeDescriptor.CollapsedToGround` carries it and the elaborator gives such a node index 0.
This is the shape a model reports for a thermal node with self-heating switched off.

**An EXTERNAL pin reported as grounded is REFUSED, deliberately.** The user wired a net to it, and
both available readings are wrong and silent: give the pin node 0 and the user's net is left floating
rather than shorted; ignore the report and the device solves a node the model says does not exist.
Neither shows on screen. A node reported both grounded and slaved is likewise refused — the two name
different masters and nothing here can tell which is meant.

Gate for all of it: `O8`/`O9` in `OsdiWorkerTests` (the test model's `crf_collapse` declares both
flavours and decides them from its own parameters, so the same provider yields a collapsed and an
uncollapsed instance — the uncollapsed one is what stops the test passing vacuously; `O9` then shows
the collapse in the ANSWER, since a report that is carried and never acted on is indistinguishable
from one that is wrong), plus 5 in `tests/Engine.Tests/External/GroundCollapsedNodeTests.cs` carrying
it through elaboration and the S-parameter assembly.

**Temperature rides as its own reserved key, never as a model parameter.**
`DeviceWorkerProvider.ReservedTemperatureKey` (`__temperatureK`, KELVIN) is lifted out of the
parameter dictionary at `create` and written as a top-level field; `__`-prefixed keys are skipped by
the descriptor check, which of course does not declare them. Writing it as a parameter would give a
model that happens to declare that name the value twice, with the two meanings competing. This is
where **A0 pays off** — and the gate observes it in the ANSWER (the test model's conductance carries
a coefficient, so 400 K must give twice what 300 K does), because a temperature that never lands
still produces finite, entirely plausible currents.

**`osdi.h` is third-party and stays BYTE-IDENTICAL** (MPL-2.0, its own notice intact, in its own
file — it does not touch the MIT core). It is the ABI contract: the struct layout must match the
producing compiler's exactly, so a hand-copied or tidied version is a silent corruption. Its
`PARA_KIND_*` macros are *signed* expressions overflowing at `3 << 30`; the masks are therefore
re-expressed as unsigned at the worker's own call sites, never by editing the header.

**`tools/fake-osdi-model/` is a test-only library, the same bargain `tools/fake-model-lib` strikes** —
the real producers of these files are GPL-3.0 Verilog-A compilers that must never be a build
dependency. It is NOT a model: every device has a closed-form answer written in its comment, so the
gate asserts against arithmetic rather than against another implementation.

**The native build cannot fail the build.** `tests/Core.Tests` runs `build.sh` with
`ContinueOnError`; a machine with no C compiler reports the gate **Skipped with a reason** naming
what to run, via a `FixtureFact` added to Core.Tests for the purpose.

**A design can name one, and this needed NO new production code.** The existing manifest already
carries a command plus arguments, already resolves a bare command name against circuitRF's own tools
folder, and already makes a relative argument absolute against the manifest's directory — an OSDI
library is exactly "which model library the worker should load", the case that mechanism was built
for. A second, OSDI-specific launch path would have been a parallel road for no gain. `dotnet build`
now copies the worker beside the application the same way it keeps the other workers in step.

**NOT done, and the honest list:** host platform only,
no cross-compilation and no VM entry — and unlike the other worker it may need neither, since a user
compiles these natively, which is worth confirming before building any of it; and the
`param_opvar[]` ordering convention is read **defensively** (each entry's own `flags` decides
model-vs-instance) rather than assumed — unconfirmed against a real compiled model.

Gate: 9 tests in `tests/Core.Tests/Devices/External/OsdiWorkerTests.cs`, driving the worker as a real
process through the real `DeviceWorkerProvider` — describe, closed-form `i`/`q`/`dg`/`dc`, temperature
observed in the answer, a 2000-point batch (which only passes if partial reads are looped on both
sides), an undeclared parameter refused, and a non-library refused. Plus `tools/osdi-worker/verify.py`
at the protocol level. Suite 6,076 pass, 0 fail.

**One unexplained failure, recorded rather than dismissed.** The full solution went red once with a
single `Engine.Tests` failure on the first run after the worker binary was built, and **the name was
not captured** — which is the mistake, not the failure. Two subsequent full runs are clean, and
`Engine.Tests` alone is clean. Per this repo's own standing note, isolated repetition proves nothing
about a load-dependent race, so this is NOT called flaky. If it returns, capture the name first.

Design-wide ambient temperature (2026-08-03) — COMPLETE. `src/Core/Devices/Temperature.cs` is now the
one definition of temperature for every component model, and a design states its ambient as the global
**`temp`**, in °C.

**Scope correction worth recording, because the opposite was believed for a while.** Temperature was
NOT missing from circuitRF — the FET family and `Diode` have carried per-instance `Temp`/`Tnom` in
degrees Celsius, with the published scaling forms, since the FET family landed. Three things were
missing, and only those: an **ambient** (every device defaulted to its own extraction point, so there
was no way to run a whole circuit at 85 °C without editing every instance), **`Dtemp`** (a rise above
ambient, which is the only thing a subcircuit can meaningfully state about itself), and a **route from
the design into the factory** — `TryCreate` saw per-instance parameters and nothing else.

- **`temp` is an ordinary global, deliberately, and this is NOT a `.cnl` format change.** Globals
  already round-trip, already resolve through the expression machinery, and are already overridden
  per point by `ParametricSweepEngine`, **which re-elaborates every point** (`ParametricSweepEngine.cs`,
  the `Elaborate` inside the sweep loop). So a temperature sweep needed no new mechanism at all — that
  is what `A7` pins, by reproducing the engine's own override-and-re-elaborate without depending on it.
  A directive would have been a format change for a capability the format already had.
- **It is REPORTED when present.** The user did not ask for `temp` to mean this, so a design that
  happens to use the name for something else must not have its meaning changed in silence. A design
  with no `temp` says nothing — asserted, because an ordinary design acquiring a temperature message
  would be noise on every run.
- **`Temperature.ResolveDeviceC` is the ONE rule** — `Temp` (absolute) beats `Dtemp` (ambient + rise)
  beats ambient — so the FET family and the diode cannot answer the question differently. Stating both
  is resolved (`Temp` wins) **and said out loud**: the two together cannot both be what the author
  meant, and a silent discard is found months later.
- **Ambient must NEVER move `Tnom`, and that is the silent failure this area is most prone to.**
  `Tnom` is the parameter set's own extraction temperature — a property of the model card, not of the
  run. Move both together and ΔT is zero at every ambient: every temperature relation collapses to the
  identity while the device still looks temperature-aware and every number stays finite. `A6` is the
  guard, asserted as a relative difference on a FET whose `Beta` carries a coefficient.
- **Additive by construction.** With no `temp` global the ambient IS `Temperature.NominalC`, so a
  design that says nothing about temperature elaborates to exactly what it did before. `A1` pins that,
  and the whole 6,070-test suite is the wider proof.
- **`Temperature` is not on `FetModelBase` any more.** It was never FET plumbing — `Diode` already
  reached for the same nominal and the external-device boundary will too. `FetModelBase.NominalTemperatureC`
  stays as a forwarding alias so no model, factory call or test changed.
- **26.85 °C is 300.00 K exactly**, verified as a bit-exact IEEE-754 double sum rather than repeated
  from the old comment. That exactness is what lets a device stating no temperature collapse every
  relation to the identity with no residual drift. Do not "tidy" it to 27 °C.

**Trap confirmed while building this, and already guarded in production:** `new Variable(name, expr, "")`
throws `Unknown unit ''` — `Evaluator.ApplyUnit` returns early on `null` only, and an empty string falls
through to `Units.Scale`. `ParametricSweepEngine` carries an explicit `IsNullOrEmpty(baseUnit) ? null`
guard for exactly this. **`null` means "no unit"; `""` is an error.** `ResolveAmbient` degrades and
reports rather than throwing, which is how one probe found it.

**NOT done here, on purpose:** passives (R/C/L) still ignore temperature — TC1/TC2 is device physics,
not plumbing. And `CreateExternalDeviceModel` does not yet receive the ambient: handing a temperature
to an out-of-process provider is a protocol question, and it belongs with the work that adds one.

Gate: 6 tests in `tests/Core.Tests/Devices/TemperatureTests.cs` (including one that recovers a
default-constructed diode's `Vt` from its own conduction current, making the extraction provably
numeric-identity rather than probably harmless) and 9 in
`tests/Core.Tests/Elaboration/AmbientTemperatureTests.cs`. Suite 6,070 pass, 0 fail.

Built-in large-signal FET family (2026-08-02) — COMPLETE, palette included.
`src/Core/Devices/Fet/`: `CurticeQuadraticFetModel`, `CurticeCubicFetModel`, `StatzFetModel`,
`MaterkaFetModel`, `AngelovFetModel`, over a shared `FetModelBase`. Types `FET_Curtice`,
`FET_CurticeCubic`, `FET_Statz`, `FET_Materka`, `FET_Angelov`.

**Each model is its own type with its own parameter set, deliberately — they are not variants.**
Several use the same spelling for different quantities: `Beta` is a transconductance parameter in the
quadratic law and a gate-voltage-shift-with-drain-bias in the cubic one. One shared parameter block
would silently mis-feed whichever model the user did not have in mind, and the result would simulate.

- **Three nets, two ports.** The user draws `gate drain source`; the model is (gate,source) and
  (drain,source), so `v[0]` is Vgs and `v[1]` is Vds — the coordinates every published FET equation
  is written in. `Elaborator` expands the three declared nets into the four the port pairs need,
  the same mechanism `Tuner`/`P1Tone`/`Diode` use.
- **Derivatives are ANALYTIC, not finite-differenced.** A finite-difference Jacobian inside a Newton
  loop costs an extra evaluation per entry and is least accurate exactly where the device is most
  nonlinear. The gate test compares every model's `gm` and `gds` against a central difference over a
  25-point bias grid, and names the model in the failure message — a bare tolerance failure would not
  say which law is wrong.
- **Below pinch-off the current AND both derivatives are exactly zero.** A fudge conductance would
  put current where there is none; the DC engine's own gmin already keeps the node solvable.
- **`Cgd` bridges gate and drain, so in (Vgs,Vds) coordinates it appears on BOTH ports and in the
  off-diagonals of `Dc`.** Dropping those cross terms is the classic plausible-but-wrong Jacobian;
  there is a test that checks all four entries and the charge consistent with them.
- **The Statz knee is continuous in value and slope by construction** at `Vds = 3/Alpha` — the
  cubic's derivative is zero there — so no smoothing is applied and none is needed.

- **Gate charge is SELECTABLE, because the published laws differ on it** — `CapModel` picks:
  `0` none, `1` constant `Cgs`/`Cgd` (default), `2` bias-dependent junction charge
  (`Cgs`, `Cgd`, `Vbi`, `Mj`, `Fc`), the standard depletion form applied to Vgs and Vgd separately
  and continued by its tangent above `Fc·Vbi`. Hardcoding one scheme would have been wrong for
  whichever models use the other.
- **The SOURCE IS AN INDEPENDENT TERMINAL.** Writing the law in (Vgs, Vds) is a coordinate choice,
  not a common-source restriction: the source is an ordinary net the user wires anywhere. A gate
  test lifts the whole device off ground and shows only the differences matter, and an elaboration
  test builds a source-degenerated stage — the configuration a common-source-only device could not
  represent at all.
- **Temperature dependence is modelled, in the published forms**, which are NOT all the same shape:
  `Beta` and `Alpha` scale as `1.01^(tc·ΔT)` (their coefficients are *percent* per degree), `Gamma`
  as `1 + tc·ΔT` (a plain fraction), and `Vto` shifts additively as `Vto + Vtotc·ΔT` (volts per
  degree). Confusing the first two costs ~4 % at ΔT = 100, which is well inside the range these
  exist for, so there is a test that asserts the exponential form *specifically* — and first asserts
  the two candidate forms actually differ at the tested point, so the check cannot pass vacuously.
  Shared with them: `Vbi` falls with temperature, `Cgs`/`Cgd` follow it, and the gate saturation
  current rises via `Xti`/`Eg`.
- **A model accepts only the coefficients whose parameters it HAS.** `CurticeCubic` takes `Gammatc`
  and no `Betatc`, because its `Beta` is not a transconductance parameter; `Materka` and `Angelov`
  take no `Betatc` at all. Mapping `Betatc` onto whatever each law happens to use as its current
  scale would be fitting, not implementing.
- **`Temp` and `Tnom` are DEGREES CELSIUS everywhere**, including `Diode` — whose model constructor
  still takes kelvin, with the conversion at the factory boundary where it belongs. Two components
  in one palette must never read the same parameter name in different units. Both default to the
  same value, so a device with no stated temperature is evaluated exactly at its extraction point
  and every relation collapses to the identity; a gate test asserts that collapse is EXACT, with
  large coefficients supplied deliberately, because that is what catches a °C/K mix-up.

**NOT modelled:** the Statz/TOM-family charge formulation. It is a different scheme, not a parameter
change — it works on a *smoothed effective voltage* (`Veff = ½{Vgs + Vgd + √((Vgd−Vgs)² + Vδ1²)}`,
then a second smoothing against `Vto`) rather than on Vgs and Vgd separately, so it needs its own
implementation and its own derivatives. Also absent: transit-time delay, breakdown, self-heating.

Gate: 23 tests in `tests/Core.Tests/Devices/FetModelTests.cs`, 8 in
`tests/Core.Tests/Elaboration/DeviceNodeExpansionTests.cs`, 15 in
`tests/Ui.Tests/DevicePaletteWiringTests.cs`.

**Palette exposure — see `src/Ui/CLAUDE.md` for the wiring.** All six are placeable, editable in the
Component Parameter dialog, and under a new `Devices` palette category.

Junction diode primitive (2026-08-02) — COMPLETE. `src/Core/Devices/DiodeModel.cs`, type `Diode`.

Standard exponential I-V with depletion and diffusion charge, optional reverse breakdown, and
optional series resistance. Parameters are the conventional ones — `Is`, `N`, `Rs`, `Cj0`, `Vj`,
`M`, `Fc`, `Bv`, `Ibv`, `Tt`, `Temp` — all optional, each defaulting to its usual value.

- **The equations are the standard ones**: exponential I-V, depletion charge
  `Cj0·Vj/(1−M)·(1−(1−V/Vj)^(1−M))`, diffusion charge `Tt·I`, optional reverse breakdown.
- **Both runaway regions are continued by their TANGENT, not clamped** — the exponential above
  `40·N·Vt`, the depletion charge above `Fc·Vj`. Value *and* slope stay continuous, which is what
  keeps Newton convergent; a clamp keeps the value finite and puts a kink in the Jacobian, which
  stalls the solve in a way that looks like a bad circuit. Two gate tests straddle each changeover
  and assert continuity of both.
- **`Gmin` defaults to ZERO, unlike SPICE.** The DC engine already adds `gmin = 1e-12 S` to every
  voltage node, so a device supplying its own doubles it exactly where it matters. Caught by a test
  that expected the closed-form current and got the leak term as well.
- **`Bv = 0` means "breakdown not modelled", never "breaks down at 0 V".** The wrong reading makes
  every reverse-biased diode conduct hugely and is silent; there is a test for it.
- **Series resistance is INSIDE the model, on a real internal node** — a separate resistor beside
  every diode is not required and would not scale to a part built from many of them. With `Rs > 0`
  the device is a two-port over three nets, `anode — internal — cathode`; `Elaborator` mints the
  internal node exactly as it does for `Tuner`/`P1Tone`/`ExtDevice`.
- **That internal node is a genuine unknown, NOT collapsed locally.** Solving `(V−Vj)/Rs = I(Vj)`
  inside `Evaluate` is exact at DC and **wrong in HB**, where the internal node carries its own
  harmonic content — at RF the junction capacitance shunts `Rs`, and a quasi-static collapse cannot
  represent that. Same reasoning as the `ExtDevice` internal nodes. `Rs = 0` stays a one-port, so a
  diode without series resistance costs no extra unknown.

**The oracles are the closed-form equations and finite differences, not stored numbers from another
simulator** — a golden file from elsewhere would only prove two implementations agree. `dQ/dV` is
checked against the returned capacitance by central difference, which is the real consistency check
between the charge and its derivative.

Gate: 23 tests in `tests/Core.Tests/Devices/DiodeModelTests.cs`. The series-resistance tests
solve the two-port pair by bisection and assert the internal-node KCL directly — both ports
carrying the same current at balance, and Ohm's law closing the loop across the resistor.
Core.Tests 920 pass.

A kit's symbols may live in a LIBRARY, not one file per part (2026-08-01) — COMPLETE for reading and
binding. `src/Core/Pdk/KitSymbolLibraryReader.cs`, bound to parts in `PdkImporter.DiscoverParts`.

**Why this shape matters.** Every symbol reader beside this one takes one drawing per file. A library
inverts it: many parts share a handful of templates and each part names the one it wants. Measured on
a kit — **7 templates serving 109 parts, in about four kilobytes**. So reading one small file is
what makes a whole kit placeable, and those same seven templates are what the palette should show.

- **Read best-effort and deliberately partial**, the same bargain `KitSymbolDefinitionReader` strikes:
  only the records whose layout is unambiguous — the symbol names and the terminals with their
  positions. The drawn body is not read, so a part gets correct, correctly-named pins and a body
  circuitRF draws itself. Pins decide whether a part can be WIRED; the rest is appearance.
- **A symbol library is its own asset kind, not symbol artwork.** It is not one part's drawing:
  matching it to a part by file name finds nothing, and counting it as unreadable artwork would warn
  about a file that reads perfectly.
- **The symbol outranks a name-matched subcircuit on terminal count.** The kit DREW it, and a part
  naming one is stating how many pins it has.
- **Keyed by extension, not content** — the payload is binary and `PeekText` returns nothing for
  anything containing a NUL, by design.

**Two bugs found by running it against a real library rather than by inspection**, both silent:

1. **Record delimiters were being read as part of the pin name.** Records are bracket-framed, so a
   name sits hard against its own record's terminator and the next one's opener; taking every
   printable byte read a pin called `1` as `1][`. No crash, nothing obviously wrong in a dump — every
   pin on every symbol renamed.
2. **A kit's catalog and its own symbol library can spell the same symbol differently.** One
   references `A_B` for a symbol it declares as `A B`. Matching only exactly cost **18 of 109 parts**
   their pins, silently, since a pinless part still imports and still appears. Now matched
   separator-insensitively through the same `Normalize` already used for artwork.

**Result on that kit: 109 of 109 parts carry pins** — 2-pin ×16, 3-pin ×69, 4-pin ×24, matching the
families exactly, and matching the pin counts derived independently from the network data's own port
labels.

**`PdkPart.Pins` carries the template outward**, and the Ui half is now done too —
`src/Ui/Schematic/KitTemplateSymbol.cs` builds a placeable symbol from it, tried only after a
per-part drawing so a part that has its own keeps it. **End to end on that kit: 109 parts → 109
palette entries → 109 symbols installed, 0 omitted.** Before it, every one of them was dropped from
the palette as unplaceable.

- **The pins are the kit's; the body is circuitRF's own.** The library states terminals, not artwork.
  Drawing a body we cannot read would be inventing the kit's drawing; drawing none would leave pins
  floating with nothing to click.
- **Placement SHARES the drawing reader's scale, snap and axis flip** rather than reimplementing
  them (`DsnSymbolReader.PinGrid`/`SnapToPinGrid`/`ChooseScale`, widened to `internal`). Pins must
  land on exact multiples of the connection grid or a wire will not attach, and two rules for that
  would drift at the first change. It also means library-backed and drawing-backed parts put their
  pins in the same places.
- **A two-terminal part still gets a body.** Its pins are colinear, so one dimension of their
  bounding box is zero; without a floor the body collapses to a line nobody can see or click.
- **`MakeKitPart` is shared by both symbol sources**, because everything it does decides what a
  PLACED instance is — port count, provider binding, parameter interface — and two copies would
  drift the moment one changed.
- **`DsnSymbolReader.TranslationVersion` was deliberately NOT bumped.** Its rule is to bump whenever
  a change could MOVE a pin, because a workspace records the version its kits were translated under
  and moved pins silently disconnect wires. Nothing here moves a pin on the drawing path — the two
  helpers were only widened to `internal` — and the template path is new, so no workspace has one
  recorded. **It does now govern this path too**: a future change to `KitTemplateSymbol`'s scale,
  snap, body or ordering moves pins on library-backed parts and needs the bump.

Gate: `tests/Core.Tests/Pdk/KitSymbolLibraryReaderTests.cs` (10) and 5 binding tests in
`ComponentCatalogDiscoveryTests` — all synthetic, including a regression for each bug above.

A kit may DECLARE its parts in a catalog (2026-08-01) — COMPLETE.
`ComponentCatalogRecognizer` + a catalog pass in `PdkImporter.DiscoverParts`.

**The gap this closes, measured before and after.** `DiscoverParts` knew two shapes: a
`<cell>/<view>/<file>` database tree, or the subcircuits a netlist declares. A third exists — a kit
that lists its parts in an XML catalog (name, model, symbol, description; one entry per part) and
supplies their behaviour from a **compiled model library** rather than any netlist. Such a kit has
nothing to walk and nothing to parse, so it imported as a pile of recognised files with an **empty
palette**. Measured of that shape: **0 parts → 109**, grouped by catalog file.

- **Recognised STRUCTURALLY, never by a schema URI or namespace.** A namespace names the tool that
  wrote the file; keying off one would put supplier knowledge in circuitRF and would fail on the next
  kit with the same shape. A test proves a namespace-prefixed catalog reads.
- **The discriminator is an element that NAMES a part**, not the number of times the word appears.
  Counting mentions was tried first and wrongly rejected a catalog offering a single part — the rule
  was wrong, not the fixture. A document that merely *talks about* components is still not a catalog.
- **The identity taken is the MODEL name, falling back to the declared name.** Real catalogs disagree
  between the two: an entry named for a package variant can point at a shared model. Taking the
  display name would name a part nothing can resolve at run time.
- **Parsed by regex, not an XML parser** — deliberately. These files carry a default namespace, so
  element lookups would have to be qualified against a URI this code must not know. Matching the
  local name reads both spellings.
- **The catalog file name is the part's category.** A kit of this shape splits its catalog by family,
  one file each; it is the only grouping offered and it is a real one.
- **The catalog wins over the other two shapes** when present — it is the kit *saying* what it
  offers, rather than circuitRF inferring it from which files happen to be lying around.

**A zero-part import now ALWAYS says why**, and this is the half that matters most. Recognising files
and reporting nothing leaves the user with an empty palette and no reason for it, which satisfies
neither of the two distinct messages the three `PdkAssetSupport` states exist to keep apart. The
reason names which shape was found — a catalog that declared nothing readable, netlists whose
subcircuits the kit never draws, drawings not arranged as a cell database, or no declaration at all —
because those need different fixes.

**A compiled model library (`.dll`/`.so`/`.dylib`) is now recognised as model data.** circuitRF never
loads one into its own process; a device provider runs it out of process. Recognising it is what
turns "the parts do not simulate" into a message at IMPORT time — the existing provider warning now
fires — instead of a failure at Run.

Gate: `tests/Core.Tests/Pdk/ComponentCatalogDiscoveryTests.cs` (12, synthetic fixtures — the repo
commits no kit data). Core.Tests 883 pass, Firewall 4 pass.

An extracted network says where its externally-connectable ports stop — sometimes (2026-08-01) —
COMPLETE for the reading half. `src/Core/Pdk/TouchstonePortLabels.cs`.

**The problem this solves.** A kit part can be delivered as nothing but a Touchstone file, and that
file routinely declares **more ports than the part has pins**: it was extracted from a physical
structure with an opening left at every place a lumped component attaches. Stamping all of them as
pins builds a different circuit — and it still simulates, which is what makes it dangerous.

**Solvers commonly write `! Port[k] = <name>` alongside the data**, naming each port after the
geometry it sits on. That is a property of the FORMAT and of how such tools emit it; nothing here
knows anything about any supplier, and nothing may. RfCore already preserves comments
(`SNP.Comments`), so no file-reader change was needed.

- **The split is decided only by the file's own structure**: ports are external until the first port
  belonging to a **multi-terminal object**, since an object carrying several terminals is a place
  components attach, not a pin. A label's *group* is itself with a terminal suffix removed — both
  `name:1` (modal) and `name_T3` (terminal) are stripped, so a single-terminal port is its own group
  and is therefore distinguishable from a shared one.
- **When structure does not decide, the answer is `Ambiguous` — never a plausible-looking number.**
  Two real shapes hit this: every port naming a different object (a structure whose attachment
  points are one port each, far side grounded), and a multi-terminal object starting at port 1. Both
  are reported with a reason. A wrong split is silent, so "I cannot tell" is the only safe answer and
  the caller supplies the split as run-time data — the same rule as everything else in this area.
- **Labels are NOT necessarily near the top of the file.** One real file declares them at line 226,
  after a long variables block. Read them off the parsed network, never off a capped read of the text
  — a probe written the other way reported "no labels" for a file that has fourteen.

**Gate — and the oracle is exact, needing no reference simulator.** S-parameters are *defined* with
every other port terminated in the reference impedance, so terminating the attachment ports in Z0 and
measuring the external ones must reproduce the corresponding sub-block of the file's own matrix,
entry for entry. That is a real check on **port order**, the **reference-node rule** and the
**Z-expansion** — the three things most likely to be quietly wrong.
`tests/Core.Tests/Pdk/TouchstonePortLabelsTests.cs` (17) and
`tests/Engine.Tests/Linear/ExtractedNetworkExternalPortsTests.cs` (2, synthesised fixtures — the repo
commits no kit data). The second of those two is the guard that gives the gate teeth: leaving the
attachment ports **open** must change the answer, or a stamp that ignored them entirely would pass.

**Verified against kit data by disposable probe** (deleted after running), across four families
and port counts 6 to 27: the external sub-block reproduces to **1.7e-13 … 2.7e-11**, i.e. machine
precision. Two files correctly reported `Ambiguous` rather than guessing.

**NOT done here, and deliberately:** nothing yet turns such a file into a placeable part. That needs
the split as run-time data for the ambiguous cases, plus the lumped devices that attach at the
remaining ports — see the standing rule that a kit's own facts are data beside the kit, never
knowledge compiled in.

The provider-unavailable message names the usual cause (2026-08-01) — COMPLETE. `ExternalDeviceRegistry.Require`
now says the model is **usually a compiled one whose implementing library was not found**, that such a
library often ships as a separate package beside the kit, and what to do about it (reference the package
in File ▸ Manage PDKs, or supply a `device-provider.json`). Everything the message already said is kept.

**Named as the USUAL cause, not as a diagnosis.** This layer knows only that nothing answered to the name;
asserting the library is missing would be over-claiming. Leaving it out, though, sends the user looking
for a missing provider REGISTRATION when what is actually missing is a file on disk — which is the wrong
half of the system entirely.

**`DeviceWorkerProviderResolver.Describe`'s empty case now says what it MEANS.** It read "no kit folders",
which is literally true and tells a user nothing; it now reads "no kit in this workspace settled on a way
to evaluate its devices" — the reason there are none being the interesting part. The
nothing-was-ever-registered case is still worded separately, because it is a different situation with a
different fix.

Gate: `ProviderUnavailableMessageTests` (4).

Compiled device models on Windows (brief-windows-device-worker.md, 2026-07-31) — COMPLETE, with the
one gate that needs a Windows machine still open. The worker now builds and runs for Windows, and a
synthesised manifest names `win-x64` again.

**The Windows and Linux builds need the SAME 15 functions** — not a similar set, the same set, in
two manglings (`_ZN15DeviceInstallerC1EPKcPFivEi` vs `??0DeviceInstaller@@QEAA@QEBDP6AHXZH@Z` for
the fifteenth). There was no new ABI to work out and no vendor header to obtain.

**The one real problem was that a Windows model IMPORTS its host callbacks from a NAMED MODULE.** A
Linux model leaves them undefined and the loader resolves them against whatever loaded it (that is
what `-rdynamic` is for); an executable's exports are never consulted for a DLL's import-by-name, so
a module under that name has to exist at load time. Hence two products from one source file:
`crf-model-host.dll` holds the callbacks, the protocol and `crf_worker_main`; `senior_worker.exe` is
a launcher that derives the name, stages the DLL under it, loads it and calls in. **The logic is in
the DLL because the callbacks are not pure** — they write worker state, and splitting them from that
state would need a registration handshake and a forwarding thunk per callback.

- **The module name is read out of the model, never remembered** (`PeImports.ModuleSupplying`, and
  `derive_host_module` in the worker). The import descriptor is selected by matching **our own ABI
  symbols**; matching a remembered module name would put kit knowledge back in one string at a time
  and would silently serve nothing for a kit naming its host module differently. This is the third
  instance of the principle already load-bearing here — the ELF symbol-table scan instead of a
  compiled-in name list, the runtime alias map instead of a compiled-in table.
- **Two implementations of that walk exist, deliberately.** The C one runs inside the launcher,
  which has to do it before any managed code exists in its process; the C# one lets the RULE be
  exercised on every platform and lets the importer say whether a kit's Windows build is drivable.
  Keep them in step.
- **The staged shim is loaded EXPLICITLY, before the model** — Windows resolves an import by first
  checking whether a module with that base name is already loaded, so no `SetDllDirectory`, no
  `AddDllDirectory`, no `PATH` edit. The search-path approaches work by accident of ordering.
- **The staged copy is per-user, never the repo/install/kit** (`%LOCALAPPDATA%\circuitRF\hostshim\`).
  The file bearing the vendor's module name is created on the user's machine, from their own kit.

**`DeviceLibraryDiscovery.Find` gained a `LibraryFormat` filter, and it fixed a latent bug.** The
`preferPathContaining` hints only ever RANKED; they never filtered. So a kit shipping a single
library answered both a "find the Linux build" and a "find the Windows build" search with the same
file — harmless only while the `win-x64` entry was suppressed, and a launch failure the moment it was
written back. The filter is decided by the file's own **magic bytes**, not its extension: a vendor
names its folders for the toolchain and its files for whatever it likes, and the magic is the one
property that cannot be a naming convention.

**`DeviceLibraryDiscovery.ServedTypes` needed no change** — its plain byte scan is format-agnostic by
design, and that design is now paid off rather than merely claimed. Nor did `DeviceWorkerManifest`,
`MatchScore` or the process transport: a launch entry is `Command` + `Arguments` through an ordinary
`Process.Start`, and `win-x64` was already computed.

**Gate — the managed half.** `PeImportsTests` (15, everywhere — descriptor selected by our ABI
symbols, ordinal imports skipped, PE32 as well as PE32+, and a truncated/garbage image refused rather
than read past its end, all against PEs built in the test). `PdkWindowsWorkerManifestTests` (7 — the
`win-x64` entry is written again, names the Windows build, and carries a bare `.exe` command that
resolves in the tools folder wherever it runs; a Linux-only kit still gets no Windows entry).
**Verified beyond our own code:** the C# parser resolves `KERNEL32.dll` / `SHELL32.dll` out of a real
mingw-built binary's import table, and derives `crf_test_host.dll` from `tools/fake-model-lib`'s real
DLL.

**Gate — the native half, `tools/senior-worker/verify-windows.sh`.** None of this can be a C# test:
staging a file under a name read out of a model's import table, an import binding to an
already-loaded module, and stdio mode are properties of a RUNNING process. The script loads a real PE
under Wine and checks them. An unprivileged user with a **read-only** install stages the shim,
resolves the model's import against it, walks the PE exports to find the family, and completes a
`describe` → `create` → `eval` exchange with bit-exact currents; a second model naming a different
host module stages alongside the first; a newer shipped shim refreshes the staged copy. The Linux
worker drives the same fixture end to end too (adding `probe`).

**R-win-7 is proven load-bearing, not assumed.** The script builds a control with the two `_setmode`
calls neutralised and nothing else changed: the identical payload comes back corrupted and the stream
desyncs, while `describe` passes identically in both runs — which is exactly why the bug is easy to
ship and why a `describe`-only test cannot catch it.

**STILL OPEN — and it is the one that matters: no production library has been loaded on Windows.**
Wine is a reimplementation and the fixture is ours, so a PASS exercises the mechanism without
standing in for the kit. Unsettled until a Windows machine with a kit runs it: whether the 15
symbols are *sufficient* (they are demonstrably necessary); a CRT mismatch against a genuinely
UCRT-built library; whether the kit's own `an extra export` export wants anything at load time; and
the vectored exception handler under a real access violation. See `tools/senior-worker/README.md`.

**Bisected with the CLI, 2026-07-31 — the solver and the topology are NOT the problem.** A vendor
kit's package would not settle (`residual 35.6`, 279k iterations). Two substitutions in the generated
`netlist.cnl`, both run headless with `dotnet run --project src/Cli -- dc`:

- **compiled FET → 1 GOhm open**: converges, and to the right answer (n26 = 28 V, n24 = 3 V).
- **compiled FET → a square-law SDD** in the same socket, all five instances: **converges in 5
  iterations**, residual 2e-7, Ids 36 mA.

So elaboration, the 15-port package network, the supplies, the thermal wiring and the nonlinear DC
engine are all sound at this topology; what remains is the compiled model itself or how its ten nodes
(4 external + 6 internal, one thermal) are mapped. **Substituting into the generated netlist is the
cheap bisection for any kit-backed non-convergence** — it separates "our circuit handling" from "that
model" in one run, and needs no VM.

A bare word after a parameter is a UNIT, never a net (2026-07-31) — COMPLETE, and it was building a
different circuit in silence. `Units._scales` had `Ohm/kOhm/MOhm/GOhm` but **no `TOhm`**, and a real
kit ties every unused package pin to ground through `R=1 TOhm`. The unrecognised token fell through
to `nets.Add(tok)`: fourteen resistors all wired to one node named **`TOhm`**, that node constrained
by nothing, the MNA matrix singular with an all-zero row — and no message anywhere mentioning a unit.
Same failure class as brief-unit-token-phantom-nodes, one table entry further along.

- **`TOhm` and `mOhm` added** — the series had a hole at each end.
- **The parser no longer guesses.** Nets come first and are finished by the first `name=value`, so a
  bare word after that can only be a unit; one that is not recognised is now reported by name.
  Reported rather than absorbed, because the old behaviour produced a *working parse of a different
  circuit*, and the error is what makes a one-entry fix obvious.
- **Instance lines never stripped their trailing `;` comment** — only `SplitExprUnit` did, for
  variable assignments. So every word of a comment joined the net list:
  `C:C1 a b C=1m ; near-short at 2 GHz` gave a capacitor with eight nets, six named after English
  words. Invisible because a two-terminal model reads the first two and ignores the rest; found only
  because the stricter rule above threw on the `;`. Now stripped quote-aware, so a `;` in a file path
  survives.

Gate: 11 tests in `tests/Core.Tests/Netlist/BareTokenAfterParameterTests.cs`.

A kit's data files reach the guest by being mounted WHERE THEY LIVE (2026-07-31) — COMPLETE.
**The bug the whole macOS path had been walking towards.** A compiled model is told which data files
to read through its OWN parameters: this kit's FET declares exactly four (`File`, `TAMB`, `RTH`,
`CTH` — confirmed by the worker's own `pars=4` against the kit's
`KITLIB_DEVICE_v1:FET1 … File=File TAMB=TAMB RTH=RTH CTH=CTH`), so its entire parameterisation is a
data file plus three thermal numbers. Those parameters arrive from the netlist long after the VM has
started, so **unlike the model library there is no command line left in which to rewrite them**. The
model then refused every operating point — cleanly, `analyze_nl` returning 0 with no SIGSEGV — and the
only symptom was a non-finite result far downstream.

**So the path is made true rather than translated.** `crf-vmhost` gained `--share-at TAG=PATH[:ro]`,
which mounts a share at the same absolute path it has on the host; `guest-init` honours it via
`crf.mountat=<tag>,<base64 of the mount point>` (base64 for the same reason argv is — the kernel
command line is space-separated and unquoted, and a macOS path may contain a space; it cannot contain
a comma, so the split is unambiguous). Nothing anywhere rewrites a data path.

- **The tree offered is exactly the one `KitDataFileResolver` can anchor a file within** —
  `OutermostSearchRoot`, the same constant, deliberately not a second notion of "near the kit". Two
  would drift, and the failure when they do is a path that resolves perfectly at import and cannot be
  opened at run time, which is this bug one level along.
- **Anchored on the kit's NETLIST folder, not the import root.** They are not interchangeable: for a
  kit imported whole the import root is the delivery folder, and for one healed from an installed
  workspace it is the netlist folder itself.
- **A library override under a `--share-at` tree is left exactly as written** — the host path is
  already valid in the guest, and rewriting it to `/mnt/<tag>/…` would name a place nothing was
  mounted.
- **`generatedFormat` on our own manifests, bumped to 2.** "Stale" used to mean only that a named
  program had gone, so a manifest whose every path still resolved was never reconsidered — and one
  that runs is exactly the one nothing else would ever replace. An older format is now redone at the
  next workspace open instead of staying runnable and wrong.

Gate: 5 tests in `tests/Ui.Tests/PdkKitDataShareTests.cs` — including one that reads a data file
through `KitNetlistReader` and asserts the shared tree contains what it resolved, which is the
assertion that actually ties the two halves together — plus 1 in `VmHostLibraryOverrideTests`. **The
guest half is unverified by any automated test**: the Swift and the initramfs build and their
non-VM paths run, but nothing here can start a VM.

A failed evaluation point must not assert a cause the worker never reported (2026-07-31) — COMPLETE.
`senior_worker.c` sets `status[k] = 0` for **three different things** — the model's `analyze_nl`
returned 0 (it refused the point), a SIGSEGV was caught inside it, or a current came back non-finite
— and the wire cannot tell them apart. The message named only the last and went on to suggest the
bias was out of range, which reads as a diagnosis and sent a real investigation the wrong way. It now
says all three and names the two usual causes: a bias outside the model's valid range, **and a file
the model needs and could not open**.

- **The worker's own log is now attached here too.** A failed point arrives as a perfectly normal
  reply, so it never passes through `DeviceWorkerChannel.Failed` — the only place that used to attach
  worker output. Yet the log is the one thing that separates the three cases: the worker writes
  `eval: SIGSEGV caught` when the model crashed, and a model that cannot read a data file usually
  says so there.
- **`ExternalDeviceModel` no longer rethrows an `ExternalDeviceException` unchanged**; it adds the
  instance label. A worker can only name the TYPE, which is useless the moment a design holds several
  devices of one type — this kit's package holds five of `KITLIB_DEVICE_v1`, wired differently, two
  with gate and drain shorted and a thermal node joined to nothing else. Which instance failed is the
  first thing anyone asks.

Gate: 2 tests in `DeviceWorkerProviderTests` (the message names all three possibilities; the existing
failed-point test kept).

A worker must never outlive circuitRF (2026-07-31) — COMPLETE. **Found from a leaked VM still
running 23 minutes after the application that started it had quit**, and it was not presenting as a
leak: the NEXT run failed with `the connection failed (Broken pipe)` and no worker output at all,
naming neither the leak nor the run that caused it.

**Why a leaked worker is a real fault on macOS and not untidiness.** macOS allows only a few VMs at
once. A leaked one holds its slot indefinitely, because closing the pipe tells the GUEST nothing — a
virtio console has no end-of-stream to deliver, so the guest blocks on a read forever and the VM never
powers down. The next run then cannot start its VM and is **killed by the system before it can print
anything**, which is why the report contains no diagnostic.

**Windows and Linux need nothing, and that is checked rather than assumed.** There the worker is an
ordinary child on a real pipe: when circuitRF goes away by any means, the OS closes the write end,
`read_exact` sees `r <= 0` (`senior_worker.c`), the command loop breaks and the worker exits. The
virtio console is the *only* reason macOS needs any of the following.

- **`ExternalDeviceRegistry.ResetResolved()` now runs on `AppDomain.CurrentDomain.ProcessExit`**
  (`App.axaml.cs`). It was wired only to a workspace switch, so quitting left one worker per kit the
  design had used. Hooked on ProcessExit rather than added to `Quit()` because Quit is not the only
  way out — three paths reach `Environment.Exit` directly, and a fourth added later would silently
  not be covered.
- **That is not sufficient on its own, and cannot be.** No code of the caller's runs on a crash or a
  `kill -9`. So `crf-vmhost` also ends ITSELF when the caller goes, on two independent triggers: the
  parent process exiting (a `DispatchSource` process watch — this is the one that makes the leak
  impossible rather than merely unlikely), and its own stdin reaching end of stream (the caller's
  deliberate shutdown signal, and the only one available while the application is still running).
  Exiting is enough to take the VM with it: the machine's state belongs to that process.
- **The parent can die between reading its id and the watch being armed**, leaving the source
  registered against a process that is already gone so it never fires. Re-reading `getppid()`
  afterwards closes that window — a reparented process reads 1.
- **A startup reaper for orphaned VMs was considered and NOT added.** With the watch in place a leak
  can no longer be created, so a reaper would only ever collect strays from a build that predates it
  — a migration aid, permanently on, that kills processes by heuristic. The one existing stray was
  cleared by hand instead.

Gate: 3 tests in `tests/Core.Tests/Devices/External/ResolvedWorkerTeardownTests.cs`, over a real
worker process — teardown reaches the worker through both `ResetResolved` and `Clear`, and a provider
the HOST registered is left alone (the same bug in the opposite direction). Identifying the worker by
diffing the process table was tried and is wrong: other test classes start the same reference worker
concurrently, so the diff is whoever happened to launch at that moment — it flaked in a full-solution
run and passed alone. The transport the resolver built is kept instead. **The Swift half is
unverified by any automated test** — it compiles, signs and its non-VM paths run, but nothing here can
start a VM.

**A fast-dying worker's own words were being lost, and the first attempt to settle this reached the
wrong answer (2026-07-31).** Error output arrives on a background reader, so a worker that dies during
start-up can be reported before a single line is delivered — leaving `(Broken pipe)` and nothing else,
in exactly the case where the worker's message is the only description of what happened.
`ProcessDeviceWorkerTransport.RecentErrorOutput` now waits for the reader to finish first.
`WaitForExit()` with NO timeout is the only overload that waits for the redirected readers to reach
end of stream; the timed one returns as soon as the process is gone and leaves output in flight. It is
run off-thread under a 2 s bound, and only ever on a process that has already exited.

**It only reproduces under load, and that is the part worth remembering.** 12 isolated runs passed and
were taken as proof the race did not exist — the wrong experiment, and the wrong conclusion drawn from
it. Measured properly afterwards: **5 failures in 12 full-solution runs without the fix, 0 in 12 with
it**, and 0 in 40 runs of the test on its own either way. If
`AWorkerThatDiesImmediately_StillReportsWhatItSaidOnTheWayOut` fails, it is not flaky — and it must
not be "stabilised" by waiting for the process to be reaped, because that wait IS the grace period the
test exists to remove.

A per-instance model-library override (2026-07-31) — COMPLETE. `ModelLibrary` is circuitRF's own
file-valued parameter on every kit part, blank by default: set it and just that instance is evaluated
with a different library, which is what makes two revisions comparable side by side in one schematic.

**It travels in the PROVIDER NAME** (`kit|path`, `DeviceWorkerProviderResolver.ComposeOverride`)
because that is what `ExternalDeviceRegistry` keys on — two instances naming different libraries must
get two providers, or the second is silently evaluated by the first's models.

**The argument replaced is the one that NAMES a shared library** (`.so`/`.dll`/`.dylib`) — a checkable
property of the value, not its position. A worker's arguments are the kit's to arrange, so replacing
"the last one" would be reading a habit; appending would hand the worker two libraries and let it
choose. Nothing to replace, or more than one candidate, is reported.

Gate: 7 tests in `tests/Core.Tests/Devices/External/ModelLibraryOverrideTests.cs`.

**A chosen library is put through the VM's SHARE mechanism on macOS, never handed over as written
(2026-07-31)** — the first real-kit bring-up failure, and it looked nothing like an override bug. The
kit's own library reaches the worker as `/mnt/kit/…` because the worker runs inside `crf-vmhost`; the
substitution wrote the chosen path in verbatim, so a path on the **Mac** replaced a working **guest**
path. The VM then started perfectly and failed inside the guest with `dlopen … No such file or
directory`, naming a file that plainly exists — the reported path is on the host, and the host's
filesystem is not the guest's.

`VmHostArguments` is now the ONE place that knows the contract's two halves — a directory is offered
as `--share TAG=PATH` and the guest sees it at `/mnt/TAG` — because anything writing one half without
the other produces exactly this failure. `PdkPartInstaller` builds the osx entry through it too, so
the importer and the override cannot drift apart.

- **An existing share is reused whenever it already covers the file**, which is the common case and
  not a nicety: another revision of a library normally sits beside the kit's own. It also keeps the
  guest command short, and the kernel command line carrying it is a fixed-size buffer that
  `crf-vmhost` refuses to overflow.
- **Only the argv run INSIDE the guest can name the library to replace.** The VM host's own options
  describe the machine, not the work, and a share value can carry a `.so`-shaped directory name.
- **A new share is inserted BEFORE the `--`**, so the option stays an option; past it, the share
  would be lost and the worker would get a flag it never asked for.

Gate: 12 tests in `tests/Core.Tests/Devices/External/VmHostLibraryOverrideTests.cs`. **Verified to
fail without the fix** (4 red) and **verified against a kit**: the workspace's own manifest
plus the host path the schematic actually carries now resolve to `/mnt/kit/RfPowerDesignKit.so`,
reusing the kit's share and adding none.

**Platforms, verified against a kit rather than assumed.** It ships Linux x86-64 ELF and
Windows only — no macOS build at all. So Windows and Linux x86-64 load the library natively; macOS
cannot load a Linux ELF under any circumstances, because that is a binary-format and OS-ABI mismatch,
not an instruction-set one. Rosetta 2 translates x86-64 *macOS* binaries and does not apply. What does
apply is **Rosetta for Linux** — Apple's Virtualization framework offering Rosetta *inside* an arm64
Linux VM so it can run x86-64 Linux binaries. The VM is not optional; Rosetta only makes it fast. That
is exactly what the working macOS entry does today (`orb -m <vm> …`), and it needs no special code:
per `MatchScore`, it is just another platform's command.

**Re-verified exhaustively (2026-07-31), because "can macOS run this out of the box" keeps recurring:**
the kit carries **34 build directories, every one Linux or Windows** — zero `darwin`, zero `.dylib`,
nothing macOS-shaped anywhere in the tree. There is no vendor build to fall back to, and the reason is
structural: the kit targets a simulator that itself ships Linux/Windows only.

**So on macOS the VM is not a workaround, it is the only mechanism — the open question is only WHO
SUPPLIES IT.** macOS has no Linux ABI personality (no `linuxulator`, no WSL1-style pico process), so
there is nothing to load a Linux ELF into. The three real options, stated once so they are not
re-derived:
1. **The user installs a Linux VM** (OrbStack/Lima/Docker) — today's `orb -m <vm> …` entry. Works,
   costs the user an install.
2. **circuitRF ships its own VM** — a bundled Linux kernel + minimal rootfs driven through Apple's
   `Virtualization.framework` (built into macOS) with `VZLinuxRosettaDirectoryShare` for x86-64. Out of
   the box *from the user's side*, since the component ships with us rather than being installed by
   them. Needs a small native helper (the framework is Objective-C, not reachable from .NET), the
   `com.apple.security.virtualization` entitlement, and a GPL-source offer for the kernel. **It needs
   no circuitRF engine change at all** — `MatchScore` already resolves `"platform": "osx"` (score 2,
   verified), so it is a manifest `command` swap.
3. **A macOS-native model** — either the vendor ships one (they do not) or the MINT effort produces
   one. This is the only path with no VM anywhere; it is paused by owner decision.

**Option 2 is built and proven (2026-07-31).** `tools/macos-vmhost` (a Swift VM host driving
`Virtualization.framework`) plus `tools/macos-vmimage` (a reproducible, Mac-buildable Linux image).
Verified with the production library: the worker starts under Rosetta, `dlopen`s the x86-64 Linux
library and reports its device families ready. See `tools/macos-vmhost/README.md` for the five
non-obvious things that had to be right, each found by measurement.

**A kit may now name a shipped helper by BARE NAME** — `"command": "crf-vmhost"` — and
`DeviceWorkerManifest.ResolveCommand` finds it in `ToolsDirectory` (circuitRF's own install,
`AppContext.BaseDirectory`). Deliberately narrow: the COMMAND only, never arguments (those name the
kit's files and have no business resolving inside circuitRF's install), and only a bare name
(anything with a separator is a path the kit meant literally). The kit's own folders are searched
first, so a kit shipping its own build of a tool keeps it. Gate:
`tests/Core.Tests/Devices/External/ShippedToolResolutionTests.cs`.

**Do not go looking for a fourth.** Emulating a Linux x86-64 `.so` in-process on macOS means an ELF
loader plus a syscall translator plus an x86-64 emulator — which is precisely what option 2's VM
already is, bought rather than built.

Reading a kit's own netlist — Stages A and B (brief-kit-netlist-reader.md, 2026-07-31) — COMPLETE.
**A vendor kit imported as-is into a fresh workspace now yields a working part, with no file placed
anywhere afterwards by anyone.** `KitVariantDiscovery` finds a part's formulations from the names
alone (a shared stem, a differing trailing token) and picks the default by BUILDABILITY.

**Buildability is "was it read completely", NOT "are its types familiar" — getting that backwards
inverts the answer, measured.** An unfamiliar type is very often a device a provider
supplies, which is the normal case for the formulation a kit expects you to use; the formulation that
cannot be built is typically the one written in a form the reader could not take. Testing for
unfamiliar types marked the working formulation broken and the broken one working. It is recursive:
a cell reads cleanly while the cell it instantiates does not.

**A part with no buildable formulation offers no choice at all** — a picker that cannot produce an
answer is worse than no picker. **A tie between two families identifies nothing**, because guessing
would attach a formulation choice to the wrong part.

A frequency-dependent value may now CROSS A CELL BOUNDARY (2026-07-31) — COMPLETE. `freq` is bound
at stamp time by the three models defined as functions of it (`Z_Port`'s `Z[i,j]`, `Chain`'s A/B/C/D,
the SDD's `H[w]`), while the elaborator resolves parameters once with no frequency bound. A kit's
frequency-dependent transmission line computes its RLGC — skin effect, dielectric loss — in ordinary
cell variables and passes them DOWN to one of those models, so the value has to survive the trip as
an expression. `src/Core/Expressions/FreqDeferral.cs` is that mechanism.

**This is a deliberate, bounded amendment to the scope/binding rule, taken with owner approval** (the
"Ask before" item below). The invariant it does NOT weaken: the numeric layer still receives a
self-contained expression in `freq` and nothing else — the same form `Z_Port`/`Chain` have always
accepted — never an unbound name.

- **Deferral is opt-in by dependence, not by position.** Only a transitively frequency-dependent
  value is carried as an expression; everything else still folds to a number exactly as before.
  Deferring indiscriminately would put a growing expression tree in the HB inner loop, which
  evaluates every device once per harmonic sample per Newton iteration.
- **Two rules, and conflating them is the mistake that got made and caught.** At a CELL BOUNDARY
  (`InlineForCellBoundary`) every non-`freq` name is folded to a literal, because the expression is
  about to be bound into the child's scope where the parent's names are not visible. At a DEVICE
  (`InlineForDevice`) only names that are THEMSELVES frequency-dependent are inlined — a `Z[i,j]`
  mentions `freq` **by definition**, so the cell-boundary rule fires on every existing ZPort and
  folds its scope variables into literals, which is numerically harmless and still breaks the
  inject-by-name contract `InjectZPortScopeVars` provides. Caught by `ZPortDiagTest`, not by review.
- **Recursion always folds, whatever the caller asked.** Inside a binding, names resolve in the
  binding's OWN scope, which need not be the one the result is evaluated in; leaving one as a
  reference could silently pick up a different binding of the same name.
- **Inlining is AST-level.** Splicing expression text gets precedence wrong the first time a
  substituted body is more than a single term. `Render` is fully parenthesised for the same reason —
  the gate test asserts the re-parsed VALUE, never the text.
- **A unit is applied exactly once.** Inlining absorbs each binding's own unit, so the site unit is
  skipped when the original expression referenced a unit-bearing var — through `Evaluator`'s own
  `ReferencesUnitBearingVariable` rather than a second copy of the var-unit-wins rule.
- **Frequency dependence MUST terminate at a model that binds `freq`.** Reaching anything else raises
  `FrequencyDependentValueException` naming the device, the parameter, and the three models that can
  take one — a resistor takes a single number, and saying so beats a bare "Unresolved name 'freq'"
  reported from somewhere inside the value.
- **`complex(re, im)` is now a builtin**, the rectangular counterpart of the existing `polar()`. It is
  the natural way to write a frequency-dependent immittance and is the form `FreqDeferral` renders a
  resolved Complex back into, so a deferred expression can be re-parsed by the model that evaluates it.

Gate: `tests/Core.Tests/Expressions/FreqDeferralTests.cs` (22) and
`tests/Engine.Tests/Linear/FreqDependentCellParameterTests.cs` (10) — every engine-level result checked
against the analytic response of the network the chain matrix describes (series L between two ports),
across two- and three-deep hierarchies, plus the flat-response guard, the units guard, the termination
error, and the inject-by-name regression. **Verified to fail without the fix** (`Unresolved name 'freq'`)
and **verified against the production kit**: its own 2 mm × 30 µm m1 line elaborates and sweeps —
|S21| 0.978 → 0.507 and ∠ −12.5° → −194.6° over 1–20 GHz, with the 1 GHz phase agreeing with a hand
calculation from the kit's own RLGC formulas (β·l = 11.95° before loss).

A kit's declarations must SURVIVE THE ROUND TRIP, not merely be extracted (2026-07-31) — three
bugs found in one sitting, all with the same shape: extraction was correct and the loss happened
between `CnlWriter` and `CnlReader`. **The run path is `RunAnalysis → WriteNetlist → netlist.cnl →
SchematicRunService.RunNetlist → CnlReader`, so anything the writer cannot say is gone by the time
the elaborator sees it** — and the error then names a position in a generated file that no longer
contains the thing that was wrong. When a kit-backed run fails, check the round trip before the
extractor; two of these three looked exactly like extractor bugs and were not.

- **`CnlWriter` never emitted `tb.Functions`.** `CnlReader` has always PARSED the `name(a, b) = expr`
  form, so only the writing half was missing — a kit's cells call its functions by bare name, and the
  file arrived with every call site and no declaration (`Unknown function 'KIT_TECH_CALC_PAD_CAP'`).
  Globals were unaffected, so a missing global is NOT the same bug. Gate: `CnlWriterTests`
  `UserFunctions_RoundTrip` + `AFunctionAndAVariable_StayDistinct_AcrossTheRoundTrip` (the reader
  tells them apart only by the parenthesised parameter list).
- **`**` is the kit's exponentiation operator; circuitRF's is `^`.** `KitNetlistReader` translated
  `if…then…else…endif` and `strcat` but not this, so `(Gy*(TL_FREQ*1.0e-9)**GLE_val)` reached the
  parser verbatim. Both operators are right-associative and bind tighter than the arithmetic ones, so
  `RewritePowerOperator` is a spelling change — **quote-aware**, because the same values carry file
  paths where a `**` is data, not an operator.
- **A kit writes a unit glued as readily as spaced** — a kit has `CLINE=1 pF  LLINE=1pH` on ONE
  line. The reader handled only the spaced form, so `1pH` reached the expression engine. Fixed by
  **sharing `CnlReader.TrySplitGluedUnit`** (now `internal`) rather than writing a second splitter:
  its guards — numeric head, recognised unit — are the entire reason splitting is safe, and a second
  copy is a second set of guards to keep in step.

**`RewriteExpression` is the ONE place a kit's expression text is translated.** All three call sites
route through it; add a dialect rewrite there, never at a call site.

**A kit's data files are ANCHORED where the netlist is read (2026-07-31)** — `KitDataFileResolver`,
reached from `KitNetlistReader.ReadFile`. A kit writes `File=strcat(DataPath,"X.s15p")` with
`DataPath="SomeKit_Data\"`: a path relative to the simulator's own data search path, which its
installation puts there. circuitRF has no such search path, so left relative the value survives into
the generated `.cnl` and is finally resolved against **that** file's folder — the workspace — and the
run fails naming a file in a directory the kit has nothing to do with, while the file sits untouched
in the kit. This is the same shape as the three round-trip bugs above: the extraction was right and
the loss happened downstream of it.

- **The netlist's own folder is NOT the anchor.** A kit keeps netlists in `circuit/models/` and
  data in `circuit/data/`, so this is a bounded look AROUND the netlist — two ancestors, one level of
  children each — not a resolve against one root.
- **The bound is load-bearing and is not to be relaxed when something is not found.** Each ancestor's
  children are listed, so one level too far starts listing a home or temp directory, and a value that
  happens to match a file in there resolves to something the kit never named.
- **Nothing is rewritten unless a real file is found**, which is what makes this safe to try on every
  value instead of on a list of parameter names. A kit names its files with whatever keyword it likes
  — `.s15p` for the network, `.mdl`/`.mds` for the compiled models — so a name list would silently
  cover some and not others.
- **Reading from TEXT anchors nothing.** There is no file to be relative to, and resolving against the
  process's working directory would make the result depend on where circuitRF was started.

Gate: 10 tests in `tests/Core.Tests/Netlist/KitDataFileAnchoringTests.cs`, synthetic. **Verified
against the kit:** all 12 of its `File=` values — one `.s15p` and eleven `.mdl`/`.mds` — now
resolve into `circuit/data/`, where they are.

**FOLLOW-ON, NOT FIXED: those absolute paths are HOST paths, and on macOS the worker reads them
inside the VM.** An anchored `.mdl`/`.mds` is correct for a native Linux or Windows worker and is
strictly better than the relative form, which resolved nowhere at all. But the guest only has the
shares `crf-vmhost` was given — the worker's folder and the model library's — so a kit data file
reaches it by no path at all. It needs the same treatment the model-library override just got (see
`VmHostArguments`), except that these paths arrive as DEVICE PARAMETERS rather than command
arguments, so the share cannot be added at launch from the argument list alone.

**Standing check when adding anything to `TestBench` or `Cell`: can `CnlWriter` say it?** A field the
writer cannot express is silently absent from every run, and the symptom appears far from the cause.

**OPEN, NOT FIXED — an empty parameter value is lost AND eats its neighbours.** A kit writes
`InterpDom=` with no value; `CnlWriter` emits it verbatim; `CnlReader.MergeSpacedAssignments` sees a
token ending in `=` and glues the NEXT token on as its value. Measured's 15-port SnP:
`File Type InterpMode InterpDom ExtrapMode Temp CheckPassivity NumPorts` read back as five, with
`InterpDom = ExtrapMode="constant"` and **`ExtrapMode`, `Temp` and `CheckPassivity` gone** — so the
extrapolation mode silently reverts to its default. This is a wrong answer, not a crash. Left open
deliberately: every candidate fix (writer omits an empty value / writer emits `""` / reader refuses to
glue a token that carries its own `=`) changes the `.cnl` contract, which this file's own "Ask before"
rule covers.

Reading a kit's own netlist — Stage A (brief-kit-netlist-reader.md, 2026-07-31) — COMPLETE.
`src/Core/Netlist/KitNetlistReader.cs` reads the dialect a kit ships into the same
`Library`/`Cell`/`Instance` model `CnlReader` produces, so a kit's part is treated exactly like a cell
the user drew. **Why it exists:** a vendor kit is read-only and self-contained, and importing one must
produce a working part with no file placed anywhere afterwards — the three facts a part needs (that it
offers a choice of formulation, which one is buildable, what circuit it is) are all in this file, and
every alternative is a declaration someone has to write and put somewhere.

**It reads a FORMAT, not a kit** — nothing in it names a supplier, library, part or model family — and
it is deliberately not a general-purpose importer: everything it does not understand is reported by
line and skipped, never guessed at.

Rules worth keeping:
- **A bare word after a value is that value's UNIT** (`R=1 TOhm`); a word containing `=` starts the
  next parameter. This is the one that silently corrupts: `R=1 TOhm` read as `R=1` is a resistor a
  thousand billion times too small and everything downstream still runs.
- **A backslash in a quoted value is a directory separator, not an escape** — a kit spells a folder
  `Path="Data\"`. Normalised to `/`, so joining it to a filename gives a path rather than a run-on word.
- **`if(c) then (a) else (b) endif` → `if(c, a, b)`**, purely syntactic; a malformed one is left exactly
  as written, because an expression that fails with the kit's own text beats one that evaluates to
  something nobody wrote. Same rule for `strcat` over an unresolvable piece.
- **A mismatched `define`/`end` is an error, not a note** — every later cell would be attributed to the
  wrong define.
- **`Options:` is the one `Type:Name` line that is not a device**, skipped and reported rather than
  special-cased into silence.

Gate: 19 tests in `tests/Core.Tests/Netlist/KitNetlistReaderTests.cs`, all over synthetic fixtures —
the reader is a format reader, and the repo does not commit proprietary kit data. **Verified against
the production kit by disposable probe:** its two netlists read as 2 + 7 cells with 4 notes total
(two `Options:` lines and two genuinely unhandled constructs), the 26-port package defines both read,
`strcat` resolved to real paths, and units survived. Full suite 5,623 pass.

**A kit's netlists are ONE library split across files** — the file defining a part instantiates cells
declared in another and its process constants live in a third — so `NetExtractor.NetlistImports` reads
every netlist beside the named one, the named file winning on a name collision. Reading only the named
file gives a definition whose own contents do not resolve (measured: 2 cells and no constants, against
9 cells, 38 constants and 4 functions once siblings are read).

Out-of-process device-worker provider (M5 transport, 2026-07-31) — COMPLETE: the first concrete `IExternalDeviceProvider` in the repo. `src/Core/Devices/External/`: `DeviceWorkerProtocol` (frame codec), `DeviceWorkerTransport` (`IDeviceWorkerTransport`, a process transport and a stream transport), `DeviceWorkerChannel` (request/reply), `DeviceWorkerProvider`, `DeviceWorkerInstance`. **Still nothing about any particular provider in circuitRF** — a worker executable path is runtime configuration, and every device type, parameter name, pin count and node role is learned from the worker's replies. Gate: 46 tests in `tests/Core.Tests/Devices/External/`, 0W/0E, full suite 5,490 pass.

**Why a process and not a library — the reason is structural, not a preference.** A compiled device model calls back into the process that loaded it for services that process must export as C symbols, which a managed host cannot do; and one process can hold exactly one build of one library, so several builds means several processes. Both constraints dissolve once the model lives in its own process, and circuitRF then loads nothing and links against nothing. This also means **the macOS path needs no separate design**: a worker built for another OS runs in a VM and is driven over the same two streams, so `IDeviceWorkerTransport` is the only thing that varies.

Wire format: `[ uint32 jsonLen ][ uint32 binLen ][ JSON ][ raw little-endian doubles ]`. JSON control plane so a frame stays readable in a hex dump; bulk numerics as raw doubles so a large batch costs no parsing. Commands `describe | create | probe | eval | destroy | shutdown`. `eval` sends `count × nodes` doubles and returns `status[count]` then, per point, `I[n], Q[n], G[n×n], C[n×n]`, all row-major.

- **Batching is the point, not an optimisation.** Measured against a real worker: ~100 µs per evaluation one at a time, ~4.2 µs at batch 2000 — **~24×**. HB evaluates every device once per sample per Newton iteration, so the per-call version makes the transport, not the model, the simulator. `EvaluateBatch` is one round trip.
- **No sign flip, and this is checked rather than inherited.** A worker reports current positive INTO the device, already matching this repo's convention (see the M3 note below). Confirmed against behaviour: at a drain bias the drain node's current is positive while the device sinks it, and the thermal node's current is negative with magnitude equal to the dissipated power — power leaving the device. A second, defensive flip would invert every operating point *and still converge*.
- **Partial pipe reads must be looped.** A short read is normal on a pipe; treating one as end-of-stream yields frames that decode as garbage only under load. Tested with a stream that returns one byte per read.
- **An implausible frame length is a desync, not a large result.** Believing a corrupt length means allocating gigabytes instead of reporting the stream is out of step.
- **stderr is drained on a thread.** Nobody reading it fills the pipe and the worker blocks forever inside a write — presenting as a hang midway through a long solve with no error anywhere. The drained output is attached to the exception, since it is usually the only description of what went wrong.
- **One request at a time, locked.** Two threads writing frames into one pipe interleave them and the worker reads a header out of the middle of somebody else's JSON. Correctness, not convenience.
- **An unknown parameter name is rejected at `Create`.** The worker matches by keyword and ignores what it does not recognise, so a typo would otherwise present as a device quietly running on a default. The error names the parameter and lists the real ones. A *blank* value is omitted instead, so the model keeps its own default.
- **A point the worker could not evaluate raises.** `IExternalDeviceInstance` has no channel for per-point status; returning the non-finite numbers would put a NaN in the matrix and surface far away as unexplained non-convergence. When a damping channel exists, this becomes a status return.
- **Node roles are measured, not declared.** `create` is followed by `probe`, which reports per node whether it is a free unknown and whether an external pin is thermal — the discriminator being Jacobian *symmetry*, not magnitude. A worker too old to probe is not an error; the declared descriptor stands.

Test seam: `FakeDeviceWorker` speaks the real wire format over in-memory streams, so the provider is exercised end to end with no model present. Its device is deliberately **asymmetric** (`G[0,1] = 3`, `G[1,0] = 0`) — a symmetric one lets a transposed Jacobian, or a charge block read as a current block, pass every check.

**Zero-setup provider resolution (2026-07-31).** The user's path is *import kit → place part → configure analysis → Run*, with no provider to configure. `ExternalDeviceRegistry` therefore takes `IExternalProviderResolver`s alongside registered providers: when a netlist names a provider nobody registered, the resolvers are asked, and whatever one produces is cached under that name.

- **Resolvers, not providers, are registered at workspace open — so nothing starts.** A workspace may hold many kits and a given design typically uses none of them. A worker process starts the first time a design actually asks for that kit's devices.
- **`DeviceWorkerProviderResolver` looks for a `device-provider.json` manifest** in each kit folder (a root and one level down). The manifest is the single fact circuitRF cannot derive — which program evaluates this kit's devices — and it is **data beside the kit**, not knowledge compiled in. Per-platform entries pick by `MatchScore`: exact runtime identifier > operating system > catch-all, so a kit gives one general entry and overrides it for one platform. **This is where the "runs in a VM on macOS" case lands with no special code** — it is just another platform's command.
- **Relative paths resolve against the manifest's folder, then its declared `baseDirectory`.** Importing copies the manifest into the workspace while the worker and model files stay in the installed kit, so the copy records where the kit was. Manifest-folder-first means dropping real files beside the copy overrides the kit — the only escape hatch that needs no configuration.
- **The copy is always named for the kit.** Each installed cell records `Provider = <kit name>`, so that is what a netlist asks for. A copy keeping the manifest's own name leaves every step working and only Run failing — caught by a test, not by inspection.
- **`ResetResolved()` on workspace switch** ends providers the registry started (their workers point at the old workspace's kits) and leaves host-registered ones alone.
- **`Require`'s message names the folders searched** and the manifest filename. "Provider unavailable" with nowhere to look is a dead end.
- A kit with no manifest imports **silently** — its parts still place, draw and export. Only simulating them needs one, so a message here would be noise on nearly every import.

**Reference worker + real-process coverage (2026-07-31).** `tools/DeviceWorkerExample` is a complete worker serving one synthetic square-law FET, and it **references nothing — not even `CircuitRF.Core`**. That is the point: a real worker is a native program that cannot use our frame codec, so this one implements the framing itself. `DeviceWorkerProcessTests` (17 tests) then compares two independent implementations rather than one agreeing with itself.

Until this landed, `ProcessDeviceWorkerTransport` had **zero** coverage — every test spoke the protocol over `MemoryStream`, which cannot produce the failures that actually occur: short reads, writes buffered until a flush, a deadlock when nobody drains stderr, an abrupt EOF when the child exits. The tests that matter most: a 2000-point batch (~72k doubles, far past any pipe buffer, so it only passes if partial reads are looped on *both* sides), 200 sequential round trips (a framing error leaving one stray byte is invisible on the first call and corrupts every one after), and Kirchhoff's law holding on the decoded currents. Whole file runs in ~0.5 s.

Two failure paths are deliberately distinguished, because they have different causes and different fixes: a worker that **died on its own** surfaces as `ExternalDeviceException` naming the worker (driven by really shutting one down and then reading), while using a device after the **application ended its provider** is `ObjectDisposedException` — a mistake in the calling code, not a transport failure.

The worker's path reaches the tests as build-recorded assembly metadata (`DeviceWorkerExampleDir`), not a relative guess from the test's output folder, which would break the first time a layout changed. The shipped `device-provider.json` is itself asserted valid — it is the template a kit author copies, and no product code reads it.

**End to end: a netlist that merely names a kit now solves (2026-07-31).** `tests/Engine.Tests/External/WorkerBackedAnalysisTests.cs`, 8 tests. No provider is registered — a kit folder with a manifest is stood up the way an import leaves one, and `ComponentModelFactory.Require` resolves it mid-elaboration. Covers: closed-form operating point through a worker process; the device genuinely **off** below threshold (a device that conducts nothing converges beautifully, so the on case alone proves nothing); **source degeneration**, where the operating point depends on itself and so actually tests the Jacobian that crossed the pipe; netlist parameters reaching the model; two devices sharing one worker; a sweep reusing the resolved provider rather than relaunching per bias point; and both "kit not installed" and "type not served" messages.

**A real integration bug, found by exactly that test.** `CreateExternalDeviceModel` forwarded **every** non-selector parameter to the provider — including circuitRF's own `__instanceLabel`, which it reads two lines further down. A permissive provider ignores an undeclared name, so this survived unnoticed against the in-process `SquareLawFetProvider`; a strict one rejects it and fails **every** device it serves. The strict behaviour is the correct one (it is what turns a misspelled parameter into an error rather than a silently defaulted device), so the fix is in the factory: **`__`-prefixed parameters are circuitRF plumbing and are not part of "everything else is forwarded".**

*Not a bug, worth knowing before asserting:* an "off" device does not read exactly zero current. The DC engine adds `gmin = 1e-12 S` to every voltage node for continuity, so 5 V leaks exactly 5 pA regardless of the device. Asserting zero asserts against the solver's own regularisation.

*Open, unreproduced:* one Core.Tests failure occurred on the single run immediately after this project was first built, and was not captured by name. Six subsequent full runs — including one forcing a rebuild — and three isolated runs of the process tests are all clean. Recorded rather than dismissed; if it returns, capture the test name before assuming it is first-build noise.

`Chain` — ABCD two-port primitive (M4, 2026-07-30) — COMPLETE: `src/Core/Devices/ChainModel.cs`, a two-port given by its chain matrix, entries as expressions in `freq` exactly like `Z_Port`'s `Z[i,j]`. Four nets as ± pairs (`[p1+, p1−, p2+, p2−]`); Group 2, two branch unknowns. Convention `V1 = A·V2 − B·I2`, `I1 = C·V2 − D·I2` with both currents INTO the device, matching every other model here. Omitted entries default to the identity two-port, so a partially-specified block degrades to a wire rather than a silent zero matrix.

**Why it exists when `Z_Port` already does — the reason is specific, not stylistic.** A chain matrix describes two-ports that have **no impedance matrix at all**. The case that matters: a pure series element has `C = 0`, so `Z11 = A/C` is infinite. Frequency-domain line models routinely degenerate to exactly that at DC (`A = D = 1`, `C = 0`, `B` = the series resistance), so a model that is perfectly well-behaved in ABCD form cannot be expressed as a Z-block at ω = 0. Stamping the chain relations directly stays non-singular there: with `C = 0, D = 1` the second constraint reduces to `I1 = −I2` and the first to `V1 − V2 = B·I1` — a series impedance. Gate: 8 tests in `tests/Engine.Tests/Linear/ChainModelTests.cs`, each against the analytic result for the network the matrix describes (series-Z at DC across three decades of value, identity, defaults, shunt-Y, ideal transformer, and a series-L whose |S21| is checked against `2·Z0/(2·Z0 + jωL)` at three frequencies).

Three v1 language capabilities wired up (M4, 2026-07-30) — COMPLETE. Each was specified but unreachable from a netlist; the gap was wiring, not arithmetic. Gate: 20 tests in `tests/Core.Tests/Expressions/LanguageAdditionsTests.cs`, all through the full `.cnl` → elaborate path.

- **User-defined expression functions are now declarable in `.cnl`**: `name(a, b) = expr` at top level. `Evaluator.RegisterFunction` has existed since v1 and the root CLAUDE.md lists the feature, but nothing parsed a declaration — `CnlReader.IsVariableAssignment` requires a bare identifier on the left, so such a line never reached any parser. Now `TryParseFunctionDeclaration` runs **before** the assignment case, stores into `TestBench.Functions`, and `Elaborator.Elaborate` registers them **before flattening** (a cell parameter default may call one). Declarations inside a `define` are rejected. `y = (a+b)*2` is still a variable — the parameter list must be identifiers.
- **String equality.** `Value.Equal`/`NotEqual` accept two `String` values (ordinal). Deliberately narrow: `==`/`!=` only, both sides String, no coercion either way. `String` stays storage-only otherwise — a string in arithmetic, or compared to a number, is still an error, and both are tested.
- **Rounding family**: `floor`, `ceil`, `round` (away from zero), `int` (truncate toward zero). Componentwise for Complex, which keeps them total. `int` vs `floor` on negatives is tested explicitly since that is the distinction that bites.

**Known constraint, worth knowing before writing any `.cnl` generator:** the generic instance-line parser splits on whitespace and treats bare tokens as nets, so an **unquoted parameter value must contain no spaces** — `R=if(a,1,2)` is fine, `R=if(a, 1, 2)` silently becomes a value plus two phantom nets, shifting every later node index. Only the SDD line parser does depth-aware boundary detection. Quoted values (file paths) are safe, the tokeniser is quote-aware. Not changed here: widening the generic parser touches every device and was not needed, since expressions are perfectly valid without spaces.

External devices — descriptor-driven `ExtDevice` (M3, 2026-07-30) — COMPLETE: a generic component whose behaviour comes from a registered external provider, with **nothing about any particular provider in circuitRF's code**. New `src/Core/Devices/External/`: `ExternalDeviceDescriptor` (TypeId/DisplayName/pin+node counts/params/nodes — all opaque, rendered never interpreted), `IExternalDeviceProvider` + `IExternalDeviceInstance` (with a **batched** `EvaluateBatch` on the interface from the start — HB evaluates per harmonic sample per Newton iteration, so a per-eval round trip to an out-of-process provider would dominate runtime), `ExternalDeviceRegistry` (host registers providers; Core never constructs one), `ExternalDeviceModel`, `ExternalDeviceException`.

**The mapping that makes this need zero engine change.** A provider reports currents per NODE and derivatives per node PAIR; `ComponentModel` is written in ports that each span a node pair. The two reconcile exactly when **every node is its own ground-referenced port** — the elaborator lays the node array out `[n0,0, n1,0, …]`, so `PortVoltages[k]` IS node k's voltage, `I[k]` the current into it, `Dg[k,l] = ∂I[k]/∂V[l]`. The engine's existing four-way port stamp does the rest.

**Passive sign convention, no flip anywhere.** A provider's current is positive INTO the device, which is exactly what `NonlinearDcEngine` stamps (`f[np-1] += ip`, "port current flows into device at np"). Verified by test, not assumed.

**Internal nodes are real unknowns.** `Elaborator.BuildExternalDeviceNodes` mints `__extdev_{instancePath}_n{k}` via `Nodes.GetOrAssign` — the same mechanism Tuner/P1Tone/PnTone already use — so they get ordinary global matrix rows. They are deliberately **not** locally eliminated: Schur reduction is simpler and is wrong for HB, where an internal node voltage carries its own harmonic content.

**Slaved nodes cost nothing.** A descriptor node reporting `SlavedTo` is given its master's node index instead of a fresh one; the engine's four-way stamp then folds the chain rule by itself (slaved row is identically zero → adds nothing; slaved COLUMN lands on the master's column → exactly what slaving requires). Chains and self-reference are hard errors. A provider that reports a node as degenerate **without** naming what it follows is a hard error at elaboration — the alternative is a silently dead device, which is the failure mode this path is most prone to.

`ExtDevice` reserves two parameter names, `Provider=` and `Type=`; **every** other parameter is forwarded to the provider verbatim, matched against the names its own descriptor declared. `ResolveExtDeviceParameters` follows the string-param rule (see below): `Provider`/`Type` are stored raw, and any other override that fails to evaluate as an expression is stored verbatim rather than throwing — a provider may declare file paths or enum-valued parameters, and a leading `/` alone crashes the expression parser at position 0 (the same trap `SnP`'s `File=` hit).

Gate: 11 tests in `tests/Engine.Tests/External/` against a synthetic square-law FET provider (`Id = β(Vgs−Vth)²(1+λVds)`, Rg/Rs creating two genuine internal nodes, plus a self-heating thermal node with its own internal Rth to a fixed reference). Asserted against a **closed-form scalar oracle that never touches the matrix** — operating point, internal node voltages, the exact identity `Tj = Id·Vds·Rth` at an externally-open thermal pin, self-heating actually derating the current, the analytic Jacobian entry-by-entry vs finite difference, the passive sign convention, and three distinguishable failure modes. Tolerances are set to what each side actually guarantees (engine `AbsTol=1e-6`; oracle iterates to 1e-15 on its own update) — asserting tighter tests the solver's stopping rule, not its correctness. Build 0W/0E; 5,284 tests pass.

Var-unit-wins in `Evaluator.Eval` (brief-var-unit-wins-consistency Part B, 2026-06-23) — COMPLETE: `Evaluator.Eval(expr, scope, unit)` now applies the **var-unit-wins** rule: when the expression references any variable that declares its own unit in scope (`scope.Lookup(name).Unit` non-empty), the site unit is **skipped** — the variable's unit was already applied once in `Resolve`. A new `private static bool ReferencesUnitBearingVar(Expr ast, Scope scope)` uses `AstWalker.CollectRefs` + `Scope.Lookup` to check. Guard: `!string.IsNullOrEmpty(unit)` — the no-site-unit path is untouched. Literals (no refs) still get the site unit; unit-less variable refs still get the site unit. Fixes `P1 Freq=RFfreq GHz` where `RFfreq` is unit-bearing (swept override with `Unit=Hz` or VAR declared `= 2 GHz`), and the latent prefixed-unit double (e.g. `Cval pF` where `Cval = 1 pF` gave `1e-24` instead of `1e-12`). 5 gate tests in `tests/Core.Tests/Expressions/EvaluatorVarUnitWinsTests.cs`. Build 0W/0E.

Parametric-sweep range units (brief-sweep-range-units, 2026-06-22) — COMPLETE: `SweepSpec` gains `public string Unit { get; } = ""` (optional trailing ctor param). The spec constructor of `ParametricSweepAnalysis` applies `Units.Scale(spec.Unit) ?? 1.0` when materializing `SweepValues`: Start and Stop are always scaled; `StepOrCount` is scaled only in StepSize mode (PointCount count is dimensionless). `SweepValues` are therefore always base-unit — the engine injection of bare doubles stays correct. CnlWriter emits `Unit=<unit>` after `Step=|Npts=` when non-empty; CnlReader reads `Unit=` (default `""`) in the spec form. Absent `Unit=` on existing files → `""` → scale 1.0 (back-compatible). 3 gate tests in `SweepSpecCnlTests.cs` (T1: StepSize scaling; T2: PointCount scaling (count not scaled); T3: CNL round-trip with/without Unit=). Build 0W/0E.

`ToneSourceModel.LastBranchIndex` (brief-sdd-control-current-tonesource, 2026-06-19) — COMPLETE: `ToneSourceModel` (`V_1Tone`/`V_nTone`) now exposes `public int LastBranchIndex { get; private set; } = -1`, captured in `Stamp` (`int br = LastBranchIndex = mna.AddBranch();`) exactly like `VdcModel`. This makes the tone source's branch current referenceable as an SDD control current (`C[n]=<toneSrc>`); the three engine resolvers (DC/HB/S-param) validate it as a two-terminal kind. No factory change — `CreateSddModel` stores raw control-ref instance names and only cross-validates `_cn`↔`C[n]`; kind validation lives in the engines. Engine-side detail + tests in `src/Engine/CLAUDE.md`.

SDD arbitrary weighting `I[p,w]` + `H[w]=expr` (brief-sdd-weighting-parser, 2026-06-19) — COMPLETE: SDD now accepts arbitrary weighting `I[p,w]` for `w≥2` with user `H[w]=expr` (Complex, in `freq`). `H[0]=1` and `H[1]=jω` are built-in and not redefinable. `I[p,w]` uses the real dual-AD evaluator (`SddEvaluator`); `H[w]` uses the Complex general `Evaluator` with `freq=ω/2π` bound at evaluation time. Parser chain: **CnlReader** `SddAssignmentHeader` regex extended to match `H\[\d+\]`; **Elaborator** `RxSddEquation` extended with `H` so `H[w]` params are stored raw and scope vars injected; **ComponentModelFactory** drops the v1 hard-error for `w≥2`, parses `H[w]` entries via `RxWeightFn`, and cross-validates that every referenced `w` has a matching `H[w]`. **SddModel** gains `_higherAst[][]` (per-port `w≥2` bucket lists) and `_weightAst` (w→H[w] AST), emits one `WeightedTerm` per distinct `w` from `Evaluate`, and overrides `Weight(w,ω)` to evaluate `H[w]` via the Complex evaluator. Ctor gains two optional params (existing tests unaffected). 10 gate tests: 8 in `SddWeightingParserTests.cs` (Core.Tests/Devices) + 2 in `SddWeightingParserE2eTests.cs` (Engine.Tests/HarmonicBalance). Build 0W/0E.

`NonlinearCModel` + `PolynomialFit` (brief-nonlinearc-model, 2026-06-19) — COMPLETE: Added `src/Core/Devices/NonlinearCModel.cs` (1-D polynomial nonlinear capacitor; `PortCount=1`, `Kind=Nonlinear`; `CapAt` Horner, `ChargeAt` Horner-integrated, `Stamp` no-op, `Evaluate` returns `Dc[[C(Vd)]]` and `Q[ChargeAt(Vd)]`). Added `src/Core/Expressions/PolynomialFit.cs` (normal-equation least-squares, Gauss partial-pivot, lowest-power-first output). Wired `"NonlinearC"` into `ComponentModelFactory._parameterizedTypes` and `TryCreate(typeName, params)` with `CreateNonlinearCModel` (reads `C0,C1,…` consecutively). 8 gate tests in `NonlinearCModelTests.cs`. Build 0W/0E, 1899 total tests.

`ComponentModel.StampLinearized` (brief-nonlinear-engine-seam, 2026-06-19) — COMPLETE: Added `using System.Numerics` and a new `public virtual void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)` to `ComponentModel` base class. Default implementation calls `Evaluate(bias)` and stamps `Y[p,q] = Dg[p,q] + jω·Dc[p,q]` as an N-port admittance block via `AddBlockAdmittance`, using the same port→node-pair convention as `NonlinearDcEngine`. Linear engines call this for `Kind==Nonlinear` devices; HB/DC never call it.

Schematic housecleaning (brief-schematic-housecleaning, 2026-06-19) — COMPLETE (Core items): **Item 2 (P1Tone S-param lint):** `Elaborator.LintTopLevelTerms` now includes `P1ToneModel` in the top-level port-family filter alongside `PortModel` and `TermModel`. A netlist with `P1Tone Num=1` + `Term Num=2` no longer emits a "port 1 missing" warning. Warning text still says "Terms are numbered…" but the diagnostic now spans the full S-param port family. **Item 5 (ohm/ohms units):** `Units._scales` (case-sensitive ordinal map) now includes `{ "ohm", 1.0 }` and `{ "ohms", 1.0 }` so `Z=50 ohms` no longer tokenizes `ohms` as a phantom net. `IsKnown("ohm")` / `IsKnown("ohms")` return true; `Scale("ohm")` / `Scale("ohms")` return 1.0. `Ohm`/`Ohms` (Title-case) are unchanged. 9 gate tests: 5 in `OhmLowercaseTests.cs` + 4 in `P1ToneLintTests.cs`. Build 0W/0E.

Standing instructions for `src/Core` (the design layer, the elaboration layer, the expression
engine, and the `ComponentModel` types). Read with the root `CLAUDE.md`. Design notes:
`docs/design/data-model.md`, `docs/design/expressions.md`. `Data/` and (numeric `ComponentModel`
behavior) the engine have their own notes.

## What lives here
- **Design layer:** `Library`, `Cell`, `Instance`, `TestBench`, `ParameterDeclaration`,
  `ParameterAssignment`, `Variable`, `Analysis` subtypes, `Measurement`. Editable, serializable
  (`.cnl` + JSON), human-readable.
- **Elaboration layer:** flatten hierarchy → `ElaboratedNetlist` (`ElaboratedComponent` list +
  `NodeMap`), resolving parameters/variables and numbering nodes.
- **Expression engine:** tokenizer → Pratt parser → AST → evaluator. Serves variables, cell
  parameters, the SDD, and measurements.
- **`UnitNormalizer`** (`Expressions/UnitNormalizer.cs`): `ToEngineUnit(editorUnit)` maps editor glyph
  unit strings (Ω, µ) to ASCII engine spellings (Ohm, u). Called at the extraction boundary only — do
  not scatter; editor glyphs and the `Units` table are both unchanged.
- **`ComponentModel`** base + `Devices/` (the numeric behaviors; their stamping/evaluation contract
  is detailed in `data-model.md` §5 and exercised by the engine).

## Key type distinctions — do not blur
- **`Cell` vs `TestBench`.** A `Cell` is a **reusable** definition (ports, parameter interface,
  contents) that gets instanced. A `TestBench` is the **one** thing you simulate (top cell +
  globals + analyses + measurements). **Analyses and measurements attach to the `TestBench`, never
  to a `Cell`.**
- **`ComponentModel`, not "Device".** The single base for passive **and** active parts. "Device" is
  reserved for its RF meaning (an active part). A resistor and a FET are both `ComponentModel`s.
- **Three layers flow one way:** design → elaboration → numeric. Nothing in `src/Core` may depend
  on `src/Engine` or `src/Ui`. The GUI edits the design layer; it never hands a design-layer object
  to the engine — always elaborate first.

## Expression engine — non-negotiables
- **Never string substitution.** Real tokenize → Pratt-parse → AST → evaluate. (This replaces the
  prototype's NSExpression/NSPredicate/regex path.)
- **Parse once, evaluate many.** Cache the AST on the owning `Variable`/parameter/SDD/`Measurement`;
  the SDD hot path (per time sample × Newton step × sweep point) must allocate no garbage.
- **Kinded values: Real / Complex / Bool.** A resolved variable or parameter is Real **or** Complex
  (not forced complex) — most component values are Real, impedances Complex. Ordering comparisons
  are **real-only**; SDD equations are real-only time-domain (no `j`).
- **Cycle detection is mandatory** and spans variables, cell-parameter defaults, and instance
  overrides. Report the offending chain (e.g. `a → b → a`); never recurse without the in-progress
  guard. Fixture `recursion.log` (a valid multi-hop chain → resolves to `2`) and a synthetic
  cyclic fixture (`a=b, b=a` → must be reported) are the Phase-1 tests.
- **Scope is structural,** not string-keyed: globals at the base; each cell instance pushes a scope
  binding parameters. **Override expression evaluates in the PARENT scope; default in the cell's
  own scope.** Resolution is local-then-global; no upward/sideways reach.

## Elaboration — string-param devices (do not Eval their non-numeric params)

`Elaborator.ResolveParameters` dispatches to a per-device resolver for primitives whose overrides
must NOT be expression-evaluated. Currently: **SDD**, **Z_Port**, **V_1Tone/V_nTone**, **P1Tone**,
and **SnP** (brief-snp-fixes, 2026-06-17). Each stores its string-valued params as `new Value(raw)`
(verbatim), and only evaluates genuinely numeric overrides via `_evaluator.Eval()`.

**Why:** a file path (`File=/Users/…/x.s2p`) is not an expression — the leading `/` crashes the
expression parser at position 0. Similarly, `InterpMode=Cubic` and `ExtrapMode=NearestEdge` are
string-valued enum names, not numeric expressions.

**Rule:** when adding a new primitive device that has string-valued params (file paths, enum names,
equation strings), add a `ResolveXxxParameters` dispatcher in `Elaborator.cs` that stores those
params raw. The generic `ResolveParameters` fallback evaluates ALL overrides — never let a string
param fall through to it.

## Elaboration
- Flatten depth-first; primitives emitted, cells recursed with a fresh scope.
- Resolve every expression to a kinded value (units applied here), with cycle detection.
- Number nodes: ports map onto parent nets; internal nets uniquified by instance-path prefix;
  **ground = 0**. Node names carry the instance path (`X1.drain`) — this is what measurement paths
  resolve against, so keep it stable.
- Compute `NonlinearComponents` / `NonlinearNodes` (the HB partition seed) here.
- Numbering is stable + unique; the fill-reducing permutation for the solve is the **engine's** job,
  not the elaborator's.

## `.cnl` reader + writer
- Vendor-neutral hierarchical netlist; maps directly onto the design layer. JSON carries the same
  logical model.
- `CnlReader` (existing) parses `.cnl` text to `TestBench`.
- `CnlWriter` (Phase 6e Step 2, `src/Core/Netlist/CnlWriter.cs`) is the exact inverse: emits a
  `TestBench` as `.cnl` text that `CnlReader` round-trips back to an equivalent `TestBench`.
  Handles: variables, standard instances (R/L/C/Port/SnP…), SDD (equation format), Z_Port (Z[i,j]=
  format + N-or-N+1 rule), Tuner (skips synthetic TunerName), typed analyses (HB/Loadpull/etc.),
  measurements, raw directives verbatim, and a top-level `labelednets <name> <name> …` directive
  recording which nets came from user-placed schematic labels (see below). Gate: 10 round-trip tests
  in `tests/Core.Tests/Netlist/CnlWriterTests.cs`, all green.
- **`labelednets` directive (brief-cnl-labelednets-provenance, 2026-06-16).** `CnlWriter` emits a
  top-level `labelednets n1 n2 …` line (sorted, stable) from `tb.LabeledNets` when any labeled nets
  exist; `CnlReader` parses it back into `tb.LabeledNets`. This is what lets the node-picker
  labeled-filter survive the schematic→`.cnl`→CnlReader run path. `HbLabeledNodesCubeTests` (T4/T6)
  previously only exercised the in-memory injection path and missed the `.cnl` round-trip gap; T7
  (`EndToEnd_SchematicCnl_EmitsLabeledNodesCube`) is the regression guard for the full round-trip.
- **Skip unknown header/comment lines** so real-world exports import cleanly; committed fixtures are
  clean `.cnl`.
- The **VendorA importer** (a separate front-end, Phase 2) translates legacy `if…then…else…endif`
  → canonical `if(cond,then,else)` at import time, so the engine grammar stays single-form. (Native
  `.cnl` only ever uses the canonical form.)
- Analysis/measurement **directive grammar is deliberately deferred** (data-model §10) — nail it
  down before implementing those lines; the circuit/cell/variable lines are settled.
- **SnP / frequency-domain N-port reference node (N-or-N+1 rule).** A frequency-domain N-port
  component (SnP block, impedance block, TLIN, user freq model) lists either **N nets** (each port
  referenced to ground, node 0) **or N+1 nets**, in which case the **last net is the common
  reference node** for all ports (the floating-block case). The reader validates node count against
  `NumPorts`: `== NumPorts` or `== NumPorts + 1`, else error. The reference node is recorded on the
  component (a `ReferenceNet`, ground when absent) and the model uses it in its own stamp
  (linear-engine §4.1). This rule does **not** apply to 2-terminal R/L/C. SnP line fields:
  `File` (relative paths resolved against the `.cnl` file's dir; absolute as-is), `Type`
  (v1: `"touchstone"` only — **hard-error on any other value**; extensible, future `"datacube"`),
  `InterpMode` (`"spline"` default | `"linear"`), `InterpDom`, `ExtrapMode` (`"clamp"` default |
  `"extrapolate"`); `Temp` and passivity/noise flags are parsed and ignored.

## Phase 1 deliverable — COMPLETE (2026-05-30)
Expression engine + elaboration + `.cnl` reader, validated by the cycle-detection fixtures.
**Phase 1 does not need RfCore** — it is built and tested standalone.

### Implementation notes (reality vs. design)
- `if(...)` keyword is handled in the **Parser** (produces `ConditionalExpr`), not in `Evaluator.EvalCall`.
- `Evaluator.InjectResolved(scopeDebugName, name, value)` lets the Elaborator inject
  pre-resolved override values into the memo cache without round-tripping through `ToString()`
  (avoids breakage on Complex values).
- Left-assoc operators use `rbp = lbp + 1` in the Pratt table; right-assoc (`^`) uses `rbp = lbp - 1`.
- Analysis/measure `.cnl` lines → `RawDirective { Kind; RawLine }` on `TestBench`. Typed `Analysis`
  subclasses (`SParameterAnalysis`, etc.) are defined but not populated by the Phase-1 reader.
- Top-level instances in a `.cnl` (outside any `define` block) live directly on `TestBench.Instances`
  (no synthetic wrapper cell).
- `ComponentModel.Stamp` uses `object` placeholders for `mna` and `c` — types resolved in Phase 2.

## Phase 2 Step 1 deliverable — COMPLETE (2026-05-31)
SnP/Touchstone block end-to-end: `.cnl` reader parses `SnP:` lines, elaboration creates `SnpModel`,
`SnpModel.Stamp` performs Z-expansion into a real `MnaSystem`. RfCore is now a dependency of Core.
Gate: 4 tests in `tests/Engine.Tests/Linear/SnpStampTests.cs` — all green.

### Implementation notes (reality vs. design)
- `ValueKind.String` was added to `Value` (storage-only; no operators, no coercions). This is the
  mechanism for SnP configuration params (`File`, `Type`, `InterpMode`, `ExtrapMode`). The tokenizer
  lexes `"..."` as `StringLiteral`; the parser produces `StringLiteralExpr`; the evaluator returns
  `Value.String(...)`. A String value is a type error in any arithmetic context.
- `Instance.RefNetBinding` (nullable string net name) carries the N+1 floating reference node for
  frequency-domain N-ports. `null` = ground. The elaborator resolves it to `ElaboratedComponent.ReferenceNode`.
- `IMnaContext` interface lives in `CircuitRF.Core` (not Engine) because `ComponentModel.Stamp` must
  be able to call it. `MnaSystem` in `CircuitRF.Engine` implements it.
- `ComponentModel.Stamp(IMnaContext mna, ElaboratedComponent c, double omega)` — `object` placeholders
  replaced with real types.
- The elaborator now resolves parameters **before** creating the model (order swapped). This allows
  `ComponentModelFactory.TryCreate(typeName, params)` to construct `SnpModel` with its file path
  and settings baked in.
- `CnlReader` stores `_sourceDirectory` (from `ReadFile`); relative `File=` paths are resolved to
  absolute at parse time and re-stored in the `ParameterAssignment.Expression` as a string literal.
- `TokeniseLine` in CnlReader now respects quoted regions so `key="value with spaces"` stays one
  token.

## Phase 3 deliverable — COMPLETE (2026-06-01)
AD engine + SDD device, validated by the hero GaN HEMT bias.

### Step 1: AD engine
New files in `src/Core/Expressions/`:
- `IAdScalar.cs` — static-abstract interface (C# 11 generic math) for real-only scalar operations
- `Dual.cs` — forward-mode dual number, N-wide gradient via `[InlineArray(8)]` (MaxN=8, allocation-free)
- `SddScalar.cs` — thin `double` wrapper implementing `IAdScalar<SddScalar>` (FD / plain-eval path)
- `AdWarnings.cs` — thread-static model-name context for domain-clamp warnings to `Console.Error`
- `SddEvaluator.cs` — generic `Eval<T>(Expr, bindings, modelName)` — ONE tree-walk, two scalar types
- `AstWalker.cs` — collects all `RefExpr` names from an AST (used by Elaborator for SDD scope injection)
- `FiniteDiff.cs` — central-difference gradient helper (AD oracle + production FD fallback)

Gate (tests/Core.Tests/Expressions/AdVsFdTests.cs): AD of the hero i2 at (v1=−3.05, v2=48) matches
central FD to ≥4 sig figs: gm = ∂i2/∂v1 ≈ 62.4 mS, gds = ∂i2/∂v2 ≈ −9.45 µS (negative — correct).

### Implementation notes (reality vs. design)
- `SddEvaluator.Eval<T>` is a generic local-function nest inside a single static method. Conditions in
  `ConditionalExpr` are evaluated by extracting `T.ValueOf()` (the scalar) and comparing doubles —
  AD takes the active-branch derivative, the other branch is not evaluated.
- `Dual.Exp` caps argument at 700 (preventing overflow); `Dual.Log`/`Sqrt` clamp with warn.
  Together, `log(exp(x)+1)` (softplus) evaluates correctly for all x — large x gives ≈ x, very
  negative x gives ≈ exp(x). No special softplus pattern needed.
- SDD equation expressions **may contain whitespace** — the SDD line parser uses bracket-depth-zero
  boundary detection instead of the general whitespace tokenizer (Phase 3 follow-up, 2026-06-02).
  Boundary: next `I[p,w]=`, `Q[p,w]=`, etc. at paren-depth 0. Multiple assignments on one line OK.
  Backslash line-continuation (`\` at end of line) is also supported.
- `Dual.NMax(a, b)`: picks the larger N for binary operations; constants have N=0 (zero gradient).

### Step 2: SDD device
New file `src/Core/Devices/SddModel.cs`:
- `ComponentModel` subclass, `ModelKind.Nonlinear`, `Stamp` is a no-op.
- Constructor receives cached equation ASTs + resolved scope-variable dict.
- `Evaluate(in PortVoltages v)` calls `SddEvaluator.EvalDual` for each port equation → (i, q, dg, dc).

`ComponentModelFactory` change: "SDD" added to `_parameterizedTypes`. `CreateSddModel` parses
`Value.String` equation entries, validates `F[]/C[]/w≥2` hard errors, skips `In[]/Nc[]` noise entries.

`Elaborator` change: `ResolveSddParameters` special-cases SDD — stores equation strings as
`Value.String`, walks each equation AST to collect scope-variable references, resolves them from scope,
and injects them as `Value.Real` in the resolved-params dict. The factory then sees both strings and
resolved numbers.

Gate (tests/Core.Tests/Devices/SddModelTests.cs): hero SDD parses; `Evaluate` at (−3.05, 48) returns
i2 ≈ 49.11 mA, i1 = −61 mA, gm ≈ 62.4 mS, gds ≈ −9.45 µS (negative).

### New device: VoltageSourceModel
`src/Core/Devices/VoltageSourceModel.cs` — Group-2 branch-current element. Stamps Va − Vb = V
(branch constraint + KCL). Parameter `V=`. Required for bias sources in the DC hero circuit.
Registered as type `V` in `ComponentModelFactory`.

## Enabled semantics — AnalysisChain (brief-sweep-revamp-2-dispatch, 2026-06-17)

`AnalysisChain` (`src/Core/Design/AnalysisChain.cs`) is a pure, framework-free resolver that
honors `Analysis.Enabled` when walking a parametric-sweep chain.

- **`ResolveEffectiveInner(innerName, tb)`**: descends from `innerName`, skipping disabled
  `ParametricSweepAnalysis` nodes (collapse), until it reaches either an enabled sweep or any base
  analysis. Used by `ParametricSweepEngine.Run` in place of the former raw name lookup.
- **`ResolveEffectiveTop(root, tb)`**: from the chain root, skips disabled outer sweeps to the
  first thing that actually runs. Used by `SchematicRunService` dispatch.
- **`IsChainRunnable(top, tb)`**: true only if the chain eventually bottoms out at an enabled base
  analysis. Used by `SchematicRunService` to skip dead chains (disabled base).

Semantics:
- Disabled sweep → collapses (its axis is dropped; its inner runs in its place). Spec is untouched.
- Disabled base → whole chain is inert (nothing runs, no result emitted).
- Both sweeps disabled → effective top is the base; runs as a plain single-point analysis.

Gate: 9 tests in `tests/Core.Tests/Design/AnalysisChainTests.cs` (pure); 4 integration tests in
`tests/Engine.Tests/Parametric/ParametricSweepEnabledTests.cs`. Build 0W/0E; 1629 total pass.

Stage 3 (unified editor UX with per-axis Enabled + reorder) follows.

## `.cnl` enabled flag + Spec persistence (brief-sweep-revamp-1-persistence, 2026-06-17)

- **`enabled=false` in `.cnl`**: `CnlWriter` appends `enabled=false` to every sub-line of any
  analysis whose `Enabled` is false (multi-segment S-param gets it on each segment line). `CnlReader`
  has a shared `ParseEnabledToken` helper wired into all six typed parsers (DC, HB, S-param,
  parametric sweep, loadpull, loadpull-pursuit). Absent token → `Enabled = true` (default). Gate:
  5 tests in `tests/Core.Tests/Netlist/CnlEnabledTests.cs`.
- **Sweep Spec round-trip through `.csch`/`.canl`/clipboard**: `CschAnalysis` DTO now carries
  `PsaMode`, `PsaStart`, `PsaStop`, `PsaStepOrCount`, `PsaKind` for spec-form sweeps. `ToDto`
  prefers the Spec fields (omits `PsaValues`) when `Spec` is non-null; `FromDto` has a Spec arm
  (tried first) and a list arm. Explicit-list PSAs still round-trip unchanged. Gate: 7 new tests in
  `tests/Ui.Tests/AnalysisSerializationTests.cs`.

## Parametric sweep — Start/Stop/Step|Npts (brief-parametric-sweep-stepcount, 2026-06-16)

`SweepExpander` and `SweepAxisMode` moved from `src/Ui/Schematic/` → **`src/Core/Design/SweepExpander.cs`** (no Avalonia deps) so the CNL reader can use them without violating the Core→UI firewall.

`SweepSpec` (in `Analysis.cs`) redesigned: `{ Start, Stop, StepOrCount, Mode: SweepAxisMode, Kind: SweepKind }` — no `Variable` field. `ParametricSweepAnalysis` gains a **spec constructor** that expands eagerly (populates `SweepValues`) and stores `Spec` for `.cnl` round-trip fidelity; the existing array constructor sets `Spec = null`.

**CNL reader** (`TryParseParametricSweepDirective`): now accepts both `Values=v1,v2,…` (list; `Spec=null`) and `Start= Stop= (Step= | Npts=) [log | log=true]` (spec; `Spec` retained). Bare `log` keyword detected via `HashSet<string> bare` (same pattern as SParam parser).

**CNL writer** (`FormatParametricSweepAnalysis`): emits compact `Start= Stop= Step=|Npts=` form when `psa.Spec != null`, falling back to `Values=` list for array-only PSAs.

6 gate tests in `tests/Core.Tests/Netlist/SweepSpecCnlTests.cs`: StartStopStep (121 pts), StartStopNpts (7 pts linspace), Log (4 decades), log=true keyword, Values regression, round-trip compact form. Build 0W/0E; 260 Core.Tests pass.

## Vdc — DC voltage source (brief-vsource-vdc-fix, 2026-06-16)

`VoltageSourceModel` **deleted**; replaced by `VdcModel` (`src/Core/Devices/VdcModel.cs`).

**Root bug:** legacy `V:` CNL lines produced `Vac=` parameter names, but `VoltageSourceModel.Stamp` only read the `"V"` key → voltage silently stamped as 0 V at DC.

**Fix architecture:**
- `VdcModel` stamps at DC only: `Math.Abs(omega) < OmegaTolRads (1 rad/s)` → stamps `Vdc` value; all other ω → stamps zero. Reads `"Vdc"` param (alias `"V"`). Keeps `LastBranchIndex` for HB linear extractor.
- **CnlReader backward compat** (`ParseInstanceLine`): if `typeName == "V"` (OrdinalIgnoreCase) → remap to `"Vdc"`. Also normalizes `Vac=` or `V=` param names to `Vdc=` for any `Vdc` instance (if no `Vdc` override already present). Old `.cnl` files with `V:` sources load and simulate correctly with no manual conversion.
- **`ToneSourceModel`**: fixed 0-Hz tone superposition — at ω=0, now accumulates `_currentVdc` plus all tone phasors whose `FreqHz ≈ 0` into the source voltage. `GetZeroHzToneWarnings(path)` returns a list of warnings (one per zero-Hz tone with non-trivial phasor); called by the Elaborator which routes them into `netlist.AddWarningOnce`.
- **`HbLinearExtractor`**: updated `VoltageSourceModel` references at lines 390 and 547 → `VdcModel`.
- **`ComponentModelFactory`**: `"V"` factory entry → `"Vdc"` → `new VdcModel()`.

8 gate tests: 4 Engine.Tests (`tests/Engine.Tests/Devices/VdcComponentTests.cs`) + 4 Ui.Tests (`tests/Ui.Tests/VdcComponentTests.cs`). Build 0W/0E; 1419 total tests pass.

## VAR variable component — design note
`NetExtractor` in `src/Ui` routes `SymbolKind.Var` component parameter rows into `Cell.Variables` (sub-cell) or `TestBench.GlobalVariables` (testbench top). No Core change was needed: `Elaborator.BuildGlobalScope` already binds `tb.GlobalVariables` and `BuildCellScope` already binds `cell.Variables`, so per-cell isolation and HB sweepability are automatic. VAR never appears as an `Instance` or `ElaboratedComponent`; its `EngineReference` sentinel is `"VAR"` (not a factory primitive).

## CNL generic instance parser — unit token handling (brief-unit-token-phantom-nodes, 2026-06-16)

The CNL generic instance parser (`ParseInstanceLine` in `CnlReader.cs`) now recognises
**identity/measurement unit tokens** — `V`, `A`, `W`, `dBm`, `dB`, `kV`, `mV`, etc. — as
consumable trailing units after a `key=value` param token. Previously, only linear-scale units
(in the `Units._scales` table) were consumed; tokens like `V` and `dBm` that are absent from that
table leaked into the net list as phantom "net" entries, shifting all subsequent node indices.

**Root cause fixed:** A P1Tone line `Pavl=Pin dBm` or a Vdc line `Vdc=-3.05 V` placed `dBm`/`V`
in the net section because `Units.IsKnown` is intentionally linear-scale-only (see `Units.cs`
comments). The fix adds `Units.IsRecognizedUnit(u)` = `IsKnown(u) || _identityUnits.Contains(u)`,
where `_identityUnits` is a fixed allow-list of valid-but-identity units. This predicate replaces
`IsKnown` in both the separate-token path and `TrySplitGluedUnit` in `ParseInstanceLine`.

**Position gate (safety):** the consume check fires **only inside the `key=value` param branch**,
never in the leading net section. A net token (even one named `V`) can never appear in that
position, so the single-letter `V` is unambiguous.

**Evaluator:** `Evaluator.ApplyUnit` extended to treat identity/measurement units as scale = 1.0
(value already in base unit) rather than throwing `Unknown unit`. Linear-scale units are unchanged.

**Node-picker effect:** with phantom nodes removed, the V-cube node axis contains only real
user-named nets. The existing `__`-prefix filter hides internal engine-minted nodes. No additional
picker code was needed — the cleanup is fully from the parser fix.

5 gate tests: `CnlReader_P1Tone_NoPhantomUnitNets`, `CnlReader_Vdc_NoPhantomUnitNets`,
`CnlReader_DoesNotEatRealNet`, `GluedUnit_StillSafe` (in Core.Tests) and
`Hb_Vout2_NonZeroFundamental` (Engine.Tests — verifies back-solved linear node is non-zero
after the index-shift bug is eliminated).

## SDD single-index equations + net-arity validation (brief-sdd-single-index-nets, 2026-06-16)

SDD equations accept **single-index** sugar in both `CnlReader` and `ComponentModelFactory`:
- `I[p]=expr` ≡ `I[p,0]` (port-p current); `Q[p]=expr` ≡ `I[p,1]` (port-p charge).
- Two-index `I[p,w]` and `I[p,1]` (legacy charge form) still work unchanged.

**CnlReader**: `SddAssignmentHeader` regex extended to `\d+(,\d+)?` — single-index `I[1]=` is now a valid boundary marker, so equation fragments no longer leak into the net list as phantom nodes. `ParseSddLine` also strips any `key=value` tokens in the net section (e.g. `Ports=2`) into parameter overrides rather than treating them as net names.

**Elaborator**: `RxSddEquation` extended to `^[IFCQi][^\[]*\[` to pass `Q[p]` single-index through to the factory. Odd net count (not divisible by 2) now throws: `"SDD '<inst>': expected an even number of nets (2 per port: +,−); got N."` — no more silent `portCount = N/2` truncation.

**Factory**: `RxCurrentEq1 = ^I\[(\d+)\]$` and `RxChargeEq1 = ^Q\[(\d+)\]$` handle single-index forms. The shared `ValidateAndBind` helper gives a clear error when an equation references a port index beyond the net count: `"equation references port P but only K ports of nets were given (need 2P nets for a P-port SDD)"`.

**User-facing correction**: `SDD:X1  Vin 0  Vout 0  I[1]=…  I[2]=…` — 4 nets (each port referenced to ground). `_v1 = V(Vin)−V(0)`, `_v2 = V(Vout)−V(0)`.

**Node-picker (deferred)**: `n1/n2/n3`-style auto-named nodes are real user nets — filtering them from the axis combo requires a scope decision (hide `^n\d+$`? user toggle?). Not implemented here.

7 gate tests: 6 in `tests/Core.Tests/Devices/SddSingleIndexTests.cs` (net/equation split, `I[p]` binds current, `Q[p]` binds charge, two-index regression, odd-net error, port-ref-beyond-nets error) + 1 in `tests/Engine.Tests/HarmonicBalance/SddSingleIndexHbTests.cs` (full HB sweep with single-index SDD, Vout fundamental non-zero, no phantom nodes in axis).

## Ask before
- Changing the `.cnl` or JSON format (round-trip + interop).
- Changing the scope/binding rule or the kinded-value model (ripples into the engine and SDD).