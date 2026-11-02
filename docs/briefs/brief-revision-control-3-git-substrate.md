# Sonnet Brief — RC-3: the git substrate

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §2.4, §3.2, §4 (**including §4.5, §4.6 and §4.7, new in rev 3, and
§4.3a, new in rev 5**), §5.2b, §5.6a, §6.1, §8.1, §8.1a and §8.3. **Depends on RC-1** (the `.gitignore`
line that excludes the `.cwsuser`). **rev 5 corrected three things in this brief**: §4.6's serialiser
(R-rc3-1b), the reflog rationale (R-rc3-7), and where the identity comes from (R-rc3-1c); and added the
macOS shim (R-rc3-2a), `safe.directory` per invocation (R-rc3-4), the hook bypass mechanism (R-rc3-7a)
and the reclaim operation (R-rc3-16a).

**Scope: everything that has to be true before circuitRF may take a single commit, and no user-facing
revision-control UI at all.** Finding git, driving it, translating what it says, creating the
repository, generating the two policy files, and owning the packing git will never do for us.

---

## 1. The process boundary

**R-rc3-1. One type owns every git invocation, it starts a subprocess, and it lives in `src/Design`.**
No git library is linked, ever. `src/Cli` must be able to take a checkpoint headlessly and cannot
reference `src/Ui`, so anything above the firewall would put the safety net out of reach of the very
caller §1.2 is written about. `tests/Firewall.Tests` is what holds this.

Why a subprocess at all, from §4.1 — all three reasons belong in the type's own header:

- **The escape hatch survives.** The repository is an ordinary git repository, readable and repairable
  by every existing tool, including the one the user's IT department already supports. Anything
  circuitRF's UI cannot fix, the command line can — worth more than any convenience an embedded
  library would buy, and lost the moment circuitRF writes a format only circuitRF understands.
- **The failure mode is an exit code**, not an in-process fault in a native library, which matters
  given this repo's standing cross-platform native-dependency policy.
- **Absence is detectable and cheap.**

`src/Ui/Updates/ProcessRunner.cs` is the in-repo precedent for running a platform tool and collecting
its output, **including the trap recorded in its header**: a bare program name resolves through `PATH`,
and `PATH` is attacker-influenced on the platforms this targets. Resolve the configured path, or the
discovered one, to an absolute path once and start that. **It is a precedent, not a dependency** — it
lives in `src/Ui`, so `src/Design` cannot reference it. Read it, do not import it, and if the two end
up close enough that a shared primitive is obviously right, **report that rather than deciding it** —
moving a type across the firewall is not this brief's to do quietly.

**R-rc3-1a. Every invocation carries its own environment, and each entry prevents one specific silent
failure** (§4.5):

- **`GIT_TERMINAL_PROMPT=0`.** Without it an operation needing credentials — RC-9's fetch or push
  against a private repository — **blocks forever** on a prompt written to a terminal that does not
  exist. **A hang is the worst failure mode available**, because it is the only one with no message, no
  exit code and no end. Set it in this brief even though RC-9 is the brief that reaches a network:
  the environment belongs to the type that starts the process.
- **A locale pinned to `C`, and machine-readable output wherever a result is parsed.** R-rc3-4's table
  keys on exit codes and stable formats; a localised git will not produce the English a substring match
  expects, and that failure appears only on someone else's machine.
- **`--no-pager`, an explicit working directory, and a timeout on every call.** A subprocess this type
  cannot bound is a subprocess that can wedge a workspace close. **The bound is wall-clock for a local
  call and inactivity for a network one** (§4.5, §9.1, rev 5): a fixed limit turns RC-9's legitimate
  slow clone of a large library into a reported failure, which is not the defect the bound exists for.
- **`safe.directory` naming the open workspace's root**, per invocation (§4.7, rev 5). The only remedy
  a designer could otherwise be handed is a global config write in git vocabulary, which §4.4 rules
  out. Narrowed to the one root, never `*`; the trade — trusting a folder the user chose to open and
  whose contents circuitRF already loads — is stated in the type's header.
