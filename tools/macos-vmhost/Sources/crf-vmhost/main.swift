// crf-vmhost — runs one Linux program inside a throwaway VM and wires it to this process's stdio.
//
// WHY THIS EXISTS
// ---------------
// Some device models ship as compiled Linux libraries with no macOS build. macOS cannot load a
// Linux ELF at all — that is a binary-format and OS-ABI mismatch, not an instruction-set one, so
// no amount of translation at the library level helps. A Linux VM is the only mechanism, and this
// program is how circuitRF supplies one itself instead of asking the user to install a container
// runtime first.
//
// WHAT IT IS NOT
// --------------
// Not a container runtime, not a general VM manager, and not aware of any particular model or
// vendor. It boots a kernel, mounts the directories it was told to, runs one argv, and exits when
// that argv exits. Everything specific lives in the caller's arguments.
//
// THE STDIO CONTRACT — the reason the design is this shape
// -------------------------------------------------------
// The caller speaks a binary framed protocol over the child's stdin/stdout. That rules out sharing
// one channel with the kernel console: boot chatter injected mid-frame desynchronises the stream
// and presents as corrupt numbers much later. So there are TWO virtio serial ports (verified
// accepted by Virtualization.framework before this was written, not assumed):
//
//   hvc0  kernel console + guest stderr  -> this process's stderr
//   hvc1  the guest program's stdin/stdout <-> this process's stdin/stdout, bytes untouched
//
// hvc1 is attached DIRECTLY to our own standard handles, so no byte is copied, buffered or framed
// by this program. A relay thread here would be a place for the protocol to break; there isn't one.

import Foundation
import Virtualization

// ── Diagnostics ───────────────────────────────────────────────────────────────

let programName = "crf-vmhost"

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("\(programName): \(message)\n".utf8))
    exit(2)
}

func note(_ message: String) {
    FileHandle.standardError.write(Data("\(programName): \(message)\n".utf8))
}

// ── Options ───────────────────────────────────────────────────────────────────

struct Share {
    let tag: String
    let path: String
    let readOnly: Bool

    /// Where the guest mounts it. Nil means `/mnt/<tag>`.
    ///
    /// <para>Setting it to the host path itself is what lets a program be handed an ordinary path on
    /// this Mac and open it inside the guest. That matters because a device model is told which data
    /// files to read through its OWN parameters, which are the kit's to write and arrive long after
    /// the VM has started — there is no command line left to rewrite them in. Mounting the tree
    /// where it already lives means nothing has to be rewritten at all.</para>
    let guestPath: String?
}

struct Options {
    var kernel: String?
    var initramfs: String?
    var shares: [Share] = []
    var cpus: Int = 2
    var memoryMB: UInt64 = 1024
    var rosetta = true
    var quiet = true
    var guestArgv: [String] = []
}

let usage = """
usage: \(programName) [options] -- <guest-program> [args...]

  --kernel PATH         Linux kernel image      (default: crf-linux-kernel beside this program)
  --initramfs PATH      initramfs cpio.gz       (default: crf-linux-initramfs.cpio.gz beside it)
  --share TAG=PATH[:ro] Share a host directory into the guest at /mnt/TAG. Repeatable.
  --share-at TAG=PATH[:ro]
                        Share a host directory and mount it in the guest at PATH — the same
                        absolute path it has here. Use this when the guest program will be told
                        about files by paths it did not get from this command line, so there is
                        nothing to rewrite them in. Repeatable.
  --cpus N              Virtual CPUs (default 2)
  --memory-mb N         Guest memory in MB (default 1024)
  --no-rosetta          Do not offer x86-64 translation (native x86-64 hosts do not need it)
  --verbose             Let the kernel log to stderr; otherwise it boots quiet

The guest program runs with stdin/stdout joined to this process's stdin/stdout as raw bytes,
and its stderr joined to this process's stderr.
"""

