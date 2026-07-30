# Sonnet Brief — Persist the dock layout in `.cws`, including floating windows

Remember how the workspace's docks are arranged: which panels are open, where they sit, which tab is visible
in a tabbed group, and the position and size of torn-off windows — restoring them safely onto a screen that
actually exists.

Gate command is plain `dotnet test`.

---

## 1. What is persisted

| | Recorded |
|---|---|
| **Tool panels** (Project Tree, Messages, Properties, Palette, …) | identity, open/closed, dock side (left/right/top/bottom), size along the docking axis |
| **Tabbed groups** | membership order and **which tab is active** |
| **Floating windows** | position, size, and which dockables they contain |
| **Document tabs** | arrangement only — see R-dock-2 |

**R-dock-1. Tool panels persist by stable ID; documents persist by path.** A tool panel's identity is fixed at
compile time; a document's is its file. Conflating them means a renamed file silently loses its placement, or
a tool panel's placement is keyed to something that can vanish.

**R-dock-2. The layout records *arrangement*, not *membership*.** `.cws` already records which documents are
open (the `kind="layout"` restore path from L0b). That list stays **authoritative for what is open**; the new
data only says where things sit. When the two disagree — a document in the layout that is not in the open
list, or vice versa — **the open list wins** and the layout entry is dropped. Two mechanisms describing the
same fact is how they drift.

## 2. Format — our own schema, not the docking library's

**R-dock-3. Define a small explicit schema inside `.cws`. Do not serialize the docking library's object graph
into it.**

`.cws` is a **human-readable, long-lived** file, which is one of the project's stated values. A third-party
library's serialized graph is neither: it is opaque to a reader, and a library upgrade can invalidate every
saved workspace in the field. A dozen fields of our own cost little and stay stable.

If the docking library ships a serializer, it may still be useful as a *reference* for what needs capturing —
but the bytes in `.cws` are ours.

**R-dock-4. Version the layout block, and treat it as optional.** A `.cws` without it opens on the default
layout; a `.cws` with a *newer* version than the code understands opens on the default layout and reports.
Neither is an error.

## 3. Restore must never prevent a workspace from opening

**R-dock-5. Any failure to restore the layout falls back to the default layout and reports — it never fails
the open.** A malformed block, a missing panel ID from an older build, a floating window referencing a
dockable that no longer exists: all of these are recoverable, and none of them is a reason a user cannot get
to their work.

This is the single most important rule here. A layout is a convenience; a workspace is the user's data.

## 4. Floating windows — the off-screen problem

The owner has seen this fail in other tools, particularly with multiple monitors. It is worth doing properly
because the failure mode is **unrecoverable by the user**: a window whose title bar is off-screen cannot be
dragged back.

**R-dock-6. Validate every restored floating window against the *current* screens before showing it.**

The algorithm, in order:

1. **Enumerate screens** and use each screen's **working area**, not its full bounds — the working area
   excludes the taskbar, dock and menu bar, and a window placed under one of those is effectively lost.
2. **Require the title bar to be reachable.** It is not enough for *some* of the window to intersect a screen;
   the draggable strip at the top must be visible and inside the working area. This is the specific failure
   the owner is describing, and an intersects-any-screen test passes it while leaving the window unusable.
3. **If it fails, move it onto the nearest screen** — nearest to its saved position, so a three-monitor layout
   collapsing to one keeps the relative ordering intelligible rather than stacking everything at the origin.
4. **Clamp the size to the target working area** before positioning. A window saved on a large display must
   never be restored larger than the screen it lands on.
5. **Cascade collisions** so several relocated windows do not land exactly on top of each other.

**R-dock-7. Store logical (DPI-independent) coordinates, not raw device pixels.** A window saved on a scaled
4K display and restored on a 1080p one must come back the right *apparent* size. Getting this wrong produces
windows that are subtly wrong on one machine and absurd on another, and it will not show up in testing on a
single monitor.

**R-dock-8. Record the screen configuration alongside the layout** — count and working-area bounds. It is a
few fields, and it lets restore distinguish *"the same setup as last time"* (restore verbatim) from *"a
different setup"* (validate hard). It also makes a bug report diagnosable, which this class of bug otherwise
is not.

