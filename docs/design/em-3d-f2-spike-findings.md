# circuitRF — 3D EM, F2 spike (the 3D pane): findings

**Status:** the agent's part is complete on macOS; all three routes render in the owner's hands on
macOS; the remaining hand checks (macOS feel, Windows, Linux) are **pending** (§7). **Brief 28 step 0
(§8):** route A's D3D11 and Vulkan halves are built; Vulkan's harness passes on Linux (software driver),
D3D11 is compiled but has never run — **both panes await the owner's Windows and Linux runs** (§7). **D3 decided
2026-09-25: route A primary, route B secondary** (§6) · **Date:** 2026-09-25 ·
**Brief:** [`brief-em3d-27-3d-pane-spike.md`](../sonnet-briefs/brief-em3d-27-3d-pane-spike.md) ·
**Design note:** [`em-3d.md`](em-3d.md) §8 · **Code:** [`tools/Viewer3dSpike/`](../../tools/Viewer3dSpike/README.md)
(throwaway; not in `circuitrf.slnx`; the README has every command per OS)

Every number below comes from the spike's counter harness on the agent's machine unless it says
otherwise: Apple M4 (10 cores), 16 GB, macOS 27.0, .NET 10, Avalonia 12.0.3, Dock 12.0.0.2,
wgpu-native v29.0.1.1. The scene is **F0 case A's Gmsh boundary** — 20 objects (the wire's three
entities, pads, ports, ground, eight wall faces and the air/substrate interface recovered from the
tetrahedra), replicated 76 times to **1,006,088 triangles, 537,624 vertices, one 8.2 MiB vertex
buffer and one 11.5 MiB index buffer** — drawn at 1600 × 1000 through a 600-frame orbit paced at
60 Hz with the pick cursor moving every frame.

**The agent never saw the pane.** Its shell cannot open an Avalonia window (overview §1e), so nothing
in this document says how the pane looks or feels. The agent did see the harness's *offscreen* frames
(written as PNG), which is how the defect in §5.1 was found; that is not the pane.

---

## 0. The answers in one screen

| # | Question | Answer |
|---|---|---|
| Q1 | Did it host? | **Built, not seen.** Route C (`OpenGlControlBase`) builds for every OS. Routes A (Metal) and B (wgpu) are hosted through composition GPU interop on **macOS**; B is **blocked** on Windows and Linux (Q6). **Brief 28 step 0 (§8) built route A's other two image sources** — D3D11 keyed-mutex textures into ANGLE on Windows, Vulkan opaque-fd images + semaphores on Linux — **built, not seen**: whether each appears is the owner's check (§7) |
| Q2 | Did it pick? | **Yes, all three routes**, offscreen on macOS: with the cursor on the wire's highest vertex, the ID pass read back a wire entity. Route B also picked on **Linux arm64** (Vulkan, in a container, software rasteriser) |
| Q3 | Do the counters hold? | **Yes, all three routes, Debug and Release:** 0 bytes uploaded per orbit frame, 0 bytes of managed allocation per frame (steady state *and* first frames), pick latency **1 frame in 599 of 599** picks. §2 |
| Q4 | Pane time per frame (reported, not asserted) | p50 **A 0.05 ms, B 0.10 ms, C 1.5–1.6 ms**; Debug and Release within noise of each other (§2). C's cost is the macOS GL driver, which is a translation layer over Metal (`4.1 Metal - 91.7`) |
| Q5 | Native dependencies | **A: none** on any OS (OS frameworks/DLLs/loader only; the D3D11 and Vulkan halves use managed Vortice bindings, §3). **C: none.** **B: `wgpu_native`**, 6.9–9.5 MB per platform, MIT OR Apache-2.0, plus the Visual C++ runtime on Windows (its MSVC build imports `VCRUNTIME140.dll`). §3 |
| Q6 | Can route B hand a texture to the compositor? | **On macOS, yes, with a CPU wait** — wgpu-native v29 exposes the native `MTLDevice` and `MTLTexture`, but **its `MTLCommandQueue` getter returns NULL** (§5.6), so the copy into the IOSurface Avalonia imports runs on a queue of circuitRF's own after `wgpuDevicePoll(wait)` blocks the render thread until wgpu's frame is done (3.9 ms/frame on that thread, none on the UI thread). **On Windows and Linux, no**: `webgpu.h` has no external-memory interop and wgpu-native exposes no D3D12 or Vulkan handle, so the only path is a CPU readback of every frame (6.4 MB/frame at 1600 × 1000) or a native change to wgpu-native |
| Q7 | One shader language for route A? | **Yes, offline:** route B's WGSL cross-compiled by naga (MIT/Apache-2.0, build-time only) to MSL rendered and picked **identically** in route A (`tools/Viewer3dSpike/shaders/`, README §5); naga's HLSL and SPIR-V come out with usable bindings. naga's CLI emits a placeholder binding in MSL (`[[user(fake0)]]`) that needs a one-line fix-up (§5.4). **Brief 28: `tools/ShaderGen` calls naga's library with the bindings stated — no fix-up — and the three outputs now run: MSL (Metal), SPIR-V (Vulkan, §8), HLSL (D3D11: built, not run)** |
| Q8 | Lines of platform-specific code | C: **1** (the GLSL `#version` line). B: **0** in the renderer, 14 in the macOS present bridge. A: all of it — ~330 renderer + ~100 image-source lines **per API** as estimated; measured for the two added halves: D3D11 278 + 95, Vulkan 639 + 110 (§4, §8) |
| Q9 | Avalonia API stability | Everything used is public in 12.0.3; **nothing carries `[Unstable]` or `[PrivateApi]`**. The interop interfaces are `[NotClientImplementable]` (consume, do not implement — all the spike does). §4 |
| D3 | Recommendation → **decided** | **Owner chose route A as primary, route B as secondary (§6).** Recommended: **Route A — the native API per platform, through composition interop — with one WGSL shader source cross-compiled offline.** Runner-up: **route B**. §6 |
| — | Owner's hand checks | **Pending**, every route on every OS (§7) |

