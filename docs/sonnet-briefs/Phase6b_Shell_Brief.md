# Phase 6b — Application Shell (Claude Code / Sonnet)

**Goal:** stand up the circuitRF **application shell** — the Workspace window, the Dock-based regions
(Project Tree / Properties / Content / Messages), tabbed Content with tear-out + re-dock, the menu bar, the
Workspace toolbar, and the Messages region (color+icon coded, clickable file links) — driven by a **stub**
project tree and stub content. **No schematic canvas, no data display, no net extraction yet** (those are
6c–6e). 6b proves the frame works end to end and is cross-platform.

> Read first: `docs/design/ui-design.md` (§1 workspace, §2 regions + the §2.0 Dock mapping, §2.3 Content
> tabs, §7.1 Workspace toolbar, §8 Messages, §9 cross-cutting), `docs/design/ui-architecture.md` (the
> firewall — §3), `src/Ui/CLAUDE.md` (standing UI rules), root `CLAUDE.md`. **Study the splotRF app shell as
> the template** (see "Mirror splotRF" below). Design notes win.

## The firewall still holds (it's enforced)
All 6b code lives in **`src/Ui`**. The firewall test from 6a will fail the build if anything in
`Core`/`Engine`/`RfCore`/`Cli` gains an Avalonia reference — so 6b must not push UI types downward. The shell
talks to the engine only via the design-model-in / `DataSet`-out contract (none of which is exercised yet in
6b — the tree and content are stubs).

## Mirror splotRF (the proven template) — cite these files
splotRF (`.././splotRF/src`) is a complete Avalonia 12 app with the same stack;
**mirror its scaffolding** rather than invent. Specific references:
- **App bootstrap:** `Program.cs`, `App.axaml` + `App.axaml.cs`, `ViewLocator.cs`, `app.manifest` — the
  Avalonia startup, the view-locator (view↔viewmodel resolution), the application lifetime.
- **MVVM base & structure:** `ViewModelBase.cs`, the `Views/` ↔ `ViewModels/` split (CommunityToolkit.MVVM),
  the `Converters/` pattern.