## 4A. `View ▸ Hide/Show Dockers` — make it real

The command exists (`WorkspaceWindow.axaml` ~line 366, bound to `HideShowDockersCommand`) but is a
placeholder: it posts *"use Dock title-bar controls to float/minimize regions"* and does nothing. That is
enabled, looks actionable, and does not act — a direct violation of **R13a**, which requires a command either
to do something or to be disabled with a stated reason.

**R-dock-9. Implement it as a full-canvas toggle.** First invocation collapses **every tool dock** so the
document area fills the window; second invocation **restores the previous arrangement exactly**. That is what
the label promises, and it is the one useful action with no other single-gesture route today.

**R-dock-10. The stashed pre-collapse arrangement uses §2's schema — do not invent a second representation.**
This is why the two features belong together: capturing an arrangement, holding it, and reapplying it is
precisely what persistence already does. The toggle then exercises that code on every use, which is worth more
than any test.

**Details that decide whether it feels right:**

- **Tool docks only.** Document tabs, the menu bar and the status bar stay. "Hide the dockers" means the
  panels, not the application.
- **Floating tool windows collapse too**, and reappear at their prior position and size on restore. A toggle
  that leaves a floating Messages panel covering the canvas has not done its job.
- **The stash is session state, not `.cws` state.** A workspace saved while collapsed reopens **expanded**,
  with its real arrangement intact — nobody wants to reopen a project and wonder where their panels went. Save
  the *underlying* layout, never the collapsed one.
- **Toggle state survives a workspace switch within a session**, since it is a view preference rather than a
  property of the design.
- **Reflect the state in the menu** — a checkable item, or a label that changes — so the user can tell which
  way the next press will go.

**R-dock-11. Restore is exact, not approximate.** Sizes along the docking axis, tab selections and floating
geometry all come back as they were. A toggle that loses your panel widths is one people stop using after the
second time.

Give it a keyboard shortcut if one is free — this is a command people press repeatedly — but **audit for
collisions first**, per the standing rule.

## 4B. Two window-behaviour bugs

### 4B.1 macOS: the menu bar empties when a floating tool window has focus

The owner is right that this is macOS-specific, and it is the failure
`brief-file-menu-restructure.md` §4A.3 predicted: **the macOS `NativeMenu` is application-global, and its
contents follow the key window.** A floating tool window is a separate `Window` with no menu of its own, so
when it becomes key the menu bar has nothing to show.

**R-dock-12. Attach the native menu at *application* scope, not only to the workspace window.** Set it on
`Application.Current` so it persists regardless of which window is key; any window without its own menu then
inherits it. Attaching a duplicate menu to every floating window would also work and is the wrong fix — it
multiplies the thing that must stay in sync.

**R-dock-13. Visible is not the same as correctly enabled — a tool window is not a document context.**
`brief-file-menu-restructure.md` **R-menu-4** requires enablement to follow the key window. But a Messages or
Properties panel is not a document, so "the active document" must continue to mean **the last active
*document*** — in the main shell or in a torn-off document window — rather than becoming null the moment the
user clicks into a tool panel.

Otherwise `Save` greys out because someone clicked on the Messages list, which is a worse bug than the one
being fixed. Assert this explicitly; it is the natural failure of a naive key-window implementation.

### 4B.2 Floating tool windows should raise with the workspace, without taking focus

**R-dock-14. Floating *tool* windows raise with the workspace window; torn-off *document* windows do not.**
That distinction is the owner's, and it maps exactly onto the standard utility-window model: tool palettes
belong to their parent, documents are peers.

**Use the owner relationship rather than an activation handler.** Setting a floating tool window's `Owner` to
the workspace window makes it stay above its owner and raise with it on every platform — no `Activated`
hook to maintain, and no risk of the raise stealing focus. `Topmost` is the wrong tool: it would float the
panel above every other application, permanently.

**R-dock-15. Raising must not steal focus — the workspace window stays active.** If any code path calls
`Show()` or `Activate()` on an already-visible tool window, focus will jump and typing will go to the wrong
place. The owner relationship avoids this by construction, which is the main reason to prefer it.

