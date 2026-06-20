# Phase 6 — Color Themes (`.ccolor`) (Claude Code / Sonnet)

Implement user-customizable color themes. Most visible in the schematic editor now, but architected to theme
**everything** later. The design is fully specified in `docs/design/color-themes.md` — **read it first and
implement to it**; this brief is the build plan, the doc is the authority. Three layers, sub-gated in order;
report and stop between layers. Firewall green throughout.

> Read first: `docs/design/color-themes.md` (the architecture + the role table + both light/dark default
> palettes — authoritative), `docs/design/project-file-formats.md` (the `.ccolor` entry + serialization
> conventions: System.Text.Json, enum-as-string, nullable-defaulted, `format_version` reject-on-mismatch,
> Id-not-persisted), `docs/design/ui-architecture.md` (the firewall). Context code:
> `src/Ui/Renderers/SchematicRenderTheme.cs` (the existing SKColor token bundle — becomes a projection),
> `src/Ui/Renderers/SchematicRenderer.cs` (consumes the theme), `src/Ui/Controls/SchematicCanvas.cs` (selects
> Light/Dark via `ActualThemeVariant`, applies `WithAccent`), `src/Ui/Views/Dialogs/SettingsView.axaml(.cs)`
> (the settings UI to extend).

## The three-layer separation (the whole point — keep them distinct)
1. **Theme data** — a `ColorTheme` model: semantic **roles** → RGBA, for Light + Dark variants. Framework-free
   (no SkiaSharp/Avalonia — RGBA ints), System.Text.Json-serializable. The `.ccolor` file holds this. Single
   source of truth for "what color is each role."
2. **Renderer tokens** — `SchematicRenderTheme` (SKColor bundle) becomes a **projection** built FROM a
   `ColorTheme` (role → SKColor), not a hardcoded static. The renderer keeps consuming it unchanged; only its
   *construction* changes.
3. **Selection & persistence** — active theme is a user preference (records the theme *name*); the workspace
   `.cws` records its chosen scheme; resolution order workspace dir → user themes → `/Assets/Color`; editing a
   color forks to a custom theme; custom themes save as `.ccolor`.

Adding a future color = a new role (L1) + reading it in a token struct (L2); adding a future canvas = a new
token struct projecting the same roles (L2). L3 never changes. Honor that separation — don't let SKColor or
Avalonia types leak into L1, and don't hardcode colors in the renderer (L2 reads roles).

---

## LAYER 1 — `ColorTheme` model + `.ccolor` read/write + the Default preset

**Where:** the `ColorTheme` model should be **framework-free**. Per the firewall it could live in core, but
since it's presentation data consumed only by `src/Ui` today, placing it in `src/Ui` (e.g.
`src/Ui/Theming/`) is acceptable **provided it carries no Avalonia/SkiaSharp types** (RGBA as ints/a small
plain `record struct`, not `SKColor`/`Avalonia.Media.Color`). Note in `src/Ui/CLAUDE.md` that it's
framework-free and could migrate to core if another assembly ever needs it.

