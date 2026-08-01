#!/bin/sh
# Container side of verify-windows.sh. Sets the stage, then hands over to verify-windows-drive.py.
# Expects the tools/ directory mounted at /w. Not meant to be run directly on a host.
set -eu

apt-get update -qq >/dev/null 2>&1
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    wine64 python3 gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 >/dev/null 2>&1

# ── a SECOND fake model, importing from a different host module name ─────────
# So the "two derived names coexist" check has two genuinely different names to work with.
mkdir -p /tmp/fm2
cd /tmp/fm2
sed 's/crf_test_host\.dll/crf_other_host.dll/g; s/^LIBRARY.*/LIBRARY crf_other_host.dll/' \
    /w/fake-model-lib/crf_test_host.def > crf_other_host.def
x86_64-w64-mingw32-dlltool -d crf_other_host.def -D crf_other_host.dll -l libcrf_other_host.a
x86_64-w64-mingw32-gcc -O1 -std=gnu11 -shared /w/fake-model-lib/fake_model.c \
    -o fake_model2.dll -L. -lcrf_other_host

# ── the control build: the two _setmode calls neutralised, nothing else ──────
# Without this, "the doubles arrived intact" proves nothing -- it could just be that stdio never
# translated anything here.
mkdir -p /tmp/nosm
cd /tmp/nosm
sed 's/^\( *\)_setmode(/\1(void)(/' /w/senior-worker/senior_worker.c > senior_worker.c
grep -q '(void)(_fileno(stdout)' senior_worker.c || {
    echo "verify-windows: the _setmode control could not be built -- did the call site move?"; exit 1; }
x86_64-w64-mingw32-gcc -O2 -std=gnu11 -DCRF_HOST_DLL -shared \
    senior_worker.c /w/senior-worker/crf-model-host.def -o crf-model-host-nosetmode.dll

# ── an unprivileged user, and a READ-ONLY install directory ──────────────────
# A kit is read-only and an install may sit under Program Files, so neither is a legal place to
# stage into. Root ignores mode bits, so this has to run as somebody else to mean anything.
useradd -m crf
mkdir -p /opt/crf
cp /w/senior-worker/build/senior_worker.exe /w/senior-worker/build/crf-model-host.dll /opt/crf/
cp /w/fake-model-lib/build/fake_model.dll /tmp/fm2/fake_model2.dll /home/crf/
cp /w/senior-worker/verify-windows-drive.py /home/crf/
chown -R root:root /opt/crf
chmod -R 555 /opt/crf
chown -R crf:crf /home/crf

if su crf -c "touch /opt/crf/probe" 2>/dev/null; then
    echo "verify-windows: the install directory is writable -- the read-only check would be vacuous."
    exit 1
fi

su crf -c "WINEDEBUG=-all WINEPREFIX=/home/crf/wp HOME=/home/crf /usr/lib/wine/wine64 wineboot -u" \
    >/dev/null 2>&1 || true
sleep 2

exec su crf -c "python3 /home/crf/verify-windows-drive.py"
