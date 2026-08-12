# Brief — harmonicaRF Round 1A: the crash, the menu policy, and colour

**Read first:** `docs/design/harmonicarf.md` (**§3.1**, **§7.6**, **§7.9**), then `src/Ui/CLAUDE.md`'s
own **H7** and **H8** entries and `src/Harmonica/CLAUDE.md`. H4–H8 built the document, the panels, the
menus and the standalone binary; this is the first round of owner reports back from actually using it.

**Round 1 is three briefs and they are independent.** This one is chrome, menus and colour — the
crash, the macOS/Windows menu policy, the colour roles and every place a colour change fails to reach
the canvas. **1B** is the panels (drag, Z0, titles, DCIV, the power sweep). **1C** is the toolbar
removal, the readouts, Set DUT and the testbench export. Nothing here depends on either of those.

---

## 0. What already exists, and what is genuinely wrong

**Do not rebuild any of this.** If something below seems missing, it is a lookup you have not found —
ask before writing a second one.

| you need | it is here |
|---|---|
| the role vocabulary | `ColorRole.Harmonica*` (`src/Ui/Theming/ColorRole.cs`, lines 97–145) |
| the default colours, both variants | `ColorTheme.BuiltIn` (`src/Ui/Theming/ColorTheme.cs`, lines 93–115 light, 153–174 dark) |
| the Layer-2 token bundle | `HarmonicaRenderTheme` (`src/Ui/Renderers/HarmonicaRenderTheme.cs`) |
| `.charm` appearance ⇄ `ColorTheme` | `HarmonicaAppearanceBridge` (`src/Ui/Harmonica/HarmonicaAppearanceBridge.cs`) |
| the application's live theme | `ThemeService.Active` + `ThemeService.ThemeChanged` (`src/Ui/Theming/ThemeService.cs`) |
| the current OS variant | `ThemeService.CurrentVariant`, written by `App` on `OnActualThemeVariantChanged` |
| the colour editor dialog | `HarmonicaPreferencesDialog` + `HarmonicaColorEditor` |
| the iso-line alpha ramp | `IsoLineAlphaRamp` (`src/Ui/Harmonica/IsoLineAlphaRamp.cs`), applied in `HarmonicaPanelRenderer.DrawContours` |
| the fade parameters | `HarmonicaRenderTheme.IsoAlphaFloor` / `IsoAlphaExponent`, persisted in `CharmAppearance` |
| Copy Plot | `HarmonicaClipboard.CopyAsync` → `HarmonicaCanvasRenderer.DrawPanel` / `DrawAll` |
| the two menu surfaces | `HarmonicaMenuView.axaml` — a `NativeMenu.Menu` on the UserControl **and** an in-window `Menu` |
| the attach decision | `HarmonicaMenuView.AttachNativeMenuIfOwnWindow()` (`HarmonicaMenuView.axaml.cs`) |
| circuitRF's own shared-menu attach | `WorkspaceViewModel.AttachSharedNativeMenuIfMacOS` — the working precedent |

**Genuinely wrong, and this brief's work:** the detach crash; the whole macOS/Windows menu policy;
five dark-mode colours; two independent "colour change does not reach the canvas" bugs; the iso-line
fade shape; Copy Plot's background; and *Edit ▸ Preferences…* opening nothing.

---

## 1. The crash — `NativeMenu.SetMenu` on a menu that is already attached

### 1.1 What the owner reported

Launch circuitRF, *Tools ▸ harmonicaRF*, drag the tab out of the dock. Immediately:

```
Unhandled exception. System.ArgumentException: The menu being updated does not match. (Parameter 'menu')
   at Avalonia.Native.Interop.Impl.__MicroComIAvnMenuProxy.Update(IAvaloniaNativeFactory factory, NativeMenu menu)
   at Avalonia.Native.AvaloniaNativeMenuExporter.SetMenu(IAvnWindow avnWindow, NativeMenu menu)
   at Avalonia.Native.AvaloniaNativeMenuExporter.DoLayoutReset(Boolean forceUpdate)
   at Avalonia.Native.AvaloniaNativeMenuExporter.SetNativeMenu(NativeMenu menu)
   at Avalonia.Controls.NativeMenu.<>c.<.cctor>b__35_1(AvaloniaPropertyChangedEventArgs`1 args)
   ...
   at Avalonia.Controls.NativeMenu.SetMenu(AvaloniaObject o, NativeMenu menu)
   at CircuitRF.Ui.Views.Harmonica.HarmonicaMenuView.AttachNativeMenuIfOwnWindow()
   at CircuitRF.Ui.Views.Harmonica.HarmonicaMenuView.<.ctor>b__1_1(Object _, VisualTreeAttachmentEventArgs _)
```

**The owner asks whether this is the same bug as the recent wBond crash. It is not** — that one was a
hand-written `InitializeComponent` shadowing the generated one (`src/Ui/CLAUDE.md`'s own entry, fixed
2026-08-12, and `HarmonicaShellWindow`/`HarmonicaSetDutDialog` were both in that sweep). This is a
different mechanism and the stack trace names it precisely: `NativeMenu.SetMenu` on the window.
**Do not go looking for a shadowed `InitializeComponent`; there is not one.**

The owner also asks whether it is "related to being a scratch document". **It is not** — it fires on
`AttachedToVisualTree`, which happens for every harmonicaRF document, scratch or saved. It reproduces
on a saved `.charm` too. Say so in the completion note rather than leaving the question open.

### 1.2 The cause, stated so it is not re-derived

`HarmonicaMenuView.axaml` declares the `NativeMenu` as an attached property **on the UserControl**
(line 28, `<NativeMenu.Menu>`) — deliberately, because that is what gives its `{Binding}`s a
DataContext to resolve against (`src/Ui/CLAUDE.md`'s own note on `NativeMenu`/`NativeMenuItem` being
`AvaloniaObject`, not `StyledElement`). `AttachNativeMenuIfOwnWindow` then hands that **same
instance** to the hosting `Window` when the window is not a `WorkspaceWindow`.

One `NativeMenu` instance now belongs to two `AvaloniaObject`s, each of which drives its own
`AvaloniaNativeMenuExporter`. The second exporter's `Update` compares the menu against the one its
native proxy already owns, they differ, and it throws. **This is exactly why the schematic side works:
`WorkspaceViewModel.AttachSharedNativeMenuIfMacOS` attaches `WorkspaceWindow`'s own menu to a second
WINDOW — window-to-window, never control-to-window.**

### 1.3 R-h9a-1 — the menu is attached to exactly one object at a time

Whatever design §2 lands on, the invariant is: **at any instant a given `NativeMenu` instance is set
on at most one `AvaloniaObject`.** Detaching is `NativeMenu.SetMenu(o, null)`; do it before attaching
elsewhere, and do it on `DetachedFromVisualTree` so a closed window does not leave the menu owned by a
dead exporter.

If a per-surface instance turns out to be needed instead (one for the control, one for the window),
say so and build the second one **from the same view model** — never a second hand-mirrored XAML tree.
`HarmonicaMenuView.RebuildNativeBandMenus` already shows the shape: `NativeMenu` has no `ItemsSource`,
so the band submenus are filled in code from the SAME `SourceBands`/`LoadBands` collections the
in-window menu binds to.

---

## 2. The menu policy — the owner's own words, made concrete

> when harmonicaRF is compiled as its own app, the menu appears at the top of its window and no
> document tabs are allowed in its window. On macOS in the compiled app, only the native menu is ever
> used (and menu is never rendered inside the window). When harmonicaRF is used as a document inside
> the circuitRF app, on macOS, the harmonicaRF menu is shown as a native menu whenever a harmonicaRF
> document has focus. On Windows/Linux the menu is rendered within the harmonicaRF just as it does
> today.

### R-h9a-2 — the policy as a table

| host | platform | in-window `Menu` | `NativeMenu` |
|---|---|---|---|
| standalone binary (`CrfApp=harmonica`) | macOS | **hidden** | attached to its own `HarmonicaShellWindow` |
| standalone binary | Windows / Linux | **shown**, at the top of the window | not used |
| document inside circuitRF, docked | macOS | **hidden** | attached to `WorkspaceWindow` **while a harmonicaRF document has focus**, restored to circuitRF's own on focus loss |
| document inside circuitRF, docked | Windows / Linux | **shown**, as today | not used |
| document inside circuitRF, torn off | macOS | **hidden** | attached to that float's own window |
| document inside circuitRF, torn off | Windows / Linux | **shown**, as today | not used |

Two rows are new behaviour, not a fix: **on macOS the in-window menu must never render at all** (it is
`IsVisible="False"` today only by accident of nothing setting it), and **a DOCKED harmonicaRF document
on macOS must take over the app menu bar while it has focus** — today `AttachNativeMenuIfOwnWindow`
returns early for a `WorkspaceWindow`, so it never does.

### R-h9a-3 — focus tracking, and restoring circuitRF's menu

The docked-macOS row needs a focus hook. **The mechanism already exists and must be reused rather
than reinvented:** `WorkspaceViewModel` already tracks the active document
(`OnDocumentDockPropertyChanged` → `RaiseFileMenuEnablementChanged`, plus `TryWireWindowFocusTracking`
for torn-off windows) — see `src/Ui/CLAUDE.md`'s **§4B / R-menu-4** section, which is the authority on
per-window menu ownership in this codebase.

**Restoring is as load-bearing as attaching.** When focus leaves the harmonicaRF document, the
`WorkspaceWindow` must get circuitRF's own `NativeMenu` back — otherwise the user is left with
harmonicaRF's File menu over a schematic. Whatever restores it must be the SAME reference
`WorkspaceWindow.axaml` declares, not a rebuild.

### R-h9a-4 — "no document tabs in the standalone window"

The owner's first sentence also says the standalone window allows no document tabs. **Check before
changing anything:** `HarmonicaShellWindow` is already a plain `Window` hosting one
`HarmonicaView` — H8's own R-h8-8 ("several `.charm` files open as several WINDOWS, not tabs"). If it
is already tab-free, say so and change nothing. If a tab strip has crept in, remove it.

### R-h9a-5 — the two surfaces must stay hand-mirrored

`HarmonicaMenuView.axaml`'s own header comment says it, and there is an existing test asserting the
absence of a Simulate menu on both surfaces. Anything this brief moves must move on both.

---

## 3. Colour

### R-h9a-6 — dark mode goes to pure phosphor green for five roles plus the fundamental marker

In `ColorTheme.BuiltIn`'s **Dark** map (`src/Ui/Theming/ColorTheme.cs`, ~line 153), set these to
`new(0, 255, 0)`:

- `Harmonica.ReadoutText`
- `Harmonica.AxisText`
- `Harmonica.Isoline`
- `Harmonica.GainTrace`
- `Harmonica.DcivFamily`

and set **`Harmonica.MarkerBand1`** to `new(0, 255, 0)` in the Dark map too. Today it is
`(34, 177, 76)` in **both** maps, which is why the owner reports the f₀ termination markers do not
stand out from the iso-lines or the grid points.

**In LIGHT mode, `Harmonica.MarkerBand1` must also become brighter/more saturated green** so the same
contrast complaint is answered there. The owner did not name a value; pick one, state it in the
completion note, and keep it distinguishable from `Harmonica.GridPoint` (`60,150,90`) and
`Harmonica.Isoline` (`0,110,40`) — those are what it currently competes with.

**Bands 2–5 are not changed.** Which colour means "2f₀" is a harmonic-identity convention, identical
in both variants on purpose (`src/Ui/CLAUDE.md`'s own H4–H5 entry says so directly).

**There is an existing test asserting red is reserved to the loadline and the efficiency trace, with a
non-vacuity guard.** It must stay green. If a new value trips it, the value is wrong, not the test.

### R-h9a-7 — two new roles

| role | dark | light |
|---|---|---|
| `Harmonica.Messages` | `0, 90, 30` | `170, 205, 180` |
| `Harmonica.ProgressBar` | `0, 90, 30` | `170, 205, 180` |

Both are the owner's own values. Add the constants to `ColorRole`, the entries to **both**
`ColorTheme.BuiltIn` maps, the `ColorRole.All` list, and the corresponding
`HarmonicaRenderTheme` properties. **They are consumed by brief 1C** (the message line and the
solving progress bar) — this brief only creates them, so 1C has something to read. Adding a role is
purely additive: `ColorTheme.Resolve` falls back to `BuiltIn`, so an old `.charm` still opens.

`HarmonicaAppearanceBridge.Roles` is derived from `ColorRole.All` by prefix, so both roles become
editable in the Preferences colour list and persist in a `.charm` with no extra work — check that
`HarmonicaColorEditor.LabelFor` gives them a readable label.

---

## 4. The two places a colour change does not reach the canvas

These are **two independent bugs with two different causes.** Fix both; do not assume one fix covers
the other.

### R-h9a-8 — the system light/dark switch

`HarmonicaView.ApplyVariant()` reads `Application.Current.ActualThemeVariant` and writes
`HarmonicaViewModel.Variant`. **It is called from exactly one place: `OnDataContextChanged`.** Nothing
re-runs it when the OS variant changes, so an open harmonicaRF document keeps whichever variant it
happened to open with.

Subscribe to the variant change and re-apply. `App` already handles `OnActualThemeVariantChanged` and
writes `ThemeService.CurrentVariant`; either hook Avalonia's own `ActualThemeVariantChanged` on the
control, or ride `ThemeService`. Whichever you pick, **unsubscribe on detach** — this view already has
a symmetric subscribe/unsubscribe block in `OnDataContextChanged` and the new hook belongs in it.

`HarmonicaViewModel.OnVariantChanged` already raises `RedrawRequested`, so the repaint is free once
the variant actually moves.

### R-h9a-9 — the circuitRF Settings dialog

`HarmonicaViewModel.RenderTheme` is:

```csharp
public HarmonicaRenderTheme RenderTheme
    => HarmonicaAppearanceBridge.ToRenderTheme(Appearance, Variant);
