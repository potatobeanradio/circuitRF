// brief-dfr35-1 R-dfr35-1-10 — what a 3D view sizes its air box from while no setup is active.
//
// A blank setup asks for no pattern, so a layout whose .cem asks for one was drawn with the non-radiating rule: on a two-layer
// antenna board the sides took the structure's largest extent, the floor 10 × H and the top the height, against the same padding on
// every face in that .cem's own run. The panel showed a box no run of that design would solve in. Where exactly one .cem names
// what this view places, the box takes that setup's problem type, sweep and pattern; the rule itself is untouched.

using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.ThreeD;

/// <summary>What a view with no active setup sizes its box from, and the <c>.cem</c> it came from when one did.</summary>
public sealed record C3dNoSetupBasis(EmSetup Setup, string? CemPath);

public static class C3dNoSetupBox
{
    /// <summary>The setup a new one would start as: the document's own box, nothing asked of it.</summary>
    public static EmSetup Blank(C3dDocument document) => new()
    {
        Solver3D = CircuitRF.Design.Em3d.Em3dSolvers.Default,
        AirBox = EmSetupPersistence.FromCem(document.AirBox),
    };

    /// <summary>The one <c>.cem</c> in the workspace naming this view or a layout it reads; null when none does, or several do,
    /// since then nothing says which of them this view stands for. It walks the workspace and reads each <c>.cem</c>.</summary>
    public static string? SoleSetupFile(string documentPath, string? workspaceCws, IEnumerable<string> filesRead)
    {
        if (workspaceCws is null) return null;
        var named = filesRead.Where(f => f.EndsWith(".clay", StringComparison.OrdinalIgnoreCase)).Append(documentPath)
                             .SelectMany(f => EmSetupResolver.FindSetupsForLayout(workspaceCws, f))
                             .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>The blank setup with <paramref name="cemPath"/>'s problem type, sweep and pattern; the document's box where it states
    /// one, else that setup's. Just the blank when the file is null or cannot be read.</summary>
    public static C3dNoSetupBasis For(C3dDocument document, string? cemPath)
    {
        var blank = Blank(document);
        if (cemPath is null) return new(blank, null);
        EmSetup cem;
        try { cem = EmSetupPersistence.LoadFromFile(cemPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or FormatException)
        {
            return new(blank, null);
        }
        blank.Problem3D = cem.Problem3D;
        blank.Frequency = cem.Frequency;
        blank.RadiationPattern = cem.RadiationPattern;
        blank.PatternFrequencyGHz = cem.PatternFrequencyGHz;
        blank.AirBox ??= cem.AirBox;
        return new(blank, cemPath);
    }
}
