# Brief DFR32-5 — Trace Impedance: a trace entering its own via's clearance is not a broken return

**Series:** designer feedback round 32 (overview: `brief-dfr32-0-overview.md`). · **Tag:** `R-dfr32-5-<n>`

## The gap

On a four-layer board, an Inner 2 stripline runs 6.2 mm between two vias at 46.9–51.0 Ω, 100 % inside the pass band.
The Trace Impedance report fails it anyway, with four findings: 'Inner 1' missing above and 'Bottom Copper' missing
below, for about 0.5 mm at each end. Those stretches are where the trace crosses the clearance hole each plane has
round the via the trace ends on. Every trace that changes layer does that, and the hole is the via's design, not a
break in the return path. A report that fails every layer change teaches its reader to ignore FAIL.

## What to do

**R-dfr32-5-1** — when a reference plane is missing under (or over) a trace only for a stretch that runs from the
trace's own end pad to the edge of a clearance hole round the via at that pad, report it as an Info line ("via
clearance at the end, 506 µm") and leave it out of the trace's result. A missing stretch is still a finding at its
present severity when:
- it is longer than the clearance hole's radius plus a tenth of a millimetre (the trace runs over a slot, not just
  the hole);
- it does not reach the trace's own end pad (a gap mid-trace);
- the via at the pad is not one the trace connects to.

**R-dfr32-5-2** — the map draws the Info stretch hollow and unnumbered, so the drawing and the findings agree.

## Gates

- One test per claim, on synthetic geometry: a stripline into a via through clearance holes passes with two Info
  lines; the same trace with a slot in the plane longer than the hole still fails; a mid-trace gap still fails.
- Docs: the Trace Impedance page's "How it was measured" paragraph.
