// Framework-free. No Avalonia, no SkiaSharp.

using System.Text.RegularExpressions;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// Which of a run's notes explain a radiation pattern rather than report on the run: what each metric means, why one was not
/// published, a check that passed. They stay in <see cref="RunResult.Notes"/> and on a headless run's output; the Messages panel
/// puts them behind one row, so that the warnings and errors around them get read. A check that fails is also a warning, so it
/// is never hidden. A note this does not recognise is simply shown.
/// </summary>
public static partial class RunNotes
{
    [GeneratedRegex(@"^(?:Far field \(|Polarization at |The Ludwig-3 reference angle was |Check(?: at [^:]+)?: |Fields '[^']+' at [^:]+: the pattern of |[\w -]+? (?:is|are) not published\b)")]
    private static partial Regex Detail();

    /// <summary>True when <paramref name="note"/> belongs behind the row rather than on one of its own.</summary>
    public static bool IsDetail(string note) => Detail().IsMatch(note);
}
