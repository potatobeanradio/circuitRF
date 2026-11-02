# Brief — harmonicaRF Round 6A: the menu surfaces, one Settings dialog, and the defaults

**Read first:** `src/Ui/Views/Harmonica/HarmonicaMenuView.axaml` (both surfaces — the
`<NativeMenu.Menu>` block AND the in-window `<Menu>` block), `src/Ui/Views/Harmonica/HarmonicaMenuView.axaml.cs`
(`RecomputeAttachment`, `InjectDockedItemsIfNeeded`, `WithdrawInjectedItemsIfAny`),
`src/Ui/Harmonica/HarmonicaAppMenuInjector.cs`, `src/Ui/Harmonica/HarmonicaMenuViewModel.cs`, and
`src/Ui/RESOLVED.md`'s brief-harmonicarf-r3a section.

**Fixture:** the shipped default document, opened three ways — docked in circuitRF, torn off into its
own window, and the standalone `harmonicaRF` binary. Several items below behave differently in each,
and that is exactly the point.

**Housekeeping rule for this brief and every other R6 brief: do NOT update any `CLAUDE.md`.** If you
find something genuinely worth recording — a mechanism that was not obvious, a measurement, a trap —
write it to the nearest `RESOLVED.md` (`src/Ui/RESOLVED.md` for UI work, `src/Harmonica/RESOLVED.md`
for engine work). Nothing else.

---

## 1. The docked macOS menu bar — one real bug, and one owner ruling

### 1.1 What the owner sees, and what is actually specified

> "on macOS, the harmonicaRF native menu does not render correctly when the document is docked. All
> the regular native menus appear, then to the right of Help is harmonicaRF's *Markers* menu. I can
> only see the File menu when the document is detached."

Two separate facts are tangled there:

1. **`File` being absent while docked is BY DESIGN today** — `HarmonicaAppMenuInjector.BuildTopLevelItems`
   returns `[Markers, Display, Grid]` only, and its own header comment says File/Edit/Help are omitted
   because "circuitRF's own bar already carries" menus with those names. That design is being
   **changed** by §1.3 below, but it is not a bug in the sense of "the code does not do what it says".
2. **`Display` and `Grid` not appearing IS a bug.** The injector builds three items and `Inject`
   appends all three. The owner sees one. Find out why before changing anything else in this section.

### 1.2 Diagnose the missing two first — do not "fix" it by rewriting the injector

Candidate mechanisms, in the order they are cheap to eliminate:

- `NativeMenu.Items`' own validator throws on an item that already has a `Parent`. `Inject` is a
  `foreach` with no try — a throw on item 2 leaves item 1 injected and item 3 never attempted, which
  is *exactly* the observed symptom (Markers present, Display and Grid absent). `BuildTopLevelItems`
  returns fresh instances, but `BuildDisplay`/`BuildGrid` build **sub-menus** whose items are also
  fresh; check whether anything shared (a `HarmonicaBandMenuItem`-backed item, a `ContourHarmonics`
  element, an `ICommand` carrying a parent) is reachable from two places.
- `RefreshInjectedItemsIfAny` withdraws then re-injects on every band change. If a withdraw is partial
  (an item whose `Parent` was already cleared), the re-inject can throw on the survivor.
- The macOS exporter may only flush the app menu once per focus transition.

**Deliverable: a headless test in `tests/Ui.Tests/Harmonica/HarmonicaAppMenuInjectorTests.cs` that
injects, withdraws and re-injects several times against a plain `NativeMenu` and asserts the exact
top-level set each time** — including after a band toggle and a view-model swap. Constructing a
`NativeMenu`/`NativeMenuItem` needs no platform (that file already relies on this). If the throw
reproduces there, it is the bug; say so and fix it. If it does *not* reproduce headlessly, say that
plainly in your report and fix the injection loop to be failure-visible anyway: it must never leave a
partial set behind — build the whole list, then add, and surface any failure on
`HarmonicaViewModel.SolveError` instead of losing it.

### 1.3 Owner ruling — a docked document gets ONE extra top-level menu named `harmonicaRF`

Chosen by the owner over "inject every menu, duplicate names and all" and over "merge into circuitRF's
own File/Edit". The docked bar becomes:

```
circuitRF ▸ File  Edit  View  …  Help │ harmonicaRF  Markers  Display  Grid
```

