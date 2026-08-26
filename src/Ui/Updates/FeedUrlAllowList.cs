using System;
using System.Collections.Generic;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// The hostnames a manifest's <c>feedUrl</c> may point at, compiled into the binary.
///
/// <para><b>Why an allow-list rather than honouring whatever the manifest says.</b> The manifest is
/// served over TLS by the host, and TLS is all that stands behind it today — there is no manifest
/// signature yet (design §15.5). So a field that lets a release re-point the updater is a field that
/// lets a <i>compromised</i> release re-point it, at an arbitrary host, permanently, on every machine
/// that reads it once. Constraining it to hosts we chose in advance costs nothing until the day we
/// move, and on that day the new host is one entry added here in the release that announces it.</para>
///
/// <para>The payload's integrity does not rest on this: authenticity comes from the code signature
/// and the publisher-identity check (design §9), so a hostile feed can withhold updates but cannot
/// substitute one. This narrows even that.</para>
/// </summary>
public static class FeedUrlAllowList
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    /// <summary>The allow-listed hostnames, for the test that pins them and for diagnostics.</summary>
    public static IReadOnlyCollection<string> AllowedHosts => Hosts;

    /// <summary>
    /// True when <paramref name="url"/> is an absolute <c>https</c> URL on an allow-listed host.
    /// Plain <c>http</c> is refused outright: a feed URL is exactly the thing an on-path attacker
    /// would want to rewrite.
    /// </summary>
    public static bool IsAllowed(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? u)
           && u.Scheme == Uri.UriSchemeHttps
           && Hosts.Contains(u.Host);

    /// <summary>
    /// What this BUILD will actually fetch from: the allow-list, or — when a release key is compiled
    /// in — any <c>https</c> host.
    ///
    /// <para><b>The relaxation is what design §15.5 bought, and it is not a weakening.</b> The
    /// allow-list exists because, with nothing but TLS behind a manifest, the host <i>is</i> the
    /// trust anchor. A build carrying a release key has a different anchor: a manifest is honoured
    /// only when signed by a key that is not on any host, and the payload is accepted only against a
    /// SHA-256 inside that signed manifest. At that point constraining the hostname stops adding
    /// anything and starts preventing the two things the signature was for — moving the payload off
    /// GitHub (§15.4) and mirroring it across several hosts (§15.5) — neither of which a shipped
    /// client could otherwise be told about.</para>
    ///
    /// <para><c>https</c> is still required. TLS no longer carries integrity here, but it still
    /// carries confidentiality, and which version of which application a machine is fetching is not
    /// something to put on the wire in the clear.</para>
    /// </summary>
    public static bool IsAcceptable(string? url)
        => IsAllowed(url)
           || (ReleaseKeys.RequireSignedManifest
               && Uri.TryCreate(url, UriKind.Absolute, out Uri? u)
               && u.Scheme == Uri.UriSchemeHttps);
}
