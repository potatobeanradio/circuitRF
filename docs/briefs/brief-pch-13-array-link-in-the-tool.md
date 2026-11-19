# Brief PCH-13 — the mmWave Array Link in the Phased Array tool

**Status:** implemented 2026-11-19 (option A). **Series:** PCH-0 … PCH-13 · **Tag:** `R-pch13-<n>` · **Depends on:** PCH-8 (instance ends), PCH-9 (the tool's Channel tab),
PCH-12 (the example), PAA-9 (the tool) · **Area:** `examples/mmWave Array Link`, the fields analysis on a channel-end array
(`src/Design/Em3d/FieldCosimulation*`, `src/Engine/Envelope/EnvelopeChannel.cs`, `PropagationChannelModel`), `src/Ui/PhasedArray`,
`src/Engine/RESOLVED.md`, `src/Ui/RESOLVED.md`.

## The gap

The example's README says: open the bench in the Phased Array tool with `PT` or `PR` selected, and its **Channel** tab draws the
drop's rays with the candidate beams and lists the ranked pairs. Neither works.

1. **The tool refuses.** It evaluates the array through a fields analysis that names it (`PhasedArraySession.FieldsAnalysisFor`), and
   even the Channel tab takes its frequency from that frame (`PhasedArrayToolViewModel.RefreshChannel` reads `frame.State.Hz`). The
   bench declares only `ENV1` and the drop sweep, so the tool opens on "The bench has no fields analysis on 'PT'", with no tab.
   This is how the tool was designed and tested: `BeamSelectionTests` Gate 7 opens it on a channel end whose bench carries
   `analysis F1 type=fields block=PA1 freq=5.9 freqUnit=GHz drive=1`. The example was never given one.
2. **Adding the obvious one shows nonsense.** With `analysis F1 type=fields block=PT freq=28 freqUnit=GHz drive=1` appended to the
   netlisted bench (EM result present), the tool opens, but: realized gain −300 to −340 dBi over the whole (u, v) disc,
   beamwidths 360°, empty principal cuts, power balance NaN, worst |Γ act| 0.000 — and the settled frame took **177 s**.

What the first investigation found. **The −300 dBi is not a wrong field computation.** `drive=1` is circuit port 1, which on this
bench is `UE`, a terminal on the receive side; the run warns that it "is not connected to 'PT', so its results are empty", reports
Success with gain −∞, and the tool draws that without the warning. **No fields analysis can give either array's pattern on this
bench as it stands:** PT's only source is `GNB`, a `MOD_Source`, which a fields analysis refuses as a driver (it sends its wave in
from a port's Z0), and `waves=` reads an amplifier on every element's pin, which is Elements mode, not Feed. PR's pattern driven from
its feed is zero at every element, because the wave would have to pass the LNAs backwards — still zero with the channel removed. The
*Phased Array* example's *2 Receive* has the same empty pattern; its README's G/T, T_A and SNR come from the incident-wave receive
model and still reproduce. **The 177 s is not the fields analysis** (1.1 s): the settled frame also runs every analysis the bench's
measurements name, here the 20-drop envelope sweep.

## Requirements

**R-pch13-1 — A MOD_Source drives a linear fields analysis.** `drive=` may name a `MOD_Source`, as it names a `P1Tone`: a wave in
from its `Z` at the fields analysis's frequency, its amplitude √Pavg at that Z when no weight is given (Pavg is its available power),
so it compares with P1Tones and not with unit drives, as a P1Tone's √Pavl does. Its modulation plays no part in a frequency-domain
pattern. The S-parameter solve the fields analysis runs treats it as the port it then is, for that analysis only; every other
analysis sees the source it always was.

**R-pch13-2 — A receive array's pattern is the receive pattern.** When the fields analysis's block is a receive array — the `Rx` end
of a propagation channel, or an array given incident waves (`RxTheta[k]`) — the waves at its elements are the transfer from each
element to the driven port, not from the port to each element: element *k*'s weight is S(port ← element *k*), the direction its
LNAs conduct. For a reciprocal network the two are equal and nothing changes; behind one-way LNAs the pattern stops being zero.
Stated in the run's note ("the receive pattern: each element's transfer to port 1"). *2 Receive*'s README numbers must still hold.

**R-pch13-3 — An empty pattern is said, not drawn.** The tool shows the run's "results are empty" sentence as its status instead of
drawing a pattern 300 dB down.

**R-pch13-4 — The settled frame runs what it shows.** A settled frame runs the fields analysis and only the analyses the measures it
displays need; the bench's envelope drop sweep is not one of them unless a displayed measure reads it. Measure where the time goes
and state the settled frame's time.

**R-pch13-5 — The example carries what the tool needs.** `analysis FT type=fields block=PT freq=f0 drive=GNB` and
`analysis FR type=fields block=PR freq=f0 drive=UE` (or by number), so the tool opens on either array with its true pattern, steered
as the bench steers it; the bench's own Run still answers what the README tabulates, in about the time it states.

**R-pch13-6 — The Channel tab, as the README describes it.** On `PT` and on `PR`: the drop's rays, the codebook beams, the ranked
pairs, the drop slider, a click on a pair writing `BeamSelect=index`, **Apply to array** — PCH-9's R-pch9-7 on this bench. UiSnapshot
PNGs of both arrays' Beam and Channel tabs, looked at. The README's tool paragraph matches what the tool shows.

## Gates

1. **MOD_Source drive.** A fields analysis driven from a `MOD_Source` gives the pattern a `P1Tone` of the same Z and power at the
   same node gives, to 1e-9 of its peak.
2. **Receive pattern.** On a reciprocal receive array (no LNA) the receive pattern equals the driven one to 1e-9; behind one-way LNAs
   it is non-zero, and its peak realized gain is the array's directivity plus the element's gain plus the channel cell's S21 within
   0.5 dB. *2 Receive*'s README table reproduces.
3. **Pattern on the example.** The tool on `PT`: peak realized gain within 1 dB of the 16 × 16 array's directivity plus the patch's
   realized gain and the TxChannel's 32 dB at 28 GHz, at the bench's steering; non-empty cuts; a finite power balance. The same for
   `PR` (8 × 8, with the RxChannel's 17 dB).
4. **Channel tab.** On `PT` at drop 1, the chosen pair is the pair the bench's own run chooses at drop 1.
5. **No regression.** The bench's Run reproduces the README's table to its stated precision.
6. **Time.** A settled frame on `PT` measured and stated (a scratch harness, not a new timing test).

## How to reproduce

Copy the example to a scratch folder (File ▸ New ▸ Examples, or `create what=example`), run `Patch Element`'s `.cem` so
`results/Patch Element.cemr` exists, then:

```
circuitrf netlist "Array Link/schematic/Array Link.csch" -o bench.cnl
echo 'analysis F1 type=fields block=PT freq=28 freqUnit=GHz drive=1' >> bench.cnl
dotnet run --project tools/UiSnapshot --no-build -- --phased-array bench.cnl -o pt.png
```

Without the appended line the same command prints the refusal.

## Out of scope

Making the Channel tab work without a fields analysis, and `waves=` in Feed mode (each element's wave read at its channel cell's
output): the first keeps the tool's one source of frequency and frame, the second would put the amplifiers' compression in the
pattern at the cost of an envelope run per frame, which an array of identical amplifiers does not need.
