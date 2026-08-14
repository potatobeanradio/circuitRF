# Brief — harmonicaRF Round 3A: the macOS menu bar, and the crash it causes

**Read first:** `src/Ui/Views/Harmonica/HarmonicaMenuView.axaml.cs` in full (all 253 lines — every
comment in it is about this problem), then `WorkspaceViewModel.UpdateHarmonicaDockedMenuFocus` /
`RestoreCircuitRfMenuBar` / `AttachSharedNativeMenuIfMacOS` (`src/Ui/ViewModels/WorkspaceViewModel.cs`
~8410–8470 and ~8735–8760), `WorkspaceWindow.AttachNativeMenuAtApplicationScope`
(`src/Ui/Views/WorkspaceWindow.axaml.cs` ~120–150), and R2B §1.1/§1.2 in
`docs/sonnet-briefs/brief-harmonicarf-r2b-menus-chrome-and-readouts.md`.

**This brief supersedes R2B §1.2's own conclusion.** That section wrote the crash off as "a genuine
Avalonia.Native race this view cannot see into", added `try`/`catch` around the attach and a
"defensive clear" of the desired target, and said so in the code. **That diagnosis is wrong, the
mechanism is fully knowable, and the defensive clear it added actively makes things worse.** §1 below
gives the mechanism from Avalonia 12.0.3's own source. Do not re-derive it; do verify it.

---

## 0. What the owner reported

> *"Native menu is still not shown on macOS when a harmonicaRF document is in focus. It does show up
> when the document is detached from the dock. Also, circuitRF crashed when I switched apps in macOS
> and then gave the document focus again. It crashed both when the document was detached and attached
> to the dock. Same crash for a lot of other circuitRF operations, like opening the circuitRF
> settings."*

```
Unhandled exception. System.ArgumentException: The menu being updated does not match. (Parameter 'menu')
   at Avalonia.Native.Interop.Impl.__MicroComIAvnMenuProxy.Update(IAvaloniaNativeFactory factory, NativeMenu menu)
   at Avalonia.Native.AvaloniaNativeMenuExporter.SetMenu(IAvnWindow avnWindow, NativeMenu menu)
   at Avalonia.Native.AvaloniaNativeMenuExporter.DoLayoutReset(Boolean forceUpdate)
   at Avalonia.Native.AvaloniaNativeMenuExporter.DoLayoutReset()
   at Avalonia.Threading.DispatcherOperation.InvokeCore()
   at Avalonia.Threading.DispatcherOperation.Execute()
   at Avalonia.Threading.Dispatcher.ExecuteJobsCore(Boolean fromExplicitBackgroundProcessingCallback)
```

**The two halves are one bug.** The menu not appearing and the crash have the same cause, and one fix
closes both.

---

## 1. The mechanism, from Avalonia 12.0.3's own source

Read these two files if you want to confirm it (fetched with
`gh api "repos/AvaloniaUI/Avalonia/contents/src/Avalonia.Native/AvaloniaNativeMenuExporter.cs" --jq '.content' | base64 -d`,
and the same for `src/Avalonia.Native/IAvnMenu.cs`). Four facts, in order:

1. **Each `TopLevel` gets exactly one `AvaloniaNativeMenuExporter`, created once, for the window's
   lifetime.** `NativeMenu.MenuProperty.Changed` (in `NativeMenu.Export.cs`) routes every
   `NativeMenu.SetMenu(window, x)` to that one exporter's `SetNativeMenu(x)`. There is no per-menu
   exporter and nothing is torn down when the attached property changes.

2. **The exporter binds itself to the FIRST `NativeMenu` instance it is ever given, permanently.**
   ```csharp
   private void SetMenu(IAvnWindow? avnWindow, NativeMenu menu)
   {
       if (_nativeMenu is null)                       // first time only
       {
           _nativeMenu = __MicroComIAvnMenuProxy.Create(_factory);
           _nativeMenu.Initialize(this, menu, "");     // <-- ManagedMenu = menu, forever
           setMenu = true;
       }
       _nativeMenu.Update(_factory, menu);
       ...
   }
   ```
   and in `__MicroComIAvnMenuProxy`:
   ```csharp
   internal void Update(IAvaloniaNativeFactory factory, NativeMenu menu)
   {
       if (menu != ManagedMenu)
           throw new ArgumentException("The menu being updated does not match.", nameof(menu));
       ...
   }
   ```
   `Initialize` is never called a second time. **So on macOS you can never change WHICH `NativeMenu`
   instance a given window shows.** Setting a different one throws, synchronously, out of
   `NativeMenu.SetMenu`.

