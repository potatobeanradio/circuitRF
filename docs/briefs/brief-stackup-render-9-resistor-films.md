# Brief 9 — resistor films on the stackup drawing: see them, select them, drag them

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk9-n`
**Area:** `src/Render/Renderers/StackupScene.cs`, `src/Ui/Controls/StackupCanvas*.cs`,
`src/Ui/Layout/TechEditorViewModel.cs`, `src/Ui/Layout/StackupLayerRowViewModel.cs`, `src/Design/Layout`
**Depends on:** briefs 1–7 (shipped), and [EM-TFR](brief-em-tfr-resistive-film-in-planar-em.md) §1

---

## 0. What this brief delivers

EM-TFR gives a resistor film a meaning in the stackup. A drawing layer carrying
`SheetResistanceOhmPerSq` and bound into a conductor entry's `DrawingLayers` is a film **on** that
level. It also gives the film two optional 3D fields, `FilmThicknessDbu` and `FilmAt`. EM-TFR adds
only the minimum UI needed to author one: a *Sheet resistance* column on the Layers tab, and a chip
on the conductor card.

The drawing shows none of it. A technology whose films are bound looks exactly like one whose films
are not, and the one fact the stackup now carries about a resistor — which level it is on, and which
face — can only be found by reading a check list. This brief puts the film in the picture and makes
it a first-class thing to click, edit and drag, on the series' own rule:

> **The rendering is a second VIEW of the stackup. It is never a second MODEL of it.**

| Gesture | Means | Writes through |
|---|---|---|
| Click a film | select it | `TechEditorViewModel` selection (§2) |
| Double-click its R_s or thickness | edit in place | the Layers-tab row's own commit (§3) |
| Drag it to the other face of its band | set `FilmAt` | one `CommitEdit` (§4) |
| Drag it onto another conductor | rebind it | one `CommitEdit` (§4) |
| Drag it from the tray onto a conductor | bind it | one `CommitEdit` (§4) |
| Drag it off the stack into the tray | unbind it | one `CommitEdit` (§4) |
| Right-click | Face ▸ · Move to ▸ · Unbind · Add Resistor Film ▸ · New Resistor Film… | §5 |

**If this brief is taken before EM-TFR §8**, add `FilmThicknessDbu` and `FilmAt` here as model fields
only (`LayerDef`, `TechPersistence`, `TechPersistence.Clone`; additive, nullable, `WhenWritingNull`,
no `FormatVersion` bump). §8 then reads them. Nothing in this brief needs §8's 3D emission.

---

## 1. The scene

### R-stk9-1 — a film is drawn ON its band, against its face

A film is not a band. It is a patterned feature of one, so it takes **no height of its own in the
sandwich**, and §3a of the overview (proportional within a kind, compressed when the range will not
fit) does not apply to it.

- A **strip** inside its conductor's band rect, flush with the face it sits on:
  `FilmAt ?? the level's SheetAt ?? Bottom`. It is a fixed `StackupScene.FilmStripPx` tall (a theme
  constant, small next to the band), never proportional to 50 nm against 3 µm.
- Drawn in the film layer's own colour and fill pattern, the way a band takes its conductor's.
- **Several films on one face share the strip** in equal lateral segments, in the Layers tab's
  order. There is no persisted lateral position. A via's lane (brief 5) is a cosmetic field the user
  asked for. Nobody asked for one here, and `R-stk5-9` shows what a cosmetic geometry field costs.
- The label column gets one `Spec` piece per film: `Resistor · 50 Ω/sq · 50 nm · top`. Thickness
  and face appear only when stated, so a film with neither reads `Resistor · 50 Ω/sq`.
- **No film, no change.** A technology with no bound film produces a scene bit-identical to
  today's: same rects, same labels, same hit list. Gate it on the starter technologies with their
  films unbound.

### R-stk9-2 — a hit kind of its own

