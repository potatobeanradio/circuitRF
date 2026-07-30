# Sonnet Brief — L5 follow-ups: parameter resolution, MTee orientation, PCell inspector and double-click

Six owner reports after `brief-L5-schematic-to-layout.md` landed. **§1 is diagnosed precisely and is probably
also the cause of half of §6** — read it first.

Gate command is plain `dotnet test`.

---

## 1. Bug: PCell parameters "could not be resolved"

Reported: `MKF1 (MKLOPF): parameter 'Z1' could not be resolved — skipped.` with **default** parameters. After
switching MKlopf to `W1`/`W2` entry: `parameter 'GammaMax' could not be resolved`.

### 1.1 Cause

`SchematicToLayoutGenerator.ResolveComponentLayout` walks **every** parameter on the schematic instance and
evaluates its **stored expression**:

```csharp
foreach (var p in comp.Parameters)
{
    if (NonPCellParamNames.Contains(p.Name)) continue;
    if (!TryResolveSiValue(p.Expression, p.Unit, scope, evaluator, out var value))
    {
        resolveWarning = $"parameter '{p.Name}' could not be resolved";
        return null;
    }
    ...
}
```

`TryResolveSiValue` wraps `evaluator.Eval` in a bare `catch { return false; }`.

**A parameter the user has never touched has no stored expression** — the value lives in the component's
declared default, not on the instance. So `Eval("")` throws, the catch swallows it, and the first untouched
parameter is reported.

**That explains both messages exactly.** With defaults, the first parameter reached is `Z1`. After the owner
edited the entry mode, `Z1`/`Z2` and `W1`/`W2` had been written, so the next *untouched* one — `GammaMax` —
failed instead. The pattern "it always names the first parameter I haven't edited" is the signature.

### 1.2 Fix

**R-L5f-1. Fall back to the component's declared default when the instance's stored expression is empty.**

**R-L5f-2. Better: use the resolution path the netlist already uses.** A schematic with default MKlopf
parameters **simulates correctly today** — so a working default-resolution path exists in elaboration. This
is a *second* resolution path that doesn't match it, which is the same failure as the S-parameter card's
private `FormatFreq`. Find how elaboration resolves an untouched parameter and route through it rather than
adding a default lookup here.

**R-L5f-3. Skip parameters that are inactive for the current entry mode.** With `Z1`/`Z2` entry selected,
`W1`/`W2` are not in play (and vice versa) — resolving them is meaningless and will fail the moment one is
blank. `NonPCellParamNames` already establishes the skip mechanism; entry-mode-inactive parameters need the
same treatment, driven by the component's own entry-mode state rather than a hardcoded name list.

**R-L5f-4. Never swallow the exception.** `catch { return false; }` is why this surfaced as a vague
"could not be resolved" instead of naming the actual failure. Capture the exception message and include it in
the report — the difference between *"parameter 'Z1' could not be resolved"* and *"parameter 'Z1': empty
expression"* is the difference between an afternoon and a minute.

## 2. Bug: default MTee draws with port 3 pointing up

The symbol was previously corrected for this same "upside-down T"; the PCell now disagrees with it.

**R-L5f-5. The branch arm points the same direction the symbol's port 3 does — downward.** State the rule as
*match the symbol*, not as a coordinate sign. `brief-mtaper-mklopf.md` and the PCell contract's R4 said
"branch along +Y", and +Y is up in layout coordinates — which is how this happened. Fix the generator, and
**correct the wording in both documents** so the next reader doesn't reintroduce it from the spec.

Assert it against the symbol's own port-3 direction rather than against a hardcoded sign, so the two cannot
drift again.

## 3. Bug: double-clicking a PCell enters its sub-hierarchy

**R-L5f-6. Double-clicking a PCell instance opens its parameter editor. Push-in is disabled for PCells, with
a reason.**

Entering a PCell's contents is meaningless: its geometry is *generated* and read-only (PCell contract R9), so
there is nothing to edit inside it. The parameters **are** the way to modify it — that is what the generator
is for. Push-in remains correct for ordinary hierarchical cells.

Disable with a stated reason per R13a — *"A parametric cell's geometry is generated; edit its parameters
instead."* — rather than silently doing nothing on double-click.

## 4. Bug: the Properties Inspector shows Cell and Re-target for a PCell

**R-L5f-7. Hide the cell reference and the Re-target button for a PCell instance.**

Re-targeting is meaningless here: a generated cell is *derived* from `(generator, parameters, technology)`, so
pointing the instance at a different cell either breaks the derivation or silently detaches the instance from
its parameters. The generated cell's identity is an implementation detail of the PCell mechanism, not
something a user should see or repoint.

Both stay visible for ordinary instances.

