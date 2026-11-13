# Issue 67 §1 — an unwired thermal pin of a compiled model is held at the ambient's value, and read as a rise

## Why

A compiled (OSDI) device whose thermal terminal is drawn but wired to nothing else runs 25 K hot, and its own thermal
network is overridden. Measured on a compiled GaN model at −0.8 V / 8 V:

| case | V(t) | Id |
|---|---|---|
| `t` drawn and unwired | 25 (held) | 6.26 mA |
| the same, plus `R:RT t 0 R=1e12` | 1.354 | 6.87 mA |
| the same model as a native `ComposedFET` | 1.354 | 6.87 mA |

Two things combine.

1. **The self-reference check misses the model's network.** `Elaborator.SelfReferencedThermalNodes` asks only for the
   pin's own diagonal, `res.Dg[n.Index, n.Index]`, at the all-zero point. circuitRF's own Verilog-A export of a composed
   FET (`docs/design/model-composer.md` §25.13) puts `Rth ∥ Cth` on the internal node `ti` and, when the port is
   connected, joins `t` to `ti` with `potential(t, ti) <+ 0`. Nothing lands on `t`'s own diagonal, so the pin is judged
   unreferenced and `PinUnreferencedThermalNodes` adds a `Vdc` on it.
2. **The held value is an absolute temperature on a rise-type pin.** The `Vdc` is `_ambientC` (25). The export's pin,
   like circuitRF's native thermal nodes and most compact models, carries the rise above ambient, so 25 is +25 K. The
   warning then says the part "is simulated with no temperature rise", the opposite of what happened.

## Requirements

**R-1 — Find out where the zero-volt branch goes.** Before changing the check, establish what `ExternalDeviceModel`
does with `potential(t, ti) <+ 0` at the zero point: an OSDI collapsible pair (`t` and `ti` one node, the pin's
diagonal then being `ti`'s), a branch-current unknown, or neither. Record the answer in `src/Core/RESOLVED.md`; R-2's
shape depends on it.

**R-2 — A pin is referenced when the device reaches a positive conductance from it.** Replace the own-diagonal test
with reachability over the device's full Jacobian at the zero point, internal nodes and branch unknowns included: the
pin is self-referenced when a chain of non-zero couplings joins it to a node whose diagonal clears
`1 / Temperature.ImplausibleThermalResistanceCPerW` (the rule the current check already uses, positive and above the
implausible-resistance line). A collapsed pair counts as one node. Keep today's conservative answer for a device that
refuses to evaluate.

**R-3 — When a pin must be held, hold it at the value that means "no rise".** Owner decision **D1**:
- (a) hold every compiled thermal pin at 0, circuitRF's own convention (the composed export and every native thermal
  node are rise-type); or
- (b) hold at 0 by default and let a kit declare an absolute-temperature pin (a descriptor key), held at the ambient.

The warning states the value held and what it means: *"…holding it at 0 (no rise above the ambient 25 °C)…"*.

**R-4 — Gates** (routine tier, the fake model library or a composed export compiled in a test, as
`VerilogAThermalTests` already does):
- the table above: `t` drawn and unwired reads `Id` and `V(t)` within 1e-6 relative of the `R:RT t 0 R=1e12` case
  and of the native `ComposedFET`, with no pinning warning;
- a model with NO thermal network of its own and an unwired pin is still held, at the D1 value, with the corrected
  warning;
- the three-pin placement (`t` not drawn) is unchanged, byte for byte in its DC and S results.

## Out of scope

Native models (`FetModelBase` and its kin) — the check reads only `ExternalDeviceModel`. The Verilog-A export's
`$port_connected` shape is right and stays.
