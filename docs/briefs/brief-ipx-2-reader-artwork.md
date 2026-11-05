# Brief IPX-2 — IPC-2581 reader: layers, units, dictionaries and copper

**Series:** IPC-2581 import, IPX-0 to IPX-4 · **Tag:** `R-ipx2-n`
**Area:** new `src/Design/Layout/Interchange/Ipc2581Reader*.cs`, new `testdata/ipc2581/`,
`tests/Ui.Tests/Ipc2581/`
**Depends on:** IPX-0.

---

## 0. Scope

`Ipc2581Reader.Read(Stream, dbuPerMicron, stepName)` fills a `PcbBoard`. This brief covers everything needed
for correct copper on every layer. IPX-3 adds nets, pads as parts, holes, vias and the stackup. The reader is
pure: no cell folders, no technology, no messages outside `PcbBoard`.

## 1. `R-ipx2-1` — reading the file

- **`R-ipx2-1a`** Read with `XmlReader`, using `DtdProcessing.Ignore` and a null `XmlResolver`, as
  `ComponentLibraryXmlReader` does. Build each `Set` as a small subtree and release it before the next. A
  single file can be a quarter of a gigabyte, so never load the whole document as a tree.
- **`R-ipx2-1b`** Match elements by local name. Files in the field carry a namespace, a different one, or
  none.
- **`R-ipx2-1c`** A root other than `IPC-2581`, or a file with no `Ecad` section or no conductor layer, is a
  refusal saying what the file holds. A BOM-only or stencil-only export is the common case, and
  `FunctionMode` is quoted in the sentence.
- **`R-ipx2-1d`** Apply `PcbReader.EntityHardCeiling` by counting as the read proceeds. Above it, refuse with
  the same advice the board reader gives.
- **`R-ipx2-1e`** Several `Step`s: name the choice. With no `stepName` given, one step is read, and more than
  one is a refusal listing them. A step whose content is only `StepRepeat` is a panel; refuse it and name the
  board step it places. Do not expand step-and-repeat.

## 2. `R-ipx2-2` — the forms a file takes

The revision does not predict these. Handle each form by the elements present, with one fixture per column.

| Item | One form | Another form |
|---|---|---|
| Root | `revision="A"`, `"B"`, `"C"` | No `revision` attribute |
| Container | `Layer`, `Stackup`, `Step` directly under `CadData` | Inside `LayerDesc` and `StepList` wrappers |
| Copper functions | `CONDUCTOR`, `PLANE` | `SIGNAL`, `MIXED` |
| Outline function | `BOARD_OUTLINE` | `BOARDOUTLINE` |
| Inner side | `INTERNAL` | `INNER` |
| Pads and holes | `Set` holding `Pad` or `Hole`, under a `LayerFeature` | `PadStack` at step level holding `LayerHole` (with `Span`) and `LayerPad` |
| Names | Plain | Prefixed with the step name, `step:NET1` |
| Booleans | `true` | `TRUE` |

- **`R-ipx2-2a`** Strip a leading `<step>:` from layer-independent names only when the prefix equals the step's
  own name. Do not strip other colons.
- **`R-ipx2-2b`** Parse booleans case-insensitively. Files write `mirror` as both `TRUE` and `true`; the standard's
  text also uses the word `MIRROR` in one place, so accept that too.
- **`R-ipx2-2c`** An unknown element is counted in `UnknownTokenCounts` and skipped with its subtree. It is never
  a parse failure. `TextAttribute` holds name/value metadata, is not drawn text, and must not produce a label.

## 3. `R-ipx2-3` — units and coordinates

- **`R-ipx2-3a`** Units are per section. `CadHeader@units` and the `units` on each dictionary
  (`DictionaryStandard`, `DictionaryUser`, `DictionaryLineDesc`, `DictionaryFillDesc`) are read independently.
  `MILLIMETER`, `MICRON` and `INCH` are the accepted values. A section with geometry and no stated unit is a
  refusal, because a guess would differ by a factor of 25.4 or 1,000.
- **`R-ipx2-3b`** Y is up. Never call `PcbUnits.Y`, because doing so mirrors the board and the result still looks
  like a board. Rotation is counter-clockwise viewed from the top.
- **`R-ipx2-3c`** `Xform` applies in the standard's stated order: move the origin (`xOffset`, `yOffset`), then
  `rotation`, then `mirror` (negating x), then `scale`, and `Location` places the result. Pin this order with a fixture
  using an asymmetric pad under each combination, and let IPX-4 §5 confirm it on real bottom-side parts: an order error
  shows only on a shape that is both rotated and mirrored, and only when the shape is not symmetric.

