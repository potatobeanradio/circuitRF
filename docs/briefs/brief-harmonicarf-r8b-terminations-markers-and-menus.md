# Brief — harmonicaRF R8B: terminations, the marker Γ, and the context menus

**Read first, in this order:**
`src/Ui/Views/Dialogs/HarmonicaSetTerminationDialog.axaml.cs` (all 183 lines — every doc comment in it
is the record of a previous failed attempt at §1) and its `.axaml`,
`src/Ui/Harmonica/HarmonicaReadoutFormatting.cs:232–296` (`TryParse` and its two shapes),
`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs:503–585` (`GammaToCanvas`, `CanvasToGamma`,
`MarkerToCanvas`, `CanvasToMarker`) and `:795–870` (`DrawMarkers`, `DrawVswrLocus`),
`src/Ui/Harmonica/IntrinsicGlyphScale.cs` (all of it),
`src/Ui/Harmonica/HarmonicaPointer.cs:150–215` (the four hit-test passes) and `:478–545` (`Apply`),
`src/Ui/Harmonica/HarmonicaVswrHandle.cs` (all of it — its header is the geometry this brief needs),
`src/Ui/Harmonica/HarmonicaViewModel.cs:35–66` (the default marker set), `:405–480`
(`SetMarkerImpedance`, `AddMarkerBand`, `RemoveMarkerBand`), `:535–560` (`SetMarkerVswr`),
`src/Ui/Views/Harmonica/HarmonicaView.axaml.cs:1010–1260` (every menu builder) and `:1356–1360`
(`BuildFormatRow`),
`src/Ui/Views/DataDisplay/MarkerInfoBoxView.axaml.cs:120–225` — **this is the reference implementation
the owner is pointing at; read it before writing a single menu line**,
`src/Ui/Views/Dialogs/HarmonicaSetVswrDialog.axaml`,
`src/Harmonica/CircuitModel.cs:205–260` (`TerminationSet`, `UnmarkedBandOhms`).

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` only. **No screenshot verification.**

Tag new comments `R8B §n`.

---

## 0. Read this first — §1 is the fourth report of one bug

> "I still can't type 50 in the Z of the Set Termination dialog. Seriously, I have listed this as a
> bug 3 times to fix and it's still not fixed. **The validation system is BROKEN!** Fix it."

He is right, and the reason is visible in the test file. `tests/Ui.Tests/Harmonica/
HarmonicaSetTerminationDialogTests.cs`'s own header says it:

> "HarmonicaSetTerminationDialog is a Window and cannot be constructed headlessly in this suite … So
> this drives the dialog's ACTUAL parse/format functions … **through hand-built simulations of the OLD
> and NEW handler shapes**, rather than a real TextBox."

Three fixes have shipped against a **hand-written model of the handler**, and each was verified by
asserting that the model behaves. None of them could observe the control. The comment even concedes it
— *"this file cannot pin the literal '200 → 190' figure"*. **That is what "the validation system is
broken" means, literally, and §1.3 is the part of this brief that fixes it.** Do not ship a fourth
patch to the handlers with a fifth simulation behind it.

---

## 1. The Set Termination dialog

### 1.1 The mechanism, stated precisely

Three `TextBox`es (`GammaRealImagBox`, `GammaMagAngleBox`, `ZRealImagBox`) are kept in sync by having
each one's `TextChanged` handler **write the other two's `Text`** (`LoadFields(except: edited)`).
Re-entrancy is held off by a single `bool _loading` set inside `LoadFields` and cleared in its
`finally`.

`_loading` is a **window in time**, not a statement about identity. It is correct only if every
`TextChanged` raised by a programmatic `Text` write lands *inside* that `try`. The moment one lands
after the `finally` — a deferred raise, a re-entrant write, an IME/composition commit, a binding
round-trip — the sibling box's echo is processed as if the **user** had typed it:

```
user types "5" in Z
  → OnZRealImagChanged: _z = 5,  _gamma = GammaOf(5, 50) = −0.818
  → LoadFields(except: Z) writes both Γ boxes
      → (echo, guarded or not) OnGammaRealImagChanged fires with "−0.818+j0.000"
          → _lastEditWasGamma = TRUE          ← the user typed Z, not Γ
          → _z = ImpedanceOf(−0.818, 50)
          → LoadFields(except: GammaRealImagBox) REWRITES ZRealImagBox   ← the box being typed in
