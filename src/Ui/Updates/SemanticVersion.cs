using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// SemVer 2.0 precedence — the one thing that decides whether a release is an update at all.
///
/// <para><b>Why not <see cref="Version"/>.</b> <c>System.Version</c> cannot parse
/// <c>1.0.0-beta.1</c> at all: it throws. And a lexicographic string comparison gets prerelease
/// ordering exactly backwards, because <c>"1.0.0-beta.1" &gt; "1.0.0"</c> as text while
/// <c>1.0.0-beta.1 &lt; 1.0.0</c> as a version.</para>
///
/// <para><b>The case a naive implementation gets wrong</b> is <c>beta.2 &lt; beta.10</c>:
/// dot-separated prerelease identifiers that are all digits compare <b>numerically</b>, not as
/// text. This is the second appearance of a trap <c>packaging/version.sh</c> already documents for
/// dpkg's <c>~</c> spelling, so it gets one implementation rather than two.</para>
///
/// <para>Build metadata (<c>+sha</c>) is parsed and then <b>ignored for precedence</b>, per the
/// specification. <see cref="AppVersion"/> already strips it, so it is only ever seen on a tag.</para>
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The dot-separated prerelease identifiers, empty for a stable release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>Build metadata, kept for round-tripping and never used in a comparison.</summary>
    public string Build { get; }

    /// <summary>True when this version carries a prerelease suffix, e.g. <c>1.0.0-beta.1</c>.</summary>
    public bool IsPreRelease => PreRelease.Count > 0;

    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> pre, string build)
    {
        Major = major; Minor = minor; Patch = patch; PreRelease = pre; Build = build;
    }

    /// <summary>
    /// Parses <c>[v]major.minor[.patch][-prerelease][+build]</c>. A missing patch is zero, so the
    /// <c>VERSION</c> file's shorter <c>1.0</c> spelling — which `Directory.Build.props` accepts —
    /// parses here too, and so does a GitHub tag written <c>v1.0.0-beta.1</c>.
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        // A tag longer than this is not a version anyone typed; it is padding.
        if (s.Length > MaxLength) return false;

        string build = "";
        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            build = s[(plus + 1)..];
            s = s[..plus];
            if (build.Length == 0) return false;   // "1.0.0+" is not a version
        }

        string[] pre = [];
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            string preText = s[(dash + 1)..];
            s = s[..dash];
            if (preText.Length == 0) return false;
            pre = preText.Split('.');
            // An empty identifier ("1.0.0-beta..1") is not a version; refuse rather than guess.
            if (pre.Any(string.IsNullOrEmpty)) return false;
            if (!pre.All(IsIdentifier)) return false;
        }

        if (build.Length > 0 && (build.Split('.').Any(string.IsNullOrEmpty) ||
                                 !build.Split('.').All(IsIdentifier))) return false;

        string[] core = s.Split('.');
        if (core.Length is < 2 or > 3) return false;

        if (!TryNumber(core[0], out int major) ||
            !TryNumber(core[1], out int minor)) return false;

        int patch = 0;
        if (core.Length == 3 && !TryNumber(core[2], out patch)) return false;

        version = new SemanticVersion(major, minor, patch, pre, build);
        return true;
    }

    /// <summary>Parses or throws — for literals in tests and for values already validated.</summary>
    public static SemanticVersion Parse(string text)
        => TryParse(text, out SemanticVersion? v) && v is not null
            ? v
            : throw new FormatException($"Not a semantic version: '{text}'.");

    /// <summary>The longest text this will look at. A version is short; a payload is not.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// A SemVer identifier: <c>[0-9A-Za-z-]</c> and nothing else, exactly as the specification says.
    ///
    /// <para><b>Enforcing the charset is a path-safety property, not pedantry</b> (security review,
    /// 2026-08-25). Prerelease and build identifiers were previously taken verbatim, so
    /// <c>1.0.0+../../evil</c> parsed — and <see cref="ReleaseInfo.VersionText"/>, which is the TAG's
    /// own spelling, becomes a path segment in <c>&lt;install root&gt;/app-&lt;ver&gt;</c> and in
    /// <c>updates/staged/&lt;ver&gt;/</c>. Nothing reached those paths, because the matching asset
    /// name would have carried the same separators and
    /// <see cref="UpdateAssetNames.IsSafeAssetFileName"/> refuses those — but that is one guard
    /// standing between a release tag and a directory outside the install root, and the
    /// specification's own rule removes the class instead of guarding it.</para>
    /// </summary>
    private static bool IsIdentifier(string s)
    {
        foreach (char c in s)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        return s.Length > 0;
    }

    private static bool TryNumber(string s, out int value)
    {
        value = 0;
        if (s.Length == 0 || !s.All(char.IsAsciiDigit)) return false;
        // A leading zero is not a valid numeric identifier ("01.0.0"), and refusing it here keeps
        // a mistyped tag from sorting somewhere surprising.
        if (s.Length > 1 && s[0] == '0') return false;
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        int c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);     if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);     if (c != 0) return c;

        // "A pre-release version has lower precedence than the associated normal version."
        if (PreRelease.Count == 0 && other.PreRelease.Count == 0) return 0;
        if (PreRelease.Count == 0) return 1;
        if (other.PreRelease.Count == 0) return -1;

        int n = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (int i = 0; i < n; i++)
        {
            c = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
            if (c != 0) return c;
        }

        // "A larger set of pre-release fields has a higher precedence" — beta < beta.1.
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool na = a.Length > 0 && a.All(char.IsAsciiDigit);
        bool nb = b.Length > 0 && b.All(char.IsAsciiDigit);

        // THE trap: numeric identifiers compare numerically, so beta.2 < beta.10 rather than the
        // text ordering, which puts "10" before "2".
        if (na && nb)
        {
            return long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out long x) &&
                   long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out long y)
                ? x.CompareTo(y)
                : string.CompareOrdinal(a, b);
        }

        // "Numeric identifiers always have lower precedence than alphanumeric identifiers."
        if (na) return -1;
        if (nb) return 1;
        return string.CompareOrdinal(a, b);
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', PreRelease));

    public static bool operator <(SemanticVersion a, SemanticVersion b)  => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b)  => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator ==(SemanticVersion? a, SemanticVersion? b)
        => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemanticVersion? a, SemanticVersion? b) => !(a == b);

    public override string ToString()
    {
        string core = $"{Major}.{Minor}.{Patch}";
        if (PreRelease.Count > 0) core += "-" + string.Join('.', PreRelease);
        if (Build.Length > 0)     core += "+" + Build;
        return core;
    }
}
