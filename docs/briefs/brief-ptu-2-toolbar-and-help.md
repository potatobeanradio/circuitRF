# Brief PTU-2 — Toolbar-style buttons with glyphs, and a Help button

**Series:** `brief-ptu-0-overview.md` · **Tag:** `R-ptu2-<m>` · **Depends on:** PTU-1 (only for the render)
**Area:** `src/Ui/Recognition/ImageSourcePane.axaml` (+ `.axaml.cs`), `src/Ui/Recognition/CreateSchematicFromArtworkDialog.axaml`
(+ `.axaml.cs`), possibly one small glyph control under `src/Ui/Controls/`

---

## 1. Goal

The Create … from Image dialog's top row is a row of word buttons — `Replace…`, `Paste`, the `It is` segment
(`Auto`, `Schematic`, `Layout`, `Plot`), the `Make` segments (`Schematic`/`Layout`, or `Touchstone`/`CSV`) and, in plot
mode, `Add picture…`. The owner wants them to look like the Schematic / Layout / 3D editors' toolbars: flat icon
buttons, grouped, with tooltips. **Every button in the dialog except Cancel and Create** (and Create's split part).
It is ONE window for all modes, so Create Schematic from Image, Create Layout from Image and Create Touchstone from
Image all get the look by construction.

## 2. Requirements

**R-ptu2-1 — The idiom is the editors'.** Copy `src/Ui/Views/Content/SchematicView.axaml`'s toolbar exactly:
a `StackPanel Orientation="Horizontal" Spacing="2" Classes="Toolbar"`, each `Button`/`ToggleButton` with
`Padding="6,3"` and a 16 × 16 `mi:MaterialIcon`, groups separated by the 1-px `Border` separator it uses. Mutually
exclusive choices (`It is`, `Make`) are toggle buttons in a group whose checked state uses the theme's checked fill —
the same treatment `ToggleButton.PanelToggle` in `CircuitRfStyles.axaml` documents (flat while unchecked; do not set a
local `Background`, which would beat `:checked`). Keep the radio semantics (`GroupName`) and every existing binding;
only the presentation changes. The small captions "It is" and "Make" may stay as labels before their groups.

**R-ptu2-2 — Glyphs.** Use a Material icon wherever one says the thing plainly:

| Button | Icon (suggested — pick the closest that exists) |
|---|---|
| Replace… | `FileReplaceOutline` / `ImageEditOutline` |
| Paste | `ContentPaste` |
| Add picture… (plot) | `ImagePlusOutline` |
| Auto | `AutoFix` — keep the confidence dot beside it |
| Schematic (It is / Make) | the icon the Project Tree shows for a schematic view |
| Layout (It is / Make) | the icon the Project Tree shows for a layout view |
| Plot | the rectangular-plot glyph the Data Display's plot-type picker draws (`PlotTypeGlyphControl`) |
| CSV | `FileDelimitedOutline` |
| Touchstone | **custom**: a small `S` over a 2 × 2 matrix grid, drawn in the style of `PlotTypeGlyphControl` |

Reuse rather than invent: grep for the tree's view icons and `PlotTypeGlyphControl` before drawing anything. Only
the Touchstone glyph should need drawing; make it a tiny Skia/`DrawingContext` control beside
`PlotTypeGlyphControl`, theme-aware the same way. Every tooltip keeps its current text and leads with the button's
name ("Replace — read another picture instead of this one.") since the word is gone from the face.

**R-ptu2-3 — Help.** A Help button (`HelpCircleOutline`, the Match Designer's — `MatchDesignerWindow.axaml`'s
`HelpButton`) at the left of the dialog's bottom row, opposite Cancel/Create. It calls
`DocLauncher.Open("reference/create-from-image.html#<anchor>")` with the anchor of what is on screen: `#plots` in
plot mode, `#schematic` for a schematic picture, `#photos` for a board photograph, `#dialog` otherwise. For the
artwork (`.clay`) flavour of the same window, open whatever page that flow's docs live on (grep `docs/user/src` for
"Create Schematic from Artwork"); if none, `#dialog`. A unit test asserts the anchor per mode through a small pure
function (`HelpAnchor(...)`), not through the launcher.

## 3. Gates

- `RecognitionDialogHelpTests` (new): the anchor per mode — four cases.
- One headless render (overview §4) of plot mode, and look at it. Glyphs must read at 16 px in both themes; if one
  does not, say so in the report rather than iterating on artwork.

## 4. Out of scope

Moving controls between rows, changing what any control does, the canvas's own floating toolbar (already
icon-style). Docs: one sentence in `create-from-image.md` `#dialog` only if it names a button by its word.
