# CXP-3 — the annular port on EMerge

Read `brief-cxp-0-overview.md` and `brief-cxp-1-c3d-annular-port.md` first.

## Why

`EmergeLoweringRules` refuses an annular port because EMerge's `LumpedPort` drives one uniform field direction. But the
port is OUR code's to state: `circuitrf_emerge.py` builds every port, and EMerge's lumped-port machinery reads what a
port object tells it.

## What EMerge 3.0 offers (read from the source of the 3.0.0 that circuitRF installs)

- `LumpedPort` (`bcs/port_bcs.py`): a Robin condition with γ = jk₀·η₀/Zs, Zs = `Z0·width/height`; its source is
  `get_Uinc`, its field shape `port_mode_3d_global` (a constant vector); its S-parameters come from the voltage along
  `voltage_integration_line`, which `_define_lumped_port_integration_points` fills ONLY when the port has none.
- `CoaxPort`: an analytic TEM-mode port for a face on the domain's EXTERIOR (EMerge warns when a port other than a
  lumped one is not on the exterior, and when a lumped one is).

## Requirements

**R-cxp3-1 — A radial lumped port in the driver.** In `circuitrf_emerge.py`, a subclass of `LumpedPort` for a ring:

- the sheet impedance is Zs = 2π·Z0/ln(b/a) (so the ring terminates the port in Z0);
- `port_mode_3d_global` returns r̂/(r·ln(b/a)) about the ring's centre, in the ring's plane;
- `get_Uinc` scales that field so the port voltage is EMerge's own `voltage`, by the same arithmetic the base class
  uses for a uniform field (do not copy its constant blindly: derive it, and state the derivation in a comment);
- `voltage_integration_line` is set before the solve to several radial lines from the inner circle to the outer one
  (at least four, evenly spaced in angle), so EMerge averages them as it does its own.

The ring's surface comes from the lowering as Palace's does (brief 1's `EmitAnnulus` shape, or the driver's own
OpenCASCADE disk difference — whichever the EMerge lowering already uses for port sheets).

**R-cxp3-2 — Where it may sit.** Measure the ring both inside the domain and on an air-box face. Ship what the gates
pass; refuse the other by name, giving the alternative (a coax wave port on the face, or a ring moved one step in).

**R-cxp3-3 — The refusal goes.** `EmergeLoweringRules`' annulus refusal is replaced by R-cxp3-2's rule. The
`explain`/dry-run text names the port as coaxial.

## Gates

1. **Routine tier, no solver:** the written script for the coax fixture contains the subclass, Zs from ln(b/a), and the
   radial lines; a z-ring and an x-ring write the right plane.
2. **The coax fixture on EMerge** (overview gate 2): |S11| < −25 dB, |S21| within 0.05 dB of the closed form,
   ∠S21 within 2° of −β·ℓ, and agreement with brief 1's Palace answer to 0.1 dB / 2°.
3. If gate 2 cannot be met, stop and report with the numbers; do not ship a looser tolerance silently.

## Out of scope

EMerge's `CoaxPort` as a second, wave-like spelling (D5: the wave port already covers a coax on the face), and
eigenmode problems (lumped ports there follow EMerge's existing rules).

## Completion note (2026-11-12)

Built as R-cxp3-1 describes, in `circuitrf_emerge.py` (`ring`, `port_surface`, `RadialLumpedPort`); the C# side writes the ring
(`annulus`: centre, normal, inner, outer, outward) instead of a rectangle, the EMerge refusal is gone, and the run's mapping table
names the radial port. R-cxp3-2: both placements pass, so neither is refused.

Gate 2 (`CoaxialPortEmergeTests`, Benchmark): on the box's PEC faces |S11| −52.0/−57.4/−53.8 dB, |S21| −0.0129/−0.0345/−0.0477 dB
against the closed form's −0.0148/−0.0347/−0.0468 dB, ∠S21 within 0.7° (15 s, EMerge Accurate). Inside the box (open coax ends,
0.5 mm of air to PEC walls) EMerge and Palace at element order 2 agree to 0.006 dB and 0.24° in |S21|, 0.8 dB in |S11|. Two fixes
the gate needed: the voltage lines stop just inside both conductors (an endpoint on the outer radius read zero, 0.09 dB), and the
Palace lowering no longer claims the pin's end cap into a ring inside the model (it had made the port nearly open — a CXP-1 defect
this gate is the first to reach). Routine tier: `CoaxialPortTests` (the written EMerge input, the Palace claim) and
`EmergeLoweringTests` (the refusal row removed).