func parseOptions() -> Options {
    var o = Options()
    var args = Array(CommandLine.arguments.dropFirst())

    func next(_ flag: String) -> String {
        guard !args.isEmpty else { fail("\(flag) needs a value") }
        return args.removeFirst()
    }

    func number(_ text: String, _ flag: String) -> Int {
        guard let value = Int(text), value > 0 else { fail("\(flag) needs a positive number, got \"\(text)\"") }
        return value
    }

    while let arg = args.first {
        args.removeFirst()
        switch arg {
        case "--":            o.guestArgv = args; args = []
        case "-h", "--help":  print(usage); exit(0)
        case "--kernel":      o.kernel = next(arg)
        case "--initramfs":   o.initramfs = next(arg)
        case "--cpus":        o.cpus     = number(next(arg), arg)
        case "--memory-mb":   o.memoryMB = UInt64(number(next(arg), arg))
        case "--no-rosetta":  o.rosetta = false
        case "--verbose":     o.quiet = false
        case "--share", "--share-at":
            let spec = next(arg)
            guard let eq = spec.firstIndex(of: "=") else {
                fail("\(arg) wants TAG=PATH[:ro], got \"\(spec)\"")
            }
            let tag = String(spec[spec.startIndex..<eq])
            var path = String(spec[spec.index(after: eq)...])
            var readOnly = false
            if path.hasSuffix(":ro") { readOnly = true; path = String(path.dropLast(3)) }

            // Mounting somewhere relative would leave the guest resolving it against whatever its
            // working directory happens to be, which is not a property this contract has.
            if arg == "--share-at" && !path.hasPrefix("/") {
                fail("--share-at needs an absolute path to mount at, got \"\(path)\"")
            }

            o.shares.append(Share(tag: tag, path: path, readOnly: readOnly,
                                  guestPath: arg == "--share-at" ? path : nil))
        default:
            fail("unknown option \"\(arg)\" (use --help)")
        }
    }

    if o.guestArgv.isEmpty { fail("no guest program given — put it after --\n\n\(usage)") }
    return o
}

let options = parseOptions()

// ── Never outliving the caller ────────────────────────────────────────────────
//
// A leaked VM is not the stray-process nuisance a leaked ordinary child would be. macOS allows only
// a few virtual machines at once, and one left behind holds its slot INDEFINITELY: it waits for a
// request that can no longer arrive, because closing the pipe tells the GUEST nothing — a virtio
// console has no end-of-stream to deliver, so the guest blocks on a read forever and the VM never
// powers down. The next run then cannot start its VM and is killed by the system before it can say
// why, so the failure a user is shown contains no diagnostic at all and does not mention the leak.
// Measured, not imagined: one found still running 23 minutes after the application that started it
// had quit.
//
// Two triggers, because neither covers the other:
//
//   the parent exiting   covers a CRASH, or a kill -9, where none of the caller's own code runs.
//                        This is the one that makes the leak impossible rather than merely
//                        unlikely, and it is why cleanup cannot live in the caller alone.
//   stdin ending         the caller's own shutdown signal, and the only one available while it is
//                        still running — ending one worker without quitting the application.
//
// Exiting is enough to take the VM with it: the machine's state belongs to this process, so there
// is nothing left behind to unwind. Asking the guest to stop first would be politer and is
// deliberately not attempted — a guest wedged on a read cannot answer, and by here there is no one
// left to be polite to.

let farewellGate = NSLock()
var alreadyLeaving = false
var parentWatch: DispatchSourceProcess?

func leaveBecauseCallerIsGone(_ why: String) {
    farewellGate.lock()
    let first = !alreadyLeaving
    alreadyLeaving = true
    farewellGate.unlock()

    guard first else { return }   // both triggers can fire; the second must not race the exit

    note("the caller is gone (\(why)) — shutting the virtual machine down")
    exit(0)
}

let parentAtStart = getppid()

// A parent of 1 means this was started by launchd or already reparented, so there is nothing whose
// exit would mean anything. Watching it would fire immediately or never.
if parentAtStart > 1 {
    let watch = DispatchSource.makeProcessSource(
        identifier: parentAtStart,
        eventMask: .exit,
        queue: DispatchQueue(label: "com.circuitrf.vmhost.parent"))
    watch.setEventHandler { leaveBecauseCallerIsGone("its process exited") }
    watch.resume()
    parentWatch = watch
}

