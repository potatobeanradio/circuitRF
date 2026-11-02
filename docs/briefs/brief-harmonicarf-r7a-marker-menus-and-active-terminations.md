# Brief — harmonicaRF R7A: active terminations, and every fly menu becomes a real context menu

**Read first:** `src/Ui/Views/Harmonica/HarmonicaView.axaml.cs` (`OnCanvasContextMenuOpening`,
`BuildMarkerMenu`, `BuildFormatRow`, `BuildSmithBodyMenu`, `BuildSmithTitleMenu`,
`BuildPowerSweepTitleMenu`, `BuildPowerSweepXUnitMenu`, `BuildCopyMenuItem`),
`src/Ui/Harmonica/IntrinsicGlyphScale.cs`, `src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs`
(`GammaToCanvas` / `CanvasToGamma` / `MarkerToCanvas` / `CanvasToMarker`, ~line 500–590),
`src/Ui/Harmonica/HarmonicaPointer.cs` (`~line 480–540`, the drag path),
`src/Ui/Views/Dialogs/HarmonicaSetTerminationDialog.axaml.cs`,
`src/Harmonica/HarmonicaDataSet.cs` (`GammaOf` / `ImpedanceOf`), and
`src/Ui/Views/WorkspaceWindow.axaml` (~line 254 onward) for the repo's existing `MenuItem.Icon`
convention.

**Do NOT update any `CLAUDE.md`.** Write to `src/Ui/RESOLVED.md` (or `src/Harmonica/RESOLVED.md`)
only if you find something genuinely worth recording — a trap, an invariant, a refuted premise.

Tag new comments `R7A §n`.

---

## 0. What the owner reported

> "When a marker is outside the Smith chart, (ie. Real part of Z is negative), the Set Termination
> dialog box is reporting incorrect gamma and Z. Also, the fly menu for the marker is reporting
> incorrect gamma."

> "Change all the harmonicaRF fly menus to context menus (with appropriate icons). This means that
> Locked and Autoscale menu's will need a dynamic icon that changes with state."

> "Cannot turn on VSWR using fly menu due to its 'Set' submenu. This needs to work somehow now that
> it will be a context menu."

---

## 1. The active-termination bug — it is the compressed radial scale saturating

### 1.1 What was measured (do not re-derive this — it is measured, not assumed)

A throwaway probe drove the real production path — `HarmonicaPanelRenderer.GammaToCanvas` to place a
pointer at a given *drawn* radius on the −real axis, then `CanvasToMarker` (what
`HarmonicaPointer.OnMove` actually calls for an `ExtrinsicMarker` drag) to turn that pixel back into
the Γ the marker is written with, then `HarmonicaDataSet.ImpedanceOf` at Z0 = 50 Ω:

| pointer at drawn radius | Γ written to the marker | Z reported | Γ after the Γ→Z→Γ round trip |
|---|---|---|---|
| 1.02 | −1.087 | −2.083 Ω | −1.087 (exact) |
| 1.10 | −1.667 | −12.5 Ω | −1.667 (exact) |
| 1.15 | −2.5 | −21.4 Ω | −2.5 (exact) |
| 1.20 | −5.0 | −33.3 Ω | −5.0 (exact) |
| 1.24 | −25.0 | −46.2 Ω | −25.0 (exact) |
| 1.245 | −50.0 | −48.0 Ω | −50.0 (exact) |
| **1.25** | **−1.000 000 028 × 10⁹** | **−50.000 Ω** | **−9.999 999 87 × 10⁸** (error ≈ 41) |
| 1.30 | −1.000 000 028 × 10⁹ | −50.000 Ω | identical to 1.25 |
| 1.60 | −1.000 000 028 × 10⁹ | −50.000 Ω | identical to 1.25 |

Everything the user sees comes from `marker.Gamma`: `BuildMarkerMenu`
(`HarmonicaView.axaml.cs:~1049`) formats `marker.Gamma` and `ImpedanceOf(marker.Gamma, z0)`;
`ShowMarkerSetDialogAsync` and `OnReadoutOpenSetDialogAsync` both pass `marker.Gamma` into
`HarmonicaSetTerminationDialog`; `HarmonicaSolver.BuildReadouts` shows
`ImpedanceOf(m.Gamma, z0)` on the strip. **All four are faithful reporters of a value that was
written wrong by the drag.**

