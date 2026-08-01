# crf-vmhost — running Linux device workers on macOS

Some device models ship only as compiled Linux libraries. macOS cannot load a Linux ELF at all —
that is a binary-format and OS-ABI mismatch, not an instruction-set one, so nothing at the library
level bridges it and there is no Linux ABI personality on macOS to load it into. **A Linux VM is the
only mechanism.** These two tools are how circuitRF supplies one itself, so a user installs nothing.

```
tools/macos-vmhost/    crf-vmhost — boots a VM, runs one program, wires it to stdio
tools/macos-vmimage/   the kernel + initramfs it boots, built reproducibly from pinned sources
```

## Build — it happens on its own

On macOS, `dotnet build` / `dotnet run --project src/Ui` keeps this in step automatically and copies
all three artifacts next to the application. **It can only ever warn.** The VM host is optional —
circuitRF builds and runs without it — so a missing Swift toolchain, no network or a checksum
mismatch prints a message and the build still succeeds. A missing VM host must never be the reason
somebody cannot build the application.

The two artifacts are treated differently because they cost very differently:

| Artifact | Cost | When it builds automatically |
|---|---|---|
| `crf-vmhost` | seconds, no network | whenever its sources change |
| the Linux image | **~330 MB of downloads** | only when those downloads are already cached |

Downloading a third of a gigabyte because somebody typed `dotnet run` is not something to do
quietly, so the first time you need the image, ask for it explicitly — either is fine:

```sh
dotnet build src/Ui -p:CrfBuildVmImage=true        # allow the download as part of the build
tools/macos-vmimage/build-image.sh --out tools/macos-vmhost/build   # or build it directly
```

After that it is cached, and ordinary builds keep it current by themselves. Skip the whole thing
with `-p:CrfSkipVmHost=true`; use `--strict` (CI, release) to make failures fatal instead of silent.

### If you would rather do it by hand

```sh
tools/macos-vmhost/build.sh                                          # the host binary
tools/macos-vmimage/build-image.sh --out tools/macos-vmhost/build    # the kernel + initramfs
```

Requires the Xcode command line tools (`xcode-select --install`) and nothing else.
`crf-vmhost` looks for `crf-linux-kernel` and `crf-linux-initramfs.cpio.gz` beside itself, so a
caller only ever names the guest program.

## Can a contributor build the image from source?

**Yes, on a Mac, with nothing installed** — `build-image.sh` uses only tools macOS already ships
(`curl`, `tar`, `cpio`, `gzip`, `shasum`, `python3`). No Linux box, no container runtime, no
cross-compiler. Inputs are pinned by exact version and SHA-256 in `sources.lock`, so the same
circuitRF commit produces the same image, and that file is the one place to look to answer "what is
actually running inside the VM".

Nothing kit-specific or library-specific is in the image: no device library, no model data, no
worker. Those stay on the user's disk and are shared in read-only at run time. The image is generic
infrastructure and is identical for everybody.

## How it is wired in

The worker manifest names `crf-vmhost` as the command for `"platform": "osx"`. Nothing in the
circuitRF engine changes — per `DeviceWorkerManifest.MatchScore`, it is just another platform's
command:

```json
{ "platform": "osx",
  "command": "crf-vmhost",
  "arguments": ["--share", "kit=/path/to/kit:ro", "--",
                "/mnt/kit/worker", "/mnt/kit/model-library.so"] }
```

## Four things that are load-bearing, each found by measurement

1. **The kernel must be a raw uncompressed `Image`.** Stock arm64 kernels are EFI *zboot* images —
   a PE wrapper around a compressed kernel, unpacked by its own EFI stub. `VZLinuxBootLoader` runs
   no EFI stub, and hands back only `Internal Virtualization error` if given one.
   `build-image.sh` extracts the payload.

2. **Two serial ports, never one.** The worker protocol is binary-framed; kernel boot chatter
   sharing that channel desynchronises it and shows up as corrupt numbers much later. `hvc0` is the
   console (to stderr), `hvc1` is the data channel.

3. **A virtio console drops writes made before the guest opens its end.** Attaching stdin directly
   loses the caller's first request, and the caller then waits forever for a reply to a request the
   guest never saw. The guest emits a readiness marker on `hvc0` and the host holds input until it
   appears. The guest→host direction has no such problem and stays directly attached.

4. **A tty resets termios on LAST close.** Setting raw mode and then letting the program *reopen*
   `/dev/hvc1` silently discards the settings, and NUL comes back as `^@`. `guest-init` holds the fd
   open across both steps. This one presents as corrupt data rather than as a terminal setting,
   which is what makes it expensive to find.

## Status

Verified end to end on Apple Silicon (macOS 26.5, arm64), with only what circuitRF ships:

- the VM boots, and a host directory mounts read-only over virtio-fs;
- arbitrary bytes — NUL, `0x01`, `0xFF` — round-trip **exactly** through the data channel;
- a **statically** linked x86-64 program runs under Rosetta;
- a **dynamically** linked glibc x86-64 program runs under Rosetta against the image's own x86-64
  runtime, reports `x86_64`, and exits 0;
- **a real device worker loads a compiled device library**: the x86-64 worker starts under
  Rosetta, `dlopen`s the x86-64 Linux library, enumerates its device families and reports ready —
  from a plain `-- /mnt/<share>/<worker> /mnt/<share>/<library>.so` invocation, with no loader
  incantation anywhere in the caller's arguments.

**Not yet done.** Driving the worker through a full request/reply exchange from circuitRF itself —
everything above stops at "worker ready". Also open: circuitRF should resolve `crf-vmhost` from its
own tools directory (a bare command name currently falls through to `PATH`), the Intel checksum in
`sources.lock` is deliberately unset, and release packaging/notarization is untouched.

## Fifth thing that is load-bearing

**Rosetta translates instructions; it does not supply libraries.** A dynamically linked x86-64
program needs an x86-64 glibc present in the guest, and this guest's own userland is musl on arm64 —
so without the bundled runtime it dies at the dynamic loader, not at the translator. `guest-init`
puts that runtime where such a binary ALREADY looks — `/lib64/ld-linux-x86-64.so.2` is baked into
its ELF header as the interpreter — instead of detecting dynamic-vs-static and rewriting the command
with an explicit loader.

Detection was tried first and is a trap worth recording: busybox `grep` has no `-a`, so the test
silently reported every binary static, and Rosetta then failed with `failed to open elf at
/lib64/ld-linux-x86-64.so.2`. Making the path real needs no detection and works for static and
dynamic binaries alike.
