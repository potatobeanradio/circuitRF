// brief-em3d-26 R-em3d26-2 — Palace, run inside the user's Linux subsystem distribution.
//
// The run is STAGED in the distribution's own Linux filesystem, never on the Windows drive it mounts
// (em-3d.md §7.4): file access across that boundary is slow, and /mnt/c is where the failed build report
// in Palace's tracker was building. The Windows run directory stays the one series 1 defined and holds
// everything — the mesh, the configuration, the log, and every file Palace wrote that circuitRF reads — so
// a result does not depend on where it was computed (§7.3).
//
//   Windows run dir ── model.msh, config.json ──▶ ~/.circuitrf/runs/<run key>/   (the \\wsl.localhost\ share)
//                                                 Palace runs there, its output relayed on stdout
//   Windows run dir ◀── postpro: CSVs, palace.json ──                              (then the results are published)
//   Windows run dir ◀── postpro/paraview: the saved fields, one tar stream ──        (then the leg ends)
//
// The saved fields come over LAST, after the Touchstone and the .npy are written. On a large multi-port board they are
// gigabytes (137 MB per driven port per saved frequency on a 147k-tetrahedron mesh), and copied file by file through the
// \\wsl.localhost share they once held an 8-port result back for 23 minutes after Palace had finished, under a progress
// bar that still said "solving". They travel as one tar stream on wsl.exe's standard output; the share copy is the
// fallback when that stream fails or delivers anything other than every file at its own size.
//
// The Linux run directory is circuitRF's scratch space and is removed when the leg ends.

using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>R-em3d26-2a — what one subsystem run copies, and where it runs.</summary>
/// <param name="LinuxRunDirectory">The staging directory inside the distribution.</param>
/// <param name="CopyIn">The run directory's files that go in, by name: the mesh and the configuration, only.</param>
/// <param name="PidFile">Where the process-group wrapper writes the group id.</param>
public sealed record WslStagingPlan(string LinuxRunDirectory, IReadOnlyList<string> CopyIn, string PidFile);

/// <summary>Palace in a Linux subsystem distribution (brief-em3d-26).</summary>
internal sealed class WslPalaceRunner(WslSession session, string linuxHome, string? mpirun, string mpiHow) : IPalaceRunner
{
    /// <summary>The runs directory under the distribution's home.</summary>
    public const string RunsDirectory = ".circuitrf/runs";

    /// <summary>The wrapper's pid file, in the staging directory.</summary>
    public const string PidFile = "circuitrf.pid";

    /// <summary>The stage the results' copy runs under.</summary>
    public const string ResultsCopyLabel = "copying the results out of the Linux subsystem";

    /// <summary>The stage the saved fields' copy runs under, counted in MB.</summary>
    public const string FieldsCopyLabel = "copying the saved fields out of the Linux subsystem";

    private readonly HashSet<string> _staged = new(StringComparer.Ordinal);

    /// <summary>Run directories whose saved fields are still in their staging directory, by Windows run directory.</summary>
    private readonly Dictionary<string, WslStagingPlan> _fieldsPending = new(StringComparer.Ordinal);

    /// <summary>The MPI launcher inside the distribution, and how it was found.</summary>
    public string? MpiLauncher => mpirun;

    /// <summary>The distribution the run is in.</summary>
    public string Distribution => session.Distribution;

    public PhysicalCoreReading Cores => session.Cores();