---

## 1. What was done, and by whom (R-em3d27-5)

| Part | Who | State |
|---|---|---|
| Route C: renderer, Avalonia pane, headless harness (CGL) | agent | **done** — harness run on macOS |
| Route B: hand-written wgpu-native v29 bindings, renderer, harness | agent | **done** — harness run on macOS (Metal) and Linux arm64 (Vulkan, llvmpipe, in a container) |
| Route B: hosted in Avalonia on macOS (blit into the shared IOSurface) | agent | **built** — not run (no window) |
| Route A, Metal half: renderer, harness, composition-interop pane | agent | **done** (harness) / **built** (pane) |
| Route A, Vulkan half (Linux): renderer, harness, composition-interop image source | agent (brief 28 step 0) | **done** (harness, Linux arm64 container, lavapipe) / **built** (pane — the container's software drivers cannot host it, §8) |
| Route A, D3D11 half (Windows): renderer, harness, composition-interop image source | agent (brief 28 step 0) | **built, not run** — compiled on macOS; nothing here can run D3D11 (§8) |
| Route B hosted on Windows / Linux | — | **not attempted** — no texture-handle path in wgpu-native's C API (Q6) |
| Single-source shaders for route A (WGSL → MSL/HLSL/SPIR-V via naga) | agent | **MSL and SPIR-V run** (Metal on macOS; Vulkan on Linux, `spirv-val` clean); HLSL generated and compiled into the D3D11 half, not run. Brief 28: generated by `tools/ShaderGen`, each output carrying the WGSL's SHA-256 |
| The six §2 behaviours and "does it feel fast", per route, per OS | owner | **pending** (§7) |
| D3 | owner | **pending** (§6) |

Order followed: C first, then B, then A — B's hosting on macOS needs the Metal interop half anyway,
so that half was written once and serves both.

---

## 2. The counters (R-em3d27-3)

The four counters live in `Spike.Scene/FrameCounters.cs`, written to be lifted into brief 28 as they
are. Every upload goes through `CountUpload`; per-frame camera/hover bytes go through `CountUniform`
and are **reported apart**, because WebGPU core has no push constants and can only deliver a uniform
through a buffer write — never geometry, but not literally "no bytes into any buffer" either.

macOS, 600 orbit frames, 60 Hz pacing, moving pick every frame, `data/case.msh` scene:

| Route | Build | Upload / orbit frame | Uniform bytes / frame | Managed alloc / frame | Pane ms p50 / p95 / max | Pick latency (frames) | Draw calls / frame |
|---|---|---|---|---|---|---|---|
| C `gl` | Debug | **0 B** | 84 (glUniform) | **0 B** | 1.486 / 1.611 / 2.883 | 1 × 599 | 761 |
| C `gl` | Release | **0 B** | 84 | **0 B** | 1.602 / 1.733 / 6.708 | 1 × 599 | 761 |
| A `metal` | Debug | **0 B** | 287 (inline setVertexBytes/setFragmentBytes) | **0 B** | 0.059 / 0.085 / 0.450 | 1 × 599 | 761 |
| A `metal` | Release | **0 B** | 287 | **0 B** | 0.052 / 0.075 / 0.350 | 1 × 599 | 761 |
| B `wgpu` | Debug | **0 B** | 191 (queue.writeBuffer, 96 B + 96 B on a pick frame) | **0 B** | 0.104 / 0.167 / 1.009 | 1 × 599 | 761 |
| B `wgpu` | Release | **0 B** | 191 | **0 B** | 0.099 / 0.182 / 0.961 | 1 × 599 | 761 |
| B `wgpu`, Linux arm64 container, llvmpipe | Release | **0 B** | 190 | **0 B** | 65.4 / 69.9 / 73.1 *(software rasteriser — not a performance figure)* | 2 × 120 *(frame period exceeded)* | 761 |
| A `vulkan` (brief 28), Linux arm64 container, lavapipe | Release | **0 B** | 1,201 (608 B colour pass + 608 B on a pick frame, memcpy into a mapped ring) | **0 B** | 65.5 / 68.2 / 74.8 *(software rasteriser — not a performance figure)* | 3 × 120 *(frame period exceeded; the ring is 3 deep)* | 761 |
| A `vulkan` (brief 28), Linux arm64 container, lavapipe | Debug | **0 B** | 1,201 | **0 B** | 66.3 / 79.2 / 86.1 | 3 × 120 | 761 |
| A `metal` on the ShaderGen MSL (brief 28) | Release | **0 B** | 1,812 | **0 B** | 0.052 / 0.089 / 0.199 | 1 × 299 | 761 |

- **761 draw calls** = one call for every opaque object in all 76 replicas (they are contiguous in the
  index buffer), one per translucent object (760, sorted back to front per object, never per
  triangle — brief §2.4), and the 1 × 1 ID pass.
- **Picking** draws the opaque range once more into a **1 × 1 R32Uint target** through a projection
  narrowed so the cursor's pixel fills the viewport, then reads that one texel back asynchronously
  (GL: pixel-pack buffer + fence; Metal: blit to a shared buffer, command-buffer status; WebGPU:
  `copyTextureToBuffer` + `mapAsync`). Hover highlighting is one shader uniform.
- **Debug vs Release are within noise.** The frame loop's managed code is a few hundred lines that
  allocate nothing, so the Debug build the owner runs is not disadvantaged *in the frame loop*. The
  in-app UI thread is not measured here — the app's status line and exit summary report it (§7).
- **Pane time is where the routes differ.** C's 1.5 ms is on the calling thread — in the app that is
  whichever thread Avalonia calls `OnOpenGlRender` on, which the status line reports (the design note
  expects the UI thread). A's and B's are on the pane's own render thread; their UI-thread share per
  frame is handing a finished image to the compositor.
- **One-time costs:** reading the 15.6 MB `.msh` and replicating it: ~140 ms. The 20.7 MB upload: GL
  24–37 ms, Metal 15–80 ms, wgpu 13–24 ms (592 ms on its first run on the machine).

---

## 3. Every native dependency (R-em3d27-4)

| Route | macOS | Windows | Linux | Licence |
|---|---|---|---|---|
| **A** | **none** — `Metal`, `IOSurface`, `CoreFoundation` and `libobjc` are the OS's; the spike calls them through the Objective-C runtime with no binding package | **none native.** `d3d11.dll`, `dxgi.dll`, `d3dcompiler_47.dll` are the OS's. Bindings (brief 28): **Vortice.Direct3D11 3.8.3** (947,931 B `.nupkg`), **Vortice.DXGI 3.8.3** (566,136 B), **Vortice.D3DCompiler 3.8.3** (67,500 B), pulling **Vortice.DirectX 3.8.3** (1,857,553 B), **Vortice.Mathematics 2.1.1** (536,801 B), **SharpGen.Runtime** and **SharpGen.Runtime.COM 2.4.2-beta** (173,245 + 456,675 B) — all MIT, every one unpacked: `lib/` assemblies and build props only, **no `runtimes/*/native`**. Note the SharpGen pair is a *beta* version pin that Vortice 3.8.3 itself requires | **none native.** `libvulkan.so.1` is the OS/driver loader (not shipped). Binding (brief 28): **Vortice.Vulkan 3.2.3** (1,402,372 B `.nupkg`, MIT, no dependencies, `lib/net9.0`+`net10.0` only — no native payload) | MIT |
| **A, shaders** | naga CLI, **build time only** (Rust, MIT OR Apache-2.0); generated `.metal`/`.hlsl`/`.spv` committed; nothing shipped | same | same | MIT OR Apache-2.0 |
| **B** | `libwgpu_native.dylib` **6.9 MB** arm64, **7.3 MB** x64 | `wgpu_native.dll` **9.1 MB** x64, **7.7 MB** arm64; imports **`VCRUNTIME140.dll`** (the Visual C++ runtime — present on most machines, not guaranteed; a `-gnu` x64 build exists). DX12 shaders via FXC by default (OS component); DXC (`dxcompiler.dll`) only if selected | `libwgpu_native.so` **9.3 MB** x64, **9.5 MB** arm64; links only libc/libm/libdl/libpthread/libgcc_s, **glibc ≥ 2.28** | MIT OR Apache-2.0 |
| **C** | none | none beyond Avalonia's own (ANGLE ships with Avalonia.Desktop) | none (system libGL) | — |

B also has **no maintained .NET binding for the current header**: Silk.NET.WebGPU 2.23 (the latest)
binds an older `webgpu.h` that predates the string-view and callback-info ABI and the native Metal
getters Q6 relies on. The spike's bindings (`Spike.Render/Wgpu/Wgpu.cs`, 167 lines) were written by
hand from the v29 header; adopting B means owning that file and pinning the wgpu-native version.

---

## 4. Code shape and the Avalonia surface

**Lines** (`wc -l`, all throwaway):

| | Shared | Route C | Route B | Route A (Metal only) |
|---|---|---|---|---|
| Scene, camera, counters, mesh reader | 652 | — | — | — |
| Renderer | — | 415 (`Gl.cs` + `GlRenderer.cs`) | 506 (`Wgpu.cs` + `WgpuRenderer.cs`) | 427 (`ObjC.cs` + `MetalRenderer.cs`) |
| Avalonia hosting | 412 (window, Dock, input, session) | 47 (`GlPane.cs`) | shares A's pane + 14-line bridge | 301 (`InteropPane.cs`) |
| **Platform-specific** | 0 | **1** (GLSL `#version`: ES vs desktop) | **0** renderer, 14 bridge (macOS) | **all 728**, and the same again per extra API |

Route A on Windows and Linux would each add a renderer (~330 lines at this scene's size) and the
image-source part of the pane (~100 of its 300 lines: D3D11 shared handle + keyed mutex; Vulkan
opaque FD + semaphores). **Measured after brief 28 step 0:** `D3D11Renderer.cs` 278 and `D3D11Source`
95; `VulkanRenderer.cs` 639 (Vulkan's explicit render passes, pipelines, descriptor set, fences and
image creation are most of it) and `VulkanSource` 110. The pane became one control over an
`IImageSource` per platform (`InteropPane.cs`, 588 lines with all three sources). With single-source shaders (Q7) the per-API code is resource and command
plumbing only; everything the viewer will grow into — mesh, fields, colour maps, clip planes — lives
in the shared scene and the one shader source.

**Avalonia API used** (12.0.3): `ElementComposition.GetElementVisual`/`SetElementChildVisual`;
`Compositor.TryGetCompositionGpuInterop`, `CreateDrawingSurface`, `CreateSurfaceVisual`,
`RequestCompositionUpdate`; `ICompositionGpuInterop.SupportedImageHandleTypes`,
`SupportedSemaphoreTypes`, `GetSynchronizationCapabilities`, `ImportImage(IPlatformHandle, …)`,
`ImportSemaphore`; `CompositionDrawingSurface.UpdateWithTimelineSemaphoresAsync` and `UpdateAsync`;
`OpenGlControlBase` (`OnOpenGlInit/Deinit/Render/Lost`, `RequestNextFrameRendering`, `GlVersion`) and
`GlInterface.GetProcAddress` only; `AvaloniaNativePlatformOptions.RenderingMode`. None is marked
`[Unstable]` or `[PrivateApi]` (checked by reflection); the interop interfaces are
`[NotClientImplementable]`. Two facts that shape brief 28:

- **circuitRF sets no rendering mode, so it gets Avalonia's defaults**: **Metal** on macOS
  (`Metal, OpenGl, Software` — read from a constructed `AvaloniaNativePlatformOptions`; the property's
  XML documentation still says OpenGL, which this document first repeated), ANGLE (D3D11 underneath)
  on Windows (`AngleEgl, Software`), GLX on Linux (`Glx, Software`). What the compositor can import
  depends on that. On macOS the owner's run settled it: the default compositor offers IOSurfaceRef
  with MetalSharedEvent timeline semaphores (§7). On Windows and Linux it is still unknown.
- **`Update*Async` returns a `Task` per frame**, so the UI thread allocates once per presented frame
  on routes A and B however clean the pane's own code is. The UI-lane counter will show it; the
  "~0 allocations" target may be unreachable there for a reason outside circuitRF.

---

## 5. What broke

1. **Route C's blend wrote destination alpha below 1.** The pane composites over the window, so every
   translucent object would have punched a hole through it — invisible in any opaque-window test, and
   found only by comparing the GL and WebGPU harness PNGs, whose colours differed. Fixed with a
   separate alpha blend (`ONE, ONE_MINUS_SRC_ALPHA`), which routes A and B already set. **Brief 28
   rule: the shared image's alpha stays 1.**
2. **Pick latency must be measured paced.** Unpaced, the harness queued frames behind the one-time
   upload and pipeline builds and read 6–11 frames of latency; paced at the display rate it reads 1
   frame in every pick on all three routes. In the app, the compositor paces.
3. **Route B cannot present on Windows or Linux** through wgpu-native's C API (Q6). The options are a
   per-frame CPU readback (which puts a 6.4 MB copy per frame back on the path the design keeps
   clear), a native change to wgpu-native exposing its D3D12/Vulkan handles (a Rust build circuitRF
   would own, or an upstream contribution — an owner call), or Dawn, whose shared-texture-memory
   interop covers all three but which has no .NET distribution.
4. **naga's CLI has no binding map for MSL** and emits `[[user(fake0)]]` for the uniform block; the
   spike patched it to `[[buffer(1)]]`. Production would call naga's library API from a small build
   tool, or keep a checked fix-up. HLSL (`register(b0)`) and SPIR-V (set/binding) come out usable.
5. **`OpenGlControlBase` does not work under Avalonia 12's default macOS compositor.** The owner's
   run: `Unable to initialize OpenGL: … Unable to locate
   'Avalonia.OpenGL.IPlatformGraphicsOpenGlContextFactory'` — the Metal compositor registers no GL
   context factory, so the control never initialises and draws nothing. Route C on macOS needs the
   **whole application** moved to Avalonia's OpenGL compositor (`--route gl` now does that). That is a
   further, decisive strike against C: it would change how every other panel in circuitRF is drawn.
6. **wgpu-native v29.0.1.1's `wgpuQueueGetNativeMetalCommandQueue` returns NULL** (the device and
   texture getters work). The pane's first version encoded its copy and its "ready" signal on that
   queue; a message to nil is a silent no-op in Objective-C, so nothing was committed, the compositor
   waited on the ready event forever, and **the whole window's composition stalled** (owner's run:
   101 UI frames, then none; 7 release timeouts). Reproduced headlessly with the harness's
   `wgpu-bridge` route, which exercises exactly the pane's present path and reads the IOSurface back.
   Fixed as Q6 describes. Lesson for brief 28: **a GPU wait the compositor performs on circuitRF's
   signal is a way to freeze the application**, so a present step must check every native handle and
   treat a missing signal as a fault, not a timeout.
