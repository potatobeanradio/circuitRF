# Sonnet Brief — AUT-9: result documents a caller can trust and afford

**Read `brief-automation-7-mcp-hardening.md` first** — §4 there carries the measured payload sizes
this brief acts on. Depends on **AUT-1**: the result document and the structured-failure shape are
its output.

**Scope: what a result document says, and what it costs a caller to receive.** Two halves that belong
together because they interlock — R-aut9-10's "return the shape inline" is most of R-aut9-9's summary
mode. The first seven requirements are correctness: a result that is wrong, missing, or in units the
caller cannot determine. The last five are size.

Nothing here changes a computed value. Every requirement is about the reporting of one.

---

## R-aut9-1. `run sparam` with a `.npy` output path writes a Touchstone under that name

The `run` tool's schema says the extension picks the format. For `sparam` it does not: the file
begins `! NOTE:` and is Touchstone. `render --data` then refuses it with `Not a NumPy .npy file:
expected magic \x93NUMPY, got 21-20-4E-4F-54-45`, which is accurate and useless — the caller asked
for `.npy` and was given something else without being told.

Either honour the extension for `sparam`, or **refuse the `.npy` request naming the formats `sparam`
can write**. Writing format A to a path named B is the one option to remove. This is recorded in
project memory as a known behaviour; it is reachable from the MCP surface and should now be fixed
rather than remembered.

## R-aut9-2. Per-port reference impedance is reported as the uniform value

A two-port whose second port was declared `Z=12 Ohm` produced a Touchstone whose header reports
`Port 1: Z0 = <50; 0>` and `Port 2: Z0 = <50; 0>`, and a `Z0` cube reading `[[50,0],[50,0]]`. The
solve plainly honours 12 Ω — the DC reflection coefficient is exactly `(12-50)/(12+50)`. Only the
reporting is wrong, and it is wrong in the direction that matters: **a client that renormalises using
the reported `Z0` computes a wrong answer from a correct simulation.** Project memory records a
related note about this printing path; close it. Cover both the Touchstone header and the `Z0` cube
returned by `read` and by `run`.

## R-aut9-3. One quantity is reported in two different units inside one result file

A loadpull-pursuit result carries `Efficiency` as a per-point cube and `MXE_Eff` as a summary scalar.
On the same run, at the same operating point, the cube reads `65.84` and the scalar reads `0.7087`.
**The cube is in percent and the scalar is a fraction.** Nothing in either cube's metadata says which.

A client reading `MXE_Eff` and formatting it as a percentage reports 0.7%; one reading `Efficiency`
and multiplying by 100 reports 6584%. Both are one plausible line of code. Pick one convention for a
dimensionless ratio, apply it across every cube and scalar, and **carry the unit in the cube metadata
so it does not have to be inferred at all** — the axes already carry units, the values do not.

Audit the whole result vocabulary for the same split while you are here; efficiency is unlikely to be
the only ratio in it.

## R-aut9-4. A run whose device is entirely inert must say so

A bench whose device was inert — the mis-wired tuner of
`brief-automation-7-mcp-hardening.md` §3 — returned `Pout = -300.00 dBm, Gt = -300.00 dB, compr = 0.00 dB` at
**every** one of 56 drive points, then `Unscorable (Stop=PinMax)`, then an MXE seed of `Eff=-∞%` — and
exited `status: ok` with **no diagnostic**. `-300 dBm` is the engine's own floor sentinel, not a
measurement. This is the run that persuaded the client a working component was broken.

Emit a warning when a drive ladder produces the floor at every rung, and say what it means: the
device delivered no power at any drive, which usually means it is unbiased or off. A human reading a
terminal infers this from the shape of the log; a client reading `status: ok` and a result document
does not.

## R-aut9-5. A non-converged run returns no diagnostics and no partial results

A loadpull that failed to converge at every grid point returned `status: not-converged`, `exitCode 2`,
an **empty** `diagnostics` array and **no** `result`. The stderr log carried a per-grid-point account
of exactly what happened; none of it reached the caller.

`status` alone is not a diagnosis. Return at minimum: how many grid points were attempted, how many
converged, the stop code distribution, and the partial grid where one exists. A structured surface
whose failure payload is emptier than its own console output has the split backwards — AUT-1's whole
premise is that the failure is the payload.

