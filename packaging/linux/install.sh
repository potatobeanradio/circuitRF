#!/bin/bash
set -euo pipefail

# ── circuitRF user-local installer ────────────────────────────────────────────
#
#   ./install.sh            install (or upgrade) into ~/.local
#   ./install.sh --uninstall [--yes]
#
# No root, no package manager, no system directory. This is the Linux channel that can update
# itself, because everything it writes is inside $HOME (docs/design/auto-update.md §1).
#
#     ~/.local/share/circuitRF/current -> app-<version>    the launch path; a symlink, flipped by rename(2)
#     ~/.local/share/circuitRF/app-<version>/              the application
#     ~/.local/share/circuitRF/staging/                    the updater's own scratch space
#     ~/.local/bin/circuitrf                               on PATH
#     ~/.local/share/applications/circuitrf.desktop        the menu entry and file associations
#
# Both the launcher and the .desktop entry point at the STABLE `current/` path, never at a versioned
# directory, so an update re-registers nothing and the desktop database is written once, here.

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
BIN_DIR="$HOME/.local/bin"
ROOT="${DATA_HOME}/circuitRF"
APPS="${DATA_HOME}/applications"
ICONS="${DATA_HOME}/icons/hicolor/512x512/apps"
MIME="${DATA_HOME}/mime"

# ── uninstall ─────────────────────────────────────────────────────────────────
#
#   ./install.sh --uninstall [--yes]
#
# Also installed as ${ROOT}/install.sh, which is the copy circuitRF's own "Uninstall circuitRF..."
# runs (with --yes, after its own warning and after it has removed the solvers itself).
#
# ROOT IS ALSO circuitRF's PER-USER FOLDER. On Linux the application keeps its preferences, recovery
# files and the 3D solvers the install assistant built under ${XDG_DATA_HOME:-~/.local/share}/circuitRF
# - the same directory. So this removes ONLY what this script and the updater laid down (app-*,
# current, staging, this script) and never the directory wholesale: an `rm -rf "$ROOT"` here deleted
# every preference while printing that it had left them alone (brief-em3d-25).
#
# The 3D solvers circuitRF installed go too, with a warning (docs/design/em-3d.md §7.2): the CLI prints
# what it would remove and its size, and nothing is removed unless the user says yes. The removal is
# the CLI's own `solver remove --all`, the function the application calls - never a path this script
# works out for itself.

if [ "${1:-}" = "--uninstall" ]; then
    YES=""
    [ "${2:-}" = "--yes" ] && YES="yes"
    APP="${ROOT}/current/circuitRF"

    # Asked, not guessed: without --yes the CLI removes nothing and says (by code, in --json) whether
    # there is anything to remove. Captured rather than piped, since the refusal exits 1 and pipefail
    # would read that as "no".
    PLAN=""
    [ -x "$APP" ] && PLAN="$("$APP" solver remove --all --json 2>/dev/null || true)"
    if printf '%s' "$PLAN" | grep -q "solver.remove.consent-required"; then
        echo "Uninstalling circuitRF also removes the 3D solvers it installed for you. Reinstalling"
        echo "circuitRF later means reinstalling them too. Solvers other accounts installed stay theirs."
        echo ""
        "$APP" solver remove --all 2>&1 | sed '$d' || true
        if [ -z "$YES" ]; then
            ANSWER=""
            read -r -p "Uninstall circuitRF and remove these solvers? [y/N] " ANSWER || ANSWER=""
            case "$ANSWER" in
                y|Y|yes|YES) ;;
                *) echo "Nothing was removed."; exit 1 ;;
            esac
        fi
        "$APP" solver remove --all --yes || {
            echo "circuitRF was not uninstalled, because its solvers could not be removed (above)." >&2
            exit 1
        }
    fi

    for d in "${ROOT}"/app-*; do
        [ -e "$d" ] && rm -rf "$d"
    done
    rm -rf "${ROOT}/staging" "${ROOT}/current.tmp"
    rm -f  "${ROOT}/current" "${ROOT}/install.sh"
    rmdir  "$ROOT" 2>/dev/null || true
    rm -f  "${BIN_DIR}/circuitrf"
    rm -f  "${APPS}/circuitrf.desktop"
    rm -f  "${ICONS}/circuitrf.png"
    rm -f  "${MIME}/packages/circuitrf-mime.xml"
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" || true
    command -v update-mime-database    >/dev/null 2>&1 && update-mime-database "$MIME"    || true
    echo "circuitRF removed from ~/.local. Your workspaces and preferences were left alone."
    exit 0
fi

# ── install ───────────────────────────────────────────────────────────────────

VERSION_DIR="$(cat "${HERE}/current")"          # e.g. app-1.0.0-beta.1, written by build-linux.sh

