#!/bin/sh
# Builds the Linux kernel + initramfs that crf-vmhost boots.
#
# CONTRIBUTOR-BUILDABLE, ON A MAC, WITH NOTHING INSTALLED.
# ------------------------------------------------------
# This deliberately uses only tools macOS already ships — curl, tar, cpio, gzip, shasum, python3 —
# so anyone with a checkout can reproduce the image byte for byte and inspect exactly what runs.
# There is no Linux box, no container runtime and no cross-compiler in the loop. That is the whole
# point: an image nobody but CI can rebuild is a binary blob with extra steps.
#
# WHAT IT PRODUCES
#   crf-linux-kernel              uncompressed Linux kernel image
#   crf-linux-initramfs.cpio.gz   busybox userland + our guest-init
#
# WHAT IS *NOT* IN IT
# Nothing kit-specific and nothing library-specific: no device library, no model data, no worker.
# Those stay on the user's disk and are shared into the guest read-only at run time. The image is
# generic infrastructure and is the same for everybody.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
out=$here/build
work=$here/.work
keep_work=0

# --arch names the GUEST architecture: aarch64 for an Apple Silicon host, x86_64 for an Intel one.
# It defaults to this machine's, which is the only one this machine can actually BOOT.
#
# It is nonetheless a real option rather than a fixed fact, because building the image is pure
# download-and-repack — curl, tar, cpio, gzip and python3, not a compiler — so either architecture's
# image can be produced from either kind of Mac. That is what lets one run of
# packaging/macos/build-dmg.sh produce both disk images.
arch=""

while [ $# -gt 0 ]; do
    case "$1" in
        --out)       out=$2; shift 2 ;;
        --arch)      arch=$2; shift 2 ;;
        --keep-work) keep_work=1; shift ;;
        -h|--help)   sed -n '2,20p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

[ -n "$arch" ] || arch=$(uname -m)

# Absolute from here on: the initramfs is packed from inside a subshell that has cd'd elsewhere, so
# a relative --out would silently write into the work directory instead.
mkdir -p "$out"
out=$(cd "$out" && pwd)

. "$here/sources.lock"

case $arch in
    arm64|aarch64) arch=aarch64; url=$ALPINE_NETBOOT_AARCH64_URL; want=$ALPINE_NETBOOT_AARCH64_SHA256 ;;
    x86_64|x64)    arch=x86_64;  url=$ALPINE_NETBOOT_X86_64_URL;  want=$ALPINE_NETBOOT_X86_64_SHA256 ;;
    *) echo "unsupported guest architecture: $arch" >&2; exit 2 ;;
esac

if [ -z "$want" ]; then
    cat >&2 <<EOF
No pinned checksum for $arch in sources.lock.

Adding one is deliberate manual work: download the tarball, verify it against Alpine's own
published signature, then record the SHA-256 here. A checksum invented from whatever happens to
download today pins nothing and only looks like provenance.
EOF
    exit 2
fi

mkdir -p "$work" "$out"
tarball=$work/$(basename "$url")

# ── Fetch, pinned ─────────────────────────────────────────────────────────────
if [ -f "$tarball" ] && [ "$(shasum -a 256 "$tarball" | cut -d' ' -f1)" = "$want" ]; then
    echo "==> using cached $(basename "$tarball")"
else
    echo "==> downloading $(basename "$tarball") (~300 MB)"
    curl -fSL --retry 3 -o "$tarball" "$url"
fi

got=$(shasum -a 256 "$tarball" | cut -d' ' -f1)
[ "$got" = "$want" ] || {
    echo "checksum mismatch for $(basename "$tarball")" >&2
    echo "  expected $want" >&2
    echo "  got      $got" >&2
    exit 1
}

echo "==> extracting"
rm -rf "$work/nb"; mkdir -p "$work/nb"
tar xzf "$tarball" -C "$work/nb"

# ── Kernel ────────────────────────────────────────────────────────────────────
# THE TWO ARCHITECTURES NEED DIFFERENT THINGS HERE, and the difference is in VZLinuxBootLoader, not
# in Alpine:
#
#   aarch64  Stock arm64 kernels are EFI *zboot* images: a PE wrapper whose payload is a compressed
#            kernel, meant to be unpacked by its own EFI stub. VZLinuxBootLoader does not run an EFI
#            stub and needs a raw uncompressed Image, so the payload is extracted here. Getting this
#            wrong is not subtle but it is opaque: the VM refuses to start with nothing but
#            "Internal Virtualization error".
#
#   x86_64   The bzImage is handed over UNCHANGED. Its self-decompressing stub is part of the x86
#            boot protocol VZLinuxBootLoader implements — the same thing QEMU's -kernel takes — so
#            unwrapping it would be work that produced a file the boot loader cannot use.
#
# The check below is per-architecture for the same reason. A single "does this look like a kernel"
# test is what would print a warning about missing arm64 magic on a perfectly good bzImage.
echo "==> extracting kernel"
python3 - "$work/nb/boot/vmlinuz-virt" "$out/crf-linux-kernel" "$arch" <<'KERNELPY'
import gzip, lzma, struct, sys

src, dst, arch = sys.argv[1], sys.argv[2], sys.argv[3]
data = open(src, 'rb').read()