// The parent can die between reading its id and the watch being armed, in which case the source is
// registered against a process that is already gone and never fires. Re-reading closes that window:
// a reparented process reads 1.
if getppid() != parentAtStart { leaveBecauseCallerIsGone("its process exited") }

// ── Locating the bundled image ────────────────────────────────────────────────
//
// Defaulting to files beside this executable is what lets the caller name only the guest program:
// the kernel and initramfs are circuitRF's own build artifacts and travel with the binary, so a
// caller's manifest never has to know where they landed.

let toolDirectory = URL(fileURLWithPath: CommandLine.arguments[0])
    .resolvingSymlinksInPath()
    .deletingLastPathComponent()

func imageFile(_ explicit: String?, default name: String, what: String) -> URL {
    if let explicit { return URL(fileURLWithPath: explicit) }
    let beside = toolDirectory.appendingPathComponent(name)
    guard FileManager.default.fileExists(atPath: beside.path) else {
        fail("""
             no \(what) found.
             Looked beside this program: \(beside.path)
             Build one with tools/macos-vmimage/build-image.sh, or pass an explicit path.
             """)
    }
    return beside
}

let kernelURL    = imageFile(options.kernel,    default: "crf-linux-kernel",             what: "kernel")
let initramfsURL = imageFile(options.initramfs, default: "crf-linux-initramfs.cpio.gz",  what: "initramfs")

// ── Guest command, handed over on the kernel command line ─────────────────────
//
// Base64 of the argv joined by NUL. Encoding it keeps spaces, quotes and non-ASCII in paths intact
// through a channel that has no quoting rules of its own — a plain space-joined string would split
// a path with a space in it, which is ordinary on macOS.

let joined = options.guestArgv.joined(separator: "\u{0}")
let encodedArgv = Data(joined.utf8).base64EncodedString()

// The kernel command line is a fixed-size buffer in the guest; overflowing it truncates silently
// and the guest would exec something subtly wrong. Refuse instead, naming the real limit.
let commandLineBudget = 1800
if encodedArgv.count > commandLineBudget {
    fail("""
         guest command is too long to pass on the kernel command line \
         (\(encodedArgv.count) encoded bytes, limit \(commandLineBudget)).
         Shorten the paths, or share a directory and use a shorter path inside the guest.
         """)
}

// ── Configuration ─────────────────────────────────────────────────────────────

let configuration = VZVirtualMachineConfiguration()

let clampedMemory = min(max(options.memoryMB * 1024 * 1024,
                            VZVirtualMachineConfiguration.minimumAllowedMemorySize),
                        VZVirtualMachineConfiguration.maximumAllowedMemorySize)
let clampedCPUs = min(max(options.cpus,
                          VZVirtualMachineConfiguration.minimumAllowedCPUCount),
                      VZVirtualMachineConfiguration.maximumAllowedCPUCount)

configuration.cpuCount   = clampedCPUs
configuration.memorySize = clampedMemory

let bootLoader = VZLinuxBootLoader(kernelURL: kernelURL)
bootLoader.initialRamdiskURL = initramfsURL

var kernelArguments = [
    "console=hvc0",
    "crf.argv=\(encodedArgv)",
]
// Quiet by default so kernel chatter does not drown the guest program's own stderr, which is the
// output a user actually needs when something fails.
if options.quiet { kernelArguments.append(contentsOf: ["quiet", "loglevel=3"]) }
if options.rosetta { kernelArguments.append("crf.rosetta=1") }
for share in options.shares {
    guard let at = share.guestPath else {
        kernelArguments.append("crf.mount=\(share.tag)")
        continue
    }
    // Base64 for the same reason argv is: the kernel command line is space-separated with no
    // quoting of its own, and a mount point on macOS may perfectly well contain a space.
    kernelArguments.append("crf.mountat=\(share.tag),\(Data(at.utf8).base64EncodedString())")
}

let commandLine = kernelArguments.joined(separator: " ")

