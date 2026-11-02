# Core length units — making a metre representable

**Phase:** a `src/Core` expression-engine fix. Not a wBond phase, not a layout phase — but wBond
WB-B2 is what found it and WB21 is what it unblocks.

**Design authority:** the root `CLAUDE.md` §Expressions ("One expression engine … values are kinded
Real/Complex/Bool"), `src/Core/CLAUDE.md` (**"Ask before: changing the `.cnl` or JSON format"** —
§5 turns on that rule), and `docs/design/expressions.md` §8's unit-scale table.

**Predecessor:** WB-B2 (`src/Ui/CLAUDE.md`, "A real pre-existing defect found on the way, NOT fixed
here") named the sweep symptom and deliberately left it. WB-E §6 named it again and asked for this
brief. **Read WB-B2's own paragraph before starting** — it is correct as far as it goes, and §0
below explains why it does not go far enough.

---

## 0. What this is, in one paragraph

`src/Core/Expressions/Units.cs`'s table cannot represent a length. Not "has an awkward corner" —
**cannot represent one**: of the six length units the parameter editor offers a user, three
evaluate to the wrong number, silently, by factors of 100, 1000 and 1,000,000,000. WB-B2 found this
through a parametric sweep, which is the *hardest* path to it — a swept length is wrong twice, once
in the table and once again in the sweep engine's re-attach step, compounding to 1e-6 of intent. But
the sweep is a symptom, not the disease. **A plain component parameter authored `L = 1 cm` in the
ordinary parameter editor evaluates to 1.0, not 0.01, today, with no error and no warning.** That is
the thing to fix. The sweep round-trip then falls out of it, and WB21 (`wbond.md`'s "the feature a
PA designer will actually use the tool for") becomes reachable.

**The scope trap to name up front:** the obvious reading of WB-B2's note is "add one row to a
dictionary." It is not one row. `"m"` is already taken by the SI prefix *milli*, `"nm"` and `"cm"`
are in the wrong dictionary entirely, `"mil"` is missing from the base-unit map, and the one
mechanism that would round-trip a swept length (`Units.BaseUnit`) has no correct answer to return
for any of them. Fixing it is a decision about what the base symbol for length *is*, and that
decision is visible to the user in at least one place (§2, R-len-4). Do not start typing until §5
question 1 is answered.

---

## 1. What exists, and what is actually broken

Read this section before writing anything. Every number in it was measured against the shipped
code on 2026-08-07 with a disposable probe, not derived by reading the table.

### 1.1 The table, and the three ways length fails

`src/Core/Expressions/Units.cs` holds three collections:

```csharp
private static readonly Dictionary<string, double> _scales      // unit → linear multiplier
private static readonly HashSet<string>            _identityUnits // valid, but carry no multiplier
private static readonly Dictionary<string, string> _baseUnitMap  // prefixed unit → scale-1 base symbol

public static double? Scale(string unit)            // null when absent from _scales
public static bool    IsKnown(string unit)          // _scales.ContainsKey
public static bool    IsRecognizedUnit(string unit) // IsKnown || _identityUnits
public static string  BaseUnit(string unit)         // _baseUnitMap, else pass-through
```

Length appears in all three, inconsistently:

- `_scales` has `mm` = 1e-3, `um` = 1e-6, `mil` = 2.54e-5 — **and `m` = 1e-3, which is the SI prefix
  milli, not the metre.**
- `_identityUnits` has `nm` and `cm`, with the comment *"length not in linear table"*. An identity
  unit is one that genuinely carries no multiplier (`V`, `A`, `dBm`). A nanometre is not one of
  those. `Scale("nm")` therefore returns null.
- `_baseUnitMap` maps `mm`/`um`/`nm`/`cm` → `"m"`, and **omits `mil` entirely**.

`Evaluator.ApplyUnit` (`src/Core/Expressions/Evaluator.cs`, ~line 770) resolves a unit as
`Units.Scale(unit) ?? (IsRecognizedUnit(unit) ? 1.0 : throw)`. So an identity unit silently becomes
a multiplier of exactly 1. **Measured, `Eval("1", unit)`:**

| unit | evaluates to | correct SI | error |
|---|---|---|---|
| `nm` | **1** | 1e-9 | 1e9 high |
| `um` | 1e-6 | 1e-6 | ✓ |
| `mm` | 1e-3 | 1e-3 | ✓ |
| `cm` | **1** | 1e-2 | 100 high |
| `m` | **1e-3** | 1 | 1000 low |
| `mil` | 2.54e-5 | 2.54e-5 | ✓ |
| `in` / `inch` | *throws* `Unknown unit` | 2.54e-2 | not recognised at all |

**All six of the wrong ones are user-selectable.** `ComponentTypeRegistry.UnitOptions` (`src/Ui/`)
offers `["None", "nm", "µm", "mm", "cm", "m", "mil"]` for `UnitDimension.Length`, and the glyph `µm`
is normalised to `um` by `UnitNormalizer.ToEngineUnit` before it reaches the table. Three of the six
are wrong; nothing anywhere reports it.

### 1.2 `BaseUnit`, and why a swept length is wrong a second time

`Units.BaseUnit` exists for one caller. `src/Engine/ParametricSweepEngine.cs` (~lines 65–92)
expands a sweep to already-SI values and then re-injects each point as a `Variable` carrying
`Units.BaseUnit(effUnit)` — a scale-1 symbol — so the elaborator marks the global as unit-bearing
(`GlobalsWithExplicitUnit`) and `FreqUnit.ResolveHz`'s var-unit-wins rule fires. The comment says so
directly: *"BaseUnit reduces it to scale-1 … so injecting it leaves the value unchanged."*

For frequency that is exactly true: `BaseUnit("GHz")` = `"Hz"`, `Scale("Hz")` = 1.0, no-op.
For length it is false in both directions:

| swept unit | `BaseUnit` | `Scale(base)` | net effect on an already-SI value |
|---|---|---|---|
| `mm` | `m` | **1e-3** | ×1e-3 |
| `um` | `m` | **1e-3** | ×1e-3 |
| `nm` | `m` | **1e-3** | ×1e-3 |
| `cm` | `m` | **1e-3** | ×1e-3 |
| `mil` | `mil` (unmapped) | **2.54e-5** | ×2.54e-5 |

Combined with §1.1's table error the compounded factor for a `mm` sweep is 1e-6 of intent, and for
a `mil` sweep 6.45e-10. **This is WB-B2's own measured symptom**: the loop-height sweep at 10 mil
and 45 mil produced bit-identical |S21| because both collapsed below the wire's own foot drop and
clamped to the same geometry — a perfectly plausible flat curve, not an error. The same two heights
driven through a hand-written `.cnl` give 1073.8 pH and 2189.9 pH. That pair is this brief's phase
gate (§3, M4).

### 1.3 Where a length unit is scaled today — the full list

There are two scaling sites for a sweep and they are alternatives, not a double application
(confirmed by reading both call paths — do not "fix" a double-scale that is not there):

- `SweepAxisRowViewModel.BuildValues()` (`src/Ui/ViewModels/`) — `Units.Scale(EffectiveUnit) ?? 1.0`
  applied to Start/Stop, and to Step only in `StepSize` mode. Used for the live preview and for
  `List`-mode axes. **Note `?? 1.0`: an `nm` or `cm` axis is silently not scaled at all here.**
- `ParametricSweepAnalysis`'s spec constructor (`src/Core/Design/Analysis.cs`, ~line 245) — the same
  arithmetic against `spec.Unit`. This is the path `AnalysisEditorViewModel.BuildAnalyses` takes for
  `StepSize`/`PointCount` axes.

`EffectiveUnit` is the user's chosen unit, else the swept VAR's own declared unit
(`SweepAxisRowViewModel`, ~line 102). It is persisted — `.cnl` writes ` Unit={spec.Unit}` on the
sweep line (`CnlWriter.cs` ~line 399) and `.csch` carries it as `PsaUnit`. **The persisted string is
the user's display unit (`"mm"`), never the base symbol** — which is what keeps §5's format question
answerable with "no change needed," provided the fix respects R-len-3.

Elsewhere, `Units.Scale` is read by `ParameterEditorViewModel` (MKlopf entry-mode conversion, ~lines
708/747), `LayoutShapePropertiesViewModel` (~1389–1398), `SchematicToLayoutGenerator` (~530) and
`FreqDeferral` (~247). Every one of those is a length-capable path and every one of them inherits
§1.1's error.

### 1.4 What is NOT affected, and must not be touched

Two other unit systems exist in this repo and both are already correct, with their own exact decimal
tables:

- **`LayoutUnits`** (`src/Ui/Layout/LayoutUnits.cs`) — DBU-based, `decimal` arithmetic,
  `1 mil = 25400 DBU` exactly. It accepts `nm`/`u`/`um`/`µm`/`mm`/`mil`/`in`/`inch`.
- **`WBondUnits`** (`src/WBond/`) — nanometre-based, same discipline.

Neither routes through `Units`. **Do not unify them with the expression engine's table.** They are
integer/decimal systems answering a different question (exact database coordinates); `Units` is a
double-precision multiplier applied to a resolved expression value. Consolidating them would trade
a fixable bug for an unfixable rounding class.

---

## 2. The traps — read this section twice

### R-len-1 — `"m"` is already milli, and the bare SI prefixes may or may not be load-bearing

`_scales` carries nine bare SI prefixes (`T G M k m u n p f`) as standalone units. `"m"` is one of
them. Re-pointing `"m"` at the metre is the *obvious* fix and it silently changes the meaning of any
existing `X = 5 m` that meant 5 milli-somethings.

**Measure before deciding.** The bare prefixes are consumed by `CnlReader`/`VendorAReader` token
gates (`Units.IsKnown`, `CnlReader.cs` ~316 and ~1706, `VendorAReader.cs` ~222/312/475) and by
`Evaluator.ApplyUnit`. Establish, with a grep over `testdata/` and the committed `.cnl`/`.csch`
corpus, whether a bare-prefix unit is used anywhere at all. If it is not, §5 question 1's cheapest
answer is available. If it is, it is not.

### R-len-2 — `nm` and `cm` are in `_identityUnits`, and that is not a typo to delete blindly

Moving them into `_scales` flips `Units.IsKnown("nm")` from false to true, which changes two token
gates that read `IsKnown` rather than `IsRecognizedUnit` (`CnlReader.cs` ~316, ~1706;
`VendorAReader.cs` ~222/312/475). Today, a `.cnl` line `L1 a b L=5 nm` leaves `nm` unconsumed at
those sites — the exact shape of the phantom-node failure `Units.cs`'s own `TOhm` comment records
(*"the absence of this one entry silently turned `TOhm` into a NET"*). **So this change fixes a
second latent bug at the same time — verify that it does, with a test, rather than assuming it.**
`IsRecognizedUnit` is unaffected either way (it is `IsKnown || identity`).

### R-len-3 — the base symbol is INTERNAL, except where it is not

`BaseUnit`'s output only has to be a key into `_scales` whose value is 1.0. It never has to be a
unit a user can type, and it is never persisted to `.cnl` or `.csch` — those carry the user's own
`Unit=`/`PsaUnit` string. **But it is not purely internal:** `ParametricSweepEngine.cs` ~line 141
does `new Axis(sweep.SweepVarName, sweep.SweepValues, baseUnit)`, so the base symbol becomes the
sweep axis's unit in the resulting `DataSet` — rendered as an axis label in the Data Display and
written into the `.npy` results file. Whatever symbol §5 question 1 settles on has to read
acceptably there, or the axis needs its own display mapping. Decide which; do not discover it after
the fact by looking at a plot.

### R-len-4 — the fix moves every existing length sweep, and there is no correct old behaviour to preserve

A `mm` sweep's injected value changes by 1000×; a `mil` sweep's by ~39370×. Every saved workspace
containing one produces different numbers after this lands. **That is a correction, not a
regression** — the current values are wrong by construction and were producing plausible flat
curves. The same is true of any design carrying an `nm`, `cm` or `m` parameter (§1.1). The owner
decision (§5 question 2) is not *whether* to fix it but whether an opened design containing one
should say so.

### R-len-5 — do not "fix" the sweep by deleting the re-attach

The tempting narrow fix is to stop `ParametricSweepEngine` attaching a base unit at all and mark the
global unit-bearing some other way. That would make the sweep correct without touching the table —
and would leave §1.1's three broken parameter units live, which is the larger bug and the one a user
hits without ever opening the sweep dialog. **Fix the table (M1). Then decide whether the re-attach
is still the right mechanism (M2), on its own merits.**

### R-len-6 — `Scale(...) ?? 1.0` is the pattern that hid this

`SweepAxisRowViewModel.BuildValues` and several other call sites fall back to a multiplier of 1.0
for an unrecognised unit. That is what turned "nm is missing from the table" into "an nm sweep is
silently unscaled" rather than an error. Audit every `Scale(...) ??` in `src/` while you are in
here; each one is a place a future missing row will hide the same way. Changing them is not
necessarily in scope — **listing them in the completion note is**.

---

## 3. Milestones

Each gated independently. M1 is the phase; M2–M4 are what make it reachable and provable.

### M1 — a metre is representable

Decide the base symbol (§5 q1), then make `Units` answer correctly for every unit
`ComponentTypeRegistry.UnitOptions(UnitDimension.Length)` offers, plus `mil`. `nm` and `cm` leave
`_identityUnits`. `mil` gains a `_baseUnitMap` entry. `BaseUnit` returns a symbol whose `Scale` is
exactly 1.0 for every length unit.

**Gate.** A table test asserting `Eval("1", u)` for all of `nm um mm cm m mil` against §1.1's
*correct* column, and a second asserting `Scale(BaseUnit(u)) == 1.0` for the same set — the property
`ParametricSweepEngine`'s own comment claims and length has never satisfied. Both must fail against
the pre-fix code; confirm that they do rather than assuming it. Plus the R-len-2 check: a `.cnl`
carrying `L=5 nm` parses the unit rather than minting a net.

### M2 — the swept length round-trips

With M1 in place, re-examine `ParametricSweepEngine`'s re-attach. It may need no change at all — if
`BaseUnit` now returns a genuine scale-1 symbol, the comment becomes true for length as it already
is for frequency. If it does need a change, R-len-5 governs: state why, in the code, at the site.

**Gate.** A sweep over a `mm`-declared and a `mil`-declared global, driven through
`ParametricSweepEngine`, injecting values equal to the hand-computed SI metres — asserted on the
injected `Variable`, not on a downstream result, so the test names the actual quantity. Plus the
frequency control: a `GHz` sweep is bit-identical before and after.

### M3 — every consumer audited, and the change surfaced

Walk §1.3's list. For each site, establish whether it now produces a different number and whether
that difference is the intended correction or a second bug the old wrongness was masking. Pay
particular attention to `SweepAxisRowViewModel.BuildValues`'s `?? 1.0` (R-len-6) and to the
MKlopf entry-mode conversion, which round-trips a length through the table twice.

Settle §5 question 2 and implement whatever it decides. If it is a warning, it belongs where the
design is opened, named per unit, once — not per parameter and not per frame.

**Gate.** `dotnet test` at the repo root, green. Any pre-existing test that pinned the *wrong*
behaviour is **updated with its reasoning stated inline, never deleted** — and named in the
completion note, because a test that changes here is evidence about blast radius.

### M4 — the phase gate: WB21's own sweep produces WB-B2's own numbers

A wBond loop-height sweep at 10 mil and 45 mil, through the product path, producing 1073.8 pH and
2189.9 pH — the two values WB-B2 measured through a hand-written `.cnl` when the sweep path could
not reach them. The sweep and the hand-written netlist must agree.

**Gate.** That comparison, as a test. It is the one claim in this brief that proves the fix reaches
a user rather than a dictionary. Mark it `Category=Benchmark` only if it measures over the ~5 s
threshold; measure before tagging.

---

## 4. Guardrails

- **`src/Core/Expressions/Units.cs` is the fix.** `LayoutUnits` and `WBondUnits` are not touched
  (§1.4).
- **No `.cnl` / `.csch` / `.clay` / `.cws` format change without the owner's word** — `src/Core/CLAUDE.md`'s
  own "Ask before" rule. §5 question 3 exists so this is answered rather than assumed. The design
  intent is that no format change is needed (R-len-3).
- **No new unit dimension, no unit *algebra*.** The engine multiplies a resolved value by a scalar;
  it does not track dimensions and this brief does not make it start.
- **`src/Engine` is touched only in `ParametricSweepEngine`**, and only if M2 concludes it must be.
- The UI firewall is unchanged: `Units` is framework-free and stays that way.
- Do not add `in`/`inch` on your own initiative — §5 question 4.

---

## 5. Open questions the owner should settle before or during M1

1. **What is the base symbol for length, and can `"m"` be it?** Three shapes:
   (a) re-point `_scales["m"]` at 1.0 (the metre) and drop the bare-prefix reading — cheapest and
   most readable, *if* R-len-1's measurement shows no bare-prefix use;
   (b) keep `"m"` as milli and introduce a distinct internal symbol for the metre — safest, but it
   appears on a sweep axis label (R-len-3), so it needs to read acceptably or need a display map;
   (c) something else entirely. **This decision blocks M1.**
2. **Should opening a design that used `nm`, `cm`, `m` or a length sweep say so?** The values change.
   Silent correction is defensible (the old ones were wrong); a one-line report naming the unit and
   the factor is friendlier and costs little. Not both.
3. **Does anything about this touch a persisted format?** The intent is no (R-len-3). If M1's chosen
   answer forces a change to the `Unit=`/`PsaUnit` spelling, stop and ask before writing it.
4. **`in` / `inch`:** the expression engine throws on them today while `LayoutUnits` accepts both.
   Add them, or leave the asymmetry and state it? Adding is one row and one map entry; leaving it is
   defensible if nobody authors inches in a netlist.

---

## 6. Known gaps that are NOT this phase

- **Unit *dimensions* are not checked.** Nothing stops `R = 5 mm`; the engine multiplies and moves
  on. Real dimensional analysis is a language change, not a table change.
- **`Scale(...) ?? 1.0` remains the fallback idiom** at several sites (R-len-6). Listing them is in
  scope; changing them all is not.
- **The two other unit systems stay separate** (§1.4).
- **WB21 itself.** This brief unblocks the length sweep; it does not build whatever WB21's own brief
  scopes on top of it.

---

## 7. Completion note — what to record

In `src/Core/CLAUDE.md` (the table lives in `src/Core`) and, because this is how it was found, a
back-reference from `src/Ui/CLAUDE.md`'s WB-B2 entry:

- **The measured before/after table** for all six length units — the §1.1 table with a correct
  column beside it. This is the whole phase in one artifact.
- **The base-symbol decision and its reasoning**, including whichever of §5 q1's shapes was rejected
  and why. The next person to look at `_scales["m"]` must not have to re-derive it.
- **Whether `nm`/`cm` moving into `_scales` fixed a `.cnl` token-gate bug** (R-len-2), with the test
  that proves it either way.
- **Every test that had to be updated**, by name, with what it had been pinning. Those are the
  measurement of blast radius.
- **The §1.3 consumer list, re-verified** — which sites changed numbers and which did not.
- **What §5 questions 2 and 4 settled on**, stated as decisions, not as behaviour to be inferred
  from the diff.
