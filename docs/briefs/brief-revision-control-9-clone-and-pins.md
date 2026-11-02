# Sonnet Brief — RC-9: clone-as-workspace, fetch/push, and commit-pinned references

**Read `brief-revision-control-0-overview.md` first.** The architecture is
`docs/design/revision-control.md` §7, §7A.4, §7A.5, §9, §9.1 and §5.2a. **Depends on every earlier brief**, and is
deliberately placed last: it is worthless until sharing exists, and it must not be built on a
foundation that is still moving.

**Scope: making a design's dependence on someone else's work reproducible.** This is what turns RC-2's
read-only libraries from a restriction into a managed collection.

---

## 1. Clone as a workspace

**R-rc9-1. If a cloned directory contains a `.cws`, circuitRF opens it as a workspace.** Once git is
already being driven as a subprocess this is nearly free, and it is the strongest *product* argument in
the architecture: a public library of reference designs, opened in one action, from any git host — with
fetch and push for those who want them, and clone-and-read for a manager who only wants to look.

**R-rc9-2. It is also the mechanism RC-2's refusal points at.** The way to edit a library is to open it
as a workspace of its own; on a shared library that is a clone plus a push, or a request to the
librarian. **Either way it is a deliberate act with a record, which is what was missing.**

**R-rc9-3. It depends on one property the workspace model must guarantee: references inside a workspace
are relative and portable.** The workspace-archive work already established this and already found the
trap — a `.clay`/`.csym` bitmap reference that never resolved relatively until it was made to. Cloning
exercises exactly the same property, so **verify it rather than assume the archive work covered every
case**, and report any reference type that does not survive a clone.

**R-rc9-4. Nothing here is host-specific.** The design binds to git, not to any hosting service. No
provider API, no account, no host-specific URL parsing beyond what git itself accepts.

**R-rc9-5. A clone arrives with no `.cwsuser`**, which RC-1 R-rc1-4 already requires to be the normal
state of a shared workspace and by far the most common way one is ever opened by someone other than its
author. If a clone produces any message about the missing sidecar, RC-1's gate was wrong and this is
where it shows.

**R-rc9-5a. A clone also arrives with NO RESTORE POINTS, and that is correct** (§5.2a). Git's default
fetch takes branches and tags and nothing under a private namespace, so RC-5's per-checkpoint
references do not travel. **Do not "fix" this by widening the refspec.** The safety net is a property of
one machine and one designer's sessions — ordered by a monotonic sequence that means nothing anywhere
else (RC-5 R-rc5-8) and thinned by that machine's retention preference (RC-6). What clones is the
narrative; the recipient starts a safety net of their own on their first boundary.

**It is nonetheless a §10B.1 row rather than an unstated fact**, and this brief owns it: *restore
points, in a clone — not kept, not recoverable; the narrative history clones, the safety net does not.*
The three journeys disagree deliberately (an archive carries checkpoints, a clone does not, a Save As
copy carries no history at all), and a designer who is not told will assume the strongest of the three.

**R-rc9-5b. A clone onto a share, or from a foreign-owned tree, hits `safe.directory`** — RC-3's
translated failure (R-rc3-4), never git's wording. RF workspaces live on shares and §7A's librarian
scenario assumes one, so this is an ordinary path rather than an edge.

**R-rc9-5c. A clone arrives with no management marker, and therefore reaches the ordinary arming path**
(R-rc3-7b, R-rc0-13). Git does not clone a repository's config, so RC-3's marker does not travel — which
is correct rather than a gap: a clone is a new workspace on a new machine, and it reaches R-rc5-4a's
first-boundary rule on its own terms, announced once (R-rc5-4b). **Do not carry the marker across by
hand**; a marker that travelled would make one designer's management decision everybody's, which is
the reason it is config rather than a reference. **One thing does travel, and rev 4 said otherwise**
(corrected in rev 5, RC-6 R-rc6-14c): the `.cws` is versioned, so a workspace its owner switched **off**
clones as off, and the recipient's preference does not override that — the flag says *not for this
one* about the workspace, and the workspace is what travelled. The recipient sees RC-6's indicator at
their first boundary, with the setting one click away.

---

## 2. Fetch and push

**R-rc9-6. Fetch and push are explicit user actions, never automatic.** Nothing in this series
contacts a network without being asked. An automatic fetch would silently change what a design resolves
against, which is the failure §7A.4 is written to prevent.

**R-rc9-7. A non-fast-forward push is one of RC-3's translated failures** and says that the other side
moved, in circuitRF's vocabulary. The remedy offered is whole-file, pick a side (RC-7 R-rc7-13) — never
a merge.

**R-rc9-7a. circuitRF has no credentials, asks for none, and never hangs waiting for them** (§9.1).
Fetch, push and clone against a private repository need authentication; circuitRF supplies **whatever
git is already configured to supply** — the user's credential helper, their SSH agent, the setup their
IT department gave them — and adds nothing of its own. No prompt, no stored token, no keychain entry.
It has no business holding a credential for a service it does not integrate with, and a stored
credential is a liability whose only justification would be a convenience this brief does not need.

