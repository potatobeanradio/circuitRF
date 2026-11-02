# Brief 101 — Reference images in 3D: image sheets, and images mapped onto faces

**Tag:** `R-em3d101-n` · **Series:** 3D editor (follows 100).
**Area:** `src/Design/ThreeD/C3dDocument.cs` (+ `C3dPersistence`, `C3dElaborator`, `C3dFragment`, `C3dValidation`),
`src/Render/Scene3D/Scene3DModel.cs`, `Scene3DBuilder.cs`, `Scene3DFramePlan.cs`, `src/Render/Renderers/BitmapCache.cs`,
`Em3dDrawing.cs`, `Em3dSectionRenderer.cs` (the iso picture), `src/Ui/Viewer3D/Shaders/scene.wgsl` (+ the three generated
files), `src/Ui/Viewer3D/{Metal,D3D11,Vulkan}/*Backend.cs`, `src/Ui/Viewer3D/Viewer3DPane.cs` (file drop),
`src/Ui/ThreeD/C3dEditorViewModel.*` (new partial `.Images.cs`), `C3dPropertiesViewModel` (new partial `.Images.cs`),
`src/Ui/Views/ThreeD/C3dEditorView.axaml`, `src/Ui/Views/WorkspaceWindow.axaml` (both 3D ▸ Draw menus), `src/Cli/DocumentSchema.cs`,
`tools/ShaderGen` (binding map), `docs/user/src/reference/drawing-in-3d.md`, `docs/user/src/reference/cli.md`
**Depends on:** 92 (per-object transparency), 93 (Model flag), 95 (copy/paste) · **Blocks:** —

Two phases, one brief. **Phase A** (image sheets, §1–§7) carries all the shared infrastructure, the GPU texture path above
all, and ships on its own. **Phase B** (images on faces, §8–§11) is the marginal part on top of it. Land and gate A before
starting B; they are two commits.

## Why

A schematic and a layout already take a reference image (`SchematicBitmap`, `BitmapPrimitive`, `BitmapShape`, the last from
`brief-layout-bitmaps-and-insert-button.md`). The layout use is the one that matters here: **trace a photo or a drawing with
native primitives, or lay the design over a reference and compare.** The 3D editor has no equivalent, so a die photo, a
package outline from a datasheet or a fixture drawing cannot be traced in 3D and the result extruded.

**Mapping an image onto a face is worth doing too**, and it is cheap once Phase A exists, because the expensive part (a
textured draw on three GPU backends) is shared. It is how a die photo goes onto a die's top face, a part marking onto a
package lid, or a fixture photo onto the wall it was taken of. That last case checks a model against the real thing, which a
loose sheet floating near the face does not do as well.

## 0. What already exists — read this first

| Piece | Where |
|---|---|
| The decode cache: `Load`, `Invalidate`, `TryGetPixelSize`, `DrawBrokenPlaceholder` (one cache for every editor) | `src/Render/Renderers/BitmapCache.cs` |
| The layout's placement path: viewport-relative sizing (R-bmp-4), one `PlaceBitmap` shared by drop and button, Resolve Path… / Refresh Cache, Locked | `src/Ui/Layout/LayoutEditorViewModel.Bitmaps.cs` |
| The layout toolbar button (`ImagePlusOutline`, *Insert Bitmap…*, a `StorageProvider` picker in code-behind) | `src/Ui/Views/Layout/LayoutEditorView.axaml` ~215 |
| A sheet: `Plane`, `Offset`, `Rect` **or** `Outline`+`Holes`, `ThicknessUm` | `C3dSheet`, `C3dDocument.cs` ~264 |
| Per-object transparency, one constant for the cap | `C3dObject.Transparency`, `C3dTransparency.Max` (brief 92) |
| The Model flag, the ONE solve filter, the *Not Modeled* tree group | `C3dObject.Model`, `C3dModelled.Filter` (brief 93) |
| A record drawn on a face, listed **beneath its object** in the tree, selected by clicking it in the view | EM face boundaries: `C3dFaceBoundary`, tree kind `EmBoundaryKind`, tints (`Scene3DBuilder.FaceTintPrefix`, `C3dEditorViewModel.Records.cs`) |
| Coincident-face precedence (who wins the depth test) | `Scene3DDepthTie`, `Scene3DFramePlan.cs` ~78 |
| THE visibility function every tick, H key and Hide all goes through | `SetRowsVisible`, `C3dEditorViewModel.TreeGrouping.cs` |
| The Face-mode context menu | `FaceMenuItems()`, `C3dEditorViewModel.FaceEdit.cs` ~508; *Drawing Plane from Face*, `.Draw.cs` ~681 |
| A face's plane: point + normal only (no in-plane axes), and it lives in `src/Ui` | `C3dFaceFrame`, `src/Ui/ThreeD/Operations/FaceEditTools.cs` |
| One WGSL source, cross-compiled offline to `.metal`/`.hlsl`/`.spv`, each stamped with the WGSL's hash | `tools/ShaderGen`, `src/Ui/Viewer3D/Shaders/` |
| The pane's drop handlers (text payloads from the Project Tree only, today) | `Viewer3DPane.OnTreeDragOver` / `OnTreeDrop` |
| The archive's reference walk (recognises a path by what it RESOLVES to, `.c3d` already included) | `src/Ui/Archive/DocumentFileRefs.cs` |

