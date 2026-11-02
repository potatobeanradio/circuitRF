# Brief — PCAL3: let the calibration standard contain the neighbour it actually has

**Series:** `brief-portcal-0-overview.md`. **No longer provisional — PCAL1 has reported**
(`src/Engine/Mom/RESOLVED.md`, "PCAL1 — how much clearance a calibrated port actually needs") and this
brief is warranted. Write it after PCAL2 and before PCAL4.
**Scope:** widening a port's transverse profile so that metal near the feed is reproduced in the
standard, for the case where that metal is **not itself a port**.
**Not in scope:** two ports on mutually coupled conductors. One driven mode only. That is brief 4.

---

## What PCAL1 measured, and what it changes here

**The premise holds and the optimistic reading of it is refuted.** A passive neighbour is *not*
benign: at the 246 µm separation the series opened on, with ports 3 and 4 simply deleted, the answer is
**18.0 dB out in S₁₁ and 8.1 dB out in S₂₁ at 1 GHz** and non-passive at 3 of 7 points. What is true is
that it is a **milder and different** failure — it needs about **half** the clearance a driven
neighbour does:

| s/h | driven, max \|ΔS\| | passive, max \|ΔS\| |
|---|---|---|
| 0.27 | 0.985 | 0.871 |
| 1.00 | 0.922 | 0.181 |
| 2.00 | 0.477 | 0.058 |
| 3.00 | 0.169 | 0.053 (at the floor) |

**So this brief's own clearance target is ≈ 2 substrate heights, not brief 4's 5.** And two scope
changes follow:

- **A ground pour is NOT a separate case and needs no special handling.** Measured at 20× the line
  width, a pour behaves like a 254 µm trace to within 2 % at every separation. The neighbour's own
  width does not enter the law at all; only its distance in substrate heights does. R-pcal3-1 below is
  therefore already general enough as written — do not grow a pour branch.
- **A flare on the port's own net is somebody else's case and is already handled** by
  `PlanarFeedExtension` (measured passive, clearance warning silent). Do not widen the profile for it.

---

## 0. Why this is the cheap half

D6's peel carries a per-port **scalar** error box. Two driven, coupled conductors support two modes and
cannot be described that way — which is what makes brief 4 expensive. But a neighbour that is merely
*present* — a passive trace, a ground pour, an adjacent net with no port on it — leaves **one driven
mode**, and the error box may stay scalar. If so, the entire fix is: put the neighbour in the profile.

And the profile is already the right shape. `PlanarPortProfile` carries `Lines[]` with
`PositiveLo/Hi` and `NegativeLo/Hi` — it was built so a **coplanar** port's standard reproduces the
signal *and* its grounds. D4 builds the standard from the DUT's own transverse gridlines verbatim, and
`SpanLoM`/`SpanHiM` exist precisely so `CheckFeedClearance` does not report the profile's own
conductors as intruders: **everything inside the profile is already reproduced.** A neighbour outside
it simply has no way in.

---

## 1. Requirements

**R-pcal3-1 — A conductor within the clearance distance of a port's feed, carrying no port, is
included in that port's profile** and therefore in its calibration standard, over the standard's full
length. The neighbour is reproduced from the DUT's own gridlines, exactly as the port's own conductor
already is — not idealised, not re-meshed.

**R-pcal3-2 — The standard must be uniform along its length, and a neighbour that is not is DECLINED.**
The two-line calibration needs a section it can vary the length of. A neighbour that bends, ends, or
changes width inside the standard's run cannot be extruded, and guessing costs more than declining.
Decline by name, fall through to today's behaviour plus PCAL2's refusal, and say which conductor and
why. This is `PlanarFeedExtension`'s own rule and it should read like it.

**R-pcal3-3 — What the neighbour does at the standard's ends is a decision, not an accident, and the
brief must record which was taken and why.** The DUT's neighbour continues; the standard's has to stop
somewhere. Leaving it open, shorting it to ground, and extending it past the port plane are three
different structures with three different error boxes, and the two standards must treat it
*identically* or D5's γ extraction is measuring the difference. Measure at least two of the three
against the oracle rather than reasoning about it.

**R-pcal3-4 — Nothing that passes today changes.** A port with a clear feed builds the profile it
builds today and produces a **bit-identical** Touchstone. Held by a test, not by inspection.

**R-pcal3-5 — Cost is reported before it is accepted.** A wider profile is a wider standard, and the
overview's original run already spent **6.51× the DUT's unknowns** on standards, with one standard at
N = 5,874 against a DUT of N = 2,112 — standards are already the dominant cost and a wider profile
makes them worse. Report the unknown counts and the wall clock before and after, and if the ratio goes
somewhere unreasonable, **stop and report** rather than shipping it behind the accelerated solve.

**R-pcal3-6 — The oracle is Kernel A where the geometry allows it, and separation otherwise.** Same
rule as PCAL1: Kernel A is never an input (overview §3b). For a portless neighbour the oracle is
Kernel A's exact four-port **reduced with the neighbour's ports terminated in Γ = +1** — PCAL1
established that this reduction is sound by measuring that its own well-separated floor is the same
0.052 the four-port comparison has.

---

## 2. What not to do

- **Do not widen the profile "a bit" by default.** The width is the clearance distance PCAL1 measured
  — **2 substrate heights for a portless neighbour** — and a profile wider than it needs to be costs
  unknowns on every standard, on every run, forever.
- **Do not include metal on another level.** PCAL1 did not measure one — every case it swept was
  co-planar with the port — so there is no evidence either way and "it was checked" must not be read
  into its silence. Start with the port's own conductor level and say so.
- **Do not let this brief grow into brief 4.** The moment the neighbour has a port on it, decline and
  stop. Two modes is a different piece of algebra and merging the two briefs will produce neither.

---

## 3. Gates

1. `testdata/portcal/coupled-pair` with ports 3 and 4 deleted — 18.0 dB out in S₁₁ at 1 GHz today —
   de-embeds to within **0.005 of the kernel-A-vs-kernel-B floor** (0.052 on that fixture; PCAL1 §1
   for why the floor has to be subtracted and why refining the mesh does not move it), and is
   **passive across the band**.
2. The clear-feed case is byte-identical (R-pcal3-4).
3. A neighbour that bends inside the standard's run is declined **by name**, and the run then behaves
   as PCAL2 specifies.
4. The standard's own coordinates still satisfy R-prt-5's equality against the DUT's mesh — the
   construction is still a construction, not a tolerance.
5. Unknown counts and wall clock reported for the fixture set, before and after.

---

## 4. On completion

1. Findings to **`src/Engine/Mom/RESOLVED.md`**, including R-pcal3-3's measured decision about the
   neighbour's ends — that is the part a later reader will not be able to re-derive.
2. **Do not write any of this to a `CLAUDE.md`.**
3. `docs/user/src/reference/mom-engine.md`: the validity condition changes, so the chapter that states
   it must change with it. Regenerate the docs.
4. Update the app note: its remedy is currently "separate the feeds", and for this class of neighbour
   it will no longer need to be.