// The whole command line shares one fixed-size buffer, and overflowing it truncates SILENTLY — the
// guest would then miss a mount, or exec something subtly wrong, with nothing said about either.
// Checked here as well as for argv alone, because mount points are the part that can now grow.
let commandLineLimit = 3500
if commandLine.count > commandLineLimit {
    fail("""
         the kernel command line is too long (\(commandLine.count) bytes, limit \(commandLineLimit)).
         Share fewer directories, or share a shorter path higher up the tree.
         """)
}

bootLoader.commandLine = commandLine
configuration.bootLoader = bootLoader

// ── Serial ports: see the stdio contract at the top of this file ──────────────

func serialPort(reading: FileHandle, writing: FileHandle) -> VZVirtioConsoleDeviceSerialPortConfiguration {
    let port = VZVirtioConsoleDeviceSerialPortConfiguration()
    port.attachment = VZFileHandleSerialPortAttachment(fileHandleForReading: reading,
                                                       fileHandleForWriting: writing)
    return port
}

// THE READINESS RACE, and why stdin is not attached directly.
//
// A virtio console drops anything written to it before the guest has opened its end. The guest does
// not open /dev/hvc1 until it has booted, so input handed over at start — which is exactly when a
// caller sends its first request — is silently lost, and the caller then waits forever for a reply
// to a request the guest never saw. Measured, not theorised: attaching stdin directly made a
// round-trip test hang with an empty reply, while the guest-to-host direction was already perfect.
//
// So the guest announces itself on the console the moment before it execs, and this process holds
// the caller's bytes in the pipe until it sees that. The guest-to-host direction has no such
// problem and stays attached straight to our stdout — untouched, uncopied, unframed.
let guestReadyMarker = "\u{1}CRF-GUEST-READY\u{1}"

let consoleInput  = Pipe()   // never written; keeps the guest's console from seeing instant EOF
let consoleOutput = Pipe()   // guest console -> here -> our stderr, watched for the marker
let guestInput    = Pipe()   // here -> guest stdin, opened for business once the marker arrives

let consolePort = serialPort(reading: consoleInput.fileHandleForReading,
                             writing: consoleOutput.fileHandleForWriting)

let dataPort = serialPort(reading: guestInput.fileHandleForReading,
                          writing: FileHandle.standardOutput)

// Console relay: forward everything to stderr verbatim, and start pumping stdin the first time the
// marker appears. Forwarding verbatim matters — the console carries the guest program's own stderr,
// which is the diagnostic a user reads when a run fails.
let readyGate = DispatchSemaphore(value: 0)
var markerSeen = false
var consoleTail = ""

consoleOutput.fileHandleForReading.readabilityHandler = { handle in
    let data = handle.availableData
    guard !data.isEmpty else { handle.readabilityHandler = nil; return }
    FileHandle.standardError.write(data)

    if !markerSeen {
        // The marker can be split across reads, so match against a rolling tail rather than one chunk.
        consoleTail += String(decoding: data, as: UTF8.self)
        if consoleTail.contains(guestReadyMarker) {
            markerSeen = true
            consoleTail = ""
            readyGate.signal()
        } else if consoleTail.count > 4096 {
            consoleTail = String(consoleTail.suffix(guestReadyMarker.count))
        }
    }
}

// Stdin pump: blocks until the guest is ready, then copies until end of input. read(2) on our own
// stdin is left until after the gate opens, so nothing is consumed that could then be dropped.
Thread.detachNewThread {
    readyGate.wait()
    let input = FileHandle.standardInput
    while true {
        let chunk = input.availableData
        if chunk.isEmpty { break }
        guestInput.fileHandleForWriting.write(chunk)
    }
    try? guestInput.fileHandleForWriting.close()

    // End of input is the caller's shutdown signal, and closing the pipe is NOT enough on its own:
    // the guest is on a virtio console, which has no end-of-stream to pass on, so it would block on
    // a read forever and the VM would never power down. This is the same behaviour a worker running
    // natively on Windows or Linux already has for free — its read returns 0 and it exits — and the
    // console is the only reason macOS needs it spelled out.
    leaveBecauseCallerIsGone("it closed the connection")
}

configuration.serialPorts = [consolePort, dataPort]
configuration.entropyDevices = [VZVirtioEntropyDeviceConfiguration()]
configuration.memoryBalloonDevices = [VZVirtioTraditionalMemoryBalloonDeviceConfiguration()]

