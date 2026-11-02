# Brief — harmonicaRF H8: Set DUT, the standalone binary, and packaging

**Read first:** `docs/design/harmonicarf.md` (**§3.1–§3.2**, **§4.3**, **§4.5.5**, **§7.6**, **§8.1**,
**§10**), then `src/Harmonica/CLAUDE.md` and `src/Ui/CLAUDE.md`'s **H7** entry. H0–H3 built the
headless engine; H4–H5 the document, panels, pool and scheduler; H6 the gesture and the inverse solve;
H7 the menus, the §7.5 inputs, the trace picker, Edit Display, interchange and the colour editor.

**H8 is the phase where harmonicaRF stops being a tab inside circuitRF and becomes an instrument you
can hand someone.** Two halves, and they are independent: making the DUT changeable (without which a
standalone app is a demo of one transistor), and making the app exist.

**Read §0.2 before scheduling anything.** Binary size is a CLOSED question — measured, ruled on by the
owner, and not this phase's work. §0.2 exists so nobody re-opens it.

---

## 0. What already exists, and what genuinely does not

**Do not rebuild any of this.** If something below seems missing, it is a lookup you have not found —
ask before writing a second one.

| you need | it is here | notes |
|---|---|---|
| the app's entry point | `src/Ui/Program.cs` (`Program.Main`, `BuildAvaloniaApp`) | Windows single-instance pipe lives here |
| the `Application` | `src/Ui/App.axaml(.cs)` | styles, ProcessExit cleanup, launch action, `WorkspaceWindow` |
| the ColorPicker Fluent include | `App.axaml` line ~165 | **`…/Themes/Fluent/Fluent.xaml`** — note `.xaml`, not `.axaml` |
| macOS bundling | `src/Ui/bundleForMacOS.sh`, `Assets/macOS/Info.plist`, `Entitlements.plist` | `.crfw` document type + `com.circuitrf.crfw` UTI |
| Windows/macOS icons + publish settings | `src/Ui/CircuitRF.Ui.csproj` | `PublishSingleFile`, `ApplicationIcon`, `MacOSXBundle*` |
| the harmonicaRF document + view + menus | `HarmonicaDocument`, `HarmonicaView`, `HarmonicaMenuView` | H4–H7; the menu already attaches its `NativeMenu` when the host window is not a `WorkspaceWindow` |
| `.charm` open / save | `HarmonicaView.OpenCharmAsync` / `SaveCharmAsync` | H7; needs no workspace (§1.2) |
| the model's declared parameters | `HarmonicaInputs.DeclaredModelParameters` | H7 / R-h7-4 — reads the model, never a table |
| a built-in's declared parameter set | `ComponentTypeRegistry.DefaultParameters(kind, portCount)` | the same declaration the schematic editor renders |
| engine type name ↔ symbol | `ComponentTypeRegistry.EngineReference` | H7 inverts it; do not write a second map |
| external-device descriptors | `ExternalDeviceRegistry`, `ExternalDeviceDescriptor` | `ExternalParamDescriptor`, `ExternalNodeDescriptor` |
| a bare `.osdi` by path | `VerilogAFileResolver` (`Prefix = "VerilogA|"`) | resolves with **no** kit manifest |
| a kit's worker, with NO workspace | `DeviceWorkerProviderResolver(searchRoots)` | folder paths only; `src/Cli`'s `--kits <dir>` already ships this — see R-h8-4 |
| copy a plot to the clipboard | `PlotExporter.CopyPlotToClipboardAsync` | PDF / SVG / JSON / bitmap |
| export a `DataSet` | `DataExporterViewModel`, `DataExporterDialog` | `.mat` / `.npy` / `.txt` |
| a parameter-editing dialog | `Views/Dialogs/ParameterEditorDialog` | and `CellParameterEditorView` for the richer shape |
| the intrinsic-plane mapping for an external model | `DutSpec.IntrinsicMapping` (`GateNode`, `DrainNode`, `SourcePin`) | §4.5.5 — deliberately **not** defaulted |

**Genuinely does not exist yet, and is this phase's work:** any way to change the DUT at all (File ▸
*Set DUT…* is a menu entry with no hook); a second entry point or `Application`; any harmonicaRF
window that is not a Dock document; `.charm` file association or double-click open on either binary;
harmonicaRF icons, bundle identity or a publish path; and four §7.6 menu entries H7 left deliberately
unwired — *Export Data*, *Copy Plot*, *Help*, and *Set DUT…* itself.

