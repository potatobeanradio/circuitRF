#!/usr/bin/env bash
#
# Sign a release: build update-manifest.json from dist/, sign it, and say plainly whether users
# will receive this release as an automatic update.
#
# Run this ONCE, after all three platform scripts have run and all fifteen artifacts are together
# in dist/. It cannot live inside those scripts: a release carries exactly one manifest covering
# every asset, and the three scripts run on three different machines that each see only their own
# share. Running it per-platform would produce three partial manifests that cannot be combined -
# and would put the private key on three machines instead of one.
#
#   ./packaging/sign-release.sh                      prompt for the key
#   CRF_RELEASE_KEY=~/keys/x.pem ./packaging/...     take it from the environment (CI, scripts)
#   CRF_RELEASE_KEY=skip ./packaging/...             build no manifest; just report the consequence
#
# Other knobs: CRF_PUBLISH=1 uploads without asking and 0 never uploads; CRF_RELEASE_BASE_URL when
# the tag is not the VERSION string; CRF_RELEASE_TITLE for the release title.
#
# It NEVER fails for a key problem. A missing, wrong or declined key leaves dist/ exactly as it
# found it and prints what that means, because being told "your users will not get this" is the
# whole point and an exit code nobody reads is not.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${ROOT}/packaging/version.sh"
source "${ROOT}/packaging/signing-status.sh"

DIST="${ROOT}/dist"
KEYS_CS="${ROOT}/src/Ui/Updates/ReleaseKeys.cs"
MANIFEST="${DIST}/update-manifest.json"
SIGNATURE="${MANIFEST}.sig"
EXPECTED_FILES=15

BASE_URL="${CRF_RELEASE_BASE_URL:-https://github.com/potatobeanradio/circuitRF/releases/download/${CRF_VERSION}}"

# --- the channel -------------------------------------------------------------
#
# A version with a SemVer prerelease suffix is published as a GitHub prerelease, and that flag is the
# WHOLE channel mechanism: UpdateSelector filters on `includeBetas || !r.IsPreRelease` and on nothing
# else. Getting it wrong is silent in both directions - a beta published as a stable release is pushed
# to every user who never asked for beta code, and a stable release published as a prerelease is
# offered to nobody but the testers. It is derived from VERSION rather than asked for, because the one
# thing that decides it is already written there.
case "$CRF_VERSION" in
    *-*) IS_PRERELEASE=1 ;;
    *)   IS_PRERELEASE=0 ;;
esac

if [ "$IS_PRERELEASE" -eq 1 ]; then
    TITLE="${CRF_RELEASE_TITLE:-${CRF_VERSION} Public Beta}"
else
    TITLE="${CRF_RELEASE_TITLE:-${CRF_VERSION}}"
fi

echo "=== Signing circuitRF ${CRF_VERSION} ==="
echo ""

[ -d "$DIST" ] || { echo "No ${DIST}. Build the artifacts first (BUILDING.md step 3)."; exit 1; }

# A previous run's output must not become this run's input: the manifest lists every file in dist/,
# so a stale signature left lying there would be hashed into the new manifest as though it were an
# artifact. ReleaseSigner skips the two by name, but not a *.rejected from an earlier failure.
rm -f "$MANIFEST" "$SIGNATURE" "${MANIFEST}.rejected" "${SIGNATURE}.rejected"

# .DS_Store is not an artifact and would be advertised to every client as one. Silently dropped
# rather than warned about: on macOS it reappears whenever anyone opens the folder in Finder.
rm -f "${DIST}/.DS_Store"

COUNT="$(find "$DIST" -maxdepth 1 -type f ! -name '.*' | wc -l | tr -d ' ')"
echo "dist/ holds ${COUNT} artifact(s)."
if [ "$COUNT" -ne "$EXPECTED_FILES" ]; then
    echo ""
    echo "WARNING: a complete release is ${EXPECTED_FILES} files, from all three platforms."
    echo "         Signing ${COUNT} means the missing ones are not in the manifest, and a client on"
    echo "         a platform whose asset is absent is offered nothing - silently. Continuing."
fi

PUB="$(crf_release_public_key "$KEYS_CS")"
if [ -z "$PUB" ]; then
    echo ""
    echo "This build carries NO release key, so there is nothing to sign and no manifest to write."
    crf_report_release_key "$KEYS_CS"
    exit 0
fi
echo "Release key compiled into this build: ${#PUB} chars."

# --- the private key ---------------------------------------------------------
KEY="${CRF_RELEASE_KEY:-}"
DEFAULT_KEY="${HOME}/keys/circuitrf-release.pem"

if [ -z "$KEY" ]; then
    if [ -t 0 ]; then
        echo ""
        printf 'Private key [%s, or "skip"]: ' "$DEFAULT_KEY"
        # `|| KEY=skip` because `set -e` would otherwise make a Ctrl-D here exit the script silently,
        # mid-run, with no line of output saying why.
        read -r KEY || KEY="skip"
        [ -n "$KEY" ] || KEY="$DEFAULT_KEY"
    else
        # Not a terminal and nothing in the environment: prompting would block a script forever.
        KEY="skip"
    fi
