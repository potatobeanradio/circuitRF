using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>One line of <c>wsl -l -v</c>.</summary>
/// <param name="State">As wsl.exe printed it (<c>Running</c>, <c>Stopped</c>, …) — localized on a
/// non-English Windows, so nothing branches on it but <see cref="Running"/>.</param>
/// <param name="Version">1 or 2: the WSL version the distribution runs under.</param>
/// <param name="IsDefault">The one marked <c>*</c>, which <c>wsl</c> with no <c>-d</c> starts.</param>
public sealed record WslDistribution(string Name, string State, int Version, bool IsDefault)
{
    public bool Running => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What stands between this computer and a Linux subsystem Palace can run in.</summary>
public enum WslCondition
{
    /// <summary>At least one distribution is registered.</summary>
    Ready,

    /// <summary>The Windows feature is not enabled (or wsl.exe is not there at all).</summary>
    FeatureMissing,

    /// <summary>The feature is there, but virtualization is off in the firmware, so no WSL 2 VM starts.</summary>
    VirtualizationOff,

    /// <summary>The feature is enabled, and no distribution is installed.</summary>
    NoDistribution,

    /// <summary>wsl.exe answered with something this does not recognise; its words are the refusal.</summary>
    Unrecognised,
}

/// <summary>The subsystem as <c>wsl -l -v</c> reported it, and the refusal when it is not usable.</summary>
public sealed record WslSubsystem(WslCondition Condition, IReadOnlyList<WslDistribution> Distributions, string? Refusal)
{
    public bool Ready => Condition == WslCondition.Ready;

    /// <summary>The WSL 2 distributions, in the order <c>wsl -l</c> lists them — the order discovery tries.</summary>
    public IReadOnlyList<WslDistribution> Version2 => Distributions.Where(d => d.Version >= 2).ToList();
}

/// <summary>
/// brief-em3d-26 R-em3d26-1a/3b — listing the distributions, and the preconditions, each refused with the
/// ONE step that fixes it. circuitRF performs none of them: enabling the feature needs administrator
/// rights, virtualization is a firmware setting, and a distribution is the user's own.
/// </summary>
public static class WslDistributions
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Reads <c>wsl -l -v</c> and classifies what it says. Starts no distribution: listing is
    /// answered by the WSL service alone.</summary>
    public static WslSubsystem Read(IWsl wsl)
    {
        if (!wsl.Available) return new(WslCondition.FeatureMissing, [], FeatureMissingRefusal);
        var run = wsl.Run(["-l", "-v"], ListTimeout);
        if (run.Failure is { } failure)
            return new(WslCondition.Unrecognised, [], $"The Linux subsystem did not answer ({failure}).");
        var list = run.ExitCode == 0 ? Parse(run.Stdout) : null;
        if (list is { Count: > 0 }) return new(WslCondition.Ready, list, null);
        return Classify(run.Message, run.ExitCode == 0 && list is { Count: 0 });
    }

    /// <summary>
    /// The distributions in <paramref name="listing"/> — wsl.exe's own bytes, decoded as UTF-16LE (see
    /// <see cref="WslText.DecodeUtf16"/>). Null when the text is not a listing at all; empty when it is a
    /// header with no rows.
    ///
    /// <para>The header is localized, so it is recognised by SHAPE (the first non-empty line, with no
    /// version number at its end) and every other line is <c>[*] name state version</c> — the version and
    /// state are the last two words, and a name can hold no whitespace.</para>
    /// </summary>
    public static IReadOnlyList<WslDistribution>? Parse(byte[] listing) => ParseText(WslText.DecodeUtf16(listing));