### 0.1 What H7 left, stated so it is not rediscovered

- **`SetDutHook`, `ExportDataHook`, `CopyPlotHook`, `HelpHook` are null.** They are present on
  `HarmonicaMenuViewModel` and unwired, so their menu items exist and do nothing. That was deliberate
  — an unwired hook is honest where a faked implementation is not — but it is a debt this phase pays.
- **While a custom Γ grid is installed, §6.8's `CoarseGrid` rung is a no-op.** Not a defect; recorded
  in `src/Ui/CLAUDE.md`. Do not "fix" it by thinning a grid the user imported.
- **Open item 6 is still open** — whether a `.charm` inside an open workspace appears in the project
  tree like `.cdd`/`.clay`. M4 settles it.

### 0.2 Binary size — CLOSED, do not re-open

**Owner's ruling, 2026-08-07: harmonicaRF's file size is not worth working on.** This section records
why, so the question is not asked a third time.

The worry route **(b)** invites is that a build-configuration standalone carries the layout editor,
DRC, the EM kernels and the PDK importer — and that "standalone" ought to mean "without them".
Measured, `dotnet publish -c Release -r osx-arm64`, self-contained single-file:

| | size | share |
|---|---|---|
| **published output, total** | **138 MB** | |
| ├ `CircuitRF.Ui` single-file bundle | 118.5 MB | 86% |
| ├ `libSkiaSharp.dylib` | 14.5 MB | 11% |
| └ HarfBuzz + AvaloniaNative | 4.3 MB | 3% |

Inside the ~20 MB `CircuitRF.Ui.dll`:

| | size | note |
|---|---|---|
| embedded fonts (`Assets/Fonts`, 51 `.ttf`) | 14.5 MB | code names only 9 of them |
| all our IL, every subsystem | ~5 MB | |
| ├ the layout editor (`src/Ui/Layout`, 39.8k lines) | ~1.3 MB | **0.9% of the bundle** |
| └ the MoM engine (`src/Engine/Mom`, 17.3k lines) | ~0.35 MB | **0.25% of the bundle** |
| .NET runtime + Avalonia + Dock + deps | ~95 MB | the floor; no source split touches it |

**Excluding the layout editor AND the MoM engine would save ~1.6 MB of 138 — about 1.2%.** Route (c)
(lifting the display layer into `src/Display`) cannot do much better, because the ~95 MB runtime floor
dominates and no project split reaches it.

The only levers with real leverage — narrowing the font glob (~10.8 MB, and it would cost circuitRF
nothing to do too) and `PublishTrimmed` (which is the only thing aimed at the floor) — are **out of
scope by the owner's ruling**: too much work for too little improvement. Do not schedule them, do not
schedule (c) on size grounds, and do not spend a benchmark measuring any of it.

**What this means for the rest of the brief:** the standalone binary is the same assembly with a
different `Main`, it is the same size as circuitRF, and that is the accepted outcome of §3.1's route
(b). Nothing below tries to change it.

---

## 1. Scope

Five milestones, in this order. **M1 and M2 are each independently useful and each a legitimate
stopping point** — M1 makes harmonicaRF a tool for more than one transistor, and M2 makes it an app.

1. **Set DUT** (§4.3, §4.5.5). Choose and configure the device: an SDD's equations, one of the five
   native FET laws, a bare `.osdi`, or a kit part. Plus the intrinsic mapping an external model needs.
2. **The standalone entry point** (§3.1). A second `Main`, a second `Application`, a window that is not
   a Dock document, and the style includes that fail silently if missed.
3. **Packaging and identity** (§3.1). Icons, bundle identity, `.charm` association and double-click
   open — on **both** binaries.
4. **The four unwired hooks, and open item 6.** *Export Data*, *Copy Plot*, *Help*, and whether a
   `.charm` in a workspace appears in the project tree.
5. **The phase gate** (§9, §10). One `.charm`, two binaries, the same numbers.

---

## 2. M1 — Set DUT

### R-h8-1 — the dialog EDITS `DutSpec`, and every kind is the same edit

`CircuitModel.Dut` is one value object with four kinds (§4.3). The dialog produces a new `DutSpec` and
hands it to the same write-back H7 already built (`HarmonicaViewModel.ApplyInput`'s structural path,
or a sibling of it) — **it must not reach into `HarmonicaContext`, `TerminationSet` or the scheduler
itself**. Changing the DUT moves `StructuralKey`, so the ladder resets and the context rebuilds; that
is R-h7-3's mechanism and it already works.

