# Sonnet Brief — URGENT: crash selecting a snap-distance value (re-entrant collection rebuild)

Selecting an item in the snap-distance combobox throws
`ArgumentOutOfRangeException` from Avalonia's selection model and takes the process down.

**This is a defect in the previous brief's instructions, not a misreading of them** — see §2.

Gate command is plain `dotnet test`.

---

## 1. The exact loop, from the stack trace

```
ListBoxItem.OnPointerReleased
  → SelectingItemsControl.UpdateSelection → BatchUpdateOperation.Dispose
    → SelectionModel.CommitOperation → OnSelectionModelSelectionChanged
      → LayoutEditorView.OnSnapDistanceSelectionChanged
        → LayoutEditorViewModel.CommitSnapLadderSelection
          → CommitSnapDistanceText
            → set_SnapDbu → OnSnapDbuChanged
              → RebuildSnapLadderOptions()          ← mutates the ObservableCollection
                → ObservableCollection.OnCollectionChanged
                  → SelectionNodeBase.OnPostChanged
                    → InternalSelectionModel.OnSourceCollectionChangeFinished
                      → SelectionModel.CommitOperation → OnSelectionModelSelectionChanged
                        → SelectedItems.GetEnumerator → ItemsSourceView.get_Item(index)
                          → ArgumentOutOfRangeException
```

**`RebuildSnapLadderOptions()` mutates the very collection the ComboBox is mid-way through notifying about.**
Avalonia's `SelectionModel` still holds the pre-rebuild index; after the collection is rebuilt that index no
longer exists, and reading it throws.

## 2. Why this happened

`brief-snap-combobox-and-consistency.md` said two things and omitted a third:

- **R-cmb-1** — "the ladder always contains the document's current `SnapDbu`… insert it in sorted position."
- **R-cmb-2/3** — "rebuild the ladder when the technology resolves… when the display unit changes."
- **It never said: never mutate the items collection in response to `SnapDbu` changing.**

Wiring the rebuild to `OnSnapDbuChanged` is a reasonable reading of those instructions, and it creates the
loop, because selecting a ladder entry *sets* `SnapDbu`.

## 3. Fix — preferred: the items list must not depend on `SnapDbu` at all

**R-crash-1. `RebuildSnapLadderOptions()` is never called from `OnSnapDbuChanged` or any selection path.** The
ladder is a function of **technology and display unit only**. Those are the only two things that may rebuild
it, and neither fires from a selection.

**R-crash-2. Show an off-ladder value through the ComboBox's text, not by inserting a list entry.** The stack
shows a `CommitSnapDistanceText(string)` path already exists, so the control accepts typed text (R-snp-3). If
it is editable, the text can display **any** value while the item list stays static — which satisfies R-cmb-1's
actual requirement (never blank) **without ever mutating the collection.**

That removes the root cause rather than guarding it. R-cmb-1 named the wrong mechanism; the requirement was
"never blank," not "insert a rung."

## 4. Fix — fallback, if the ComboBox cannot show a non-member value

If the control genuinely cannot display a value outside its items, then all three of these are required
together:

**R-crash-3. Skip the rebuild when the incoming value is already in the list.** Selecting a ladder entry can
never need a rebuild — the value came *from* the list. This alone breaks the reported loop.

**R-crash-4. Add a re-entrancy guard.** A simple flag so a rebuild cannot run while one is already in progress
or while a selection change is being handled. Necessary but **not sufficient** on its own — it prevents the
crash while leaving the selection stale.

**R-crash-5. Reconcile in place; never `Clear()` and refill.** A clear-and-refill invalidates every index and
destroys the selection even outside this loop. Insert or remove **only** the entries that differ, so the
selected item survives the update.

If a rebuild genuinely must happen during a notification, **post it to the dispatcher** so it runs after the
selection change completes.

## 5. Guardrails

- Do not fix this by swallowing the exception or wrapping the handler in `try/catch`. The collection mutation is
  the bug.
- Do not make the setter skip `OnSnapDbuChanged` entirely — other things legitimately depend on it. Only the
  **ladder rebuild** must go.
- Do not reintroduce blanking while fixing this; both properties must hold together.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **The reported crash** — select **every** entry in the ladder in turn, one after another, in a single
   session. No exception. This is the direct repro.
3. **Off-ladder current value** — open a `.clay` whose `SnapDbu` is off-ladder (e.g. 0.5 mil), then select a
   ladder entry, then re-enter the off-ladder value. No crash, and **never blank** at any point.
4. **Typed value** — commit a typed value that is not a ladder entry, then select a ladder entry. No crash.
5. **Headless re-entrancy test (R-crash-1)** — set `SnapDbu` directly on the view-model and assert the ladder
   collection **raises no change notification**. This is the assertion that keeps the fix from regressing, and
   it needs no UI.
6. **Rebuild still happens when it should (R-cmb-2/3)** — changing the technology and changing the display unit
   each repopulate the ladder, with the current selection preserved and never blank.
7. **In-place reconciliation (R-crash-5, if §4 was taken)** — a rebuild that changes one entry raises a
   targeted change, not a reset; the selected item survives.

## 7. On completion

Record in `src/Ui/CLAUDE.md`: **that the snap ladder must never be rebuilt in response to `SnapDbu` changing**
— only on technology or display-unit change — and that the crash was a re-entrant `ObservableCollection`
mutation inside Avalonia's `SelectionChanged` notification. Note the general rule plainly, because this pattern
will recur anywhere a selection handler writes back to a property that feeds its own items source: **an items
collection must not be a function of the selection made from it.**
