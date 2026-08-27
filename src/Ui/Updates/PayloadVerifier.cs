using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Updates;

/// <summary>What verification concluded. Anything but <see cref="Ok"/> discards and blacklists.</summary>
public enum VerifyOutcome
{
    Ok,

    /// <summary>The bytes are not the bytes the feed said they were.</summary>
    HashMismatch,

    /// <summary>The payload is not validly signed, or its seal is broken.</summary>
    SignatureInvalid,

    /// <summary>Validly signed by <b>someone else</b>. This is the check that actually matters.</summary>
    IdentityMismatch,

    /// <summary>No signing infrastructure on this platform to check against — Linux.</summary>
    NotApplicable,
}

/// <summary>The verdict, with enough detail for a log line and none for a dialog.</summary>
public sealed record VerifyResult(VerifyOutcome Outcome, string Detail)
{
    public bool Ok => Outcome is VerifyOutcome.Ok or VerifyOutcome.NotApplicable;
}

/// <summary>
/// Three checks, in order, before anything is staged — and the last one is the only one that is a
/// security boundary.
///
/// <list type="number">
/// <item><b>Hash</b>, against the feed's digest when it published one. Best-effort.</item>
/// <item><b>Code signature.</b> macOS: <c>codesign --verify --strict</c>. Windows: Authenticode.</item>
/// <item><b>Publisher identity.</b> The staged bundle's Team ID equals the running application's.</item>
/// </list>
///
/// <para>Steps 1 and 2 establish that the bytes are the bytes the host served. <b>Only step 3
/// establishes that WE produced them</b> — it is what survives a mis-issued certificate, a
/// compromised release, or a mistake in the naming convention that points the updater at the wrong
/// file. It is also the reason the host is trusted for availability and never for integrity, which
/// is what makes moving off GitHub later a small change rather than a security re-analysis.</para>
///
/// <para>Any failure: delete the staging directory, record the version in a local blacklist so it is
/// not retried in a loop, and <b>say nothing</b>.</para>
/// </summary>
public sealed class PayloadVerifier
{
    /// <summary>Computes the SHA-256 of a file as lower-case hex.</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using FileStream fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Step 1. Absent digest ⇒ <see cref="VerifyOutcome.Ok"/>, not a refusal: the code signature is
    /// the guarantee, and refusing every release that predates GitHub's <c>digest</c> field would
    /// simply stop updates.
    /// </summary>
    public static async Task<VerifyResult> VerifyHashAsync(string file, string? expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return new VerifyResult(VerifyOutcome.Ok, "no digest published");

        string actual = await Sha256Async(file, ct).ConfigureAwait(false);

        return string.Equals(actual, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal)
            ? new VerifyResult(VerifyOutcome.Ok, "sha256 matched")
            : new VerifyResult(VerifyOutcome.HashMismatch, "sha256 did not match the published digest");
    }