- **The commit identity — author AND committer** (§4.4). Git resolves the two separately, and supplying
  only one leaves the other to config or to a guess at `user@hostname`. **Nothing is written to a git
  config file**; where circuitRF's own preference is unset, git's ordinary resolution applies. RC-4
  owns the dialog that captures the value; **R-rc3-1c owns reading it**, so that `src/Cli` sees it too.
- **An empty `core.hooksPath`**, per invocation (R-rc3-7a) — `--no-verify` leaves `post-commit` to fire.
- **Signing off for circuitRF's own commits.** A designer who signs globally would otherwise have every
  automatic checkpoint block on a passphrase prompt with no window to appear in. **Per invocation, not
  in the repository config**, because it is a statement about circuitRF's commits rather than about the
  repository — that designer's own commits from a shell in the same folder should still sign, exactly
  as they configured.

**R-rc3-1b. Two processes on one repository are kept apart by a temporary index and git's own locks —
not by the advisory lock** (§4.6, corrected in rev 5). The GUI, `src/Cli`, the packing scheduler and —
per RC-5 §5.3b — an agent's own shell all reach one repository. rev 4 assigned the serialisation to
`WorkspaceLock` (`src/Design/Workspace/WorkspaceLock.cs`); **read its header before building anything
on it**: it holds no file handle, both "open anyway" and "open read-only" are always available, and it
says a lock this product treated as authoritative *would become a stale file that locks out a team*. It
also fires in exactly the situation RC-5 §2.3 designs for — the GUI and `serve` holding one workspace
at once — so it cannot be what keeps them apart. Three rules:

- **A checkpoint never touches the shared index.** It is built through a private index file (§5.2b,
  RC-5 R-rc5-1c), so the resource git's writers contend on is left to the designer's own shell, and two
  circuitRF processes checkpointing at once contend only on reference updates, which git makes atomic.
- **Git's own lock files serialise what remains.** They are atomic-create files — the correct primitive,
  and the one a second implementation would have had to reinvent. A collision is retried for a short
  bound before R-rc3-4 translates it, because a collision between two automatic operations is a delay.
- **Reads never wait on a writer** — listing restore points or resolving a pin must not block behind a
  pack — and **packing yields to the advisory notice** rather than queuing (R-rc3-14). That is the
  notice used as a notice.

**R-rc3-1c. The commit identity is read from circuitRF's per-user preference file by a type in this
project, so `src/Cli` sees the value the Settings tab captured** (§4.4, §12 Q21, rev 5). rev 4 let the
headless case fall through to git's own resolution because `AppPreferences` is in `src/Ui`. On the fresh
Windows machine §4.4 describes that resolution names nobody, so the AI-batch checkpoint — the governing
motive — was refused for exactly the population it exists to protect, with no remedy but the global
config write circuitRF refuses to make. Reading a JSON file in `AppPreferencesIo.PrefsDir` crosses no
firewall; only the type that owns the dialog does. The reader knows the file's location and the two
identity keys and nothing else about `AppPreferences`; RC-4 writes through its own type as today, and
the two agree on the path and the key names by a shared constant in this project. Where the file names
nobody, git's ordinary resolution applies; where neither does, the feature does not arm and the refusal
names the Settings tab.

**R-rc3-2. Discovery: a configured path outranks `PATH`, and blank means "search `PATH`".** A
preference that lost to `PATH` would be inert on exactly the machine it exists for — the one with two
gits, or one somewhere `PATH` does not reach. This mirrors `VerilogACompilerDiscovery` exactly; RC-4
builds the settings row on top of it.

**R-rc3-2a. On macOS, presence is established without running anything named `git`** (§4.3a, §12 Q23,
rev 5). `/usr/bin/git` exists on every Mac whether or not git is installed: with the Command Line Tools
absent it is a shim that **opens Apple's "install the developer tools?" dialog** instead of running.
Discovery by `git --version` would show that dialog, unbidden, on every launch, to precisely the users
R-rc3-3 promises never learn the feature exists. Check that the tools are installed first —
`xcode-select -p` succeeds, or the receipt is present — and treat a shim without tools as absent. A
configured path (R-rc3-2) is run as configured, because there the user named it.