**The 3D shaders sample no texture today.** There is no texture binding in `scene.wgsl`, no sampler, no UV in
`Scene3DVertex`, and none of the three backends creates a sampled texture (the Metal backend makes render targets and pick
targets only; the Vulkan descriptor set holds two dynamic uniform buffers). **§4 is new infrastructure on three GPU APIs, and
it is most of this brief's cost.** Do not try to avoid it with a grid of vertex colours (one vertex per pixel is a million
vertices for a 1000 × 1000 photo, and still blurry), and do not draw images in a Skia overlay on top of the GPU picture
(that has no depth, so an image would show through the solids in front of it).

Do **not** unify the 3D image record with `BitmapShape`, `BitmapPrimitive` or `SchematicBitmap`. The layout brief kept those
apart for good reason, and 3D is a fourth coordinate system. **Share the cache, not the model.**

---

# Phase A — Image sheets

## 1. `R-em3d101-1` — the document: an image sheet IS a sheet

An image placed in 3D is a **`C3dSheet` that carries an image**, not a new object kind. The owner wants it usable "as any
other sheet" (Model on, a material, Make Port from its row, moved, rotated, grouped, copied, arrayed, hidden), and a sheet
already does all of that. A new `$type` would have to opt in to each one again.

```csharp
/// The image a sheet is drawn with, or one mapped onto a face (§8). Drawing only: no solver, mesher or exporter reads it.
public sealed class C3dImage
{
    public string Path { get; set; } = "";   // §1b
    // Phase B adds the face placement fields (§8); a sheet's image has none, since it fills its sheet.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unread { get; set; }
}

public sealed class C3dSheet : C3dObject
{
    …
    /// brief-em3d-101 — drawn with this image instead of its material's colour. Written only when set.
    public C3dImage? Image { get; set; }
}
```

- **a. The image fills its sheet.** For a `Rect` sheet, the image's corners are the rectangle's corners. For an `Outline`
  sheet (someone edited a vertex), the image fills the outline's bounding rectangle in the plane and the outline clips it.
  The image is never cropped or tiled to fit. Its orientation on the plane is fixed by §1c, and the sheet's own `Placement`
  (rotate, mirror) moves it with the sheet.
