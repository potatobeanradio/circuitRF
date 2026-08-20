#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Fails if the committed user documentation is not what the generator produces.
#
# Generated output must be regenerable AND checked, or it drifts the other way:
# somebody hand-edits a generated page, the next run silently reverts it, and
# the edit is lost with nothing reported. Regenerating and diffing turns that
# into a build failure the first time it happens.
#
#   tools/DocGen/check-docs-current.sh
#
# There is no CI workflow in this repository yet; when one is added, this is the
# step to add to it. It is a plain script so it can also be run by hand before
# committing a UI change that moves a figure.
# ---------------------------------------------------------------------------
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

echo "Regenerating docs/user from the live application..."
dotnet run --project tools/DocGen -- --out docs/user

if ! git diff --exit-code -- docs/user; then
  echo
  echo "docs/user is out of date with the generator." >&2
  echo "Either commit the regenerated output above, or — if a generated page was" >&2
  echo "hand-edited — move the change into its Markdown source under docs/user/src/." >&2
  exit 1
fi

echo "docs/user is current."
