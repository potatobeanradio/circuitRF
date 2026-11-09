# Brief NZ-6 — noise figure as a loadpull and sourcepull metric

Series: `brief-nz-0-overview.md`. Depends on NZ-5.

**Why.** The loadpull engine reports Pout, gain, efficiency and PAE at every termination of its grid, and sweeps the
source tuner as readily as the load (`Sweep=Source`). It reports no noise. A receiver front end's input match is chosen
on noise and gain together, and a transmit/receive design wants to see what a PA's load does to its noise in the
receive band. In the small-signal case, the circles of NZ-2 answer this without a sweep (D7). Under drive they cannot,
and the loadpull engine is where the sweep already exists.

## What to build

1. **`Noise=yes` on `loadpull` (and `loadpull_pursuit`, if it falls out for free; otherwise say why not).**
   - At each grid point's reported operating point (the compression level, `Compression=`), the engine runs NZ-5's HB
     noise at one output frequency.
   - **`NoiseFreq=`** gives that output frequency. Default: the tone, which is the LNA-under-drive case.
   - `NoiseIn`/`NoiseOut` default to the source and load tuner ports, and `NoiseSideband` defaults to 0.
2. **Metrics:**
   - `NF` (dB, SSB) per grid point, in `LoadpullResult` and the `.spl` interchange, beside the existing metrics;
   - the existing contour machinery draws its contours. Contours of NF on the source plane, with gain and PAE on the
     same chart, are the sourcepull picture a designer wants.
   - The Data Display's loadpull contour plot offers NF in its metric list.
3. **A small-signal shortcut.** When the grid's drive ladder holds a point at or below `PinStart` (small signal), NF at
   that point equals the S-parameter noise analysis on the same terminations. Add a test that they agree. It is the
   cheapest check that the tuners' noise is handled (a tuner is noiseless by definition, like a port).
4. **The `.spl` column.** NF is written as one more metric column. `.spl` readers that predate it ignore a column they
   do not know. Confirm that, and name the reader that does it.

## Gates

1. **Small-signal agreement** (item 3): to 1e-6 dB on a lossless-tuner grid around a device with NZ-4 noise.
2. **Sourcepull NF contours** of a small-signal device are circles. Their centre and radius agree with `ns_circle` at
   that NF to the grid's own interpolation error, stated.
3. **No key, no change.** A loadpull run without `Noise=yes` is byte-identical, `.spl` included.
