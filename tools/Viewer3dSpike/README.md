# Viewer3dSpike — brief em3d-27 (the 3D pane spike, opens F2)

**Throwaway.** Not in `circuitrf.slnx`, references no project in this repository, and nothing under
`src/` may reference it. Brief 28 lifts `Spike.Scene/FrameCounters.cs` and the hosting pattern in
`Spike.App/InteropPane.cs`, not the scene code. Findings: `docs/design/em-3d-f2-spike-findings.md`.

It draws F0 case A's Gmsh boundary (the bond wire, pads, ports, ground, box walls and the
air/substrate interface — 20 objects), replicated 76 times to **1,006,088 triangles** in one vertex
buffer and one index buffer, three ways:

| Route | `--route` | Hosting in Avalonia | GPU API | Native dependency |
|---|---|---|---|---|
| **A** | `metal` | composition GPU interop (IOSurface + MTLSharedEvent), own render thread | Metal (macOS) | none (OS frameworks only) |
| **A** | `d3d11` | composition GPU interop (D3D11 shared handle + keyed mutex, into ANGLE), own render thread | Direct3D 11 (Windows) — brief em3d-28 step 0 | none: OS DLLs, managed Vortice bindings |
| **A** | `vulkan` | composition GPU interop (Vulkan opaque fd + semaphores), own render thread | Vulkan (Linux) — brief em3d-28 step 0 | none: system loader, managed Vortice bindings |
| **B** | `wgpu` | composition GPU interop; wgpu's frame is blitted into the shared image on wgpu's own Metal queue | WebGPU via wgpu-native | `wgpu_native` 7–9.5 MB per platform (fetched, never committed) |
| **C** | `gl` | `OpenGlControlBase` (Avalonia's context, Avalonia's thread) | OpenGL 3.3 core / GLES 3.0 (ANGLE on Windows) | none |

Projects: `Spike.Scene` (mesh reader, replication, camera, **counters**), `Spike.Render` (the
renderers — GL, wgpu, Metal, D3D11, Vulkan — no Avalonia), `Spike.Harness` (headless counters), `Spike.App` (the Avalonia pane in a
Dock layout with a text box).

## 1. One-time setup (every OS)

From `tools/Viewer3dSpike/`:

```bash
# the F0 case A mesh (Gmsh 4.15.x; ~40 s, ~2.7 GB peak). data/ is git-ignored.
mkdir -p data && cp ../../testdata/em3d/f0/A-bondwire/palace-round/case.geo data/
(cd data && gmsh case.geo -3 -o case.msh)

./fetch-wgpu.sh                                   # route B only — macOS / Linux
```

Windows (PowerShell):

```powershell
mkdir data; copy ..\..\testdata\em3d\f0\A-bondwire\palace-round\case.geo data\
cd data; gmsh case.geo -3 -o case.msh; cd ..
powershell -ExecutionPolicy Bypass -File fetch-wgpu.ps1    # add -Gnu if wgpu_native.dll will not load (no VC++ runtime)
```

No Gmsh? `circuitRF solver install gmsh` installs one. Without `data/case.msh` both programs fall
back to a SYNTHETIC scene of the same size and say so on their first line — record that, the
numbers are not comparable.

## 2. Headless counters (the harness)

```bash
dotnet run --project Spike.Harness -c Release -- --route gl    --msh data/case.msh   # macOS only (CGL)
dotnet run --project Spike.Harness -c Release -- --route metal --msh data/case.msh   # macOS only
dotnet run --project Spike.Harness -c Release -- --route wgpu  --msh data/case.msh   # every OS
dotnet run --project Spike.Harness -c Release -- --route d3d11 --msh data/case.msh   # Windows only (brief em3d-28 step 0)
dotnet run --project Spike.Harness -c Release -- --route vulkan --msh data/case.msh  # any OS with a Vulkan loader + driver (Linux)
# and the same with -c Debug
```

Windows (PowerShell), the D3D11 half: `dotnet run --project Spike.Harness -c Release -- --route d3d11 --msh data\case.msh`,
then `-c Debug`. It compiles `shaders/scene.hlsl` with the OS's `d3dcompiler_47.dll` at start-up.

Linux, the Vulkan half: needs `libvulkan1` and a Vulkan driver (`vulkaninfo --summary` names it). On a
machine with no GPU, Mesa's software driver works (`mesa-vulkan-drivers`: lavapipe) — that is how the
agent ran it, in an Ubuntu 24.04 arm64 container:

```bash
dotnet publish Spike.Harness -c Release -r linux-arm64 --self-contained -o /tmp/h      # on the Mac
docker run --rm -v "$PWD":/spike -v /tmp/h:/h -w /spike <ubuntu:24.04 + mesa-vulkan-drivers libvulkan1 libicu74> \
    /h/Spike.Harness --route vulkan --msh data/case.msh --frames 120 --png /h/vk.png
```

Each prints the four §3 counters, two asserts (**zero bytes uploaded per orbit frame**, **the pick
reads back the wire under the cursor**; exit 1 if either fails) and two targets (zero managed
allocation per frame, pick latency one frame). `--png out.png` writes the last frame. Options:
`--frames 300 --size 1600x1000 --tris 1000000 --hz 60`.

