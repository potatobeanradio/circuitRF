# `src/Ui/Viewer3D` — resolved findings

Findings from work on the 3D viewer's view model. The field model itself (readers, surfaces, samplers) is recorded in
`src/Render/RESOLVED.md`.

## A right-click on the selection selected what stood in front of it (2026-11-05)

A selected face is drawn again with the depth test off (`Scene3DPipeline.OnTop`) and a selected object's outline likewise, so the
selection can be seen through what covers it. The ID pass keeps only the front fragment, so `HoveredItem` under that highlight was the
covering face, and `OpenContextMenu`'s test of the hovered item against the selection failed and selected it. It happened only where something
overlapped the selection on screen. `SelectionUnderCursor` now asks the CPU ray `B` uses (`HitsAlongRay`, shared with `Cycle`, air-box faces
included) whether anything selected lies along the cursor's line of sight at any depth. The rule is deliberately the same in Object mode,
so a large selected solid keeps the menu over anything in front of it until a left-click selects something else. Left-click is unchanged.

## brief-em3d-82 — Plot Field on a face (2026-09-28)

- **The EM path now counts its builds.** `FieldGeometryBuilds` was temperature's alone; the EM branch of
  `ScheduleFieldGeometry` now increments it too, which is what gate 6 holds (100 phase steps with a painted face: no build,
  the same `FieldGeometry.Version`). A phase step was already uniforms-only; nothing about that changed.
- **Painted faces are resolved against the scene on the UI thread** (`FieldFaceTargets`: triangles, normal, role, tolerance)
  and painted off it. The painter keeps each region boundary it cuts for the next face of the same region within a build.