### 1.2 The mechanism

`IntrinsicGlyphScale.DisplayRadius` maps the whole of `|Γ| ∈ [1, ∞)` into the annulus
`[1, 1 + margin)` with `margin = 0.25`, `rate = 1`. Its inverse, `TrueRadius`, therefore has a pole
at exactly `1 + margin`, and the code deliberately saturates instead of returning infinity:

```csharp
double u = Math.Min((displayRadius - 1.0) / m, 1.0 - 1e-9);
return 1.0 + u / (k * (1.0 - u));            // → 1e9 at the clamp
```

So **every pointer position at or beyond drawn radius 1.25 collapses to the same Γ ≈ −10⁹** — and
`ImpedanceOf` maps that to exactly `−Z0`. The marker stops moving on screen (correct — it is at the
edge of the annulus), but the number it reports is a pure artefact of the `1 - 1e-9` clamp. Worse,
that Γ does not even survive its own round trip: `Z = z0(1+Γ)/(1−Γ)` at |Γ| ~ 10⁹ is catastrophic
cancellation, so the Γ that comes back off the stored impedance differs from the Γ that went in by
~41. Anything that re-derives Γ from Z (the strip, a `.charm` reload, `RebuildMarkersFromTerminations`
at `HarmonicaViewModel.cs:2017`) will disagree with the menu.

The sensitivity below the clamp is also brutal and is part of what makes the numbers read as wrong: a
**one-tenth-of-a-radius** pointer move from drawn 1.10 to 1.20 moves Γ from −1.67 to −5.0.

### 1.3 What to fix

**(a) The saturation plateau must be unreachable.** Give `IntrinsicGlyphScale` a named public
constant for the largest reflection coefficient a marker may hold, and make `TrueRadius` saturate
*there* instead of at `1 - 1e-9`:

```csharp
/// <summary>R7A §1 — the largest |Γ| a pointer may write. …</summary>
public const double MaxTrueMagnitude = 10.0;
```

At |Γ| = 10 the impedance is `Z = z0(1−10)/(1+10) = −0.818·z0` (−40.9 Ω at 50 Ω, −65.5 Ω at the
document default 80 Ω), which is past every physically interesting active termination; and it is a
value that round-trips through Z to full double precision. Derive the clamp from `MaxTrueMagnitude`
rather than hard-coding a `u`, so the two can never drift:

```
u_max = k·(MaxTrueMagnitude − 1) / (1 + k·(MaxTrueMagnitude − 1))
u     = Math.Min((displayRadius − 1.0) / m, u_max)
```

Keep `DisplayRadius` unchanged — it is already monotone and bounded, and the glyph rendering depends
on it. The pair must remain exact inverses for `|Γ| ≤ MaxTrueMagnitude`; that is the test below.

**(b) Do not change `margin` or `rate`.** Both are shared with the *intrinsic* glyph (§4.5's own
machinery), and re-tuning them silently moves every intrinsic glyph on both charts. If you believe
the annulus is too sensitive to be usable, say so in your report with numbers — do not change it
here.

**(c) Verify — do not assume — that nothing else clamps.** `HarmonicaViewModel.SetMarkerGamma`
already had its own `0.999` clamp removed on purpose (see its doc comment) and must stay clamp-free.
`HarmonicaSetTerminationDialog.ApplyGammaEdit` still pulls its **preview** Z back to `|Γ| ≤ 0.999`
while the user types — that is deliberate (it keeps the Z row finite mid-keystroke) but it is now
*wrong for this brief's whole subject*: a user typing `Γ = -3` into the Γ box watches the Z row show
the Z of −0.999 instead of −25 Ω. Fix it: the preview should nudge only the true singularity, exactly
as `HarmonicaDataSet.ImpedanceOf` already does (`|1 − Γ| < 1e-12`), and otherwise pass Γ straight
through. **This is the second half of the owner's dialog complaint** and it is independent of (a).

