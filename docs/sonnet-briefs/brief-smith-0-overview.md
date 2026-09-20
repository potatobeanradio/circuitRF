# Brief — Smith Chart: narrowband matching by hand — the series

**Status:** unstarted, briefs 1-11 · **Date:** 2026-09-19 · **Design note:** [`docs/design/smith-chart.md`](../design/smith-chart.md) rev 1
**Area:** `src/Design/Smith/`, `src/Ui/Smith/`, `src/Ui/Views/Smith/`, `src/Cli/`
**Requirement tag for the series:** `R-smith<n>-<m>`, scoped per brief (`R-smith3-5` is brief 3's fifth)

---

## 0. The short answer

A `.csmith` document holds a generator impedance and an ordered cascade of two-pin elements. The window
draws one Smith chart, one curve **per element** showing where that element takes the impedance, and a
gripper at every joint in the walk that drags the element it belongs to. The design note is the
specification and this series does not restate it; what this overview does is **fix the boundaries between
the briefs** and record what came out of reading the code rather than out of the note.

### The one rule the whole series is built on

> **This tool adds ONE evaluator and no second copy of anything else.**

Three corollaries, each of which is a gate somewhere below:

- **One Smith renderer.** The chart is a Data Display `Plot` in `PlotType.Smith` rendered by
  `PlotControl`. Grippers and the constant-Q arcs hang off a small overlay seam (brief 5). A second Smith
  renderer would drift from the first and the difference would be invisible until someone compared a
  screenshot with an export.
- **One clipboard path.** `SchematicClipboard.CopyAsync`/`PasteAsync` and
  `PlotExporter.CopyPlotToClipboardAsync` are *called*. **Brief 7 writes no clipboard code**, and the
  Windows `CF_ENHMETAFILE` single-P/Invoke-session work is inherited rather than repeated.
- **One component vocabulary.** Every element in §3.3 of the note is a `SymbolKind` that
  `ComponentTypeRegistry` already declares with its parameters and defaults. That is what makes brief 7's
  paste-out a circuit that really simulates, and it is what makes brief 2's gate possible at all.

That last corollary is the load-bearing one, so it gets its own sentence:

> **The evaluator is closed form and the engine is not, and that is exactly why the engine is its oracle.**
> Brief 2 builds the equivalent `.cnl` for every element type in both placements, runs the ordinary
> S-parameter analysis, converts S₁₁ back to an impedance and compares. The point is not catching an
> arithmetic slip. It is catching a **convention** slip — a sign, a port order, a reference impedance, a
> `tan` where a `cot` belongs — which is the class of error that produces a plausible picture.

---

## 1. Ten things that are not obvious, resolved here once

These came out of reading the code before writing the briefs. Each is restated where it bites.

### 1a. There is no new project, and the evaluator cannot live in `src/Engine`

The reference graph is `Core → Engine → Design → Render → Ui`, with `Cli` beside `Ui`. So:

- **`src/Engine` may not name a `SmithDesign`** or any other element record. The note already places the
  evaluator in `src/Design/Smith/` (§8); this is the reason, and it is the same reason railRF's mode
  eigensolve could not take a `Technology`.
- **`src/Design` already references `RfCore`**, which is what makes `TouchstoneIO`, `SnpInterpolator` and
  `RfHelpers` reachable for the S1P/S2P elements and the generator's `.s1p` import with no new reference.
- **No new `.csproj` is created below the firewall.** `src/Design/Smith/` is a folder in an already-gated
  project, so `tests/Firewall.Tests`' transitive no-Avalonia assertion covers it the moment the files
  land. That is brief 1's cheapest gate and it needs no new test.

### 1b. A document type has seven registration points, and three existing tests hold them shut

`.charm` and `.crail` are the precedent. `.csmith` has to appear in:

| # | Where | What breaks without it |
|---|---|---|
| 1 | `src/Ui/App.axaml.cs` — the `OpenFiles` dispatcher | A double-click launches circuitRF and opens nothing |
| 2 | `src/Ui/ViewModels/WorkspaceViewModel.cs` — the extension switch and an `OpenSmithPath` | The project tree's double-click and Open item do nothing |
| 3 | `WorkspaceScanner`'s `NodeKind` map | The file appears in the tree as a generic file, or not at all |
| 4 | `src/Ui/Assets/macOS/Info.plist` — `CFBundleTypeExtensions` | macOS does not associate the type |
| 5 | `packaging/windows/circuitRF.wxs` | Windows does not associate the type |
| 6 | `packaging/linux/circuitrf-mime.xml` | Linux does not associate the type |
| 7 | `src/Cli/DocumentKinds.cs` — `Classify` | `check`, `render` and `find` call a `.csmith` unreadable |

**Three tests already compare those lists against each other** and will fail on a partial job:
`tests/Ui.Tests/WBondStandaloneTests.cs` (the plist's extensions ↔ `App.OpenFiles`, and
`circuitrf-mime.xml`'s globs ↔ `App.OpenFiles`), `tests/Ui.Tests/AsyncDocumentOpenRoutingTests.cs` (the
routing, source-scanned) and `tests/Ui.Tests/WorkspaceUserSidecarTests.cs` (the mime file). **Brief 1
does all seven in one change and runs those three.** A type declared to the operating system with no case
in the dispatcher reads to a user as a broken file, and nothing reports it.

### 1c. The mirror is a flag and a sign, not geometry work

`EditableComponent.MirrorX` and `SchematicComponent.MirrorX` both exist, and
`SchematicGeometry.LocalToWorld(lx, ly, X, Y, Rotation, MirrorX)` already honours the flag **for pin
coordinates as well as for the glyph**. So brief 6's mirror button negates the direction the projection's
x-cursor advances and sets `MirrorX = true` on each component. Nothing else.

### 1d. `PlotControl` has no overlay seam yet, and the shape the new one should take is already there

The control exposes four `Func<>` hooks — `NextMarkerIndexProvider`, `FindMarkerInfoBoxVmProvider`,
`ContainerProvider`, `SelectedMarkersProvider`. **Brief 5's seam follows that established shape** (a draw
callback in canvas space, a hit-test returning a handle, press/move/release) rather than inventing a
control-extension mechanism this codebase does not otherwise use.