**What makes that safe rather than merely small is RC-3 R-rc3-1a's `GIT_TERMINAL_PROMPT=0`**: an
operation that would have asked **refuses** instead, with a sentence naming what the remote wanted.
Without it the same minimalism produces a subprocess blocked on an invisible prompt — no message, no
exit code, and a workspace close that never completes. **A hang is the worst failure mode available**,
and it is the one this brief is most likely to produce by accident.

**And that variable reaches git's own prompts and nothing else** (§9.1, rev 5). An SSH key with a
passphrase and no agent prompts through `ssh`, which does not read it; with no terminal `ssh` fails
rather than hangs, which is the wanted outcome — but only because RC-3's type gives the subprocess no
terminal. A credential helper that opens its own window, the ordinary Windows case, is the user's
configured setup and may open it: that is the helper doing what the user installed it to do, not a
hang, and §9's posture is to let it. What this brief must never do is supply a terminal, an
`SSH_ASKPASS`, or a stored answer of its own. **And the bound on every network call is inactivity, not
wall-clock** (R-rc3-1a): a slow clone of a large library is not a hang, and a fixed limit would report
it as one.

**R-rc9-7b. Reaching a network is the question Security & Permissions answers** (RC-4 R-rc4-2a). The
updater is on that tab for exactly this reason. Whether git's network operations come under the same
consent is a question for that tab, not this brief — **read RC-4's answer and follow it**; do not build
a second consent model for one program.

---

## 3. The pin

Under version control, an unversioned reference is a hazard: *"this design uses the amplifier cell from
that workspace"* resolves to **whatever that workspace contains today**, which is not reproducible and
not what was simulated.

**R-rc9-8. A reference carries a commit identity** — pinning the exact version of the referenced content
this design was built and verified against, with an explicit, visible action to move to a newer one.

**R-rc9-9. The identity belongs on the ALIAS, not on each cell.** `CwsWorkspaceRef`
(`src/Design/Workspace/WorkspacePersistence.cs:228`) is already the one place a cross-workspace path is
written down exactly once — its own header says so — and pinning there pins every `ws://alias/…`
through it, consistently. **One referenced workspace is one repository with one commit identity.**
Pinning per cell would allow one design to reference two mutually inconsistent versions of one library:
a state nobody wants and nothing detects.

The field sits beside RC-2's editability field, on the same record, for the same reason.

**R-rc9-10. This is the same construct as the configuration object in a centralised design-management
architecture** (RC-7 R-rc7-14), and it is **the only feature in this series that changes what a
simulation result means**: a result becomes reproducible in the strong sense, from a commit identity,
rather than from "the files as they were".

---

## 4. What the pin buys, which is decoupling

**R-rc9-11. The librarian publishes whenever they like, and nothing in any designer's workspace
changes.** A library that silently updates under a design that was signed off is precisely the
IP-integrity failure §1.4 is written against.

**R-rc9-12. "A newer version is available" is a visible, per-reference state with an explicit action to
take it** — not an ambient event, and not a prompt.

**R-rc9-13. Moving to a newer version is itself a change to the local `.cws`**, so it lands in the local
history with a date and an author. The designer can later see **when their design started using the new
library**, which is usually the question being asked when something stopped working.

**R-rc9-14. A checkpoint captures the pin along with everything else**, so restoring to last Tuesday
restores which library version last Tuesday's design resolved against. **Without the pin a restore is
only partial and does not say so** — the designer gets their files back and silently keeps today's
library. That is the strongest argument for this brief in the whole architecture, and it means RC-5's
R-rc5-14 caveat can finally be retired.

**R-rc9-15. The pin surprises someone, deliberately, and that must be explained rather than
discovered.** With an unpinned reference, editing a cell in the library and returning to the design
shows the new cell. **With a pinned one it deliberately does not** — the pin says "the version this
design was verified against", and silently moving it would defeat the entire feature. The design shows
that a newer version is available instead. *"I just edited it and my design didn't change"* is a thing
to explain.

**R-rc9-16. A pin is not a copy.** The referenced content still lives in the other workspace; the pin
records an identity, not bytes. **If that workspace is unreachable or has had its history rewritten,
the pin cannot be honoured — and that is reported plainly rather than falling back to "whatever is
there now"**, which would defeat the entire purpose. A designer who needs the content to travel with
the design has the workspace archive, which is a different tool for a different problem.

---

## 5. What this is not

**R-rc9-17. Not a package manager.** No version ranges, no resolution, no transitive constraint
solving. **A pin is an exact identity and moving it is a human decision.**

**R-rc9-18. Not a lock server and not a permission system.** RC-2's read-only is circuitRF declining to
write, not the filesystem declining. The goal is to make the accidental case impossible and the
deliberate case visible, which is the whole of what is achievable without a server.

