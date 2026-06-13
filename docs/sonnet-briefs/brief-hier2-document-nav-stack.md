# Brief hier2 — Document navigation stack + active-VM retarget

**For:** Claude Code (Sonnet) · **Phase:** 6i hierarchy navigation, step 2 of 4
**Design authority:** `docs/design/schematic-hierarchy-navigation.md` (§1.2, §2, §6). Read it first.
**Prereq:** hier1 (session registry) landed and green.

## Goal
Let a single `SchematicDocument` (tab) navigate a hierarchy **in place** — no new tab. The document holds
a **stack of navigation frames**; the view renders the **active** frame's session VM. This brief adds the
stack + retarget plumbing only; the actual Push In / Pop Out *commands and wiring* are hier3, and the
breadcrumb UI is hier4. Use a tiny internal test hook to drive push/pop here.

## Scope (do exactly this)

### A. `src/Ui/Schematic/SchematicDocument.cs`
1. **Frame model:** a private `readonly record struct NavFrame(SchematicViewModel Session, string Label)`
   and a `List<NavFrame> _frames` initialized with the base frame (the VM passed to the constructor; label
   = the base title). Add `int NavDepth => _frames.Count - 1;` and `bool CanPopOut => NavDepth > 0;`.
2. **Active VM:** replace direct use of the single `ViewModel` with an **active** accessor:
   `public SchematicViewModel ActiveViewModel => _frames[^1].Session;`
   Keep the existing `ViewModel` property meaning "the **base** session" (rename internally if clearer,
   but the constructor still receives the base VM). `Model` becomes `=> ActiveViewModel.RenderModel`.
3. **Notify on retarget:** add `event EventHandler? ActiveViewModelChanged;`. Raise it whenever the active
   frame changes (push/pop/popTo). Also raise `OnPropertyChanged(nameof(Model))` so the canvas binding
   refreshes.
4. **Navigation ops (pure stack — no workspace knowledge):**
   - `void PushIn(SchematicViewModel session, string label)` → push frame, raise events, update title.
   - `bool PopOut()` → if `CanPopOut`, pop, raise events, update title, return true (else false). Return
     the popped session so the caller (hier3/WorkspaceViewModel) can retire it if unreferenced.
     Signature suggestion: `SchematicViewModel? PopOut()` returning the popped session or null.
   - `void PopTo(int frameIndex)` → pop down to `frameIndex` (clamp; index 0 = base), returning the list
     of popped sessions (for retirement). Used by the breadcrumb (hier4).
   - Expose the frames read-only for the breadcrumb: `IReadOnlyList<(SchematicViewModel Session, string Label)> NavFrames`.
5. **Title + dirty follow the active frame.**
   - Title shows the active frame. Suggested format: base title when depth 0; otherwise the active cell's
     name (the breadcrumb in hier4 shows the full path, so the tab title can be just the active cell name).
     Preserve the existing `• ` dirty-bullet behavior, now driven by the **active** session's dirty state.
   - The current dirty wiring subscribes to **one** VM's `UndoRedo` in the constructor. Refactor so the
     document tracks the **active** session's dirty signal: on each retarget, unsubscribe the old active
     session's dirty source and subscribe the new one; recompute the bullet. (Each session also remains
     independently dirty for Save All — that's hier1; here we only reflect the *active* one in this tab's
     title.)
   - Keep `Model` change-notification alive across retargets (re-point the `RenderModel`→`Model`
     forwarding to the active session on retarget).

### B. `src/Ui/Views/Content/SchematicView.axaml.cs`
The view currently binds the canvas `Model="{Binding Model}"` (fine — `Model` now follows active) and
reaches the VM via `Vm => (DataContext as SchematicDocument)?.ViewModel`, subscribing in
`OnDataContextChanged`. Retarget it:
1. Change `Vm` to return `(DataContext as SchematicDocument)?.ActiveViewModel`.
2. Factor the body of `OnDataContextChanged` (unsubscribe old VM, set `SchematicCanvasCtrl.EditContext`,
   subscribe new VM's `PropertyChanged`/`Selection.Changed`, set `AutoGenSymbolCallback`, update button
   states) into a private `RebindActiveViewModel()`.
3. Call `RebindActiveViewModel()` from `OnDataContextChanged` **and** subscribe to
   `SchematicDocument.ActiveViewModelChanged` (subscribe on DataContext set to a `SchematicDocument`,
   unsubscribe on DataContext change) → on that event, call `RebindActiveViewModel()` and
   `SchematicCanvasCtrl.InvalidateVisual()`.
4. Keep `_subscribedVm` tracking so we always unsubscribe the previously-bound active VM.

### C. Test hook
Add an `internal` method on `SchematicDocument` is not needed if `PushIn`/`PopOut` are already public.
Ensure they're reachable from tests (public or internal+`InternalsVisibleTo`, matching the codebase).

## Constraints / rules
- **No new tab/window** on push/pop — only the active frame changes.
- Don't resolve cells or touch the registry here — `PushIn` takes an already-resolved session VM. (hier3
  resolves and calls it.)
- Selection is shared (the session is shared) — accepted per design §1.1. Do not add per-view selection.
- Canvas zoom/pan live in the canvas control; a retarget should not reset the user's zoom unexpectedly —
  verify the canvas keeps its transform across a `Model` swap (if it resets, note it; a sensible default is
  to leave the transform alone, since the same cell pushed/popped should feel continuous). If the canvas
  ties transform to the model identity, flag it rather than hacking.
- Firewall unaffected.

## Tests (add; keep green)
`tests/Ui.Tests/HierarchyNavStackTests.cs` (headless on `SchematicDocument` with stub sessions):
- Construct a doc on session A; `PushIn(B,"X1")` → `NavDepth==1`, `ActiveViewModel==B`, `CanPopOut`.
- `PopOut()` → returns B, `ActiveViewModel==A`, `CanPopOut==false`, `PopOut()` again returns null/no-op.
- `PushIn(B)`, `PushIn(C)`, `PopTo(0)` → back to A, returns [C,B] (order documented).
- `ActiveViewModelChanged` fires on each push/pop/popTo.
- Title/dirty: with B dirty and active, the doc title shows the bullet; popping to clean A clears it.
- (If `SchematicViewModel` is hard to stub, construct real ones over tiny in-memory `SchematicEditModel`s,
  mirroring existing VM tests.)
- Full suite green; report count.

## Done when
- A `SchematicDocument` can push/pop/popTo frames; `ActiveViewModel`/`Model`/title/dirty follow the active
  frame; `ActiveViewModelChanged` drives the view to rebind and repaint.
- No command/menu wiring yet (that's hier3) — but the stack + retarget are exercised by tests.
- Full suite green; report the number and confirm whether canvas zoom/pan survives a retarget.