### 1e. The trap that will otherwise be found last: a `PlotControl` with no container copies NOTHING

`PlotExporter.CopyPlotToClipboardAsync` opens with:

```csharp
if (container is null) return;
```

and `PlotControl` obtains its container from `ContainerProvider?.Invoke()`. So a `PlotControl` hosted in a
bespoke window **without** a `PlotContainerViewModel` produces no clipboard content, raises nothing, and
looks exactly like a successful copy. railRF sets `plot.ContainerProvider = () => container` in
`RailRfWindow.axaml.cs`; **brief 5 must do the same, and brief 7's gate must assert real bytes on the
clipboard rather than that the call was made.**

### 1f. `LaunchAction` is an ordinal and its combobox is a positional string array

`AppPreferences.LaunchAction` is serialized as a number, and `SettingsView.LoadGeneralPrefs` fills
`LaunchActionCombo.ItemsSource` from a bare `string[]` whose **index is cast directly to the enum**. The
file already carries the warning. So `NewSmithChart` is **appended** to the enum, `"Smith Chart"` is
**appended** to the array, and the two are edited in the same change — brief 4, `R-smith4-10`, with a test
that asserts the array's length and order against the enum so the next person cannot get it wrong quietly.

### 1g. Every element already exists, and no new `ComponentModel` is needed anywhere in this series

`ComponentTypeRegistry` declares all of them with parameters, defaults, units and search terms:
`Resistor`/`Inductor`/`Capacitor`, `Srlc` and `Prlc` (`R`, `L`, `C`), `ZPort` (`NumPorts=1` → `Z[1,1]`),
`Snp` (`NumPorts` 1 or 2 → `File`), `Tline` (`Z`, `E`, `F`). The engine components behind them —
`SeriesRlcModel`, `ParallelRlcModel`, `ZPortModel`, `SnpModel`, `TLineModel` — are all built.

**This is a scope statement, not a convenience.** A new device type would need a factory registration and
a golden-reference test, and this tool needs neither. A `.csmith` element that cannot be spelled as an
existing component is an element that does not go in.

### 1h. Projecting a non-editable ladder onto the schematic renderer is a solved problem

`MatchSchematicModel` builds the Designer's ladder pane and `MatchSchematicCopy` projects the same layout
onto `EditableComponent`/`EditableWire` for the clipboard — including the two findings baked into it (one
ground per *column* under its lowest shunt symbol, and spine wires in the **gaps** between series bodies
because a built-in glyph carries its own leads). `SchematicRenderer.Draw` takes a `SchematicModel`, an
optional `SchematicSpatialIndex`, a theme and an optional `SchematicOverlay`.

**Briefs 6 and 7 build the model and the projection. Neither touches the renderer.**

### 1i. `InlineEditText` has two hostings, and this window needs only the easy one

