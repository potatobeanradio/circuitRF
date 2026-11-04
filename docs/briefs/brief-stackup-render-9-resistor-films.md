# Brief 9 — resistor films in the stackup: define them, see them, select them, drag them

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk9-n`
**Area:** `src/Design/Layout` (the model), `src/Render/Renderers/StackupScene.cs`, `src/Ui/Controls/StackupCanvas*.cs`,
`src/Ui/Layout/TechEditorViewModel.cs`, `src/Ui/Layout/StackupLayerRowViewModel.cs`
**Depends on:** briefs 1–7 (shipped), and [EM-TFR](brief-em-tfr-resistive-film-in-planar-em.md) built

---

## 0. What this brief delivers

EM-TFR gives a resistor film a meaning in the planar and 3D solves, and shipped no field to edit one: a `.ctech` states it by hand, and
the conductor's drawing-layer check list shows a chip (`Resistor · 50 Ω/sq`).

**A sheet resistance is a material fact about a level, and it is stored on the wrong object.** The Layers tab is for drawing, and
`LayerDef.SheetResistanceOhmPerSq` makes a drawing layer carry a material definition. So does EM-TFR's `FilmThicknessDbu` and `FilmAt`.
This brief moves all three to the stackup (§1), and then gives the Stackup tab the whole of the film: the card that defines it, and the
drawing that shows it, on the series' own rule:

> **The rendering is a second VIEW of the stackup. It is never a second MODEL of it.**

| Gesture | Means | Writes through |
|---|---|---|
| Click a film | select it | `TechEditorViewModel` selection (§3) |
| Double-click its R_s or thickness | edit in place | the one film commit (§4) |
| Drag it to the other face of its band | set its face | one `CommitEdit` (§5) |
| Drag it onto another conductor | move it | one `CommitEdit` (§5) |
| Right-click | Face ▸ · Move to ▸ · Remove Film · Add Resistor Film ▸ · New Resistor Film… | §6 |

**The Layers tab shows nothing about a film and gains no column.** A film's drawing layer is an ordinary row there: name, colour,
pattern, order.

---

## 1. The model: a film belongs to its conductor

### R-stk9-0 — `StackupLayer.Films`

```csharp
public sealed class StackupFilm
{
    public LayerKey Layer { get; set; }                  // the drawing layer patterning it
    public double   SheetResistanceOhmPerSq { get; set; }  // positive
    public long?    ThicknessDbu { get; set; }           // 3D only; null lets the generator pick
    public ConductorSheetSurface? At { get; set; }       // 3D only; null is the level's own SheetAt
}
```

`StackupLayer.Films` is a nullable list on a Conductor entry, omitted on write when empty, with no `FormatVersion` bump (the additive
convention `FillPatterns` and `LvsTolerances` already follow). `TechPersistence.Clone` carries it.

**`DrawingLayers` becomes metal only.** That is the point of the move and it removes a trap: EM-TFR had to teach every reader of
`DrawingLayers` not to read a film as copper (`ResistiveFilms.MetalLayers`, in `Conductors.Of`, `LayerRegions`, `Regions`, the DC rail
extractor, the cross-section readers and the copper-layer lists), and two MMIC LVS tests caught the one it missed. With the film out of
that list no reader can make that mistake. **Retire `MetalLayers` and put those readers back as they were.**

`LayerDef.SheetResistanceOhmPerSq`, `FilmThicknessDbu` and `FilmAt` go.

### R-stk9-0a — one place asks

`ResistiveFilms` (`src/Design/Layout`) becomes: the film of a layer key, the conductor a film is on, and every film of a technology in
stackup order. Everything below reads it:

| Reader | Today | Then |
|---|---|---|
| `PlanarExtractor`, `Em3dLayoutSolids` | a shape on a bound layer with a sheet resistance | a shape on a layer with a film entry, carrying that entry's values |
| `MmicStack.ResistorLayer`, `MmicPCells`, `MmicPassiveInjection` | first layer stating R_s | first film in stackup order, with its layer and its R_s |
| `DeviceRecognition` (`TfrSheetResistance`) | the resistor layer's R_s | that film's |
| `TechValidation` | three binding errors, and `SheetResistanceInvalid` on a layer | the rules of R-stk9-6, on the entry |
| `CrossSectionExtractor` | skips a film with a note | unchanged: the shape binds to no metal; keep the note |
| `PcbImport`, `TechnologyMerge` | copy the layer's field | the entry's list comes with the entry |
| `ComponentEmExtraction` | lifted when the film layer is bound | a layer with a film entry is bound |
| `DocumentSchema` prose, `StarterTechnologies` | name the layer's field | name the entry's |

### R-stk9-0b — files already written

A `.ctech` stating `SheetResistanceOhmPerSq` on a layer has existed since the TFR model did, so the reader still takes it, and
`TechPersistence` moves it on load. It is never written back.

- A layer bound into a conductor's `DrawingLayers`: its entry moves to that conductor's `Films` and the key leaves `DrawingLayers`.
- A layer bound to nothing: it goes to the level the `TFR` part draws its contacts on, which is `MmicStackResolver`'s base metal. This
  changes what a run does with shapes on it, from an open to a film, which is EM-TFR's own stated behaviour change.
- No conductor to put it on: the value cannot be kept, and `TechValidation` reports `tech.film.no-level` naming the layer. `MmicStack`'s
  existing "has no resistor film" sentence then answers for the part.

### R-stk9-0c — a layer with no film entry is not a film

There is no longer such a thing as a resistor mask with a value but no level. A resistor layer with no film entry is a drawing layer
bound to nothing, so EM-TFR's "this layer is a resistor film but is not bound" notes have nothing to detect. Their job passes to
two things that already exist: the `TFR` part refuses through `MmicStack` when the technology has no film, and the extractors'
`ignoredOther` note **names the layers it ignored**, as a warning when they carry area, so a hand-drawn film on an undefined layer is
still not silent.

### R-stk9-0d — the layer key and the row's own operations

A film entry follows its layer: changing a layer's number or datatype on the Layers tab rewrites `StackupFilm.Layer` wherever it
rewrites `DrawingLayers`, and removing a drawing layer removes its film entry in the same `CommitEdit`.

### R-stk9-0e — every copy of the technology states the film

`src/Ui/Layout/StarterTechnologies.cs`, `src/Design/resources/technologies/mmic-GaAs_2LM_100um.ctech` and
`examples/LVS/tech/mmic-GaAs_2LM_100um.ctech` are rewritten in the new form: `Resistor` on Metal1, 50 Ω/sq.

**`examples/PDK PCells/tech/mmic-GaAs_2LM_100um.ctech` gains the same entry.** It has a `Resistor` layer and states no sheet resistance
at all, so a `TFR` placed in that example is refused for having no film. It is older than the other two and differs in other ways
(no `Line Marker` or `Inductor Marker`, for instance): add the film and leave the rest. Nothing in that example draws on the layer, so
no result moves. `testdata/em-tfr/zigzag/tech/` follows too; its committed references do not move, because the values do not.

**A gate holds them together:** every `mmic-GaAs_2LM_100um.ctech` in the repo, and the starter, states the one film on Metal1 at the same
R_s. Three copies had already drifted on what mattered here, which is how the example came to differ.

---

## 2. The scene

### R-stk9-1 — a film is drawn ON its band, against its face

A film is not a band. It is a patterned feature of one, so it takes **no height of its own in the
sandwich**, and §3a of the overview (proportional within a kind, compressed when the range will not
fit) does not apply to it.

- A **strip** inside its conductor's band rect, flush with the face it sits on:
  `At ?? the level's SheetAt ?? Bottom`. It is a fixed `StackupScene.FilmStripPx` tall (a theme
  constant, small next to the band), never proportional to 50 nm against 3 µm.
