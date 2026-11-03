using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// The per-user vendor-library list (brief vendorlib-1 R-vl1-4c, overview D3) — what this user has
/// added, so New Workspace can reference it from the start and a missing library can be repaired from
/// where the user last had it. The WORKSPACE is the truth; this is only the convenience, which is why
/// it is never read by anything in <c>src/Design</c> — a preference reaches there as an argument.
/// </summary>
public static class UserVendorLibraries
{
    public static IReadOnlyList<UserVendorLibrary> All()
        => AppPreferencesIo.Load().VendorLibraries ?? [];

    /// <summary>Adds or updates <paramref name="name"/>, keeping its Use-in-new-workspaces choice.</summary>
    public static void Remember(string name, string path)
        => AppPreferencesIo.Update(p =>
        {
            var list = p.VendorLibraries ?? [];
            var hit  = list.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null) list.Add(new UserVendorLibrary { Name = name, Path = path });
            else             { hit.Name = name; hit.Path = path; }
            p.VendorLibraries = list;
        });

    public static void SetUseInNewWorkspaces(string name, bool use)
        => AppPreferencesIo.Update(p =>
        {
            if (p.VendorLibraries?.FirstOrDefault(
                    l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is { } hit)
                hit.UseInNewWorkspaces = use;
        });

    /// <summary>What File ▸ New Workspace references: every ticked entry.</summary>
    public static IReadOnlyList<(string Name, string Path)> ForNewWorkspace()
        => [.. All().Where(l => l.UseInNewWorkspaces && l.Name.Length > 0 && l.Path.Length > 0)
                    .Select(l => (l.Name, l.Path))];

    /// <summary>Where this user last had a library of that name, when that folder still exists.</summary>
    public static string? ExistingPathFor(string name)
        => All().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)
                                  && Directory.Exists(l.Path))?.Path;

    /// <summary>The publishers this user trusts, name → key fingerprint (brief vendorlib-15 D6).</summary>
    public static IReadOnlyDictionary<string, string> TrustedPublishers()
        => AppPreferencesIo.Load().TrustedPublishers is { } t
            ? new Dictionary<string, string>(t, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Trusts <paramref name="publisher"/> with the key <paramref name="fingerprint"/> — the user's answer to
    /// a signed library's "Trust this publisher?".</summary>
    public static void TrustPublisher(string publisher, string fingerprint)
        => AppPreferencesIo.Update(p =>
        {
            var t = p.TrustedPublishers ?? new Dictionary<string, string>(StringComparer.Ordinal);
            t[publisher] = fingerprint;
            p.TrustedPublishers = t;
        });

    /// <summary>A library's signature on this user's terms.</summary>
    public static CircuitRF.Design.VendorLibraries.ModelLibrary.LibrarySignatureVerdict Judge(
        CircuitRF.Design.VendorLibraries.ModelLibrary.LibrarySignatureInspection inspection)
        => CircuitRF.Design.VendorLibraries.ModelLibrary.LibrarySignature.Judge(inspection, TrustedPublishers());

    public static bool? UseInNewWorkspaces(string name)
        => All().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))?.UseInNewWorkspaces;
}