- **The list clears on a new run** (`RefreshFields`' not-the-same branch) **and on a solution whose volume mesh has a
  different node or cell count** (the other solver's, a re-meshed run) — the brief's "the run or the solution changes shape".
  Faces are scene faces, so a new solution on the same mesh keeps them.
- **A refused face never changes the quantity** (owner, Q2: keep the quantity). The sentence is appended to `FieldText`, after
  the triangle count, one per face.
- **The hover readout re-reads where the paint was read**: a boundary quantity through the boundary sampler, openEMS through
  `FieldFaces.SampleOffFace` (half a cell off), Palace a hair (1e-6 of the scene's diagonal) off the face along
  `FieldFacePaint.ReadToward` — without the hair, a point ON a sheet is located in whichever tetrahedron the bucket lists
  first, which is either side.
- **Plot Field is not refused on a STALE EM result**, unlike Plot Temperature: an EM field from an older run is still drawn
  (R-em3d49-5b — the banner says so and the picture is the solver's own geometry), so the item follows the field, not the
  banner.
- **Test cross-talk, not a regression:** `OpenEmsBackendTests.Gate10` counts the pids its `OpenEmsRun.ProcessStarted` handler
  sees. The event is static, so when another class's openEMS run (`FieldTests.Gate9`) runs concurrently in the same process
  the list holds three pids and `Assert.Single` fails. It passes alone.

## Reference images: the texture path on each backend (brief-em3d-101, 2026-10-03)

- **The 3D view had no texture path at all**; this brief added one, on all three APIs, for image sheets and images on faces.
  An image draw is its own pipeline (`Scene3DPipeline.Image` / `ImageTranslucent`), its own vertex stream
  (`Scene3DImageVertex`, 32 bytes: position, uv, id, face, colour — `Scene3DVertex`'s 24-byte stride is untouched) and its own
  shader pair (`vs_image` / `fs_image`). Picking never samples: the ID pass draws the object's ordinary triangles with
  `fs_pick`, so a fully transparent texel still picks (the brief's `fs_pick_image` was not needed and was not written).
- **UNORM, never sRGB.** Every backend creates `RGBA8 UNORM` (Metal 70, `R8G8B8A8_UNorm`, `VK_FORMAT_R8G8B8A8_UNORM`), so a
  texel reaches the framebuffer with the value a vertex colour of that value would — brief 69's sRGB trap. Levels are
  unpremultiplied RGBA, rows top first, built ONCE on the CPU (`Scene3DTextures.Reduce`, SkiaSharp Mitchell): no backend
  generates mips, so the three pictures stay alike. The long edge is capped at `C3dImages.MaxTexturePixels` (4096) first.
- **Uploads are keyed by identity, never per frame or per re-elaboration** (`Scene3DTextureResidency<T>`, `src/Render`).
  `Scene3DTextures.Get(path)` hands out one object per path until `Refresh(path)` (Refresh Image, Resolve Path…, Browse…),
  so a scene rebuilt for a move, a hide, a transparency or a Model toggle holds the SAME object and uploads nothing; the
  residency releases what no scene holds. `Uploads` is the counter gate 6 asserts. Every broken file is the one
  `Scene3DTextures.Placeholder` object, so they share one upload.
- **Metal**: `texture2DDescriptorWithPixelFormat:…mipmapped:` with `mipmapLevelCount` set to the CPU chain's length,
  `replaceRegion:mipmapLevel:withBytes:bytesPerRow:` per level (the `MTLRegion` struct goes by value through
  `objc_msgSend`), one `MTLSamplerState` (linear/linear, mip linear, clamp to edge), `setFragmentTexture:atIndex:0` and
  `setFragmentSamplerState:atIndex:0` before each image draw. Verified on this Mac (the gates' Metal read-backs).
- **D3D11**: an `Immutable` `ID3D11Texture2D` created with every level as `SubresourceData` (each level's array pinned for the
  call), a shader-resource view, one `SamplerDescription(MinMagMipLinear, Clamp)` at s0, `PSSetShaderResource(0, …)` per image
  draw. **The rasterizer states are indexed by `tie − Scene3DFramePlan.TieMin`** now that `Underlay` (−2) and `FaceImage`
  exist — the old `+ 1` would have read off the front of the array. Compiled here; NOT run on Windows.
- **Vulkan — the per-texture descriptors**: set 1 is its own layout (sampled image at binding 0, sampler at binding 1; set 0
  is unchanged and still written once). **Each texture owns a descriptor pool of exactly one set**, created in
  `UploadTexture` beside its image, view and memory, and destroyed in `ReleaseTexture` with them — destroying the pool frees
  the set. There is no shared pool to size, so a re-elaboration (or the hundredth) cannot exhaust one; release waits for
  the device idle first (a frame in flight may still sample it), which happens only when a scene drops an image, never per
  frame. The upload stages every level in one buffer and copies level by level between two layout transitions (undefined →
  transfer destination → shader read-only). Compiled here; NOT run on Linux.
- **HLSL samplers.** naga writes samplers through a D3D12 sampler heap that D3D11 cannot compile; ShaderGen rewrites it to a
  plain `SamplerState : register(s0)` and refuses to generate if the spelling changes (`tools/ShaderGen/README.md`).
- **A face image is clipped in the SHADER, not by a border sampler.** The brief proposed clamp-to-border with transparent
  black on each API; `fs_image` instead discards outside uv [0, 1], which every backend runs identically, needs no border
  colour (Metal's `clampToZero` is the odd one out) and spends no texture memory either. The one sampler is clamp-to-edge.
- **No nudges exist in the 3D editor**, so Locked has none to refuse; it refuses Move, Rotate, the quarter turns, Mirror,
  Align (as a mover — a locked image may be the reference), the gizmo and a vertex move. Duplicate is allowed (the copy moves).

## brief-em3d-101 follow-up: Vulkan texture upload on failure (2026-10-03)

- **`UploadTexture` released nothing if it failed part-way.** On out of device memory with a large photo, the staging buffer, the
  image and its memory were left behind. The staging buffer is now freed in a `finally`, and an upload that does not complete
  releases what it made through `ReleaseTexture`. The image's memory is recorded before it is bound, so a failed bind frees it.
  `NewBuffer` frees its buffer when the memory allocation fails, and `OneShot` frees its command buffer on any failure. Compiled
  only: no Vulkan device has run it.

## The shade stream: upload and binding (brief-em3d-104, 2026-10-04)

- **Lazy, by `Viewer3DSession.ShadeStream`** (brief 106 sets it). Off, nothing is uploaded or held: a scene costs exactly
  vertices + indices + lines, as before. On, the next frame uploads the whole stream once (16 bytes a vertex); a later scene is
  PATCHED when the backend held the previous scene's stream and the scene itself was patched, else uploaded whole. Off again,
  the next frame releases the buffer (Metal also drops any staged patch aimed at it).
- **Shade ranges are listed apart** (`Scene3DPatch.ShadeRanges`), compared object by object as the vertices are, so the default
  view's patch is byte-for-byte what it was. A recolour or a translation lists none (normals unchanged); a tilt lists the object.
- **Buffer mapping** (also at the top of `scene.wgsl` and in `tools/ShaderGen/README.md`): attributes `@location(4)` normal and
  `@location(5)` slot; Metal vertex buffer index **3** (0 geometry, 1 uniforms, 2 per-draw transform share one argument table),
  D3D11 input slot **1** (semantics LOC4, LOC5), Vulkan vertex binding **1**. No pipeline declares it yet; 106's do. The WGSL
  edit is comment-only: the regenerated shaders differ only in their hash line.
- **Gate 11 ran on Metal**: the CaseA scene drawn with and without the duplicates reads back identical bytes. D3D11 and Vulkan's
  upload/patch/release are compiled only.

## The realistic view: bindings, formats, what ran (brief-em3d-106, 2026-10-04)

- **Binding slots, per backend** (also in `scene.wgsl`'s realistic section and `tools/ShaderGen/README.md`):

  | What | WGSL | Metal | D3D11 | Vulkan |
  |---|---|---|---|---|
  | scene vertex | `@location(0..3)` | vertex buffer 0 | input slot 0 | vertex binding 0 |
  | shade stream (brief 104) | `@location(4)` normal, `@location(5)` slot | vertex buffer **3** | input slot **1** | vertex binding **1** |
  | uniforms `U` (now 2,304 B) | group 0 binding 0 | `[[buffer(1)]]`, inline bytes | `b0` | set 0 binding 0, dynamic |
  | per-draw transform | group 0 binding 1 | `[[buffer(2)]]` | `b1` | set 0 binding 1, dynamic |
  | appearance table (12,288 B) | group 0 binding 2 | fragment `[[buffer(4)]]`, an `MTLBuffer` (over the 4 KB inline limit) | `b2` | set 0 binding **2**, a plain uniform |
  | environment map / sampler / split-sum table | group 2 bindings 0/1/2 | `[[texture(1)]]`, `[[sampler(1)]]`, `[[texture(2)]]` | `t1`, `s1`, `t2` | set **2**, bindings 0/1/2 |

  The environment's sampler is the image sampler brief 101 made (linear, linear between mips, clamp), bound a second time
  at index 1. Vulkan's pipeline layout grew to three set layouts; binding set 0 again for a transform leaves set 2 bound,
  so the realistic draws bind set 2 once a frame. Vulkan keeps the 12 KB appearance buffer for the device's life (set 0
  names it, so its descriptor is always valid); Metal and D3D11 release theirs with the environment.
- **Texture format: RGBA16F on all three** (`MTLPixelFormatRGBA16Float` 115, `R16G16B16A16_FLOAT`, `R16G16B16A16_SFLOAT`),
  so the RGBE-in-RGBA8 fallback the brief allowed was not needed: every Metal device, D3D11 at feature level 10+ and every
  Vulkan device (a required format) samples it with linear filtering. The upload path took a format argument: Metal's
  `NewSampledTexture`, D3D11's `NewSampledTexture`, Vulkan's `NewSampledImage` (brief 101's images now go through it with
  RGBA8).
- **PbrTranslucent blends PREMULTIPLIED** (RGB one, 1 − src-alpha; alpha one, 1 − src-alpha): a reflection on glass then
  adds on top of what shows through instead of being capped at the coverage. The swap image's alpha still ends at 1.
- **The session decides the uploads** (`Viewer3DSession`): with `ShadeStream` on, the appearance table when its ROWS change
  (a rebuilt scene with the same looks uploads nothing) and the environment when it is a different object; with it off,
  `ReleaseEnvironment` and `ReleaseShade`. A rotation, exposure or intensity is the uniform look block and uploads nothing
  (gate 7 counts it).
- **Which backends ran**: **Metal** — the gate's offscreen frames (a mirror sphere against the CPU reference, within 3/255;
  the theme background; alpha 255 everywhere; a rough dielectric with no peak) and three studio pictures looked at by eye.
  **D3D11 and Vulkan were compiled only; their runtime is unverified on this machine**, as briefs 62 and 101 recorded.
  The HLSL is naga's (validated) and was not put through `d3dcompiler_47` here.
- **The mirror gate had to look at a smooth patch of the studio.** Behind the default camera, the unrotated Studio puts the
  fill softbox's soft edge exactly where a mirror sphere's centre reflects: 1° of normal moves the colour ~15/255, and the
  GPU's interpolated normal is a fraction of a degree off the exact one, so the first run read 238 against 226. A uniform
  environment matched to 0.1/255. The gate now turns the studio 90° and asserts the environment varies under 2/255 within
  ~1° of the reflected direction before comparing.

## Shadows, contact shading, the ground, the realistic picture (brief-em3d-107, 2026-10-04)

- **The shadow map's invalidation rule.** The map lives on the BACKEND between frames and is re-rendered only when
  `Scene3DFramePlan.ShadowKey` moves. The plan lists the casters every realistic frame (cheap: no GPU work) and hashes what
  the map depends on: the scene object and its generation (a new generation or a patch is a new scene), the light's matrix
  (the key's direction — so the Look's rotation — and the casters' bounds, so visibility), the map's size, and every caster
  draw with the contents of any per-frame transform slot it is drawn under (a drag's preview). Nothing about the camera is
  in it, so an orbit renders no shadow pass. **The session decides** (`Viewer3DSession.DecideShadowPass`, and its
  `ShadowPasses` counter): the pass runs when the key differs from the key the map was last rendered with; releasing the
  realistic view's lighting resets it. A picture plans a 4096² map (live is 2048²), so the next live frame after an export
  renders the map once more at its own size. Element casters are never LOD-boxed — the boxing depends on the eye.
- **The transparent picture's read-back is PREMULTIPLIED.** Only `RenderPixels` with `Scene3DFramePlan.Transparent` clears to
  (0, 0, 0, 0) and skips the backdrop; with the blend every pipeline already uses (colour src-alpha or one / 1 − src-alpha,
  alpha one / 1 − src-alpha) the pixels come back premultiplied with real alpha. `FieldPictureShot.Transparent` carries that
  through Compose (which paints legends on a premultiplied bitmap, so it stays right) and `Png()` straightens it in C#
  (`PictureResample.Unpremultiply`, rounded) and encodes it as UNPREMUL so Skia converts nothing. The live image is never
  cleared to 0: its alpha stays 1 (brief 27's rule). Copy never asks for transparency.
- **Supersampling** happens in `CapturePicture`: the plan is drawn at k × the output (k from 1, 2, 4, halved until the drawn
  side fits `FieldPicture.MaxSide`) and brought down by `PictureResample.Downsample` before the legends are painted, so the
  legend, caption and overlay layer are composed at the output size exactly as before.
- **Bindings** (also in `scene.wgsl` and `tools/ShaderGen/README.md`): group 3 — the shadow map, its
  comparison sampler, the blurred occlusion, the prepass's depths, the raw occlusion — is Metal fragment textures 3, 4, 5, 6
  and sampler 2; D3D11 t3, s2, t4, t5, t6; Vulkan set 3, bindings 0–4. The HLSL comparison sampler comes out of naga's
  COMPARISON sampler heap, which `plain_hlsl_samplers` now rewrites too (`SamplerComparisonState smap_s : register(s2)`).
- **Per backend.** Metal: three extra encoders (the shadow pass, the prepass into R32Float with the main depth texture
  reused, then the horizon pass and blur into two R8 targets), 1 × 1 stand-ins for an absent map or occlusion. D3D11: the
  map is R32_TYPELESS (a D32 view to draw, an R32 float view to read); t3..t6 are unbound before every pass that draws into
  one of them, since the runtime would otherwise unbind the input itself and the next pass would read nothing. Vulkan: three
  render passes that leave their targets SHADER_READ_ONLY_OPTIMAL, the prepass with its own depth buffer (no per-image
  framebuffer), set 3 always naming valid images (stand-ins made with the device and transitioned at once), and every map or
  target made, and set 3 re-pointed, BEFORE the command buffer is begun (a set may not change under a recording that binds
  it). The comparison sampler is linear only where the device filters D32 linearly (`_depthLinear`), else nearest.
- **What ran.** Metal: every brief-107 pixel gate (a box's shadow on its plate 30/255 darker than its mirror, gone with
  `Shadows: false`; glass at 0.9 casts nothing and 0.2 casts 30/255; an inside corner 21/255 darker, equal without occlusion;
  a field pixel in shadow and in a corner exactly (51, 102, 153); a transparent picture 0 / 40 / 255 alpha; a 50 % slab's
  straightened colour composited over black and white within 1/255). **D3D11 and Vulkan were compiled only; their runtime
  is unverified on this machine**, as briefs 62, 101 and 106 recorded.

### Brief 107 review fixes (2026-10-04)

- **The section plane is in the shadow key.** `fs_depth` discards what the section plane cuts away, so the map depends on the
  clip state; the key had left it out, and a moved (or toggled) section kept casting the cut-away part's shadow. `KeyOf` now
  hashes `Clip.Enabled` and, while on, its equation. Moving the plane while the section is OFF still renders nothing.
- **A picture's occlusion reaches as far in the world as the view's.** The kernel's radius in pixels is capped
  (`Occlusion.MaxPixels`, 64 WINDOW pixels). A picture drawn at 2 × the window and supersampled 2 × hit that cap at a quarter of
  the view's world reach, so exported contact shading came out visibly tighter than what the view showed. `Plan(pixelScale:)`
  (CapturePicture passes scale × supersample) writes `ao.w`, and the shader's cap is `AO_MAX_PIXELS × ao.w`.
- **Supersampling is Export Picture's only.** `CapturePicture(supersample:)` defaults to 1; the editor's Export passes
  `ExportSupersample`, and Copy (already 4 × the window) draws once.
- **A refused picture allocation is a reported fault, not a crash.** On D3D11 a texture the device refuses throws
  `SharpGenException`, which `CapturePicture`'s handler does not catch; `RenderPixels` now turns it into a
  `Viewer3DPresentFault`. Every D3D11 reallocation (`EnsureDepth`, the map, the occlusion targets) nulls the old views BEFORE
  making the new ones, and Vulkan re-points set 3 at the stand-ins before allocating, so a failed allocation never leaves a
  disposed view behind to be bound by the next frame.
- **The stand-ins are cleared** (depth 1, occlusion 1) on all three backends rather than left with undefined contents: a
  stand-in read with its uniform on would otherwise have shadowed everything.
- **The HLSL is compiled on Windows by a test** (`HlslCompileTests`, entry points read from the backend's own `Compile` calls;
  no GPU needed). The D3D11 backend compiles every entry point from one file at construction, so one construct FXC rejects
  anywhere takes the whole 3D view down on Windows, not just the feature that added it. Elsewhere the test holds only that every
  entry point named exists. Vulkan's occlusion targets are sized from the swap image, not the plan, on purpose: its main pass
  draws at the image's size, and `fs_pbr` loads the occlusion at its fragment's pixel.

## Editing appearance live: the display-only reload, and previews by restyle (brief-em3d-108, 2026-10-04)

- **The display-only reload.** A technology change used to re-elaborate every open `.c3d` (`OnChildChanged` → `Invalidate`).
  `C3dEditorViewModel.OnTechnologyChanged(path)` now compares the technology the adopted scene was elaborated with against the one
  the cache hands back, in **canonical physics form** (`C3dRunDocument.PhysicsText`: the file's own materials and every library
  material with `Source`, `Color` and `Appearance` cleared, plus the library thermal interfaces). Equal: the KEPT elaboration is
  re-assembled into a scene (`Build` was split; its second half is `Assemble`), handed to the view through
  `Viewer3DViewModel.RegenerateWith`. The builder runs, so a changed `Color` recolours vertices and the session patches exactly
  those objects' vertex and edge-line bytes; every primitive is a tessellation-cache hit and the elaborator is not called. Any
  physical change, a build already in flight, a pushed-in frame, a boolean's or fillet's preview, or a name-drag preview takes
  today's path. Results stay current either way: the run manifest hashes the technology in the same form (brief 105 §5).
- **A cue per technology.** A library two technologies name raises one `TechnologyChanged` per technology. The second cue arrives
  while the first display rebuild is in flight; it is measured against THAT rebuild (`_pendingDisplay`), not refused for the build
  in flight, or the first library edit in a workspace with two technologies would have re-elaborated after all. A technology the
  elaboration never read (not in `FilesRead`, not one of its technologies) is ignored — unless the design has a refusal or no
  technology, where the changed file may be the one it was waiting for.
- **A live technology is no longer mutated in place.** `TechnologyCache.LibraryChanged` used to re-resolve an open technology
  editor's live override IN PLACE, so the scene's `Elaboration.Technology` and the cache's answer were the same object and "did
  anything physical change" could not be asked. It now resolves a clone and replaces the entry (the editor already installed deep
  copies through `SetLive`).
- **`ObjectsElaborated` is the wrong counter for "was it elaborated".** An εr edit re-elaborates with every object a cache hit, so
  the count does not move. `C3dEditorViewModel.Elaborations` counts elaborator runs; the gates use it.
- **Previews are restyles, not rebuilds.** The Materials dialog's unsaved edit (`PreviewAppearance(material, appearance)`, pushed
  by the workspace to every open view on that technology or naming that library) and the Inspector's slider
  (`PreviewObjectAppearances(map)`) both go through `Scene3DLooks.Restyle`: the adopted scene copied with the looks asked again, a
  new table and the shade slots of objects whose row moved. The view keeps the adopted scene; ending a preview puts it back by
  reference, so Cancel's table is byte for byte. A scene adopted while a preview stands is restyled too; OK and a slider's
  release end the preview at the next scene built after the write (`EndAppearancePreviewAtNextScene`), so the view never flashes
  back to the old look in between.
- **A restyled scene keeps its generation, and caches of geometry compare `Scene.Geometry`.** The FDTD grid and the field and
  temperature surface builds compared the scene by reference; a restyle mid-drag would have thrown each away. `Scene3DModel.
  Geometry` is the scene a restyle came from, and those five comparisons use it. The frame plan's per-scene classification (which
  material is translucent) and the shadow key compare by reference on purpose: a Transmission preview changes both.
- **The Look panel's drags** preview through `C3dEditorViewModel.PreviewLook` (the view's `LookSource` reads the preview first) and
  write one `C3dRecordsEdit` of the Look's text alone on release, as the field plots' edits do — not through `ChangeRecords`, whose
  `RecordsChanged` regenerates the scene. `Viewer3DViewModel.LookWrites` counts the uniform writes.

## Field plots in the realistic view: the two field pipelines and the normal stream (brief-em3d-109, 2026-10-04)

- **Two pipelines, appended to `Scene3DPipeline`.** `FieldBlend` is `fs_field` blended (straight alpha, the translucent blend), used
  for Exact or Glow below 100 %; `FieldLit` is `vs_field_lit`/`fs_field_lit`, always blended (at 100 % its alpha is 1 and the blend
  changes nothing). Both write depth. The default view and Exact at 100 % keep the old `Field` pipeline untouched.
- **The Lit field's normal stream binds where the shade stream does**: Metal vertex buffer 3, D3D11 input slot 1 (semantic LOC3),
  Vulkan vertex binding 1, 12 bytes a vertex (`FieldShading.Stride`). Vulkan used to leave the shade stream bound at binding 1 for the
  frame on the grounds that nothing else read it; it now tracks which of the two binding 1 holds. `UploadFieldNormals([])` releases.
  A Lit draw also needs the environment (its sheen samples the prefiltered map and the split-sum table), so it is skipped, as a
  `Pbr` draw is, until that is uploaded.
- **The indicator in the live view** is drawn by `Viewer3DOverlay` under the legend stack (`Legends` now returns where the stack ends)
  or alone at its top, whether or not the legend is shown; a picture's is `FieldPicture`'s, from `FieldPictureShot.Indicator`, which
  `CapturePicture` always fills from `Viewer3DViewModel.FieldIndicator` — not from the legend or caption options.
- **Not seen**: the live overlay's label was not looked at (no window here); its gate is the view model's property under every
  legend/caption combination, and the exported picture's pixels.

## Fit, keyboard navigation and label placement (general designer feedback round 14, 2026-10-08)

A designer on a Windows laptop with a trackpad could not frame the Die to Heatsink example or get closer to it, and its probe
labels covered one another in the Front and Side views.

- **View ▸ Zoom to Fit (and Ctrl/Cmd+0, the gesture it shows) had no 3D case.** `WorkspaceViewModel.ZoomToFit` dispatched to
  the schematic, layout, symbol and wBond documents and told a 3D view "no document is focused" in the Messages panel — a
  message nobody reads while looking at the canvas. It now calls `ThreeDFit`, the 3D menu's own Fit. The window binds no
  KeyBinding for Ctrl+0 on Windows/Linux (the MenuItem's `InputGesture` is display-only), so the pane answers Ctrl/Cmd+0 itself
  too.
- **Fit framed the bounding SPHERE, which ignores the direction.** `Camera3D.FitBounds` sets the distance from the box's
  half-diagonal, so a 12 × 10 × 1.8 mm stack seen edge-on (Front) filled about a third of the width and Fit looked like it did
  nothing. `Camera3D.FrameBounds` projects the eight corners onto the current right/up/back axes and solves for the distance
  at which every corner sits inside both half-angles (perspective) or the half-height (orthographic), FitMargin to spare. Yaw,
  pitch and projection are kept. **Only the interactive Fit uses it** (Home, the toolbar, 3D ▸ Fit, Zoom to Fit, the standard
  view keys, which Fit after turning): the FIRST view of a scene and every headless picture (`render`, glTF's camera) still
  call `Camera3D.Fit`/`FitBounds`, so no CLI output or committed picture moved. A box's corners are symmetric about its centre,
  so the projected extent is too and the target stays the box centre.
- **`EditorRound5TreeTests.Fit_FramesOnlyTheVisibleObjects…` asserted `< withoutBox / 2`**, a ratio of sphere radii. The tight
  fit's ratio for that geometry in iso is ~1.8 (height binds), so it now reads `/ 1.5`; the claim it holds (the hidden box is
  no longer framed) is carried by the target's x, which is unchanged.
- **The keyboard camera** (`Viewer3DViewModel.CameraKey`): Ctrl/Cmd +/− zoom one wheel notch about the centre; bare arrows pan
  by `CanvasArrowPan`'s screen step, the VIEW moving the arrow's way as on every 2D canvas; Shift+arrows orbit 15°, the CAMERA
  travelling the arrow's way (Up rises over the top). The 2D canvases spend Shift on a coarse pan; a 3D view has a third motion
  to give it. Nothing in the 3D views nudges a selection with an arrow, so the arrows pan whatever is selected. **The editor
  view forwards an unused arrow to the pane only from a toolbar button** — an arrow the object tree leaves unhandled at its
  first or last row would otherwise pan the view.
- **Labels** (`Viewer3DLabelLayout`): greedy, top of the screen first, each label trying the four corners round its point and
  then sliding a label-height at a time; the points are obstacles too, except a point at the label's OWN place — two probes on
  one face share it exactly, and treating the twin's marker as an obstacle blocked every position beside it. A label that
  moved away from its point gets a leader line.
- **A face probe's marker was at its SOLID's centre** (`p.Face` fell through to the solid branch), so die/zmax sat inside the
  die and both flange/zmin probes at the flange's middle. It is now the face's area-weighted centre (`FaceCentre`, from
  `Scene3DFaces.Triangles`).

## A selected sphere: bands in the realistic view, and its silhouette outline (2026-10-09)

- **The bands were the sphere's own far side.** An object selected in Object mode is drawn faded with the translucent ones —
  depth test, NO depth write, cull none on every backend — so its far-side triangles blend over its near side in INDEX order.
  A sphere's tessellation runs in latitude rings, so at some angles whole rings of the far side land on top. `fs_color` shades a
  back face almost as it shades the front, which hides it in the default view; `pbr()` flips a back face's normal and shades
  the inside, lit and shadowed differently, so the realistic view showed it as horizontal stripes. **Fix:** a shade-slot bit,
  `Scene3DShadeVertex.Closed` (0x200, every kind but Sheet/Port/Boundary), and `pbr()` discards a back face of a closed,
  Object-mode-selected object while the clip is off (with the clip on the far side is the cut's cap and must stay). A sheet
  keeps both sides — seen from behind, its back IS its visible surface. A non-convex solid's near side can still overlap
  itself and blend in index order; nothing reported, so not addressed.
- **A curved surface had no selection outline**: the outline is feature edges (face boundaries), and a sphere has none. Its
  SILHOUETTE is drawn by the overlay (`Scene3DSilhouette` in `src/Render`, `Viewer3DOverlay.Silhouette`), in both views:
  triangle edges INSIDE one face (vertex Face ids equal — so a box's edges, already drawn by the GPU, are never drawn twice)
  whose two triangles face opposite ways from the eye. Adjacency is built once per (scene, object), welding by position so a
  seam's duplicated vertices join; past 200,000 triangles nothing is built. On screen it is `2 / RenderScaling` DIPs, the
  GPU outline's two device pixels; it follows a drag preview's copies like the feature edges.

## Linux, AMD: the 3D view drawn as horizontal streaks (issue #20, 2026-10-28)

- **Symptom:** on an AMD integrated GPU (Mesa: RADV for Vulkan, radeonsi for GL, X11) every solid in the 3D view came out
  as full-width horizontal bands of its colour, while the labels and outlines Avalonia draws on top were correct.
- **Cause — the hand-off, not the drawing.** The compositor on X11 is GL (Avalonia's default, GLX then software; circuitRF
  never selects its Vulkan mode). Avalonia 12.0.3's `ExternalObjectsOpenGlExtensionFeature.ImportImage` goes straight from
  `glImportMemoryFdEXT` to `glTexStorageMem2DEXT` and never sets `GL_DEDICATED_MEMORY_OBJECT_EXT`. For a memory object not
  marked dedicated, radeonsi ignores the buffer's tiling metadata and assumes a linear layout (`si_texture.c`, by design).
  The backend allocated the image dedicated and OPTIMAL, so the compositor read tiled pixels as rows.
- **Fix (`VulkanViewer3DBackend._linearShared`):** on an AMD device (vendor 0x1002) whose RGBA8 linear tiling can be
  drawn into, blended, sampled and copied, the images the compositor imports are created LINEAR. Depth, pick, offscreen
  and export images stay optimal. Both drivers compute a linear image's pitch with the same surface code, which is what
  makes the two readings agree.
- **Not verified on hardware here** — macOS cannot run the Vulkan half; the reporter's run is the check. Intel (iris)
  takes a different guess for an undedicated object and is untested either way.
- **If X11's Vulkan compositor is ever enabled**, revisit this: Avalonia's Vulkan importer re-creates the image OPTIMAL,
  so a linear export would then be the mismatch. An Avalonia that marks the memory object dedicated makes the switch
  unnecessary.

## A label whose point is off screen stays off screen (2026-11-02)

- **Symptom:** a port's or a probe's name label whose point had gone just past the edge of the 3D view was pulled back
  in, slid up to twelve label heights with a leader line, so it jumped about as the view moved and pointed at nothing
  visible.
- **Cause:** `Viewer3DLabelLayout.Place` preferred a box wholly inside the view for every label, whatever its point.
- **Fix:** a point outside the view gets its box against it on the corner facing away from the view
  (`Viewer3DLabelLayout.Outward`), no leader, never slid. The label leaves with its point, which itself says the point
  is out of sight, and swings back on its spring as the point comes on screen. A point on screen near an edge is laid
  out as before, kept in view.
- Gate: `ViewNavigationRound14Tests.Labels_WhosePointIsOffScreen_StayOffScreen`.

## A re-hosted view's new render thread could start on a stale wake (2026-11-06)

- **Symptom:** a Reconnect, or a loss rebuild, after the render thread had ended (it returns on an exception, which is
  what starts the rebuilds) or had never started (the view had refused to draw) could fault at once with "Index was
  outside the bounds of the array", thrown by `WaitReusable` on the render thread, and each further rebuild could fault
  the same way until they were spent.
- **Cause:** `Teardown` sets `_go` to wake the render thread so that it sees `_stop`. With no thread alive to take it,
  the wake stays set, and the next `Host`'s new thread takes it as a planned frame: it calls
  `WaitReusable(_nextImage)` before `PlanFrame` has created the fresh backend's images. A race: when the UI thread
  plans the first frame first, the two wakes merge and the frame draws, which is why a rebuild sometimes recovered.
- **Fix:** `Host` resets `_go` before it starts a thread. No thread is running then (it refuses while the last one is
  alive), and only `PlanFrame`, once the images exist, sets it again.
- Found on Linux without GPU interop, through a view that had refused and was then reconnected with software present
  (a separate change) allowed. Driving the app on a virtual display: before, one Reconnect logged 4 faults from
  `WaitReusable` before a rebuild won the race (on the remote desktop it was found on, all of them lost); after,
  none, and the view draws at once and redraws on a wheel zoom.