`harmonicaRF` holds the document-scoped items that live in harmonicaRF's own `File` and `Edit` menus
when it is torn off — at minimum: New, Open .charm…, Save, Save As…, Set DUT…, Refresh DUT,
Import .gam…, Export .gam…, Export Data…, Export Testbench…, Copy Plot, Copy Readouts,
Copy Termination Set, **Settings…** (§2), Close. Keep the separators that group them in the torn-off
`File`/`Edit` menus.

`Undo`/`Redo` stay OUT of the injected menu — circuitRF's own Edit ▸ Undo already occupies ⌘Z on that
window and two Undo items with one gesture is worse than one. Note this decision in your report.

**Three surfaces stay hand-mirrored.** `HarmonicaMenuView.axaml`'s own comment ("Anything added here
must go on BOTH") now covers three: the in-window `Menu`, the torn-off `NativeMenu`, and the injected
docked set. The injected set is *derived* from the same view-model commands, never a copy of either
XAML block. The existing `HarmonicaMenuNativeAttachTests` / `HarmonicaAppMenuInjectorTests` pattern is
where you pin it: assert the `harmonicaRF` menu's item headers match the union of the torn-off
File/Edit items minus Undo/Redo, computed from the view model, not typed twice.

**Gate:** the owner docks the document and reports the bar. Four top-level injected items, all
functional, and they withdraw cleanly on blur (dock a harmonicaRF tab, click a schematic tab, confirm
the four are gone and circuitRF's own menus are untouched).

---

## 2. One Settings dialog — Edit ▸ Preferences… and Display ▸ Advanced Settings… merge

### 2.1 What is there now

- `Edit ▸ Preferences…` → `HarmonicaMenuViewModel.PreferencesCommand` → `PreferencesHook` →
  `HarmonicaView.ShowPreferencesAsync` → `HarmonicaPreferencesDialog` — the colour-role editor, plus
  the iso-line α floor/exponent sliders, the iso-label checkbox, and the tickle default.
- `Display ▸ Advanced Settings…` → `AdvancedSettingsCommand` → `HarmonicaAdvancedSettingsDialog` —
  loadline samples, FFT oversampling, multiplicity M, compute-charge.

The owner reports **`Edit ▸ Settings` does nothing when selected**, and that a previous round was
supposed to have fixed it. Before merging, find out which item they are actually clicking — on macOS
the item under the *app* menu (⌘,) is not the same item as harmonicaRF's own `Edit ▸ Preferences…`,
and while docked the visible `Edit` menu is **circuitRF's**, not harmonicaRF's. State in your report
which of those three paths was dead. (§1.3's injected `harmonicaRF` menu removes the docked ambiguity
by giving the document its own `Settings…` item, but that does not excuse leaving a dead item behind:
whichever path did nothing must either work or be removed.)

### 2.2 The merge

**One dialog, `HarmonicaSettingsDialog`, with tabs.** Suggested tabs, but the split is yours as long
as everything below is reachable and nothing is lost:

| tab | contents |
|---|---|
| **Appearance** | everything `HarmonicaPreferencesDialog` has today — the role list, hex/pick, iso α floor & exponent, iso-line labels, tickle default, Import/Export `.ccolor`, Reset All Colours |
| **Advanced** | everything `HarmonicaAdvancedSettingsDialog` has today — loadline samples, FFT×, M, compute charge — **plus §3's contour-kernel controls** |

**One menu item, named `Settings…`**, on all three surfaces (in-window Edit, torn-off NativeMenu Edit,
and §1.3's injected `harmonicaRF` menu). `Display ▸ Advanced Settings…` is removed. Keep ⌘, as its
gesture where a gesture is offered today.

Reuse the two existing dialogs' code-behind rather than retyping it — both are small, self-contained
and already validate their inputs; lifting each into a `UserControl` per tab is the least-risk shape.
Delete the two old dialog classes once nothing references them (do not leave a dead
`HarmonicaAdvancedSettingsDialog` behind; that is what makes the next person think there are two
settings surfaces again). `HarmonicaMenuViewModel.AdvancedSettingsHook` and `PreferencesHook` collapse
to one hook — update `HarmonicaMenuAndInputTests` / `HarmonicaHooksAndInterchangeTests` accordingly.

---

## 3. Contour surface controls on the Advanced tab (kernel / smooth / epsilon)

`RfCore.Loadpull.Rbf2D` already carries all three (`src/RfCore/Loadpull/Rbf2D.cs`):

```csharp
public enum RbfKernel { Multiquadric, ThinPlate, Gaussian }
… Factorize(re, im, values, RbfKernel kernel = Multiquadric, double smooth = 1e-3, double? epsilon = null)
```

`ContourGrid.Fit` (`src/Harmonica/ContourGrid.cs:592`) calls `Rbf2D.Factorize(re, im, values)` with
every default. Thread the three settings through:

1. **New fields on `HarmonicaSettings`** (`src/Harmonica/CircuitModel.cs`): `ContourKernel`
   (default `Multiquadric`), `ContourSmooth` (default `1e-3`), `ContourEpsilon` (**nullable**;
   null = `Rbf2D`'s own scipy-style auto epsilon — do not substitute a number for "auto", the
   difference is visible in the contours).
2. **Persist in the `.charm`** — `CharmIo`'s settings block, same nullable-defaulted shape every other
   setting there uses ("absent means default, never empty").
3. **`ContourGrid` reads them** the same way it re-reads `Z0` at the start of every `Build` — a worker's
   grid is a long-lived reused object, so these must track the document's live value, not freeze at
   construction. **They are part of the factorization cache key**: `_factor`/`_factorMask` are keyed on
   (positions, NaN mask) today, and a kernel/smooth/epsilon change with unchanged positions MUST
   invalidate the cached factor or the user changes a setting and sees nothing move. This is the one
   correctness trap in this section — write a test for it specifically.
4. **UI**: a `ComboBox` for the kernel, text boxes for smooth and epsilon. Validation, robustly:
   smooth ≥ 0 and finite; epsilon either blank (= auto) or > 0 and finite. Reject-and-keep-the-text on
   bad input, the same shape `HarmonicaAdvancedSettingsDialog.OnFieldLostFocus` already uses — never
   silently substitute a value.
5. **Changing any of the three re-runs the fit and everything downstream of it**: contours, iso-line
   labels, MXP/MXE (they are the argmax over the *fit*, `ContourGrid`'s own D6 note), and the readout
   columns that report them. The grid POINTS do not need re-solving — the Pin searches are unchanged —
   so this must be a re-fit, not a full re-solve. If the plumbing makes a full re-solve the only honest
   option, do that and say so rather than faking a partial update.

**Gate:** a test that fits the same grid under two kernels and asserts the interpolated MXP position
or value differs; and a test that changing `ContourSmooth` alone invalidates the cached factor.

---

## 4. Menu removals

Both are removals from **all three surfaces** (in-window, NativeMenu, injected):

- **`Display ▸ Cursor Snap to Compression`** — remove the menu item. `ToggleCursorSnapCommand` and
  `HarmonicaViewModel.SnapCursorToCompression` (`HarmonicaViewModel.cs:~1264`, default `true`) stay,
  and the behaviour stays on. This is a menu-only removal.
- **`Display ▸ Edit Display`, `Add Trace…`, `Remove All Traces`, and the separator below them** —
  editing the display is deferred to a harmonicaRF v2. **Keep every line of code behind them**:
  `ToggleEditDisplayCommand`, `AddTraceCommand`, `RemoveAllTracesCommand`, `HarmonicaEditDisplay`,
  `HarmonicaTracePicker`, `HarmonicaTracePickerDialog`, the `PickedTraces` plumbing and all their
  tests. Only the three menu items and their separator go. Follow the precedent already in
  `HarmonicaMenuView.axaml`: the diagnostics-overlay items were removed the same way in R5, with a
  comment at the removal site saying re-adding the lines is the whole of turning it back on. Write the
  same comment here.

Note that `HarmonicaEditTarget` (the right-click panel resolver) is used by live features — Copy Plot
and the panel context menus — so it is not part of the deferral.

---

## 5. Defaults

### 5.1 Default grid = 3 × 12

`FrameScheduler` (`src/Harmonica/FrameScheduler.cs:102`) declares `FullRings = 5, FullSpokes = 12` and
`CoarseRings = 3, CoarseSpokes = 12`, and `HarmonicaViewModel.cs:1224` hard-codes `Rings = 5,
Spokes = 12` for the full-quality request. Making 3 × 12 the default collapses the full tier onto the
coarse tier. **Decide and state which of these you did:**

(a) `FullRings = 3` and the two tiers coincide — the ladder then has one fewer distinct rung, which is
    a real change to the drag-quality ladder, not a cosmetic default change; or
(b) `FullRings = 3` with the coarse tier reduced below it (e.g. 2 × 12) so the ladder keeps two rungs.

Prefer **(b)** unless the scheduler's own tests say otherwise — the ladder exists to make a drag cheap,
and flattening it silently pushes drag cost up. Run `tests/Harmonica.Tests` and report any ladder test
that changes meaning.

The `Grid ▸ Grid Preset` menu keeps its 3 × 12 / 5 × 12 / 7 × 16 entries; 3 × 12 is simply what a new
document starts at.

### 5.2 Default Z0 = 80 Ω

`HarmonicaSettings.Z0` (`src/Harmonica/CircuitModel.cs:252`) is `50.0`. The owner wants **80 Ω** — it
matches the current DUT's R_opt. Change the default only; an existing `.charm` carries its own Z0 and
must open unchanged.

**Everywhere 50 is assumed rather than read must be found**, not assumed absent. Grep for `50.0` and
`z0 = 50` across `src/Harmonica`, `src/Ui/Harmonica`, `tests/Harmonica.Tests` and
`tests/Ui.Tests/Harmonica`. `ContourGrid`'s constructor default (`ContourGrid(double z0 = 50.0)`) is one
such site — a default argument that only ever gets overwritten by `Build` is harmless, but say whether
you left it or changed it. Tests that assert 50 Ω-specific numbers should be updated to construct their
own settings explicitly rather than leaning on the default.

---

## 6. Bug — the Set Termination dialog remaps a typed 200 Ω to 190 Ω

`src/Ui/Views/Dialogs/HarmonicaSetTerminationDialog.axaml.cs`.

### 6.1 The mechanism to check first

All three text boxes fire `TextChanged` on **every keystroke**, and each handler ends in `LoadFields()`,
which **rewrites all three boxes' `Text` — including the one the user is currently typing in**:

```csharp
private void OnZRealImagChanged(…)
{
    if (!HarmonicaReadoutFormatting.TryParse(ZRealImagBox.Text, …, out var z)) return;
    _z = z; _gamma = HarmonicaDataSet.GammaOf(z, _z0);
    LoadFields();                     // ← rewrites ZRealImagBox.Text under the caret
}
```

`FormatZ` returns `"200+j0 Ω"` — a reformat with a unit suffix and a `+j0` tail appended around the
caret. Depending on where Avalonia leaves `CaretIndex` after a programmatic `Text` set, subsequent
keystrokes land in the middle of the reformatted string, and a partially-typed value can be committed
as a different number than the one on screen. A round trip through `TryParse`/`FormatComplex` also
carries `0.###` rounding.

**Reproduce it before fixing it.** A headless test in `tests/Ui.Tests/Harmonica` can drive
`HarmonicaReadoutFormatting.TryParse`/`FormatZ` through the exact keystroke sequence the handler
implies (parse → format → insert next char → parse …) for the string `"200"`, starting from the
dialog's initial text for L1 in the shipped default document. If that reproduces 190, you have it. If
it does not, the caret behaviour is platform-side — say so, and drive the live dialog to confirm before
claiming a fix.

### 6.2 The fix

**Never rewrite the box the user is typing in.** On each keystroke: parse the *edited* box; on success
update the model values and refresh **only the other two** boxes; leave the edited box's text exactly
as typed. Reformat it once, on commit (OK) or on losing focus. Keep the existing "refuse silently and
leave the text alone" behaviour for un-parseable in-progress input — that part is right.

While you are there: the Z field must accept a bare `200` (no `+j0`, no `Ω`) and mean 200 + j0 —
`TryParseRectangular` already handles that, so this is a test, not a change.

**Gate:** a test that types each of `"200"`, `"200+j0"`, `"200 + j0 Ω"`, `"-25+j40"`, `"1e3"` character
by character through the dialog's handler contract and asserts the committed `TerminationEdit` is
exactly the typed value. Plus the owner typing 200 into L1 and getting 200.

---

## 7. Gates for this brief

1. `dotnet test tests/Ui.Tests` and `dotnet test tests/Harmonica.Tests` green (add `--no-build` after
   the first build). `tests/Firewall.Tests` green — nothing here may pull Avalonia into
   `src/Harmonica`, and §3 puts three new settings on the framework-free side, so this is a real check,
   not a formality.
2. The owner confirms, interactively: the docked menu bar shows four injected items and they all work;
   `Settings…` opens one dialog with both tabs from all three surfaces; a new document starts at
   3 × 12 and 80 Ω; typing 200 Ω into L1 gives 200 Ω.
3. Report which of §2.1's three Settings paths was dead and why.

**Do not update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` gets a section only if §1.2's diagnosis or
§6.1's mechanism turns out to be worth the next person's time.