```

and `ToRenderTheme`'s `baseTheme` parameter defaults to **`ColorTheme.BuiltIn`**. The application's
live theme is `ThemeService.Active`, which is what the Settings dialog writes. **So a Settings colour
edit is structurally invisible to harmonicaRF** — nothing is subscribed and nothing would read it if
it were.

Two changes, both needed:

1. Pass `ThemeService.Active` as the base theme, so a `.charm`'s own appearance overlays the
   application theme rather than the built-in defaults. `ToColorTheme` already starts from the basis's
   own maps and overlays only what the file stated — R-h45-12's "absent means default, by
   construction" still holds, the default is simply now the user's theme.
2. Subscribe to `ThemeService.ThemeChanged` and raise `RedrawRequested`. `RenderTheme` is a computed
   property, so nothing needs invalidating beyond the canvas.

**R-h45-11 must survive this and is the thing most at risk.** A colour change may re-project the token
struct and invalidate the canvas — and **nothing else**: no re-solve, no re-fit, no contour-cache or
RBF-factorization invalidation. `HarmonicaColorEditorTests` already gates 20 recolours through the
editor with `SolveCount`/`FactorizationCount`/`RebuildCount` unmoved and a negative control proving
those counters CAN move. **Extend that gate to cover a `ThemeService.Active` change and a variant
change**, or the new subscriptions are one careless line away from making a colour change re-solve.

### R-h9a-10 — the standalone binary has no Settings dialog, and that is fine

`HarmonicaApp` sets `ThemeResolver.SetBuiltInProvider` and the saved theme at startup (H8's R-h8-7),
so `ThemeService.Active` is populated there too. Reading it is correct in both binaries; nothing needs
a `#if` or a host check.