**R-rc9-20. Clone, pin, fetch and push have a headless spelling on RC-5's `history` verb** (§5.3d,
R-rc5-23), or on nouns of their own if the owner prefers — each calling the `src/Design` function the
GUI calls, because a build machine reproducing a signed-off result is the reason the pin exists at all.
Gate 14 is what holds it. Adding nouns to the repo-root `CLAUDE.md` is the owner's edit; flag it.

**R-rc9-19. Not nested repositories.** A repository *inside* a workspace — a library cloned into it —
is left alone entirely: not committed to, and not committed *as* anything by the enclosing workspace.
It is reported as an excluded subtree, exactly as RC-6 reports the enclosing case.

---

## 6. Gates

1. **A cloned directory containing a `.cws` opens as a workspace**, and every relative reference in it
   resolves — including a bitmap reference from a `.clay` and a `.csym`, which is the case the archive
   work found broken (R-rc9-3).
2. **A clone posts no message about the absent `.cwsuser`** (R-rc9-5).
3. **The pin is on the alias, and one alias yields one identity** (R-rc9-9): assert that two cells
   reached through one alias cannot resolve to different versions.
4. **An absent pin is unpinned, not an error** — the existing behaviour, for every `.cws` written before
   this field existed. (Note this is the *opposite* default from RC-2's field on the same record, and
   the two headers must each say why.)
5. **A pinned reference does not move when the library changes** (R-rc9-15), and the design reports that
   a newer version is available.
6. **Taking the newer version writes the `.cws`** and therefore lands in the local history (R-rc9-13).
7. **A checkpoint captures the pin** and a restore restores it (R-rc9-14) — assert by restoring a
   workspace whose library moved in between, and checking which version it resolves against.
8. **An unhonourable pin is reported, never fallen back from** (R-rc9-16): make the referenced workspace
   unreachable, and assert a refusal rather than a silent resolution to current content.
9. **A nested repository is excluded and reported**, and the enclosing workspace's own history is
   otherwise normal (R-rc9-19).
10. **No network access without an explicit action** (R-rc9-6): source-scan the open and checkpoint
    paths for a fetch, and fail on one.
10a. **A clone reaches the arming path** (R-rc9-5c): clone, open, and assert the marker is absent and
    the recipient's own preference governs — not the sender's.
11. **A clone carries no restore points, and the refspec was not widened** (R-rc9-5a): assert the
    clone has none, and source-scan for a refspec reaching circuitRF's checkpoint namespace.
12. **An operation needing credentials refuses within its timeout rather than blocking**
    (R-rc9-7a) — drive it against a fixture remote that demands them, **and separately against an SSH
    remote with a passphrase-protected key and no agent**, which `GIT_TERMINAL_PROMPT` does not cover.
    **Give both tests a bound**: a test that hangs is the exact defect being gated. And assert a slow but
    live transfer is **not** cut off by the inactivity bound — drive a fixture remote that trickles.
13. **`safe.directory` on a cloned or share-hosted tree produces circuitRF's sentence** (R-rc9-5b).
14. **Headless** (R-rc9-20). Clone, pin and pin-resolution work from `src/Cli` with no display, byte
    for byte against the GUI path, since a build machine reproducing a signed-off result is the reason
    the pin exists at all — and a comment-stripped source scan proves the view model kept no second copy.
15. **A clone of a switched-off workspace arrives off** (R-rc9-5c): clone one, open it with the
    preference on, and assert no checkpoint is taken and RC-6's indicator shows.

---

## 7. Documentation (§10B.2 scenario 5)

*"I use a library another team maintains."* Read-only, the pin, what *"a newer version is available"*
means, and **what happens to the pin when they restore an old state** — which is the part that makes
a restore complete and is worth stating as such.

It pairs with RC-2's scenario 10 (*"I need to fix a cell in a library I reference"*), and the two
should cross-reference: one is the consumer's view, the other the fixer's.

`docs/design/workspace-and-project-tree.md` §5B/§5C gains the pin beside RC-2's read-only default.

---

## 8. On completion

Findings to `src/Design/RESOLVED.md`, `src/Ui/RESOLVED.md` and `src/Cli/RESOLVED.md` — **never to a
`CLAUDE.md`**.

**Report, do not silently absorb:**
- Any reference type that does not survive a clone (R-rc9-3). That is a portability defect in the
  workspace model and is worth reporting whether or not this brief fixes it.
- Whether RC-5's R-rc5-14 caveat can now be retired, in full or in part (R-rc9-14).
- **Whether anyone was tempted to widen the clone refspec** (R-rc9-5a). If the absence of restore
  points in a clone read as a bug during development, that is a documentation finding — the row exists
  precisely because the behaviour is correct and looks wrong.
- **What the credential story actually looked like on each platform** (R-rc9-7a), and whether any path
  could still block on a prompt.
- What a pin costs to verify on open, on a workspace with several referenced libraries — measured, not
  asserted in a test.