1. **Color roles.** Define the role keys as a stable enum or string-keyed set, exactly the roles in
   `color-themes.md`'s table: `Schematic.Background`, `Schematic.Grid`, `Schematic.Wire`,
   `Schematic.NodeLabelText`, `Schematic.InstanceNameText`, `Schematic.ParameterNameText`,
   `Schematic.ComponentNameText`, `Schematic.ConnectedPin`, `Schematic.WireJunctionDot`,
   `Schematic.SymbolLine`, `Schematic.SymbolPlus`, `System.Warning`. (Use a string-keyed map internally so
   adding roles later doesn't churn an enum — but a typed accessor per role is fine for the renderer.)

2. **`ColorTheme` model.** A theme has a **Name**, a **`format_version`**, and a **role → RGBA map for each
   of Light and Dark**. RGBA as a plain `record struct Rgba(byte R, byte G, byte B, byte A=255)`. Provide
   `Resolve(role, variant)` returning the role's color, **falling back to the built-in default** for any role
   absent from the file (so partial/old custom files load, and newly-added roles get a default without
   breaking existing `.ccolor` files — the nullable-defaulted pattern).

3. **`.ccolor` read/write.** System.Text.Json, human-diffable, stable key ordering, `format_version` written
   and **rejected-on-mismatch** with a clear error (no migration — alpha policy). One `.ccolor` carries BOTH
   Light and Dark variants (selecting light/dark mode picks the variant within the active theme). **Never
   persist any `Id`** (Id-not-persisted rule applies to all formats).

4. **Built-in defaults + the Default preset file.** Encode the `color-themes.md` default table as the
   **built-in fallback** (in code, so the app always has valid colors even with no files), AND ship it as
   **`/Assets/Color/Default.ccolor`** (the user-visible preset). The numbers come straight from the doc's
   table (light + dark). Make sure `Default.ccolor` is included as content in the build/packaging (the
   `.csproj` content glob + the macOS/linux/wix bundling — mirror how `Assets/Fonts` is bundled).

5. **Tests:** round-trip a `.ccolor` (write → read → equal, modulo no Ids); a partial `.ccolor` (missing
   roles) resolves missing roles to defaults; a `format_version` mismatch is rejected with a clear error;
   the Default preset loads and matches the built-in table.

**Layer 1 gate:** `ColorTheme` + `.ccolor` I/O + `/Assets/Color/Default.ccolor` + tests; framework-free
(firewall green); report.

---

## LAYER 2 — `SchematicRenderTheme` as a projection of the active theme

1. **Add `SchematicRenderTheme.FromTheme(ColorTheme theme, variant)`** that reads each role and produces the
   SKColor bundle. Mapping (per `color-themes.md` §"Layer 2"):
   - `Wire` ← `Schematic.Wire`; `ConnectedPin` ← `Schematic.ConnectedPin`;
     `ConnectionDot` ← `Schematic.WireJunctionDot`; `NetLabelText` ← `Schematic.NodeLabelText`;
   - `UnconnectedPort` ← `System.Warning`; `Warning` ← `System.Warning` (both reference the one Warning role);
   - `SymbolLine`/`ComponentBody` ← `Schematic.SymbolLine`; the symbol "+" color ← `Schematic.SymbolPlus`;
   - `Background` ← `Schematic.Background`; `Grid` ← `Schematic.Grid`.
   - **Three text roles:** the renderer currently uses one `Label` token. Split label rendering so
     **instance names use `Schematic.InstanceNameText`, parameter names use `Schematic.ParameterNameText`,
     component/type names use `Schematic.ComponentNameText`**. The label list is already ordered
     (0 = type/component name, 1 = instance name, 2+ = parameters), so the renderer can pick the role by
     label kind. (This is the one place Layer 2 needs a renderer change beyond construction — verify each
     label kind picks up its distinct color.)
   - Overlay/selection/ghost/rubber-band/LOD colors are **not user-themed yet** — keep their current
     defaults (they can become roles later). `WithAccent` still applies for rubber-band.

2. **Retarget construction.** Where `SchematicCanvas`/the renderer currently picks the hardcoded
   `SchematicRenderTheme.Light`/`.Dark` static by `ActualThemeVariant`, instead build it via
   `FromTheme(activeTheme, variant)`. Keep the hardcoded statics ONLY as the built-in-default fallback (or
   derive them from the built-in default `ColorTheme` so there's one source) — don't leave two independent
   color definitions.

3. **The Warning question is resolved by the role model** (no dynamic color): unconnected pins and
   unconnected wire endpoints both draw with `Warning` (= `System.Warning` role). Confirm both reference the
   single role so a Warning override updates both.

4. **Test/verify:** rendering with the Default theme looks identical to today's Light/Dark (the defaults are
   the same palette family — confirm no visual regression); changing a role in the active `ColorTheme` and
   re-projecting changes the corresponding rendered element.

**Layer 2 gate:** renderer draws from the projected theme; three text roles distinct; Warning shared by both
unconnected cases; no hardcoded color left in the renderer; firewall green; report.

---

## LAYER 3 — selection, preference, workspace tracking, Settings UI

1. **Preference store.** Record the **active theme name** (not the colors) in the app preferences. If no
   preferences store exists yet, add a small JSON preferences file in the app config dir (per-OS path);
   `SettingsView` reads/writes it. (Confirm the per-OS config path; mirror any existing app-config usage.)

2. **Light/Dark variant** follows the existing `ActualThemeVariant` the canvas already uses; the active
   `ColorTheme` supplies the current variant. (App light/dark toggle is unchanged; it now selects the variant
   *within* the active theme.)

3. **Theme resolution order** (resolve a theme *name* to a `.ccolor`): **(1) workspace directory**, then
   **(2) user themes dir**, then **(3) `/Assets/Color`**. First match by name wins (a workspace can ship a
   local `.ccolor` that overrides a preset name).

4. **Workspace `.cws` records the active scheme** (the theme name) — add it to `WorkspacePersistence`/the
   `.cws` manifest. On workspace open, resolve that name via the order above and apply it. (A workspace with a
   local `.ccolor` carries its look.)

5. **Edit forks to a custom theme.** When the user edits any role color while a preset (e.g. Default) is
   active, the active scheme becomes a **custom** theme: a copy of the active theme with the edit applied,
   renamed (e.g. "Custom" or a user-supplied name). It's no longer the preset. The user can **save** it — to
   the user themes dir (and/or, by choice, the workspace dir for a workspace-local theme).

6. **Settings UI** (`SettingsView`): choose a theme (presets + custom); a **per-role color picker** to edit
   colors; editing forks to custom (per #5); name + save the custom `.ccolor`. **Live preview**: as a color
   changes, re-project `SchematicRenderTheme` and invalidate the schematic canvas so the user sees the change
   immediately. Editing colors for Light and Dark variants (at least: edit the current variant; both-variants
   editing is nice-to-have).

7. **Tests/verify:** active-theme preference persists across restart; a workspace records + restores its
   scheme; resolution order picks workspace-local over user over Assets; editing a Default color forks to a
   named custom theme that saves and reloads as `.ccolor`; live preview updates the canvas.

**Layer 3 gate:** preference + workspace tracking + resolution order + edit-forks-to-custom + Settings color
editor with live preview; firewall green; report.

---

## Acceptance (whole feature)
1. `ColorTheme` (framework-free, roles, light+dark, default fallback) + `.ccolor` I/O + `/Assets/Color/Default.ccolor`, bundled.
2. `SchematicRenderTheme.FromTheme(...)`; renderer draws from the active theme; three text roles distinct;
   unconnected pin + endpoint share `System.Warning`; no hardcoded renderer colors; no visual regression vs today.
3. Active-theme user preference; `.cws` records the scheme; resolution order workspace → user → Assets;
   edit-forks-to-custom; custom `.ccolor` save/load; `SettingsView` per-role color editor with live preview.
4. Firewall green (L1 model framework-free); `dotnet build`/`dotnet test` green; nothing in prior phases regresses.

## Guardrails
- **Keep the three layers separate** — L1 is framework-free data (no SKColor/Avalonia), L2 projects to SKColor,
  L3 is selection/persistence. The renderer reads roles via the projection; **never hardcode a color in the renderer**.
- **Roles, not widgets** — colors keyed by semantic role; reuse a role across elements (Warning is shared by
  unconnected pin + endpoint; node-label may match wire per the defaults). Adding a color = a new role.
- **One source of color truth** — the built-in default `ColorTheme` is the single fallback; don't leave the
  old hardcoded `Light`/`Dark` statics as an independent second definition (derive them from it or remove them).
- **Serialization conventions** (project-file-formats.md): System.Text.Json, enum-as-string, nullable-defaulted
  (absent role → default), `format_version` reject-on-mismatch, **no Id persisted**, human-diffable.
- **Missing/old `.ccolor` must still load** (resolve absent roles to defaults) — don't hard-fail on a partial file.
- Sub-gate the three layers; report and stop between them; don't run the full suite into the output limit.
- Update `src/Ui/CLAUDE.md` (the theme model is framework-free; the role→projection pattern; edit-forks-to-custom)
  and confirm `color-themes.md` matches what was built (note any deviation).

*Exit: schematic colors are user-themable via `.ccolor` (built-in presets + custom, workspace-tracked), built
on a role-based three-layer system ready to theme the data display and symbol editor later by adding token
projections — no change to the theme data or selection layers.*