**Note the trade, since it is inherent rather than accidental:** an owned window is always above its owner, so
the workspace window can no longer be placed on top of a floating tool panel. That is standard tool-palette
behaviour and it is what was asked for — but say so in the completion note, because it will be noticed.

**Interaction with §4A:** owned tool windows must still collapse and restore with the full-canvas toggle, and
**with §4's restore path**: an owned window still needs R-dock-6's off-screen validation, since ownership
governs stacking, not position.

## 5. Guardrails

- Do not fail a workspace open because of a layout problem (R-dock-5).
- Do not write the docking library's serialized form into `.cws` (R-dock-3).
- Do not use full screen bounds where working area is meant (R-dock-6).
- Do not make the layout authoritative for which documents are open (R-dock-2).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 6. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Round-trip** — arrange panels on all four sides, close one, tab two together and select the second,
   float a third; save, reopen: every one of those facts is restored, including **which tab was active**.
3. **Absent block (R-dock-4)** — a `.cws` with no layout block opens on the default layout, silently.
4. **Malformed and future-version (R-dock-4/5)** — a corrupted block and a higher-versioned block each open on
   the default layout **and report**; the workspace itself opens normally in both cases.
5. **Membership conflict (R-dock-2)** — a layout naming a document absent from the open list drops that entry;
   a document open but absent from the layout appears in the default document area.
6. **Off-screen, headless (R-dock-6)** — with a synthetic single 1920×1080 screen, a window saved at
   `(3000, 200)` is relocated onto it. **Assert the title bar lies inside the working area**, not merely that
   the window intersects the screen — that is the assertion that catches the real bug.
7. **Negative coordinates** — a window saved at `(−1200, 100)` from a left-hand second monitor is relocated.
8. **Oversized (R-dock-6)** — a 3000×2000 window restored onto a 1920×1080 screen is clamped to the working
   area.
9. **Cascade** — three windows all needing relocation do not land at identical positions.
10. **Scaling (R-dock-7)** — a window saved under 2× scaling restores to the same logical size under 1×.
11. **Same-configuration fast path (R-dock-8)** — with the screen configuration unchanged, positions restore
    **exactly**, with no relocation nudge.
12. **Toggle collapses and restores (R-dock-9/11)** — arrange panels on three sides with distinct sizes, tab
    two together with the second selected, float one; toggle off, toggle on: **every one of those facts is
    restored exactly**, including panel sizes and the floating window's position.
13. **Scope (R-dock-9)** — collapsing hides tool docks and floating tool windows; document tabs, menu bar and
    status bar remain.
14. **Collapsed state is not persisted (R-dock-9)** — save a workspace while collapsed, reopen: it opens
    **expanded** with the real arrangement intact, and the saved `.cws` contains the underlying layout, not
    the collapsed one. Assert the file contents, not just the restored view.
15. **Menu reflects state** — the item shows which way the next invocation will go.
16. **macOS menu persists (R-dock-12)** — with a floating tool window focused, the File/Edit/View/Simulate/Help
    menus remain present and populated. Platform-gated, but assert the menu is attached at application scope
    on every platform so the wiring is not macOS-only code.
17. **Tool focus is not a document context (R-dock-13)** — with a document open and a floating **Messages**
    panel focused, `Save` and the document-scoped `Save … As` items stay **enabled** and act on that document.
18. **Tool windows raise (R-dock-14/15)** — place another application over everything, click the workspace
    window: floating **tool** windows come to the front, torn-off **document** windows do not, and the
    **workspace window keeps focus** (assert focus, not just z-order — that is the half a naive fix breaks).
19. **Owned windows still validate (R-dock-14)** — a floating tool window saved off-screen is still relocated
    by R-dock-6 on restore.

## 7. On completion

Record in `src/Ui/CLAUDE.md`: the layout schema and its version; **that `.cws`'s open-document list stays
authoritative for membership while the layout records only arrangement**; **R-dock-5** — that layout restore
never fails a workspace open; **R-dock-6's title-bar rule**, since "the window intersects a screen" is the
obvious test and it is the wrong one; and that **`Hide/Show Dockers` was a placeholder that violated R13a**,
now a full-canvas toggle sharing §2's schema, whose collapsed state is deliberately **session-only** and never
written to `.cws`.
