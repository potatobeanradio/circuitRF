# Brief 99 — A live window resize stutters while a 3D view is on screen (macOS)

**Tag:** `R-em3d99-n` · **Series:** standalone investigation, found while building brief 98.
**Area:** `src/Ui/Viewer3D/Viewer3DPane.cs`, `src/Ui/Viewer3D/Metal/MetalViewer3DBackend.cs`, Avalonia 12.0.3's composition
GPU interop on macOS (`CompositionDrawingSurface.UpdateWithTimelineSemaphoresAsync`), `docs/design/em-3d.md` §8.3–8.4.
**Depends on:** — · **Blocks:** —

**Status: RESOLVED 2026-10-02.** The cause was Avalonia drawing a surface update one render late. The fix and the
measurements are in `src/Ui/RESOLVED.md` ("Live window resize with a 3D view").

## The symptom (owner report, 2026-10-02)

- Resizing a WINDOW by dragging its edge is chunky when that window shows a `.c3d` (a docked 3D editor tab, or a torn-off
  `.c3d` window). The owner's test document was the solved 3D Connector example with field plots drawn.
- With no `.c3d` open, the same window resizes very smoothly.
- **Dragging a splitter is smooth** (the Object tree panel, the Library panel), although that resizes the 3D pane just as
  much. Orbit, pan and zoom are smooth too.
- **Not new.** Yesterday's build (`f58f6553`, before brief 98) is just as chunky. Brief 98's glyphs and status checks are
  ruled out: they took under 2 ms in total across a 43 s recording.
- The owner runs the **Debug** build (memory: owner-runs-the-debug-build). macOS on Apple silicon, Metal backend.

## What is established (measured, not guessed)

All measurements were taken with `dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time` on the owner's live
Debug app while they dragged, then converted to Speedscope and broken down per thread and per second (script below). Three
threads matter: the UI thread (`Program.Main`), Avalonia's render thread (`ThreadProxyRenderTimer.RenderTimerThreadFunc`),
and the pane's own render thread (`Viewer3DPane.RenderLoop`).

**1. The stall is lock contention between macOS's synchronous resize paint and Avalonia's render thread.** During a live
resize, Avalonia.Native paints synchronously on the UI thread: `IAvnTopLevelEvents.Paint` →
`ServerCompositor.RenderReentrancySafe`, which takes the compositor lock. Avalonia's render thread was meanwhile inside
`ServerCompositor.RenderCore` → `SkiaMetalRenderTarget.BeginRenderingSession`, blocked waiting for a Metal drawable WHILE
HOLDING that lock. The UI thread's `Monitor.Enter_Slowpath` under `Paint` reached 4.2 s of 43 s (one second alone: 397 ms).
The working theory: during live resize the layer's drawables are not returned until the synchronous paint's transaction
commits, so each side waits for the other until something times out. It is not proven.

**2. With no `.c3d`, Avalonia's render thread barely runs during a resize** (render ticks ~0–60 ms/s). The synchronous paint
draws everything (`Paint` ~100–150 ms/s). That is why ordinary windows resize smoothly.

**3. With a `.c3d`, the render thread composes during the resize.** In the original code it did so at 600–1000 ms/s, with
`BeginRenderingSession` waits of ~500 ms/s, even AT REST.

