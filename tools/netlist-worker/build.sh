#!/usr/bin/env bash
# Cross-build the two products from the one source. Run inside the container (see run.sh), or on a
# machine with mingw-w64 installed.
#
# The host SHIM is not built here: it cannot be, because its exports are the user's kit's own symbol
# names and are not known until --gen-shims has read them. run.sh builds it per kit, per run.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here/build}
CC=${CC:-x86_64-w64-mingw32-gcc}

mkdir -p "$out"
$CC -O2 -Wall -Wextra -DCRF_DRIVER -o "$out/netlist_worker.exe" "$here/netlist_worker.c"

# The test library, so the worker can be exercised with no kit present. Same reason
# tools/fake-model-lib exists for tools/senior-worker.
$CC -O2 -Wall -Wextra -shared -o "$out/crf_testlib.dll" "$here/testlib.c"

echo "built $out/netlist_worker.exe and $out/crf_testlib.dll"