`StackupHitKind.Film`, with the film's `LayerKey` on `StackupHit`. A film strip is tested BEFORE its
band, so a click on the strip is the film's and a click elsewhere on the band is the band's. The
label pieces carry `StackupField` values for R_s and thickness, so brief 4's label hit-testing works
on them unchanged.

### R-stk9-3 — the tray: unbound films, outside the picture

A film layer that is bound to nothing (a resistor mask the circuit model reads, invisible to every EM
solve) has nowhere to be drawn in the sandwich. It is exactly what someone looking at this tab needs
to see. So below the bottom boundary note, **only when such a layer exists**, a single row:
*Resistor films not on a level:* followed by one chip per film layer, each draggable (§4).

**The tray is interactive chrome, not part of the drawing.** `StackupGraphicExport` (brief 7's Copy)
and the documentation figures leave it out. The overview's rule (owner, 2026-09-13) keeps the picture
free of anything that is not the stackup, and an unbound mask is not part of the stackup.

---

## 2. Selection

### R-stk9-4 — a film is selected by key, alongside the band selection

`TechEditorViewModel` gains `SelectedFilmLayerKey`. It is mutually exclusive with
`SelectedStackupLayerName`: setting either clears the other. It is held by key for brief 3's reason,
which is that a reference does not survive a committed edit. Selecting a film:

- outlines its strip (brief 3's highlight, not a new style);
- scrolls the **conductor's** card into view and shades that card's film sub-row (§6);
- clears the card filter if the filter would hide that card (overview §3d);
- `Esc` clears it, after an open inline editor has taken its own `Esc` (overview §3c).

---

## 3. Inline editing

### R-stk9-5 — one write path, and it is the Layers tab's

R_s and thickness are properties of the DRAWING layer, so a double-click on either label opens brief
4's `SchematicInlineEditBox` and commits through **the same `LayerRowViewModel` commit the Layers
tab's *Sheet resistance* column calls** (EM-TFR §1). It does not go through a stackup row, and it
does not write `Working.Layers` directly. Parsing, units (`Ω/sq`, a thickness in the display unit),
refusal of zero or negative values (`TechValidation`'s `SheetResistanceInvalid`) and the undo entry
all come from that commit.

---

## 4. Drag

### R-stk9-6 — the binding rules live in `src/Design`, once

Add `ResistorFilmBinding` in `src/Design/Layout`, with `CanBindTo(Technology, LayerKey film, string
conductorName, out string reason)`, plus the move itself. `TechValidation` (EM-TFR §1's three errors:
bound to two levels, bound to a via, bound to a ground reference) **calls it**. The canvas calls it
to decide whether a drop target is live. A drop the canvas would allow and validation would then
flag cannot happen, because the two ask the same function.

### R-stk9-7 — four drags, one undo entry each

All four use brief 5's machinery: the movement threshold that separates a click from a drag, the
ghost, pointer capture on press, and commit once on release.

- **Face:** dragging a film vertically within its own band snaps it to the nearer face and sets
  `FilmAt`. Dropping it back on the face it came from writes nothing.
- **Rebind:** dragging a film onto another conductor band removes its key from the old entry's
  `DrawingLayers` and adds it to the new one's in ONE `CommitEdit` (*"Move Resistor to Metal2"*).
  Two edits would be two undo entries, and the one in between is a film bound to nothing or to two
  levels at once, which is an error state validation would flash.
- **Bind:** dragging a tray chip onto a conductor band adds the key in one `CommitEdit`.
- **Unbind:** dragging a film off the stack onto the tray removes the key in one `CommitEdit`.

**Refused targets** (a via, a dielectric, a ground-reference conductor) show brief 5's no-drop ghost.
A tooltip carries `CanBindTo`'s own reason, never a second wording. Releasing there writes nothing.

---

## 5. Context menu

### R-stk9-8 — built on brief 6's one `Opening` handler

| Under the pointer | Adds |
|---|---|
| A film | **Face ▸** Top / Bottom (✓ on the current one) · **Move to ▸** each conductor `CanBindTo` accepts · **Unbind** · ─ · Copy |
| A conductor | **Add Resistor Film ▸** each unbound film layer (absent when there are none) · **New Resistor Film…** |
| A tray chip | **Add to ▸** each conductor `CanBindTo` accepts |

**New Resistor Film…** asks for a name and an R_s. It then creates the drawing layer AND binds it in
one `CommitEdit`, through a single `TechEditorViewModel.AddResistorFilm(conductorName, name, rs)`.
That method takes the new layer's key, colour and z-order from the Layers tab's own add-layer
function rather than repeating its rules. This is the one place this series creates a Layers-tab
row, and it is allowed because it goes through that tab's own function. Every item writes its own
field and only that field.

`R-stk6-7`'s comment-stripped source scan covers every file this brief adds to the canvas.

---

## 6. The card

### R-stk9-9 — the conductor card's film sub-row

EM-TFR §1's chip becomes a sub-row under the conductor card's drawing layers: one line per bound film
with its name, R_s, thickness and face. Each is editable and commits through the same functions as
§3 and §4, so the card and the canvas are two views of one write path. The existing *Drawing
layers* check list marks a film layer as one (`Resistor · 50 Ω/sq`). Checking it there is still a
legitimate way to bind, and it runs `CanBindTo` like every other route.

---

## 7. Gates

| Requirement | Test |
|---|---|
| `R-stk9-1` | Strip inside its band, flush with the right face for each of `FilmAt` Top/Bottom/unset; equal segments for two films on one face; label pieces; **no film → scene bit-identical**. `StackupSceneTests` |
| `R-stk9-2` | Strip hit before band; label hits carry the R_s / thickness fields. `StackupSceneTests` |
| `R-stk9-3` | Tray present only with an unbound film; absent from `StackupGraphicExport`. `StackupCopyTests` |
| `R-stk9-4` | Film/band selection mutually exclusive; card scrolls and shades; filter cleared when hiding; `Esc` order. `StackupSelectionTests` |
| `R-stk9-5` | An inline R_s edit leaves the same `Technology` and the same undo stack as the same edit typed in the Layers tab. `StackupInlineEditTests` |
| `R-stk9-6` | `TechValidation` and the canvas agree on every target in the starter MMIC stack (a table, not a sample). |
| `R-stk9-7` | Each drag is exactly one undo entry, and one `Ctrl-Z` restores the technology bit for bit; refused drops write nothing; same-face drop writes nothing. `StackupDragTests` |
| `R-stk9-8` | Each menu item writes its own field only; *New Resistor Film…* is one undo entry; the source scan. `StackupContextMenuTests` |
| `R-stk9-9` | Card sub-row edits and canvas edits produce identical technologies. |

**Visual verification is required** (root `CLAUDE.md`). Render with `tools/UiSnapshot`, adding a
Technology-editor Stackup-tab target if it has none, in light and dark:

- the starter MMIC stack with `Resistor` on Metal1's top face;
- a stack with two films on one face and one on the other;
- the tray with an unbound film;
- a selected film;
- a drag ghost over a refused target.

Look at each one, and list them in the completion note.

```
dotnet test tests/Ui.Tests      --no-build --filter "FullyQualifiedName~Stackup"
dotnet test tests/Firewall.Tests --no-build
```

Findings go in `src/Render/RESOLVED.md` (scene) and `src/Ui/RESOLVED.md` (canvas, view model), and
never in a CLAUDE.md.

---

## 8. Scope

- **Docs:** `docs/user/src/reference/stackup.md` gains the gestures, beside EM-TFR §7's section on
  what a film is. The `stackup-mmic` figure changes, because the starter technology's `Resistor` is
  bound once EM-TFR §7 lands. **Do not run DocGen**; the owner regenerates at the end of the series.
- **Not here:** a film's lateral position (§1), zoom (overview §4), drawing films in the layout
  editor differently from today (they are ordinary drawing layers there), and anything EM-TFR
  computes.
