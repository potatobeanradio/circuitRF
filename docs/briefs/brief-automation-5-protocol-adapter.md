# Sonnet Brief — AUT-5: The protocol adapter

**Read `brief-automation-0-overview.md` first.** This brief **depends on AUT-1 through AUT-4** and
adds **no capability of its own** — R-aut-1 and R-aut-13. The architecture is
`docs/design/automation-architecture.md`.

**Scope: one adapter, and a parity test.** It translates an external protocol request into a
capability call and translates the result back. If it makes a decision, chooses a default, or
implements a rule, it has broken R-aut-1 and the rule belongs below it.

**This is the disposable layer.** It is written expecting to be replaced or joined by others, and it
must be deletable in one commit without taking anything with it. Everything durable was built in
AUT-1 through AUT-4.

---

## 1. What it is

A **stdio protocol server** that advertises circuitRF's capabilities to an external client and
invokes them on request. The concrete target is the Model Context Protocol — a JSON-RPC convention in
which a server advertises tools, resources and prompts over stdin/stdout, and a client discovers and
calls them.

The protocol is named here as the first adapter, not as the architecture. R-aut-13 exists so a second
one costs a few hundred lines and a deleted one costs nothing.

---

## 2. How it ships

**R-aut5-1. It is a verb on the existing binary — `circuitrf serve` — not a second executable.**

Not a style preference. Packaging is the constraint:

- Three platform scripts each build everything their platform ships, and **the no-argument form is
  the release form** — 1.0.0-beta.2 shipped 7 of its 15 artifacts, silently, when one script
  defaulted narrowly.
- **What ships is named after the application, not the assembly**, and that name is repeated as a
  literal in **five packaging files** (the `.wxs` + `build-windows.ps1`, the Debian `postinst` +
  `.desktop`, the three `bundleFor*MacOS.sh` + their `Info.plist`s), because .NET names the published
  host after the assembly and `CrfRenameApphost` renames it after publish only.
- `PackagingScriptTests` holds both rules shut.

A second executable means a second apphost rename, a second set of literals across those files, and a
second thing a platform script can silently omit. A verb costs none of that.

**R-aut5-2. `serve` is exempt from `cli.md` §3.1's channel split, and only `serve`.** stdout carries
the protocol framing, so **nothing else may be written to it — ever**. Every engine progress line,
`[circuitRF]` note and worker log that the run verbs deliberately send to stderr must keep going
there; a single stray `Console.WriteLine` on a code path reached under `serve` corrupts the stream
and the client sees a parse error with no indication of the cause.

Audit for this rather than hoping: run every capability the server exposes under a redirected stdout
and assert not one byte arrives outside the framing. `PrintWorkerOutput` (`Program.cs:1213`), the
`ProcessDeviceWorkerTransport.Logged` hook (`:35`) and the EM progress reporter
(`EmProgressToStderr:1139`) are the three known chatterers; the test is what finds the fourth.

**R-aut5-3. The project is firewall-gated.** If it lands as its own project, add it to
`UiFirewallTests.NonUiAssemblies` in the same commit. That list's own comments explain why:
a project that starts clean and is not gated does not stay clean.

---

## 3. The tool surface

**R-aut5-4. Small and broad — the surface is a standing cost, not a per-call one.** R-aut-9: a client
that discovers tools up front carries every tool's description for the whole session, whether or not
it calls it. Start here and add only on evidence:

| Tool | Capability behind it |
|---|---|
| `run` | `sparam` / `dc` / `hb` / `lp` / `lpp` / `em`, selected by argument — one tool, not six |
| `check` | AUT-4 |
| `explain` | AUT-4 |
| `create` | AUT-3's `new workspace` / `new cell` |
| `import` | AUT-3's `import part`, and `convert` |
| `read` | read a document or a result file back |

**R-aut5-5. Every tool returns AUT-1's document, unchanged.** The result-document builder lives in
`src/RfCore/Export` and the `Diagnostic` serializer in `src/Diagnostics` precisely so that this
adapter emits bytes identical to the CLI's `--json` (AUT-1 R-aut1-9). The adapter does not reshape,
summarise or re-wrap them.

**R-aut5-6. Narrowing is exposed, because reading is the expensive direction.** AUT-1's `--only` and
`--group` (R-aut1-4) must be reachable as tool arguments. A client that receives eight full loadpull
cubes when it wanted one number is the failure mode this whole series is trying to avoid.

**R-aut5-7. Tool descriptions are terse, English and invariant.** They are read by a machine that
pays for every word of them, and `cli.md` §7A's rule applies here as it does to stderr: never
localized, ever.

---

## 4. What the adapter must refuse

**R-aut5-8. The server runs with the invoking user's authority and touches their filesystem. It
constrains what it will do, and it does so in one place.**

