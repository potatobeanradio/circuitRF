# Sonnet Brief — L1h fix: the Scale dialog rewrites the width the user typed

Third report on this bug. **The linker math is correct and is not the problem** — two rounds of fixing it
were fixing the wrong layer. Read §1 before changing anything.

---

## 1. Why two rounds of fixes did not work

`ScaleFieldLinker` is correct. `TrySetWidthText` derives the factor by exact double division from the parsed
DBU and the original width, never from a rounded display string, and `ScaledWidthDbu` round-trips a typed 400
back to exactly 400. Its doc comment describes the bug accurately and the class genuinely prevents it.

**The bug is in `ScaleDialog.axaml.cs` — the shim.** And the shim is, by the project's own note, the layer
that *cannot be constructed in the headless test suite*. So the correct component was tested and stayed
correct, while the untestable one kept the defect. That is the structural reason this survived two rounds,
and it is why the fix below is mostly about **removing logic from the shim**, not repairing it in place.

### 1.1 The mechanism

Every box commits on **`TextChanged`** — on every keystroke. `RefreshFields` then writes the *other* boxes,
guarded by a `_updating` bool that is reset in a `finally` before `UpdatePreview()`.

The failure chain:

1. User types `400` in Width → `OnWidthChanged` → `TrySetWidthText` sets `FactorX = 400/origW` **exactly** →
   `RefreshFields(skipWidth: true)` writes `HeightBox.Text` and `FactorBox.Text`, both formatted to **4
   decimal places** (`"0.####"` for the factor, `LayoutUnits.Format(..., maxDecimals: 4)` for the height).
2. That programmatic `HeightBox.Text` write raises `TextChanged`. If its delivery lands after `_updating` has
   been reset — Avalonia's `TextBox` raises `TextChanged` through its text presenter and undo stack, and the
   ordering is not something to rely on — `OnHeightChanged` runs for real.
3. `TrySetHeightText` parses that **rounded** height string, sets `FactorY = roundedH/origH`, and because
   **Uniform is on it also assigns `FactorX = FactorY`** — silently replacing the exact factor with one
   re-derived from a 4-decimal display value.
4. `RefreshFields(skipHeight: true)` then writes **`WidthBox.Text`** from that degraded factor. The user's
   `400` becomes `399.98`-ish.

Step 3 is the amplifier: `TrySetHeightText` in uniform mode overwrites `FactorX`, so a stray Height event is
enough to corrupt Width. Step 4 is why the user sees it in the box they typed into.

**Do not spend time proving the exact delivery ordering.** The fix below removes the entire class of failure
whether or not `TextChanged` is deferred, and per-keystroke commit is wrong for independent reasons anyway
(typing `400` momentarily commits a width of `4`, then `40`, rewriting the other fields through nonsense on
the way).

---

## 2. Fix

### R-fix-1. Commit on LostFocus and Enter, not TextChanged

**Every other text field in the layout editor already does this.**
`LayoutShapePropertiesView.axaml.cs` says so in its header — *"Commit convention mirrors
`LayoutEditorView.axaml.cs`'s toolbar fields exactly: LostFocus commits, Enter commits"* — and the toolbar
fields, the properties panel and the flatten-tolerance field all follow it. **The Scale dialog is the only
outlier in the application.** Bring it into line: `LostFocus="On…Commit"` and `KeyDown` → Enter, dropping
`TextChanged` entirely.

This alone removes the intermediate-prefix commits and shrinks the re-entrancy window to almost nothing.

### R-fix-2. The field the user last edited is authoritative and is never written

Not "not during this refresh call" — **never**, until the user edits a different field. Track the
authoritative field in the **linker** (see R-fix-3) and have the refresh skip it unconditionally. This closes
the hole even if a stray event does arrive late, because the only box the user can observe being wrong is the
one they typed into.

### R-fix-3. Move the policy out of the untestable shim

The linker was extracted so this logic could be tested. Finish the job: the decision of *which field is
authoritative* and *which boxes are stale* is policy and belongs in the linker.

