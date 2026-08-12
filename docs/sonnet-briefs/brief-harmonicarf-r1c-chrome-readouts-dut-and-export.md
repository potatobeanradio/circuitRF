# Brief — harmonicaRF Round 1C: the chrome, the readouts, Set DUT, and the export

**Read first:** `docs/design/harmonicarf.md` (**§4.3**, **§4.5**, **§7.5**, **§7.6**, **§7.8**), then
`src/Harmonica/CLAUDE.md` and `src/Ui/CLAUDE.md`'s **H7** and **H8** entries.

**Round 1 is three briefs and they are independent.** **1A** is the crash, the menu policy and colour
— it adds `Harmonica.Messages` and `Harmonica.ProgressBar`, which **§2 and §3 of this brief consume**.
**1B** is the panels. This one is everything below the panels: the toolbar goes away, the messages
move, the readouts are rebuilt, Set DUT becomes real, and the testbench export becomes a `.csch`.

**This is the biggest of the three.** §5 (the readout rebuild) and §6 (Set DUT) are each a milestone
in their own right; take them in the order below and stop-and-report if §6 turns out to need more than
this brief scopes.

---

## 0. What already exists

| you need | it is here |
|---|---|
| the toolbar being removed | `HarmonicaView.axaml` lines 30–84 (`Border` + `StackPanel`), handlers in `HarmonicaView.axaml.cs` |
| the status text being moved | `StatusText` in that toolbar; fed by `HarmonicaView.Refresh()` from `HarmonicaViewModel.StatusMessage` |
| the status message's own sources | `StatusMessage` = `SolveError` › `InverseMessage` › `Scheduler.StatusMessage` |
| the progress-bar mechanism | `IMessageSink.BeginProgress` → `IProgressMessage` → `LiveProgressMessage` (`src/Ui/Messages/`), and `RunControl`/`RunProgress` in `src/Engine` |
| the readout list | `HarmonicaSolver.BuildReadouts` → `HarmonicaFrame.Readouts` (`(label, value, tooltip)`) |
| the readout strip | `ReadoutStripView` (`src/Ui/Views/Harmonica/`) — `SetItems`, `SetInputs`, `SetInputError` |
| the §7.5 input list | `HarmonicaInputs.Build` / `Apply` — framework-free, derives `Structural` by probing `StructuralKey` |
| the schematic's inline editor | `SchematicView`'s `InlineEditBox` + `CommitInlineEdit` — the pattern §5 must reuse |
| Γ ⇄ Z | `HarmonicaDataSet.GammaOf` / `ImpedanceOf` |
| the grid optima | `ContourGrid.Mxp` / `Mxe` → `GridExtremum(Index, GridPoint, Value)`; `GridPoint(Gamma, Z, PinSearchResult)` |
| the per-drive FOMs | `PinStep(PavlDbm, Compression, Point)` + `Foms` (`LoadpullEngine.FomResult`), `PdcW`, `GainDb`, `De`, `Pae` |
| the published cubes (Zin, Γ_intr, …) | `HarmonicaDataSet.Build(ctx, point, terminations)` |
| the DUT spec | `DutSpec` (`Kind`, `TypeName`, `Provider`, `Multiplicity`, `Parameters`, `IntrinsicMapping`) |
| the Set DUT dialog | `HarmonicaSetDutDialog` + `HarmonicaDutEditor` + `HarmonicaDutCatalog` |
| applying a DUT | `HarmonicaViewModel.ApplyDut` — routes through `StructuralKey`, touches nothing else |
| the current testbench export | `HarmonicaInterchange.ExportTestbench` → `.cnl` text |
| the `.csch` writer | `SchematicPersistence.SaveToFile`, `SchematicEditModel`, `EditableComponent`, `EditableWire` |
| kit / PDK resolution with no workspace | `DeviceWorkerProviderResolver(searchRoots)` — folder paths only; `HarmonicaDutCatalog.RegisterKitResolver()` already calls it |

---

## 1. Remove the toolbar

### R-h9c-1

> **owner:** *"Remove all the toolbar buttons and indicators, the toolbar and the row that contains the
> toolbar."*