    internal static IReadOnlyList<WslDistribution>? ParseText(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return null;
        var rows = new List<WslDistribution>();
        bool header = !Row.IsMatch(lines[0]);
        foreach (string line in header ? lines.Skip(1) : lines)
        {
            var m = Row.Match(line);
            if (!m.Success) return null;
            rows.Add(new WslDistribution(m.Groups[2].Value, m.Groups[3].Value,
                                         int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), m.Groups[1].Value == "*"));
        }
        return header || rows.Count > 0 ? rows : null;
    }

    private static readonly Regex Row = new(@"^\s*(\*?)\s*(\S+)\s+(\S+)\s+(\d+)\s*$", RegexOptions.CultureInvariant);

    // ── the preconditions ────────────────────────────────────────────────────────────────────────

    /// <summary>What a failed listing, or a failed start, means — by the codes and phrases wsl.exe prints.</summary>
    public static WslSubsystem Classify(string message, bool emptyListing = false)
    {
        string m = message.Trim();
        if (VirtualizationOff.IsMatch(m))
            return new(WslCondition.VirtualizationOff, [], VirtualizationRefusal(m));
        if (FeatureMissing.IsMatch(m))
            return new(WslCondition.FeatureMissing, [], FeatureMissingRefusal);
        if (emptyListing || NoDistribution.IsMatch(m))
            return new(WslCondition.NoDistribution, [], NoDistributionRefusal);
        return new(WslCondition.Unrecognised, [],
                   $"The Linux subsystem answered with an error circuitRF does not recognise. wsl.exe said: “{Short(m)}”");
    }

    // 0x80370102 is the Hyper-V "not running" code wsl.exe prints when virtualization is off in firmware;
    // HCS_E_HYPERV_NOT_INSTALLED is its name. Both, and the sentence that names the BIOS, are matched.
    private static readonly Regex VirtualizationOff = new(
        @"0x80370102|HCS_E_HYPERV_NOT_INSTALLED|virtuali[sz]ation is enabled in the BIOS|enable virtuali[sz]ation",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 0x8007019e: "The Windows Subsystem for Linux optional component is not enabled".
    private static readonly Regex FeatureMissing = new(
        @"0x8007019e|optional component is not enabled|Subsystem for Linux is not installed|WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NoDistribution = new(
        @"has no installed distributions|WSL_E_DEFAULT_DISTRO_NOT_FOUND|no installed distributions",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The feature is not enabled — the one step that needs administrator rights, once.</summary>
    public const string FeatureMissingRefusal =
        "The Linux subsystem (WSL) is not enabled on this computer, and on Windows Palace runs only inside it. " +
        "Enabling it needs administrator rights, once: open a terminal as administrator, run 'wsl --install', " +
        "and restart Windows. circuitRF does not do this for you.";

    /// <summary>No distribution — the step that installs one.</summary>
    public const string NoDistributionRefusal =
        "The Linux subsystem is enabled but has no Linux distribution installed. Install one with " +
        "'wsl --install -d Ubuntu', start it once so it can create your Linux user, then try again. " +
        "The distribution is yours; circuitRF never removes it.";

    /// <summary>Virtualization off — a firmware setting, which is all that can be said about it from here.</summary>
    public static string VirtualizationRefusal(string said) =>
        "The Linux subsystem is installed, but this computer's virtualization is turned off, so no WSL 2 " +
        "distribution can start. It is a firmware (BIOS/UEFI) setting, not a Windows one: turn on the processor's " +
        "virtualization there (Intel VT-x, or AMD-V/SVM), then try again." +
        (said.Length > 0 ? $" wsl.exe said: “{Short(said)}”" : "");

    /// <summary>R-em3d26-1b — a WSL 1 distribution, refused with the conversion command.</summary>
    public static string Wsl1Refusal(string name) =>
        $"'{name}' is a WSL 1 distribution. circuitRF runs Palace only in a WSL 2 one, which has a real Linux kernel " +
        $"and its own Linux filesystem; convert it with 'wsl --set-version {name} 2' (it keeps everything in it).";

    private static string Short(string s)
    {
        string one = string.Join(" ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return one.Length <= 300 ? one : one[..300] + "…";
    }
}
