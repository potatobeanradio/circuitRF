# CXP-4 — the annular port on openEMS

Read `brief-cxp-0-overview.md` and `brief-cxp-1-c3d-annular-port.md` first.

## Why

openEMS's `LumpedElement` and our lumped-port lowering (`CsxcadWriter`, "Ports: resistor, voltage probe, current
probe") run along one axis. A ring's field is radial. openEMS can still state the port from parts it already has, and
`CsxcadWriter` already writes some of them for the wave-port terminals (brief-em3d-116: a source with `Weight`
functions, voltage and current probes).

## Requirements

**R-cxp4-1 — The source.** A soft E-field `Excitation` on the ring's cells, in its plane, with weight functions that
make it radial and 1/r about the centre — for a ring in a plane of constant x, centred at (y₀, z₀):
`Y = (y-y0)/((y-y0)^2+(z-z0)^2)`, `Z = (z-z0)/((y-y0)^2+(z-z0)^2)`, `X = 0` — and the sign that makes the positive
conductor's voltage positive. The box written is the ring's bounding square ON the grid line of its plane (the
brief-sar-8 rule: on the line's own value); the weights are cut to zero outside the ring (inside the pin and the bore's
metal they are irrelevant, but in any dielectric between the square's corners and the ring they are not).

**R-cxp4-2 — The termination.** The ring is terminated in Z0 by a conducting ring one cell thick along its normal:
a material of conductivity κ = ln(b/a)/(2π·Z0·Δ), Δ that cell's thickness, written as a `CylindricalShell` (or the
primitive the writer's cylindrical code already uses) of the ring's radii, with priority above the dielectric it sits
in. State in the run's notes the R the grid actually gives, from the edges the ring covers after staircasing.

**R-cxp4-3 — The probes.** Voltage: a line probe from the inner circle to the outer one along a grid axis in the
ring's plane, weight −1 as the other lumped probes. Current: a square current probe round the inner conductor on the
half-cell plane next to the ring, its side between the two radii with at least one cell of room each side; when the
grid gives no such square, refuse by name, saying how many cells there are and that a finer grid or a grading ratio
near the coax (the 3D Wave Ports example's 1.15) gives the room.

**R-cxp4-4 — Where it may sit.** A ring ON a PEC air-box face is refused: openEMS's wall forces the tangential E there
to zero and would short the port. The refusal says to move the ring one cell in, with nothing behind it but air or the
coax's own continuation. The misleading direction refusal brief 1 replaced is removed.

**R-cxp4-5 — The grid.** The ring's two circles and centre are the grid's own lines' sources (as a cylinder's are),
and the existing staircase warnings fire on the pin and the bore.

## Gates

1. **Routine tier, no solver:** the written XML for the coax fixture holds the weighted source, the conducting ring with
   its κ, the voltage and current probes, all on grid lines; a ring on a PEC face is refused.
2. **The coax fixture on openEMS** (overview gate 2), at the 3D Wave Ports' grid settings: |S11| < −20 dB, |S21| within
   0.1 dB of the closed form, ∠S21 within 3° of −β·ℓ. The staircased coax is not 50 Ω exactly (that example's note
   measured 54.6 Ω at its grid), so the gate also states the line impedance the run measured and checks |S11| against
   the mismatch that impedance predicts, to 2 dB.
3. If gate 2 cannot be met, stop and report with the numbers.

## Out of scope

openEMS's cylindrical grid (a ring about the cylinder's axis on that grid would be exact; a later brief if wanted), and
multi-port excitation changes beyond what one more port kind needs.