# This string comes out of the ARCHIVE and is about to be interpolated into an `rm -rf`. A `current`
# holding `../..` would delete ~/.local, and the person who ran this script would have no idea why.
# So it is checked against the one shape build-linux.sh ever writes, before it is used for
# anything (security review, 2026-08-25). Same rule the updater applies to a release tag and the
# Windows stub applies to the same file.
case "$VERSION_DIR" in
    app-*) ;;
    *) echo "This archive's 'current' does not name a version directory. Download it again." >&2
       exit 1 ;;
esac
if printf '%s' "$VERSION_DIR" | LC_ALL=C grep -q '[^A-Za-z0-9.+_-]'; then
    echo "This archive's 'current' is not a version directory name. Download it again." >&2
    exit 1
fi

[ -d "${HERE}/${VERSION_DIR}" ] || {
    echo "This archive does not contain ${VERSION_DIR}. It is incomplete; download it again." >&2
    exit 1
}

mkdir -p "$ROOT" "$BIN_DIR" "$APPS" "$ICONS" "${MIME}/packages" "${ROOT}/staging"

# The uninstaller travels with the install: circuitRF's "Uninstall circuitRF..." runs this copy, since
# the archive it came from is long gone by then.
if [ "${HERE}" != "${ROOT}" ]; then
    cp "${HERE}/install.sh" "${ROOT}/install.sh.tmp"
    chmod +x "${ROOT}/install.sh.tmp"
    mv -f "${ROOT}/install.sh.tmp" "${ROOT}/install.sh"
fi

echo "Installing ${VERSION_DIR} into ${ROOT} ..."
rm -rf "${ROOT}/${VERSION_DIR}.partial"
cp -a "${HERE}/${VERSION_DIR}" "${ROOT}/${VERSION_DIR}.partial"
chmod +x "${ROOT}/${VERSION_DIR}.partial/circuitRF"

# Nothing incomplete ever holds a real name — the same discipline the updater follows, so that an
# interrupted install leaves debris the next launch reclaims rather than a half-tree that looks
# installed. See design §13.2 rule 4.
rm -rf "${ROOT}/${VERSION_DIR}"
mv "${ROOT}/${VERSION_DIR}.partial" "${ROOT}/${VERSION_DIR}"

# `current` is re-pointed by symlink-then-rename, NEVER by rm-then-ln: the naive form has a window
# in which the application has no launch path at all, and a failure inside it leaves the install
# unusable. rename(2) over an existing symlink is atomic.
ln -sfn "$VERSION_DIR" "${ROOT}/current.tmp"
mv -Tf "${ROOT}/current.tmp" "${ROOT}/current"

# The launcher and the menu entry point at `current/`, which is why an update re-registers nothing.
cat > "${BIN_DIR}/circuitrf" <<LAUNCH
#!/bin/sh
# Generated by circuitRF's install.sh. Points at the STABLE current/ path, never at a version.
exec "${ROOT}/current/circuitRF" "\$@"
LAUNCH
chmod +x "${BIN_DIR}/circuitrf"

cp "${HERE}/circuitrf.png" "${ICONS}/circuitrf.png"
cp "${HERE}/circuitrf-mime.xml" "${MIME}/packages/circuitrf-mime.xml"

sed "s|^Exec=.*|Exec=${ROOT}/current/circuitRF %F|" \
    > "${APPS}/circuitrf.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Version=1.0
Name=circuitRF
GenericName=RF Circuit Simulator
Comment=RF circuit simulation - DC, S-parameters, harmonic balance, loadpull, layout and 2.5D EM
Exec=PLACEHOLDER %F
Icon=circuitrf
Terminal=false
MimeType=application/x-circuitrf-workspace;application/x-circuitrf-harmonica;application/x-circuitrf-wbond;application/x-circuitrf-schematic;application/x-circuitrf-layout;application/x-circuitrf-symbol;application/x-circuitrf-datadisplay;application/x-circuitrf-technology;application/x-circuitrf-emsetup;
Categories=Science;Engineering;Electronics;
Keywords=RF;microwave;simulation;S-parameters;harmonic balance;loadpull;
StartupNotify=true
DESKTOP

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" || true
command -v update-mime-database    >/dev/null 2>&1 && update-mime-database "$MIME"    || true
command -v gtk-update-icon-cache   >/dev/null 2>&1 && \
    gtk-update-icon-cache -f -t "${DATA_HOME}/icons/hicolor" >/dev/null 2>&1 || true

echo ""
echo "OK  circuitRF ${VERSION_DIR#app-} is installed."
echo "    Run it with:  circuitrf"

case ":${PATH}:" in
    *":${BIN_DIR}:"*) ;;
    *) echo ""
       echo "    NOTE: ${BIN_DIR} is not on your PATH. Add this to your shell profile:"
       echo "          export PATH=\"\$HOME/.local/bin:\$PATH\"" ;;
esac

echo ""
echo "    This install updates itself in the background; Settings has a checkbox to turn that off."
