# Brief DFR32-4 — power lost through a plane pair the solve region cuts

**Series:** designer feedback round 32 (overview: `brief-dfr32-0-overview.md`). · **Tag:** `R-dfr32-4-<n>`

## The gap

A 6-port Palace solve of an output filter on a four-layer board (Draft, element order 1, 260,120 tetrahedra, air box
20 % on every face) read about 1 dB more insertion loss at 868 MHz than the bench measurement of the same board, with
lossless ideal parts in the circuit. Measured: S21 −0.78 dB, including a pigtail and real parts. Simulated: −1.07 dB.
Mismatch accounts for only 0.006 dB of that, so nearly all of it is power the model lost.

Driving each port of the solved block in turn, with the others matched, 1 − Σᵢ|Sᵢⱼ|² gives:

| driven port | lost at 0.5 GHz | lost at 3 GHz |
|---|---|---|
| 1 | 7.9 % | 11.8 % |
| 2 | 28.5 % | 35.2 % |
| 3 | 7.4 % | 6.8 % |
| 4, 5, 6 | 0.1–0.2 % | 0.5–0.6 % |

FR-4 at tanδ 0.02 over this length should lose about 1 %. Ports 4–6 behave. They are top-layer only. Ports 1–3 sit at
the two ends of an Inner 2 stripline that the signal reaches through vias, between the Inner 1 and Bottom ground
planes. Their loss is already large at 500 MHz and barely changes with frequency, so it does not look like dielectric
loss or leakage into the air.

**Leading explanation, not yet checked:** a signal via between two planes launches a parallel-plate wave between them.
The solve region cuts both planes at its edges, and the air box's faces there are absorbing, so the wave runs into a
near-matched load and leaves the model as loss. One port's via is about 1 mm from the region's edge. On the real
board, ground vias stitch the planes and the board edge reflects, so the bench measures none of this loss. The 20 %
z padding, about 0.3 mm of air above top copper, may add a smaller share (see DFR32-1).

## What to do

**R-dfr32-4-1 — confirm on synthetic geometry before changing anything.** Build a four-layer coupon from shipped
technology: a top microstrip, a via down to an Inner 2 stripline between two ground planes, and a via back up. Use
four solve regions:
- (a) the planes cut close to the vias, absorbing faces;
- (b) the same, with the lateral faces conducting;
- (c) a ring of ground vias round each signal via;
- (d) a generous region.

Record 1 − Σ|Sᵢⱼ|² per port and the run cost. Keep each run to a few minutes: a small coupon, Draft. **Go:** (a)
loses several percent where (b), (c) and (d) lose about 1 %. **No-go:** stop and report the table; do not tune
anything.

**R-dfr32-4-2 — say so in the run.** When the solve region cuts two or more ground-reference planes and a lateral
face there is absorbing, the run posts a note before it starts. The note names the planes and the face, explains that
a wave between them leaves the model as loss there, and offers the two ways out: make that face conducting, or widen
the region and stitch the planes with ground vias. After a driven run, also report each port's 1 − Σ|Sᵢⱼ|² at the
band's ends in the run summary. Flag any port whose loss is several times the median, so a result like the table
above announces itself.

**R-dfr32-4-3 — the default for a cut face, if R-1 confirms it.** *Decision D1 for the owner:*
- **(a) recommended:** a lateral face that cuts a plane pair defaults to conducting, which is what a section of a
  stitched board looks like from inside. A face that cuts nothing keeps today's absorbing default. The note says
  which faces were made conducting and why.
- (b) Keep absorbing and rely on the note.

## Gates

- One test per claim: the note fires when a region cuts a plane pair at an absorbing face, and stays quiet when the
  face is conducting or no plane is cut; the per-port loss line is computed correctly from a synthetic S-matrix (no
  solver needed); the D1 default, if taken.
- The R-1 table in `src/Design/RESOLVED.md`, with the board's own numbers above kept as the motivating case and
  described only in general terms (private board).
- Docs: `em-3d.md`, where the solve region and air box are explained.