fi
KEY="${KEY/#\~/$HOME}"

# --- creating the release and uploading ---------------------------------------
#
# A DRAFT, always. A release is created empty and its assets arrive one at a time over several
# minutes, and the updater reads the release list - so a published-but-still-uploading release is a
# window in which clients are offered a version whose asset for their platform does not exist yet.
# A draft is invisible to the feed until it is published, which closes that window entirely.
#
# Notes are left EMPTY on purpose: they are written by hand afterwards, and the publish is the
# separate, deliberate act that ends the process.
human_size() {
    awk -v b="$1" 'BEGIN {
        split("B KiB MiB GiB", u, " "); i = 1
        while (b >= 1024 && i < 4) { b /= 1024; i++ }
        printf (i == 1 ? "%d %s" : "%.1f %s"), b, u[i]
    }'
}

publish_to_github() {
    if [ "${CRF_PUBLISH:-}" = "0" ]; then return 0; fi

    if ! command -v gh >/dev/null 2>&1; then
        echo ""
        echo "  (The GitHub CLI 'gh' is not installed, so the release was not created.)"
        return 0
    fi
    if ! gh auth status >/dev/null 2>&1; then
        echo ""
        echo "  ('gh' is installed but not logged in - run 'gh auth login'. Release not created.)"
        return 0
    fi

    # `--target main` names a BRANCH, so GitHub creates the tag at whatever origin/main points at
    # when the draft is published - not at the tree these artifacts were built from. An unpushed
    # VERSION bump therefore tags a commit that does not contain it, and on a keyed release a commit
    # that does not contain the key the manifest verifies against.
    local head remote
    head="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || true)"
    remote="$(git -C "$ROOT" rev-parse origin/main 2>/dev/null || true)"
    if [ -n "$head" ] && [ -n "$remote" ] && [ "$head" != "$remote" ]; then
        echo ""
        echo "WARNING: local HEAD is not origin/main. The tag is created on origin/main, so it will"
        echo "         NOT name the commit these artifacts were built from. Push first."
    fi
    if [ -n "$(git -C "$ROOT" status --porcelain 2>/dev/null || true)" ]; then
        echo ""
        echo "WARNING: the working tree has uncommitted changes, so no tag can name what was built."
    fi

    # Everything in dist/ except dotfiles and any *.rejected an earlier run moved aside.
    local files=()
    while IFS= read -r f; do files+=("$f"); done < <(
        find "$DIST" -maxdepth 1 -type f ! -name '.*' ! -name '*.rejected' | sort)
    local total=${#files[@]}

    if [ "${CRF_PUBLISH:-}" != "1" ]; then
        if [ ! -t 0 ]; then return 0; fi
        echo ""
        local channel="stable release"
        [ "$IS_PRERELEASE" -eq 1 ] && channel="PRE-RELEASE (beta channel)"
        printf 'Create a DRAFT GitHub release %s as a %s and upload %d files? [y/N]: ' \
               "$CRF_VERSION" "$channel" "$total"
        local answer=""; read -r answer || answer="n"
        case "$answer" in [yY]*) ;; *) echo "  Skipped. Nothing was uploaded."; return 0 ;; esac
    fi

    # A release tagged this way may already exist for two very different reasons, and they need
    # opposite answers. A PUBLISHED one is being offered to clients right now, and re-uploading over
    # it would swap assets under them - refuse. A DRAFT is this script's own half-finished work from a
    # run whose uploads did not all land, and refusing that makes the "re-run to retry" advice below
    # unreachable, which is exactly what it did before.
    local existing_draft
    existing_draft="$(gh release view "$CRF_VERSION" --json isDraft -q .isDraft 2>/dev/null || true)"

    if [ "$existing_draft" = "false" ]; then
        echo ""
        echo "  A release tagged ${CRF_VERSION} is already PUBLISHED. Refusing to touch it - re-uploading"
        echo "  over it would swap assets under clients already being offered it."
        echo "  Delete it first, or bump VERSION."
        return 0
    fi

    echo ""
    if [ "$existing_draft" = "true" ]; then
        echo "Resuming the existing DRAFT ${CRF_VERSION} - --clobber replaces each asset rather than"
        echo "duplicating it, so re-uploading everything is safe and fixes a truncated one."
    else
        echo "Creating draft release ${CRF_VERSION} (empty notes) ..."
        if [ "$IS_PRERELEASE" -eq 1 ]; then
            gh release create "$CRF_VERSION" --draft --prerelease \
                --target main --title "$TITLE" --notes ""
            echo "  Marked PRE-RELEASE, from the '-' in ${CRF_VERSION}. Only users who have ticked"
            echo "  Include beta releases will be offered it."
        else
            gh release create "$CRF_VERSION" --draft \
                --target main --title "$TITLE" --notes ""
            echo "  Marked a STABLE release. Every user with automatic updates on will be offered it."
        fi
    fi

    echo ""
    local i=0 failed=0 start
    for f in "${files[@]}"; do
        i=$((i + 1))
        start=$SECONDS
        printf '[%2d/%d] %-46s %10s  ' "$i" "$total" "$(basename "$f")" "$(human_size "$(wc -c < "$f")")"
        if gh release upload "$CRF_VERSION" "$f" --clobber >/dev/null 2>&1; then
            printf 'OK %ds\n' "$((SECONDS - start))"
        else
            printf 'FAILED\n'
            failed=$((failed + 1))
        fi
    done

    echo ""
    echo "Uploaded $((total - failed))/${total}."

    # Compare what is on the release against what is on disk, by name AND byte size. An upload that
    # reported success and truncated is the one failure this catches and a count never would.
    local bad=0
    for f in "${files[@]}"; do
        local n; n="$(basename "$f")"
        local want; want="$(wc -c < "$f" | tr -d ' ')"
        local got; got="$(gh release view "$CRF_VERSION" --json assets \
                          -q ".assets[] | select(.name==\"${n}\") | .size" 2>/dev/null || true)"
        if [ "$got" != "$want" ]; then
            echo "  MISMATCH ${n}: local ${want} bytes, release '${got:-absent}'"
            bad=$((bad + 1))
        fi
    done

    if [ "$failed" -eq 0 ] && [ "$bad" -eq 0 ]; then
        echo "  All ${total} verified on the release, name and byte size."
    else
        echo ""
        echo "  ${failed} upload(s) failed and ${bad} did not match. Re-run to retry;"
        echo "  --clobber replaces an asset rather than duplicating it. DO NOT publish until clean."
        return 0
    fi

    echo ""
    echo "  The release is a DRAFT and no client can see it yet. Add the notes, then publish:"
    echo "      gh release edit ${CRF_VERSION} --draft=false"
    echo ""
    # `gh release edit` builds its request from the flags actually passed - every bool is a
    # NilBoolFlag, so an omitted one is left OUT of the API call rather than sent as false. Publishing
    # therefore changes `draft` and nothing else, and the prerelease flag set above survives. Re-passing
    # it would be harmless but would teach the wrong rule; check the result instead.
    if [ "$IS_PRERELEASE" -eq 1 ]; then
        echo "  That preserves the PRE-RELEASE flag set above - only --draft is sent. Confirm with:"
    else
        echo "  That leaves it a STABLE release - only --draft is sent. Confirm with:"
    fi
    echo "      gh release view ${CRF_VERSION} --json isDraft,isPrerelease"
    echo "  Remember the README download table still points at the previous version."
}

