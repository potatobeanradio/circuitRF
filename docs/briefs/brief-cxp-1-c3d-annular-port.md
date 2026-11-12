# CXP-1 — the annular port in a `.c3d`, lowered in any plane, run on Palace

Read `brief-cxp-0-overview.md` first: its decisions D1–D6 and its gates hold here.

## Why

`Em3dPort.Annulus` exists and Palace writes it, but no document can state one, and the numeric layer accepts only a
z-facing ring. An edge-launch connector's ring faces x or y.

## Requirements

**R-cxp1-1 — The format.** `C3dPort` gains one optional key, written only when set:

```json
"Annulus": { "Centre": [u, v], "Inner": dbu, "Outer": dbu }
```

on the port's existing `Plane` and `Offset`; `Centre` is in the plane's own (u, v) as `Rect.Min` is. All three bind to
expressions as `C3dRect` does (`IC3dBindable`), so `"Inner": {"Expr": "pin_d/2", "Unit": "Mm"}` follows the VAR.
With `Annulus` set, `Rect` and `Angle` are not read and stating `Angle`, `Cylinder` or `Kind: Wave` with it is a
refusal naming both keys. A port written before this brief reads unchanged; a file that states `Annulus` read by an
older build lands in `Unread` and is reported, as every unknown key is.

**R-cxp1-2 — The resolver.** In `C3dPorts`, an annular port's two conductors are inferred from what its two circles
touch, by the rule a rectangular port's edges follow: the conductor whose surface the inner circle lies on is positive,
the one the outer circle lies on is negative (D2). `Positive`/`Negative` override, both or neither, as today. A circle
touching no conductor, or touching two, is a refusal naming the circle, its radius and what it found — for instance
*"P1's inner circle (r = 0.2 mm) touches no conductor: the pin is 0.19 mm in radius."* `Flip` swaps the two.
The lowered `Em3dPort` has `Annulus = new Em3dAnnulus(Inner, Outer, Outward)` with `Outward` true when the negative
conductor is the inner one, `Min`/`Max` the ring's bounding square in world coordinates, and its reference plane in
the ring's plane, its normal pointing into the structure (into the side the inner conductor continues on).

**R-cxp1-3 — Any plane in the numeric layer.** Lift the z-only rule in `Em3dProblem`'s validation: the bounding
square may lie in a plane of constant x, y or z. `GmshGeoWriter.EmitAnnulus` builds the two disks in that plane
(a `Disk` is made in XY; rotate it about the right axis, then translate). `Scene3DBuilder`'s ring (its 32 segments)
is built in that plane too. `Em3dDcLowering` already takes the annulus; check it with a non-z ring. Keep every
existing refusal of a turned or wave annulus.

**R-cxp1-4 — Palace.** Nothing new in `PalaceConfigWriter`: `+R`/`-R` already follows `Outward`. Confirm, in the
routine tier, that a YZ ring writes one `LumpedPort` with `Direction: "+R"` (or `-R`) and its Gmsh surface is a ring
(two disks, one difference).

**R-cxp1-5 — The other consumers say what they do.** Everything that reads a port and cannot take a ring refuses by
name rather than writing its bounding square: the EMerge rule already does (brief 3 replaces it), openEMS's lowering
gets an explicit refusal now — *"Port N is a coaxial lumped port, which circuitRF does not write for openEMS yet"* —
in place of today's misleading "direction is not along an axis" (brief 4 replaces it), and the Hertzon export
(`C3dToHzn`) states the ring if the format has a coaxial port, else refuses by name. `EdgePortSheet`, the placed-cell
port rules in `C3dHierarchy` and the field-drive code are checked and left consistent.

**R-cxp1-6 — `check`, `explain`, and the references.** `check` reports the resolver's refusals as errors with the
port's name. `explain` prints an annular port as `coaxial, a = 0.2 mm, b = 0.67 mm, ln(b/a) = 1.209, from 'pin' (+)
to 'housing' (−)` and, as a NOTE, the ring's TEM impedance for the material it lies in, η₀/(2π√εr)·ln(b/a), beside the
port's Z0 — not a warning: a port of any Z0 is legal, and the difference is a mismatch the result shows. The
`3d-view` reference (MCP and `docs/design`) documents the key.

## Gates

1. **Format round trip.** A `.c3d` with an annular port in each plane reads, writes and reads back identically,
   expressions included. A file with none is byte-identical to today's.
2. **Inference.** Pin + shell inferred with the right polarity in each plane; `Flip` reverses it; each refusal in
   R-cxp1-2 and R-cxp1-1 is produced, with its sentence.
3. **The coax fixture on Palace** (overview gate 2): |S11| < −30 dB at all three frequencies; |S21| within 0.02 dB of
   the conductor-loss closed form; ∠S21 within 1° of −β·ℓ. The fixture is a `.c3d` in `tests/` built from the 3D
   Connector's VARs, the coax's ends on absorbing-free PEC walls with nothing behind the rings.
4. **Orientation** (overview gate 3): the fixture along x, y and z agrees to ±0.01 dB and ±0.1°.
5. **Nothing shipped moves** (overview gate 1).

## Out of scope

The editor (brief 2), EMerge (brief 3), openEMS (brief 4), a ring on a cylinder or a turned plane, an elliptical or
off-centre coax, and a multi-mode coaxial port.
