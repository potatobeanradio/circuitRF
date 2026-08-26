using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// Design §15.5 — the signed manifest, which is the only thing that closes §9.1's two named gaps:
/// the Windows Python payload that Authenticode cannot cover, and the whole of Linux.
///
/// <para><b>The fixture below was produced by <c>tools/ReleaseSigner</c>, not by this test.</b> That
/// matters: the tool references nothing in this repository and implements the format independently,
/// so the two agreeing is evidence rather than a tautology. A test that signed with the same code
/// path the client verifies with would pass no matter what the wire format drifted to.</para>
/// </summary>
public sealed class SignedManifestTests
{
    // ── the real fixture, from `ReleaseSigner keygen | manifest | sign` ──────────────────────

    private const string FixturePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8z/WiiqNJiwdhnnJ5zGjQEZywnDdaK76v/Sf4xbPjBz92QEQpskX"
        + "h9D+7WwhjxXHdS1F++fMsNmk1+S4cfRCDA==";

    /// <summary>
    /// The manifest as BASE64 OF ITS BYTES, not as a string literal. The signature covers the file
    /// exactly as served, and a multi-line literal in a <c>.cs</c> file is whatever line endings the
    /// checkout used — so a literal here would verify on the machine that wrote it and fail on a
    /// Windows clone, once, for a reason nobody would look for.
    /// </summary>
    private const string FixtureManifestBase64 =
        "ewogICJhc3NldHMiOiBbCiAgICB7CiAgICAgICJuYW1lIjogImNpcmN1aXRSRi0xLjAuMC1hcm02NC5kbWciLAog"
        + "ICAgICAidXJsIjogImh0dHBzOi8vbWlycm9yLmV4YW1wbGUvci8xLjAuMC9jaXJjdWl0UkYtMS4wLjAtYXJtNjQu"
        + "ZG1nIiwKICAgICAgInNpemUiOiAxNCwKICAgICAgInNoYTI1NiI6ICIxYWMzMGZkNjc3MTY4ZGZmYThlNjlhNGM4"
        + "MzI1NmJjOTUxZmQ5ZDUwYWI2ZDg3NzRmNjBkMjc5Zjg0ZWU2NDA2IgogICAgfQogIF0KfQ==";

    private const string FixtureSignature =
        "MEQCICoC0TotBfY8tR/OioY+h6EEPqWt863hJ6gsNM6P0l2ZAiBIogH/y62Nr+aAgYjy8OttqA/YvuKjSYViHRHqsYonOQ==";

    private static byte[] FixtureManifest => Convert.FromBase64String(FixtureManifestBase64);

    [Fact]
    public void TheClientVerifiesASignatureTheReleaseToolProduced()
        => Assert.True(ReleaseKeys.Verify(FixtureManifest, FixtureSignature, FixturePublicKey));

    [Fact]
    public void OneFlippedByteInTheManifestBreaksIt()
    {
        byte[] tampered = FixtureManifest;
        tampered[10] ^= 0x01;

        Assert.False(ReleaseKeys.Verify(tampered, FixtureSignature, FixturePublicKey));
    }