```

Both consequences match the report exactly: the Z box is rewritten under the caret so the next
keystroke lands in the wrong place ("I can't type 50"), and **OK commits through `SetMarkerGamma`
instead of `SetMarkerImpedance`** because `_lastEditWasGamma` was flipped by an echo. `TryParse("50",
RealImaginary)` returns `50+j0` correctly — verified by reading `TryParseRectangular`; the parse is
not the bug, and no amount of further work on `HarmonicaReadoutFormatting` will touch this.

### 1.2 The fix: ownership, not a flag

Replace `_loading` with an **identity** test that is true or false regardless of when the echo arrives.

- Add `private TextBox? _editing;`. Set it in a `GotFocus` handler on each of the three boxes; clear it
  in `LostFocus` (before `OnFieldLostFocus`'s reformat, so the reformat's own echo is also disowned).
- **Every one of the three `TextChanged` handlers begins with**
  `if (!ReferenceEquals(sender, _editing)) return;` — replacing the `if (_loading) return;` line.
- `LoadFields` no longer needs `_loading` at all. Keep its `except:` parameter — never writing the
  edited box is still correct and still the R6A §6 fix — but it is now a second line of defence rather
  than the only one.

A `TextChanged` on a box the user is not focused in can no longer move the model, no matter how it was
raised. That is the property `_loading` was reaching for and could not express.

**Leave the XAML alone.** `OK`'s `IsDefault="True"` was checked and is not implicated — Enter commits,
which is correct — and neither is `TryParse`. The defect is entirely in the four handlers and the flag.

### 1.3 Make it testable, or this returns a fifth time

Extract the whole edit state machine into a plain class with **no Avalonia reference**, in
`src/Ui/Harmonica/`:

```csharp
public sealed class TerminationEditModel      // R8B §1.3
{
    public TerminationEditModel(Complex initialGamma, double z0);
    public Complex Gamma { get; }
    public Complex Z     { get; }
    public bool    LastEditWasGamma { get; }

    /// Which field the user is in. Null = nobody; every Edit call for a different field is IGNORED.
    public TerminationField? Editing { get; set; }

    /// Returns true if the text parsed and the model moved.
    public bool Edit(TerminationField field, string? text);

    /// The text a field should DISPLAY right now. Never called for Editing.
    public string TextFor(TerminationField field);

    public TerminationEdit Commit();      // the existing (Impedance?, Gamma?) record
}
public enum TerminationField { GammaRealImag, GammaMagAngle, ZRealImag }
```

The dialog becomes a thin shell: `GotFocus` sets `Editing`, `TextChanged` calls `Edit(field, box.Text)`
and then writes `TextFor(...)` into the two boxes that are not `Editing`, `OnOkClick` returns
`Commit()`. **The dialog holds no state of its own** — no `_z`, no `_gamma`, no `_lastEditWasGamma`, no
`_loading`.

Now the tests are real, not simulations. `TerminationEditModelTests` must include, at minimum:

- **The owner's case.** `Editing = ZRealImag`; `Edit(ZRealImag, "5")`, `Edit(ZRealImag, "50")`;
  `Commit()` returns `Impedance = 50+j0`, `Gamma = null`. Repeat character-by-character for `"200"`,
  `"12.5"`, `"50-j10"`, `"1e3"`.
- **The echo, made explicit.** With `Editing = ZRealImag`, call `Edit(GammaRealImag, "...")` — the
  exact call an echo makes — and assert the model **did not move** and `LastEditWasGamma` is still
  false. This is the test no previous round could write.
- `TextFor(ZRealImag)` is never consulted while `Editing == ZRealImag` — enforce it by having
  `TextFor` throw on the editing field, and assert the throw. A silent wrong answer here is how the
  bug came back twice.
- Deleting precision holds: `Edit(ZRealImag, "158")`, then `Editing = null`; `TextFor(ZRealImag)` may
  reformat to `158.000 Ω`, but `Commit()` still carries 158.0 — the R6A follow-up's own case,
  preserved.
- An un-parseable in-progress string (`"0.5+j"`, `""`, `"-"`) returns false and leaves the model
  exactly where it was.

`PreviewImpedance` folds into the model (it is `HarmonicaDataSet.ImpedanceOf`, per R7A §1.3(c)); keep
its existing test by re-pointing it, do not delete the assertion that |Γ| > 1 passes through unclamped.

---

## 2. The marker Γ is exactly where the pointer is

> "This should be a very basic calculation, and I suspect you've been overthinking this. Keep it
> simple: User moves marker on a gamma plane with real and imaginary world coordinates. Simple and
> done. Then calculate Z from that value based on the Z0."

### 2.1 What is actually happening

An **extrinsic termination marker** is drawn, hit-tested and dragged through
`IntrinsicGlyphScale`'s compressed radial map:

```csharp
public static SKPoint MarkerToCanvas(Complex gamma, (double W,double H) size)
    => GammaToCanvas(IntrinsicGlyphScale.DisplayPosition(gamma), size);          // :578