3. **`SetNativeMenu(null)` is not a clear — it substitutes a brand-new empty `NativeMenu`:**
   ```csharp
   _menu = menu ?? new NativeMenu();
   DoLayoutReset(true);
   ```
   so `NativeMenu.SetMenu(window, null)` on a window that already has one **also throws**, for the
   same reason, and leaves `_menu` pointing at that throwaway empty menu.

4. **`_menu` is assigned BEFORE the throw, and a later dispatcher-queued reset re-reads it.**
   `__MicroComIAvnMenuProxy.Initialize` subscribes to `ManagedMenu.Items.CollectionChanged`; any item
   added to or removed from the exporter's *original* menu calls `QueueReset()`, which does
   `Dispatcher.UIThread.Post(DoLayoutReset, DispatcherPriority.Background)`. That queued call runs
   `SetMenu(_nativeWindow, _menu)` — with the poisoned `_menu` — and throws **on the dispatcher, where
   no `try`/`catch` at our call site can reach it.** That is the reported stack trace, exactly.

### 1.1 — What this means for our code, concretely

`HarmonicaMenuView.RecomputeAttachment` currently does, on a docked document taking focus:

```csharp
if (_attachedTo is { } current && !ReferenceEquals(current, desiredTarget))
    try { NativeMenu.SetMenu(current, null); } catch (Exception) { }
try { NativeMenu.SetMenu(desiredTarget, null); } catch (Exception) { }   // R2B's "defensive clear"
try { NativeMenu.SetMenu(desiredTarget, _ownMenu); _attachedTo = desiredTarget; }
catch (Exception) { _attachedTo = null; }
```

Against the `WorkspaceWindow`, whose exporter was bound at startup to circuitRF's own app menu:

- the defensive clear sets `_menu = new NativeMenu()` and throws → **swallowed, but the window's
  exporter is now poisoned**;
- the real attach sets `_menu = _ownMenu` and throws → **swallowed, so harmonicaRF's menu bar never
  appears**. That is the "not shown when docked" half, in one line;
- the window keeps showing circuitRF's menu (the native side was never updated);
- **from that moment, the next time anything mutates circuitRF's own menu items, the app dies.**
  `WorkspaceWindow.RebuildNativeWindowMenu` rebuilds the Window menu's items on `Activated` — i.e.
  **on every app switch** — and opening Settings goes through the app-menu path too. That is the
  owner's "switched apps and then gave the document focus again", and the "same crash for a lot of
  other circuitRF operations".

`RestoreCircuitRfMenuBar` sets the *original* instance back, which matches `ManagedMenu` and therefore
succeeds — which is why the failure is intermittent rather than immediate, and why it looked like a
race.

