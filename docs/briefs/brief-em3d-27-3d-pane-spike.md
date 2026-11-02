# Brief 27 — the 3D pane spike (opens F2)

**Series:** [3D EM, second series](brief-em3d-20-overview.md) · **Tag:** `R-em3d27-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8 (all of it, especially §8.3 and §8.6); Open 2
**Area:** `tools/Viewer3dSpike/` (new, **not** in `circuitrf.slnx`, never shipped),
`docs/design/em-3d-f2-spike-findings.md` (new)
**Depends on:** — · **Blocks:** 28, once the owner has decided D3

---

## 0. What this brief delivers

The design wants a 3D view that feels as immediate as the best open-source modelling tools. Before any
viewer code is written, this brief proves one thing: **Avalonia 12 can host a GPU surface that orbits a
million triangles and picks under the cursor, on all three operating systems, without slowing the
rest of the window.**

It ends in a findings document and a recommendation. **The owner decides** the GPU API and any native
dependency it brings (overview §1f, D3). Brief 28 starts after that.

---

## 1. `R-em3d27-1` — the candidates

Build the same small pane each of these ways, as far as each can get in the time box:

| Route | Hosting | GPU API | Native dependency? |
|---|---|---|---|
| **A** | composition GPU interop (`CompositionDrawingSurface` + `ICompositionGpuInterop`) | the platform's own: Metal (macOS), D3D11 (Windows), Vulkan (Linux), through managed bindings | whatever the bindings need. **Find out and list it.** |
| **B** | composition GPU interop | WebGPU through wgpu | **yes**: the wgpu native library, per platform |
| **C** | `OpenGlControlBase` | OpenGL 4.1 | none beyond Avalonia's own; but GL is deprecated on macOS |

The design leans to A or B and names C as the simplest to prototype (§8.3). **Do C first**, in hours,
as the baseline every counter is compared against. Then do A or B. If time allows both, **B before A**
on macOS, because one shader language across three OSes is the question B exists to answer.

**Do not add any native package to a project in `circuitrf.slnx`.** The spike lives in `tools/` for
exactly this reason.

---

## 2. `R-em3d27-2` — what the pane must do

1. **A million triangles**, from a real source: F0 case A's Gmsh mesh boundary (read the `.msh`, with
   a throwaway parser in the spike) replicated in a grid to reach 10⁶, in one vertex buffer.
2. **Orbit, pan and zoom** by mouse and trackpad. The camera is one matrix, and moving it uploads
   nothing.
3. **GPU picking.** Draw object IDs into an offscreen target and read back the one pixel under the
   cursor. Hovered objects highlight through a shader uniform.
4. **A translucent layer** over an opaque one (air and dielectric over metal), sorted per object, not
   per triangle.
5. **Live inside a real Avalonia window** with a text box beside it that keeps typing smoothly while
   the pane orbits. That is the "one compositor" property (§8.3). Also inside a **Dock** panel that is
   floated and re-docked, because that is where `NativeControlHost` fails (§8.3).
6. **Resize and DPI change** (drag between a Retina and a non-Retina display on macOS).

---

## 3. `R-em3d27-3` — counters, not timings (§8.6)

Instrument every candidate the same way. Assert these in a tiny harness inside the spike. These are
the same counters brief 28 will gate on, so write them to be lifted:
- **bytes uploaded per orbit frame = 0**;
- **managed allocations per frame on the UI thread**, from `GC.GetAllocatedBytesForCurrentThread`
  delta: record it, and target ~0;
- **UI-thread time per frame** spent in the pane's own code: record the distribution. It is reported,
  not asserted;
- **pick readback latency in frames** (1 is the target).

Measure all four in **Debug and Release**. The owner runs Debug, and a Debug build that stutters is
the one they will see.

---

## 4. `R-em3d27-4` — the findings document

`docs/design/em-3d-f2-spike-findings.md`, in the F0 findings' shape (answers in one screen, then
evidence):
- per route, per OS: did it host, did it pick, do the counters hold, what broke;
- **every native dependency** each route adds, with its licence and size per platform;
- lines of platform-specific code per route;
- Avalonia API surface used, and how stable it looks (anything marked unstable or internal);
- **a recommendation** for D3, and the runner-up;
- what the owner saw by hand on each OS: "does it feel fast" (§8.6).

---

## 5. `R-em3d27-5` — who runs what

- **This Mac (agent):** build all routes, run the counter harness headless where the platform allows,
  and write the findings. The agent **cannot see the pane** (overview §1e): it says so, and does not
  claim anything visual.
- **Owner, on macOS, Windows and Linux:** run each route's executable and record the six behaviours in
  §2 and the feel. The spike's README lists the exact commands per OS.

---

## 6. Gate

This is a spike, so the gate is the document, not tests in the suite.

1. `em-3d-f2-spike-findings.md` exists and answers every §4 point for every route attempted, with
   "not attempted" stated rather than omitted.
2. The spike does not build as part of `dotnet build` at the repo root, and no project in
   `circuitrf.slnx` references anything new (Firewall.Tests unchanged and green).
3. The owner's hand checks are recorded in the findings, per OS.
4. §11 Open 2 in `em-3d.md` is updated with the owner's decision.

## 7. Scope

- Throwaway code. Brief 28 lifts the counter harness and the hosting pattern, not the spike's scene
  code.
- No circuitRF document is read except the one F0 `.msh`.