`HarmonicaView.axaml`'s `Border`/`StackPanel` block (Solve · plane toggle · X-unit · cursor mode ·
Edit Display · `StatusText`) goes, and with it the row. `HarmonicaView.axaml.cs`'s `OnSolveClick`,
`OnCycleXUnitClick`, `OnToggleCursorSnap`, `OnToggleEditDisplay` and the `Refresh()` lines that write
`PlaneLabel`, `XUnitLabel`, `CursorModeLabel`, `EditDisplayToggle`/`EditDisplayLabel` go with them.

**Five commands lose their only affordance. Check each before deleting anything, and report:**

| toolbar button | is it reachable elsewhere? |
|---|---|
| Solve (full grid, full raster) | Grid menu has presets and Reset Grid, but **no explicit "solve now at full quality"** |
| plane toggle (intrinsic/extrinsic) | Display ▸ Loadline Plane — **yes** |
| X-unit cycle | Display has none, but **1B's R-h9b-10 adds a right-click on the X-axis label** |
| Edit Display | Display ▸ Edit Display — **yes** |
| cursor snap-to-compression | **nowhere else** (R-h6-11's own control) |

**Do not silently drop a capability with the button.** The two with no other route (Solve, cursor
snap) need a menu item on **both** surfaces, or an explicit owner decision. Add them to Display (or
Grid, for Solve) and say so; a capability removed by accident is worse than one removed on purpose.

**The status text does not disappear — it moves.** See §2.

---

## 2. The message line

### R-h9c-2 — one line, at the bottom, showing only the last message

> **owner:** *"Move the messages indicator text that is currently in the toolbar to be at the bottom
> of the harmonicRF document window. All harmonicaRF messages will displayed here as a one-line
> indicator. It only ever displays the last message. Create a Harmonica.Messages color theme for its
> color which is RGB=0,90,30 for dark mode and 170,205,180 for light mode."*

- **The role is 1A's** (`Harmonica.Messages`, exactly those values). This brief consumes it; it does
  not define it. If 1A has not landed, add the role here and say so, keeping the values identical.
- A `Border` docked bottom in `HarmonicaView.axaml`, below `PanelHost`, holding a single
  `SelectableTextBlock` — **selectable, never `TextBlock`**, the same rule §7.5 already imposes on the
  readout strip: a message you cannot copy is one you retype into a bug report.
- **It shows the LAST message only.** No history, no list, no scroll. `HarmonicaViewModel
  .StatusMessage` already collapses three sources in priority order (`SolveError` › `InverseMessage` ›
  `Scheduler.StatusMessage`) — bind to it and keep that priority.
- **The three fallback lines `Refresh()` currently composes** ("N HB solves · M Γ points · …", the
  cursor mode, the plane) were toolbar decoration. Decide which, if any, belong on the message line
  when there is nothing to say, and say which you kept. A permanently-populated line is fine; a
  permanently-*changing* one is noise.
- **This is also where 1A's swallowed-exception fix surfaces.** 1A's R-h9a-13 routes hook failures
  into `SolveError`; with this line in place, "the menu item did nothing" becomes "the menu item said
  why".

---

## 3. The solving progress bar

### R-h9c-3 — reuse the Messages-panel mechanism, do not build a second one

> **owner:** *"When solving for a whole set of grid points, a progress message should be displayed:
> 'Solving….' with a progress bar after it. We implemented progress bars in the Messages panel for
> circuit analysis; reuse a similar system. Make the progress bar have a width of 75. Give it a color
> theme of Harmonia.ProgressBar which is RGB=0,90,30 for dark mode and 170,205,180 for light mode."*

**The mechanism to mirror** is `IMessageSink.BeginProgress` → `IProgressMessage` →
`LiveProgressMessage` (`src/Ui/Messages/`), driven by `RunControl`/`RunProgress` in `src/Engine`. Read
`src/Ui/CLAUDE.md`'s own "The run says what it will do BEFORE doing it" entry first — it records four
rules learned the hard way, and **three of them apply here unchanged**:

- **The bar is INLINE, immediately after the text, and any changing counter comes AFTER the bar.**
  Nothing that grows may sit to the bar's left, or the bar jitters. "Solving…" is constant, so it goes
  first; a "17 / 61" counter, if you add one, goes after the bar.
- **Do not `PadLeft` a counter to a fixed width** — a space is roughly half a digit in a proportional
  font. Right-align it in a fixed-width box instead.
- **Observations must be throttled** (~25/s) and the final tick of a known total always delivered, so
  the throttle can never leave the bar short.

**What is different here, and it is the part to get right:** harmonicaRF has no `IMessageSink` at all.
It is a standalone-capable document (§1.2/§3.1) with its own message line from §2 — so **do not wire
it to `MessagesTool`.** Build the smallest thing that satisfies the owner: a bool + a fraction on
`HarmonicaViewModel`, rendered as text + an Avalonia `ProgressBar` (`Width="75"`, foreground from
`Harmonica.ProgressBar`) in §2's bottom line. Reusing the *shape* of `IProgressMessage` is the ask;
reusing the *plumbing* would drag a workspace dependency into a binary that has none.

**Where the progress comes from:** `ContourGrid.Build` walks the Γ points and already has a
cancellation point between them (R-h45-9). That is the natural tick — grid points solved / total. It
runs on a `SolvePool` worker, so **every update must be marshalled to the UI thread**, exactly as
`HarmonicaView`'s `OnFrameCompleted` already marshals `PublishFrame`.

**A frame with `SkipContours` (tier A alone) solves no grid** — it must not show a bar that never
moves. Show the bar only for a frame that actually sweeps a grid.

---

## 4. The readout font scales with the window

### R-h9c-4

> **owner:** *"The data read out (and config settings) in the lower left of the data display should
> render at a size that depends on the window size… Same as how the Smith charts currently scale size
> to match occupy the maximum space and reduce in size when less pixels are available."*

Every `FontSize` in `ReadoutStripView` is a hardcoded `10`. The strip is placed by `CharmLayout`
fractions (`HarmonicaView.PlaceReadoutStrip`), so its pixel size is already known per layout pass —
derive the font size from it there and push it into `SetItems`/`SetInputs` alongside the brush they
already take.

Three constraints:

- **Clamp both ends.** Below ~8 pt the strip is unreadable; above ~16 pt it stops being dense, and
  density is §7.5's whole design constraint. State the range chosen.
- **Scale everything in the strip together** — labels, values, units, the input editors and the error
  line — or the rows stop aligning.
- **The in-place update path must not be broken.** `SetInputs` deliberately rebuilds only when the row
  SHAPE changes and otherwise writes values in place, skipping whichever editor has focus, "because
  the caret vanishing mid-number is the single most disruptive thing this panel could do". A font-size
  change is a shape change in the layout sense but **must not** count as one for that guard — thread
  the size through the in-place path too.

---

## 5. The readouts, rebuilt

This is the largest single item. `HarmonicaSolver.BuildReadouts` produces a flat
`(label, value, tooltip)` list and `ReadoutStripView.SetItems` renders it as one wrapping run of
pairs. **The owner is asking for COLUMNS**, which that shape cannot express — so the data type changes
as well as its contents.

### R-h9c-5 — what goes away

From **`BuildReadouts`** (the read-only lower area), delete:

| readout | owner's reason |
|---|---|
| `compr` | *"Remove 'compr' indicator (and its value)"* |
| `stop` | *"Remove 'stop' and its value"* |
| `K` | *"Remove 'K'. Why is this even here in the first place? The user sets K, no need to read it back!"* |
| `solves` | *"Remove 'solves' and its value"* |
| `Gss` | *"Remove 'Gas' and its value"* — this is `Gss`, small-signal gain |

**`compr` and `K` also exist as INPUTS** (`HarmonicaInputs` keys `settings.compression` and
`settings.k`, labels `compr` and `K`). The owner's reasoning — *the user sets it, no need to read it
back* — is precisely about the readout half. **Keep both inputs; delete both readouts.** Say so
explicitly in the completion note, because deleting the wrong one of an identically-labelled pair is
the obvious mistake here.

Separately, from the **input** row:

> **owner:** *"Remove the I[1,0] and I[2,0] readouts; they are no longer needed because the Set DUT
> dialog."*

These are not readouts either — they are the SDD's own declared parameters, surfaced as
`param:I[1,0]` / `param:I[2,0]` text inputs by `HarmonicaInputs.Build`'s R-h7-4 model-declared-parameter
pass. They are equation text hundreds of characters long in a 160 px box. **Stop surfacing SDD
equation parameters in the strip** now that §6's dialog edits them properly. Keep the pass for other
DUT kinds' scalar parameters — a native FET's `Ipk`/`Vpk` in the strip is exactly what R-h7-4 is for.
Draw the line on the DUT KIND, not on the parameter name.

### R-h9c-6 — the four new columns

> **owner:** MXP and MXE performance columns (MXP left, MXE right), each: `"MXP xf0 Load/Source"` /
> `"Pout: x (dBm)"` / `"Efficiency: x (%)"` / `"PAE: x (%)"` / `"Gain: x (dB)"` / `"Gp: x (dB)"` /
> `"Zin: R+jX Ω"` / `"AM/PM: x (°)"`. Plus a **Load** column and, to its left, a **Source** column, each
> listing every marker on that plane: `"ZL1=R+jX Ω"`, `"ΓL1 = x +jy"`, `"ZL2=…"`, `"ΓL2=…"`, one pair
> per marker, no marker ⇒ no row.

**The MXP/MXE values are the INTERPOLATED optimum, and 1B builds it — this section consumes it.**

The owner confirmed directly that MXP and MXE are the interpolated value. Today they are not:
`ContourGrid.Extremum` returns the best grid **sample** and the glyphs are drawn there. **1B's
R-h9b-15/16/17 fix that** — the argmax of the fitted `Rbf2D` surface, refined off the raster, inside
the supported region, with the FOMs coming from one `PinSearch.Run` at that state (not from N
separately-interpolated surfaces, which would be mutually inconsistent and could not produce `Zin` or
AM/PM at all). It lands as one record on `SmithPanelData`, read by both the glyph and this column.

**So this column reads that record and formats it. It does not compute an optimum.** If 1B has not
landed yet, read `ContourGrid.Mxp`/`Mxe` as the interim source, **label the interim clearly in the
code**, and say in the completion note that the column is showing grid samples until R-h9b-15 lands —
do not build a second interpolation here, or the readout and the glyph will describe different states,
which is worse than a coarse answer.

Three things still to check on this side and report:

- **`Gp`** — `PinSearch`'s own comments mention Gt and Gp; find whether `LoadpullEngine.FomResult`
  exposes it. If it does, it is free off the solved `PinStep`. If not, say so rather than substituting
  Gt under a `Gp` label.
- **`AM/PM`** — confirm what it is derived from (the phase of the fundamental output relative to the
  drive, across the drive-up) and whether the published `DataSet` already carries it. If nothing does,
  say so; do not invent a definition.
- **The band and plane in the header row** (`"MXP 1f0 Load"`) are `GridHarmonic` and `GridSide`, which
  already exist and are already document-wide.

**MXP/MXE may not exist**, and the column must say so rather than show dashes: `Extremum` already
returns null when every point is a hole, and 1B adds two more "no optimum" states — a degraded ladder
rung and a `SkipContours` frame.

**The Source/Load termination columns update live as markers are dragged.** They read
`HarmonicaMarker.Gamma` and `Terminations.Z(side, band)` — the same two the marker itself is written
through (`SetMarkerImpedance` keeps them in step in ONE place; do not add a third source). A drag
raises `RedrawRequested` on every move, so "live" costs nothing new.

**Column order, left to right:** Source terminations · Load terminations · MXP · MXE. The owner
specifies MXP left of MXE and Source left of Load; that leaves the two groups' relative order to you —
put the editable ones nearest the charts they belong to and say why.

### R-h9c-7 — per-row format menus and the Set… dialog

> **owner:** right-click a Z or Γ row for a **MenuFlyout** switching between real/imaginary and
> mag/angle(°) format; plus a **"Set…"** item opening a small dialog that edits that marker in either
> format.

- **Format is per row, and it is display-only.** Changing it must never touch the model. Persist the
  choice in the `.charm` (`CharmAppearance` is the natural home — it already carries display-only
  toggles) so it survives a reload; absent ⇒ default. Say which default you chose.
- **"Set…" writes through `SetMarkerImpedance` / `SetMarkerGamma`** — the same two calls a drag uses.
  Never a third write path. `SetMarkerGamma`'s existing Γ = 1 nudge (0.999) is load-bearing: an open
  has infinite impedance and would take the whole solve down.
- **Z₀ is 1B's setting.** If 1B has landed, Γ here must be against the user's Z₀, not 50 Ω. If it has
  not, use 50 and leave a comment naming R-h9b-6.
- **MXP/MXE rows are NOT editable** — the owner says so directly (*"obviously, MXP/MXE impedance and
  the performance summary data cannot be edited because those are a consequence of the simulation"*).
  Give them the format flyout if it is free; never a Set… item.

### R-h9c-8 — the inline editor

> **owner:** *"Replace all the readout UI with an inline text editor (see how the schematic editor does
> this). Any editable readout allows user to double-click on it to make changes… All readout text
> should still be selectable."*

The schematic's pattern is `SchematicView`'s `InlineEditBox` + `CommitInlineEdit` (and its
`ComponentLabelAnchor` geometry, `LabelRowGeometry` → `WorldToScreen`). Read
`src/Ui/CLAUDE.md`'s **"Inline editor fixes"** entry before starting — it records the two rules that
cost a debugging session:

- **The edit box's position comes from the SAME geometry source the renderer uses**, never a
  hand-rolled formula. Here that is far easier than in the schematic: the readout strip is Avalonia
  controls, so the anchor is the control's own bounds.
- **Selection semantics differ by what is being edited** — a name-and-value row selects all; a
  value-with-a-unit row selects the value only.

Concretely: every readout stays a `SelectableTextBlock` (so §7.5's "all text is selectable" survives);
double-click on an **editable** one swaps in a `TextBox` in place, commits on Return **and** LostFocus,
reverts on Escape, and `e.Handled = true` on Return so the hosting window's default button does not
take it. That three-key contract is already implemented twice in this codebase
(`ReadoutStripView.SetInputs`, `SettingsView`'s hex field) — **reuse it, do not re-derive it.**

**Which rows are editable:** the §7.5 inputs (already are), and the Z/Γ termination rows. Nothing
else.

### R-h9c-9 — the data shape has to change

`HarmonicaFrame.Readouts` is `IReadOnlyList<(string, string, string)>`. Columns, editability, format
mode and a marker reference do not fit in a triple. Introduce a small record — label, value, tooltip,
column, and enough identity for the editable rows to write back (which marker, which side/band).
**Build it in `HarmonicaSolver.BuildReadouts`**, not in the view: the solver is where the numbers are,
and §0.3 item 1's rule ("the engine is DONE — do not recompute any of it in a view model") is what
keeps it that way.

`HarmonicaView.CopyReadoutsAsync` walks `h.Frame.Readouts` as a triple — update it, and keep its
tab-separated output sensible for the new column shape.

---

## 6. Set DUT

### R-h9c-10 — first, why it does nothing

> **owner:** *"File -> Set DUT menu command does not do anything."*

`HarmonicaView.WireMenuHooks` sets `menus.SetDutHook = () => _ = ShowSetDutAsync();` — a **discarded
task**, so any exception (including one thrown synchronously in `HarmonicaSetDutDialog`'s
constructor) is captured and silently swallowed. That is 1A's R-h9a-13; if 1A has landed the failure
is already visible on §2's message line and you are diagnosing with a message in hand. If it has not,
add the failure path here first.

**Then find and report the real cause.** `HarmonicaSetDutDialog` was one of the twelve views in the
`InitializeComponent`-shadowing sweep (fixed 2026-08-12, `src/Ui/CLAUDE.md`) — that specific bug is
gone, so it is something else. Do not guess in the completion note.

### R-h9c-11 — what Set DUT must offer

> **owner:** *"User should have option to use SDD2 or SDD3, or point to any 2-port or 3-port circuitRF
> cell (within the workspace or not), be able to change the cell's parameters. (Port 1 is always gate,
> Port 2 is always drain, Port 3 is source). If no port 3, it is assumed to be a grounded source. This
> allows user to specify a linear extrinsic work."*

Today `DutSpec.Kind` is `Sdd` / `NativeFet` / `Diode` / `External`. This adds a **fifth kind: a
circuitRF CELL**, which is a genuinely new capability, not a dialog tweak.

**The port convention is the contract and it is fixed:** port 1 = gate, port 2 = drain, port 3 =
source; a 2-port cell has a grounded source. State it in the dialog itself, not only in the code — a
user who wires a cell the other way round gets a plausible wrong answer.

**"SDD2 or SDD3"** is the existing SDD kind with a port count. Check whether `DutSpec` can already
express both (the default document is a 2-port SDD with `I[1,0]`/`I[2,0]`); if the 3-port form needs a
field, add one.

### R-h9c-12 — elaboration happens ONLY on Set and on Refresh

> **owner:** *"harmonicaRF will have to elaborate the cell in order to run it. To keep harmonicaRF
> running fast and responsive, elaboration of the cell should only happen when the DUT is set (or when
> user explicitly 'refreshes' the cell from within harmonicRF. Add a Refresh DUT button."*

This is the load-bearing rule of §6 and it maps cleanly onto machinery that already exists:

- **`HarmonicaViewModel.ApplyDut` is already the one write-back**, and it already routes through
  `CircuitModel.StructuralKey` — a changed DUT rebuilds the context and resets the frame ladder, a
  same DUT is a no-op. Elaborate the cell **there**, once, and store the elaborated result on the
  model. Do not elaborate inside `EnsureContext`, `HarmonicaContext.Apply` or anything a frame touches
  — §6.1's standing rule is that a value change mutates in place and never rebuilds, and elaboration
  is ~1000× the cost of the thing being changed.
- **Refresh DUT** re-elaborates the same cell and re-applies. It needs a real affordance: a menu item
  on **both** menu surfaces (File, beside Set DUT…). The toolbar is gone (§1), so it cannot be a
  toolbar button.
- A cell can change on disk between Set and Refresh. **Refresh must re-read, not reuse a cached
  parse.**

### R-h9c-13 — resolving a cell from another workspace, and its kit

> **owner:** *"Note that the cell could have an external PDK in it (and cell could be from a different
> workspace). harmonicaRF must try to resolve the kit using the cell's workspace in order to it. It
> gives an error indicator if the external PDK cannot be resolved (or any other issues with the cell)."*

Two mechanisms already exist and must be reused rather than re-derived:

- **Finding the cell's own workspace** is `WorkspaceRootFinder.FindAncestorCws(startDir)` — the
  ancestor-`.cws` walk built for `brief-foreign-documents.md` (see `src/Ui/CLAUDE.md`'s **R-fgn-3**).
  A cell from a different workspace resolves its technology and its kits against **its own** ancestor
  workspace, never the currently-open one. That entry also records why it must be resolved **live**
  rather than snapshotted.
- **Resolving a kit with no workspace at all** is
  `DeviceWorkerProviderResolver(IEnumerable<string> searchRoots)` — the folder-only constructor
  `src/Cli --kits` already ships and `HarmonicaDutCatalog.RegisterKitResolver()` already calls with
  `AppPreferences.HarmonicaKitFolders` (H8's R-h8-4). harmonicaRF standalone has no workspace, so this
  is the fallback when the ancestor walk finds nothing.

**Every failure is reported by name, never substituted.** That is `CharmIo`'s own rule for an
unresolved model reference and it applies identically here: an unresolvable kit, a cell that will not
elaborate, a cell with the wrong port count, a missing `.ccell` — each says which artifact and what to
do, on §2's message line and as a persistent indicator in the dialog. **harmonicaRF must never
substitute a different model**, and the document must still open.

### R-h9c-14 — the intrinsic plane

A cell-backed DUT still needs `DutSpec.IntrinsicMapping` for the glyphs (H8's R-h8-3: *asked for,
never guessed*). With the fixed port convention of R-h9c-11 the mapping is derivable — port 1 gate,
port 2 drain, port 3 (or ground) source — so **derive it and say that you did**, rather than leaving
the intrinsic panels empty. `IntrinsicPortMap.For(dut, model, package)` is where that resolution
lives; check whether it already handles a cell or needs an arm.

Note its existing refusal: a resolved mapping still refuses the **source** side by name when the
package states a source lead (`Rs`/`Ls` ≠ 0), because §4.5.3's `J′` route reads the model's own gate
port, referenced to ground rather than to the lifted source terminal. That refusal is correct and must
survive.

---

## 7. Export Testbench → `.csch`

### R-h9c-15

> **owner:** *"File->Export Testbench should generate a .csch file for the user to run (instead of a
> .cnl file). Place component symbols (and any cells) and wires at appropriate locations. Use 'best
> effort'. The exported Testbench should have an Analysis configured the same was as harmonicaRF (at
> the time of export), same bias, same Z[1], Z[2], Z[3] termination settings for source and load
> (using circuitRF tuner components)."*

**Read `HarmonicaInterchange.ExportTestbench`'s own doc comment before writing a line.** It records
H7's biggest finding, and it is directly relevant:

> §7.8's "export the terminations as a `Tuner` pair" **does not work** under a plain `type=hb` run. A
> `Tuner` is inert there — nothing in `HbEngine` calls `SetRole`/`SetTone`/`SetSourceDrive`; those are
> the **loadpull** engine's, and the CLI has no loadpull verb. A Tuner-pair export would run, converge
> and be wrong. So the `.cnl` export uses `P1Tone` (source) and `PnTone` with no tones (load).

The owner is now asking for **Tuner components** in a `.csch`. **These are not in conflict, and the
completion note must say why:** a `.csch` is opened in circuitRF, where the user configures and runs
whatever analysis they like — including loadpull, which is exactly the engine that drives a `Tuner`.
The `.cnl` export's constraint was the CLI's `hb` verb, not the component. **But check it rather than
assuming:** if the exported `.csch` carries an HB analysis (which the owner asks for — *"an Analysis
configured the same way as harmonicaRF"*), a Tuner pair in that schematic is inert for the same
reason. Resolve this explicitly — either export Tuners **and** an HB analysis and warn that the
terminations need a loadpull analysis to be honoured, or export `P1Tone`/`PnTone` as the `.cnl` does
and say so. **Do not ship a schematic that runs and is wrong.**

Everything else is mechanical, using machinery that all exists:

- `SchematicEditModel` + `EditableComponent` + `EditableWire` + `SchematicPersistence.SaveToFile`.
- **Placement is "best effort" per the owner** — a left-to-right chain (source termination → embedding
  → DUT → embedding → load termination) with orthogonal wires on the connection grid `P` is enough.
  **Every pin world-coordinate, wire endpoint and wire bend must land on an exact multiple of `P`** —
  that is R7, the on-grid invariant, and `OnGridInvariantTests` guards it. Use
  `SchematicEditModel.SnapToGrid`, never `SnapToAuthorGrid`.
- Bias: `Vdc` components at the model's own `Bias.Vgs`/`Vds` (or `Idq`).
- The DUT: an `Sdd` for an SDD, the matching `SymbolKind` for a native FET, a `CellRef` component for
  a cell-backed DUT (§6). A DUT harmonicaRF cannot express as a schematic component is **reported, not
  silently omitted**.
- Analysis: an `HarmonicBalanceAnalysis` matching `Settings.FrequencyHz` / `HarmonicCount`, written
  through `AnalysisSerialization` — **the one encoder** (`src/Ui/CLAUDE.md`'s §5.4 rule); never a
  second one.
- **Keep the `.cnl` export.** It is R-h7-13's own gate — a `.charm`'s numbers are checkable through
  `dotnet run --project src/Cli -- hb`, and `HarmonicaTestbenchCliTests` runs that real process. The
  owner said "instead of"; the honest reading is *the menu item the user reaches produces a `.csch`*.
  Offer both file types in the save picker (the extension chooses, exactly as *Export Data* already
  does for `.npy`/`.mat`/`.txt`) and keep the `.cnl` path and its test intact.

---

## 8. Scope guardrails

- No crash fix, no menu policy, no colour-role definitions (**1A** — this brief consumes
  `Harmonica.Messages` and `Harmonica.ProgressBar`).
- No Smith titles, no Z₀ mechanism, no DCIV dialog, no grid-point toggle, no drag fixes (**1B**).
  §5's Γ formatting reads 1B's Z₀ if it exists and 50 Ω if not.
- **No MXP/MXE interpolation here.** That is 1B's R-h9b-15/16/17; §5's column reads the record it
  produces. Building a second interpolation in the readout would let the cross and the numbers
  describe different states — the one thing R-h9b-17 exists to prevent.
- **`HarmonicaViewModel.ApplyDut` stays the single DUT write-back.** It reaches no further than
  `Model`; nothing here may touch `HarmonicaContext`, `TerminationSet` or the scheduler directly.
- **§6.1's rule is absolute: never re-elaborate on a value change.** Elaboration happens in `ApplyDut`
  and on Refresh, and nowhere a frame can reach.
- **No `.charm` `FormatVersion` bump** — the format-mode persistence and any new DUT field are
  additive-with-a-default, per `CharmIo`'s own rule.
- `src/Core`, `src/Engine`, `RfCore` untouched unless §6 genuinely needs a Core-side accessor — and if
  it does, **stop and report before changing one**.

---

## 9. Gates

1. **Build + `dotnet test` green** — `tests/Ui.Tests` and `tests/Harmonica.Tests` while working, full
   solution at the end.
2. **The toolbar and its row are gone**, and every capability it carried is reachable elsewhere or was
   removed by an owner decision named in the completion note.
3. **A one-line message strip sits at the bottom** in `Harmonica.Messages`, shows the last message
   only, is selectable, and a failed menu hook lands there instead of doing nothing.
4. **A grid-solving frame shows "Solving…" with a 75-wide bar** in `Harmonica.ProgressBar`, the bar
   does not jitter, and a tier-A-only frame shows none.
5. **The strip's font follows the panel size**, clamped, with the in-place update path (and its
   focused-editor skip) intact — a solve landing mid-typing must not eat the caret.
6. **`compr` / `stop` / `K` / `solves` / `Gss` are gone from the READOUTS while `compr` and `K` remain
   as INPUTS**, and the SDD equation parameters no longer appear in the input row.
7. **Four columns render** — Source, Load, MXP, MXE — with the owner's exact row labels; a marker-less
   band contributes no row; the MXP/MXE columns say "no optimum" in all three of the states that
   produce one; and **the readout values and the MXP/MXE glyph positions come from the same record**,
   asserted directly rather than eyeballed.
8. **Termination rows update live during a marker drag**, right-click switches format per row, the
   choice survives a `.charm` round trip, and Set… writes through `SetMarkerImpedance`/`SetMarkerGamma`
   only.
9. **Double-clicking an editable readout edits it in place**, commits on Return and LostFocus, reverts
   on Escape; every readout stays selectable.
10. **Set DUT opens**, with the original failure named. It offers SDD2/SDD3, the native laws, an
    external model **and a 2- or 3-port circuitRF cell**; the cell's parameters are editable; the
    port convention is stated in the dialog.
11. **Elaboration happens exactly twice for two user actions** — once on Set, once on Refresh — and
    never on a marker drag, a bias edit or a frame. Counter-gated (`ContextRebuildCount` is the
    existing shape), not timed.
12. **A cell from a different workspace resolves its own kit**, and every failure mode is reported by
    name with the document still open and the previous DUT intact.
13. **Export Testbench writes a `.csch`** that opens in circuitRF with symbols placed, wires on the
    connection grid, the same bias, the same terminations and an analysis matching harmonicaRF's own —
    with the Tuner-vs-`P1Tone` question resolved in writing. The `.cnl` path and
    `HarmonicaTestbenchCliTests` still pass.

**Interactive verification is required** for the message line, the progress bar, the readout columns,
the inline editor, Set DUT and the exported `.csch` opening in circuitRF — no visual driver here,
matching every prior harmonicaRF phase. List the exact gestures in the completion note under "please
confirm on your end".