7. **`OpenGlControlBase` gives each control instance a fresh context**, and a Dock float or re-dock
   re-templates the view, so route C re-uploads its geometry (~20 MB here) on every re-host. Routes
   A/B keep device and buffers in a session object that outlives the view. The status line counts
   re-initialisations and total bytes uploaded; **the owner's run confirms or refutes this**.

---

## 6. Recommendation for D3 (overview §1f, `em-3d.md` §11 Open 2)

**Recommended: route A — each platform's own GPU API (Metal, D3D11, Vulkan) behind composition GPU
interop, with one WGSL shader source cross-compiled offline by naga and the generated shaders
committed.**

- It is the only route whose presentation path exists on all three OSes by construction: Avalonia's
  documented import handles are exactly what those APIs export (IOSurface + MTLSharedEvent; D3D11
  shared handle + keyed mutex; Vulkan opaque FD + semaphores), and the macOS half is built and passes
  every counter.
- It ships **no native dependency on any OS** (the D3D11/Vulkan bindings are managed code or
  hand-written calls; naga runs at build time only), which is the question CLAUDE.md asks first.
- It is the fastest measured (0.05 ms/frame), and its frame never touches the UI thread.
- The cost is real and bounded: two more thin API backends (~430 lines each at this scene, mostly
  resource plumbing) and a shader build step. The single-source shader removes the cost the brief
  worried about most — three shader languages drifting apart.

