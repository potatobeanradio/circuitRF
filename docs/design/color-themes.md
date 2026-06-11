# circuitRF — Color Themes (`.ccolor`)

**Status:** Draft (rev 1) for review · **Date:** 2026-06-08 · **Phase:** 6 (schematic) / cross-cutting

User-customizable color themes for circuitRF rendering. Most visible in the schematic editor now, but
architected to theme **everything** eventually (data display, symbol editor, all canvases). Companions:
`ui-design.md`, `project-file-formats.md` (the `.ccolor` extension joins the format family), `ui-architecture.md`
(the firewall — the theme *model* is framework-free; SkiaSharp/Avalonia consumption stays in `src/Ui`).

## The principle: theme data ≠ renderer tokens ≠ scheme selection

Three layers, each with one job, so the system stays clean as more colors and more canvases adopt it:

1. **Theme data (the `.ccolor` file + its model)** — a named set of color *roles* (semantic keys like
   `Schematic.Wire`, `Schematic.ConnectedPin`) mapped to RGB(A) values, for **light** and **dark** variants.
   This is plain data: framework-free (no SkiaSharp/Avalonia), JSON-serializable, the thing a `.ccolor` file
   holds. The single source of truth for "what color is each role."
2. **Renderer tokens (`SchematicRenderTheme` and future per-canvas theme structs)** — the SkiaSharp `SKColor`
   bundle a renderer actually draws with. These are **built from** the theme data (role → `SKColor`), not
   hardcoded. The existing `SchematicRenderTheme` becomes a *projection* of the active theme's roles into the
   SKColors the schematic renderer needs.
3. **Scheme selection & persistence (the preference + workspace tracking)** — which theme is active, the
   built-in presets, the custom-theme files, and where they're found. A user preference; a workspace records
   its chosen scheme.

This separation is why it scales: adding a new themable color = a new **role** (layer 1) + reading it in the
relevant renderer token struct (layer 2); no change to selection/persistence. Adding a new themable *canvas*
= a new token struct (layer 2) projecting from the same roles; no change to layers 1 or 3.

## Layer 1 — the theme model & `.ccolor` files

### Color roles (semantic, not literal)
Colors are keyed by **role**, not by widget, so a role can be reused (your light set already reuses one
magenta for both wire and node-label). Initial schematic roles (extensible — more may be added later):

| Role | Light default (RGB) | Dark default (RGB) |
|---|---|---|
| `Schematic.Background` | 250 250 250 | 28 28 30 |
| `Schematic.Grid` | 170 170 170 (α70) | 70 70 80 (α70) |
| `Schematic.Wire` | 164 63 129 | 214 122 178 |
| `Schematic.NodeLabelText` | 164 63 129 | 214 122 178 |
| `Schematic.InstanceNameText` (Text1) | 59 28 243 | 138 120 255 |
| `Schematic.ParameterNameText` (Text2) | 24 8 122 | 120 104 230 |
| `Schematic.ComponentNameText` (Text3) | 106 142 246 | 140 174 255 |
| `Schematic.ConnectedPin` | 94 105 216 | 130 145 240 |
| `Schematic.WireJunctionDot` | 59 28 243 | 138 120 255 |
| `Schematic.SymbolLine` | 45 20 195 | 150 132 250 |
| `Schematic.SymbolPlus` | 210 99 40 | 245 140 75 |
| `System.Warning` | 206 74 36 | 240 120 70 |

**`System.Warning` is a role, overridable like any other.** This resolves the "dynamic System.Warning"
question: rather than a special dynamic color, **Warning is just another theme role** with a sensible default.
Unconnected pins and unconnected wire endpoints both reference `System.Warning` (they don't get their own
role) — so they always match, and a user who overrides Warning updates both at once. (If you later want
unconnected-pin to differ from unconnected-endpoint, split them into their own roles then; today they share
`System.Warning`.)

**Pin vs wire-junction color distinction (per owner):** where a wire connects to a **component pin**, use
`Schematic.ConnectedPin`; a **wire-to-wire** junction uses `Schematic.WireJunctionDot`. Two distinct roles,
already separated above.

### The `.ccolor` file
- **Extension `.ccolor`** (circuitRF color), text/JSON (System.Text.Json, the project convention), human-
  diffable. Joins the format family in `project-file-formats.md`.
- Contents: a **theme name**, a `format_version` (alpha reject-on-mismatch policy), and the **role→RGBA map
  for both Light and Dark variants**. A theme is one file carrying both variants (so "MyTheme" themes light
  and dark together); selecting light/dark mode picks the variant within the active theme.
- Roles **absent** from a `.ccolor` fall back to the built-in default for that role (so a partial/old custom
  file still loads, and newly-added roles get a default without breaking existing custom files — the
  nullable-defaulted pattern from `DataDisplayConfig.cs`).
- Model is **framework-free** (RGBA ints, not SKColor/Avalonia Color) — `src/Ui` projects it to SKColor.

### Built-in presets & custom themes
- **Built-in presets** ship as `.ccolor` files in **`/Assets/Color`** (e.g. `Default.ccolor` with the table
  above). Read-only (shipped).
