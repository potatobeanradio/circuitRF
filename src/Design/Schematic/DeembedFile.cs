namespace CircuitRF.Design.Schematic;

/// <summary>
/// brief-de-3 — the one rule a Deembed's file must meet before the editor takes it: an even port count, one side matching the
/// other. The reader refuses an odd <c>NumPorts</c> anyway (brief-de-2); this is the same refusal, made when the file is
/// picked rather than when the circuit runs, so a symbol is never redrawn with pins the block cannot have.
/// </summary>
public static class DeembedFile
{
    /// <summary>The sentence refusing <paramref name="path"/> for a Deembed, or null when its port count is even.</summary>
    public static string? OddPortsRefusal(string path, int ports)
        => ports >= 2 && ports % 2 == 0 ? null
         : $"'{System.IO.Path.GetFileName(path)}' is a {ports}-port; a Deembed needs an even number of ports, one side matching " +
           "the other, so its file was not changed.";
}
