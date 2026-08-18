# ── The version, read from the one place it is written ────────────────────────
#
# Sourced by the packaging and bundle scripts:  source ".../packaging/version.sh"
#
# Sets:
#   CRF_VERSION       the full string from the repo-root VERSION file, e.g. 0.9.0-beta.1
#                     — what users see, and what installer file names carry
#   CRF_VERSION_CORE  the numeric head, e.g. 0.9.0 — for fields that must be purely numeric
#                     (CFBundleVersion, the MSI ProductVersion)
#   CRF_DEB_VERSION   dpkg's spelling: the first '-' becomes '~', because dpkg sorts 0.9.0~beta.1
#                     BEFORE 0.9.0 and 0.9.0-beta.1 AFTER it — i.e. the plain form would make the
#                     beta look newer than the release it precedes.
#
# Override for a one-off build with CRF_VERSION=1.2.3 in the environment; nothing is ever written
# back, so the VERSION file stays the source of truth.

_crf_version_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ -z "${CRF_VERSION:-}" ]; then
    CRF_VERSION="$(tr -d '[:space:]' < "${_crf_version_root}/VERSION")"
fi

CRF_VERSION_CORE="${CRF_VERSION%%-*}"
_crf_tilde='~'                      # a literal ~ in a ${x/y/z} replacement needs a variable
CRF_DEB_VERSION="${CRF_VERSION/-/$_crf_tilde}"

export CRF_VERSION CRF_VERSION_CORE CRF_DEB_VERSION