```csharp
public enum ScaleField { FactorX, FactorY, Width, Height }

// Records the edit, updates the exact factors, and marks the source field authoritative.
public bool Edit(ScaleField field, string text);

// The display string for a field, or null if it is the authoritative one and must not be written.
public string? DisplayFor(ScaleField field);
```

`ScaleDialog.axaml.cs` then reduces to: on commit, call `Edit(...)`; on success, loop the four boxes and
assign `DisplayFor(field)` where it is non-null. No `skip*` flags, no policy, nothing to get wrong in the
layer that cannot be tested.

### R-fix-4. Never write a box with the value it already holds

```csharp
if (!string.Equals(box.Text, newText, StringComparison.Ordinal)) box.Text = newText;
```

A no-op assignment cannot raise `TextChanged` if it never happens. Cheap, and it removes the re-entrancy
source outright for the common case. Keep `_updating` as well — belt and braces.

### R-fix-5. Uniform mode must not let a derived field overwrite the authoritative one

`TrySetHeightText`/`TrySetWidthText` cross-assign the other factor when `IsUniform`. That is correct when the
user genuinely edited that field, and catastrophic when the call came from a stray refresh event. With
R-fix-2 and R-fix-3 in place the stray call cannot happen — but assert it directly anyway (gate 4), because
this is the specific step that turns a cosmetic glitch into a wrong committed geometry.

---

## 3. Gate (acceptance)

Tests 1–4 are linker-level and therefore actually runnable.

1. **Typed width survives (the headline)** — `Edit(Width, "400")`, then read every field's `DisplayFor`:
   `DisplayFor(Width)` is `null` (authoritative, not written back), and `FactorX` is exactly `400·unit/origW`
   as a double. Repeat with origW chosen so the factor is a non-terminating decimal
   (e.g. origW = 137 mil, target 400 mil) — the case where 4-decimal rounding does visible damage.
2. **Refresh is idempotent** — after `Edit(Width, "400")`, applying every `DisplayFor` value and re-reading a
   hundred times leaves `FactorX` bit-identical. Any drift fails this.
3. **No round-trip through display text** — assert that feeding `FactorText` back through
   `Edit(FactorX, …)` is *not* something the dialog ever does, by asserting `DisplayFor` returns `null` for
   the authoritative field. This is the invariant, stated as a test.
4. **Uniform cross-assignment (R-fix-5)** — with `IsUniform = true`, `Edit(Width, "400")` followed by a
   simulated stray `Edit(Height, DisplayFor(Height))` must leave `FactorX` unchanged. Without R-fix-2 this
   fails, which is the regression test for the actual bug.
5. **Commit uses the exact factors** — `TryCommit` reads `linker.FactorX/FactorY`, never a text box.
6. **End-to-end** — manual, and record the result: open Scale on a rect of a deliberately awkward width, type
   `400`, tab away, confirm the box still reads `400`, press OK, and confirm the resulting shape measures 400
   in the display unit (within one DBU).
7. **Commit convention** — no `TextChanged` handler remains in `ScaleDialog.axaml`; all four boxes use
   `LostFocus` + Enter, matching `LayoutShapePropertiesView.axaml.cs`.

## 4. Guardrails

- Fix only the Scale dialog and `ScaleFieldLinker`. No changes to `ApplyScale`, `BuildScaledShapes`, the
  scale handles, or anything else in L1h — **the geometry math is correct**; `BuildScaledShapes` rounds to
  DBU without snapping and reproduces a typed width exactly, and the anchor path is fine.
- Do not add an Avalonia headless test harness for `Window` in this pass. The right move here is to shrink
  the untestable surface (R-fix-3), not to grow test infrastructure. If someone wants to revisit
  `Avalonia.Headless` later, that is its own task.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 5. On completion

Add an "L1h fix (Scale dialog)" note at the top of `src/Ui/CLAUDE.md` recording: that **the linker was
correct and the shim was not**, that the shim is the layer which cannot be tested headlessly and therefore
must hold no policy, the **LostFocus/Enter convention** the dialog was violating, and **R-fix-2 — the
authoritative field is never written back** — as the invariant that makes the whole class of bug impossible.