### R-h8-2 — the parameter list is READ from the model, and H7 already has the reader

`HarmonicaInputs.DeclaredModelParameters` is the one place that answers "what does this model
declare". **Extend it if a kind is missing; do not write a second reader in the dialog.** An SDD
declares equations; a native FET declares `ComponentTypeRegistry.DefaultParameters` for *that law*; an
external model declares `ExternalParamDescriptor`s. A hardcoded list of plausible-looking parameters
is worse than none (R-h7-4, unchanged).

### R-h8-3 — the intrinsic mapping is ASKED FOR, never guessed

§4.5.5: nothing can guess which internal node of an external model is the intrinsic drain, and
`DutSpec.IntrinsicMapping` is deliberately not defaulted. So an external DUT with no mapping must draw
the intrinsic panels **empty** rather than with a plausible-looking wrong answer, and the dialog must
offer the model's own declared node names (`ExternalNodeDescriptor.Label`) to choose from. **A
defaulted mapping is the single most expensive bug this dialog could ship** — it produces glyphs that
look right and are not.

### R-h8-4 — a kit part does NOT need a workspace, and the CLI already proves it

`DeviceWorkerProviderResolver` has **two constructors**, and only one of them is the workspace's:

- `DeviceWorkerProviderResolver(IEnumerable<string> searchRoots)` — plain folder paths, each searched
  for a manifest "directly inside it and one level down, which is how kits are laid out". **No
  workspace, no project, no configuration.**
- `DeviceWorkerProviderResolver(IEnumerable<(string Kit, DeviceWorkerManifest)> known)` — manifests
  already in hand. This is the one `WorkspaceViewModel` uses, "the shape a workspace uses when it
  records its kits' settled settings itself" (its own doc comment). A *convenience*, not a requirement.

**`src/Cli/Program.cs` already ships the first form**: `--kits <dir>` →
`ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver(kitFolders))`, with its own
comment saying it "makes externally-provided devices work headlessly, the same way opening a workspace
does in the GUI". The CLI has no workspace and resolves kit devices.

So harmonicaRF standalone needs **a kit-folder list, which is a preference — not a workspace, and
certainly not a synthesised one.** Add it to `AppPreferences` beside the existing PCell-trust and
theme entries, seed it from any folder a `.charm` was opened from if that is cheap, and let *Set DUT…*
browse to one.

> **Do not create an in-memory workspace to get at this.** A `WorkspaceViewModel` drags in the project
> tree, the dock layout, technologies, PCell resolvers and the launch action — every one of which would
> then need a headless answer, and none of which the device path actually reads. The dependency being
> worked around does not exist.

The failure message must still be good: `DeviceWorkerProviderResolver.Describe` already words the
empty case as what it *means* rather than what it is, and its current text names a workspace. Give it
the standalone's wording too, or a user with no kit folders configured gets told about a workspace
that this build does not have.

### Gate for M1

- Switching an SDD → `FET_Angelov` rebuilds the context **exactly once**, resets the ladder exactly
  once, and produces a different §7.5 input list — counters, not clocks.
- The new DUT's parameters round-trip through the `.charm` and come back as the model's own
  (§8.1: an SDD or built-in is **embedded whole**; an `.osdi` or kit part is a **reference**).
- An external DUT with no `IntrinsicMapping` draws the intrinsic glyph panels empty and the readouts
  say why — asserted, because "empty" and "broken" look identical otherwise.
- The five FET laws produce five different parameter lists, and none is the SDD's.

---

## 3. M2 — the standalone entry point

### R-h8-5 — TWO `Main`s in one assembly needs an explicit `StartupObject` in BOTH configurations

`src/Ui` sets `TreatWarningsAsErrors`, so a second entry point is CS0017 (multiple entry points) the
moment it compiles. `<StartupObject>` selected by an MSBuild property is the mechanism §3.1 means by
"a build configuration". **Set it explicitly for the default build too** — relying on "there is only
one `Main` today" is what breaks the moment the second one lands.

### R-h8-6 — the standalone `Application` MUST carry every style include harmonicaRF reaches, and one of them fails SILENTLY

§7.9.4 names it and H7 hit it from the other side: `ColorView`'s template comes from
`avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml` — **`.xaml`, not `.axaml`**, which
is the actual embedded resource name. Omit it and the colour editor renders as an empty box **with no
error**. The `FluentTheme` and every `CrfTileBorderBrush`-style application resource harmonicaRF's own
views bind to are in the same category.

