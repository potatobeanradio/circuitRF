# Brief — harmonicaRF R9D: ZS1 conjugate match, and the PA-class preset terminations

**Read first, in this order:**
`src/Harmonica/IntrinsicAbcd.cs` — **all 107 lines; this is the transform §3 is built on, and its
header states the exact predicate under which it is valid**;
`src/Harmonica/CircuitModel.cs:102–147` (`DutCapacitance`, `DutCapacitances`, `DutCapacitanceKind`),
`:554–600` (`IntrinsicDragAllowed` and its four refusals), `:230–270` (`TerminationSet`,
`MarkedBands`, `Set`, `Z`), `:299–330` (`HarmonicaSettings.Z0`);
`src/Ui/Harmonica/HarmonicaViewModel.cs:400–520` (`SetMarkerImpedance`, `AddMarkerBand`,
`RemoveMarkerBand`, `SetMarkerGamma`), `:576–598` (**the linearized-capacitance pattern §3.4 reuses
verbatim**), `:1127–1150` (`RequestFrame`), `:1180–1194` (`PublishFrame` and its
`ApplyInverseOutcome` hook), `:1934–1960` (`ApplyInverseOutcome` — the pattern §2 mirrors);
`src/Ui/Harmonica/HarmonicaFrame.cs:370–420` (`Inverse`, `Published`, and the doc comment explaining
**why a worker-computed answer rides on the frame** — that reasoning is §2's);
`src/Harmonica/HarmonicaDataSet.cs:106–…` (`Build`, and the `Zin` cube it publishes);
`src/Harmonica/PinSearch.cs:499–640` (`Sweep`, `PinStep`, `CompressionReadout`);
`src/Ui/Views/Harmonica/HarmonicaView.axaml.cs:1164–1241` (`BuildMarkerMenu`), `:1096–1133`
(`Item`/`Toggle`);
`src/Ui/Views/Harmonica/HarmonicaMenuView.axaml:69–86` and `:224–249` (the Markers menu, both
surfaces), `src/Ui/Harmonica/HarmonicaAppMenuInjector.cs:112–127` (`BuildMarkers`, the third surface)
and `:222–226` (`Item`);
`src/Ui/Harmonica/HarmonicaMenuViewModel.cs:160–200` and `:360–400` (the `[RelayCommand]` shape).

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` and `src/Harmonica/RESOLVED.md` only.
**No screenshot verification.**

Tag new comments `R9D §n`.

**Ordering note:** this brief is independent of `brief-harmonicarf-r9c`, but §2 reads the power sweep's
own compression point, and R9C changes which function produces it for MXP/MXE. If R9C has landed, §2
must read `PinSearchResult.SweepCompression` for the compression Pin — see §2.3.

---

## 1. Two features, one shared piece

§2 is the S1 marker's **Match to Zin\***. §3 is **Markers ▸ Preset Terminations ▸ Class B / J / J\* /
F / F⁻¹**. They share nothing structurally, but both write a termination and then re-solve, so they
share §4's "apply and re-solve" discipline. Build §2 first — it is the smaller of the two and it
establishes the frame-carried-outcome plumbing.

---

## 2. `Match to Zin*` on the S1 marker

> "Add a menu to the S1 marker called 'Match to Zin\*'. This menu command will set the ZS1 to the
> conjugate of the Zin for the current power sweep at approximately 5 dB backoff from compression.
> (Find the nearest backoff point that was already pre-calculated.) After setting this, the S1 marker
> renders in its new position and a loadpull is performed to regenerate the isolines (as per normal
> usage)."

### 2.1 Where the number comes from, and why it cannot be read off the frame

`Zin` is published in the frame's `DataSet` (`HarmonicaDataSet.Build` → cube `"Zin"` at
`(TerminationSide.Source, harmonic 1)`), but **only at the frame's own operating point** — the
compression step, or the user-placed cursor. There is no per-ladder-step `Zin` anywhere on the frame,
and there must not be: publishing a `DataSet` per rung would cost a `Build` per solve.

So the backoff `Zin` is computed **on a solve worker, once, when the command is invoked**, and crosses
back on the frame. That is exactly the contract `HarmonicaFrame.Inverse` already documents:

> "Carried on the frame because the answer is computed on a WORKER and the terminations it writes are
> UI-visible state — so the value crosses the thread boundary the same way every other frame value
> does, rather than through a field two threads share."

Do not invent a second mechanism.

### 2.2 The plumbing

**`HarmonicaSolver.Options`** gains

```csharp
/// <summary>R9D §2 — when set, this frame ALSO computes the source-side Zin at approximately this
/// many dB of backoff from the compression point, and reports it on the frame. Null on every
/// ordinary frame; nothing about the grid, the panels or the readouts changes when it is set.</summary>
public double? ConjugateMatchBackoffDb { get; init; }
```

**`HarmonicaFrame`** gains `ConjugateMatchOutcome? ConjugateMatch { get; init; }`, with

```csharp
/// <param name="Found">False means NOTHING is written — R-h6-9's rule, applied here.</param>
/// <param name="Reason">Why not, when it was not found. Shown on the message line, never thrown.</param>
/// <param name="RequestedBackoffDb">What was asked for (5 dB).</param>
/// <param name="ActualBackoffDb">What the nearest ALREADY-SOLVED ladder rung actually is, which is
/// what "approximately" in the owner's request means and what the message line must state.</param>
/// <param name="PinDbm">That rung's own Pin.</param>
/// <param name="Zin">Zin there, at the extrinsic source plane, fundamental.</param>
public sealed record ConjugateMatchOutcome(
    bool Found, string? Reason, double RequestedBackoffDb, double ActualBackoffDb,
    double PinDbm, Complex Zin);
```

**`HarmonicaSolver.Solve`**, after the tier-A drive-up (`sweep` is already in hand — do not re-solve):

```csharp
// R9D §2 — the backoff point is chosen from the rungs the drive-up ALREADY SOLVED ("find the nearest
// backoff point that was already pre-calculated"), never by a new search: the ladder's own steps are
// the pre-calculated set, and re-searching would answer a question the owner did not ask and cost a
// second sweep. Only the ONE chosen rung pays a HarmonicaDataSet.Build.
```

- if `opt.ConjugateMatchBackoffDb` is null → leave `ConjugateMatch` null and change nothing;
- if the sweep did not compress → `Found: false`, reason "the drive-up did not reach the compression
  target, so there is no backoff point to measure from";
- else target `Pin_c − backoff` and pick `IndexOfNearestPin(sweep.Steps, target)` — the same helper
  `SolveAtOptimum` already uses. Report `ActualBackoffDb = Pin_c − step.PavlDbm`, which will differ
  from 5 by up to half a ladder step and must be stated rather than rounded away;
- `HarmonicaDataSet.Build(ctx, step.Point, terminations)` → read `Zin` at `(Source, 1)`. If it is not
  finite, `Found: false` with a stated reason rather than a NaN termination.

### 2.3 Which "compression Pin"

`Pin_c` is `sweep.SweepCompression?.PinDbm ?? sweep.AtCompression!.PavlDbm` — the interpolated reading
first, the nearest rung as the fallback, the same `sc?.X ?? at.X` shape
`HarmonicaSolver.cs:629–634` already uses for the operating-point column. The backoff itself is then
measured from the *interpolated* compression Pin while landing on a *solved* rung, which is what the
owner's "approximately… already pre-calculated" describes.

### 2.4 Applying it

`HarmonicaViewModel.PublishFrame` already calls `ApplyInverseOutcome` before publishing; add the twin:

```csharp
if (frame.ConjugateMatch is { } match) ApplyConjugateMatch(match);
```

```csharp
/// <summary>
/// R9D §2 — writes S1 to conj(Zin) at the reported backoff and asks for a normal frame, which is what
/// re-renders the marker and regenerates the iso-lines ("as per normal usage"). A NOT-FOUND outcome
/// writes nothing and only sets the message — R-h6-9's rule ("nothing but a converged solve may write
/// a termination") applies here for the same reason: a marker that lands somewhere the solve did not
/// actually reach is worse than one that does not move.
/// </summary>
private void ApplyConjugateMatch(ConjugateMatchOutcome match)
{
    if (!match.Found) { InverseMessage = match.Reason; return; }

    var s1 = Markers.FirstOrDefault(m => m.Side == TerminationSideKind.Source && m.Band == 1);
    if (s1 is null) return;                       // the marker was removed between request and reply

    SetMarkerImpedance(s1, Complex.Conjugate(match.Zin));
    InverseMessage = $"S1 set to conj(Zin) = … at {match.ActualBackoffDb:0.0} dB backoff " +
                     $"(Pin {match.PinDbm:0.0} dBm).";
    RequestScheduledFrame(dragging: false);
}
```

`InverseMessage` is the existing status-message channel (`StatusMessage` reads it) — reuse it rather
than adding a second. **Note the interaction with `brief-harmonicarf-r9a` §11:** that brief blanks the
message line while a gesture is live. This message is posted from a menu click, never mid-drag, so the
two do not collide — but say so in a comment so nobody "fixes" one against the other.

### 2.5 The menu item

In `BuildMarkerMenu` (`HarmonicaView.axaml.cs:1172`), between the Snap-to-Grid toggle and the Add Grid
Points rows:

```csharp
// R9D §2 — S1 only. On any other marker the row is absent rather than disabled: "match the SOURCE to
// the device's own input impedance" is not a thing a load marker or a harmonic marker can mean, so
// there is nothing to explain in a tooltip (R13a's "disabled with a stated reason" rule is for items
// that are meaningful but unavailable, which this is not).
if (marker.Side == TerminationSideKind.Source && marker.Band == 1)
    items.Add(Item("Match to Zin*", MaterialIconKind.ArrowLeftRight,
        () => { h.RequestConjugateMatch(backoffDb: 5.0); Refresh(); },
        tooltip: "Sets ZS1 to the conjugate of Zin at the nearest already-solved drive level about " +
                 "5 dB below compression, then re-solves the loadpull."));
```

`RequestConjugateMatch(double backoffDb)` on the view model is
`RequestFrame(OptionsFor(Scheduler.NextPlan(false), dragging: false) with { ConjugateMatchBackoffDb = backoffDb, SkipContours = true })`
— **`SkipContours: true`**, because this frame exists only to measure `Zin`; the contour layer carries
forward (R-h9r2-1) and the real grid is solved by the frame `ApplyConjugateMatch` requests afterwards.
Two frames, one grid.

`5.0` is a named constant on the view model (`ConjugateMatchBackoffDb = 5.0`), not a literal at the call
site.

### 2.6 Gate

`tests/Ui.Tests/Harmonica/` — a new `HarmonicaConjugateMatchTests`:

- with a frame carrying `Found: true` and a known `Zin`, `ApplyConjugateMatch` sets the S1 marker's
  impedance to exactly `Complex.Conjugate(Zin)` and its Γ to `GammaOf` of that at the document's Z0;
- `Found: false` leaves the S1 termination bit-identical and sets a message;
- with no S1 marker present (the shipped default! — R8B §3 removed it), nothing throws and nothing is
  written;
- the outcome is produced only when `ConjugateMatchBackoffDb` is set — an ordinary frame's
  `ConjugateMatch` is null;
- **the backoff selection is a pure function and must be tested as one**: extract
  `internal static int IndexOfBackoffStep(IReadOnlyList<PinStep> steps, double compressionPinDbm,
  double backoffDb)` and pin it on a synthetic ladder, including the case where the target falls below
  the ladder's first rung (answer: the first rung, with `ActualBackoffDb` reported honestly).

---

## 3. PA-class preset terminations

### 3.1 The five presets, verbatim

Source: **Sharma, T. (2018). *Modelling and Design Methodology of Higher-Efficiency Harmonic Tuned
Power Amplifiers for 5G Applications* (Doctoral thesis, University of Calgary).**
<https://prism.ucalgary.ca/handle/1880/106695> — cite it in the code comment, not only here.

All values are **intrinsic** and assume the user has set `Z0 = R_opt`; `Z0` below is
`Model.Settings.Z0`.

| preset | ZL1 (intrinsic) | ZL2 | other even ZL | other odd ZL |
|---|---|---|---|---|
| Class B | `Z0` | 1e-6 | 1e-6 | 1e-6 |
| Class J | `Z0·(1 − j·α)`, α = 0.5 | `j·3πα/8·Z0` | 1e-6 | 1e-6 |
| Class J\* | as Class J with α = −0.5 | `j·3πα/8·Z0` (α = −0.5) | 1e-6 | 1e-6 |
| Class F | `2·Z0/√3` | 1e-6 | 1e-6 (near short) | 1e6 (near open) |
| Class F⁻¹ | `√2·Z0/2/(0.5 − 8/9/π/π)` | 1e6 | 1e6 (near open) | 1e-6 (near short) |

Transcribe the two awkward expressions **exactly as the owner wrote them** —
`Math.Sqrt(2) * z0 / 2 / (0.5 - 8.0 / 9.0 / Math.PI / Math.PI)` — rather than pre-simplifying to a
decimal. A reader must be able to check the formula against the thesis without reversing arithmetic.
(For reference when reading a test failure: that is ≈ 1.7249·Z0, and Class F's is ≈ 1.1547·Z0.)

Note that Class F's and Class F⁻¹'s rules for band 2 fall out of the even/odd columns and are listed
separately only for readability — implement one even/odd rule, not a special case for band 2.

### 3.2 Only existing markers change — nothing is created

> "Note that new markers are never added when these Presets are used. Only change the existing
> markers. (So if HB Order K=5 and user selects Class F preset, but only has markers showing up to
> ZL3, then ZL4 and ZL5 remain at the harmonicaRF 'undefined' termination (which is currently 1e-6).)"

So: iterate the **Load-side markers that exist** (`Markers.Where(m => m.Side == Load)`), compute each
band's intrinsic target from the table, and write it through `SetMarkerImpedance` — the one call that
keeps Γ and the `TerminationSet` in step. Source markers are untouched. Unmarked bands are not written
at all — not even to 1e-6, which is already what `TerminationSet.Z` answers for them
(`UnmarkedBandOhms`), so writing it would be a no-op that only risks creating an entry.

### 3.3 Intrinsic → extrinsic

> "Note these terminations are for intrinsic impedance, so the actual marker impedances will need to be
> calculated (with the ABCD transform or similar) when the DUT has capacitance."

`IntrinsicAbcd.ExtrinsicFor(model, TerminationSide.Load, band, zIntr)` is exactly this map, per band,
in closed form. **Every** value goes through it, the near-shorts and near-opens included — a short at
the intrinsic drain is not a short at the package plane, and that is the whole point of the request.

Three guards, all of them stated to the user rather than swallowed:

1. **The map's own pole.** `ExtrinsicFor`'s doc comment warns that `−C·Z_intr + A → 0` yields a
   non-finite value. A band whose transform is non-finite is **left unchanged**, and the message names
   it ("ZL3 could not be transformed to the extrinsic plane and was left as it was"). Do not clamp, do
   not substitute.
2. **`IntrinsicAbcd.Chain` throws** when `Cdg` is present or the package couples input and output. Do
   not let that reach the user as an exception — see §3.4.
3. Everything is per-band: `ω = 2π·f₀·band`, which `ExtrinsicFor` already does.

### 3.4 "Best effort", made precise

> "If nonlinear capacitance is present, use the linearized capacitance. This whole intrinsic setting
> should only be 'best effort' in the presence of capacitance and extrinsic networks. Do not use a
> solver for this."

`CircuitModel.IntrinsicDragAllowed` has four refusals; the preset handles them differently from the
drag, because "best effort" is the owner's explicit instruction here:

| refusal | preset behaviour |
|---|---|
| non-SDD DUT | no ABCD chain exists → write the intrinsic values AT the extrinsic plane, and say so |
| nonlinear Cgs/Cdg/Cds | **substitute the linearized value and proceed** (below) |
| `Cdg` present | cannot be inverted side by side → write intrinsic values at the extrinsic plane, and say so |
| shared source lead / `CgdExt` (`CouplesInputAndOutput`) | same |

The message is never silent: "Preset applied at the EXTRINSIC plane — <reason>. The intrinsic
terminations will differ." A user who sees the markers move and does not know the transform was skipped
is the failure mode this avoids.

**The linearized substitution reuses the strip's own pattern verbatim** —
`HarmonicaViewModel.Inputs` (`:576–598`) already computes exactly these three numbers through
`HarmonicaSolver.LinearizedCapacitanceFarads(ctx, Frame.Published, coefficients, kind)` at the last
published operating point. Build a **model copy** for the transform only:

```csharp
// R9D §3.4 — the ABCD chain is a LINEAR two-port, so a nonlinear capacitor has to be replaced by a
// number before it can enter one. The number is the same linearized value the readout strip already
// shows for that capacitor at this operating point (HarmonicaViewModel.Inputs), so what the user reads
// in the strip is what the transform used. This copy is used for the transform ONLY and is never
// written back to Model — substituting a linearized capacitor into the document would change the
// circuit the engine solves, which is not what "best effort" means.
var linearized = Model with { Dut = Model.Dut with { Capacitances = ... } };
```

with each nonlinear `DutCapacitance` replaced by `new DutCapacitance { Farads = <linearized> }` and
`RgsOhms` carried across unchanged (rgs is in the Cgs branch of the chain — R8C §3.2 — and dropping it
would silently change the source-side transform). If a linearized value is unavailable (nothing solved
yet, or the intrinsic plane is not located), fall back to the capacitor's own `Coefficients[0]` — the
"(at V=0)" value the strip itself falls back to — and say so in the message.

**No solver, no iteration, no `InverseSolver`.** The owner is explicit, and `IntrinsicAbcd` is closed
form.

### 3.5 Where the code goes

A new framework-free file **`src/Harmonica/PaClassPresets.cs`**, so the physics is testable in
`tests/Harmonica.Tests` without a view model:

```csharp
public enum PaClass { B, J, JStar, F, FInverse }

public static class PaClassPresets
{
    /// <summary>The INTRINSIC target for one load band under one class, given Z0 = R_opt. Pure.</summary>
    public static Complex IntrinsicLoad(PaClass paClass, int band, double z0);

    /// <summary>harmonicaRF's own "undefined" termination — TerminationSet.UnmarkedBandOhms.</summary>
    public const double NearShortOhms = 1e-6;
    public const double NearOpenOhms  = 1e6;
}
```

`NearShortOhms` must be **read from `TerminationSet.UnmarkedBandOhms`**, not re-declared as a literal —
the owner's own text calls it "the harmonicaRF 'undefined' termination (which is currently 1e-6)", and
*currently* is the word that matters.

The view-model half (`HarmonicaViewModel.ApplyPaClassPreset(PaClass)`) does the marker walk, the
model-copy/transform, the message, and one `RequestScheduledFrame(dragging: false)` **after every band
is written** — one re-solve for the whole preset, never one per band.

### 3.6 The menus

`Markers ▸ Preset Terminations ▸ …`, on all three surfaces, with `Class F-1` spelled `Class F⁻¹` in the
UI text (the repo already uses `f₀`/`2f₀` superscripts in these menus).

| surface | file | where |
|---|---|---|
| macOS `NativeMenu` | `HarmonicaMenuView.axaml:69–86` | after `Load Bands`, before the separator |
| in-window `Menu` | `HarmonicaMenuView.axaml:224–249` | same position |
| docked app menu | `HarmonicaAppMenuInjector.BuildMarkers` | same position |

One `[RelayCommand] private void SetPaClassPreset(string? name)` on `HarmonicaMenuViewModel`, taking
`"B"`/`"J"`/`"JStar"`/`"F"`/`"FInverse"` as `CommandParameter` — the same string-parameter shape
`SetEfficiencyMetric`/`SetGridSide` already use, and for the same reason (a `NativeMenuItem` binds a
command and a parameter, nothing richer).

### 3.7 Shortcuts

Verified free — harmonicaRF's menus currently bind only `Ctrl/Meta+S`, `Ctrl/Meta+Z` and
`Ctrl/Meta+Shift+Z`:

| preset | in-window (`InputGesture`) | native (`Gesture`) |
|---|---|---|
| Class B | `Ctrl+B` | `Meta+B` |
| Class J | `Ctrl+J` | `Meta+J` |
| Class J\* | `Ctrl+Shift+J` | `Meta+Shift+J` |
| Class F | `Ctrl+F` | `Meta+F` |
| Class F⁻¹ | `Ctrl+Shift+F` | `Meta+Shift+F` |

`HarmonicaAppMenuInjector.Item` (`:222–226`) does not set `Gesture`; give it an optional
`KeyGesture? gesture = null` parameter and pass it through. That surface is the docked-on-macOS one and
a shortcut that works standalone but not docked is the kind of split the R3A native-menu work already
paid for once.

**Trap, from `src/Ui/CLAUDE.md`'s standing rule:** a window's `NativeMenu` INSTANCE is fixed for its
lifetime — change its `Items`, never the instance. These are static items in the declared menu, so
nothing here needs to touch that, but do not "refresh" the Markers menu by rebuilding it.

### 3.8 Gate

**`tests/Harmonica.Tests/PaClassPresetsTests.cs`** — pure, fast, and the place the physics is pinned:

- Class B at Z0 = 80: ZL1 = 80+j0; bands 2..K are `NearShortOhms`;
- Class J at Z0 = 80: ZL1 = 80 − j40 exactly; ZL2 = `+j·(3π·0.5/8)·80` ≈ `+j47.12`;
- Class J\*: ZL1 = 80 + j40; ZL2 ≈ `−j47.12` — and **assert J\* is the complex conjugate of J band by
  band**, which is the property the name claims and is a stronger gate than two decimal literals;
- Class F: ZL1 ≈ 92.376 (`2·80/√3`); band 2 near-short, band 3 near-open, band 4 near-short;
- Class F⁻¹: ZL1 ≈ 137.99 (`√2·80/2/(0.5 − 8/9/π²)`); band 2 near-open, band 3 near-short;
- **an identity check that is worth more than all of the above**: with `DutCapacitances.None` and an
  empty package, `IntrinsicAbcd.ExtrinsicFor` is the identity map, so the extrinsic values a preset
  writes equal the intrinsic table exactly. Any drift there means the chain is being built wrong.
- **a round trip**: with a real Cds and a real Ld, transform intrinsic → extrinsic and back through
  `IntrinsicAbcd`'s forward form `(A·Z_ext + B)/(C·Z_ext + D)` and recover the intrinsic value to
  1e-9. §5.4 item 3's own round trip is what caught an element-ordering mismatch in that file before;
  this is the same guard for a new caller.

**`tests/Ui.Tests/Harmonica/`** — a new `HarmonicaPaClassPresetTests`:

- with markers at L1, L2, L3 only and K = 5, applying Class F writes exactly three terminations and
  leaves bands 4 and 5 reporting `UnmarkedBandOhms` (the owner's own example, made a gate);
- **no marker is created** — `Markers.Count` is unchanged, on both sides;
- Source markers are untouched;
- with `Cdg` set (transform refused), the markers still move, the values are the intrinsic ones, and
  a non-empty message is set;
- with a nonlinear `Cgs`, the transform runs against a linearized model copy and **`Model.Dut.
  Capacitances.Cgs.IsNonlinear` is still true afterwards** — the copy must never be written back;
- exactly one frame is requested per preset application (count `RequestFrame` calls through the pool,
  as the existing drag tests do).

Plus source-scan tests for the three menu surfaces (comments stripped) covering the five headers, the
five command parameters and the five gestures.

---

## 4. Shared discipline for both features

- **A termination is only ever written through `HarmonicaViewModel.SetMarkerImpedance`.** It is the one
  place the marker's Γ and the `TerminationSet` entry are kept in step, and its doc comment says why.
- **One re-solve per user action.** §2 costs two frames (a `SkipContours` measurement frame, then the
  real one); §3 costs one, after all bands are written.
- **Nothing is written when the answer was not found** — §2.4's refusal, and §3.3's per-band pole
  guard. R-h6-9's rule, restated: a marker that lands somewhere the computation did not actually reach
  is worse than one that does not move.
- **`src/Harmonica` gains no UI reference.** `PaClassPresets` is plain math over `Complex` and
  `double`; the linearized-capacitance lookup and the message stay on the `src/Ui` side.

## 5. Gate for the whole brief

```
dotnet build
dotnet test tests/Harmonica.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Write the outcome to `src/Ui/RESOLVED.md` and `src/Harmonica/RESOLVED.md` — including, for §3, the
thesis citation and the two awkward closed forms, so the next reader can check them without finding
the paper. **No `CLAUDE.md` edits.**
