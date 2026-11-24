# Brief DDL-6 — the knob, the illuminated button and the readout

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl6-<n>` · **Depends on:** DDL-5
**Area:** `src/Render/DataDisplay/Controls/` (`KnobControl`, `ButtonControl`, `ReadoutControl`, `ControlRenderer`),
`src/Render/DataDisplay/TraceExpression.cs`, `Models/Trace.cs` and `TraceConfig` (visibility binding), the control
inspector and the trace card in `src/Ui/DataDisplay/`.

## The gap

After DDL-5 a display has one kind of input, the slider. A panel built to be explored also wants a rotary input in
less space, an on/off input, and a place to show a value as a number rather than as a curve. In front-panel terms these
are a control and an indicator. The 0/1 of a button has nowhere to go yet: it is not an index into anything, and
trace expressions take no display values.

## Requirements

Every control here takes DDL-5's frame rules: operate versus arrange, S/M/L and the gripper, and **Lock Position**.

**R-ddl6-1 — The knob.** `KnobControl` has the slider's two domains (bound to an axis, or a range) and its value rules.
- Its face is a dial over a 270° sweep, with tick marks, an indicator line and the value under it.
- A vertical drag turns it: up increases. The rate is one full sweep per 200 px, and one tenth of that with Ctrl. The
  mouse wheel steps one stop (a bound knob) or one step (a range knob).
- On a bound knob, a click on a tick sets that stop.
- The face operates and the frame arranges (DDL-5 R-ddl5-4).

**R-ddl6-2 — The button.** `ButtonControl` is `Toggle` (click flips it) or `Momentary` (1 while pressed). It has an
illumination colour from a palette picker (any colour, with eight presets), and a label on or under the face.
- **Off:** a dim face, the colour at low saturation.
- **On:** the face lit in the colour, with a soft glow beyond its edge.

The glow is a radial gradient from the colour at partial alpha to transparent, drawn on a disc larger than the face,
and a second, tighter gradient makes the face's own bloom. No blur filter is used (DDL-0 D7), so the glow survives SVG
and PDF export as vector gradients. Its value is 0 or 1, a `Scalar` with no unit.

**R-ddl6-3 — The readout.** `ReadoutControl` is an indicator: it shows the value of a link source (a marker, a control,
`selected`) as text, with its unit and SI prefix, and optionally its name. It can also show a coordinate the source
supplies (`m1 · Vgs`), or a marker's reading on its own trace (the info box's value line). It has no input. Its size
presets scale the digits. It is the cheap way to put "f = 2.437 GHz" on a panel beside a slider.

**R-ddl6-4 — Display values in expressions.** `TraceExpression` accepts `@name` as a scalar operand, as well as a slice
token. It is the source's value in base SI: a control's value, a marker's X value, a button's 0 or 1. It is injected
through `Evaluator.InjectResolved`, the path the expression engine already takes, never by string substitution (root
`CLAUDE.md`, "never string substitution"). So `if(@button1, dB(SP1.S(2,1)), dB(SP1.S(1,1)))` switches what a trace draws,
and `Pout - @slider1` offsets a curve. The link graph (DDL-1 R-ddl1-8) counts these operands, so cycles are refused
here too.

**R-ddl6-5 — Shown by a control.** A trace may be bound to a source for visibility: shown when it is non-zero (or when
it is zero, inverted). This is set from the trace card's visibility toggle, and persisted as `TraceConfig.ShownBy` and
`ShownByInverted`. A button can then switch a group of traces on and off without an expression. A hidden trace still
resolves, so switching it on is instant, and the legend marks it as hidden rather than dropping it.

**R-ddl6-6 — A two-valued pin.** A pinned axis linked to a button takes index 0 or 1 (DDL-1 rule 3, on the button's 0/1),
so a button can flip between two cases of a two-element axis (two corners, two bias points written as an axis).

## Gates

1. **Knob.** UiSnapshot PNGs at S, M, L and after a free resize, looked at. Scripted vertical drags of 100 px move a range
   knob half its span, and 10 px with Ctrl move it a twentieth. The wheel steps a bound knob one stop.
2. **Button glow, vector.** Export a lit button to SVG: the glow is a `radialGradient`, not an `<image>`. The PNG of the
   same export is looked at in both the light and the dark theme.
3. **Momentary.** Press and hold: the value is 1. Release: 0. A linked trace follows both.
4. **Expression.** `if(@button1, dB(SP1.S(2,1)), dB(SP1.S(1,1)))` draws S21 when lit and S11 when not. A trace whose
   expression reads `@k1`, where k1 is a knob whose domain is bound to that same trace, is refused as a cycle.
5. **Readout.** A readout of m1 updates as m1 is arrow-stepped, with the unit and the prefix the marker's info box uses.

## Not here

Plugin-declared controls (DDL-7); text input or drop-down controls (none of the cases needs one yet, DDL-0 D8); colour
that follows a value (a lamp that turns red past a limit) — a later brief if a panel asks for it.
