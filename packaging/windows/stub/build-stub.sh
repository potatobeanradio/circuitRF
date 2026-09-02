#!/usr/bin/env bash
# -- Build the circuitRF per-user launcher stub ------------------------------------------------
#
#   build-stub.sh [x64|arm64|x86] [app-name]
#
# Writes build/<app-name>-stub-<arch>.exe. Follows tools/senior-worker/build.sh: zig cc is the
# preferred route because it cross-compiles a Windows PE from any host with one download and no
# daemon, which is what lets this stub be built and checked on a machine that is not Windows.
#
# The Windows-native route is build-stub.ps1, which build-windows.ps1 calls. THE TWO MUST AGREE:
# this script drifted from it once and produced a stub that could not work at all (see the app-name
# and subsystem notes below), which nothing detected because a release is cut on Windows.
set -eu

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
arch="${1:-x64}"
app="${2:-circuitRF}"
out="$here/build"
mkdir -p "$out"

case "$arch" in
    x64)   target=x86_64-windows-gnu  ; machine=8664 ;;
    arm64) target=aarch64-windows-gnu ; machine=aa64 ;;
    x86)   target=x86-windows-gnu     ; machine=014c ;;
    *) echo "unknown architecture '$arch' (expected x64, arm64 or x86)" >&2; exit 2 ;;
esac

ZIG=${CRF_ZIG:-zig}
if ! command -v "$ZIG" >/dev/null 2>&1; then
    echo "zig is not on PATH. Install it (brew install zig / winget install zig.zig), or build on" >&2
    echo "Windows with packaging/windows/stub/build-stub.ps1." >&2
    exit 1
fi

exe="$out/$app-stub-$arch.exe"
rm -f "$exe"

# -Wl,--subsystem,windows and NOT -mwindows. Both are meant to ask for the GUI subsystem so that
# launching the app opens no console window - and with zig cc, -mwindows silently does not. Measured
# by reading the subsystem field back out of the built PE (zig 0.13.0):
#
#     -mwindows                 -> 3 (CONSOLE)   wrong, and no warning
#     -Wl,--subsystem,windows   -> 2 (GUI)       correct
#     both together             -> 3 (CONSOLE)   -mwindows wins and undoes it
#
# -municode: wWinMain is the Unicode entry point; without it the mingw CRT looks for WinMain.
#
# -mcpu=baseline: without it zig resolves the CPU natively whenever the target's architecture and OS
# match the host's, so the same command is a cross build on one machine and a native one on another.
# The stub ships to other people's machines, so it must not be built for whichever CPU cut it - and
# on Windows on ARM the native path is also where zig crashes outright.
#
# THE APP NAME IS A BARE TOKEN and the stub stringifies it itself. Passing it pre-quoted is what this
# script used to do, and after build-stub.ps1 moved to a bare token this one was left behind: the
# name arrived as "circuitRF" INCLUDING the quotes, so the stub went looking for a file literally
# called "circuitRF".exe. See the note in circuitrf-stub.c.
#
# THE ICON. In a per-user install the file at the install root is this stub, not the application, so
# the stub's own PE resources are what Explorer draws, what a shortcut inherits, and what the .wxs
# file associations resolve through. build-stub.ps1 does exactly this; the two must agree.
#
# The .ico is COPIED next to a generated .rc and named without a path. Both halves were measured
# against zig 0.16.0's resource compiler: a path in a .rc resolves relative to THE .rc FILE'S OWN
# DIRECTORY rather than the working directory, and an absolute path is rejected outright.
#
# A MISSING .ico IS NOT AN ERROR - they are build products of tools/IconGen, which the packaging
# scripts run first, and an icon-less stub still launches the application.
rc_arg=""
ico="$here/../../../src/Ui/Assets/${app}Icon.ico"
if [ -f "$ico" ]; then
    cp "$ico" "$out/$app-icon.ico"
    printf '1 ICON "%s-icon.ico"\n' "$app" > "$out/$app-icon.rc"
    rc_arg="$out/$app-icon.rc"
else
    echo "no ${app}Icon.ico in src/Ui/Assets - building without an icon."
    echo "generate it with: dotnet run --project tools/IconGen"
fi

"$ZIG" cc -target "$target" -mcpu=baseline -O2 -municode -Wl,--subsystem,windows \
    "-DCRF_APP_NAME=$app" \
    "$here/circuitrf-stub.c" ${rc_arg:+"$rc_arg"} -o "$exe" -luser32

# Read back what was actually built, exactly as build-stub.ps1 does: never trust a toolchain to have
# done what it was asked. Both fields ship silently wrong otherwise.
read_le16() { od -An -tx1 -j "$1" -N2 "$exe" | tr -d ' \n' | sed 's/\(..\)\(..\)/\2\1/'; }
pe=$(( $(od -An -tu4 -j 60 -N4 "$exe" | tr -d ' ') ))
[ "$(od -An -c -j "$pe" -N2 "$exe" | tr -d ' \n')" = "PE" ] || { echo "not a PE: $exe" >&2; exit 1; }

got_machine=$(read_le16 $(( pe + 4 )))
got_subsys=$(read_le16 $(( pe + 24 + 68 )))
if [ "$got_machine" != "$machine" ]; then
    echo "built the wrong architecture: machine 0x$got_machine, expected 0x$machine for $arch" >&2
    rm -f "$exe"; exit 1
fi
if [ "$got_subsys" != "0002" ]; then
    echo "subsystem is 0x$got_subsys, not 2 (GUI): a console window would open on every launch" >&2
    rm -f "$exe"; exit 1
fi

# The icon, read back out of the PE for the same reason the two fields above are: a resource
# compiler that quietly did nothing exits 0. RT_ICON (3) holds the images and RT_GROUP_ICON (14) is
# the directory the shell asks for; one without the other draws nothing. A WARNING, NOT A FAILURE -
# the stub launches the application either way.
icon_note=""
if [ -n "$rc_arg" ]; then
    nsec=$(( 16#$(read_le16 $(( pe + 6 ))) ))
    optsz=$(( 16#$(read_le16 $(( pe + 20 ))) ))
    sec=$(( pe + 24 + optsz ))
    types=""
    i=0
    while [ "$i" -lt "$nsec" ]; do
        o=$(( sec + i * 40 ))
        if [ "$(od -An -c -j "$o" -N5 "$exe" | tr -d ' \n')" = ".rsrc" ]; then
            root=$(( $(od -An -tu4 -j $(( o + 20 )) -N4 "$exe" | tr -d ' ') ))
            n=$(( 16#$(read_le16 $(( root + 12 ))) + 16#$(read_le16 $(( root + 14 ))) ))
            e=0
            while [ "$e" -lt "$n" ]; do
                id=$(( $(od -An -tu4 -j $(( root + 16 + e * 8 )) -N4 "$exe" | tr -d ' ') & 2147483647 ))
                types="$types $id"
                e=$(( e + 1 ))
            done
        fi
        i=$(( i + 1 ))
    done
    case " $types " in
        *" 3 "*) case " $types " in *" 14 "*) icon_note=" (with icon)" ;; esac ;;
    esac
    if [ -z "$icon_note" ]; then
        echo "WARNING: the icon is NOT in this stub's resources; it will draw the generic one." >&2
    fi
fi

echo "built $exe$icon_note"