- **Tabs:** `TabViewModel.cs` + `TabHeaderView.axaml` — the Content tab model + header (circuitRF's Content
  tabs follow this, but hosted in Dock's DocumentDock — see below).
- **Tear-out window:** `DisplayWindow.axaml(.cs)` + `DisplayWindowViewModel.cs` — the pattern for a torn-out
  view in its own window (circuitRF tears out Content tabs the same way; Dock provides the mechanic).
- **Dialogs / chrome:** `AboutWindow`, `AcknowledgmentsWindow`, `SettingsView`, `SaveChangesDialog` — reuse
  the patterns (and the About/Acknowledgments content structure) for circuitRF's equivalents.
- **Assets & theming:** `Assets/Fonts`, `Assets/Licenses`, the icon set, semantic styling — reuse the font
  loading and asset structure; circuitRF gets its own icons.
- **Cross-platform packaging:** `bundleForMacOS.sh`, `linux/` (`.desktop`, mime, postinst/postrm, png),
  `package.wx` (Windows/WiX), `Assets/macOS/*.plist`, `app.manifest` — **adapt these for circuitRF**; this is
  exactly the packaging circuitRF needs and re-deriving it would waste days.
- **Clipboard:** `WindowsClipboard.cs` — the platform clipboard abstraction pattern (6b needs only the
  scaffold; real copy/paste is 6d).

**The one thing splotRF does NOT have: Dock.** splotRF uses a custom tab/window arrangement, not the
AvaloniaUI `Dock` library. circuitRF's regions use **Dock** (decided, `ui-design.md` §2.0) — so mirror
splotRF for the *app skeleton and packaging*, but build the *region layout* with Dock (new). Add the `Dock`
NuGet package to `src/Ui` only (never the core — the firewall).

## Scope

### STEP 1 — project + bootstrap (mirror splotRF)
- Create/confirm the `src/Ui` Avalonia app project (`CircuitRF.Ui`), referencing `Core`, `Engine`, `RfCore`
  (up-the-stack only). Add Avalonia 12, CommunityToolkit.MVVM, SkiaSharp, and **Dock** as `src/Ui`-only deps.
- Port the bootstrap: `Program.cs`, `App.axaml(.cs)`, `ViewLocator.cs`, `ViewModelBase`, `app.manifest`,
  fonts/assets/licenses, theming — adapted to circuitRF (name, icon, About content). App launches to an empty
  Workspace window.

### STEP 2 — the Workspace window + Dock regions (the §2.0 mapping)
- The Workspace window hosts a Dock **RootDock → ProportionalDock**:
  - **Project Tree** (ToolDock, left-top, ~10% width) — a `TreeView` bound to a **stub** model (a few fake
    libraries/cells/data-display-config nodes) with the right-click menu scaffold (Open / Open in New Window
    / Delete — wired to no-op/placeholder handlers for now). Double-click → opens a stub Content tab.
  - **Properties** (ToolDock, left, below the tree) — empty placeholder panel for now (the Component Palette
    that fills it is 6c/6d; 6b just creates the region).
  - **Content** (DocumentDock, center) — Dock document area; tabs are **stub** views (a labeled placeholder
    panel per tab). Tear-out to a window and re-dock must work (Dock provides it — verify it actually does).
  - **Messages** (ToolDock, bottom) — the real Messages region (STEP 4).
- **Layout persistence:** serialize the Dock layout into a workspace file (or app settings for 6b) and
  restore on relaunch; a **View → Reset Layout** command restores the default arrangement (the §2.0 table).
- Regions resize/rearrange/tear-out/minimize via Dock — **do not hand-roll any of it**; confirm Dock's
  behaviors work and wire circuitRF's contents into them.

### STEP 3 — menu bar + Workspace toolbar
- **Menu bar** (`ui-design.md` §8): File (New Workspace, Open, Add Library, Save, Save As — Open/Save wired
  to stub workspace load/save; Add Library a placeholder), Edit (Undo/Redo, Cut/Copy/Paste, Select All —
  bound to the command infrastructure, no-op targets for now), View (region show/hide, Reset Layout,
  zoom-to-fit placeholder, theme), Simulate (Run/Stop — placeholder, no engine call yet in 6b).
- **Workspace toolbar** (`ui-design.md` §7.1): Start Page/New, Open/Save, Cut/Copy/Paste, Undo/Redo,
  Print/Help, Hide/Show Dockers, Fit Windows to Frame, Run/Stop Analysis (placeholder), Status Messages
  (expand/pop the Messages region). Buttons invoke the **same commands** as the menu (one command per action;
  toolbar and menu are two faces of it) — establish the command infrastructure here so 6d's editor commands
  and undo/redo slot into it.
- The **command pattern** scaffolding (`ui-design.md` §10): a command/undo-redo stack the menu, toolbar, and
  later editors all route through. 6b wires it with the global commands (New/Open/Save/Undo/Redo); editor
  mutation commands come in 6d.

### STEP 4 — the Messages region (real, not stub)
Per `ui-design.md` §8 — this one is fully built in 6b because it's simple and useful immediately:
- A scrollable, selectable text/list area showing messages.
- **Color + icon coded** (never color alone — `src/Ui/CLAUDE.md` accessibility): error (red + error icon),
  warning (yellow + warning icon), success (green + check), info (neutral). The icon carries meaning
  regardless of locale/color-vision.
- **Clickable file links:** a file path in a message is underlined; clicking it **reveals the file in the OS
  file manager** (Finder/Explorer/xdg-open) — implement the platform reveal-in-file-manager call (this is
  real OS integration, useful now and used by engine errors later).
- A simple `IMessageSink` (or similar) the rest of the app posts messages to — so when 6e wires the engine,
  its messages land here. For 6b, a Help/test command can post sample messages to exercise the coding/links.
- **Status Messages** toolbar button (STEP 3) expands the region if minimized or pops it to its own window if
  not (Dock float).

### STEP 5 — cross-platform packaging (adapt splotRF's)
- Adapt `bundleForMacOS.sh`, the `linux/` recipe, `package.wx`, the macOS plists, `app.manifest` for
  circuitRF (names, icons, mime/identifiers). Confirm the app builds and launches on the dev platform;
  the full tri-OS packaging is validated in CI/Phase 8, but the recipes should be in place and adapted now
  (mirroring splotRF means this is adaptation, not authoring).

## Acceptance
1. circuitRF Avalonia app launches to a Workspace window with the four Dock regions in the §2.0 default
   arrangement; regions resize/rearrange/tear-out/re-dock/minimize via Dock; layout persists + Reset Layout.
2. Project Tree shows a stub model with right-click scaffold; double-click opens a stub Content tab; Content
   tabs tear out to a window and re-dock.
3. Menu bar + Workspace toolbar present; global commands (New/Open/Save/Undo/Redo) routed through the command
   infrastructure; Run/Stop and editor-specific actions are placeholders.
4. Messages region: color+icon coded, scrollable/selectable, clickable file links reveal in the OS file
   manager, an `IMessageSink` other code can post to.
5. Packaging recipes adapted from splotRF; app builds and runs on the dev OS.
6. **The 6a firewall test still passes** — no Avalonia reference leaked into Core/Engine/RfCore/Cli.
   `dotnet build`/`dotnet test` green; Phases 1–5 untouched.

## Guardrails
- **Shell only — stubs for Tree content, Content tabs, Palette, Run.** No schematic canvas, no data display,
  no net extraction, no engine calls (6c–6e). Resist building features; 6b is the frame.
- **Mirror splotRF** for the app skeleton + packaging (cite the files above); **use Dock** for regions (new).
  Don't hand-roll docking/tear-out — Dock provides it.
- **Firewall:** all Avalonia/Dock/UI code in `src/Ui` only; the 6a test must stay green. If a task seems to
  need a UI type in the core, stop — the boundary is being crossed.
- **Command pattern from the start** (§10): menu + toolbar route through one command/undo stack so 6d's
  editor commands and undo/redo slot in cleanly. Don't wire actions directly to handlers that bypass it.
- Honor `src/Ui/CLAUDE.md`: MVVM (thin views, logic in view-models), accessibility (color+icon), no
  simulation logic in the UI.
- Diagnostics over grinding; flag Dock or cross-platform issues rather than hacking around them.
- Update `src/Ui/CLAUDE.md` with any shell conventions established (the command infra, the message sink, the
  Dock layout approach).

*6b exits with a working, cross-platform, empty-but-navigable circuitRF shell: regions, tabs, menus,
toolbar, messages — ready for 6c to drop the schematic canvas into a Content tab.*