| Recording | Render-thread ticks, at rest | Metal wait, at rest | Render-thread ticks, dragging | Metal wait, dragging |
|---|---|---|---|---|
| Original code (today and yesterday's build) | 550–1000 ms/s | ~500 ms/s | ~900 ms/s | 500–900 ms/s |
| With render-on-demand (below, in the working tree) | ~40 ms/s | ~10 ms/s | 600–800 ms/s | 500–577 ms/s |

**4. The 3D frame itself is cheap.** The pane's render thread was idle 99% of the time. Rebuilding the swapchain images on
a size change (`CreateImages` + the compositor's `ImportImage`) cost 78 ms over a 20 s drag. The timeline-semaphore
handshake is correct: the frame signals `_readyEvt` with value N, and `Present` waits for N and releases with N.

**5. The 3D view model already requests a frame at every change of what is drawn** (63 `FrameRequested`/`RequestFrame` call
sites: hover, pick results, scene adoption, selection, field builds via `LayersChanged`, the field-animation timer, measure,
snap). Nothing relied on the pane's constant redraw.

## What was tried

| Attempt | Result | State |
|---|---|---|
| **Render on demand.** `Viewer3DPane.OnTick` planned a new frame on every tick it was not busy, and the render thread's `Post(RequestFrame)` after each frame kept that going, so the pane rendered at display rate forever. Now `RequestFrame` sets `_dirty`; a tick plans only when dirty, on a size change, or while orbiting; a finished frame queues `QueueTick`, which presents it and plans nothing new. | **At rest: fixed** (render ticks 600–1000 → ~40 ms/s). **Resize: still chunky.** Not yet confirmed by eye that nothing goes stale. | **In the working tree, uncommitted.** Keep it unless this brief finds a reason not to. If anything ever updates only on the next mouse move, that change is missing its `FrameRequested`: add it at the change, and never restore the constant loop. |
| Gate brief 98's solved checks on window motion (`src/Ui/Controls/WindowMotion.cs`), and make the glyphs layout-stable. | No visible change, as expected from finding 3. | In the working tree; harmless, keep. |
| **Hold frames during window motion**: no new images or frames, and the compositor stretches the last image; catch up 150 ms after the drag stops. | Owner rejected it: the 3D image distorts while dragging. | Reverted. |
| **Draw the frame as ordinary control content during a live resize**: render offscreen at each size step (a reused texture and shared buffer, BGRA copied straight into a `WriteableBitmap`), hide the composition visual, switch back once still. | Still chunky. `DrawStill` cost 19% of the UI thread, so each step ran long and the render thread composed between paints again (24% `Monitor.Enter_Slowpath`, 23% `BeginRenderingSession`). The object also moved to a wrong place in the view while resizing: probably the plan's camera aspect against the pane's, not investigated. | Reverted. |

## What to find out

**R-em3d99-1 — Why does Avalonia's render thread compose during a live resize when a 3D surface is present, and not
otherwise?** Candidates to check, in order:
1. Each 3D present is a surface update (a server job) committed between synchronous paints. The render timer picks it up and
   composes asynchronously, instead of the next synchronous paint consuming it. Check whether `RenderReentrancySafe` applies
   pending batches and server jobs before it renders. If it does, the fix may be to present only from inside the resize
   step, synchronously (render on the UI thread in the bounds change, wait for the GPU, `Present` in the same pass).
2. Whether Avalonia.Native pauses its render timer during live resize for ordinary content but a pending
   `RequestCompositionUpdate`, or a dirty `CompositionDrawingSurface`, keeps it running. Read Avalonia 12.0.3's
   `DefaultRenderLoop`, `ServerCompositor`, `MediaContext` and Avalonia.Native's live-resize path (`TopLevelImpl` `Paint`,
   `viewWillStartLiveResize` / `inLiveResize`). Decompiling works: `~/.dotnet/tools/ilspycmd -t <Type> <dll>`; the
   assemblies are in `~/.nuget/packages/avalonia*/12.0.3/lib/net10.0/`.
3. `presentsWithTransaction` on Avalonia's own `CAMetalLayer` during live resize, and the drawable pool size: what
   `BeginRenderingSession` actually blocks on (`nextDrawable`?).
4. Whether Avalonia has an issue or fix for exactly this ("live resize", "CompositionDrawingSurface", "GPU interop",
   "macOS", "stutter") in a release after 12.0.3.

**R-em3d99-2 — Fix it without distorting the image.** The owner's bar: the 3D view redraws at the new size during the drag,
as smoothly as an orbit, with no stretching or misplacement. Splitter drags and camera operations must stay as they are.

**R-em3d99-3 — Confirm render-on-demand by eye**: hover highlight, selection, field animation, a field plot finishing its
build, a placed layout changing on disk, a theme change. Each must update without moving the mouse.

## How to measure (the method that worked)

1. The owner launches the Debug build (`src/Ui/bin/Debug/net10.0/CircuitRF.Ui`). Wait for the new pid and record:
   ```bash
   for i in $(seq 1 1200); do P=$(pgrep -f "src/Ui/bin/Debug/net10.0/CircuitRF.Ui$" | head -1)
     [ -n "$P" ] && { sleep 20; ~/.dotnet/tools/dotnet-trace collect -p $P --profile dotnet-sampled-thread-time \
       --duration 00:00:01:00 -o run.nettrace; exit 0; }; sleep 1; done
   ```
   Run it in the background (it re-invokes you when it exits) and ask the owner to open the `.c3d` and drag a window edge
   for ~20 s inside the recording. An A/B in one recording is the most useful: drag, then close the `.c3d`, then drag again.
2. `~/.dotnet/tools/dotnet-trace convert run.nettrace --format Speedscope -o run.json`
3. Break it down per second with the script below. It finds the three threads by a frame each contains. Columns: UI CPU,
   UI time in the synchronous `Paint`, `Viewer3DOverlay.Render`, UI lock waits, render-thread ticks, render-thread Metal waits.
4. To compare with an older version without touching the working tree, build it in a worktree:
   `git worktree add --detach <scratch>/old <commit> && dotnet build <scratch>/old/src/Ui`. It has its own
   `geometry-kernel` folder. Remove it afterwards with `git worktree remove`.

Headless resizing (`tools/DocGen`'s `HeadlessHost` driving `WorkspaceWindow`) does NOT reproduce it: ~34 ms per step with or
without a `.c3d`. It has no Metal surface and no native live resize. Do not use it to judge a fix.

```python
# per-second breakdown of a Speedscope export: python3 tl.py run.speedscope.json
import json, collections, sys
d = json.load(open(sys.argv[1])); frames = [f['name'] for f in d['shared']['frames']]
def intervals(p):
    stack, last, out = [], None, []
    for e in p['events']:
        t = e['at']
        if last is not None and stack: out.append((last, t, tuple(stack)))
        if e['type'] == 'O': stack.append(e['frame'])
        elif stack and stack[-1] == e['frame']: stack.pop()
        elif e['frame'] in stack: stack = stack[:len(stack) - 1 - stack[::-1].index(e['frame'])]
        last = t
    return out
def find(marker):
    for p in d['profiles']:
        iv = intervals(p)
        if any(any(marker in frames[f] for f in st) for _, _, st in iv[:20000]): return iv
    return []
def per_sec(iv, target):
    c = collections.Counter()
    for a, b, st in iv:
        if any(target in frames[f] for f in st):
            s = int(a // 1000)
            while a < b:
                e = min(b, (s + 1) * 1000); c[s] += e - a; a = e; s += 1
    return c
main, rt = find('Program.Main'), find('RenderTimerThreadFunc')
cols = [per_sec(main, 'CPU_TIME'), per_sec(main, 'IAvnTopLevelEvents.Paint'), per_sec(main, 'Viewer3DOverlay.Render'),
        per_sec(main, 'Monitor.Enter_Slowpath'), per_sec(rt, 'DefaultRenderLoop.TimerTick'), per_sec(rt, 'BeginRenderingSession')]
print('sec UIcpu paint overlay UIlockWait renderTick metalWait')
for s in range(61): print(f'{s:3d} ' + ' '.join(f'{c[s]:6.0f}' for c in cols))
```

## Gate

- The owner drags a window edge with the solved 3D Connector `.c3d` (field plots drawn) and calls it smooth, with the 3D
  image redrawn at the new size during the drag (no stretching, no misplacement).
- A recording of that drag shows the UI thread's lock waits under `Paint` near zero, and the render thread no longer
  blocked in `BeginRenderingSession` for most of each second.
- Splitter drags, orbit, pan and zoom unchanged. At rest the render thread stays near idle (render-on-demand).
- No new test needs a GPU. If a structural property can be counted (for example, no async present during a live resize),
  assert the counter, not a time (memory: no new timing benchmark tests).

## On completion

`src/Ui/RESOLVED.md` (its 2026-10-02 entry on the 3D pane holds the measurements above), never CLAUDE.md. Do not commit
unless the owner asks.
