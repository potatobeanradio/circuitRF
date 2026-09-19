# Brief 7 — the clipboard, both ways, and the topology recognizer

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith7-n` · **Phase:** P1
**Area:** `src/Ui/Smith/` · **Depends on:** 5, 6 · **Blocks:** nothing
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §6

---

## 0. What this brief delivers

Three commands and one recognizer. The commands are calls into code that already exists; the recognizer is
the only genuinely new part and it is mostly refusals.

### `R-smith7-1` — the rule, stated as a rule

> **This tool writes no clipboard code.**

The owner instruction is explicit, and it is also the cheapest route: the schematic editor's copy path has
had a great deal of debugging invested in it across three platforms — Windows' `CF_ENHMETAFILE` in
particular, which must be written in a **single** P/Invoke session because Avalonia's `SetDataAsync`
empties the clipboard and keeps ownership — and none of that should be spent again.

`src/Ui/Match/MatchSchematicCopy.cs` and `src/Ui/Harmonica/HarmonicaClipboard.cs` are the two worked
examples of a tool pane's copy done correctly on this path. **Read one before writing this.**

---

## 1. `R-smith7-2` — copy the network out

Right-click the network strip ▸ **Copy**. Build the projection exactly as `MatchSchematicCopy` builds the
Designer's — real `EditableComponent`s at the coordinates brief 6's strip drew them at, real
`EditableWire` spine segments in the gaps between series bodies, one ground per shunt column — and hand it
to **`SchematicClipboard.CopyAsync`**.

That one call produces, simultaneously: the **schematic JSON** (pastes into a real `.csch` as real,
editable components), **SVG** and **PDF** vector (Keynote, and PowerPoint through the EMF path), and
**PNG** for everything else.

Two decisions about what the copied circuit *contains*:

- **The generator becomes a `TermG` with `Num=1` and `Z` = the generator impedance at the design
  frequency**, and **the load end becomes a `TermG` with `Num=2` and `Z` = Z₀_chart.** What lands in a
  schematic is a complete, runnable two-port, not a fragment with dangling ends.
- **No analysis card is copied.** A pasted selection is a fragment of a circuit, and the TestBench it
  lands in owns its analyses.

### `R-smith7-3` — a multi-row generator table is a lossy projection, and it is STATED

A `Term` carries one impedance. When the generator table has more than one row, the status strip says so
on copy, **naming the frequency that was used**. Stated rather than prevented: the copy is still the right
circuit at the design frequency, and that is what someone pasting into a presentation or a schematic
wants.

### `R-smith7-4` — the copy follows the mirror

`MatchSchematicCopy`'s own stated rule is that a copy is *the drawing on screen, not the flattened cell* —
it places every component at the coordinates the pane drew it at. Take that rule with it. Someone who
flipped the network to make a figure and then copied it would not thank us for un-flipping it on the way
out.

`MirrorX` travels on each component, so a 2-port's port 1 still faces the generator. The pasted circuit is
electrically identical either way; only its geometry is reflected.

---

## 2. `R-smith7-5` — copy the chart

Right-click the chart ▸ **Copy**, or Edit ▸ Copy with the chart focused, calls
**`PlotExporter.CopyPlotToClipboardAsync`** — PDF, SVG, the Data Display config JSON and a 2× bitmap, all
on the clipboard at once. Trajectories, grippers, targets, Q arcs and markers are all in the picture,
because they are all in the `Plot` and its overlay.

**This is where brief 5 `R-smith5-5` is collected.** That call opens with `if (container is null) return;`
— so without the `ContainerProvider` brief 5 set, this command produces **nothing, silently, and looks
like a success.** `R-smith7-10` gates it with real bytes for exactly that reason.

Every emitted SVG passes through `SvgFontNormalizer` on the way out of Skia's SVG device, as every other
export in this repository does. That is not optional and it is not this tool's business to know why.

### `R-smith7-5a` — narrow a COPY, never the shared object

If the copy hides chrome — grippers, targets, the Q arcs — it does so on a **clone** of the `Plot` and the
`RenderTheme`, never by mutating the live ones and putting them back.

This is `TechnologyCache`'s defect, which `render --layers` had to fix the same way: a cache hands back a
**shared** instance, so narrowing it in place quietly narrows every later use in the same process. It is
the class of bug that only appears on the **second** call — the first copy is perfect, the chart is then
missing its grippers until the document is reopened, and nothing reports anything.

---

## 3. `R-smith7-6` — paste a `.csch` selection in, or refuse it by name

Right-click the network strip ▸ **Paste**. `SchematicClipboard.PasteAsync` returns components and wires; a
recognizer decides whether they form a cascade this tool can represent, and either **replaces the network
wholesale** or **refuses with a sentence naming what stopped it.**

Build the net graph and require, in this order:

1. every component is a vocabulary element, a ground, or a `Term`/`Port`;
2. every non-ground net has degree 2, except the two end nets;
3. exactly two end nets exist and the walk between them is unique — **any branch is a refusal naming the
   net**;
4. every element hanging off the through path has its other pin on ground, and no other component does;
5. no component carries a parameter this tool cannot represent — an expression, a swept variable, a
   hierarchical reference — **a refusal naming the instance and the parameter**, because silently dropping
   an expression would change the circuit.

**The refusals matter more than the successes.** A permissive reader that accepted *part* of a paste would
replace a user's network with something that is not what they copied and report success — which is exactly
what `RailClipboard`'s marker guard exists to prevent, and why brief 1 built this tool's guard in its
shape.

### `R-smith7-7` — which end is the generator, and why it is not just "leftmost"

Decided by: a `Term`/`Port` with the **lowest `Num`**, if there is one; otherwise **the end that matches
the strip's current mirror setting** — leftmost by x when the drawing runs generator-left, rightmost when
it is mirrored (brief 6 `R-smith6-6`).

The geometric fallback has to be stated that way rather than as a bare "leftmost": **a flipped strip would
otherwise reverse every pasted network that carried no port, silently and half the time.** That is this
brief's own regression and it is gated.

The strip **states which of the two rules fired**, because they can disagree and the user is the only one
who knows which they meant.

### `R-smith7-8` — one paste is one undo entry

Restoring the entire previous network. Brief 5 `R-smith5-8`'s rule, applied to a discrete command instead
of a drag.

### `R-smith7-9` — Cut/Copy/Paste route by focus

Edit ▸ Copy means the chart when the chart has focus and the network when the network does. There is no
third meaning and no ambiguity to resolve at the command; the routing is the focused pane's.

---

## 4. The gate

`tests/Ui.Tests/Smith/SmithClipboardTests.cs` — one test per claim:

1. **`R-smith7-10` — real bytes, not a call.** After a chart copy, assert the clipboard carries non-empty
   SVG **and** that the SVG text contains a marker of this tool's own rendering. Asserting that
   `CopyPlotToClipboardAsync` was invoked would pass with a null container and an empty clipboard, which
   is precisely the failure mode.
2. **Round trip.** Copy a network, paste it back, and the element list is identical in type, placement,
   order and value.
3. **A mirrored network survives the clipboard**: copy while mirrored, paste into a **non**-mirrored
   strip, and get the same element list in the same order (`R-smith7-7`'s regression).
4. **Every refusal in `R-smith7-6` fires and names its object.** One test, five cases: a branch, a
   three-pin component, an unrepresentable parameter, a shunt element not on ground, three end nets. Assert
   the **sentence**, not just the failure — a refusal whose text does not identify the offending object is
   not a refusal anyone can act on.
5. **The copied circuit is runnable**: the generator `TermG` carries `Num=1` and the design frequency's
   impedance, the load `TermG` carries `Num=2` and Z₀_chart, and no analysis card is present.
6. **The multi-row note fires** when the table has more than one row, naming the frequency used
   (`R-smith7-3`).
7. **One paste is one undo entry** and a single Undo restores the whole previous network.

---

## 5. What this brief must NOT do

- **No clipboard code.** Not a format, not a P/Invoke, not a second Avalonia session. `R-smith7-1`.
- **No second SVG font repair**, no second page-framing pass, no per-call colour parameter —
  `ClipboardRenderPolicy.Resolve()` is one app-wide setting.
- **No partial paste.** A recognizer that gets most of it is the defect, not the feature.
- **No new marker/guard format.** Brief 1 built `SmithClipboard`; if this brief needs a payload of its own,
  it uses that one.

---

## 6. On completion

Findings to `src/Ui/RESOLVED.md`. **Never a `CLAUDE.md`.**
