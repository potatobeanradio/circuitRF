# Brief NZ-9 — an oscillator analysis

Series: `brief-nz-0-overview.md`. Independent of the other briefs. Needed by NZ-10.

**Why.** circuitRF's harmonic balance solves driven circuits: every fundamental is a stated tone. A free-running
oscillator has no tone to state. Its frequency is part of the answer, and an HB started on it converges to the DC
solution, which is always a solution and is the wrong one. The WSProbe already reports whether a circuit would start to
oscillate (its stability metrics), but nothing finds the oscillation that results: its frequency, its power, its
harmonics, or how they tune. Phase noise (NZ-10) needs that solution first.

**Risk (from NZ-0).** High. If gate 1 or 2 fails after a reasonable attempt, stop and report what was tried. Do not
widen the brief.

## What to build

### 1. The marker: `OscProbe`

`OscProbe` is a two-terminal schematic part placed in series in the oscillator's loop, or across the resonator, where
the oscillation is strong. It draws like the WSProbe and does nothing in any other analysis: a short in series, an
open in shunt, by its `Mode=series|shunt`. An oscillator analysis needs exactly one, and `check` says so otherwise.

### 2. `analysis OSC1 type=osc Probe=X1 Fguess=… MaxHarm=…`

**The method, in our own words.** At the probe, an ideal generator of amplitude A and frequency f is inserted. It is
an open circuit at every other frequency. For any trial (A, f), an ordinary driven HB at f gives the current the
generator delivers at the fundamental. The oscillation is the (A, f) where that current is zero: the circuit sustains
itself, and the generator is doing no work. That is two real equations, the real and imaginary parts, in two real
unknowns. The outer solve is a small Newton on (A, f) around the existing HB engine, which stays untouched.

**Start-up.**
1. From the small-signal admittance at the probe across a frequency span (`Fstart`/`Fstop`, default ±50 % around
   `Fguess`), find where its real part is negative at a zero of its imaginary part. That is the start-up condition.
2. If none is found, refuse with that sentence and the span searched. The WSProbe numbers should agree with it, and the
   refusal says so.
3. Otherwise, ramp A from small, continuing the solution, until the generator current vanishes. The trivial A = 0
   solution is excluded by construction.

**Keys:** `Probe`, `Fguess`, `Fstart`, `Fstop`, `MaxHarm` and `Tol` (as HB), and `Aguess` (optional).

**Results:**
- `Fosc` (Hz);
- the converged HB result at Fosc, in the same cubes an HB analysis writes, so every existing measurement and Data
  Display preset works unchanged;
- `Aosc` at the probe;
- `Startup` [freq], the probe admittance used to start.

Inside a `parametric_sweep` (a varactor bias, say), `Fosc` against the swept variable is a tuning curve, and a
measurement gives Kvco as its derivative.

### 3. Surfaces

Analysis editor body, `.cnl`/`.csch`, CLI `circuitrf osc` as a run verb with the standard anatomy (`docs/design/cli.md`),
MCP `run analysis=osc`, the `reference analyses` text, and `explain --analysis` printing the start-up search before a
run.

## Gates

1. **Cubic negative conductance across an LC tank.** Frequency 1/(2π√LC) and amplitude from the closed form of a cubic
   conductance balancing a load, to 1e-6 relative at MaxHarm = 1, and the harmonic content falls with MaxHarm as
   expected.
2. **A transistor oscillator built from shipped models** (a Colpitts on a built-in BJT or FET) converges from `Fguess`
   ±20 %. Its Fosc is within the small-signal start-up frequency's expected pull, stated.
3. **No oscillation, no answer.** A circuit whose probe admittance never has a negative real part is refused with the
   start-up sentence. It never returns the DC solution as an oscillation.
4. **Driven HB is untouched.** Every existing HB result is byte-identical.