Its own remarks name them: the Match Designer's rows **swap the box in place** because a row is a fixed
grid cell, while harmonicaRF's readout strip **floats** its box in a `Canvas` overlay because its columns
are width-shared and an in-place box would shove every column sideways. The generator table is the only
grid of cells in this window and its three columns are fixed-width by construction, so it takes the
in-place hosting. **Nothing in this series needs the floating overlay**, which is the more delicate of the
two.

### 1j. An S-parameter file is fitted once, not once per sample

`SnpInterpolator` behind `TouchstoneCache` already exists. The S-parameter engine's own defect of
re-fitting its splines at every frequency point is recorded in `src/Engine/RESOLVED.md`; here the same
mistake would land the cost **inside a drag**, which is the one place in this tool with a budget.

---

## 2. The briefs

| Brief | Phase | What it delivers | Gate |
|---|---|---|---|
| [1 — the document](brief-smith-1-document.md) | P1 | `SmithDesign`, the element records, `SmithDesignIo` (`.csmith`), `SmithClipboard`, the seven registration points. No arithmetic, no UI. | `SmithDocumentTests.cs` + the three registration parity tests |
| [2 — the cascade and the trajectories](brief-smith-2-cascade.md) | P1 | `SmithCascade`, every element immittance, the trajectory sampler, Touchstone elements, generator interpolation and its refusal. | `SmithCascadeTests.cs` — **the engine oracle**, every element, both placements |
| [3 — the gripper inverse](brief-smith-3-gripper-inverse.md) | P1 | Every closed-form inverse, the TLIN Z₀ quadratic, the stub branch unwrap, physicality pinning. | `SmithInverseTests.cs` — drag → value → re-evaluate → land on the drag point |
| [4 — the document and its window](brief-smith-4-document-window.md) | P1 | `SmithChartDocument`, the view model, the chrome, `InlineEditText` everywhere, the generator panel with `.s1p` import and Conjugate, Tools ▸ Smith Chart, the On Launch row. | `SmithWindowTests.cs` + the `LaunchAction` ordinal test |
| [5 — the chart and the overlay seam](brief-smith-5-chart.md) | P1 | The `Plot` built from the evaluator, `PlotControl` hosting **and its container**, the gripper overlay seam, the drag loop and its undo contract, the load points, their labels and the conjugate targets. | `SmithChartTests.cs` |
| [6 — the network strip](brief-smith-6-network-strip.md) | P1 | The projection onto `SchematicRenderer`, selection, add/insert/delete/reorder, the sliders, the active-parameter rule, **the mirror button**. | `SmithNetworkStripTests.cs` — mirroring is view-only |
| [7 — the clipboard, both ways](brief-smith-7-clipboard.md) | P1 | Copy the network, copy the chart, paste a `.csch`, the topology recognizer and its five refusals, the mirror-aware end rule. | `SmithClipboardTests.cs` — real bytes, and every refusal fires |
| [8 — overlays and markers](brief-smith-8-overlays-markers.md) | P2 | Touchstone and cube sources, renormalization to Z₀_chart, derived stability circles, markers and their VSWR circles. | `SmithOverlayTests.cs` |
| [9 — constant Q and the swept band](brief-smith-9-q-and-sweep.md) | P2 | The constant-Q circle pair, its drag inverse and the shift quarter-step; the optional swept band and its clamp. | `SmithConstantQTests.cs` — `\|x\|/r = Q` on every drawn sample |
| [10 — the `smith` CLI verb](brief-smith-10-cli-verb.md) | P3 | `circuitrf smith`, per `docs/design/cli.md`. `--set`, `-o out.s1p`, the picture through `render`. | `SmithCliVerbTests.cs` — byte identity against the in-process call |
| [11 — docs, example, closeout](brief-smith-11-docs-and-example.md) | — | The user chapter, the figures, an example `.csmith` in an example workspace, and the design note's own status. | DocGen, reported |
| [12 — overlays via the inspector](brief-smith-12-overlays-via-the-inspector.md) | post-ship | **Replaces brief 8's Overlays panel.** Reference data is added through Plot Properties, as on any Data Display Smith chart; a user trace is persisted as a `TraceConfig` and survives the rebuild. | `SmithOverlayTests.cs`, extended |

### Dependency order

```
1 ──┬── 2 ── 3 ──┬── 5 ──┬── 7 ── 8 ── 9
    │            │       │
    └── 4 ───────┴── 6 ──┘
                                 10 (after 2)        11 (last)
```