    /// <summary>
    /// Steps 2 and 3 for the downloaded <c>.dmg</c>, <b>before it is mounted</b>.
    ///
    /// <para><c>hdiutil attach</c> hands attacker-supplied bytes to the kernel's HFS+/APFS parsers,
    /// and until this check existed it did so on a payload nothing had verified — the bundle's own
    /// seal was only examined after the image was already mounted and copied out (found in the
    /// security review, 2026-08-25). A disk image is the one payload format where "unpack it and
    /// then look at it" runs privileged code on the attacker's data first.</para>
    ///
    /// <para>It costs nothing to close because <c>build-macos.sh</c> already signs the image with the
    /// same Developer ID identity as the bundle inside it and staples the notarisation ticket to it
    /// — so the container carries exactly the identity the bundle does, and an image that does not
    /// is not one of ours whatever it contains. The bundle check still runs afterwards: this proves
    /// the container, that one proves the contents.</para>
    /// </summary>
    public static async Task<VerifyResult> VerifyMacImageAsync(
        string dmgPath, string runningBundle, CancellationToken ct)
    {
        ProcessResult verify = await ProcessRunner.RunAsync(
            "codesign", ["--verify", "--strict", dmgPath], ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        if (!verify.Ok)
            return new VerifyResult(VerifyOutcome.SignatureInvalid, "disk image: " + Trim(verify.StdErr));

        string? image   = await TeamIdAsync(dmgPath, ct).ConfigureAwait(false);
        string? running = await TeamIdAsync(runningBundle, ct).ConfigureAwait(false);

        if (image is null)
            return new VerifyResult(VerifyOutcome.SignatureInvalid, "the disk image has no Team ID");

        if (running is null)
            return new VerifyResult(VerifyOutcome.IdentityMismatch,
                                    "the running application is not Developer ID signed");

        return string.Equals(image, running, StringComparison.Ordinal)
            ? new VerifyResult(VerifyOutcome.Ok, $"disk image Team ID {image}")
            : new VerifyResult(VerifyOutcome.IdentityMismatch,
                               $"disk image Team ID {image} is not the running {running}");
    }

    /// <summary>
    /// Steps 2 and 3 for a staged macOS bundle: the seal is intact, and the Team ID is ours.
    ///
    /// <para><b>The identity of the RUNNING application is read, not verified.</b> circuitRF writes
    /// <c>__pycache__</c> into its own bundle when a PCell generator runs, which breaks the installed
    /// copy's seal — measured on a real Developer ID install, 2026-08-25. So
    /// <c>codesign --verify</c> on the running app would fail for a reason that has nothing to do
    /// with the update, and would disable updates on every machine that has ever opened a kit. The
    /// staged bundle, which is fresh from the image, is what gets verified.</para>
    /// </summary>
    public static async Task<VerifyResult> VerifyMacBundleAsync(
        string stagedBundle, string runningBundle, CancellationToken ct)
    {
        ProcessResult verify = await ProcessRunner.RunAsync(
            "codesign", ["--verify", "--strict", "--deep", stagedBundle], ct, TimeSpan.FromMinutes(3))
            .ConfigureAwait(false);

        if (!verify.Ok)
            return new VerifyResult(VerifyOutcome.SignatureInvalid, Trim(verify.StdErr));

        string? staged  = await TeamIdAsync(stagedBundle, ct).ConfigureAwait(false);
        string? running = await TeamIdAsync(runningBundle, ct).ConfigureAwait(false);

        if (staged is null)
            return new VerifyResult(VerifyOutcome.SignatureInvalid, "the staged bundle has no Team ID");

        // An AD-HOC running build has no Team ID at all, and one cannot silently self-update even in
        // principle (design §4.2) — so there is nothing to compare against and nothing is staged.
        if (running is null)
            return new VerifyResult(VerifyOutcome.IdentityMismatch,
                                    "the running application is not Developer ID signed");

        return string.Equals(staged, running, StringComparison.Ordinal)
            ? new VerifyResult(VerifyOutcome.Ok, $"Team ID {staged}")
            : new VerifyResult(VerifyOutcome.IdentityMismatch,
                               $"staged Team ID {staged} is not the running {running}");
    }

    /// <summary>
    /// Whether this build has a publisher identity at all — the thing R-AU-25's third step compares
    /// the payload AGAINST. False for an ad-hoc macOS bundle and an unsigned Windows publish, which
    /// is to say for every developer build.
    ///
    /// <para><b>Asked before the feed, not after the unpack.</b> The answer cannot change with the
    /// release, so deferring it only bought a full payload fetched and discarded on a 24-hour timer,
    /// plus a blacklist entry against a release that was never at fault — in a state file shared with
    /// the real installation on the same machine, since <c>AppDataRoot</c> is one directory for all
    /// three applications and every build of them.</para>
    ///
    /// <para>Linux has no signing infrastructure to ask, so it answers true and the hash and TLS are
    /// what there is, exactly as <c>VerifyStagedAsync</c> already concludes.</para>
    ///
    /// <para><b>Windows accepts a release key in place of Authenticode</b> (design §15.5). An
    /// unsigned Windows publish has no publisher to compare a payload against, but a build carrying
    /// a compiled-in release key does not need one: the signed manifest names the payload's SHA-256,
    /// and the key that signed it is on no host. That anchor is the STRONGER of the two here — it
    /// covers every byte, where Authenticode covers only the PEs and therefore not
    /// <c>pcell-python/**/*.py</c>, which circuitRF executes (§9.1). Where the build IS signed both
    /// checks still run; this only stops the ABSENCE of a certificate being fatal.</para>
    ///
    /// <para><b>macOS is deliberately not given the same relaxation.</b> Its builds are Developer ID
    /// signed already, so accepting a key instead would trade an anchor away for nothing.</para>
    /// </summary>
    public static async Task<bool> RunningBuildCanAcceptUpdatesAsync(
        InstallSite site, CancellationToken ct, ReleaseTrust? trust = null)
    {
        trust ??= ReleaseTrust.Compiled;

        try
        {
            if (site.Shape == InstallShape.MacOsBundle)
                return await TeamIdAsync(site.Root, ct).ConfigureAwait(false) is not null;

            if (OperatingSystem.IsWindows())
                return trust.RequireSignedManifest || RunningWindowsPublisher() is not null;

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Reads a bundle's Team ID out of its signature. Null when it has none.</summary>
    public static async Task<string?> TeamIdAsync(string bundle, CancellationToken ct)
    {
        ProcessResult r = await ProcessRunner.RunAsync(
            "codesign", ["-dv", "--verbose=2", bundle], ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);

        // codesign writes its description to STDERR, which is easy to get wrong and produces a
        // silent "no Team ID" rather than an error.
        foreach (string line in (r.StdErr + "\n" + r.StdOut).Split('\n'))
        {
            string t = line.Trim();
            const string key = "TeamIdentifier=";
            if (!t.StartsWith(key, StringComparison.Ordinal)) continue;

            string id = t[key.Length..].Trim();
            return id.Length == 0 || id == "not set" ? null : id;
        }
        return null;
    }

    /// <summary>
    /// Steps 2 and 3 for the whole staged Windows tree: <b>every</b> PE in it is validly
    /// Authenticode-signed by the publisher that signed the running executable.
    ///
    /// <para><b>Why the tree and not just the apphost</b> (security review, 2026-08-25). Checking one
    /// file only proves that one file. A payload can carry a genuine, correctly-signed
    /// <c>circuitRF.exe</c> copied verbatim from a real release beside anything at all — and on a
    /// publish that is not single-file, "anything at all" is the managed assembly holding every line
    /// of the application. The check has to cover what actually runs, which is the set of PEs, not
    /// the first one.</para>
    ///
    /// <para>Today <c>PublishSingleFile</c> makes that set exactly one file, so this costs one
    /// <c>WinVerifyTrust</c> call. It is written for the set anyway, because the day someone turns
    /// single-file off is the day the narrow check silently stops meaning anything — and this way
    /// that change makes updates fail visibly in the log instead.</para>
    ///
    /// <para><b>It does not cover non-PE content.</b> The publish tree also carries
    /// <c>pcell-python/**/*.py</c>, which circuitRF executes and Authenticode cannot sign. See
    /// design §9.1 for what that leaves exposed and what closes it.</para>
    /// </summary>
    public static VerifyResult VerifyWindowsTree(string stagedDir, string runningExe, string appExeName)
    {
        string appExe = Path.Combine(stagedDir, appExeName);
        if (!File.Exists(appExe))
            return new VerifyResult(VerifyOutcome.SignatureInvalid, $"the staged tree has no {appExeName}");

        string[] binaries;
        try
        {
            binaries = Directory.GetFiles(stagedDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new VerifyResult(VerifyOutcome.SignatureInvalid, e.Message);
        }

        int checkedCount = 0;
        foreach (string f in binaries)
        {
            string ext = Path.GetExtension(f);
            if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            // A tree with more PEs than any publish of ours has is not one of ours; refusing bounds
            // the cost of this loop against a payload built to make it expensive.
            if (++checkedCount > MaxSignedBinaries)
                return new VerifyResult(VerifyOutcome.SignatureInvalid,
                                        $"the staged tree carries more than {MaxSignedBinaries} binaries");

            VerifyResult one = VerifyWindowsExecutable(f, runningExe);
            if (!one.Ok)
                return one with { Detail = $"{Path.GetRelativePath(stagedDir, f)}: {one.Detail}" };
        }

        return new VerifyResult(VerifyOutcome.Ok, $"{checkedCount} binaries signed by the running publisher");
    }

    /// <summary>The most PEs a staged Windows tree may hold before it is refused unexamined.</summary>
    public const int MaxSignedBinaries = 2048;

    /// <summary>
    /// Steps 2 and 3 for one staged Windows executable: Authenticode is valid, and the publisher is
    /// the one that signed the running executable.
    ///
    /// <para>An <b>unsigned running build</b> — a developer's own publish — has no publisher to
    /// compare against, so nothing is staged, which is the same conservative answer the macOS path
    /// gives an ad-hoc build.</para>
    /// </summary>
    public static VerifyResult VerifyWindowsExecutable(string stagedExe, string runningExe)
    {
        try
        {
            // Authenticode VALIDITY first, and it is a separate question from "is a certificate
            // embedded": a tampered payload keeps its certificate and fails the hash inside it, so
            // reading the subject alone would accept exactly the file this check exists to reject.
            if (!WinTrust.IsAuthenticodeValid(stagedExe))
                return new VerifyResult(VerifyOutcome.SignatureInvalid,
                                        "Authenticode verification failed on the staged executable");

            string? staged  = PublisherOf(stagedExe);
            string? running = PublisherOf(runningExe);

            if (staged is null)
                return new VerifyResult(VerifyOutcome.SignatureInvalid, "the staged executable is not signed");

            if (running is null)
                return new VerifyResult(VerifyOutcome.IdentityMismatch,
                                        "the running application is not signed, so there is nothing to match");

            return string.Equals(staged, running, StringComparison.Ordinal)
                ? new VerifyResult(VerifyOutcome.Ok, $"publisher {staged}")
                : new VerifyResult(VerifyOutcome.IdentityMismatch,
                                   $"staged publisher {staged} is not the running {running}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new VerifyResult(VerifyOutcome.SignatureInvalid, e.Message);
        }
    }

    /// <summary>
    /// The running Windows build's Authenticode publisher, or <c>null</c> when it carries no
    /// certificate at all — which is every unsigned publish, and the shipping state of the Windows
    /// build today. Always <c>null</c> off Windows.
    /// </summary>
    public static string? RunningWindowsPublisher()
        => OperatingSystem.IsWindows()
            ? PublisherOf(Path.Combine(AppContext.BaseDirectory, UpdateApp.Name + ".exe"))
            : null;

    /// <summary>
    /// Whether the Windows platform-signature check applies to this build — the one place design
    /// §15.5's Windows relaxation is stated, so that the gate before the feed and the check after the
    /// unpack cannot drift apart.
    ///
    /// <para>It is skipped in exactly one case: an unsigned build that carries a release key, where
    /// the signed manifest's SHA-256 is the anchor instead. A signed build is always checked, so a
    /// certificate adds a second anchor rather than replacing the key. <b>And with neither — no
    /// certificate and no key — it APPLIES</b>, which refuses the payload: the fail-safe direction,
    /// and unreachable in practice because
    /// <see cref="RunningBuildCanAcceptUpdatesAsync"/> has already answered notify-only.</para>
    /// </summary>
    public static bool WindowsPlatformCheckApplies(string? runningPublisher, bool requireSignedManifest)
        => runningPublisher is not null || !requireSignedManifest;

    private static string? PublisherOf(string exePath)
    {
        try
        {
            // SYSLIB0057 marks the whole certificate-loading family obsolete in favour of
            // X509CertificateLoader, which loads certificate FILES. There is no replacement for
            // reading the certificate embedded in a signed PE, so this stays, deliberately and with
            // the suppression narrowed to the one call.
#pragma warning disable SYSLIB0057
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate
                                   .CreateFromSignedFile(exePath);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (CryptographicException) { return null; }   // not signed at all
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}

/// <summary>
/// <c>WinVerifyTrust</c>, which is what actually answers "is this Authenticode signature valid" on
/// Windows — the certificate subject alone does not, because a tampered payload keeps its
/// certificate and fails only the hash sealed inside it.
///
/// <para>A byte-moving primitive, so the platform branch belongs here and not in any policy.</para>
/// </summary>
internal static class WinTrust
{
    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_SAFER_FLAG = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

#pragma warning disable SYSLIB1054
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
#pragma warning restore SYSLIB1054

    internal static bool IsAuthenticodeValid(string path)
    {
        if (!OperatingSystem.IsWindows()) return true;   // nothing to check; the caller is not on Windows

        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        IntPtr filePtr = IntPtr.Zero;
        try
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pathPtr,
            };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(file, filePtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice          = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice       = WTD_CHOICE_FILE,
                pFile               = filePtr,
                dwStateAction       = WTD_STATEACTION_VERIFY,
                dwProvFlags         = WTD_SAFER_FLAG,
            };

            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
