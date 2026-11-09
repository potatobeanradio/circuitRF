# Brief NZ-2 — gain and stability circles beside the noise circles

Series: `brief-nz-0-overview.md`. Independent of the other briefs.

**Why.** An LNA's input match is a trade between three things on the source plane: noise figure, available gain and
stability. `ns_circle()` (PR #50) draws the first. Nothing draws the other two, so the trade cannot be seen on one
chart. The expression library has `max_gain`, `mu`, `mu_prime` and the stability factor, but no circle for any of them.

## What to build

### 1. Circle functions

All are in the expression engine, next to `ns_circle` in `src/Core/Expressions/Evaluator.Network.cs`, with the math in
`src/RfCore` beside `NoiseMetrics`. Each returns a complex point list on a new point axis, exactly as `ns_circle` does,
so a Smith-chart trace draws it with no new plotting code.

| Function | Gives |
|---|---|
| `ga_circle(SP1.S, GdB[, npts])` | Constant available-gain circle on the SOURCE plane |
| `gp_circle(SP1.S, GdB[, npts])` | Constant operating (power) gain circle on the LOAD plane |
| `stab_circle(SP1.S, "source"\|"load"[, npts])` | The stability circle on that plane |
| `stab_side(SP1.S, "source"\|"load")` | `1` when the inside of that circle is the stable region, `-1` when the outside is |

Rules:
- **Two-ports only.** A cube that is not [freq, 2, 2] is an error naming its shape, as `nf()` reports one today.
- **A gain above the maximum** (`max_gain`) has no circle. It returns NaN points, and a Data Display trace draws nothing
  there. One diagnostic per evaluation says which frequencies had no circle, the way `NoiseFunctionDiagnostics` does.
- **The reference** is port 1's Re Z0 for source circles and port 2's for load circles. That matches `Sopt`'s reference.
- **`npts`** default and closure follow `ns_circle`, so a circle closes on itself.

### 2. One chart that shows the trade

A Data Display preset, **Noise and gain circles**, offered on any result with an `NF` cube and listed where the other
presets are (`SParameterViewerPreset` is the model). It is one Smith chart of the source plane showing:
- `Sopt` as a marker;
- three NF circles, at NFmin + 0.25, 0.5 and 1 dB;
- three available-gain circles, at max_gain − 1, 2 and 3 dB;
- the source stability circle, with its unstable side hatched or shaded.

A frequency selector picks the frequency, as the loadpull contour plots do. All circles are ordinary expression traces,
so a user can edit any of them afterwards.

The minimum-clicks rule applies: a result with noise opens to this chart in one action from the result's context menu
and from the Data Display's preset list.

## Gates

1. **Textbook values.** On a published two-port example (the one PR #50's tests already use for its noise circles), the
   `ga_circle` and `stab_circle` centres and radii match the closed forms to 1e-9. Points lie on the circle to 1e-12.
2. **Consistency.** At the centre of a `ga_circle`, the available gain computed from S and that Γs equals GdB to 1e-9.
   On a `stab_circle`, |Γout| = 1 to 1e-9. `stab_side` agrees with a point test at the centre.
3. **Unconditionally stable and no circle.** On a part with K > 1 and |Δ| < 1, `stab_side` puts the whole Smith chart
   on the stable side. A gain above `max_gain` gives NaN and one diagnostic.
4. **The preset** opens on the brief-11 LNA result, and a UiSnapshot of it is looked at.
