using System;
using System.Security.Cryptography;

namespace CircuitRF.Ui.Updates;

/// <summary>
/// The release signing key — the trust anchor design §15.5 rests on, compiled into the binary.
///
/// <para><b>What it buys.</b> Without it the payload's integrity rests on the platform's own code
/// signing, which covers everything on macOS, only the PEs on Windows (so not
/// <c>pcell-python/**/*.py</c>, which circuitRF executes) and nothing at all on Linux — so whoever
/// can publish a release to the host gets code execution on two of the three platforms (§9.1). With
/// it, a release is accepted only when a manifest signed by the matching PRIVATE key names the
/// payload and its SHA-256, and that key is not on the host. The host becomes a bucket of bytes on
/// every platform equally, which is the property §15.5 promised and the only one that closes both
/// gaps.</para>
///
/// <para><b>ECDSA P-256 / SHA-256, not Ed25519.</b> The design note says "minisign-style EdDSA",
/// which is what Sparkle uses. .NET has no managed Ed25519, and adding a native dependency for it
/// would need the root <c>CLAUDE.md</c>'s "ask before" — for a change in signature algorithm, not in
/// security level. P-256 is in the BCL, is the same 128-bit security level, and needs nothing that
/// is not already on every machine the application runs on.</para>
///
/// <para><b>The signature is DETACHED.</b> It covers the exact bytes of <c>update-manifest.json</c>
/// as served, carried in a sibling asset named <c>update-manifest.json.sig</c>. The manifest's own
/// reserved <c>signature</c> field is NOT used and is still parsed-and-ignored: a signature inside
/// the document it signs needs a canonicalisation rule, and a canonicalisation rule is a second
/// specification that has to be got exactly right by two programs written years apart. Signing the
/// bytes has no such rule to get wrong.</para>
/// </summary>
public static class ReleaseKeys
{
    /// <summary>
    /// The public key, base64 of its SubjectPublicKeyInfo (DER) — i.e. the body of a
    /// <c>-----BEGIN PUBLIC KEY-----</c> PEM with the newlines removed.
    ///
    /// <para><b>Empty means unsigned releases are accepted</b>, which is where this shipped and what
    /// every build before the key exists must keep doing — a client that demands a signature nobody
    /// is producing yet is a client that never updates again. Fill it in and the demand becomes
    /// unconditional from that build onward (see <see cref="RequireSignedManifest"/>).</para>
    ///
    /// <para>A PUBLIC key belongs in version control: it is the thing being trusted, so it should be
    /// reviewable, diffable and attributable to the commit that introduced it. The private half
    /// never touches this repository — see <c>tools/ReleaseSigner</c> and <c>BUILDING.md</c>.</para>
    /// </summary>
    public const string PublicKeySpkiBase64 = "";

    /// <summary>
    /// Whether this build demands a signed manifest before it will install anything.
    ///
    /// <para><b>It is not "verify the signature if one is present."</b> That is a downgrade attack
    /// with extra steps: an attacker who can publish a release can publish one with no manifest at
    /// all, and an updater that treats the absence of a signature as "nothing to check" has learned
    /// nothing from checking it. So the presence of a compiled-in key makes the manifest, its
    /// signature, and a SHA-256 for the chosen asset all mandatory, and a release missing any of them
    /// is simply not a candidate.</para>
    /// </summary>
    public static bool RequireSignedManifest => PublicKeySpkiBase64.Length > 0;

    /// <summary>
    /// True when <paramref name="signatureBase64"/> is a valid signature over <paramref name="data"/>
    /// by the compiled-in key.
    ///
    /// <para>False for everything else, including a malformed key, a malformed signature and no key
    /// at all — <b>never an exception</b>. This is called on attacker-supplied bytes on a background
    /// thread, and "the updater is not permitted to be the reason anything else fails" applies to the
    /// verification step most of all.</para>
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> data, string? signatureBase64)
        => Verify(data, signatureBase64, PublicKeySpkiBase64);

    /// <summary>Verify against an arbitrary key rather than the compiled-in one.</summary>
    public static bool Verify(ReadOnlySpan<byte> data, string? signatureBase64, string? publicKeySpkiBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64)) return false;
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;

        // A signature field long enough to be worth refusing before it is decoded. A P-256 DER
        // signature is ~72 bytes, so ~100 base64 characters.
        if (signatureBase64.Length > 1024) return false;

        try
        {
            byte[] key = Convert.FromBase64String(publicKeySpkiBase64.Trim());
            byte[] sig = Convert.FromBase64String(signatureBase64.Trim());

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);

            // The curve is pinned rather than taken from the key: a key whose SPKI names a weaker
            // curve would otherwise be honoured because it is the key we compiled in, and the whole
            // point of compiling it in is that its properties are decided here and not at run time.
            if (ecdsa.KeySize != 256) return false;

            return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Which release key a check runs against — <b>a value passed in, never a global that can be set</b>.
///
/// <para>The alternative was a mutable <c>ReleaseKeys.PublicKey</c> that tests could assign, and a
/// trust anchor with a setter is a trust anchor with a way to remove it. This carries the same
/// information immutably: the application constructs nothing and gets <see cref="Compiled"/>, tests
/// construct their own, and there is no state for either to leave behind for the other.</para>
/// </summary>
/// <param name="PublicKeySpkiBase64">The key to verify against. Empty means "no key compiled in".</param>
public sealed record ReleaseTrust(string PublicKeySpkiBase64)
{
    /// <summary>What the application always uses: the key compiled into this build.</summary>
    public static readonly ReleaseTrust Compiled = new(ReleaseKeys.PublicKeySpkiBase64);

    /// <summary>Whether this build demands a signed manifest. See <see cref="ReleaseKeys.RequireSignedManifest"/>.</summary>
    public bool RequireSignedManifest => PublicKeySpkiBase64.Length > 0;

    /// <summary>True when the signature is valid under this key. Never throws.</summary>
    public bool Verify(ReadOnlySpan<byte> data, string? signatureBase64)
        => ReleaseKeys.Verify(data, signatureBase64, PublicKeySpkiBase64);
}