- **Editing a color forks to a custom theme.** When the user changes any color while a preset is active, the
  active scheme becomes a **custom** theme (a copy with the edit applied); it's no longer "Default," it's
  "Custom" (or a user-named theme). The user can save it.
- **Custom themes persist as `.ccolor` files.** Saved to a user themes location (app preferences dir) and/or
  the workspace directory (workspace-local themes — see Layer 3).

## Layer 2 — renderer tokens (projection)

`SchematicRenderTheme` already holds the SkiaSharp `SKColor` tokens the schematic renderer draws with, with
hardcoded `Light`/`Dark` statics. Change it from *source of truth* to *projection*:
- Add a builder: `SchematicRenderTheme.FromTheme(ColorTheme theme, ThemeVariant lightOrDark)` that reads each
  role from the theme data and produces the `SKColor` bundle (role `Schematic.Wire` → `.Wire`, etc.).
- Map the existing tokens to roles: `Wire`→`Schematic.Wire`, `ConnectedPin`→`Schematic.ConnectedPin`,
  `ConnectionDot`→`Schematic.WireJunctionDot`, `UnconnectedPort`→`System.Warning`, `Warning`→`System.Warning`,
  `Label`→ split into the three text roles (instance/parameter/component name) where the renderer draws each,
  `ComponentBody`/`SymbolLine`→`Schematic.SymbolLine`, the `+`→`Schematic.SymbolPlus`,
  `NetLabelText`→`Schematic.NodeLabelText`, `Background`/`Grid` to their roles.
- The renderer keeps consuming `SchematicRenderTheme` exactly as today — only its *construction* changes
  (built from the active theme instead of a static). Overlay/selection/accent colors that aren't user-themed
  yet can keep current defaults or become roles later; `WithAccent` still applies.
- **Future canvases** (data display, symbol editor) get their own token structs projecting from the same
  `ColorTheme` roles — same pattern, shared role vocabulary.

## Layer 3 — selection, preference & workspace tracking

- **Active theme is a user preference** (in the app preferences store — the same one `SettingsView` will read/
  write; a small JSON preferences file in the app config dir if one doesn't exist yet). The preference records
  the **active theme name** (not the colors — the colors live in the `.ccolor`).
- **Light/Dark variant** follows the app theme variant (the existing `ActualThemeVariant` the canvas already
  uses); the active `ColorTheme` supplies whichever variant is current.
- **Theme resolution order (where a named theme is found):**
  1. the **workspace directory** (workspace-local `.ccolor` files — a project can ship its own theme),
  2. the **user themes dir** (custom themes the user saved),
  3. **`/Assets/Color`** (built-in presets).
  First match by name wins; this lets a workspace override a theme name with its own local version.
- **The workspace (`.cws`) records which color scheme it uses** (the theme name). On open, the workspace
  resolves that name via the order above. A workspace with a local `.ccolor` carries its look with it.
- **Settings UI** (`SettingsView`): pick a preset, edit individual role colors (a color picker per role),
  see the edit fork to a custom theme, name/save the custom theme. Live-preview the schematic as colors
  change (re-project `SchematicRenderTheme` and invalidate the canvas).

## Build order (when implemented)
1. Layer 1: `ColorTheme` model (framework-free, roles + light/dark RGBA), `.ccolor` read/write (System.Text.Json),
   `/Assets/Color/Default.ccolor` with the table above. Roles absent → built-in default.
2. Layer 2: `SchematicRenderTheme.FromTheme(...)`; retarget the renderer's construction to the active theme;
   delete the hardcoded `Light`/`Dark` statics (or keep as the Default fallback only).
3. Layer 3: preference store (active theme name), workspace `.cws` records the scheme, resolution order
   (workspace → user → Assets), edit-forks-to-custom, save custom `.ccolor`, `SettingsView` color editor with
   live preview.

## ColorPicker control theme

`ColorPickerDialog` hosts `Avalonia.Controls.ColorPicker 12.0.3`'s `ColorView` control.
The theme include **must** appear in `App.axaml Application.Styles`:

```xml
<StyleInclude Source="avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml"/>
```

Notes:
- Extension is `.xaml` (not `.axaml`) — that is the actual embedded resource name in the 12.0.3 DLL.
- Place after the Dock Fluent theme; order within `Application.Styles` does not otherwise matter.
- Without this include, `ColorView` instantiates with no template and renders blank.

## Hex field behavior

The `HexBox` in `SettingsView` is an **editable input** (standard color-picker UX):
- **Return** — applies the typed hex code to the active role; `e.Handled = true` prevents Return
  from reaching the window's default button (which would otherwise close Settings).
- **Escape** — reverts the hex display to the current working-map color via `RefreshEditor()`;
  `e.Handled = true`.
- **LostFocus** — applies the hex code (same as Return, no `e.Handled` needed on focus loss).
- Format: `RRGGBBAA` (8 hex digits). A 6-digit input is treated as fully opaque (`AA = FF`).

## Open items
- Exact `.ccolor` JSON schema (settle at Layer 1 implementation).
- User themes directory location (app config dir — confirm per-OS path with the preferences store).
- Whether overlay/selection/accent colors become themed roles (later; not in the initial role set).
- Splitting unconnected-pin vs unconnected-endpoint from `System.Warning` into distinct roles (only if a use
  case arises; they share `System.Warning` for now).