**Runner-up: route B (WebGPU through wgpu-native).** One renderer, zero platform-specific lines,
proven on Metal and Vulkan from the same code. It loses on the three things this spike was asked to
settle: a 7–9.5 MB native library per platform (plus the VC++ runtime on Windows), no current .NET
binding, and — decisively — **no way to hand its image to Avalonia's compositor on Windows or Linux**
without a native change. If wgpu-native gains D3D12/Vulkan handle getters (its v29 Metal getters
show the direction), B becomes the better choice and the switch touches one project's references,
as brief 28 is written to allow.

**Not recommended: route C** — the baseline did its job. On macOS it does not even initialise under
Avalonia 12's default (Metal) compositor (§5.5), so shipping it means moving every panel of the
application onto the OpenGL compositor. GL frozen at 4.1 on macOS and translated
over Metal there (30× A's frame cost), ANGLE's GLES 3.0 on Windows, frame work on Avalonia's thread,
and a re-upload on every Dock re-host.

**What the recommendation rests on that was not measured:** A's D3D11 and Vulkan halves. Before
brief 28 grows the scene, those two halves should go through this spike's harness and pane (the
harness backends are the pattern; ~430 lines each). That is the first thing to do once the owner
decides, whichever way. (On macOS the owner's run has already shown the default compositor accepts
the IOSurface with timeline semaphores.)