public static Complex CanvasToMarker(SKPoint canvas, (double W,double H) size)
    => IntrinsicGlyphScale.TruePosition(CanvasToGamma(canvas, size));            // :581
```

That map was invented for the **intrinsic glyph** (R-h45-4: `|Γ_intr|` is unbounded, so it is squeezed
into a bounded annulus outside the rim). It was then applied to the termination marker too. Inside the
unit disc it is the identity, so most of the time it is invisible — and outside it, it is why the
number does not match the pointer.

**It is already provably inconsistent inside this repo**, and the code says so in two places:

- `DrawMarkers` draws the marker at `IntrinsicGlyphScale.DisplayPosition(m.Gamma)` (`:816`) but calls
  `DrawVswrLocus` on the line above, which draws the marker's own VSWR circle through
  `tf.PrimaryToCanvas(pts[i]...)` on **raw** Γ (`:865–869`).
- The hit-test says it outright at Pass 2.5: *"Tested through the SAME raw-Gamma transform the locus is
  drawn with (`GammaToCanvas`, never `MarkerToCanvas` — the locus is not on the compressed intrinsic
  scale)."*

So for any active marker (`|Γ| > 1`, ordinary since R7A) **the marker glyph and its own VSWR circle are
drawn on two different radial mappings** — the circle is not centred on, and does not pass around, the
marker as painted. That is visible, wrong, and is what §7.3 below is also about.

### 2.2 The change

`MarkerToCanvas` / `CanvasToMarker` become pass-throughs to `GammaToCanvas` / `CanvasToGamma` — the
plain affine chart map, nothing else — **for extrinsic termination markers**. Concretely: delete both
wrappers and have the extrinsic call sites use `GammaToCanvas`/`CanvasToGamma` directly, so no future
reader can reintroduce the composition by "reusing the marker helper".

Call sites to re-point (grep `MarkerToCanvas|CanvasToMarker` — the list is short):

| site | now | becomes |
|---|---|---|
| `HarmonicaPointer` Pass 1, `m.Gamma` (`:166`) | `MarkerToCanvas` | `GammaToCanvas` |
| `HarmonicaPointer` Pass 2, `m.GammaIntrinsic` (`:181`) | `MarkerToCanvas` | **stays compressed** — write it as `GammaToCanvas(IntrinsicGlyphScale.DisplayPosition(m.GammaIntrinsic), size)` |
| `HarmonicaPointer.Apply` (`:485`) `CanvasToMarker` | inverse-compressed | `CanvasToGamma` for the `ExtrinsicMarker` branch; the `IntrinsicGlyph` branch keeps `IntrinsicGlyphScale.TruePosition(CanvasToGamma(...))` |
| `HarmonicaPanelRenderer.DrawMarkers` (`:816`) | `DisplayPosition(m.Gamma)` | `m.Gamma` unchanged |

`IntrinsicGlyphScale` itself, `MaxTrueMagnitude`, `TrueRadius`, `IsCompressed`, `DisplayRadius` — **all
stay**, used by the intrinsic glyph and by `DrawReachableRegion` (which must keep matching the glyph;
do not touch it).

### 2.3 The two consequences, both intended

1. **An active marker now draws where it truly is, and can leave the panel.** Γ = 1.4 lands 40% past
   the rim. The owner already took this trade once, for the intrinsic glyph, when he set
   `AnnulusHeadroom = 0` — read that constant's own comment (`HarmonicaPanelRenderer.cs:496–501`):
   *"accept that a sufficiently far-out intrinsic glyph can be clipped again."* Same ruling, applied
   to the marker. `DrawMarkers` is **not** inside a `ClipRect` (only `DrawReachableRegion` is), so an
   off-rim marker still paints into the panel's own bounds; keep it that way.
2. **`MaxTrueMagnitude`'s saturation stops applying to a drag.** With no compression, the largest Γ a
   pointer can express is whatever the panel extent reaches (~1.3 at the chart margins) — a hard,
   obvious, self-explaining bound instead of a soft one at Γ = 10. `ExtrinsicIsOutsideUnitCircle` and
   its hatched outline are untouched and still flag the case.

### 2.4 Tests

`HarmonicaMarkerGammaTests`, routine tier, pure arithmetic:

- Round trip: for Γ ∈ {0, 0.5+j0.2, −0.9, 0.999, 1.2−j0.4, 2.0}, `CanvasToGamma(GammaToCanvas(Γ, size),
  size)` returns Γ to 1e-9. **The 1.2 and 2.0 cases fail today** — state that in the comment.
- Z agreement: `HarmonicaDataSet.ImpedanceOf(CanvasToGamma(p, size), z0)` equals the readout strip's
  own `Z{marker}` row value for the same pointer position, at Z0 = 50 and Z0 = 12.
- Marker/locus agreement (the §2.1 defect): for a marker at Γ = 1.2 with VSWR = 2, every sample of
  `LoadpullSurface.VswrLocus` projected through `GammaToCanvas` is within one grab radius of the
  circle whose centre is the **drawn marker position**. Today the drawn marker is ~0.24 Γ-units away
  from where the locus says it is.

---

## 3. S1 and S2 are off by default, and S1 is 50 Ω

> "By default, S1 and S2 termination markers are turned off. User must turn them on from the menu to
> activate them. Also, set S1 to be ZS1=50 ohms by default. (This matches the input impedance of the
> default DUT.)"

### 3.1 The trap: an unmarked band is a near-SHORT

`TerminationSet.Z` (`CircuitModel.cs:253`) answers `UnmarkedBandOhms` = **1e-6 Ω** for any band with no
entry. So simply deleting the S1 marker gives the DUT a 1 µΩ source — not 50 Ω, and not "no source".
The circuit would change the moment the marker went away, which is the exact thing `AddMarkerBand`'s
own comment says must never happen (*"adding a marker does not itself change the circuit"*).

So the two halves of the item are one change:

- **The default `TerminationSet` gains an explicit `Source` band-1 entry of 50 Ω**, written even though
  no marker exists for it. The `Terminations` set is what the engine reads; markers are a *view* onto
  it. That is already the model's stated design (`AddMarkerBand`: *"two sources for 'what is band 2
  terminated in' drift the moment either is written without the other"*) — this is the first case where
  a termination exists with no marker on it, and it is legitimate.
- **Source band 2 keeps `UnmarkedBandOhms`**, as it does today, because that is what an unmarked
  harmonic band means and S2 was already at the epsilon value.

### 3.2 The default marker set

`HarmonicaViewModel`'s constructor (`:35–66`) today builds S1, S2, L1, L2, L3. It becomes:

```csharp
// R8B §3 — S1/S2 start with NO marker. The Source band-1 termination is still written (50 Ω,
// matching the default DUT's own input impedance) so removing the marker does not change the
// circuit; band 2 keeps TerminationSet.UnmarkedBandOhms exactly as before.
Terminations.Set(TerminationSide.Source, 1, new Complex(50.0, 0.0));
SetMarkerImpedance(Markers[0] /* L1 */, new Complex(80, 10));
```

L1 = 80+j10 and the L2/L3 unmarked-epsilon markers are **unchanged**; only the two source markers go.
The `HarmonicaCount >= 3` assertion stays. The old `SetMarkerImpedance(Markers[0], new Complex(25, 0))`
line (S1 at 25 Ω) is deleted with the marker.

Then check every consumer that assumed a source marker exists:

- `HarmonicaSolver.BuildReadouts` emits a "Source" header row and then iterates
  `markers.Where(m => m.Side == Source)`. With none, the chunk is a bare header. **Keep the header** —
  a missing chunk reads as "broken", a header with nothing under it reads as "none set", and R7C §1.4
  already established that a column's row shape must not collapse. **Do not add a ghost
  `ZS1 … (no marker)` row** — the owner asked for the marker to be off, and a row that shows a
  termination nobody can see on the chart is a second story about the same thing. A bare header, whose
  tooltip reads "No source markers — right-click the Smith chart to add one."
- `HarmonicaViewModel.RebuildMarkersFromTerminations` (the load path) is untouched — a `.charm` that
  carries source markers still restores them. **Only a brand-new document is affected.** Verify that
  by test (§3.4), because "we changed the default" quietly becoming "we changed every file" is the
  failure mode here.
- Anything that indexes `Markers[0]` positionally. Grep for it; `Markers[0]`/`Markers[1]` appear in the
  constructor and must be re-derived from the new order (L1 is now first).

### 3.3 Band 1 must become removable on the source side

`RemoveMarkerBand` refuses band 1 outright (`:465`, `if (band == 1) return false;`), and
`BuildMarkerMenu` greys "Remove S1/L1" with *"is the fundamental and is always present."* That is no
longer true for the source. Change the rule to: **band 1 is removable; removing it leaves the
termination entry in place rather than calling `Terminations.Remove`.**

```csharp
public bool RemoveMarkerBand(TerminationSideKind side, int band)
{
    var marker = Markers.FirstOrDefault(...);
    if (marker is null) return false;
    // R8B §3.3 — a fundamental marker may be removed, but its TERMINATION stays: removing the
    // VIEW of a termination must never change the circuit (AddMarkerBand's own invariant, read
    // backwards). Bands ≥ 2 still drop their entry — there, absence IS the unmarked value.
    if (band != 1) Terminations.Remove(engineSide, band);
    Markers.Remove(marker);
    ...
}
```

Apply it on **both** sides, not just the source — one rule, and a user who removes L1 gets the same
"the circuit did not move" guarantee. Update the menu's disabled-reason text accordingly (it becomes
always enabled).

### 3.4 Tests

`HarmonicaDefaultMarkerSetTests`: a fresh `HarmonicaViewModel` has `Markers.Count == 3`, none with
`Side == Source`; `Terminations.Z(Source, 1) == 50+j0`; `Terminations.Z(Source, 2) ==
UnmarkedBandOhms`. Then `AddMarkerBand(Source, 1)` produces a marker whose Γ is exactly
`GammaOf(50, Z0)` and **`Terminations` is byte-for-byte unchanged** by the add. Then
`RemoveMarkerBand(Source, 1)` returns true and again leaves `Terminations` unchanged. Separately: load a
`.charm` fixture that carries S1/S2 and assert both markers come back — the "existing files are
unaffected" claim.

---

## 4. Add Load Marker / Add Source Marker

New items on the **Smith panel body** menu (`BuildSmithBodyMenu`, `HarmonicaView.axaml.cs:1240–1260`),
above the existing "Show Grid Points":

```
Add Load Marker          ← adds the next unused LOAD band, lowest first
Add Source Marker        ← same, source side
Show Grid Points         (existing)
─────────────
Copy                     (existing — stays LAST, per the standing rule)
```

Semantics, stated so there is nothing to guess:

- "Next higher marker termination available" = the **lowest band ≥ 1 on that side that has no marker
  yet**. With S1 and S2 both absent (§3's new default) "Add Source Marker" adds **S1** first, then S2.
- **Disabled** when that band would exceed the HB order — `Terminations.HarmonicCount`, which is what
  `AddMarkerBand` itself throws on (`:432`). Disabled with a stated reason in the tooltip
  (*"All load bands up to the harmonic order (K = 3) already have markers."*), never hidden — R13a's
  standing rule, which every other item in these menus already follows.
- The item goes through **`AddMarkerBand`**, unchanged. It already returns the existing marker if there
  is one and already seeds from `Terminations.Z`, so nothing new is needed on the view-model.
- Built with `Item(...)` so it carries an icon like every other action row:
  `MaterialIconKind.PlusCircleOutline` for both (matching "Add Point", which is the same gesture class).

Test (`HarmonicaAddMarkerMenuTests`, in the shape of the existing `HarmonicaR6eDialogsAndMenusTests`):
on a fresh document, the next source band is 1; after adding, it is 2; after adding again the item is
disabled and `Markers` has not grown. Assert against the **selection function**, extracted as an
`internal static int? NextUnusedBand(IEnumerable<HarmonicaMarker>, TerminationSideKind, int k)` — the
menu itself cannot be instantiated in `tests/Ui.Tests`.

---

## 5. The context-menu icon convention

> "The context menus for the plots are all messed up due to the menu 'check mark'. The implementation
> is currently wrong. Use the same context menu style as the loadpull marker used on the Data Display.
> It is using the dynamic icons correctly, while the harmonicaRF context menus are not."

### 5.1 The rule

`src/Ui/Views/DataDisplay/MarkerInfoBoxView.axaml.cs:159–218` is the reference. A toggle there is:

```csharp
var item = new MenuItem
{
    Header = "Show Info Box",
    Icon   = new MaterialIcon { Kind = marker.ShowInfoBox
        ? MaterialIconKind.CheckboxOutline : MaterialIconKind.CheckboxBlankOutline },
};
```

**No `ToggleType`. The icon slot carries the state.** harmonicaRF already knows why — read
`AddAutoscaleLockedItems`' own comment (`HarmonicaView.axaml.cs:1116–1124`): the Fluent `MenuItem`
template puts the check glyph and `Icon` in the **same leading slot**, so an item with both shows a
missing icon, a missing checkmark, or a doubled indent depending on theme. R7A §2.3 fixed exactly two
items this way and left every other toggle on `ToggleType`. This section finishes the job.

### 5.2 Add one builder and route every toggle through it

```csharp
/// <summary>R8B §5 — the ONE way a toggle row is built, matching Data Display's loadpull marker
/// menu (MarkerInfoBoxView). Never ToggleType: the check glyph and Icon share the Fluent
/// MenuItem template's leading slot and fight (see AddAutoscaleLockedItems).</summary>
private static MenuItem Toggle(string header, bool on, Action onClick,
                               bool enabled = true, string? tooltip = null)