## 5. Add: the Properties Inspector lists a PCell instance's editable parameters

**R-L5f-8. Selecting a PCell instance shows its parameters, editable, in a virtualized list.**

**The two virtualization traps from L1j's vertex list apply verbatim** and are worth re-reading rather than
rediscovering:

- The list **must not sit inside the inspector's outer `ScrollViewer`** — unbounded height realizes every row
  with no error and no symptom until a cell has many parameters.
- Row view-models need **lazy, index-addressed materialization**, because Avalonia virtualizes *containers*,
  not *items*.

Values are literal (no expressions — a layout has no variable scope), parsed and formatted through the
layout's display unit like every other dimension field, and SI underneath per R-pc-6.

**R-L5f-9. Editing a parameter here is copy-on-write (R-L5-2).** If other instances reference the same
generated cell, the edit forks a new cell rather than altering its siblings. And per R-L5-9 the schematic
still wins on the next re-run — which is reported, not silent.

## 6. Bug: a deleted PCell is not recreated by a re-run

### 6.1 What has been eliminated

| | Status |
|---|---|
| `GeneratedCellStore.GetOrCreate` | correct — `if (File.Exists(clayPath)) return cellDir;` is a proper get-or-create |
| The add/update branch | correct — `!hasExisting` takes the add path and calls `AddInstanceCommand` |

**So the add path is reachable and the cell store is not the problem.** Two candidates remain.

### 6.2 Candidate one — and check this first

**R-L5f-10. §1's resolution failure produces exactly this symptom.** When `ResolveComponentLayout` returns
null the component is `continue`d — **nothing is placed, on every run.** So for any component hitting §1, "not
recreated" is not a separate bug. Fix §1, then retest before investigating further.

### 6.3 Candidate two

**R-L5f-11. Verify that deleting a PCell instance actually removes the `LayoutInstance`.** If the delete
removes it from the selection or the render set but leaves it in `LayoutView.Instances`,
`existingBySchematicId` still finds it, the re-run takes the **update** branch, reports **unchanged**, and
nothing appears. That matches the symptom precisely — including the owner's "sometimes not updated."

Check the report: if a re-run after deleting says **unchanged** rather than **added**, this is the cause. That
single observation distinguishes the two candidates.

**Note the stale snapshot.** `target.SchematicPCellSnapshots[schematicId]` persists after an instance is
deleted. The add branch overwrites it, so it is harmless today — but confirm nothing else reads a snapshot
whose instance no longer exists.

## 7. Guardrails

- Do not add a second parameter-resolution path (§1) — converge on elaboration's.
- Do not change the MTee *symbol*; the layout follows it (§2).
- Do not enable push-in for PCells (§3).
- Do not add expression support to the layout parameter editor (§5) — literals only, as L5 decided.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 8. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Untouched parameters resolve (R-L5f-1/2)** — a freshly placed MKlopf with **no parameters edited** runs
   schematic→layout and places successfully. Same for MLIN, MBEND, MTEE, MCROSS, MTAPER.
3. **Entry mode (R-L5f-3)** — MKlopf resolves in `Z1`/`Z2` mode and in `W1`/`W2` mode; the inactive pair is
   not resolved and does not warn.
4. **Diagnostics (R-L5f-4)** — a genuinely unresolvable parameter reports the underlying reason, not just
   "could not be resolved".
5. **MTee orientation (R-L5f-5)** — the generated branch arm points the same way the symbol's port 3 does;
   assert against the symbol, not a literal sign.
6. **Double-click (R-L5f-6)** — double-clicking a PCell opens the parameter editor and does **not** push in;
   push-in is disabled with a reason; double-clicking an ordinary cell instance still pushes in.
7. **Inspector fields (R-L5f-7)** — no cell reference and no Re-target button for a PCell; both present for an
   ordinary instance.
8. **Parameter list (R-L5f-8)** — parameters are listed and editable; with many parameters, the number of
   materialized row view-models stays small (the L1j assertion).
9. **Copy-on-write (R-L5f-9)** — editing one of two instances sharing a generated cell forks a new cell and
   leaves the other unchanged.
10. **Delete and re-run (§6)** — place, delete the instance, re-run: the instance is **recreated** and the
    report says **added**. State in the completion note which of §6.2 / §6.3 it was.

## 9. On completion

Record in `src/Ui/CLAUDE.md`: that **the generator evaluated stored expressions and untouched parameters have
none** — with the note that this was a second resolution path diverging from elaboration's, the same shape as
the `FormatFreq` defect; that **`catch { return false; }` hid the real error** and now doesn't; that the MTee
branch direction is **defined by the symbol**, with the "+Y is up" wording corrected in both documents; that
**PCells never push in** because their geometry is generated; and **which candidate §6 turned out to be**.
