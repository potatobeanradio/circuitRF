#!/usr/bin/env bash
# Run the worker against a Windows model library from macOS or Linux.
#
#   ./run.sh --scan      <model-library>
#   ./run.sh --gen-shims <model-library> <out-dir-inside-work>
#
# TEMPORARY MECHANISM. See README.md §"macOS and Linux" — this is expected to be replaced by the
# sandbox circuitRF already ships for tools/senior-worker, so that a user importing a kit does not
# need a container runtime at all.
#
# Docker Desktop is pinned deliberately: a wedged VM under a different manager cost a great deal of
# time once, and the pin makes picking one up by accident impossible.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
img=crf-netlist-worker
ctx=${CRF_DOCKER_CONTEXT:-desktop-linux}

# The library lives outside this repo; mount its directory read-only and refer to it by basename.
lib=${2:?usage: run.sh <verb> <model-library> [args...]}
libdir=$(cd "$(dirname "$lib")" && pwd)
libname=$(basename "$lib")

docker --context "$ctx" image inspect "$img" >/dev/null 2>&1 \
  || docker --context "$ctx" build -q -t "$img" "$here" >/dev/null

verb=$1; shift 2
docker --context "$ctx" run --rm \
  -v "$here":/work -v "$libdir":/kit:ro \
  "$img" bash -c "
    set -e
    /work/build.sh /work/build >/dev/null
    wine /work/build/netlist_worker.exe '$verb' '/kit/$libname' $*
  "