## 4. `R-ipx2-4` — layers

- **`R-ipx2-4a`** Fill `PcbBoard.LayerTable` from `Layer`. `CONDUCTOR`, `SIGNAL`, `CONDFILM` and `CONDFOIL` map to
  type `signal`, `PLANE` to `power`, `MIXED` to `mixed`, and all others to `user`. Copper is decided by
  `layerFunction` and never by the layer's name.
- **`R-ipx2-4b`** `Ordinal` is the layer's position top to bottom in the stackup, for layers the stackup names, and
  document order otherwise. Layer document order is not physical order.
- **`R-ipx2-4c`** `RESISTIVE` and `CAPACITIVE` layers are imported as non-copper artwork and named in
  `DegradedCounts`. `StackupFilm` mapping is not attempted here.
- **`R-ipx2-4d`** `PcbLayerReconciliation` and `PcbLayerNaming` were written around one format's names. Confirm that
  names such as `TOP`, `GND` and `Conductive1` reconcile sensibly and that surface roles come from `side`. Any
  change needed is made through the seam, and the `.kicad_pcb` tests stay unchanged.

## 5. `R-ipx2-5` — features to shapes

- **`R-ipx2-5a`** `Polyline` or `Line` plus `LineDesc` or `LineDescRef` becomes a `PathShape`. `lineEnd` `ROUND` →
  `Round`, `SQUARE` → `Square`, `NONE` → `Flush`. `PolyStepCurve` becomes an `EdgeKind.Arc` from its centre and
  direction; it is not flattened here.
- **`R-ipx2-5b`** `Polygon` or `Contour` with `Cutout`s becomes a shape with holes. A contour with a nonzero line
  width and a fill is drawn at its stated outline, and the case is counted in `DegradedCounts` if width is lost.
- **`R-ipx2-5c`** Primitives resolve through the dictionaries or inline: `Circle`, `RectCenter`, `RectCorner`,
  `RectRound`, `RectCham`, `Oval`, `Octagon`, `Hexagon`, `Donut`, `Thermal`, `Moire`, `Butterfly`, `Diamond`,
  `Triangle`, `Ellipse`. An unsupported or unresolved one is counted and draws nothing. No placeholder rectangle
  is ever drawn.
- **`R-ipx2-5d`** `UserPrimitiveRef` to `UserSpecial` expands to its feature list, with its transform applied.
  Guard against a reference cycle, with a refusal naming the primitive.
- **`R-ipx2-5e`** `Hole` and `LayerHole` are held for IPX-3. Non-copper layers (silkscreen, mask, paste, document)
  import as artwork on their own layers. The user can leave them off in the mapping dialog, and the import default
  for silkscreen follows what the Gerber path does.

## 6. `R-ipx2-6` — polarity, which decides what is copper

This is the part most likely to look plausible and be wrong.

- **`R-ipx2-6a`** Positive layer: features are copper. A `Set` with `polarity="NEGATIVE"` erases what was drawn before
  it, in document order.
- **`R-ipx2-6b`** Negative layer (`Layer@polarity="NEGATIVE"`): features are clearances. Copper is the outline minus
  the features. The outline is the layer's own `Profile` when stated and otherwise the step's `Profile`.
- **`R-ipx2-6c`** A negative layer with no usable profile is a refusal for that layer: the layer is not imported and
  this is reported by name.
- **`R-ipx2-6d`** Erasing must keep each shape's net. The Gerber composite loses per-object net names, and that must
  not happen here: each surviving piece keeps the net of the shape it came from. Clip only the shapes an eraser's box
  touches, using a spatial query, so a few hundred antipads do not rewrite a whole layer.
- **`R-ipx2-6e`** A thermal relief on a negative plane is a clearance of the thermal's shape. Its spokes remain copper
  through the subtraction, and this needs its own fixture.

## 7. Gates

Hand-written fixtures in `testdata/ipc2581/`, each a few dozen lines, one per form in §2, with its expected shapes
calculated by hand:

1. One test per form: the same board's copper from each form has equal area on each layer.
2. Units: inch and millimetre copies of one fixture agree exactly in DBU.
3. Transform order: the asymmetric pad, mirrored and rotated, lands where the hand calculation says.
4. Polarity: a negative plane with a clearance and a thermal; a positive pour with an erased island whose surrounding
   net survives.
5. A refusal for each case in `R-ipx2-1c`, `R-ipx2-1e`, `R-ipx2-3a` and `R-ipx2-6c`.

Run those classes only. Real-file acceptance is in IPX-4 §5.