**Gate it by construction, not by inspection:** assert that the standalone `Application`'s style/resource
set is a **superset** of what harmonicaRF's own views reference. A test that merely greps for one
`StyleInclude` will pass the day someone adds a second dependency.

### R-h8-7 — what the standalone must NOT do, and what it must STILL do

Not: create a `WorkspaceWindow` or a `WorkspaceViewModel`, run `ApplyLaunchSettings`, register
`ProcessTechnologyRecognizers`, or handle `.crfw`. There is no workspace (§1.2).

Still: the two `ProcessExit` cleanups in `App.OnFrameworkInitializationCompleted` —
`ExternalDeviceRegistry.ResetResolved()` and `PCellRegistry.ClearResolvers()`. **The first one is not
optional and the reason is written down in that method's own comment**: a leaked device worker on
macOS holds a VM slot indefinitely and the *next* run dies with a broken pipe and no worker output.
harmonicaRF can hold an external DUT, so it can leak one. Also still: `ThemeResolver.SetBuiltInProvider`
and the saved theme preference, or every `.ccolor` the user has resolves to nothing.

### R-h8-8 — the shell window is a plain `Window`, and H7's menu already knows

`HarmonicaMenuView` attaches its `NativeMenu` to the hosting window when that window is not a
`WorkspaceWindow`, so a standalone shell gets the macOS menu bar with no new mechanism. **Assert that
positively** — the check is currently a type-NAME comparison (deliberately, so the view takes no
dependency on the workspace shell, which does not exist in this build), and a rename would silently
turn it off.

Dock is not needed for a single-document app. If more than one `.charm` should be open at once, say
which you chose and why — tabs, or one window per document.

### Gate for M2

- The standalone binary launches, draws the four §7.1 panels and solves the default document, with
  **no workspace, no project tree and no launch action**.
- Opening the colour editor in the standalone shows a populated `ColorView` — the §7.9.4 gotcha, tested
  rather than remembered.
- Quitting the standalone with an external DUT loaded leaves **no worker process running** — measured
  by process enumeration, not by inspecting the code path.
- Both `Main`s build under `TreatWarningsAsErrors` with no entry-point warning suppressed.

---

## 4. M3 — packaging and identity

### R-h8-9 — harmonicaRF is a DIFFERENT application, and macOS will not tolerate it pretending otherwise

Its own `CFBundleIdentifier`, `CFBundleName`, `CFBundleExecutable` and icon, and its own
`Entitlements.plist` if the signing arguments differ. `Info.plist`'s existing comment already records
that `CFBundleIdentifier` must match `BUNDLE_ID` in `bundleForMacOS.sh` **and** the `--entitlements`
arg to `codesign` — three places, and they are not derived from one another. If that is worth fixing,
fix it once for both apps rather than duplicating the trap.

### R-h8-10 — `.charm` gets a UTI and a document type on BOTH binaries

circuitRF opens `.charm` documents too (Tools ▸ harmonicaRF is not going away). So the association is
declared twice, and **double-click must open the file, not merely launch the app** — the existing
`.crfw` path (`IActivatableLifetime.Activated` on macOS, argv on Windows/Linux, the named pipe for a
second Windows instance) is the mechanism, and `App.OnActivated`/`HandleFilesInternal` are **stubs
today** that ignore the paths they are handed. Wiring `.charm` through them means finishing that path;
say plainly whether you also finished it for `.crfw` or left it stubbed.

### Gate for M3

- `dotnet publish` produces a runnable harmonicaRF for one RID on this machine; the command is recorded
  in the repo, not in a shell history.
- Double-clicking a `.charm` opens **that document** in whichever binary is associated — verified by
  the file's own contents appearing, not by the app launching.
- The two apps' bundle identifiers differ, and neither's icon is the other's.

---

## 5. M4 — the four unwired hooks, and open item 6

### R-h8-11 — *Copy Plot* and *Export Data* go through the EXISTING exporters

`PlotExporter.CopyPlotToClipboardAsync` (PDF/SVG/JSON/bitmap) and `DataExporterViewModel`. §7.8 says
"Not reinvented" about the first and it applies to the second. *Export Data* exports the frame's own
published `DataSet` — R-h7-6's, the one the panels drew from, not a re-solve.

**Copy Plot needs to know WHICH panel.** harmonicaRF's canvas is one Skia surface with five or more
panels on it; the `.cdd` path copies a `PlotContainerViewModel`. Say which you chose — the panel under
the pointer, a selected panel, or the whole canvas — and why.

### R-h8-12 — open item 6, settled

