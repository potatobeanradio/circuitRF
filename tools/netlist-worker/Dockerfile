# amd64 Debian + Wine + mingw-w64: enough to cross-build the worker and run it against a Windows
# model library on a machine that is neither.
#
# TEMPORARY. Requiring Docker in front of importing a kit is not an acceptable end state — see
# README.md §"macOS and Linux". The replacement is the sandbox circuitRF already ships for
# tools/senior-worker (crf-vmhost and the Linux images beside it), so that circuitRF provides the
# isolation rather than demanding a container runtime from the user.
FROM --platform=linux/amd64 debian:bookworm-slim

RUN apt-get update -qq \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
      wine wine64 gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Wine refuses a prefix on a directory it does not own, so it must live under the work mount.
ENV WINEPREFIX=/work/.wineprefix WINEDEBUG=-all WINEDLLOVERRIDES=mscoree=d
WORKDIR /work
