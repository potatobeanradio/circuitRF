# Brief — harmonicaRF Round 6E: axis limits, autoscale, and the DCIV dialog

**Read first:** `src/Ui/Views/Dialogs/HarmonicaDcivSweepsDialog.axaml` + `.axaml.cs`,
`src/Harmonica/DcivFamily.cs`, `src/Harmonica/CircuitModel.cs` (`HarmonicaSettings`'s existing
`DcivVgsMin/Max/Steps`, `DcivVdsMin/Max/Steps` — the exact pattern this brief copies),
`src/Harmonica/CharmIo.cs` (the settings block, lines ~205-295 and ~397-410),
`src/Ui/Harmonica/Renderers/HarmonicaPanelRenderer.cs` (`BuildLoadlinePlot`, `BuildPowerSweepPlot`,
`AutoScale`, `PinAxisPin`).

**Depends on R6B §4** (the fly-menu dispatch) and **R6D §3/§4** (panel titles and the power-sweep title
menu). Land those first — this brief adds items to menus those briefs create, and adds a second
axis-limits dialog alongside the DCIV one.

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` only if warranted.

---

## 1. Bug — the Drain Sweep fields are pinned to the bottom of the DCIV dialog

`HarmonicaDcivSweepsDialog.axaml` declares `RowDefinitions="Auto,Auto,*,Auto"`:

| row | content | height |
|---|---|---|
| 0 | "Gate sweep (Vgs)" title | Auto |
| 1 | Vgs min/max/steps | Auto |
| 2 | **"Drain sweep (Vds)" title** | **`*` — takes all remaining space** |
| 3 | Vds fields + error text + buttons | Auto |

So the Vds title floats at the top of a stretched row and its fields are shoved to the bottom of the
window. The Vgs pair reads correctly only because both its rows are `Auto`.

**Fix:** give the Vds title and its field grid the same `Auto`/`Auto` treatment as the Vgs pair, and put
the `*` spacer *between* the fields and the button row (or drop the `*` entirely and let the window size
to content, which is what `CanResize="False"` implies anyway). The error text stays directly above the
buttons.

`tests/Ui.Tests/Harmonica` cannot instantiate the dialog, so pin what is pinnable: a source-scan test in
the shape of the existing XAML-scanning tests (`HarmonicaR3cStripTests.ColumnsPanel_…` is the
precedent) asserting each section title's row index is immediately followed by its field row, and that
no title row is the `*` row. **Strip comments before scanning** — a source-scan test that matches text
inside an XML comment is the trap H8 already recorded.

---

## 2. Persisted axis limits + autoscale — one mechanism, two plots

The owner asked for the same feature twice, for the DCIV plot and for the power-sweep plot. Build it
**once** and use it twice. Divergent copies of this will drift.

### 2.1 The stored state

Per plot, on `HarmonicaSettings` (`src/Harmonica/CircuitModel.cs`) and persisted in the `.charm`
settings block, following `DcivVgsMin`'s existing nullable-defaulted convention exactly (absent means
"never set", never zero):

| plot | fields |
|---|---|
| DCIV / loadline | `DcivXMin`, `DcivXMax`, `DcivYMin`, `DcivYMax`, `DcivAutoscale` |
| Power sweep | `PowerSweepXMin`, `PowerSweepXMax`, `PowerSweepYMin`, `PowerSweepYMax`, `PowerSweepY2Min`, `PowerSweepY2Max`, `PowerSweepAutoscale` |

`Autoscale` defaults to **false**. The owner is explicit about why: *"Autoscale should be off by
default, meaning that the axes are never changed while user drags markers."* The axes breathing during
a drag is the symptom being removed.

The power sweep has a **right** axis too, so it gets its own pair of limits. Whatever §2.3 does to the
X window must be done identically to `WindowSecondary`'s X, for the reason `PinAxisPin` already
records: the two windows must share one X mapping or gain and efficiency separate horizontally.

### 2.2 What happens when limits have never been set

This is the case the owner's wording does not cover, and getting it wrong makes a fresh document
unusable. Rule:

> **Autoscale off + no stored limits** → compute limits once from the first frame that has data
> (exactly what `AutoScale` computes today), store them as the document's limits, and hold them from
> then on.

So a new document looks like it does today on the first frame, and then stops moving. A drag never
recomputes them. **Do not** fall back to "autoscale every frame when limits are absent" — that is the
current behaviour and the thing being fixed.

### 2.3 What autoscale ON means

`Autoscale` ON recomputes limits from the current data **every published frame** — today's behaviour —
and **writes the computed values into the stored limits**, so turning it back off freezes exactly what
is on screen. That is what makes the fly-menu toggle useful: flip it on, let it fit, flip it off, drag.

Turning autoscale on must also be enough to make a plot that has been dragged out of view usable
again — a one-shot "fit now" is not being asked for and should not be added as a fourth control.

### 2.4 Where it applies

`BuildLoadlinePlot` and `BuildPowerSweepPlot` both call `AutoScale(plot)`. After that call, apply the
stored limits (when autoscale is off) or capture them (when it is on). Interaction with the two
existing X-axis behaviours in the power sweep, in this order:

1. `AutoScale`;
2. `PinAxisPin` (pins Pin-domain X to the sweep's configured range);
3. **R6D §2's right-edge headroom**;
4. **this brief's stored limits, which override all of the above when set.**

An explicit user limit is the user's, and nothing may silently correct it. Say so in the code — a
future reader will otherwise "fix" the ordering.

---

## 3. The two dialogs

### 3.1 DCIV Sweeps dialog gains axis limits

Add an **Axis limits** section to `HarmonicaDcivSweepsDialog`: X min/max, Y min/max, and an
**Autoscale** checkbox that disables the four boxes while it is checked. The sweep-range fields
(Vgs/Vds min/max/steps) stay exactly where §1 puts them.

### 3.2 A new Power Sweep axis dialog

The owner asks for *"a Power Sweep dialog, accessible by flymenu just like the DCIV flymenu, that
allows the user to change the X and Y axis limits (and right-y axis limits)"*.

**Do not overload the existing `Display ▸ Power Sweep…` dialog** (`HarmonicaPowerSweepDialog`) — that
one configures the *sweep* (PinStart/PinMax/step), which is physics, not display. Add a separate
`HarmonicaPowerSweepAxesDialog`: X min/max, left-Y min/max, right-Y min/max, Autoscale checkbox.

### 3.3 Validation, both dialogs

- min < max on every pair, finite, parseable;
- reject-and-keep-the-text with a stated reason in the dialog's existing `ErrorText` block — never a
  silent substitution and never a clamp the user cannot see (this is the same rule R6A §6 applies to
  the Set Termination dialog, and for the same reason);
- committing on `LostFocus` and on Enter, as `OnFieldLostFocus` / `OnFieldKeyDown` already do here.

---

## 4. Fly-menu entries

Per R6B §4's dispatch:

- **loadline/DCIV panel** right-click → `Copy` (R6D §6), `Autoscale` (checkbox), `DCIV Sweeps…`;
- **power-sweep panel title** right-click → `Power Sweep` / `Time Domain` (R6D §4), separator,
  `Autoscale` (checkbox), `Axis Limits…`; and `Copy` on the panel body menu.

`Autoscale` in the menu writes the same setting the dialog's checkbox does — one property, two
surfaces, no shadow state.

In **Time Domain** mode (R6D §5) the power-sweep axis limits describe a different quantity entirely
(time / volts / amps rather than power / dB / %). **Store a separate set of limits for that mode**
rather than reusing the power-sweep set — switching modes must not corrupt the other mode's axes.
Name them plainly (`TimeDomainXMin`, …) and persist them the same way. Say in your report if you find a
cheaper representation that keeps the two modes independent.

---

## 5. Gates

1. `dotnet test tests/Ui.Tests --no-build`, `dotnet test tests/Harmonica.Tests --no-build`,
   `tests/Firewall.Tests` green.
2. Round-trip tests in `tests/Harmonica.Tests/ContextAndPersistenceTests.cs`'s shape: every new setting
   survives write → read; a `.charm` written before these fields existed opens with all of them absent
   and behaves as §2.2 says.
3. A test that pins the §2.4 precedence: with a stored X limit set, neither `AutoScale`, nor
   `PinAxisPin`, nor the headroom fraction changes the window.
4. A test that pins the anti-breathing property directly: publish two frames whose data ranges differ
   materially, with autoscale off, and assert `plot.Axes.Window` is identical across both — for both
   plots. This is the owner's actual complaint expressed as an assertion.
5. Owner check: the DCIV dialog's Drain Sweep fields sit under their title; axis limits typed into
   either dialog hold through a drag and survive save/reopen; the Autoscale checkbox and the fly-menu
   item agree.
