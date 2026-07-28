# Sonnet Brief — Make `dotnet test` fast BY DEFAULT (third attempt — the first two failed)

**Read this first.** Two previous briefs tried to fix test time and both failed, for the same reason: they
relied on a human remembering a `--filter`. `brief-test-suite-fast-loop.md` documented a filter.
`brief-benchmark-gate-split.md` redefined "the gate" as a filtered command and **explicitly instructed
"do not silently change what `dotnet test` with no filter does."** That instruction was wrong and is
**hereby reversed.**

**The requirement, stated by the owner three times:**

> The bare command `dotnet test` — with no arguments, no filter, no flags — must not run the long tests.
> The 500k-shape benchmarks must not run for **anything**, including `Category!=Nightly`, unless explicitly
> asked for. Nobody will remember a filter. Fast must be the default.

Do not re-measure or re-justify the 500k runtime. It is known. Change the default.

---

## 1. The mechanism: a repo-wide default filter that `dotnet test` picks up automatically

**R-tst-A. A `.runsettings` at the repo root sets `RunConfiguration/TestCaseFilter`, and
`Directory.Build.props` sets `RunSettingsFilePath` so every invocation of `dotnet test` uses it without
being told.**

```xml
<!-- circuitrf.runsettings -->
<RunSettings>
  <RunConfiguration>
    <TestCaseFilter>Category!=Benchmark</TestCaseFilter>
  </RunConfiguration>
</RunSettings>
```

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <RunSettingsFilePath>$(MSBuildThisFileDirectory)circuitrf.runsettings</RunSettingsFilePath>
</PropertyGroup>
```

This is the whole point: **the exclusion is a property of the repository, not of the command someone types.**
`dotnet test`, `dotnet test tests/Ui.Tests`, an IDE test run, and a CI invocation all inherit it. There is
nothing to remember and nothing to get wrong.

A command-line `--filter` **overrides** the runsettings filter, so opting in stays possible:

```
dotnet test --filter "Category=Benchmark"        # the benchmarks, and only them
```

**Verify this override behaviour early** — if this VSTest version merges rather than overrides, use a
distinct opt-in path (§3) instead and say so.

## 2. One tag, not three

`Slow` and `Nightly` currently overlap and neither is excluded by default. Consolidate:

**R-tst-B. `Category=Benchmark` marks anything that should never run in a routine test pass.** Retire
`Nightly`. Keep `Slow` **only** if it means something distinct that should still run by default; if it
doesn't, fold it in and delete it. Two tags with fuzzy boundaries is how the 500k cases stayed reachable.

**Tag by cost, with a stated threshold** — suggest **anything over ~5 seconds** — not by subject matter.
The rule must be mechanical so the next person adding a slow test knows what to do without asking.

## 3. Better still: benchmarks are not tests

**R-tst-C. Move the 500k *timed sweeps* out of the assertion suite entirely.** They measure; they do not
assert. A measurement that fails a machine-dependent threshold is a flaky test, and a measurement that
asserts nothing is not a test at all.

Land §§1–2 first — that fixes the owner's problem today. Then, as a follow-up in the same pass if it is
cheap: put the timed sweeps behind an explicit runner (a small console entry point, or a project outside the
solution's test discovery) so they are *unreachable* by `dotnet test` rather than merely filtered out.
**Cheap counter assertions may stay** as ordinary tests — they are one frame and they are what catch
algorithmic regressions.

If §3 turns out to be more than a small change, stop and report — §§1–2 are the requirement.

## 4. Guardrails

- **No test is deleted, skipped, or weakened.** Tagging changes *when* things run, never whether they exist.
- Do not use `[Fact(Skip=…)]`.
- Do not change any product code.
- The opt-in path must stay a one-liner. If running the benchmarks becomes awkward, they will never be run.

## 5. Gate — measured, not asserted

1. **`dotnet test` at the repo root, with no arguments, completes in under 60 seconds.** Report the actual
   time and test count. This single number is the brief.
2. `dotnet test tests/Ui.Tests` with no filter is likewise fast — the runsettings applies per-project too.
3. `dotnet test --filter "Category=Benchmark"` still runs the full timed sweeps, unchanged in what they
   measure.
4. The total number of tests that *exist* is unchanged; only the number that run by default differs, and
   the difference is exactly the `Benchmark` set.
5. No remaining test outside the `Benchmark` category takes longer than the stated threshold — list any that
   do, with times.

## 6. On completion

Update the root `CLAUDE.md` testing section to say, in one short paragraph:

- `dotnet test` is fast by default and needs no filter;
- `--filter "Category=Benchmark"` is how you run the benchmarks, and when you should
  (touching rendering, the spatial index, the caches, LOD, or at a performance-phase boundary);
- how to tag a new slow test, with the threshold.

**Delete or supersede the conflicting guidance** from `brief-test-suite-fast-loop.md` and
`brief-benchmark-gate-split.md` so no future reader follows the old two-command incantation. Every brief's
gate item 1 now means plain `dotnet test`.