- **b. Path storage.** A reference, never the bytes (the owner's requirement). It is written **relative to the `.c3d` when
  the file lies inside the `.c3d`'s workspace, absolute otherwise**. That is the layout's convention, and
  `DocumentFileRefs`'s `RefBase.Document` already covers it. It resolves against **the `.c3d` that holds it**, which for
  an instance's content is the CHILD document, never the parent (gate 9). **Owner decision D1** below covers the
  alternative: copy the file into the cell's `3d/` folder, as STEP import does.
- **c. Orientation, one rule, written once** in `src/Design` (e.g. `C3dImageFrame`): on an XY sheet the image's right is
  +X and its up +Y; on XZ, right +X and up +Z; on YZ, right +Y and up +Z. Seen from the plane's positive side, the
  picture is never mirrored. Phase B's face rule (§8c) extends this same function, so there is one.
- **d. Defaults on creation:** `Model = false` (the owner's requirement, written to the file as brief 93 writes it).
  `Material` is null, `ThicknessUm` null, `Transparency` null. A not-modelled sheet with no material must raise **no**
  "has no material" warning: `C3dElaborator.NoMaterialWarning` applies only to what a solve sees. Turning Model on with
  no material raises the existing warning, unchanged.
- **e. Drawn whatever its material.** An object with no material draws as a wireframe today (3D editor bugs round 1). An
  image sheet draws its image whether or not it has a material, modelled or not.
- **f. Transparency is the object's.** On an image sheet, brief 92's `Transparency` row is the image's transparency.
  There is one knob, not two, because the image *is* the sheet's surface. The null default draws it opaque. A PNG's own
  alpha is honoured and multiplied by it. **Owner decision D2.**
- **g. `Locked`** (owner decision D3, recommended yes): `C3dSheet.Locked`, written only when true. It is offered **only
  on a sheet with an Image**, as the layout's `Locked` is offered only on a bitmap. A locked image sheet stays selectable,
  pickable and editable in the Inspector, but Move, Rotate, the gizmo, nudges, vertex moves and Delete-by-drag are refused
  with a status sentence. A tracing underlay that moves when a click lands on it is the failure this prevents.
- **h. Not geometry to anything outside the drawing.** The image is never read by the EM lowering, the thermal run, the
  mesher, STEP export (brief 69) or a boolean. The *sheet* takes part exactly as any sheet does when it is modelled. STEP
  export **skips a not-modelled image sheet with one note** ("2 image sheets skipped: reference images are not
  geometry"), the layout's R-bmp-3. Leave a forward comment at each consumer.
- **i. An image edit never makes a result stale.** Adding, removing, re-pointing or changing the transparency of an image
  is drawing-only, like `Hidden` and `Transparency`. It must not change the input brief 98's solved-status glyphs and
  brief 87's inputs manifest compare against. Toggling **Model**, or editing the sheet's geometry while it is modelled,
  makes results stale as it does today.
- **j. Additive.** No `FormatVersion` bump. A document with no image is byte-identical after a load and save.
  `DocumentSchema.cs` documents `Image`, `Image.Path` and `Locked`.

## 2. `R-em3d101-2` — placing one: the toolbar button, the menu, and a drop

**One placement function, `PlaceImageSheet(path, centre, …)`, in `C3dEditorViewModel.Images.cs`.** The button, the menu
item and a drop all call it, and it is the only place an image sheet is built. If a second caller grows its own sizing,
something has been duplicated.

- **a. Insert Image… button** on the 3D editor toolbar, after the Polyline tool and before Cylinder (it draws a sheet,
  so it sits with the sheet tools). It is a `Button`, not a `ToggleButton`, because nothing is armed. Use the
  `ImagePlusOutline` icon (what the layout toolbar uses) with the tooltip *"Insert Image…: a reference image as a sheet on
  the drawing plane, not modelled"*. `IsVisible="{Binding ViewModel.IsEditable}"`; disabled with a reason in a setup's
  view (`IsViewOnly`) and on a locked document. The picker is a `StorageProvider` dialog **in code-behind** (UI firewall),
  filtered to what SkiaSharp decodes: `.png .jpg .jpeg .bmp .gif .webp`. It places the image on the **current drawing
  plane**, centred where the view's centre ray meets that plane (the viewport centre projected onto the plane if the ray
  is parallel to it).
- **b. 3D ▸ Draw ▸ Image…** in **both** menus: the `NativeMenu` (`WorkspaceWindow.axaml` ~258) and the in-window
  `_Draw` menu (~1152). It is the same command.
- **c. Drag and drop** of an image file onto the 3D view: `Viewer3DPane` adds a **file** drop path beside its
  Project-Tree text path. Read `DataFormat.File` and accept only the extensions above. Any other file shows
  `DragDropEffects.None` and does nothing, so a `.step` dropped by mistake is not half-imported. The sheet lands on the
  current drawing plane, **centred where the drop point's ray meets the plane**. Several files dropped at once make one
  sheet each, laid side by side along the plane's right axis, as **one undo entry**. **Owner decision D6:** Shift held on a
  drop over a flat face maps the image onto that face (Phase B) instead.
- **d. Size (R-bmp-4, carried over):** the long edge spans **~25 % of the visible width of the view at the placement
  point**. That is the width at that depth, because a perspective view's width depends on distance. Round to a tidy
  number in the document's `DisplayUnit` and snap to `SnapDbu`. The image's pixel aspect ratio is kept exactly. If the file
  will not decode, use a 4:3 box of the same size and a Messages warning; placing never silently fails. Do not use DPI
  metadata, for the reason the layout brief gives.
- **e. Name:** `NextName("image")`, so `image1`, `image2`, …
- **f.** Placing is **one undo entry** and selects the new sheet, which brings its Inspector forward.
- **g. Drawing Plane from Image** in an image sheet's context menu (and its tree row's) sets the drawing plane to the
  sheet's plane and offset, the sheet's version of the existing *Drawing Plane from Face*. Tracing then takes one click to
  set up: pick the Polygon tool and trace.

## 3. `R-em3d101-3` — tree, Inspector, menus

- **a. Tree.** An image sheet is a sheet, listed where sheets are listed. Because it is not modelled by default, *By type*
  puts it under **Not Modeled** (brief 93) until Model is turned on. Its row uses an image glyph (`ImageOutline`) instead
  of the sheet glyph, and its detail text is the file name. The type filter gains **"Image sheet"** as its own entry, so
  references can be hidden from the tree in one click. A broken path shows the row's warning glyph and the reason as a
  tooltip.
- **b. Inspector, an "Image" section** on an image sheet, above the existing sheet rows:
  - **File**: the stored path, read-only text, with **Browse…** (re-point it; one undo entry) and **Reveal**.
  - **Pixels**: `1920 × 1080`, read-only, from `BitmapCache.TryGetPixelSize`. *"File not found"* when broken.
  - **Width / Height**: the sheet `Rect`'s size in the display unit, with a **Keep aspect** toggle on by default (aspect
    = the image's pixels). Changing one changes the other when it is on, and **Reset to image aspect** restores the
    image's ratio. Each change is one undo entry, through the same writer the sheet's own `Rect` rows use (expressions
    allowed, as they already are on a sheet's dimensions).
  - **Transparency**: brief 92's row, which is already there, unchanged.
  - **Locked** (D3).
  - **Remove Image**: the sheet stays and becomes an ordinary sheet. It is what turns a traced reference into a real
    sheet without redrawing it.
  - The existing sheet rows stay: Model, Material, Plane/Offset, Placement, Group.
- **c. The view's context menu** on a selected image sheet adds: **Drawing Plane from Image**, **Lock / Unlock**,
  **Replace Image…**, **Resolve Path…** (only when broken) and **Refresh Image** (`BitmapCache.Invalidate` + a texture
  re-upload, for a file edited outside circuitRF). Everything a sheet offers today stays (Make Port on the row, Model,
  Hide, Delete, …).
- **d. Material hover (brief 94)** on an image sheet reads *"image1 — die.png (not modelled)"*.
- **e. Corner resize on the canvas.** In Vertex mode, moving a corner of an image sheet's `Rect` resizes the rectangle
  with the opposite corner fixed, **keeping the image's aspect unless Shift is held**, and the sheet stays a `Rect`. It is
  never converted to an `Outline`, which would turn a resize into a clip. Today's vertex move on an ordinary sheet is
  unchanged.

## 4. `R-em3d101-4` — drawing it: a textured draw on all three backends

The decisions go below the firewall, in `Scene3DBuilder`/`Scene3DFramePlan`, as for every other draw. The backends stay
plumbing.

- **a. The scene.** `Scene3DModel` gains an image list: for each, its object id, the **absolute resolved path** (the
  texture key), and its triangles in a separate vertex stream of `Scene3DImageVertex { X, Y, Z, U, V, Id, Face }` (do not
  widen `Scene3DVertex`, whose 24-byte stride every existing draw relies on). An image sheet's triangles are its own
  triangulation with UVs from §1c. `Scene3DBuffer` gains `Image` and `Scene3DPipeline` gains `Image`. Keep the existing
  rule of one draw per object, sorted back to front with the translucent ones when its alpha is below 1 (transparency > 0,
  or a PNG with any alpha below 255, which the decode reports once). Otherwise it goes in the opaque pass with depth
  write.
- **b. Coincident faces.** `Scene3DDepthTie` gains an **`Underlay`** step, the lowest, **below `Behind`**. An image sheet
  gives way to **every** face lying on its plane, so a polygon traced on the image's own plane is drawn over it and is
  the one the ID pass finds. That is the 3D form of the layout's "bitmaps always render behind" (R-bmp-2). Gate it with a
  coplanar pair (gate 5).
- **c. The shader.** Add `vs_image` / `fs_image` / `fs_pick_image` to `scene.wgsl`: a 2D texture and a sampler at new
  bindings, the clip plane (`clipped()`), hover and selection highlight (`highlight()`), and the per-draw transform
  (brief 46: dragging an image sheet previews like any object). Regenerate with `tools/ShaderGen`. **ShaderGen's binding
  map has no texture or sampler entry today:** add one for each target (MSL `[[texture(0)]]`/`[[sampler(0)]]`; HLSL
  `register(t0)`/`register(s0)`; SPIR-V set 0, bindings 2 and 3, or a second descriptor set if that suits the Vulkan
  backend better; record which in the README's table). Commit the regenerated files with their WGSL hash stamp. The
  existing stale-shader test must pass.
- **d. The three backends.** Each needs: a texture created and uploaded per **distinct path** (an `RGBA8` *unorm* format,
  **not** `_SRGB`, so a pixel arrives at the framebuffer with the same value a vertex colour of that value would; brief 69
  found the sRGB trap once already), with **CPU-built mip levels** (SkiaSharp, high quality, built once in `src/Render`, so
  all three backends upload levels and none generates them, which keeps the three pictures alike). Also a linear-mip
  sampler with clamp-to-edge, a bind before each image draw, and release when the scene drops the path.
  - Metal: `newTextureWithDescriptor:` (mipmapped), `replaceRegion:` per level, `setFragmentTexture:atIndex:` /
    `setFragmentSamplerState:atIndex:`.
  - D3D11: `ID3D11Texture2D` with `MipLevels`, `UpdateSubresource` per level, `PSSetShaderResources` / `PSSetSamplers`.
  - Vulkan: an image + view + memory per texture, a staging upload with the layout transitions, a combined image sampler
    descriptor per texture from a pool sized by the scene's image count. The current single descriptor set is written once
    (`VulkanViewer3DBackend` ~252–307), so this is the largest of the three. **Say in `RESOLVED.md` exactly how the
    per-texture descriptors are allocated and when they are freed**, since a pool exhausted on the hundredth re-elaboration
    is the kind of fault that only shows after an hour's work.
- **e. Upload is keyed by path, never per frame and never per re-elaboration.** A scene change that keeps an image's path
  reuses its texture. Only a new path, a Refresh Image or Resolve Path uploads. An image sheet moved, hidden, made
  transparent or Model-toggled uploads **nothing**. That is a counter, not a timing (gate 6), in the spirit of
  `project-3d-visibility-tick-fast`.
- **f. Size cap.** Decode at full size, then **downsample to a long edge of 4096 px** before upload, with a Messages note
  naming the file once. That is the minimum every target GPU guarantees, and an 8000 × 6000 photo is 190 MB of mips
  otherwise. One named constant (`C3dImage.MaxTexturePixels`). **Owner decision D10.**
- **g. Broken path.** Draw the sheet with a checker placeholder texture (built once, the 3D form of
  `DrawBrokenPlaceholder`), never a hole. It stays selectable and movable.
- **h. Picking** uses the sheet's geometry, as every pick does. A fully transparent pixel of a PNG still picks; say so in
  the doc.
- **i. Exports.** *Copy Picture* / *Export Picture…* (GPU read-back) get images for nothing. Gate the transparent-background
  case anyway. *Export Drawing…* and *Copy as vector* (`Em3dDrawing`, orthographic): draw each visible image with Skia's
  `DrawImage` under the affine map of its face in that view, clipped to the face polygon, in the fill's slot. An image
  sheet seen edge-on contributes nothing. SVG embeds the image as an `<image>` (an export, so the bytes are fine there),
  and PDF must stay vector apart from the image itself.

## 5. `R-em3d101-5` — around the edges

- **a. Copy / paste (brief 95).** An image sheet copies like any object. **The path is made absolute on copy and written
  relative to the target `.c3d` on paste** (§1b's rule against the new document), so pasting into a document in another
  folder keeps a working reference. The layout accepted that limitation and left it to Resolve Path…; 3D does not need to.
  Duplicate and Array carry the image.
- **b. Archive.** `DocumentFileRefs` already finds any string that resolves to a file. Gate that a `.c3d` image inside
  the workspace, and one outside it, are both offered and both repointed (gate 10). Expect no code change. If one is
  needed, the walk has a hole worth recording.
- **c. Instances / hierarchy.** An image sheet in a child cell draws in the parent, its path resolved against the
  **child's** `.c3d`. `C3dHierarchy`'s visitors pass `Image` through untouched.
- **d. Extrude of an image sheet** (D9, recommended: allowed). It extrudes the sheet as any sheet; the new solid has **no
  image**, and the status line says so.
- **e. Booleans.** An image sheet as an operand behaves as a sheet does today. Its image is not drawn while it is inside
  the boolean, as an operand's `Transparency` is not.
- **f. Revision control.** An image inside the workspace is an ordinary file in the history. One outside it is not
  versioned, and `history` has no reason to know. Nothing to build; one sentence in the user doc.

## 6. `R-em3d101-6` — headless: `check`, `render`, the schema

- **`check x.c3d`**: an image whose file is missing or will not decode is a **warning** naming the object and the
  resolved path, not an error. It is drawing-only, and a design must not fail `check` because a photo moved. A
  `Locked` sheet without an image is a warning (the key does nothing there). Reuse a validator the GUI also calls
  (`C3dValidation`) rather than a rule only `check` knows, on `check`'s own terms.
- **`render x.c3d --iso`** draws images through the same `src/Render` path the GUI's vector export uses (§4i), with no
  drawing of its own in the verb (`cli.md` §13.1). Section cuts draw none (a plane cut edge-on has no area), and **field
  plot pictures' context geometry draws none** (D8).
- `DocumentSchema.cs`: the keys of §1j (and §8's in Phase B).

## 7. Phase A gate

1. **Round trip.** A `.c3d` with an image sheet (relative path, `Locked`, `Transparency: 40`, `Model: false`) loads and
   saves byte-identical. A document with no image is byte-identical to before. No `FormatVersion` change.
2. **Placement, one path.** The button, the menu command and a file drop each go through `PlaceImageSheet` (a
   comment-stripped source scan finds `new C3dSheet` with an `Image` in exactly that one function). The same file at the
   same point gives the same document from button and drop. One undo entry each, and a 3-file drop is one undo entry.
3. **Defaults.** The new sheet is `Model == false`, has no material, raises **no** no-material warning, draws its image,
   and is under *Not Modeled* by type. Turning Model on puts it in the solve (`C3dModelled.Filter` keeps it) and raises
   the no-material warning; Make Port on its row works as on any sheet.
4. **Sizing.** Long edge ≈ 25 % of the visible width at the placement depth, aspect kept to within one DBU. An undecodable
   file gives the 4:3 box and a Messages note, not an exception.
5. **Underlay.** A rectangle traced on the image's own plane is drawn over it and is what a click picks (frame-plan
   assertion on the tie, plus the ID pass).
6. **Upload counter.** Moving, hiding, making transparent and Model-toggling an image sheet upload no texture. Placing a
   second sheet with the **same** path uploads none, and Refresh Image uploads one. Assert the counter
   (`FrameCounters`-style), not a time.
7. **Pixels.** An off-screen render of a known 4-quadrant test PNG on an XY sheet, top view, reads the right colour in
   each quadrant, which proves orientation (§1c) and no mirroring. Run it on whichever backend the test host has, as the
   existing read-back gates do, and say which ran.
8. **Shaders.** `tools/ShaderGen --check` is clean, and the generated files carry the new WGSL's hash.
9. **Hierarchy.** An image sheet inside an instanced cell whose `.c3d` is in a different folder from the parent resolves
   against the child and draws.
10. **Archive.** An image inside and an image outside the workspace are both listed and both repointed.
11. **Copy/paste across folders** keeps a resolving path.
12. **Not stale.** Changing an image's path or transparency leaves a solved setup's glyph solved. Toggling Model does
    not.
13. **Locked** refuses Move, Rotate, gizmo, nudge and vertex move with a status sentence, and still allows selection and
    Inspector edits.
14. **CLI.** `check` warns on a missing image file and exits 0 at the default severity. `render x.c3d --iso -o a.svg`
    run **as a process** is byte-identical to the in-process renderer call (the `RenderCliVerbTests` pattern), and
    contains an `<image>`.
15. **Firewall.** `src/Design`, `src/Render`, `src/Cli` gain no Avalonia reference (`Firewall.Tests`, `--no-build`).

---

# Phase B — Images mapped onto faces

## 8. `R-em3d101-8` — the document

```csharp
public sealed class C3dFaceImage
{
    public string Face { get; set; } = "";          // the face's NAME, as C3dObject.FaceNames() spells it
    public C3dImage Image { get; set; } = new();    // Path, as §1b
    public int?  Transparency { get; set; }          // its OWN, independent of the object's; same cap constant
    public double RotationDeg { get; set; }          // in the face's plane, counter-clockwise seen from outside
    public long? Width  { get; set; }                // DBU; null = fitted to the face (§8d)
    public long? Height { get; set; }
    public C3dPoint2 Offset { get; set; }            // the image centre from the face's centre, in the face frame, DBU
    public bool Hidden { get; set; }
}

public abstract class C3dObject
{
    …
    /// brief-em3d-101 — images mapped onto this object's faces, at most one per face. Written only when non-empty.
    public List<C3dFaceImage>? FaceImages { get; set; }
}
```

- **a. On the object, not in a document list.** `C3dFaceBoundary` sits in a document-level list keyed by object name, so
  a rename has to find it and a delete has to cascade to it. A face image stored **on its object** gets rename, delete,
  duplicate, array, group, copy/paste, instance content and undo for free, because each of those already carries the whole
  object. An operation carries face images **for its result**, as it carries `Transparency` and `Hidden`, and the object
  it wraps has none of its own.
- **b. One image per face** (D4). Mapping onto a face that has one **replaces** it, as one undo entry with the old record
  in undo.
- **c. The face frame.** Extend `C3dImageFrame` (§1c) to give **in-plane right/up axes and a centre** for a named flat
  face, below the firewall in `src/Design`. Today's `C3dFaceFrame` (point + normal, in `src/Ui`) has no in-plane axes and
  cannot be reached by `src/Render` or `render`. The rule: seen **from outside the solid**, up is world +Z projected into
  the face; for a face whose normal is ±Z, up is +Y. Right = up × outward normal, so the picture is never mirrored seen
  from outside. The face's centre is its polygon's centroid. A tilted flat face (a polyhedron's) works by the same rule.
- **d. Fitted by default.** With `Width`/`Height` null, the image fits inside the face's bounding rectangle in that frame,
  aspect kept, centred, rotation 0. Setting either makes it explicit (Keep aspect, as §3b). **Fit to Face** clears both and
  the offset.
- **e. Clipped to the face.** The image is drawn **only where the face is**: the face's own triangles, with UVs from the
  image's frame and a sampler that is **transparent outside [0,1]** (clamp-to-border, transparent black, on all three
  backends: Metal `clampToZero`, D3D11 `BORDER` with a zero border colour, Vulkan `CLAMP_TO_BORDER` with
  `FLOAT_TRANSPARENT_BLACK`). A face image larger than its face is cropped by the face, and a smaller one leaves the face's
  own colour around it. No texture memory is spent on the clip.
- **f. Which faces.** Flat faces only. **A curved face (a cylinder's side) is refused** with a reason in the menu's
  tooltip, as *Copy as Sheet* and *Align to Face…* refuse it. A STEP part's `face<n>` is allowed, since its names are
  tied to the recorded hash. A face of an operation's result follows whatever rule `ResultEditRefusal` / face boundaries
  apply to faces of results today. **Do not invent a second rule**; if the face is addressable for a boundary, it is
  addressable for an image.
- **g. A face that no longer resolves** (the object was edited and the face is gone) is **kept in the file, never
  silently dropped**. `check` names it as a warning, its tree row shows the warning glyph, and the Inspector offers
  Remove. That is the `Unread` principle applied to a reference that went stale.
- **h. Drawing only**, exactly as §1h–i: no solver reads it, and it never makes a result stale.

## 9. `R-em3d101-9` — mapping, editing and **removing** one

- **a. Map Image…** in the **Face-mode context menu** (`FaceMenuItems()`), on a single selected flat face of an editable
  object. It opens the same picker as §2a and maps the chosen file fitted (§8d). On a face that already has an image the
  item reads **Replace Image…**. On a result's face it follows the D13 disabled-with-reason pattern beside it, and on a
  curved face it is disabled with §8f's reason. Not offered on an instance's face: the image belongs to the cell's own
  `.c3d` (*"Open the cell to map an image onto its faces."*).
- **b. Removing a face image: three ways in, one function.** `RemoveFaceImages(IReadOnlyList<(C3dObject, string face)>)`
  is the only writer that removes one, one undo entry per gesture:
  1. **The face's context menu: Remove Image**, shown when the selected face has one (beside *Replace Image…*).
  2. **The Properties Inspector: a Remove Image button** in the face image's section (§9d), whether that section came up
     by selecting the image's tree row, clicking the image in the view, or selecting its face in Face mode.
  3. **Its tree row**: the row's context menu **Remove Image**, and **Delete** with the row selected. A multi-row
     selection removes them all as one entry.
  Removing the object removes its face images with it (§8a), with no separate step.
- **c. The tree: beneath its object**, as EM face boundaries are (`EmBoundaryKind`). A new kind, `FaceImageKind =
  "Image"`, shows the row as *"Image on zmax — die.png"* with the image glyph. Its tick is its `Hidden` (saved, one undo
  entry), and it is wired into **`SetRowsVisible`** as one more kind with its own writer, **not** a second visibility path,
  so the H key, Hide all / Show all and the row tick all work. `ListedRows()` includes it the way it includes EM boundary
  rows. The tree filter's *Image sheet* entry (§3a) gains a sibling, *Face images*.
- **d. The Inspector, a face image's section**: File (Browse… / Reveal), Pixels, **Transparency** (its own slider + box
  + Default, built from the same `C3dTransparency.Max` constant, **not** a copy of brief 92's row logic), **Rotation**
  (degrees, a number, plus ⟲ 90° / ⟳ 90° buttons), **Width / Height** with Keep aspect, **Offset** (right, up), **Fit to
  Face**, **Hidden**, and **Remove Image**. Each edit is one undo entry and a drag of the transparency slider is one entry,
  as brief 92's is. When a **face** is selected in Face mode and has an image, the Inspector shows this section below the
  face's own readout.
- **e. Clicking it in the view** selects the image's record and its row, as clicking a boundary tint does (brief 90), and
  its menu is the record's: **Select Face** (what B also reaches), Replace Image…, Remove Image, Hide, Properties. B steps
  to the face beneath, as for a tint.
- **f. On-canvas manipulation** (dragging the image across its face, rotate handles) is **owner decision D7**, recommended
  **not** in this brief: the Inspector's numbers, Fit to Face and the 90° buttons cover tracing and alignment, and an
  in-face drag tool is a gesture of its own worth briefing separately.

## 10. `R-em3d101-10` — drawing it

- One textured draw per face image, from the face's own triangles (§8e) re-emitted into the image vertex stream with
  UVs. Same pipeline, same textures (keyed by path, so a photo on a sheet and on a face uploads once), same exports as §4.
- **Depth:** a face image wins over the face it lies on and loses to a port's surface or a field plot on that face. Add
  the step in `Scene3DDepthTie`'s order, with the enum's own comment updated so the precedence stays documented in one
  place.
- Its transparency is its own (§8). The object's transparency does **not** multiply onto it, which is the owner's
  requirement: a lid at 80 % can carry a marking at 0 %. An instance's transparency does multiply onto it, as brief 92
  multiplies an instance onto its parts.
- A hidden object hides its face images. A hidden face image leaves its face drawn as before.
- Exports and `render --iso` per §4i and §6.

## 11. Phase B gate

1. **Round trip** of an object with two face images (one with explicit size, rotation and offset, one fitted), an operation
   carrying one, and an image on a face that no longer resolves (kept, byte-identical).
2. **Frame.** A 4-quadrant PNG on each of a box's six faces, rendered from outside each face, reads unmirrored and upright
   by §8c's rule, and so does a tilted polyhedron face.
3. **Clip.** An image twice the face's size draws only within the face (pixel read-back), and a half-size one leaves the
   face's colour around it.
4. **Transparency independence.** An object at 80 % with its face image at 0 % reads the image opaque, and the reverse
   too.
5. **Removal, all three ways:** the face menu's Remove Image, the Inspector's button and the tree row's Delete each call
   `RemoveFaceImages` (source scan), and each is one undo entry that undo restores exactly. Deleting the object takes its
   face images with it and undo brings them back.
6. **Replace** on a face with an image is one undo entry, and undo restores the old one.
7. **Refusals.** A cylinder's side, an instance's face and (per the existing rule) a result's face each show Map Image
   disabled with a reason.
8. **Visibility** through `SetRowsVisible`: the row tick, H and Hide all each hide a face image, saved, one undo entry.
9. **Carried for free:** rename, duplicate, array and copy/paste across folders keep the face images with working paths.
10. **CLI:** `check` warns on an unresolvable face and a missing file. The `render --iso` process-vs-in-process byte
    identity holds with face images present.

Run only the classes touched, with `--filter`; `Firewall.Tests` with `--no-build`.

## 12. Decisions for the owner

All are built as recommended unless the owner says otherwise.

- **D1 Path storage.** *Recommended:* reference the file where it is, relative to the `.c3d` inside the workspace and
  absolute outside it (the layout's rule, and what the request asks for). *Alternative:* copy it into the cell's `3d/`
  folder as STEP import does, which travels with the cell but duplicates a large photo per cell.
- **D2 An image sheet's transparency is the object's** (brief 92's row), one knob. A face image has its own (the
  request asks for that).
- **D3 Locked**, on image sheets only. *Recommended:* yes; the layout has it for exactly this tracing use.
- **D4 One image per face**, replaced by a second Map. *Recommended:* yes. Layering several on one face has no use case
  yet.
- **D6 Shift-drop onto a flat face maps the image onto it.** *Recommended:* yes. A plain drop always makes a sheet, so
  the plain gesture is predictable.
- **D7 On-canvas drag/rotate of a face image.** *Recommended:* a later brief. Numbers and 90° buttons here.
- **D8 Field-plot and section pictures draw no images.** *Recommended:* yes; a field picture's context geometry is
  deliberately plain.
- **D9 Extrude of an image sheet** is allowed, and the solid has no image. *Recommended:* yes, as "any other sheet".
- **D10 Texture cap 4096 px**, one constant. *Recommended:* yes.

## Docs

`drawing-in-3d.md`: a "Reference images" section (placing, tracing with *Drawing Plane from Image*, Locked, Model off by
default and how to turn it on, mapping onto a face, removing one, where the file must live for an archive to carry it).
`cli.md`: `check`'s warnings, and `render` drawing images. Edit doc sources only; DocGen runs at the end of the series.

## On completion

`src/Ui/Viewer3D/RESOLVED.md` (the texture path on each backend, the Vulkan descriptor lifetime, the unorm-not-sRGB
choice, the upload counter), `src/Render/RESOLVED.md` (`Underlay`, the face-frame rule, clip-by-border) and
`src/Design/RESOLVED.md` (why face images live on the object). Never CLAUDE.md. Do not commit unless the owner asks.
