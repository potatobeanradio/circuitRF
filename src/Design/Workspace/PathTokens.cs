namespace CircuitRF.Design.Workspace;

/// <summary>
/// SL1 R-sl1-5 — <c>${NAME}</c> expansion for the three stored <c>.cws</c> fields that name a
/// location OUTSIDE the workspace: <c>ReferencedWorkspaces[].Path</c>, <c>LibraryRefs</c> and
/// <c>KnownFiles</c>.
///
/// <para><b>Why it exists.</b> A library on a network share is always the absolute branch of
/// <c>CwsWorkspaceRef.Path</c>, and the absolute spelling is per-machine:
/// <c>Z:\eda\stdlib\.cws</c>, <c>\\server\eda\stdlib\.cws</c>, <c>/Volumes/eda/stdlib/.cws</c>. The
/// alias indirection means each user repairs it once, but it also means a librarian cannot hand out
/// a starter workspace with a working library reference in it — which is the one thing a librarian
/// most wants to hand out. A <c>.cws</c> containing <c>${CRF_LIB}/stdlib/v2.3/.cws</c> is portable to
/// every user who has <c>CRF_LIB</c> set, and version pinning (R-sl-6) becomes a path the librarian
/// publishes rather than a resolver anyone has to build.</para>
///
/// <para><b>One syntax, on every platform.</b> Never <c>%NAME%</c> and never bare <c>$NAME</c>: a
/// <c>.cws</c> travels between machines, and a per-platform spelling would resolve on the machine
/// that wrote it and nowhere else.</para>
///
/// <para><b>An undefined token is a BROKEN reference that names the token, never an empty
/// expansion</b> (R-sl1-7). <c>Environment.GetEnvironmentVariable</c> returns null for an unset
/// variable, and substituting empty turns <c>${CRF_LIB}/stdlib/v2.3/.cws</c> into
/// <c>/stdlib/v2.3/.cws</c> — a rooted path that resolves to somewhere real on some machines and
/// reports a missing folder on others. Both are worse than the truth.</para>
///
/// <para><b>A <c>CellRef</c> is NEVER expanded</b> (R-sl1-6). It is the workspace-relative remainder
/// (§5C R45) and has no business naming a machine; a token there would create a second place a
/// cross-workspace path can hide, which is the whole thing the alias form exists to prevent.</para>
///
/// <para><b>Nothing is ever WRITTEN with a token in it.</b> circuitRF writes a plain path; a token is
/// something a librarian or a user types into a <c>.cws</c> by hand, or that a site template ships.
/// Resolve it, never produce it.</para>
///
/// <para>It lives here, beside <see cref="ExternalCellRef"/>, rather than in <c>src/Ui</c>, because a
/// headless <c>circuitrf convert</c> or EM run resolves these references too (R-sl1-8) — the same
/// constraint <see cref="ExternalCellRef"/>'s own re-implementation of the resolve rule already
/// records.</para>
/// </summary>
public static class PathTokens
{
    /// <summary>
    /// Expands every <c>${NAME}</c> in <paramref name="stored"/> from the environment.
    /// </summary>
    /// <param name="expanded">
    /// The expanded path when this returns true; otherwise <paramref name="stored"/> unchanged, so a
    /// caller that ignores the result still never sees a half-expanded path.
    /// </param>
    /// <param name="unsetToken">
    /// The first token with no value on this machine — the whole <c>${NAME}</c> text, for the message
    /// the user reads — or null when every token resolved.
    /// </param>
    /// <returns>False when any token is unset (R-sl1-7): the reference is BROKEN, not empty.</returns>
    public static bool TryExpand(string? stored, out string expanded, out string? unsetToken)
    {
        expanded   = stored ?? string.Empty;
        unsetToken = null;
        if (string.IsNullOrEmpty(stored) || stored!.IndexOf("${", StringComparison.Ordinal) < 0)
            return true;

        var sb = new System.Text.StringBuilder(stored.Length);
        int i = 0;
        while (i < stored.Length)
        {
            int open = stored.IndexOf("${", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(stored, i, stored.Length - i); break; }

            int close = stored.IndexOf('}', open + 2);
            if (close < 0) { sb.Append(stored, i, stored.Length - i); break; }   // unterminated: literal text

            sb.Append(stored, i, open - i);
            string name  = stored[(open + 2)..close];
            string? value = name.Length == 0 ? null : Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
            {
                unsetToken = stored[open..(close + 1)];
                expanded   = stored;
                return false;
            }
            sb.Append(value);
            i = close + 1;
        }

        expanded = sb.ToString();
        return true;
    }

    /// <summary>
    /// The expanded form, or null when a token is unset — the shape a resolver wants, since an unset
    /// token and a path that does not exist are the same outcome to it (a reference that does not
    /// resolve) and differ only in the sentence the user is shown.
    /// </summary>
    public static string? ExpandOrNull(string? stored)
        => TryExpand(stored, out string expanded, out _) ? expanded : null;

    /// <summary>
    /// The first <c>${NAME}</c> in <paramref name="stored"/> that has no value on this machine, or
    /// null. Callers use it to say WHICH variable to set rather than "unresolved".
    /// </summary>
    public static string? UnsetTokenIn(string? stored)
    {
        TryExpand(stored, out _, out string? unset);
        return unset;
    }

    /// <summary>True when <paramref name="stored"/> contains at least one <c>${NAME}</c>.</summary>
    public static bool ContainsToken(string? stored)
        => !string.IsNullOrEmpty(stored) && stored!.Contains("${", StringComparison.Ordinal);
}
