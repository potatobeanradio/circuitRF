# Brief — ANT-12: the user page, and a shipped example antenna

**Series:** `brief-antenna-0-overview.md` §4. **Depends on:** whatever has shipped. **Blocks:** nothing.

The series is not delivered until a user can find out what it does and what it does not, and until
there is one antenna in the tree that is known to be right.

**This brief can be taken incrementally** — after ANT-4/5, and again after ANT-11 — rather than only
at the end. The Cannot list in particular should never be more than one phase out of date.

---

## 1. M1 — the Cannot list is the deliverable, not the Can list

`docs/user/src/reference/mom-engine.md` opens its limits section with the standing rule:

> A user who discovers a limit by getting a wrong answer has been failed by the documentation.

Every limit in `brief-antenna-0-overview.md` §2 belongs there, in the user's words:

- **The ground plane is laterally infinite.** No back radiation, no front-to-back (until ANT-11), and
  a directivity that reads optimistic on a small ground plane. Give the number that makes it concrete:
  the example board's ground is 0.40 λ₀ across.
- **Apertures in the ground plane are not representable at all** — so slot antennas, CPW-fed slots and
  aperture-coupled patches are out. Edge-fed, inset-fed and probe-fed are in. **Say which feeds work**,
  not only which do not; a limits list that does not tell you what to do instead is half a list.
- **The metal is a perfect conductor**, so radiation efficiency reads optimistic by the copper term.
  The existing page already says conductor loss is missing for kernel B; the antenna page must connect
  that to efficiency explicitly, because a user reading "97 % efficient" will not go looking.
- **Surface-wave power is booked as loss**, permanently, because the substrate is laterally infinite.
  On a small board it reaches the edge and radiates. So the reported efficiency is a lower bound and
  the pattern is missing that contribution.
- **Which cross-pol definition** (ANT-6) and **which gain** (ANT-5) — the two places a number is
  reproducible only if the convention is stated.

**The design note and the user page must not contradict each other** — the existing standing rule for
this pair. Anything ANT-4 through ANT-11 wrote into `docs/design/mom-engine.md` gets its user-facing
half here, in the same phase where possible.

## 2. M2 — an antenna page

A new section, or a sibling page, covering the workflow rather than the physics:

- how to set a patch up — the port at the feed, or the internal port for a probe feed;
- **which mesh intent to choose** (ANT-3), and why a radiator is not a line;
- how to find the resonance (ANT-9) and why a 10-point sweep across a decade will not;
- how to read the pattern plots, and what normalised vs absolute means;
- how to read the loss itemisation, and what to change for each term;
- the limits from §1, at the point where they bite rather than only in a list.

**The probe-fed case deserves its own worked paragraph.** The existing internal port type is defined as
*between the metal and the ground plane at the point you put the label* — which is a coaxial probe
feed exactly, with no feed line and therefore no de-embedding. It is the cleanest antenna the tool can
express and it is not obvious from the port table that it is an antenna feed at all.

## 3. M3 — the shipped example

**Two designs, and they are for different purposes. Do not conflate them.**

### 3a. A validation example — a good antenna

The measured board is a poor patch by construction: the intended reference is the first inner plane at
203.2 µm, so h/λ₀ = 0.0012, giving very low radiation resistance, a high Q, a sub-1 % bandwidth and
dielectric loss dominating everything. It is a fine board and it is a bad teaching example, because
every number it produces is dominated by a substrate choice rather than by the antenna.

**Ship a well-proportioned patch instead** — a thicker, lower-loss substrate, a feed inset that
actually matches 50 Ω, and a resonance the adaptive sweep can find. It should demonstrate, in one run:
an S11 notch, a pattern with a recognisable broadside lobe, a directivity in the range a patch is
known to give, an efficiency itemisation where the terms are all visible, and a current map that shows
the half-cosine.

**Use conformal boundary cells for it.** ANT-6 §4 records why that matters more for an antenna than
for a filter — staircase quantisation of the patch length is a direct error in resonant frequency —
and an example is exactly where the better setting should be shown, even though the default does not
move.

### 3b. The imported board stays as an import regression case

It is the fixture that proves ANT-1, ANT-2 and ANT-3 on artwork nobody authored for the solver:
Gerber-rounded corners, connector via lands, a stroked feed, a finite pour on a ground-designated
layer. **Anonymise it** — no personal paths, no workspace names, no user name; the fixture should
carry the *shape* of the problem, not its provenance.

## 4. M4 — the CLI does all of it

Everything in §3 must run headlessly: `circuitrf em` on the example produces the pattern cubes and the
metrics, `circuitrf plot` produces the cuts, `circuitrf check` passes on the workspace. That is what
makes the example a gate rather than a screenshot.

If any of it cannot, that is a finding about the CLI surface and it belongs in the write-up.

## 5. Gates

- The user page's Can and Cannot lists cover every limit in `brief-antenna-0-overview.md` §2, and the
  design note and the user page agree — assert by review, and record the pairing.
- **The example is a test**: it runs end to end through the CLI, headless, and its metrics sit inside
  stated tolerances. That is what stops the series from silently regressing.
- The example's numbers are checked against the **cavity model** — an independent analytic reference,
  the same oracle ANT-5 uses — and the comparison is in the docs, so a user can see how close the two
  are and judge the tool by it.
- The imported regression fixture extracts the metal it should (ANT-1), meshes under the ceiling
  (ANT-2, ANT-3) and carries no personal path or name.
- Docs build; `DocGen`'s figures are checked for the known nondeterministic families before any figure
  churn is reported as a change.
- `RESOLVED.md` write-up.

## 6. Must NOT

- **Do not ship the measured board as the example antenna.** §3a.
- **Do not put a personal path, workspace name or user name in any fixture.**
- **Do not write a Cannot list that offers no alternative.**
- **Do not let the user page and the design note drift.**
- **Do not report a metric in the docs without its convention.**

## 7. Reading order

`docs/user/src/reference/mom-engine.md` §"What can and cannot be simulated" and §"Ports" (the port
table, and the internal-port row that is a probe feed) · `docs/design/mom-engine.md` §10.9 ·
`docs/design/user-docs-factory.md` · the completed briefs in this series, for what actually shipped ·
`brief-antenna-0-overview.md` §2 and §5.