**The torn-off case works because that window's exporter has never had a menu**, so `_nativeMenu is
null` and `Initialize` binds it to `_ownMenu`. Nothing about the attach code is right there and wrong
here; the window's history is the whole difference.

**Verify all of this before fixing it** — read the two Avalonia files yourself and confirm the flow.
Say in the completion note that you did, and name anything the source says that this section does not.

---

## 2. The fix

**Rule, and it is the whole brief: on macOS, a window's `NativeMenu` instance is chosen once and never
changed. To change what the menu bar SHOWS, mutate that instance's `Items`.** Item mutation is the
supported path — `OnMenuItemsChanged` → `QueueReset` → `Update` walks the item list and inserts,
moves and removes native items to match.

### 2.1 — Docked focus injects items into the app menu, it does not replace it

`RecomputeAttachment`'s three cases become two:

| case | what happens |
|---|---|
| torn-off document window, or the standalone shell | unchanged — `NativeMenu.SetMenu(window, _ownMenu)` on a window that has never had one. **Assert that precondition** (see §2.3). |
| docked | **never touch `NativeMenu.SetMenu` on the `WorkspaceWindow` at all.** On focus, append harmonicaRF's own top-level items to the app menu instance; on blur, remove exactly those. |

Where "the app menu instance" is `NativeMenu.GetMenu(Application.Current)` — the SAME object
`WorkspaceWindow.AttachNativeMenuAtApplicationScope` captured off the shell window at startup and the
same one `RestoreCircuitRfMenuBar` reads. It is the shell window's exporter's `ManagedMenu`, which is
what makes mutating it work.

**The injected items must be freshly-built `NativeMenuItem` instances, not `_ownMenu`'s children.**
`NativeMenu`'s own list validator throws `InvalidOperationException` for an item that already has a
`Parent`:
```csharp
void IAvaloniaListItemValidator<NativeMenuItemBase>.Validate(NativeMenuItemBase item)
{
    if (item.Parent is { } parent) throw new InvalidOperationException(...);
}
```
So build them from the same source the two existing surfaces already build from — the
`HarmonicaMenuViewModel`'s own collections and commands. `RebuildNativeBandMenus` is the pattern; the
injected set is a third rendering of one source, not a copy of a second.

**Decide and state which items get injected.** Two defensible answers:
- **(a) harmonicaRF's own top-level menus only** (Markers / Display / Grid …), appended after
  circuitRF's — the user keeps File/Edit/View/Simulate/Help *and* gains the document's menus. This is
  the better UX and is what a document-scoped menu normally means on macOS.
- **(b) a full swap** — remove circuitRF's items, add harmonicaRF's, restore on blur. Closer to what
  R-h9a-3 originally described, and strictly more to get wrong.

**Take (a) unless you find a concrete reason not to**, and say why in the completion note. Either way,
removal on blur must remove exactly what was added — keep the injected item references in a field and
remove *those*, never by header match, never by index.

### 2.2 — Delete the defensive clear and say so

Remove the `try { NativeMenu.SetMenu(desiredTarget, null); }` line R2B added. §1 shows it is a
poisoning step, not a safety step. Replace R2B's remark in `RecomputeAttachment`'s doc comment with
the real mechanism — the current text tells a future reader that this is an unknowable platform race,
which is now known to be false and would send them the wrong way.

The `try`/`catch` around the *real* attach may stay as a floor, and `DetachNativeMenuFromWindow`'s own
guarded detach stays as it is (that one runs against a window that is already being destroyed, which
is a different and legitimate case — its comment is accurate).

### 2.3 — The one remaining exclusivity trap, which you must check rather than assume

`WorkspaceViewModel.AttachSharedNativeMenuIfMacOS(shellWindow, tornOffWindow)` attaches the shared app
menu to **every** floated window. A torn-off harmonicaRF document window that gets the shared menu
FIRST can never afterwards show `_ownMenu` — its exporter would already be bound. The owner reports
torn-off currently works, so today's ordering happens to favour harmonicaRF, **and nothing enforces
that.**

Find the call site (`WorkspaceViewModel.cs` ~8684), determine the actual order, and make the
invariant explicit: a window that floats a harmonicaRF document must not be given the shared menu.
Gate it. A comment is not enough — this is exactly the kind of ordering that a future dock change
silently inverts.

### 2.4 — A dispatcher backstop, as a floor and not as the fix

Even with §2.1 done, a queued `DoLayoutReset` that throws takes the process down and no call-site
`catch` can see it. Avalonia 12 exposes `Dispatcher.UnhandledException`
(`Avalonia.Threading.Dispatcher.Exceptions.cs`, `DispatcherUnhandledExceptionEventArgs.Handled`).
Subscribe once at application start (`App.axaml.cs`, beside the existing macOS menu wiring), log it
through whatever this app already uses, and set `Handled = true` **only** for this specific failure —
match on `ArgumentException` whose stack originates in `Avalonia.Native` menu code, not on
`Exception`. A blanket handler that swallows everything would hide real bugs and is refused.

**State plainly in the completion note that this is a backstop, and that §2.1 is the fix.** If you
find yourself relying on it to make the menu work, you have not fixed the bug.

---

## 3. Gates

Nothing here is headlessly reproducible — `Ui.Tests` has no Avalonia platform and no
`AvaloniaNativeMenuExporter`. The existing `HarmonicaMenuNativeAttachTests` already source-scans this
file for R-h9a-1's discipline; extend that approach.

1. **Build + `dotnet test` green.** `tests/Ui.Tests` and `tests/Firewall.Tests` while working; full
   solution at the end.
2. **A source-scan test asserts `HarmonicaMenuView` never calls `NativeMenu.SetMenu` with a
   `WorkspaceWindow`-typed target** — i.e. the docked path no longer swaps instances at all.
3. **A source-scan test asserts the defensive `SetMenu(desiredTarget, null)` line is gone.**
4. **A headless unit test over the item-injection logic itself**, factored so the "which items go into
   the app menu, and which come back out" decision is a pure function of the view model's collections
   and a bool, testable with no window: inject → the app menu's `Items` contains exactly circuitRF's
   original items plus harmonicaRF's; withdraw → it is `SequenceEqual` to the original list, by
   reference. Round-trip it twice — a second inject must not duplicate.
5. **A test asserts the injected items are fresh instances** (their `Parent` is the app menu, and
   `_ownMenu`'s own item list is unchanged) — the `Validate` throw in §2.1 is a real one and a test
   that never exercises it proves nothing.
6. **The `AttachSharedNativeMenuIfMacOS` ordering invariant from §2.3 is gated**, by whatever means
   fits (source scan of the call site, or a unit test over the predicate that decides it).

**Interactive verification is required** and must be listed in the completion note under "please
confirm on your end":
- docked harmonicaRF document focused → harmonicaRF's menus appear in the macOS bar;
- click away to another document → they disappear, circuitRF's bar is intact and complete;
- switch to another application and back, repeatedly, with the harmonicaRF document focused → no
  crash;
- open circuitRF Settings while a harmonicaRF document is focused → no crash;
- tear the document off → its own menu bar appears, unchanged from today;
- re-dock it → §2.1's docked behaviour, and still no crash;
- switch the OS colour theme light↔dark with the document focused and docked (R2B §1.2's own gesture).

---

## 4. Scope guardrails

- **Nothing in this brief touches the frame loop, the solver, the panels or the readout strip** —
  those are **R3B** and **R3C**.
- **Do not change `WorkspaceWindow.AttachNativeMenuAtApplicationScope`'s own behaviour.** Attaching one
  instance to several *fresh* windows is fine and is what makes floating tool windows show a bar; §1
  only forbids giving one window a *second* instance.
- **Do not build a second copy of circuitRF's menu** anywhere. One instance, mutated.
- `src/Core`, `src/Engine`, `RfCore`, `src/Harmonica` untouched — this is `src/Ui` only.

---

## 5. Write-up — READ THIS BEFORE YOU FINISH

**Do NOT append a phase write-up to any `CLAUDE.md`.** `src/Ui/CLAUDE.md` reached 21,417 lines that
way and had to be archived; the maintenance rule at the top of it stands.

Instead: **create `src/Ui/RESOLVED.md`** (it does not exist yet) and put this brief's detail there,
following the shape of the existing `src/Ui/DataDisplay/RESOLVED.md` — a title, a one-paragraph note
about why the file exists, then one `##` section per completed brief.

**Use it sparingly.** Only findings that are still true, still surprising, and would cost someone real
time to rediscover. For this brief that is a short list, and §1 is most of it:

- the exporter's one-menu-per-window binding, with the `Update` guard quoted;
- `SetMenu(window, null)` being a substitution rather than a clear;
- `_menu` being assigned before the throw, which is what turns a swallowed exception into a delayed
  crash on the dispatcher;
- the item-`Parent` validator that forces fresh instances.

Everything else — what you renamed, which test you added, how the injection is structured — belongs in
the completion note you hand back, not in a checked-in file. If `src/Ui/CLAUDE.md` needs anything at
all, it is at most **one or two lines** stating the standing invariant ("on macOS a window's
`NativeMenu` instance is fixed for its lifetime; change the items, never the instance — see
`RESOLVED.md`"), because that is a rule the next person must not violate.