## 3. The pane, by hand (owner)

```bash
dotnet run --project Spike.App -c Debug -- --route gl      # on macOS this switches Avalonia to its OpenGL compositor (see below)
dotnet run --project Spike.App -c Debug -- --route metal   # macOS
dotnet run --project Spike.App -c Debug -- --route wgpu    # macOS
dotnet run --project Spike.App -c Debug -- --route d3d11   # Windows (brief em3d-28 step 0)
dotnet run --project Spike.App -c Debug -- --route vulkan  # Linux, on Avalonia's default GLX compositor
dotnet run --project Spike.App -c Debug -- --route vulkan --compositor vulkan   # Linux, if GLX offered nothing
```

`--exit-after 20` closes the window by itself after 20 s and prints the summaries (handy for a first
look at what the compositor offered: the pane prints `[pane] compositor offered: …` to the terminal on
every OS). On Linux the GLX compositor can import the Vulkan images only if the GL driver has
`GL_EXT_memory_object_fd` **and** `GL_EXT_semaphore_fd` (`glxinfo | grep -E "memory_object_fd|semaphore_fd"`);
Mesa's hardware drivers have both; llvmpipe has only the first.

Debug on purpose: it is the build the owner runs. Avalonia 12's macOS compositor defaults to **Metal**
(`Metal, OpenGl, Software`; its API documentation still says OpenGL), and under it
`OpenGlControlBase` cannot initialise at all — so `--route gl` puts the whole window on Avalonia's
OpenGL compositor, which is what circuitRF would have to do to ship route C. `--compositor
default|metal|gl` overrides it; the window title says which is in use. On Windows and Linux, `--route metal` and
`--route wgpu` open and say *NOT HOSTED* in the pane — route B's interop hosting is macOS only (it
has no texture hand-off on Windows or Linux, findings Q6). `--route d3d11` is Windows only and
`--route vulkan` Linux only; any other combination also says *NOT HOSTED* and why — expected, not a
failure to report. A *NOT HOSTED* for `d3d11` on Windows or `vulkan` on Linux **is** the finding: copy
the whole status line (it names what the compositor offered).

The status line (bottom) shows the device, the hosting path and what the compositor offered, the
scene, UI frames/s, **which thread the GPU work runs on**, how many times the GPU state was
(re)initialised and the bytes uploaded in total, and the last frame's upload / allocation / time and
pick latency. **On close, the counter summaries print to the terminal — paste them into the
findings.**

For each route on each OS, record:

1. **A million triangles** — the status line says `1,006,088 triangles` and `case.msh`, not SYNTHETIC.
2. **Orbit, pan, zoom** by mouse (drag / right-drag or Shift-drag / wheel) and trackpad (two-finger
   scroll, pinch). While orbiting, *last UI frame: upload* stays **0 B**.
3. **Picking** — the object under the cursor turns cyan within a frame; a click turns it magenta; the
   status line names it.
4. **Translucency** — the air walls and the green dielectric over the grey ground and gold wire look
   right from every angle, with no popping as the order changes.
5. **One compositor** — tick *Auto-orbit*, click in *Notes* and type fast. Does typing stay smooth?
   Then drag the *3D view* tab out to float it, move the floating window, and drag it back. Does the
   pane survive both? (Route C: *GPU (re)initialisations* goes up by one and *bytes uploaded* by
   ~20 MB per re-host — expected, and part of the finding. Routes A/B should not re-upload.)
6. **Resize and DPI** — resize the window and the splitter; on macOS drag the window between a Retina
   and a non-Retina display. The image stays sharp and fills the pane.

And **"does it feel fast"** — in your own words, per route.

## 4. Files and licences

`native/` (wgpu-native v29.0.1.1, MIT OR Apache-2.0) and `data/` are git-ignored. The D3D11 and Vulkan
halves use managed NuGet bindings only — Vortice.Direct3D11 / Vortice.DXGI / Vortice.D3DCompiler 3.8.3 and
Vortice.Vulkan 3.2.3, all MIT, each `.nupkg` checked to carry no `runtimes/*/native` payload (findings §3). No source in here
is copied from anywhere; the wgpu bindings in `Spike.Render/Wgpu/Wgpu.cs` were written by hand from
that release's `webgpu.h`.

## 5. Single-source shaders (findings Q7)

`shaders/scene.wgsl` is route B's shader, extracted verbatim. `shaders/scene.metal`, `scene.hlsl` and
`scene.spv` are generated from it by **`tools/ShaderGen`** (brief em3d-28), which calls naga 30.0.1's
library with every binding stated — no fix-up — and records the WGSL's SHA-256 in each output:

```bash
(cd ../ShaderGen && cargo build --release)
../ShaderGen/target/release/shadergen shaders/scene.wgsl shaders           # regenerate all three
../ShaderGen/target/release/shadergen --check shaders/scene.wgsl shaders   # exit 1 if any is stale
```

Route D3D11 compiles `scene.hlsl` at start-up; route Vulkan loads `scene.spv`.

Route A runs on the generated MSL instead of its own (macOS):

```bash
SPIKE_MSL=shaders/scene.metal dotnet run --project Spike.Harness -c Release -- --route metal --msh data/case.msh
```