// ── Directory shares ──────────────────────────────────────────────────────────

var sharingDevices: [VZDirectorySharingDeviceConfiguration] = []

for share in options.shares {
    do { try VZVirtioFileSystemDeviceConfiguration.validateTag(share.tag) }
    catch { fail("share tag \"\(share.tag)\" is not usable: \(error.localizedDescription)") }

    var isDirectory: ObjCBool = false
    guard FileManager.default.fileExists(atPath: share.path, isDirectory: &isDirectory),
          isDirectory.boolValue else {
        fail("share \"\(share.tag)\" points at \(share.path), which is not a directory")
    }

    let device = VZVirtioFileSystemDeviceConfiguration(tag: share.tag)
    device.share = VZSingleDirectoryShare(
        directory: VZSharedDirectory(url: URL(fileURLWithPath: share.path), readOnly: share.readOnly))
    sharingDevices.append(device)
}

// ── Rosetta ───────────────────────────────────────────────────────────────────
//
// Only relevant when the host is Apple Silicon: it lets the arm64 guest run x86-64 Linux binaries.
// On an Intel host the guest is x86-64 already and needs nothing, so a missing Rosetta there is not
// an error. Everywhere else, an x86-64 program simply will not start without it, so the failure is
// reported here — at a point where the message can say what to do — rather than as "exec format
// error" from inside the guest.

#if arch(arm64)
if options.rosetta {
    switch VZLinuxRosettaDirectoryShare.availability {
    case .installed:
        do {
            let device = VZVirtioFileSystemDeviceConfiguration(tag: "rosetta")
            device.share = try VZLinuxRosettaDirectoryShare()
            sharingDevices.append(device)
        } catch {
            fail("Rosetta is installed but could not be shared with the guest: \(error.localizedDescription)")
        }
    case .notInstalled:
        fail("""
             Rosetta is required to run x86-64 Linux programs on this Mac and is not installed.
             Install it with:  softwareupdate --install-rosetta
             """)
    case .notSupported:
        fail("Rosetta is not supported on this Mac, so x86-64 Linux programs cannot run here.")
    @unknown default:
        fail("Rosetta reported an availability this program does not understand.")
    }
}
#endif

configuration.directorySharingDevices = sharingDevices

do { try configuration.validate() }
catch { fail("the virtual machine configuration was rejected: \(error.localizedDescription)") }

// ── Run ───────────────────────────────────────────────────────────────────────
//
// The guest's exit status cannot be read back through Virtualization.framework, so the contract is
// simpler than a process's: the VM stopping cleanly means the guest program finished and its output
// has been written. A guest that fails reports on stderr, which the caller already surfaces.

final class Lifecycle: NSObject, VZVirtualMachineDelegate {
    func guestDidStop(_ virtualMachine: VZVirtualMachine) {
        FileHandle.standardOutput.closeFile()
        exit(0)
    }

    func virtualMachine(_ virtualMachine: VZVirtualMachine, didStopWithError error: Error) {
        note("the virtual machine stopped unexpectedly: \(error.localizedDescription)")
        FileHandle.standardOutput.closeFile()
        exit(3)
    }
}

let queue      = DispatchQueue(label: "com.circuitrf.vmhost")
let lifecycle  = Lifecycle()
let machine    = VZVirtualMachine(configuration: configuration, queue: queue)

queue.async {
    machine.delegate = lifecycle
    machine.start { result in
        if case .failure(let error) = result {
            // Virtualization.framework's top-level message is almost always the useless "Internal
            // Virtualization error"; the actionable reason is in the underlying error, so report the
            // whole chain rather than the summary.
            let ns = error as NSError
            var detail = "\(ns.domain) \(ns.code): \(ns.localizedDescription)"
            if let reason = ns.localizedFailureReason { detail += "\n  reason: \(reason)" }
            if let underlying = ns.userInfo[NSUnderlyingErrorKey] as? NSError {
                detail += "\n  underlying: \(underlying.domain) \(underlying.code): \(underlying.localizedDescription)"
            }
            fail("the virtual machine did not start: \(detail)")
        }
    }
}

dispatchMain()
