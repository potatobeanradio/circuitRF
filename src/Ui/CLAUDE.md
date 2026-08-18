# `src/Ui` — the Avalonia UI layer

Standing instructions for `src/Ui` (schematic editor, layout editor, Harmonica RF-tuning UI, EM/layout
view, Data Display, dialogs, theming — the whole Avalonia application). Read with the root `CLAUDE.md`.

> ### Where the history went
> This file was an append-only phase log that reached **21,417 lines / 1.8MB** — by far the largest
> `CLAUDE.md` in the repo. The full text is preserved verbatim at **`src/Ui/HISTORY.md`**, unchanged.
>
> **Unlike `src/Core`, `src/Engine`, and `src/Engine/Mom`, this file was archived WITHOUT a hand-curated
> living-reference rewrite** — it has no separable architecture preamble to keep (content starts
> directly with dated `§`-numbered brief write-ups on line 1 and never establishes a standing
> "what lives here" section), and condensing ~400 sections faithfully requires reading the file, which
> was deliberately not done here for cost reasons. **Grep `HISTORY.md`** — `grep -n "^## " src/Ui/HISTORY.md`
> for a table of contents, `grep -n "<topic or R-code>" src/Ui/HISTORY.md` for a specific area,
> `grep -n "2026-0[78]" src/Ui/HISTORY.md` for a date range — rather than reading it whole.
>
> **Anyone doing focused work in one area of `src/Ui`** (schematic, layout, Harmonica, Data Display,
> theming, a specific dialog) is encouraged to grep out that area's sections from `HISTORY.md` into a
> short scoped note here, under a heading, the next time they're already reading that history for a
> task — that is the cheap, incremental way this file should regrow into a real reference over time,
> rather than another single expensive pass.
>
> **Maintenance rule, or this stays permanently thin.** A completed phase's narrative belongs in
> `HISTORY.md`, not here. This file should only ever gain durable, still-true content: an invariant, a
> current default, a refusal, a trap with a name — never a phase-by-phase write-up.

**macOS `NativeMenu` invariant:** a window's `NativeMenu` instance is fixed for its lifetime — change
its `Items`, never the instance (`NativeMenu.SetMenu` a second time on the same window throws, and can
crash on a later dispatcher-queued reset even when the throw itself is swallowed). See
`src/Ui/RESOLVED.md`'s harmonicaRF R3A entry.

**A ComboBox selection pushed from a view-model notification must be assigned AFTER its items, in the
same code path.** A XAML `ItemsSource` binding is attached when the DataContext reaches the ComboBox —
*after* a code-behind handler subscribed in `OnDataContextChanged` — so a handler that sets
`SelectedItem` first resolves it against the OLD item list, and Avalonia silently clears a selection its
items do not contain. The combo then renders blank with no error anywhere. Own both halves in code
(`WBondWirePropertiesView.SyncGroupSelection` is the worked example), and keep the item list a STABLE
reference so re-assigning it does not re-raise the selection.

**Exactly ONE colour theme ships, it is `Default`, and its `wBond.*` roles ARE the palette the owner
chose** (2026-08-17). The six `wBond-…` themes existed to be judged side by side; the winner's six roles
were folded into `Default` and all six files deleted. **`Assets/Color/Default.ccolor` and the in-code
`ColorTheme.BuiltIn` are two copies of one palette and must agree** — the file is what the Settings editor
shows, the in-code one is the per-role fallback for anything a theme leaves unsaid; a test holds them
together. `null` in `preferences.json`/`.cws` means `ThemeResolver.DefaultThemeName`; compare against that
constant rather than a literal.

**A theme change is TWO different events and a view that paints its own colours needs both.**
`ActualThemeVariantChanged` is light-vs-dark; `ThemeService.ThemeChanged` (static, so subscribe on attach
and drop on detach) is a different theme being selected. `LayoutCanvas` handles the second for the layout
itself, but it redraws an `ILayoutCanvasOverlay` from whatever palette object the host handed it — so the
host has to re-resolve and repaint, or the wires stay in the old colours while the layout under them
changes (owner-reported twice now, in different views).

**A dock panel's SIDE is decided by what SEPARATES it from the documents** (`DockLayoutCapture.SideOf`).
Two owner-reported bugs have come out of this one method, both the same shape: `Alignment` records the
edge a dockable was *dropped against*, not the column it landed in; and a container that holds BOTH the
tool and the documents in one branch (which is exactly what Dock's `CreateSplitLayout` builds when a
panel is dropped beside the documents) says nothing about the side at all, so it must be skipped rather
than resolved by an index comparison of `0 < 0`. Anything added here needs a test that the *other*
sides still capture — a naive fix for one trades it for the other.

**Window Layout (Settings ▸ On Launch) is the ONLY place a dock layout is chosen** — View ▸ Reset
Layout deliberately offers no options of its own and resets to whatever that setting names
(`WorkspaceViewModel.PerformLayoutReset`). The shipped default since 2026-08-15 is
`WindowLayout.ProjectTreeAndLibrary` — Project Tree left, Library in its own column RIGHT of the
documents (`DockLayoutDefaults.ProjectTreeAndLibrary`, transcribed from the owner's own `.cws`). The
enum's ordinals are a file format: it is serialized as a number, and the first two members keep the
retired `LaunchPane`'s ordinals so a pre-rename `preferences.json` still means what it said
(`AppPreferencesIo.Migrate`).

**Workspace archives (`src/Ui/Archive/`) are portable because references are REPOINTED, not because
files are copied.** Everything a workspace cannot be read without — every cell, technology and loose
file — is archived unconditionally and never appears in the dialog; only kits, outside-referenced files
and `results/` are asked about — **kits one row per NAMED kit** (`CwsPdkRef.Provider`, which is the
name a placed part resolves through and the name the user sees everywhere else; the folder underneath
is usually a build string), so each is separately taken in or left as a reference. The copy still lands
under the kit's FOLDER name — a kit's own files reference each other against it. When one is included,
the `.cws` and the documents naming it
are rewritten to point at the archived copy: workspace-relative in the `.cws`, **document-relative in a
document**, because the recipient's absolute paths are unknowable at archive time. That is also why
`LayoutPersistence`/`SymbolPersistence` resolve a relative bitmap ref against the document's own folder
at LOAD time — `BitmapCache.Load` hands the string to Skia, which would otherwise resolve it against
the process working directory. A reference the user left out is **reported**, never silently rewritten
to a copy that is not there.

**Permission to run a kit's generator scripts never travels with a workspace** — `PCellTrustStore`
lives in this installation's `preferences.json`, keyed by the kit's ABSOLUTE directory, and is
deliberately absent from `.cws` (an archive arriving with its scripts pre-marked trusted would run them
on open with no prompt). An unarchived kit therefore lands at a new absolute path, reads back
`Unknown`, and `ResetPCellGenerators` — which runs on every workspace open — asks. **The trap:
`PCellWorkerResolver`'s manifest scan is the root plus ONE level, and an archive puts a bundled kit at
`kits/<kit>/`, which is two.** `CandidateDirectories` looks *through* the `kits` folder for exactly that
reason; without it a bundled kit is never found, never asked about, and its cells stay placeholders with
nothing said anywhere.