if arch == 'aarch64':
    if data[4:8] == b'zimg':
        offset, size = struct.unpack_from('<II', data, 8)
        payload = data[offset:offset + size]
        for name, decompress in (('gzip', gzip.decompress), ('xz', lzma.decompress)):
            try:
                image = decompress(payload)
                print(f"    zboot payload ({name}) -> {len(image)} bytes")
                break
            except Exception:
                image = None
        if image is None:
            sys.exit("zboot payload is in a compression this script does not handle")
    else:
        image = data
        print(f"    already a raw image -> {len(image)} bytes")

    # An arm64 Image carries "ARM\x64" at offset 56. Checking it here turns a wrong-format kernel
    # into a clear message now, instead of an unexplained VM start failure later.
    if len(image) > 60 and image[56:60] not in (b'ARM\x64', b'ARMd'):
        if image[:4] not in (b'\x7fELF', b'MZ\x00\x00'):
            print("    warning: no arm64 Image magic found; the kernel may not boot",
                  file=sys.stderr)
else:
    image = data
    # A bzImage carries "HdrS" at 0x202 — the x86 boot-protocol header magic, and the one thing
    # worth asserting: it is what tells VZLinuxBootLoader this file speaks the protocol at all.
    if image[0x202:0x206] != b'HdrS':
        print("    warning: no bzImage 'HdrS' magic at 0x202; the kernel may not boot",
              file=sys.stderr)
    else:
        print(f"    bzImage, passed through unchanged -> {len(image)} bytes")

open(dst, 'wb').write(image)
KERNELPY

# ── initramfs ─────────────────────────────────────────────────────────────────
# The stock boot initramfs already carries busybox and, importantly, the fuse and virtiofs modules
# the guest needs to see a shared host directory. Reusing it means no module archaeology and no
# squashfs tooling; the only change is replacing its init with ours.
echo "==> building initramfs"
rm -rf "$work/ird"; mkdir -p "$work/ird"
(cd "$work/ird" && gzip -dc "$work/nb/boot/initramfs-virt" | cpio -idm --quiet 2>/dev/null) || true

[ -x "$work/ird/bin/busybox" ] || { echo "no busybox in the stock initramfs — cannot continue" >&2; exit 1; }
for module in fuse virtiofs; do
    find "$work/ird" -name "$module.ko*" | grep -q . \
        || echo "    warning: $module.ko not found; directory sharing will not work"
done

# ── x86-64 glibc runtime (BOTH architectures) ────────────────────────────────
# NOT an Apple-Silicon-only step, though it reads like one. The guest userland is Alpine, which is
# MUSL, and everything circuitRF runs in there — senior_worker itself, and the vendor model
# libraries it loads — is built against GLIBC and names /lib64/ld-linux-x86-64.so.2 as its ELF
# interpreter. Alpine has no such file on either architecture, so without this the worker does not
# start at all, with a message about a missing loader rather than anything to do with the model.
#
# The two hosts need it for slightly different reasons, and both need it:
#   Apple Silicon  Rosetta translates INSTRUCTIONS and supplies no libraries, and the native
#                  userland is arm64 musl besides.
#   Intel          the program is native x86-64, so no translation is involved — but musl is still
#                  not glibc.
base=$work/$(basename "$UBUNTU_BASE_AMD64_URL")
if [ -f "$base" ] && [ "$(shasum -a 256 "$base" | cut -d' ' -f1)" = "$UBUNTU_BASE_AMD64_SHA256" ]; then
    echo "==> using cached $(basename "$base")"
else
    echo "==> downloading x86-64 glibc runtime (~30 MB)"
    curl -fSL --retry 3 -o "$base" "$UBUNTU_BASE_AMD64_URL"
fi
got=$(shasum -a 256 "$base" | cut -d' ' -f1)
[ "$got" = "$UBUNTU_BASE_AMD64_SHA256" ] || {
    echo "checksum mismatch for $(basename "$base")" >&2
    echo "  expected $UBUNTU_BASE_AMD64_SHA256" >&2
    echo "  got      $got" >&2
    exit 1
}

# The whole of usr/lib/x86_64-linux-gnu goes in, not a hand-picked list. A device library's own
# dependencies are not knowable here, and a missing one fails at the dynamic loader with a bare
# symbol name — far more expensive to diagnose than a few tens of megabytes cost to carry.
echo "==> adding x86-64 glibc runtime"
mkdir -p "$work/ird/x86"
tar xzf "$base" -C "$work/ird/x86" usr/lib/x86_64-linux-gnu usr/lib64 2>/dev/null \
    || { echo "could not extract the x86-64 runtime" >&2; exit 1; }
[ -e "$work/ird/x86/usr/lib64/ld-linux-x86-64.so.2" ] \
    || { echo "no x86-64 dynamic loader in the extracted runtime" >&2; exit 1; }

cp "$here/guest-init" "$work/ird/init"
chmod +x "$work/ird/init"

(cd "$work/ird" && find . | sort | cpio -o -H newc --quiet 2>/dev/null | gzip -9 > "$out/crf-linux-initramfs.cpio.gz")

[ "$keep_work" = 1 ] || rm -rf "$work/nb" "$work/ird"

echo
echo "==> $out/crf-linux-kernel            ($(wc -c < "$out/crf-linux-kernel" | tr -d ' ') bytes)"
echo "==> $out/crf-linux-initramfs.cpio.gz ($(wc -c < "$out/crf-linux-initramfs.cpio.gz" | tr -d ' ') bytes)"
echo
echo "Put both beside crf-vmhost; it looks for them there by default."