    [Fact]
    public void AnotherKeysSignatureIsNotOurs()
    {
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string sig = Convert.ToBase64String(
            other.SignData(FixtureManifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.False(ReleaseKeys.Verify(FixtureManifest, sig, FixturePublicKey));

        // ...and it verifies under ITS OWN key, so the fixture is not simply unverifiable.
        Assert.True(ReleaseKeys.Verify(
            FixtureManifest, sig, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
    }

    /// <summary>
    /// Verification is called on attacker-supplied bytes on a background thread. "The updater is not
    /// permitted to be the reason anything else fails" applies to this step most of all.
    /// </summary>
    [Theory]
    [InlineData(null, "sig")]
    [InlineData("", "sig")]
    [InlineData("not base64 at all !!", "sig")]
    [InlineData(FixturePublicKey, null)]
    [InlineData(FixturePublicKey, "")]
    [InlineData(FixturePublicKey, "not base64 at all !!")]
    [InlineData(FixturePublicKey, "AAAA")]
    public void MalformedInputIsFalse_NeverAnException(string? key, string? sig)
        => Assert.False(ReleaseKeys.Verify(FixtureManifest, sig, key));

    [Fact]
    public void AnAbsurdlyLongSignatureIsRefusedBeforeItIsDecoded()
        => Assert.False(ReleaseKeys.Verify(FixtureManifest, new string('A', 4096), FixturePublicKey));

    /// <summary>
    /// A weaker curve is refused even though the key is the one compiled in — the point of compiling
    /// a key in is that its properties are decided at build time, not read out of the key at run time.
    /// </summary>
    [Fact]
    public void AKeyOnADifferentCurveIsRefused()
    {
        using ECDsa p521 = ECDsa.Create(ECCurve.NamedCurves.nistP521);
        string sig = Convert.ToBase64String(
            p521.SignData(FixtureManifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Assert.False(ReleaseKeys.Verify(
            FixtureManifest, sig, Convert.ToBase64String(p521.ExportSubjectPublicKeyInfo())));
    }

    // ── how the shipped build behaves, and how a keyed one would ────────────────────────────

    /// <summary>
    /// <b>This build ships with no key</b>, so nothing changes for anyone until one is generated —
    /// which is the only way to add the mechanism without stranding every installed copy on the day
    /// it lands. The moment <c>PublicKeySpkiBase64</c> is filled in, the demand becomes unconditional.
    /// </summary>
    [Fact]
    public void ShippedToday_NoKeyIsCompiledIn_SoNothingIsDemanded()
    {
        Assert.Equal("", ReleaseKeys.PublicKeySpkiBase64);
        Assert.False(ReleaseKeys.RequireSignedManifest);
        Assert.False(ReleaseKeys.Verify(FixtureManifest, FixtureSignature));   // no key ⇒ no
    }

    /// <summary>
    /// With no key compiled in the allow-list is the constraint; with one, any <c>https</c> host is
    /// acceptable — which is the whole of "mirroring becomes free" and of §15.4's migration.
    /// </summary>
    [Fact]
    public void WithoutAKey_OnlyTheAllowListedHostsAreAcceptable()
    {
        Assert.True(FeedUrlAllowList.IsAcceptable("https://api.github.com/x"));
        Assert.False(FeedUrlAllowList.IsAcceptable("https://mirror.example/x"));
        Assert.False(FeedUrlAllowList.IsAcceptable("http://api.github.com/x"));
    }

    [Fact]
    public void APersistedFeedUrlOffTheAllowListIsIgnoredOnAnUnkeyedBuild()
    {
        Assert.Equal(GitHubReleasesFeed.DefaultApiUrl,
                     UpdateScheduler.ResolveFeedUrl("https://mirror.example/releases"));

        Assert.Equal("https://api.github.com/repos/x/y/releases",
                     UpdateScheduler.ResolveFeedUrl("https://api.github.com/repos/x/y/releases"));
    }

    // ── the manifest's own rules ────────────────────────────────────────────────────────────

    /// <summary>
    /// A signed manifest that names no digest for the asset is a signature over nothing that matters:
    /// the signature proves the manifest, and only the hash carries that proof through to the bytes.
    /// It is refused rather than falling back to name matching, which would silently drop to the
    /// unsigned path.
    /// </summary>
    [Fact]
    public void ASignedManifestEntryWithNoDigest_IsNotSelected()
    {
        UpdateManifest m = Parse("""
            {"assets":[{"name":"circuitRF-1.0.0-arm64.dmg","url":"https://mirror.example/a.dmg","size":10}]}
            """);
        m.SignatureVerified = true;

        Assert.Null(m.Select("circuitRF-1.0.0-arm64.dmg"));
    }

    [Theory]
    [InlineData("abc")]                                                     // too short
    [InlineData("1AC30FD677168DFFA8E69A4C83256BC951FD9D50AB6D8774F60D279F84EE6406")]   // upper case
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]   // not hex
    public void AMalformedDigestIsNotADigest(string sha)
    {
        UpdateManifest m = Parse($$"""
            {"assets":[{"name":"a.dmg","url":"https://mirror.example/a.dmg","size":10,"sha256":"{{sha}}"}]}
            """);
        m.SignatureVerified = true;

        // Upper case is normalised by Select, so it is IsSha256Hex that must be strict about the
        // form it is handed; the entry itself is only rejected when the value is not 64 hex digits.
        Assert.False(UpdateManifest.IsSha256Hex(sha));
        if (sha.Length != 64) Assert.Null(m.Select("a.dmg"));
    }

    /// <summary>
    /// A SIGNED manifest may name a payload on any https host. An UNSIGNED one may not: with nothing
    /// but TLS behind it, the host IS the trust anchor and the compiled-in list is what constrains it.
    /// </summary>
    [Fact]
    public void OnlyASignedManifestMayNameAPayloadOffTheAllowList()
    {
        const string json = """
            {"assets":[{"name":"a.dmg","url":"https://mirror.example/a.dmg","size":10,
             "sha256":"1ac30fd677168dffa8e69a4c83256bc951fd9d50ab6d8774f60d279f84ee6406"}]}
            """;

        Assert.Null(Parse(json).Select("a.dmg"));

        UpdateManifest signed = Parse(json);
        signed.SignatureVerified = true;
        Assert.NotNull(signed.Select("a.dmg"));
    }

    [Fact]
    public void EvenSigned_APayloadUrlMustBeHttps()
    {
        UpdateManifest m = Parse("""
            {"assets":[{"name":"a.dmg","url":"http://mirror.example/a.dmg","size":10,
             "sha256":"1ac30fd677168dffa8e69a4c83256bc951fd9d50ab6d8774f60d279f84ee6406"}]}
            """);
        m.SignatureVerified = true;

        Assert.Null(m.Select("a.dmg"));
    }

    // ── the selector, end to end, against a fake feed ────────────────────────────────────────

    /// <summary>
    /// The signature is checked against the BYTES the feed served, so a manifest is verified through
    /// the same path a release actually takes rather than through a reconstruction of it.
    /// </summary>
    [Fact]
    public async Task AValidlySignedManifestIsRecognisedThroughTheSelector()
    {
        (byte[] manifest, string sig, string key) =
            SignFor("circuitRF-9.9.9-arm64.dmg", "https://mirror.example/a.dmg");

        UpdateCandidate? c = await SelectAsync(manifest, sig, new ReleaseTrust(key));

        Assert.NotNull(c);
        Assert.True(c!.FromManifest);
        Assert.True(c.ManifestSigned);

        // A signed manifest is what lets the payload live somewhere other than GitHub — §15.4's
        // migration and §15.5's mirroring, neither of which a shipped client can otherwise be told.
        Assert.Equal("https://mirror.example/a.dmg", c.Asset.Url);
        Assert.Equal("1ac30fd677168dffa8e69a4c83256bc951fd9d50ab6d8774f60d279f84ee6406", c.Asset.Sha256);
    }

    /// <summary>
    /// The demand is unconditional, not "check the signature if there is one" — an attacker who can
    /// publish a release can publish one with no manifest at all, and an updater that reads the
    /// absence of a signature as "nothing to check" has learned nothing from checking.
    /// </summary>
    [Fact]
    public async Task OnAKeyedBuild_AReleaseWithNoManifestIsNotACandidate()
    {
        const string asset = "circuitRF-9.9.9-arm64.dmg";
        ReleaseInfo release = CannedReleases.Release("9.9.9", assetNames: [asset]);

        var trust = new ReleaseTrust(FixturePublicKey);
        Assert.True(trust.RequireSignedManifest);

        UpdateCandidate? c = await UpdateSelector.SelectAsync(
            new FakeUpdateFeed([release]), [release], SemanticVersion.Parse("1.0.0"),
            includeBetas: false, "circuitRF", UpdatePlatform.MacOS, Architecture.Arm64,
            CancellationToken.None, trust);

        Assert.Null(c);

        // The SAME release IS a candidate on a build with no key — which is what makes adding the
        // key a forward migration rather than a flag day.
        Assert.NotNull(await UpdateSelector.SelectAsync(
            new FakeUpdateFeed([release]), [release], SemanticVersion.Parse("1.0.0"),
            includeBetas: false, "circuitRF", UpdatePlatform.MacOS, Architecture.Arm64,
            CancellationToken.None, new ReleaseTrust("")));
    }

    /// <summary>A keyed build refuses a manifest signed by a key that is not the one it carries.</summary>
    [Fact]
    public async Task OnAKeyedBuild_AManifestSignedByAnotherKeyIsNotACandidate()
    {
        (byte[] manifest, string sig, _) = SignFor("circuitRF-9.9.9-arm64.dmg", "https://mirror.example/a.dmg");

        Assert.Null(await SelectAsync(manifest, sig, new ReleaseTrust(FixturePublicKey)));
    }

    /// <summary>
    /// One flipped byte in the manifest and the signature no longer covers it — so on a keyed build
    /// there is no candidate, even though the release's own asset list still names a perfectly good
    /// payload on GitHub. That is the point: a keyed build does not fall back to the unsigned path.
    /// </summary>
    [Fact]
    public async Task OnAKeyedBuild_ATamperedManifestLeavesNoCandidateAtAll()
    {
        (byte[] manifest, string sig, string key) =
            SignFor("circuitRF-9.9.9-arm64.dmg", "https://mirror.example/a.dmg");
        manifest[5] ^= 0x01;

        Assert.Null(await SelectAsync(manifest, sig, new ReleaseTrust(key)));
    }

    /// <summary>
    /// On a build with NO key the manifest is never signed, so it may not name a payload off the
    /// allow-list — and the release's own asset list is what answers instead. This is exactly how
    /// every copy shipped so far behaves, and it must keep behaving that way.
    /// </summary>
    [Fact]
    public async Task OnAnUnkeyedBuild_AnOffAllowListManifestIsIgnoredAndNameMatchingAnswers()
    {
        (byte[] manifest, string sig, _) = SignFor("circuitRF-9.9.9-arm64.dmg", "https://mirror.example/a.dmg");

        UpdateCandidate? c = await SelectAsync(manifest, sig, new ReleaseTrust(""));

        Assert.NotNull(c);
        Assert.False(c!.FromManifest);
        Assert.False(c.ManifestSigned);
        Assert.StartsWith("https://objects.githubusercontent.com/", c.Asset.Url);
    }

    private static async Task<UpdateCandidate?> SelectAsync(byte[] manifest, string sig, ReleaseTrust trust)
    {
        const string asset = "circuitRF-9.9.9-arm64.dmg";

        ReleaseInfo release = CannedReleases.Release(
            "9.9.9", assetNames: [asset, UpdateManifest.AssetName, UpdateManifest.SignatureAssetName]);

        FakeUpdateFeed feed = FakeUpdateFeed.WithBytes([release], new Dictionary<string, byte[]>
        {
            [UpdateManifest.AssetName]          = manifest,
            [UpdateManifest.SignatureAssetName] = Encoding.UTF8.GetBytes(sig),
        });

        return await UpdateSelector.SelectAsync(
            feed, [release], SemanticVersion.Parse("1.0.0"), includeBetas: false,
            "circuitRF", UpdatePlatform.MacOS, Architecture.Arm64, CancellationToken.None, trust);
    }

    /// <summary>
    /// Signs a one-asset manifest with a throwaway key. Used only where the test needs the SELECTOR's
    /// behaviour rather than the wire format — the wire format is pinned by the ReleaseSigner fixture
    /// above, which is the half a self-signing helper could not prove.
    /// </summary>
    private static (byte[] Manifest, string Signature, string PublicKey) SignFor(string name, string url)
    {
        string json = $$"""
            {"assets":[{"name":"{{name}}","url":"{{url}}","size":1234,
             "sha256":"1ac30fd677168dffa8e69a4c83256bc951fd9d50ab6d8774f60d279f84ee6406"}]}
            """;

        byte[] bytes = Encoding.UTF8.GetBytes(json);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string sig = Convert.ToBase64String(
            key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        return (bytes, sig, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static UpdateManifest Parse(string json)
    {
        UpdateManifest? m = UpdateManifest.TryParse(json);
        Assert.NotNull(m);
        return m!;
    }
}