    /// <summary>The run key: a hash of the Windows run directory, so two setups never share a staging directory.</summary>
    public static string RunKey(string windowsRunDir)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(windowsRunDir).TrimEnd('\\', '/'))))[..16];

    /// <summary>The staging plan for <paramref name="windowsRunDir"/>. Pure, which is what gate 4 reads.</summary>
    public static WslStagingPlan Plan(string linuxHome, string windowsRunDir)
    {
        string dir = WslPaths.Combine(linuxHome, RunsDirectory, RunKey(windowsRunDir));
        return new(dir, [GmshGeoWriter.MeshFile, PalaceConfigWriter.ConfigFile], WslPaths.Combine(dir, PidFile));
    }

    /// <summary>
    /// R-em3d26-2a step 4 — which of Palace's output files come back BEFORE the results are read, by their path under
    /// <c>postpro/</c>: every CSV and <c>palace.json</c> (top level and each adaptive pass's), which the completion summary
    /// reads the element counts from. Nothing under a field directory — those come after the results are published
    /// (<see cref="BringFields"/>) — and nothing else: a mesh archive per refinement pass is Palace's working state.
    /// </summary>
    public static IReadOnlyList<string> CopyOut(IEnumerable<string> postproFiles, IReadOnlyList<string> fieldDirectories)
        => postproFiles.Select(f => f.Replace('\\', '/'))
                       .Where(f => (f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || WslPaths.Name(f) == "palace.json")
                                   && !UnderAny(f, fieldDirectories))
                       .Order(StringComparer.Ordinal).ToList();

    private static bool UnderAny(string relative, IReadOnlyList<string> directories)
        => directories.Any(d => relative.StartsWith(d.TrimEnd('/') + "/", StringComparison.Ordinal));

    /// <summary>The field directories under <c>postpro/</c> the setup asked Palace to write
    /// (<see cref="PalaceConfigWriter.FieldDirectories"/>). Empty copies no field back — which is what every run did
    /// until designer feedback round 11, because nothing set it: the Field plot then said the run saved none.</summary>
    public IReadOnlyList<string> FieldDirectories { get; init; } = [];

    /// <summary>The grace period between TERM and KILL (a seam for the gate).</summary>
    internal TimeSpan Grace { get; init; } = WslProcessGroup.Grace;

    public PalaceStep Solve(string runDir, string configJson, string palace, int processes, RunControl? control,
                            CancellationToken ct, out string? note, PalaceStageTracker? tracker, long physicalBytes,
                            ProcessLimits? limits = null)
    {
        note = null;
        File.WriteAllText(Path.Combine(runDir, PalaceConfigWriter.ConfigFile), configJson, new UTF8Encoding(false));
        // R-em3d7-4e — nothing from an earlier run may be read as this one's.
        string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        if (Directory.Exists(post)) Directory.Delete(post, recursive: true);

        var plan = Plan(linuxHome, runDir);
        if (Stage(runDir, plan) is { } stageFailure) return new(false, false, false, stageFailure);

        if (processes > 1 && mpirun is null)
            note = $"Palace ran as one process: {processes} were asked for, and {mpiHow}.";
        var (exe, args) = PalaceRun.PalaceCommand(palace, processes, processes > 1 ? mpirun : null);

        if (tracker is not null) tracker.Begin();
        else control?.BeginStage("solving (Palace, in the Linux subsystem)", 0, "lines");
        var run = Start(plan, plan.LinuxRunDirectory, Path.Combine(runDir, PalaceRun.PalaceLogFile),
                        [exe, .. args], control, ct, PalaceRun.Watch(limits, tracker is null ? null : tracker.Line, 0, runDir));
        if (run.Cancelled) return new(false, true, false, null);
        if (run.Stopped is { } stopped) return new(false, false, false, null) { Stopped = stopped };
        if (run.StartFailure is { } why) return new(false, false, false, $"Palace could not be started in the Linux subsystem ({why}).");
        if (run.ExitCode != 0)
            return new(false, false, false,
                (PalaceRun.KilledSentence(run.ExitCode, run.Tail, session.MemoryBytes(), wsl: true) is { } killed
                    ? $"In the Linux subsystem distribution '{session.Distribution}': {killed} "
                    : $"Palace failed in the Linux subsystem distribution '{session.Distribution}' (exit code {run.ExitCode}). " +
                      $"Its own words: {Quote(run.Tail)} ") +
                $"The full log is {Path.Combine(runDir, PalaceRun.PalaceLogFile)}.");

        control?.BeginStage(ResultsCopyLabel);
        if (CopyBack(runDir, plan) is { } copyFailure) return new(false, false, false, copyFailure);
        if (FieldDirectories.Any(d => Directory.Exists(session.ToWindows(WslPaths.Combine(plan.LinuxRunDirectory,
                                          PalaceConfigWriter.OutputDirectory, d)))))
            _fieldsPending[runDir] = plan;
        return PalaceStep.Done();
    }

    public bool FieldsPending(string runDir) => _fieldsPending.ContainsKey(runDir);

    /// <summary>
    /// The saved fields, from the staging directory into the Windows run directory's <c>postpro/</c>: one tar stream on
    /// wsl.exe's standard output, checked file by file against the share's listing, and copied through the share instead
    /// when that stream fails. Counted in MB on <see cref="FieldsCopyLabel"/>. Whatever goes wrong, the results are
    /// already written: a field copy that does not finish leaves no partial field folder and says so as a warning.
    /// </summary>
    public PalaceFieldCopy BringFields(string runDir, RunControl? control, CancellationToken ct)
    {
        if (!_fieldsPending.Remove(runDir, out var plan)) return PalaceFieldCopy.None;
        string linuxPost = WslPaths.Combine(plan.LinuxRunDirectory, PalaceConfigWriter.OutputDirectory);
        string from = session.ToWindows(linuxPost);
        string to = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        const string Lost = "The results are written; the 3D view has no field to plot from this run.";
        List<(string Relative, long Length)> manifest;
        try { manifest = FieldManifest(from, FieldDirectories); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, $"The saved fields could not be listed in the Linux subsystem ({e.Message}). {Lost}");
        }
        if (manifest.Count == 0) return PalaceFieldCopy.None;
        var dirs = FieldDirectories.Where(d => manifest.Any(m => UnderAny(m.Relative, [d]))).ToList();
        long total = manifest.Sum(m => m.Length);
        var progress = new CopyProgress(control, total);
        var clock = Stopwatch.StartNew();
        try
        {
            control?.BeginStage(FieldsCopyLabel, progress.TotalMb, "MB");
            string? streamFailure = StreamFields(linuxPost, to, dirs, progress, ct) ?? Mismatch(to, manifest);
            string how = "as one stream";
            if (streamFailure is not null)
            {
                RemoveFields(to, dirs);
                progress.Restart();
                control?.BeginStage(FieldsCopyLabel, progress.TotalMb, "MB");
                CopyFiles(from, to, manifest, progress, ct);
                if (Mismatch(to, manifest) is { } still) throw new IOException(still);
                how = $"file by file, because the single stream failed ({streamFailure.TrimEnd('.')})";
            }
            progress.Finish();
            return new($"The saved fields ({MachineMemory.Format(total)} in {manifest.Count} files) were copied out of the Linux " +
                       $"subsystem in {PalaceRun.Took(clock.Elapsed)}, {how}.", null);
        }
        catch (OperationCanceledException)
        {
            RemoveFields(to, dirs);
            return new(null, $"The saved fields were not copied out of the Linux subsystem: the run was stopped while they were. {Lost}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RemoveFields(to, dirs);
            return new(null, $"The saved fields could not be copied out of the Linux subsystem ({e.Message}). {Lost}");
        }
    }

    /// <summary>Every file under the field directories, by its path under <c>postpro/</c> and its size, read through the share.</summary>
    private static List<(string Relative, long Length)> FieldManifest(string fromPost, IReadOnlyList<string> fieldDirectories)
    {
        var files = new List<(string, long)>();
        foreach (string d in fieldDirectories)
        {
            var dir = new DirectoryInfo(Path.Combine(fromPost, d.Replace('/', Path.DirectorySeparatorChar)));
            if (!dir.Exists) continue;
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                files.Add((Path.GetRelativePath(fromPost, f.FullName).Replace('\\', '/'), f.Length));
        }
        return files;
    }

    /// <summary>The first file of <paramref name="manifest"/> that is not in <paramref name="toPost"/> at its own size, in a
    /// phrase; null when every one is.</summary>
    internal static string? Mismatch(string toPost, IReadOnlyList<(string Relative, long Length)> manifest)
    {
        foreach (var (rel, length) in manifest)
        {
            var f = new FileInfo(Path.Combine(toPost, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!f.Exists) return $"{rel} did not arrive";
            if (f.Length != length) return $"{rel} arrived as {f.Length:N0} bytes of {length:N0}";
        }
        return null;
    }

    /// <summary>
    /// <c>tar -cf - &lt;field directories&gt;</c> in the staging <c>postpro/</c>, read off wsl.exe's standard output and
    /// unpacked into <paramref name="toPost"/>. Null when tar exited 0 having sent a well-formed archive; otherwise why not.
    /// An entry outside the field directories is refused rather than written.
    /// </summary>
    private string? StreamFields(string linuxPost, string toPost, IReadOnlyList<string> dirs, CopyProgress progress, CancellationToken ct)
    {
        var psi = session.Wsl.StartInfo(session.Arguments(["tar", "-cf", "-", "--", .. dirs], linuxPost));
        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return e.Message; }
        if (p is null) return "wsl.exe did not start";
        using (p)
        {
            void Kill()
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
            if (psi.RedirectStandardInput) p.StandardInput.Close();
            // As bytes: wsl.exe's own messages are UTF-16LE and tar's are UTF-8 relayed through it (WslText.Decode decides).
            var errBytes = new MemoryStream();
            var stderr = psi.RedirectStandardError ? p.StandardError.BaseStream.CopyToAsync(errBytes) : Task.CompletedTask;
            using var stop = ct.Register(Kill);
            string root = Path.GetFullPath(toPost).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            try
            {
                using var reader = new TarReader(new CountingStream(p.StandardOutput.BaseStream, progress.Add), leaveOpen: true);
                while (reader.GetNextEntry() is { } entry)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = entry.Name.Replace('\\', '/');
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                    string dest = Path.GetFullPath(Path.Combine(toPost, name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
                    bool inside = dirs.Any(d => name.TrimEnd('/') == d.TrimEnd('/') || UnderAny(name, [d]));
                    if (!inside || !dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        Kill();
                        return $"the stream held '{entry.Name}', which is not under the field folders";
                    }
                    switch (entry.EntryType)
                    {
                        case TarEntryType.Directory:
                            Directory.CreateDirectory(dest);
                            break;
                        case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            entry.ExtractToFile(dest, overwrite: true);
                            break;
                        // A link or a device: Palace writes neither, and the manifest check finds anything that should have been a file.
                    }
                }
                // The reader stops at the end-of-archive marker, but tar pads its output to a whole record (10 KB by default)
                // and a Windows pipe holds about 4 KB: left unread, that padding blocks wsl.exe on its write and it never exits.
                p.StandardOutput.BaseStream.CopyTo(Stream.Null);
            }
            catch (OperationCanceledException) { Kill(); throw; }
            catch (Exception e) when (e is InvalidDataException or IOException or FormatException or ArgumentException
                                          or UnauthorizedAccessException)
            {
                if (ct.IsCancellationRequested) { Kill(); ct.ThrowIfCancellationRequested(); }
                // A tar (or wsl.exe) that failed is a better reason than what the reader made of its truncated output.
                if (p.WaitForExit(TimeSpan.FromSeconds(2)) && p.ExitCode != 0) return Exited();
                Kill();
                return e.Message;
            }
            p.WaitForExit();
            ct.ThrowIfCancellationRequested();
            return p.ExitCode != 0 ? Exited() : null;

            string Exited()
            {
                bool read;
                try { read = stderr.Wait(TimeSpan.FromSeconds(5)); }
                catch (AggregateException) { read = false; }
                string said = read ? WslText.Decode(errBytes.ToArray()).Trim() : "";
                return $"tar exited {p.ExitCode}" + (said.Length > 0 ? $": {said.Split('\n')[^1].Trim()}" : "");
            }
        }
    }

    /// <summary>The share copy: each file of <paramref name="manifest"/>, counted as it goes.</summary>
    private static void CopyFiles(string fromPost, string toPost, IReadOnlyList<(string Relative, long Length)> manifest,
                                  CopyProgress progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        foreach (var (rel, _) in manifest)
        {
            ct.ThrowIfCancellationRequested();
            string native = rel.Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.Combine(toPost, native);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = new FileStream(Path.Combine(fromPost, native), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                dst.Write(buffer, 0, n);
                progress.Add(n);
            }
        }
    }

    /// <summary>A field copy that did not finish leaves nothing behind: a partial folder would read as a run's fields.</summary>
    private static void RemoveFields(string toPost, IReadOnlyList<string> dirs)
    {
        foreach (string d in dirs)
            try
            {
                string path = Path.Combine(toPost, d.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A copy's bytes, on its stage in whole MB, never past the stage's own total.</summary>
    private sealed class CopyProgress(RunControl? control, long totalBytes)
    {
        private long _bytes, _mb;

        public long TotalMb { get; } = Math.Max(1, (totalBytes + (1L << 20) - 1) >> 20);

        public void Restart() => (_bytes, _mb) = (0, 0);

        /// <summary>The stage at its total: the last part-MB, which whole-MB counting never reaches by itself.</summary>
        public void Finish() => Add(Math.Max(0, (TotalMb << 20) - _bytes));

        public void Add(long bytes)
        {
            _bytes += bytes;
            long mb = Math.Min(TotalMb, _bytes >> 20);
            if (mb <= _mb) return;
            control?.TickStage(mb - _mb);
            _mb = mb;
        }
    }

    /// <summary>A read-only stream that reports each read's byte count.</summary>
    private sealed class CountingStream(Stream inner, Action<long> read) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

        private int Counted(int n)
        {
            if (n > 0) read(n);
            return n;
        }
    }

    public IReadOnlyList<PalaceWaveMode>? SecondModes(string runDir, string configJson, string palace, double topHz,
                                                      IReadOnlyList<int> wavePorts, CancellationToken ct, out string? note)
    {
        var plan = Plan(linuxHome, runDir);
        return PalaceRun.SecondModes(runDir, configJson, topHz, wavePorts, out note, (dir, watch) =>
        {
            // The mode-2 configuration names ../model.msh, which the staging directory already holds.
            if (Stage(runDir, plan) is { } failure) return new PalaceRun.ProcessRun(-1, false, failure, []);
            string linuxDir = WslPaths.Combine(plan.LinuxRunDirectory, PalaceRun.SecondModeDirectory);
            var mkdir = session.Exec(["mkdir", "-p", linuxDir]);
            if (!mkdir.Ok) return new PalaceRun.ProcessRun(-1, false, mkdir.Failure ?? mkdir.Message, []);
            try
            {
                File.Copy(Path.Combine(dir, PalaceConfigWriter.ConfigFile),
                          session.ToWindows(WslPaths.Combine(linuxDir, PalaceConfigWriter.ConfigFile)), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new PalaceRun.ProcessRun(-1, false, e.Message, []);
            }
            bool bare = palace.EndsWith(".bin", StringComparison.Ordinal);
            return Start(plan, linuxDir, Path.Combine(dir, PalaceRun.PalaceLogFile),
                         bare ? [palace, PalaceConfigWriter.ConfigFile] : [palace, "--serial", PalaceConfigWriter.ConfigFile],
                         null, ct, watch);
        });
    }

    public IReadOnlyList<System.Numerics.Complex>? FaceModes(string runDir, string configJson, string palace, int attribute, int count,
                                                             double fHz, CancellationToken ct, out string? note)
    {
        var plan = Plan(linuxHome, runDir);
        return PalaceRun.FaceModes(runDir, configJson, attribute, count, fHz, out note, dir =>
        {
            // The face-mode configuration names ../model.msh, which the staging directory already holds; Palace's log is
            // written on this side, which is all that is read back.
            if (Stage(runDir, plan) is { } failure) return new PalaceRun.ProcessRun(-1, false, failure, []);
            string linuxDir = WslPaths.Combine(plan.LinuxRunDirectory, PalaceRun.FaceModeDirectory);
            var mkdir = session.Exec(["mkdir", "-p", linuxDir]);
            if (!mkdir.Ok) return new PalaceRun.ProcessRun(-1, false, mkdir.Failure ?? mkdir.Message, []);
            try
            {
                File.Copy(Path.Combine(dir, PalaceConfigWriter.ConfigFile),
                          session.ToWindows(WslPaths.Combine(linuxDir, PalaceConfigWriter.ConfigFile)), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new PalaceRun.ProcessRun(-1, false, e.Message, []);
            }
            bool bare = palace.EndsWith(".bin", StringComparison.Ordinal);
            return Start(plan, linuxDir, Path.Combine(dir, PalaceRun.PalaceLogFile),
                         bare ? [palace, PalaceConfigWriter.ConfigFile] : [palace, "--serial", PalaceConfigWriter.ConfigFile],
                         null, ct, PalaceRun.Watch(null, null, 0, dir));
        });
    }

    /// <summary>A fresh staging directory holding exactly the plan's copy-in files. Once per run directory
    /// per leg: the second-mode check reuses what the solve staged.</summary>
    private string? Stage(string runDir, WslStagingPlan plan)
    {
        if (_staged.Contains(plan.LinuxRunDirectory)) return null;
        session.RemoveTree(plan.LinuxRunDirectory);
        var mkdir = session.Exec(["mkdir", "-p", plan.LinuxRunDirectory]);
        if (!mkdir.Ok)
            return $"The run could not be staged in the Linux subsystem distribution '{session.Distribution}' " +
                   $"({mkdir.Failure ?? mkdir.Message}).";
        try
        {
            foreach (string file in plan.CopyIn)
                File.Copy(Path.Combine(runDir, file), session.ToWindows(WslPaths.Combine(plan.LinuxRunDirectory, file)), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The mesh and configuration could not be copied into the Linux subsystem distribution " +
                   $"'{session.Distribution}' ({e.Message}).";
        }
        _staged.Add(plan.LinuxRunDirectory);
        return null;
    }

    /// <summary>Palace's result files (<see cref="CopyOut"/>), copied back into the Windows run directory's <c>postpro/</c>.</summary>
    private string? CopyBack(string runDir, WslStagingPlan plan)
    {
        string from = session.ToWindows(WslPaths.Combine(plan.LinuxRunDirectory, PalaceConfigWriter.OutputDirectory));
        try
        {
            if (!Directory.Exists(from)) return null;   // ReadPortS then names the missing file, as natively
            var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                                 .Select(f => Path.GetRelativePath(from, f)).ToList();
            foreach (string rel in CopyOut(files, FieldDirectories))
            {
                string dest = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(from, rel.Replace('/', Path.DirectorySeparatorChar)), dest, overwrite: true);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Palace finished in the Linux subsystem, but its results could not be copied back ({e.Message}).";
        }
    }

    /// <summary>One Palace process inside the distribution, as a process-group leader, streamed through
    /// the run service's own reader; cancelling stops the group, then <c>wsl.exe</c>.</summary>
    private PalaceRun.ProcessRun Start(WslStagingPlan plan, string linuxCwd, string logPath, IReadOnlyList<string> argv,
                                       RunControl? control, CancellationToken ct, ProcessWatch watch)
    {
        string pidFile = WslPaths.Combine(linuxCwd, PidFile);
        var psi = session.Wsl.StartInfo(session.Arguments(
            WslProcessGroup.Wrap(pidFile, ["env", "OMP_NUM_THREADS=1", .. argv]), linuxCwd));
        PalaceRun.CountPalace();
        return PalaceRun.RunProcess(psi, logPath, Em3dProcessKind.Solver, control, ct, watch,
                                    kill: _ => WslProcessGroup.Stop(session, pidFile, Grace), sampleMemory: false);
    }

    private static string Quote(IReadOnlyList<string> tail) => PalaceRun.Quote(tail, "rror");

    /// <summary>The Linux run directories this leg staged are circuitRF's scratch space, and go with it.</summary>
    public void Dispose()
    {
        foreach (string dir in _staged)
            try { session.RemoveTree(dir); } catch (ArgumentException) { }
        _staged.Clear();
        _fieldsPending.Clear();
    }
}