**Owner's decision (2026-09-25): route A is the primary path, route B the secondary.** Each
platform's own GPU API is what the viewer briefs build. Route B is implemented immediately only on a
platform where route A turns out not to work, and is implemented on every platform later. The
decision was taken knowing that route A's Windows and Linux halves are untested.

---

## 7. What the owner saw, per OS (R-em3d27-5)

Commands: `tools/Viewer3dSpike/README.md` §3.

**First owner run, macOS, Debug (2026-09-25).** Route A hosted on Avalonia's **default** compositor —
the IOSurface import and timeline semaphores were offered. (That default compositor is Metal — §4.) Its
exit summary: UI thread 0.011 ms p50 per frame with **408 B allocated per steady-state frame** (the
`Task` `Update*Async` returns, as §4 predicted); render thread 0.38 ms p50, 0 B upload per orbit frame,
0 B steady-state allocation, pick latency 1 frame in 3,225 of 3,226.

Routes B and C, after the fixes of §5.5–5.6, on the same machine and build: B's render thread
6.0 ms p50 (it includes the CPU wait for wgpu's frame) with 0 release timeouts; C 2.74 ms p50 /
5.17 ms p95 **in the pane's own code on the thread Avalonia renders it on** — against A's
0.010 ms UI-thread share. In the app, Debug, C's frame costs ~1.8× what the harness measured
offscreen (1.49 ms), and it is the only route whose frame cost lands on the compositor's schedule. Paste the exit summary (printed to the terminal on
close) under each run.

