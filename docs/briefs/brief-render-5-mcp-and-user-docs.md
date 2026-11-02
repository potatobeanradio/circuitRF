# Sonnet Brief — RND-5: advertising it, and writing it down

**Read `brief-render-0-overview.md` first.** Depends on **RND-2** and **RND-3**; **RND-4** if it
landed, and if it did not, this brief says so rather than describing a capability that does not exist.

**Scope: making the new surface discoverable, by a protocol client and by a person.** Two halves:
`src/Cli/Serve/ToolCatalog.cs`, and `docs/user/src/reference/cli.md`. No new behaviour on either side
— a discoverability brief that changes what a verb does has stopped being one.

---

## Part A — the protocol adapter

### A.1 One tool, and the existing table is the whole mechanism

**R-rnd5-1. `render` is one new `ToolSpec` row, and `explain`'s existing row gains three
`ToolOption`s.** That is the entire change to the surface: **eight tools, up from seven.**

`ToolCatalog` is one table and **the JSON schema is generated from the same rows that build the
command line** (`cli.md` §11.3). So there is nothing to keep in step: every argument is named after
the CLI flag it becomes, adding a flag is one row, and a description that claims an argument the verb
does not read is caught by
`ServeProtocolAdapterTests.EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` — which found three
real cases the last time it was written. Do not add a hand-written schema.

**R-rnd5-2. `render` is a single-mode tool** (no `SelectorName`) — the document kind comes from the
path, exactly as `check`'s does. Do not make the view type a selector: `--view` is one of several
optional arguments and a selector would advertise three modes where there is one verb.

**R-rnd5-3. `-o` is an `OptKind.Path`**, confined to `--root` by `PathRoot` like every other path.
`render` is the first tool whose *purpose* is to write a file rather than to write one as a side
effect of a run; nothing about the confinement changes, and §11.5 is unaltered — there is still no
tool that deletes, and this one writes only where the caller named.

### A.2 Handing the client the picture

**R-rnd5-4. The tool result carries the rendered image as MCP image content, and this is the point of
the whole series for an agent.** A client that receives only a path has to be able to read that path
— many cannot — and an agent that cannot *see* the picture it asked for has gained nothing over
`--json`.

Three constraints on it, and they matter more than the feature:

- **The same bytes the verb wrote, read back — never a second render.** R-aut-1: the adapter calls
  the verb and does not re-implement it. It attaches the file `outputs` names; it does not draw.
- **Opt-in, and capped.** An image is expensive in a way a JSON document is not: a 2000×1500 PNG is
  megabytes, and base64 adds a third. The attachment is an explicit tool argument (default **off**),
  the cap is a stated constant, and **a render that exceeds it comes back as the path plus a
  diagnostic saying how large it was and what to narrow** (`--size`, `--window`, `--layers`,
  `--detail screen`) — never truncated, and never silently dropped. The refusal is the useful answer;
  a client that asked for a picture and got nothing, with no explanation, retries.
- **SVG and PDF are attached as their own MIME types or not at all.** Do not transcode a PDF to a PNG
  to make it attachable: that is the adapter making a rendering decision, which is the one thing
  §11.1 says it never does.

**R-rnd5-5. Nothing becomes a resource.** `cli.md` §11.3a's rule is that a resource is right for a
standing catalogue whose bytes do not change — the reference topics. A rendered image is per-call and
ephemeral; advertising one as a resource would mean advertising a URI whose content depends on
arguments the URI does not carry.

**`cli.md` is still not a reference topic.** §12.2 excludes it deliberately at 54 kB, and this series
makes it larger. A protocol client has every verb's schema from `tools/list` and needs the page least
of all; that reasoning is unchanged and this brief does not revisit it.

### A.3 Gates

1. **The standard parity gate**, unchanged: for `render` and for each of `explain`'s three new
   options, the document that comes back through the server is compared **byte for byte** against
   what `circuitrf <verb> --json` writes.
2. **`EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads` covers the new rows** — it does so
   automatically, which is the point; assert that it actually exercised them.
3. **The attachment is the file.** The bytes the client receives, decoded, are byte-identical to the
   file on disk (R-rnd5-4).
4. **The cap refuses rather than truncates**, with a diagnostic naming at least one argument that
   would narrow the render.
5. **stdout stays protocol.** §11.2's audit already asserts every byte of stdout is a protocol frame;
   confirm it still runs over the new capabilities — a renderer that prints a progress line to
   `Console.Out` would break the server and nothing else would notice.

---

## Part B — the user documentation

### B.1 What to change

**R-rnd5-6. `docs/user/src/reference/cli.md` is the source; the `.html` is a build product.** Never
edit `docs/user/reference/cli.html` — it is regenerated by `tools/DocGen`.

