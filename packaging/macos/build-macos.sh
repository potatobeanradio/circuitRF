#!/bin/bash
set -euo pipefail

# ── circuitRF macOS .dmg builder ──────────────────────────────────────────────
#
#   packaging/macos/build-macos.sh                 → BOTH disk images, for circuitRF
#   packaging/macos/build-macos.sh harmonica       → both, for harmonicaRF
#   packaging/macos/build-macos.sh wbond           → both, for wBond
#
#   packaging/macos/build-macos.sh circuitrf arm64 → Apple Silicon only
#   packaging/macos/build-macos.sh circuitrf x64   → Intel only
#
# → dist/circuitRF-<version>-arm64.dmg  and  dist/circuitRF-<version>-x64.dmg
#
# BOTH ARCHITECTURES ARE BUILT FROM WHICHEVER MAC YOU ARE ON, and that is a measured claim rather
# than a hopeful one. Every piece of the bundle cross-builds:
#
#   the .NET application     `dotnet publish -r osx-x64|osx-arm64`, either way round
#   crf-vmhost               `swift build --arch` — Apple's toolchain targets both slices, and
#                            Virtualization.framework is in the SDK for both. main.swift's Rosetta
#                            block is behind `#if arch(arm64)`, a TARGET test, so the x86-64 build
#                            correctly contains no Rosetta code at all
#   osdi-worker              `cc -arch`
#   the Linux VM image       pure download-and-repack (curl, tar, cpio, gzip, python3) — no
#                            compiler is involved in producing either guest kernel
#   senior_worker            one file for both: it is an x86-64 LINUX binary either way, because
#                            that is what the vendor model libraries are
#
# What makes it safe is that nothing here is trusted to have done the right thing: before writing a
# disk image this script reads the architecture back out of the built bundle with `lipo`. A stale
# helper build directory, or a helper that quietly fell back to the host, is caught rather
# than shipped — the failure it prevents is an application that launches, reads a kit, describes it
# correctly and then cannot evaluate a single compiled device model.
#
# The .app itself is built by the bundle scripts that already live in src/Ui/ — this adds the two
# things a distributable disk image needs on top of one: the icon (rasterised from the committed
# SVG, since no icon binary is tracked) and the .dmg with its /Applications drop target.
#
# Requires: .NET 10 SDK. Everything else (hdiutil, codesign, lipo) ships with macOS.

APP="${1:-circuitrf}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

case "$APP" in
    circuitrf) NAME="circuitRF";   BUNDLE_SCRIPT="bundleForMacOS.sh" ;;
    harmonica) NAME="harmonicaRF"; BUNDLE_SCRIPT="bundleForHarmonicaMacOS.sh" ;;
    wbond)     NAME="wBond";       BUNDLE_SCRIPT="bundleForWBondMacOS.sh" ;;
    *) echo "Usage: $0 [circuitrf|harmonica|wbond] [arm64|x64|both]"; exit 1 ;;
esac

# BOTH IS THE DEFAULT. A release needs both disk images, and the failure mode of the old
# one-at-a-time default was silent: whoever cut the release shipped whichever architecture they
# happened to be sitting at, and the other one simply did not exist.
case "${2:-both}" in
    both)  ARCHES="arm64 x64" ;;
    arm64) ARCHES="arm64"     ;;
    x64)   ARCHES="x64"       ;;
    *) echo "Usage: $0 [circuitrf|harmonica|wbond] [arm64|x64|both]"; exit 1 ;;
esac

# One source for the version — the repo-root VERSION file. The bundle script stamps the same value
# into the .app's Info.plist, so a disk image can never be named a version the installed app does
# not report.
source "${ROOT}/packaging/version.sh"
VERSION="$CRF_VERSION"

echo "🎨 Building icons..."
dotnet run --project "${ROOT}/tools/IconGen" -- "$APP"