**R-rc3-3. Absence is silent.** No git on `PATH` and none configured means the feature is simply not
there (§4.3) — every affordance hidden, not disabled. A designer who does not want this should never
learn it exists. **This is the opposite of the hold state RC-6 builds**, and the difference is the
point: absent is harmless, held is a designer who may believe they are protected.

**R-rc3-3a. There is a minimum git version, it is stated once, and a git below it counts as absent**
(§4.7). A floor exists whether or not it is written down; unwritten, it is discovered from a bug
report. Discovery reports the version it found (RC-4's Detect shows it), and an older git is treated as
**absent** rather than as broken — a designer with an old git does not want to be told about a feature
they cannot have.

Set the floor from the oldest behaviour this brief and RC-6 depend on, not from novelty, and **record
which requirement set it** so the next person can lower it deliberately rather than by guessing.

---

## 2. Translating what git says

**R-rc3-4. A bounded set of recognised failures, each mapped to one sentence in circuitRF's
vocabulary.** Git's diagnostics are written for people who already understand git, and shelling out
means inheriting them. At minimum, and each with its own recognisable signature rather than a substring
match on English text where an exit code or a status will do:

| failure | the sentence is about |
|---|---|
| no identity configured | what must be filled in, and where (§4.4) |
| nothing to commit | not a failure — the state is already recorded |
| a lock file left by a crashed process | what to remove, and that it is safe |
| an embedded repository — a directory that is itself a repository, handed to `git add` | that a nested repository was found and excluded (RC-6 R-rc6-7); nothing about gitlinks |
| a non-fast-forward push | that the other side moved (RC-9) |
| git not executable / wrong thing at the path | the path that was tried |
| **`safe.directory` — "detected dubious ownership"** | reached only by a git too old to accept the per-invocation answer (R-rc3-1a); that the workspace is owned by someone else, and that a newer git would not ask |
| a git older than the floor | treated as absent (R-rc3-3a), not reported as broken |
| a credential the remote wanted and git could not supply | what it wanted — never a hang (R-rc3-1a) |

**`safe.directory` is the entry most likely to be left out and most likely to be hit** (§4.7). Git
2.35.2 and later refuse to operate on a repository owned by another user, and that is the **ordinary**
state of a workspace on a network share and of an archive someone else extracted. RF workspaces live on
shares, and §7A's librarian scenario assumes one. Git's own wording must never reach a designer. **And
on a git at or above the floor it is never reached at all**, because R-rc3-1a names the root per
invocation (rev 5); the row exists for the git that predates the option.

**R-rc3-5. Anything unrecognised is reported verbatim and honestly, never swallowed** — the same
posture the run services already take with an engine error. The raw output is always available and
never foregrounded.

**R-rc3-6. Failures are `Diagnostic`s** (`src/Diagnostics`), with stable dotted ids and typed
arguments, so the CLI and the Messages panel render the same failure from one source. Do not flatten a
translated failure into an English sentence on the way out.

---

## 3. Creating the repository

**R-rc3-7. circuitRF creates a repository at the workspace root, and nowhere else** (R-rc0-5), and
**configures it at creation** (§4.5). Every value below is written to the repository's own config,
**never the user's global config** — circuitRF has no business changing a setting that affects every
other repository on the machine.

**The dividing line is what a setting is a property OF, and rev 3 corrected rev 2 on it.** Repository
config carries properties of the **repository** — how it is packed, how its files reach disk — which
are correctly shared by everyone who opens the folder. **Anything that is a property of the PERSON, or
of circuitRF's own behaviour, is supplied per invocation** (R-rc3-1a) and written to no config file.
The commit identity is the clearest case and the one rev 2 put in the wrong place: identity in a
repository's config makes a person a property of a **directory**, so on a network share the second
designer to open the workspace commits under the first one's name (§4.4).

Each row exists to prevent one specific silent failure, and the reason belongs beside the value in the
code:

| setting | value | the failure it prevents |
|---|---|---|
| `gc.auto` | `0` | this brief's byte-based packing schedule and git's count-based one fighting |
| `gc.pruneExpire` | `never` | **R-rc3-16.** Otherwise a routine pack permanently destroys thinned checkpoints after two weeks |
| `gc.reflogExpire`, `gc.reflogExpireUnreachable` | `never` | the reflog is the escape hatch for the **designer's own branch** — a reset or amend from a shell in Stage 3; at its 30-day default it is gone before §1.3 says the designer looks. **It is not the way back after a sweep** (rev 5): a deleted reference takes its reflog with it, and references outside `refs/heads/` carry none by default. RC-6's journal is that way back |
| `core.longpaths` | `true` (Windows) | nested cell folders plus git's own object paths pass 260 characters — a refusal on one machine class only |
| `core.autocrlf` | `false` | §5's byte-stability argument; see R-rc3-12a |
| a **management marker** naming circuitRF, and recording RC-6's §12 Q4 answer | written once | **R-rc3-7b.** "Did circuitRF create this repository" is load-bearing three times and a `.git` directory alone cannot answer it |

**Note what is NOT in that table:** the commit identity and the signing-off flag. Both were in rev 2's
version of it; both are now per-invocation (R-rc3-1a), for the reason above. **A `commit.gpgsign=false`
row survived here from rev 2 and was removed in rev 4** — it contradicted this very note three lines
below itself, and a table beats prose when someone is implementing from it. Writing it would disable
signing for **the designer's own commits from a shell in that folder**, which is exactly the thing
R-rc3-1a says must keep working. Suppressing the signature is a flag on circuitRF's own invocation and
belongs nowhere else.

**R-rc3-7b. The management marker is written at creation and at adoption, and it records which answer
§12 Q4 got.** It is repository configuration because whether circuitRF manages this repository is a
property of the **repository** — which is §4.5's own dividing line, and which produces the two
behaviours wanted: **an archive carries it** (§9A copies the directory, so RC-8's extracted archive is
recognised as circuitRF's without ceremony — R-rc8-5 asserts that and previously had no mechanism), and
**a clone does not** (git does not clone a repository's config), so a clone reaches R-rc0-13's arming
path on its own terms. A marker held in a **reference** instead would be pushed, carrying one designer's
management decision into everybody else's clone. Recording the *answer* rather than merely circuitRF's
presence is what stops RC-6 asking the question on every open.

**R-rc3-7a. Every commit circuitRF makes bypasses hooks.** RC-6's §12 Q4 offers **adoption** of a
repository the user created, which may carry a `pre-commit` hook written for their own workflow. Running
someone's hook on an automatic checkpoint they did not initiate is R-rc0-5's ambush arriving by
another route: the checkpoint is circuitRF's, not theirs, and it must neither be blocked by their
tooling nor set it off. **`--no-verify` is not the mechanism** (rev 5): it skips `pre-commit` and
`commit-msg` only, and `post-commit` still fires. The bypass is an empty `core.hooksPath` supplied per
invocation (R-rc3-1a) — and with R-rc5-1c's plumbing path, `git commit` is not even the command that
runs, so the invocation-level bypass is what covers every path.

**R-rc3-8. Creation is not automatic in this brief.** RC-3 provides the operation; RC-4's settings and
RC-5's first checkpoint decide when it runs. A repository appearing in a folder because the user
launched circuitRF once is a surprise, and RC-6 owns the detection that makes "may I?" answerable.

### 3.1 The generated `.gitignore`

**R-rc3-9. Results are excluded by default** — `.npy`, `.spl`, `.lpcwave`, and simulation output
directories. **The justification is reproducibility, not size:** results are a function of the design
and the engine, so the design is the thing worth versioning. That they are binary and float-valued and
would delta terribly is a second reason, not the first.

**R-rc3-10. `.cwsuser` is excluded** (RC-1 R-rc1-16), and `.generated-cells` with it — a pure cache
every layout rebuilds from its own recorded snapshots, which the archive scanner already skips for the
same reason (`WorkspaceArchiveScanner.cs:36`). **Read that skip list and keep the two in step**; a
file the archive considers rebuildable is a file the repository does too.

**The skip list also names circuitRF's own per-session bookkeeping dropped in a workspace root** —
SL4's advisory lock (`.crf-open.json`) and SL2's write probe — and **those matter more here than in the
archive**, because they are live while the workspace is open, which is exactly when RC-5 takes a
`commit -a`-shaped checkpoint. A checkpoint that captures an advisory lock records one machine's
session in a design's history. Exclude them, and exclude `AtomicFile`'s in-flight temporaries for the
same reason.

**R-rc3-11. New Workspace writes the `.gitignore` whether or not the workspace is a repository.**
`WorkspaceCreate.Create` (`src/Design/Workspace/WorkspaceCreate.cs:84`) is the one function the GUI's
own New Workspace calls and the one `circuitrf new workspace` calls; the file is inert until there is a
repository and correct the moment there is.

**R-rc3-11a. And it is written again whenever a workspace first GAINS a repository** (§8.1a). This is
the half rev 2 of the architecture missed, and on its own R-rc3-11 is not enough: **every workspace
that exists today was created before that function wrote one**, so under R-rc3-11 alone they would
never receive a `.gitignore` at all and would track results forever. They are the workspaces that
matter — they are the ones with years of accumulated output already in them.

The rule is therefore not *"on creation"* but **"a repository circuitRF created has circuitRF's policy
files"**, and every path that creates one reaches it: New Workspace, the first arming of an existing
workspace, and RC-6's **adoption** of a user's repository — adoption because that is circuitRF taking
responsibility for what is kept, and it is the moment the user said yes.

**R-rc3-11b. Once written, the file belongs to the workspace, not to circuitRF.** A designer may edit
it, and RC-5's large-file guard appends to it. **circuitRF adds lines and never rewrites the file**,
and never removes a line it did not add in that same operation. A policy file that is silently
regenerated is a policy file whose user edits vanish — and the user finds out when something they had
excluded turns up in an archive. **The first commit into an existing workspace is RC-5's problem**
(§8.1a): this brief only guarantees the `.gitignore` is there before it happens.

### 3.2 The generated `.gitattributes`

**R-rc3-12a. The same file pins end-of-line treatment for those five types, and this is not
housekeeping** (§4.5). §5's entire measurement rests on `.clay` being byte-stable, line-oriented text
with `long` coordinates. Git's end-of-line conversion makes the bytes on disk **platform-dependent** —
a whole-file diff on every cross-platform exchange, and a `.clay` that is not the file
`LayoutPersistence` wrote. Pin it in `.gitattributes` and set `core.autocrlf=false` in the repository
(R-rc3-7); belt and braces, because the two answer to different scopes and a user's global config can
reintroduce the problem the attribute alone would have fixed. **A design document's bytes are the
design**, which is why this lives in the same file as the unmergeable marking.

**R-rc3-12. `.clay`, `.csch`, `.csym`, `.cws` and `.ctech` are marked unmergeable** (§6.1). A
three-way text merge of a polygon's vertex list can produce geometry that is invalid, or valid and
wrong, while remaining well-formed JSON that opens without complaint. The same is true of a
schematic's connectivity. **A merged design that is silently wrong is worse than a conflict**, because
the conflict is at least visible. Conflict resolution is whole-file, pick a side — RC-7 builds that;
this brief makes it the only thing git will offer.

---

## 4. Packing, which is the real cost and which nothing runs

This is the finding rev 1 of the architecture missed by measuring only the tidied state, and it is the
one this brief must actually design against.

Between commits git writes each new version of a file as a **loose object**: a standalone
zlib-compressed copy of the *entire* blob, no delta against anything. Measured on a real 28.4 MB
board (§2.4):

| | |
|---|---|
| 21 commits (initial + 20 mid-file drags), no `gc` | **63 loose objects, 124.3 MB** |
| the same repository after one `git gc --prune=now` | **12 MB** |

**A tenfold overhang that does not clear itself.** Git's `gc --auto` triggers on `gc.auto`, whose
default is **6,700 loose objects** — a *count*, with no notion of size. A workspace whose history is a
handful of enormous files sits at a few dozen loose objects indefinitely, hundreds of megabytes over
its packed size, and git never once decides to do anything about it. This is exactly the shape of
complaint the feature exists to avoid.

**R-rc3-13. circuitRF owns packing, on a byte-based trigger** — loose-object bytes over a threshold,
never git's count-based one. `gc.auto = 0` in the repository circuitRF creates (R-rc3-7) so the two
schedulers cannot fight.

**R-rc3-14. It runs where it cannot be noticed** — after a workspace closes, or idle. Never in front
of a save or a checkpoint: packing 124 MB is seconds of CPU and heavy I/O.

**R-rc3-15. It must be interruptible and safe to abandon.** `git gc` is, which is part of why it is
the right tool. An abandoned pack leaves a correct repository.

**R-rc3-16. Packing must be CONFIGURED not to prune, and leaving `--prune=now` off does not achieve
that.** This corrects rev 2 of the architecture, which said routine packing prunes nothing. **It does.**
`git gc` runs a prune at `gc.pruneExpire`, whose default is **two weeks**, and expires the reflog at
`gc.reflogExpire` (90 days) and `gc.reflogExpireUnreachable` (**30 days**) — all unasked, all invisible.

RC-6's retention promises a grace period "measured in weeks" and §1.3 promises a recovery window of the
same order; **both are false unless R-rc3-7's `gc.pruneExpire=never` and the two reflog expiries are
set.** Leaving `--prune=now` off remains necessary and was never sufficient. This is the load-bearing
half of R-rc0-7, and it is now three config values rather than one absent flag.

**Verify it rather than assuming it**: create a repository, make an object unreachable, back-date it
past two weeks, run the packing path, and assert the object is still there. That test is the whole
guarantee.

**R-rc3-16a. Reclaiming is a separate operation this brief provides and nothing in this brief calls**
(§5.6a, §12 Q20, rev 5). rev 4's architecture said *"until ordinary packing eventually reclaims them"*
in the same document that set `gc.pruneExpire = never`; both could not be true, and §1.4 decided for
never. So packing never reclaims, and reclaim is one function: given an age, protect every entry in
RC-6's thinning journal **newer** than that age by giving each a temporary reference, prune unreachable
objects with an immediate expiry, drop the temporary references, and remove the journal entries acted
on. The protection step is not optional — git's own expiry is by object age, which is when a state was
*made*, not when it was thinned, so a two-year-old restore point thinned yesterday would otherwise be
reclaimed by *"thinned more than a month ago"*. RC-4 exposes the function as the tab's one confirmed
destructive action; RC-6 owns the journal it reads.

**R-rc3-17. The threshold is a constant in this brief and a setting in RC-4.** Ship a default, measure
it (R-rc0-9), and let RC-4 expose it. Ordinary users never touch it; it exists because §2.4 means
someone eventually asks where the disk went.

---

## 5. The gzip reserve is revoked

`layout-view.md` §4 holds gzip in reserve, and `GzipTextFile.ReadAllTextAutoGzip`
(`src/Design/Layout/LayoutPersistence.cs:316`) already sniffs the magic bytes so a future gzip
**writer** would need no `FormatVersion` bump. Re-measured on the real board (§3.2):

| operation | plain `.clay` | gzipped `.clay` | penalty |
|---|---|---|---|
| 20 shape **additions** | 2.1 KB/commit | 6.6 KB/commit | 3× |
| 20 mid-file **polygon drags** | **5.3 KB/commit** | **2.69 MB/commit** | **~508×** |
| 5 mid-file **deletions** | 1.3 KB/commit | 2.14 MB/commit | ~1,600× |
| final `.git` after 46 commits | **5.12 MB** | **74.22 MB** | **14.5×** |

**R-rc3-18. The comment at `LayoutPersistence.cs:17` becomes a warning rather than an invitation**, and
so does the one on `GzipTextFile`. State the trap precisely, because an append-only test reports a
false pass: **deflate resynchronises after an append**, so the addition row looks almost respectable.
It is the mid-file edit — moving one polygon, which is what designing actually consists of — that
destroys the delta, and deletion is worse still.

**R-rc3-19. State the general rule once, where it will be read**, because it also governs `.npy`:
*compressed or binary content does not delta. A format that saves disk once costs the repository a
full copy on every save. In a versioned workspace, plain text is the compressed format.*

**Nothing else about `.clay` changes.** §2 measured 46 design commits at 197 KB against a 28.4 MB
layout: **no change to how `.clay` is written is required**, and the minimal-diff serializer that
investigation set out to design is unnecessary. Shape-order stability matters far less than intuition
suggests — a full reorder of 3,284 shapes costs 41 KB, because git's delta compression is
content-based rather than line-based. **Preserve shape order for the human reading a diff, and never
let git be the reason a serializer is constrained.**

---

## 6. Gates

1. **Firewall.** `src/Design` references no Avalonia; the git type is reachable from `src/Cli`.
2. **Absence.** With no git on `PATH` and none configured, every entry point answers "not available"
   and nothing throws, nothing is created, and nothing is posted (R-rc3-3).
3. **Discovery precedence.** A configured path outranks `PATH`; a configured path that is not git
   produces the translated failure naming the path tried, not a stack trace.
4. **Every recognised failure in R-rc3-4's table is reached by a test with a deliberately broken
   repository**, asserting the `Diagnostic` id. Anonymize fixture paths to the *shape* of a path — a
   real workspace name must never reach the repo.
5. **An unrecognised failure survives verbatim** (R-rc3-5): assert the raw text is carried, not
   replaced by a generic sentence.
6. **The repository configuration is complete** (R-rc3-7): assert every row of the table on a freshly
   created repository, and assert **no code path anywhere passes `--prune`** — source-scan, comments
   stripped. `gc.auto=0` alone is not the gate it was in rev 2.
7. **Packing does not destroy an old unreachable object** (R-rc3-16): back-date an unreachable object
   past two weeks, run the packing path, assert it survives. **This is the test that makes RC-6's grace
   period real**, and it is the one that would have caught rev 2's error.
7a. **The marker distinguishes the two cases** (R-rc3-7b): a repository circuitRF created and one
   created by `git init` beside it are told apart **from the marker alone**, with no inference from the
   presence of `.git`, `.gitignore` or `.gitattributes` — a user may perfectly well have written all
   three. Assert an archive-extracted copy is still recognised, and that a clone is not.
8. **Identity is in NO config file** (R-rc3-1a, §4.4): after a checkpoint, assert the repository's
   config carries no `user.name`/`user.email` **and** that the user's global config is untouched, while
   the commit itself names the right author and committer. **Then assert two identities against one
   workspace** — commit as one, change the supplied identity, commit again, and assert the two commits
   are attributed differently. That is the network-share case, and it is the gate rev 2 would have
   failed.
9. **Hooks and signing** (R-rc3-7a, R-rc3-1a): a repository carrying a `pre-commit` hook that fails is
   committed into anyway, **a `post-commit` hook does not fire** — assert by a hook that writes a
   sentinel file — and a global signing setting neither blocks nor slows a checkpoint.
10. **The environment is applied** (R-rc3-1a): assert `GIT_TERMINAL_PROMPT=0` reaches the subprocess
   and that a call that would prompt **returns a failure rather than blocking** — drive it with a
   fixture remote that demands credentials and assert the call completes within its timeout. A test
   that hangs is the defect this requirement exists to prevent, so give it a bound.
11. **The generated files.** A new workspace's `.gitignore` contains the results patterns, `.cwsuser`,
   `.generated-cells` and the session bookkeeping (R-rc3-10); its `.gitattributes` marks all five
   document types unmergeable **and pins their end-of-line treatment** (R-rc3-12a). Assert the
   `.gitignore` is written by `WorkspaceCreate` and therefore appears in a workspace made headlessly
   by `circuitrf new workspace` (R-rc3-11).
12. **A workspace created BEFORE this feature gets the files when it first gains a repository**
   (R-rc3-11a) — build a fixture workspace with no `.gitignore`, arm it, assert both files appear.
   **This is the gate that catches the gap rev 2 shipped**, and the fixture must be an existing
   workspace, not a fresh one.
13. **circuitRF appends to `.gitignore` and never rewrites it** (R-rc3-11b): a user line added by hand
   survives every path that touches the file.
14. **Round-tripped bytes are stable across platforms** (R-rc3-12a): commit a `.clay`, check it back
   out, and assert byte identity with what `LayoutPersistence` wrote — on each platform CI runs.
15. **Packing is interruptible** (R-rc3-15): cancel mid-pack and assert the repository is still valid
   and the operation can be re-run.
16. **The measurement, not a timing assertion** (R-rc0-8, R-rc0-9). Re-run §2's and §3.2's sequences
   on the target machine class — **including the no-`gc` pass**, which is the one that finds the
   overhang — and report the numbers. Do not add a wall-clock test.
17. **`safe.directory` is answered per invocation, and translated where it cannot be** (R-rc3-1a,
    R-rc3-4): construct a repository the running user does not own, or simulate the condition git
    reports, and assert first that the call **succeeds** with the root named, and second — with the
    per-invocation answer withheld to simulate an old git — that circuitRF's own sentence appears
    rather than git's.
18. **Every gate in this brief needs git, and says what it does without one** (R-rc0-10b). Follow
    `RfCore.Tests`' `FixtureFact`/`FixtureTheory` idiom: **skip with a reason**, never fail. CI has git
    on all three platforms, so nothing is lost. Do not commit or vendor a git binary.
19. **Two writers, no clobber, no advisory lock** (R-rc3-1b): drive two concurrent checkpoints at one
    repository and assert both references land and the shared index is byte-for-byte untouched; drive a
    reference-update collision and assert the retry resolves it with no `Diagnostic` posted; assert a
    **read** issued during a pack does not wait; and source-scan for any use of `WorkspaceLock` on the
    write path, comments stripped — its only permitted caller here is packing's yield.
20. **macOS discovery never invokes the shim** (R-rc3-2a): with the developer-tools check seamed to
    "absent", assert no process named `git` is started and the answer is "not available". Runs on every
    platform through the seam; the real shim is a manual check recorded in the write-up.
21. **Reclaim protects what is newer than the age** (R-rc3-16a): thin three checkpoints at three journal
    times, reclaim with an age between them, and assert the newer ones' commits are still present and
    the older one's is gone. **Then assert that with no age given nothing is reclaimed**, and that
    packing (R-rc3-13) never removes any of them.
22. **`src/Cli` sees the identity the tab wrote** (R-rc3-1c): write the preference through RC-4's own
    type, take a checkpoint from a `Cli` process, and assert the commit is attributed to it — with the
    user's global git identity absent, which is the case that matters.

---

## 7. Documentation

The §10B.1 row this brief makes true: **simulation results (`.npy`, `.spl`, `.lpcwave`) are not kept
in history and are not recoverable — re-run the analysis.** Say **no** plainly; softening it is how a
designer ends up with a false belief.

`docs/design/layout-view.md` §4's gzip reserve is annotated as revoked for a versioned workspace, with
the measurement, so the next person to read the reserve reads the reason it is not taken.

---

## 8. On completion

Findings to `src/Design/RESOLVED.md` — **never to a `CLAUDE.md`**.

**Report, do not silently absorb:**
- The re-measured §2 and §3.2 numbers, and the loose-object overhang on the target machine (the measurement gate).
- Any git failure encountered during development that R-rc3-4's table does not name. That list is the
  most valuable thing this brief produces, because every entry missing from it becomes raw git output
  in front of an RF designer.
- Whether any platform's git needs handling the other two do not.
- **The version floor chosen and the requirement that set it** (R-rc3-3a) — so it can later be lowered
  deliberately rather than guessed at.
- **Whether the packing configuration actually held** — the back-dated unreachable-object gate,
  which is R-rc3-16's whole guarantee. If an unreachable object did not
  survive, say so loudly: RC-6's entire retention design rests on it, and rev 2 of the architecture
  already got this wrong once by measuring the tidied state instead of the configured one.
- **Whether the marker survived every journey it is supposed to and none it is not** (R-rc3-7b) —
  archive yes, clone no. If a git version copies configuration on clone in some path, say so: the whole
  distinction rests on it.
- Whether `ProcessRunner` and this type converged closely enough that a shared primitive is obviously
  right (R-rc3-1). **Report it; do not move a type across the firewall as part of a findings
  write-up.**
- **What the macOS shim actually did on a machine without the tools** (R-rc3-2a) — this is the one
  gate that cannot run in CI, and the write-up is where it is recorded.
- Whether git's retry on a reference-update collision ever exhausted its bound (R-rc3-1b), and how
  often two circuitRF processes actually collided in practice.
- Whether the per-invocation `safe.directory` was honoured on every platform and every git version CI
  runs (R-rc3-1a); if a version ignored it, that version sets the floor.
