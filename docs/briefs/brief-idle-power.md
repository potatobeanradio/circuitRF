# Brief — circuitRF should do no work while nothing changes (idle CPU, GPU and wake-ups)

**Tag:** `R-idle-n` · **Series:** standalone; follows brief-em3d-99, which found it.
**Area:** `src/Ui` as a whole, starting with `src/Ui/Viewer3D/` (`Viewer3DOverlay.cs`, `Viewer3DPane.cs`,
`Viewer3DViewModel.Fields.cs`, `Metal/MetalViewer3DBackend.cs` `WaitReusable`), every `IsIndeterminate` `ProgressBar`, every
`DispatcherTimer` / `System.Threading.Timer`, style `Transitions`/`Animation`s, and the Skia canvases' invalidation.
**Depends on:** — · **Blocks:** —

## Why

On a laptop, battery life is set mostly by how often the CPU and GPU are woken, not by how hard they work once awake. A
window that wakes 50 times a second keeps the package out of its deep idle states even at a few percent CPU. The rule this
brief establishes: **when nothing on screen is changing, circuitRF draws nothing, ticks nothing, and polls nothing.** Every
change that needs a redraw asks for one, and nothing redraws "just in case".

## What is established (measured 2026-10-02, owner's Mac, Debug build, solved 3D Connector `.c3d` with field plots drawn, untouched)

- **`top -pid <pid> -stats pid,cpu,idlew,power`: ~9 % CPU, ~50 idle wake-ups per second, energy impact ~11.** A still
  window should be near 0 % with a handful of wake-ups.
- **Avalonia's render loop never goes to sleep.** In a 47 s idle stretch of a `dotnet-trace` recording, the render timer ran
  `TimerTick → ServerCompositor.RenderCore` throughout (~40 ms/s). About 1 s of the 47 was spent COMPOSING the window, with
  `BeginRenderingSession`, so real GPU work: `ServerCompositionTarget.Render` drawing a gradient brush (`ConfigureGradientBrush`),
  glyph runs and the 3D surface visual.
- **On the UI thread, `MediaContext.Render` ran at idle,** including `MediaContextClock.Pulse` (some animation clock had a
  subscriber) and `Compositor.CommitCore → CompositingRenderer.UpdateCore → RenderDataDrawingContext.GetRenderResults`, which
  spent its time hashing NEW objects (`RuntimeHelpers.GetHashCode` on first use). Something was re-recording its drawing over
  and over, with freshly made brushes or pens.
- **No circuitRF method was caught in a sample on the UI thread at idle,** so either the work is Avalonia's own (an animation
  or transition), or a circuitRF `Render` is too quick to be caught at 1 ms sampling but runs every frame.
- brief-em3d-99 already made the 3D pane render on demand: at rest it used to render and present at display rate. Keep that.

## Suspects, in order (hypotheses, not findings)