# THE VM IMAGE IS BUILT HERE EVEN THOUGH A PLAIN BUILD LEAVES IT ALONE. Compiled device models are
# Linux libraries — nothing on macOS can load one — so circuitRF runs the worker inside the small
# Linux VM it ships, and the kernel and initramfs are part of that. Building them from scratch pulls
# ~330 MB per architecture, which is why an ordinary `dotnet build` prints the command instead of
# doing it silently. Packaging is the one moment that download is exactly what the operator asked
# for.
export CrfBuildVmImage=true

# ── Signing identity, and what it decides for the people who download this ────
#
# THE DEFAULT IS TO SIGN IF THIS MACHINE CAN, AND TO BUILD UNSIGNED IF IT CANNOT. Nothing here ever
# fails for want of a certificate: no certificate means an ad-hoc build, which is what circuitRF has
# always shipped.
#
#   CRF_SIGN_IDENTITY   use this identity verbatim, ask nothing (CI, scripts)
#   CRF_SIGN=never      force an ad-hoc build even on a machine that could sign
#   CRF_NOTARY_PROFILE  the notarytool keychain profile to notarise with
#   CRF_NOTARIZE=never  sign, but do not notarise
#
# WHY THIS MATTERS AT ALL: an ad-hoc signature has no identity behind it, so Gatekeeper cannot trust
# it however well the bundle is formed. `spctl -a` rejects it, and since macOS 15 the Control-click
# Open bypass is gone, leaving the user a trip through System Settings, Privacy & Security, Open
# Anyway. Signing with a Developer ID certificate AND notarising is the only thing that removes it.
SIGN_IDENTITY=""
INTERACTIVE=0
[ -t 0 ] && [ -t 1 ] && INTERACTIVE=1

if [ "${CRF_SIGN:-}" = never ]; then
    SIGN_IDENTITY="-"
elif [ -n "${CRF_SIGN_IDENTITY:-}" ]; then
    SIGN_IDENTITY="$CRF_SIGN_IDENTITY"
else
    # ONLY "Developer ID Application" WILL DO, and this is the one place a paid Apple account
    # misleads people. A paid membership issues "Apple Development" certificates automatically, and
    # they appear in exactly the same list — but they are for running builds on your own machines.
    # Signing a release with one produces something WORSE than ad-hoc: it looks signed, Gatekeeper
    # still refuses it, and the notary service rejects it outright. A Developer ID Application
    # certificate has to be created deliberately (Xcode, Settings, Accounts, Manage Certificates, +).
    # So the grep is for that exact prefix, and anything else is treated as no certificate at all.
    IDS=$(security find-identity -v -p codesigning 2>/dev/null \
          | sed -n 's/.*"\(Developer ID Application: [^"]*\)".*/\1/p')
    COUNT=$(printf "%s" "$IDS" | grep -c . || true)

    if [ "${COUNT:-0}" -eq 0 ]; then
        SIGN_IDENTITY="-"
    elif [ "${COUNT:-0}" -eq 1 ]; then
        SIGN_IDENTITY="$IDS"
    elif [ "$INTERACTIVE" = 1 ]; then
        echo "More than one Developer ID Application certificate is installed:"
        printf "%s\n" "$IDS" | nl -w4 -s') '
        printf "Which one? [1-%s, or Enter for an unsigned build]: " "$COUNT"
        read -r PICK
        if [ -n "$PICK" ]; then
            SIGN_IDENTITY=$(printf "%s\n" "$IDS" | sed -n "${PICK}p")
            [ -n "$SIGN_IDENTITY" ] || { echo "No such choice."; exit 1; }
        else
            SIGN_IDENTITY="-"
        fi
    else
        # Never guess between certificates in a script: the wrong one is a release signed by the
        # wrong entity, which is not the sort of thing to decide by sort order.
        echo "Several Developer ID Application certificates are installed and this is not an"
        echo "interactive shell. Name one with CRF_SIGN_IDENTITY. Building unsigned."
        SIGN_IDENTITY="-"
    fi
fi

export CRF_SIGN_IDENTITY="$SIGN_IDENTITY"

