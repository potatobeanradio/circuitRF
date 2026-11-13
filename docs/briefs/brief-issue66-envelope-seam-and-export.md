# Issue 66 §3 and §4b — the envelope's record of a device-only net, and the group a CLI export writes

Issue 66's other parts are fixed: a Stop below 15 steps is refused with the shortest that is not, the lattice
envelope's input power adds a source's carrier groups as powers, and `circuitrf env` dispatches at the outermost sweep
through `ChainSelector`. Two parts are left, each needing a decision first.

**Status (2026-11-13): done.** §1: a recorded node that no linear element touches is read from the accepted Newton
unknown (D1: only device-only nets, so a node record and its IProbe's voltage never disagree; `src/Engine/RESOLVED.md`).
§2: D2 = (b), displays accept both (`docs/design/cli.md` §3, `src/RfCore/RESOLVED.md`).

## 1. A net only nonlinear-device ports touch is recorded with Newton's residual on it

### Why

`EnvelopeEngine` records a labelled net as `products[k].Xm1[row]`, the linear state after the step's update
(`EnvelopeEngine.cs`, the probe loop; `EnvelopeEngineMt.cs`, `Record`). Where nothing linear holds the node, as at
the outputs of two `Amp` blocks feeding a third, that value carries the device injection's residual, within `Tol` in
current but large as a voltage. Nothing downstream reads it wrongly; only the seam check does, since it compares the
segments' records at their overlap:

| `labelednets` | EnvSeamError | note | EVM, Pavg, ACLR |
|---|---|---|---|
| with `n2` (A1/A2 outputs, PA input) | 20.649 | "segments disagree at their seams on probe 'n2'" | 3.340 %, 1.92 dBm, identical |
| without `n2` | 0.0025 | none | identical |

Reproduction: issue 66 §3 (16 segments, `Tol=1e-6`, NR 64-QAM through a bandpass and three amplifiers).

### Requirements

**R-1 — Record an interface node from the accepted interface spectrum.** For a probed node that is one of the
nonlinear interface nodes, record the Newton unknown `V` the step accepted, on both envelopes, rather than the linear
state. Every other node keeps the linear state. Check first whether `V` and `Xm1` already agree at an interface node
that a linear element also holds; if they do not, decide which one is the node's voltage (**D1**) and record it in
`src/Engine/RESOLVED.md`.

**R-2 — Gates.** The reproduction reads EnvSeamError within 2× of the unlabelled value with `n2` labelled, no seam
note, and EVM, Pavg and ACLR unchanged to the printed digits. A bench with no device-only net is unchanged bit for bit.

## 2. `circuitrf env -o` writes the default group; Simulate writes the analysis's group

### Why

`circuitrf env -o x.npy` writes `V`, `Pin_avg_dBm`, … in the default group; Simulate puts each analysis's cubes under
its name (`ENV1.V`), which is what a display authored against a Simulate result reads. This is not the envelope verb's
alone: `sparam`, `hb` and `lp` export the default group too, and some readers already handle both (the noise-circles
preset).

### Decision first (**D2**)

- (a) every run verb's `-o` writes the analysis's group, as Simulate does, so a CLI result opens in a display made
  from a Simulate one. It changes the field names of every existing CLI export, and anything scripted against them
  (`np.load(...)["V"]`) has to move; or
- (b) keep the default group, and make the displays' source binding take a default-group cube where they look for
  `<analysis>.<cube>` and the file has no such group; or
- (c) a flag, `--group`, defaulting to today.

Whatever is chosen, `docs/design/cli.md` states it once for every run verb, and a gate exports one bench from the CLI
and from Simulate and opens both in one `.cdd`.