## R-aut9-6. The gap between the tickle and the first drive step is not checked

The default ladder tickles at −50 dBm and then starts the sweep at −20 dBm — a 30 dB jump. Every one
of 44 grid points then failed at the first real drive step, reporting `2 Pin steps, 1 converged`;
lowering `PinStart` to −45 made the identical bench converge in 38 s. Project memory records this
failure mode already: a large bracket jump breaks the harmonic-balance warm start.

The engine knows both numbers before it starts. **Warn when the first drive step is far above the
tickle**, and say what to change. A blanket non-convergence that a single parameter fixes should not
look like a broken circuit — which, combined with R-aut9-5, is exactly what it looked like.

## R-aut9-7. `render --data` reports an unreadable data file as an unreadable data display

When a data display's own `SelectedDataSource` cannot be read, the refusal is
`render: '<path>' is not a readable data display` — but `<path>` is the *result* file named by the
document, not the display being rendered. A caller reasonably concludes the `.cdd` it just wrote is
malformed and rewrites it. Name the source that failed, say which document referenced it, and keep
the two refusal texts distinct: a bad `--data` argument, a bad reference inside the document, and a
bad document are three different problems.

## R-aut9-8. Every diagnostic is emitted twice

`message` and `arguments.text` carry byte-identical strings on the great majority of diagnostics. On
one Gerber import that is roughly 15 KB of exact duplication in a single response. Emit `text` in
`arguments` **only when it differs from `message`**, or drop it and let structured arguments carry
only the substitutable values. This is the cheapest single reduction available and it costs no
information.

## R-aut9-9. Results return whole and cannot be narrowed by axis

A 551-point two-port S-parameter run returns every frequency value and all 2,204 complex entries
inline. `only` and `group` narrow by *cube name*, which does nothing when the result has one cube.
The task's actual question was the value of one entry at one frequency; answering it required
receiving the entire dataset and then re-reading the written file.

Add axis-level narrowing to both `run` and `read` — at minimum an `at` selector taking unit-bearing
axis values (`{"freq": "2GHz"}`, nearest point or interpolated, and say which), and an axis range.
Consider a `format: "summary"` that returns extents, cube names, axis lengths and units without
values, so a client can decide what it wants before paying for it.

## R-aut9-10. `run` returns the result inline for some analyses and an empty document for others

`run sparam` returns the full result inline in `result.groups`. `run lpp` returns
`status: ok`, the written output path, and **no `result` at all** — the caller must `read` the written
file back to see anything. Nothing in the tool schema distinguishes the two.

Whichever behaviour is intended, make it uniform and state it. If large results are deliberately
file-only, say so in the schema and return the shape (group names, cube names, axis lengths) inline so
a caller knows what it may ask for — which is also most of R-aut9-9's `format: "summary"`.

## R-aut9-11. `import` and `convert` need a summary mode

The Gerber import's diagnostics are the best-written text on the whole surface and should not be
weakened. They should not be the *default* payload for a listing call whose answer is one cell name.
Add `summary: true`, returning counts by severity plus the outputs, and a path to the full text.
Prefer the same option anywhere a verb routinely emits tens of notes.

## R-aut9-12. Return structured content rather than JSON inside a JSON string

Every tool result is a JSON document serialised into `content[0].text`, so every quote and every
em-dash is escaped and every client parses twice. Emit structured content where the protocol allows
it, keeping the current shape as the fallback.

---

---

## Gates

Per-requirement gates are stated above. In addition:

**A test asserting that every cube a run emits carries a unit in its metadata**, so R-aut9-3 cannot
recur in a quantity nobody thought about.

**A byte-size assertion on one representative result document** — a regression that reintroduces the
duplicated diagnostic text or the un-narrowable payload should fail visibly rather than be noticed
months later on someone's token bill.

## On completion

Write the findings to the relevant `RESOLVED.md`; **never to a `CLAUDE.md`.** Update
`docs/design/cli.md` and `docs/design/automation-architecture.md`, and the user docs at
`docs/user/src/reference/cli.md` where a verb or flag changed — do not regenerate the user docs.
See `brief-automation-7-mcp-hardening.md` §7 for the series-level completion rules.
