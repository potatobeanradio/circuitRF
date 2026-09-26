namespace CircuitRF.Design.Em3d;

/// <summary>Where Palace is looked for (em-3d.md §7.3).</summary>
public enum PalaceLocationKind
{
    /// <summary>The default: a native Palace if there is one, then the Linux subsystem (Windows only).</summary>
    Automatic,

    /// <summary>This machine's own programs only.</summary>
    Native,

    /// <summary>One named Linux subsystem distribution only (Windows).</summary>
    Subsystem,
}

/// <summary>
/// brief-em3d-26 R-em3d26-1d — the location setting: <i>Automatic</i>, <i>Native</i>, or <i>Linux
/// subsystem: &lt;distribution&gt;</i>. Stored as one string — <c>automatic</c>, <c>native</c>,
/// <c>wsl:&lt;name&gt;</c> — per user, because where a program is installed is a property of the machine.
/// On a platform with no Linux subsystem every value behaves as <i>Automatic</i>, which there is native.
/// </summary>
public sealed record PalaceLocation(PalaceLocationKind Kind, string? Distribution = null)
{
    public static readonly PalaceLocation Automatic = new(PalaceLocationKind.Automatic);
    public static readonly PalaceLocation Native = new(PalaceLocationKind.Native);

    public static PalaceLocation Subsystem(string distribution) => new(PalaceLocationKind.Subsystem, distribution);

    private const string SubsystemPrefix = "wsl:";

    /// <summary>The stored spelling; null for <see cref="Automatic"/>, which is the default and so is not stored.</summary>
    public string? ToSetting() => Kind switch
    {
        PalaceLocationKind.Native    => "native",
        PalaceLocationKind.Subsystem => SubsystemPrefix + Distribution,
        _                            => null,
    };

    /// <summary>Reads a stored spelling; anything unrecognised is <see cref="Automatic"/>, the default.</summary>
    public static PalaceLocation Parse(string? setting)
    {
        string s = setting?.Trim() ?? "";
        if (s.Equals("native", StringComparison.OrdinalIgnoreCase)) return Native;
        if (s.StartsWith(SubsystemPrefix, StringComparison.OrdinalIgnoreCase) && s.Length > SubsystemPrefix.Length)
            return Subsystem(s[SubsystemPrefix.Length..].Trim());
        return Automatic;
    }

    /// <summary>What the Settings row shows.</summary>
    public string Describe() => Kind switch
    {
        PalaceLocationKind.Native    => "Native",
        PalaceLocationKind.Subsystem => $"Linux subsystem: {Distribution}",
        _                            => "Automatic",
    };
}
