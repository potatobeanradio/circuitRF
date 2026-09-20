# src/Design — resolved findings (detail, off the CLAUDE.md growth path)

## railRF brief 23 — mount and unmount, and comparing against the run you just did (2026-09-20)

A first-time designer asked how to take a placed part off the layout in order to depopulate and
re-simulate, and was told to go and delete it from the artwork. That answer is wrong three ways: it
is destructive, it is not what was meant, and it throws away the mounting loop the geometry gave the
part, so the part cannot be put back the way it was. `RailPart.Mounted` is the fix — a bool
defaulting to true, written into the `.crail` only when it is false.

### Absent means mounted, and that is the whole of the format change

`CrailPart.Mounted` is `bool?` and `ToFile` writes `p.Mounted ? null : false`. With
`JsonIgnoreCondition.WhenWritingNull` already set on the reader/writer, an ordinary document gains
no line at all and every `.crail` written before this flag existed reads exactly as it did. A
non-nullable `bool` with a `true` default would have written `"Mounted": true` onto all thirteen
rows of the shipped example for no reader's benefit, and — worse — a plain `bool` field would have
defaulted an OLD document's parts to `false` had anyone ever flipped the default.

### The exclusion has exactly one site, and the counts moved with it

`PdnSweep` builds its branch list from `request.Parts.Mounted` instead of `.Models`.
`RailPartModelSet.Models` still carries every row, because the parts table lists an unmounted part
— greyed, with its part number, its position and its computed mounting loop intact, which is the
whole difference between unmounting and deleting. Everything on that set that describes THE ANSWER
moved to the mounted view with it: `Unresolved`, `ModelledFromFile`, `WithoutBiasCurve`,
`WithoutEsrBasis`, `Indicative`, `AnyIndicative`, `Warnings`, `Summary`, and `Annotate`'s four
metadata cubes. A `part` axis naming a part the curve does not contain would be the same defect as
the curve losing one silently.

**And the unmounted set is NAMED**, on `Summary` and in the result's notes. A curve that has quietly
lost a bulk capacitor looks entirely normal — the shipped example without `C10` is 16.3 dB worse and
nothing about the picture says why.

**The removal ranking needed no change at all** (R-rail23-4a). `PdnSweep.Rank` ranks the BRANCHES,
and an unmounted part contributes none — so it is absent from the ranking rather than ranked at
zero, which is what it would have read as, and which is indistinguishable from a part that is not
earning its place.

### Two independent paths to one number, and they agree to three decimals

The gate is the cross-check rather than a hard-coded figure. Q2's ranking re-solves the sweep once
per part with a branch dropped from a list it already built; unmounting goes through the document
row, the resolver and a whole fresh run. On the shipped Power Rail example both put the worst margin
at **−15.567 dB** (the README publishes 16.3 dB → −15.6 dB), so a change that broke either would
move one and not the other. Measured, not asserted from the README.

### `RailComparisonReport.InputChanges` — and what was deliberately left out of it

R-rail23-3c: a comparison of two runs of one document must name what changed between them, or the
reader is left diffing curves to infer it. It is computed from `RailComparison.Parts` alone — the
match already pairs the two documents' own rows — so it costs nothing and works for two different
designs as well as for two runs of one.

**A changed mounting inductance is NOT on that list**, and the first draft's was what caught it:
brief 16's own gate asserts the findings block is three sentences, and its fixture changes `C3`'s
mounting loop, so the fourth line broke it. The right reading is that the loop is already the
subject of the per-part table AND of the first of R-rail16-6's three sentences — *"these three parts
got worse mounting"* — so naming it at the top is the same finding twice. The block is for the
inputs nothing else on the page reports: what is fitted, and what part it is.

### The DC half needed nothing, and that is a fact about today's code

R-rail23-1e says a decoupling capacitor carries no DC current, so unmounting one changes no drop and
railRF should say nothing about it rather than print an unchanged number as a finding. It already
does: `PdnDcRequest.ShuntParts` is never populated by the window at all, so the DC path does not read
part rows. `RailDcRun.WithSourceLevel` copies `rail.Parts` into its working copy purely so the copy
does not silently hold less than the rail it copies — its own comment says so — and nothing
downstream reads them. **Brief 25's series element is the case that will need a refusal here**, since
unmounting one opens the rail rather than removing a shunt branch; nothing in the document can
express one yet.

## railRF brief 21 — two numbers with one name, and where the breakdown's extra millivolts are (2026-09-20)

A first-time designer's pass produced three reports, and the first two were the same failure: one
window printing two different quantities under one name. Nothing here was an arithmetic bug — every
number was correct — so all three fixes are naming, presentation and one measurement.

### The 465 Ω and the 23 mΩ are both right, and now say which is which

The board map is `PdnPlaneModes.Of`'s `ImpedanceMap`: the PLANE PAIR alone, its copper, its shape
and its stackup, driven from one observation port. The plot's curve is `PdnSweep`'s: the DECOUPLED
rail — thirteen capacitors, their ESR, their mounting loops, the source's R and L. At 50 MHz a bare
plane pair between two points is hundreds of ohms and the same rail with its decoupling is tens of
milliohms. The correct sentence already existed, in `PdnPlaneAnswer.Notes` — a list on a different
tab, which is not where somebody looking at a picture reads.

`PdnImpedanceNames` is now the one place the distinction is spelled (`plane pair alone`, `this
rail`, and the note itself). The map caption, the hover readout, the strip over the map, the |Z|
tab's tooltip, the plot's Y axis and every marker on it take their words from it. **The marker
needed a new seam**: its text came from the cube name, so it read `Z(1,1) Mag` — a matrix-element
shorthand that names no quantity at all. `Trace.QuantityName` is an owner-supplied override that
`TraceLabeler.QuantityFor` honours, and `ComputeMinimalLabels` was re-pointed at `QuantityFor`
rather than keeping its own copy of the cube/network dispatch, so a legend and a marker box cannot
honour it differently.

### R-rail21-2c — the breakdown's total is 50.131 mV, and the gap is a parallel leg

**Measured on the shipped Power Rail example** (`circuitrf rail "examples/Power Rail/Sensor
board/Sensor board.crail"`), not inferred. Thirteen rows:

| drop (mV) | R (mΩ) | I = drop/R (mA) | what |
|---:|---:|---:|---|
| 22.722 | 64.919 | 350.0 | 26.462 mm of 0.209 mm BOT copper |
| 21.000 | 60.000 | 350.0 | the source's series resistance |
|  1.582 |  5.441 | 290.8 | 3.927 mm of 0.387 mm TOP copper |
|  1.318 | 22.264 |  59.2 | 9.334 mm of 0.227 mm BOT copper |
|  1.069 |  3.056 | 349.8 | 2.577 mm of 0.45 mm TOP copper |
|  0.974 |  2.782 | 350.1 | 2.313 mm of 0.43 mm TOP copper |
|  0.619 |  1.770 | 349.7 | the reference return |
|  0.322 |  5.441 |  59.2 | 3.927 mm of 0.387 mm TOP copper |
|  0.219 |  0.627 | 349.3 | 2 parallel vias |
|  0.182 |  0.627 | 290.3 | 2 parallel vias |
|  0.087 |  1.471 |  59.1 | 1.5 squares of IN3 copper |
|  0.037 |  0.627 |  59.0 | 2 parallel vias |
|  0.000 |  0.647 |   ~0  | 0.7 squares of IN3 copper |

They sum to **50.131 mV** — which is the 50.1 the designer reported, and the README's published
table shows only its top seven rows, which is where the 49.3 came from. `U1.VDD` is **48.368 mV**
below the source. The difference is **1.763 mV**.

Sort the rows by current and it is immediate. Six groups carry the full 350 mA and drop 46.603 mV
between them. The rest divide: four carry **290.8 mA** and drop 1.582 + 0.182 = **1.764 mV**, and
four carry **59.2 mA** and drop 1.318 + 0.322 + 0.087 + 0.037 = **1.764 mV**. Two legs across one
pair of nodes drop the same voltage, as they must. 46.603 + 1.764 = 48.367, the port's own drop;
46.603 + 1.764 + 1.764 = 50.131, the table's.

**So the gap is the second parallel leg, counted once by the port and twice by the table. It is NOT
the reference return** — the brief's own guess, and the obvious one, because the return is "not in
the source-to-port path". It is: the port's voltage is V(power) − V(reference) at its pin field, so
the return copper is inside the loop that drop is measured across and is counted exactly once.

The arithmetic in `PdnBreakdown.Rank` is right and unchanged — its shares are deliberately taken
against the sum of the rows so they add to one exactly whatever the board is. What was missing is
that the sum had no name and the difference had no explanation, so `PdnBreakdown.TotalLine` and
`PdnBreakdown.Reconcile` sit beside `Rank`, which is the one place that knows what the total IS.
The reconciliation prints only where the two differ by more than display rounding (n rows × half a
display unit), because a note about two numbers that agree is noise under a table.

### The legend was unreadable because the PLATE shrinks, not because the fitting rule was wrong

R-rail18-4 fixed the three labels OVERLAPPING on a small canvas by shrinking them to fit, with no
floor, on the argument that small text is recoverable and a missing model name is not. Measured:
`RailMapLegend.Box` is 7.5 % of the map's longer side in DBU, so on a 400 px pane the whole plate is
about 24 device pixels tall, its colour ramp takes half of that, and **the strip left for the labels
is 9 px**. The fitting rule was then doing exactly what it was told — the caption came out at 4.99 px
at 200 px and 10 px was simply larger than the box.

So there are two halves to the fix and only one of them is the floor:

- `RailMapRenderer.LegendFloorPx` (= `LabelSizePx`, so the legend is never smaller than the marker
  callouts drawn beside it) is the size text is never drawn below. Below it the CAPTION is dropped
  and the end labels kept — they are the scale — and below that nothing is drawn at all, because a
  colour ramp with no numbers is a coloured box sitting where the answer would be. This reverses
  R-rail18-4's "the caption may never be dropped": a six-pixel caption names the model to nobody
  while taking the space the two end labels need.
- `TryPlate` gives the plate a MINIMUM SCREEN SIZE — tall enough for a floor-height label band, wide
  enough for the three labels at the floor — anchored at the world box's own top-left so a dragged
  plate stays where it was put, and clamped to the viewport. R-rail18-4 ruled out widening the plate
  and was right about the reason (the box is world geometry and `RailMapScene.Bounds` frames it),
  but that reason binds on the SCENE, which is what Zoom to Fit reads. Painting a minimum-size plate
  is a screen decision and leaves the world extent, the drag and the framing untouched.

**Two float traps, both of which silently dropped the caption on every legend the plate had been
widened for.** `TryPlate` derives the label band from the plate height by one route and
`LayOutLabels` compares it by another, so an exactly-equal band read 9.9999995 px; and the plate is
sized from the strings measured at the FLOOR while the fit measures them at `LegendSizePx` and
scales down, so a plate widened to the exact requirement lands a hundredth of a pixel on the wrong
side of it. Both are answered with half a pixel of slack and a `Math.Max` onto the floor, rather
than by making the two routes agree exactly — which they cannot, being different arithmetic.

Measured after, on the |Z| map: **10, 10 and 10.05 px at 200, 400 and 1200 px canvas widths**, where
before they were **1.57, 3.15 and 9.44 px**. The 10.05 is not a rounding artefact — at 1200 px the
world plate is genuinely wide enough for the fit to land just above the floor, which is the 2026-09-19
"the text tracks the plate upwards" behaviour still working. At 200 px the caption is dropped and the
two end labels are kept at the floor; at 36 px nothing is drawn.

## The Smith Chart's RLC vocabulary: eight kinds, two formulas, one element set (2026-09-20)

Owner: put the six new two-element RLC parts in the Smith Chart's element list too. The Chart's
`SmithElementKind` is a deliberately CLOSED list — every member has to map one-to-one onto a
component `ComponentTypeRegistry` already declares — so the six were admissible the moment the
library gained them, and nothing here needed a factory registration or a golden-reference test of
its own.

**The element set is `CircuitRF.Core.Devices.RlcElements`, not a second enum.**
`SmithComponentMap.RlcElementsOf` answers which of R, L and C a kind carries, and it answers with
the SAME flags the engine model reads: `SmithElementKind.Prl` binds `SymbolKind.Prl` binds engine
`PRL`, whose model is `ParallelRlcBranchModel(RlcElements.Rl)`. `src/Design` already references
`src/Core`, so there is one place "a PRL has no capacitor" is written down. The symptom of that
drift would have been a trajectory that looks plausible.

**Three consumers, and each of them lost seven arms rather than gaining six.**
`SmithCascade.Immittance` is now two cases — the series branch's `Z = R + jωL + 1/(jωC)` and the
parallel one's dual — each summed over the elements present; `SmithInverse.SolveRlcFamily` is the
same shape, with each parameter's inverse reading the OTHER reactance's term out of the current
values and getting zero where the part has no such element; `SmithComponentMap.Parameters` reads
four rows keyed by the element set for all eight kinds.

**An absent element contributes a ZERO term, never an infinite one, and the two are only
distinguishable at the corners.** A PRESENT C of zero gives 1/(ωC) = +∞ and pins, which is the
answer SRLC has always given and is correct. An SRL has no C at all and must not go near that
arithmetic — the same distinction that made `0 · ∞` a NaN in the engine model (see
`src/Core/RESOLVED.md`), written the same way here on purpose.

**The default gripper parameter is a rule now, not a table.** §3.3 said "L for an SRLC, C for a
PRLC" — the REACTANCE rather than the loss, because dragging a part's loss is a move nobody
reaches for first. The two-element members only add "and whichever of the two it has": an SRC has
no inductor, a PRL no capacitor, and neither ever falls back to R.

**Everything keyed off `SmithComponentMap.AllKinds` came along for free, and that is the payoff of
the way those tests were written.** `SmithCascadeTests.TheClosedFormAgreesWithTheEngine_…` builds a
real `.cnl` and compares the closed form against the engine at every node — the six new kinds in
both placements passed at under 1e-9 relative with no new assertion, which is also an independent
check on the engine models added the same day. `SmithInverseTests.NothingReturnsNaN_…` swept them
likewise. Only the per-kind VALUE fixtures (`Dut`, `Base`) had to gain rows.

**One hand-written fixture claimed to cover a table and did not have to.**
`SmithClipboardTests.EveryKind()` is the copy-out/paste-back round trip and is written out element
by element; it silently stopped being "every kind" the moment the vocabulary grew. It now has a
companion test asserting it covers `AllKinds`, which is the cheap way to stop that recurring — and
the round trip is where it matters most, because the network strip WRITES a per-parameter list and
`SmithPasteRecognizer` READS one, so a part emitting a `C` it does not have would come back as a
refusal or as a value nobody typed.

**The Add/Insert flyout is grouped, because a flat one had outgrown the window** (owner,
2026-09-20). 17 kinds in up to two placements each is 30 rows at a 39 px glyph apiece — about
1,350 px against a window 741 px tall. It already scrolled at 18; the six new members are what made
scrolling the normal way to reach the bottom half. The eight RLC members now sit under one `RLC`
submenu and the top level is 15 rows.

- **`ElementMenu` is unchanged and still flat.** It is the VOCABULARY — what may be placed — and
  every command, test and future surface reads it. `ElementMenuGroups` is a PRESENTATION of the
  same rows, holding none the flat list does not; the gate asserts set equality in both directions
  AND order, because a row that fell out of the layout is a part that can no longer be placed from
  either menu with nothing said anywhere.
- **The grouping lives on the view model, not in `SmithChartView.axaml.cs`.** Two surfaces build
  these menus from one list (R-smith6-2), and a shape decided in one of them is a shape the other
  does not have.
- **The eight that moved are the ones a reader picks BETWEEN**, which is what makes it a grouping
  rather than a hiding place: they are one part with a different subset of R, L and C in it, and
  choosing among them is a second question after "I want a lumped combination". R, L and C stay at
  the top level, because reaching for an L is not that question. Membership is
  `SmithComponentMap.RlcElementsOf`, so a ninth member joins the group on its own.
- **The submenu's parent row carries no command.** A `MenuItem` that both opens children and
  invokes something is a control whose click does two things, one of them by accident.

## User-installed technologies: the middle rung the resolution chain never had (2026-09-20)

`TechnologyCatalog` + `TechnologySummary` in `src/Design/Layout`, and the Settings ▸ Technology tab
above them. A user's own `.ctech` in `<per-user state>/technologies` is now an ordinary member of the
list a new workspace is created from, and which member is the default is a preference rather than a
`const`. Gated by `tests/Ui.Tests/TechnologyCatalogTests.cs` (11 tests, ~100 ms).

**1. Dropping a `.ctech` beside the executable had never done anything, and could not have.**
`ShippedTechnologies.Discover` enumerates `Assembly.GetManifestResourceNames()` — EmbeddedResource
manifest names, not a directory — and nothing anywhere scanned `AppContext.BaseDirectory`, the `.app`
bundle or `LocalApplicationData` for a technology. What made this worth fixing rather than
documenting is that the same resolution chain already existed twice: `ThemeResolver.UserThemesDir`
and `TemplateManager.UserTemplatesDir` both go workspace → per-user → embedded. Technologies were the
one asset kind with the middle rung missing.

**2. A second type, not a wider `ShippedTechnologies`.** That type means exactly one thing — what
this binary carries — and its "ship" gate test, its `TechPersistence` round-trip test and a dozen
fixtures across `Ui.Tests` depend on that meaning. Widening it would have made the gate that proves
circuitRF's own five technologies parse depend on what is in somebody's home directory.

**3. `WorkspaceCreate.DefaultTechnologyId` had to stop being a `const`.** It was
`public const string DefaultTechnologyId = ShippedTechnologies.DefaultId`, and a constant is folded
at every call site at compile time — so every caller would have gone on naming the shipped default
while the dialog beside it opened on the user's choice, with nothing to see. It is a static property
now.

**4. The preference is READ below the firewall and WRITTEN above it**, which is
`RevisionIdentity`'s arrangement and was adopted for its reason. `circuitrf new workspace` with no
`--tech` must create what the dialog would (R-aut3-3), so `TechnologyCatalog.DefaultId` reads
`preferences.json` directly — reading a JSON file in the per-user directory crosses no firewall.
Writing goes through `AppPreferencesIo`, which holds the one in-process copy and writes the file
whole; a second writer down here would silently drop whatever another window had changed.

**5. An id collision is a REFUSAL, and shadowing was the tempting wrong answer.** A user file whose
stem matches an installed or shipped id is refused, naming the collision and the remedy. Shadowing
would let one id mean different bytes on two machines — and the id is what the `.cws` records, what
`--tech` takes and what the default preference stores, so a design's own history would disagree with
itself. `TemplateManager` shadows by name; that is defensible for a template, which nothing refers to
by id afterwards, and wrong here.

**6. A removed default falls back and is deliberately NOT cleaned up.** `DefaultId` answers the
shipped default when the preference names nothing installed, so deleting a file cannot break File ▸
New Workspace. The preference keeps naming it, so re-adding the same file restores the choice instead
of having silently forgotten it. `PreferredDefaultId()` is the unfiltered read, for the one caller
that needs to tell "never chosen" from "chose one that has gone".

**7. Nothing is cached.** The Settings tab adds and removes files in that directory while the
application is running, and a cache would need invalidating from each of those places. `All` parses a
handful of small JSON files per call, which is what `NewWorkspaceDialog` already did on every open —
and it now parses FEWER, because the entry carries the name the combo box displays.

**8. `AuthoringCliVerbTests` had to be pinned to a throwaway state directory.** Its gates compare a
child process byte for byte against an in-process call; once the default became a preference and the
catalog could hold user files, both halves were reading the developer's own installation. They agreed
by luck on a machine that had never opened the tab. In-process through `AppDataRoot.RedirectTo`,
out-of-process through `CRF_STATE_DIR` set on every `RunCli` launch.

**9. The summary's thickness needed two decimals, not `LayoutUnits.Format`'s four.** A stackup's sum
carries every entry's rounding: the nominally 20 mil shipped board reads **22.7559 mil** once its two
1.4 mil coppers are added. The last two digits are arithmetic, not a specification, and this is a
summary — the technology editor is where the exact figures are.


## The Smith cascade evaluator, and the one sign the design note had backwards (2026-09-19)

Brief 2 of the Smith Chart series: `src/Design/Smith/SmithCascade.cs` and its
`.Trajectories.cs` partial — the whole numeric half of the tool, closed form, with no matrix, no
solve and no iteration anywhere. Gated by `tests/Ui.Tests/Smith/SmithCascadeTests.cs` (46 tests,
~100 ms). Seven findings.

**1. The engine oracle agreed on the first run, in all 18 cases, with nothing to fix.** That is
the headline and it is worth stating plainly, because the brief's own closing instruction assumes
it will not: for every `SmithElementKind` in every legal `SmithPlacement`, the equivalent `.cnl` is
built, `SParameterEngine` is run, S11 is converted back to an impedance and compared against
`SmithCascade.Evaluate` — at **every node** of a three-element cascade, not just the last —
and the worst relative error was inside 1e-9 everywhere. No sign, no port order, no reference
impedance and no `tan`-for-`cot` came out of it. The gate's value is not spent by passing: it is
what makes the next element, and brief 3's inverse, cheap to trust.

**2. The design note said a shunt trajectory through the pole is traversed through Γ = 1. It is
Γ = −1**, and the note is corrected (§4.2; it said it twice). Every constant-*conductance* circle
passes through the SHORT, and every constant-*resistance* circle through the open. `B → ±∞` is
`Y → ∞` is `Z → 0` is `Γ = −1`. The measurement is in the gate rather than in an argument: a 135°
open stub's polyline contains a point within 0.05 of −1 and none near +1.
`brief-smith-2-cascade.md` R-smith2-8 carries the same sentence and is left as authored — a brief
is the record of what was asked.

**3. A SHORTED stub's trajectory starts at the short, not at Γ_in — the one place §3.5's rule and
its own sentence part company.** The rule is `θ(t) = t·θ_total`, and a zero-length shorted stub is
a dead short across the node, so `t = 0` is Γ = −1 exactly rather than the impedance the walk
arrived with. Every other kind starts at Γ_in, including the open stub (whose zero-length form
contributes nothing) and the series line. **And the arc does not in general recover the input
point either** — that was the first guess and the test refuted it: the curve reaches Γ_in only
when the stub passes a quarter wave, because below that its susceptance never crosses zero. It is
pinned by a test rather than fixed, because the rule is what the note and the brief both specify
and the picture it draws is the honest one: this is where the node goes as the stub grows out of
nothing. If it reads wrong on the chart, the thing to change is the rule, in one place, with the
gate already standing.

**4. The walk carries a (numerator, denominator) PAIR, not an impedance.** Z = 0 and Z = ∞ are both
ordinary values on a Smith chart and both occur in ordinary designs — a quarter-wave open stub is
Y → ∞, so the node behind it is a short, and the shunt element after THAT would need 1/0. Keeping
the node as `Zp(N, D)` and dividing only at the boundary makes every one of those a finite
expression, and Γ = (N − Z₀·D)/(N + Z₀·D) never divides by zero at all. The `tan θ` forms stay in
`Immittance`, because that is what §3.3's table states and what a reader checks against; the walk
and the trajectories use the equivalent `cos`/`sin` forms, which have no pole in them. Each step is
renormalised to order 1 so a long cascade of large impedances cannot run out of exponent.

**5. The oracle netlist's three non-obvious lines.** An `S1P` in **series** is an SnP with N+1 nets
(the extra one is its floating reference) and in **shunt** one with N (the reference is ground) —
`InstanceNetContract` states the rule and the reader tells the two apart by how many were written.
An **open stub** is a `TLIN` whose far net is connected to nothing else; that solves, because
`TLIN` is ground-referenced and its Y₂₂ is non-zero, so the dangling node is not a singular row. And
the **generator is a `Z_Port` to ground with `Z[1,1]=complex(re,im)` and no spaces anywhere in the
expression** — the instance-line parser splits on whitespace and would read the pieces as nets.
A shunt element does not advance the net name: it hangs off the node it found, which is what makes
"the impedance looking back toward the generator" the same node before and after.

**6. A Touchstone element must use `SnpModel`'s OWN interpolation defaults or the oracle
disagrees** — cubic spline, MAGNITUDE/PHASE, interpolated in S, through `TouchstoneCache`. Those
are not this file's choice to make: the element and the engine have to read the same numbers out of
the same file, and the format in particular is easy to get wrong because `SnpInterpolator`'s own
constructor defaults to RealImag while `SnpModel` asks for MagPhase. The target frequency is
**clamped into the file's stored range here as well**, which is exactly what `WarnClamp` — the
engine's default, and therefore the oracle's — does with it anyway; doing it on this side too keeps
a drag or a sweep past the end of a file from emitting one warning per sample. The fit itself is
`TouchstoneCache`'s and this brief adds no second cache: a 201-point sweep over an S2P element adds
no spline fits at all.

**7. `tests/Firewall.Tests/UserFacingTextGateTests` is a registration point nobody lists.** Eight
new refusal sentences below the firewall failed the firewall build, which is the right outcome and
is not in any brief's table. They are allow-listed beside brief 1's own five, with the same
reasoning: they are refusals raised WHILE EVALUATING and brief 4's window catches the
`InvalidDataException` its siblings already throw, so a `Diagnostic` here would mean two catch
paths inside one tool for one class of problem. The family converts together or not at all.


## The `.csmith` document, and the registration points the table did not have (2026-09-19)

Brief 1 of the Smith Chart series: `src/Design/Smith/` — `SmithDesign` and its element records,
`SmithComponentMap`, `SmithDesignIo`, `SmithClipboard`, `SmithGeneratorImport` — plus the
registration that makes `.csmith` a document type the shell and the CLI both know about. No
arithmetic, no UI. Five findings.

**1. There are EIGHT registration points, not seven, and the eighth fails a test rather than a
user.** The overview's table lists the plist, the `.wxs`, `circuitrf-mime.xml`, `App.OpenFiles`,
`WorkspaceViewModel`, `WorkspaceScanner` and `DocumentKinds`. The Linux `.desktop` entry's
`MimeType=` line is a ninth place the type name appears and is not on it — and
`WBondStandaloneTests.TheDesktopEntryAndTheMimeFileClaimExactlyTheSameTypes` is what says so. That
test is the reason the omission cost a minute rather than a release: a MIME type declared in
`circuitrf-mime.xml` with no matching entry in the `.desktop` file is a type the desktop knows the
name of and has no application for, and the only symptom is a double-click that opens nothing.

**2. A `DocumentKind` with no case in `check` is not a crash — it is worse than one.** The brief
expected `check` to throw on an unhandled kind. It does not: `CheckPath`'s `default:` arm reports
`CheckUnknownKind`, whose sentence is *"Nothing circuitRF reads is named '<path>'"*, at severity
Error. So a perfectly well-formed `.csmith` — of a type the same change had just registered with
three operating systems — would have made `check` exit 1 with a statement that is false. `explain`
has the identical arm and the identical sentence. Both now have a case: `check` reads the document
through `SmithDesignIo.LoadFromFile` and reports its refusal (no validation of its own — that verb's
standing rule), and `explain` joins the group whose only resolution is the workspace walk. `render`
needed nothing: its refusal already names the kind through `DocumentKinds.Name`, so adding the
`"smith"` row there is what turned *"nothing circuitRF reads"* into *"is smith, and render draws a
schematic, a symbol or a layout"*. `find` never enumerates documents by kind and was unaffected.

**3. `src/Design` cannot see `MarkerConfig`, `FreqUnit`, `MatrixFormat`, `MarkerStyle`,
`MarkerKind` or `DerivedParameters` — they are all in `src/Render`, which is ABOVE it.** The design
note asks for "the Data Display Marker shape, verbatim", and the reference graph
(`Core → Engine → Design → Render → Ui`) makes a direct use of that type impossible. Declaring the
four enums again here would be four copies to keep in step. What `SmithMarker` does instead is hold
each of them as the enum's own MEMBER NAME, in a string, with `MarkerConfig`'s own default — which
is exactly the token `JsonStringEnumConverter` writes for them in a `.cdd`. So "verbatim" is true of
the thing that matters to a format: the bytes a `.csmith` writes for a marker are the bytes a `.cdd`
writes for the same marker, and brief 8's mapping is one `Enum.Parse` per field rather than a
translation. `SmithOverlayRef.Derived` is a string for the same reason.

**4. The placement rule is enforced generally, which is a deliberate widening of R-smith1-3.** The
brief names one placement refusal — a shunt `S2P` — and its reason is that *a 2-port with its second
port grounded is a different component than the one the user placed*. That reason is exactly as true
of a `Tline` in shunt and of a `StubOpen` in series, and all three are documents `SmithCascade` will
have no formula for. Worse, those three kinds share one `SymbolKind` (`Tline`) and are told apart by
nothing BUT their placement, so a mis-placed one is not a document with a wrong field — it is a
document that means something else. `SmithElement.Refusal` therefore reads
`SmithComponentMap.AllowedPlacement`, one rule closing all three, and the S2P case is one instance
of it.

**5. `EngineReference(SymbolKind.ZPort)` is `"Z_Port"`, not `"ZPort"`.** §3.3's table spells the
engine column `ZPort`, and `ComponentTypeRegistry`'s own display name is `"Z"`; the string that
actually goes in a `.cnl` is `"Z_Port"`. `ToneSource` and `CurrentToneSource` differ the same way.
This is why `SmithComponentMap` names a `SymbolKind` and a port count and stops: every engine
reference, parameter name, default and unit is asked of `ComponentTypeRegistry` at the call site. A
transcribed table here would have been the place that was wrong, and brief 2's oracle netlist is the
first thing that would have found it — by failing for a reason that looks like arithmetic.

**Two smaller notes.** The firewall gate was not quite free: `UserFacingTextGateTests` catches the
five `throw new InvalidDataException("…")` literals in `SmithDesignIo` and `SmithGeneratorImport`,
and they are allow-listed rather than converted to `Diagnostic`s — the first two are
`RailDocumentIo`'s own two lines with `.crail` respelled, R-smith1-4 says to mirror that reader
exactly, and a `Diagnostic` cannot be the exception the callers already catch. And
`SmithElement.SliderRange` serialises as a sorted LIST rather than a keyed object:
`System.Text.Json` writes an enum dictionary key as a NUMBER unless told otherwise, and a document
saved twice with no edit between has to be the same bytes or revision control has something to show
every time.


## The |Z| map far below the first mode: a flat answer that looked like a broken one (2026-09-19)

Owner, running the shipped board at 10 MHz: *"I got a gradient from 2.405 kΩ to 2.405 kΩ … does
that answer make sense? From where to where?"*

**The number is right.** Measured: the +3V3 plane pair is **7.98 pF** of bare copper-to-copper
capacitance, and 1/ωC at 10 MHz is 1,996 Ω against the map's 2,405 Ω — the same thing, the
difference being the pair's own series terms. The first cavity mode is at **2.677 GHz**, so 10 MHz
is **1/268 of it**. That far under resonance a plane pair is a LUMPED CAPACITOR: one connected
piece is one equipotential, every reachable cell reads the same ohms, and there is no *where* for a
map to show. The map is correct and has nothing to say, which is a state it had no way to express.

Three faults sat behind that one screenshot, and none of them is the arithmetic.

**1. A flat field was painted as a full rainbow.** `RailMapScene.Normalise` stretches whatever span
it is handed across the whole cold-to-hot ramp, so a field varying by **0.003 %** was drawn as a
dramatic gradient made entirely of the fourth significant digit, under a plate printing the same
number at both of its ends. `PdnPlaneAnswer.SpatialSpread`/`MapIsFlat` measure it (threshold 1 %);
a flat map collapses its own ramp to one colour, its plate reads one value, and the answer carries
a note giving both numbers that make the claim checkable — how far below the first mode the
frequency is, and the 1/ωC the map is reporting in place of a field.

*Unreachable cells are excluded from that spread, and must stay excluded* — they read exactly zero,
and a ratio against zero would report the flattest possible field as the most structured one there
is.

**2. 454 of the board's 2,544 cells vanished silently.** This rail is **three galvanically separate
pieces**. A cavity cell on a piece the drive cannot reach has no edge to the driven piece, so its
MNA node carries only its own shunt capacitor and no injection: **V = 0 exactly, so |Z| = 0**. That
is −∞ on a log ramp, the renderer drops the tile, and the copper under it draws uncoloured —
indistinguishable from copper that is not on this rail at all. **Zero there means "not reachable",
not "a dead short"**, which is the opposite of what a reader takes from it. Counted as
`UnreachableCells`, named in a note, and the cursor readout over one now says so instead of
printing `0 Ω`.

**3. `PdnMask.Ohms` did not scale upward.** A PDN mask lives in milliohms, so the kΩ decade never
came up; the flat note printed `2404.777 Ω` beside a plate reading `2.405 kΩ` — one number, two
spellings, in one window. It now scales both ways.

`PdnPlaneAnswer` also carries its own `ModelKind` now, for the caption fault recorded in
`src/Ui/RESOLVED.md`: the |Z| plate was reading the DC run's.

## The |Z| map was unreachable on every real board: two cell-size rules, one mesh (2026-09-19)

Owner report, on the shipped Power Rail example: pressing **|Z|** in the Board area showed a
sentence they could not parse, and the map never appeared. Both halves were real and the second one
was total — **the |Z| map could not be produced on that document at all, at any frequency.**

**What collided.** `PdnMeshExtractor` sizes a cell by the SMALLER of two rules (R-rail14-2):
λ/20 at the extraction frequency, and R-rail3-14's FEATURE rule — enough cells across the narrowest
current-carrying conductor that a thin trace's RESISTANCE comes out right. `RailPlaneRun` inherited
the DC request wholesale, so the cavity got the DC answer to a DC question. On the Sensor board the
narrowest copper is a fraction of a millimetre, so the cell is **0.066 mm and the cavity is 61,129
cells** — against the dense mode solve's ceiling of 4,000. Refused, every time.

**Three things worth keeping from it.**

1. **A plane pair's modes do not depend on a trace hanging off it.** Paying 61,129 cells to resolve
   a 0.2 mm trace's resistance buys nothing the mode list or the map contains. The feature rule is
   correct and is the wrong rule here — which is why the fix is a cavity-specific mesh and not a
   loosened ceiling.

2. **The refusal named two knobs and one of them was inert.** `PdnModeSolver`'s sentence offers
   "state a coarser cell, or find the modes against a lower band top, since λ/20 at half the
   frequency is twice the cell." On this board λ/20 at 100 MHz in εr 4.3 is **72 mm — wider than
   the board** — so the wavelength rule never binds and halving the frequency changes the mesh by
   *nothing at all*. Measured directly: 100 MHz and 1 GHz both produced 230,786 conductor cells at
   0.066 mm. Advice that cannot work reads exactly like advice that can.

3. **No test caught it because every gate ran on a uniform rectangle.** `RailZMapTests`' fixture is
   a 30 × 20 mm plane pair whose narrowest copper IS the plane, so the two rules agree on it and
   the collision is unrepresentable. `TheShippedExampleProducesAnImpedanceMapWithNoCellSizeTyped`
   runs on the real document for that reason.

**The fix — `RailPlaneRun.Fit`.** With no cell size stated, the cavity sizes its own mesh: n cells
of side Δ cover about n·Δ², so the Δ that lands on a target count follows from the extraction
already in hand (no second reading of the geometry, which is that file's standing rule). It
coarsens until the mesh is at or under the ceiling, in at most four re-extractions, and reports
what it chose on the ANSWER's own notes — a map drawn at a cell size the rest of the window is not
at is one somebody will compare against the drop map square for square. On the Sensor board that is
**0.745 mm and 2,544 cells**, converged in two extractions.

**The target is not the ceiling, and that is the part most likely to be "simplified" later.** The
solve is cubic: `PdnModeOptions.MaxCells`' own measurements are 800 cells → 0.7 s, 1,575 → 4.4 s,
3,200 → **36 s**, 4,000 → about 70 s. An auto-fit aimed at the ceiling makes every board that needs
one wait a minute for a picture somebody is clicking to look around in, and buys resolution nobody
asked for — six modes of a plane pair do not need a 63 × 63 grid to sit on. `AutoFitCells` is 1,500;
the ceiling stays what it always was, a refusal threshold.

**A STATED cell size is never re-meshed.** It is the knob the convergence sweeps turn, and a run
that silently replaced it would make those sweeps measure nothing — so that case stays a refusal,
and the refusal now names a size that would have fitted.
`AStatedCellSizeIsRefusedRatherThanReMeshed` is the negative half, without which the fit would
reasonably be rewritten as "coarsen until it fits" with no exception for it.

`RailDcRun.RequestFor` gained an optional `mesh` override for this — one builder still, because two
would drift in the reference extent or the class overrides and the modes would be of a board the
drop map is not of. `PdnPlaneModes.CavityCellCount` answers "what will this cost" without building
any matrices, which is what makes the fit cheap enough to run before paying for the solve.

The message half of the report is in `src/Ui/RESOLVED.md`.

## railRF's exporters moved here so the WINDOW could have an Export button (2026-09-19)

Owner report: *Compare… and Export are disabled in the UI with a tooltip saying they are not wired
yet — why not?* The honest answer was that the CSV writer, the `DataSet` pack, the report-page
sections and R-rail10-5's provenance record were all **private to `src/Cli/Rail.cs`**, and `src/Ui`
may not reference `src/Cli`. Wiring the button meant either moving them below the firewall or
writing a second set.

`src/Design/RailRf/RailExport.cs` is the first of those, and the reasoning is the mirror image of
the rule `src/Cli/Authoring.cs` already states for the headless verbs: *an operation that lives only
in a view model is not a capability, and a verb that re-implements one diverges from it silently.*
A second CSV writer would agree with the first today and drift the first time either was touched,
and the drift would be invisible because both produce a plausible file — which is exactly the
failure R-rail10-8 ("no second export path") exists to forbid.

What moved: `RailProvenance` (the record, its `Lines`, `ModelText`, `ExtentText`, and a new `Of`
factory that also carries R-rail11-6's two headline COUNTS, so the window's status strip and the
verb's banner cannot disagree about one document), `RailExport.Csv`, `RailExport.Pack` and
`RailExport.Sections`. `Rail.cs` keeps only what is genuinely its own — argument parsing, path
resolution, the console report, `--json` and the writes — and each of its former methods is now a
one-line delegation, which is what makes the two surfaces comparable byte for byte.

What did NOT move: the PAGE. `RailReportPage` is in `CircuitRF.Render`, above this project, so the
sections' wording is here as `RailReportText` and the drawing is there — the same split
`RailComparisonReport.Sections` already makes, and for the reason `src/Render`'s own `.csproj`
gives: it draws, it does not decide what a finding says. Both callers map `RailReportText` onto
`RailReportSection`; two records of two fields is the price of the firewall and it is the right
price.

### The trap in `RailLengthFormat.ParsePoint`: the unit sits OUTSIDE the bracket

Added in the same pass, because a source or load anchor is now TYPED into the row that prints it
(see `src/Ui/RESOLVED.md`). `Point` prints the unit ONCE for the pair — `(26.5, 9.875) mm` — so a
parser that splits on the comma hands `9.875) mm` to `LayoutUnits.TryParse`, which refuses it, and
the round trip comes back null while the refdes form works perfectly. Written that way first and
caught by the round-trip assertion rather than by reading. The unit is now lifted off the closing
bracket and given to whichever half does not carry one of its own, so `26.5mm, 300um` is also legal
and the two halves need not agree.


## The EM run asked the kernel's verdict before it asked whether there were any ports (2026-09-18)

User report. An experienced designer opened an imported two-layer board, pressed Simulate with **no
port labels placed at all**, and was shown the full-wave kernel's via electrical-length paragraph —
a correct statement about a physics limit, and the wrong sentence for someone who has not drawn a
port yet. His own summary: *"the EM part did not say anything though I did not had placed a single
port on the board."*

`EmPortExtraction` has said exactly the right thing since it was written (*"This layout has no port
labels, so the full-wave planar kernel has nothing to drive. Use the Port tool to…"*). Nothing was
missing. **`EmRunService.RunPlanar` and `Preview` simply called `PlanarKernel.CanSolve` first**, and
only one refusal is ever shown, so the port sentence was unreachable whenever the kernel also had an
opinion. The EM panel's own `BlockingReason` ordered the two the same way and had the same hole.

Both now ask the ports first. The argument is that a port refusal is about something the user did or
did not DRAW, while the kernel's verdict is a limit on solving a structure there is as yet no reason
to solve; and by that line `PlanarExtractor` has already refused every no-metal case, so asking port
extraction — which is geometry-only and free — first costs nothing and loses no diagnosis.

**It also made `circuitrf check` more useful than the reorder was aimed at.** On the reported board
the preflight now reports all ten resolved ports (*"Port 5 ('P5') at (136519.92, 167949.88 µm) was
taken to be on the conductor's low-x (left) end…"*) beside the kernel refusal, where before the
refusal came first and the port resolutions were never computed. That is how the duplicate P5/P6 in
that file became visible at all — see `src/Engine/Mom/RESOLVED.md`, same date.

Gate: `tests/Ui.Tests/Em/EmRefusalOrderTests.cs`.

## railRF brief 18 — two defects nothing reported, and the third that was never one (2026-09-18)

`brief-railrf-18-six-defects.md`, R-rail18-1 and R-rail18-2. Both were found by writing brief 17's
chapter and authoring the `Power Rail` example rather than by a test, **and the suite was green on
both**: each produced a complete, plausible answer with nothing reporting a failure. The other four
are in `src/Render/RESOLVED.md` and `src/Ui/RESOLVED.md`. Brief 17's own entry here is superseded by
this one and has been removed rather than left as a second record of the same thing.

### 1. `R-rail18-1` — a pooled area measurement cannot use a summed total

`PdnMeshExtractor.MinimumFeatureWidthDbu(IReadOnlyList<PdnRegion>)` pooled every layer's copper into
one `Paths64` and handed it to the `Paths64` overload, which measures by morphological opening. That
overload's `LosesArea` compares the area of the opened UNION against `total`, **the SUM of the
per-path areas**. Wherever two pooled paths overlap the sum exceeds the union, the comparison is
unsatisfiable at every width, and the bisection bottoms out on its floor of 1 DBU.

**A rail on more than one layer crosses itself at every via**, so this was the ordinary case rather
than a corner:

| pooled set | measured |
|---|---|
| one 5 mm square | 4.92 mm |
| **the same square, listed twice** | **0 mm** |
| the example's TOP copper alone | 0.346 mm |
| the example's BOT copper alone | 0.199 mm |
| **TOP and BOT pooled** | **0 mm** |

`baseDeltaDbu` became `max(1, 0/3) = 1 DBU`, so `PdnGrid.Build`'s `MaxCells` coarsening chose the
accurate mesh instead of R-rail3-14's *"three cells across the narrowest copper"*, and the provenance
said so in words nobody reads as a fault: *"the rail's narrowest copper, 0 mm, at 3 cells across it"*,
`CellSizeMetres = 1e-9`.

**The fix is PER LAYER, not a bigger union.** The quantity wanted is "the narrowest copper this rail
has anywhere", and copper on two drawing layers is not one 2-D shape — unioning the pooled set would
make a trace crossing a plane read as plane-wide, which is the other wrong answer and just as
plausible. Each layer's own unioned copper is measured and the minimum taken across layers. Islands on
ONE layer stay pooled: they are galvanically separate pieces of the same sheet, and the narrowest of
three collinear segments is the narrowest copper on that layer.

**The `Paths64` overload now UNIONS defensively**, which is the decision R-rail18-1b asked to be
written down. Its two other callers (`PdnCopperClassifier`, `PdnGraphExtractor`) each pass one
region's already-unioned copper, so the union costs one Clipper call on a set that is already a union
— and it removes the precondition entirely rather than documenting it. What unioning does NOT make
safe is pooling copper from different DRAWING LAYERS into one call; that is a semantic error rather
than an arithmetic one, and the `PdnRegion`-list overload is the only route a rail's whole copper
should take. Both halves are said in the overload's own remarks.

**Measured consequence on the `Power Rail` example.** The accurate mesh is now set by the rule that is
stated: the accurate drop moves from 49.488 mV to **49.392 mV** (1.4 % above the fast reading rather
than 1.6 %), the reference return from 3.7 mΩ over 327,457 cells to **3.597 mΩ over 330,902**, and the
run **no longer raises the "mesh was coarsened … to stay under the 400,000-cell ceiling" note at all**.
`examples/Power Rail/README.md` was updated to the new numbers; the example itself was not re-tuned,
per the brief's own scope rule.

**One thing this exposed and did not change:** a via's BARREL disc is on a via drawing layer and is in
`regions.Power`, so it participates in the minimum. On a board whose drill is narrower than its
narrowest trace the cell size is therefore set by copper the mesh never resolves into a layer
(`ResolveConductors` maps drawing layers to stackup CONDUCTORS, and a via layer is not one).
`PdnGraphExtractor` already skips via drawing layers for the neighbouring reason — a barrel is a
BRIDGE rather than sheet copper. It is recorded rather than fixed: it makes the mesh finer and the
answer more conservative, and deciding it is a change to what the stated rule means.

Gate: `tests/Ui.Tests/RailRf/PdnMeshExtractorTests.cs` — the two-line oracle (one square measures its
own width; the same square listed twice measures the same width), the two-layer rail measuring its
narrower layer, and an end-to-end accurate extraction on a three-conductor board whose provenance
names a real width and whose `CellSizeMetres` is that width over `CellsAcrossMinimumFeature`. The
first two were confirmed RED at HEAD and green after, by restoring `PdnMeshExtractor.cs` from `HEAD`
and re-running them.

### 2. `R-rail18-2` — a check that exists to catch a wrong stackup told everyone theirs was wrong

`RailDcResult.PlaneCapacitanceLine` exists because §2.4 calls plane capacitance the number *"a
designer recognises a wrong one instantly and would never notice buried in a curve"*, and it is
printed even on a DC run — where nothing is stamped from it — precisely so the stackup gets checked.

`PdnMeshExtractor` fills `PlaneSeparationMetres`, `PlaneCapacitanceFarads` and
`PlaneOverlapSquareMetres`. **`PdnGraphExtractor` filled none of them.** So on the DEFAULT model, on
every board, the line read *"Plane capacitance: none. The stackup states no dielectric between this
rail's copper and its reference…"* — a statement about the user's stackup, untrue of any stackup that
states one.

**2a — the fast model computes the same three numbers.** It had everything it needed and no mesh to
get it from: `PdnGraphExtractor.PlaneCapacitance` intersects the rail's per-layer copper with the
reference's, one Clipper intersection per (rail layer, reference layer) pair, and takes `h` and the
medium from `PdnStackupGeometry` / `PdnCavity.MediumBetween` exactly as the mesh side does. **Neither
reading invents its own arithmetic** — `PdnCavity.CapacitanceFarads` is the one expression and both
call it. The same two notes the mesh raises (a missing dielectric, a class-default tan δ) are raised
here in the same words, because §2.9 rule 4 compares the two models on the user's own board and a
warning appearing on only one of them reads as a difference between the boards.

**The two agree to the GRID rather than to machine precision, and the gate says 5 %** rather than
discovering it: the mesh sums per-cell overlap on a discretised grid and reads a little under wherever
an edge does not fall on a cell boundary; the graph intersects the polygons exactly. On the
`Power Rail` example both now print **1.029 pF over 0.11 cm² at εr 4.3** — identical to the printed
precision.

*(Brief 17's own note recorded that figure as "1.03 pF over 1.12 cm²". The area was a unit slip in the
note: the value is 1.12e-5 m², which is 0.112 cm², and 1.12 cm² is not consistent with the same line's
capacitance at any plausible `h`. Nothing in the code changed it.)*

**2b — "not computed" and "not stated" are different sentences.** `PlaneCapacitanceLine` reached the
stackup sentence through `<= 0`, so a model that never computed the number blamed the user's stackup
for its own omission. `PdnProvenance.PlaneCapacitanceBasis` carries the distinction in
`PdnPlatingBasis`' style — **a defaulted number and an uncomputed one are not the same state** — with
three members and three sentences: `Computed` (and a zero then means the rail's copper genuinely does
not overlap its reference, which names the reference extent rather than the stackup),
`NoDielectricStated` (the original sentence, for the case it was written for), and `NotComputed`
(which names the MODEL and says nothing about the board).

Gate: `tests/Ui.Tests/RailRf/PdnFastExtractorTests.cs` — Fast reports a capacitance agreeing with the
mesh's inside 5 %; a stackup with no dielectric between rail and reference still gets the original
sentence from BOTH models; an uncomputed number names its model, and a source scan finds exactly one
spelling of the stackup sentence in `RailDcResult.cs`.

### 3. A through via joins only the two conductors its span NAMES

Not a defect — `DrcConnectivity` is explicit about it — but it is the shape of a real board that
`R-rail2-14`'s four allowances did not anticipate, and it decided the `Power Rail` example's routing.
For each `StackupKind.Via` entry the walk unions a barrel with *the first touching piece on
`SpanFromLayer`* and *the first on `SpanToLayer`*, and with nothing else:

- **An intervening plane is never shorted**, so a board needs no anti-pads for railRF to read it
  correctly. (The example draws them anyway, because a real board has them and they are part of the
  reference's own copper area.)
- **An inner layer the via physically lands on is never CONNECTED either.** A `TOP`→`BOT` through via
  cannot hand current to `IN3`. A board routed on an inner layer needs a via entry whose span names
  that layer — a blind or buried via — or the rail comes back as three regions and the load floats.

This is why the example's supply runs on `TOP` and `BOT` with the reference on L2, rather than on the
inner layer the design note's own worked example uses.

---

## railRF brief 5 — the DC solve, the breakdown, and the rail chain (2026-09-18)

`src/Design/RailRf/RailDcRun.cs` + `RailDcResult.cs`, `src/Engine/Pdn/PdnBreakdown.cs`, and one new
file in the numeric layer, `src/Engine/LinearDcEngine.cs`. Tests in
`tests/Ui.Tests/RailRf/PdnDcSolveTests.cs` (10, ~2 s).

Measured, on §2.8's own board — a battery, a 350 mΩ protection FET bridging the gap the copper leaves
at its pads, and 50 mm of 0.3 mm 0.5 oz inner copper beyond it, at 100 mA:

| Row, as the breakdown prints it | R | Drop | Share | Elements |
|---|---|---|---|---|
| `Q1 at 350 mΩ (the part library's on-resistance)` | 350.000 mΩ | 35.0000 mV | **66.19 %** | 1 |
| `166.3 squares of TOP copper` | 163.854 mΩ | 16.3855 mV | **30.99 %** | **14,053** |
| `11.4 squares of TOP copper` | 11.191 mΩ | 1.1191 mV | 2.12 % | 1,282 |
| `11 squares of BOT copper, the reference return` | 2.710 mΩ | 0.2710 mV | 0.51 % | **93,544** |
| `BT1.1's series resistance, 1 mΩ` | 1.000 mΩ | 0.1000 mV | 0.19 % | 1 |

That is the ACCURATE reading: **108,884 netlist elements, five rows**, and the rows sum to the
52.8755 mV total drop. The FAST reading of the same board is 276 elements and the same five rows —
`49.72 mm of 0.301 mm TOP copper, priced as one section (165 squares)` at 163.089 mΩ against the
mesh's 163.854 mΩ, and a total drop of 52.6813 mV against 52.8755 mV, **0.37 % apart** on the number
§2.9's fourth rule asks a user to trust.

Rev 3 of the design note exists for that second row and the number is not a rounding error: the
copper is **second, at nearly a third of the drop**, it is half the series semiconductor, and
§2.8's own arithmetic — 50 mm of 0.3 mm copper is 167 squares — comes back out of the mesh as 166.3.

### 1. `NonlinearDcEngine` is the wrong engine for a mesh, and the reason is storage rather than physics

R-rail5-1 says the solve is the existing machinery and nothing here is new. The existing DC engine
materialises its augmented system as a **dense `double[n,n]`** before assembling a CSC out of it —
which is what makes its thermal measurement and its per-unknown residual report cheap to write, and is
entirely reasonable for a circuit. brief 3's mesh ceiling is **400,000 cells**, which is 1.28 TB of
that array; even a modest 10,000-node board is 800 MB. Newton is the other half: a resistive mesh is
LINEAR, so the iteration, the source stepping and the convergence trace have nothing to do.

So `LinearDcEngine` is that engine's linear pass **with the dense array removed** — §4.4's own
sentence, "at ω = 0 the system is real, symmetric and positive-definite and the same code path solves
it far faster". It implements no matrix, no ordering and no factorisation: `MnaSystem` assembles, AMD
orders, CSparse's `SparseLU` factorises, exactly as they do for an S-parameter point, and it keeps
every convention that pass sets (Port and Term inert at DC, gmin on every voltage row, mutuals stamped
last, a branch current flowing from its element's FIRST node to its SECOND). It lives in `src/Engine`
proper and **not** in `src/Engine/Pdn/`, where the series' own scope rule forbids a matrix.

**gmin is load-bearing here, not a continuity aid.** A decoupling capacitor is an EXACT open at ω = 0
and R-rail3-4 says it stays in the netlist anyway, so a node reached only through one has no equation
of its own and `Factorize` reports a structurally zero row. gmin gives it one and it settles at zero
volts, which is the honest answer for copper with no DC path. At 1e-12 S it is 4e-7 S across a
400,000-cell mesh, against milliohms of copper.

### 2. The breakdown's three numbers come from DISSIPATION, which is what makes the aggregation exact

R-rail5-4 aggregates thousands of cell edges into one row, and the obvious arithmetic — sum the
resistances, take a current — is right for a series chain and **wrong by a factor of six for six
parallel via barrels**. What works for every shape is: a group's dissipation is `Σ Iₑ²Rₑ`, its THROUGH
CURRENT is half the sum over every node of the net current its own elements deliver there (which
cancels at an interior node and does not at a terminal), and then `drop = P/I`, `R = P/I²`.

- A series chain: `I` is the common current, `R` is `ΣRₑ`, drop is `I·ΣRₑ`.
- Six barrels between one pair of cells: `I` is their SUM, `R` their parallel combination, and the
  drop is the voltage actually across them.
- A meshed sheet with one way in and one way out: `R` is its spreading resistance.

`Σ drop` then equals the source-to-load drop **exactly** on a series path, which is what the gate
asserts, and the shares sum to one by construction on any board.

### 3. Contiguity is a union-find over MESH EDGES ONLY

"Contiguous cells belonging to one trace section **on one layer**" (R-rail5-4) falls straight out of
running the union-find over mesh-edge origins alone: a via is a different origin and a different row,
so it never merges two conductors into one row and the layer survives the aggregation. §2.6's worked
example turns on exactly that. A mesh group's own elements each describe ONE cell, so the row's label
is BUILT rather than borrowed — a row reading "0.1 mm of 0.1 mm copper" over five thousand cells would
be wrong by the whole aggregation — and it is printed in **squares**, which is the unit §2.8's
correction is counted in.

### 4. The chain's DC coupling is the headroom and NOTHING else

R-rail5-7 says the output rail's source "starts from" the upstream answer, and §2.7 says railRF "does
not model a regulator's forward transfer … and does not approximate it either". Those reconcile
exactly along brief 1's own two cases for `RailSource.OpenCircuitVoltageV`:

- **Stated** — a REGULATED output, used exactly as stated. The upstream answer never scales, offsets
  or caps it. An earlier draft capped it at the measured input (a regulator "cannot output more than
  it takes in"); that is a forward transfer, it is false for a boost converter, and it was dropped.
- **Not stated** — brief 1's "a branch whose impedance is known and whose level is not". On a chained
  rail that level IS known: it is the answer at this part's own input pin field upstream, carried
  through a series element whose resistance the row already states. That is a pass element, not a
  transfer.

The second case is what makes §7's second assertion possible at all: **narrow the input rail's copper
and the output rail's node voltages move.** A chain reading a nominal passes the first assertion (the
input voltage is 3.6308 V) and fails this one.

### 5. Superposition's exact form is three terms, not the brief's two

§7 and R-rail5-6 say "solve with source A alone and source B alone" and that the two sum to the
two-source solve. Taken literally that is not superposition: a source that is **removed** leaves an
OPEN where a **zeroed** one leaves its own series resistance in circuit, and the two are different
networks. And a load's current injection is a third source, so with one present it appears in BOTH
one-source solves and the sum double-counts it.

The gate is therefore written in the exact form — sources zeroed rather than removed, ports stating no
current so the decomposition is exactly two terms — and it holds to **better than 1e-9 V** node for
node, which is round-off in the back-substitution.

**The negative in the brief does not work as written, and the one below does.** "Stamp the second
source at the wrong node and assert the sum no longer matches" cannot fail: superposition is
insensitive to WHERE a source sits, so moving it in every solve at once still sums correctly. What
reproduces §9's defect — "a second source stamped at the wrong node produces a plausible number, not
an error" — is moving it in ONE of the three solves and asserting the sum breaks. It also has to be
read at the load's own node rather than over the whole field: moving a source's pad moves the
junctions the graph reading puts on the copper, so the two netlists no longer share every node name.

### 6. A brief-4 defect this brief had to fix: the fast model refused §2.8's own board

`PdnGraphExtractor.PourDominatedRefusal` decides a path is pour-dominated when the source cannot reach
the load through **trace edges** (plus vias, which it unions explicitly). It did not union **series
elements** — and §2.8's whole point is that "on imported artwork the copper stops at every pad, so the
board is not electrically continuous until the user has said what bridges each gap". So a battery, a
protection FET and a run of copper — the design note's own worked board — was refused as pour-dominated
with no pour anywhere in it, and so would be every real board carrying a ferrite or a load switch on
the rail. The refusal's own message even named the load it "could not reach".

Fixed by unioning each series part's two anchors exactly as the via loop unions a barrel's two ends,
with the same guard and the same justification the file already states: being permissive there can
only ever make a refusal LESS likely. Brief 4's own pour gate (`FastAgreesWithAccurateOnTracesAnd
RefusesOnAPour`, including the forced-to-`Trace` negative) still passes.

### 7. The interpolation is stated ONCE, on `RailDcResult.VoltageAt`

R-rail5-2: the window (brief 8) and the headless report may not differ about what the voltage is
between two cells, and they are written by different briefs. The rule is **inverse-distance-squared
over the four nearest cells on the same drawing layer and the same side of the rail**, with an exact
hit on a cell centre answering that cell's own value. Four rather than three because the mesh is a grid
and three points on a grid pick a direction; squared rather than linear because a linear weight leaves
visible creases along the cell rows. One rule has to serve both readings — the mesh's cells are a
regular grid and the graph's are junctions at irregular spacings — or the fast and accurate drop maps
would be drawn by different arithmetic.


## railRF brief 4 — the fast graph extractor, the classification, and the gate (2026-09-18)

`src/Design/Layout/Pdn/` — `PdnModelKind`, `PdnCopperClassifier`, `PdnGraphExtractor`, plus
`PdnAssembly`, which is brief 3's attachment stamping **lifted out of `PdnMeshExtractor` so both
readings share it**. Tests in `tests/Ui.Tests/RailRf/PdnFastExtractorTests.cs` (10, ~7 s).

Measured, fast against accurate on one board, loop resistance:

| Board | Fast | Accurate | Δ | Fast elements | Mesh elements |
|---|---|---|---|---|---|
| 50 mm × 0.3 mm straight run | 163.78 mΩ | — | **−0.06 % of the closed form** | 5 | — |
| A: stepped trace, three widths | 77.48 mΩ | 78.69 mΩ | −1.5 % | 5 | 41,793 |
| B: a T and two stubs | 50.82 mΩ | 51.16 mΩ | −0.7 % | 76 | 102,948 |
| C: 20 × 15 mm supply polygon | **refused** | 3.31 mΩ | — | — | 827 |
| C **forced** to `Trace` | 2.44 mΩ | 3.31 mΩ | **−26 %, optimistic** | 42 | 827 |
| linear taper, 0.2 → 0.8 mm | 22.51 mΩ | 22.94 mΩ | 0.24 % of the closed-form integral | 5 | 41,086 |

### 1. The mesh's assembly had to move before the fast model could exist at all

§4.6's "not a second simulator, a second reading of the geometry" is only true if the two readings
differ in **one** thing. Brief 3 put the vias, the ground choice, the series parts, the shunts, the
sources, the load ports, the ties, the island drop and the node numbering inside a private class of
`PdnMeshExtractor`; a fast extractor that re-implemented them would have drifted on the first change
to any of it, and the symptom would be two answers differing by something other than the copper —
precisely the comparison §2.9's fourth rule asks a user to trust.

So `PdnAssembly` is that class, moved verbatim, parameterised on a small `IPdnNodeSource` (node count,
`PdnCellRef` per node, point → node, nearest reference node, a deterministic order, a name). The mesh
keeps `StampMesh`; the graph stages sections and coarse pour edges; **`StageCopper` is the whole of
the difference.** The interface deliberately mentions no grid — one existed and one does not.

### 2. A grid cannot find a trace section. The medial axis can, and it is not ours

A trace two grid cells across is topologically a ladder, not a chain, and no contraction turns it
into one resistance; a grid coarse enough to make it a chain merges the trace beside it. So a piece
classified `Trace` is rasterised, thinned by **Zhang-Suen** (CACM 27(3), 1984, unchanged), and walked:
degree ≠ 2 is a junction, degree-2 chains are sections. Bends, 45°s, T-junctions and stubs all fall
out; nothing special-cases an orientation.

**The raster is not the model.** It finds the topology and measures the width; what leaves is one
element per section, which is what makes the netlist a few hundred elements. The tests assert the
element COUNT and never a wall clock — a timing threshold passes on a fast machine whatever the
algorithm does.

Three things had to be got right and each was wrong first:

- **Width is area per unit length, and the raster's boundary bias is removed in one multiply.** Every
  copper pixel is assigned to its nearest skeleton pixel by a breadth walk, so `W = A/Δs` needs no
  distance transform. A 0.3 mm trace is not an exact number of pixels wide, so the assigned areas are
  scaled once by (the polygon's true area)/(the raster's). That single correction is what gets the
  closed form to 0.06 % at six pixels across.
- **Eight-connected adjacency with the diagonal suppressed where both its legs exist.** Without it a
  staircase of three pixels is a triangle in which every pixel has degree two — a cycle, not a chain —
  and the section walk never terminates.
- **The two ends of a chain carry HALF a step of arc and a WHOLE step of copper.** `R = Σ Δs·d/(σTA)`
  is then exactly `ρL/(WT)` on a uniform section; the obvious `Σ Δs²/(σTA)` is half a pixel short at
  each end, which is invisible on a 667-pixel trace and 6 % on a ten-pixel one.

### 3. The section runs from the PAD, not from where thinning stopped — and that is most of the answer

A thinned skeleton stops about half a width short of the copper's end. On a ribbon that is a fraction
of a percent. **On a region forced to `Trace` it is nearly all of it**: the medial axis of a
20 × 15 mm rectangle is a 4 mm spine, and reading a section between its two ends alone prices 300 mm²
of copper as a **dead short** — 0.00 mΩ, reported as an answer, which is the one number this model
must never produce. Anchoring the chain's arc at the attachment coordinate, with the copper that
pixel owns, gives 0.91 mΩ against the mesh's 1.66 mΩ: optimistic, which is the point, and not zero.
It also moves the straight-trace span from 49.66 mm to 49.90 mm — the pads' own separation.

### 4. "Branch points closer together than the copper is wide are one junction" is measured on the
POSITIONS, not on the chain

Six vias in one 2 × 2 mm land pad must be **one node and six parallel barrels** (R-rail4-6). The
centreline joining them wanders the length of the pad, so a chain-length test keeps them apart —
first attempt gave six barrels across three node pairs. Measuring the straight-line distance between
the two ends' own positions against the section's width gives 0.6 mm on 1.6 mm copper → merged, and
23 mm on 14 mm copper (the forced pour) → not merged. One rule, both cases, and it is §2.9's sentence
read literally.

### 5. Two derived numbers, and what each is derived FROM

- **The trace threshold is ten squares** (`PdnCopperClassifier.TraceSquaresThreshold`). The closed
  form omits a constriction term of about `(1/π)·ln(2W/πd)` squares at each boundary — under half a
  square for a contact a quarter of the width, so about one square for a section with a boundary at
  each end. One in ten is 10 %, one in twenty is 5 %, which is §7's own gate.
- **The frequency ceiling is a tenth of the first cavity mode** (`ShuntBandTopHz`). At `f₁/10` the
  plane pair is `βa = 18°` long and treating a distributed shunt as absent costs `tan(βa)/βa − 1` =
  **3.4 %**, inside §7's 5 %; it grows without bound as `f → f₁`. For a 40 mm span in ε_r 4.3 that is
  **180.7 MHz**, which is the brief's own worked sentence to three figures.

### 6. The classifier measures area and perimeter, and a compact shape has NO ribbon reading

A ribbon of width `W` and centreline length `L` — straight, bent, L-shaped — has `A = W·L` and
`P ≈ 2(L+W)`, and those invert in closed form: `L` and `W` are the roots of `x² − (P/2)x + A = 0`.
One area and one perimeter, both exact from Clipper, give the equivalent width and length with no
medial axis, no orientation and no assumption of axis alignment. `L/W` is then the **number of
squares**, which is the unit §2.8's whole table is in.

A shape too compact to be a ribbon makes the discriminant negative — there is no real solution, which
is the cleanest possible statement that it is not a trace, and not a threshold being crossed.

### 7. Three rules whose absence would have been silent

- **Via drawing layers are not classified.** A via's barrel disc reaches the rail through the
  connectivity walk. Reading it as sheet copper put a compact "spreading" region in the class tab for
  every via on the board — a decision nobody has to make, hiding the ones that matter.
- **Two raster-pitch rules, finer wins.** Width/6 is what a real trace needs; `L_eq/32` is what a
  forced region needs. Width alone rasters a 20 × 15 mm pour eight pixels across and it thins to a
  single junction.
- **Two spur-prune bounds, smaller wins.** `1.5 × W_eq` identifies the corner artifact thinning leaves
  on a rectangle; `L_eq/8` stops that rule eating a whole region — on a forced pour, `1.5 × 15 mm` is
  longer than the region itself and three rounds of pruning leave nothing.

### 8. The pour refusal is on the RAIL's copper only, and that is deliberate

`PdnGraphExtractor` refuses when a source reaches a load only through copper classified `Spreading`
(R-rail4-5: "refuse rather than differ"). **The reference return is excluded from that test.** It is a
plane on every real board and therefore always spreading, so a rule that included it would refuse
every board and §2.9's "meshed, and coarsely" would mean nothing. What the coarse reference costs is
stated as a provenance note and measured by rule 4, on the user's own design — which is what rule 4 is
for. The refusal names Accuracy **and** the class-tab override, because both are answers to it.

### 9. Smaller things

- `PdnProvenance.ModelKind` is `required`. A result that can be constructed without a model kind is a
  result that can reach a user without one, and §2.9's rule 1 is then a convention rather than a type.
- `PdnResultsByModel` files a result under the kind its own provenance names, never one the caller
  supplies — a result filed under the wrong kind is the mislabelling rule 1 exists against.
- `PdnOriginKind.TraceSection` is distinct from `MeshEdge`: a ranked breakdown that could not tell a
  closed-form section from a meshed cell could not tell a reader which rows carry the fast model's own
  assumption.
- `PdnClassification` carries the copper it is about, so brief 8 draws the decision the extraction
  used rather than re-deriving one that might differ.
- The overrides live on `RailDocument` keyed by `PdnRegionRef` (drawing layer + the lowest-then-
  leftmost vertex of the outer ring). Copper that MOVED gets a different identity, which is correct:
  an override says "the current through *this* polygon follows a path", and a re-laid-out polygon is
  not the one the user looked at.
- `PdnResultsByModel.Add`'s guard is in `tests/Firewall.Tests/user-facing-text-allowlist.txt`. It is
  an internal invariant — a refusal is reported as a refusal by whoever asked for the extraction and
  never reaches that method.


## railRF brief 3 — the netlist contract, and the accurate DC extractor (2026-09-18)

`src/Design/Layout/Pdn/` — `PdnNetlist`, `PdnRailRegions`, `PdnViaModel`, `PdnAttachments`,
`PdnMeshExtractor`. Tests in `tests/Ui.Tests/RailRf/PdnMeshExtractorTests.cs` (15, ~0.4 s). Copper in,
`ElaboratedNetlist` out, at ω = 0; no solve, no `EmProblem`, no result type, held by a
comment-stripped source scan.

Measured on the brief's own headline row — 50 mm of 0.3 mm inner trace on 0.5 oz copper over a
reference: **7,620 cells, 14,212 elements, 66 ms**, and the closed form reproduced to under 1 %.

### 1. `R = 2·Rs` is the LOOP's, and stamping it per cell edge gives 4·Rs

The brief is emphatic about the factor of two — rev 2 of the design note used one plane's sheet
resistance and every derived crossover frequency came out at half its real value — and asks for it to
be carried in the code's own comment. **It is carried, but not where a first reading puts it.**

§4.3 requires SEPARATE power and reference nodes: "each capacitor … connecting the power node to the
reference node", "each load port across the power and reference nodes of its own pin-field cells".
That is not the collapsed plane-pair formulation §4.1's `R = 2·Rs` is written for, where one 2-D node
carries the inter-plane voltage and a loop traverses one edge. With both conductors meshed, a loop
crosses one square of the rail on the way out and one square of the reference on the way back — so a
per-edge 2·Rs would make the loop **4·Rs**, which is the same error rev 2 made, doubled instead of
halved.

So each edge carries **one square of its own conductor** and the 2 arrives by construction. The
simplification that loses it is not "drop the 2" — it is **"mesh only the rail and tie the reference
to ground"**, which halves every loop resistance and reports nothing. `ThePlanePairLoopIsTwiceOnePlane`
is what holds that shut: an identical conductor above and below, out along one and back along the
other, asserted at exactly 2.0.

This also reconciles the brief's two gates, which look inconsistent and are not: §7's closed form
`R = L/(σWT)` is one conductor, §2.8's crossover arithmetic says "using §4.1's **loop** resistance".

### 2. Cell conductances are built from copper AREA, not from cell counts — and that is what makes R-rail3-14 safe

A binary present/absent cell makes a conductor's width a multiple of Δ, so a 0.3 mm trace meshed at
0.1 mm is right and the same trace at 0.13 mm is 33 % wrong, silently, in whichever direction the
rounding fell. Instead each cell carries the area of copper actually inside it and an edge is the
series pair of the two half-cells it joins:

```
half-cell along x  =  dx² / (2·σ·T·A)          R(edge) = half(i) + half(i+1)
```

the ordinary finite-volume harmonic form. On a straight trace of **any** width and **any** alignment
the areas in a column sum to the real width whether or not the grid lines fall on its edges, so this
is exact — and it is exact on a stepped trace too, half a cell of each width in series.

**That is what lets the DC mesh be coarse without being optimistic**, which is the whole of
R-rail3-14. The shipped default is three cells across the narrowest copper, and the 50 mm gate passes
at that. It also means the `MaxCells` coarsening is honest rather than a degradation: a coarser cell
still carries the right copper, and what it loses is the separation between two conductors that share
a cell.

Two limits follow and are stated rather than hidden: two pieces of the same region passing through one
cell are given ONE node, and an edge is gated only on both cells being present — so a diagonal
staircase run is slightly optimistic. Both shrink with Δ.

### 3. A pad coordinate seeds the REFERENCE as well as the rail

The rail's copper is found by seeding `DrcConnectivity`'s partition with the source and load anchors.
An anchor is a POINT, and on a board with a return plane the plane is under all of them — so the
unfiltered seed put the reference's own net into the rail's net set and **the reference came back as
an island OF the rail.** A shorted board, reported as an ordinary one, on the most common board shape
there is. `PdnRailRegions.NetsAt` now excludes the reference layer from rail seeding.

Found by the island test returning 2 regions on a board with one rail region and one plane — which is
why that test asserts the COUNT rather than only that a report was produced.

### 4. A via's barrel disc arrives as "a layer the rail reaches with no conductor entry"

`DrcRegions.Expand` decomposes a `ViaShape` into a barrel on its own via drawing layer and a pad on
its landing layer, and `DrcConnectivity` then unions the barrel with the metal — which is exactly what
makes an offset staircase connect. The consequence is that the rail's layer set contains a **via
drawing layer**, which deliberately carries no thickness and no conductivity.

The refusal that protects against a conductor with no sheet resistance (a mesh of zero-ohm links — the
optimistic answer nothing reports) therefore had to skip via drawing layers explicitly, or it refused
every board with a via in it. Both halves matter: without the refusal an unmapped copper layer meshes
as a perfect plane; without the exemption nothing with a via extracts at all.

### 5. `PortModel` is safe at DC only because the DC engine skips it

§4.3 puts a port and a current injection on the same nodes, and one netlist serves both the DC
operating point and brief 12's sweep. A `PortModel` stamps a **0 V voltage source**, which across power
and reference is a dead short — so a DC solve that stamped it would report a perfect rail.

`NonlinearDcEngine` already skips `PortModel`/`TermModel` (`src/Engine/NonlinearDcEngine.cs:311`), and
that pre-existing line is the whole reason the arrangement works. Anything that changes it silently
turns every railRF DC answer into zero drop. Recorded here because it is a dependency this brief
acquired without touching the file it depends on.

### 6. The document has no series-part list, so the extraction takes one

`RailSpec` carries `Sources` and `Loads` and nothing else that attaches. But §2.8's "the copper stops
at every pad, so the board is not electrically continuous until the user has said what bridges each
gap" makes the bridging part a first-class input, and §4.3 makes it an ELEMENT rather than an
annotation ("at DC these are the largest terms after the source" — 350 mΩ for a protection FET against
165 mΩ for 50 mm of thin inner copper).

`PdnSeriesElement` is therefore an extraction input rather than a document field. **A document field is
brief 7's or brief 10's to add** — whichever first needs a user to type one — and when it arrives it
should map onto this record rather than introducing a second spelling.

### 7. An island with no DC path is REPORTED and not stamped, and that is a solvability decision

R-rail3-4 is explicit that two islands joined by nothing at DC and by a capacitor at AC is not an
error. It is also, at DC, a netlist with floating nodes — and "the deliverable is a netlist" is worth
nothing if the netlist has no answer.

So the island structure is always an output (`PdnRailRegionSet`, the provenance's `IslandReport`, a
diagnostic naming the count), and by default an island with no DC path to the reference point
contributes no elements. `PdnMeshSettings.IncludeIsolatedRegions` carries them anyway.
**This is a judgement, not a requirement of the brief** — if brief 5 would rather see the singular
system and refuse on it itself, flipping the default is the whole change.

### 8. The minimum feature width is measured, by morphological opening

R-rail3-14 sets the DC cell size from the narrowest conductor on the rail, and nothing stated what
that number is. It is bisected: a shape survives erosion by `w/2` followed by dilation by `w/2` only
where it was at least `w` wide, so the largest `w` that loses no area is the minimum width. A handful
of Clipper offsets rather than a scan.

It lands within a few percent — 0.295 mm for a 0.3 mm trace, the bisection's own resolution — and that
costs nothing at all, because finding 2 means the AREAS carry the width and the cell size only has to
resolve SHAPE.

### 9. The reference point is the reference conductor under the first source, and it is stated

Nothing in the design note names the node everything is measured from. It is the reference cells under
the first source's own pads, tied together exactly as §4.3 ties a load's pin field — a drop is a drop
relative to where the supply returns. It falls back to the first load where there is no source, and
`PdnProvenance.ReferencePoint` says which in words, because an unwritten convention here makes every
number in the report ambiguous.

Where a pad has no reference copper under it at all — an antipad, a split — the nearest reference cell
is used, the distance is reported, and the diagnostic says the spreading between the two is NOT in the
answer.

### 10. A zero-ohm part is a near-short, not a short

`ASeriesPartIsAnElementOnThePath` asserts the brief's "the same board without it differs by **exactly**
350 mΩ". It differs by 0.349 999 997, and the residue is not the mesh: `ResistorModel` substitutes
`Gmax = 1e12` for `R = 0`, so the "without it" board keeps 1 pΩ. The test states the tolerance and why,
rather than rounding the claim away.

## railRF brief 2 — the three companion readers (2026-09-18)

`PlacementFile`, `BomFile` and `RefdesCell` in `Layout/Interchange/` over a shared `DelimitedTable`;
`PartLibrary` + `PartLibraryIo` (`.crlib`) in `RailRf/`. Tests in
`tests/Ui.Tests/RailRf/RailReaderTests.cs`. All of it is `BoardNetlistFile`'s rule verbatim — evidence
about the artwork, never geometry — and nothing here creates, moves or deletes a shape.

### 1. Adding a `GerberFileKind` is TWO edits, and the second one is the silent half

`Placement` and `Bom` joined the enum so a companion table in a Gerber folder is skipped **by name**
rather than as "no Gerber or drill content in its head". `GerberImport` selects its `skipped` list by
KIND (`Other or Archive`), so a new kind left out of that predicate does not become a new category —
**it vanishes from the folder report entirely**, which is the exact silent drop R-L4g-2 exists
against. The same predicate is repeated in `FindSiblingDrillCandidates`' exclusion and both were
changed together. Check both when adding a third.

### 2. The delimiter inference is a CONSISTENCY test, and a frequency count is wrong

A tab-separated bill of materials whose description column reads `MLCC, 10n0, 50V` carries more commas
than tabs. A most-frequent-character inference picks the comma and shreds every row. The test is
therefore: parse the head with each candidate, take the one whose rows **agree on a field count**,
with more fields breaking a tie — and a single-column reading (which is what a wrong delimiter looks
like, and is perfectly self-consistent) scores zero rather than winning.

### 3. A `value` column is NOT a bill-of-materials signal, and that decision is load-bearing

R-rail2-12 refuses a header matching both signatures, naming `--placement / --bom`. The first draft of
the bill-of-materials signature accepted `refdes + value`, which **made every ordinary placement export
ambiguous** — placement files routinely carry a value column. The distinguishing columns are the part
number, the quantity and the description; with those, the refusal is reachable only on a header that
genuinely carries a reference, a part number AND coordinates. One more refusal is cheap; refusing every
import is not.

### 4. The three origins are only three DIFFERENT readings if the footprint offsets differ

R-rail2-3 wants one file read at each origin to produce three landed counts. It cannot be done without
footprint geometry, and the near-misses are instructive: on a footprint whose symbol origin **is** the
body centre, two of the three readings are identical, and on one whose symbol origin is pin 1 a
different two are. So `FootprintGeometry` (pad offsets with pin 1 first, a body-centre offset, and a
has-bottom-side-artwork flag) is a deliberate **seam for brief 3**, not a footprint model: brief 3
builds them from artwork, `PlacementFile.CheckLanding` only consumes one. A row whose footprint is not
supplied counts as `Unknown` and **never as landed** — a missing footprint scoring as a success is how
a wrong origin wins the comparison it was supposed to lose.

The test fixture is worth keeping: four 0402s written at pin 1, plus two *neighbouring* pads 0.2 mm
along, so reading at the symbol origin lands two of the four **on the neighbour's pad**. That is the
brief's own failure story reproduced as a number (4 / 2 / 0 landed across the three origins).

### 5. The origin refusal comes LAST, and `ParsedRowCount` survives it

`Refusal` non-null means nothing was read and nothing may be used (`BoardNetlist`'s contract, kept).
But the import dialog that asks the origin question wants to say how many parts it is asking about, so
the rows are parsed first, the diagnostics are filled, and only then does the refusal replace the row
list. `ParsedRowCount` is the one number that crosses it.

### 6. Imperial case codes ONLY, deliberately — the metric set would be a confident wrong answer

`0402` imperial and `0402` metric are different parts (the metric one is `01005` imperial), and a
description carrying four bare digits does not say which system it is in. Matching one system leaves
the other's codes **null**, which is what R-rail2-5 requires; matching both produces a confident wrong
answer on exactly the small parts where the difference matters. Same discipline elsewhere in the parse:
a voltage needs its unit letter (a bare number in free text is a quantity, a length or part of a code),
a tolerance needs its percent sign, and the single-letter EIA tolerance codes (J/K/M) are **not** read —
a K in a description is as likely to be a kilo- prefix.

### 7. `RailAggressorOrigin.Bom` already existed, so no second spelling was added

Brief 1 shipped the enum with `Typed` and `Bom`. The brief 2 text names the value `RecognisedFromBom`;
adding that would have been a second spelling of one state. The existing value is used.

The pre-fill requires a **frequency** as well as a kind: a converter recognised as a converter but
stating no frequency contributes **no row** and is counted in the diagnostics instead. The harmonic
count is 1 — the fundamental alone, the fewest a row can mean — because nothing in a bill of materials
carries one and this is not the place to invent it.

### 8. The `Bom` / byte order mark collision is handled once, at the bottom

R-rail2-14's naming hazard is real in this exact code path. The byte order mark is stripped in
`DelimitedTables.Parse` and **nowhere in `BomFile`**, so `BomFile` never contains a line that could be
read as "the bill of materials strips a bill of materials". Both files spell U+FEFF out in full every
time.

### 9. The firewall's user-facing-text gate caught three, and the REFUSALS are not among them

`UserFacingTextGateTests` fails on a new English sentence THROWN below the UI firewall.
`PartLibraryIo`'s two format-version/deserialize sentences and `PlacementFile.CheckLanding`'s
called-without-an-origin invariant were allowlisted alongside `RailDocumentIo`'s identical pair. The
readers' own refusal sentences — which are the substance of this brief — do not trip it because they
are **returned as data on the result record**, not thrown, which is `BoardNetlistFile`'s pattern and
another reason to keep it.

### 10. `.crlib` stores no derived inductance, and sorts the bias curve on read

`L = 1/((2·π·f₀)²·C)` follows from two fields already in the file, so a stored third copy is a field
that would be edited on one side only. `stated_inductance_henries` exists **only** to be compared:
`PartLibrary.InductanceDisagreements()` reports a row past `InductanceTolerance` (5 %) and the derived
value is used regardless. The bias curve is sorted by bias on the way in, because brief 11 interpolates
it and an interpolation over an unsorted curve is a plausible wrong number rather than an error.

### 11. What the committed tests do NOT prove, and it is written in the test file's own header

The fixtures are synthetic: **they prove the readers parse their own output, not that they parse a real
export.** The reference package arrives at manual testing (railrf.md §8.5), so the file carries a second
`FixtureFact`-guarded tier keyed on `testdata/railrf-reference/` that skips with a stated reason until
the bytes land — verified skipping, not failing, on this clone. R-rail2-14's four shape allowances each
have a synthetic case now, so they are green or red on the day the package arrives.


## A port on metal drawn twice on ONE level was refused, in a sentence that could not be true (2026-09-17)

Owner report: a `.clay` holding the same taper drawn twice, one exactly over the other, was refused
with `Port 1 … sits on metal on 2 of this EM setup's 1 conductor levels`. The question that came with
it is the right one — that artwork would be perfectly acceptable if manufactured, so why should the
EM setup object to it?

**It counted POLYGONS and reported LEVELS.** `EmPortExtraction.NearestPolygon` incremented one
counter for every polygon containing the port's point, across every level, and the caller spent it as
"how many of this setup's conductor levels the label sits on" — hence "2 of … 1", which is what an
arithmetic mismatch between a numerator and a denominator drawn from two different quantities looks
like when it reaches a user.

**The guard itself is right and stays.** A label sitting on metal on two LEVELS is genuinely
ambiguous: driving the wrong one drives a different conductor with the same footprint and returns a
complete, plausible answer for a structure nobody drew. But two coincident polygons on ONE level are
not that. The level is the same either way, and the mesher rasterises a level's polygons into cell
OCCUPANCY — measured directly rather than assumed: one 20 x 2.9 mm line meshes to 297 cells and 552
bases, and the same line drawn twice meshes to 297 cells and 552 bases. Metal over metal on one level
is a drawing habit, not a structure.

So the count is of distinct levels, and the message now names the levels the port actually sits on
rather than listing every level in the setup — the same change on the RETURN terminal's copy of it.
Gate: two facts in `tests/Ui.Tests/Em/EmPortExtractionTests.cs`, one per half — drawn twice on one
level resolves, and metal on two levels under one port is still refused and names both.

**The duplicate metal in that report came from `Update Layout from Schematic`**, which is the other
half of the same owner report and is written up in `src/Ui/RESOLVED.md`.


## A plate drawn with no mask over it lost its film SILENTLY when the shipped tie moved (2026-09-16)

Found reviewing `brief-em-mim-11-nitride-mask-and-patterned-film.md`, and it was reproducing in this
repository's own fixtures: `EmMultiLevelSetupTests.WithNothingLeftOut_TheWarningBlockIsEmpty` draws
Metal1 + MIM Metal + Metal2, ticks all three, and asserted that a complete structure warns about
nothing. It passed — while the capacitor in it was being solved with **air** between its plates.

### What happened

MIM-11 re-pointed the shipped MMIC technology's `MIM Dielectric` from `PresentWithLayer: "MIM Metal"`
(a conductor) to `"Nitride"` (the mask), which is the true statement of where the film is. The mask
branch of `PatternedDielectric.Deactivate` then reasoned that *"with no mask artwork there is no
capacitor in the layout at all"*, so a film switched off by a missing mask was reported as an
ordinary NOTE — one among thirty — rather than as the warning the conductor tie's own branch already
raises for the same outcome.

**That premise is true only of artwork drawn by a generator that draws the mask.** A plate reaches a
layout without one in three ordinary ways, and all three are real:

- artwork from a generator that PREDATES the mask — every capacitor `KIT_MIMCAP` drew before MIM-11;
- an import whose nitride layer was not mapped;
- a capacitor drawn by hand.

In each of them the plate metal is in the layout, the plate level is usually in the run, and the film
is air: εᵣ 6.8 → 1, so the part reads its capacitance about **6.8× low**, and the paired MIM-6 rule
also puts Metal1's sheet back on the BOTTOM of its band. Nothing said so above note severity.

### The fix

The mask branch asks the question the conductor branch already asks — *does the plate carry
artwork?* — and warns when it does. The plate is the conductor directly ABOVE the film, which is what
`StackupLayer.PresentWithLayer`'s own documentation tells an author to name, so `Above` is the mirror
of the `Beneath` this file already had. The call site needed nothing: `PlanarExtractor` was already
passing `drawnOn.Contains`, which is every conductor level the LAYOUT draws on (not the run's level
list). `CrossSectionExtractor` passes null there and still gets the note, correctly — that kernel
refuses multi-level geometry, so a plate and a line cannot both be in one of its runs.

Gate: `PatternedDielectricTests.APlateDrawnWithNoMaskOverIt_IsAWarningRatherThanANote`, which asserts
both halves — the warning with the plate drawn, and the plain note without it, because with no plate
there really is no capacitor and the old sentence is right.


## The `.s2p` now says which drawn label became which port number (2026-09-16)

`brief-em-mim-10-lc-resonator-without-the-capacitor.md`; the full write-up and every measurement are
`src/Engine/Mom/RESOLVED.md` §MIM-10 and `HISTORY.md` §MIM-10. What lives here is the change:

```
! circuitRF-EM port 1: '1' on 'Metal1' at (-110, -125 um), edge, de-embedded, 50 Ohm
! circuitRF-EM port 2: '2' on 'Metal1' at (-155, -46 um), edge, de-embedded, 50 Ohm
! circuitRF-EM: port N of this file is the label named on the 'port N' line above. …
```

**It is PCAL2's finding and MIM-9's finding, one case further on.** The run already says all of this
in its notes and the notes do not survive onto the file — and MIM-10's whole recipe is that an EM
result is composed in a circuit, so it is read by a netlist rather than by the person who ran it.
Swapping two ports of a reciprocal part gives a curve that is smooth, passive and wrong, and there is
no symptom. The reference impedance rides the same line because Touchstone states ONE `R` for the
whole file, so a run whose ports differ has already lost that in the format.

Three things it does deliberately:

- **The label names come from `EmPortExtractionResult.SourceLabels`, index-aligned, never
  re-derived.** That field's own note says why: a label whose text names no number is auto-numbered
  in document order, and a second copy of that ordering is free to drift from the first.
- **`PortMap` takes level NAMES, not the `PlanarProblem`**, which is all it reads — so its gate
  drives the function with two ports and two labels in 7 ms rather than through a full-wave sweep.
- **ASCII, asserted.** `EmSnpProvenance` has no transliterator and Touchstone is written in an
  encoding that turns µ and Ω into `?`. Writing the test found a pre-existing one in the same file:
  every `.sNp` this application has ever written opened with `Generated by circuitRF ? EM:`. Fixed.

Additive and omitted when absent, so kernel A's files gain no byte. Gate:
`tests/Ui.Tests/Em/MimResonatorCompositionTests.ThePortMapNamesEachLabel_…`.

## A port on a PLACED INSTANCE gets the pin's width only on EXACT coincidence (2026-09-15)

Owner, on the PDK PCells example: placing a port on each end of a `KIT_SPIRAL` gave one port the
right width and the other the wrong one, and the component's own pin looked like it was in the
middle of the metal rather than on its edge. The two are one defect, and the pin is the cause.

### What the lookup can and cannot say about an instance

`LayoutConductorLookup.LookupFor`'s instance branch returns `ConductorInfo(bbox, PinAt(...))` with a
**null Shape** — a placed instance owns no top-level shape, so there is nothing to measure. So there
are exactly two answers available:

* the pin's own `WidthDbu`/`OutwardDeg`, when `PinAt` finds a pin, and `PinAt`'s default tolerance is
  **zero — exact coincidence**, deliberately (a port merely NEAR a pin has not named it); or
* `WidthAcross(bbox, direction)` — the instance's **array-expanded bounding box**.

There is no third. On a straight run of metal the box is a fair approximation and the gap never
shows; on a coil it is the whole 250 µm envelope against a 10 µm turn.

### The pin was half a turn width inside the metal

`_spiral_cell` built pin 1 from `points[-1]`, the last point of the **centre line**, while
`_segment_runs` cuts a free end square `half` a width PAST that point. Pin 2, on the landing pad's
drawn edge, sat exactly on the metal. Measured on the shipped cell: pin 1 at (−110, −120) µm with
the metal's face at y = −125.

That 5 µm is the whole bug, because of where a user aims. Geometry snap ranks `Pin` above every
other feature but only among candidates **within the pixel tolerance**, and the user aims at the
metal's visible end, not at a pin 5 µm behind it. Measured with `LayoutSnapQuery.FindCandidates` at
(−110, −125):

| tolerance | first candidate |
|---|---|
| 0.5 µm | `Midpoint@(−110,−125)` — the end face's own midpoint |
| 2 µm   | `Midpoint@(−110,−125)` |
| 6 µm   | `Pin@(−110,−120)` |

At the other terminal the pin IS the edge midpoint, so every tolerance answers `Pin`. Landing on the
midpoint, `PinAt` finds nothing and `LayoutPortDirection.Resolve` reports **250 µm** — 25× the
metal. One terminal right, one terminal wrong, from one pin half a line width out of place.

### And the interior label changed the port's TYPE, not only its width

Driven all the way through `PlanarExtractor` + `EmPortExtraction`, a port at the old pin 1 came out
`Kind = Internal` with `Side = MinX` — a shunt-to-ground port on the wrong axis, not an edge port.
`LayoutPortDirection.IsInterior` is right to say so: the label was in the conductor's interior. **A
pin that is not on the metal's edge is not a terminal**, and the misclassification is silent — an
internal port produces a complete, plausible answer for a different structure.

### The other half: a terminal covered by two conductor levels cannot be driven at all

The same cell's landing pad was exactly the via's footprint with the Metal2 crossover ending on top
of it, so pin 2 carried metal on BOTH levels. `EmPortExtraction` refuses that by name — *"a port's
LEVEL is part of its identity: driving the wrong one drives a different conductor with the same
footprint"* — and the refusal is correct. What was wrong was the artwork: **a cell that hands out a
pin has to hand out single-level metal to stand on.** `KIT_MIMCAP` had the identical arrangement at
its own strap terminal and was fixed with it.

Both fixes are in `examples/PDK PCells/pcell-kit/kit.py` as RULE 10 (the pad runs a lead past the
via) and RULE 11 (a pin sits on the metal's own edge, not on the end of a centre line). `KIT_MLIN`
had always followed RULE 11; the spirals are a walk rather than a rectangle and it was not restated.
Gated by `PdkPCellExampleTests.APortOnEitherTerminalDrivesTheCoilsOwnLevel` (both spirals, both
metals, through the real extractor) and `.APortOnThePlacedCoilTakesItsWidthFromThePin_NotTheInstanceBox`.

### What was NOT changed, and is worth knowing

`PinAt`'s zero tolerance is right and the bbox fallback is honest. **Widening either would hide this
class of defect rather than fix it** — a kit whose pins are off the metal would then report a
plausible width measured from somewhere the pin is not. The lookup could instead resolve the
instance's own sub-cell SHAPE at the point and measure it, which would make the fallback as good as
a top-level polygon's; it was not done here because the artwork was genuinely wrong and fixing the
artwork is what makes the pin authoritative again.

## A relative file reference means the WORKSPACE ROOT, and a run verb thought otherwise (2026-09-15)

Owner, on the shipped S-Parameters example after copying it out of Tools ▸ Examples:

```
Running 'Amplifier.csch'  'SP1': SnP 'S1': Touchstone file not found:
'…/S-Parameters/potentially_unstable_amp.s2p'.
```

Reported as the file not having been copied. **It was copied** — it was in the repository, in the
build output, and in the installed workspace, beside the schematic that names it. What was wrong was
the STRING.

### One design, two answers

`SnpPathPolicy` states the rule and gives its reasons: a stored `File` is relative to the
**workspace root**, on all three sides — `ToStored` writes it that way when you browse to a file,
`Elaborator.ResolveSnpFilePath` resolves it that way at Run, and `MoveRefRegistry` repairs it that
way when the workspace moves. The example stored `potentially_unstable_amp.s2p`, which is the
SCHEMATIC-relative spelling, and `Amplifier.csch` sits two directories down.

Every gate that existed passed. The file was committed, the packaging item group shipped it,
`WorkspaceCopy` copied it, the schematic extracted cleanly, and every part reached the netlist.
Nothing asked the one question that mattered — **does the string resolve** — so
`EveryFileAnExampleNamesResolvesAgainstItsWorkspaceRoot` now does, for every `File` parameter of
every shipped example, against the workspace root and nothing else.

### Why it ran perfectly headlessly, which is the part worth keeping

`circuitrf sparam Amplifier.csch` produced 161 correct points. The base directory is not the
Elaborator's here: `CnlReader` resolves an SnP `File` against the `.cnl`'s own folder, and
`CircuitSource.FromSchematic` was handing it **the schematic's** folder. Simulate writes
`netlist.cnl` at the workspace ROOT and hands the same root to the Elaborator, so the two agreed
only for a schematic sitting at the workspace root — the usual layout, and why nothing reported it.

That divergence is worse than either answer being wrong, because it is what makes a green CLI run
stop meaning anything about the window; `docs/design/cli.md`'s own rule is that a design which runs
headless runs when opened. `CircuitSource.ReferenceBaseOf` is now the one answer: the nearest
ancestor workspace root, falling back to the document's own folder when it belongs to no workspace —
which is the same fallback `SnpPathPolicy.Resolve` takes, and the only base a loose `.csch` can
reasonably have.

## A kit that shipped only artwork had no schematic side at all (2026-09-15)

Owner, on `examples/PDK PCells`: with `SpiralInductor.clay` open, **Update Schematic from Layout
creates a schematic and puts nothing in it**; `KIT_SPIRAL` needs a better-looking glyph; and neither
it nor `KIT_MLIN` has any parameters on the schematic.

The three are one gap. A complete kit part is four artefacts (`Reference ▸ PDK Authoring`
§four-halves) and this kit ships the last one only — the Python generators. Nothing else in the
workspace declared a schematic part for those cells, so `PdkKitRegistry` held none.

### What each symptom actually was

`LayoutToSchematicGenerator.Run` walks the layout's instances, works out which part each generator
belongs to (`KitLayoutGenerators.PartRefFor`, which DID answer — the palette publishes a reference
for a layout-only generator too), and then asks `PdkKitRegistry.Find` for the part. It got null, and
took the "kit is not loaded" branch:

```
"KIT_SPIRAL" was left alone — the kit "pcell-kit" is not loaded in this
workspace, so there is no part to create.
```

**The kit was loaded.** What it had no part for was the cell, and the sentence names the wrong thing
— which is why the report read as a kit problem and the schematic came out empty.

The other two follow from the same absence. A palette tile for a layout-only generator carries
`Kind: Generic, PortCount: 0` and **no `CellDir`**, so `SchematicCanvas.OnPaletteDrop` takes its
`CommitPlacement` branch rather than `CommitCellPlacementAsync`: a bare generic box, no pins, no
parameters. There was never a worse glyph to improve — there was no symbol at all.

### The fix: the kit ships the symbol, and the file's NAME is the declaration

`PCellKitSchematicParts` builds a `PdkKitPart` around a `<generator-id>.csym` found beside
`pcell-generators.json` (or in a `symbols/` folder beside it). Three choices in it are worth the
words:

* **Nothing lists the symbols**, for the reason `PCellGeneratorManifest` lists no generators: a
  second list is a cache that can silently disagree with the thing it describes. A `.csym` matching
  no generator is a file nothing reads; a generator with no `.csym` is a layout-only cell, which is
  the state every such kit was in before.
* **circuitRF draws nothing on the kit's behalf.** Generating a box with the right pin count was the
  alternative — `AutoSymbolGenerator` already does exactly that for a cell folder — and a spiral
  inductor drawn as a rectangle reads as a part nobody recognises. The kit author knows what the
  part is, and `.csym` is circuitRF's own format, so they draw it in circuitRF's symbol editor.
* **Pins from the symbol, parameters from the GENERATOR.** The symbol decides the port count and
  where wires attach; the published interface is `resolver.DeclaredParameters(id)`, the same list the
  generator reads. So a symbol cannot disagree with its cell about a parameter name — the defect
  whose only symptom is wrong artwork.

### Two traps found while wiring it

**A length default is in SI metres and a schematic field is not.** Written verbatim, a 10 µm width
becomes `1E-05` in a box with no unit beside it. It is written in the fixed `mm` baseline instead —
the one `MicrostripSubstrateInjection.ApplyTechnologyLengthUnit` rewrites to the placing technology's
own display unit — so the same declaration reaches an MMIC schematic as `10 µm` and a PCB one as
`0.394 mil`. That rewrite had no production caller for cell placement, so
`CommitCellPlacementAsync` now makes it, **gated on the reference being a kit ref**: a cell FOLDER's
parameters are what its author typed, and rewriting those on placement would change a design's own
spelling.

**`SetKit` replaces everything held for one kit name, and the two halves of a kit are replaced on
different occasions.** A kit re-import rebuilds its schematic parts while its interpreters keep
running; a kit's scripts are re-read while its imported parts sit untouched. Either would have wiped
the other. `KitScope.Generated` records which entries circuitRF synthesised, `SetKit` no longer
removes those, and `SetPCellParts` never overwrites an entry the kit itself supplied — the import is
the kit's own statement about that part, and the synthesised one exists only for a cell the kit says
nothing about. All readers (`Find`, `PartsOf`, `FindRefByPartId`) keep asking one dictionary and
getting the whole kit.

A symbol buys the schematic side and not a model. These two cells still have no device equations, and
a run says so rather than inventing one — which is why the synthesised `.ccell` deliberately does
**not** set `ExternalProvider`: claiming the part can be evaluated would turn "this cell has no
electrical model" into a device the worker resolver goes looking for and reports as missing, a
different sentence about a different thing.

Gate: `tests/Ui.Tests/Examples/PdkPCellExampleTests.cs` §5 — the owner's exact gesture, run against a
clone of the shipped example with the generated-cell cache deleted.

## A port's TYPE moved from the `.cem` to the `.clay`, and silence stopped meaning "edge port" (2026-09-14)

Owner bug report: place a port in the middle of a 50 Ω trace with geometry snapping OFF. The ghost
draws an internal port under the cursor; the click lands; **the marker jumps to the far end of the
conductor and reads as an edge port.** Reported as a snapping bug: the port was said to snap to some
other geometry on the click.

**Nothing was snapping.** Driven headlessly against the reporting file, with geometry snap off and the
cursor on the saved anchor, the ghost and the committed label agree to the DBU:

```
ghost  = (254000, 812800)  PortHint Interior = True
placed = (254000, 812800)  PortHint Interior = True
```

What moved was the PICTURE, one frame later, and the `.cem` moved it:

1. The ghost passed `statedKind: null`, so the renderer inferred from the artwork — `Interior = True`
   → `Internal` → the ring at the cursor. That is the good picture.
2. The click committed. `LayoutChangeKind.Added` cleared `InternalPortMarks`, so for a frame it still
   drew Internal.
3. The open `.cem` re-extracted (posted at Background priority) and published one mark **per port**,
   the kind from `EmSetup.ResolvePortKind(slot)`. The file carried no `PortKinds` array at all, and
   that method's documented default for an unstated slot was `PlanarPortKind.Edge`.
4. `MarkKindOf` returned Edge, so `DrawPortMarker` drew the bar at `hint.PlaneX` — the conductor's
   **end face**, measured at x = −1,648,887 DBU, **1.9 mm from where the user clicked**.

**This was the same defect `LayoutRenderer.MarkKindOf` already records from 2026-09-09** — *"'says
nothing' used to mean 'edge port', and that was the bug"* — one layer up. That fix taught the LAYOUT
that silence means "infer"; the `.cem` went on manufacturing an `Edge` assertion for a slot nobody had
ever set. **The run agreed with the wrong half**: the Port tool stamps a `PortDirection`, so
`EmPortExtraction` took the `stated` branch and would have driven a mid-trace port from the trace's
MinX end — no refusal, a complete and plausible wrong answer.

### The type is `LabelShape.PortKind` now

Owner decision, after the diagnosis. The arguments, for the record:

- **It was already three-quarters there.** `IsPort`, `PortDirection`, `PortLayer`, `PortReference`
  and `PortReturn` all live on the label. The type was the odd one out.
- **The split was incoherent.** `PortReference` is on the shape, but `EmPortExtraction` refuses it
  unless the *`.cem`* calls that port `InternalDeltaGap` — one file's field whose legality was decided
  by another file's.
- **RP-2b's own argument applies more strongly to the type than to the return**: a port cut across
  the middle of a trace is not the same port as one driven from its end; it is a different port.
- The reference IMPEDANCE stays in the `.cem` (`PortZ0s`) and that is not an inconsistency: 50 Ω
  versus 75 Ω really is one port measured twice.

**What was given up, deliberately:** two `.cem` files analysing one `.clay` can no longer disagree
about a port. `InternalPortMarksOwner`, `WorkspaceViewModel.AdoptPortTypes` and its takeover message
existed only for that and are gone.

### The engine's enum, not a layout one

Unlike `LayoutPortReference`, whose note explains that the engine's `PlanarPortReference` carries a
fourth member naming a refusal rather than anything anyone can draw. `PlanarPortKind`'s three members
are exactly the three things a user can draw, so a parallel layout enum would have been a mapping
table and two places to add the fourth kind.

### One resolution, `LayoutPortDirection.KindOf`

`label.PortKind ?? InferredKind(Resolve(conductorAt, label))`. The renderer, the hit test, the Port
tool's ghost, the Properties Inspector, the port context menu, the `.cem` panel's port list and
`EmPortExtraction` all call it. **`EmPortExtraction.Extract`'s `kindFor` parameter is gone and must
not come back** — a caller supplying a type is a second source by definition, and
`InternalDeltaGapPortUiTests.NoExtractorCallerHandsItAPortTypeOfItsOwn` scans all three call sites
(`EmRunService`, `EmSetupEditorViewModel`, `Cli/Explain`) for one.

### `EmSetup.DeclaresInternalPort()` had to become `EmPortExtraction.AnyNonEdgePort(shapes)`

It read a list of STATED types. A port that is an internal port because of **where it is drawn**, with
nothing stated, was invisible to it — so `Auto` would have routed exactly the port that guard exists
for to the kernel that cannot carry it. It asks the artwork now, needs no problem and no mesh, and is
asked in `Refresh()` rather than in the planar half: the whole point is that it fires when the
CROSS-SECTION kernel was chosen, and the planar refresh does not run then. That is a real trap — the
first version set the flag inside the planar extraction and `AGapPortOnTheCrossSectionKernel_
BlocksTheRunByName` caught it immediately.

### The migration is ONE door, not two readers

`EmPortKindMigration`. Reading the label and falling back to the `.cem` would leave the value with two
homes indefinitely and keep the drawing disagreeing with the run for exactly the files this change
fixes — the renderer knows nothing about a `.cem`, so a legacy stated type would be driven and not
drawn.

- `EmRunService` and `circuitrf explain` call `ApplyInMemory` and **write nothing**, so a build machine
  analysing an un-migrated pair gets the application's answer without modifying the tree it was handed.
- `WorkspaceViewModel.MigrateLegacyPortKinds` does it as a real, undoable layout edit, reports it, and
  clears `EmSetup.PortKinds`. Both documents end up dirty and the user saves when they choose;
  silently rewriting two of somebody's files on open is not a thing to do.
- **A label that states its own type is never overwritten**, so migrating twice is a no-op and a type
  changed in the layout after the setup was written is not reverted by it.

`EmSetup.PortKinds` survives as a read-only legacy field, documented as a migration SOURCE and nothing
else. `ResolvePortKind` is deleted rather than kept — keeping it is how the Edge default would come
back.

### What an interior port infers as

`Internal` (to ground), not `InternalDeltaGap`. That is what the DRAWING has said since 2026-09-09, so
making the run agree changes nothing on screen; a user who means a delta gap says so, now in three
places. **Existing tests encoded the old default and had to change**: a port at the middle of a
20 mm line used to read `Edge` because the `.cem` said so, and now reads `Internal` because the metal
does. Five assertions in `PortTypeIdentityTests` and `InternalDeltaGapPortUiTests` were updated, not
suppressed.

### `PortTypeIdentityTests`' whole defect class is now unrepresentable

That file exists because `PortKinds` was read by POSITION, so deleting P1 slid P2 into slot 0 and P2
was extracted, meshed and solved as an internal port. The slot became `portNumber - 1` in September;
a field on the label has no slot to slide along at all. The tests stay, asserting the property rather
than the mechanism. `PortZ0s` still uses the slot rule and is still tested for it.

---

## `EmSetup.Deembed` is GONE, and a legacy `.cem` that carries it warns (2026-09-14)

Owner bug report: an EM run of a 3.8 mm microstrip reported about -80 dB S21. The `.cem` carried
`"Deembed": false`, and the raw delta-gap solve reads an EDGE port as an OPEN CIRCUIT rather than as
a degraded answer — the full measurement is `src/Engine/Mom/RESOLVED.md` §RAW1.

What matters on this side of the firewall:

- **`EmSetup.Deembed` and the panel checkbox are removed.** The switch was added (PCAL2/R-pcal2-3)
  only so the mesh-ceiling refusal's own recommended remedy could be followed from the GUI; that
  recommendation is deleted, so the switch has no purpose. It never had a second case either —
  de-embedding applies to edge ports only, so on internal delta gaps it already did nothing.
- **`CemFile.Deembed` is still DESERIALISED, and only that.** A legacy document opens rather than
  being refused — refusing would leave a user with a file they cannot open in order to fix it — and
  sets `EmSetup.LegacyRawSolveRequested`, which `EmRunService` turns into a warning before running
  de-embedded. `ToFile` never sets it, so the field leaves the document on the next save.
- **This is a deliberate single exception to the `.cem` byte-identical round-trip rule.** Every other
  nullable flag in `EmSetupPersistence` exists to preserve that property. This one does not, because
  the field no longer has a meaning to preserve, and keeping it would make the warning permanent.
- `LegacyRawSolveRequested` rides in `Clone()` for the reason every other field does: the editor's
  undo snapshots go through it, and an unrelated edit must not be what silences the warning.

Gate: `tests/Ui.Tests/Em/PortClearanceRefusalTests.cs` — the legacy file warns and runs de-embedded,
the field is dropped on save, `"Deembed": true` carries no warning, and reflection asserts that no
`Deembed` property survives on `EmSetup` or on `EmSetupEditorViewModel` (a property that came back
would compile and silently restore the switch).


## `DrawLaneFraction`'s cosmetic-only status is now written down OUTSIDE this file (2026-09-13)

Brief 8's closeout. The STK5 note below is the finding; what it lacked was a statement anyone reading
the design in the ordinary way would meet. Two now exist:

- **`docs/design/mom-engine.md` §10.4b** — "`StackupLayer.DrawLaneFraction` is COSMETIC, and must stay
  cosmetic", beside the model fields it sits among, including the additive-nullable persistence rule
  and the fact that a test rather than a paragraph is what holds it.
- **`docs/user/src/reference/stackup.md`, the Vias section** — "Where a via sits on the cross-section
  means nothing", for the user who has just dragged one sideways and wants to know whether they have
  changed their board. They have not: a via entry is a KIND of connection between two named
  conductors, and where the holes are is in the layout.

The rule is unchanged and so is the gate (R-stk5-9: a technology differing only in this field extracts
to an identical `EmProblem`). Nothing in `src/Design` changed for brief 8.

---


## STK5 — `StackupLayer.DrawLaneFraction`: a drawing position that must never become geometry (2026-09-13)

brief-stackup-render-5-drag.md R-stk5-7. One additive nullable `double?` on `StackupLayer`, in the
via block: where the barrel is DRAWN across the width of a stackup cross-section, 0 at the band
column's left edge and 1 at its right. Null means "wherever the drawing puts it" — the automatic lane
spread, and what every technology written before the field means.

### Why a fraction and not a coordinate

The drawn band's width is the PANE's, and changes when the pane is resized. A stored pixel offset
would drift every time the user dragged the splitter, and two vias aligned at one width would
separate at another. The lateral drag's snap grid is a fixed count of divisions for the same reason:
a grid whose spacing came from the pane would store a different number for the same gesture after a
resize.

### It is a drawing position and nothing else, and that is a TEST

A stackup is a cross-section of a laterally infinite sandwich. A via ENTRY is a KIND of connection
between two named conductors — not one hole at one place — and every via drawn on its drawing layer
is an instance of it. So this field has no physical meaning at all, and it is one careless read away
from acquiring one; the failure mode if it ever does is an answer that changes when someone tidies up
a picture.

`StackupDragTests.ATechnologyDifferingOnlyInTheLaneFraction_ExtractsIdentically` takes every shipped
technology, sets every via's lane to a different value, and extracts both through
`PlanarExtractor.Extract`. The comparison is `EmSnpProvenance.GeometryHash` **plus an
element-for-element walk** of the levels, the vias and the medium stack — not record equality, for
the reason the ground-override gate already records here: `PlanarProblem` is a record over ARRAYS and
its own `Equals` is reference equality on the level list, so it would pass on two entirely different
problems.

### No `FormatVersion` bump, and the no-churn claim is asserted rather than assumed

`TechPersistence` serialises `Stackup` directly with `DefaultIgnoreCondition = WhenWritingNull`, so a
null field writes nothing and no existing `.ctech` changes by one byte — the
`SheetAt`/`PresentWithLayer`/`Fill` precedent. `TechPersistenceTests` pins that against **committed
fixture text** written before the field existed, not against a re-serialisation of the same object: a
re-serialisation agrees with whatever the writer currently does, including writing a field it should
not. It also asserts the string does not appear in any shipped technology's own output.

### Clamped on read, in `TechPersistence` and not in the property

A hand-edited file carrying 7 (or −0.5) would put a barrel off the page, where it cannot be seen,
hovered or dragged back. `FromFileModel` clamps to [0, 1] — and refuses nothing, because the field is
a picture and a picture is never worth failing a load over. It is clamped there rather than in the
setter so `StackupLayer` stays a plain data class and so what is clamped is exactly what came off
disk; a value the application itself wrote is already in range (`TechEditorViewModel.SetViaDrawLane`
clamps on the way in). `NaN` reads back as null rather than as a clamp of a value that has no order.

### The honouring needed no plumbing, because brief 1 had already left the seam

`StackupScene.LaneCentre` already took an explicit per-via lane from `StackupSceneOptions`. It now
falls back to the entry's own field when the options name none — options first, because THAT is a
drag in flight and has to win over what the technology still says until the release commits it. One
consequence worth having: every reader of a technology draws the barrel where the user put it with no
code of its own — the Stackup tab, the clipboard export (brief 7) and the documentation figures
(brief 8) alike.

## GVIA-1 — a via that CROSSES the return plane is classified from the plane's artwork, per via (2026-09-12)

User-reported. A board with an **inner ground plane** analysed from one outer conductor lost **every
via it had**. `PlanarExtractor.BuildVias` resolved a via's two terminals from the stackup entry's
`SpanFromLayer`/`SpanToLayer` alone — correct, and documented as "the artwork says WHERE a via is;
the stackup says WHICH TWO CONDUCTORS it joins" — so a plated through-hole declared top-copper to
bottom-copper named a conductor that is neither an analysis level nor the return plane, fell into the
`wrongGround` counter, and was dropped with a note. On the reported board that was **327 of 327**:
top copper solved over a plane it was not stitched to, which is a complete, plausible, wrong answer
for a structure nobody drew.

**The technology is not wrong and editing it cannot fix this.** Every barrel on that board really
does run top to bottom; whether a given one is a ground stitch or a signal via is decided by the
PLANE's own artwork — pour copper at that point, or a void. Both are ordinary and both are on every
such board. So the span stays a per-ENTRY fact and the TERMINATION became a per-SHAPE one, in exactly
the case that used to be a flat refusal: when the entry's span crosses this run's return plane (one
named conductor is an analysis level, the other is below the plane, and the plane lies between them),
each drawn footprint is tested against the plane. On plane copper → a `PlanarVia.GroundTerminal`
attachment basis. In a void → dropped and counted, as before.

`Terminals` became tri-state (`ViaSpan.Rejected` / `Fixed` / `CrossesPlane`) rather than growing a
second resolution path, so the per-entry answers keep their single spelling. Gate:
`tests/Ui.Tests/Em/GroundViaThroughPlaneTests.cs` (9), whose first two tests are that a span which
resolved BEFORE this is bit-for-bit what it was — the new branch is reachable only from the refusal.

### FINDING 1 — "copper on the plane layer" is the wrong test, and it gets every via wrong

The obvious implementation is containment against the ground layer's artwork. **Measured on the
reported board it grounds all 327 vias**, including the 61 that must not be.

A plane layer is a pour with a VOID per through-hole it clears, and inside most of those voids is a
small ISLAND — the signal via's own annular pad, which is copper drawn on that layer and is not the
plane. The reported board carries 62 polygons on its plane layer: one 269-vertex pour with 61 voids,
and 61 isolated 0.113 mm² pads, one inside each void. Metal ≠ plane; the question is connectivity.

**The rule is enclosure, not area.** A polygon whose outline lies inside a void of another polygon on
that layer is separated from it by construction — that is what a void is. Everything else is the
plane. The tempting alternative, "the largest pour is the plane", is rejected: a board with two
genuine ground pours has two planes, and ranking by area would silently drop the stitching on one of
them. Deeper nesting stays an island, which is the conservative direction — an unclassified via is
dropped and reported, which is the old behaviour.

With that rule the reported board splits **266 stitched / 61 passed through**, and the split was
reproduced independently in a throwaway Python pass over the raw `.clay` before any C# was written.

### FINDING 2 — `PlanarPolygon.Contains` is the wrong shape for this, for a cost reason only

A plane pour carries a void per through-hole, so `Contains` walks every hole ring of a ~15,000-vertex
polygon for every via on the board: extraction went **18 ms → 200 ms**. `PlaneMetal` keeps a bounding
box per RING and skips each void by its own box, which is **34 ms**. The ring test itself is
`PlanarPolygon.RingContains`, made **public** for this — a second even–odd test written in
`src/Design` would be a copy that drifts on exactly the on-the-scan-line boundary case that
function's own comment is about, so the one implementation is shared across the firewall instead.

### GVIA-2 — the via that bypasses the plane and JOINS the two outer conductors

Owner's follow-up: what happens to a via that really does connect top copper to bottom copper
without touching the plane, and should the count be reported? It **is** ignored — it is part of the
"land in a void" count — but **"landed in a void" and "joins the two outer conductors" are different
sets**, and only the second one damages the answer. Measured on the reported board: **61 bypass the
plane and 60 of them have copper on BOTH outer conductors.** (The owner's own estimate was 0, so the
number was worth measuring rather than reasoning about.) The remaining one has copper on the bottom
only, and one more of the 61 is a drilled hole with nothing on the far side.

Those 60 are signal vias. The structure this run solves CONTINUES through them into metal the run
does not contain, the s-parameters stop there, and **nothing else in the result says a path ended
early** — which is why this is a `WARNING:` note of its own rather than a clause in the paragraph
about via counts. It is also not a setting anyone can turn on: one laterally infinite plane is what
the Green's function terminates on, so the two sides of an unbroken plane are two problems.

**Three sentences, and the brevity is a requirement rather than a style preference** (owner): a
designer does not read a paragraph, so a long warning warns nobody. It carries the count, the two
conductors, the plane, that they are NOT modelled, and what to do — and nothing else, because every
clause added costs the sentence that matters. The reasoning lives here and in the code comments,
which is where reasoning belongs. Gated three ways in `GroundViaThroughPlaneTests`, since a note
with no length gate grows back: an absolute cap, a sentence count, and a RELATIVE one — it must stay
under half the companion note's length, which is the rule stated in a form that survives whatever
layer names a given board has.

**Both ends are tested against the ARTWORK, never inferred from the span.** The stackup says where a
barrel LANDS; whether there is copper there is a question about the drawing, and warning on the span
alone would make the number mostly drilled holes. The far conductor's shapes are flattened ON
DEMAND — `conductorShapes` already holds every shape on every bound conductor layer including the
bands the level loop skips, so nothing new is read, it is the same
`RegionsToMesh`→`Flatten`→`ToPoints` chain a level takes, and an ordinary run never enters it.
Extraction on the reported board: 34 ms → 38 ms.

### Both notes are short, and one of them stopped firing twice

Same instruction applied to the companion (owner). It went **1,050 → 423 characters**, and what
survives is the two counts, what decided them, and *that the technology needs no change* — the last
because "my `.ctech` must be wrong" is the conclusion a user otherwise reaches from a note about a
via span. What was cut was either reference material (what an attachment basis IS) or already in the
WARNING beside it. **The island clause stays and earns its length**: "the via IS on my gnd layer, why
is it not grounded" is the one question this note exists to pre-empt.

**The BACKSIDE note no longer fires for a crossed via.** `toGround` counts only the vias whose ENTRY
names the plane; a via that got there by crossing is counted by `stitched`. Before this, a board of
this shape got "266 vias go to the plane" twice in a row in different words — 370 characters of pure
duplicate, and exactly the noise that makes a run's notes unread.

Net on the reported board: **1,420 characters of via notes became 728**, and one of the two is a
warning that now stands out instead of being the third paragraph.

### What it does NOT change

* The published plane SIZE (R-fg-2) still measures every shape on the return plane, islands included.
  That number is the outline this run reads, and what it should measure is a separate question from
  which vias reach ground. On the reported board the islands are 3.6 mm² against 2,050.
* Nothing in the `.cem` or the `.ctech` — there is no new setting, and none is wanted: the case this
  fires in produced no vias at all, so an opt-out would only restore a model with no stitching in it.
* The flipped-stack path needs nothing of its own. `stack`, `levels` and `groundBand` are all in the
  same frame by the time `BuildVias` runs, so analysing the conductor on the OTHER side of the plane
  classifies the identical vias identically — gated.

### What it does not fix

The reported board still refuses: 393,718 unknowns against this kernel's 5,000 ceiling, which is the
whole-board size and was true before this change (the 10,709 new via unknowns are 2.7% of it). The
remedy is the refusal's own — analyse a smaller region.


## RF3 — a painted pour arrives as a region, not as 29,000 scanlines (2026-09-12)

`docs/sonnet-briefs/brief-rasterfill-3-coalesce-raster-fill-on-import.md`. `LayoutRasterFillCoalesce`
(new, in `src/Design/Layout`) turns a raster-filled layer back into the `PolygonShape`s it paints;
`GerberImport` step 7b and `PcbImport.Coalesce` call it, `circuitrf convert --no-coalesce` and the
Settings ▸ General ▸ Import checkbox turn it off. **On by default**, which is the owner's own ask and
the brief's R-rf3-7: the coalesced document is the one that can be simulated, and the import had
already been telling people in words to go and do the merge by hand. Gates:
`tests/Ui.Tests/RasterFillCoalesceTests.cs` (18) plus one in `ConvertCliVerbTests.cs`.

### What it buys, measured

Two measurements, both on a **synthetic stand-in built to the overview's §1a counts** — the real
boards are not in the repo and nothing here names one. Scratch console harness in Release, not a
`Category=Benchmark` test.

One pour layer, 22,740 one-mil scanlines tiled over a 49 × 52 mm board:

| | strokes | shapes out | outline vertices | region vertices | union |
|---|---|---|---|---|---|
| a raster-filled layer | 22,740 | **1** | 636,720 | **45,506** | 129 ms |

And the whole board (44,571 shapes as authored, 3,153 coalesced), real `LayoutRenderer.Draw` against a
CPU `SKSurface` with a persistent `LayoutPathCache` sized as `LayoutCanvas` sizes it:

| | repaint | pan | one zoom step in (2×) |
|---|---|---|---|
| as authored, 1600×1000 | 47.9 ms | 37.6 ms | 24.8 ms |
| **coalesced**, 1600×1000 | **4.9 ms** | **5.5 ms** | 19.5 ms |
| as authored, 3200×2000 | 52.0 ms | 50.4 ms | 63.3 ms |
| **coalesced**, 3200×2000 | **11.0 ms** | **12.8 ms** | 35.4 ms |

**The absolute figures are NOT the overview's** — this harness does not reproduce §1b's 288 ms
baseline on this machine, and no claim is made that it should. What it measures is one board class
before and after, in one process, and that comparison is what §5.4 asks for: **pan falls 6.8× at
1× and 3.9× at 2× DPI**, and the 2×-DPI column — the one that matched the original report — crosses
from roughly 20 fps to roughly 78 fps.

**What this says about brief 4 (the tiled raster cache).** The 77% is gone at the source, and the
remaining cost is no longer rasterization of painted area. What is left is the ZOOM column: 35.4 ms for one
2× step at 2× DPI against 12.8 ms to pan, on a document that is now 3,153 shapes. That is a
cache-rebuild cost, which is brief **2**'s subject (`widenDbu` is an unbucketed cache key), not
brief 4's. Brief 4 is still the only thing that helps a board **already imported** — but on a board
imported after this change its threshold is a long way off, and it should be re-judged after brief 2
rather than before.

### Clipper2, not `SKPath.Simplify`, and it is one pass either way

R-rf3-1 is right that the union must be one pass — pairwise booleans over 29,000 shapes are quadratic
and will not finish — and wrong only about which primitive. `LayoutClipper` is already this repo's
single boolean seam: its arithmetic is exact on DBU integers with no scaling step, its `PolyTree64`
output already carries the hole nesting R-rf3-2 asks for, and `ToClipperPaths` already builds a
`PathShape`'s outline through `InflatePaths` **with the cap style R-rf3 warns against discarding**.
`Simplify` would have meant a second geometry pipeline, in float, beside it.

### The brief's own counting rule cannot fire, and the fix is to compare like for like

R-rf3-4 says to compare "the candidate group's stored vertex count against the union's" and replace
when the union is materially smaller. **Measured on the brief's own gate-1 fixture — 1,750 abutting
scanlines painting a 40 × 40 mm rectangle — that rule says KEEP:**

| stored centreline vertices | stroked-outline vertices | union vertices |
|---|---|---|
| 3,500 | 49,000 | 35,008 |

A scanline stores **two** vertices and stroke-to-fills to about **twenty-eight**, so the literal
comparison pits a centreline against an outline and the tier can never pass on anything. The union is
in fact 1.4× smaller than the geometry it replaces — 14× smaller on the board-scale fixture above —
but only when both sides are counted as what every consumer actually derives: the outline. That is
what `LayoutRasterFillCoalesce` compares.

### The rule that decides, and the fixture that first got it wrong

Two counted conditions, both required:

1. **`group.Count >= 50 * regions.Count`** — how many strokes one region absorbed. It is a RATIO, not
   the stroke-count threshold R-rf3-4 forbids: a 300-stroke pour collapsing to one region scores 300
   and is treated exactly like a 30,000-stroke one.
2. **`regionVertices <= strokeOutlineVertices`** — the union is not more complex than what it replaces.

Measured scores, three orders of magnitude apart:

| fixture | strokes | regions | score | decision |
|---|---|---|---|---|
| painted rectangle, 0–50 % overlap | 1,575–3,150 | 1 | 1,575–3,150 | coalesce |
| board-A-scale pour | 22,740 | 1 | 22,740 | coalesce |
| 500 nets × 6 connected traces | 3,000 | 500 | 6.0 | keep |
| 30,000 traces, no overlap | 30,000 | 30,000 | 1.0 | keep |
| one trace | 1 | 1 | 1.0 | keep |

**An AREA-overlap rule was tried first and abandoned.** It looks like the obvious discriminator and
the brief's "what not to do" names overlap explicitly — but gate 1 requires a rectangle painted at
**zero** overlap (just touching) to coalesce, and at zero overlap `Σ strokeArea / unionArea` is
exactly 1.0, which is also what a trace network scores. The measure cannot separate the two cases it
has to separate.

**The trace fixture that first "failed" was the fixture's fault, and it is worth recording.** A random
walk of 200 nets × 6 segments in a 40 × 40 mm area put 1,200 strokes into **two** regions — a score of
600 — because randomly placed traces cross each other everywhere. Real traces on one layer do not:
they are DRC-clean by construction, so a net is a region and the score is its segment count. Laying
the same fixture out on a 20-mil pitch drops it to 6.0. A fixture that does not obey the design rules
the input obeys measures nothing.

### The polarity trap, and why nothing here has to remember it

R-rf3-3's hazard is real and silent: a CLEAR (`%LPC*%`) stroke REMOVES copper, and unioning it with a
dark one paints the hole solid with a perfectly plausible-looking result. Nothing in the coalescer
filters for it — instead **a layer that painted any clear object is COMPOSITED by `GerberReader`
already** (R-L4e-13), and `GerberImport` skips every composited read wholesale. So "never union across
polarity" is true by construction rather than by a check that could be forgotten, and the composited
layer's geometry is exact anyway.

Grouping is **per READ**, which is narrower than R-rf3-3's "per layer" and deliberately so: two files
can land on one layer and they are different artwork, which is the same hazard `CarveClaimedPads`
records for itself. The rest of the key is everything else that makes two strokes interchangeable —
layer, net, aperture function, component, pin — so a `GND` pour and a `VCC` pour on one layer stay two
pours.

### The tolerance is stated because it is the only approximation

R-rf3-5. The union of stroked outlines is exact in principle; a round end cap's arc flattening is not.
`ToClipperPaths` gained an optional `arcTolDbu` (**default 0 = Clipper2's own default, so no existing
caller's geometry moves by a byte**) and the coalescer passes 0.1 µm, which the import note prints.

The three candidates, on a 12,700 DBU offset:

| tolerance | segments per cap circle | 29,000-stroke union input |
|---|---|---|
| Clipper2's default (~1 DBU) | ~250 | ~7,000,000 points |
| **0.1 µm (100 DBU)** | **25** | **~800,000 points** |
| `LayoutFlattener.DefaultTolDbu`, 1 µm | 8 | ~290,000 points |

1 µm is the model's fallback and it is **too coarse here**: on a one-mil scanline that is a 4% chord
error, and the pour boundary is exactly where a DRC clearance is measured. Clipper2's own default is
accurate past anything downstream can use and costs an order of magnitude more. Gate 9 asserts the
consequence rather than the number — a clearance `DrcEngine` reports on the un-coalesced document is
reported identically on the coalesced one, and one it passes still passes.

### Two things that would have been silent

* **`CarveClaimedPads` and `DrillViaPairing` still work** because only `PathShape` is a candidate. A
  flash stays a flash, so the pads via pairing measures against are untouched — and that is also why
  coalescing sits *after* the attributes ride onto the shapes and *before* step 8.
* **`PcbImport` coalesces after `ResolveViaLayers`, not before.** That method pairs `sources[i]` with
  `reconciled[i]` **by index**, and coalescing changes the list's length. Before it, every via on the
  board would have been given some other shape's layers.

### What changed in an existing gate, and why it is not a re-baseline

`ConvertCliVerbTests`' byte-identity comparisons are unchanged — its fixtures are flashes and traces
with no painted fill in them, which is a property of those fixtures and not an exemption; the new test
beside them carries both sides of the flag. The one gate that did move is
`GerberImportTests.AVectorFilledPour_NamesTheLayer_TheCount_AndTheMergeAction`: R-L4g-16's "select
them and use the editor's Merge action" advice is now given **only when coalescing is off**, because
when it is on the import has already performed exactly that merge. Both halves are asserted, so the
message stays reachable rather than quietly dying with the default.


## RC-12 — the longer note on a history entry, and where a note is allowed to live (2026-09-11)

`docs/design/revision-control.md` §5.12. Both recording dialogs and the correction dialog now carry a
second, multi-line field; `MessageNotes` is the one encoding, and `CommitMessage`, `CheckpointMessage`,
`WorkspaceCommit`, `WorkspaceCheckpoints`, `RestorePoints.Rename`/`MarkKept` and `VersionCorrections`
all route through it. Gate: `tests/Ui.Tests/Revision/NotesOnEntriesTests.cs`.

### The extent is COUNTED, and that is not fussiness

The note sits in the body of the message the entry already carries, directly under the subject. The
obvious way to read it back is to recognise circuitRF's own generated sentences — `ExplicitLine`, the
restored-from line, `Explanation(origin)` — and treat whatever is left as the note. **That fails
silently the first time one of those sentences is reworded**, and every workspace recorded before the
change then reads its own explanation back as part of somebody's paragraph. There is no error and no
way to notice except by reading an old entry.

So a `CircuitRF-Note-Lines: N` trailer says how long it is, and the note is lines `[2, 2+N)`. Exact,
reworded-sentence-proof, and ignored on the way in by every reader that predates it.

### The count is read from the TRAILING trailer block alone

A note is free text a person typed, so a line of it can perfectly well read `CircuitRF-Origin: …`.
Scanning the whole message for the count would let **a note describe its own extent** — and both
readers would then parse a line of somebody's prose as a key. `MessageNotes.CountedLines` therefore
walks backwards from the end over blank and trailer lines and stops at the first line that is neither,
which is git's own definition of a trailer block. Both builders put a generated sentence between the
note and the trailers, so the note can never be inside that run.

The same extent is then stepped over by `CommitMessage.Read` and `CheckpointMessage.Read` — otherwise
the note's first line is read as the SUBJECT on any entry whose own subject line is blank, and a
trailer-shaped line inside it overwrites a real trailer.

### Every rebuild has to carry it, and two of them are easy to miss

`RestorePoints.MarkKept` and `RestorePoints.Rename` rebuild the whole message over the identical tree.
Either of them dropping the note deletes somebody's paragraph as a side effect of an operation about a
label, with nothing said anywhere — the same trap R-rc11-4 already records for the sequence, the
origin, the kept mark and the left-out record. `Rename`'s early-out also had to learn about it: it
returned success without writing when the label was unchanged, so **correcting only the note reported
success and wrote nothing**.

### Null is "leave it alone"; empty is "remove it"

Every design-layer entry point takes `string? note = null`. Null is what a caller that predates notes
means and is what `history retitle --title …` with no `--note` means, so correcting a title never
discards the paragraph under it. An empty string removes the block and the trailer.

### A no-note entry is byte-identical to what was written before

No block, no trailer, nothing. Asserted directly, because the alternative is every workspace growing a
line that says an entry has no note — visible in every message anybody ever reads with `git log`, and
a format change made by accident.

### The shared-version annotation became a message

§5.11 case (c) writes a git note on `refs/notes/circuitrf`, which was the corrected title as one line.
It is now `VersionCorrection` — first line the title, remainder the note — and **a single-line
annotation written before this still means a title and no note**, which is what it always meant.


## ANT-1 — a width-bearing Path is metal, and it never was (2026-09-10)

`brief-antenna-1-stroked-path-is-metal.md`. `PlanarExtractor` discarded **every** `PathShape` on
every layer, on the premise its own comment stated: a Path "is a centreline … it encloses no area".
That is true of a zero-width one and false of every other. `PathShape` carries a `Width`, an `End`
style, optional arc `Edges` and its own flatten tolerance; the Gerber reader keeps an imported track
as one "primitive-for-primitive", the board reader builds them, the Gerber and DXF writers write them
back out through an aperture of that width, and DRC outlines them. **Everything in the application
except that one branch already treated a stroke as copper**, so an imported board was EM-simulated
with its traces missing and said nothing about it.

### The measurement, and why it excludes every other explanation

Owner-supplied imported patch board, one frequency point:

| what was solved | Zin near 1.7 GHz | power leaving the port |
|---|---|---|
| as imported, N = 3,796 | 0.70 − j56.5 Ω | 2.3 % |
| as imported, N = 7,442, conformal, accelerated | 0.70 − j56.5 Ω | 2.3 % |
| feed replaced by an equivalent `Rect`, N = 4,854 | 13.6 − j89.4 Ω | 22.6 % |

The first two are a pure 1.6 pF capacitor, flat and monotonic across 1.55-1.90 GHz. **Doubling the
mesh and switching conformal cells on moved neither digit**, which is what rules out mesh coarseness
and leaves only "the conductor is not in the model". With the feed present the board resonates at
1.740 GHz against a cavity-model estimate of 1.734 GHz.

### The fix is a FALL-THROUGH, not a new branch

The classification loop's Path branch now catches `PathShape { Width: <= 0 }` only. A width-bearing
stroke falls through to the ordinary dispatch and is classified by its layer like any other artwork —
**which is what keeps the conductor-binding-before-via-binding order intact**. Doing the width test
ahead of the dispatch (the obvious spelling) would have re-ordered it, and MIM-1's own guarantee is
that a layer bound to both a conductor entry and a via entry behaves exactly as it did before.

The outlining is `LayoutBooleans.Repair`, reached through the new one-line `RegionsToMesh` helper —
everything that is not a Path is its own region and is returned unchanged, so every existing
conversion is bit for bit what it was. **No second offsetter was written**: `LayoutClipper`
dispatches a Path to `InflatePaths` on the flattened centreline at `Width/2` with the cap taken from
its `End` style (all four; `Extended` maps to `Square`, the same offset amount), and the NonZero union
that follows resolves a self-crossing track into clean outer rings and holes. DRC and both writers
reach the same routine, which is what makes "what the solver meshes" and "what the fab sees" the same
copper — a second offsetter disagreeing at a mitre would be a defect nobody could localise.

### The via-layer case: converted, by the same rule (§3a)

The brief left this to the owner and recommended conversion; it is taken. A routed, plated slot is
drawn as a stroke, and the alternative is that one shape means copper on a conductor layer and
nothing one layer down. `regionViaShapes` therefore takes a width-bearing Path exactly as it takes a
rectangle, and the run reports the count separately.

**This is the one place a recorded expectation moved.** `RegionViaExtractionTests`'
`APathOnAViaBoundLayer_IsNamedRatherThanFoldedIntoTheUnboundSentence` drew a **10 µm-wide** stroke
and asserted an empty `ViaList` — it was asserting MIM-1's premise, which ANT-1 has just shown false.
It is narrowed to `Width = 0` and renamed; what it was really about (a via-bound layer never produces
the *unbound* sentence) is untouched. Nothing else in the repository constructed a `PathShape` that
reached `PlanarExtractor`: of the 88 `new PathShape` sites under `tests/`, the intersection with the
files that mention `PlanarExtractor` is two, and the other one — `EmRefusalWordingTests`' bent trace —
goes to `CrossSectionExtractor` (kernel A), which ANT-1 does not touch and which still refuses it.

### A converted stroke is counted as CURVED, deliberately

`flattenedCurves` uses `LayoutBooleans.IsCurved`, which answers true for **every** `PathShape`
regardless of its `Edges`. That is the repository's single predicate for "this needed the flattener"
and it is kept rather than second-guessed: a stroke's outline really is built at the layout's flatten
tolerance, and `JoinType.Round` means any stroke with an interior vertex or a round cap genuinely
carries approximated arcs. The over-count is the straight two-point flush stroke, which is honest
about the tolerance it was offset at and wrong about nothing a user would act on. A second predicate
here would encode `LayoutClipper`'s internal join/cap choice in a second place.

Both counts are per SHAPE, not per resulting region — an outlined stroke may yield several polygons,
and the flattening note is about the shape whose curves were approximated.

### The report says what was CONVERTED, not only what was ignored

The brief's own diagnosis of why this went unnoticed for as long as it did: the only sentence about
strokes was an ignored-count among twenty. There are now two conversion notes (conductor artwork,
with the outlined area in mm²; via footprints), and **both ignored-sentences stopped claiming a
stroke encloses no area** — after this phase they can only ever be about `Width == 0` and they say
so. Leaving a refusal standing on a reason that has just been shown wrong is worse than the count
being silent.

### What moves, and what does not

- **No recorded number moves.** There is no `.clay` file anywhere in the repository, so every EM
  fixture is constructed in code and none of `HISTORY.md`'s measurements sits on a stroke.
- **`EmSnpProvenance.GeometryHash` changes for any design containing a stroke** — correctly, since it
  hashes the extracted `PlanarProblem`'s conductor artwork and there is now metal where there was
  none. Every `.snp` sitting beside such a design is stale and will be **reported** as stale on the
  next comparison. That is the right outcome and is stated here so it is not discovered.
- A zero-width Path is still ignored on both layer kinds, and a stroke on a **ground-designated**
  conductor is still ignored as ground artwork — the conversion cannot smuggle a finite ground pour
  into the mesh.

Gate: `tests/Ui.Tests/Em/StrokeIsMetalTests.cs` — 11 tests, 0.5 s including two solves. The decisive
one is an independently-authored oracle: the same line as a `PathShape` and as a rectangle **written
by hand from the stroke's endpoints and width**, extracted to the same area and extent and solved
through `EmRunService` to S-matrices agreeing to 1e-9. The rest are the four end styles asserted as
extents (`Round`'s cap may only ever fall short of the true semicircle, never past it), an arc-bearing
stroke counted in the flattening note, the two zero-width sentences, the ground-layer refusal, the
conversion count and area in the note, and the via-bound footprint.


## RP-2's extraction-side audit, before any of it is built (2026-09-10)

`brief-em-return-plane-2-per-port-reference.md` was measured and then split; the kernel half of the
finding is in `src/Engine/Mom/RESOLVED.md` (short version: the mesher cannot produce a basis spanning
a slot, structurally, so a conductor-referenced port is **two cuts, one per conductor**, not one cut
across the gap). What is extraction-side and worth having written down before RP-2b starts:

- **`EmPortExtraction` constructs every `PlanarPort` at one site** (`ports.Add(new PlanarPort(...))`),
  and it already threads `Kind`, `LayerIndex` and `GroundPathWidthM` through per-port branches. A
  reference and a negative-terminal point are one more pair of arguments there, not a second path.
- **`LabelShape` is the right home for the reference** (R-rp2-8) and the precedent is already set
  twice over: `PortDirection` and `PortLayer` are both nullable, both additive, both round-trip with
  no `FormatVersion` bump, and both mean "work it out" when null. `LayoutGeometry.cs`'s clone of
  `LabelShape` enumerates every property by hand — a new one added to the record and not to that
  clone is dropped silently on copy/paste, which is the trap to name.
- **The negative terminal cannot be a second `LabelShape`.** Two labels naming one port number is
  already a refusal by name (§10.6), and re-using that spelling for a port's two terminals would make
  the existing refusal wrong. It has to be a coordinate on the port's own label.
- **`PlanarExtractor`'s "Every port returns through …" note is the only place the return path is
  visible**, and *both* of its spellings assert "That plane is the negative terminal of every port in
  this run and is not selectable per port." R-rp2-9 makes that sentence false; it is one string in one
  method (`PlanarExtractor.cs`, the R-em-4 block) and must become per-port where the ports differ.
- **`explain` reports the return plane as a run-level fact** since RP-1. The same change reaches it.

## RP-1 — a `.cem` may name its own return plane, and R-em-4's query turned out to be spelled twice (2026-09-10)

`EmSetup.GroundStackupLayerName` names the conductor every port in one run returns through. **Empty
means R-em-4** — the top surface of the highest ground-designated conductor below the lowest analysis
level — so every document written before the field, and every one that leaves it empty, takes the
inferred path unchanged. It is omitted from the file when empty and needed no `FormatVersion` bump,
the rule `AnalysisLevelNames` and `PortZ0s` already follow.

Gate 1 was the one that mattered: a two-level fixture extracted with no settings at all, with the
field null, and with it set to `""` produces the same slab, the same level z's, the same interfaces
and the same interface materials. **No existing fixture's `PlanarProblem` moved.** The comparison is
a structural signature rather than record equality, because `PlanarProblem` is a record over ARRAYS
and its own `Equals` is reference equality on the level list — it would pass on two entirely
different problems.

### The refusals are the substance, and the fourth case is deliberately not one

- **No conductor by that name** — refused, naming the technology and LISTING every conductor it does
  have, ground-designated or not. Falling back to R-em-4 would answer a question nobody asked.
- **A conductor that is also an analysis level** — refused. It cannot be both the meshed metal
  carrying unknowns and the laterally infinite boundary that metal returns to, and there is no
  reading of "both" the kernel could act on. Checked BEFORE the height rule, because on the common
  spelling of this mistake both would fire and the collision is the specific one.
- **A conductor at or above the lowest level** — refused, naming the level and BOTH heights. This is
  R-em-4's own physics, not a limitation of the override: a port returns through a plane beneath the
  conductor it feeds. The heights are in the message because the user is looking at a stackup table
  and cannot see the analysis levels from there.
- **A conductor the technology does not designate as ground** — **accepted**, and that is the whole
  point: the alternative is un-ticking "Ground reference" in a technology every other design shares,
  which also turns that plane into meshed signal metal everywhere. A note says the technology and the
  run disagree and that the run won, for this run only. Without it the `.ctech` and the answer
  disagree with nothing on screen to say which did.

The `.cem` panel's combobox lists **every** conductor behind an "(automatic)" first row and MARKS the
designated ones rather than filtering the others out — a list that hid the legal choices would teach
the wrong rule. A name the technology no longer has stays selected, marked as missing, rather than
being quietly reset: the run refuses it by name, and a panel that cleared the setting would hide the
disagreement the refusal exists to report.

### The finding the brief asked for: R-em-4's query IS spelled a second time

`HighestGroundBelow` made this a one-site change **within `PlanarExtractor`** — all three of its call
sites (the incidental-level trim's two probes and the return-plane resolution) go through it. But the
rule is written a second time in **`CrossSectionExtractor`**, which resolves its own ground with its
own copy of "highest ground-designated conductor below the signal". The two are not textually
unifiable as they stand: each extractor has a private `Band` record, and the cross-section rule reads
`signal.BottomM` where the planar one reads `signal.SheetM` (MIM-6's sheet-surface distinction, which
kernel A has no notion of). So they are two implementations of one sentence, and they agree today by
inspection rather than by construction.

**What that costs RP-1 immediately:** `EmRunService` runs BOTH extractors on every launch, so a setup
that named a return plane and then resolved to kernel A would have run with the field silently
ignored. `CrossSectionExtractor` now emits a note saying so and naming the analysis-kind setting that
would honour it. A note rather than an honouring, because teaching that file the override would be a
third spelling of rules whose entire value is being written once; and not a refusal, because it would
break `Auto` for anyone who set the field on a uniform line, where kernel A is the right and much
cheaper answer.

### Two things measured rather than assumed

**On the shipped 4-layer starter the overridden medium legitimately collapses to one region.** All
three of its dielectrics are the same FR-4, and the two inner coppers are absorbed into their
neighbours, so a Top Copper trace referenced to Bottom Copper is one homogeneous 1508.2 µm slab and
`MediumStack` stays null — the L8 one-slab path, which is correct. The intervening layers are carried
as THICKNESS, not as interfaces. To exercise the stratified half the gate gives the core a different
εr; then `LayerCount > 1` and the regions sum to the same slab. A gate that asserted "the LayerStack
carries the intervening layers" against the stock starter alone would have failed for the right
answer.

**The skipped-plane WARNING is not suppressed under an override, and matters more there.** A
designated plane between the levels and the chosen return is absorbed into the dielectric — its metal
modelled as substrate — and the override is precisely how someone reaches that state on purpose.

### `explain` reports it, and is asserted against the extraction

`circuitrf explain <file.cem>` gains a `return plane` step: the conductor, its height in µm, and
whether R-em-4 or the document chose. It runs the extraction (geometry and a stackup in, a medium
out) rather than restating the rule, because a rule restated in the CLI is a rule that can disagree
with the run — which is exactly what a caller asks that verb to rule out. The plane is carried out of
the extractor on a new `PlanarReturnPlane` beside the problem, because `PlanarProblem` is the neutral
engine type and knows only a height; a caller wanting the NAME would otherwise have to re-derive
R-em-4 from the technology. The step is absent when the extraction refuses.


## Stray import artwork on an inner layer set the return plane for the whole run (user report, 2026-09-10)

A patch antenna imported from a board file and drawn on Top Copper reported

> Every port returns through 'Bottom Copper', the ground-designated conductor at 35 µm — the highest
> one below the signal level at 335 µm.

The plane directly under the patch is Inner 1, 300 µm below it. Every word of the note was true, and
the run it described was wrong by a factor of 5.6 in substrate height.

### The signal level was not the layer anyone drew on

`PlanarExtractor` took its analysis levels as *every signal conductor that carries artwork*, and
`signal = levels[0]` — the LOWEST — is what R-em-4 asks its ground question of. The import had brought
in the inner-layer annular pads of the connector's two through-holes: two 0.485 mm circles on Inner 2,
which is a signal conductor, carrying artwork, and lower than Top Copper. That made Inner 2 the signal
level, and "the highest designated ground BELOW the signal" then could not see Inner 1 at all.

**What happens to the plane that is passed over is worse than being unused.** `BuildMediumStack`
absorbs any conductor band that is not an analysis level into a neighbouring dielectric, so Inner 1's
18 µm of copper was modelled as 18 µm of FR-4. The real ground plane did not merely fail to be the
reference — it left the physics entirely, and nothing said so.

### Ports decide one question, and deliberately not the level set

The obvious fix — seed the levels from the ports, grow across drawn vias — was implemented, and the
MIM acceptance fixtures refused it immediately: a capacitor's bottom plate is coupled to its top plate
by the capacitor dielectric and by *nothing else*, so no port and no via reaches it, and
`MimCapacitorTests` lost `Metal1` from all three of its level assertions. A parasitic stacked patch, a
broadside-coupled pair and a floating shield are the same shape. **Field coupling is what a full-wave
kernel is for; a rule that can only follow metal cannot be the one that decides what gets meshed.**

So port reachability answers one narrow question instead. The default is still every level with
artwork; what changed is that the LOWEST level — the only one that sets the return plane — is dropped
when **both** of these hold:

- no port sits on it and no drawn via joins it to one that does, **and**
- dropping it moves the return plane to a *higher* designated ground.

Neither half alone would do. Unreachability cannot condemn a level (that MIM plate), and moving the
return plane cannot either (that is what a lower conductor legitimately does). Only the conjunction
describes metal that is in the way rather than in the structure. It iterates, because an import that
left one such layer usually left two, and it never empties the level list.

**R-em-4's own query is now factored out as `HighestGroundBelow`**, because the check has to ask it of
a level it is considering discarding, and a second spelling of that query is a second chance to get
R-em-4 wrong. It is the one place the rule is written.

### A designated plane between the levels and the return is now named

Reachable only by listing the levels explicitly, and worth a sentence when it happens: such a plane
cannot be chosen (a return must sit beneath the conductor it feeds), is absorbed into the surrounding
dielectric as above, and the run otherwise never mentions it. The note states what becomes of it, not
merely that it was not used.

### Not fixed here, and the reason is structural

The report also asked for a per-port return layer. **The ground plane is not a property of a port** —
it is a boundary condition of the medium, present in every `G(r, r')` evaluation and therefore in
every entry of the MoM matrix, on top of which the ports are `Y = BᵀZ⁻¹B`. `BuildMediumStack` ends in
`new LayerStack(Termination.Pec, layers, Termination.Air)`, and a `LayerStack` has exactly two
terminations: a PEC can sit at either end and nowhere in between, so a second reference plane has no
representation at all. `PlanarPort.cs`' D3 says the same in the kernel's vocabulary. The reachable
version of that capability is a port referenced to *meshed* metal —
`PlanarPortReference.CoplanarGround` / `.SecondConductor` — which is
`docs/sonnet-briefs/brief-em-return-plane-2-per-port-reference.md`. A per-run override of the plane,
which needs no kernel change, is `brief-em-return-plane-1-explicit-ground-layer.md`.


## A Windows-authored workspace would not resolve its technology on macOS (owner report, 2026-09-10)

A workspace shared by a Windows colleague opened with

```
Technology file not found: …/square_patch_antenna_gerber/layout/..\..\square_patch_antenna_gerber.ctech
```

**A path carrying BOTH separators is the tell.** The `.clay` stored
`"TechRef": "..\\..\\square_patch_antenna_gerber.ctech"` and the `.cws` stored
`"DefaultTechRef": "tech\\pcb-4layer_FR-4_62mil_1oz.ctech"`. On Unix a backslash is an ordinary
filename character, so `Path.Combine(layoutDir, techRef)` produced a single non-existent file whose
name happens to contain three backslashes — and the failure was reported honestly, quoting exactly
the path it had built, which is the only reason it was diagnosable at all.

### The convention already existed; two writers did not follow it

`src/Ui/Schematic/WorkspaceRefs.cs` has stated the rule since the stability/passivity brief: **every
relative reference stored in a document is `/`-separated**, and it even names the trap — "it only
fails when a workspace crosses platforms, i.e. never on the machine that wrote it." Most writers do
`Path.GetRelativePath(...).Replace('\\', '/')`. The two that mint a technology reference did not:
`GerberImport` (which produced this workspace) and `LayoutConvert`'s CLI equivalent, plus the three
`.cws` `DefaultTechRef` writers in `WorkspaceViewModel` and `LayoutEditorView`'s
`ComputeRelativeTechRef`.

**And the breakage is one-way, which is why it lasted.** Windows accepts `/` as a separator as well
as `\`, so a workspace authored on macOS or Linux has always opened on Windows. Only the reverse
direction fails, and it fails on someone else's machine.

### The fix: tolerant reading, strict writing

`CircuitRF.Core.RefPath` is now the one place a stored ref crosses to a filesystem path and back:

- `ToNative` / `Resolve` accept **either** separator. A reader may not rewrite the documents already
  out in the world, so every resolution site goes through it — the technology resolver, the cell and
  EM-setup resolvers, `ExternalCellRef`, the `.wasm` resolver, bitmap refs, the clipboard fragment's
  rebase, `check`, `.cdd` sources and the `.cws` open-document restore.
- `ToStored` is what every site that COMPUTES a ref passes its `Path.GetRelativePath` result through,
  so nothing new leaves this machine in a form the next one cannot read. Null in, null out — a
  `TechRef` of null means "use the workspace default" and must not become an empty ref.

`RefPath` lives in `src/Core` rather than here because `src/Design`, `src/Ui` and `src/Cli` all need
it and Design already references Core. `src/Ui/GlobalUsings.cs` **aliases** it
(`global using RefPath = CircuitRF.Core.RefPath;`) rather than importing `CircuitRF.Core` wholesale —
that root namespace also holds `ComponentModel`, which would shadow `System.ComponentModel`.

**The one thing given up:** a `\` is a legal character in a Unix filename, so a reference to a file
genuinely named that way no longer resolves. Since every ref circuitRF writes is separator-normalized,
a `\` in a stored ref can only be a Windows separator.

**Not converted, deliberately:** git paths (`Revision/`), zip entry names (`Archive/`), kit-relative
paths from a vendor `PdkImporter`, and `DocLauncher`'s URL-to-file mapping. Those are `/`-only by
their own format's rule, and the last is a path-traversal guard.

Gates: `tests/Ui.Tests/TechnologyResolverTests` (both resolution branches read a Windows-authored ref)
and `tests/Ui.Tests/GerberImportEntryTests` (the import's own `TechRef` is written with `/`).


## A Gerber import now completes the substrate from one generic FR-4 board (owner, 2026-09-08)

**This reverses GI2's most emphatic rule, on purpose, and the reversal is narrower than it looks.**
`GerberStackupMapping` built the structure an artwork-only set implies — conductors in their resolved
order, a dielectric between each pair, each conductor bound to its drawing layer — and wrote every
substrate VALUE as zero. Zero was chosen precisely: it is outside `TechValidation`'s `Epsr < 1` and
outside both extractors' own `Epsr >= 1` guards, so a fresh import was **unsimulatable by
construction**. `Gi2StackupSkeletonTests` said in capitals not to "correct" it to 1.0, and that half of
the argument still stands — 1.0 is AIR, an entirely valid substrate that RUNS and answers a different
question with nothing downstream to question it.

What the owner weighed against it is what the zeros cost: eleven rows of numbers typed by hand after
every six-layer import, every time, for values that are the same on nearly every board. **A default
that is named and reported is not the silent-air failure the zeros guarded against.** So
`SubstrateDefaults.Fill` completes the stack and the import says which values it supplied.

### What is supplied, and where the numbers come from

The shipped `pcb-2layer_FR-4_70mil_1oz.ctech`, which is why a two-layer set now reproduces that
technology exactly — arrived at rather than copied. εr 4.4, tanδ 0.02, µr 1, outer copper 35 µm, inner
copper 18 µm (the shipped four-layer says 17.5; 18 is the rounded figure asked for).

**Dielectric thickness is derived, never a constant**, and that is the part worth knowing. A stated
overall board thickness wins: budget = stated − every conductor, shared evenly among the dielectrics
that have none. With nothing stated the budget is 1.778 mm, the shipped two-layer's core — so two
layers come out at exactly 1.778 mm and six come out as an ordinary 1.8 mm board rather than a 9 mm
one. A fixed per-dielectric constant would have produced the second.

### Three rules the pass keeps

- **Unset only, never a correction.** Zero thickness, zero εr, non-positive µr. Anything else is a
  number somebody or some file stated and is left exactly as it is — *including values that are
  wrong*, because correcting one would hide a real mistake behind a plausible number and would make
  the import unpredictable. This is `TechValidation`'s own unset-versus-wrong rule, applied.
- **εr and tanδ move together.** A dielectric with a stated permittivity and a zero loss tangent is a
  LOSSLESS dielectric, which is a legitimate thing to author; tanδ is filled in only where εr was
  also unset.
- **Nothing is inferred from the material NAMES in the files.** That refusal is unchanged and
  permanent — it is a lookup table of laminate trade names and it would put third-party product names
  in this repository. `SubstrateDefaults` is ONE generic grade and a second would be the first row of
  that table.

### The trap that made the job-file branch a separate fix

That branch left an omitted `DielectricConstant` at `StackupLayer`'s own C# default of **1.0**, not at
zero — so it read back as a measured vacuum and `Fill` would have left it alone. It writes 0
explicitly now, which is why both branches complete through one pass. The skeleton branch still writes
zeros for the same reason: build the structure with the spelling of "nobody said", then let one pass
complete it.

### The two guards that replaced the zeros

Both are required and neither is in this file. The import's own message — a SECOND paragraph, kept
apart from the one reporting what the files said, because a paragraph mixing read values with guessed
ones is one nobody can act on. And `StackupFieldReadiness`, which marks a field an EM run cannot use in
the Technology editor.

Gates: `tests/Ui.Tests/Gi2StackupSkeletonTests.cs` (rewritten, with its header recording what it used
to assert) and `GerberImportTests`. One assertion survives unchanged and should: the dielectrics are
never 1.0.

## Which stackup field an EM run cannot use, asked once (owner, 2026-09-08)

`StackupFieldReadiness` answers *would an EM run refuse this field*, per FIELD, and every predicate in
it mirrors one `TechValidation` already reports. **The two shapes are not redundant.** A validator
emits SENTENCES attributed to a TAB, which is right for a problems list and useless to a TextBox: a
sentence naming a layer by name cannot tell a box whether it is the one at fault. The alternative —
re-deriving the condition beside the control — drifts from the one the extractors actually refuse on,
silently, and in the direction that costs: a field marked fine that stops a run.

**It marks UNUSABLE, never IMPLAUSIBLE.** A defaulted 4.4 on a board that is really 3.66 is not marked,
because nothing in circuitRF knows which board it is looking at — that is what the import's own "these
are guesses" paragraph is for. A mark that appeared on plausible numbers would be one nobody could
ever clear.

A field the row's kind does not use answers null, so a hidden control never contributes a mark with
nowhere to go — the same reason a via is never asked for a thickness (it has no z band of its own) and
only a plated one is asked for a wall.


## The WSProbe glyph bent every wire it was placed in (owner, 2026-09-08)

The WSProbe was drawn on the IProbe's geometry deliberately — two pins at `(0,100)`/`(100,100)`, stems
rising to a connector at `y = 0` — on the reasoning that both are placed into a wire the same way and
`SeriesProbeInsertion` needs both pins on one straight segment. The second half is true; the first
half was not. **An IProbe hangs BELOW the wire it measures; a WSProbe sits IN it.** Dropped into a
horizontal run, the dropped pins turned the wire through 90 degrees at each end.

It is now a straight horizontal through: pins at `(0,0)` and `(100,0)`, a short lead from each pin to
the body edge, and the body's `G`/`L` letters on that same line. **The leads stop at x = 15 and x = 85
rather than crossing the body** — a line through the middle ran straight through the two letters,
which are the only marks that tell the terminals apart. Two files, and they must agree:
`EditableSchematic.PortDefsFor` (the pins) and `BuiltInSymbols.BuildWSProbe` (the drawing).

**This MOVES the pins of an already-placed WSProbe by 100 units in y.** Nothing shipped in the
repository places one — no `.csch`, no template, and the WSProbe test fixtures are all `.cnl` netlists
— so there was nothing to migrate here. A design drawn before this needs its probes re-connected, and
there is no format version to detect that: the pin offsets are geometry, not stored state.

Gate: `tests/Ui.Tests/Schematic/WSProbePlacementTests.cs`, whose `PinDrop` constant is now 0 and whose
wire-cut cases pass unchanged — which is the evidence that the cut affordance never depended on where
the pins sat, only on their being collinear.

## The component catalogue reported one number where there are two (AUT-10, 2026-09-08)

`CatalogEntry` carried `Ports` — the SYMBOL's pin count, from `SymbolPortDefs` — and the CLI printed
it under a line labelled `nets:`. Those are different quantities wherever a terminal is implicit on
the glyph (`Tuner`, `Port`, `Term`, `Vdc`, `IProbe` all draw one fewer pin than their instance line
binds nets) and different by construction for `SDD`. The catalogue is the only statement of the
netlist contract a client with no schematic editor and no source tree has, and it was wrong for about
a third of the registry.

`CatalogEntry.Nets` is the second field, from `CircuitRF.Core.Netlist.InstanceNetContract` — the table
the elaborator refuses a wrong count with, so there is one statement rather than two. `Ports` is
unchanged and still means what it meant; only the label it is printed under changed, to `terminals:`.

**Two smaller things fell out of it.**

`NoteFor`'s text for a symbol-less type ended "…and nothing below the UI firewall states how many nets
its instance line takes." That was true when it was written and became the defect: the reader knew all
along. The note now says what such a type is genuinely missing — the palette's parameter defaults and
its pin names — and points at the `nets` line for the count.

`TokenPorts` returns a blank `CatalogPorts` for TWO different reasons — no symbol draws the type, or
several tiles draw it and disagree about the pin set (`Port` has `Term` and `TermG`) — and the CLI
printed one sentence for both. They are different facts and the second one has an answer: each tile's
own line below IS it.

## GI series — review follow-up (2026-09-07)

Three defects found reviewing the five phases against a production six-layer output set. All three
are in GI4, all three are fixed, and the first of them was a **regression**: the phase made a folder
that had imported correctly stop importing at all.

### 1. A declaration that settled the digits and not the suppression made the file WORSE than no declaration

**The bug.** The zero-suppression ladder's full-width rung was keyed on
`digitsEvidence == DrillFormatEvidence.CoordinateWidth` — that is, on the digit count having been
settled *by* the coordinate width. GI4 put the declaration rung above that one, so a parameter file
that stated the digit split displaced the conclusion the coordinates still proved, and the
suppression fell through to `Defaulted`. `Defaulted` is the value that raises the format prompt, and
headless it is a refusal: `The drill format was not settled, so nothing was imported.`

So a drill file that writes every coordinate at its full field width — which settles the question by
itself, and did — was refused the moment a companion parameter file **that agreed with it** was added
to the folder. Adding evidence made the answer worse. That is the shape of failure this whole series
exists to remove, arriving through the series' own new code.

**The fix.** The rung now keys on the FORMAT rather than on the digits' provenance: it fires whenever
the file's own coordinate words are at the resolved full width, whichever rung supplied the digit
count. `DrillFormatDeclarations.CoordinateDigitWidth` is already non-null only when every coordinate
word is one width **and** one of them carries a leading zero — which is what proves nothing is
suppressed — so the condition is a restatement of what that field already means, not a new inference.
The declaration rung still sits above it, so a declaration that *does* state suppression still wins.

The resolution table in §3 of the GI4 entry below is unchanged by this: the rung stayed where it was,
it stopped being unreachable.

### 2. The suppression keywords were not recognised clipped

`SUPPRESS-LEADING-ZEROS` and `SUPPRESS-TRAILING-ZEROS` were in the vocabulary; `SUPPRESS-LEAD-ZEROES`
and `SUPPRESS-TRAIL-ZEROES` were not. These tables are written to a fixed column width and the words
are routinely clipped to fit it.

**The cost of the miss is not a missing keyword.** The file still classifies as a declaration on the
strength of its other lines, so it is recognised, read, and reported as having settled the format —
while the one field it exists to settle is silently absent. Combined with §1 that turned a working
import into a refusal; on its own it leaves the most dangerous of the three unknowns defaulted with
the answer sitting in the file. Eight aliases added, four per zero class.

### 3. An artwork parameter file scoped itself through a keyword that was not read

`GerberCompanionFiles.Read` compares two parameter files in one folder and, when they disagree,
discards **both** — correct when they describe the same data, and wrong when they describe different
kinds of it. An artwork parameter file usually says which kind it is only by naming the **output
device** the job was written for, and `DEVICE-TYPE` was not a `DATA-TYPE` alias. So it read as
`Unstated`, landed in the drill bucket beside the drill parameter file, contradicted it (4:5 against
3:4, which is not a contradiction at all — it is two statements about two different files), and took
both out of the run with a message asserting they *"declare DIFFERENT coordinate formats"*.

An output folder holding one parameter file per data kind is an ordinary shape, so this disabled GI4
outright on a whole class of set while reporting a contradiction that did not exist. `DEVICE-TYPE`
and `OUTPUT-DEVICE` added; the value guard is what keeps them from claiming a plotter model number,
since `DataType` yields a scope only when the value names a data kind.

**And a latent one found in that guard.** `NC` was tested as a SUBSTRING, and it is two letters — it
is inside `INCH` and `ENCODED` among others, and the drill markers are tested before the artwork
ones. So a value naming artwork could be scoped at the drill data. It is now matched as a whole
hyphen-separated word; every other marker in that method is long enough that a substring cannot
collide by accident.

### What the review did not find

* **R-gi5-1 holds.** Every netlist path attaches facts to shapes the readers built. `AttachNets` walks
  a list it is handed and sets a string; `SpanFor`, `ApplyPlating` and `NetlistHoleIndex` return facts.
  No shape is created, moved or removed anywhere in `BoardNetlistFile.cs`.
* **`Plated` round-trips.** `TechPersistence` serialises `Stackup` whole and omits nulls, so GI1's
  field needs no format work and an older `.ctech` reads bit-identically.
* **`CrossSectionExtractor` needed no non-plated exclusion.** GI1's brief asks both extractors to skip
  a non-plated via; that extractor already ignores every via shape (a uniform cross-section has none),
  so the requirement is met without a change there.
* **The archive path is zip-slip safe.** `ExtractedArchive.Destination` refuses any entry landing
  outside the temporary root. There is no size cap on extraction, which is worth knowing but is not
  reachable without an explicit yes.

## Phase GI5 — COMPLETE (2026-09-07)

`docs/sonnet-briefs/brief-gi5-netlist-companion.md`, last of the GI series and the largest. A Gerber
import of a real multi-layer board printed three apologies in one run — that a via and a plated
component hole are *"indistinguishable from artwork alone"*, that a composited layer's *"per-object
net names are gone"*, and that *"no layer span was declared"* so every hole is assumed to go through
the board. Every one of those is true about the **artwork** and none of them is true about the
**folder**: a production output set routinely ships an IPC-D-356/356A board netlist beside its
artwork, and that file was classified *"no Gerber or drill content in its head"* and skipped. New
reader: `src/Design/Layout/Interchange/BoardNetlistFile.cs` (the reader plus the evidence/matching
half, one file, exactly as `GerberDeclarationFile.cs` holds GI4's two). Gated by
`tests/Ui.Tests/Gi5NetlistCompanionTests.cs` — **28 tests**, one or more per gate.

Written from public documentation only. The format is a standards-body one in the same class as
Gerber and Excellon; nothing in the reader or its fixtures names a tool, product or toolchain.

### 1. What the format turned out NOT to carry (the brief's §8.5, and the biggest finding)

**The layer span.** The brief's §1 lists, among what the netlist carries, *"the layers the feature
reaches — which is the layer span"*. It does not carry that. It carries an **access code**: one
value per record, saying either "reachable from both outer surfaces" or naming the **single** layer
the feature is reachable from. That is **accessibility, not a span**, and the difference is exactly
the case R-gi5-5 was written for — **a buried via is reachable from neither surface**, so no single
access code can describe one.

A span is therefore recoverable only where a writer emits **two records at one coordinate naming two
different layers**, and whether it does is a property of the writer rather than of the format. So
`BoardNetlistEvidence.SpanFor` is deliberately three-valued:

| What the records at one hole say | What is done |
|---|---|
| any record says "both surfaces" | a through-hole span, top conductor to bottom |
| two or more records name two or more layers | that span, min to max, classified through / blind / buried |
| exactly one record naming one inner layer | **reported and used for nothing** — one layer is not a span, and inventing the other end is inventing a stackup |

R-gi5-5's other half holds: **no stackup entry is synthesised per span**. The span lands on the via
entry `GerberImport` already mints for that drill file, through `SpanEndName`, and a file whose holes
disagree about their span applies **neither** and names both (one drill file is one stackup entry and
it cannot carry two).

Two smaller things the format does not carry the way one would assume:

* **Net names are limited to a fourteen-column field.** A longer name is written into the header as
  an alias and the record carries the alias — so a reader that ignores the header table reports a
  net name that is **wrong** rather than missing, and nothing downstream would question it. Both
  spellings of the header record in circulation are read (`BoardNetlistFile.LongNetNames`), because
  the difference between them is whitespace and getting it wrong costs every long name in the file.
* **Conductor-route and board-outline records exist and are geometry.** They are counted, reported
  as present, and read for nothing — R-gi5-1, and §7's rule that building connectivity from this
  data is a different piece of work.

### 2. The match rate (§8.1) — and there is no real set in this repository to measure it on

**Unmeasurable here, and that has to be said rather than answered with a number.** GI4's own note
records the same fact and it has not changed: `testdata/pcb-samples` is board files, every Gerber
fixture under `tests/` is hand-authored, and a search of the whole tree for a netlist's own header
record or its record codes finds **nothing**. Manufacturing every number below from fixtures I wrote
is not a match rate; it is a check that the reader does what its own tests say.

What the fixtures do measure, stated as fixtures:

| Fixture | Records | Containment | Near | Unmatched | Nets attached | Ambiguous |
|---|---|---|---|---|---|---|
| via + component hole | 2 | 2 | 0 | 0 | 1 (+1 already named) | 0 |
| one net in a composited pour | 1 | 1 | 0 | 0 | 1 | 0 |
| two nets in one composited region | 2 | 2 | 0 | 0 | 0 | 1 |
| two nets, one on an isolated island | 2 | 2 | 0 | 0 | 2 | 0 |
| a coordinate one count outside a pad | 1 | 0 | **1** | 0 | 1 | 0 |

**The near rung is real and is not decoration** — but the reason it exists is not round-off between
two exporters. It is that **the coarsest resolution this format defines is COARSER than the pairing
tolerance the drill reader uses**: one count at 0.0001 in is 2.54 µm, against
`DrillViaPairing.SnapMicrons`' 1 µm. A fixed one-micron tolerance would have missed real matches on
every inch-resolution set in the world. `BoardNetlistEvidence.ToleranceDbu` is therefore **derived
from the netlist's own resolution** (one count, floored at one micron) and stated in the report by
the number rather than by the word "tolerance". It is still two orders of magnitude below the
tightest pad pitch in circulation, which is the bound that matters.

### 3. What R-gi5-6's headline capability is actually worth (§8.2)

**Most large pours should come back with a usable net name, and the reason is structural rather than
lucky.** This was the open question in the brief — *"if most large pours are ambiguous, say so"* —
and it is answerable from how compositing works, then measured.

A pad of a second net inside a pour is separated from it by a **clearance**. Compositing unions the
layer through Clipper, which turns that clearance into a **hole** in the pour — and
`LayoutClipper.FromClipperTree` **recurses into a hole's own islands and emits them as further
top-level shapes**. So the isolated pad is its own shape, with its own outline, and the containment
test names it directly. Measured:
`APadIsolatedInsideAPourByItsClearance_IsItsOwnRegion_AndBothNetsAreNamed` — **two nets, two shapes,
two names, zero ambiguous.**

Ambiguity therefore requires two nets to share **one** region, which means copper that is
galvanically joined — which on a correctly drawn board is not two nets. The fixture that produces it
(gate 7) has to join them deliberately. **R-gi5-9 is still exactly right and must not be relaxed**:
when it does happen, `LayoutShape.Net` is one nullable string and a region holding two nets cannot
honestly carry either, so it carries neither and is counted. A test asserting it picked one would be
asserting the bug.

The one loss that stays permanent, and the loss sentence now says only this: **the shape
IDENTITIES are gone.** The net names are not, when a netlist came with the set — so the sentence in
`GerberImport`'s "what this format cannot carry back" list is rewritten rather than added to
(R-gi5-12).

### 4. The via/component split versus the heuristic it replaces (§8.3)

**They disagree on every through-hole component pin on a board, and that is not a tuning problem —
the old rule is systematically wrong there and says so itself.** `DrillViaPairing`'s pre-GI5 rule is:
a hole with a copper flash at the same coordinate is a via, unless the drill tool DECLARED itself a
component or mechanical drill. A through-hole resistor's pin is a hole with a copper flash at the
same coordinate, so it was a via — and the diagnostic that said *"the distinction was not
available"* was an accurate description of that, not a hedge.

The netlist settles it by a field lookup, and the two agree exactly where the drill file already
declared a tool function (which is where the old rule was not guessing) and disagree exactly where it
did not. Ranking is unchanged from the rest of this series: **the drill file's own attribute
outranks the netlist**, because a file is authoritative about itself.

**R-gi5-3 is read literally, and deliberately so.** A record with a reference AND a pin is a
component hole; a record with a net and NO reference is a via; **a record with a reference and no
pin settles neither way** and is counted. The tempting shortcut — treat any reference as a component
— fails badly on a writer that puts a marker word in the reference field of a via: every via on the
board would be counted as a component hole, silently. The unclassified bucket is reported instead,
which is the only outcome a reader can check.

### 5. Every disagreement between the netlist and the artwork, by kind (§8.4)

All seven are reported with counts and **none of them repairs anything**:

| Kind | What is said |
|---|---|
| a coordinate matching no copper | counted, with the tolerance; nothing is drawn |
| a hole named but drilled by no file | counted; the usual cause named as revision skew |
| a net name disagreeing with the artwork's own `%TO.N` | the artwork's is kept, the disagreement counted |
| plating: netlist vs the drill file's own statement | the drill file wins, and it is said |
| plating: netlist vs the tool listing | **neither** is applied, both are named (see below) |
| two layer spans in one drill file | neither applied, both named |
| the netlist's scale against the artwork's extent | settled against the artwork, or the file is dropped by name |

**The tool-listing case is the one R-gi5-4 exists for and it needed a three-rank design.** GI4's
listing and GI5's netlist are companions of **equal** standing: neither is the drill file, so when
they disagree there is no principled winner and picking one is the silent guess this series exists to
remove. `BoardNetlistEvidence.ApplyPlating` therefore takes the read **as the drill file itself left
it** — before any listing was applied — which is the only way to tell a declared value from a
contributed one.

### 6. Traps found while building it

* **The apology lives in two places, and only one of them is visible.** Replacing
  `DrillViaPairing.MapSpan`'s "no layer span was declared" note left the sentence still printed —
  the copy that reaches the user comes from `ExcellonReader`'s own `Diagnostics`, which
  `GerberImport` prints verbatim a few lines later. That copy is true about the drill FILE and false
  about the folder once a netlist in it has stated the span, so it is suppressed at the point of
  printing (matched as a prefix, so the two copies cannot drift into never matching) rather than
  removed from the reader that is right about itself.
* **A via is copper on a layer that is not a copper layer.** It sits on the DRILL layer and carries
  its landing copper layer separately, so the net-attach pass's "copper layers only" filter skipped
  it — and reported the coordinate whose pad had just BECOME that via as *"matched nothing"*. A via
  is now matched by its landing layer, and its net comes from the pad's own attribute first and the
  netlist's only where the artwork carried none, which is every via on a composited layer.
* **A companion disagreement RETRACTS a value, and patching only fills nulls.** `ApplyToolListing`
  writes plating onto the tools AND onto every hit. When the netlist then disagrees and the tool goes
  back to unstated, a fill-the-nulls pass leaves the hits still marked — two halves of one file
  disagreeing with each other. The hits are rebuilt from the drill file's own, against the final
  tool table.
* **The extent cross-check must be a PROPORTION, not zero-outside.**
  `ExcellonReader.CrossCheckExtents` demands that no hit fall outside the artwork, which is right for
  drill data. A netlist names fiducials and tooling holes that legitimately sit outside the copper's
  centreline extent, so the same rule would have rejected every real netlist. Ten per cent is far
  above what a board's edge features produce and orders of magnitude below what any resolution error
  does.
* **A netlist whose records sit at one point has no span**, so the ratio against the artwork's is a
  zero that reads like a catastrophic scale error rather than like the absence of a measurement. Said
  as an absence.
* **Recognition needed a header-plus-one-record rule.** Two feature records is the standalone
  minimum, for the reason `GerberDeclarationFile.MinimumKeywords` is two — but a
  `P  KEYWORD VALUE` header record is this format's own and nothing else writes one, so **one record
  beside a header is a stronger signature than two bare records**. Recognition runs after the artwork
  and drill tests (nothing here can take a file the drill test already claimed) and **before** the
  declaration test, because a specific signature must never be reachable only when a loose one
  happens to miss.
* **The record tail is read as an ordered TAG STREAM, not by exact column.** The format is
  column-oriented and the head (net name, reference, pin) is read that way — but the tail's fields
  are self-identifying and their exact columns are not honoured by every writer, while a coordinate
  read one column short is wrong by a factor of ten. The field ORDER is what disambiguates the two
  X/Y pairs in one record: the first is the location, the second is the feature's own width and
  height, from which this reader takes nothing (R-gi5-1 — the artwork is the sole source of every
  dimension).
* **The containment flattening tolerance is a hundredth of the match tolerance, not one DBU.** One
  nanometre cannot change a yes/no about a point that is matched to 2.54 µm, and on a curved trace it
  would flatten a millimetre-scale arc into thousands of segments to answer it.

### 7. One thing left undone on purpose, because doing it would have broken gate 1

**A drill file that declares its OWN blind or buried span still mints a via stackup entry spanning
the whole stack.** `SpanEndName` can narrow it and does — but only for a span the NETLIST supplied.
Narrowing the drill file's own would be strictly more correct and would change the `.ctech` of a set
with **no netlist in it**, which is the single thing R-gi5-2 promised not to do. It is a small,
self-contained piece of work for whoever picks it up: pass `read.Span` into `spanByDrillFile`
alongside the netlist's, and expect `tests/Ui.Tests` to need a new expectation rather than a fix.

### 8. What is deliberately NOT here

Everything in the brief's §7, unchanged: no connectivity model and no net extraction from this data
(`NetExtractor` derives nets from geometry and the two must not be conflated), blind/buried vias as a
first-class stackup concept, component placement or footprint hierarchy (`GerberImport`'s own note at
the site where footprint inference would go still stands), and any second netlist dialect — a second
dialect is a second reader and its own decision.

## Phase GI4 — COMPLETE (2026-09-07)

`docs/sonnet-briefs/brief-gi4-companion-declaration-files.md`, fourth of the GI series. A production
output set ships a plain-text parameter file stating the coordinate format the whole job was written
with, and a tool listing carrying the plating column a drill file most often omits. Both were
classified *"no Gerber or drill content in its head"*, skipped, and reported as skipped — in the same
run that said, in words, that the digit format had been **INFERRED**. Gated by
`tests/Ui.Tests/Gi4CompanionDeclarationTests.cs` (18 tests, one per gate plus the four the archive
needs).

### 1. The keyword set, and whether two was enough

`GerberDeclarationFile.Vocabulary` — **nine canonical keys behind 39 aliases**, and the aliases are
what make this work without naming anyone. Every key is normalized before lookup: uppercased, and
every run of non-alphanumeric characters collapsed to one hyphen, so `INTEGER_PLACES`,
`Integer Places` and `integer.places` are one key. That is the whole reason the table can be small
and still cover the sets in circulation — the same phrase is spelled four ways and means one thing.

| Canonical key | What it settles |
|---|---|
| `INTEGER-DIGITS` / `DECIMAL-DIGITS` | the digit split, one half each |
| `FORMAT` | both halves at once, as `3:4` or `3.4` |
| `UNITS` | inch or mm |
| `SUPPRESS-LEADING-ZEROS` / `SUPPRESS-TRAILING-ZEROS` | one flag per zero class |
| `ZERO-SUPPRESSION` | the single-word spelling of the same pair (`LEADING`/`TRAILING`/`NONE`) |
| `SCALE` | R-gi4-5's refusal |
| `DATA-TYPE` | R-gi4-6's scope — drill or artwork |

**Two was enough, and it is doing real work.** The false positives it stops are not hypothetical:
`UNITS`, `FORMAT` and `SCALE` are each single English words that appear as a bare `KEY VALUE` line in
unrelated settings files, and any one of them alone would have claimed such a file as a declaration.
Two recognised keys is the same caution `LooksLikeJobFile` already applies when it refuses to call
arbitrary JSON a job file. A one-keyword file classifies `Other` and is gate 1.

**A tool listing is recognised separately** — `GerberDeclarationForm.ToolListing`, on the same
`GerberFileKind.Declaration` — by ≥ 2 rows of *(tool number, decimal diameter)* of which at least one
carries a plating word. Two independent guards, and neither is spare: a bare integer is **not**
accepted as a diameter (these tables carry a hit count, which would otherwise read as one), and a
listing with no plating column is refused outright, because R-gi4-7 reads it for that column and
nothing else.

**One trap found while writing the row parser.** `NON PLATED` is written with a space in some of
these tables. Testing the plating word field by field sees `NON` (which matches nothing) followed by
`PLATED` (which means the opposite of what the row says), so the row parser normalizes the **whole
tail** of the row and tests that — where the separator collapse turns both spellings into one. A
field-by-field test would have marked every non-plated tool plated, silently, and the only visible
symptom would have been an EM run whose mounting holes short the stack.

### 2. How often the declaration agreed with the inference

**Unmeasurable in this repository, and that has to be said rather than answered with a number.**
There is no production output set in the tree: `testdata/pcb-samples` is board files, and every
Gerber fixture under `tests/` is hand-authored (L4e/L4f/L4g's own precedent — worth less than a real
set as a dialect test, costs nothing to redistribute, names no toolchain). Nothing in the repository
carries a companion parameter file at all — a `grep` for the keyword set finds exactly two files, the
reader and its gate.

So the honest form of the brief's §8.1 and §8.2: **the agreement rate is a question for whoever has
the real sets, and the import now answers it for them on every run.** That is what §3 below is for —
a disagreement between a declaration and a file that speaks for itself is reported by name, with both
values in one sentence, and is the only signal anyone will ever get that one of the two is stale.
What can be stated from here is what the fixtures prove: the inference and the declaration agree on a
file written at full coordinate width (the `CoordinateWidth` rung reproduces `3:4` from a 7-digit
word), and they disagree on the case the declaration exists for — a file with too few coordinate
words for the width inference, which defaults to `3:3` and lands every hole **ten times** too far out.

### 3. Where a declaration ranks, and the one place the ladder cannot say it

`DrillFormatEvidence.Declaration` sits where the brief puts it: directly below `Override`, above
`FormatComment`. **The ladder is one axis and this is two**, and the enum's own doc comment now says
so — a job-wide declaration is authoritative about the JOB, and a drill file's own `;FILE_FORMAT` or
`INCH`/`METRIC` line is authoritative about ITSELF. `ExcellonFormat.Resolve` therefore prefers the
file over its rung, and reports the disagreement rather than resolving it silently. Reading the enum
alone would tell you the opposite, which is why the note is on the enum member and not only here.

Resolution order, per unknown, after this phase:

| | Unit | Digits | Zero suppression |
|---|---|---|---|
| 1 | caller override | caller override | caller override |
| 2 | the file's own `INCH`/`METRIC`/`M71`/`M72` | the file's own `;FILE_FORMAT` / digit field | the file's own `LZ`/`TZ` |
| 3 | **the declaration** | the file's decimal-point coordinates | **the declaration** |
| 4 | the tool diameters | **the declaration** | full-width coordinates |
| 5 | defaulted | the coordinate width | defaulted |
| 6 | — | defaulted | — |

Two of those placements are decisions, not transcription. **Decimal-point coordinates outrank the
declaration** (row 3, digits column) because a file whose every coordinate carries a literal point has
answered the question itself — there is no digit split left to state. And **the coordinate-width
inference is below it** but still reported: when a declaration settles the digits and the file's own
words are a different width, the declaration is used *and the mismatch is printed*, because a
parameter file that has drifted from its drill data is exactly the set this phase exists to surface.

**Moved, and it is behaviour-identical:** the `found.ZeroOmission` branch now runs *before* the
full-width branch. It always effectively did — the full-width branch has always been guarded by
`found.ZeroOmission is null` — but with a third source between them the order had to be the order it
is decided in.

**One field added to `DrillFormatInference`: `ZeroSuppressionApplies`**, defaulting to `true` so every
existing construction reads as it did. Zero suppression is a THREE-way question and `GerberZeroOmission`
only has two values, so "the declaration says nothing is suppressed" had nowhere to live and would
have rendered as *"leading zeros suppressed"* over an evidence line saying the opposite — GI1's
R-gi1-1 failure exactly, in a new arm. `ToString` still tests `CoordinateWidth` by name as well, so
the case GI1 fixed keeps its own spelling.

### 4. The inversion (R-gi4-4), and why the conversion is where it is

`ExcellonFormat.ZeroOmissionFromSuppressionFlags` — **deliberately in the file whose header already
warns about the opposite conversion**, and immediately above `Resolve`. Three senses, and getting any
two of them confused is a board wrong by four orders of magnitude that parses perfectly:

| Source | Names | `SUPPRESS-LEADING-ZEROS YES` maps to |
|---|---|---|
| Gerber `%FS<L\|T>` | the zeros **omitted** | — |
| Excellon `LZ`/`TZ` | the zeros **kept** | — |
| a declaration's flags | the zeros **suppressed** | `GerberZeroOmission.Leading` |

So a declaration maps **straight through with no inversion**, and `ExcellonReader.ScanUnitsKeyword`
remains the only place in the codebase that inverts anything. Gate 3 asserts all three in one test and
finishes with the coordinate values: the word `12` under 3:3 is the format integer **12** (0.012 mm)
read the declaration's way and **120000** (120 mm) read Excellon's `LZ` way.

Both flags off is a real third answer and is carried as one (`ZeroSuppressionNone`), not as a
`Leading` nobody stated. **One flag off is not**: a file stating only that leading zeros are not
suppressed has said nothing about trailing ones, and the mapping returns "unstated" for that.

### 5. The tool listing: one column, and the question it cannot answer

`ExcellonReader.ApplyToolListing` runs **after** the format is settled and never before — matching is
by tool number *and* diameter, and a diameter cannot be compared until the drill file's own unit is
known. A row whose diameter disagrees beyond 0.5 % is reported with **both** numbers and its plating
is **not** taken: a row that disagrees about a tool's size may be describing a different tool. A tool
that already carries plating from the drill file's own `;TYPE=` section or `TA.AperFunction` keeps it,
and the disagreement is reported — the same rule as R-gi4-3, one level down.

**The case with no answer, and it is recorded rather than resolved:** GI1's `Plated` is a field on a
`StackupLayer`, and a drill file is one drawing layer. A listing that marks *some* of one file's tools
non-plated leaves that layer with no single answer. Marking the whole entry non-plated would delete
every real via on it; marking it plated is what it already was. So a mixed file keeps its unstated
(plated) entry, its individual holes carry the plating the listing gave them, and the import says in
words that the fix is to split the non-plated tools into their own drill file. A file whose *used*
tools are uniformly non-plated does mint a non-plated via entry — that is gate 7's second half, and
`Fill` and `WallThicknessDbu` go unstated with it, exactly as GI1/GI3 established.

`ExcellonReadResult` became a `record` for this, for its `with` expression and for nothing else. A
hand-written copy constructor over fifteen properties is a property somebody forgets to carry the
next time one is added; nothing compares two of these.

### 6. The archive: an offer, and the two shapes it takes

**Two new file kinds, not one.** The brief asks for `Declaration`; `Archive` came with it, because
R-gi4-10 is not implementable without it. An archive was classified `Other` with the `Why` string
`"not text"`, and the only way to find one again would have been to match on that string — which is
also the string the binary-drill-file advice keys on, so an archive was already inflating a message
about EIA-coded drill files. It is now named as what it is, stays in the skipped list (R-gi4-9), and
leaves that count.

**ZIP only, deliberately.** Recognition is the local-file-header signature, read from the head the
classifier already has (`PK\x03\x04` and its two siblings are ASCII-range, so they survive the UTF-8
decode unchanged and no second read is needed). Offering to look inside something circuitRF cannot
open would be worse than saying nothing.

**The trigger is R-gi4-11's, and it is the narrow one:** the offer is raised only when the chosen set
yields **no artwork of its own**. A set that already yields artwork imports from the files on disk and
never touches an archive beside it, even one holding the same board — and the gate asserts the
callback is never *called*, not merely that nothing was extracted. Reading both is how two versions of
one board get silently merged.

**No nested offer.** An archive inside an archive is not something to open on the strength of one
"yes", so the inner import is run with the offer callback null. One round of unpacking is the whole of
what was agreed to.

Everything lands in `ExtractedArchive`, which is `IDisposable` and deletes its whole tree — *"leave
nothing behind"* is a gate and is measured, not assumed: the two archive tests count the
`crf-gerber-zip-*` folders in the system temp directory before and after and require the list to be
identical. Entries that would land outside the temporary root are skipped (an archive is an untrusted
file like any other). The folder **inside** the archive with the most Gerber content is what gets
imported, ties going to the shallowest — an output set is routinely archived with its own folder
around it, and its top level is often a read-me and nothing else.

**Whether the archive path ever ran, and what it cost:** it ran only under the gate, on a two-file
fixture, where it is not separable from process noise. **No timing is recorded, and none should be** —
the repo's standing rule is to assert counters, not wall clock, and a number measured on a 400-byte
archive would be quoted later as if it meant something about a real set. What *is* recorded: it
unpacks the whole archive once, classifies each extracted folder, and imports one of them, so its cost
is one decompression plus one ordinary import.

Headless, the same question is a **refusal naming the flag that answers it** — `--open-archives`,
exactly as an unstated drill coordinate format already refuses with `--accept-inferred-drill-format`.
A dialog's question becomes a flag, never a guess.

### 7. Scoping, and the one boundary that costs nothing to honour

R-gi4-6 is enforced by **directory**, not by proximity in the file list: a declaration speaks for the
files of its own kind in its own folder, and `GerberImport` never reaches outside the list the user's
choice resolved to. Two parameter files in one folder that agree are a copy and the first is used; two
that disagree are a folder nobody can read a job format out of, and **none** of them is used — picking
one would be the silent guess this phase removes.

**An artwork-scoped declaration is recognised, reported, and never used.** That is not a gap: every
Gerber artwork file carries a mandatory `%FS`/`%MO` pair, so a declaration about the artwork is always
outranked by the file it describes (R-gi4-3). The message says exactly that, which is R-gi4-8's
"recognised and not used says why". A declaration that states **no** data type is applied to the drill
data — the only kind in a Gerber set that does not state its own format — and the report says so
rather than assuming it silently.

`GerberImportEntry.Survey` deliberately does **not** count declarations or archives. It answers one
question — would importing the folder produce different *layers* than importing this one file — and
neither is a layer. Counting them would raise R-L4h-3's prompt on a folder whose two answers produce
the same one-layer cell.

### 8. One collateral gate, and it is the one that should have caught this

`CliStructuredOutputTests.DiagnosticIds_AreTheCommittedSet_UniqueAndCaseDistinct` failed on the first
full run, exactly as designed: a diagnostic id is a permanent contract (R-aut1-8), so a new one has to
be recorded in that test's committed list rather than appearing silently. `convert.gerber.archive-not-opened`
is now in it. `tests/Firewall.Tests` is green throughout, which is what holds the no-Avalonia boundary
shut while `GerberDeclarationFile` and `GerberArchive` grow in `src/Design`.

**The two `SharedLibraryConcurrencyTests` failures are the ones GI3 already recorded, and they are
still not this phase's.** `WithoutTheCache_OneEditCostsFourFilesystemCallsPerReferencedComponent`
(expected 40, got 55) and `TheOnFocusRefresh_DoesNotReadTheReferencedLibrary_ButTheButtonDoes`
(expected 0, got 24) both assert an exact value of **`CellStat.Calls`, a process-global static
counter** reset and read across a parallel suite: any concurrently running test that stats a cell
inside that window inflates it. Attributed rather than chased, in the repo's own cost order —
`git status` shows nothing changed on `WorkspaceScanner`, `CellStat` or the shared-library path, and
all 25 tests in that class pass in isolation in 143 ms. An isolated pass separates "deterministic
break" from "load-dependent"; it does not prove the race absent, and whether it has genuinely
worsened is a question about the repo rather than about this change.

### 9. What gate 11 actually compares

"A set with no declaration file imports exactly as it does today" has no *today* to compare against
inside a test run, so it is asserted in the form that has one: the same set imported twice into two
parents, once plain and once with the whole declaration path present but **inert** — a companion
refused for its scale, plus one scoped to the other kind. The written `.clay` and `.ctech` must be
**byte-identical**. If GI4 changed anything about a set it cannot speak for, those two documents
differ. `ADrillDeclarationDoesNotAlterArtworkReading` is the same comparison from the other side.

## Phase GI3 — COMPLETE (2026-09-07)

`docs/sonnet-briefs/brief-gi3-substrate-fields.md`, third of the GI series. Every other field on the
Technology editor's Stackup tab carried guidance; the two a board import leaves blank — a conductor's
`σ` and a plated via's `Wall` — had no tooltip, no preset and, after a Gerber import, no value. GI3
answers both, puts the conductivity constants in one place, and adds the three structural checks that
ride along on the same tab. Gated by `tests/Ui.Tests/Gi3SubstrateFieldsTests.cs` (37 tests).

### 0. Two existing tests moved, and one that did not

* **`ExtractionRefusalTests.ASignalConductorWithZeroSigma_…`** asserts the cross-section refusal
  contains the literal `5.8e7`, and reading the number out of the table briefly turned it into
  `5.8e+7` — because the interchange notes format with `0.###e+0`. Restored to `0.###e0`, and the two
  spellings are **deliberately not unified**: this sentence is one the user reads back INTO the σ
  field, and a number someone retypes should not carry a `+` they then have to decide about.
* **`Gi2StackupSkeletonTests.AFreshSkeletonReportsOneStackupSummary_…`** counted THREE stackup
  problems on a fresh skeleton and named the third in its own comment as *"the drill layer's plated via
  has no wall thickness (GI3's field, unchanged here)"*. R-gi3-4 is that field, so the count is now
  two and the third's absence is asserted by name rather than inferred from a number.
* **`SharedLibraryConcurrencyTests.TheOnFocusRefresh_…` failed under full-suite load and passes
  alone — NOT this phase's.** Nothing here touches `WorkspaceScanner`, `CellStat` or the referenced
  library, and the assertion is `Assert.Equal(0, CellStat.Calls)` around a **process-global static
  counter** (`CellStat._calls`, reset and read across a parallel suite). Any concurrently running test
  that stats a cell in that window increments it. Recorded, not chased.

### 1. Where the conductivity constants were, and where they are now

The table is **`ConductorMaterials` in `src/Design/Layout/StackupDefaults.cs`** — silver 6.30e7,
copper 5.80e7, gold 4.10e7, aluminium 3.77e7, nickel 1.43e7 S/m at 20 °C. Element conductivities out
of any physics handbook; a laminate/dielectric table is refused permanently for the reason
`GerberStackupMapping`'s own header gives, and that file states the refusal so the next person does
not have to rediscover it.

**Consolidated out of six sites in three files** (a comment-stripped source scan over all three is
gate 2, so a seventh copy fails the build):

| Was | Now reads |
|---|---|
| `PcbStackupMapping.DefaultCopperConductivitySm` — `const 5.8e7` | `=> ConductorMaterials.Copper.SigmaSm` (still a `public` member, because R-L4d-7 is what the default *is* and every message names it by that; it is no longer a second copy of the value) |
| `StarterTechnologies.cs:86, :97` — `SigmaSm = 5.8e7` | `ConductorMaterials.Copper.SigmaSm` |
| `StarterTechnologies.cs:185, :201, :229, :247` — `SigmaSm = 4.1e7` | `ConductorMaterials.Gold.SigmaSm` |
| `CrossSectionExtractor.cs:370-377` — two refusal strings quoting `5.8e7`/`4.1e7` as advice | one `ConductivityHint` built from the table |

That last one was not in the brief's list and is the one worth naming: it is **advice text**, so a copy
there does not fail a run, it silently tells the user a number the editor's own preset list may have
stopped agreeing with. Prose copies are the ones nothing catches.

**Two copies were deliberately NOT consolidated, and neither is reachable from here:**

* **`src/WBond/Materials.cs`** (`WireMaterials.Gold/Aluminium/Copper/Silver`) repeats four of these
  numbers. `CircuitRF.WBond` references only `CircuitRF.Diagnostics` — not `CircuitRF.Design` — so it
  cannot read the table without a new project reference, and it should not: a `WireMaterial` carries a
  temperature coefficient and a density as well, and is evaluated at an **operating** temperature
  (85 °C by default, 22-25 % below the 20 °C figure). Two tables, two purposes, and the shared subset
  agrees. Do not "unify" them by pulling Design into WBond's reference graph.
* **`src/Core/Devices/ComponentModelFactory.DefaultSubstrateSigmaSPerM`** is 5.8e7 for the same metal.
  `src/Design` references `src/Core`, so the dependency runs the wrong way — the table cannot move to
  Core without dragging the stackup vocabulary with it, and Core is the CIRCUIT design layer that
  knows nothing about stackups by design. Left alone, recorded here so the next reader does not
  "find" it and wire something backwards.

### 2. The total-height readout: what it found on the stackups already in the tree

`Stackup.TotalThicknessDbu` sums Conductor and Dielectric entries only (a via has no z band of its own
— `PlanarExtractor.BuildStack` skips them and `TechValidation` asks no thickness of one). It is
`[JsonIgnore]`d for `DrcRule.NeedsSecondRegion`'s reason: get-only properties serialize by default, and
a derived total in every `.ctech` is noise that looks authoritative.

**No shipped technology is wrong, but the five of them do not agree on what their own file name
measures** — which is exactly the ambiguity the readout exists to surface:

| Technology | Σ Conductor + Dielectric | What the file name names |
|---|---|---|
| `pcb-2layer_FR-4_70mil_1oz` | 1.848 mm (72.76 mil) | the **dielectric**: 1.778 mm = 70.00 mil |
| `pcb-2layer_RO4350B_20mil_1oz` | 0.578 mm (22.76 mil) | the **dielectric**: 0.508 mm = 20.00 mil |
| `pcb-2layer_RO4350B_30mil_1oz` | 0.832 mm (32.76 mil) | the **dielectric**: 0.762 mm = 30.00 mil |
| `pcb-4layer_FR-4_62mil_1oz` | 1.5782 mm (62.13 mil) | the **total**: 62 mil, to 0.13 mil |
| `mmic-GaAs_2LM_100um` | 112 µm | the **substrate**: 100 µm of GaAs |

The three two-layer boards name their dielectric; the four-layer board names its overall thickness; the
MMIC names its substrate. Every stackup adds up to what its own rows say — nothing is mis-transcribed
— so **nothing was adjusted**, per the brief's instruction to report rather than quietly fix. None of
the five carries a `BoardThicknessDbu`, so none of them raises the R-gi3-7 disagreement flag either.
The reason to write it down is that a reader comparing "70mil" against a 72.76 mil readout will think
one of them is a bug, and neither is.

**`Stackup.BoardThicknessDbu`** is the additive nullable field the job file's `BoardThickness` is
carried onto. It is set in **`GerberImport`, not in `GerberStackupMapping`** — deliberately: the
mapping returns a null stackup on a drill-only set and on a job file whose stackup declares nothing
electrical, and the board's thickness is a fact about the BOARD, not about whether rows were built for
it. Nothing derives geometry from it; the stack a solver sees is still built from the entries alone.

**The `.kicad_pcb` path had the same gap and now does the same thing** (owner's ask, after the phase
was otherwise complete — the brief scopes GI3 to the Gerber import and lists neither
`PcbStackupMapping` nor `PcbImport` in its Touches). `PcbStackupMapping.Build` took an
`overallThicknessMm`, named it in one sentence and dropped it; it now sets `BoardThicknessDbu` on the
stackup it BUILDS.

**That placement is the one real difference between the two importers.** Gerber mints its own
technology, so the fact is written onto `tech.Stackup` and survives every branch where no rows were
built. A board import never replaces a stackup that is already there — `ApplyImportToTechnology`
assigns `clone.Stackup = imported` only when the destination declares none — so the thickness must
travel WITH the rows and be refused with them. A board thickness applied to a technology whose stackup
describes a different board would be worse than dropping it.

The disagreement tolerance is **1 % of the stated board thickness, floored at 1 µm**, and is stated in
`TechEditorViewModel.StackHeightTolerance`'s own doc comment: the percentage is what makes one rule
work across a 100 µm die and a 1.6 mm board, the floor is what stops a rounding difference on a thin
stack reading as a discrepancy. It is a **transcription** check, not a fabrication-tolerance one — a
real board's thickness tolerance is far wider, and a stackup inside it still adds up.

### 3. Duplicate stackup names: none existed, and one was about to

**There are exactly five `.ctech` files in the repository** — the five shipped technologies under
`src/Design/resources/technologies/` — and **not one of them carries a duplicate stackup entry name**.
There are no `.ctech` fixtures under `testdata/`, `tests/` or `docs/`; every test that needs a
technology builds one in C#. So nothing was load-bearing, nothing had to be renamed, and the new check
is silent on everything the application ships. A test pins that (`NoShippedTechnologyHasADuplicate…`),
because a shipped file that trips a validator the product added is a self-inflicted wound.

**The one duplicate that DID exist was in the editor's own Add button.** `AddStackupLayer` wrote a flat
`$"New {kind}"`, so two clicks on "＋ Conductor" produced two entries called "New Conductor" — which,
the moment R-gi3-8 landed, is a reported problem the editor inflicts on itself the first time anyone
uses it. Now numbered exactly as `NextFreeDrcRuleName` already numbers a new DRC rule. **This is the
shape of the requirement worth remembering: adding a uniqueness check means auditing every path that
MINTS a name, not just the files that hold them.**

Why the check matters at all: `SpanFromLayer`, `SpanToLayer` and `PresentWithLayer` all resolve an
entry **by name**, and `TechValidation` builds `conductorNames` as a `HashSet<string>`. Two conductors
sharing a name collapse to one, every reference to that name silently resolves to the first, and the
via-span check *passes* — which is why gate 7 asserts that a via spanning the ambiguous name produces
**no second message**. The cause is reported once naming every entry that carries the name; the
consequence is not reported at all.

### 4. Via rows are outside the z order — a presentation change, and only that

`Stackup.Layers` is documented "Ordered TOP to BOTTOM" and the editor bound it directly, so a Via entry
— which has no position in that order — rendered as a row in the middle of the stack, and a via added
before the conductors rendered **above the top copper**, reading as a layer sitting over the board.

`ApplyStackupFilter` now projects the substrate entries in the model's own order and then the vias as
their own labelled group. **`Stackup.Layers` itself is never touched**, and gate 8 asserts the
Conductor/Dielectric sequence is identical before and after: that order IS z, and a reversed stack
simulates cleanly and answers a different question (L4d's R-L4d-5, restated by `GerberStackupMapping`'s
R-L4g-10). The group header is set by the owner as it builds the filtered list, and cleared on **every**
row first — a header left set on a row the filter excluded reappears in the wrong place the moment the
filter is cleared.

**`MoveStackupLayer` needed two changes, not one.** R-gi3-10 asks only that the control be disabled on
a via row (`CanMove`, bound to both buttons, and refused in the method as well — a disabled button is
not an enforcement point). But the *same* premise makes the old unconditional swap wrong for substrate
rows too: with `[Conductor, Via, Conductor]` in the list, moving the second conductor up swapped it
with the **via**, changing the reading order and not one physical fact — and under the new grouping it
would have appeared to do nothing at all. A substrate row now swaps with the next **substrate** entry,
stepping over any vias between them; the vias' own indices do not move, which is correct precisely
because those indices carry no meaning.

### 5. The wall thickness: the default existed everywhere but where it was needed

Every shipped PCB technology writes `WallThicknessDbu: 25000` and `StarterTechnologies` writes
`Um(25)`, while a Gerber import minted its via entries with **no wall thickness at all** — so the one
document in the product *guaranteed* to need the field was the only technology that failed the
product's own validator on a field with a known answer (`"Via stackup layer … is Plated with no wall
thickness."`). Now defaulted through `ViaDefaults.PlatedWallThicknessUm`, which the five shipped
documents' own value also reads from.

**Kept in MICRONS, not DBU.** An import's destination resolution is a parameter (`dbuPerMicron`), and a
`25000` constant would be silently correct at the default and silently wrong at every other resolution
— the same class of bug as reading a sweep mark without its scale. `ViaDefaults.PlatedWallThicknessDbu(int)`
converts at the point of use.

**The `.kicad_pcb` path was QUIET rather than right, and now states both** (same owner's ask).
`PcbViaSpanMapping` minted its via entries with `Fill` unstated as well as `WallThicknessDbu`, and the
validator's rule only fires on `Fill == ViaFillKind.Plated` — which is the only reason a board import
never reported the problem a Gerber import reported on every run. An entry is minted there ONLY for a
span that resolved to two DIFFERENT conductor entries, which is to say only for interconnect, and
interconnect is plated by definition; leaving the fill model unstated was an omission that happened to
dodge a diagnostic.

**The two fields have to be written together, and that is the trap worth recording.** `Fill = Plated`
alone engages the wall-thickness rule and hands the user back the exact problem R-gi3-4 removed — so
"state the fill model" is not a one-line change, and a future reader tidying one of them away
reintroduces it. `PcbViaSpanMapping.Build` grew a `dbuPerMicron` parameter for this, for
`ViaDefaults`'s own reason: the constant is in microns and a DBU literal is silently wrong at any
resolution but the default.

Named as a default **once for the whole import**, not once per drill file: it is one fact about one
process, and the per-file lines are already the busiest part of that report. A **non-plated** entry
still gets no wall thickness and is not counted — wall thickness is a property of metal, which is the
same rule that leaves `Fill` unstated on a hole that is not a conductor (GI1 R-gi1-2).

The tooltip answers the question actually being asked, out of `TechModel.cs`'s own `ViaFillKind`
documentation: it is a **plating** thickness, not the hole radius; 20-25 µm is typical; **above roughly
1 GHz it barely matters**, because a wall a few µm thick is already many skin depths; for **thermal**
it is a direct multiplier on conductive cross-section. Someone who reads that stops worrying about
getting it exactly right for an S-parameter run, which is the useful outcome.

## Phase GI2 — COMPLETE (2026-09-07)

`docs/sonnet-briefs/brief-gi2-stackup-skeleton.md`, second of the GI series. An import that resolved
six copper layers, worked out their order, bound each to a drawing layer and reported all of it — and
then wrote a stackup holding two via entries and nothing else. GI2 emits the STRUCTURE the import had
already computed and still refuses every VALUE that describes the substrate. Gated by
`tests/Ui.Tests/Gi2StackupSkeletonTests.cs` (16 tests).

### What a skeleton saves, in numbers, on the six-layer set

**11 rows created against 21 numbers still required.** The created half is 6 `Conductor` entries — each
named after its own drawing layer and bound to it, in the resolved top-to-bottom order — 5 positionally
named `Dielectric` entries between them, and the drill layer's two span ends, which now name real
conductors instead of nothing. The required half is 11 thicknesses, 5 εᵣ and 5 tanδ, every one of them
a fabrication fact that no Gerber file states.

That ratio is the honest measure of the phase: it does not shorten the list of numbers anyone has to
type by one, and it was never going to — what it removes is having to hand-author eleven rows **in the
right order with the right bindings** first, reproducing a result the importer already had.

### `Epsr = 0` survived the tree, but it exposed a THIRD consumer the brief did not list

Zero, not `StackupLayer.Epsr`'s own C# default of `1.0`, because 1.0 is **air** — a perfectly valid,
entirely simulatable substrate. A skeleton shipping 1.0 would be worse than the empty stackup it
replaces, because it would run to completion and answer a different question. Zero is already outside
`TechValidation`'s `Epsr < 1` and outside both extractors' `Epsr >= 1` guards, so it is the spelling of
"unset" this codebase already treats as unusable.

Every consumer of `StackupLayer.Epsr` was checked. Nothing treated zero as air and nothing divided by
it, but the survey turned up two things worth carrying:

* **`PlanarExtractor` refused for the WRONG REASON, and that had to be fixed here.** With every
  thickness zero every band sits at z = 0, so the first check to notice was the slab-height one, which
  answered *"the signal conductor sits at or below the ground plane — either mark that conductor as a
  ground reference, or check the stackup order"*. Every word of that is a wrong diagnosis of a stack
  whose order is fine and whose thicknesses were simply never entered. A stackup whose **every**
  non-via entry is zero thick is now refused at the top of `Extract`, before any geometry reasoning can
  reach a misleading conclusion about it. Deliberately `All`, not `Any` — a partly filled stackup is a
  different state with its own per-layer refusals and this must not widen into them.
  `CrossSectionExtractor` needed nothing: its `ValidateStack` already names the zero-thickness layer.
  (On the six-layer set the cross-section kernel refuses on multi-level geometry first, which is
  correct and unrelated — a six-layer board is not one cross-section whatever its substrate says.)

* **`SubstrateResolver` — the microstrip path — was a NEW silent-garbage route that this phase itself
  opened.** It is the third consumer of a stackup and it had no εᵣ check at all, because until GI2
  nothing could hand it an unset one: `Epsr` read as 1.0, which is air and computes. Its `hDbu <= 0`
  guard catches a *fresh* skeleton, but a stackup whose thicknesses have been filled in and whose
  permittivities have not reaches Kirschning-Jansen with εᵣ = 0, which returns a number. It now refuses
  `er < 1` where the value is still attributable to a layer. **This is the finding worth carrying
  forward**: the risk of the zero spelling is not the fresh document, which everything refuses, but the
  half-edited one.

The remaining consumers are inert on zero: `PcbWriter` writes `epsilon_r 0` to an exported
`.kicad_pcb`, which is honest; `PlanarExtractor.UngroundedRefusal` prints it inside a refusal string;
`TechnologyMerge`, `PatternedDielectric` and `StackupLayerRowViewModel` copy or render it.

### The validator's message count: 18 → 3 (measured, not estimated)

A skeleton has conductors, so `stackupIsSubstrateless` goes false and every per-row check re-engages at
once. Measured directly by disabling the new summary and running `TechValidation.Analyze` on a freshly
imported six-layer skeleton: **18 Stackup problems** — 11 "non-positive thickness", 5 "εr < 1", the
ground-reference one and the via wall-thickness one. That is the same 22-message wall the
substrateless rule was written to stop, arriving by a different door, and it would have made the
skeleton a regression.

With the summary: **3**, and each is a different fact — the skeleton summary, the missing ground
reference (a real decision no artwork file can make, deliberately left on its own), and GI3's via wall
thickness. Before GI2 the same set produced 1, but that one said the stackup was missing and the
stackup was in fact missing.

**The rule that makes it progressive is UNSET versus WRONG, and it is keyed on exact zero.** A
thickness of `0` or an `Epsr` of `0` is unset and is counted in the summary; a negative thickness, or a
permittivity someone typed as 0.5, is a value a person entered and keeps its own row. Filling in one
dielectric drops it out of both counts and raises nothing new.

### Two things the wiring made obvious only once it was written

**The skeleton needs the technology, not just the layer keys.** `GerberStackupMapping.Build` took
`IReadOnlyList<LayerKey>` and had no way to NAME a conductor entry after the drawing layer it binds.
The names live only in the `Technology` that `BuildTechnology` returns two lines earlier, so the call
now passes them (and the mask/paste/legend names) in. Naming a conductor "Conductor 3" beside a layer
table calling it "Inner 2" would have been two documents rather than one.

**Soldermask is genuinely a dielectric in the physical stack, which is exactly why leaving it out has
to be SAID.** Its artwork states neither a thickness nor a permittivity, and adding an entry for it
silently changes the conductor-to-conductor geometry a solver sees. One message names the layers and
says they are artwork only.

### What the artwork turned out to state about thickness — nothing

Left on the table, honestly: **nothing.** The only thickness-shaped number anywhere in a no-job-file
set is an Excellon tool diameter, which is a hole size. A job file's `BoardThickness` is still reported
when one is present and is deliberately **not** distributed across the dielectrics — dividing one
overall height by five unknown layers is a substrate invented under another name. Copper weight
sometimes appears inside a job file stackup entry's material or notes text; reading it would be
inferring a value from a name, which is the one thing `GerberStackupMapping`'s header forbids outright.


## Phase GI1 — COMPLETE (2026-09-07)

`docs/sonnet-briefs/brief-gi1-report-says-less-than-it-knows.md`, first of the GI series
(`brief-gi-series.md`). Three facts a Gerber import establishes and then contradicts, discards or
undersells. **No parsing changed** — every number was already right; what changed is what the import
says about them and, in one case, what it builds from them. Gated by
`tests/Ui.Tests/Gi1ImportSaysWhatItKnowsTests.cs` (15 tests).

### The headline/evidence contradiction was a two-way render of a three-way question

`DrillFormatInference.ToString()` switched on `ZeroOmission`, which carries a **nominal** `Leading` on
both rungs that mean "the question does not arise" — precisely because the value is unused there. So a
file settled by `CoordinateWidth` printed "leading zeros suppressed" one sentence before its own
evidence line read "Zero suppression: none". The import prints the two together
(`GerberImport`'s `"{file}: {read.Format}. {evidence}"`), so the message argued with itself.

Fixed by keying on `ZeroOmissionEvidence`. The replacement wording deliberately contains **no form of
the word "suppress"** — "full-width coordinates (neither zero convention applies)" — because the gate
that stops this regressing is a bare `DoesNotContain("suppress")` over the rendered headline, and a
phrasing that needs a cleverer assertion is a phrasing that will be broken again.

**Answering the brief's §6.1** — whether any other `ToString()` in the interchange readers renders
fewer cases than its object carries: no other one was found. This one was found by reading a real
import log, not by a test, and that remains the only route to this class of defect.

### `Plated` had to be a new field, because `ViaFillKind` has no non-conductor

`GerberImport` settled each drill file's plating (`read.Plated ?? PlatingFromFileName`), used it to
choose the drawing layer's NAME, and then minted the via stackup entry with `Fill = ViaFillKind.Plated`
hard-coded 230 lines later. The fact was in scope and discarded.

It could not simply be pushed into `Fill`: **both `ViaFillKind` values are metal.** `Plated` is a hollow
barrel with a wall and `Solid` is a filled one; neither can express "this hole is not a conductor",
which is what a non-plated hole is. So `StackupLayer.Plated` is a new nullable bool — **null means
plated**, so every technology authored before it reads bit-identically, and `false` is written only
when a file said so.

**`PlanarExtractor.BuildViaBinding` is the single exclusion point, and that mattered more than
expected.** It is the sole route from a drawing layer to a via entry, so filtering there covers the
point-via branch (a `ViaShape`) and MIM-1's region branch at once, and cannot be bypassed by a third
kind of via artwork arriving later. `CrossSectionExtractor` needed nothing — it already ignores every
via entry outright, since a 2D cross-section has no vias — so the brief's §6.2 "before and after"
count applies to the planar path only.

### A rout-only file was the worse half of the same bug, and the safe default here inverts

A file with slots and no hits minted a full `Plated` via entry spanning topmost to bottommost
conductor. The consequence is larger than it first reads: **a slot is a drawn REGION on the drill
layer**, and the extractor's region branch builds a vertical conductor out of every region on a
via-bound layer — so a board outline and its cutouts became metal shorting the whole stack.

So a rout-only file defaults to **non**-plated, which is the opposite of the "unstated means plated"
rule everywhere else. That asymmetry is deliberate and is the only safe direction: unstated-means-plated
turns cutouts into metal, and a castellated edge that really is plated declares itself and keeps what it
declared. The entry itself stays — it is the drawing-layer marker that makes a bare opening re-export as
a routed feature rather than as copper.

### The numeric-prefix rung needed a second condition that the brief did not anticipate

R-gi1-4 as briefed required every conductor file to yield a distinct number. That is not enough: names
like `top_1oz` and `inner_35um` both yield one, and ordering by it is nonsense. The implemented rule
adds that **the digit run must start at the same character index in every conductor name** — which is
what separates a set that is systematically numbered from one whose names merely contain digits, while
still allowing the fixed word before the number that these sets routinely carry.

**Answering the brief's §6.3** — how often the prefix was available, and whether it ever disagreed with
the side/inner ordering it replaced: unmeasured against real sets, because none is committed to this
repository. On the hand-authored fixtures it is decisive exactly where it was meant to be — the
alphabetical tiebreak it replaces sorts `l10` before `l2`, and the gate uses that pair specifically so
a pass cannot be the old ordering agreeing by luck. **Whoever next imports a real multi-layer set
should record the answer here.**

### The validator had to learn the same distinction, or the fix traded one bug for two messages

A non-plated via entry — and every rout-only layer, which reaches this as `Plated == false` for
exactly that reason — has no span, because it connects nothing. `TechValidation` reported that as two
"spans an unknown conductor layer" problems. Invisible on a set with no job file (the
`stackupIsSubstrateless` shortcut suppresses the whole via block) and immediate on a set that ships
both a job file and a rout file. The span and wall-thickness checks now skip a non-plated entry; its
drawing-layer binding and thickness rule still apply, because those are not questions about metal.

### Two things this phase exposed and deliberately did not fix

1. **A set mixing DECLARED and GUESSED conductors orders every declared one before every guessed
   one**, because `OrderBy(c => c.CopperIndex ?? SideRank(c.Side))` ranks a real copper index (a small
   int) against `SideRank`'s sentinels (`int.MaxValue / 2`, `int.MaxValue - 1`). A declared bottom
   layer therefore lands above an unidentified inner one. Pre-existing, unrelated to the prefix rung,
   and found only because a fixture happened to mix the two. Recorded rather than changed: it is a
   ranking question of its own and GI1's remit was the guessed-only path.
2. **A `.ctech` from a set with no job file has no conductor stackup entries at all**, so the stack
   order the import worked out cannot be read back off the technology it wrote — the gate reads it out
   of the reported message instead. That is GI2's whole subject and is not worked around here.

### One thing added beyond the brief, on purpose

`StackupLayer.Plated` gets a **checkbox in the Technology editor's via row**. The brief did not ask for
a control, but the import's own message tells the reader to tick it — and a model field that nothing in
the application can show or edit means a wrong inference is uncorrectable outside a text editor. It
writes `null` rather than `true` when ticked, so a technology that never had an opinion round-trips
byte-for-byte instead of gaining a field. The rest of the Technology editor's via and conductor fields
are GI3's.


## RC-7 — the commit and the history browser (2026-09-06)

`brief-revision-control-7-commit-and-history.md`; `docs/design/revision-control.md` §5.2, §5.5's first
row, §6.1, §6.3, §6.4, §5.3d. Five new types in `src/Design/Revision/` — `WorkspaceCommit`,
`CommitMessage`, `RestoreProvenance`, `HistoryBrowser`, `DocumentClash` — plus `HistoryMessages`.
Gated by `tests/Ui.Tests/Revision/CommitAndHistoryTests.cs` (20 tests).

### The restored-from line cannot be derived, which is why a restore writes something down

R-rc7-6 requires the commit after a restore to name what it was restored from, and the obvious
implementation — compare the new tree against every restore point and report a match — **is wrong in a
way that only shows up in use**. It finds a match for the first commit made straight after a restore
and none at all once one more edit has landed on top, so the line appears or vanishes depending on how
much work happened in between. That is the least predictable behaviour available, and a designer would
reasonably conclude the line means something it does not.

The deeper reason it cannot be derived: **from the content alone, a commit that went back to Tuesday is
indistinguishable from a commit that undid three days of work by hand.** Those are different decisions
and the history exists to tell them apart. So `WorkspaceRestore` writes `RestoreProvenance` —
`.git/circuitrf/restored-from.json`, beside the restore marker and the thinning journal — and
`WorkspaceCommit` reads it, reports it, and clears it. The **last** restore wins: restore to A, then to
B, then commit, and the content is B's, so naming A would be a plain untruth in a perfectly well-formed
entry.

Not in the `.cws`, deliberately: a designer's workspace file must not gain a field that changes on every
restore, and one that travelled with a Save As copy would put a sentence about this machine's history
into somebody else's workspace.

### `update-ref HEAD` is why nothing here needs a reference name

R-rc7-16 withdrew the variant, and the mechanism that makes "no branch, ever" easy rather than
effortful is one plumbing call: `update-ref HEAD <new> <old>` follows the symbolic reference the
workspace is already on and moves **that**. Nothing in `WorkspaceCommit` names a reference, creates one,
or needs to know what the current one is called — so R-rc7-3's rule is not merely enforced by a source
scan, there is genuinely nothing for a name to be needed for. The compare-and-swap third argument is
what makes two processes committing at once resolve as one winner and one retry rather than a lost
commit.

### The commit had to write the SHARED index, and a checkpoint still must not — the reason is HEAD

**Found by a gate, not by inspection**, and it is the finding most likely to bite the next person.

`GitCheckpoint` builds through a private index for §4.6's reasons, and leaves the repository's shared
index alone. That is exactly right for a checkpoint, whose reference sits outside `HEAD`: with `HEAD`
unborn and the index empty, `git status` in the workspace shows every design file as untracked, which is
the truth.

**The moment RC-7 puts a commit on `HEAD`, the same empty index becomes a lie.** The index is defined
relative to `HEAD`, so git's own porcelain reads every file as staged-for-deletion *and* untracked at
once: `git status` lists the whole design as deleted, and `git checkout` refuses to do anything at all
because untracked files would be overwritten. That is not "an ordinary git repository, readable and
repairable by every existing tool" (§4.1) — it is a repository that looks broken to the single tool the
escape hatch is a promise about.

So `WorkspaceCommit` ends with a best-effort `read-tree <tree>`, which writes the index from the tree and
touches no file in the working tree. **Only the commit does this.** A checkpoint doing the same would
re-introduce exactly the index contention §5.2b removed, for a state the index has nothing to say about.

The gate that caught it is `AClashOffersTwoNamedVersionsAndKeepsOneWhole`, whose fixture needs raw git to
create a second line of work — and could not, because `checkout` refused. A test that only drove
circuitRF's own paths would never have found it.

### RC-6's "has this workspace a history" was one question and is now two

`RevisionSwitch.ExistingRepository` decided whether to record an off/on transition by counting restore
points. A workspace whose designer has only ever kept **versions** has none — so turning recording off
recorded no transition, the off period had no ends, and RC-7's browser then rendered it as an ordinary
quiet interval between two versions. That is precisely the false belief §5.7 claims does not happen,
arrived at from the other side. It now also asks whether the line of work holds anything.

### Two histories, two panels, and the `.axaml` template RC-5 never had

R-rc7-9's separation is mechanical rather than a rendering choice — checkpoints live on references in
circuitRF's own namespace and versions on the line of work, and neither reader walks the other — so the
gate asserting it is cheap and worth having anyway.

Building the second panel turned up that **`RestorePointsTool` had no `DataTemplate` in `App.axaml` at
all**: `ViewLocator.Match` requires a `ViewModelBase` and a Dock `Tool` is not one, so RC-5's panel
resolved to nothing. Both are registered now. Anything deriving from `Dock.Model.Mvvm.Controls.Tool`
needs its row there; the convention-based locator does not cover them.

### Clash resolution reads the index, not the working tree

`.gitattributes` marks the five document types `-merge`, so git writes **no conflict markers** for them —
there is nothing in the working tree to find. The two versions live in the index's unmerged entries,
stage 2 and stage 3, and `ls-files -u` is the only thing that reports them. An implementation that
scanned the files for markers would find nothing and report all-clear on every clash it exists for.

### What was measured, and what is deliberately not asserted

A commit into the 11.56 MB stand-in board of RC-3's measurement costs the same as a checkpoint of the
same tree — the object write is identical; only the parent and the reference differ. No timing gate was
added (R-rc0-8).


## RC-3 — the git substrate (2026-09-06)

`src/Design/Revision/` (`brief-revision-control-3-git-substrate.md`; `docs/design/revision-control.md`
§2.4, §3.2, §4, §5.2b, §5.6a, §6.1, §8.1, §8.1a). Nine types, one `src/Cli` verb, no GUI.

### The measurement, re-run on this machine class — including the no-`gc` pass

Gate 16, R-rc0-9. The real 28.4 MB board §2 measured is not in this repository, so this is a
**stand-in of the same shape**: 11.56 MB, 657,246 lines, 3,284 shapes (1,928 `Poly`, 1,172 `Path`,
113 `Rect`, 71 `Circle`), `long` DBU coordinates, indented JSON. Identical edit sequence: 20
additions, 20 mid-file polygon drags, 5 mid-file deletions.

| | plain, `gc` each step | plain, **no `gc`** | gzipped, `gc` each step |
|---|---|---|---|
| initial commit | 2,813 KiB | 3,245 KiB | 2,785 KiB |
| 20 **additions** | **921 B/commit** | — | 5,017 B/commit (5.4×) |
| 20 mid-file **drags** | **1,945 B/commit** | — | **1,154,508 B/commit (594×)** |
| 5 mid-file **deletions** | **1,024 B/commit** | — | 1,631,846 B/commit (**1,594×**) |
| loose objects at the end | 0 | **138 objects, 148,560 KiB** | 0 |
| final `.git` | 2,874 KiB | 148,147 KiB → **5,660 KiB after one `gc`** | 33,400 KiB (**11.6×**) |

**§2.4's premise reconfirmed, and it is worse here than the architecture measured.** 45 commits with
no `gc` leave **148 MB against a 5.7 MB packed repository — a 26× overhang**, where §2.4 measured 10×.
And it is **138 loose objects**, which is 2% of git's own `gc.auto` trigger of 6,700: git would never
once have decided to do anything about it. That is R-rc3-13's whole case, and the number moves in the
direction that makes it stronger rather than weaker.

**§3.2's gzip trap reproduced almost exactly**, including the part that makes it a trap: the
**addition** row is 5.4× and looks almost respectable, because deflate resynchronises after an append,
so an append-only test reports a false pass. The mid-file rows are 594× and 1,594× against §3.2's
~508× and ~1,600×. The final packed ratio is 11.6× here against §3.2's 14.5× — lower only because a
synthetic board's random coordinates compress differently from a real one's.

**Nothing is asserted from any of this** (R-rc0-8). The harness is a scratch script, not a
`Category=Benchmark` test.

### The version floor is 2.9.0, and `safe.directory` is not what sets it

R-rc3-3a asks for the requirement to be recorded so the floor can later be lowered deliberately.
**It is set by `core.hooksPath` (git 2.9, 2016)** — R-rc3-7a's hook bypass. Nothing else reaches
that high: the checkpoint is plumbing (`write-tree`, `commit-tree`, `update-ref`, `GIT_INDEX_FILE`),
the pack trigger is `count-objects -v`, and `:(exclude)` pathspec magic is 1.9.

**`safe.directory` looks like it should raise the floor to 2.35.2 and does not.** The ownership check
arrived in 2.35.2 and the backports 2.30.3/2.31.2/2.32.1/2.33.2/2.34.2, and **every git that enforces
it also accepts the `-c safe.directory=` answer** — so a git old enough to lack the option is old
enough to lack the check and needs no answer. R-rc3-4's row for it therefore covers a version band
that may be empty; it is kept because the cost of keeping it is a sentence and the cost of being wrong
is git's own wording in front of an RF designer.

`git init --initial-branch` (2.28) is deliberately **not** used, for the same reason: it would raise
the floor for a name no user-visible string in RC-1…RC-6 ever shows.

### Two bugs the gates found that a path-string comparison would have shipped

**1. `IsRepositoryRoot` cannot be a string comparison, and the symptom is "the repository I just
created is absent".** `git rev-parse --show-toplevel` prints git's own **symlink-resolved** view;
.NET's `Path.GetFullPath` resolves no links. On macOS `Path.GetTempPath()` is `/var/folders/…` and git
answers `/private/var/folders/…`; the same gap opens through any symlinked home directory or network
share, on any platform. `circuitrf history checkpoint --create-repository` created a repository and
then refused, saying there was none. **The fix is to stop comparing paths at all**: `rev-parse
--show-prefix` is empty exactly at the top of the work tree, which is the question, and git answers it
in its own coordinates.

**2. The stale-lock row cannot be recognised by a path prefix either, for the same reason.** Git names
the lock file in its own resolved coordinates, so `candidate.StartsWith(workspaceRoot)` silently stops
matching and the row degrades to `revision.git.unrecognised` — raw git output in front of a designer,
which is the one thing R-rc3-4 exists to prevent. `GitFailures.FindLockPath` now requires the token to
end in `.lock`, to name a file that **exists**, and to sit inside a directory named `.git`. The last
condition is what keeps "a path in a message" from being a reason to go looking anywhere else on disk.

### `--prune` is in exactly one file, and gate 6 had to be restated to stay true

Gate 6 (written against rev 2) says *no code path anywhere passes `--prune`*. **rev 5's §5.6a then
added the reclaim operation, which by definition prunes with an immediate expiry.** Both cannot be
literally true, so the checkable form is the one `PruneAppearsOnlyInTheReclaimOperationAndNothingInTheProductCallsIt`
asserts: `--prune` appears in `GitReclaim.cs` and nowhere else, **nothing in the product calls
`GitReclaim`**, and `GitPacking`'s own `gc` arguments are read directly and contain no prune.

**And reclaim cannot use a bare `git prune`.** That removes only **loose** objects, so a thinned state
that had already been through one pack would be permanently un-reclaimable — the control that exists to
answer *"where did the disk go"* would answer *"nowhere"*. It runs `git gc --prune=now`, whose
command-line expiry overrides the repository's own `gc.pruneExpire = never` for that one invocation and
nothing else (verified directly).

**The back-dated-object gate held.** An unreachable commit whose objects were aged 40 days survives
`GitPacking.Pack` with the three `never` rows in place — which is R-rc3-16's whole guarantee and the
one rev 2 of the architecture got wrong by measuring the tidied state.

### The marker survived every journey it is supposed to and none it is not

R-rc3-7b, gate 7a. A repository circuitRF created and one made by `git init` beside it — with a
hand-written `.gitignore` and `.gitattributes` — are told apart from `circuitrf.managed` alone. A
directory copy (what an archive is) **carries** it; `git clone` **does not**, because git does not
clone a repository's config. No git version copied configuration on clone in any path tested.

### `--no-verify` is not the hook bypass, and the sentinel proves it

Measured before writing the code: with a `post-commit` hook installed, a plain `git commit` fires it;
`git -c core.hooksPath= commit` does not. R-rc3-7a is right that `--no-verify` skips `pre-commit` and
`commit-msg` only. With the plumbing path (`commit-tree`) `git commit` never runs at all, so the
invocation-level empty `core.hooksPath` is what covers every route — and the gate asserts the absence
of a sentinel file rather than the absence of an error.

### The commit identity needed a per-user directory below the firewall, and a lever for a process

R-rc3-1c wants the identity readable by `src/Cli`. `AppPreferences` is in `src/Ui` and stays there;
what moved down is only the **directory** — `CircuitRF.Design.UserStateDirectory`, with
`CircuitRF.Ui.AppDataRoot` delegating to it so there is still exactly one lever and the three caches
it invalidates still get invalidated.

**That was not enough for a separate process.** In-process redirection is unreachable from a CLI run,
and the platform variables do not substitute: on macOS .NET resolves `LocalApplicationData` from the
platform, not from `XDG_DATA_HOME` or `HOME` (which is the finding `AppDataRoot`'s own header already
records). So `UserStateDirectory` gained **`CRF_STATE_DIR`**, the same arrangement
`CRF_VERILOGA_COMPILER` already has. Without it gate 22 is untestable and, more to the point, an
agent's container has no way to tell circuitRF who it is.

### The `.gitignore` excludes result EXTENSIONS and deliberately not the `results/` folder

R-rc3-9 says "results, and simulation output directories". **The folder is not purely output** and
excluding it would silently drop two things from every restore and every clone:

- a **`.cdd`** Data Display is a design document a designer authored, and the archive scanner already
  classifies them out of `results/`;
- an EM run's **`.sNp`** lands there under a predictable name *precisely so a schematic's SnP reference
  survives a re-run* (`EmRunService.ResolveSnpPath`, R-em-19).

So the block is `*.npy`, `*.spl`, `*.lpcwave`, `*.mat`. **`.sNp` is never excluded** for the mirror
reason: a vendor-supplied Touchstone is design INPUT and shares the extension with an EM result, and
there is no pattern that separates them.

### Where the sentence "results are not kept" actually reaches a user, for now

§7's §10B.1 row is **owned by a table RC-5 creates** — there is no revision-control user chapter yet,
and RC-3 ships no user-facing revision-control surface at all, so a page describing one would document
a feature that does not exist. The statement is made where a designer actually meets it today: as
prose in the generated `.gitignore`, which says the exclusion is about **reproducibility rather than
size** and that the answer is to re-run the analysis. The user-doc row lands with RC-5's chapter.

### `ProcessRunner` and `GitCommand` did NOT converge

R-rc3-1 asks whether a shared primitive is obviously right. **It is not**, and the reason is not
distance across the firewall. `ProcessRunner` has a closed allow-list of six tools with hard-coded
absolute paths, no stdin, no per-call environment, no inactivity bound and no cancellation.
`GitCommand` is one program whose path is discovered, carries a per-invocation environment and a
per-invocation `-c` prefix, pipes a commit message on stdin, distinguishes a wall-clock bound from an
inactivity bound, and retries a lock collision. The only shared part is thirty lines of
"start, pump both streams, bound it, kill the tree" — and extracting that would leave both callers
importing a type whose interesting behaviour is entirely in the parts that differ. **Reported, not
moved** (a type does not cross the firewall as part of a findings write-up).

### Git failures encountered during development that R-rc3-4's table does not name

The list the brief says is the most valuable thing it produces. **Two**, both structural rather than
new rows:

1. **A repository created by `init` at a path whose ancestor is a symlink** — reported as no
   repository at all. Not a git failure: circuitRF's own comparison. Recorded above.
2. **`git update-ref` under a private `GIT_INDEX_FILE` reports its lock failure through
   `update_ref failed for ref … : cannot lock ref … : Unable to create '<abs>.lock': File exists`** —
   three nested clauses, of which only the innermost names a file anyone can act on. That is the row
   the table already has; what was new is how much of the message has to be ignored to find the one
   actionable path in it.

No platform's git needed handling the other two did not, on the one platform this was developed on.
`safe.directory` was honoured per invocation on every call made here; the foreign-ownership row is
exercised by simulating git's answer, because constructing a repository owned by another account needs
a second account and no test may assume one.

### What could not be checked here

- **The macOS shim (R-rc3-2a) was never seen doing its thing**, because this machine has the Command
  Line Tools installed (`xcode-select -p` succeeds, `/Library/Developer/CommandLineTools/usr/bin/git`
  exists). The gate runs through the seam on all three platforms and asserts that **no process named
  `git` is started** when the tools check says absent; the real dialog is a manual check on a machine
  without them, and it has not been done.
- **Whether two circuitRF processes ever collide on a reference update in practice.** Eight concurrent
  checkpoints against one repository produced no collision the retry did not absorb, and the retry
  bound (six attempts, ~250 ms total) was never exhausted. That is a laboratory answer; the field one
  needs RC-5's own scheduling.

## RC-2 — the reference editability field, and the memo it could not share (2026-09-06)

`CwsWorkspaceRef.Editable` plus `ReferencedWorkspacePolicy`
(`brief-revision-control-2-read-only-references.md`; `docs/design/revision-control.md` §7A.2,
`workspace-and-project-tree.md` §5C.1a). The UI half is in `src/Ui/RESOLVED.md`.

### The inverted default, and why it does not contradict SL2's "never a field in the `.cws`"

`workspace-and-project-tree.md` §5D R-sl2-A says read-only is **never** a `ReadOnly: true` field in a
`.cws` — it would be advisory, maintained by hand, and wrong on precisely the machine where it
mattered. RC-2 adds a field that looks exactly like the thing that rule forbids, and it is not one.
They answer different questions:

| | question | source of truth | same answer for everyone? |
|---|---|---|---|
| `WorkspaceWritability` (SL2) | **can** circuitRF write into this directory? | the filesystem, discovered by attempting a write | yes |
| `ReferencedWorkspacePolicy` (RC-2) | **should** it, given which workspace is asking? | this workspace's own `.cws` | **no** — false from the window that owns the content |

That last column is the load-bearing one and it is why the policy is **not** memoised globally per
root the way SL2's writability is. `WorkspaceWritability.OpenReadOnlyThisSession` marks a root
read-only for the whole process, which is right for its question and would be catastrophic here: it
would refuse the *owner's* own save, which is the one save §7A.3 exists to route the edit to. Every
entry point therefore takes the referencing workspace root as an argument.

The default inverts the house rule its own sibling states three lines away (`CellsOnly`: *false on
every entry written before this existed, which is the old behaviour*). Both headers now say so
explicitly, pointing at each other, because a reader who finds only one of them will copy the wrong
instinct into the next field.

### The alias table could not simply be widened

`ExternalCellRef.AliasMapFor` already memoises alias → other-workspace-root per referencing root, and
widening its value to carry editability was the obvious move. It is the wrong one: that map is how a
`ws://` reference **resolves**, and resolution deliberately does not read this flag — a read-only
reference addresses exactly what an editable one addresses. Coupling them would put a policy read on
the hot path of every cell-instance render for no benefit. So there is a second, small memo, dropped
from the same `WorkspaceRootFinder.InvalidateCache` as the other four, which is now the fifth entry
in that method.

### A `.cws` this build cannot read is "no references", not an error

`ReferencedWorkspacePolicy.Read` swallows the load exception and returns an empty table, matching
`ExternalCellRef.ReadAliasMap` beside it. Worth naming because a hand-written test fixture with the
wrong `FormatVersion` then reads as **NotReferenced** rather than as a parse failure — the fixture in
`ReferencedWorkspaceReadOnlyTests` was written that way first and produced a silently wrong gate. Any
hand-built `.cws` fixture must carry `WorkspacePersistence.CurrentFormatVersion`, not a literal.

### Prefix matching needs the separator check

A path is inside a referenced root when it IS the root or sits under it *behind a separator*. Without
the separator test `…/stdlib-old` reads as content of `…/stdlib`, and a sibling project would go
read-only for no visible reason. Same rule, and the same reason, as
`WorkspaceWritability.IsUnderSessionReadOnlyRoot`.


## RC-1 — the `.cwsuser` split: what the measurement said, and the reader that was not a choke point (2026-09-06)

Per-user session state moved out of the `.cws` into a sibling `.cwsuser`
(`brief-revision-control-1-workspace-file-split.md`; `docs/design/revision-control.md` §3.1/§3.1a).
`WorkspaceUserPersistence` is the new type; nothing else may write the file.

### The measurement, re-taken with the real writer

§3.1 estimated ~96% per-user on a 2,178-byte `.cws`. Measured on this repo's own demo workspace by
loading and re-saving through `WorkspacePersistence` itself, not by counting fields:

| | bytes |
|---|---|
| `.cws` before | 2,914 |
| `.cws` after | **65** |
| `.cwsuser` | 2,873 |
| per-user share | **97.8%** |

The figure is slightly *worse* than §3.1's, and the shape is what matters: what is left of the `.cws`
on a workspace with no libraries and no kits is a format version and two empty arrays. A file that
changed on every session close was, in content, almost entirely one person's monitor.

### `TryLoadCws` is not a read choke point, and the brief's premise (R-rc1-10) is wrong about it

The brief says the read is "one read becoming two, inside `TryLoadCws`
(`WorkspaceViewModel.cs:2531`) — the corresponding choke point, which has existed since the
beginning." **It is not one.** There are THREE private `TryLoadCws` helpers — in
`WorkspaceViewModel`, `WorkspaceScanner` and `WorkspaceArchiveScanner` — and roughly **twenty-five
direct calls** to `WorkspacePersistence.LoadFromFile` in `src/Ui`, `src/Design` and `src/Cli` that go
through none of them. Merging there would have given the merged shape to one of four readers.

So the merge went **into `LoadFromFile` itself**, which is the actual lowest level and the exact
mirror of what SL2 did for writes. The consequence is the one R-rc1-10 wanted: **no caller changed**,
and a twenty-sixth reader inherits the merge without knowing the split exists.

This is worth stating rather than fixing quietly, because the brief drew the wrong conclusion from a
real fact: reads DID have a choke point "since the beginning" — it is `LoadFromFile`, not the view
model's corruption-tolerant wrapper around it.

### The `.cws` is stripped at the JSON level, and the list names what LEAVES

`Serialize` serializes the whole `CwsFile` and then removes five keys, rather than copying a typed
object with five fields left out. A hand-written copy list drops any field it forgets — silently, and
only for the workspaces of whoever hits it. Naming the five that leave means a sixth field added to
`CwsFile` next year keeps being written with no thought required, which is the direction the mistake
should fall. `WorkspaceArchiveWriter.RewriteCws` already edits the parsed tree for the same reason.

### Delete-on-empty is not tidiness, it is what makes the split behaviour-preserving

Before RC-1, a caller that assembled a `CwsFile` with no session state and saved it CLEARED those
fields out of the `.cws` — and several callers do exactly that (`ExternalRefs`' `catch { cws = new
CwsFile(); }` branches). If the sidecar were merely left alone in that case, the two halves would
disagree about whether a layout exists and "close every tab, then save" would reopen the closed tabs.
So a save with nothing to record DELETES the sidecar. The behaviour is identical to before; only the
file it happens in changed.

### The sidecar carries a `FormatVersion` that is never rejected on

Deliberate, not an omission. `Deserialize` refuses a `.cws` whose version it does not know because
the alternative is loading a design wrongly. The worst a misread `.cwsuser` can do is restore the
wrong panel, and its malformed case is already specified as "treated as absent" — so a version check
here could only ever turn a readable file into a silently-discarded one.

### `PythonInterpreter` stays, and `ColorSchemeName` moved (owner, 2026-09-06)

The brief flagged both as judgement calls. `PythonInterpreter` stays in the `.cws` as the brief
proposed, and the implementation found no reason to disagree: it is per-*machine* rather than
per-user, but the `.cwsuser` is not a per-machine file either, and moving it would cost a kit-using
workspace a process-launch storm on every fresh clone for no gain.

`ColorSchemeName` was the one field in §3.1's table whose side had been **assigned rather than
measured**, and the owner settled it the other way from §3.1: **it is per-user and lives in the
sidecar.** The costs accepted with that are real and are recorded in §3.1 — a workspace deliberately
shipping a house theme no longer activates it for a colleague (the `.ccolor` files still travel; the
*selection* does not), and deleting the sidecar resets the theme along with the panels.

### `Path.GetExtension` of a dotfile with no stem returns the whole name

`Path.GetExtension(".cws")` is `".cws"`, not `""` — which is what `App.OpenFiles`' switch has always
relied on, and what makes `case ".cwsuser":` work. `MoveRefRegistry` carried a comment asserting the
opposite; it was harmless there (that table matches by whole file name anyway) but it is exactly the
kind of belief that would make someone "fix" the dispatcher into opening nothing. Corrected, and
pinned by a test.

### Open for RC-3

`.cwsuser` must be in the generated `.gitignore` (R-rc1-16). RC-3 owns the generator; the line and
the reason for it belong to this brief and there is no generator to put it in yet.

## VProbe — a probe that names a net, and the two ways it could have changed the circuit (2026-09-06)

The voltage probe is a one-terminal component that stamps nothing: extraction emits a `VProbe:` line,
the elaborator reads the one net it names, publishes that net under the probe's own instance name and
builds no model. Deleting every VProbe from a design must leave the answer bit-identical, and two
things found while building it would have broken that quietly.

### 1. Two roots under one name MERGE into one node, silently, as a different circuit

`Elaborator` resolves a net by NAME (`NodeMap.GetOrAssign`), so two distinct union-find roots that
`AssignNetNames` happens to give the same name become **one** node — one matrix row where the drawing
has two. Nothing reports it; the run converges and answers the wrong question. A probe is the easiest
way to reach that state, because its name is typed by hand into an instance field rather than onto a
wire, so three things guard it:

- extraction refuses to hand a probe's name to a net when a label or a Pin port already owns it, and
  reports it (the probe's net keeps its automatic name and the probe becomes an alias instead);
- the elaborator REFUSES THE RUN when a probe's name is any other net's name, and when two probes
  share a name — a warning is the wrong answer for something that makes one name mean two traces;
- **the auto-namer now skips a name already taken.** This was a pre-existing hazard with nothing to do
  with probes: a user net LABELLED `n2` and an auto-named net that reaches `n2` merged, and had done
  since auto-naming was written. `AssignNetNames` now advances past any `nK` already spoken for.

### 2. A probe must never MINT the net it points at

`_voltageProbes` is collected during the flatten and resolved afterwards, deliberately. Resolving
inline would call `GetOrAssign` on the named net, and for a name nothing else reaches that ADDS a node
— a matrix row nothing touches, which is a singular solve. After the walk, a name that is still absent
is genuinely absent and is reported as such. The late pass also gets to see every net that exists,
which is what makes the collision check above complete.

### 3. A probe attaches the way a LABEL does, not the way a pin does

`AddGeometricUnions` unions a component pin only where a wire has a VERTEX. That is right for a device
— a pin sitting mid-span would silently tap a run the user drew straight past it — and wrong for a
probe, whose whole gesture is being dropped onto the middle of a wire. `FindWireSpanKey` is the
segment scan `FindLabelNetKey` already had, minus its "the point is already a key" shortcut: every
component pin is seeded into the union-find before it runs, so a probe asking about its own pin
position would otherwise be answered with its own pin.

### 4. Rename or alias, decided by whether the user had already named the net

Owner, 2026-09-06: a probe on an UNNAMED net becomes that net's name outright rather than adding a
second row, because `n7` was never a name anybody chose and two rows for one point in the circuit is
one row too many. A probe on a LABELLED net leaves the label standing and aliases it, so both names
appear. Extraction decides this (it is the half that knows where a name came from — label, Pin, or
auto); the elaborator treats "the alias IS this net's name" as nothing to do rather than as a
collision, which is what lets the two halves agree without a second flag in the `.cnl`.

An alias carries NO matrix row: `NodeMap.Aliases` is a name pointing at an existing node, and
`NodeMap.ResultRows` is the one place that turns it into an extra row on a result's node axis — used
by `DcResultPacker`, all three `HbEngine` packers and the CLI's own `dc` listing, so a probe cannot
report under one engine and vanish under another.

### 5. It is the one component that comes OFF a wire when dragged (2026-09-06)

`ComponentTypeRegistry.DetachesFreely` is true for `VProbe` and nothing else. The editor's standing
guarantee — moving the picture never re-wires the circuit — is enforced in **six** separate places,
and a probe has to be excluded from every one of them or the exemption is only half true and the two
halves disagree on screen:

| Where | What it does for every other kind | Why a probe is out |
|---|---|---|
| `UpdateConnectedWireEndpointsLive`'s port map | drags wire endpoints live | the preview must show what the commit will do |
| `BuildPortMoves` (commit) | same, at commit | the other half of the same map |
| `BuildTapStubs` (via both maps) | grows a stub back to a wire a pin has left | a probe leaves nothing behind |
| `_dragPinOnPinContacts`, **both directions** | auto-wires a separating pin-on-pin contact | a probe coming off a lead, and a lead coming off a probe |
| `IsPointHeldByStationaryPin` | a shared point stays with the STATIONARY pin | a probe on a lead would otherwise freeze every later drag of the part it watches |
| `ComputeWireSlideClamp` (b) | bounds a wire's slide so a body tap stays on it | a wire the user grabbed would refuse to move, with no visible reason |
| `PinFollowReroute.Build`'s `moves` | re-routes a wire onto a rotated/mirrored pin | rotating a probe must not redraw the schematic |

**`PinFollowReroute` excludes it from `moves` and NOT from `pinPoints`.** A probe drags nothing, but a
re-route laid across a probe's pin would silently CONNECT it — the one remaining way this could
surprise somebody — so it stays in the obstacle scan.

**Nothing is needed for the together case.** Select the probe and its wire and both move by the same
delta, so the pin is still on the wire when the gesture ends; the exclusion governs only the case
where the two move by different amounts. `VProbeDragDetachTests` pins both halves, and 7 of its 9
tests were confirmed to fail with `DetachesFreely` forced to `false`.

### 6. Detaching mid-drag is the first thing that made DEFERRED connectivity wrong (2026-09-06)

A drag never rebuilt the render model — connectivity is O(N) and the drag runs per frame — so port
markers and wire-endpoint squares showed the state as it was when the gesture began. That was
*correct*, not merely cheap: every component takes its wires with it, so a pin connected at drag start
is connected at every frame in between, and the only live override needed was one that turns a marker
GREEN (`liveDotKeys`, for a pin that has just met a junction dot).

A probe breaks the premise. It comes off, so does the wire endpoint it was holding, and both kept
drawing as attached until the user let go — at exactly the moment the user is deciding where to drop
it (owner report).

- `SchematicEditModel.ComputeLiveConnectivity` returns the dots **and** both connection tests from
  ONE `ComputeConnectivityGeometry` pass, so a frame cannot draw a junction dot where its own port
  test says nothing is attached. `BuildRenderModel` now calls the same two closures
  (`PortConnectionTest` / `EndpointConnectionTest`) it used to declare as local functions — a second
  copy for the drag is precisely how a pin comes to render connected while the model says otherwise.
- Bounded by the SAME `LiveDotMaxObjects` (1,500) the live dots already used; past it all three come
  back null and the renderer falls back to the deferred answer, exactly as before.
- **The port override runs in one direction only — Connected → Unconnected.** A port the model calls
  unconnected may be unconnected because the user DETACHED it, and `SchematicPortDef` carries no flag
  separating that from "nothing is there": turning it green because a wire passes under it would undo
  an explicit disconnect on screen. The green direction stays with `liveDotKeys`, where it was.
- **A preview route counts as a connection.** A separating pin-on-pin pair and a mid-span tap's stub
  are wires the COMMIT will create; they are not in the model yet, so a naive live test calls both
  ends loose and every such drag flashes red for its whole length. `LiveConnectivity(previewWires)`
  folds their points in. `MidDrag_ASeparatingPinOnPinPair_DoesNotFlashUnconnected` fails without it.

### 7. Both probes name their quantity, at ONE size

`BuiltInSymbols.ProbeGlyphTextSize` is the `I` in the ammeter window and the `V` in the dial. One
constant because the two are read side by side and a letter a little bigger on one reads as a mistake
rather than a distinction (owner). **The binding frame is the ammeter window, not the dial** — its
clear height is ~36 units (the bowed edges reach y = -19 and y = -55) against the dial's 52 — so if
the window is ever redrawn smaller, both letters shrink together. `BothProbesNameTheirQuantity_…`
measures that headroom rather than pinning the number.


## AUT-2 — the schematic and symbol model moved below the UI firewall (2026-09-05)

`brief-automation-2-schematic-below-the-firewall.md`. 41 files left `src/Ui/Schematic` for
`src/Design/Schematic` (36) and `src/Design/Symbol` (5); 64 stayed. No behaviour change, no refactor:
whole files, namespace renamed, `using` churn absorbed in `src/Ui/GlobalUsings.cs` and its
`tests/Ui.Tests` mirror. The chain `.csch → SchematicEditModel → NetExtractor.Extract → CnlWriter` now
runs in a project that references no UI framework, gated by
`tests/Firewall.Tests/SchematicChainBelowTheFirewallTests` against a golden the GUI's own path writes.

### 1. `CircuitRF.Design.Symbol` is both a namespace and a type, and no `using` can fix it

`SymbolModel.cs` declares `public sealed class Symbol`. Putting it in namespace
`CircuitRF.Design.Symbol` — which R-aut2-5 prescribes, and which is the only spelling consistent with
the folder — makes bare `Symbol` **CS0118 from every sibling `CircuitRF.Design.*` namespace**. It bit
8 files, 280 occurrences, `BuiltInSymbols.cs` alone accounting for 266.

**A compilation-unit `using`-alias does not help, and the reason is worth knowing.** C# name lookup
(spec 7.8.1) walks enclosing namespaces from the inside out and, at each one, asks *"is `I` the name
of a namespace in `N`?"* **before** it looks at any alias. Reaching `N = CircuitRF.Design`, the
namespace `Symbol` wins, and the alias sitting at the top of the file is never consulted — it is
attached to the compilation unit, which is only reached last.

**What does work is an alias placed AFTER the file-scoped namespace declaration**, which makes it a
member of *that* namespace declaration and therefore consulted while `N` is still
`CircuitRF.Design.Schematic`:

```csharp
namespace CircuitRF.Design.Schematic;
using Symbol = global::CircuitRF.Design.Symbol.Symbol;
```

Verified against the compiler on a two-file scratch project before being applied, not reasoned from
the spec alone. It is one line per file and carries a comment pointing here. **A new file in
`CircuitRF.Design.Schematic` that names `Symbol` will need it too** — that is the standing cost, and
the alternative considered and rejected was naming the namespace `Symbols`, which would have removed
the trap at the price of contradicting an explicit requirement over one character.

### 2. `PdkPartInstaller` moved although R-aut2-2 forbade it — reported, not absorbed

**The dependency is three references to one string constant.** `NetExtractor.cs:505`, `:527` and
`:809` read `PdkPartInstaller.ModelLibraryParameter` — the literal `"ModelLibrary"`, circuitRF's own
name for "evaluate this instance with a different model library" — to keep it out of the parameters
forwarded to a provider. Nothing else in the closure touches the type.

R-aut2-2 groups it with `VerilogACompilerInstaller` under *"Installation UX"*. On reading, the two
have nothing in common: `VerilogACompilerInstaller` downloads a compiler and reads `AppPreferencesIo`
and `AppDataRoot`, whereas `PdkPartInstaller` is 1,526 lines whose every `using` is
`CircuitRF.Core.*`, with no reference to any `CircuitRF.Ui.*` namespace at all. It turns the parts a
kit reports into ordinary cells — a document-layer operation. The classification looks like it was
made from the name.

Both alternatives were worse than moving it. Leaving it above the wall leaves `NetExtractor`
uncompilable, and the brief's own §6 asks for *"anything R-aut2-2 forbade moving that the closure
nonetheless required, with the exact dependency"* — which presumes it moved. Inverting for a string
constant is the shim R-aut2-7 says not to write. **It moved; this is the report.** If the owner wants
it back above the wall, the constant is the whole coupling and R-aut2-7 resolution 2 on that one
declaration is the cheapest way there.

### 3. Two dependency inversions, and why each type could not simply stay above the wall

Both follow `LayoutTextOutline.TypefaceSource`: a settable static in `src/Design`, a
`[ModuleInitializer]` in `src/Ui`, and an unset fallback that is exactly what a headless process
should get.

**`WBondPlacement.NewWireFootZNm`** (`Func<long?>`, default `() => null`). `WBondPlacement` is reached
by both `NetExtractor` and `ComponentTypeRegistry`, so it had to move; the one thing it could not
bring is `WBondDefaults.FootZNm`, which is
`AppPreferencesIo.Load().WBondWireFootZNm ?? ShippedFootZNm` — a per-installation preference, and
`WBondDefaults` itself reads `CircuitRF.Ui.Theming`. `null` already meant "the shipped 4 mil" to
`WBondEmbedding.DefaultDesign`, so the unset hook changes nothing about what a headless placement
produces. Installed by `src/Ui/WBond/UiWBondDefaultsInstaller.cs`.

**`VerilogAModelIntrospection.CacheDirectory`** (`Func<string?>`, default `() => null`). Pulled in by
`EditableSchematic`, which asks it for a component's terminal labels. Its one coupling was
`AppDataRoot.SubDir("cache")`. Moving `AppDataRoot` instead was considered and rejected: it has 43
call sites across the updater, the crash reporter, preferences and `tools/DocGen`, and it exists
precisely to be the *single* lever the docs factory redirects — so recomputing
`LocalApplicationData/circuitRF` below the wall would recreate the two-independent-callers problem it
was written to solve. Unset, every read is a cache miss and every write is dropped, which is already
what an unreadable cache directory has always meant there. Installed by
`src/Ui/Schematic/UiVerilogACacheInstaller.cs`, reading `AppDataRoot` lazily so a later redirect still
takes effect.

**This is a deliberate exception to this project's own "a preference is an ARGUMENT" rule.** That rule
(see `src/Design/CLAUDE.md`) says a preference should be a parameter, as `EmRunService.Run`'s core cap
is. Here it could not be: R-aut2-3 forbids reshaping a moved file, and both values are read from deep
inside call chains whose signatures the brief does not permit changing. A settable hook with a safe
default was the narrowest thing that preserves behaviour. `tests/Ui.Tests`'
`TheHooksSrcUiInstallsIntoTheMovedCode_AreActuallyInstalled` asserts both are wired in a running
process, because an uninstalled hook fails silently by construction.

### 4. `PCellContract.cs` and `SubstrateResolver.cs` came with it, and cost nothing

`MicrostripSubstrateInjection` — which `NetExtractor` calls to inject H/T/Er/Sigma/TanD from the
stackup — needed `PCellLayerSelection` and `SubstrateResolver`, both still in
`CircuitRF.Ui.Layout.PCells`. This is R-aut2-7 resolution 2 in its purest form: they are design-layer
artifacts (a technology-stackup resolver and a layer-choice record), and `CircuitRF.Design.Layout.PCells`
already existed as their destination and was already in both `GlobalUsings` files. The seven PCell
generators, `PCellRegistry`, `GeneratedCellStore` and the handle solver all implement or use the
contract and all stayed in `src/Ui` — they saw the new namespace through the global using and needed
no edit. Only three fully-qualified `CircuitRF.Ui.Layout.PCells.…` spellings had to change.

### 5. Two `using CircuitRF.Ui.Layout;` lines were already dead

`NetExtractor.cs` and `WBondSymbolGenerator.cs` each carried one, and neither referenced a single type
from that namespace — the types they once reached had moved to `CircuitRF.Design.Layout` in the 2026-08
carve-out, where `src/Ui/GlobalUsings.cs` had been supplying them ever since. Deleted. R-aut2-7's own
advice — *"check before assuming a real dependency"* — earned its place: `NetExtractor` was named in
the brief as the file to look at first for a genuine cross-namespace coupling, and it had none.

### 6. A gate tripped on 15 sentences nobody wrote

`UserFacingTextGateTests` fails on any user-facing exception text below the firewall that is not
allow-listed, and 15 pre-existing sentences became "below the firewall" purely by moving. Converting
them to `Diagnostic`s is a behaviour change and out of scope, so they were added to
`tests/Firewall.Tests/user-facing-text-allowlist.txt` under a *"Moved, not authored"* heading — the
same treatment, and the same wording, the 2026-09-02 interchange move used. The allowlist's own header
calls this a deliberate choice, and it is: the backlog grew by 15 without any new prose being written.

### 7. Methodology: Roslyn stops binding bodies once a `using` fails to resolve

Worth recording because it wasted a real detour. A single `CS0234` on a `using` directive is a
DECLARATION-phase error, and the compiler then **skips the method-body pass entirely** — so
`dotnet build` reported one error and stayed silent about the ~280 unresolved names behind it. It
looked exactly like a reference that was somehow resolving. It is not: fix the `using`, and the rest
appears. When driving a move by "let the compiler decide the boundary", clear every `using`-directive
error before believing any error count.

## Phases PL1/PL2 — post-implementation review (2026-09-05)

Both phases re-read against their briefs, and the whole path exercised on library folders shaped the
way a user's are rather than the way the fixtures are: many parts under one root, each written out
once per target format into a sibling folder, and pin names that repeat. Every finding below is a
defect the committed gates passed over. All four are fixed.

### 1. A pin NAME was being used as a pin IDENTITY, and it fails on nearly every real part

`ComponentTerminals.Build` keyed the pin↔pad join on the symbol pin's NAME. R-PL1-11's "a pin bonded
to several pads appears once in the symbol" was implemented as "a name seen twice is one pin seen
twice", and those are not the same statement. **A real part declares `VSS` seven times and `VDD` six,
each its own pin on its own pad** — so six of those seven lost their symbol side, were counted as
"pads referenced by no symbol pin", and, worse because nothing reported it, **all seven declarations
were given the FIRST one's `PortIndex`**. That breaks R-PL1-8's whole invariant on the majority of
parts with more than one supply pin.

Measured, on a 144-pad part whose imported section declares 72 pins: **65 of them joined before the
fix, 72 after** — all of them. On a 6-terminal part, 5 → 6.

**The identity is the DECLARATION, not the name** (`ComponentTerminal.SymbolPinIndex`, and
`ComponentImport.BuildSymbol` keys its `PortIndex` lookup on it). The genuine bonded case — the XML
library's `GND@1`/`GND@2`, one logical pin drawn twice — is separated by the format's own suffix and
nothing else, so `ComponentSymbolPin.Bonded` / `ComponentConnect.Bonded` record that the suffix was
there before it is stripped, and only bonded declarations sharing a name collapse to one terminal.

**Why the fixtures could not catch it**: `WIDGET9`'s nine pins have nine distinct names, and every
other fixture in both phases is the same shape. A part whose pin names are all distinct cannot
distinguish "keyed on the name" from "keyed on the declaration". `REPEAT6` exists for exactly that
and declares `GND` three times (`Gate5b`); `Gate5` holds the bonded case shut from the other side.

### 2. One candidate could be built out of two different parts' files

`ComponentFolderScan` ranked the WHOLE scanned tree as a single pool. A library folder holds many
parts, and one part is routinely written out once per target format into a subfolder of its own, so:

- a symbol in one folder paired with **every** footprint in the tree — one cell came out carrying
  another part's land patterns, reported only as "these land patterns are not density levels of one
  pattern";
- a multi-file set (`.p`/`.d`/`.c`, the `.hkp` four) collected every such file in the tree into ONE
  candidate, and the reader takes the first file of each kind — **so pointing at a folder of four
  parts imported one and silently dropped three.**

Measured, on a root holding four parts in eight formats each: **13 candidates before, most of them
mixtures of two or more parts; 23 after, each one a single component.**

The fix is a grouping pass ahead of the ranking (`RankGrouped`). A group is not a directory — a
symbol's land patterns genuinely sit in a child folder — so **a directory that directly holds a file
which can BEGIN a component is a group root, and every other file joins the nearest group root above
it.** Groups keep their candidates together and are ordered by the best candidate in each, so the
preselected top row is still the best reader rather than whichever folder sorts first
(`Gate2c`, `Gate2d`). Each row now names its folder (`ComponentCandidate.Location`), because a part
written out in eight formats produces rows that are otherwise indistinguishable.

### 3. The folder walk's ceilings were silent, and unstoppable

`MaxDepth`/`MaxFiles` made the walk finite, which is not the same as stoppable: twenty thousand file
opens on a network share is minutes of apparently-hung window, and a scan that stopped at a ceiling
reported a short list indistinguishable from a small folder. `Scan` now takes a `CancellationToken`
and a progress callback and reports `Truncated`; the UI runs it behind the same live progress row and
Cancel the Gerber import uses, and says when it was cut short (`Gate2e`, `Gate2f`).

### 4. The chooser was skipped on a candidate the user had not pointed at

R-PL1-4's "pointed at a single file, skip the chooser and import it" was implemented as "skip whenever
the scan found one option", which imports something other than what was clicked and also removes the
only route to the folder picker. It now skips only when the clicked file IS the whole of the only
candidate.

### What the review did NOT find

**The readers themselves hold up, and that is the useful half of the answer.** One nine-terminal part
written out in all seven grammars produces the *same* terminal table from every one of them — same
order, same pad identifiers, same pin names, both `symPinNum` indirections followed — and 23 of 23
candidates across four parts import with no refusal and no exception. Multi-section parts remain the
one visible limitation, and they are named rather than merged (R-PL1-23).


## Phase PL2 — COMPLETE: component library import, breadth (2026-09-05)

`docs/sonnet-briefs/brief-PL2-component-library-breadth.md`. Five more formats behind PL1's single
**File ▸ Import ▸ Component…** — no new menu item, no new cell shape, no second import path. The
classifier is the only thing widened (§5); everything below `ComponentPart` is PL1's, unchanged.

Gate: `dotnet test tests/Ui.Tests` and `tests/Firewall.Tests` (10), both green. 41 new tests in
`ComponentImportBreadthTests`, covering the brief's §7 items 1-16.

### 1. All five landed — and the return went flat before the first one, which the owner overrode

**R-PL2-3 asked for the return to be measured after each format and for the phase to stop when it
went flat. It was flat at the start.** PL1's own completion note (§6, above) already concluded PL2
was speculative breadth, and PL2 §1 concedes the same in its opening paragraph: every format here
sits alongside one PL1 already reads, so no part becomes reachable that was not reachable before.
That was put to the owner before a line was written, with the sizing below, and the decision was to
build all five as insurance. **Recorded because "we measured and built anyway" is a legitimate
outcome and "we never measured" is not.**

What PL2 does buy, and it is the honest whole of it:

- **A library folder that happens to hold none of PL1's four now imports** instead of being refused
  by name (R-PL2-1). That is the entire value proposition, and it is about which format a folder was
  assembled with, never about which parts exist.
- **The chooser's "text formats circuitRF has no reader for" count drops sharply** on a folder
  carrying several of these, because five of its formats stop being noise and one — the encrypted
  `.hkp` twins — stops being reported at all (R-PL2-7).

**The cost was HIGHER per format than PL1's, not lower, and for a reason worth keeping.** PL1's §2
records that its footprint half cost 94 lines because `.kicad_mod` *is* the board format and
`PcbReader.ReadFootprint` already existed. **No PL2 format shares that lineage.** Each states its own
pads, outlines and layer numbering, so each needed its own footprint geometry reader as well as its
own symbol reader and its own map join. `ComponentArtwork.cs` (305 lines) exists purely to stop that
being written five times: readers emit neutral pads/paths/circles and it alone builds the
`PcbFootprintCell`, synthesises the layer table and converts to DBU.

Measured, in lines:

| Format | Reader | Lines |
|---|---|---|
| shared | `ComponentArtwork` + `ComponentFootprintBuilder` | 305 |
| `.p`/`.d`/`.c` | `ComponentRecordsReader` | 547 |
| `.hkp` set | `ComponentHkpReader` | 619 |
| `.PLX`/`.DSL` | `ComponentPlxReader` + `ComponentPlxSexpr` | 370 + 180 |
| `.cxf` | `ComponentCxfReader` | 359 |
| `.scr` | `ComponentScrReader` | 417 |

### 2. The `symPinNum` indirection (R-PL2-12), and its undocumented twin in the `.hkp` set

**The brief flags this for `.PLX` only. It is present in the `.hkp` set too, and the brief does not
say so** — which is the single most useful thing this phase learned.

- **`.PLX`/`.DSL`**: `compDef` states one `compPin` per pad, each carrying a `pinName` and a
  `symPinNum` that is *not* the pad number. Followed always. **The `padPinMap` cross-check never
  disagreed on a well-formed file** — it is a redundant restatement, and every file that parsed at
  all had the two in agreement. It is kept because a disagreement is the exact signature of the bug
  this rule exists to prevent, and `Gate9b` proves the refusal fires by feeding it a fixture whose
  two spellings contradict each other on one pad.
- **The `.hkp` twin**: the part file states the map as three parallel lists (SwapIDs, PinNames,
  PinNumbers) joined by POSITION, **ordered by pad**; the symbol file numbers its own pins in
  DRAWING order and carries its own name/number text records keyed by that ordinal. **The two orders
  are different.** Indexing the part file's lists by a symbol pin's ordinal yields a fully populated,
  correctly-shaped, wrongly-wired part. The symbol file's own text records are authoritative for the
  symbol; the two maps are cross-checked as sets and a genuine contradiction is a refusal.

**How both are proven**: `Gate3a` runs one fixture part through all five grammars and asserts one
map string. The fixture's symbol is drawn in the order ALPHA, DELTA, BETA, GAMMA, THERMAL while its
pads number 1, 2, 3, 4, TPAD — so an ordinal join produces `1=ALPHA 2=DELTA 3=BETA 4=GAMMA` and the
correct answer is `1=ALPHA 2=BETA 3=GAMMA 4=DELTA`. Nothing but that assertion separates them.

**The indirection is frequently the identity**, which is the trap inside the trap: a part whose
symbol happens to be drawn in pad order has `symPinNum == padNum` throughout, and a reader that
notices this on the file in front of it and takes the shortcut is correct until it is not.

### 3. Was the `.hkp` symbol grammar worth its cost? Yes — and for a reason that is not its content

It is the most work of the five (two grammars behind one extension, four files, a two-step padstack
name indirection) and it is the only format here whose cell file **names its layers semantically** —
`ASSEMBLY_OUTLINE`, `SILKSCREEN_OUTLINE`, `PLACEMENT_OUTLINE`. Every other format in this phase
numbers its layers and ships no legend, so every other reader carries a `RoleOf(int)` table of fixed
meanings plus an R-PL2-14 report for the rest.

**That makes this format the only one whose layer assignment is not a claim.** If a future format is
being sized, that property is worth more than its richness: a numbered-layer format's `RoleOf` table
is the part of its reader that can be quietly wrong for years, because artwork on the wrong
documentation layer still looks like artwork.

The symbol grammar specifically was worth it for a second reason — being self-sufficient, it is what
makes the cross-check in §2 possible at all. A format that stated its map once would have offered no
way to catch the ordinal trap.

### 4. The separate S-expression tokenizer (R-PL2-11): 180 lines, 71 of them code

`ComponentPlxSexpr.cs` is 180 lines total and **71 non-blank, non-comment lines**. The brief
estimated ~150 and asked for the measurement so the decision could be re-judged rather than
re-argued: it came in under, and the two dialect quirks that forced it are both inside the ATOM,
which is the one part of `PcbSexpr` a caller cannot parameterise — commas *inside* coordinate atoms
(`(pt 0, -100)`) and unit words *after* numbers (`(pinLength 300 mils)`). Widening `PcbSexpr` would
have put a foreign dialect's quirks into the reader L4d's board import depends on, to save 71 lines.

### 5. The handedness rule here is the INVERSE of PL1's, and that is the sharpest edge in the phase

`PcbUnits.Y` negates because the board format is +y down. **Every format in this phase is already
+y up**, so the footprint half passes Y through untouched — and calling `PcbUnits.Y` out of habit
mirrors the whole land pattern. The reason is structural rather than incidental: the board format is
a PCB editor's own on-disk frame (screen convention), while these are library artwork in the
drafting convention, which is what `.clay` uses.

`Gate3c` holds it shut over a fixture whose pads sit at +30 and +10 mil and nowhere below the axis —
a land pattern symmetric about its X axis imports identically whether the flip happened or not,
which is PL1 §3's trap pointing the other way. The symbol half is unaffected: readers hand over +y up
and `ComponentImport.FlipY` does the `.csym` flip downstream, exactly as PL1's readers do.

### 6. Two things the format documentation does not tell you, both found by replaying counts

- **The `.d`/`.c` decal header declares TWO counted text runs, and they are not adjacent.** `labels`
  precede the drawn pieces and `texts` follow them. Reading only the first — the obvious mistake,
  since the two are spelled identically — leaves the cursor `2 × texts` lines short, and the pad
  stacks are then parsed out of the middle of a free-text label. This is R-PL2-4's exact failure mode
  and the geometry that comes out of it looks entirely plausible.
- **A `.scr` restates its whole `Connect` map once per land pattern it edits.** A part with three
  density variants states the same joins three times. They are one map; `ComponentTerminals` reads
  the table as a set of joins, and a triplicated entry misreports the pin count of every part that
  ships variants (`Gate12b`).

### 7. R-PL2-18 vs. the file formats' own magic — resolved, and worth knowing

**Three of these five formats carry a commercial product's name inside the banner a reader must
match to classify the file at all** — root `CLAUDE.md`'s "Commercial Vendor References" rule forbids
that name anywhere in the repo, including as a string literal, and names `.kicad_pcb` as the only
standing exception.

Resolved without an exception: **the banners are matched on their vendor-free substring**, which is
just as specific in practice. `-LIBRARY-PART-TYPES-`, `-LIBRARY-PCB-DECALS-` and
`-LIBRARY-SCH-DECALS-` for the triple (anchored to a `*`-prefixed first line); `_LIBRARY_ASCII` and
`_INTERMEDIATE_ASCII` for the two S-expression extensions, which is also exactly what distinguishes
them from each other and therefore all the banner was ever needed for. The synthetic fixtures carry
invented prefixes in the same slot and classify identically.

**The obvious gate for this — scanning new files against a list of tool and manufacturer names — is
itself forbidden**, and that is not a technicality: the rule says "not even as a glossery of names to
filter out", so a test storing that list is the leak it claims to prevent. It was written that way
first, and removed. (It was also blunt enough to be wrong twice over: an early draft matched the
plural of "pad" against this codebase's own vocabulary, and the word "librarian" against a
pre-existing note about a site librarian handing out a starter workspace.)

What replaced it asserts the property that makes a leak impossible rather than enumerating leaks:
`Gate15a` requires every banner constant to BEGIN with the separator that follows a product word, so
none of them can spell one; `Gate15b` requires each fixture's banner to carry the invented prefix and
to still classify, which is the proof the product word was never needed; `Gate15c` pins the
extensions and the family names.

### 8. What still cannot be imported, by category

- **Proprietary binary containers** — schematic and PCB library binaries, and the packaged project
  files that wrap them. Reported as binary formats in the skipped summary. Out of scope by §5, not
  by accident.
- **Three-dimensional models** — reported as such, by count.
- **Dimensioned drawings** — PL1 R-PL1-30 stands and PL2 §5 restates it: they carry no pad
  identifiers and no pin names, so they cannot satisfy the pin↔pad invariant and are listed in the
  skipped summary rather than offered as a component that would import wired to nothing.
- **Every PL1 limitation, unchanged and now five times as reachable**: no netlist and no simulation
  model, first section only for a multi-section part, first device variant only, no stackup, no
  derived symbols. PL1 §6 named multi-section parts and a package-variant chooser as the cheaper
  next step *because they affect files circuitRF can already read*; landing five more readers has
  made that argument stronger, not weaker — there are now more paths to a part whose second section
  is reported rather than imported.
- **No writer of any of these formats.** §5, and unchanged from PL1.

### 9. One reader-independent fact worth not re-investigating

Two formats describing the same part can disagree about the NAME of a pin, with neither being wrong.
Where a part has two pins that genuinely share a name, each exporter invents its own disambiguating
suffix, and two of them assign that suffix to opposite pins. Both files are internally consistent —
the cross-checks in §2 correctly stay silent — and the pads, coordinates and electrical map agree
exactly. **This is a property of the source files, not a reader defect, and it is not fixable from
this side.**


## Phase PL1 — COMPLETE: component library import (footprint + symbol + the map) (2026-09-05)

`docs/sonnet-briefs/brief-PL1-component-library-import.md`. One menu item — **File ▸ Import ▸
Component…** — that takes a file or a folder, scans for the files an import can use, and writes ONE
cell: a `.csym`, one `.clay` per density variant, the pin↔pad map shared by both views, the free text
the file states as read-only parameters, and a copy of the bytes each view was built from.

Gate: `dotnet test tests/Ui.Tests` — 11,797 tests green, 47 of them new — and `tests/Firewall.Tests`
(10, green). An earlier run of the same suite reported three failures on paths this change does not
touch (`LayoutSnapPrewarmTests`, `SharedLibraryConcurrencyTests`, `BrokenInstanceVisibilityTests`);
each passes in isolation and each counts filesystem calls against `CellStat.Freshness`'s time window,
so they are load-dependent rather than caused here. Recorded because the same three will surface again
under load.

### What the code does

| Piece | Where | Does |
|---|---|---|
| `ComponentClassifier` | `src/Design/Layout/Interchange` | classifies one file from its first 8 KB |
| `ComponentFolderScan` | " | walks a folder, classifies, returns ranked candidates + a skipped summary |
| `ComponentSymbolSexprReader` | " | `.kicad_sym` → `ComponentSymbolDrawing` (mils, +y up) |
| `ComponentSymbolLegacyReader` | " | `.lib` → the same |
| `ComponentLibraryXmlReader` | " | `.lbr` → symbol, package and the separate pin↔pad table |
| `PcbReader.ReadFootprint` | " | `.kicad_mod` (both epochs) → `PcbFootprintCell` |
| `ComponentTerminals.Build` | " | assigns `PortIndex` once, for both views |
| `ComponentRead` | " | reads one candidate into one `ComponentPart` |
| `ComponentImport` | `src/Ui/Layout` | reconciles layers, writes the cell folder |
| `ComponentImportChooserDialog` | `src/Ui/Views/Dialogs` | shows the ranked list |

### 1. The spike's four findings, as measurements

**(1) R-PL1-17's exact-mil mapping HELD, and nothing is fitted.** `SymbolModel.cs` states 100 local
units per connection-grid square P and `DsnSymbolReader.PinGrid` is `100.0`, so one local unit is one
mil. The scale handed to `KitTemplateSymbol.BuildFromDrawing` is the literal `1.0`
(`ComponentImport.SymbolScale`) — not `ChooseKitScale`, not `ChooseScale`, not clamped. The two symbol
epochs of the gate fixture agree exactly: a pin stated at `2.54 mm` and the same pin stated at `100`
mil both land on local 100, every pin coordinate is a whole multiple of 100, and the two epochs' nine
pins compare equal on X, Y and PortIndex (gate 8). The fallback the brief allowed for — a fitted scale,
at the cost of imported and hand-drawn symbols no longer sharing a grid — was not needed.

*Not covered by that:* a part drawn on a 50-mil pin grid would collide two pins on circuitRF's 100-mil
connection grid. `ComponentImport.BuildSymbol` counts the pins the snap moved and reports the count, so
it is not silent, but no fixture exercises it — the count is a report rather than a tested behaviour.

**(2) The synthesised two-copper-layer table expands the wildcards exactly as R-PL1-13 predicted.**
`PcbReader.SynthesiseFootprintLayerTable` declares `F.Cu`/`B.Cu` as `signal` plus
`PcbLayerNaming.TechnicalRows` as `user`; `ExpandLayerSpec` then resolves `*.Cu` → 2, `*.Mask` → 2,
`*.Paste` → 2, and a through-hole pad lands on exactly `F.Cu`, `B.Cu` and `Drill` (gate 11c). The
existing `ExpandLayerSpec` needed no change — it already keys on the table's TYPE word rather than on a
name or an ordinal range, which is what made a synthetic table work.

**(3) Every pad shape the S-expression format can state is already expressible.** Read out of
`PcbReader.BuildPadShape`: `circle`, `rect` (cardinal and non-cardinal), `oval`, `roundrect` (including
`chamfer`), `trapezoid` (including `rect_delta`) and `custom` (anchor UNION every primitive, plus each
primitive's own pen width) all map onto `LayoutShape`. **Nothing was found that `LayoutShape` cannot
express.** The XML library's pad vocabulary — `round`, `square`, `octagon`, `long`, plus
`<smd roundness>` — is likewise covered.

**(4) Multi-section and multi-variant are handled by REPORTING, and both paths are exercised.** The XML
fixture states two `<gate>`s and two `<device>`s; the second of each is named in Messages and neither is
merged nor dropped (gate 14). The S-expression symbol format's `_<unit>_<style>` sub-symbol suffix is
read the same way — unit 0 and unit 1 at style 1 are the drawing, everything above is named.

### 2. How much of §5 was genuinely new code

**Almost none, and the honest number is 94 lines.** `git diff --stat` on the two files §5 touches:
`PcbReader.cs` +82 (the `ReadFootprint(text, dbuPerMicron)` entry point, its result record, the
two-root-tag guard and `SynthesiseFootprintLayerTable`) and `PcbLayerNaming.cs` +12 (exposing the
technical rows the writer already transcribes). **Zero lines of the existing footprint reader
changed** — `ReadFootprint(PcbNode, Ctx)`, `ReadPad`, `BuildPadShapes`, `Drill`, `ExpandLayerSpec` and
every `fp_*` graphic are called unmodified, and `PcbReader.Read`'s own root-tag guard was left as it
was (gate 11b asserts the board reader still refuses a footprint).

**This sizes PL2 downward, not upward.** The footprint half of a new format is the cheap half only
where that format's footprints are already the board format's; the ~3,300 lines this phase added are
almost entirely the symbol side, the pin↔pad join, the classifier and the folder scan — none of which a
new format reuses beyond `ComponentPart` and `ComponentTerminals`. Per format, expect roughly the size
of `ComponentSymbolLegacyReader` (275 lines) for a simple text grammar and of
`ComponentLibraryXmlReader` (697) for one that carries its own packages and layer table.

### 3. The two-flip trap — how each was proven, and what would have missed it

**Both halves of this import negate Y, for different reasons.** The FOOTPRINT flips because its source
is +y down and `.clay` is +y up (`PcbUnits.Y`, unchanged from L4d). The SYMBOL flips because the source
symbol formats are +y **UP** and `.csym` is +y **DOWN** (`SymbolModel.cs`: "+x right, +y down (screen
convention)"). Reasoning "the layout is y-up so the symbol must be too" gets the symbol backwards, and
it then renders upside down while the footprint beside it renders correctly.

Proven as two independent tests over ONE fixture asymmetric on both axes in both views:

- `Gate7a` asserts the footprint's L-shaped silkscreen outline coordinate by coordinate — 14 numbers,
  Y negated and X untouched.
- `Gate7b` asserts, separately, that a symbol pin stated at `+7.62 mm` lands at `−300` and that the
  symbol's asymmetric corner mark keeps its handedness.

**What would have missed it:** any fixture whose art is symmetric in one axis — a plain rectangular
body with pins on the left and right. It imports identically whether the flip happened or not. The same
applies to the arc: `Gate10` asserts the SWEEP DIRECTION (`−90°` in the file becomes `+90°` here).

A third part of the same trap: **the arc's ANGLES must NOT be negated alongside its coordinates.**
`KitSymbolArc` states its angles counter-clockwise while circuitRF's arc primitive measures them
clockwise, and `KitTemplateSymbol.Convert` already flips both fields — which is the sign change
negating Y calls for. Flipping them in `ComponentImport.FlipY` as well would cancel it out and leave a
correct-looking arc drawn from the wrong end. `FlipY` therefore negates `Cy` only.

### 4. What an imported part still cannot do — limitations, not omissions

- **No netlist, no schematic view, no simulation model** (R-PL1-6). `Gate17c` asserts the `schematic/`
  folder is empty and `PrimarySchematic` is null. Placing one and elaborating will not produce a device.
- **No multi-section parts.** The first section is imported; the rest are named.
- **No package-variant chooser.** The first `<device>` is imported and the rest are named. Density
  variants of ONE pattern are the exception and become sibling `.clay` views (R-PL1-25).
- **No stackup, and nothing invented in its place** (R-PL1-27). `ComponentImport.ImportResult` has no
  stackup field, the destination technology's own is untouched (`Gate16`), and one Messages line states
  what an EM run still needs.
- **No derived (`extends`) symbols.** Reported; only what the definition states itself is read.
- **No binary formats, no 3D models, and no DXF fallback** (R-PL1-30) — a dimensioned drawing is
  classified as one and listed in the skipped summary.
- **No writer of any of these formats** (§13).
- A pin bonded to several pads gets one symbol pin on the FIRST of them; the rest are terminals with
  copper and no drawn pin, reported as such.

### 5. The folder chooser's ranking — UNVERIFIED against real files, and that must stay visible

> **ANSWERED 2026-09-05, and the predicted cost was real** — see "Phases PL1/PL2 — post-implementation
> review" at the top of this file, §2. The paragraph below correctly names the opposite error
> ("a folder holding two unrelated parts in the same format is offered as one candidate") and
> understates it: for a multi-FILE set the reader takes the first file of each kind, so the other parts
> were dropped with nothing said. Grouping now happens before ranking. §6's own conclusion below still
> stands on its own terms.

**§15's question 5 cannot be answered from what this phase tested, and is not.** The ranking is
exercised only against the synthetic tree `Gate2` builds (`toolA`…`toolE`, five subfolders, two
readable). What that test DOES establish is the property the ranking rests on: **classification is by
content, never by name**, proven the hard way — the footprint sits in a folder called `symbols` under
the name `part.txt` and is found, while the file named `part.kicad_sym` holds prose and is reported as
unreadable text.

Two decisions inside the ranking that only real files would settle:

- **Candidates are grouped by FORMAT FAMILY, not by folder.** A symbol file and the footprint files it
  pairs with may sit in sibling folders, so grouping by directory would split one importable component
  into two incomplete candidates. The cost is the opposite error: a folder holding two unrelated parts
  in the same format is offered as one candidate.
- **Confidence order is S-expression, then XML, then the older text symbol format**, on the ground that
  the first reuses L4d's whole footprint path. That is a claim about reader maturity, not about files.

### 6. Is PL2's breadth worth building? Not on this evidence

PL1 ships `.kicad_sym`, `.kicad_mod`, `.lib` and `.lbr`. PL2's own §1 already concludes its breadth buys
nothing beyond that, and this phase produced no evidence to the contrary — it produced no evidence about
real files at all. The measurement that would change the answer is the one §5 above says is missing: run
`ComponentFolderScan.Scan` over several real component folders and count how many rank
`SymbolFootprintAndMap` at the top. Until that number exists, PL2 is speculative breadth, and the
cheaper next step is one of PL1's own limitations — multi-section parts, or a package-variant chooser —
both of which affect files circuitRF can ALREADY read.

### Two smaller things worth knowing

**A `.lbr`'s DOCTYPE would have read as a corrupt file.** `XDocument.Parse` prohibits DTDs outright and
throws "For security reasons DTD is prohibited" on the second line of an ordinary library.
`ComponentLibraryXmlReader` parses through an `XmlReader` with `DtdProcessing.Ignore` and a null
`XmlResolver` — tolerated, never resolved, and no entity in it can expand.

**The XML format is recognised by its STRUCTURE, not by its root element's name.** `<library>` wrapping
`<packages>`/`<symbols>`/`<devicesets>` is the test, in both the classifier and the reader, so a file
whose root element is named anything at all still imports. The fixture's root is `partlib`, which is
what proves it.

## A technology imported from Gerber greeted the user with 22 validation messages describing 2 facts (2026-09-04)

Owner report: opening a `.ctech` imported from a real 21-file Gerber set filled the Technology
editor's banner with a wall of warnings, and the Messages panel with the same wall on every workspace
load. Measured on the reported file: **22 messages, 2 facts.** Three separate causes, in the order
they were found.

### 1. Every layer claimed the Gerber suffix "art", and that is not a cosmetic clash

The board was written as twenty `.art` files (`GERB_01_Top_Layer.art`, `GERB_02_Layer_2.art`, …) — an
ordinary convention where the extension means "artwork" and the STEM says which layer. R-L4g-7 records
the source extension as the minted layer's `GerberSuffix` *unconditionally*, so all twenty claimed
"art", and the pairwise collision check reported that nineteen times.

**A `GerberSuffix` is a layer ALIAS, and a shared extension cannot be one.** The nineteen warnings were
the small half of it; the alias was already broken in both directions:

- `GerberExport.Write` names each file `<cell>.<suffix>` and disambiguates a repeat as
  `<cell>.<suffix>_<layer>_<datatype>` — so the very names R-L4g-7 exists to preserve were not
  preserved for nineteen of the twenty layers anyway.
- `GerberLayerIdentity.SuffixOwner` (rung 2 of the identification cascade) is a `FirstOrDefault` over
  the destination technology's layers. **A re-import of that same set against that technology would
  have identified all twenty files as "Top Copper"** — every one of them then falling into
  `BuildSourceLayers`' "resolved to the same technology layer as an earlier file" branch.

Fixed at the source: `GerberImport.AliasableExtensions` records the extension only for an extension
that identifies exactly ONE file in the set. Everything else is left unset, where the export's own
`G{layer}_{datatype}` fallback is at least unique. The rule is per-extension, not per-set — a real
board of many `.art` files beside one `.rou` drill file keeps the `.rou` alias, which is what the
reported file's re-import now produces. A set of distinct extensions (`.gtl`/`.gbl`/…) is untouched,
which is what L4h's byte-identity round trip runs on, and R-L4g-7's own gate
(`EachImportedLayerCarriesItsSourceExtensionAsItsGerberSuffix`) is unchanged.

### 2. The via reported three problems, none of which could be answered

A Gerber set with no job file carries **no stackup information anywhere in the files**, so the import
mints a `StackupKind.Via` entry (the drill layer must be marked as one) into a stackup with no
conductor entries at all. `TechValidation` then reported the span-from end, the span-to end, and
`Fill = Plated` with no wall thickness — three messages, all of them unfixable until conductors exist,
and none of them naming the reason they cannot be fixed.

The import is right not to invent a substrate (its own comment says so, and that stands). The
validator now says the one true thing — the stackup names a via but no conductor layers — and skips
the three checks that condition makes unanswerable. **Scoped to that condition only:** a technology
that HAS conductors and names the wrong one is a typo and is still caught per end.

### 3. One message per shared alias, not one per additional claimant

`ValidateInterchange` reported collisions pairwise: first-vs-second, first-vs-third, … Now it groups
by the shared VALUE and reports it once, naming up to three layers and counting the rest
(`20 layers ("Top Copper", "Inner 1", "Inner 2" and 17 more) share the Gerber suffix "art"`). A PAIR —
the case someone actually mistyped — still names both, which is what the existing gate asserts.

### `Validate` grew a sibling, and kept its own shape

`TechValidation.Analyze` returns `TechProblem(Area, Message)`, attributing each problem to the editor
tab whose fields would fix it. `Validate` is now a projection to the messages alone, unchanged in
signature — the ~30 existing call sites and tests were not touched. What the editor does with the area
is in `src/Ui/RESOLVED.md`.

### Measured

The reported file, before: **22**. After: **2** (one Stackup, one Interchange; none on Layers). A
fresh import of the same folder: **1** — the stackup one, which is a true statement about what a
Gerber set without a job file can carry.

Gates: `tests/Ui.Tests/TechValidationNoiseTests.cs` (all three collapses, plus the two scope limits),
and two additions to `tests/Ui.Tests/GerberImportTests.cs` for the shared-extension rule.


## `CellHierarchy.InstanceBbox` takes an optional layer filter, and a filtered answer is never cached (2026-09-04)

Added for the clipboard's graphic export, which sizes its page from what will be PAINTED and therefore
must not be sized by a layer the user turned off (owner report; the full account is in
`src/Ui/RESOLVED.md`'s layout-editor misc-round entry).

**Null is the default and means "measure everything", which is what every interactive caller wants.**
The spatial index, hit-testing and the renderer's LOD decision all cull against where geometry IS, not
against what is currently painted — a hidden layer's shapes still occupy their coordinates, and a
picking query that pretended otherwise would be wrong in a way that only shows up when a layer is
toggled. An EXPORT is the one caller with the opposite need.

**A supplied filter bypasses the `ShapesBbox` memo.** That cache is a `ConditionalWeakTable` keyed on
the `LayoutView` REFERENCE alone — it has no room for "which layers were asked about" — so storing a
filtered result would hand a hidden-layer-less bbox to the spatial index the next time anything asked
without a filter, and the geometry would simply stop being pickable. The filtered path unions directly
and stores nothing. It is O(shapes) rather than O(1), which is why the parameter exists at all instead
of being unconditional: the memo is there because a generated cell can hold a six-figure via field and
`InstanceBbox` is called per placement, per frame.


## `LayoutPersistence.LoadFromFile` is interruptible, and the hooks are on the shape loop (2026-09-04)

An overload takes a `CancellationToken` and an `Action<int,int>` progress callback, for the caller
that has moved the read onto a background thread and owes the user a progress row and a Cancel (see
`src/Ui/RESOLVED.md`'s "The whole UI crawled…" entry for what asked for it).

**Both hooks land on the SHAPE LOOP, and the placement is the finding.** Reading a layout is not
proportional to how big the file looks: `LayoutClipper.EnsureValidHoles` runs over every shape, and on
a Gerber-imported board — thousands of composited pours, each with hundreds of holes — that is seconds
to tens of seconds, against a JSON parse measured in hundreds of milliseconds. So the loop is both the
only place a cancel can land promptly and the only phase with an honest denominator; the parse ahead of
it is one indeterminate step.

Every 256 shapes, not every shape: at these counts a callback and a token read per shape would cost
more than the normalization they are reporting on, and a progress bar cannot show more than a few
dozen steps anyway.

**Cancelling throws rather than returning a half-built view.** A partially loaded layout is
indistinguishable from a corrupt one to everything downstream, and this is a document the user is
waiting to see — not a partial result worth salvaging.

`Deserialize(string)` and the parameterless `LoadFromFile(string)` are unchanged in behaviour; the
version check moved into a private `ParseFile` so the interruptible path could put a cancellation point
between the parse and the shape loop without a second copy of that rule.

## The moved-cell forwarding record, and why the redirect lives in `ExternalCellRef` (TM2, 2026-09-04)

`brief-tree-move-2-moves-across-a-shared-library.md`. The `src/Ui` half — the report, the three surfaces,
the adoption gesture and the measurement — is in `src/Ui/RESOLVED.md`; this is what landed on the
framework-free side, and it is the half that makes `circuitrf convert` and `circuitrf em` resolve a
moved reference with no code of their own.

### `Workspace/MoveRedirects.cs` — reading the record, not just writing it

TM1 already wrote `.cmoves`. TM2 adds `Resolve` (longest-prefix match, chained, hop-capped and
cycle-guarded), `RootAbove` (which root owns a reference) and `CanRecord` (whether the safety net can be
laid at all), plus an atomic replace on `Append` and a memo on both read paths.

**`RootAbove` cannot be `WorkspaceRootFinder.WorkspaceDirOf`, although R-tm2-8 says it can.** That helper
walks up for a `.cws` and answers null for a bare-directory library — and a bare-directory library is the
case the whole feature is about (`WorkspaceScanner.ResolveLibrary` accepts one; it is R-tm2-5's decisive
reason for `.cmoves` being a file of its own rather than a `.cws` section). Using it would have produced
something that passed every test written against a workspace and did nothing in the field. `RootAbove`
walks up for a **`.cmoves`** and **stops at the first `.cws`, inclusive**: that directory is a workspace
root, and a root above it owns a different tree and cannot have recorded a move of this cell. That stop is
also what bounds the walk on a path with no project above it.

**`CanRecord` is a real write, not an attribute read** — SL2 R-sl2-1's rule, because a share ACL, a POSIX
mode and a read-only mount are all invisible to `File.GetAttributes`. It additionally opens an existing
`.cmoves` for write, which is the case a create-a-probe-file test misses entirely: the DIRECTORY is
writable and the FILE is not, so the probe says yes and the record is then lost. It leaves nothing behind
— in particular no empty `.cmoves` for a move that is refused for some other reason.

**`WorkspaceLock` is NOT the instrument R-tm2-15 asks for**, and §8 predicted this. It is advisory by
design — `Take` *overwrites* a lock someone else holds, and its own doc comment says treating it as
authoritative would produce a stale file that locks out a team — and it is per-workspace, with no notion
of a library root that is not a workspace. Gating a write on it would be reading it as the thing it
refuses to be.

### `Workspace/ExternalCellRef.cs` — the one resolution point, now with two extra jobs

The redirect goes in `ResolveCellDir` and nowhere else. That is this type's own standing rule — a call
site that splits the reference forms itself is a call site that will be missed — and it is what gives the
CLI the behaviour for free. **Checked rather than assumed:** every stored-reference resolution site in the
repo does route through it, including `CellLayoutResolver`, `PcbExport`, `GdsiiExport` and `DxfExport` on
this side. The list is in `src/Ui/RESOLVED.md`.

**The order is existence-then-redirect, and the existence answer is now RETURNED.** `ResolveCellDir` has
to ask `Directory.Exists` for R-tm2-8's step 2, and `CellSymbolResolver` was asking the identical question
three lines later — so the four-argument overload hands the answer back rather than letting the caller
re-ask. Asking twice cost a **fifth** filesystem round trip per referenced component per edit in the
uncached world, which is exactly the number SL4 R-sl4-6's gate pins, exactly so it cannot drift up one
call at a time. It went red, which is the gate doing its job. With the cache on, both the old and the new
code cost 4 cold and 0 warm.

`MoveRedirects.Resolve`'s own existence checks go through `CellStat` too, so a redirect's cost is counted
rather than invisible — and safely, since `CellStat` never caches a negative and a dead-end rung of a
chain must be re-asked.

## What a cell reference costs, counted — and an advisory lock that claims no authority (SL4, 2026-09-03)

`brief-shared-library-4-concurrency-and-latency.md`. The `src/Ui` half of this — the measurement table, the
tree's referenced-subtree rule, and the design decisions behind both — is in `src/Ui/RESOLVED.md`; this is
what landed on the framework-free side.

### `Cells/CellStat.cs` — the counting seam, and a cache that is opt-in per CALL SITE

Every filesystem call on a cell reference's resolution path goes through one type, so its cost is a **number**
(`CellStat.Calls`) rather than an intuition. That is the brief's own rule (R-sl4-6) and the repo's: a timing
assertion measures the machine, flakes under parallel test load and inverts under a debug build; a call count
describes the algorithm and reads the same everywhere. Measured: **4 calls per referenced component per edit**
— `Directory.Exists` on the cell folder, `Directory.Exists` + `Directory.GetFiles` on its `symbol/`, and the
primary's mtime, all before the symbol cache can be consulted, because the mtime IS its key — and 6 when the
folder holds more than one symbol.

Positive answers are then cached for **`CellStat.Freshness` = 2 s** (R-sl4-7), which is the one guarantee the
shared-library series traded away and is stated on that field in full. **A negative is never cached**
(R-sl4-8): a cell folder that was not there, a `symbol/` with no `.csym` in it, an mtime for a file that is
not present. Caching "not found" for even a second turns a share that blinked into a design full of
Not-Found glyphs that persist after the network recovers.

**The trap, and it is a boundary rather than a value.** `CellFolder.ResolvePrimary` is shared by
`CellSymbolResolver.Resolve` AND by the project tree's own scan (and by every cell node view model, three
times each). Caching *inside* it silently applied a bound justified for a network wire to a file the user had
just written themselves — `WorkspaceScannerTests.Rescan_ContradictionAppearsWhenPrimaryFileDeleted` and its
restore twin caught it. `ResolvePrimary` therefore takes `useStatCache:` (default **false**, which is exactly
the pre-SL4 behaviour) and only the reference resolver passes true. Both callers still COUNT — the counting
seam and the caching policy are separate questions, and conflating them is what went wrong the first time.

Dropped by `WorkspaceRootFinder.InvalidateCache`, which now clears four memos on one lifecycle: the walk-up,
`ExternalCellRef`'s alias table, `WorkspaceWritability`'s probe and this. A memo with a lifecycle of its own
is the one that goes stale.

### `Workspace/WorkspaceLock.cs` — advisory, and it must read as advisory

`.crf-open.json` beside the `.cws`: user, host, pid, time. Written when the workspace is opened **and is
writable** (a read-only workspace takes none and needs none — nobody can write it), removed on close, and
released only when it is ours.

- **No open file handle** (R-sl4-4). `CrashReporter` holds one with `FileShare.Read` so an exclusive open by
  a probe proves ownership, and the single-instance check uses the same idiom; both are right **locally**.
  Those guarantees do not survive SMB, NFS or a dropped connection, and a handle-based lock over a share
  fails in the direction that produces a confident false statement about another person.
- **Two independent staleness rules, and the host scoping is load-bearing.** Rule one is *this host* plus a
  pid that is not running; rule two is age, 8 hours. Scoping rule one to this host is not tidiness — a lock
  from another machine, checked against local pids, reads as abandoned whenever that pid happens to be free
  here, which is a confident "they have gone" about a session that has not. That is the exact false statement
  R-sl4-2 exists to bound, and it was observed once during the work before the scoping was added.
  "Cannot tell whether a process is running" reads as ALIVE, deliberately.
- **A malformed lock file is no evidence at all**, and is ignored rather than treated as a refusal. Refusing
  to open a workspace over an unreadable file is precisely the stale-file failure the design forbids.

### `Workspace/WorkspaceWritability` — read-only by CHOICE reuses read-only by permission

`OpenReadOnlyThisSession(root)` marks a workspace and everything beneath it unwritable for this session,
checked before the memo and before the probe. It is a **prefix** rule, not set membership, because the
question is asked about a document's own directory far more often than about the root (R-sl2-4) and marking
only the root would leave every file inside it saveable.

This deliberately reuses SL2 rather than adding a second concept. Everything a read-only workspace does — the
`.cws` write choke point skipping silently, Save disabled with a reason, Save As on quit, the provenance
band, the generated-cell wipe not running, the PCell refusal naming the workspace — is already built and
already tested, and "a workspace we have chosen not to write" wants identical behaviour from every one of
them. A parallel flag would have been the one that is true in fourteen places.

## Writability is DISCOVERED, and `.cws` writes now have a choke point (2026-09-03)

`brief-shared-library-2-read-only-workspaces.md` R-sl2-1/-2/-3/-6. Two changes here; the behaviour that
hangs off them is in `src/Ui/RESOLVED.md`.

**`WorkspaceWritability` sits beside `WorkspaceRootFinder`, not in `src/Ui`,** for the ordinary reason:
`src/Cli` writes workspaces too and cannot reference Avalonia. It answers "can a file be created in this
directory?" by creating one and deleting it. `File.GetAttributes` reports the DOS read-only bit and says
nothing about a share ACL, a POSIX mode or a read-only mount option; `Directory.Exists` says nothing at
all. **The only portable answer is to try**, which is why there is no cheaper implementation waiting to
replace this one.

Its memo is dropped by `WorkspaceRootFinder.InvalidateCache` rather than on a lifecycle of its own —
that call already drops the ancestor walk-up and `ExternalCellRef`'s alias table, and a third memo that
had to be invalidated separately would be the one that went stale.

**`WorkspacePersistence.SaveToFileAtomic` returns `bool` and is now the guard.** It skips the write and
returns `false` when the containing directory is unwritable. The guard is at the LOWEST level on purpose:
there were fifteen call sites and no choke point (reads have had one — `TryLoadCws` — since the
beginning), and a rule fifteen callers have to remember is a rule that is true in fourteen places. A
sixteenth site inherits it without knowing the rule exists.

**A trap for anyone adding a `.cws` writer:** `SaveToFile` (non-atomic) is still public and is NOT
guarded — it exists for test fixtures and for the doc-fixture generator, which build throwaway workspaces
under a scratch directory. Production code must use `SaveToFileAtomic`;
`ReadOnlyWorkspaceTests.EveryCwsWriteInProductionCodeGoesThroughTheChokePoint` is what says so.

**`FileOptions.DeleteOnClose` is not a crash guarantee on Unix — measured.** It is a kernel flag on
Windows; on Unix .NET emulates it by unlinking at handle close, and a `SIGKILL` closes no handles, so
a process killed mid-probe leaves the file. `Probe` therefore sweeps stale `.crf-write-probe-*` files
(age cut-off five minutes, so a concurrent probe is never touched) rather than trusting the flag. It
matters because the project tree hides only `.DS_Store` and `*.source`, not dotfiles generally.

**A second trap, in the probe's failure mode:** `AtomicFile.WriteAllText` has never created the target
directory, so a `.cws` write into a directory that does not exist used to throw. It now returns `false`
silently instead, because a probe of a non-existent directory answers "read-only" — the same answer, for
the same underlying reason, delivered quietly. A caller that depended on the exception must check the
return value.

## `${NAME}` in a stored cross-workspace path, and why it lives here (2026-09-03)

`brief-shared-library-1-reaching-the-library.md` R-sl1-5/-8. `PathTokens` expands `${NAME}` from the
environment in the three `.cws` fields that name a location OUTSIDE the workspace —
`ReferencedWorkspaces[].Path`, `LibraryRefs`, `KnownFiles` — so a librarian can hand out a starter
workspace whose library reference works on every engineer's machine. One user's `Z:\eda\stdlib` is
another's `\\server\eda\stdlib` and a third's `/Volumes/eda/stdlib`; the alias indirection already meant
each user repaired that once, but a site-wide `.cws` template was impossible.

**It is in `src/Design/Workspace/`, not in `src/Ui`, and that is the load-bearing part of the decision.**
`ExternalCellRef.ResolveOtherRoot` already re-implements `WorkspaceRefs.Resolve`'s rule in three lines
rather than calling it, and its own comment says why: `WorkspaceRefs` is in `src/Ui`, on the far side of
the firewall, and a headless `circuitrf convert` or `em` run resolves these references too. A token
expander sitting in `src/Ui` would resolve a tokenised alias in the GUI and silently fail to in the CLI —
the two would disagree about what the same `.cws` means. Gated by a test that resolves a tokenised
`ws://` reference through `src/Design` types alone.

**Three traps, all of which produce a plausible wrong answer rather than an error:**

- **An unset variable must NOT expand to empty.** `Environment.GetEnvironmentVariable` returns null, and
  substituting empty turns `${CRF_LIB}/stdlib/v2.3/.cws` into `/stdlib/v2.3/.cws` — a ROOTED path that
  resolves to somewhere real on some machines and reports a missing folder on others. `TryExpand` returns
  false with the offending token, callers report a broken reference naming it, and nothing is ever
  half-expanded (an unset token in the middle leaves the whole string untouched).
- **One syntax on every platform.** `${NAME}` only — never `%NAME%`, never bare `$NAME`. A `.cws` travels
  between machines; a per-platform spelling resolves on the machine that wrote it and nowhere else.
- **A `CellRef` is never expanded.** It is the workspace-relative remainder and has no business naming a
  machine — a token there would be a second place a cross-workspace path can hide, which is exactly what
  the `ws://` alias form exists to prevent. `ExternalCellRef.ResolveCellDir` expands the alias's stored
  PATH and leaves the remainder verbatim, in both the `ws://` and the plain relative form.

Nothing ever WRITES a token: circuitRF writes a plain path, and a token is what a librarian or a site
template types by hand — the same treatment R-mw2-5 gives the raw relative `CellRef` (resolve it, never
produce it). There is deliberately no token *definition* mechanism: the environment is where a site
already configures this on all three platforms, and a second definition site would need precedence rules
of its own.

## The interchange stack moved here, and `circuitrf convert` is what it bought (2026-09-02)

The layout interchange readers and writers — GDSII, DXF, Gerber, Excellon and `.kicad_pcb`, ~16,700
lines across 61 files — moved from `src/Ui/Layout/Interchange` to `src/Design/Layout/Interchange`,
namespace and all. The `em` verb's own carve-out (`brief-cli-em-verb.md` R-emcli-1/R-emcli-4) is the
precedent it followed, including the rule that the namespace changes with the project.

The point of the move is the CLI: `src/Cli` cannot reference `src/Ui`, so a headless conversion had
to have the readers on this side of the wall. `src/Ui/RESOLVED.md` §"A headless import verb, and what
moving L4e-L4g to `src/Design` would cost" scoped exactly this and left it unattempted; the numbers
below are what it actually cost.

### 1. What had to move with it, and the one thing that could not

Seven `src/Ui/Layout` files went too, all framework-free as written: `LayoutFragment`,
`LayoutLayerMapping`, `FallbackPalette`, `LayoutViewport`, `PinInference`, `LayoutDesignFlatten` and
`LayoutTextFlatten`. None of them needed an edit beyond its namespace line, and none of their five
other consumers in `src/Ui` needed one either — `src/Ui/GlobalUsings.cs` already carries
`CircuitRF.Design.Layout`, so a type moving INTO that namespace is invisible to every file that used
it. The whole `using` churn across `src/Ui` was one added line for `…Layout.Interchange`.

**`LayoutTextOutline` was the one genuine obstacle, and `src/Ui/RESOLVED.md`'s scoping said it was:
it depends on Skia, so "GerberExport must NOT move".** That prediction was half right. SkiaSharp is
explicitly ALLOWED across the firewall (`tests/Firewall.Tests`: "headless 2D graphics is not a UI
framework"), so glyph geometry crosses fine; what does not is `SkiaFonts`, which loads the embedded
IBM Plex faces through Avalonia's `AssetLoader` and needs a live app host. So the split is not
import-here/export-there. It is **one line lower down**: `LayoutTextOutline` moved with everything
else and gave up only its font SOURCE, now a
`Func<LabelFontStyle, SKTypeface>? TypefaceSource` that `src/Ui` fills in from a `[ModuleInitializer]`
(`UiTypefaceInstaller`) and that falls back to `SKTypeface.Default` when nothing did.

A module initializer rather than a call from `App.Initialize` because `src/Ui` has three entry points
(circuitRF, harmonicaRF, wBond) and a startup step that must run in all three is a startup step
somebody eventually forgets in one.

**The consequence is real and is reported rather than hidden:** a label flattened headlessly is a
different SHAPE from the same label flattened in the app, because the glyph outlines come from a
different face. `LayoutTextOutline.HasEmbeddedTypefaces` is false in that case and `convert` prints a
note whenever it flattened a label without them.

`ResolveLabelAnchor` moved out of `LayoutRenderer` into `LayoutTextOutline` for the same reason it
was shared in the first place: the renderer draws a label with it and the flattener places glyphs
with it, and the property worth protecting is that those two can never disagree. One copy, in the
project both callers reach.

### 2. `convert` is one import and one export, and the intermediate is a real cell

Every reader lands on a cell folder plus a technology and every writer starts from one, so the
N x N table of conversions is not N x N pieces of code. A conversion whose target is `.clay` stops
after the import; every other one runs the import into a scratch directory and exports out of it
(`--keep-cells` keeps that directory, which is the way to see what a conversion understood).

Two things the GUI answers with a dialog had to be answered another way:

- **The layer-mapping dialog** — handed a null callback, every importer already falls through to
  `LayoutLayerMapping.BuildChoices`, which is the same default the dialog pre-selects. Nothing to
  decide; the CLI just does not pass one.
- **The drill-format prompt is a REFUSAL, not a default.** Leading versus trailing zero suppression
  differ by four orders of magnitude on identical text (L4f §2), so `convert` prints the inference,
  its evidence and the artwork cross-check, names the three flags that answer it, and exits 1 having
  created nothing. `--accept-inferred-drill-format` takes the inference as it stands.

### 3. A null destination technology silently drops every layer — measured, not reasoned

The first working conversion wrote Gerber files named `via.G-2_0` from a technology with **zero**
layers. The cause is not in the move: every importer reconciles the file's layers against the
DESTINATION technology and returns the ones it would ADD, and handed a `null` destination there is
nothing to compare against, so `LayersToAdd` comes back empty and the layers arrive as bare numeric
keys with no names, no colours and no `GerberSuffix`. A re-export then names its files from a
synthetic suffix.

The fix is one line — `destTech ??= new Technology { Name = name }` — and the reasoning is that an
EMPTY technology is the honest destination for a conversion that has none: every source layer is then
an unmatched row, which is exactly what it is. **This is the failure mode to remember whenever a
headless caller reuses an importer**, because nothing errors and the result looks structurally fine.

### 4. GDSII is the one format that cannot carry names through, and that is the format's doing

The same fix does nothing for GDSII, deliberately. `GdsiiImport` does not apply the
NoMatch → AddToTechnology default that DXF, board and Gerber import all do (L4b's own divergence, and
it was reasoned about name-keyed formats). GDSII identifies a layer by a NUMBER, so an import has
nothing to name it with: numbers come through exactly, names do not. `--tech` pointing at the
technology those numbers belong to is the answer, and it is documented as such rather than papered
over. The gate asserts a non-empty layer table for every source EXCEPT gdsii, and says why.

### 5. Two smaller things the matrix exposed

- **`$MODEL` is DxfReader's own name for model space**, not something anyone typed, and it reached
  the Gerber writer as a file stem: `$MODEL.gbr`. A DXF's drawing is named after the file, so that is
  what `convert` calls it; `--name` overrides.
- **A `--to clay` result is not shaped the same way for every source.** Gerber import puts its whole
  result inside an `ImportFolder` of its own (R-L4g-13) while the others create cells directly under
  the parent. That is a real difference between the importers, and `convert` does not normalize it
  away — the gate searches recursively rather than pretending otherwise.

### 6. The firewall's text gate fired, and it was right to

23 exception messages appeared "below the UI firewall" the moment the code crossed it — unchanged
sentences that have been in the tree since the importers were written. They are all format invariants
(a truncated GDSII record, a shape type no writer has a case for, an unbalanced macro expression),
which is the deliberate plain-exception case `user-facing-text-allowlist.txt` describes, so they were
added there under a heading that says they moved rather than being authored.

### 7. What the gate proves, and what it does not

`tests/Ui.Tests/ConvertCliVerbTests.cs`, 32 tests, 7 s, untagged and in the routine gate. It launches
the built `CircuitRF.Cli.dll` as a real process (EmCliVerbTests' pattern, for its recorded reason) and
checks all 24 ordered format pairs plus byte identity against the in-process `GdsiiExport` and
`GerberExport` calls the GUI's own File ▸ Export makes.

**A GDSII file is not byte-comparable raw**, and the first version of this gate only looked like it
was: BGNLIB and BGNSTR record when the library and each structure were written, so two writes of the
same design differ at byte 21 unless they land in the same second. It passed for an afternoon and
then failed on a second boundary. Masked by record type and named, the way `EmCliVerbTests` names the
Touchstone provenance line — everything else still compares byte for byte, which is the point.

**It proves the two sides agree and nothing more.** The matrix's sources are built by `convert`
itself, so a pair is tested against our own writer's output, not against a third party's dialect —
the same limitation L4h's round-trip gate states about itself (R-L4h-16), and for the same reason.

**Stale after this change:** the root `CLAUDE.md` source map still describes `src/Ui` as the home of
the layout interchange code and lists seven CLI verbs. Neither is true now. Left for the owner.

## MIM-7 — a dielectric that is patterned with its plate, so ONE MMIC technology serves both (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-7-one-technology.md`. The extraction half is here; the shipped-file
merge, the editor row and the documentation are `src/Ui/RESOLVED.md` §MIM-7. **`src/Engine` is
untouched — the refusal, the via z-integral and the kernel are exactly as they were.**

### The premise that was actually wrong

circuitRF shipped two MMIC technologies that differed only by a capacitor module, and MIM-2 measured
two real reasons for the split (`src/Ui/RESOLVED.md` §MIM-2): a capacitor dielectric between the
interconnect metals makes every Metal1-Metal2 airbridge post cross a dielectric interface — which
`PlanarKernel.CanSolve` refuses for the WHOLE RUN — and it sits on a Metal1 line as superstrate, so
Z₀ falls 2.8%.

**Both costs come from the film being in the medium of EVERY run, including runs with no capacitor in
them — and the 2.5D premise does not require that.** It forces "laterally infinite per RUN"; it says
nothing about which runs a patterned film belongs to. Physically the nitride exists under the plates
and nowhere else, and the honest per-run proxy for "this run has capacitors in it" was already being
computed: **is the plate conductor among the run's levels?** The extractor's default level selection
is "every non-ground conductor that carries artwork", so an interconnect-only layout answers no with
no configuration at all. It is also the kernel's own suggested remedy, verbatim in its refusal text:
*"…or remove the interface if it carries no physics."*

### The field, and the two halves of the rule

`StackupLayer.PresentWithLayer` (`string?`, a conductor entry's NAME) — additive, nullable, no
`.ctech` `FormatVersion` bump, meaningless on a non-Dielectric entry: the `SheetAt`/`SpanFromLayer`
pattern. `TechValidation` requires an existing, non-ground Conductor and refuses the field on a
non-Dielectric entry. **"Name the conductor directly ABOVE" is a RECOMMENDATION, not a validation
rule** — a tie further away is expressible and honoured, only harder to read — so it is stated in the
field's own documentation, the editor's tooltip and the user page rather than failed.

When the named plate is not in the run:

1. the film's band enters the medium as **air** — εᵣ 1, tanδ 0, µᵣ 1, thickness untouched, so every
   band above it keeps the height the process states;
2. **`SheetAt = Top` on the conductor whose band sits directly BENEATH the film is treated as unset
   for that run.**

**(2) is what makes the gate bit-identity rather than "close", and it is not a convenience.** MIM-6
put Metal1's sheet on the top of its band expressly so a plate gap reads 0.2 µm; with no film there
is no gap to read, and the pre-MIM-6 placement is the established baseline for interconnect. Without
the revert the same airbridge would extract at z = 103/106 instead of 100/106 — a plausible answer,
3% out, to a question about a stack with no capacitor in it.

**A tie naming a conductor the stackup does not have leaves the film ACTIVE**, with a note. The other
choice would let a typo silently thin the medium, which is the failure the mechanism exists to
prevent. It is not a refusal, because the extraction is still a valid one — validation is where the
typo is called an error.

### BOTH extractors read it, and finding that was the one surprise

The brief named `PlanarExtractor`. Implementing only that left nine `Ui.Tests` failures, four of them
the acceptance tests: **`CrossSectionExtractor` builds its own layered medium from the same stackup**,
so a film left switched on there is exactly MIM-2's second cost — measured, not argued:
`Mmic_LineOnMetal1_...` came back at Z₀ 48.25 Ω against the hand-built 49.62, and the 72 µm line's
ε_eff at 8.54 against a (6, 8.5) band. Both pass with the tie honoured there too.

Its version of "in this run" is a set of one: a uniform-line cross-section refuses multi-level
geometry outright, so the question is "is the plate THE signal conductor". There is no sheet surface
to revert — that kernel models real metal of real thickness and never reads `SheetAt` (MIM-6's own
recorded decision).

So the rule lives in one file, `Em/PatternedDielectric.cs`, against this area's standing rule that
the two extractors restate the stackup rules rather than call each other. **That rule is about the
cross-section extractor's REDUCTION test and its refusals**, which must never appear on the planar
acceptance path. This is the opposite shape: one paragraph of policy and one sentence of user-facing
text — and the sentence is the reason. Two copies of the "your medium lost a layer" note would drift
into two accounts of one decision.

Mechanically both callers rebuild rather than patch: deactivating changes materials and z, and the
bands already in hand are re-resolved by their stackup INDEX, which the rebuild preserves. The
`Technology` object the caller passed in is never mutated (it is a live document, re-extracted at
every frequency of a sweep) — the affected entries are cloned field for field.

### The gates

- **`MimCapacitorTests.AnAirbridgePost_SolvesOnTheOneTechnology_AndExtractsIdenticallyToTheModuleFreeStack`**
  — the brief's own gate, and it flipped a test that asserted the refusal. Level names, every level z
  and thickness, every medium region's thickness/εᵣ/tanδ/µᵣ, the slab, the via's indices and its
  footprint areas: all compared with `Assert.Equal` on doubles, no tolerance. The comparison
  technology is DERIVED from the shipped one by removing the module, not restated.
- **`MimCapacitorTests.TheCapacitorRun_IsWhatTheRetiredSecondTechnologyProduced`** — the ACTIVE side,
  as literals captured from `MmicGaAsMim()` before the merge, because the object they came from no
  longer exists. 103 / 103.2 / 106 µm, medium 103 µm εᵣ 12.9 | 0.2 µm εᵣ 6.8 | 2.8 µm air, the plate
  via 1→2 at 3.6e-11 m².
- **`PatternedDielectricTests`** — the mechanism on a probe technology built in the test, so the
  assertions are about the rule rather than about what circuitRF happens to ship: both extractors,
  the note, the broken tie, named analysis levels overriding artwork, and the schema half
  (validation, `.ctech` round trip and absence when unset, merge conflict description, editor row).

`dotnet test tests/Ui.Tests` 10,364 passed / 0 failed; `tests/Firewall.Tests` 10/0.

### What did NOT come out bit-identical, and why it cannot

Two measured residuals, both outside the brief's stated gate and both stated rather than tuned away:

- **A Metal2 line's CLOSED-FORM substrate is 102.75 µm instead of 103** (−0.24%), with ε_eff a shade
  higher. `SubstrateResolver` sums dielectric bands and has no notion of an analysis level, so it
  cannot ask the tie's question — and teaching it would not close this anyway: skipping the film
  gives 102.55 µm, further away. The missing 0.25 µm is the plate METAL, and no closed-form path
  counts a metal band. Pinned in
  `MimCapacitorTests.TheClosedFormPathDoesNotReadTheTie_AndTheOnlyCostIsAMetal2LineBy025Micron`.
- **A run whose LOWEST analysis level is Metal2 gets a sizing εᵣ of 9.78 instead of 9.58** (+2.1%).
  `slabBands` sums the dielectric bands under the lowest level; the deactivated film is still a
  0.2 µm dielectric band and the plate's 0.25 µm is a conductor band, which that sum never counts —
  the same structural gap as above. Since MIM-4 the slab is a SIZING object only (calibration-standard
  geometry, the β seed, the near-radius floor, the mesh), never the published reference impedance.
  A Metal1-fed run — every de-embedded one, until MIM-4's ports move — is unaffected: its slab is the
  GaAs alone, bit for bit.

## MIM-4 — the stratified sub-feed refusal, retired (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-4-interior-static-greens.md`, gap 4 of the MIM series. The engine
half is `src/Engine/Mom/RESOLVED.md` §MIM-4; what changed HERE is `PlanarExtractor`.

**What was refused.** More than one dielectric entry between the ground plane and the lowest analysis
level: *"L9's Green's function handles a stratified medium happily — what does not is the
de-embedding … Merge the layers under the feed into one substrate entry, or wait for a static Green's
function at interior heights."* That merge was **a change to the physics offered as a workaround** —
two dielectrics in series under a trace are not one dielectric of either εᵣ — and the only reason for
it was that `C_pul` came from an image series over one grounded slab. MIM-4's
`InteriorStaticImages` removes the reason.

**What it does now.** The layers are carried at their stated thicknesses (`BuildMediumStack` always
built them; nothing there changed), and a note replaces the refusal.

**Two things worth keeping.**

1. **The `GroundedSlab` is now a SIZING object where the region is stratified, and the right average
   for that job is the series-capacitance equivalent** — `h/ε_eff = Σ d_i/ε_i`. It still sets the
   calibration standards' geometry, the branch-continuation β seed, the accelerated near-radius floor
   and the mesh; none of those is the published reference impedance any more. It reduces to the single
   layer's own εᵣ, bit for bit, when there is one, and it is what a wide line over the real stack
   converges to: 21.3% / 10.3% / 3.2% / 1.1% difference from the true stratified `C_pul` at
   W/h = 0.5 / 2 / 8 / 24. The note says out loud that the number is for sizing and never for the
   reference impedance — a number the user can see and misread is exactly the shape of thing that
   gets trusted silently.
2. **A stratified medium turns the general kernel on at ONE level too.** The explicit `MediumStack`
   used to be attached only when `levels.Count > 1`. Before this brief that was sufficient — a
   stratified region under the lowest level was refused, and with one level there is nothing above it
   in the stack, so a one-level problem was always one dielectric. Carrying the layers without also
   changing this would have handed L8's one-slab kernel a stack it does not describe:
   `generalMedium = levels.Count > 1 || mediumStack.LayerCount > 1`.

**Held by** `tests/Ui.Tests/Em/StratifiedSubFeedExtractionTests.cs` — it extracts, both layers reach
the medium, the sizing slab is the series equivalent while the medium is not, the note carries the
layer names and says what the effective εᵣ is and is not for, and a ONE-dielectric region is
unchanged (one layer, the slab's own material bit for bit, no note, and still the one-slab kernel
path).

## MIM-6 — the level reference surface: a conductor's sheet learns which surface of its band it sits on (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-6-level-reference-surface.md`, the fifth gap of the MIM series —
MIM-2's own finding 1. Extraction only; `src/Engine` untouched (a level's `ZM` is already arbitrary
there), and kernel A's cross-section path untouched (it models real metal thickness and has no sheet
to place).

### The problem

`PlanarExtractor` placed every conductor level's zero-thickness sheet at the BOTTOM of its stackup
band and absorbed the band's own z range into the dielectric ABOVE it. Both rules are right for
everything that came before — together they are what makes a microstrip's height come out as the
substrate thickness. Between two capacitor plates they are wrong: the lower plate's whole metal
thickness lands INSIDE the gap. The shipped MIM technology extracted its levels at
z = 100 / 103.2 / 106 µm, so the solver saw a **3.2 µm plate separation where the process states
0.2 µm — 16×** — with the whole 3.2 µm carrying the capacitor dielectric's εᵣ.

Not fixable by authoring: the gap is `Metal1.Thickness + MIMDielectric.Thickness`, `TechValidation`
requires a positive thickness on every band, and Metal1's sheet was pinned 100 µm above ground by
the microstrip case.

### The shape, and why the two halves are ONE choice

`StackupLayer` gains `SheetAt` (`ConductorSheetSurface?` — `Bottom`/`Top`), additive and nullable,
no `.ctech` `FormatVersion` bump, meaningless on a non-Conductor entry: the `Fill`/`SpanFromLayer`
pattern already in `TechModel.cs`.

**The absorption direction is not a second setting — it follows the surface, and that pairing is
load-bearing.** `PlanarProblem.CanSolve` refuses a level that is not on an interface of its own
medium (L9c's first earned refusal). Sheet at the bottom + band absorbed upward puts the sheet on an
interface; sheet at the top + band absorbed downward puts it on an interface. Either half alone does
not: a sheet moved to the top of its band while the band still went to the dielectric above would
land 3 µm inside a region, and every MIM extraction would refuse.

`Band` gained a `SheetM` alongside `BottomM`/`TopM`, and every z decision in the file — level z, the
ground-band query, the slab height, the slab-band window, the medium's cut set, `topOfInterest`, the
level ordering, the ungrounded refusal's "dielectric under this level" — reads `SheetM`.
`BottomM`/`TopM` stay the band's own extent, which is what the absorption arithmetic and the
conductor's reported `ThicknessM` are written in. **`SheetAt` chooses where the sheet is, never how
thick the metal is.**

`BuildMediumStack`'s absorption is one added branch: when an interval's midpoint is inside no
dielectric, find the CONDUCTOR band it is inside and ask its surface — `Top` takes the dielectric
whose top is this interval's bottom, anything else keeps the pre-existing "dielectric whose bottom is
this interval's top". That is why `Bottom` and unset are bit-identical rather than merely equivalent:
the old expression is still literally the else branch.

### The gate, measured

On the shipped MIM technology (`Metal1 = Top`), the series capacitor:

| | Before | After |
|---|---|---|
| Levels (Metal1 / MIM Metal / Metal2) | 100 / 103.2 / 106 µm | **103 / 103.2 / 106 µm** |
| Region between the plates | 3.2 µm at εᵣ 6.8 | **0.2 µm at εᵣ 6.8** |
| Region under Metal1 | 100 µm GaAs | **103 µm GaAs** |
| Slab height | 100 µm | 103 µm |

The airbridge post's kernel refusal is UNCHANGED and was re-measured, not assumed: the post now runs
z = 103 → 106 µm rather than 100 → 106, and it still straddles the plate dielectric's upper interface
at 103.2, so `PlanarKernel.CanSolve` refuses it by the same sentence with a different z in it.

### The notes now name the surface, and that is not decoration

A level at 103 µm on a conductor whose band runs 100–103 is either a mistake or a deliberate
reference-surface choice, and the run notes are the only place a user can tell which — the panel's
own stackup readback is bound to the CROSS-SECTION readback, which a full-wave run does not produce.
The multi-level note now reads `103 µm (top of 'Metal1'), 103.2 µm (bottom of 'MIM Metal'), …`.

### `SubstrateResolver` is deliberately NOT taught the field — the decision, with its measurement

The closed-form microstrip path sums dielectric thicknesses. On the MIM technology it and the EM
extractor now disagree about a Metal1 line's substrate by exactly one metal thickness: **100 µm
against 103**. Measured cost of teaching it the field instead: a 70 µm line's static Z₀ would go
**49.42 → 50.06 Ω, +1.3%**.

**Not taught, because the two numbers answer different questions.** Hammerstad-Jensen models real,
finite-thickness metal and takes that thickness as its own parameter `t`; its h is the physical
substrate — ground plane to the underside of the metal — which is what the process states. The
extractor's h is where a ZERO-thickness sheet was placed, a discretisation position rather than a
dimension. Feeding the sheet position to the closed form would count Metal1's 3 µm twice and move
every Metal1 microstrip on this technology to agree with a discretisation artifact. The discrepancy
is bounded by one metal thickness by construction and the run prints the number it used. Recorded in
`MimCapacitorTests.AMetal1Microstrip_ResolvesDifferentlyOnTheTwoTechnologies_ButAgainstTheSamePlane`,
which asserts both heights side by side so the divergence stays deliberate.

### What this brief does NOT claim

**No capacitance accuracy.** A 0.2 µm gap against micron cells is exactly MIM-3's unmeasured regime;
this fixes the geometry so MIM-3's ladder measures the real one. The raw-solve gate is still a
with-via/without-via comparison carrying no magnitude band (|S21| 3.92e-4 with the plate via against
1.49e-4 without, ratio 2.64 — re-measured at the new geometry), because a raw port's own
discontinuity dominates any absolute number, which is MIM-2's finding-2 retraction.

### Tests

`tests/Ui.Tests/Em/SheetReferenceSurfaceTests.cs` is the mechanism: unset ≡ explicit `Bottom` as a
WHOLE-EXTRACTION identity (levels, medium regions, slab, vias, polygon count and every note) over all
three shipped technologies; clearing the shipped `Top` restores 100 / 103.2 / 106 exactly; a purpose-
built stackup where the intervening dielectric is THINNER than the metal below it, so the absorption
direction is visible rather than a rounding difference; `SheetAt` on a non-conductor entry ignored;
`.ctech` round trip (and absent from the file when unset); the merge clone and its conflict
description. `MimCapacitorTests` carries the shipped technology's own numbers.


## MIM-1 — region vias: drawn via artwork beyond the point `ViaShape` (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-1-region-vias.md`, gap 1 of the MIM series. Extraction and
reporting only; `src/Engine` untouched, and every §7 via refusal still fires unchanged.

### What was wrong, and why it was silent rather than refused

`PlanarExtractor`'s classification loop recognised a via-bound drawing layer in exactly one place:
inside its `if (s is ViaShape)` branch. Every other shape fell through to `binding`, the layer→z-band
map — and `BuildStack` builds that map from **non-Via entries only**, because a via contributes no
thickness and has no z band of its own. So a rectangle or polygon drawn on a via layer missed the map
and landed in `ignoredOther`.

**The counter it landed in is what made the failure worse than a drop.** `ignoredOther`'s note says
the shape is *"not bound to a stackup conductor or via entry"* — which is exactly the wrong advice
for artwork on a layer that IS bound, and sends the user to the technology editor to redo something
already done. The same silence swallowed a drawn backside-via slot or bar.

This is the same map-vs-branch split that made `BuildVias` unreachable at L9's phase gate. It is
worth stating once more: the two bindings answer different questions (where a layer sits in z, versus
which two conductors a via joins), keeping them apart is right, and the cost of keeping them apart is
that every new shape kind has to be routed to the second one deliberately.

### What it does now

- **A filled region on a via-bound layer becomes a `PlanarVia` footprint**, through the conductor
  path's own shape→`PlanarPolygon` conversion — outer ring plus holes, the layout's own flatten
  tolerance, the same degenerate-ring floor. Reused rather than restated: a via footprint and a
  conductor footprint are resolved onto the same tensor grid, and two conversions that could drift
  apart would show up as a via meshing to a slightly different set of cells than the metal it lands
  on.
- **The footprint is NOT squared.** The equal-area square (side = 0.886 × drill) exists so a round
  barrel *nobody drew* does not contribute a hard gridline per facet. A drawn outline already is the
  footprint, so it goes to the mesher as it stands.
- **Span, conductivity and the ground rule come from the stackup entry**, identical to the point
  path, and a region via participates in the same `noSpan` / `unknownLevels` / `notAdjacent` /
  `toGround` / `wrongGround` accounting — counted in SHAPES, because a shape is what the user drew
  and can go and look at.
- **Nothing on a via-bound layer falls into `ignoredOther` any more.** A `PathShape` there gets its
  own sentence (a centreline encloses no area; draw the region), and so does a region that flattens
  to nothing.

### The one design decision worth the words: regions are GROUPED PER STACKUP ENTRY

Every region on one via entry becomes **one `PlanarVia` carrying several footprint polygons**, not
one `PlanarVia` each. The obvious reason is that the span, conductivity and ground rule all come from
the entry, so per-shape vias would be N identical records. The real reason is correctness:

`SurfaceMesher` scans every grid cell against a via's polygon list and **stops at the first polygon
that covers it**. Two overlapping footprints inside one `PlanarVia` therefore give a shared cell
**one** vertical basis. As separate `PlanarVia`s they would give it **one each**, silently doubling
the vertical current in the overlap — and a plate connection drawn as two overlapping rectangles is
an ordinary thing to draw, not a corner case. `TwoOverlappingRegions_GiveTheirSharedCellsOneVerticalBasisEach`
pins it as a counter that is independent of the mesh pitch: no cell index appears twice,
and the meshed footprint is the union (60 × 40 µm) rather than the sum (2 × 40 × 40 µm).

**The same hazard exists on the point path and was left alone**, deliberately — it is pre-existing
behaviour, changing it would move existing runs, and the brief forbids touching the point path. It
was *measured* while sizing the structural gate below: a 2 × 2 array of nominally touching point
vias overlaps by 0.37 nm (see the next section), and the meshed footprint comes out at
1600.0591 µm² against the true union — i.e. the overlap strip is counted twice, exactly as the
first-cover argument predicts.

### The structural gate, and why it compares AREA rather than a basis list

The brief asks that a region via covering the cells of an N×N array of touching point vias yield the
same vertical basis functions. **That cannot be a basis-list comparison, and asserting it would be
asserting something false.** L9c's own mesher finding is that a via footprint must contribute HARD
gridlines or the via vanishes silently — so N×N touching footprints put N−1 interior gridlines per
axis into the shared tensor grid that one large footprint does not. Those lines *subdivide* the
covered cells; they do not move the covered boundary. Measured: 943 unknowns (4 vertical) for the
single region against 943 (4 vertical) for the drawn 2 × 2 array on this fixture.

The grid-independent statement of the same claim is **the plan-view area the vertical bases cover**
— still a cell counter (one basis per covered cell, summed over the cells' own areas), never an
S-parameter. The gate is in two halves:

| | Fixture | Claim | Result |
|---|---|---|---|
| A | 2 × 2 drawn squares vs one drawn rectangle over their union | covered area equal, **to the bit**; the single footprint needs no more unknowns | 1600 µm² both, N = 943 both |
| B | 2 × 2 point vias vs the same region | covered area equal to the equal-area square's own DBU rounding | −3.6926 × 10⁻⁵ relative, **predicted exactly** |

**Half B's discrepancy is predicted rather than bounded**, which is the part worth keeping. A point
via's square is 0.886 × drill and a drill is an integer number of DBU, so the square that gets meshed
has side s′ ≠ the nominal s and the array covers n²s′² against the region's (ns)². On this fixture
s′ − s = +0.37 nm, so nominally touching point vias in fact *overlap*, and 1 − (s′/s)² reproduces the
measured area difference to 12 decimal places. If the two ever disagree by anything that rounding
does not account for, one of the two paths has a real defect. (The overlap also costs N: 1096
unknowns with 16 vertical bases, against the drawn array's 943 with 4 — a sub-nanometre sliver run,
and a good illustration of why the point path snaps nothing.)

### Milestone 4's assumed paths, checked rather than assumed

- **A Via stackup row binds a drawing layer and states its span** — real, `ShowsDrawingLayerPicker`
  is `Kind == Via` and a Conductor row deliberately does not show that control (it binds through the
  layer table). Verified against a live `TechEditorViewModel` over the MMIC starter, including that
  the picker's option list actually contains the layer the extractor keys on.
- **A rectangle drawn on that layer reaches the extractor** — real, and is now the main body of tests.
- **`EmDiagnostics`' via count includes region vias** — **this path does not exist.** `EmDiagnostics`
  is the EM run service's REFUSAL family (`em.run.cancelled`, `em.layout.not-found`, …); it has no
  via counter and no counter of any other extraction quantity. The via count a user actually sees is
  carried in the run's NOTES, which `EmRunService` concatenates from the extractor and the mesher.
  Nothing was built: the smallest version is a test that both note sources count a region via, which
  is what `TheRunsOwnViaCount_IncludesRegionVias` asserts. Growing a diagnostic for a *quantity*
  would be the first non-refusal member of that family and is a decision for whoever converts the
  next family, not a side effect of this brief.

### Tests

`tests/Ui.Tests/Em/RegionViaExtractionTests.cs`, 13 methods, all routine tier (~70 ms). Point-via
bit-identity is asserted with `BitConverter.DoubleToInt64Bits` against the documented rule restated
in the test, not read back from the object under test.

The terminal resolution (`SpanFrom`/`SpanTo` → a level pair or one of five counters) is now a single
local function both artwork kinds call. That is not tidiness either: "the artwork says WHERE, the
stackup says WHICH TWO CONDUCTORS" only holds if the answer cannot depend on how the via was drawn,
and a second copy of that block is exactly how it would stop holding.

## R-em-4's ground query returns null for TWO reasons, and the note claimed the wrong one (2026-08-30)

`PlanarExtractor` resolves the EM ground as **the highest ground-designated conductor BELOW the
lowest analysis level**. When that query comes back empty and `Stackup.Bottom == Ground`, it fell
back to the bottom of the stack and said:

> No conductor layer in technology 'X' is marked as a ground reference, so the ground plane was taken
> from Stackup.Bottom = Ground at the bottom of the stack.

**That sentence is only true for one of the two ways to reach it.** The query is scoped to
conductors *below the signal*, so it also returns null on a stackup that HAS a designated ground
sitting *above* — and there the message is flatly false, contradicted by a ticked checkbox on the
Stackup tab the user is looking at.

**It survived because no shipped technology could reach the false branch.** Every PCB starter was
2-layer and the MMIC's ground is its backside metal, so the only ground candidate was always the
bottom conductor: "none below the signal" and "none at all" were the same statement. The first
technology with an INNER ground plane (`pcb-4layer_FR-4_62mil_1oz`, added the same day) made them
different, and a trace on a lower layer was told its technology designates no ground at all. Worse
than the wording: the run **succeeded**, solving against a reference further away than the real one,
so there was no refusal to prompt anyone to look.

The fallback now asks which case it is and names the planes it did find, says why they cannot serve
(a port returns through a plane BENEATH the conductor it feeds), and states the cost — the reading
will be a higher impedance than the real structure. The original sentence is kept verbatim for the
genuinely-undesignated case.

### Two neighbouring messages were wrong in the same way — advice for a situation that was not this one

- **The zero-height slab refusal** said *"Check the stackup order in the technology editor."* The
  commoner way to arrive there is a correctly-ordered board whose BOTTOM conductor is being treated
  as the signal: it rests on the `Stackup.Bottom = Ground` boundary, so the slab has zero height and
  nothing is misordered at all. That case is now named, with the two things that actually help
  (mark it as a ground reference, or move the trace up a layer).
- **The no-signal-conductor refusal** said *"Draw the artwork on a conductor layer, or bind the layer
  it is on to a conductor entry."* When every shape is on a ground-designated conductor the layer IS
  bound — the advice sends the user to redo something already done. Reachable on any stackup with
  more than one plane (a 4-layer board whose only artwork so far is an inner pour), so it now says
  the plane is not meshed and points at the "Ground reference" tick.

**None of the three was found by reading the extractor.** They were found by running it on each
conductor of the new 4-layer technology in turn and printing the result — a scratch xunit probe, run
once and deleted. A message can only be checked against the state that reaches it.

Gated by `tests/Ui.Tests/Em/FourLayerGroundReferenceTests.cs`, which drives the extractor rather than
scanning source, and includes the negative: the 2-layer starter must reach neither new branch.

## A board outline refused the EM run, and the dielectric binding was the workaround (2026-08-30)

User proposal: remove the dielectric's "Drawing layer" control from the `.ctech` editor, since the
binding is never used except under the hood. **The premise was wrong and the conclusion was right,
for a reason neither of us had.**

### What the binding actually did

Nothing electrical: `PlanarExtractor.BuildMediumStack` reads only `Epsr`/`TanD`/`Mur`/`ThicknessDbu`
and every dielectric is a laterally infinite slab, so a dielectric bound to `(none)` is everywhere.
Every other consumer filters it out — `WBondClearance` reads `DrawingLayers` only after
`if (sl.Kind != StackupKind.Conductor) continue`, `PcbLayerNaming`/`DrcConnectivity`/`GerberExport`
take conductors and vias, `PcbWriter` writes dielectric thickness with no layer reference, and in
`PlanarExtractor` a dielectric-bound and an unbound shape reach the same `ignoredOther`.

Its ONE effect was in `CrossSectionExtractor.Classify`, and it was not subtle. Measured on the MMIC
starter with a Metal1 trace plus a die outline on `Substrate`: binding kept → `Ok=True` with a note;
binding removed → **hard refusal.** The field was the difference between the run working and failing.

### The defect underneath: the refusal fired on the normal case

Sweeping every layer of the shipped 2-layer PCB starter, one shape at a time beside a solvable trace:

| Layer | Result |
|---|---|
| Top Copper, Bottom Copper, Drill | Ok |
| Soldermask Top / Bottom, Silk Top / Bottom, **Outline** | **REFUSED** |

**Every PCB layout has a board outline**, so the failing case was the normal one — and the refusal's
advice was *"add this drawing layer to a conductor entry's DrawingLayers list"*, i.e. declare your
board outline to be copper. The dielectric-`DrawingLayers` binding was a narrow escape hatch from
this, applied only where the MMIC starter tripped over it.

### The discriminator was available and was not being asked for

A layer the technology **declares** but binds to no stackup entry is the technology stating the layer
is not metal. Silk, soldermask and outline are exactly that. A layer the technology **does not
declare at all** — a foreign import, a hand-edited file — is the case nobody has said anything about,
and there the original reasoning holds in full.

So the refusal is narrowed, not deleted: declared-but-unbound is ignored with a note that names every
distinct layer once and still offers the fix (*"If one of them IS metal, bind it to a conductor entry
on the Stackup tab"*); undeclared still refuses, now pointing at the Layers tab rather than telling
anyone to call it copper. Ignoring is REPORTED, never silent — a trace genuinely drawn on a forgotten
layer is still visible in the run's own output.

With the workaround unnecessary, the editor's dielectric picker is gone
(`StackupLayerRowViewModel.ShowsDrawingLayerPicker`, via only). **The model field stays**: shipped and
user `.ctech` files carrying a dielectric binding still parse, validate, round-trip through
`TechnologyMerge`, and take their original more-specific "substrate extent" note — removing a control
must not rewrite anyone's file. `IsSingleDrawingLayer` is deliberately left answering `true` for a
dielectric, because the CARDINALITY rule did not change.

Gated by `tests/Ui.Tests/Em/UnboundLayerArtworkTests.cs` (10 tests), including both halves that make
this safe rather than merely permissive: the MMIC die outline extracts with the binding removed, and
a file that still carries one behaves exactly as before.

**None of this was visible by reading the extractor.** It came from running it on each layer in turn
and printing the verdict — a scratch xunit probe, run once and deleted. The same method found the
ground-reference bug above. A refusal can only be checked against the state that reaches it.

## A laterally-finite dielectric cannot be drawn, because the kernel cannot represent one

Asked while the section above was being investigated: how does a user simulate a MIM cap built on a
GaAs substrate, if the dielectric is always everywhere? Surely the nitride must be drawn on a layer.

**It cannot be, and no binding would have helped** — this is a formulation limit, not a missing
feature. `BuildMediumStack` produces a `LayerStack` of `MediumLayer(thickness, material)`: a 1-D
stack of laterally infinite slabs, and the DCIM Green's function is derived from exactly that stack.
Unknowns live on conductor surfaces and via barrels only. A nitride island under a top plate needs
either volume-equivalent currents inside the dielectric (a VIE) or a surface-equivalence formulation
on its boundary, and neither exists in `src/Engine`'s planar kernel.

Drawn dielectric geometry is therefore ignored — before the change above it fell into
`PlanarExtractor`'s `ignoredOther`; it is now named in the declared-but-unbound note. Reported, but
inert either way. Note also that the MMIC starter's own `Cap Dielectric` and `Nitride` drawing layers
are bound to no stackup entry at all: they are artwork/DRC/GDS layers, and their presence must not be
read as EM support.

What actually works, best first: a **lumped C in the schematic** (C = ε₀εᵣA/d from the process's
capacitance density, with the EM run covering the interconnect around it — the normal MMIC flow); or
**stating the inter-metal dielectric as nitride** in the stackup and meshing both metal levels, which
gets the plate overlap right out of the solve but puts every airbridge and crossover in the same run
in nitride instead of air; or **splitting the run**, EM for the passive interconnect and lumped caps
combined in the schematic.

## Union was quadratic in the operand count, which made the Gerber importer's own advice unusable (2026-09-03)

`LayoutBooleans.Combine` folded every boolean **linearly**: `acc = acc op operand[i]`, one full
Clipper2 `BooleanOp` per operand against an accumulator that had already absorbed everything before it.
For Intersection, Difference and Xor that shape is required — Difference is not commutative, so those
operands must be applied in selection order. For **Union** the same shape is pure cost: operand N is
clipped against a result carrying N-1 operands' worth of contours, so the total is quadratic.

**This is not a theoretical complaint — the codebase routes users into it by name.** `GerberImport`
tells anyone importing a vector-filled pour that their layer "arrived as N separate strokes ... use the
editor's Merge action to turn them into one region before setting up EM ports". On an owner-supplied
4-up RF panel that is **46,721 strokes on one copper layer**, and Union on it ran for **over forty
minutes without finishing** (killed, not completed). The advice was not actionable on the exact file
class that triggers it.

Union is associative, so it is reduced as a **balanced tree** — `(A∪B)∪(C∪D)` rather than
`((A∪B)∪C)∪D`. That is a change of ORDER, not of semantics, and every step stays a real pairwise
`BooleanOp` between two already-resolved regions.

| | 76,517 operands, 10 layers |
|---|---|
| linear fold | >45 min, never finished |
| balanced tree | **9.8 s** |

Result: 76,517 shapes collapse to 2,478 (top copper: 47,530 strokes → 190 polygons).

### The obvious faster version is WRONG, and a test caught it

The first attempt was the one-call form: concatenate every operand's `Paths64` into a single subject
set and resolve it in one `BooleanOp(Union, all, empty, NonZero)` — which is exactly what `Repair`
already does for one self-intersecting shape. It is 40 s, still a huge win, and it produces the wrong
answer for any operand carrying a hole: **under NonZero a hole contour from one operand cancels another
operand's fill where they overlap**, so a union that should have closed a hole punches one instead.
`PcbImportTests.ACustomPad_IsOneUnionedRegion_IncludingEachFilledPrimitivesPen` failed immediately —
one region came back as two. Union two resolved regions at a time; never a raw pile of contours.

### Merging a hatched pour is the right move for the MODEL and does not make rendering faster

Worth stating because the import message implies otherwise. The unioned panel renders **slower** than
the unmerged one (240 ms vs 126 ms/frame at Zoom-to-Fit), because a hatched pour's union has a
comb-shaped boundary: 2,478 shapes carrying **308,326 outer vertices plus 771,663 hole vertices**, one
polygon of 12,335 vertices with 15 holes. The artwork really is that complicated; the strokes were
hiding it in a form Skia happened to rasterize cheaply. Merge for editability and for a meshable
conductor — which is what the import message actually claims — not for frame rate.

## Gerber import: a six-layer board that imported as nothing (2026-09-04)

A user's real board came back with **every artwork layer refused** and only the drill data through —
`"This Gerber file declares no %MO*% unit (and no G70/G71)"`, once per file, twenty times. Six
separate defects, found from that one file set. The first is the blocker; the rest were sitting
behind it.

### 1. One `%…%` block may hold SEVERAL commands, and only the first was read

```
%FSLAX45Y45*MOMM*%
%IR0*IPPOS*OFA0.00000B0.00000*MIA0B0*SFA1.00000B1.00000*%
```

This is the original RS-274X spelling — commands separated by `*` inside one `%…%` — and it is still
what several exporters emit. `ExtendedCommand` split the body on `*` and then used `segments[0]` and
nothing else, so `FS` was read and `MO` was silently dropped. The refusal that followed was accurate
about its own state and useless about the cause: the file DOES declare its unit, on the same line.

The loop now runs every segment. **`%AM` is the one command that legitimately consumes the rest of
the block** — its primitives are themselves `*`-separated — so it ends the loop rather than being
one of the iterations.

### 2. `%IR` was an unrecognized command

`%IR0*%` is the identity, and a file that emits the command at all almost always carries the
identity. Counting it as unknown put one noise line on every file of a real set while saying nothing.
It now joins `%MI`/`%SF`/`%AS`/`%LM`/`%LR`/`%LS`: identity accepted silently, non-identity refused by
name.

### 3. A numbered mid layer was not read as copper at all

The set names its outer copper "Top Layer"/"Bottom Layer" — both already in the rung-3 table — and
the four between them "Layer 2".."Layer 5", which matched nothing. **That is not a labelling
nuisance: only conductors enter the stackup and the copper order**, so four sixths of the board
quietly left the part of the import the EM path reads, and the run reported "2 of 2 copper layers".

The new row is the last in the table, so every function row wins first, and it matches only when a
NUMBER follows the word — a name that merely contains "layer" is not promoted to copper. **"Layer 2"
counts the whole stack from the top and is therefore the FIRST inner layer**, which is where the -1
comes from; "inner 2" already counts only the mid layers and keeps its number. Both spellings now go
through one `NumberAfter` helper with a per-row offset.

Guessed inner layers also needed an ordering tiebreak. They all share one `SideRank`, so they fell
back to file NAME — which orders "Inner 10" before "Inner 2". It is deliberately **not** a
`CopperIndex`: the number came from a file name, and the import's report must go on calling that
stack order a guess.

### 4. A drill DRAWING landed on the drill layer

`..._Drill_Drawing.art` matched the plain `drill` row. It is a dimensioned fabrication sheet whose
tool legend sits beside the board, so the drill layer's extent ran from -37.5 mm to 222 mm on a
111.8 mm board. A `drill` + `drawing|map|legend|chart` row now takes it first, as "Drill Map".

### 5. A ROUT FILE HAS NO HITS, so the artwork cross-check agreed with itself

The strongest evidence available for a drill file's format is whether its holes land inside the
artwork — and `CrossCheckExtents` counted `Hits` only. A rout file commonly holds routed SLOTS and
not one plain hit, so it reported "all 0 hits fall inside the artwork extent", `Agrees` came back
true, and the wrong-format retry below it never ran. The file's four slots landed at Y 231..318 mm on
a 55 mm board and nothing said so. Slot vertices are now counted the same way — a slot is cut through
the same copper a hole is.

### 6. The width of the coordinate words IS the digit format, and nothing was reading it

The same file: `METRIC`, no format statement, coordinates like `X0056999Y0318200`. Defaulted to the
classic metric 3:3 that is **six** digits, its seven-digit words were read ten times too large.

There is a new evidence rung for this, `DrillFormatEvidence.CoordinateWidth`, and it needs BOTH of
two conditions — neither alone would do:

* **every coordinate word is the same width** — which a trailing-suppressed file cannot produce;
* **at least one of them carries a leading zero** — which a leading-suppressed file cannot produce.

Together they are close to proof that the file suppresses nothing and writes each coordinate at its
full field width, and that width is then the whole format: the integer half keeps the unit's
conventional size (3 covers 999 mm, 2 covers 99 inch, no board needs more) and the measured total
settles the decimals. That reproduces every format in circulation from the width alone — 6 digits of
mm is 3:3 and 7 is 3:4; 6 of inch is 2:4 and 7 is 2:5.

It settles the SUPPRESSION question too, because a word already at the full width parses to the same
integer under either convention (`ParseCoordinateWord` pads only up to that width). Recorded as
settled rather than defaulted, which is what stops the import raising a prompt about a file that left
nothing open. Four coordinate words is the floor at which "they are all the same width" stops being a
coincidence a two-hole file could produce by accident.

### The drill-format prompt asked the same question once per file

Reported separately by the same user. A set's drill files come out of one exporter in one format, so
the second dialog is the one a user answers without reading. `DrillFormatChoice` gained
`ApplyToAll`, the prompt is told how many files remain so the checkbox appears only when there is
something to apply it to, and **a CLI `--drill-*` flag now sets it implicitly** — a flag is a
statement about the run, not about one file, which also stops the same refusal printing once per
file. A null `Override` carried this way accepts each later file's OWN inference rather than forcing
this file's format onto it: the user confirmed an inference, and only what they actually CHANGED is
worth propagating.

### Two smaller things the same log exposed

* The composite-polarity paragraph was added to BOTH `CompositeReason` and `Diagnostics`, and the
  orchestrator prints both lists — six duplicate paragraphs on a six-layer board.
* The minted `StackupKind.Via` entry named no span, so the technology validator reported "spans an
  unknown conductor layer" twice per drill file. It now names the topmost and bottommost conductor
  entries **when the stackup has any**. A set with no job file has no conductor entries to name, and
  inventing two would be a substrate invented under another name — that import already says, in
  words, that the technology is incomplete.

### Verified against the file set, not only against the tests

All 20 artwork files import (3,284 shapes, 6 copper layers). Every copper layer's extent is
0.30..111.50 × 0.30..54.71 mm inside a board outline of 0..111.80 × 0..55.00, and the routed slots
moved from Y 231..318 mm onto the board at 23.18..31.82 mm. Nothing from that set is committed: it
names a vendor, a customer and a real filesystem path in its own header comments, and every fixture
here stays hand-authored.

## Vias carved back out of a composited pour (2026-09-04)

The follow-on the note above left open. On the six-layer board every copper layer paints in clear
polarity, so every layer was composited — and compositing unions each via pad into the pour around
it. Pairing looks for a discrete `CircleShape` flash, found none, and returned **zero vias from 1,555
holes**. That is not a labelling problem: `ViaShape` on a via-bound layer is what `PlanarExtractor`
reads as a via (L9d/D5), so the board simulated with no vias in it at all.

### The pads were never gone — the reader just threw them away

Compositing is the LAST thing `GerberReader` does. Until then every flash is still a separate painted
object, so the pad's real diameter is sitting right there. `GerberReadResult.CompositedFlashes` now
carries them, and pairing treats them exactly as it treats a surviving flash. Nothing is invented: the
pad size is the file's own aperture, not a drill diameter plus an assumed annular ring.

They are EVIDENCE, not artwork — their copper is already inside the pour in `Shapes`. So claiming one
obliges the caller to cut the same disc back out, which is what `GerberImport.CarveClaimedPads` does.

### The invariant that makes it safe

**Carve + via pad = the copper that was there.** A pad is offered only if it survived compositing
WHOLE — tested at its centre and eight points around the rim, against the NonZero winding of the
composited paths. A dark flash a later clear object ate (an antipad on a plane layer is exactly this)
is not a pad any more, and pairing a hole to one would put copper back where the artwork deliberately
removed it.

Measured on the real board, per layer, against the identical artwork imported with no drill file:
five layers conserve to **0.00e+00** and the top to **4.2e-05** relative. That residual is the
measurement's own: the carve subtracts a FLATTENED disc while the check adds back exact πr², and for
the ~111-segment circles `CircleTolDbu` produces the inscribed-polygon deficit is 5.3e-4 per pad
against 5.8e-4 observed. `CarvingAViaOutOfAPour_LeavesTheLayersCopperUnchanged` pins it.

### Two cross-layer leaks the area measurement found, both older than this work

Neither was visible before, because nothing had ever compared the copper in to the copper out.

- **A pad claimed on one layer, a via landing on another.** `LandingLayer` is `landingLayer ??
  pad.Layer`, and `PickFlash` will take a flash on ANY layer when the landing layer has none — so a
  via whose top pad was missing paired with the INNER one, and 4.5 mm² left an inner plane and
  reappeared on the top. A composited pad may now only be claimed where the carve and the via's pad
  cancel, i.e. on the layer the via actually lands on. A surviving discrete flash may still come from
  anywhere, as before: consuming one removes that shape, so the asymmetry is at least visible.
- **A SOLDER MASK OPENING IS NOT A VIA PAD.** The ranking's last resort is "a flash on any layer at
  all", which on this board took the mask clearance around each mounting hole — six 4.6 mm openings
  became 4.6 mm COPPER pads, sitting on a pour that has a deliberate hole exactly there, ~100 mm² of
  copper that is not on the board. `Pair` now takes the set's copper layers and no other layer's
  flash can be a pad. Null or empty means the caller could not say and every layer stays eligible,
  so no existing caller changes behaviour.

Result on the board: **1,475 vias** from 1,555 hits, 80 unpaired — the mounting and tooling holes,
which genuinely have no copper pad.

### Cost notes

One boolean per LAYER, not one per pad: a pour here carries hundreds of thousands of vertices and a
difference per pad would be a thousand passes over all of it. The carve is restricted to the
composited shapes BY REFERENCE rather than by layer, because two files can land on one layer and a
boolean over everything on it would re-polygonise a neighbour's untouched artwork into the pour.

**The nine-point containment test was the part worth being suspicious of** — ~1,500 pads × 9 points
against a pour of tens of thousands of vertices is the shape of something that quietly costs seconds
per layer. Measured instead of assumed, in Release, warm, per file: the WHOLE read of the heaviest
copper layer — compositing included, which dominates it — is **304 ms**, and all six copper layers
together are **978 ms** of a ~13 s board import. The bounding-box prefilter is what makes it a
non-issue; without one it would not be. Do not remove it.

## Reading a `.clay` back was dominated by `EnsureValidHoles` — two box prefilters, 5.4x (2026-09-04)

Found while moving the Gerber import off the UI thread (`src/Ui/RESOLVED.md` has the user-facing
half). `LayoutPersistence.FromFileModel` runs `LayoutClipper.EnsureValidHoles` over every shape on
load — deliberately, per S3.1a R10b: a hand-edited or otherwise not-Clipper2-produced shape may carry
an invalid hole, and the loader enforces validity rather than trusting it. The comment there calls it
"a no-op for the overwhelming common case (no holes, or holes already valid)", and on hand-drawn
layouts it is. It is not a no-op on Gerber-imported artwork.

### Measure in RELEASE, and measure PER SHAPE

The first measurement of this was `dotnet run` and `dotnet test`, i.e. **Debug**, and read 17.4 s. The
Release figure for the same file is **1.69 s** — this is a tight managed loop over `long[]`, which is
about the worst case for a Debug build, while the Gerber parse beside it is string and file work and
barely moves between the two. Quoted together, Debug made the load look like ten times the import
when in the shipped app the two are about equal. Nothing here is measurable with `dotnet test`; a
scratch harness built `-c Release` is.

Per shape, on the 28 MB `.clay` a real 20-layer board imports to (3,284 shapes, 1,573 of them holed,
3,591 holes between them), it is not spread out at all — **six shapes are 1.67 s of the 1.69 s**, and
the worst one alone is 750 ms:

| holes | outer ring vertices | hole vertices | before | after |
|---|---|---|---|---|
| 228 | 1,751 | 21,772 | 750 ms | 155 ms |
| 55 | 1,562 | 12,220 | 230 ms | 25 ms |
| 33 | 1,518 | 11,868 | 182 ms | 50 ms |
| mean over all 1,573 | 390 | - | - | - |

The mean shape has 2.3 holes. **The cost lives entirely in the composited copper pours**, and any
attempt to reason about this from the average is reasoning about the wrong shape.

### What the three terms actually cost, and which one a box can help

For the 228-hole pour: point-in-outer is `holeVerts x outerV` = 38M; hole-vs-outer crossing is another
38M segment-pair tests; and **hole-vs-hole is `sum over pairs of h_i x h_j` = ~233M**, the largest of
the three, because `HolesAreValid` tested every PAIR of holes against every other in full.

Two prefilters, and they are prefilters — every reject is a case where no segment pair can possibly
meet, so the answer is unchanged:

- **Hole vs hole: one ring-box overlap test per pair.** The holes of a pour are disjoint by
  construction, so essentially every pair dies here — ~26k box tests instead of ~233M segment tests.
- **Hole vs outer: reject the OUTER's segments against the HOLE's box.** This is the part that is
  easy to get backwards and worthless if you do. A hole lies inside the outer ring's box, so
  rejecting the hole's few segments against the outer's box discards nothing; it is the outer's
  thousands of segments that have to be thrown away against the hole's small box. `RingsIntersect`
  therefore puts the LONGER ring on the outside of the loop and rejects its segments against the
  shorter ring's box — legitimate because `SegmentsIntersect` is symmetric in its two segments.

Boxes are computed once per ring into a `RingInfo`, not per pair; that is what makes the pair reject
O(1).

**Result: 1.69 s -> 0.31 s for the check, and ~1.9 s -> ~0.45 s for the whole `LoadFromFile`
(Release).**

### The remaining term is not a box problem

What is left is `PointInOrOnRing` — ~155 ms of the 0.31 s, nearly all on that one pour. A ray cast has
to see every segment the ray can cross, so there is nothing to reject. **Gating `OnSegment` behind the
segment's own box was tried and measured no better** (0.34 s against a 0.30-0.34 s spread, i.e. inside
the noise): the test is three multiplies on values already in registers, so four integer compares and
a branch buy back about what they cost. Cutting this further needs an INDEX over the outer ring —
segments bucketed by y, so a cast at height `py` visits one band instead of all N — with a build cost
of its own. Not done.

### The one place a box reject DID change an answer, and it was not the boxes

Caught by the differential gate on trial 115 of 3,000, not by reading the code.

**A ring with a repeated consecutive vertex has a ZERO-LENGTH segment, and `OnSegment` answers true
for every point against one** — its window is `0 <= dot <= lenSq`, and such a segment has `lenSq = 0`
and `dot = 0` for all points. `SegmentsIntersect`'s collinear branch then returns true, so the
unfiltered `RingsIntersect` calls such a ring intersecting against *anything at all*, wherever the two
rings are. The box reject correctly says "these are nowhere near each other" and returns false — which
would have turned every shape carrying a duplicated vertex from "re-derived through Clipper on load"
into "loaded as it stands", silently, under a performance edit.

Preserved rather than corrected: `RingInfo` carries `HasZeroLengthSegment`, computed in the same pass
as the box, and `RingsIntersect` answers that case before the boxes get a say. Whether that repair
*should* happen is a question about R10b and is left open — but it is now visible, which it was not.

### The gate

`tests/Ui.Tests/LayoutClipperHoleValidityTests.cs` is DIFFERENTIAL: it carries `BruteForce`, the
pre-change algorithm verbatim and unfiltered, and asserts the two agree on every case. Nine named
cases (touching holes, a hole touching the outer ring, boxes that overlap where the edges do not,
duplicated vertices, empty and degenerate rings, a 400-gon with 60 holes) plus a 3,000-trial
randomized corpus on a fixed seed. Half the trials are laid out adversarially on a coarse lattice —
that is what produces the exactly-touching and exactly-collinear configurations — and half place
holes in distinct cells of an interior grid, because an all-adversarial corpus answers "invalid" to
almost everything and would exercise only one branch; the test asserts that split rather than
assuming it. `HolesAreValid` was made `internal` so the corpus can drive it on ring arrays directly.

## §5C.2a — cross-workspace technology agreement (2026-09-04)

`ExternalWorkspaceGate` was refusing more than it had to, and had two cases it refused with advice the
user could not act on.

**"The same technology" now means the same layer table over the keys the referenced cell actually
OCCUPIES** (R47h), not over both tables entire. The hazard R47 names is a key being *reinterpreted*, and
only a key something is drawn on can be. Comparing the whole table refuses two projects sharing a metal
stack that differ in their documentation layers, which is the ordinary case and no hazard at all.
`CellHierarchy.OccupiedLayerKeys` is the walk: transitive through instances, and **it counts a via's
`LandingLayer` as a second key on the same shape** — reading only `LayoutShape.Layer` would have missed
where a via's copper actually lands, which is exactly the field `ViaShape`'s own doc comment warns about.
It reads no coordinates, since a transform moves a shape and never changes its layer; that is what makes
it cheap enough to run per placement with no cache.

**The cost, and where it is paid.** A permit is now a statement about the referenced cell's contents at
one moment, and that cell lives in another workspace where it can grow a shape on a disagreeing key
afterwards. `AuditPlacedExternalRefs` re-asks the whole question and stores nothing — so a fix on either
side clears the warning with no bookkeeping — and it is **reported, never enforced**: the geometry is
already placed and built on, and withdrawing it would be a worse failure than the reinterpretation.

**Two null cases were being refused with a repair that does not exist** (R47i). A technology that is not
there cannot give a key a different meaning:
- *No HOST technology* — the host already renders on generated fallback colours, so nothing is lost by the
  cell arriving. It now returns `AdoptTheirTechnology`, and the caller adopts.
- *No EXTERNAL technology* — its shapes were authored against no layer table, so there is no author's
  meaning for the host's table to contradict. Permitted.

Both used to print "(no technology)" on one side of a refusal naming two files, one of which did not exist.

**A fallback that must not be inverted:** a referenced cell whose `.clay` cannot be READ falls back to
comparing the WHOLE table, not to comparing nothing. "Compare nothing" would turn a broken file into a
silent permit, which is the one direction this gate must never fail in.

### The occupied-key walk was exponential (2026-09-05, same day, owner-reported)

Dropping a large library cell into a workspace hung the UI for ~60 s before the "Add Cell to
Workspace" dialog appeared. It was `CellHierarchy.OccupiedLayerKeys`, added the previous day.

**The defect, in one line: it carried only the DFS-PATH set, not a VISITED set.** `ResolveForWalk`'s
`visiting` argument is a path (added before recursing, removed after) because that is what cycle
detection needs, and it was the only set the walk had — so a shared sub-cell was re-walked once per
PATH reaching it, and every walk re-enumerated all of its shapes. Measured on a synthetic DAG (depth
5, fan-out 7, 43 unique cells, 2,000 shapes/leaf): **5,764 ms**. Deduped: **32 ms**, and 13 ms warm.
At depth 7 / fan-out 9 the old form has 4.8 M path-visits and does not finish in useful time; the new
one is 326 ms cold. The drop path also asked the question twice (once for the Reference refusal, once
inside `CrossWorkspaceCellCopy.Plan`); it now asks once.

**Why deduping is CORRECT here and is not for the bbox walk beside it.** `CellBboxRecursive` cannot
dedupe: a bbox depends on the transform chain that reached it, so one sub-cell down two paths is
genuinely two answers. A layer key is not transformed — a rotation moves a shape, it never changes
its layer — so a second visit can only re-derive what the first contributed. The answer is a set
UNION over reachable cells, and a union needs each cell once.

**Cycles stopped mattering; depth started.** A union over a graph is well defined however the edges
run, and the visited set terminates it, so `Cyclic` from `ResolveForWalk` is now the ORDINARY signal
for a shared sub-cell and is simply skipped. Depth is the opposite: a chain past `MaxDepth` is
truncated, and a SHORT key set is a permit the gate did not earn. That case returns **null** —
"unknown", never "none" — and `ExternalWorkspaceGate` falls back to comparing the whole table.

Own-shape keys are additionally memoized per `LayoutView` reference, on `_shapesBboxCache`'s exact
terms (a resolver hit returns the same instance; a file change makes a new one). Unlike the bbox memo
this one is unconditionally safe to share, since it depends on neither depth nor path — and it matters
because the R47h re-check runs on the process-wide live-refresh tick, where a generated cell's
six-figure via field would otherwise be re-enumerated every time.

---

## A via's span reached the technology but never the interchange writers (2026-09-05)

Reported from a public forum: there is no way to place a blind or buried via, because a via can be
assigned a layer but nothing says where it ends.

**Half of that is a documentation gap and half was a real bug.** The span HAS been expressible since
the via primitive landed — `StackupLayer.SpanFromLayer`/`SpanToLayer` on a `StackupKind.Via` entry
(R-via-3), edited in the technology editor's Stackup tab as `Spans: <conductor> → <conductor>`, with
the via's DRAWING LAYER selecting the entry. One entry per span, each on its own drawing layer, is the
mechanism; the shipped `pcb-4layer_FR-4_62mil_1oz` technology already ships a blind stitching via
beside its PTH, and the MMIC technology ships three entries. Nothing in the editor said so.

### What was actually broken

`DrcConnectivity` and `PlanarExtractor.BuildVias` both read the span. **Every interchange writer
invented its own answer instead**, and both inventions reach a fab:

- **`PcbWriter.WriteVia` wrote `from` = the pad's copper and `to` = `OppositeCopper(...)`.** Every via
  it wrote was a through via. A blind or buried via left circuitRF as a hole drilled clean through the
  board, silently. The IMPORT side has always refused to pretend in the other direction (a blind via
  it reads is reported as degraded), which is what made the asymmetry visible once looked at.
- **`GdsiiWriter`/`DxfWriter`/`GerberExport` keyed the pad off `ViaShape.LandingLayer`**, which
  `CommitViaPlacement` has never set — it writes Layer, X, Y, PadSize, DrillSize and nothing else. So
  every via drawn in the editor exported as a bare barrel with no annular ring (GDSII, DXF), or
  flashed its pad into the DRILL layer's own Gerber file (Gerber). **`GerberLayerOf`'s own doc comment
  asserted the opposite** — "the layout editor's Via tool always sets one" — and that claim is what
  had kept the case looking covered. Copper in a drill file is the exact fabrication bug the paragraph
  above it says L4h fixed; it was only ever fixed for vias that came from an IMPORT.

A fourth, in the editor: **the Via tool was enabled whenever the stackup had any via entry**, and
placed on `CurrentLayerKey` — which after `RebuildAvailableLayers` is the technology's FIRST layer,
i.e. copper. A via there belongs to no entry, so it has no span and is inert in DRC net extraction, in
the planar extractor, and in every export. It drew perfectly and did nothing.

### The fix

`src/Design/Layout/ViaSpanResolver.cs` — one answer to "which two conductors does this via join?",
plus `Explain(...)`, which writes the failure sentence once so the tool tooltip, the inspector and
three export diagnostics all say the same thing about the same state. Consumers: `PcbWriter`
(real span + the `blind` kind atom + a note when it falls back to through), the three pad writers
(`PadLayer` = the shape's `LandingLayer` when an importer set one, else the span's TOP conductor),
`ViaToolAvailability`, and a read-only "Spans" row in the properties inspector.

**Two traps worth keeping.**

- `SpanFromLayer`/`SpanToLayer` carry NO ordering promise — a hand-authored technology may name them
  either way round. The resolver takes the direction from `Stackup.Layers`' own top-to-bottom order
  (R-em-3), never from which field said what. Writing them in the field order produces a layer pair a
  board reader silently mis-orders.
- Arming the Via tool moves the current layer to the sole via layer when the stackup has exactly one,
  and deliberately does NOT choose when there are several — because with several entries the drawing
  layer IS the span choice, and picking one silently is how a blind via becomes a through via again.

### Still open

Nothing on the span itself — the import half landed in
`docs/sonnet-briefs/brief-via-span-import.md` (below). Still unchanged and still out of scope there:
`ViaShape` carries ONE landing layer, so a through via in a 4-layer board cannot state a pad on every
copper layer it passes.

---

## Importing a via SPAN, not just a via (brief-via-span-import.md)

The read half of the above. Two defects, and the brief is right that only the first is about blind
vias — but the SECOND is the one that hit every board anyone ever imported.

**(a) `PcbReader.ReadVia` discarded the pair it had just read.** It identified a blind/buried via
correctly, placed it on its top span layer and recorded a `Degraded` count. `specs[0]`/`specs[1]` were
read and dropped.

**(b) `PcbStackupMapping.Build` emitted no `StackupKind.Via` entry at all** — its `KindOf` maps only
`copper` and `core`/`prepreg`, everything else counted as ignored. So an imported board's technology
had ZERO via entries, and every imported via, THROUGH VIAS INCLUDED, resolved no span. Re-exporting
one wrote it as an unspanned through via with a note.

### The shape it took

`src/Design/Layout/Interchange/PcbViaSpanMapping.cs` is the read-side counterpart of
`ViaSpanResolver`: N vias in, one `StackupKind.Via` entry per DISTINCT span out, each binding a drill
layer of its own, plus the map that tells `PcbImport` which layer to move each via onto. The span
travels from reader to importer on `PcbImportedShape.SpanFromName`/`SpanToName` — the same route
`LandingLayerName` already took, and for the same reason: a span is a process parameter, so it must
not land on `ViaShape`.

`PcbImport.ImportResult` carries `ViaEntries` as **its own field, never folded into `Stackup`**, and
that separation is the whole reason the graft works. Both appliers
(`WorkspaceViewModel.ApplyImportToTechnology`, `Cli/LayoutConvert.MintTechnology`) refuse an imported
stackup when the destination already declares one — right for a substrate, wrong for a via entry,
which declares a drill and cannot invalidate anything. Folding the entries into the stackup would have
made a blind via importable only into a technology with no stackup, which is the one case nobody has.

### Five things measured rather than assumed

- **The brief's estimate of the hard part was right.** The graft mechanism was already wired end to
  end on both paths; what was missing was only the thing that CONSTRUCTS the entry. No new plumbing.

- **A span must be named against the stackup that will be IN FORCE, not the one the file brought.**
  `PcbImport` computes that the same way both appliers do (`destTech.Stackup.Layers.Count > 0` →
  the destination's) and resolves the two source copper names to drawing-layer KEYS, then to whichever
  conductor entries claim those keys. Naming the file's own `F.Cu`/`In1.Cu` into a technology whose
  conductors are called `Top`/`Inner 1` produces an entry `ViaSpanResolver` reads back as **no span at
  all** — the same null the change exists to remove, arrived at by a longer route.

- **`LayoutFragment.ApplyReconciliation` adds a layer only when some SHAPE was already on it**, so
  neither a minted drill layer (nothing is on it until the vias move) nor a span's own conductor (an
  inner plane a blind via lands on, with no artwork on this board) would ever reach the technology.
  Both are added explicitly in `PcbImport`. A via entry binding a layer the technology does not
  declare resolves nothing, so this is not cosmetic.

- **The span layer names must be the SPEC AS WRITTEN, not the layer table's canonical name.** At the
  20171130 epoch a renamed layer's user name occupies the canonical slot, and entities may reference
  either — so canonicalizing a via's `(layers …)` pair mints a SECOND source layer for copper the
  board's own geometry is already on. `PcbReader.SpecNameOf` keeps the spec whenever it is what
  matched.

- **Gate 5 was already green before the change, and the brief's premise for it is wrong.** It expects
  a re-exported import to report one `GerberExport.UnspannedViaPads` per via. It reports zero, and did
  before: `PcbImport.ResolveViaLayers` (then `ResolveViaLandingLayers`) has always set
  `ViaShape.LandingLayer` from the file's own `(layers …)` first entry, and `ViaSpanResolver.PadLayer`
  takes an explicit landing layer ahead of the span. The counter only ever fired for a via that
  carries NEITHER — an EDITOR-drawn one, which is the case `ViaSpanTests` already gates. The test is
  kept as a regression guard; it is not evidence of the defect it was written to describe.

### The one behaviour change outside the import

`(layers …)` is now the span, and the kind atom is only a cross-check. Where a file contradicts itself
— `(via blind … (layers "F.Cu" "B.Cu"))`, which `testdata/pcb-samples/via.kicad_pcb` carries — **the
pair wins**, because it is the specific half and it is what becomes a stackup entry; the overruled
word is reported by count rather than dropped. A via stating no `(layers …)` at all now takes the
outermost declared copper pair, which is what an unqualified via MEANS in this format, rather than
being left spanless for a writer to guess at a second time.

### Gates

`tests/Ui.Tests/PcbViaSpanImportTests.cs` (10), against
`testdata/pcb-samples/via-blind.kicad_pcb` — four copper layers, a real stackup, two vias sharing a
blind Top→In1 span and one through via. Every assertion goes through `ViaSpanResolver.Resolve`, never
through a layer name, or it would pass on a coincidence of naming while the resolver still answered
null. Gate 2 (round trip) runs the real `circuitrf convert` as a separate process and compares the
`(via …)` lines against the source file's, so a graft that works only in `WorkspaceViewModel` cannot
pass it. **Verified as a negative control**: neutering `PcbViaSpanMapping.Build` turns 8 of the 10
red.

## Imported symbols drew no pin leads in four of the eight grammars (2026-09-05)

An imported symbol's body had every pin floating clear of it — no stem from the body edge out to the
terminal. Reported against several formats at once, and it was one defect with four separate causes,
because **every format states the lead differently and four of the readers read past whichever way
theirs states it.** Measured on a nine-pin part whose body is four lines: the working readers produce
13 shapes, the four broken ones produced 4.

| Grammar | How the lead is stated | Was |
|---|---|---|
| `.kicad_sym`, `.lib`, `.lbr` | a length + rotation on the pin | drawn |
| `.hkp` | ordinary line records, like any other geometry | drawn (free) |
| `.scr` | a length WORD (`Long`) and an `R<deg>` token in the `Pin` statement | **dropped** |
| `.PLX` / `.DSL` | `(pinLength 300 mils) (rotation 180)` | **dropped** |
| `.cxf` | `LENGTH=` and `ROTATION=` fields on the `PIN` record | **dropped** |
| `.p`/`.d`/`.c` | **a separate PIN DECAL the terminal names** | **dropped** |

Three of the four were a field never read. The fourth is the one worth recording:

- **A `.c` holds more than one decal, and the reader stopped after the first.** Each `T` record ends
  with the NAME of a pin decal, defined later in the same file by the same grammar, whose own single
  piece is the lead. Field 4 of the `T` record is the terminal's orientation in quarter turns. So the
  fix is not a length to multiply out — it is reading every decal in the file and instantiating the
  one each terminal names, rotated. The reader's own comment already asserted "the stub runs one pin
  length inward from here" while nothing drew it, which is how the gap survived: the code described
  the right drawing.
- **A `T` record cannot be told from a decal header by shape** — both are 11+ whitespace-separated
  fields beginning with a non-numeric — so the header scan excludes `T` explicitly. Without that,
  the first terminal is read as a decal and the rest of the file becomes its contents.
- **A terminal naming a decal the file does not define draws no lead and says so.** The length is the
  pin decal's own geometry, and inventing one puts the body edge somewhere the file does not say it
  is.
- The four named lengths (`point`/`short`/`middle`/`long` = 0/100/200/300 mils) moved to
  `ComponentSymbolLead`, shared by the two readers of that family that state them as words. **They are
  absolute lengths in the format's own units and are NOT multiplied by the script interpreter's
  current grid scale**, which converts stated coordinates and nothing else.
- The script format also spells a MIRRORED placement (`MR90`, `SR0`). A mirror is not a rotation, and
  reading one as its bare angle draws the lead on the wrong side of the terminal — a worse drawing
  than the missing one. So only the plain `R<deg>` spelling is read and anything else leaves the pin a
  bare point.

**Gate:** `ComponentImportBreadthTests.Gate16` — one theory over all six PL2 grammars, asserting on the
DRAWING rather than on any format's own spelling (a segment runs from each pin to the body edge beside
it), plus `Gate16b` for the undefined-pin-decal report. The `.hkp` and `.c` fixtures gained the lead
records the real files carry; without them the fixture could not show the defect.

**Not fixed, and not a bug in these readers: no symbol FREE TEXT is imported, in any grammar.**
`KitSymbolShape` has line, rectangle, path and arc and no text case at all, so a designator or value
placeholder a file draws has nowhere to land and no text alignment to respect. Every one of these
formats states that text with a justification field. Adding it means a new shape case carrying the
string, its size, its rotation and its justification, mapped onto `SymbolTextAlign`/`SymbolTextVAlign`
in `KitTemplateSymbol.Convert`.

## Imported symbols carried neither the line weight nor the pin name's side (2026-09-05)

Both were the same structural gap, one level below the readers: **`KitSymbolShape` had line, rectangle,
path and arc, and no width field**, and **`SymbolPin` had no text alignment**. Nothing could be read
into either, so every reader dropped both and `KitTemplateSymbol.Convert` hard-coded
`SymbolStrokeTier.Normal` on every primitive.

### Line weight — every format states it, and the mapping is RELATIVE

Measured, not assumed: `.lbr` draws 11 wires at 0.1 mm and 5 at 0.254 mm; `.hkp` draws its body at
0.005 in and its leads at 0.008 in; the `.c` body is 5 mils and its pin decal 10. Also `(width 5)`,
`WIDTH=127000` (nm), `Wire 6`, `(stroke (width 0.127))` and field 4 of a legacy `P` record.

`KitSymbolShape.Width` is now on the base record, so no positional constructor changed and each reader
sets it in **its own coordinate unit** — the unit is deliberately not pinned down, because circuitRF has
three stroke TIERS and only the ORDER of the widths within one symbol can survive.
`KitTemplateSymbol.StrokeTiers` maps them: nothing stated or one width → all Normal; two distinct →
Normal/Thick; three or more → thinnest Thin, thickest Thick, everything between Normal. A width of 0
means "the editor's default" in several of these formats and is filtered out rather than treated as a
hairline.

**The obvious rule is wrong, and the measurement is what says so.** Anchoring Normal at the most COMMON
width was the first design. There is one lead per pin and a fixed handful of body pieces, so on
anything past a few pins the LEADS are the most common width — which put the body one tier below normal
on essentially every imported symbol and left a whole imported library looking faint beside circuitRF's
own. The thinnest stated width is the symbol's ordinary line; none of these formats draws a body
heavier than its detail.

### The pin name's side

Two grammars state it outright: `.hkp`'s `*TEXT` field 4 (a signed justification, −1 left / +1 right)
and `.PLX`/`.DSL`'s `(justify "right")` on the pinName's text node. The rest fix it through the pin's
own rotation, because the name always sits on the BODY side of the terminal — so
`ComponentSymbolLead.NameAlignFor` derives it there. **The two spellings agree, and
`Gate17` compares them in one assertion over every grammar**, which is what makes the derived answer a
measurement rather than a guess.

circuitRF had no such property, so one was added: `SymbolPin.NameAlign`, defaulting to `Left` — which is
"drawn to the RIGHT of the pin", because the alignment describes the TEXT and left-aligned text starting
at the pin runs rightward. That default is exactly what both renderers already hard-coded, so no
existing symbol changes. It persists in `.csym` as an additive field **omitted when it is the default**
(`JsonIgnoreCondition.WhenWritingDefault`), so every existing symbol re-serializes byte for byte and no
`FormatVersion` bump was needed — the same shape `LayoutFile.Pins` already uses. Gated by
`ComponentImportTests.ThePinNamesSide_RoundTripsThroughCsym_AndCostsNothingWhenItIsTheDefault`.

Without it, the whole right-hand column of an imported part's names is drawn outward, away from the
body, into empty space. `ComponentPreviewRenderer`'s fit had the matching assumption and now measures
the side per pin — reserving room on the right for a name drawn on the left is the same off-centre
error its per-pin measurement already existed to avoid, made twice.

**Still not imported: symbol FREE TEXT.** A designator or value placeholder a file draws has nowhere to
land — that needs a new `KitSymbolShape` case carrying the string, its size, its rotation and its own
justification. The pin-name alignment above is a property of the PIN and is unrelated to it.


---

## AUT-3 — the creation capabilities come below the firewall (2026-09-05)

`brief-automation-3-authoring-verbs.md`. Three operations that only a view model could perform are
now functions here, and the GUI calls them: `WorkspaceCreate.Create` (`Workspace/`), `CellCreate`
(`Cells/`), and `ComponentImport` — which moved from `src/Ui/Layout` unchanged. The verb side is
`src/Cli/RESOLVED.md`.

### `ShippedTechnologies` had to bring its resources, and the failure mode is silence

The class was already framework-free by explicit design (plain .NET `EmbeddedResource` rather than
Avalonia's `AssetLoader`, because `AssetLoader.Open` throws with no live platform). What was not
portable is that it reads `Assembly.GetManifestResourceStream` **on its own assembly**: moving the
class alone leaves it compiling, enumerating nothing, and reporting nothing — no exception, no
warning, and a `new workspace` that quietly creates a technology-less workspace.

So `src/Ui/resources/technologies/*.ctech` moved to `src/Design/resources/technologies/` with the
`EmbeddedResource` item. The manifest names change with the root namespace, which `Discover`'s "the
segment between the last dot and `.ctech` is the file stem" rule survives unchanged. Two tests read
the old path and were pointed at the new one; `ShippedTechnologiesTests` is the one that would have
caught a silent miss, and it passes on the new assembly.

### A namespace and a type both named `Symbol`, and a `using` alias does not fix it

`ComponentImport` compiled in `CircuitRF.Ui.Layout` and stopped compiling in `CircuitRF.Design.Layout`
with `CS0118: 'Symbol' is a namespace but is used like a type` — because `CircuitRF.Design.Symbol` is
a namespace *and* holds a type of the same name, and from any other namespace in this assembly the
enclosing-namespace member is found first.

**`using Symbol = CircuitRF.Design.Symbol.Symbol;` does not help**, and the reason is worth
remembering: a namespace-or-type-name is resolved by walking the enclosing namespaces first and
consulting the compilation unit's using-aliases only after, so the namespace wins over the alias. The
tell is that only the TYPE positions fail — `new Symbol(...)` in an expression is fine, because that
is simple-name resolution and prefers a type. Two positions are spelled in full, with a note beside
them.

### The GUI's own New Cell is not one call, and forcing it to be one would break R-cc-1

New Cell creates the folder, refreshes the tree, reports "Created", and only then writes the
schematic — deliberately, because a schematic that fails to write must never roll back the cell that
already exists. New Schematic writes a second view into a cell that already exists, under a file name
that need not be the cell's. So what the GUI and `circuitrf new cell` genuinely share is the WRITE,
and that is the unit extracted: `CellCreate.WriteSchematicView` / `WriteSymbolView` /
`WriteLayoutView`, with `CellCreate.Create` as the headless composition of them. The byte-identity
gate compares the two compositions; a source scan (comments stripped) proves the view model calls the
same writers rather than keeping a copy.

`NewLayoutView(tech)` is separate from `WriteLayoutView(model)` for one concrete reason: the GUI opens
an editor session on the very model it saved, and reading the file back to get one would be a second
object and a second chance to differ.

### Two GUI checks that could not be left in the shell

`WorkspaceCreate.Create` refuses over an existing directory itself, not only in each caller's
pre-flight — otherwise a second route into creation (a future verb, a future command) skips the rule
`WorkspaceLock`'s header exists to protect. And the read-only-parent refusal SENTENCE moved here as
`WorkspaceCreate.UnwritableParentRefusal`, with `WorkspaceViewModel`'s own helper forwarding to it:
three GUI sites and the verb refuse in one wording, and two copies of a refusal are two refusals that
drift.

---

## The DRC engine and the `.wasm` rule model come below the firewall (2026-09-05)

`brief-automation-4-check-and-explain.md` R-aut4-3, so `circuitrf check` can run design rules with no
display. Nine files from `src/Ui/Layout/Drc` and all seven from `src/Ui/Layout/Assembly` moved here;
namespaces became `CircuitRF.Design.Layout.Drc` (joining `DrcLayerExpr`, `DrcLayerExprParser` and
`DrcWaiver`, which crossed with the `.clay` format in 2026-08) and `CircuitRF.Design.Layout.Assembly`.

**The closure was free, and it was MEASURED rather than assumed.** The whole move produced five
compiler errors, none of them a coupling:

- a stale `using CircuitRF.Ui.Schematic` in `WasmPersistence` that nothing needed;
- `AtomicFile`, which was already resolving to `CircuitRF.Design.Cells`' one through `src/Ui`'s
  `GlobalUsings` rather than to `CircuitRF.Ui.Updates`' same-named class;
- three fully-qualified `Ui.WBond.WBondSnap.ToDbu` calls in `DrcWireCheck`.

`tests/Ui.Tests` then passed **unchanged** — 12,053 tests — which is the same evidence Gate 2 of
`brief-cli-em-verb.md` asked for when the layout model crossed.

### What did NOT move, and why each is not the engine

- **`DrcRunReport`** stays in `src/Ui/Layout/Drc`. It takes an `IMessageSink` and posts a run's
  verdict to the Messages panel; that is a UI surface, not a design rule.
- **`WBondWireClearance`** stays for the same reason one level down: it reads the built-in wire
  clearance out of the per-USER preferences file. The engine already takes the number as
  `DrcRunSettings.WireClearanceNm` (an init-only member, added exactly so a caller can state it), so
  the preference belongs to the GUI and the engine's own default — circuitRF's half a mil — is the
  right answer for a caller with no user to ask.
- **The wire half of a check needs a `WBondCheckContext`**, which carries the wBond design the layout
  editor's document installs at runtime. Which wires ride over a layout is a property of what is
  OPEN, not of the artwork, so a headless check of a `.clay` alone has no wires and checks none.

### `WBondSnap.ToNm` had to move even though `WBondSnap` cannot

`WBondClearance` is the R-wbd-1 crossing — it converts a LAYOUT into nanometres so a 3D wire point can
be measured against it — and it converts through `WBondSnap.ToNm`. `WBondSnap` itself cannot cross:
it needs `LayoutSnapQuery` and `SnapFeatureKind`, which ARE the layout editor.

The integer pair moved to **`LayoutUnits.NmToDbu` / `LayoutUnits.DbuToNm`** and `WBondSnap.ToDbu` /
`ToNm` forward to them, so every existing call site keeps its spelling and there is still exactly ONE
implementation. That property is not cosmetic: `WBondClearance`'s own header records this conversion
shipping broken twice from a second copy, invisibly, because at the default 1,000 DBU/µm nm and DBU
coincide exactly.

**The arithmetic is unchanged — `double`, `MidpointRounding.AwayFromZero` — and deliberately not the
`decimal` pair beside it.** `LayoutUnits`' documented exactness rule is about a user-entered quantity
in a named unit; this converts a whole coordinate, it was written in `double`, and every clearance
circuitRF has ever reported came out of it. A `decimal` re-derivation would be more exact past 2^53
and would also change measured results — a numeric change smuggled in under a file move.

### The `.wasm` parser's messages are on the allow list, moved not authored

`UserFacingTextGateTests` fires on user-facing text below the firewall, and 26 of these are:
`DrcPredicateParser`'s parse errors and `WasmPersistence`'s two format refusals. They are listed in
`tests/Firewall.Tests/user-facing-text-allowlist.txt` under a dated heading rather than converted to
`Diagnostic`s — R-aut4-3 moves whole files without reshaping them, and converting 26 parser messages
under cover of a file move is the change nobody could review. They are also the family where a plain
sentence is closest to defensible: a parse error already carries the offending TEXT and a character
POSITION, which is the typed half a `Diagnostic` would have added, and its reader is the person who
wrote the expression. Converting them is still worth doing, on its own, later.

### `CellViewFileValidator` moved too

`src/Ui/Schematic` → `src/Design/Cells`. It answers "would this file survive being adopted as a
cell's schematic / symbol / layout view?", every type it touches (`SchematicPersistence`,
`SymbolPersistence`, `LayoutPersistence`, `CellFolder`, `GzipTextFile`) was already here after AUT-2,
and `check` needs it. `HierarchyResolver`, listed beside it in the brief's table, did NOT move and
should not: it takes `EditableComponent` and `SchematicEditModel`, which are edit-session types.
`check` resolves cell references through `CellSymbolResolver` instead, which is what the editor draws
with.

---

## AUT-6 — the component catalogue, and the reference pages embedded (2026-09-05)

`brief-automation-6-reference-and-components.md`. Two additions to this project, both read-only.
`src/Cli/RESOLVED.md` carries the verb's own findings; what follows is what belongs to the code that
lives here.

### `ComponentCatalog` — one computation, two renderings

`Schematic/ComponentCatalog.cs`. The data half of `DocTables.ComponentParameters` was extracted here,
beside the registry it reads, and `DocTables` now renders *from* it. That is R-aut1-1's rule one level
up: **the documentation table and the machine answer must be one computation, or they will disagree
the first time one is changed.** `DocTables` stays in `src/Ui` — it also reads `ToolbarCatalog`, which
is UI — and nothing else moved.

The opaque-payload filter moved with the data half. Match's and wBond's `Design` is base64 of a whole
design's JSON; the parameter panel already declines to show it as a row, and a catalogue listing it
would invite exactly the hand edit that produces a component refused at elaboration. One filter, one
place, so the page and the machine answer omit the same row.

### `ComponentModel.PortCount` is NOT the net count, and that changed the design

The brief asked for the port count "from the model". **The model cannot give it.** `PortCount` is the
model's own port count in the MNA sense:

| | `PortCount` | nets its `.cnl` line takes |
|---|---|---|
| `ResistorModel` | 2 | 2 |
| `IProbeModel` | **1** | **2** (`np`, `nm`) |
| `FetModelBase` | **2** | **3** (g, d, s) |
| `SddModel`, 2-port | **2** | **4** (`SDD:M1 Vin 0 Vout 0`) |

The commonest components agree by coincidence, which is what makes this the quiet kind of wrong: a
catalogue published on `PortCount` would have been right for R, L and C and wrong for a third of
everything else, with nothing reporting it. So the catalogue reads **`SymbolPortDefs`** — the same
walk `NetExtractor` emits nets by — and constructs **no model at all**, not even a parameterless one.

The consequence is stated rather than hidden: a factory token with no `SymbolKind` (`Chain`,
`ExtDevice`, `I_nTone`, `SemiC`, `Short`, `Term`, `V_nTone`) has no net count anywhere below the UI
firewall, and the catalogue reports that instead of a number.

### `ComponentTypeRegistry.PortCountParameter` — a new fact, gated by measurement

A variadic component must report what SETS its count rather than a plausible default (R-aut6-9), and
no registry held that. Added here as a pure lookup beside `OwnsUniquePortNum`:
`Snp`/`ZPort`/`Sdd` → `NumPorts`, `VerilogA` → `Pins`, `Switch`/`SwitchD` → `Throws`, `WBond` →
`Arrays`.

**It is not trusted on its word.** `ReferenceCliVerbTests` measures variadicity — a kind whose
`SymbolPortDefs.For(k, 2).Length` differs from its `For(k, 3).Length` is variadic, whatever anyone
declared — and fails any such kind that the catalogue reports as fixed. So a future variadic component
added without an arm fails by name rather than publishing a specific wrong number.

**A token can be variadic where its symbols are not.** `Switch` and `SwitchD` are two tiles over one
engine component whose port count is `1 + Throws`; each tile draws a fixed pin set and seeds its own
`Throws`. The catalogue reports the tiles as fixed and the token as `Throws`-determined, which is what
is true of each.

### `LibraryCatalog.InternalOnlyKinds` is public now

`Generic` and `Unknown`. The catalogue has to answer the same "can anyone place this" question, and a
second copy of that list is a list that will disagree with the first — which is the failure
`OwnsUniquePortNum`'s own remarks record from three hand-maintained `SymbolKind` lists that had
already diverged within a week.

### The reference pages, embedded

`Reference/ReferenceLibrary.cs` plus eight `<EmbeddedResource>` items in the `.csproj`.
`ShippedTechnologies` is the precedent and the trap is the same one, already paid for once: **a class
shipped without its resources compiles, enumerates nothing and reports nothing.** The items and the
class are in one commit and `ReferenceCliVerbTests.EveryTopic_IsEmbedded_AndItsBytesAreTheAuthoredFile`
is what turns a future omission into a failing test.

Three decisions worth keeping:

- **Plain .NET `EmbeddedResource`, read through `Assembly.GetManifestResourceStream`** — never
  Avalonia's `AssetLoader`, whose `Open` throws with no live platform. This whole project is
  framework-free by design and the CLI has no platform at all.
- **The files are referenced IN PLACE from `docs/user/src/reference/`, not copied in.** A copy is a
  file that will be edited on one side only and nothing will report the drift. The front matter and
  the docs factory's `{{…}}` placeholders are therefore stripped **at read**, not at build, which is
  what leaves the embedded bytes byte-identical to the authored ones for the gate to compare.
- **The `LogicalName` is explicit**, because these items live outside the project's own cone where
  MSBuild's default manifest name is derived from a relative path nobody should have to predict.

**145,410 bytes** across 8 topics, in this assembly — so in the GUI's binary as well as the CLI's.
Reported by the gate's test output rather than asserted against a threshold, so the number stays
current in the TRX with nothing to maintain.

## The terminal table said "net 1 is terminal 1" for a third of the library (2026-09-05)

AUT-6 gave every component section a generated terminal table. The owner read it and asked what the
point was: for R, L, C, SRLC, PRLC, Bead, NonlinearC, the sources, TLIN, MLIN, the tapers, the bend
and Match it printed two columns saying net 1 is terminal 1 and net 2 is terminal 2. It restated its
own row numbers.

**The cause is one `default:` arm.** `SymbolPortDefs.For` has an explicit case for every kind whose
terminals have names — `g d s`, `a c`, `c b e`, `out+ out- ctrl+ ctrl-`, `RF LO IF`, `np nm` — and a
catch-all returning `("1", 0, -200), ("2", 0, +200)` for the vertical two-terminal parts. Those
names were never meant to be READ; they existed so a pin had a string. The moment a table rendered
them, the catch-all became a claim, and the claim was empty.

**The half the owner expected was real and was in the ARTWORK, not the data.** Vdc and VTone draw
`+` and `−` as text primitives at fixed coordinates in `BuiltInSymbols`, and Term names its pins
that way in the port table. P1Tone — which is Term with a source behind its resistance — did
neither, so its polarity existed only in a code comment. The five two-terminal sources (Vdc,
ToneSource, CurrentToneSource, P1Tone, PnTone) now name their terminals `+` and `−` in
`SymbolPortDefs`.

**That rename was safe for a reason worth writing down: nothing connects by pin NAME.**
`SchematicEditModel.PortDefsOf` returns `(LocalX, LocalY, PortIndex)` and carries no name at all, so
net extraction, hit-testing, wire snapping and pin-follow are all coordinate-and-index. A pin name
reaches only the symbol editor, `SpiceModelPeek`'s terminal listing and this catalogue. The geometry
is unchanged and `ATwoTerminalSourceNamesItsPolarity_AndKeepsItsGeometry` asserts the coordinates
alongside the names, because a rename that moved a pin would be a different circuit.

**P1Tone and PnTone still draw no polarity mark on their glyphs.** The table now names their
terminals and the symbol does not show them; adding two `Txt` primitives to each is the obvious
follow-up and was deliberately not taken here, because changing artwork regenerates figures and that
churn belongs in its own change.

**What a pin name cannot carry is whether the order MATTERS**, so that is stated separately, in
`ComponentTypeRegistry.TerminalNote` — the exact analogue of `ParameterDescription`, keyed on kind,
empty by default. It reaches `CatalogPorts.OrderNote` and from there both renderings. Three things
it records that nothing else in the codebase said out loud:

- **An inductor's terminals are not interchangeable once a `Mutual` couples it** (owner). A mutual
  stamps `−jωM` across two inductor branch rows, and each branch current runs from that element's
  own terminal 1 to its terminal 2 — so swapping one element's ends reverses the coupling. That is
  the dot convention, and terminal 1 is the dotted end. It applies to SRLC and PRLC too, because all
  three implement `IInductiveBranch` and any of them may be either end of a mutual.
- **MTAPER, MKLOPF and Match are asymmetric** and their ends are told apart by a parameter — the W1
  end, the Z1 end, the R1 side. Match's own port-def comment already warned that a swap "silently
  reverses every asymmetric match"; the warning had never reached a reader.
- **"They are interchangeable" is an answer, not a blank.** A reader told a resistor's ends may be
  swapped is finished. A reader shown a numbered table has to go and find out. The two occupy the
  same space on the page and only one is information.

**A file-backed kind gets no note and keeps its numbered pins** (`SpiceModel`, `VerilogA`) — its real
terminals come from the file it names, so nothing written here could be true of a placed one (owner).

### The VCCS's table had been rendering under the VCVS's heading

Separately, and not caused by AUT-6 — only made visible by it. `{{table: components/Vccs}}` was
authored at the end of the VCCS section; the VCVS section was later inserted ABOVE it (`5c9df273`)
and pushed it down. The result: the VCCS section had no table at all, and the VCVS section printed
`G = 10 mS` under its own heading, where the parameter is `E`. Nothing failed — both halves render,
the numbers are real, and the only way to notice is to already know what a VCVS's parameter is
called.

`EveryComponentTableIsInTheSectionForItsOwnComponent` gates the generic shape: a
`{{table: components/X}}` must sit in a section whose `{{symbol: y}}` names the same component. Two
things the scan has to get right — split on `#{3,6}` and not `###`, since the FET and MOS families
are one `###` section with a `####` (and `#####`) sub-section per law; and do not REQUIRE a table,
because Ground is one terminal on net `0` and wants none (owner), and the p-channel Statz and
Materka sub-sections legitimately share Curtice-P's figure.

Two smaller repairs came out of the same read. `IProbe` had no table at all, which is the worst
omission of the set — `np → nm` is the direction every `I("Iout", 1)` measurement is signed against.
And `DocTables.ComponentParameters` answered an empty parameter list with "this component's rows are
authored by the user", which is true of an SDD and false of an IProbe, a Ground or a Mutual;
`ComponentTypeRegistry.UserParamTemplate(kind) is not null` separates the two, so the ones with none
now say "No parameters."

A one-terminal component is rendered as a sentence rather than as a two-column table with a single
row in it — that being the same "net 1 is terminal 1" shape, at N = 1.

---

## RC-5 — restore points: what a boundary is, and what a restore may not touch (2026-09-06)

`brief-revision-control-5-checkpoints.md`. RC-3 supplied the commit primitive; this is everything
that decides WHEN one happens, what it is called, and how the workspace is put back. `src/Design/Revision`
gained `CheckpointReferences`, `CheckpointMessage`, `CheckpointOrigin`, `RestorePoints`,
`WorkspaceCheckpoints`, `WorkspaceArming`, `WorkspaceRestore`, `RestoreMarker`, `LargeFileGuard`,
`BatchSession`, `AgentContract`, `RestorePointMessages` and `WindowChannel`.

### The checkpoint was parentless from the first draft, and gate 14a would have caught it otherwise

Reported because the brief asks. `GitCheckpoint.Record` (RC-3) already passed no `-p` and said why in
its own header, so gate 14a — delete the fifth of ten references, prune with an immediate expiry in a
scratch copy, assert the fifth object is gone and the other nine are there — passed on the first run.
**The gate still earns its place**: gate 14 asserts the OTHERS survive, which is true of a parent
chain as well, so nothing else in the suite could have told the two apart. The scratch copy is the
part worth remembering: the repository's own `gc.pruneExpire = never` (§4.5) makes the measurement
impossible in place, and that configuration is itself under test everywhere else.

### `git cat-file --batch`, not one invocation per entry

`RestorePoints.List` reads every entry's metadata in two processes whatever the list's length — one
`for-each-ref` for the reference-to-object mapping, one batched object read for the rest. The obvious
shape, `git show -s` per entry, is roughly a third of a second on a list of fifty, paid on every panel
refresh, and this is a panel that refreshes on every boundary. The output is parsed by finding the
`<oid> commit <size>` header lines rather than by the announced size: the sizes are BYTES and .NET has
already decoded the stream to CHARS, so the two disagree the moment a label contains anything
non-ASCII.

### A restore may only take away what the pre-restore entry actually captured

The rule as written is "a file created after the checkpoint is removed", and the naive reading — every
file present now that is not in the target tree — is wrong in one case that matters. A file left out
under R-rc5-15a (over the size threshold, at a boundary nobody was at) is on disk, is not in the
target tree, and is **not recoverable from anything**. Removing it would be the silent design-IP loss
§8.2a spends a section rejecting, arriving by a different door. So the removal set is
`(pre-restore tree) ∩ ¬(target tree)`, which is exactly "things the fallback entry can bring back",
and `WorkspaceCheckpoints.Take` returns its `TreeId` even when it recorded nothing so a restore can
compute it after an unchanged-tree boundary.

### `.gitignore` and `.gitattributes` are restored to their CURRENT content, not the entry's

R-rc5-12c says the policy files are preserved. They are also in the tree, so `checkout-index` writes
the old ones over them; they are re-applied afterwards, and they are the only files a restore leaves
as it found them. The `.cws` is not in that set — it is restored like any other document, and then its
one recording field is put back, because a restore is the user's decision about content and never
about recording.

### The large-file guard never asks about the policy files

Found by a gate running at a 1 kB threshold: circuitRF's own generated `.gitignore` block is ~1.3 kB,
so it appeared as an unexpectedly large newly-added file. At the shipping threshold (16 MB) it never
would — but the exclusion is a rule about MEANING rather than a workaround. The guard's question is
"what IS this file", and for these the answer is fixed: they are how the workspace records what it
keeps, so leaving one out would discard the answers a designer has already given.

### The batch's modified set is measured, not declared

`BatchSession.Close` diffs the tree the batch opened on against what is on disk, through a private
index. An agent that forgot to mention a file it wrote cannot then leave that file's window showing
the old content — which is the failure R-rc5-7b exists to prevent and exactly what an honour-system
list would reintroduce. It costs one `add` and one `diff-index` at close.

### The reference namespace carries the sequence, and that is what survives a hand-deleted reference

`refs/crf/restore/<6-digit sequence>`. The next number is one past the largest that EXISTS, never a
count — a count reuses a number the moment retention drops one. It is safe because retention keeps the
newest N unconditionally, so the largest is never the one thinned. The trailer carries the same number
so an entry can be read without its reference name, and `RestorePoints.List` prefers the trailer.

## RC-6 — retention, the enclosing-repository hold, and turning it off (2026-09-06)

`docs/sonnet-briefs/brief-revision-control-6-retention-hold-and-off.md`. Below the firewall this added
`RetentionPolicy`, `RetentionSweep`, `SessionHousekeeping`, `EnclosingRepository`,
`RepositoryAdoption`, `RevisionSwitch`, `RevisionGaps` and `HoldMessages`, and gave `ThinningJournal`
its writer. Gate: `tests/Ui.Tests/Revision/RetentionHoldAndOffTests.cs` (27 tests, ~58 s, every one of
them in the routine tier).

### The finding that mattered most: two paths reached a repository that was not the workspace's own

R-rc6-6 says circuitRF writes to exactly one repository — the one whose root IS the open workspace —
and the brief asks for any path by which a checkpoint could reach another to be reported. **Two
existed, and both were silent.**

- **`WorkspaceArming.Arm` planted a repository inside somebody else's tree.** For a workspace INSIDE an
  enclosing repository, `git.IsRepositoryRoot()` answers false — it asks `rev-parse --show-prefix`,
  which is non-empty there — so the arming path fell straight through to `GitRepository.Create`, which
  runs `git init` **at the workspace root**. The result is a nested repository inside a project
  somebody else owns, created because a designer opened a folder. Nothing committed to the ancestor, so
  the worst outcome §7A.1 names did not occur; but §12 Q4's ancestor row was not implemented at all,
  and the row it silently took instead was "start a fresh history here".
- **It also rewrote a user's own repository configuration without asking.** For a repository the USER
  created at the workspace root, `IsRepositoryRoot()` answers true and the marker is absent, so
  `existed` was false and the same call ran `Configure` — every one of §4.5's rows, `--local`, into
  their repository — plus `WorkspacePolicyFiles.Ensure`, which appends circuitRF's block to their
  `.gitignore` and `.gitattributes`. That is §12 Q4 *Refinement 3*'s question answered "adopt" by
  default, on their files, with nothing said.

Both are fixed by `EnclosingRepository.Detect` running **before anything is created**, and by
`WorkspaceArming` deriving `existed` from the detected placement rather than from a root test.

**And `AgentContract.StateOf` had the same hole from the other side.** It tested
`git.IsRepositoryRoot() && !IsManagedByCircuitRf(git)` for the held state, which reports the ancestor
case as **on** — so an agent asking whether it had a floor under it was told yes, in the one situation
where a checkpoint must never be taken at all. It now goes through the same detection. This is exactly
the shape R-rc0-15 warned about: a `.git` directory alone cannot answer the question, and neither can
its absence at one particular path.

### `rev-parse --show-prefix` answers three rows; the fourth needs a walk, and the walk is not optional

`rev-parse` goes UP and never down. A repository nested INSIDE the workspace is invisible to it, and
handing such a directory to `git add` records a **gitlink** — mode `160000`, a pointer to that
repository's current commit — which is §7A.5's "committed as something by the enclosing workspace",
with a warning nobody reads. `NestedRepositories.Find` was already there from RC-3; RC-6 is what makes
it part of the placement answer, and the gate asserts the checkpoint's tree contains **neither the
files nor a `160000` entry**.

### A clock a century in the past is not expressible, and the gate had to move the century

The brief's gate 1 asks for every checkpoint's timestamp to be set a century in the past. **That
fixture records nothing.** `GitWorkspace.SetClock` writes `GIT_AUTHOR_DATE`/`GIT_COMMITTER_DATE` as
seconds since 1970, so 1926 is a negative number and git's commit path refuses it — the first
`Take` returns `Recorded: false` and every assertion after it is vacuous. The gate puts the century on
the SWEEP's clock instead (`now + 100 years`), which is identical arithmetic and is also the more
honest reproduction: the hazard is a machine that woke up in the wrong century, not a repository that
was written in one.

### The bound cannot distinguish a clock jump from a long absence, and it is not tuned away

R-rc6-3 refuses a pass that wants more than `RetentionPolicy.MaxFraction` (a quarter) of the unkept
entries. **A designer returning after a long absence produces exactly the same data as a clock jump**:
everything past the floor is legitimately expired, the pass is refused, and the message says the clock
is wrong when it is not. There is no signal in the repository that separates the two — the newest entry
is old in both cases — so the alternative is a policy that deletes in both, which §1.4 forbids. The
refusal costs nothing (nothing is removed, and the floor already protects the newest N); what it costs
is one message that is, in that one case, wrong about the cause. Recorded rather than tuned away.

### Two writers with different retention preferences on one share: still open, and now bounded

The architecture holds this open (§12) and asked this brief to report rather than settle it. **It did
not arise in review as a new failure**, because §12 Q24's rule removes most of it: a session that
recorded nothing sweeps nothing, so a colleague who only looked applies no preference at all. What
remains is two people who both EDIT one shared workspace with different retention settings — whichever
closed last applies theirs, bounded by R-rc6-1's floor and R-rc6-3's fraction, and nothing is destroyed
either way because thinning never prunes. **Recommendation, not taken here: leave retention per-user.**
Moving it into the `.cws` would make one designer's preference decide another's recovery window on
their own machine, which is the same identity mistake §4.4 corrected — and the failure it would prevent
(a state thinned earlier than you expected) is recoverable, while the one it would introduce is a
setting you cannot change on a workspace you do not own.

### `gate 7a` and `R-rc6-7a` cannot both be literally true, and the split matters

R-rc6-7a says the management marker is written **whichever answer is given** — that is what makes the
question asked once. Gate 7a says *keep my settings* leaves the repository's own configuration
**byte-for-byte unchanged**. The marker IS repository configuration, so one of the two has to give.

Built as: **`KeepUserSettings` writes only `circuitrf.managed` and `circuitrf.management`, and none of
§4.5's rows, and neither policy file.** The gate asserts every §4.5 key is *absent* and that the
config file is identical outside the `[circuitrf]` section. Dropping the marker instead was considered
and rejected: without it the question returns on every open, which R-rc6-7a explicitly forbids, and
`R-rc5-6g`'s advertised state becomes underivable. Putting the marker in `.git/circuitrf/` beside the
journal would satisfy the letter of the gate and keeps the archive-carries/clone-does-not property —
but it splits "did circuitRF create this" across two homes, which is the drift R-rc0-15 chose one home
to avoid.

### A clone presents as HELD, not as off — and that is R-rc0-15 working

R-rc6-14c's gate clones a switched-off workspace and asserts the flag travelled. It does. But the
clone's *state* is `Held`, not `Off`: git does not clone a repository's config, so the management
marker does not travel — which is the exact property R-rc0-15 chose config placement FOR — and the
clone therefore presents as somebody else's repository at the workspace root and is asked about. It
records nothing either way, which is what R-rc6-14c asks. **That it arrives held rather than off is
RC-9's to settle**; the gate pins the behaviour so it is visible rather than discovered.

### Thinning drops the reference FIRST and journals SECOND, and the order is the failure analysis

Journalling first and dropping second lists the same state **twice** on a crash — once live and once
tidied away — which is the one thing a list a designer trusts must not do. This order risks the
opposite: an unreachable state with no record, recoverable through `git fsck --unreachable`. One is
recoverable and the other is not, so this is the direction that fails safely. **A journal write that
fails stops the sweep** rather than carrying on dropping references it can no longer record.

### The transition checkpoints need `forceRecord`, and it is the only caller

`WorkspaceCheckpoints.Take` suppresses a recording whose tree equals the newest entry's (R-rc5-5a),
which is exactly right everywhere else and wrong for the two recording transitions: a transition is not
a fact about CONTENT but about whether recording is happening, and a suppressed one leaves the gap with
one end — which renders as the quiet interval §5.7 forbids. `forceRecord` exists for those two callers
and nothing else, and both are also `IsAlwaysKept`, because a pair retention could thin is a gap
retention could erase.

### Turning recording OFF must never create a repository

`RevisionSwitch.TurnOff` uses `ExistingRepository`, not `WorkspaceArming.Arm`. Arming would `git init`
in order to record that the designer does not want a history, which is absurd and is R-rc5-4a's
surprise pointed at a directory. `TurnOn` does go through arming, because there creating one is exactly
what was just asked for. A workspace with no history yet records no transition at all, and there is no
gap because there was never anything either side of it.

### `GitReclaim` now removes the journal entries it acted on

It has to: after the prune those objects are gone, and an entry left in the journal offers a designer a
way back to a state that no longer exists — and `RestorePoints.ListIncludingThinned` would keep
rendering a row for it. Removed AFTER the prune, so a failure between the two leaves an entry pointing
at nothing rather than a state pointing at nobody.

### The journal survives a `git gc` and does not survive a `git clone` — both intended

`.git/circuitrf/thinned.jsonl` is not an object, a reference or a config key, so `git gc` does not look
at it and `git clone` does not copy it. Both are the answers this design wants: a routine pack must not
disturb the record of what was thinned, and a clone must not inherit one machine's thinning history
about restore points the clone does not have (`refs/crf/` does not travel either — R-rc5-1a). Verified
by the gate's own scratch-copy pack and by the clone in R-rc6-14c's fixture, in which the arriving
workspace has neither the journal nor the entries it names.

---

## RC-9 — clone, fetch/send, and the pin (2026-09-07)

`brief-revision-control-9-clone-and-pins.md`, `revision-control.md` §7, §7A.4, §9, §9.1, §5.2a.
`src/Design/Revision` gained `WorkspaceClone`, `WorkspaceRemotes`, `WorkspacePins`, `PinnedContent`
and `SharingMessages`; `CwsWorkspaceRef` gained `Pin`. Gated by
`tests/Ui.Tests/Revision/CloneAndPinsTests.cs` (19 tests, the brief's 15 gates).

### The architecture said what a pin RECORDS and never what a pinned reference RESOLVES to

**This is the whole build, and both §7 and the brief can be satisfied without it.** They say the
reference "carries a commit identity" and that moving it is explicit. An implementation that wrote the
identity into the `.cws`, reported "a newer version is available", and went on resolving `ws://alias/…`
through the library's working tree would satisfy every sentence in either document — and would be worth
nothing. The moment the librarian checks out anything else, the design resolves against content it was
never verified against: **the exact failure §7A.4 is written to prevent, arriving through the feature
meant to prevent it.**

R-rc9-15 is the sentence that settles it: with a pinned reference, editing a cell in the library and
coming back **deliberately does not** show the new cell. That is only true if the pinned bytes are what
is read. So `ExternalCellRef`'s alias table — the one place an alias becomes a directory — hands back an
**expanded copy of that version** for a pinned alias, and `PinnedContent` is what expands it.

Three consequences that are not obvious from the requirement:

- **Nothing is written into the referenced workspace** (R-rc0-5). `git worktree add` is the short route
  and puts administrative files in a repository that is not the open workspace's own. `git archive
  --format=zip` plus `System.IO.Compression` is a pure read of theirs and a write of ours.
- **Not `cat-file --batch` through `GitCommand`.** That type collects standard output as TEXT, so a
  bitmap referenced by a `.clay` or a `.csym` — precisely the case R-rc9-3 asks to be re-checked — would
  arrive corrupted and open without complaint. `--format=tar` was the other candidate and needs an
  extractor this repository does not ship on Windows.
- **A pinned reference must be read-only whatever `Editable` says**, and this is correctness rather than
  policy. An edit through it writes circuitRF's rebuildable copy of one version: it appears to work,
  reaches nobody, is in no history, and vanishes on the next rebuild. `SetReferenceEditable` refuses and
  says so, because a toggle that appears to work and does nothing is the class of failure §7A.2 exists
  against.

### What a pin costs, measured

Five referenced libraries, each pinned, each 271 files / 5.6 MB (macOS, Debug, warm page cache):

| | |
|---|---|
| `WorkspacePins.Survey` — the on-open report, 5 pinned aliases | **350–475 ms** (three git processes per alias) |
| first resolution, expanding all five | **600–735 ms** |
| resolution once expanded, alias table dropped | **0.1 ms** |
| 600 cell resolutions through the memoised alias table | **1.5 ms** |
| the expanded cache on disk | **28 MB** for 28 MB of library |

**The 0.1 ms row is not free and was 99 ms before a second memo existed.** `ExternalCellRef`'s alias
table is dropped on ordinary editing events — `CellSymbolResolver.InvalidateAll` rides a symbol-editor
save — so without one, a design with five pinned libraries paid a tenth of a second of subprocess starts
every time somebody saved a symbol, for an answer that had not changed. `PinnedContent`'s memo therefore
**outlives that table on purpose**, and the justification is that it answers a different question: the
alias table answers *where does this alias point*, which changes whenever a `.cws` is written; the memo
answers *is this exact, immutable commit present, and where is it expanded*, which changes only when a
pin moves or somebody rewrites the library's history. The first is dropped by `WorkspacePins.Invalidate`;
the second is dropped by `Survey` when it finds a pin that can no longer be honoured, which is the path
the window and the CLI both run on open.

**Survey's 350–475 ms is subprocess starts and stays that way.** It runs once, on open, on an explicit
Refresh, and after a pin changes — never per render — and each alias needs three separate answers from
git (is this a repository, does that commit exist, what is the newest). Batching them would mean parsing
one invocation's combined output, which is how a translation stops being keyed on structure.

### Nothing carries the management marker across, and the gate forbids it rather than observing it

R-rc9-5c wants a clone to reach the ordinary arming path. Git does not clone config, so that is free —
which is exactly why the gate is a **source scan of `WorkspaceClone` for `WriteMarker`** as well as an
assertion that today's copy has none. The same shape holds R-rc9-5a: the absence of restore points in a
copy reads as a bug, and the obvious "fix" is one line widening the refspec, so the gate forbids
`refs/crf`, `--refmap` and `+refs/*:refs/*` appearing in `WorkspaceClone` or `WorkspaceRemotes` at all.
**Nobody was tempted during the build**, and the row exists precisely because the behaviour is correct
and looks wrong.

### R-rc5-14's caveat is retired in half, and saying which half is the point

The old sentence — *"anything in a workspace it refers to is not part of this workspace's history and is
left exactly as it is"* — is still exactly true of an **unpinned** reference and is now misleading about
a **pinned** one, where the restore does bring back which version the design resolves against.
`RestorePointMessages.RestoreReferenceCaveat(bool)` picks, from whether the workspace has any pin at all.
Saying the weaker thing always would tell a designer whose references are all pinned that the restore was
less complete than it is; saying the stronger thing always would tell one with none that it was more.

### `git clone` needs two `safe.directory` entries, not one

R-rc9-5b is written as though a clone touches one tree. It touches two — the parent it writes into, and,
when the source is a local path (which is §7A's librarian scenario), the repository it reads — and git's
ownership check applies to each separately. `GitRunOptions` gained `SafeDirectories` for it; each is
named individually and never `*`. The working directory is the destination's **parent**, because a
process cannot start in a folder that does not exist yet.

---

## Review of RC-0 … RC-9 against `docs/design/revision-control.md` (2026-09-07)

A read of the whole series against rev 5 of the architecture. Coverage is good — every mechanism the
document specifies is present and the shapes are right, including the four that are easiest to get
wrong (the parentless commit through a temporary index, the per-checkpoint reference namespace, the
`--root` PATH walk behind the archive's deleted-file warning, and `gc.pruneExpire = never` with reclaim
as the only pruning path). What follows is what the read actually found: four defects, each of which
turns a stated guarantee into a sentence that is technically executed and practically useless.

### A refused retention sweep reported that it wanted to remove **zero** restore points

`RetentionSweep.Plan` returns `Thin = []` on a refusal — correctly, because §5.6 rule 3 refuses the pass
*whole* rather than applying a bounded prefix. `Run` then built the report from `plan.Thin.Count`, so
the one message this rule exists to produce always read *"circuitRF was about to tidy away **0** of this
workspace's 40 restore points at once, which is far more than an ordinary tidy-up."*

Rule 3's entire value is that it converts a clock fault from silent data loss into a sentence a designer
can act on. A sentence naming zero does the opposite: it reads as a defect in circuitRF, and the reader
learns nothing about their clock. `SweepPlan` gained a `Wanted` field for the count the pass would have
dropped had it been permitted to, and it is not derivable from anything else on the record — on the one
path where the number matters, the list it would have come from is deliberately empty.

### A restore that failed before writing anything left its interrupted-restore marker behind

`RestoreMarker` is written before the first file and cleared after the last, so that a crash mid-restore
is *detected on the next open rather than discovered by simulating* (§5.8, §12 Q25). But every failure
inside `WorkspaceRestore.Restore` returned through one `Fail` helper, including the `read-tree` that
runs **before** any file is written. A `read-tree` that refuses therefore left a marker on disk over a
working tree nothing had touched — and the next open greeted the designer with a report naming two
states and two ways out of a situation they were not in.

The marker's meaning is *this workspace may be half of two states*, and a workspace nothing was written
into is not. The fix is the distinction, not the clearing: failures **before** the first write clear the
marker, failures after it leave it. `FailBeforeAnyWrite` is the second exit, and a target tree holding
no files now takes it too rather than reaching `checkout-index` with nothing to check out.

### A marker the crash truncated was treated as no marker at all

`RestoreMarker.Read`'s own header said *"a truncated marker is a marker, so it is reported rather than
dropped — a file half-written by the same crash is evidence of exactly the state this is looking for"*,
and then caught `JsonException` alongside the I/O failures and returned null. So the marker was most
likely to be dropped **precisely when it was most likely to be true**: the write that got cut off is the
write the crash cut off.

An unreadable file now resolves to `RestoreMarker.Unreadable`, whose ends are blank, and the report
picks a second sentence for it (`RestoreWasInterruptedUnnamed`). The ordinary message quotes two labels;
rendered from blank ends it would have read *"Going back to '' did not finish … or go back to ''"*,
which names a defect rather than an interruption. Only an **absent** file, or one that cannot be read at
all, still means nothing was in flight.

### `GitDiscovery.Find` memoised the answer and threw away the reasons

The cache is keyed on the configured path, which is right. It stored only the `GitInstallation?`, so a
cache hit handed back an **empty** rejection list — and RC-4's Detect line is the one place in the whole
application where an under-floor or wrong-path git is ever reported by name (§4.7). Pressing Detect
twice on such a machine gave *"No usable git: '/usr/bin/git': it is version 1.8.3, older than the 2.9
circuitRF needs"* and then *"No git was found on PATH."* Nothing about the machine had changed; only
circuitRF's memory of it had. The rejections are memoised with the answer now.

(Detect also invalidates the cache before asking, which is a separate point and not a substitute for
this one: it is the button somebody presses **because** they just installed git or fixed a `PATH`, and a
memoised "no" answers for the machine as it was before they did.)

## RC-11 — correcting what you wrote (2026-09-07)

`docs/design/revision-control.md` §5.11, `brief-revision-control-11-correcting-what-you-wrote.md`.
Gates: `tests/Ui.Tests/Revision/CorrectingWhatYouWroteTests.cs`.

### `--amend` was permitted in one file and buys nothing — the exemption is unspent

R-rc11-9 permits `git commit --amend` at exactly one site, asserted by name so it cannot spread. It is
not used, and the reason is not tidiness: **`--amend` needs the repository's shared index and restamps
the committer date**, which are precisely the two things a title correction must not do. §4.6 keeps
circuitRF off the shared index deliberately, and R-rc11-10 says a correction does not restamp the
version — a title correction that quietly moved a version's date would make §5.6 rule 2's ordering
argument false in the one list a designer reads it from, and that is `--amend`'s DEFAULT behaviour.

`commit-tree` over the recorded tree, with the recorded parents and the recorded identity handed back
through `GIT_AUTHOR_*`/`GIT_COMMITTER_*`, does the job with neither. **So RC-7 gate 11's scan stays
absolute rather than narrowing**, and `--amend` is still forbidden everywhere.

That needed one new thing: `GitRunOptions.Environment`, applied AFTER `GitEnvironment.Apply` so a
caller can override the identity that type sets. It is the only interface git offers for writing an
object with a time other than now. **The date is passed on in git's own raw spelling**
(`1700000000 +0100`) rather than through a `DateTimeOffset` — reformatting would lose the recorded zone
offset, and R-rc11-10 is about the time as it was recorded, offset included.

### Only the SUBJECT LINE is replaced; the message is otherwise carried through verbatim

Rebuilding the message through `CommitMessage.Build` would be correct for a commit circuitRF wrote and
a **lie** about anything else on the line of work — somebody's own `git commit`, an import, a script —
because it would add circuitRF's own *"You asked circuitRF to keep this version"* line and its origin
trailer to a commit that never carried them. Replacing one line preserves every trailer, including
R-rc7-6's restored-from pair, without the correction having to know which of them exist.

### A rename needs an explicit SUBJECT, not only a label — four of six origins ignore the label

`CheckpointMessage.SubjectFor` derives the subject from the ORIGIN for four of the six: a
workspace-close entry is *"workspace closed"* whatever anybody types, and the same is true of
before-restore and the two off-period ends. A rename routed through the label alone would therefore
have **silently done nothing** on exactly the entries a designer most wants to name — the automatic
ones they are trying to find again. `Build` grew an optional `subject`, and `MarkKept` had to start
passing `point.Label` through it: without that, marking a renamed entry *keep* would quietly rebuild
the subject from the origin and undo the correction, weeks later and with nothing to see.

The rename sets the INTENT to the corrected words as well as the subject. R-rc10-9's search reads the
intent, so a correction that left it behind would keep the careless wording findable in the one place
a designer cannot see it — which is the whole failure §5.11 exists to prevent, arriving by a side door.

### The annotation lives in circuitRF's own notes namespace, and travels as a SEPARATE invocation

`refs/notes/circuitrf`, not git's default `refs/notes/commits`. A designer with their own notes on the
default reference would otherwise find circuitRF replacing them, silently, with no way back that is not
`git fsck`. Same rule as the checkpoint namespace (§5.2a).

**It travels as a second, best-effort git call on fetch, send and clone — never as a second refspec on
the first one**, and the reason is a trap: a command-line refspec REPLACES git's configured one rather
than adding to it, and worse, **a non-wildcard refspec naming a reference the remote does not have is a
fatal error**. Every workspace in which nobody has written a correction is exactly that case, which is
nearly all of them — so the tidy-looking version would have broken Pull Changes for everybody in order
to carry a string almost nobody has written. The send resolves the local reference first and skips the
call when there is none, for the same reason in the other direction.

`WorkspaceRemotes.Fingerprint` gained the notes reference: a fetch that brought in nothing but a
colleague's correction did change something, and reporting *"nothing new"* for it is R-rc9-6's
silent-download defect by another route.

### "Shared" has THREE states, and a bare set could only express two

`HistoryBrowser.Shared` returned an empty `HashSet` both for *no other copy* and for *a remote
configured and never fetched* — which are opposite situations. R-rc11-2 says the second answers
**shared**, because a correction refused is recoverable and an erasure believed is not (§9A.1's rule).
`VersionSharing` returns a `SharedVersions` carrying the reach, and `Contains` answers true for every
version in the unknown state. Computed **once per list read** and handed to every row: a per-row
`merge-base --is-ancestor` would be one subprocess per entry on a panel that refreshes at every
boundary.

### The clone journey's review is the SOURCE's titles, and only for a local source

§5.11 names clone as one of the three ways a history leaves the machine, but circuitRF's own Clone
Workspace brings one IN from an address. What can honestly be reviewed first is a source this machine
can already read — §7A's librarian case, one workspace on this machine copied out of another. A source
behind an address cannot be enumerated before it has been fetched, so the list is empty and the dialog
is not shown; an empty review is a review that teaches people to click through the next one.

## A restore writes the files that differ, and names them (2026-09-08)

`brief-history-restore-in-place.md` §2, §4 phase 1. The design half of a restore that no longer
rebuilds the window around it; the window half is in `src/Ui/RESOLVED.md`.

### `checkout-index -a -f` wrote the whole workspace, every time

`WorkspaceRestore.WriteFiles` restored every file in the target tree whether it differed or not. Two
consequences, and the second is the one that mattered:

- **`FilesWritten` reported the size of the workspace**, not the size of the change — so the sentence
  a designer reads after going back named a number with nothing to do with what they had done.
- **Every untouched file came back with a new modification time.** That is what made a
  one-schematic restore look, to everything downstream, like a workspace that had changed entirely.

**Git already knew.** `Restore` holds both tree ids a few lines apart — the pre-restore checkpoint's
and the target's — and one read-only `diff-tree` answers "which paths, and how" before a single file
is written. Measured on a synthetic workspace of 2,001 files / 43 MB with one file differing:

| | wall clock |
|---|---|
| `checkout-index -a -f` (what every restore did) | **0.40 – 0.51 s** |
| `diff-tree -r -z --name-status` + `checkout-index -f -- <one path>` | **0.03 s** |

and 2,000 modification times left alone.

### Three things worth keeping in mind if this is touched again

- **A failed diff is a third answer, not an empty one.** `HistoryBrowser.Compare` returned `[]` both
  for "nothing differs" and for "git would not answer", which is fine for a browser listing changes
  and fatal for a caller deciding what to write: read as empty it would write nothing, report success,
  and leave the workspace in the state being replaced. `TryCompare` is the same call and the same
  parser returning null on failure, and `Compare` is now one line over it. `RestoreResult.ChangedPaths`
  is nullable for the same reason and **null means "everything", never "nothing"**.
- **Rename detection is off for this caller, on purpose.** To a reader "moved" is one fact; to a
  caller acting on the paths a rename is a removal AND a write, and collapsing the pair hides one of
  the two paths that has to be touched — which is rule 2 of R-rc5-12c failing silently.
- **The two policy files are excluded from the changed set on both sides.** They are put back verbatim
  a few steps later (rule 4), so a restore never changes them, and a caller told otherwise would
  reload a document for a file whose content was just preserved.

`-a` is still there as `WriteEverything`, reached only when the diff could not be produced: a restore
that is slow is a nuisance, and one that wrote half a state is the defect the whole feature exists
against. The named-path write is chunked at 500 paths per invocation because a command line has a
length bound; the gate-17c interrupt seam (`FilesPerWrite`) still overrides it and still interrupts
after the first file.

The removal pass is untouched. Which files a removal may act on is R-rc5-12c rule 2 and is not a
performance question.

Gate: `tests/Ui.Tests/Revision/RestoreInPlaceTests.cs` — the untouched file's modification time is
asserted, which is the assertion a count alone cannot make (a rewrite with identical bytes is
invisible in the content).

---

## `PrimaryViewRename` — one list of view types, for every caller that renames a cell (2026-09-09)

Added beside `PrimaryViewRepair`, and for that class's reason: giving a cell's primary view files the
cell's own name is file names and `.ccell` arithmetic over a cell folder — no dialog, no canvas, no
workspace — and any non-GUI caller that copies or renames a cell owes the same tidy-up.

It exists because two view-model operations each kept their own copy of the loop. Rename Cell was
taught `ViewType.Layout`; Duplicate Cell's copy was three hundred lines away and was not, so a
duplicated cell was born with a `.clay` named for the cell it came from. The full account is in
`src/Ui/RESOLVED.md` under "Duplicate Cell".

What it deliberately does NOT do: touch anything a view file POINTS at. A `.clay` pairs with its
`.wBond` by shared stem (`WBondCell.Resolve`, in `src/Ui`), so renaming one owes the other — that
pairing lives above this layer, and the Layout result carries the OLD file name so the caller can
honour it. A caller that reconstructed the old stem from the old CELL name would be wrong for every
cell whose primary was named something else.

`NoPrimary` covers both "no view of this type" and "several with none chosen", and neither is an
error: a cell need not have a layout, and picking between two alphabetically is a design decision
this operation has no business making — the same refusal `PrimaryViewRepair.ClearedForChoice` makes
on the way out.

---

## RP-2b — a port's return is part of what the port IS (2026-09-10)

RP-2a built the kernel's two-cut port and constructed every one of its ports by hand. This is how a
user SAYS which return a port has, and how the run says what it did. `src/Design` and `src/Ui` only;
no engine change was needed, which is the outcome the brief's own scope line asked for.

### The two fields, and why they are on the label

`LabelShape.PortReference` (`LayoutPortReference?` — `CoplanarGround`/`SecondConductor`, null = the
stackup's ground plane) and `LabelShape.PortReturn` (`LayoutPortReturn`, a DBU point). Additive, no
`.clay` `FormatVersion` bump, omitted from the file when null — the shape `PortDirection` and
`PortLayer` already have, and for the same reason: every port drawn before today reads exactly as it
did.

**Not in the `.cem`.** A port is drawn in the layout and its return is part of what it IS — a port
referenced to the strip beside it is a DIFFERENT port, not the same port analysed differently — so a
`.cem` field would split one port's identity across two files and make the artwork non-portable
between setups. The reference IMPEDANCE goes the other way for the mirror-image reason
(`EmSetup.PortZ0s`): the same artwork can legitimately be analysed at two impedances.

**A layout enum rather than the engine's `PlanarPortReference`.** `LayoutModel.cs` is the document
model and its members are what a `.clay` spells on disk; the engine's enum carries a fourth member
(`ViaBetweenLevels`) that names a refusal rather than anything anyone can draw, and putting it in a
file format would promise a spelling nothing can read back.

### The finding the brief asked for: `LabelShape` is copied field-by-field in THREE places

`LayoutGeometry.Clone` was the known one. The other two are both in
`src/Render/Renderers/LayoutRenderer.cs` — the display-only clone that applies
`EffectiveVisibleLabelHeightDbu` to a label whose stored height would render sub-pixel, once for a
ghost (`DrawGhostShape`) and once for a committed label.

**The committed one had already dropped `PortLayer`, silently**, since that field was added. It is a
display clone, so the consequence is narrow rather than a data loss — but it is exactly the class of
defect R-rp2b-2 names: the clone is handed to the port-marker code, which resolves the port's
conductor, and `PortLayer` is precisely the field that says which conductor a port COMMITTED to
rather than which one is visible. So a port whose text height falls under the visibility floor
resolved its marker against visible artwork instead of its own committed layer. Both renderer clones
now carry all four port fields.

Three copies of one record's fields is a shape that will produce a fourth. It is not consolidated
here because the renderer's clones exist to change ONE field and are in a project this brief does not
scope; recorded so the next person adding a `LabelShape` property knows there are three sites, not
one.

### Resolution: one rule, used twice

`EmPortExtraction` resolves the return point through the SAME `NearestPolygon` the positive terminal
goes through, and both terminals reach the kernel as arguments to the ONE `PlanarPort` construction
that was already there. A second resolution rule would be a second chance for the two terminals to
land on different levels with nobody noticing (RP-2's R-rp2-2, L9d's reason one level up). The level
is stated for both terminals exactly when there is more than one level to be wrong about, which is
the condition the positive terminal already used.

### The refusals are layout-side even where the kernel refuses too

Three of the four are also refused by `PlanarPorts.TryResolveTwoCut`. They are caught here as well
because **the user reads this message and not the kernel's**, and the remedy is different at each
end: the kernel's for an edge port is "cut this port as an internal delta gap", and the layout's names
where that choice actually lives — the EM setup's own port list, spelled "Internal delta gap" exactly
as the panel spells it.

- A conductor-referenced EDGE port, and a conductor-referenced internal to-ground port (whose
  negative terminal is the plane by construction — there is nothing for a second cut to be).
- A reference with no return point. **Never a nearest-conductor search**: on a real board the nearest
  metal to a line is very often not its return, and picking it silently is picking which loop the
  answer is about.
- A return point on no conductor.
- A return point on the SAME polygon as the port. The kernel catches this on the MESH (a
  4-connectivity walk, which also catches two polygons the mesher merged); the artwork check here
  fires before a mesh is ever built and names the layout coordinates the user can act on.

### The note: "every port" stopped being true, and both spellings said it

`PlanarExtractor`'s R-em-4 block asserted *"That plane is the negative terminal of every port in this
run and is not selectable per port"* in both its inferred and its `.cem`-overridden spelling. RP-2a
made that false. The plane is still named once with its height — it is still the medium's own
boundary condition, still the return for every port that did not say otherwise, and the 2%-scale trap
this note exists for is unchanged — and only the clause claiming exclusivity moves, **and only when a
port actually says otherwise**. A ground-only run's note is character for character what it was.

The OPENER is scoped with it. Leaving *"Every port returns through 'X'"* standing and correcting it
two sentences later would be a note whose first sentence is false, which is the same defect one clause
down; a mixed run's opens *"Every port that does not name its own return conductor returns through
…"* instead.

The differing ports are named from their own LABEL TEXT rather than by port number. Numbering is
`EmPortExtraction`'s (a label whose text names no number is auto-numbered in document order), and
re-deriving it in the extractor would be a second copy of the ordering the s-matrix is indexed by. The
CONDUCTOR each return landed on is named in that port's own note, which is the only place it is known
— the extractor sees labels, not resolved terminals.

### `explain`

`port N return` steps, off the resolved `PlanarPort` rather than off the label, for the same reason
the plane step is. Silent when every port returns through the plane, so an ordinary board's `explain`
is unchanged; the moment one port differs, every port gets a row, because the interesting question
about a mixed run is which ports are which and half an answer is worse here than none.

### The pick is a gesture, and a click off the metal is refused

`LayoutEditorViewModel.ArmPortReturnPick` — armed from the Properties inspector, owns the next click
whatever tool is active (the shape `_pastePlacementShapes` and `_rulerLabelMove` already have),
Escape puts it back, and a selection change cancels it so it cannot outlive the port it was armed
for. What is being named is a CONDUCTOR and the honest way to name one is to point at it; a
coordinate pair typed into two boxes would be the same guess with extra steps. The Port tool itself
seeds neither field: a port placed on ordinary metal returns through the plane, which is what null
means and what every port has always done.

Gate: `tests/Ui.Tests/Em/PortReturnConductorTests.cs` — 18 tests, ~1 s, the brief's six gates in its
own order.

## ANT-12 — the far field's first user-reachable switch, and a cover layer that was silently dropped

`brief-antenna-12-user-docs-and-example.md`. Two changes here, and the first is the reason the whole
antenna series was undeliverable until now.

### 1. `EmSetup.RadiationPattern` — ANT-4 through ANT-11 were unreachable

Every phase from ANT-4 onward recorded, in its own write-up, that "nothing reaches the CLI or the GUI,
because the phase before it does not either". Taken together that meant the far field, the metric
registry, the polarization results, the dB polar plot and the 3D pattern surface had **all shipped with
no way for a user to ask for any of them**: `PlanarSolveSettings.FarField` could only be set by editing
C#, and `EmRunService` passed nothing.

One `bool` closes it — model, `.cem` (nullable, omitted at its default, so every existing file
round-trips byte-identically), Clone, view model, and a group of its own in the EM Setup panel. **The
`em` verb needed no flag**: it takes everything from the `.cem`, which is why the setting went there
rather than onto a command line.

- **It is deliberately in NO provenance hash**, and the reason is stronger than the accelerator's:
  that one computes the same answer a different way, while this one does not touch the answer at all.
  An `.snp` written with the pattern on is byte-identical to one written with it off, so hashing it
  would mark every existing file stale for a change that cannot have moved a number.
- **`FrequenciesHz` is the sweep's own grid, not null.** Left at its default the far field produces ONE
  pattern, at `freqs[0]` — the bottom of the sweep, which on an antenna is the one frequency nobody
  wants. Measured on the shipped example: 22.6 % radiation efficiency at 5.3 GHz against 62.6 % at
  resonance, both correct about different questions. Each request maps to the nearest point that was
  actually SOLVED, so with adaptive sampling on this is one pattern per solved point.
- **Two disabled-with-a-reason states**, both things the user set in that same panel: the cross-section
  kernel, and CONFORMAL boundary cells — the far field does not transform a cut cell and refuses by
  name, so the panel declines to arm a run whose pattern cannot be computed. Everything else the far
  field refuses (a via's vertical current, a stack with no ground under it) stays the engine's own
  refusal and arrives as a run note; re-deriving it here would be a copy of a judgement that can drift.

### 2. A dielectric layer above the top metal is NOT in the solve, and nothing said so

ANT-12 §1a required one covered-patch run to be taken before anything was written about superstrates,
and anticipated two outcomes: it runs (the Can list gains it) or it refuses (the Cannot list does).
**It did neither.** A uniform 0.5 mm, εᵣ = 3.0 radome added to the shipped RO4350B technology produced
s-parameters **bit-identical** to the uncovered run at every one of 11 frequencies, with the same
pattern, the same peak field and the same power budget to every published digit.

The cause is one line in `BuildMediumStack`: `topOfInterest` is the topmost analysis level's own sheet,
and the medium is terminated in air exactly there. Anything the technology declares above it is
discarded. A real cover moves a patch's resonance by per cent and changes its surface-wave launch, so
"no difference at all" is the one answer that cannot be right.

**A WARNING, not a refusal**, on the same terms as the skipped-ground-plane warning beside it: a
uniform superstrate is genuinely inside what a layered medium can express, so the limit is in the
EXTRACTION rather than in the physics, and refusing would stop runs that are correct about everything
below the metal. The sentence names the layer, names the level it sits above, says the answer is the
UNCOVERED one, and says what a cover would have done. Gate:
`tests/Ui.Tests/Em/SuperstrateNotInTheSolveTests.cs` — the identity (every number the solver reads is
the same either way, which is stronger than "moved by less than a tolerance") and the warning.

## The Gerber mapping dialog never said which FILE a row was (2026-09-11)

Owner-reported, from a real import: the layer-mapping dialog showed no file extension, so there was no
way to tell which row was which. The reported workaround was renaming every file before importing —
which is exactly the information the dialog was throwing away.

**A layer is a FILE in this format**, and a fabricator's files all carry the board's own stem
(`<board>.gtl`, `<board>.ssb`, …). The dialog's row is named after its LAYER, and rung 4 of the
identification cascade — nothing identified this file — names a layer after the file's STEM. So on a
set whose layers are spelled only in the extension, every unidentified row read as the same word. The
extension was the one thing distinguishing them and nothing was looking at it. Three changes, and the
first is the one that answers the report:

1. **`LayerMappingRow.SourceDetail`, and a "From" column.** Where the source layer came from, when that
   is a different fact from its name. `GerberImport` fills it with the file name(s) that landed on the
   key — a list, because a composited read or two files donated one technology layer both put more than
   one file on one row. No other caller supplies it and the column takes **`MinWidth="0"`**, so a paste
   or a retarget, whose source layer has no origin apart from its own name, pays no width for it.
2. **A colliding name is disambiguated by the EXTENSION, not by the stem.** The old spelling appended
   the stem — which the colliding files share by construction — so two unidentified files produced the
   *identical* name `board (board)`, and a third produced it again. `names.Add` returned false and the
   name was left as it was, so the collision the code was there to break was not broken. The counter is
   only a backstop for a shared extension.
3. **`GerberLayerCascade.FunctionSideFamily` — the mirrored extension spelling.** `ExtensionFamily`
   already decomposes `g<side><function>` (`.gtl`, `.gbs`, `.gto`). The other convention in circulation
   writes the same thing backwards, `<function><side>`: `ss`/`sm`/`sp` for the silkscreen, the solder
   mask and the paste stencil, then `t` or `b`. A set written that way was wholly unidentified, which is
   how six rows all reading "board" reached the user in the first place.

   **Non-conductors only, and that line is deliberate.** The same convention spells copper with words
   that decompose into nothing, and a wrong guess there is the expensive one: only conductors enter the
   stackup and the copper order, so a mis-read copper layer puts the stack order wrong — which R-L4g-10
   says must never happen quietly. A mis-read mask or silkscreen costs a label, and is reported as a
   guess like every other rung-3 answer.

Gate: three tests in `tests/Ui.Tests/GerberImportTests.cs` — the row carries the file name, the minted
names are `board` / `board (ly2)` / `board (ly3)` rather than one name three times, and the five
mirrored extensions land on their own layers with no copper claimed.

## `LayoutSpatialIndex` — the empty-instance-side predicate, and a list that sized itself (2026-09-12)

RF1 (`docs/sonnet-briefs/brief-rasterfill-1-instance-query-on-flat-documents.md`). The renderer's half
and the measurements are in `src/Render/RESOLVED.md`; this is what the index itself gained.

**`InstanceSideIsEmptyAndClean(instances, resolutionVersion)`** — public, locked, O(1). Answers "this
document has nothing on the instance side, **and the index already agrees**", which is the condition a
per-frame consumer needs before it may skip the combined query entirely.

**Both halves are required and the one-half version is a silent bug.** `instances.Count == 0` alone is
not sufficient: a document whose last instance was just deleted has an empty live list and a *dirty*
index, and skipping there means `RefreshInstances` never runs, so the entry that placement owned is
never evicted and `_instancesDirty` stays set indefinitely. The second half is exactly the staleness
question the combined query's own locked section asks — which is why it is asked **here** rather than
re-derived at the call site, where the two could drift apart.

Note the deliberate asymmetry with `InstanceSideLooksStale` beside it: that one is racy on purpose
because the lock re-decides. This one **is** the decision, so it is the locked read. It stays correct
under a race regardless — every transition it could miss (an instance added, the resolver's generation
ticking, an explicit dirty mark) only turns a `true` stale, and a stale `true` costs one frame of lag,
which is what a shape-side self-heal already costs.

**Two test-only counters**, `ShapeQueryCount` and `CombinedQueryCount`, beside the existing
`FullRebuildCount` / `IncrementalApplyCount` / `InstanceRefreshCount`. "A frame on a flat document
issues exactly ONE spatial query" is a structural property and is gated on these, never on a clock.

### The combined query's result list is pre-sized — and the obvious ways to do it are both worse

At board fit on a 52,230-shape import the result reaches 51,378 entries. Grown from empty that is ~17
reallocations, several of them Large Object Heap copies, and it measured **1,026 KB per call** against
the 403 KB the entries actually occupy. **This is an allocation fix and not a traversal fix**: timed
best-of-30 the query is ~1.8 ms either way.

The estimate is **last call's match count, scaled by the ratio of this rect's area to that call's**.
Both cheaper-looking seeds the brief offered were implemented and measured, and both are worse:

- **A counting walk first** is exact but is a second full traversal — **+1.1 ms per call** at 51,378
  entries, more than it saves.
- **`_syncedCount + _syncedInstanceCount`** over-allocates the whole document on every zoomed-in
  query — 418 KB to hold 538 entries on this board.

**The area scale is what keeps the consumers from poisoning each other.** Without it, a render frame's
51,378 would seed the very next hit-test's 3-entry query at 51,378 and allocate 400 KB on a pointer
move — hit-test, marquee and snapping all call this same overload deliberately. With it, a hit-test
sized query after a full-extent one allocates **0.8 KB** (measured; it was 2.2 KB before this change).
Local density between two nearby rects is a far better assumption than global density, and a wrong
estimate only ever costs a `List` resize.

**Pinned in code beside the sort: do not replace the comparison delegate with a struct `IComparer`.**
The brief asked for it (R-rf1-4) and it is ~2.5x slower at every size — see `src/Render/RESOLVED.md`
for the numbers.

### Pre-existing behaviour this work confirmed rather than changed

**`Extent` does not shrink when an entry is removed**, and never has. `RemoveEntry` deliberately skips
bounds-shrinking and rebalancing, so after deleting the last instance the root's bounds still span the
placement until R-L2b-2's churn-triggered rebuild. RF1's brief assumed otherwise; a test asserting the
brief's wording fails identically with RF1 reverted.

## RP-3 — a return plane ABOVE the levels: solve the stack upside down (2026-09-12)

**Reported by the owner**, from a 4-layer board imported out of a Gerber set with its ground plane
on an inner layer and structure on the bottom conductor. The EM Setup note said the signal level was
below every ground-designated conductor, that none of them could be its return path, and that the
plane had been taken from `Stackup.Bottom = Ground` instead — "further away than the technology's
own plane, and will read as a higher impedance". The question was the right one to ask: *why can a
port on the bottom conductor not simply return through the plane above it?*

### The note was about the medium, not the port

The rule "a port returns through a plane BENEATH the conductor it feeds" is not a statement about
ports. It is where the boundary is in this formulation: the layered Green's function terminates on
**one** laterally infinite PEC, and every meshed level lives above it. A plane above the lowest
level cannot be that boundary — and it is worse than unused, because `BuildMediumStack` absorbs a
conductor that is neither a level nor the boundary into a neighbouring dielectric. The plane in the
report was not modelled as a poor reference; **it was modelled as 18 µm of FR-4.**

Nothing about the structure is unusual, though, and nothing about the physics forbids it.
**Reflecting a structure in a horizontal plane is an exact symmetry of an isotropic medium** — the
artwork's x and y are untouched, every material and every thickness is what it was, and the
s-parameters of the mirrored structure are the s-parameters of the original. The answer the kernel
wants was one z-arithmetic flip away the whole time.

The alternative the user was left with — hand-authoring a second `.ctech` whose stackup is typed in
backwards — is a copy of the process data that nothing keeps in step with the original.

### What is flipped, and the two things that are not

`PlanarExtractor` now builds the mirrored band set in place, after the level set is decided and
before the return plane is resolved. Two deliberate departures from a pure geometric reflection:

* **`Band.Index` is carried through untouched.** It is the stackup position, and *every* downstream
  match is made on it — the classified conductor shapes, the ground pour's own band, the shapes a
  level's polygons are gathered from. Renumbering would silently re-point all of them. (The
  patterned-dielectric rebuild a few lines above already relies on the same property and says so.)
  `Stackup.Layers` itself is never rewritten; the technology object the caller handed in is not
  touched, which matters because `TechnologyCache` hands back a shared instance.
* **The analysis sheet stays on the same NAMED surface of its own band** — bottom stays bottom —
  which *in the flipped frame* is the surface facing the plane, so the modelled height comes out as
  the substrate thickness exactly as `ConductorSheetSurface`'s own documentation says it should. A
  pure reflection would put the sheet on the far side of the metal and quietly add a conductor
  thickness to every height (35 µm on 900 µm — 3.8%, and invisible).

`Stackup.Bottom` is read through a local that becomes `Stackup.Top` when the frame is flipped, so
the fallback branch and `InferredWouldHaveBeen` both describe the boundary actually being solved.

### When it fires — and the one case no orientation can express

Only when the flipped frame resolves a **usable** plane (a positive slab) and the stackup's own
orientation does not, or resolves only the `Stackup.Bottom` boundary where the flip finds a
conductor the technology actually designates. `ResolvesAUsablePlane` restates the resolution order
narrowly for this one decision; it answers only *is there a plane*, never which one, so a
disagreement with the block below can cost a flip that was available but can never produce a plane
the run did not resolve. An ordinary microstrip reaches none of it and is bit-identical.

**A plane BETWEEN the levels is not helped by a flip, and that is not a limitation of the kernel:**
through an unbroken plane the metal above and the metal below are two decoupled structures, not one
problem. This is the shape the report actually had — a Gerber import brings in the artwork of every
copper layer, so both outer conductors become levels with the plane sandwiched between them, and
neither the ports nor the structure asked for that. The surviving note now diagnoses it and points
at the **level set** rather than at a stackup that is already correct; "designate a conductor below
this level as a ground reference" was sending users to edit process data that was right.

### `SheetAt` and `PresentWithLayer` stand the flip down

MIM-6 names a surface of a band and MIM-7 ties a film to the plate *above* it. Both are written in
the stackup's own orientation, and reflecting them is a modelling decision rather than an arithmetic
one — `PatternedDielectric.Beneath` would look the wrong way, for one. The flip declines when either
appears and **says so in a warning**, because a run that silently declined to fix itself is
indistinguishable from one that never could.

### The gate

`tests/Ui.Tests/Em/FlippedStackReturnPlaneTests.cs`. The fixture's two dielectrics differ in both
thickness and permittivity on purpose: **on a symmetric board every mirror-arithmetic mistake still
lands on the right number**, which is exactly the sort of fixture that cannot fail. (The reporter's
own board is symmetric — 0.9 mm either side of the plane — and both orientations read 953/1853 µm.)

The test that carries the promise is `TheFlippedRun_AndAHandMirroredTechnology_AreTheSameMedium`:
the same artwork through the flip and through a technology whose stackup is reversed and whose
boundary conditions are swapped must give the same slab, the same level z's, the same medium regions
and interfaces, the same vias. Anything less and the feature is an approximation of the workaround
it replaces.

### Three existing tests changed, all of them pinning behaviour this replaces

* `ReturnPlaneOverrideTests.AConductorAtOrAboveTheLowestLevel_IsRefusedWithBothHeights` →
  `AConductorBetweenTheLevels_…`. **RP-1's field must not be strictly weaker than the rule it
  overrides**: a plane above every level is the case the flip now solves, and refusing the explicit
  spelling of what the automatic path does silently is indefensible. R-rp1-3 survives for the
  mid-stack case, which is what the fixture now builds.
* `FourLayerGroundReferenceTests.ASignalBelowEveryDesignatedGround_…` — re-pointed to metal on
  **both** sides of the plane, the only place that note is still reachable.
* `FourLayerGroundReferenceTests.TheBottomConductorAsASignal_…` — every designation comes off, since
  with a plane anywhere above it that refusal is no longer right.

### Also worth knowing, from the same board

Two things the reporter's file hit that are not this bug. Its `Drill` stackup entry spans Top Copper
→ Bottom Copper, so with the levels restricted to one conductor **all 327 through-holes are ignored**
("spans a conductor that is neither an analysis level nor the ground plane"); re-spanning the entry
to the plane turns them into backside/attachment vias, at the cost of doing it to every hole on that
drill layer. And a Gerber set carries no stackup at all, so its dielectric thicknesses are the
import's defaults — 0.9 mm either side of the plane, i.e. a 1.8 mm board — which is nowhere near a
real 1.6 mm 4-layer and moves the impedance far more than anything above.

## Clip and Cut Out silently exempted every via and label on the board (2026-09-12)

Reported against a Gerber-imported board simplified with **Clip**: most artwork clipped correctly, but
"some of the Drill (thruhole) Via primitives are not getting clipped out." The reporter's `.clay` is the
whole story — 612 shapes, of which **189 are `Via` on the drill layer**, and after a Clip to a
`Rect` stencil spanning X 36,569,108..51,838,224 DBU every Poly on the board had been trimmed to
exactly that span while **all 189 vias were still there, 125 of them entirely outside it**.

**Cause: one operand test was answering two different questions.** `LayoutBooleans.IsClipperOperand`
asks *can this shape be flattened into a region* — the right question for Union/Intersect/Difference/
Xor/Offset, where combining a drill hole with a polygon means nothing, and the reason R-clip-8 made it
a positive test in the first place (a `ViaShape` or `LabelShape` reaching `LayoutFlattener.Flatten`
threw `ArgumentOutOfRangeException` out of a context-menu click). Clip and Cut Out ask a *different*
question — *is this shape inside the region I am keeping* — and a shape anchored at a point has an
exact answer to it. Sharing the one filter answered it as "always keep", so a via was never an
operand, never removed, and **never counted**: the Messages line reported the clip over 422 operands
with nothing anywhere saying 189 shapes had been skipped.

**Fix: `LayoutBooleans.IsClipOperand`** — the clipper operands *plus* the point-anchored kinds
(`AnchorOf`: a via's `X,Y`, a label's `X,Y`). `ClipCore` decides those **all-or-nothing on the
anchor** before it touches the region path: kept as the SAME OBJECT or removed, so `OperandsChanged`
can never count one and a via is never split, never rebuilt and never polygonized. The other five
operations keep `IsClipperOperand` unchanged — the two tests now differ on purpose and each says so.

Three things that decided the details:

* **The containment test is the same fill rule as the clip itself.** A 2-DBU probe square intersected
  with the stencil's own Clipper paths, not a second point-in-polygon predicate that could disagree
  with `LayoutClipper.Rule` about a stencil with holes — which is a real case here, a clip to a copper
  pour keeps nothing in the pour's own voids. A `Rect` stencil short-circuits to its bbox, and so does
  an anchor outside the stencil's bbox, so the common case builds no paths at all.
* **A `BitmapShape` stays out.** It has extent and no anchor; clipping one means cropping the image,
  which R-bmp-3 keeps out of every boolean. It is the one kind that still survives a clip silently.
* **A via straddling the stencil edge is kept whole.** Its pad hanging over the boundary decides
  nothing — the via is *at* a point, and the copper the pad sits on was already trimmed by the same
  clip.

**Trap, for anyone writing a holed-stencil fixture:** `LayoutFlattener.WithHoles` passes hole rings
through in the order they are stored and `RingToPath64` does not orient them, so under `NonZero` a
hole wound the SAME way as its outer ring is not a hole at all — it just adds winding, and the test
reads as "the hole was ignored". Real holes arrive correctly wound (from `FromClipperTree`, or through
`EnsureValidHoles` on load); a hand-written one in a test must be wound opposite the outer ring.

## `LayoutClipper` — the two terms a bounding box cannot reach (owner, 2026-09-12)

`HolesAreValid` gained box prefilters on 2026-09-04 and they work: on an ordinary pour essentially
every hole-vs-hole pair dies on its box. **Two terms are structurally beyond a box**, and on a real
Gerber import they were the whole cost:

- the **point-in-ring** cast, because a ray has to see every segment it could cross — the file's own
  comment already said no box helps here;
- a hole tested against the **outer ring that contains it**, whose box therefore rejects nothing.

Profiled on the pour that motivated this — 28 holes carrying **59,996 vertices** against a
**271-vertex** outer ring, one shape of 1,674 in a 10.6 MB board:

| term | ms |
|---|---|
| hole vs. hole | 221 |
| point in outer ring (every hole vertex) | 108 |
| hole vs. outer ring | 42 |
| **that one shape** | **379** |
| the board's 445 holed shapes | 453 |
| reading the whole 10.6 MB file | 575 |

**Note which way round that pour is.** The prefilter's own reasoning — "a hole lies inside the outer
ring's box, so reject the OUTER's thousands against the hole's small box" — assumes the outer ring is
the long one. Here each hole has ~2,142 vertices against the outer's 271, so the length-based choice
inverts and scans the hole against the outer's box, which contains it and rejects nothing.

**The fix is `RingBands`: segments bucketed by y, CSR, built lazily per ring.** Both terms reduce to
the same fact — *two things that meet share a point, so their y-extents overlap* — which is a band
lookup. The point cast visits the band containing `py`; a segment test visits the bands its own
y-extent covers, deduped by stamp so a tall segment is handed over once.

| | before | after |
|---|---|---|
| the 445 holed shapes (Release) | 453 ms | **24 ms** |
| reading the 10.6 MB board (Release) | 575 ms | **68 ms** |
| reading it in **Debug**, which is what the owner runs | 4,791 ms | **112 ms** |

**It is a candidate filter, never a decision** — every segment a band returns still goes through the
unchanged arithmetic, and `PointInOrOnRing`'s two walks share one `Cast` helper so the indexed and
unindexed paths cannot drift into two arithmetics. The `(i, j)` pairing is preserved exactly, not
merely equivalently: the crossing test divides, and swapping a segment's endpoints is algebraically
identical but not bit-identical.

**The differential gate is the only thing that makes any of this safe, and the existing one could not
see it.** `LayoutClipperHoleValidityTests` compares against a verbatim copy of the unfiltered
algorithm, but every ring in its corpus is *below the size at which an index is built* — it passed
without the new code ever running. So `RingIndexMinimumSegments` is now internal and the three new
cases read it rather than hard-coding a number that could drift and quietly make them vacuous:

- a 400-trial randomized corpus of 64-400-vertex outer rings with 50-260-vertex holes, adversarial
  and disjoint in equal measure, asserting both answers stay represented;
- holes with far MORE vertices than the ring containing them — the shape above, the one the boxes
  are blind to, in both the valid and the escaped-through-the-boundary arrangement;
- an 80-tooth comb, where every segment spans every band, which is the case that makes a y-index
  degenerate.

Corollaries recorded elsewhere: `src/Ui/RESOLVED.md` for the two symptoms that led here (a workspace
open that read every layout, and a `.cem` that reads its board on the UI thread).

---

## `Stackup` now carries two VIEW-state fields, and that is deliberate (2026-09-13)

`Stackup.DrawingPaneExpanded` and `CardPaneExpanded` say whether the Technology Editor's two Stackup
panes were left open. They are view state in a document format, which is a thing to be suspicious of
— and they are here for the reason `StackupLayer.DrawLaneFraction` already is: **how a particular
stackup is best looked at is a property of that stackup**, not of the installation, and a user who
gave the window to the drawing expects it given again when they reopen THAT technology.

Both are `bool?`, and **null means expanded**, so every `.ctech` written before them means what it
always meant. Additive, nullable, no `FormatVersion` bump — the `BoardThicknessDbu` / `SheetAt` /
`Fill` pattern. `DefaultIgnoreCondition = WhenWritingNull` keeps them out of a file that never set
them.

**Nothing downstream of the editor may read them**, exactly as for `DrawLaneFraction`, and that is
gated rather than asserted in a comment:
`StackupDragTests.ATechnologyDifferingOnlyInThePaneFlags_ExtractsIdentically`.

---

## A port's TYPE is addressed by its NUMBER, not by its place in a list (2026-09-14)

Owner question, in three scenarios: with geometry snapping on, can dragging P1 around a trace turn it
into an internal port? And if P1 is typed internal and then DELETED, does P2 become internal — drawn
as one, or actually one?

**The drag half was already safe.** A port the `.cem` has not typed as internal is pulled onto the
conductor's nearest boundary by the edge rule in `LayoutEditorViewModel.RecomputeMoveDelta`, so the
"deep in the metal" inference (`LayoutPortDirection.IsInterior`) never fires. Swept over ~1,700 drop
points on a straight trace and on a two-rect L bend, with and without a setup claiming the layout —
nowhere to drop it. The documented way to get one by dragging is still the only way: turn geometry
snapping off first.

**The delete half was not safe, and it was not merely a drawing problem.** `EmSetup.PortKinds` and
`PortZ0s` are per-port lists, and `ResolvePortKind` / `ResolvePortZ0` were read by the port's
**position in the number-sorted list**. So:

- type P1 internal, delete P1 → P2 slides into slot 0 and **is** an internal port. Not drawn as one:
  `EmPortExtraction` returns `Kind = Internal`, `PlanarGroundPath` grows a via to the plane underneath
  it, and the run returns a complete, plausible s-matrix for a structure nobody drew;
- type P2 internal, delete P1 → the type **walks onto P3**;
- delete P1 and draw a new port → it auto-numbers to the lowest free number (1), inserts at slot 0,
  and slides everything again.

It persisted, too: the shifted state is what gets written back to the `.cem`.

**The fix is that the slot is `portNumber - 1`, never a position.** A port number is the one stable
identity a port has — it is what the user typed, it indexes the s-parameter matrix, and it survives
its neighbours being deleted. A position survives nothing. `EmPortExtraction.Extract` now invokes
`kindFor`/`z0For` with `number - 1`, and the three places in the panel that addressed by row position
(`CommitPortKind`, `CommitPortRow`, and the `InternalPortMarkAnchors` the layout draws from) address
by the row's `PortNumber`.

**No format change, and every existing `.cem` means exactly what it meant.** For the contiguous 1..N
numbering every layout this tool creates has, the position IS the number minus one. A gap in the
numbering now costs a slot of padding in the file and nothing else — ports 1 and 5 store five
entries, four of them the default.

Gate: `tests/Ui.Tests/Em/PortTypeIdentityTests.cs` — the three delete scenarios at the extraction
level (the same two callbacks `EmRunService` itself passes), the panel writing to the right slot on a
layout numbered 2 and 3, the render marks addressed the same way, a round-trip proving contiguous
setups are unchanged, and the two drag sweeps with their snapping-off non-vacuity guard. Six of the
thirteen fail against the positional behaviour; the drag sweeps pass either way, which is the point.

## Git discovery had the same launcher-PATH gap (2026-09-14)

Found while fixing the Verilog-A compiler's discovery, which had it too and had no saving grace —
`src/Core/RESOLVED.md` carries the measurement: a Finder-launched `.app` gets
`PATH=/usr/bin:/bin:/usr/sbin:/sbin` from launchd and nothing of the login shell.

`GitDiscovery.Locate` searched `PATH` and stopped. On macOS that is harmless in practice, because
`/usr/bin/git` is present whether or not anyone installed git — which is exactly why the gap went
unnoticed. **On Linux it is not harmless**: a user-local install puts git in `~/.local/bin`, an
application started from a desktop entry never sees it, and revision control reports no git on a
machine where typing `git` works.

`WellKnownInstallPaths` now runs after the `PATH` loop over the same `CandidateCommands`, through a
settable `SearchDirectories` (`~/.local/bin`, `/opt/homebrew/bin`, `/usr/local/bin`; empty on
Windows, where a GUI process does inherit the user's own `PATH`). `HowFound` says `found at <path>`,
because `found on PATH` would be false there.

**It sits BELOW the macOS shim gate, deliberately.** With the developer tools absent the search
still stops dead before starting anything, so R-rc3-2a and gate 20 are untouched — even though none
of these directories could hold the shim. That leaves a real and deliberate gap: a Mac with no
Command Line Tools but a working git in `/opt/homebrew/bin` still reports none. Closing it means
changing what gate 20 asserts (no process is started at all), which is a decision about that
invariant rather than a side effect of this tier.

Gates: `GitSubstrateTests.AGitInstalledWhereALauncherCannotSeeItIsStillFound` — whose stub is named
something `PATH` cannot resolve, so it exercises the directory tier even on a machine with a real
git on `PATH` — and `NoCandidateNameMeansNoUnpromptedGitSearchAtAll`.


## `HierarchyResolver` crossed the firewall, and `DiskCellResolver` is why (2026-09-15)

`HierarchyResolver` was 55 lines in `src/Ui/Schematic/`, and its own doc comment already said
"framework-free helper … extracted so tests can call it without constructing WorkspaceViewModel". It
is now `src/Design/Schematic/HierarchyResolver.cs`, `public` instead of `internal`, with two
`using` lines added and nothing else changed.

The reason is `DiskCellResolver`, beside it: the headless half of hierarchy, and the fix for a
defect where every CLI run verb dropped cell instances in silence (the full story is in
`src/Cli/RESOLVED.md`). `src/Cli` cannot reference `src/Ui`, so the descent had to come down with
it.

**The move cost nothing at the call sites** — `src/Ui/GlobalUsings.cs` already carries
`global using CircuitRF.Design.Schematic;`, which is what `NetExtractor` and `SchematicPersistence`
arrive through, so every existing user of `HierarchyResolver` in `src/Ui` and `tests/Ui.Tests`
compiled untouched. Only the accessibility had to change: `internal` in `CircuitRF.Design` is not
visible to `CircuitRF.Ui`.

**`DiskCellResolver` is deliberately stateless.** A cell instantiated twenty times is read from disk
twenty times; `NetExtractor`'s own `CellScope` is what stops the elaborated library gaining twenty
copies of it. A cache here would need invalidating by something, and a one-shot process has nothing
to invalidate it with.
## A via that spans a level was dropped, and the MMIC level list had no legal setting (2026-09-15)

A design built from the shipped MMIC PCells — a `KIT_SPIRAL` in series with a `KIT_MIMCAP` — solved
as a flat open at every frequency, before and after EM-SEV and MIM-8 landed. Both briefs worked as
written; neither could change the number, and the reason is worth recording because it is not a bug
in either of them.

**There is no setting of `AnalysisLevelNames` that solves this structure on
`mmic-GaAs_2LM_100um`.** The technology has three signal conductors (Metal1 at z=103, MIM Metal at
103.2, Metal2 at 106) and two posts that matter: `Metal1-Metal2 Post` (drawing layer 3) and `MIM Via`
(layer 10, MIM Metal → Metal2).

| levels | capacitor | Metal1↔Metal2 posts | result |
|---|---|---|---|
| `Metal1, Metal2` | plate excluded — gone | ok | open, \|S21\| ≈ 0.003 |
| `Metal1, MIM Metal, Metal2` | ok | **non-adjacent — all dropped** | open, \|S21\| ≈ 0.0035 |

Adding the plate level is what puts a level BETWEEN Metal1 and Metal2, and `BuildVias` drops a via
whose two levels are not adjacent in the analysis. The spiral's underpass is two such posts, and the
shipped MIM capacitor draws one of its own — `kit.py`'s cap bridges up to Metal2 and back down to
Metal1 so both of its terminals abut on one layer. So the capacitor cannot be modelled without
deleting the inductor, and vice versa. Measured, not reasoned: 24 via cells on the two-level run,
4 on the three-level one.

`MimCapacitorTests.AnAirbridgePost_IsNotAdjacentOnceMimMetalIsAnAnalysisLevel` already stated the
limitation and called the two-level list an escape hatch. It is not one when the excluded level
carries artwork — that is the case EM-SEV's own warning exists to name.

**What changed here:** `notAdjacent` is now `EmFinding.Warn`, the fourth member of the group
R-emsev-2 enumerated as three. It makes the identical claim to `wrongGround`, `noSpan` and
`unknownLevels` — a connection the designer drew is not in the answer — and on this technology it is
the one that fires on every real MMIC. Its sentence now says so, and says that dropping the
intervening level is not always available.

**And then the drop itself was fixed.** A post whose intervening level carries no metal at its own
footprint is an uninterrupted barrel, so `BuildVias` now builds it as a CHAIN: one `PlanarVia` per
gap, with the barrel's own cross-section appended to `polysByLevel` for each level it passes. That
invents no metal — it states the cross-section the drawn via already has at a height the analysis
happens to sample; where the intervening level carries its own metal there, the post genuinely
shorts to it, which is what the artwork says. `conductorLayers` is built AFTER `BuildVias` for this
reason. Strictly additive: `upper == lower + 1` takes the identical single-via path, so every run
that resolved before is bit-identical, and the warning above remains for a span that cannot chain.
The `notAdjacent`-is-a-warning change stands for that residual case.

**The dropped posts were one of THREE independent breaks, and the investigation got the other two
wrong before it got them right.** Recorded because the method is the lesson, not the file.

1. An **80 nm gap** between the capacitor's output lead (x = -185.080) and the abutting MLIN
   (x = -185.000). First called harmless: the gap was closed, the structure re-solved, and the answer
   did not move. That test proved nothing — the via posts were still being dropped, so BOTH sides of
   the comparison were already open. Retested once the chain landed, it is worth **40 dB**:
   |S21| goes from -42 dB to -1.7 dB. A conclusion drawn while a known larger fault is in the circuit
   is not a measurement.
2. A **port on the wrong plate.** Moving P2 onto a MIM Metal extension of the top plate extracts
   cleanly and resolves onto level 1 — and shorts the capacitor out, because the shipped `KIT_MIMCAP`
   already exits on the top plate (up to Metal2, back down to Metal1). P2 and P1 then share a node
   and the bottom plate floats. The tell is in the data: the extracted series reactance is +78 to
   +82 ohm across 2.3-2.7 GHz and **never crosses zero**, where a series L-C must. "The port is on
   metal on exactly one level" was checked; what it was CONNECTED to was not.

Also live and out of scope: a testbench sweeping 1-10 GHz against a 0.5-5 GHz `.s2p` with
`ExtrapMode=NearestEdge` holds the top half of every curve flat at the 5 GHz value, and `check` on
that netlist reports 0 errors, 0 warnings, 0 notes. EM-SEV named it; it is still silent.

## MIM-11 — the nitride is a mask, and a film that is carried says what it costs (2026-09-16)

Two things the brief deliberately kept apart, because one is achievable and one is not.

**Achievable: the MASK was missing.** On this process the MIM insulator is streamed out as its own
layer and becomes its own mask — the plate is deposited on whatever that mask left. `KIT_MIMCAP` drew
no nitride at all, so a `.gds` written from the kit was not manufacturable and a DRC deck pointed at
it had nothing to check. It draws one now, enclosing the top plate.

**Not achievable, and not a deferral: the FILM cannot be laterally patterned.** The layered Green's
function is built on laterally infinite strata. A drawn nitride polygon does not change that, and a
drawing layer that looked like it did would be a worse lie than no drawing layer at all. `MIM-11`
explicitly did not try; it measured the error instead and made the run state it.

### `PresentWithLayer` resolves in two namespaces

A CONDUCTOR stackup entry first — exactly as MIM-7 wrote it, and the precedence is load-bearing: it
is what makes every technology authored against MIM-7 resolve the same name to the same rule and
extract bit for bit as it did, whatever a drawing layer happens to be called. Otherwise a DRAWING
LAYER, in which case "in this run" means *this run's layout carries artwork on that layer*. A name in
neither namespace keeps MIM-7's typo path: the film stays ACTIVE and says so, because deactivating on
a typo would silently thin the medium.

The shipped `mmic-GaAs_2LM_100um` ties `MIM Dielectric` to `Nitride`. `MIM Metal` was always a
proxy — it answered the same way on every layout anyone had drawn — and the proxy could not express a
mask-only structure at all.

**One consequence is deliberate and is a real behaviour change.** A layout that draws the mask
carries the film whether or not the plate LEVEL is in the run, because excluding a level from an
analysis does not etch a wafer. That costs one of the three warnings
`LeavingThePlateLevelOut_…` used to assert; the capacitor's absence from such a run is still reported
twice, by the extractor's own dropped-artwork and dropped-via warnings, and both name the level to
put back. The test now states the trade rather than losing it.

`CrossSectionExtractor` had to learn the second namespace too, and the reason is the failure mode
rather than the feature: had it not, the re-pointed shipped tie would have resolved to nothing there,
the film would have stayed active on the TYPO path, and every closed-form microstrip on the metal
below it would have moved — silently, and on that one code path only.

### What the infinite film actually costs, measured

On a Metal1 line with a DETACHED Metal2 island placed well clear of it, so Metal2 is an analysis
level, the medium reaches past the film rather than truncating at it, and no via crosses the film's
interface. `Epsr` 6.8 against 1.0 is the only thing that moves:

| quantity | change |
|---|---|
| max \|ΔS\| | 2.26e-3 |
| phase of S₁₁ | **+0.108°** |
| a Metal1 gap capacitance | **−2.68 %** |

**Second order. It is worth fixing for correctness and for the mask, and it is worth SAYING, but it
is not why a capacitor would read wrong.**

**A trap found while measuring it.** The medium is built from the ground plane up to the TOPMOST
analysis level and terminated in an air half-space there, so on a Metal1-only run the film above
Metal1 is discarded entirely — two runs differing only in `Epsr` = 6.8 against 1.0 came back
**bit-identical**, and the engine's own cover-layer warning said so correctly. Any measurement of a
superstrate has to put a level above it first. The extractor's new carried-film note is guarded on
exactly that test (`filmBand.BottomM < levels[^1].SheetM`), because "this run CARRIES the film"
printed beside "that film is NOT in this solve" is two findings contradicting each other about one
band.

### A second effect the same size that nobody had named

`MIM Metal` entering the run also makes `PatternedDielectric` stop reverting `Metal1`'s `SheetAt`, so
Metal1's analysis sheet moves from the BOTTOM of its band to the TOP — **z = 100 µm becomes
z = 103 µm**. Measured on the same line: Z_c 86.749 → 87.418 Ω (+0.77 %), gap capacitance +1.51 %.
**Putting a capacitor anywhere in a layout therefore changes the modelled height of every Metal1
conductor in it.** That is MIM-6 working exactly as designed and it is the right trade — it is the
half that makes a plate gap read as the film alone — but it was invisible, and it is the same order
as the film itself. It is a note of its own now, and it is deliberately NOT guarded on the film
reaching the medium: the sheet moved either way, so on a run where the film was discarded that
sentence is the only thing that explains the height.

### The coverage fraction, and why a bounding box

The carried-film note carries the fraction of the layout's extent the artwork that DEFINES the film
covers — the mask's own polygons for a mask tie, the plate level's for a conductor tie, so the number
is always about the layer the tie was written in. "This is approximate" is not usable; "the film is
modelled everywhere and the artwork that defines it covers 4 % of the layout" says both that the
approximation is crude and that what it is crude about is small.

The denominator is a BOUNDING BOX of every piece of artwork the run read, not a union of copper. The
question is *how much of the thing you are looking at has this film on it* — the extent of the
drawing. A union would put a sparse spiral's coverage near 100 % and say nothing at all. The mask's
own polygons ARE unioned first, because two overlapping nitride rectangles are one opening and
summing them would report more film than exists.

Real output on the 60 µm shipped capacitor: `'Nitride' artwork covers 62.5 % of this layout's
extent` — plainly right on a layout that is almost entirely capacitor, and plainly small on a coil.

### Two prose rules this created

- **The carried note may not open the way the DEACTIVATION note opens.** Both say "patterned thin
  film"; the deactivation says `'X' is a patterned thin film …` and the carried one says `This run
  CARRIES the patterned thin film 'X' …`. Two findings that say opposite things about one band must
  not share an opening clause — a reader skimming, and every gate matching on the phrase, would take
  one for the other. Four existing assertions were narrowed to `"is a patterned thin film"` for this.
- **A mask tie takes neither half of the EM-SEV split**, and that is not an omission. Both halves of
  the conductor sentence end by naming an analysis level to add, which cannot answer a question about
  whether a mask is DRAWN, and the "plate drawn but excluded" state cannot arise for a mask tie —
  with no mask artwork there is no capacitor in the layout at all.

**The enclosure the kit draws is `_min_feature_dbu`, the same number the plate enclosure takes**,
because `tech` carries layers and a stackup and no DRC rules — a nitride-to-plate enclosure rule is
not something a generator can be handed. That makes the mask come out exactly coterminous with the
bottom plate, which is a real and ordinary way to draw one; a process stating two separate enclosures
would draw two different rectangles there, and the kit says so rather than burying a second constant.

**Deliberately dropped from the brief's proposed note text: the `§MIM-11` / `§MIM-12` / `§MIM-10`
citations.** Run findings are surfaced VERBATIM to users (R-em-16) and no other one cites a brief id;
a reader has nothing to open. The substance — that the error is second order, that it is not why a
capacitor reads wrong, and that the way to model the interconnect without it is to run the two
separately — is all in the sentence.

Gates: `tests/Ui.Tests/Em/PatternedDielectricTests.cs` (two new tests — the drawing-layer namespace
A/B'd on the mask alone with identical level lists, and the two new notes),
`MimCapacitorTests`, `ShippedTechnologiesTests`, `SheetReferenceSurfaceTests` and
`PdkPCellExampleTests` (the mask is drawn and encloses the plate). Every MIM fixture in the suite now
draws nitride, because that is what a layout off `KIT_MIMCAP` contains.

## A two-layer board imported with one conductor, no dielectric, and five layers asked about by hand (2026-09-17)

A designer reported three things about one Gerber set and they turned out to be three separate
defects plus one consequence. Their own workaround — renaming every file before importing — was the
right move for the wrong reason, which is worth stating first because it is what the next person will
also conclude.

**The extension was never the problem.** Every file in the set carried the same artwork extension,
and classification here is BY CONTENT and never branches on an extension (`GerberFileClassifier`'s
own header). All nine artwork files classified correctly with no renaming at all. What the renaming
actually bought was LAYER IDENTITY, which is a different rung and does read the name.

### 1. `ContainsWord`'s four-character floor made the rung-3 table asymmetric

`Signature` lower-cased a name and split it on non-alphanumerics only, so a run-together name such as
`SoldermaskTop` was ONE word. `ContainsWord` then matched a pattern word inside a word only when the
word was four characters or longer. "bottom", "back", "mask", "silk", "paste", "copper" and "layer"
all clear that floor. "top" and "bot" are three characters and could only match as whole words.

So every row naming the bottom matched and every row naming the top did not. On the reported set:
`SoldermaskBottom` identified, while `EtchLayer1Top`, `SoldermaskTop`, `SilkscreenTop` and
`SolderPasteTop` all fell to rung 4 and the mapping dialog. The table was written twice over
specifically to guard against this asymmetry — `TopWords`/`BotWords` exist because "bot" had once
been missing from every row — and it came back through the floor instead of through the word lists.

**The fix is to split the name where its case or its digits change**, so `EtchLayer1Top` reads as
"etch layer 1 top". **Lowering the floor was considered and is wrong**: "solderstopmask" is a real
spelling and it ends the word "stop" with the letters "top", so any rule letting a three-letter side
word match inside a word reads a side off a file that states none. Splitting first makes "stop" its
own word, which is both correct and what the floor was always trying to express.

### 2. That is why the board had no dielectric

`GerberStackupMapping.Skeleton` emits a dielectric only BETWEEN two conductors. With the top copper
unidentified the set had ONE conductor, so the loop never ran and the technology came out with no
substrate. The reported symptom was the missing dielectric; nothing about the dielectric code was
wrong. Worth remembering as a shape: **a stackup complaint on a Gerber import is usually an identity
complaint one step upstream.**

### 3. The bottom-most copper layer of a top-numbered set was called an inner layer

Separately, the rung-3 table reads `Layer<n>` as inner copper at `n - 1`, which is right for a mid
layer and wrong for the last one — on a two-layer board "layer 2" IS the bottom. The stack ORDER was
right, so an EM run would have been geometrically correct, but the board had no bottom copper by
name, and on most two-layer boards that layer is the ground plane and the one someone has to find in
order to set the ground reference and the bottom boundary condition.

`GerberImport.NameBottomConductor` renames it after the order is resolved. **Only the bottom, and
only under four conditions** (top already identified by something other than this guess, at least two
conductors, nothing else claiming the bottom, and the layer itself a rung-3 guess). The symmetric
"the first one must be the top" rule is deliberately absent: a set spelling its outer layers with no
side word fails the `MinIndex` floor on layer 1, so its first FILE is not among the conductors at all
and the first conductor really is an inner layer — renaming it would be confidently wrong in exactly
the case the rule would exist for.

### 4. The import refused at the first drill file, which had stated its format

`ScanFormatComment` recognised one spelling, `;FILE_FORMAT=2:4`. The set's drill files stated
`; Format  : 3.3 / Absolute / MM / Leading` — key separated by a colon, digit counts separated by a
dot, trailing prose. The refusal itself is correct policy (an unstated Excellon format is a refusal,
never a guess, because leading versus trailing suppression differ by four orders of magnitude), and
the inference it offered happened to be right — but the file was not silent and the whole import
stopped. All three spellings are now read.

**Only the digit counts are taken from that comment, deliberately.** The trailing prose also names
units and a zero convention, and both are already settled by the `METRIC`/`INCH` line and its
`LZ`/`TZ` word — statements in the format's own grammar, where the prose is a comment whose
vocabulary nobody agreed on. The word there is ambiguous in the expensive direction: "Leading" reads
as "leading zeros kept" against Excellon's `LZ` and as "leading zeros suppressed" against Gerber's
`%FSL`. A second, weaker source that can contradict the first is not worth a refusal it cannot
resolve.

### Also, while in the table

There was no rung-3 row for a V-cut, though `KindNames` had carried its `FileFunction` spelling all
along, so a score-line file went to the dialog. Two rows added, on the same name it gets when the set
declares it.

**Result on the reported set: 9 of 9 layers identified and 0 asked about by hand, against 4 and 5;
two conductors and one dielectric; and no drill override needed.** Gate:
`tests/Ui.Tests/GerberRunTogetherNamesTests.cs`. Its fixtures name no tool, vendor or product — only
the SHAPE of the names, which is the whole content of the first defect.

## railRF brief 1 — the `.crail` document, and the five things the registration turned up (2026-09-18)

`src/Design/RailRf/` now holds the document every other brief in the series reads and writes:
`RailDocument`, `RailSpec`, `RailPortAnchor`, `RailSource`, `RailLoad`, `RailTarget`,
`RailAggressor`, `RailOrder` and `RailDocumentIo`. Framework-free, on `EmSetupPersistence`'s terms
exactly — `System.Text.Json`, `WriteIndented`, enum-as-string, `WhenWritingNull`, a `FormatVersion`
integer that is refused when it is newer, `AtomicFile` on the way out and `GzipTextFile`'s sniff on
the way in. Gate: `tests/Ui.Tests/RailRf/RailDocumentTests.cs`, 10 tests, 66 ms.

Five things came out of doing it that are not in the brief.

### 1. `RailTarget`'s record equality was reference equality, and brief 16 is what that would break

A `record`'s generated `Equals` compares each member with that member's own `Equals` — and
`IReadOnlyList<RailMaskPoint>`'s own is REFERENCE equality. So two targets read from the same bytes
compared UNEQUAL, silently, which would have made the round-trip test prove nothing while passing on
every document that happened to carry no mask. It is worse downstream than in a test: **brief 16
compares two documents for a living**, and "these two masks differ" is exactly the false finding a
comparison tool must not produce. `RailTarget` therefore overrides `Equals(RailTarget?)` and
`GetHashCode` to compare the mask's POINTS. Any later record here that gains a collection member
needs the same treatment — the compiler says nothing.

### 2. Only circuitRF's `Info.plist` claims `.crail`, not "all three"

The brief's registration table says `src/Ui/Assets/macOS/*.plist` (all three). The other two plists
are **harmonicaRF's and wBond's own standalone bundles, and each claims exactly one type — its own**.
railRF ships no standalone binary, so there is no second application for Launch Services to choose
between and no role to split; and a `.crail` claimed by a bundle whose binary has no railRF window in
it is precisely the "launches and opens nothing" failure the six parity tests exist to catch, wearing
a new hat. For the same reason circuitRF's entry is role **Editor** rather than the **Viewer** that
`.charm` and `.wBond` carry: those two are Viewer because a standalone owns them, and nothing owns
this one but circuitRF.

### 3. Classifying `.crail` in `DocumentKinds` is not free — `check` needed an arm the same day

This is the `.cdd` bug again and it is worth writing down twice. Giving `DocumentKinds.Classify` a new
extension moves the file out of `DocumentKind.Unknown`, which is one of the four kinds a folder walk
SKIPS. So `check <workspace>` stopped skipping every `.crail` and fell through to
`check.path.unknown-kind` — *"Nothing circuitRF reads is named 'board.crail'"*, false about a document
the application opens, once per file, exit 1 with nothing wrong. `CheckRail` is the arm, and it writes
no validation logic of its own on `check`'s own rule: the refusals are `RailDocumentIo`'s (the reader
the window opens one with) and the order is `RailOrder`'s. `explain` joins the group that reports the
workspace walk; `render` needed nothing, because its `default` already refuses BY KIND and now names
`rail` instead of `unknown`. **The rule: a row added to `Classify` obliges an arm in `check` and a case
in `explain` in the same change.**

### 4. `OpenRailPath` is a real route, deliberately, and it is brief 7's hand-off point

Brief 7 owns the window, but the parity tests require a `case ".crail":` in
`WorkspaceViewModel.OpenDocumentByPath` the moment the plist claims the type — and a case that does
nothing is the broken-file failure the tests exist to prevent. So the route READS the document through
`RailDocumentIo`, reports what it holds, and says the window is not available in this build. That is
not filler: it puts the reader's own refusals — an anchor that is both a pad and a coordinate, a rail
pair that cannot be ordered — in front of whoever opened the file. Brief 7 replaces the body and keeps
the route.

### 5. A stale `ExpectedIds` list was already red before this change

`CliStructuredOutputTests.DiagnosticIds_AreTheCommittedSet_UniqueAndCaseDistinct` was failing at HEAD:
`CliDiagnostics` declared `check.cdd.empty`, `check.cdd.source-not-run`, `check.cdd.summary` and
`check.cdd.trace-unbound` and the committed list carried none of them. Recorded now, alongside this
brief's three (`check.rail.summary`, `check.rail.order`, `check.rail.no-reference-layer`), which is
what that test's own doc comment asks for. Verified pre-existing with `git show HEAD:` on both files
rather than by stashing.

### Model decisions worth knowing before brief 3 or brief 5 reads one

- **A source refuses BOTH an R-L and a Touchstone file**, on `RailPortAnchor`'s own rule: a silently
  preferred one of two stated models is the same bug on a different row. §2.2 says a Touchstone
  REPLACES the R-L where one exists, so a document stating both has no single answer.
- **A coordinate anchor carrying a `Pin` is refused too.** It is the one malformed shape that reads as
  a pad anchor without being one — a fragment of the pad spelling left behind.
- **A `.crail`'s point anchor needs both `PointX` and `PointY`.** One alone reads back as `(x, 0)`,
  which is a point on the board and a plausible one, so the reader takes both or neither.
- **`RailSpec.DropBudget` and `RailSpec.ImpedanceTarget` are separate slots**, because §2.2 says a rail
  carries both and they are not alternatives; `RailLoad.Mask` is a third, because a mask is per
  observation port. `RailTarget.Refusal` takes the kinds a slot accepts, so a mask written into the
  rail's own slot is named rather than carried into a solve that would ignore it.
- **The transient derivation's 0.35 is a named constant** (`RailTransientSpec.KneeFactor`), not folded
  into the expression: it is the 10–90 % rise-time convention, and a reader who assumes 0.5 (20–80 %)
  gets a band top 43 % too high with nothing in the answer looking wrong.
- **`RailOrder` keeps declaration order among independent rails**, so two runs of one document do not
  report their rails in different orders for no visible reason. A refdes that is both a load and a
  source on ONE rail is its own refusal rather than a self-loop reported as "a rail depends on itself".

## railRF brief 6 — the via current check, and what review's own table turns out to be a table OF (2026-09-18)

`brief-railrf-6-via-check.md`. `PdnViaCurrentLimit` + `PdnViaCheck`/`PdnViaFlag` in
`src/Design/Layout/Pdn/`, gated by `tests/Ui.Tests/RailRf/PdnViaCheckTests.cs` (7 tests, ~4 s).

### 1. The drill-size table is a 10 µm-PLATING table, and that is the whole finding

§4.2 already knew the table disagrees with its own separately-quoted "0.3 mm at 20 µm → 0.8–1.0 A" by
about a factor of two, and that plating is the term that differs. What the implementation turned up is
sharper than that: **one current density reproduces the entire table at 10–12 µm of plating**, and the
same density puts the separately-quoted 20 µm figure at 0.880 A, inside its own band.

At 50 A/mm² through the annulus, on a 1.6 mm board at a 10 °C rise:

| drill | A at 10 µm | limit | the table's row | A at 12 µm | limit |
|---|---|---|---|---|---|
| 0.2 mm | 0.00597 mm² | 0.298 A | 0.3–0.5 A | 0.00709 mm² | 0.354 A |
| 0.3 mm | 0.00911 mm² | 0.456 A | 0.3–0.5 A | 0.01086 mm² | 0.543 A |
| 0.4 mm | 0.01225 mm² | 0.613 A | 0.7–1.0 A | 0.01463 mm² | 0.731 A |
| 0.5 mm | 0.01539 mm² | 0.770 A | 0.7–1.0 A | 0.01840 mm² | 0.920 A |
| 0.6 mm | 0.01854 mm² | 0.927 A | 1.0–1.5 A | 0.02217 mm² | 1.108 A |
| 0.8 mm | 0.02482 mm² | 1.241 A | 1.0–1.5 A | 0.02971 mm² | 1.485 A |
| 1.0 mm | 0.03110 mm² | 1.555 A | 1.5–2.5 A | 0.03724 mm² | 1.862 A |
| 1.2 mm | 0.03739 mm² | 1.869 A | 1.5–2.5 A | 0.04478 mm² | 2.239 A |

So the constant is a READING of review's answer rather than a number picked to fit it, and the
"factor of two" is not a discrepancy in the table — it is the table stating a thinner barrel than
anyone building a board today would plate. That is exactly why the table ships as a **sanity band**
and never as the rule, and why a computed limit outside its band is reported as outside rather than
clamped: a 20 µm board is outside it by construction.

### 2. The limit is LINEAR in the annulus, and that is a decision, not an oversight

Every energy argument gives `I ∝ √A` — the heating is I²R however the heat leaves, so a rule derived
from a rise gives a square root. §4.2's own sentence does not: *"twice the copper annulus … and
roughly twice the current"*, which is a CURRENT DENSITY and is what a table indexed on drill size
cannot express. The two readings disagree by 1.93 vs 1.39 on exactly the comparison the note is
making, so the choice had to be made rather than derived, and railRF takes the note's.

The rise budget and the span DO enter as square roots (`√(ΔT/ΔT₀)` and `√(h₀/h)`), because they move
the allowable density rather than the area and that part is ordinary I²R. The rule is therefore a
stated allowable density with two corrections, not a thermal model — §2.7 — and the code says so.

### 3. A "transition" had to be a via FIELD, and neither obvious key works

Both readings that suggest themselves are wrong in a way that only shows up on a real board:

- **The layer pair alone** makes every through via on a four-layer board one transition. "Ten would
  clear it" then says nothing about the part that is over.
- **The netlist node pair** — which is what the ranked breakdown of brief 5 already groups vias by —
  splits a real field into one group per barrel the moment the mesh is refined under it. A group of
  one cannot share unequally, and the check's whole subject disappears.

So a transition is one layer pair plus one spatial cluster: single-linkage within
`FieldLinkageDrillMultiple` (5) drill diameters, through a bucket grid so a board with thousands of
holes costs no pairwise sweep. Five because a via field's pitch is 2–3 drills and two unrelated
fields on a board are not.

### 4. The count that clears the flag CANNOT assume the split stays as uneven as it is

The natural extrapolation — hold the peaking factor constant, so `m = ⌈n·I_worst/limit⌉` — is
measurably optimistic, and the gate caught it. On its six-via fixture the field peaks at **1.91×** an
equal split; that formula asks for **nine**, and re-solving with nine leaves the flag standing,
because densifying a field over the same footprint makes it share **worse**, not better: peaking rose
from 1.912 to 2.299 and the worst via only fell from 4.46 A to 3.58 A against a 3.32 A limit.

The rule that survives is calibrated by the field's own measurement rather than by a constant:

    m = ⌈n · (I_worst / limit) ^ p⌉,   p = the measured peaking factor, clamped to [1, 6]

which collapses to the exact `n·ratio` at `p = 1` (a field that already shares evenly does improve in
proportion to its count) and is deliberately conservative above it. On the same fixture it asks for
**eleven**, and eleven clears. **A count that leaves the flag standing is worse than one that adds a
via too many** — the first is a wrong instruction and the second is a cheap one.

### 5. `Defaulted` plating is what selects the TABLE basis — the two provenances line up exactly

`PdnPlatingBasis` already had three values and brief 6 needed two bases, which looked like an
impedance mismatch until the mapping fell out: `StackupViaEntry` and `Setting` are the two sources
R-rail6-3 calls legitimate, and both give `PdnViaLimitBasis.Computed`. `Defaulted` — nobody stated it
— is precisely the case where the annulus rests on a number no one checked and is worth a factor of
two, so it is the case that falls back to the drill-size table and says so. `PdnViaModel`'s own
25 µm default still prices the barrel's RESISTANCE (a defaulted resistance is wrong by a bounded
amount); it may not price its LIMIT.

### 6. Two plumbing facts a later brief will trip over

- **The barrel travels on the origin in STRUCTURE now** (`PdnElementOrigin.Barrel`, a `PdnViaBarrel`
  that had been declared since brief 3 and never constructed). The drill, plating, plating basis and
  span were only ever in the origin's `Description` sentence, and a check that parsed them back out
  would be a second reading of the geometry that could disagree with the first.
- **`PdnProvenance.UnresolvedViaSpans` is a COUNT, not a diagnostic sentence.** R-rail6-5 needs the
  via check to make a note and no flag for holes whose span brief 3 refused to assume, and the check
  reads the RESULT — the extraction's `Diagnostics` do not travel on it.
- `PdnAssembly.LayerOfNode` reports WHICH of a stackup conductor's drawing layers a barrel landed on.
  Keying a transition on the conductor's first declared layer instead would merge two fields that are
  not the same transition, on any stackup that declares more than one drawing layer per conductor.

### 7. The symmetric gate needs a 0.05 mm mesh, and the convergence is the reason

Four identical vias across a uniform flow do not read as an exactly equal split on a coarse mesh: the
share of the worst reads **4.2 % high at 0.25 mm, 1.6 % at 0.1 mm and inside 1 % at 0.05 mm**. It is
discretization and it converges from above — the differential path resistance that separates the four
is about **0.15 of one square**, well under a cell. So the fixture that gates "each carries I/n to
under 1 %" is deliberately fine and deliberately small (a 4 mm run rather than a 12 mm one), while the
asymmetric fixture that measures a 1.57× and a 1.91× peaking runs at 0.2 mm — the noise the first one
has to chase is two orders below the effect the second one measures.

---

## railRF brief 11 — the parts over frequency, and the five findings the arithmetic turned up (2026-09-18)

`brief-railrf-11-part-models.md`. Four types compute what brief 2 made representable —
`RailEsrDefaults` (dissipation factor per dielectric class, and the same figure serving the board),
`RailDerating` (Q-12's bias correction), `RailPartModel` (the resolved element) and
`RailPartResolver` (row → model, file wins, plus the headline counts). Plus `RailSourceModel`, which
the brief's own table does not list but `R-rail11-9` requires — see §2 below.

### 1. The indicative flag needed a mechanism to cross the `DataSet` boundary, and circuitRF already had one

`R-rail11-4` names the failure exactly: *a flag computed here and dropped at the `DataSet` boundary
reaches none of* §9's *five places.* Only the first of the five — the part row — can read a
`RailPartModel`; the plot, the anti-resonance table, the mask-margin readout and **the provenance of
every export** read the result.

The mechanism is the `__`-prefixed metadata cube the node-picker work already established: such a
cube is sweep-invariant (`DataSet` passes it through rather than stacking it), is skipped by the
Data Display's signal list, and **is persisted through `.npy`** — which is the only reason an export
can carry the marking at all. `RailPartModelSet.Annotate` writes four of them on one labelled `part`
axis: the ESR basis code, the marked capacitance, the capacitance used, and which of the three that
was. **The two negative ESR codes are distinct on purpose** — a part nobody could resolve and a part
resolved with no ESR basis are different failures.

Carrying the marking as a `DataSet` *note* was not available: `DataSet` has no free-form provenance
field, and adding one for this would be a second place provenance lives.

### 2. `R-rail11-9` has no file in the brief's own table, and it is not a part

The source's R-L model, its cell-life sweep and the *optimistic near the loop crossover* statement
are a deliverable with their own test, and the brief's deliverables table lists four files, none of
which is about a source. It is in `RailSourceModel.cs` rather than folded into `RailPartModel` —
the two share `RailMeasuredPart` and nothing else, and a source is not a part.

**The converter statement cannot be derived from the source row and the model must not grow a flag
for it.** §2.2: what makes a source a regulator is that *the same refdes also appears as a
`RailLoad` on another rail* — a fact about the rail SET, which `RailOrder` already computes. So
`OptimisticLine(bool isConverter)` takes the answer from its caller. A `IsConverter` property on
`RailSource` would be a second, typeable spelling of something the document already states
structurally, and the two would disagree.

### 3. An R-L does not approximate a regulator's peak badly — it does not contain it

Worth writing down because it is why the sentence is load-bearing rather than a caveat. A series R-L
is **monotonic**: |Z| rises with f and never peaks. A real regulator's output impedance peaks at its
loop crossover, typically in the tens of kilohertz, and that peak is often the largest single
contribution to the rail's impedance in exactly the band the bulk capacitors are there to cover. The
resulting curve is not visibly wrong — it is smooth, plausible and missing a feature.

### 4. A file-backed part is NOT derated, and the row's C is not a fallback for it

Two decisions in `RailPartResolver.FromFile` that look like omissions:

- **Q-12's bias curve is not applied on top of a measured capacitance.** The file states the part at
  whatever bias the vendor measured it at and nothing in it says what that bias was, so applying the
  curve would derate an already-derated number. The model reports
  `RailCapacitanceBasis.Measured`, and `RailPartModelSet.WithoutBiasCurve` therefore does **not**
  count file-backed parts — they are not missing a correction, they do not take one.
- **A file that cannot be READ makes the part unresolved, never a silent fall back to the row.** The
  file overrides the row (Q-11), so an unreadable attachment and an attachment that was never made
  must not produce the same answer.

One consequence to know about: **a `.sNp` extension is what makes an attachment measured**, per
`PartLibrary.IsTouchstone`. A SPICE subcircuit attachment reports `PartModelSource.AttachedFile` and
takes the ROW's numbers for the parts table, because this brief builds no component model and the
subcircuit is placed as an ordinary circuitRF one.

### 5. Two arithmetic points that are easy to get subtly wrong

- **The measured and the library-row paths use ONE inductance derivation.** Where a vendor file
  contains the resonance, `RailMeasuredPart.InductanceHenries` is `1/((2πf₀)²C)` over the measured C
  and the measured f₀ — R-rail2-8's own formula — rather than L_eff read off the inductive branch.
  Two derivations would disagree on the same part and nothing would report it. L_eff is the fallback
  only where the sweep contains no crossing at all.
- **`RailPartModel.SelfResonanceHz` is NOT the row's stated f₀, and it should not be.** It is
  computed from the C and L the model actually carries, so it includes the mounting loop and Q-12's
  correction — **derating RAISES a part's resonance by √(marked/derated)**, which is a real effect a
  reader should see rather than one hidden by re-printing the table's number. The row's figure stays
  on `StatedSelfResonanceHz`. Q-9's own four numbers are the check: 470 µF with 5 nH → 104 kHz,
  1 µF → 5.3 MHz, 100 nF with 1 nH → 16 MHz.

### 6. Two model additions this needed, both small and both load-bearing

- **`RailSpec.Parts`** (`RailPart`: refdes, part number, typed mounting inductance, BOM-or-typed
  origin), with `.crail` IO. `R-rail11-8` says P1 fills the mounting loop *from the document* and the
  document had nowhere to put it. **It is not a second bill of materials**: what it carries that a
  BOM cannot is the per-instance mounting loop, which is a property of where a part was PLACED. A
  rail with no artwork and no BOM — P1's lumped case — carries typed rows, and `Origin` is what tells
  the two apart, exactly as `RailAggressor` already does.
- **`RailSpec.NominalVoltageV`** — the highest open-circuit voltage any source states, which is what
  derating is applied at before a DC answer exists. The DC answer supersedes it: a part at the far
  end of a rail sits below the source by the drop brief 5 computes.

Both are additive and write nothing on a document that has no parts, so existing `.crail` bytes are
unchanged.

### 7. What the class table is wrong about, on purpose

Q-15 makes the per-class dissipation factor **the basis rather than a stopgap**, so the two things
wrong with it are permanent conditions and are written into `RailEsrDefaults`' own header:

1. **A quoted DF is a MAXIMUM.** A real part is usually better, often by two or three times — so a
   peak height computed from one is pessimistic more often than optimistic, and *"usually
   pessimistic"* is not a number anyone can put a margin against.
2. **DF is frequency-dependent and these figures are quoted at one frequency.** `ESR = DF/(2πfC)`
   therefore falls as 1/f, while a real part's ESR flattens near its own resonance where the
   electrode resistance takes over from the dielectric loss.

Neither is repairable from what a part library holds, which is why §2.2 RECOMMENDS the part's own
Touchstone file rather than merely offering it.

**The board half has a trap of its own:** `StackupLayer.TanD` is a plain `double` that opens at 0, so
**a lossless dielectric and an unstated one are the same bytes.** `BoardLossTangent` treats 0 as
unstated, because the alternative gives every stackup that never mentioned tan δ an infinitely sharp
cavity — the optimistic direction — while treating a genuinely lossless laminate as unstated costs
nothing, since no real one is.

### Gate

`tests/Ui.Tests/RailRf/RailPartModelTests.cs`, 9 tests, one per claim. The Touchstone fixture is a
series R-L-C in the shunt-through fixture built in memory from its closed form
(`S21 = 2Z/(2Z + Z0)`), so the file path is tested against a KNOWN answer with no disk — not against
a second copy of our own reading of it. All 179 `RailRf` tests pass, `Firewall.Tests` 12/12.


## railRF brief 12 — the frequency run, and the five things it had to decide (2026-09-18)

`brief-railrf-12-impedance.md`. `PdnSweep` in `src/Design/RailRf/` is the whole design-side
deliverable: it builds the netlist, hands it to `SParameterEngine`, converts the S it gets back to
Z, packs a `DataSet`, and calls the four `src/Engine/Pdn` types for the mask, the peaks, the
coincidences and the ranking. Findings about those four are in `src/Engine/RESOLVED.md`.

### 1. The netlist is REBUILT at every frequency, and that is the design rather than a shortcut

A part's ESR is not a constant. A class default is `DF/(2πfC)` and falls as 1/f; a measured one is
Re Z out of the part's own file. A netlist assembled once with one ESR is a different circuit from
the one §2.2 describes, and the error is largest exactly at the resonances this brief exists to
find — the depth of every minimum and the height of every anti-resonance is set by ESR. So each
point gets its own assembly, which is also literally what §4.4's "one sparse complex MNA system per
frequency" says.

That decision is what makes the second one legal: **a branch is stamped as the exact `R + jX` it
has at the frequency being assembled** — a resistor in series with one reactance of the right sign,
through an internal node. There is no two-terminal model in circuitRF that takes an arbitrary
complex impedance, and synthesising a fixed R-L-C whose reactance happened to match at every point
is precisely what a per-point assembly exists to avoid.

### 2. A part with no ESR basis is NOT stamped, and the reason is not tidiness

R-rail11-3 already says a part with no file, no stated ESR and no recognised dielectric class gets
no class default. The consequence here is stronger than it looks: a lossless branch makes every
anti-resonance it takes part in **unbounded**, so stamping it with zero ESR does not produce a
slightly optimistic answer, it produces an infinite one at an arbitrary frequency. Such parts are
counted and named on the result's warnings, next to `RailPartModelSet.Summary`, exactly as
`PdnAssembly.StampShunts` already counts a part with no capacitance.

The same choice was NOT available out of band on a MEASURED part, and the third option was taken:
**outside a vendor file's own band the ESR is HELD at the nearest end the file states, and the
result says so once per part.** Dropping the branch there changes the topology between one sweep
point and the next, which manufactures an anti-resonance at the file's band edge out of nothing;
zero ESR is the unbounded case above. A held figure is wrong by a bounded amount in a band where
the part's own reactance dominates it anyway, and the sentence is what stops it reading as a
measurement. (`RailMeasuredPart.EsrOhmsAt` still returns NaN out of band — the clamp is the
caller's, stated at the call site, rather than a change to the rule that type owns.)

### 3. An ideal source is a REFUSAL, not a note

A source stating neither a series resistance nor a series inductance shorts the rail to its
reference at every frequency, so |Z| is identically zero and every mask passes. That is the absence
of an answer rather than an optimistic one, so it is refused naming `SeriesResistanceOhms` — the
house spelling. The DC side takes the same row as an ideal source and merely NOTES it, which is
right there: at DC an ideal source still leaves the copper, the vias and the series parts between
itself and the load, so the answer is a real number that flatters. Over frequency there is nothing
left.

### 4. At P1 every observation port on a rail reads the same curve, and the result says so

§6 makes P1 the lumped PDN, so the rail is one node and every part, source and port hangs on it.
Several observation ports therefore give an exactly rank-1 Z matrix. That is honest — the model
contains nothing that could make them differ, and the distributed low band is brief 13 — but it
looks like a bug, so a rail with more than one load carries a note saying it.

(The S-to-Z conversion is unaffected by that rank: `Z + Z₀I` is invertible for any Z, and
`(I − S) = 2Z₀(Z + Z₀I)⁻¹` is too. About eleven significant figures survive the
`Z = Z₀(1+S)/(1−S)` cancellation at 1 mΩ against a 50 Ω reference, which is ample;
`PdnSweepRequest.PortReferenceOhms` exists for the case somebody finds where it is not.)

### 5. `RailPartResolver.ReadMeasured` is public because a SOURCE has a file too

R-rail11-9 says a published output-impedance curve is "a part like any other", and brief 12 is the
first caller that has to read one. The six lines that turn an `SNP` into a `RailMeasuredPart` —
which port pair, which extraction mode, which reactance crossing is the resonance — are now one
method that `FromFile` calls and the window calls. A second copy would have been a second reading
of the same file that could disagree with the first about any of those three, on a part that
resolved perfectly well through the other path.

### Gate

`tests/Ui.Tests/RailRf/PdnImpedanceTests.cs`, 9 tests plus the guarded acceptance anchor. Every
fixture's answer is known by construction — a two-branch board's anti-resonance is
`1/(2π√(LC))` and nothing about that comes out of circuitRF.

**§7's acceptance anchor SKIPS and will keep skipping until real bytes land.** It wants a published
measured PDN from the open SI literature reproduced within the tolerance that literature states.
Synthesising a curve to match would turn the one gate in this series that is not our own arithmetic
into another piece of it, so it is a `FixtureFact` on `testdata/railrf/measured-pdn/` — the board as
a `.crail`, the digitised |Z(f)| as two columns, and a `# tolerance_db=` header the test refuses to
run without, because a reference file that does not carry the publication's own tolerance cannot
gate anything. Same two-tier shape as Q-18's reader gates.

### Two brief premises the arithmetic did not support

- **Q-9's 7.1 MHz** is a first-order figure; the circuit says 6.50 MHz. `src/Engine/RESOLVED.md` §1.
- **"A part in parallel with a lower-ESL neighbour 2 mm away ranks 0.0 dB"** (R-rail12-6) is not
  true where the margin actually lives. A mounting loop only decides a branch ABOVE its own
  resonance; below it the branch is `1/(jωC)` and two parts of the same capacitance contribute the
  same susceptance whatever they are mounted on. The worst margin sits at the anti-resonance, which
  is below both ceramics' resonances — so a twin mounted 4× worse ranks **1.6 dB, not 0.0**.
  Shadowing by ESL is a statement about the band above resonance; what swamps a part where the
  margin is, is CAPACITANCE. Both rows are asserted in the gate, the wrong-premise one included, so
  a later change that quietly made it read 0.0 dB would fail.

---

## railRF brief 13 — the distributed low band, and the six things the inductance turned up (2026-09-18)

`docs/sonnet-briefs/brief-railrf-13-distributed.md`, P2a. The mesh and the fast graph gain L, the
mounting loop is computed from the real via geometry, and `PdnInductance` is where every one of
those expressions now lives.

### 1. §4.1's inductance is the LOOP's, and it has to be HALVED per conductor

The design note writes the plane pair's per-square resistance with its factor of two visible —
`R = 2·Rs`, both planes — and its per-square inductance without one, `L = µ₀·h`. **Both are the
loop's.** `PdnMeshExtractor` pays the resistance's two by construction, by meshing BOTH conductors
so a loop traverses `Rs` on the way out and `Rs` on the way back (its own file header is about
exactly that). The inductance has to be split the same way, or `µ₀·h` copied onto each edge puts
`2·µ₀·h` round the loop and **doubles every plane-pair inductance in the tool**.

The asymmetry in how §4.1 writes the two is what makes this easy to get wrong, and nothing would
report it: every curve would simply sit at the wrong frequency by √2. `StampMesh` therefore stages
`µ₀·h/2` per conductor edge, and `PdnDistributedTests` measures it **round the loop** — the same way
`PdnMeshExtractorTests` already measures the resistance.

### 2. `SeriesRlcModel` cannot carry a mesh edge, and `InductorModel` can

The brief's scope says "no new elements — `SeriesRlcModel` carries an edge's R and L together, which
is what it is for." It is the wrong one of the two: **SRLC requires a `C`, and `C = 0` is an OPEN at
every frequency**, so a mesh built on it would be a mesh of open circuits with nothing to say so.
`InductorModel`'s `C=` is optional; with `R=` and no `C=` it stamps exactly `R + jωL` on one branch,
which is what §4.1 asks for. No new element exists either way.

**At ω = 0 the element is a plain `ResistorModel`, and that is not an optimisation.** §2.8: set ω = 0
and the inductance vanishes and what is left is real, symmetric and positive-definite. An inductor
with `L = 0` would be the same physics and a different matrix — one Group-2 branch current per cell
edge, which on a mesh of a hundred thousand edges is a hundred thousand extra unknowns for an answer
already known to be the resistive one.

### 3. §4.1's `Rs = √(πfµ/σ)` and §2.8's three crossovers disagree by a factor of four, and the fix is where the ½ goes

Read `Rs = √(πfµ/σ)` as ONE PLANE's sheet resistance and it is `ρ/δ`, which crosses the DC value
`ρ/T` at `δ = T` — **3.6 MHz on 1 oz copper, not the 14 MHz §2.8 tabulates**. At §2.8's own 14 MHz
the skin expression is already twice the DC one, so a piecewise rule built literally on the note's
two sentences puts a **factor-of-two STEP in R in the middle of the 1 MHz–100 MHz band this phase
exists to make quantitative**, and nothing reports it.

Read `√(πfµ/σ)` as the LOOP's skin resistance — `2·Rs`, the two planes, exactly the factor §4.1 is
about — and every number in the note is consistent at once. `Rs = ½·√(πfµ/σ) = ρ/(2δ)` is a
conductor carrying current on BOTH faces, which is what "two skin depths" means, and it meets `ρ/T`
**exactly** at `T = 2δ`: 57 MHz at 0.5 oz, 14 MHz at 1 oz, 3.6 MHz at 2 oz. Continuous, no step, and
§2.8's three numbers arrived at rather than asserted.

So `PdnInductance.SheetResistanceOhmsPerSquare` is `max(ρ/T, ½√(πfµ/σ))` and its header says why.
**A reader who "corrects" it back to `√(πfµ/σ)` per plane restores the step and halves every
crossover frequency in the tool.** The three crossovers and the continuity across each of them are
both asserted.

### 4. The extraction's netlist could not be swept, because its ports had a dot in their names

§3 of the design note is that the deliverable is a netlist every downstream consumer already sweeps.
It was not: `PdnAssembly` spelled a port `port.1`, and **`SParameterEngine` treats a `Port` whose
instance path contains a `.` as a BURIED port inside a sub-cell and skips it** (the Layer 2 scoping
rule). An extraction therefore reached the engine with no ports at all and was refused with a
sentence about placing Terms in a testbench — which is not what went wrong, and which nobody would
have connected to a name. It is `port1` now, which is what `PdnSweep`'s own lumped assembly already
spelled it.

### 5. A port made of point cells has a LOGARITHMICALLY DIVERGENT spreading term

R-rail13-4 asks for brief 3's refinement gate re-run with L in place: halving Δ under a port moves
the port inductance by less than the stated tolerance. **It does not settle, and it cannot.** A port
attaches at its pads' own cells, so it is a set of POINTS, and spreading into a point in two
dimensions grows as `ln(1/Δ)` without bound. The measured ladder climbs 82.9 → 86.8 → 89.5 →
92.5 pH — 4.7 %, 3.1 %, 3.4 % — by roughly a constant per halving rather than settling.

It passes brief 3's 10 % tolerance only because the port and the feed are each a FIELD of pads,
which divides the term by their count. **A single-pad port on the same board moves 10 % per halving,
which is that tolerance exactly** — so brief 3's gate is one halving's worth of divergence rather
than evidence of convergence, and it has been precautionary in a way its own comment does not say.

What would make it genuinely converge is a port that ties every cell its pads COVER rather than the
one cell each pad's centre lands in — §4.3's "its own pin-field cells" read as an AREA. `PdnPad`
carries no pad size, so that is a change to brief 2's readers and not to this one. **Recorded rather
than tuned away.**

**And the fixture trap that goes with it:** the divergence is dominated by whichever terminal has the
FEWEST pads. The first version of this gate used a single-point source, and its ladder climbed 10 %
per halving with the antipad field making no measurable difference at all — the number under test was
the SOURCE's spreading, not the port's. Widening the feed to a 5 × 5 field is what made the gate
measure the thing it names.

### 6. The fast model's section inductance is the mesh's own law, which is what makes the agreement structural

§2.9 gives the fast reading "one loop inductance" per trace section, and §4.6's whole claim is that
it is "not a second simulator, it is a second reading of the geometry". A section of `ℓ/W` squares
over a plane at `h` is `µ₀·h·ℓ/W` of loop — the same per-square law, through the same
`PdnInductance`, halved the same way — so Fast and Accurate agree on |Z| across 1 MHz–100 MHz for a
structural reason rather than a numerical coincidence. Measured inside 5 % at 1, 10 and 100 MHz on
brief 4's own stepped-trace fixture.

Two supporting facts: the section's square count comes from `SectionResistance`'s own integration
along the run, so a tapered section's L and its R are one reading of one piece of copper; and
`PdnExtractionRequest.FrequencyHz` is now the one spelling both readings take, with
`PdnGraphSettings.FrequencyHz` left as the graph-only fallback. §2.9 rule 4 compares the two curves
on the user's own board, and two readings answering at two different frequencies would be compared
against each other with nothing to say so.

### The mounting loop, and the one thing it clamps

`L_loop = L_p + L_r − 2·M_pr + L_pad`, over Grover's partial self-inductance of a round conductor and
the exact Neumann mutual of two parallel filaments. On the four-layer fixture it reads 330 pH at
0.5 mm of via separation and 494 pH at 4 mm — §2.2's 0.3–1.5 nH band, and §2.5's "0.4 nH on the
reference and 1.1 nH on yours" shape.

**The partial forms are each valid for a conductor far longer than it is wide**, and a pair closer
together than a barrel radius drives `L_p + L_r − 2M` negative — not a small error but a negative
inductance in a netlist, a branch that would deliver energy. `PdnInductance.LoopHenries` clamps at
zero and `PdnMountingLoopExtractor` says on the row that the clamp fired, because a silently clamped
zero is a part with a perfect mounting loop.

A computed value never beats a typed one (§2.2: "you can override it"), and `RailMountingBasis` is
what says which a reader is looking at. The railRF window now fills in only the rows nobody typed.

### Gate

`tests/Ui.Tests/RailRf/PdnDistributedTests.cs` — 7 test methods, 12 cases, 1 s. One per claim.

### What this phase does NOT do, and it is worth being explicit

**`PdnSweep` is still the lumped one-node rail**, so the window's Z(f) curve does not yet carry the
mesh's R-L: the distributed netlist exists and solves, and nothing routes it into the frequency run.
That is deliberate — brief 13's own deliverable list is three additions to the extractors, and the
shunt branch that a distributed sweep would want is brief 14 — but §2.8's sentence about every
observation port on a rail reading the same curve is still what the window prints.

## railRF brief 14 — the cavity, and the six things the shunt branch turned up (2026-09-18)

`docs/sonnet-briefs/brief-railrf-14-cavity.md`, P2b. §4.1's last two terms — `C = ε₀εᵣΔ²/h` to the
reference plane and `G = ωC·tan δ` across it — plus the sampling that makes a narrow plane resonance
findable. `PdnCavity` is where both expressions and the medium they are of now live; `PdnCavityTests`
is the gate.

**The phasing argument survives into the code, and it is not a hedge.** §4.5: a 60 × 50 mm board on
FR-4 has its first mode at about 1.2 GHz and the excitation set of §2.2 tops out at 50 MHz. Nothing
here was built at the expense of the earlier phases — the DC path is bit-identical (the shunt branch
is not stamped at ω = 0), and the cell-size change below cannot coarsen a DC mesh.

### 1. The area is the OVERLAP, and `min(A₁, A₂)` is not it

R-rail14-3 says the plane capacitance is over the real overlap rather than the outline, and the
obvious reading of that on a mesh is "the smaller of the two conductors' cell areas". **It is wrong
whenever the two coppers are in different parts of the same cell** — a trace on one conductor and a
plane on the other, a split, the edge of an antipad field — and it would price a capacitance between
two pieces of metal that do not face each other, optimistically and by an amount that depends only
on how the grid lines happened to fall. `MeshBuilder.OverlapAreas` rasterises the actual Clipper
intersection of the two unioned coppers, which is why `MeshLayer` now keeps its union rather than
re-unioning on demand. The gate is a 20 mm × 2 mm rail trace over a 20 mm × 20 mm plane: the right
answer is ten times smaller than either conductor's own area.

### 2. More than one dielectric between a plane pair combines in SERIES, which is not an average

An inner pair split by a core and two prepregs is three `StackupKind.Dielectric` entries, and the
capacitance of the stack is the series combination of the three:

```
1/C = Σ hᵢ/(ε₀εᵣᵢA)      →      εᵣ_eff = Σhᵢ / Σ(hᵢ/εᵣᵢ)
```

a harmonic mean weighted by thickness. On a 100 µm core of εr 4.3 beside a 100 µm film of εr 9 the
arithmetic mean says 6.65 and the right answer is 5.81 — **13 %, straight onto the headline number
§9 says a designer recognises at a glance**. The loss tangent combines on the same weights (each
entry's share of the RECIPROCAL capacitance, which is its share of the voltage), and the flag that
brief 11's per-class figure was used anywhere travels with it. `PdnCavity.MediumBetween` is the one
place this happens, over `PdnStackupGeometry.DielectricsBetween`, which is the existing z walk with
its via entries skipped for the reason that file's header already gives.

### 3. ε₀ is DERIVED from µ₀ and c, and that is about the plane pair rather than about precision

The same two planes carry `L = µ₀h` per square (`PdnInductance`) and `C = ε₀εᵣA/h` (`PdnCavity`), and
the wave on them travels at `1/√(LC)`. Two independently-typed constants let that velocity drift
from `c/√εᵣ` in the ninth digit for no reason anyone could point at — and **every cavity frequency
this series computes is that velocity over a length**. So `PdnCavity.Epsilon0 = 1/(µ₀c²)` off
`PdnInductance.MuZero`: one electromagnetic basis, and the pair's L and C cannot come to disagree
about the medium. (`EmProblem.Eps0` makes the same choice; it is out of scope here by the brief's own
"no `CircuitRF.Engine.Mom` types" rule, apart from the sampler.)

### 4. `ParallelRlcModel` is the wrong element for a cell's shunt, and for a matrix reason

The shunt branch is `ωC(tan δ + j)` and PRLC says exactly that in one line. It also **always opens a
Group-2 branch for its inductor**, so a cavity mesh of a few thousand cells would carry a few
thousand extra branch currents for an `L` that is identically zero. Two Group-1 elements —
`CapacitorModel` and `ResistorModel` at `1/G` — stamp the same admittance and add no unknown. This is
the same trap brief 13 hit from the other side with `SeriesRlcModel`, and no new `ComponentModel`
exists either way (overview §1f).

The `G` resistor deliberately reports **no `ResistanceOhms` on its origin**. §2.4's ranked breakdown
sorts on DC drop, this element does not exist at ω = 0, and a megohm shunt sorted into that table
would head it while contributing nothing. `PdnOriginKind.PlaneShunt` is likewise distinct from
`Shunt` because a `Shunt` is a PART with a refdes that §2.4's removal ranking may offer to delete,
and the plane pair's own capacitance is the board.

### 5. The adaptive sweep's DECISIONS were reusable; its grid-bisection LOOP deliberately was not

§4.4 names `PlanarAdaptiveSweep` as the mechanism and the brief says not to write a second refiner.
**One scope note, stated rather than assumed:** the brief's §6 names `PlanarAdaptiveSweep` as the
single `CircuitRF.Engine.Mom` exception, and the code also calls `PlanarResonanceSearch`. That is
the same mechanism rather than a second one — it lives in the sampler's own settings record
(`PlanarAdaptiveSettings.Search`), it seeds itself from `PlanarAdaptiveSweep`'s interpolant and it
stops on `PlanarAdaptiveSweep`'s criterion — and it is the half that actually finds a narrow
resonance, for the reason in point 2 below. Naming only the outer type in the brief predates ANT-9.

What `PdnAdaptiveSweep` reuses is everything that decides — `PlanarResonanceSearch` for the
crossings and the bracketing, seeded from `PlanarAdaptiveSweep`'s own interpolant and judged on its
own |ΔS| criterion. **The grid-bisection half is not reused, and that is a decision with two
reasons:**

1. `PlanarSolve`'s refinement solves a SUBSET of the requested grid and models the rest, because a
   de-embedded full-wave point costs 48–72 s there. A PDN point is one sparse complex solve, so the
   saving is worth nothing — and §2.4's mask verdict, anti-resonance table and coincidence rows are
   all read off the curve AT the requested points. A modelled point in a mask verdict is an
   interpolation reported as a measurement.
2. **Refinement cannot find a narrow resonance anyway, and the EM work already measured that**
   (ANT-9): it bisects INDICES of the requested grid, so every frequency it is allowed to look at is
   already solved. On a high-Q response it can solve 86 % of a 51-point grid, miss its tolerance by
   twenty times and never once look where the resonance is.

So the whole requested grid is solved and the search ADDS what the grid stepped over, reported in
`PdnSweepResult.AddedHz` and in a note — ANT-9's narrowed property, in the same words. The gate is
R-rail14-4's own: the peak being FOUND, with the negative half beside it (a log grid of the same
point count reads a third of the true peak and looks entirely smooth). It is ON by default in the
window, because §4.4 says it is not optional and because the table it feeds is one of the three
answers §2.4 exists to produce.

`PlanarSolve.Run`'s own loop stays where it is: re-pointing it at a shared refiner means re-running
the `Category=Benchmark` adaptive-sweep tier (`AdaptiveSweepTests.T4_1`/`T4_2`, 3–4 min each) to
prove bit-identity, which is more than this brief's budget. Nothing is duplicated by leaving it —
the criterion, the interpolant, the seeding and the search are all single-sourced; what is not
shared is thirty lines of interval bookkeeping that the PDN path does not execute at all.

### 6. A port sits half a cell in, and that is the whole residual against the closed form

The cavity-impedance gate meshes a plane-pair strip exactly one cell wide, which makes it the
classical LC ladder discretisation of a uniform line — each cell `C'Δ`, each edge `L'Δ` and `R'Δ`,
all three arrived at from the AREAS — with a textbook `Z_in = Z₀·coth(γℓ)` to check against,
loss included.

It agrees to well under 1 % below and through the first mode, and **what is left is first order in Δ
and is not the shunt branch**: the port attaches at the first cell's CENTRE, half a cell in from the
copper's edge, so the stub the mesh presents is `ℓ − Δ/2` long. Measured at 0.30·f₁: **2.56 % at
Δ = 2 mm, 1.29 % at 1 mm, 0.65 % at 0.5 mm** — halving with the pitch, three times over, which is
§7's "monotone convergence in cell size" and is asserted rather than described. A later reader who
reads that 0.65 % as a modelling error in `C` would go looking in the wrong file.

The same offset is why the **quarter-wave zero at 0.5·f₁ is deliberately not in the ratio gate**: |Z|
passes through zero there, a ratio is the wrong measure of anything (0.56 Ω against the line's
0.003 Ω, on a curve whose own scale `Z₀` is 18 Ω), and the zero is not a mode — §4.5's `f_mn` are the
half-wave family, so "below and through the first one" does not reach it.

### 7. R-rail14-5's "stated fraction" has to be 5 %, and 1 % would have failed the gate it is in

R-rail4-4's ceiling is a tenth of the first cavity resonance, on the argument that leaving a
distributed shunt out costs `tan(βa)/βa − 1` and that this is 3.4 % there. So **the fraction the
threshold is checked against must be above 3.4 %, or the assertion inverts**: the 1 % point is at
`f₁/18`, BELOW the ceiling, and "the threshold is at or below where the shunt matters" would be
false by construction rather than by defect. 5 % is §7's own Fast-versus-Accurate agreement
tolerance and is the right number for that reason — the shunt branch may not move |Z| by more than
the two readings are allowed to differ, anywhere Fast is allowed to answer. Measured on a 50 mm
strip the 5 % point is 180 MHz against a 145 MHz ceiling: honest, and within a factor of two, which
the gate also asserts so the ceiling cannot be made safe by making it meaningless.

The two solves differ in **exactly** the shunt branch: the netlist is extracted once per frequency
and the `PlaneShunt` components are removed from the copy, descending by index. Comparing the mesh
against `PdnGraphExtractor` instead would have compared two readings of the copper as well, and the
copper is not what is under test.

### 8. Both cell-size rules bind now, and the basis says which one did

Brief 3 set the cell size from the narrowest current-carrying conductor and warned that the
wavelength rule binds from this brief onward. It does, and the cell is the **smaller** of the two —
the two failures are silent and in opposite directions: too coarse for the feature loses a thin
trace's resistance, too coarse for the wavelength loses the resonance the cavity band exists to
find. `PdnProvenance.CellSizeBasis` names the rule that bound and, where the other was close, what
it asked for. A STATED cell size is still honoured exactly — it is the knob the convergence sweeps
turn — but one coarser than λ/20 now produces a note, because that curve looks entirely normal and
is missing its own resonance.

`PdnMeshExtractor.LargestEpsilonR` is shared with `PdnGraphExtractor`, which had its own copy: the
fast model's cavity-band refusal and the mesh's wavelength cell size are the same statement about
the same stackup, and two readings of it could quietly come to disagree about where Fast stops being
honest.

### 9. The plane capacitance is a SENTENCE on the result, not a number two writers format

§9 asks for the extracted plane capacitance "as a single number early and prominently". It is
`RailDcResult.PlaneCapacitanceLine`, for the reason R-rail5-2 puts the voltage-interpolation rule on
the result: the window and the headless report may not disagree about the same number at the same
moment, and the two are written by different briefs. It is the FIRST card in the window's results
column and it is on the always-on status strip, and `circuitrf rail` prints it above the tables.

It is present on a **DC** run, where nothing was stamped from it — at ω = 0 the shunt branch vanishes
and the stackup is exactly as worth checking. `PdnProvenance.ShuntBranchPresent` is therefore a
different question from "is the capacitance non-zero", and a reader comparing two curves needs to be
able to tell a run that modelled the cavity from one that only measured its stackup.

## railRF brief 16 — the A/B comparison, and the five things the pairing turned up (2026-09-18)

`RailComparison`, `RailComparisonReport` (both `src/Design/RailRf/`) and `PdnDelta`
(`src/Engine/Pdn/`) — §2.5's workflow, the one Q3 names as the reason the whole tool exists. Gate:
`tests/Ui.Tests/RailRf/RailComparisonTests.cs` (13 tests, ~0.4 s).

### 1. Rule 4 is enforced by the SIGNATURE, not by a comment

§2.5 forbids pairing "by proximity or by guessing". `RailComparison.Match` takes two
`RailDocument`s and nothing else — no artwork, no technology, no placement — so there is no
coordinate in scope to pair by, and an edit that wanted to pair by proximity would have to widen the
parameter list to do it. The behavioural test (two ports 0.2 mm apart stay unmatched) is the weaker
half; the source scan that finds no `LayoutView`, no `Math.Sqrt` and no `Math.Abs` in that file is
the half that cannot be satisfied by accident.

The one coordinate the file reads is `RailPortAnchor.Point`, compared for **exact equality and never
for nearness**. That is not a loophole: after a re-layout, two ports at exactly the same DBU are the
same port that did not move, and two ports 0.2 mm apart are unmatched however close they get.

### 2. Rule 3 needs an ambiguity rule, and §2.5 already states it

"Models by part number, where a refdes did not match" reads as a simple fallback until a board has
three unmatched 100 nF 0402s on one side and two on the other. That is not a pairing, it is the
question §2.5 says railRF asks — so the fallback fires only where **exactly one** unmatched row on
each side carries that part number, and every other case becomes an unmatched row naming the count.
A pairing rule with no ambiguity rule would have produced its most confident wrong answer on
precisely the redesign this feature is for.

### 3. Two sweeps on different grids are refused, not interpolated — and this is not pedantry

R-rail14-4's resonance search **adds points where that board's own peaks are**, so two boards swept
from one band come back on two different axes as a matter of course — and the two axes differ
precisely at the frequencies the comparison is about. Interpolating one onto the other would invent
the target curve's value exactly where the target curve is changing fastest, and the invented number
would then be reported as a several-decibel excursion with a frequency beside it. `PdnDelta.Compute`
refuses, naming the first point that differs; the caller's remedy is to sweep both sides on one
explicit grid, which is what `RailComparisonTests` does throughout.

The delta is a **ratio in dB** and not a difference in ohms for the same class of reason: a PDN
curve crosses three or four decades across its band, so a difference in ohms is almost entirely a
picture of where the curve is big, and a doubling at the bottom — the one that breaks a flat
milliohm mask — would be invisible beside it.

### 4. An excursion band is closed by the SIGN, and a resonance that MOVED is two findings

Contiguous runs past the threshold are grouped and reported by their own peak, but a run ends when
the sign changes. A resonance that moved produces a positive excursion on one side of it and a
negative one on the other: the judged design is worse here and better there, which is two facts. A
band merged across the zero crossing would be reported with its peak in the middle — the one
frequency in the whole range where nothing happened.

### 5. A 0.0 dB removal row cannot be constructed out of parallelism, and that changed the fixtures

`PdnRemovalRanking.DisplayResolutionDb` is 0.05 dB, which is an impedance ratio of 1.006. Removing
one of N equal parts in parallel moves the answer by about `8.7/N` dB, so **no bank of a plausible
size is redundant by parallelism** — reaching 0.05 dB that way needs N ≈ 174. The real 0.0 dB row is
a part that is OUT OF BAND where the judging happens, which is exactly what §2.6 means by "shadowed
by lower-inductance neighbours". The gate for R-rail16-5's second finding therefore judges over the
bulk's own decade (10 kHz–100 kHz), where a 100 nF ceramic is three orders of magnitude above the
bulk beside it and moves the worst margin by nothing at all, while the bulk still earns everything.

This is also why the *first* finding's gate hands the report two **written-down** rankings rather
than solving for them: §7's instruction is that the correct answer is constructed rather than solved
for, R-rail16-5 is a claim about how the report READS two rankings, and the physics of a 0.0 dB row
is brief 12's gate rather than this one's.

### 6. The removal ranking names a part as the RESOLVER names it, which the comparison has to know

`PdnSweep` passes `RailPartModel.Name` — `C3 (PN-100N-0402)` — into the ranking, because a ranking
read on its own has to say which part `C3` is. The comparison keys on the **refdes**, per §2.5's rule
2, so it looks *from* the refdes the match already holds rather than parsing the row's name apart —
and it must, because the two designs' part numbers may legitimately differ for one refdes (a
second-source part is still that part). An unmatched ranking row keeps its full spelling, because
there is no matched refdes for it to be named by.

---

## railRF — review round 1 (briefs 1-8), 2026-09-18

Four defects found by reading briefs 1-8 against the code. None was caught by the suite, and each
is the shape the series' own standing rules warn about: a plausible answer with nothing reporting a
failure.

### 1. `RailOrder.Resolve` threw on two rails of one name, from a method documented never to throw

`RailOrderResult`'s own doc comment says "an order, or a refusal. Never both, and **never an
exception** — the caller is a window that has to put the sentence on its status strip and a verb
that has to print it, and neither of them wants a stack trace." Kahn's two dictionaries were built
with `ToDictionary`, which throws `ArgumentException` on a duplicate key — while the `index`
dictionary immediately above it had already been written with `TryAdd` precisely because duplicates
are possible.

Unreachable from a FILE (`RailDocumentIo` validates `RailDocument.Refusal()` on read and on write,
and that check names duplicates), which is why nothing caught it. Reachable from a document being
EDITED: the window's rail-name row is an ordinary editable field. Now a refusal, in
`RailDocument.Refusal()`'s own words so the two surfaces say the same sentence.

### 2. `RailDcRun.WithSourceLevel` built a rail copy that silently held less than the rail

The chained-source copy carried `Sources`, `Loads` and `Aggressors` and dropped `Parts` — the
decoupling bank. Harmless today by two accidents rather than by design: a capacitor bridges nothing
at ω = 0, and the shunt bank reaches the extraction through `PdnExtractionRequest.ShuntParts` rather
than through the spec. It is the first reuse of this copy at a frequency that would lose a whole
bank with no error anywhere, so the copy is now complete.

### 3. A grouped reference cell with spaces round its dash invented a part called `-`

`RefdesCell.Parse` splits on whitespace as well as on comma and semicolon (an export that joins with
a space is ordinary), so `C1 - C9` split into three pieces — two ends and a bare hyphen. The hyphen
failed `TryExpandRange`, was reported in `Unexpanded`, **and was also added as a reference**. The
result is nine parts reported unresolved plus one part the board has never had, which reads as a
broken board rather than as a cell the reader could not spell. The spaces are now closed up before
the split — so the range expands as written — and a piece holding no letter or digit is never a
reference.

### 4. Two `<summary>` tags on one member

`PdnGraphExtractor.LargestEpsilonR` carried a leftover second summary. Merged; the second one's
content (why this defers to `PdnMeshExtractor`'s reading rather than making its own) is the part
worth keeping.

### Reported and NOT changed

- **`DelimitedTable.HadByteOrderMark` cannot be true on the file path.** `File.ReadAllText` detects
  and strips a byte order mark before the text is seen, so the diagnostic both readers write for it
  can only fire on the string overload. Nothing is mis-parsed either way — the mark is gone — so
  this is a diagnostic that cannot appear, not a defect in the read.
- **`PlacementFile.Read` labels a CALLER-STATED unit as `Artwork` evidence** ("settled against the
  artwork's own extent"), which is not what happened. It is `BoardNetlistFile.ReadRecords`' own
  line, copied faithfully as R-rail2-3 asks, so the wart is that reader's and the fix is a member on
  the shared `BoardNetlistUnitsEvidence` rather than anything railRF owns. No caller passes `units`
  today, in the window or in the verb, so nothing prints the wrong sentence yet.
- **`PdnViaLimit.Describe` on the table branch** can say "…of plating, so there is no annulus to
  compute one from" while naming a defaulted plating thickness in the same clause. Both halves are
  true — the thickness is defaulted, which is exactly why there is no annulus worth computing over —
  but the sentence reads as a contradiction.

---

## railRF review round 2 — an unbounded `stackalloc` on input text (2026-09-18)

Review of briefs 9-16. `RailEsrDefaults.CanonicalClass` and its private `Squash` both sized a
`stackalloc char[...]` by the LENGTH OF THEIR ARGUMENT — a part library row's dielectric-class field
and a stackup layer's name. Both are file content, and a stack overflow is the one .NET failure a
`catch` cannot reach: a maintained table that lost a delimiter takes the process down with no
diagnostic, in a method whose whole job is to answer "is this string a class I recognise".

The house guard already existed one project over — `GerberMacro.cs` writes
`s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length]` — and both sites now carry it. A
dielectric class is four characters; the heap path never runs on well-formed input.

(`src/Render/DataDisplay/Models/WspTrace.cs:304` has the same unguarded shape and predates this
series. Reported rather than changed, since it is outside railRF's diff.)

## railRF: the board netlist was read and then dropped, so no refdes ever resolved (2026-09-19)

Found from the outside, on the shipped Power Rail example: the parts table listed thirteen
capacitors and the board showed none of them, and every port on the report read a coordinate in
DBU rather than `U1.VDD`.

**`RailBoardInputs.Pads` was assigned NOWHERE in `src/`.** The import dialog read a board netlist
into `RailRfViewModel.BoardNetlist` and used it for one thing — filling the net pick list — and
nothing ever turned its records into `PdnPad`s. Opening a `.crail` read no netlist at all, because
the document had no field to name one. The consequences were total and silent:

- Every source and load anchor had to be a **coordinate**. `RailPortAnchor` calls the coordinate
  form "the FALLBACK rather than the spelling", and it was the only form that could ever work.
- `PdnMountingLoopExtractor` — the whole of brief 13's `L_p + L_r − 2M + L_pad` — answered *"the
  board netlist has no pad for it"* for **every part on every board**, so every mounting inductance
  railRF has ever reported was a typed one and `RailMountingBasis.ComputedFromGeometry` was a value
  nothing in the application could produce.

Neither failed. Both degraded to the typed path, which is the path a document with no netlist takes,
so nothing on any report distinguished "there is no netlist" from "there is one and it was dropped".

What closed it: `PdnBoardPads` (the projection `PdnAttachments` had always claimed — *"`BoardNetlistRecord`
maps onto this directly"*), and three new document references — `BoardNetlistRef`, `PlacementRef`
and `ReferenceNet` — resolved by `RailArtwork` the way `PartLibraryRef` already was, so the window
and `circuitrf rail` land on the same files. **Persisting them is half the point**: an import that
read a netlist and was then saved used to lose it, and the reopened document reported typed
inductances with nothing to say a computed set had been available.

**The trap in the projection.** A surface-mount land carries no drill, so `BoardNetlistRecord.HasHole`
is false for it and `IsComponentHole` is false too. Testing `IsComponentHole` would have kept only
the through-hole parts, which on a modern board is close to none of them — the test is the component
reference and the pin.

## A plated through hole shorts every layer it passes, and DrcConnectivity joined only its two ends (2026-09-19)

`DrcConnectivity` read a via's span from the stackup (`SpanFromLayer`/`SpanToLayer`, correctly) and
then unioned the via with the copper on **those two conductors only**. Every conductor the barrel
passes *between* them was ignored.

On a four-layer board with a power pour on an inner layer fed by through vias — the ordinary
arrangement — the pour came back a **galvanically separate island**: not on the rail, carrying no
current, contributing nothing to the solve, while the picture showed it plainly connected to
everything.

It could not fail loudly either. `PdnRailRegions` reports extra islands as ORDINARY on imported
artwork ("the copper stops at every pad, so this is ordinary and not an error"), so the island count
went up by one and read as the thing that note is about.

The fix is that the **anti-pad is how artwork says which layers a barrel connects to**: copper up to
the barrel is a connection, a clearance round it is not. So the join now considers every conductor
between the two span ends and unions the ones the via actually touches. The "a via touching only one
side connects nothing yet" rule is unchanged — what widened is which conductors are candidates.

## The Smith gripper inverse: which plane the projection happens in, and where a pin lands (2026-09-19)

`brief-smith-3-gripper-inverse.md`. `SmithInverse` is closed form in every case and the round trip
is exact for every parameter in §4.3's table, but the brief's table leaves two things underspecified
and both of them are the difference between a drag that feels right and one that feels broken.

**1. The table is written for a series SRLC and a shunt PRLC, and the document allows both in
either placement.** `SmithComponentMap.AllowedPlacement` returns null for those two kinds, so a
shunt SRLC and a series PRLC are legal documents with no row in the table. The rule that covers all
of them: the required element immittance is EXACT in the placement's own additive form (Z_e = Z_d −
Z_in for series, Y_e = Y_d − Y_in for shunt), so inverting it to the element's own form loses
nothing, and the projection then happens where the parameter is LINEAR — Z for SRLC and Z1P, Y for
PRLC, and the placement's own form for the single-parameter R, L and C.

That last clause is not a simplification of the first. §4.3 spells a shunt R as
`1/R = Re(Y_d) − Re(Y_in)` rather than `R = Re(Z_d − Z_in)`, and the two are different numbers: the
reachable set of a shunt conductance is a horizontal ray in Y and nothing like a straight line in Z.
For the single-parameter kinds the placement's form IS where the locus is straight, so taking a real
or imaginary part there is a true orthogonal projection. For the composite kinds it is not — a shunt
SRLC's locus in Y is a circle — and the choice made here projects orthogonally in Z instead. Both
readings invert exactly for a REACHABLE drag; they differ only in where an unreachable one lands,
and this one is a one-liner per parameter rather than a circle fit.

**2. "Pins at the boundary" means two different places, and picking the wrong one is a visible
defect.** A parameter that enters LINEARLY (a series R, a series L, a shunt C, an SRLC's L) has a
reachable set running from zero outward, so a negative demand pins at ZERO. A parameter that enters
RECIPROCALLY (a shunt R, a series C, a shunt L, an SRLC's C, a PRLC's R and L) reaches its demand's
boundary only as the parameter goes to INFINITY — a shunt R reaches zero conductance at R = ∞ — so
there is no representable boundary to sit on and the current value is held instead, with the limit
named in the reason. Clamping that second class to zero would swing a near-open shunt R to a dead
short under a two-pixel move, in the direction opposite the one the drag went.

**Three smaller things.**

- **`tan θ = 0` is a threshold, not an equality.** A HALF-WAVE line transforms nothing and its
  tangent is 1.22e-16, not 0. Testing for exact zero sends that case into a quadratic whose leading
  coefficient is a rounding error; the guard is `|tan θ| < 1e-12` and it catches every multiple of a
  half wave, which is what "the drag is inert" actually means.
- **A stub's Z₀ needs no quadratic.** §4.3's complex quadratic is the TLIN's, because a line
  TRANSFORMS an impedance. A stub ADDS an admittance, so `Z₀ = tan θ/B` (open) and
  `Z₀ = −1/(B·tan θ)` (shorted) are one-liners — and the three line kinds share a `Parameters` row,
  so the Z₀ inverse would otherwise have been written once and been wrong for two of them.
- **The TLIN's length inverse needs the same unwrapping the stub's does.** R-smith3-3 names the stub
  only, but Γ' = Γ·e^(−2jθ) is periodic in θ with period π just as `atan` is, so reading the angle
  difference as a principal value jumps E by a whole 180°·F_ref/f. Both length inverses go through
  one `Length` helper for that reason. The period is 180°·F_ref/f and not 180°, because E is quoted
  at the element's own reference frequency while the rotation happens at f.

**What the gate could not see until it was made to.** `TheTlineZ0Quadratic_TakesThePhysicalRoot`
first passed against a fixture where the rejected root had a NEGATIVE real part — so the sign filter
alone decided, and replacing "nearest the current value" with "the first root that is positive" went
undetected. The fixture is now an input impedance where BOTH roots are positive-real and the
physical one is the second the quadratic hands back. Verified by mutation, along with the
placement split on C and the branch unwrapping; each of the three deliberate breaks fails the test
that claims to hold it.

Worth recording for whoever revisits the selection rule: preferring the root with the smallest
imaginary part would be more robust than proximity to the current value — the spurious root in that
fixture carries j46.7 where the physical one is exactly real — but §4.3 prescribes proximity, and a
drag whose current value has wandered far from the answer is the case where the two rules disagree.
Not changed.

## The constant-Q arcs and the swept band — five things the closed form turned up (2026-09-19)

`brief-smith-9-q-and-sweep.md`, `src/Design/Smith/SmithQArcs.cs` and `SmithBand.cs`.

**The in-disc range has to be EMITTED, and the design note's reason for not emitting it is wrong.**
§4.4 and the brief both say the arcs are drawn as the portion inside the unit disc because *"the
renderer's existing disc clip does it, and no arc-endpoint arithmetic is needed."* There is no such
clip on a trace. A Smith `Plot` clips its traces to the PLOT BOX (`PlotRenderer.ViewportClipRect`) and
deliberately not to the disc — `R-smith5-4`'s own rule is that a node outside the unit circle is
DRAWN, because clamping to the disc would be a lie about a stability result. Emitting the whole circle
would therefore have painted the other half of each branch straight across the chart. The range is
closed form anyway and costs one `atan`: on the inductive circle `|Γ|² = 1 − (2/Q)·v`, so the arc is
inside the disc exactly where `v > 0`, which is `θ ∈ [atan(1/Q), π − atan(1/Q)]` — and both ends land
on Γ = ±1 with no intersection to solve.

**Γ = ±1 are the one place |x|/r is not Q, and the gate says so rather than loosening.** Both branches
pass exactly through the open and the short, where `r` and `x` vanish or diverge together: the ratio's
LIMIT is Q and its value is 0/0. The gate asserts the invariant over the interior samples and pins the
two endpoints as a separate claim, which is the honest statement. Interior accuracy is a few times
1e-15 relative — the map forms `1 − u² − v²`, which on the circle equals `(2/Q)·v` and so cancels to a
few ulps of 1 near the ends.

**The capacitive branch is the inductive one CONJUGATED, not derived again.** "The two are mirror
images about the real axis" is then exactly true rather than true to 1e-16, and the gate can assert it
with no tolerance at all. Deriving the second circle from its own centre would have been a second
chance for them to stop being mirror images.

**The drag inverse never forms z.** `Q = |x|/r = |2v| / (1 − u² − v²)` — the `(1−u)² + v²` both halves
carry cancels, so the answer is two multiplies and a divide, and the denominator's SIGN is the sign of
r. A point on or outside the unit circle falls out as "no Q" rather than as a negative one, which is
what `R-smith9-2`'s `r_d ≤ 0` pin needs, and a purely resistive drag falls out as Q = 0, which is the
real axis itself rather than a pair of arcs. Both pin and neither returns an infinity or a NaN.

**A `.csmith` block written "only when the feature is ON" throws that feature's settings away — and it
did so WITHIN the session.** Brief 1 wrote `sweep` and `constantQ` only while enabled, on the reading
that absent means off. Absent has to mean *untouched*: the undo stack is a serialize/deserialize round
trip through that same writer, so unchecking the band's box and checking it again handed back the
defaults, with the user's own start, stop and point count gone and nothing said anywhere. The rule is
now `SmithDesignIo.IsDefault`, and a document nobody has touched still writes neither block, so no
existing file changes.

## Smith cascade — two defects from a review pass over the finished series (2026-09-19)

A read-through of briefs 1-11 after closeout, probing the evaluator from a throwaway test rather
than from the window. Both findings are in `src/Design/Smith/SmithCascade.cs` and its
`.Trajectories.cs` partial, both were reachable by typing an ordinary number into an ordinary
field, and both produced a picture that was wrong rather than an error that said so. Gated by two
new tests in `tests/Ui.Tests/Smith/SmithCascadeTests.cs`.

### 1. The walk was projective in the NODE and not in the ELEMENT, so a zero-valued part was a NaN

`SmithImmittance` states each kind's immittance in §3.3's own form — an impedance for R, L, SRLC
and Z1P, an admittance for C, PRLC and the two stubs — and `Step` reached for the other one through
`imm.Z` / `imm.Y`, which is a reciprocal. **`Complex.One / Complex.Zero` in .NET is `(NaN, NaN)` and
not an infinity**: the division algorithm forms `0/0` internally whichever branch it takes. So six
ordinary documents put a NaN into every node downstream of the element, the readout strip, the
grippers, the trajectories and the exported picture, with **no refusal anywhere** — `SmithElement.Refusal`
requires R, L and C to be non-NEGATIVE (brief 1, §94) and zero is not negative:

| the element | what it is | what it did |
|---|---|---|
| shunt R = 0, shunt L = 0 | a dead short across the line | NaN |
| series C = 0 | an open | NaN |
| SRLC with C = 0 | an open — `1/(ωC)` is ±∞ before any reciprocal is taken | NaN |
| PRLC with R = 0 or L = 0 | a short, likewise | NaN |

That the projective `Zp` pair exists **for exactly this class of degeneracy** is what makes it a
defect rather than a limitation: the file's own header says so about a quarter-wave open stub, and
the element half was simply never written that way. `SmithCascade.Add` now folds the reciprocal into
the pair — `Z' = (N·Y_e + D)/(D·Y_e)` and `Z' = N·Z_e/(D·Z_e + N)` — and both singular cases come out
exact rather than approximate: a zero admittance in series is `(D, 0)`, which is Γ = +1, and a zero
impedance in shunt is `(0, N)`, which is Γ = −1. SRLC and PRLC state themselves in the DUAL form when
their own reciprocal term is singular, which is one `?:` each and needs no new type.

`Zp.Value` and `SmithCascade.Gamma` had to learn the same lesson from the other side: a node handed
back as a plain `Complex` has been divided once, so an open reads as `(∞, ∞)` there and Γ of it is
the limit, +1. **A NaN still stays a NaN** — it is not an infinity and saying so would be a lie about
which of the two happened, which is `MatchValueFormat.NonFinite`'s own rule one project along.

**Refusing the six was considered and rejected.** It is smaller, and `SmithInverse`'s own
`Linear`/`Reciprocal` split names exactly the same six cases, so the rule already had a home. But a
dead short and an open are circuits, the brief says non-negative, and a tool whose whole premise is
dragging a value continuously should not refuse the end of the range it drags to.

### 2. A line a whole number of turns long was drawn as a DOT

The adaptive sampler seeded two points and bisected on chord error. The three line kinds are
**periodic in θ** — a `TLIN` rotates Γ by 2θ, a stub's susceptance has period π — so at E = 360°,
720° or 1080° the samples at t = 0, ½ and 1 are the same point. The first chord test measured an
error of zero, the loop stopped on it, and the whole trajectory was emitted as two coincident
points. Measured: **E = 180° gave 65 points and E = 360° gave 2.** Nothing reported anything; the
node either end of it was correct, so the picture simply had a move missing from it.

The existing comment on `ChordError`'s degenerate-segment fallback is what made it look covered — it
does rescue a SINGLE closed loop, where the midpoint is the antipode, and it cannot rescue an even
number of them.

**It is not an exotic document.** E is quoted at the element's own F_ref and scales with frequency,
so a 90° line quoted at 1 GHz is θ = 720° at 8 GHz — one edit of the design frequency away from any
quarter-wave stub.

`Sample` now takes a seed count and `Seed` derives it from |θ| for the three line kinds only: a
segment of θ ≤ 45° cannot close a period of π, and everything else keeps the two-point seed it has
always had, because a lumped element's Γ(t) is the Möbius image of a straight segment — an arc,
walked once, whose ends and midpoint can never coincide.

### 3. Five smaller things the same pass turned up

**A band had no upper point count, and the ceiling that matters is the DRAG's.** `SmithDesign.Refusal`
checked `Points < 2` and nothing above; `SmithBand.Evaluate` is one whole `SmithCascade.Evaluate` per
point and it runs inside every rebuild of the chart, which is every pointer move of a gripper drag.
So a number typed into a field had a value at which the window stopped responding, with nothing said.
`SmithSweep.MaxPoints` is 1,001 — a drawn locus on a chart a few hundred pixels across is already
finer than its pixels at that — and **both halves are needed**: the document refuses the count AND
`SmithBand` declines to walk it, because a refusal the window hangs before displaying is not a
refusal.

**The band's frequency grid existed twice.** `SmithBandResult` carried the two ends and `circuitrf
smith -o out.s1p` rebuilt the spacing from them to label its rows — a second copy of a rule that would
have agreed with `SmithBand` until one of them changed, and then differed silently in a file nobody
re-checks. The result carries `FrequencyHz` now and the verb writes what was walked.

**Two elements could be named `C1` and `c1`.** `SmithDesign.Refusal` compared ordinally while both
surfaces that MAKE names — `SmithElementFactory.NextName` and the strip's rename field — already
compared case-insensitively, so the document accepted a pair neither of them would ever produce. It
matters past tidiness because brief 7 copies these out as schematic instance names.

**A blank string was written where the model reads absent.** `SmithDesignIo`'s emptiness test was
`Length > 0` and `SmithElement.Refusal`'s is `IsNullOrWhiteSpace`, so a `FileRef` of `"   "` was told
it "names no file" and written out anyway. One rule now, at the writer.

**`SeriesImpedance`/`ShuntAdmittance` were dead** — no caller anywhere, in the product or the tests,
and not named by any brief — and they were the two wrappers that still took the reciprocal that
finding 1 is about. Deleted. `SmithImmittance.Z`/`.Y` stay, because they are how a caller reads the
form the element does not state itself in, and they now answer the limit rather than a NaN.

### 4. Q-17 closed: the strip's mismatch is against the chart's Z₀, not against conj(Z_gen)

Filed at closeout as an open owner decision (`docs/design/smith-chart.md` §12) and confirmed on this
pass: the shipped example reads VSWR 1.002 and the strip said **3.411 dB**, and the number got
*smaller* at the band edges where the match is worse. Owner's answer was **report it against
Z₀_chart**, so `SmithReadings.Of` now returns −10·log₁₀(1−|Γ|²) on the Γ it already computed —
**the same Γ the VSWR beside it is made of** — and the three rows read 0.17 / 0.00 / 0.22 dB.
`ConjugateMismatchDb` is `MismatchDb`; `--json`'s `conjugateMismatchDb` is `mismatchDb`.

**§3.4's conjugate-match target glyphs are unchanged and are not now decorative** — landing a
frequency's load point on its own ⊕ is still the conjugate match; what it no longer has is a column,
because the strip has no load impedance to state one against.

**And the number needed a formatter of its own.** `MatchValueFormat.Significant` keeps a fixed count
of MEANINGFUL digits, which is right for a component value spanning decades and wrong for a quantity
whose ideal value is zero: the perfect match came out `0.000004551 dB` beside a clean `1.002`.
`MatchValueFormat.Decibels` is two fixed places — a decibel has already had its decades taken out of
it — and both the strip and the verb spell it that way, which is why the helper is below the firewall
rather than in either of them.

## `PartLibraryIo` can now write and read WITHOUT its own refusal gate (2026-09-20)

`brief-railrf-24-part-library-editor.md` R-rail24-3a asks for an editor that saves a library with a
refused row in it, and the format's own reader and writer both refused one. Both grew a
`validate` parameter, defaulted to `true`, so every existing caller is unchanged.

**The reason the gate exists is still right and is unchanged for a RUN.** A library that cannot be
read back is a library that was never written, and `RailArtwork.ResolvePartLibrary` — the one door
every rail run reads a library through — still reads validated and still reports the refusal
sentence by name.

**What was wrong was applying it to the EDITOR.** The commonest refusal a real table produces is a
row added a second before its part number, which is a state a person passes through rather than one
they are in. Refusing the write there means the user cannot save; refusing the READ means they
cannot reopen what they were allowed to save; and the one tool that could fix the problem is the one
that will not show it to them. So the refusal travels with the file instead: stated on the row and
in the editor's strip, refused by name at the next run, repaired where it was made.

**The bytes are identical either way.** The flag gates the check and never the shape, so there is no
second spelling of `.crlib` and nothing to keep in step. Gate:
`tests/Ui.Tests/RailRf/PartLibraryEditorTests.cs` — the same file saved unedited is byte for byte
what it was, and a saved refused row comes back through the validated read as the sentence the
editor showed.