1. **The 3D legends (owner's suggestion).** `Viewer3DOverlay.Render` (line ~555) builds a new `LinearGradientBrush` and
   `GradientStops` on every render, plus text. That matches the render thread's gradient and glyph work and the new-object
   hashing. The overlay invalidates on `Viewer3DPane.FramePresented` (`C3dEditorView.axaml.cs:42`), so it only redraws
   continuously if frames are presented continuously, or if something else invalidates it. Check both. Whatever the cause,
   build the legend's brushes once per legend change, not once per render.
2. **An Avalonia animation clock kept alive.** Indeterminate `ProgressBar`s hidden only by `IsVisible`: three in
   `C3dEditorView.axaml` (lines ~674, 736, 784), more in `RailRfWindow.axaml`, `MessagesView.axaml`, `InstancesToolView`,
   `ImpedanceToolView` and dialogs. Avalonia 12 PAUSES style animations on controls that are not effectively visible
   (`AnimationInstance`, `PlaybackBehavior.Auto`; decompiled). Whether a paused animation still keeps
   `MediaContext`'s clock pulsing every frame is the question: read `MediaContext.RenderCore` / `_clock.HasSubscriptions`.
   If it does, make `IsIndeterminate` follow the same binding as `IsVisible`. Also the `Transitions` in
   `PlotInspectorView.axaml` and `Controls/ScrollOffsetAnimator.cs` (a Render-priority `DispatcherTimer`): confirm each stops.
3. **The field animation timer.** `Viewer3DViewModel.Fields.cs` `OnFieldPlayingChanged` starts a 16 ms
   `System.Threading.Timer` while a field plays. By design it runs at 60 fps while playing. It must stop when the view cannot
   be seen: its tab in the background, its window minimised, or the document closed. Confirm it does, and that it was not
   running in the recording (it would show `AnimationTick` on the UI thread).
4. **A spin loop when the compositor stops compositing.** `MetalViewer3DBackend.WaitReusable` busy-waits
   (`Thread.SpinWait(100)`) up to 250 ms for the compositor to release an image. If the window is minimised or occluded and
   the compositor stops releasing images, a skipped frame posts `RequestFrame` again (`Viewer3DPane.RenderLoop`), so a frame
   request mid-minimise could become a loop of 250 ms busy-waits. Check whether that happens; if it does, a skip must not
   re-request until the window is visible again, and the wait should block rather than spin (the shared event can notify).
   The D3D11 and Vulkan backends have their own `WaitReusable`; read them too.
5. **Anything else that redraws without a change:** the schematic, layout, symbol and Data Display canvases (a hover pulse,
   a marquee, a cursor), the Messages panel, the Project Tree's watcher debounce (`WorkspaceViewModel.Solved.cs`), and
   `DeferredProgressRow`. `grep -rn "DispatcherTimer\|System.Threading.Timer\|InvalidateVisual" src/Ui` is the starting list.

## What to do

**R-idle-1 — Find every idle wake-up source, by state.** Measure each state below with nothing touched for 30 s. Record CPU,
idle wake-ups and energy impact from `top`, plus a `dotnet-trace` breakdown that names the source when one is not quiet:
1. Empty workspace, no documents.
2. A schematic open. A layout open. A Data Display open.
3. A `.c3d`, unsolved. The solved 3D Connector `.c3d` with field plots drawn, not playing. The same with the plot playing
   (expected busy), then paused.
4. That `.c3d` as a BACKGROUND tab behind a schematic. Its window minimised. Its window fully covered by another app.
5. railRF's window, harmonicaRF's window, the Messages panel with and without a progress row.

A state that is not quiet is a finding: name what keeps it awake.

**R-idle-2 — Make each one quiet.**
- Redraw on change only. A legend, a colour bar or any overlay builds its brushes when its data changes, not per render.
- An animation runs only while it can be seen and only while it moves something. Bind an indeterminate bar's
  `IsIndeterminate` to its visibility condition.
- Timers stop when their work is done or invisible, never "tick and check".
- Nothing busy-waits.
- If a source is Avalonia's own and cannot be fixed from outside, say so with the decompiled evidence. Do not work around it
  with reflection unless the owner agrees; brief-em3d-99's `SurfaceUpdateFlush` is the one sanctioned case.

**R-idle-3 — Write the rule down.** Add a short "idle is quiet" invariant to `docs/design/ui-architecture.md`: what may tick
at idle (nothing), and the two worked examples (render-on-demand in `Viewer3DPane`, and whatever this brief fixes).

## How to measure (the method that worked in brief-em3d-99)

The owner launches the Debug build (`src/Ui/bin/Debug/net10.0/CircuitRF.Ui`). This session cannot launch the GUI. The owner
runs the Debug build, so measure Debug; spot-check Release, because Debug inflates CPU. Wake-ups do not depend on the build.

```bash
P=$(pgrep -f "src/Ui/bin/Debug/net10.0/CircuitRF.Ui$" | head -1)
top -l 16 -s 2 -pid $P -stats pid,cpu,idlew,power,threads | grep -E "^ *$P"     # idlew is CUMULATIVE: take differences
~/.dotnet/tools/dotnet-trace collect -p $P --profile dotnet-sampled-thread-time --duration 00:00:00:30 -o idle.nettrace
~/.dotnet/tools/dotnet-trace convert idle.nettrace --format Speedscope -o idle.json
```

Break it down with brief-em3d-99's per-second script, and add a stack histogram per thread. **Caution, learned here:** in
`dotnet-sampled-thread-time`, a thread parked in native code can carry its last managed frame labelled `CPU_TIME`. One thread
read as "47 s of CPU in 47 s" and was the display-link thread asleep. Trust `top` for CPU and wake-ups, and the trace only
for WHERE.

**Avalonia's dirty-rect overlay** shows what redraws: `TopLevel.RendererDiagnostics.DebugOverlays =
RendererDebugOverlays.DirtyRects` flashes every region the compositor redraws. Gate it behind an environment variable (for
example `CRF_DIRTY_RECTS=1`) so the owner can watch a still window and see what keeps flashing. That may find suspect 1 or 5
in one glance. Leave the switch in, documented in `src/Ui/RESOLVED.md`; it costs nothing when unset.

`sudo powermetrics --samplers cpu_power,gpu_power -i 2000 -n 10` gives package and GPU power, if the owner wants a battery
figure rather than a proxy (it needs sudo, so the owner runs it).

## Gate

- For every R-idle-1 state except "field playing": **about 0 % CPU, idle wake-ups under ~5/s in Release, and no
  `RenderCore` composing** (no `BeginRenderingSession`) after 1 s of stillness. If a state cannot reach that, the brief says
  why and what it would take.
- A background, minimised or covered `.c3d` with a playing field does no 3D work.
- Nothing that updates today stops updating: hover, selection, field animation while visible, a finished field build, a
  theme change, a run's progress. The owner checks these by eye; list them in the report.
- Tests count, never time (memory: no new timing benchmark tests). Where the property is structural, hold it with a counter
  or a pure test: for example, the legend's brush is the same instance across two renders with unchanged data; the field
  timer is disposed when the view is hidden; no indeterminate bar animates while its condition is false. Run only the new
  test classes, with `--filter` (memory: never the full suite or all of Ui.Tests).

## On completion

Findings go in `src/Ui/RESOLVED.md`, never CLAUDE.md. Do not commit unless the owner asks.