report_no_signature() {
    echo ""
    echo "############################################################################"
    echo "#  NOT SIGNED. Users will NOT receive this release as an automatic update. #"
    echo "############################################################################"
    echo ""
    echo "  $1"
    echo ""
    echo "  Every client from the keyed build onward refuses a release with no valid"
    echo "  signature, on every platform. Publishing this as it stands offers it to"
    echo "  nobody, and says nothing anywhere when it does not."
    echo ""
    echo "  No manifest anyone could upload was left in dist/. Re-run with the right key."
}

if [ "$KEY" = "skip" ]; then
    report_no_signature "No key given, so no manifest was written."
    exit 0
fi

if [ ! -f "$KEY" ]; then
    report_no_signature "No such file: ${KEY}"
    exit 0
fi

# --- manifest, signature, and the check that decides the verdict --------------
echo ""
echo "Building the manifest from dist/ ..."
dotnet run --project "${ROOT}/tools/ReleaseSigner" -- \
    manifest "$DIST" -o "$MANIFEST" --base-url "$BASE_URL"

echo "Signing it ..."
if ! dotnet run --project "${ROOT}/tools/ReleaseSigner" -- sign "$MANIFEST" "$KEY"; then
    rm -f "$MANIFEST" "$SIGNATURE"
    report_no_signature "That key could not sign the manifest - see the error above."
    exit 0
fi

# The verdict. Verifying against the key compiled into THIS build is the only check that answers the
# question a person actually has, because a signature made by the wrong private key is perfectly
# valid and simply verifies under a key no client carries.
echo ""
echo "Verifying against the public key compiled into circuitRF ..."
if dotnet run --project "${ROOT}/tools/ReleaseSigner" -- verify "$MANIFEST" "$SIGNATURE" "$PUB"; then
    echo ""
    echo "############################################################################"
    echo "#  OK  Users WILL receive this release as an automatic update.             #"
    echo "############################################################################"
    echo ""
    echo "  The signature verifies under the key built into this very build, so every"
    echo "  client carrying it will accept these bytes and no others."
    echo ""
    echo "  Asset URLs were written as ${BASE_URL}/<file>."
    echo "  If the release tag is not '${CRF_VERSION}', re-run with CRF_RELEASE_BASE_URL set."
    publish_to_github
    exit 0
fi

# A signature that does not verify is worse than none: uploaded, it is refused by every client with
# nothing said anywhere. So it is moved aside rather than left in dist/ next to the upload step.
mv -f "$MANIFEST"  "${MANIFEST}.rejected"
mv -f "$SIGNATURE" "${SIGNATURE}.rejected"
report_no_signature "That key is NOT the one built into this circuitRF. It signed the manifest, but
  under a key no client carries. The files were renamed to *.rejected so they
  cannot be uploaded by accident."
exit 0