- Drawn in the film layer's own colour and fill pattern, the way a band takes its conductor's.
- **Several films on one face share the strip** in equal lateral segments, in the order of the entry's
  `Films`. There is no persisted lateral position. A via's lane (brief 5) is a cosmetic field the user
  asked for. Nobody asked for one here, and `R-stk5-9` shows what a cosmetic geometry field costs.
- The label column gets one `Spec` piece per film: `Resistor · 50 Ω/sq · 50 nm · top`. Thickness
  and face appear only when stated, so a film with neither reads `Resistor · 50 Ω/sq`.
- **No film, no change.** A technology with no film produces a scene bit-identical to
  today's: same rects, same labels, same hit list.

### R-stk9-2 — a hit kind of its own

`StackupHitKind.Film`, with the film's `LayerKey` on `StackupHit`. A film strip is tested BEFORE its
band, so a click on the strip is the film's and a click elsewhere on the band is the band's. The
label pieces carry `StackupField` values for R_s and thickness, so brief 4's label hit-testing works
on them unchanged.

---

## 3. Selection

### R-stk9-4 — a film is selected by key, alongside the band selection

`TechEditorViewModel` gains `SelectedFilmLayerKey`. It is mutually exclusive with
`SelectedStackupLayerName`: setting either clears the other. It is held by key for brief 3's reason,
which is that a reference does not survive a committed edit. Selecting a film:

- outlines its strip (brief 3's highlight, not a new style);
- scrolls the **conductor's** card into view and shades that card's film row (§7);
- clears the card filter if the filter would hide that card (overview §3d);
- `Esc` clears it, after an open inline editor has taken its own `Esc` (overview §3c).

---

## 4. Inline editing

### R-stk9-5 — one write path

R_s and thickness are properties of the film entry, so a double-click on either label opens brief
4's `SchematicInlineEditBox` and commits through **the same function the card's film row calls** (§7): one
method on `TechEditorViewModel`, which is the only code that writes a `StackupFilm`. Parsing, units (`Ω/sq`, a thickness in the display
unit), refusal of zero or negative values and the undo entry all come from it.

---

## 5. Drag

### R-stk9-6 — the rules live in `src/Design`, once

Add `ResistorFilmBinding` in `src/Design/Layout`, with `CanPlaceOn(Technology, LayerKey film, string
conductorName, out string reason)`, plus the move itself. `TechValidation` **calls it**, and its rules
are: a film on one conductor only, not on a ground reference, on a layer that is not also that level's metal (`tech.film.also-metal`), a
positive R_s, and a layer the technology declares. The canvas calls it to decide whether a drop target is live. A drop the canvas would
allow and validation would then flag cannot happen, because the two ask the same function.

### R-stk9-7 — two drags, one undo entry each

Both use brief 5's machinery: the movement threshold that separates a click from a drag, the
ghost, pointer capture on press, and commit once on release.

- **Face:** dragging a film vertically within its own band snaps it to the nearer face and sets
  `At`. Dropping it back on the face it came from writes nothing.
- **Move:** dragging a film onto another conductor band moves its entry from the old conductor's
  `Films` to the new one's in ONE `CommitEdit` (*"Move Resistor to Metal2"*).
  Two edits would be two undo entries, and the one in between is a film on no level or on two.

**Refused targets** (a via, a dielectric, a ground-reference conductor) show brief 5's no-drop ghost.
A tooltip carries `CanPlaceOn`'s own reason, never a second wording. Releasing there writes nothing.

---

## 6. Context menu

### R-stk9-8 — built on brief 6's one `Opening` handler

| Under the pointer | Adds |
|---|---|
| A film | **Face ▸** Top / Bottom (✓ on the current one) · **Move to ▸** each conductor `CanPlaceOn` accepts · **Remove Film** · ─ · Copy |
| A conductor | **Add Resistor Film ▸** each drawing layer that is neither metal nor a film (absent when there are none) · **New Resistor Film…** |

**Add Resistor Film** asks for the R_s, because a film has no meaning without it, and that is the one value that does not default.
**New Resistor Film…** asks for a name and an R_s, and creates the drawing layer AND the film in one `CommitEdit`, through a single
`TechEditorViewModel.AddResistorFilm(conductorName, name, rs)`. That method takes the new layer's key, colour and z-order from the Layers
tab's own add-layer function rather than repeating its rules. This is the one place this series creates a Layers-tab row, and it is
allowed because it goes through that tab's own function. **Remove Film** removes the entry and leaves the drawing layer. Every item
writes its own field and only that field.

`R-stk6-7`'s comment-stripped source scan covers every file this brief adds to the canvas.

---

## 7. The card

### R-stk9-9 — where a film is defined

The conductor card gains a **Resistive films** section under its drawing layers: one row per film, with the layer's name, R_s (Ω/sq),
thickness and face, and an add button offering what the menu's **Add Resistor Film** offers. This is what makes the Stackup tab able to
define a film without the drawing, and it commits through the same functions as §4 and §5, so the card and the canvas are two views of
one write path.

The section sits below the drawing-layer picker, a fixed part of the card whether or not it has rows, so nothing above it moves when a
film is added.

**The drawing-layer check list no longer binds a film.** EM-TFR's chip was on that list because the binding was. A layer with a film
entry is shown disabled there, still as `Resistor · 50 Ω/sq`, with a tooltip naming the conductor it is on. Ticking it as metal is what
`tech.film.also-metal` would refuse, so the control does not offer it.

---

## 8. Gates

| Requirement | Test |
|---|---|
| `R-stk9-0` | A `.ctech` round-trips its films; `DrawingLayers` holds no film; omitted when empty. `TechPersistence`'s tests |
| `R-stk9-0a` | EM-TFR's whole gate set still passes **unchanged in its numbers**: the planar, 3D and goldens read the same values from the new place. `ResistiveFilmTests`, `PlanarFilmTests` |
| `R-stk9-0b` | The three legacy cases: bound, unbound, no conductor. A written file carries no layer field |
| `R-stk9-0c` | An undefined layer with area is named in a warning; a `TFR` on a technology with no film is refused |
| `R-stk9-0d` | Renumbering and removing a layer follow through in one undo entry |
| `R-stk9-0e` | Every copy states the one film at one R_s (a scan of the repo's `mmic-GaAs_2LM_100um.ctech` files plus the starter) |
| `R-stk9-1` | Strip inside its band, flush with the right face for each of `At` Top/Bottom/unset; equal segments for two films on one face; label pieces; **no film → scene bit-identical**. `StackupSceneTests` |
| `R-stk9-2` | Strip hit before band; label hits carry the R_s / thickness fields. `StackupSceneTests` |
| `R-stk9-4` | Film/band selection mutually exclusive; card scrolls and shades; filter cleared when hiding; `Esc` order. `StackupSelectionTests` |
| `R-stk9-5` | An inline R_s edit leaves the same `Technology` and the same undo stack as the same edit typed in the card. `StackupInlineEditTests` |
| `R-stk9-6` | `TechValidation` and the canvas agree on every target in the starter MMIC stack (a table, not a sample). |
| `R-stk9-7` | Each drag is exactly one undo entry, and one `Ctrl-Z` restores the technology bit for bit; refused drops write nothing; same-face drop writes nothing. `StackupDragTests` |
| `R-stk9-8` | Each menu item writes its own field only; *New Resistor Film…* is one undo entry; *Remove Film* leaves the layer; the source scan. `StackupContextMenuTests` |
| `R-stk9-9` | Card edits and canvas edits produce identical technologies; a film layer is disabled in the check list; a value that is not a positive number is refused. |
| Layers tab | The Layers tab's view model and XAML name no sheet resistance, thickness or face (a source scan). |

**Visual verification is required** (root `CLAUDE.md`). Render with `tools/UiSnapshot`, in light and dark:

- the starter MMIC stack with `Resistor` on Metal1's top face, including Metal1's card;
- a stack with two films on one face and one on the other;
- a selected film;
- a drag ghost over a refused target.

Look at each one, and list them in the completion note.

```
dotnet test tests/Ui.Tests      --no-build --filter "FullyQualifiedName~Stackup|FullyQualifiedName~ResistiveFilm|FullyQualifiedName~MmicPassive|FullyQualifiedName~MmicRecognitionDeck"
dotnet test tests/Engine.Tests  --no-build --filter "FullyQualifiedName~PlanarFilm"
dotnet test tests/Firewall.Tests --no-build
```

Findings go in `src/Design/RESOLVED.md` (the model), `src/Render/RESOLVED.md` (scene) and `src/Ui/RESOLVED.md` (canvas, view model),
and never in a CLAUDE.md.

---

## 9. Stop and report if

- Moving the fields changes any number in `testdata/em-tfr/zigzag/refs/` or an EM-TFR golden. The move is of where a value is read from,
  and nothing it computes should change.
- An LVS or DRC result changes on a shipped technology. Retiring `MetalLayers` should leave them exactly as they read with it.

---

## 10. Scope

- **Docs:** `docs/user/src/reference/stackup.md` says how a film is defined (its section currently says a `.ctech` states it) and gains
  the gestures. `file-formats.md` and the `.ctech` reference pick up `Films`. The `stackup-mmic` figure changes, because the starter
  technology now draws a film. **Do not run DocGen for figures**; the owner regenerates at the end of the series.
- **Not here:** a film's lateral position (§2), zoom (overview §4), drawing films in the layout
  editor differently from today (they are ordinary drawing layers there), and anything EM-TFR
  computes.
- **Superseded:** EM-TFR §1's *Sheet resistance* column on the Layers tab was not built, and its "no new field" no longer holds.