**(d) The dialog must open showing what the marker actually holds.** With (a) and (c) done, opening
`Set Termination…` on a marker at Z = −25 Ω must show `Γ = -3.000+j0.000`, `Γ = 3.000∠180.0°`,
`Z = -25.000+j0.000 Ω`, and pressing OK with no edit must write that same impedance back unchanged.

### 1.4 Gate for §1

A pure test (no Avalonia runtime — see `tests/Ui.Tests/CircuitRF.Ui.Tests.csproj`'s own rule) in a new
`tests/Ui.Tests/Harmonica/HarmonicaActiveTerminationTests.cs`:

1. `IntrinsicGlyphScale.TrueRadius(DisplayRadius(r)) == r` to 1e-9 relative, for
   `r ∈ {0, 0.5, 1.0, 1.0001, 1.5, 3, 10}` — and `TrueRadius` of anything at or past
   `DisplayRadius(MaxTrueMagnitude)` equals `MaxTrueMagnitude` exactly.
2. Drive `HarmonicaPanelRenderer.CanvasToMarker(GammaToCanvas(new Complex(-r, 0), size), size)` for
   drawn radii `{1.02, 1.10, 1.20, 1.25, 1.6}` and assert every result has
   `|Γ| ≤ MaxTrueMagnitude` and `Re(ImpedanceOf(Γ, 50)) > -50`.
3. **The round-trip invariant, which is the one that actually pins the bug:** for each of those,
   `GammaOf(ImpedanceOf(Γ, z0), z0)` must equal Γ to 1e-9 relative. Today the 1.25 case fails this by
   ~41.
4. Through the view model: `vm.SetMarkerGamma(m, g)` then `GammaOf(Terminations.Z(side, band), z0)`
   must equal `m.Gamma` — the strip and the menu cannot disagree.

Also add the `Z = -25 Ω → Γ = -3` oracle to `HarmonicaSetTerminationDialogTests` at the
`ApplyGammaEdit`/preview seam if that logic is reachable without constructing the window; if it is
not, extract the preview arithmetic into a `static` helper on the dialog class (the pattern
`ReadoutStripView.SettingsRowMayBeOverwritten` already established for exactly this reason) and test
that.

---

## 2. Every fly menu becomes a context menu with icons

### 2.1 What is actually being asked for

The harmonicaRF menus are **already** `ContextMenu`s — `HarmonicaView.axaml`'s canvas menu, rebuilt
per right-click in `OnCanvasContextMenuOpening`. The codebase calls them "fly menus" throughout; the
owner's ask is the *presentation*: they must look like the rest of circuitRF's menus, which means
**icons**, and the two state toggles must show their state through the icon.

Do not rename anything. Do not restructure `OnCanvasContextMenuOpening`'s dispatch. This is an
appearance-and-behaviour pass over the item builders.

### 2.2 The icon convention already exists — use it

`src/Ui/CircuitRF.Ui.csproj:227` references `Material.Icons.Avalonia` 3.0.2, and
`src/Ui/Views/WorkspaceWindow.axaml` already uses it in XAML:

```xml
<MenuItem.Icon><mi:MaterialIcon Kind="PlusBox" Width="16" Height="16"/></MenuItem.Icon>
```

These menus are built in **code**, so the equivalent is:

```csharp
using Material.Icons;
using Material.Icons.Avalonia;

static MaterialIcon Icon(MaterialIconKind kind)
    => new() { Kind = kind, Width = 16, Height = 16 };
```

Add **one** private helper in `HarmonicaView.axaml.cs` that builds a `MenuItem` from
(header, icon kind, click handler) and route every item through it, rather than sprinkling
`Icon = …` at 30 call sites. A menu whose items are constructed two different ways drifts.

Suggested kinds (pick better ones if the set has them — this is a starting map, not a spec):

| item | `MaterialIconKind` |
|---|---|
| Copy | `ContentCopy` |
| Set… / Set VSWR… / Axis Limits… / DCIV Sweeps… | `Pencil` (or `Cog` for the dialogs) |
| Add Point | `PlusCircleOutline` |
| Add Points to VSWR | `PlusCircleMultipleOutline` |
| Remove *Ln* | `Delete` |
| Snap to Grid — on / off | `Magnet` / `MagnetOn` |
| Show Grid Points | `Grid` |
| Contour Plane / Contour Harmonic / Efficiency Metric | `ChartBellCurve` / `SineWave` / `Percent` |
| Power Sweep / Time Domain | `ChartLine` / `Waveform` |
| Γ = … / Z = … rows | `Omega` (or none — see §2.4) |

**Verify each `MaterialIconKind` name compiles.** The enum is large and the names above are from
memory; a wrong one is a build error, which is the good failure. If a kind does not exist, pick the
nearest that does and note the substitution in your report.

### 2.3 Locked and Autoscale — dynamic icons

Today (`HarmonicaView.axaml.cs:~903` for DCIV, `~982` for the power-sweep panel) both are
`ToggleType = MenuItemToggleType.CheckBox` with `IsChecked` set from
`Settings.DcivAutoscale` / `PowerSweepAutoscale` / `TimeDomainAutoscale`. They are mutually
exclusive: `Locked.IsChecked == !Autoscale.IsChecked`.

**Trap:** in Avalonia's Fluent `MenuItem` template the check glyph and the `Icon` compete for the same
leading slot. Setting both can produce a missing icon, a missing checkmark, or a doubled indent
depending on the theme. **Check this visually before choosing** (§4's screenshot gate covers it).

The owner has already chosen the resolution — "a dynamic icon that changes with state" — so:

- Drop `ToggleType` on these two items.
- `Autoscale`: `ArrowExpandAll` when it is the active mode, `ArrowExpandAll` at reduced opacity (or
  the outline variant) when it is not. Simplest legible rule: **the active one gets a filled icon,
  the inactive one gets its outline variant.**
- `Locked`: `Lock` when locked, `LockOpenVariant` when not.
- Keep both items always present and always clickable (clicking the already-active one is a harmless
  no-op that re-captures the current limits — that is what `LockDcivAxes` already does).

Apply the same treatment to the other `ToggleType` items in these menus **only if** the visual check
in §4 shows the checkmark and the icon conflicting. Otherwise leave `Show Grid Points`,
`Snap to Grid`, `Power Sweep`/`Time Domain`, `Contour Plane`'s children, `Contour Harmonic`'s
children and `Efficiency Metric`'s children as checkbox items with **no** icon — a checkmark is the
right affordance for a radio-like choice and an icon beside it is noise.

### 2.4 The VSWR bug — and the same defect on three more rows

`HarmonicaView.axaml.cs:~1067`:

```csharp
var vswr = new MenuItem
{
    Header      = HarmonicaReadoutFormatting.FormatVswr(marker.VswrValue),
    ToggleType  = MenuItemToggleType.CheckBox,
    IsChecked   = marker.VswrEnabled,
    ItemsSource = new object[] { vswrSet },      // ← "Set…"
};
vswr.Click += (_, _) => { h.ToggleMarkerVswrEnabled(marker); Refresh(); };
```

**A `MenuItem` that has children never raises `Click`.** Pointing at it opens the submenu; clicking
it opens the submenu. The toggle handler is dead code. That is the whole bug.

The identical defect is present on the three format rows built by `BuildFormatRow`
(`HarmonicaView.axaml.cs:~1260`) — `Γ = …`, `Γ = …∠…`, `Z = …` each carry a lone `Set…` child, so the
row itself is unclickable and the user must traverse a submenu to reach the only thing the row does.

**Fix — flatten all four. No submenus in the marker menu.**

```
 ⟨Ω⟩  Γ = -3.000+j0.000              →  opens Set Termination…, focused on Γ real/imag
 ⟨Ω⟩  Γ = 3.000∠180.0°               →  opens Set Termination…, focused on Γ mag/angle
 ⟨Ω⟩  Z = -25.000+j0.000 Ω           →  opens Set Termination…, focused on Z real/imag
 ─────────────────────────────────────
 ⟨✓⟩  VSWR circle                     →  toggles marker.VswrEnabled (checkbox, no children)
 ⟨✎⟩  Set VSWR… (2.00)                →  opens HarmonicaSetVswrDialog
 ⟨magnet⟩ Snap to Grid                →  toggle (unchanged)
 ⟨+⟩  Add Point                       →  unchanged
 ⟨+⟩  Add Points to VSWR              →  unchanged, still disabled with its tooltip when the circle is off
 ─────────────────────────────────────
 ⟨🗑⟩  Remove L2                      →  unchanged, still disabled with its reason on band 1
```

Notes that are load-bearing:

- The **value** stays visible. `HarmonicaReadoutFormatting.FormatVswr` produces `"VSWR: 2.00"`; put
  the number on the *Set* row (`"Set VSWR… (2.00)"`) or keep the toggle row's header as
  `FormatVswr(...)` and give the Set row a plain header — either is fine, but the number must not
  disappear, because r6b §2.1 made the menu double as the VSWR readout and a drag lands on it.
- `BuildFormatRow` loses its `ItemsSource` and gains a `Click` that calls
  `RunHook(() => ShowMarkerSetDialogAsync(h, marker, format))` directly. Its signature and its three
  call sites are otherwise unchanged.
- The submenus in the **Smith title** menu (`Contour Plane`, `Contour Harmonic`,
  `Efficiency Metric`) are legitimate — they are pickers with several children, and their parent has
  no action of its own. Leave them.

### 2.5 Gate for §2

`tests/Ui.Tests/Harmonica/HarmonicaR6eDialogsAndMenusTests.cs` already tests menu construction where
it can. Extend it (or add `HarmonicaR7aMenuTests`) with what is testable without a control tree:

- **A structural rule, asserted by scanning `HarmonicaView.axaml.cs`'s own source text:** no
  `MenuItem` initialiser in `BuildMarkerMenu`/`BuildFormatRow` sets both `Click` and `ItemsSource`.
  Strip comments before scanning — a source-scan test that reads commented-out code has bitten this
  repo before. This is a crude test and it is worth having anyway: it is the exact defect that
  shipped.
- Whatever `HarmonicaR6eDialogsAndMenusTests` already does for item ordering, updated for the new
  flat shape.

---

## 3. What is explicitly out of scope

- Do not touch `HarmonicaHitTest`, the drag gesture, or the z-order.
- Do not change the `.charm` format.
- Do not re-tune `IntrinsicGlyphScale.DefaultMargin` / `DefaultRate` (§1.3(b)).
- Do not convert the macOS `NativeMenu` (the app menu bar) to anything. See `src/Ui/CLAUDE.md`'s
  standing `NativeMenu` invariant — a window's `NativeMenu` instance is fixed for its lifetime, and
  R3A's crash came from forgetting it.

---

## 4. Gates

1. `dotnet test tests/Ui.Tests --no-build` and `dotnet test tests/Firewall.Tests --no-build` green
   (two invocations — this SDK rejects two project paths in one).
2. `dotnet test tests/Harmonica.Tests --no-build` green if that project's tests touch anything you
   changed.
3. The new `HarmonicaActiveTerminationTests` from §1.4, and it must **fail against the current code**
   before your fix. Show that in your report.
4. **Run the app and look at it** (`/run`, or `dotnet run --project src/Ui`). Screenshot: a marker
   dragged well outside the chart, with its context menu open and the Set Termination dialog beside
   it, showing agreeing finite numbers. Screenshot: the power-sweep panel's context menu showing
   Autoscale/Locked with their state icons, in both states. No test in this repo can see either of
   these — `tests/Ui.Tests` is forbidden from calling Avalonia runtime APIs — so the screenshot *is*
   the gate.
5. Report: what `MaxTrueMagnitude` you chose and the Z it corresponds to at Z0 = 50 and 80; which
   `MaterialIconKind` names you had to substitute; and whether the Fluent `MenuItem` template turned
   out to conflict between `Icon` and `ToggleType` (§2.3's trap) — that answer is worth recording in
   `src/Ui/RESOLVED.md`.