- **A root directory is required at startup** — `circuitrf serve --root <dir>` — and every path
  argument resolves under it. A path escaping it is a refusal naming the root, not a silent clamp.
- **No shell, no arbitrary process launch.** The device-worker and PCell paths already launch
  processes and must keep working; nothing new becomes launchable because a client asked.
- **Destructive operations are refusals, not confirmations.** There is no user at the other end to
  confirm: no delete, no overwrite of an existing workspace (AUT-3 R-aut3-6 already refuses this), no
  writing outside the paths a capability itself chooses. A client that wants a file gone deletes it
  itself.
- **A long run is cancellable and reports progress**, through the same `RunControl` the `em` verb
  already uses (`EmProgressToStderr:1139`) — a de-embedded full-wave point can cost over a minute,
  and a client with no progress and no cancel will simply time out and retry, doubling the cost.

---

## 5. Gates

1. **Parity, and it is the gate this brief exists to pass** (R-aut-13). For every tool, a test that
   invokes it through the server and invokes the same capability through the CLI with `--json`, and
   asserts the two documents are **byte-identical** — exempting only what is legitimately variable
   (a write timestamp, as `EmCliVerbTests` already exempts for provenance). A tool that can do
   something the CLI cannot, or vice versa, fails this test, which is the point.
2. **stdout purity** (R-aut5-2): every exposed capability exercised with stdout captured; not one
   byte outside the framing. Include an EM run and a run with a device worker, since those are the
   loudest.
3. **Root confinement** (R-aut5-8): `../` traversal, an absolute path outside the root, and a symlink
   pointing outside are each refused with a diagnostic naming the root.
4. **The server starts, advertises and shuts down cleanly** without a client, and survives a client
   that disconnects mid-run.
5. **Firewall** (R-aut5-3): `dotnet test tests/Firewall.Tests`.
6. **Full solution green.** This is the series' phase boundary. Run it **once** and read
   `tests/*/TestResults/last-run.trx` for failures.

---

## 6. On completion

Write the findings to `src/Cli/RESOLVED.md` — **never to a `CLAUDE.md`**. Update
`docs/design/cli.md` (§2's verb table, §9, and §3.1 with R-aut5-2's exemption) and
`docs/design/automation-architecture.md` §8.

**Report, do not silently absorb:**
- Anything the adapter needed that did not already exist below the firewall — by R-aut-1 that is a
  capability gap and should have been caught in an earlier brief.
- Every stdout leak §5.2 found, including ones on paths not listed in R-aut5-2.
- Any place the parity test could not be written, and why.

---

## 7. Update `README.md` — the whole series' source layout, in one pass

**This is the last brief in the series, so the README is updated here, once, against the finished
tree** — not five times against intermediate states.

`README.md` describes the source tree in **three** places, and all three drift together:

1. **`## Architecture`** (from ~line 135) — the annotated project list: `src/RfCore`, `src/Core`,
   `src/Engine`, `src/Design`, `src/Ui`, `src/Harmonica`, `src/WBond`, `src/Cli`.
2. **`### The framework firewall`** (from ~line 225) — names every project that references no UI
   framework, and describes the `src/Design` carve-out as the worked example.
3. **`## Source layout`** (from ~line 248) — the full directory tree, with per-folder annotations
   that state which half of a split lives where (`Drc/` "the DRC engine stays in src/Ui",
   `PCells/` "the generators stay in src/Ui", `Em/` "the RUN is in src/Design").

**R-aut5-9. Every one of those annotations that this series falsified must be corrected, not left
standing.** At minimum, after AUT-2 through AUT-5:

- `src/Design` gains `Schematic/` and `Symbol/` — add them to the tree with annotations saying what
  moved and what deliberately did not (the editors, the canvas, the edit session, `PlacementService`).
- The `Drc/` annotation changes if AUT-4 moved the engine, and stays as written if it did not. **Read
  the tree; do not assume the brief succeeded.**
- The firewall section's project list gains anything new, and its `src/Design` example is worth
  updating to mention the schematic move as the second instance of the same operation.
- `src/Cli`'s line gains the verbs this series added, and `serve` alongside them.
- If `src/Ui/Schematic` no longer holds the schematic model, the `src/Ui` annotations that say it
  does are now wrong.

Also update the CLI section under `## Running circuitRF` (from ~line 427) with the new verbs, and
`BUILDING.md` if AUT-5 landed as its own project rather than a verb — the repo-root `CLAUDE.md`
requires `README.md` and `BUILDING.md` to be kept in step.

Finally, update the user docs `/docs/user/src/reference/cli.md` to reflect all the new verbs,
structured results and structured failures, and check / explain.  Provide examples of each.
Do not regenerate the user docs.

**Gate: a source-scan test asserting every `src/…` path named in `README.md`'s source-layout tree
actually exists.** The three lists have drifted before and nothing catches it; a path that no longer
exists is the cheapest possible signal that the annotation beside it is also stale.