---

## 5. The iso-line fade

### R-h9a-11 — steeper at the bottom, exactly opaque at the top

The owner: *"The amount of isoline color fading needs to be increased for the lower levels. The lowest
isoline should only be barely visible to human eye. If the fader is currently linear, perhaps change
to fader to exponential. The highest level needs to have no fading at all (alpha = 255)."*

The ramp lives in `IsoLineAlphaRamp.AlphaByte(rank, count, floor, exponent)` and is already
**ranked, not value-proportional** (H4–H5's own finding — a proportional ramp crushed a long low tail
to α = 0.25 across nine levels). It already carries an exponent. **Read it before changing anything
and report what shape it actually has** — the owner's "if the fader is currently linear" is a guess,
not a statement.

Two requirements, whatever the current shape:

- **α of the TOP level is exactly 255**, not "close to". There is an existing assertion for this
  (H4–H5's note says "α of the top level is **exactly** 1.0, not merely close") — keep it exact.
- **α of the BOTTOM level is barely visible.** `HarmonicaRenderTheme.DefaultIsoAlphaFloor` is `0.25`
  and `DefaultIsoAlphaExponent` is `1.5`. Lower the floor and/or raise the exponent until the lowest
  level reads as barely-there against `Harmonica.Background` in **both** variants. State the two new
  defaults and why in the completion note.

**Do not remove either knob.** §7.9.4 puts them in the theme precisely so a user who dislikes the fade
can flatten it (`α_floor = 1`) without a code change, and they persist in `CharmAppearance`. Changing
a default does not move a `.charm` that stated its own value — check that, because
`CharmAppearance.IsoAlphaFloor` is nullable and null means "take the default".

**Note the composition rule that already exists** (`HarmonicaPanelRenderer.ScaleAlpha`): the role's own
alpha and the ramp's multiply, so a role a user made translucent stays translucent. A top level at
ramp-α 255 is still only as opaque as `Harmonica.Isoline`'s own alpha. That is correct; do not "fix"
it.

---

## 6. Copy / Paste rendering — transparent background

### R-h9a-12

The owner: *"the Copy/Paste rendering of the Smith Chart's need to have transparent background."*

`HarmonicaCanvasRenderer.FillBackground` paints `theme.Background` over the target rect. It is called
by the live canvas's draw operation **and** by `HarmonicaClipboard`'s export path, which is why a
pasted plot arrives with a near-black rectangle behind it.

The layout editor already solved this exact problem: `LayoutRenderOptions.TransparentBackground`
(see `src/Ui/CLAUDE.md`'s **L1f / R-L1f-5** entry) — when set, the renderer skips its background fill
entirely and the destination surface is expected to arrive transparent (`SKBitmap.Erase(SKColors
.Transparent)` for the bitmap path; a fresh PDF page or SVG canvas starts blank). **Mirror that, do not
invent a second mechanism.**

Three things to get right:

- **Every export surface must be cleared to transparent first.** `HarmonicaClipboard` writes PDF, SVG
  and a bitmap; check all three.
- **The live canvas must be unaffected.** It still fills its own rect — `canvas.Clear` is forbidden
  here (Src blend replaces the whole leased region and wipes sibling controls; this codebase has
  shipped that bug twice and both `HarmonicaCanvasRenderer.FillBackground` and `LayoutRenderer` carry
  the warning).
- **`PlotRenderer.Draw` also paints a background** from `RenderTheme.BackgroundColor`. Check whether
  it does so unconditionally; if it does, transparent export needs it suppressed too, and that is a
  `HarmonicaRenderTheme.ToPlotTheme` decision rather than a change to the shared Data Display
  renderer. **Do not widen `PlotRenderer` to solve a harmonicaRF problem** — the same rule
  `AnnulusHeadroom` already follows.

---

## 7. *Edit ▸ Preferences…* opens nothing

### R-h9a-13 — first make the failure visible, then fix it

The hook IS wired: `HarmonicaView.WireMenuHooks` sets `menus.PreferencesHook = () => _ =
ShowPreferencesAsync();`, and `ShowPreferencesAsync` constructs `HarmonicaPreferencesDialog` and calls
`ShowDialog`.

**Every one of the fifteen hooks in that method is `() => _ = SomethingAsync();`** — a discarded
`Task`. An exception thrown anywhere inside (including synchronously, in a dialog constructor) is
captured into that task and **silently swallowed**. From the user's side that is indistinguishable
from a dead menu item, which is exactly what was reported.

So:

1. **Give every discarded-task hook a failure path.** One shared helper that awaits and routes the
   exception message into `HarmonicaViewModel.SolveError` (which the status strip already shows, and
   which brief 1C moves to the new message line) is enough. This is not optional polish — with it,
   the next "menu item does nothing" report arrives with a message attached.
2. **Then find the real cause and report it.** It may be `TopLevel.GetTopLevel(this) is not Window`,
   a throwing constructor, or something else. Do not guess in the completion note — say what it was.

**`ShowSetDutAsync` has the identical shape and the owner reports the identical symptom** (*"File ->
Set DUT menu command does not do anything"*). Fixing the swallow here fixes the diagnosis for both.
The Set DUT feature work itself is **brief 1C's** — this brief only owes the visible failure.

---

## 8. Scope guardrails

- No panel/chart changes — no Smith titles, no Z0, no DCIV, no power-sweep axis (**1B**).
- No toolbar removal, no readout redesign, no message line, no progress bar, no Set DUT feature work,
  no testbench export change (**1C**). This brief only adds the two new colour roles 1C consumes.
- **`ColorRole.All`'s existing entries keep their names.** A role name is a `.charm` file format key
  (`CharmAppearance`'s maps are keyed by role string). Renaming one silently drops that colour for
  every existing file.
- **No `FormatVersion` bump.** Everything here is additive by the absent-takes-the-default rule
  `CharmIo` already enforces.
- `src/Core`, `src/Engine`, `RfCore` untouched. `src/Harmonica` untouched — every change is in
  `src/Ui`.

---

## 9. Gates

1. **Build + `dotnet test` green.** Scope to `tests/Ui.Tests` and `tests/Harmonica.Tests` while
   working; run the full solution at the end.
2. **The crash is gone.** Tools ▸ harmonicaRF, drag the tab out, and the app survives. A `Window`
   subclass cannot be constructed headlessly, so pin the invariant instead: a test (or, failing that,
   a source scan of the kind `InitializeComponentShadowingTests` already uses) asserting the menu is
   never `SetMenu`-ed onto a second object without being detached from the first.
3. **The menu policy holds on both platforms**, all six rows of R-h9a-2's table. The macOS rows need
   interactive confirmation; the Windows/Linux rows can be pinned structurally.
4. **A docked harmonicaRF document on macOS takes the menu bar on focus and gives it back on blur**,
   with circuitRF's own File/Edit/View/Simulate/Help restored — not left empty.
5. **The five dark roles and both `MarkerBand1` values read as specified**, asserted against
   `ColorTheme.BuiltIn` directly. The reserved-red test still passes.
6. **`Harmonica.Messages` and `Harmonica.ProgressBar` exist in both variants**, appear in
   `HarmonicaAppearanceBridge.Roles`, and round-trip through a `.charm`.
7. **Switching the OS theme repaints an open harmonicaRF document**, and **a colour change in the
   circuitRF Settings dialog repaints it**, both without a re-solve — the R-h45-11 counter gate,
   extended, with its negative control intact.
8. **The lowest iso-line is barely visible and the highest is exactly α = 255**, in both variants.
9. **A copied plot pastes into Keynote/PowerPoint with no background rectangle**, while the live canvas
   still paints its own background and no sibling control is wiped.
10. **Edit ▸ Preferences… opens the dialog**, and if it cannot, says why in the message strip rather
    than doing nothing.

**Interactive verification is required for the menu policy, the crash and the clipboard paste** — this
environment has no visual driver, matching every prior harmonicaRF phase. List those three explicitly
in the completion note as "please confirm on your end", with the exact gestures.