- **Front matter.** The `lede` says "fourteen verbs" and enumerates them; `render` makes fifteen and
  the enumeration must name it. Add to `keywords`: render, image, SVG, PNG, PDF, export, picture,
  screenshot, plot, viewport, layers, extents, thumbnail.
- **The `<nav class="toc">` block** gains `render`, and the `explain` entry gains its three new
  questions.
- **A `render` section**, placed after `import` and before `check` — the authoring verbs, then the
  verbs that look at what was authored.
- **The `explain` section** gains `--cells`, `--layers` and `--extents`, with the note that all six
  questions are refused together rather than ordered.
- **The `serve` section** gains the eighth tool and the image attachment.
- **`--json`'s section** gains `result.render` and the three new `result.explain` members.
- **`reference/index.md`** and `_nav.txt` if either enumerates verbs.

### B.2 How to write it

**R-rnd5-7. Document the traps, not just the flags.** A flag list is what `--help` is for. The things
a person cannot find out any other way, and which this series learned the hard way, are:

- **A bare number on a layout `--window` is refused, and why.** DBU, µm and mm differ by orders of
  magnitude on identical text, and the wrong one produces a picture that looks fine.
- **`--detail full` is the default and it is not free.** Give the real number: a 6-layer imported
  board is ~764,000 vertices, an SVG stores every one of them, and `--detail screen` is ~7× smaller
  and is what the screen shows. Say which to reach for and when — this is the single most useful
  sentence in the chapter.
- **What `--fit` includes**, and that a symbol's pin labels are measured at render time so its fitted
  box is larger than its `--extents`.
- **`explain --layers` before `render --layers`**, because a technology's layer list and a document's
  used layers are different sets and only the second one draws anything.
- **A `.cdd` needs `--data`**, and an empty plot is a refusal rather than a picture.

**R-rnd5-8. Say what the reader's chosen path buys, not that a cheaper one exists.**
`feedback-dont-tell-users-what-they-know` — the audience for this chapter has already decided to
script circuitRF. Do not explain that the GUI has an Export menu.

**R-rnd5-9. One worked example, end to end**, in the chapter's existing house style: author or open a
workspace, `explain --cells` to find a cell, `explain --layers` to see what it draws on,
`explain --extents` to size a window, `render` a zoomed region of one layer to PNG. That sequence is
the argument for why these are three options and one verb rather than five commands, and it shows it
instead of asserting it.

**R-rnd5-10. Anonymize every path in every example.** `feedback-no-personal-paths-in-repo` — no real
workspace names, no usernames, no home directories. The shape of the path is the content.

### B.3 Regenerating

`dotnet run --project tools/DocGen -- out docs/user`. Two things the repo has learned about this run
and both cost time if forgotten:

- **Run it twice and diff run 1 against run 2** (`docgen-nondeterministic-figure`). Three figure
  families are nondeterministic — a 7.2° rotation, harmonicaRF's HB-solves/fps readout, and a stray
  temp-path row in the Messages panel. Anything that differs between two runs of the *same* tree is
  noise, not a change you made.
- **Classify the churn before reporting it** (`feedback-minimize-generated-file-churn`). A run that
  reports hundreds of changed `.svg`s is a red flag, not a result. The id counter is **hexadecimal**,
  so a `\d+` pattern mis-classifies id churn as real change.

Report the classified diff: figures genuinely changed, figures that are id churn, pages changed.

### B.4 Gates

1. **DocGen runs clean** and the diff is classified per §B.3.
2. **Every option in RND-2 and RND-3 appears in the chapter**, checked against the verbs' own help
   output rather than against the briefs — the code is the contract.
3. **Every command in the chapter runs.** The worked example is executed as a test, end to end, from
   an empty directory, exactly as the `em` walkthrough already is.
4. **The vendor-name grep** before any commit (repo `CLAUDE.md`): no commercial EDA vendor or PDK
   name anywhere in the new prose, in an example path, or in a figure. Report what was removed.

---

## On completion

`src/Cli/RESOLVED.md` for Part A, **never a `CLAUDE.md`**. Update `docs/design/cli.md` §11.3's tool
table and §11.3a, and the repo-root `CLAUDE.md`'s verb list — that file is standing project memory
and a verb missing from it is a verb the next session does not know exists.

**Report, do not silently absorb:**
- The classified DocGen diff (§B.3).
- Whether the image attachment's cap is the right number, from the measured sizes RND-2 §7.4
  produced rather than from a guess.
- Anything the chapter could not explain without describing an implementation detail — that is
  usually a sign the flag is wrong, and it is worth saying so even though this brief cannot fix it.
