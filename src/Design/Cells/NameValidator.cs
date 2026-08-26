namespace CircuitRF.Design.Cells;

/// <summary>
/// Validates a single path component (workspace/library/cell/view name) for
/// cross-platform filesystem safety.  See workspace-and-project-tree.md §1.4.
/// </summary>
public static class NameValidator
{
    private static readonly char[] _disallowedChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> _windowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns true if the name is valid.</summary>
    public static bool IsValid(string name) => Validate(name) is null;

    /// <summary>
    /// Returns null if valid, or a human-readable reason string if not.
    /// Validates a single path component — slashes are in the disallowed set.
    /// </summary>
    public static string? Validate(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Name must not be empty.";

        if (string.IsNullOrWhiteSpace(name))
            return "Name must not be whitespace-only.";

        foreach (char c in name)
        {
            if (c <= 0x1F)
                return $"Name contains a control character (U+{(int)c:X4}).";
            if (_disallowedChars.Contains(c))
                return $"Name contains a disallowed character '{c}'.";
        }

        if (name[^1] is ' ' or '.')
            return "Name must not end with a space or dot.";

        // Check Windows reserved device names with or without extension (e.g. "CON", "CON.txt").
        string stem = name;
        int dotIdx = name.IndexOf('.');
        if (dotIdx >= 0)
            stem = name[..dotIdx];

        if (_windowsReserved.Contains(stem))
            return $"'{stem}' is a Windows reserved device name.";

        return null;
    }
}
