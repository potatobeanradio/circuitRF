#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Fails if the committed user documentation is not what the generator produces.
#
# Generated output must be regenerable AND checked, or it drifts both ways:
# somebody hand-edits a generated page and the next run silently reverts it, or
# a UI change moves a figure nobody thought to regenerate and the page beside it
# goes on showing last month's screenshot. Regenerating and diffing turns either
# one into a failure the first time it happens.
#
#   tools/DocGen/check-docs-current.sh
#
# Run by .github/workflows/docs-current.yml, and by hand before committing a UI
# change that moves a figure.
#
# WHY A CLEAN RUN MUST BE A ZERO-FILE DIFF, and why "classify the churn and put
# the rest back" is not an acceptable substitute: a figure reverted because its
# diff looked unfamiliar is a figure that is now genuinely stale, so it comes
# back on EVERY later run. That is how a one-file change came to print a
# hundred-file diff. Measured 2026-09-21: 41 of the 54 files a no-change run
# touched were not drift at all — they were what the code draws, put back by an
# earlier classify-and-revert. See src/Ui/Diagnostics/CLAUDE.md.
# ---------------------------------------------------------------------------
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

if ! git diff --quiet || [ -n "$(git status --porcelain)" ]; then
  echo "Working tree is not clean; this check regenerates into it. Commit or stash first." >&2
  exit 1
fi

echo "Regenerating docs/user from the live application..."
dotnet run -c Release --project tools/DocGen -- --out docs/user

status=0

# 1. The generator must write nothing outside docs/. It opens example workspaces to
#    draw them, and a save on the way out rewrites examples/**/*.csch under somebody
#    else's edit (seen 2026-09-15).
outside="$(git status --porcelain -- . ':(exclude)docs/user' || true)"
if [ -n "$outside" ]; then
  echo >&2
  echo "The docs run wrote OUTSIDE docs/user:" >&2
  echo "$outside" >&2
  status=1
fi

# 2. docs/user must be byte-identical. --exit-code covers tracked files; a new figure
#    is untracked and would otherwise pass silently.
if ! git diff --exit-code -- docs/user; then status=1; fi

untracked="$(git ls-files --others --exclude-standard -- docs/user)"
if [ -n "$untracked" ]; then
  echo >&2
  echo "The docs run produced files that are not committed:" >&2
  echo "$untracked" >&2
  status=1
fi

if [ "$status" -ne 0 ]; then
  echo >&2
  echo "docs/user is not what the generator produces." >&2
  echo "Commit the regenerated output. Do NOT revert files whose diff looks" >&2
  echo "unfamiliar: a reverted figure is a stale figure, and it comes back on" >&2
  echo "every later run. If a figure differs between two consecutive runs it is" >&2
  echo "nondeterministic and that is a bug in the fixture, not something to hide." >&2
  exit 1
fi

echo "docs/user is current."