```

using `MaterialIconKind.CheckboxOutline` / `CheckboxBlankOutline`. Then convert **every**
`ToggleType = MenuItemToggleType.CheckBox` in `HarmonicaView.axaml.cs` — grep gives the full list:
`powerSweep` and `timeDomain` (`:1021`, `:1028`), the X-unit rows (`:1084`), `vswr` (`:1173`), `snap`
(`:1188`), `gridPoints` (`:1247`), Contour Plane `load`/`source` (`:1269`, `:1275`), Contour Harmonic
(`:1290`), and the Efficiency Metric rows near `:1303`. Check `HarmonicaAppMenuInjector.cs` for the
same pattern in the app menus and convert those too if present.

**One exception, and it is a real one:** a group of mutually exclusive rows (Power Sweep vs Time
Domain, the X units, Load vs Source, the harmonic bands, the efficiency metric) is a *radio*, not a
checkbox. Use `MaterialIconKind.RadioboxMarked` / `RadioboxBlank` for those and
`CheckboxOutline`/`CheckboxBlankOutline` for genuine on/off. Give `Toggle` a
`MenuGlyph glyph = MenuGlyph.Check` parameter rather than two near-identical builders.

### 5.3 Power Sweep / Time Domain specifically

> "The 'Power Sweep' and 'Time Domain' menus need sorting out."

They are a two-state radio built as two independent checkboxes whose `IsChecked` values are
`!h.ShowPowerSweepTimeDomain` and `h.ShowPowerSweepTimeDomain`. With §5.2's radio glyph they read
correctly. Two further fixes in the same builder:

- **Group them under one submenu row, `Mode ▸`**, with the two radio rows inside it and the submenu's
  own header showing the current mode (`"Mode: Time Domain"`). Two loose top-level rows that are
  secretly exclusive is what "messed up" means here. Note the trap already recorded at
  `HarmonicaView.axaml.cs:1104`: **a `MenuItem` with children never raises `Click`** — the `Mode` row
  must carry no handler of its own.
- The separator/ordering below is already correct (`Autoscale`, `Locked`, `Axis Limits…`, separator,
  `Copy` last) — leave it. `BuildPowerSweepXUnitMenu` stays a separate menu; R-hui-2 already
  established that merging them was a bug.

### 5.4 Test

Extend `tests/Ui.Tests/Harmonica/HarmonicaPowerSweepFlyMenuTests.cs` and
`HarmonicaR6eDialogsAndMenusTests.cs` with a **source scan**: `HarmonicaView.axaml.cs` contains zero
occurrences of `MenuItemToggleType` outside comments. Crude, and again the only mechanism available —
but it is exactly the regression that would otherwise creep back one menu at a time.

---

## 6. The Ω prefix goes

`BuildFormatRow` (`:1356–1359`) gives all three format rows `MaterialIconKind.Omega`:

> "The context menu for markers has Omega as an icon in the menu. (As a prefix). Wrong, just wrong. It
> doesn't even make sense for a Gamma menu item. Just remove them."

The three rows lose their icon entirely. `Item(...)` currently *requires* a `MaterialIconKind` — give
it a `MaterialIconKind? icon` overload (null → `Icon` left unset) rather than substituting a different
glyph. Nothing else in these menus is icon-less today, so make the null path explicit and commented, or
someone will fill it back in.

---

## 7. VSWR

### 7.1 The menu shape

Today: one `"VSWR: 2.0"` checkbox row, then a sibling `"Set…"` row that is always present. The owner
wants:

```
Show VSWR                 ← toggle, dynamic icon (§5.1's Toggle)
VSWR: 2.00                ← ONLY present when Show VSWR is on; has ONE submenu child:
    Set…                  ←   opens HarmonicaSetVswrDialog
```

- `Show VSWR` calls `h.ToggleMarkerVswrEnabled(marker)`.
- The `VSWR: <val>` row uses `HarmonicaReadoutFormatting.FormatVswr` — **which already prefixes
  `"VSWR: "`**, so pass its result as the whole header, do not concatenate. It carries **no `Click`
  handler** (§5.3's trap: a parent with children never raises `Click` — R7A §2.4 flattened this exact
  structure for that reason, and re-nesting it is safe *only* because the parent is now purely a
  label).
- When `marker.VswrEnabled` is false, neither the value row nor `Set…` is added at all.
- `Add Points to VSWR` keeps its existing `enabled: marker.VswrEnabled` gate and its position.

### 7.2 The Set VSWR dialog's text

`HarmonicaSetVswrDialog.axaml`: delete the `Grid.Row="2"` `TextBlock` (*"Enables the circle if it is
off. No re-solve — this only moves the overlay."*) and delete the `":1"` `TextBlock` in the row-1 grid,
collapsing that grid to a single-column layout (or just let the `TextBox` span). Shrink `Height` from
170 to ~140 so the window is not mostly empty. `HarmonicaSetVswrDialog.axaml.cs` is unchanged — nothing
references either control.

Note the behaviour the deleted sentence described is still true: `SetMarkerVswr` sets
`VswrEnabled = true` (`:542`). It just no longer needs saying, and under §7.1 the dialog is only
reachable when the circle is already on.

### 7.3 Dragging the circle outside the chart

> "I can't drag the VSWR circle outside the Smith Chart. I should have freedom to drag the VSWR to any
> value."

**There is no clamp in the drag path.** `HarmonicaPointer.Apply`'s VSWR branch (`:487–505`) feeds the
raw pointer Γ straight to `HarmonicaVswrHandle.VswrThrough`, and R6B §1.2 already deleted the old rim
clamp — the file's header documents it at length. What actually stops the circle is two things:

1. **A theorem, not a bug.** For a passive marker (`|Γ| < 1`) the constant-VSWR locus is a Möbius image
   of a sub-disc of the unit disc, so it lies **strictly inside** `|Γ| = 1` for every finite VSWR and
   approaches the rim only as VSWR → ∞. `HarmonicaVswrHandle`'s own doc comment states this
   ("MEASURED, NOT ASSUMED", `:95–105`). A passive marker's circle *cannot* be dragged outside the
   chart, ever, and no code change makes it so.
2. **A saturation that hides (1) badly.** `VswrThrough` returns `MaxVswr` (1e6) the instant the drag
   point falls outside the largest circle in the bracket (`if (F(hi) >= 0) return hi;`). So the moment
   the pointer crosses the rim the readout jumps to `VSWR: 1000000` and every further pixel of drag
   produces the same number. That is what "can't drag it" feels like.

So the fix is honesty, in two parts:

- **The readout must say saturated, not a fake number.** When `VswrThrough` clamps at either end,
  report it. Add `public static (double Vswr, bool Saturated) VswrThroughEx(...)`, keep
  `VswrThrough` as a thin wrapper, and have `HarmonicaPointer`'s live readout render `"VSWR: > 10⁶"`
  (and the marker menu's row likewise) when saturated. A number the user cannot move is worse than a
  bound the user can read.
- **§2's change is the other half, and it is the one that will actually satisfy him.** With the
  extrinsic marker off the compressed scale, an **active** marker (`|Γ| > 1`) draws at its true
  position and its whole VSWR family — which for an active centre lies entirely *outside* `|Γ| = 1` —
  is finally drawn concentric with the marker it belongs to, outside the chart, unclipped
  (`DrawMarkers` carries no `ClipRect`). That is a circle outside the Smith chart, draggable, which is
  what the item asks for. Verify it on an active marker specifically.

If after both changes the owner still means something else, say so plainly in your report with the
geometry above rather than inventing a non-Möbius "circle".

Test (`HarmonicaVswrDragTests`, routine tier, pure): for a passive centre 0.3−j0.2 at Z0 = 50, every
sample of `VswrLocus` at VSWR = 1e6 satisfies `|Γ| < 1`; `VswrThroughEx` at a drag point of Γ = 1.5
returns `Saturated: true`; for an **active** centre 1.4+j0 every locus sample satisfies `|Γ| > 1` and
`VswrThroughEx` at a drag point just outside it is **not** saturated.

---

## 8. Gates

```
dotnet build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Harmonica.Tests --no-build
```

Everything new is routine tier. The existing files most likely to go red and need updating rather than
"fixing" are `HarmonicaSetTerminationDialogTests` (§1.3 replaces its simulations with the real model —
delete the simulations, keep `PreviewImpedance`'s assertions), `HarmonicaDragTests`,
`HarmonicaR6cStripTests` and `HarmonicaReadoutColumnsTests` (§3's marker set changes what the Source
chunk contains), and `HarmonicaPowerSweepFlyMenuTests` (§5.3's `Mode ▸` submenu).

**Report explicitly:** whether §1.2's ownership fix alone reproduces-then-fixes the "can't type 50"
case under the new `TerminationEditModel` tests, or whether the model's tests pass and the live control
still misbehaves — in which case say so, because that would mean a second, unlocated defect and the
owner needs to know that rather than a fourth "fixed".