| Route | OS | 1. 10⁶ tris | 2. orbit/pan/zoom (0 B upload) | 3. pick + highlight | 4. translucency | 5. typing while orbiting; float + re-dock | 6. resize + DPI | Compositor offered (status line) | Feel |
|---|---|---|---|---|---|---|---|---|---|
| C `gl` | macOS | blank on the default (Metal) compositor (§5.5); **renders on the OpenGL compositor** — OpenGL 4.1 (`4.1 Metal - 91.7`), 2.74 ms p50 / 5.17 ms p95 per frame in the pane's own code (Debug), 0 B upload per orbit frame, 0 B steady-state allocation, pick latency 1 × 225 | pending | pending | pending | pending | pending | pending | pending |
| A `metal` | macOS | yes | pending | pending | pending | pending | pending | **default compositor (OpenGL): images [IOSurfaceRef], semaphores [MetalSharedEvent], sync [TimelineSemaphores]** | "looks great" (paraphrased) |
| B `wgpu` | macOS | first run blank (the NULL queue, §5.6); after the fix: 0 release timeouts, render thread 6.0 ms p50 (Debug, includes the GPU wait), 0 B upload, pick latency 1 × 470 | pending | pending | pending | pending | pending | same as A | pending |
| C `gl` | Windows | pending | pending | pending | pending | pending | pending | n/a | pending |
| B harness | Windows | — | counters: pending | pick: pending | — | — | — | — | — |
| C `gl` | Linux | pending | pending | pending | pending | pending | pending | n/a | pending |
| B harness | Linux x64 (real GPU) | — | counters: pending | pick: pending | — | — | — | — | — |
| B pane | Windows, Linux | **not built** — no hand-off path (Q6); the pane says so | | | | | | | |
| A `d3d11` harness | Windows | — | counters: **pending** — `dotnet run --project Spike.Harness -c Release -- --route d3d11 --msh data\case.msh`, then `-c Debug` | pick: pending (same run: the `ASSERT` lines) | — | — | — | — | — |
| A `d3d11` pane | Windows | pending — `dotnet run --project Spike.App -c Debug -- --route d3d11` | pending | pending | pending | pending | pending | pending (the terminal's `[pane] compositor offered:` line; expected `D3D11TextureGlobalSharedHandle,D3D11TextureNtHandle`, sync `KeyedMutex`) | pending |
| A `vulkan` harness | Linux x64 (real GPU) | — | counters: **pending** — `dotnet run --project Spike.Harness -c Release -- --route vulkan --msh data/case.msh`, then `-c Debug` | pick: pending | — | — | — | — | — |
| A `vulkan` pane | Linux | pending — `dotnet run --project Spike.App -c Debug -- --route vulkan`; if it says *NOT HOSTED*, again with `--compositor vulkan` | pending | pending | pending | pending | pending | pending (the `[pane] compositor offered:` line under GLX, and under `--compositor vulkan` if tried; plus `glxinfo \| grep -E "memory_object_fd\|semaphore_fd"`) | pending |

---

## 8. Brief 28 step 0 — route A's D3D11 and Vulkan halves (R-em3d28-0)

D3 chose route A knowing that only its macOS half existed. Before any viewer code grows, brief 28 asked
for the other two halves in this spike's shape. **What the agent could and could not do from a Mac:**

| Half | Built | Ran here | Owner |
|---|---|---|---|
| **Vulkan** renderer (`Spike.Render/Vulkan/VulkanRenderer.cs`) + harness `--route vulkan` | yes | **yes** — Ubuntu 24.04 arm64 container, Mesa lavapipe (LLVM 20.1.2), Debug and Release: **0 B per orbit frame, 0 B managed allocation per frame, the pick reads back a wire entity** (§2 rows). Its frame matches Metal's at the same camera to a mean 0.095 per channel, 3 of 1,600,000 pixels differing by more than 24 | a real GPU's counters (§7) |
| **Vulkan** image source (`InteropPane.cs`, `VulkanSource`) | yes | **no, and it cannot here**: under Xvfb Avalonia refuses llvmpipe for GLX (*"Renderer 'llvmpipe' is blacklisted"*) and falls back to software, so `TryGetCompositionGpuInterop()` is null; `--compositor vulkan` also ends on software (lavapipe has `VK_KHR_external_memory_fd` but **not** `VK_KHR_external_semaphore_fd`, which Avalonia's Vulkan interop requires). Even un-blacklisted, llvmpipe's GL has `GL_EXT_memory_object_fd` but no `GL_EXT_semaphore_fd`. The pane reported each of these as *NOT HOSTED* with the reason, as designed — a software stack simply cannot host it | the pane, on a machine with a real GPU driver (§7) |
| **D3D11** renderer (`Spike.Render/D3D11/D3D11Renderer.cs`) + harness `--route d3d11` + image source (`D3D11Source`) | yes | **no** — compiled only. An attempt under Wine 8.0 (amd64, emulated) never reached D3D11: .NET 10 itself does not start there (*"Could not load file or assembly … System.Runtime.dll. Module not found"*) | everything (§7) |

**How each half presents** — read from Avalonia 12.0.3's own importers (decompiled), not assumed:

- **Windows (ANGLE, the default compositor):** `AngleExternalObjectsFeature` offers
  `D3D11TextureGlobalSharedHandle` and `D3D11TextureNtHandle`, **keyed mutex only** (no semaphores), and
  **R8G8B8A8 only** (it wraps the texture as an EGL pbuffer). It exposes the compositor's adapter **LUID**, so
  the device is created on that adapter — a shared handle does not open across adapters. Protocol: the render
  thread takes key 0, draws, releases key 1; `UpdateWithKeyedMutexAsync(image, 1, 0)` makes the compositor
  take 1 and give 0 back. `IDXGIKeyedMutex::AcquireSync` returns `WAIT_TIMEOUT` (0x102) as a *success* code,
  which Vortice's wrapper does not throw on, so the spike calls it through the vtable and treats a timeout as
  a **fault** (reported, rendering stopped) — §5.6's rule.
- **Linux (GLX, the default):** `ExternalObjectsOpenGlExtensionFeature` offers `VulkanOpaquePosixFileDescriptor`
  images and semaphores **only if the GL driver has both `GL_EXT_memory_object_fd` and `GL_EXT_semaphore_fd`**,
  with binary-semaphore sync; it imports the memory as `GL_RGBA8` and **waits the semaphore with
  `GL_LAYOUT_TRANSFER_SRC_EXT`**. Avalonia's Vulkan compositor (`--compositor vulkan`) re-creates the image from
  the fd with fixed parameters — R8G8B8A8, optimal tiling, usage TRANSFER_SRC|TRANSFER_DST|SAMPLED|COLOR_ATTACHMENT,
  `MUTABLE_FORMAT`, one mip, a **dedicated** allocation whose size must equal `MemorySize` — and assumes
  **TRANSFER_SRC_OPTIMAL**. So the Vulkan half creates its image exactly so, and every frame's render pass ends
  in TRANSFER_SRC_OPTIMAL. Each import takes ownership of its fd, so a fresh one is exported per import. A binary
  semaphore may be waited only after its signal was *submitted*, so before waiting "released" the render thread
  sees the compositor's update task complete; one that does not is a fault. The device is matched to the
  compositor's by **UUID**.

**Two things about the single-source shaders that the spike learned here:**
1. `OpModuleProcessed`, the natural home for the WGSL hash in SPIR-V, needs SPIR-V 1.1; `spirv-val` rejects it
   in the 1.0 module a Vulkan 1.0 driver requires. `tools/ShaderGen` records the hash in an
   `OpSourceExtension` instead (valid in 1.0, ignored by every consumer).
2. naga's SPIR-V writer defaults a DEBUG flag from **its own build profile** (`cfg!(debug_assertions)`), so a
   debug-built generator emits different words. ShaderGen sets every flag explicitly; its outputs are
   byte-identical run to run. With the binding map stated through naga's library, the regenerated MSL differs
   from the CLI's only by the header line and `[[user(fake0)]]` → `[[buffer(1)]]` — the old hand fix-up, now
   unnecessary.

**Neither half has been shown unable to present,** so no platform takes route B yet (brief 28 §1c). That
verdict needs the owner's Windows run and a Linux run on a real GPU driver (§7).