Whether a `.charm` inside an open workspace appears in the project tree like `.cdd`/`.clay`. The design
note proposes **it appears, matching every other document type, but is not required to live in one**.
Implement that unless the tree's own conventions make it wrong, and either way write the answer into
§11 so the item stops being open.

### Gate for M4

- Every §7.6 menu entry either does something or is **absent**. No dead entries.
- *Export Data* writes a file a `.cdd` can open, containing the cubes §5 publishes.
- A `.charm` saved into an open workspace appears in the tree without a reload (or does not, with the
  decision recorded).

---

## 6. M5 — the phase gate

### R-h8-13 — the end-to-end statement

One test, through the product path, that a `.charm` written by the standalone binary opens in
circuitRF's Tools ▸ harmonicaRF and re-solves to the same numbers — and the reverse. §8's "a `.charm`
is self-describing" is the claim; two binaries reading one file is the test of it.

---

## 7. Standing constraints (violating any of these is a bug, not a style choice)

- **`src/Harmonica` references no Avalonia.** `tests/Firewall.Tests` enforces it. **The standalone
  binary does not weaken it** (§3.2) — it is `src/Ui` with a different `Main`. If a firewall test needs
  a new assembly name in its list, that is the only change it should need.
- **The UI thread never solves.** Everything goes through `SolvePool`; the view publishes.
- **Tier A never degrades.** `FramePlan.IncludesTierA` is true on every rung.
- **harmonicaRF never fills contours** — owner ruling. Do not add a fill path, a setting, or a
  benchmark for one.
- **Never `PlotRenderer.BuildTransforms` on a harmonicaRF Smith panel.** `GammaToCanvas` /
  `CanvasToGamma` for Γ, `MarkerToCanvas` / `CanvasToMarker` for anything on the compressed radial
  scale.
- **No new physics.** Every number H8 displays already exists in the §5 `DataSet`.
- **Do not touch `src/Engine`, `src/Core` or `src/RfCore`.** If you think you need to, stop and report.
  (M1 reads `ExternalDeviceDescriptor` and `ComponentTypeRegistry`; both are reads.)
- **`.charm` reads stay forward-compatible.** A file written by H4–H7 must open unchanged, and an
  untouched document must still re-serialise byte-for-byte.
- **The readout strip's input row is updated IN PLACE, never rebuilt** (H7). If M1 adds inputs, keep
  that: rebuilding destroys the `TextBox` the user is typing in.

---

## 8. Cost discipline

Any test at or above ~5 s carries `[Trait("Category","Benchmark")]`, lives in a non-parallel collection
(`HarmonicaBenchmarks` in `tests/Harmonica.Tests`, `HarmonicaUiBenchmarks` in `tests/Ui.Tests`), takes a
**best-of-N minimum** (not a mean, not a median — this repo has been bitten three times), and every
reported number is measured **alone**. **H8 is expected to add NO benchmark methods** — §0.2 closed the
only measurement this phase might have wanted, and everything else here is wiring. If you find yourself
writing one, say what it is for.

**Two traps from H7 that will bite here.** `HarmonicaViewModel.PublishFrame` records the frame's cost
with the scheduler, so a test that also records a synthetic timing double-counts and the ladder falls
two rungs an iteration. And `HarmonicaInputs.Apply` **trims** — a probe or a test value that differs
only in whitespace stores the identical value and moves no key.

---

## 9. Gate command

```
dotnet test tests/Ui.Tests
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
dotnet test tests/Core.Tests    --no-build          # M1 reads Core's device descriptors
```

Baseline going in: Ui **5,251** · Harmonica **94** · Firewall **5** · Core **1,118** ·
Engine **1,004 + 1 skip** · RfCore **281**.

---

## 10. Report back

1. **What Set DUT can reach with only a kit-folder preference** (R-h8-4) — and confirmation that no
   in-memory workspace was created to get there.
2. **Whether the `.charm` double-click path required finishing the `.crfw` stub**, and whether you
   finished it or scoped around it.
3. **Which panel *Copy Plot* copies**, and why.
4. **Anything the design note got wrong.** H0–H3 found a sign error in §4.5.3(a); H4–H5 found the
   viewport-margin defect; H6 found that §6.6's "30–40 ms" is 12.9 ms and that the shipped default
   document cannot exercise an intrinsic drag at all; H7 found that §7.8's Tuner-pair export is
   **unrunnable and fails silently** and that §6.5's per-chart independence is not what the solver
   does. Say so plainly rather than working around it quietly.