# ── What the notary service wants, and where to read it off this machine ──────
#
# THREE VALUES, AND TWO OF THEM ARE ROUTINELY GOT WRONG.
#
#   Apple ID   the email you sign in to the developer account with. Individual-account certificates
#              carry it in their common name, so it can usually be read straight off the keychain.
#
#   Team ID    TEN CHARACTERS, AND IT IS THE CERTIFICATE'S "OU" FIELD -- NOT the value in brackets
#              after the name. For a Developer ID certificate those two happen to be the same string,
#              which is exactly why this bites: on an "Apple Development" certificate they are
#              DIFFERENT, so anyone reading the Team ID off the identity list
#              ("Apple Development: you (5K57RC984E)") takes a per-certificate id and calls it a Team
#              ID. crf_apple_team_hints below prints the real ones, out of the certificates.
#
#   Password   AN APP-SPECIFIC PASSWORD, never the Apple ID password. Made at appleid.apple.com,
#              Sign-In and Security, App-Specific Passwords; it looks like abcd-efgh-ijkl-mnop and
#              requires two-factor authentication on the account. Using the real account password is
#              the single most common cause of the 401 this returns, and the 401 wording does not
#              make that obvious.
#
# Only valid, unexpired certificates are consulted -- an expired one names a team the account may no
# longer be in, and suggesting it would be worse than suggesting nothing.
crf_apple_team_hints() {
    tmp=$(mktemp -d) || return 0
    security find-certificate -a -c "Apple D" -p 2>/dev/null \
      | awk -v d="$tmp" 'BEGIN{n=0} /BEGIN CERT/{n++} {print > (d "/c" n ".pem")}' 2>/dev/null
    for f in "$tmp"/*.pem; do
        [ -e "$f" ] || continue
        openssl x509 -in "$f" -noout -checkend 0 >/dev/null 2>&1 || continue
        openssl x509 -in "$f" -noout -subject 2>/dev/null
    done | sed -E 's|.*OU[ ]?=[ ]?([A-Z0-9]{10}).*O[ ]?=[ ]?([^/,]*).*|    \1  \2|' | sort -u
    rm -rf "$tmp"
}

crf_apple_id_hints() {
    tmp=$(mktemp -d) || return 0
    security find-certificate -a -c "Apple D" -p 2>/dev/null \
      | awk -v d="$tmp" 'BEGIN{n=0} /BEGIN CERT/{n++} {print > (d "/c" n ".pem")}' 2>/dev/null
    for f in "$tmp"/*.pem; do
        [ -e "$f" ] || continue
        openssl x509 -in "$f" -noout -checkend 0 >/dev/null 2>&1 || continue
        openssl x509 -in "$f" -noout -subject 2>/dev/null
    done | grep -oE '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' | sort -u | sed 's/^/    /'
    rm -rf "$tmp"
}

# ── Notary credentials ────────────────────────────────────────────────────────
#
# Signing alone does NOT remove the Gatekeeper prompt — a Developer ID build that has not been
# notarised is still refused on first launch. So the two are resolved together, and a signed build
# that cannot be notarised says so loudly rather than reporting success.
#
# The credentials are an Apple ID plus an APP-SPECIFIC password (appleid.apple.com, Sign-In and
# Security, App-Specific Passwords) — never the account password. They are stored once in the
# keychain by `notarytool store-credentials`, which does its own secure prompting; this script never
# reads, holds or echoes a password itself, and none of it reaches the shell history.
NOTARY_PROFILE="${CRF_NOTARY_PROFILE:-circuitrf-notary}"
NOTARISE=0

if [ "$SIGN_IDENTITY" != "-" ] && [ "${CRF_NOTARIZE:-}" != never ]; then
    if security find-generic-password -l "$NOTARY_PROFILE" >/dev/null 2>&1 \
    || security find-generic-password -s "com.apple.gke.notary.tool" -a "$NOTARY_PROFILE" >/dev/null 2>&1; then
        NOTARISE=1
    elif [ "$INTERACTIVE" = 1 ]; then
        echo ""
        echo "Signing as: ${SIGN_IDENTITY}"
        echo "No notary credentials are stored under the profile '${NOTARY_PROFILE}'."
        echo "Without them the disk images are signed but NOT notarised, and macOS still refuses"
        echo "them on first launch."
        echo ""
        echo "Storing them is a one-time step. Read off this machine's own certificates:"
        echo ""
        HINT_IDS=$(crf_apple_id_hints)
        HINT_TEAMS=$(crf_apple_team_hints)
        if [ -n "$HINT_IDS" ]; then
            echo "  Apple ID (the developer account's sign-in email):"
            printf "%s\n" "$HINT_IDS"
        else
            echo "  Apple ID: the email you sign in to the developer account with."
        fi
        echo ""
        if [ -n "$HINT_TEAMS" ]; then
            echo "  Team ID -- ten characters. THIS IS THE CERTIFICATE'S OU FIELD, NOT the value in"
            echo "  brackets in the identity list; on an \"Apple Development\" certificate those two"
            echo "  differ, which is the usual reason a Team ID is wrong:"
            printf "%s\n" "$HINT_TEAMS"
            echo ""
            echo "  Use the team the Developer ID Application certificate belongs to."
        else
            echo "  Team ID: ten characters, from developer.apple.com ▸ Membership."
        fi
        echo ""
        echo "  Password: an APP-SPECIFIC PASSWORD, not your Apple ID password. Make one at"
        echo "  appleid.apple.com ▸ Sign-In and Security ▸ App-Specific Passwords; it looks like"
        echo "  abcd-efgh-ijkl-mnop. notarytool prompts for it and never echoes it."
        echo ""
        echo "  (That page says app-specific passwords are for services \"not provided by Apple\"."
        echo "  Use one anyway: notarytool's own man page tells you to. It is needed because your"
        echo "  account has two-factor authentication and a command-line tool cannot show you a 2FA"
        echo "  prompt - not because of who wrote the app.)"
        echo ""
        printf "Set them up now? [Y/n]: "
        read -r ANSWER
        case "$ANSWER" in
            [Nn]*) echo "Continuing without notarisation." ;;
            *)
                if xcrun notarytool store-credentials "$NOTARY_PROFILE"; then
                    NOTARISE=1
                else
                    # A 401 here says "Username or password is incorrect", which is true and unhelpful:
                    # it is the same message whether the password was the wrong KIND, the Apple ID was
                    # a different one from the account that owns the team, or it was simply mistyped.
                    echo ""
                    echo "Credentials were not stored. If that was a 401 (invalid credentials), it is"
                    echo "almost always one of these, in order of how often it is the answer:"
                    echo ""
                    echo "  1. The password was your Apple ID password. It has to be an APP-SPECIFIC"
                    echo "     password from appleid.apple.com ▸ Sign-In and Security. The account"
                    echo "     needs two-factor authentication before that option appears."
                    echo "  2. The app-specific password was generated on a DIFFERENT Apple ID from"
                    echo "     the one entered here. They must be the same account."
                    echo "  3. It was mistyped, or has been revoked. Generate a fresh one and paste"
                    echo "     it exactly, hyphens included."
                    echo ""
                    echo "Try again on its own, without a build, until it takes:"
                    echo "    xcrun notarytool store-credentials ${NOTARY_PROFILE} \\"
                    echo "        --apple-id <your-apple-id> --team-id <TEAMID>"
                    echo ""
                    echo "Continuing without notarisation."
                fi
                ;;
        esac
    fi
fi

NOTARISED=0

DIST="${ROOT}/dist"
mkdir -p "$DIST"
BUILT=""

for ARCH in $ARCHES; do
    case "$ARCH" in
        arm64) RID="osx-arm64"; MACHO_ARCH="arm64"  ;;
        x64)   RID="osx-x64";   MACHO_ARCH="x86_64" ;;
    esac

    echo ""
    echo "══ ${NAME} · ${ARCH} ══════════════════════════════════════════════════"

    # The bundle scripts default to the host's architecture; this is what makes the RID this script
    # names and the RID the .app is built at ONE value rather than two that happen to agree.
    export CRF_RID="$RID"

    echo "📦 Building ${NAME}.app (${RID})..."
    ( cd "${ROOT}/src/Ui" && bash "./${BUNDLE_SCRIPT}" )

    APP_BUNDLE="${ROOT}/src/Ui/bin/Release/net10.0/${RID}/${NAME}.app"
    [ -d "$APP_BUNDLE" ] || { echo "❌ ${APP_BUNDLE} was not produced."; exit 1; }

    # ── What the app needs beside its assemblies ──────────────────────────────
    #
    # The .csproj builds these and its CrfPublishHelperPrograms target publishes them, but both
    # build steps are warn-only BY DESIGN: nobody should be unable to build circuitRF for want of a
    # Swift toolchain or a C compiler. That is right for a build and wrong for a RELEASE — a disk
    # image missing them installs an application that reads a kit, describes it correctly, and then
    # refuses at Run naming programs the user never installed and had no way to install.
    #
    # All four or none, on this platform: the worker here is a LINUX build (that is what the models
    # are), so without the VM host, its kernel and its initramfs there is nothing that can run it.
    #
    # Set CRF_ALLOW_NO_DEVICE_WORKER=1 to package without them on purpose.
    NEEDED="senior_worker crf-vmhost crf-linux-kernel crf-linux-initramfs.cpio.gz"
    MISSING=""
    for f in $NEEDED; do
        [ -f "${APP_BUNDLE}/Contents/MacOS/${f}" ] || MISSING="${MISSING} ${f}"
    done

    if [ -n "$MISSING" ]; then
        if [ "${CRF_ALLOW_NO_DEVICE_WORKER:-}" = 1 ]; then
            echo "⚠️  Packaging without:${MISSING}. Compiled device models will not run."
        else
            echo "❌ Missing from ${NAME}.app (${ARCH}):${MISSING}"
            echo ""
            echo "   These are built during \`dotnet build\`, which only WARNS when it cannot:"
            echo ""
            echo "       senior_worker                 needs zig, docker/podman, or a cross-compiler"
            echo "       crf-vmhost + kernel/initramfs  need Xcode's Swift toolchain and a network"
            echo ""
            echo "   Build them by hand to see why:"
            echo "       tools/senior-worker/ensure-built.sh"
            echo "       tools/macos-vmhost/ensure-built.sh --arch ${MACHO_ARCH} --with-image"
            echo ""
            echo "   To package deliberately without them: CRF_ALLOW_NO_DEVICE_WORKER=1 $0 $APP $ARCH"
            exit 1
        fi
    fi

    # ── Architecture, measured rather than assumed ────────────────────────────
    #
    # Mirrors what build-linux.sh does with the worker's ELF header, and for the same reason: a binary
    # of the wrong architecture is not a lesser version of a working one. The app host, crf-vmhost
    # and osdi-worker are Mach-O, so `lipo -archs` reads it straight out; a wrong one here means a
    # bundle that either will not launch at all or cannot evaluate a compiled device model.
    #
    # senior_worker is deliberately NOT checked here — it is a Linux ELF, always x86-64 on purpose,
    # and lipo knows nothing about it. build-linux.sh's ELF check is the one that covers that file.
    BAD=""
    for f in "${NAME}" crf-vmhost osdi-worker; do
        path="${APP_BUNDLE}/Contents/MacOS/${f}"
        [ -f "$path" ] || continue
        archs=$(lipo -archs "$path" 2>/dev/null || echo "?")
        case " $archs " in
            *" ${MACHO_ARCH} "*) ;;
            *) BAD="${BAD}\n       ${f}: ${archs}" ;;
        esac
    done

    if [ -n "$BAD" ]; then
        echo "❌ ${NAME}.app (${ARCH}) contains Mach-O binaries that are not ${MACHO_ARCH}:"
        printf "%b\n" "$BAD"
        echo ""
        echo "   A helper fell back to this machine's own architecture instead of the one being"
        echo "   published, or a stale build directory was picked up. Delete"
        echo "   tools/macos-vmhost/build and tools/osdi-worker/build and run this again."
        exit 1
    fi

    # ── The guest kernel ──────────────────────────────────────────────────────
    #
    # The one per-architecture artifact `lipo` cannot speak for: it is a LINUX kernel, not Mach-O.
    # An aarch64 Image carries "ARM\x64" at offset 56; an x86-64 bzImage carries "HdrS" at 0x202.
    # A bundle carrying the other one starts its VM and gets "Internal Virtualization error", which
    # names nothing, so it is worth the four lines to catch here.
    KERNEL="${APP_BUNDLE}/Contents/MacOS/crf-linux-kernel"
    if [ -f "$KERNEL" ]; then
        python3 - "$KERNEL" "$MACHO_ARCH" <<'KPY' || exit 1
import sys
data = open(sys.argv[1], 'rb').read()
want = sys.argv[2]
is_arm = data[56:60] in (b'ARM\x64', b'ARMd')
is_x86 = data[0x202:0x206] == b'HdrS'
got = 'arm64' if is_arm else 'x86_64' if is_x86 else 'unrecognised'
if got != want:
    sys.exit(f"❌ crf-linux-kernel in this bundle is {got}, not {want}. The guest kernel must "
             f"match the host that boots it; delete tools/macos-vmhost/build and build again.")
KPY
    fi

    # ── Notarise and STAPLE THE .app, before it goes into the image ───────────
    #
    # WHY THIS EXISTS AS A SEPARATE PASS, given that the .dmg is notarised and stapled below.
    #
    # A staple is attached to one artifact. The .dmg's ticket covers the .dmg; it does NOT travel
    # with the .app when the .app is copied out of it — measured, not assumed: `xcrun stapler
    # validate` on a bundle extracted from a stapled circuitRF .dmg reports "does not have a ticket
    # stapled to it" (2026-08-25). So without this pass the INSTALLED application has no ticket, and
    # every Gatekeeper assessment of it has to reach Apple over the network.
    #
    # It matters twice. For a hand-installed copy it is the difference between launching offline and
    # meeting a prompt on a flaky connection. For an AUTOMATIC UPDATE it is the belt to the braces:
    # the swapped-in bundle carries no com.apple.quarantine (the updater fetched it with HttpClient,
    # which does not set the attribute), so no assessment runs at all and the app launches either
    # way — but a stapled bundle is one that survives the day some other mechanism does trigger one.
    #
    # ORDER IS NOT NEGOTIABLE: notarise, then staple the .app, THEN build the image. You cannot
    # staple an archive, and you cannot staple a bundle sealed inside a read-only disk image.
    if [ "$SIGN_IDENTITY" != "-" ] && [ "$NOTARISE" = 1 ]; then
        echo "📤 Notarising ${NAME}.app (this waits on Apple; minutes, not seconds)..."

        APPZIP="$(mktemp -d)/${NAME}.zip"
        # ditto, never `zip`: it is the only tool that preserves the Unix mode bits and the
        # Frameworks symlinks a bundle's code signature is computed over. A bundle missing its
        # executable bit has a BROKEN signature, and the failure arrives as a refusal at launch.
        ditto -c -k --keepParent --sequesterRsrc "$APP_BUNDLE" "$APPZIP" || {
            echo "❌ Could not archive ${NAME}.app for notarisation."; exit 1; }

        xcrun notarytool submit "$APPZIP" --keychain-profile "$NOTARY_PROFILE" --wait || {
            echo "❌ Notarisation of ${NAME}.app failed. For the reasons:"
            echo "     xcrun notarytool log <submission-id> --keychain-profile $NOTARY_PROFILE"
            exit 1; }

        echo "📎 Stapling the ticket to ${NAME}.app..."
        xcrun stapler staple "$APP_BUNDLE" || {
            echo "❌ Could not staple ${NAME}.app."; exit 1; }

        rm -rf "$(dirname "$APPZIP")"
    fi

    DMG="${DIST}/${NAME}-${VERSION}-${ARCH}.dmg"
    STAGE="$(mktemp -d)/${NAME}"
    mkdir -p "$STAGE"

    echo "💿 Staging disk image..."
    cp -R "$APP_BUNDLE" "$STAGE/"
    ln -s /Applications "${STAGE}/Applications"   # the drag-to-install target users expect

    rm -f "$DMG"
    hdiutil create -volname "$NAME" -srcfolder "$STAGE" -ov -format UDZO -quiet "$DMG"
    rm -rf "$(dirname "$STAGE")"

    # ── Sign and notarise the DISK IMAGE ──────────────────────────────────────
    #
    # The .app inside was signed by the bundle script; this is the container, and it is a separate
    # signature. It matters because the STAPLE goes here: `stapler` attaches the notarisation ticket
    # to the artefact the user downloads, and a ticket stapled to the .dmg means the very first
    # launch works offline. Without a staple the Mac has to reach Apple to check, so a user on a
    # flaky connection meets a Gatekeeper prompt for an app that is in fact notarised.
    #
    # Skipped entirely for an ad-hoc build: there is nothing to notarise, and saying so once at the
    # end is more use than a failure here.
    if [ "$SIGN_IDENTITY" != "-" ]; then
        echo "🔐 Signing the disk image..."
        codesign --force --sign "$SIGN_IDENTITY" --timestamp "$DMG" || {
            echo "❌ Could not sign ${DMG}."; exit 1; }

        if [ "$NOTARISE" = 1 ]; then
            echo "📤 Notarising (this waits on Apple; minutes, not seconds)..."
            xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait || {
                echo "❌ Notarisation failed. For the reasons:"
                echo "     xcrun notarytool log <submission-id> --keychain-profile $NOTARY_PROFILE"
                exit 1; }

            echo "📎 Stapling the ticket..."
            xcrun stapler staple "$DMG" || { echo "❌ Could not staple ${DMG}."; exit 1; }
            NOTARISED=1
        else
            echo "⚠️  Signed but NOT notarised. macOS still refuses this on first launch."
        fi
    fi

    BUILT="${BUILT}\n   ${DMG}"
done

echo ""
echo "✅ Built:"
printf "%b\n" "$BUILT"
echo ""

if [ "$NOTARISED" = 1 ]; then
    echo "   Signed and notarised. These open with no prompt, on any Mac, offline."
elif [ "$SIGN_IDENTITY" != "-" ]; then
    echo "   Signed but NOT notarised — Gatekeeper still refuses these on first launch."
    echo "   Set CRF_NOTARY_PROFILE and run again; see BUILDING.md."
elif [ -n "$(security find-identity -v -p codesigning 2>/dev/null | grep -v "Developer ID Application")" ] \
  && [ -z "$(security find-identity -v -p codesigning 2>/dev/null | grep "Developer ID Application")" ]; then
    echo "   AD-HOC SIGNED — no Developer ID Application certificate is installed."
    echo ""
    echo "   This machine DOES have signing certificates, but they are \"Apple Development\" ones."
    echo "   A paid membership issues those automatically and they are for running builds on your"
    echo "   own machines: signing a release with one is worse than ad-hoc, because it looks signed,"
    echo "   Gatekeeper still refuses it, and the notary service rejects it outright."
    echo ""
    echo "   Create the right kind once — Xcode ▸ Settings ▸ Accounts ▸ (your Apple ID) ▸"
    echo "   Manage Certificates ▸ + ▸ Developer ID Application — then run this again and it will"
    echo "   be found and used automatically."
    echo ""
    echo "   Meanwhile, on the machine that downloaded these:"
    echo "       xattr -dr com.apple.quarantine /Applications/${NAME}.app"
else
    echo "   AD-HOC SIGNED. Gatekeeper will refuse these on a machine that downloaded them, and"
    echo "   since macOS 15 there is no Control-click → Open bypass: the user must go to"
    echo "   System Settings → Privacy & Security → Open Anyway, after one blocked launch."
    echo ""
    echo "   Locally you can clear the quarantine flag instead:"
    echo "       xattr -dr com.apple.quarantine /Applications/${NAME}.app"
    echo ""
    echo "   To ship something that simply opens, sign and notarise — see BUILDING.md:"
    echo "       CRF_SIGN_IDENTITY=\"Developer ID Application: NAME (TEAMID)\" \\"
    echo "       CRF_NOTARY_PROFILE=circuitrf-notary $0 $APP"
fi