- **1 blocks everything.** It is the document every other brief reads and writes.
- **2 blocks 3**, because an inverse is only checkable against a forward evaluation that exists.
- **4 blocks 5 and 6.** The document window hosts both panes.
- **5 and 6 both block 7**, which copies out of one and pastes into the other.
- **10 needs 2 only**, not the window — that is the point of the CLI, and it is what §9 of the note means
  by *"the arithmetic lives below the firewall from day one, so P3 is wiring."*
- **8 and 9 are independent of each other** and can be taken in either order.
- **12 replaces 8's panel** and is post-ship. Brief 8 still stands for everything else it decided — the
  renormalization requirement, the reference-not-copy rule, the markers and their VSWR circles; what 12
  takes away is the side panel it built to pick a source.

---

## 3. What this series does NOT do

Restated from §2.2 of the note, because a scope boundary that lives only in a design document is a
boundary nobody reads at implementation time.

- **No synthesis, no optimiser, no goal, no error function.** Nothing here computes a network. That is the
  Match Designer's job and a second copy of it would be worse than the first.
- **No power, no dBm, no gain.** The generator has an impedance and no available power. Every quantity in
  this tool is a linear immittance.
- **No branching, no hierarchy, no sub-cells.** One cascade, ground on the shunt side. This is not a
  simplification to be relaxed later — it is what makes a per-element trajectory *mean* something.
- **No new `ComponentModel`, no new analysis directive, no new result type.** §1g.
- **No second Smith renderer, no second clipboard path, no second Touchstone interpretation.** §0.
- **No standalone binary.** This is a document type; §5.1 of the note.
- **No layout, no physical length.** A TLIN is an ideal line. `MLIN` and the microstrip family are a
  schematic's business.

---

## 4. Standing rules for every brief in this series

1. **Base SI in the document, with the scale nowhere near the number.** Hertz, henries, farads, ohms. The
   one deliberate exception is electrical length, stored in degrees in a field named `…Deg`. The 2 Hz
   sweep recorded in `src/Engine/RESOLVED.md` is the standing example of what this prevents.
2. **A refusal names the thing that answers it** — the instance, the parameter, the file, the table span.
   The house spelling is set by `convert` and `em`; follow it exactly. A refusal whose text does not
   identify the offending object is not a refusal anyone can act on.
3. **Every editable value is an `InlineEditText`.** Not most; every one. §1i, and §5.3 of the note for the
   list and the three-key contract.
4. **One gesture is one undo entry.** A slider drag, a gripper drag, a Q drag, a paste, a mirror toggle.
   The entry is pushed on *release* carrying the before-value captured on *press*. The Match Designer's
   slider write-back defect — every undo *adding* an entry, so eight edits took fourteen undos — is the
   regression this rule exists to prevent, and brief 5 gates it by counting.
5. **No timing tests.** Assert a counter or a structural property, never wall clock. Nothing in this
   series should carry `[Trait("Category", "Benchmark")]`; it is all arithmetic and file I/O.
6. **The firewall.** `src/Design`, `src/Render` and `src/Cli` reference no UI framework.
   `tests/Firewall.Tests` fails the build otherwise and is instant to run.

---

## 5. Build and test

Everything in this series is reachable from `tests/Ui.Tests` and `tests/Firewall.Tests` — including
`src/Design/Smith`, because `src/Design`'s existing tests already live in `tests/Ui.Tests`.

```
dotnet test tests/Ui.Tests       --no-build --filter "FullyQualifiedName~Smith"
dotnet test tests/Firewall.Tests --no-build
```

One project path per invocation — this SDK's `dotnet test` rejects two (`MSB1008`). Add `--no-build`
after the first build of a session.

**Do not run the full solution suite for this work.** Nothing in briefs 1-11 changes `src/Core`,
`src/Engine` or `RfCore`; the root run is minutes of wall clock and its likely failures are known flakes.
**Read `tests/Ui.Tests/TestResults/last-run.trx` for failures rather than re-running** — it holds every
test's name, outcome, duration, failure message and captured stdout, so a diagnostic line is readable
without a second run.

Brief 2's oracle is the one exception worth naming: it runs the real `SParameterEngine`, so it reaches
`src/Engine` at **runtime** without changing it. It is still a `tests/Ui.Tests` test and still fast.

**On completion of each brief:** record findings in the `RESOLVED.md` beside the code —
`src/Design/RESOLVED.md` for briefs 1-3, `src/Ui/RESOLVED.md` for 4-9, `src/Cli/RESOLVED.md` for 10.
Create one where none exists. **Never write findings into a `CLAUDE.md`.**
