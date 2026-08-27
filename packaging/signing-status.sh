# --- The release key, and what it means for automatic updates -----------------
#
# Sourced by the packaging scripts:  source ".../packaging/signing-status.sh"
#
# The PUBLIC key is compiled into the binary (src/Ui/Updates/ReleaseKeys.cs), so a build either
# carries one or does not, and that is decided long before packaging runs. These helpers only READ
# it. The private half is never touched here - signing is a separate step over the finished dist/,
# because one manifest covers all fifteen artifacts and they are built on three different machines.
# See packaging/sign-release.sh and BUILDING.md.

# The compiled-in public key, or "" when the build carries none. The constant is written as adjacent
# string literals across several lines, so every quoted run in the declaration is concatenated -
# reading only the first line would silently truncate the key and compare unequal to itself.
crf_release_public_key() {
    sed -n '/PublicKeySpkiBase64[[:space:]]*=/,/;/p' "$1" \
        | grep -o '"[^"]*"' | tr -d '"' | tr -d '\n'
}

# A short end-of-run statement of what this build can and cannot do about updates. It never fails:
# the state it reports is a property of the source that was compiled, not of the packaging run, so
# there is nothing here a build could have got wrong and nothing worth stopping for.
crf_report_release_key() {
    _crf_keys_cs="$1"
    _crf_pub="$(crf_release_public_key "$_crf_keys_cs")"

    echo ""
    if [ -n "$_crf_pub" ]; then
        echo "Release key: COMPILED IN (${#_crf_pub} chars)."
        echo "   These artifacts can be installed as automatic updates - but ONLY once the release"
        echo "   manifest is signed. Collect all 15 files from all three platforms into dist/, then"
        echo "   run packaging/sign-release.sh on the machine holding the private key."
        echo "   Publishing without it means NO client is offered the release at all, silently."
    else
        echo "Release key: NONE compiled in."
        echo "   macOS and Linux clients will still update, anchored by the platform signature and"
        echo "   the download hash. WINDOWS CLIENTS WILL NOT: an unsigned Windows build has no"
        echo "   publisher for the updater to compare a payload against, so it stays notify-only."
        echo "   See BUILDING.md, 'The release signing key'."
    fi
}
