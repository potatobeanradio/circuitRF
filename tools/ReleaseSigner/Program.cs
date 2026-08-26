using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Tools.ReleaseSigner;

/// <summary>
/// The release-signing half of docs/design/auto-update.md §15.5 — keygen, manifest, sign, verify.
///
/// <para>It exists because the client half is useless without it: an updater that demands a signed
/// manifest and a release process with no way to produce one is an updater that stops updating.
/// Three commands, in the order a release uses them.</para>
///
/// <para><b>It references nothing else in this repository</b>, per the root CLAUDE.md's rule for
/// tools/ — it implements the format the client reads rather than sharing code with it, so the two
/// agreeing is evidence rather than a tautology.</para>
/// </summary>
internal static class Program
{
    private const string ManifestName  = "update-manifest.json";
    private const string SignatureName = ManifestName + ".sig";

    private static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        try
        {
            return args[0] switch
            {
                "keygen"   => KeyGen(args),
                "manifest" => Manifest(args),
                "sign"     => Sign(args),
                "verify"   => Verify(args),
                _          => Usage(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            ReleaseSigner - circuitRF release manifest signing (design/auto-update.md 15.5)

              keygen <private-key.pem>
                  Creates an ECDSA P-256 key pair. Writes the PRIVATE key to the named file and
                  prints the PUBLIC key for src/Ui/Updates/ReleaseKeys.cs.

                  The private key file is written with owner-only permissions. Keep it off this
                  repository, off the build machine if you can, and backed up: losing it means every
                  installed copy stops updating until they reinstall by hand.

              manifest <dist-dir> [-o update-manifest.json] [--base-url <url>] [--min-from <version>]
                  Builds a manifest from the artifacts in <dist-dir>, computing each SHA-256.

              sign <update-manifest.json> <private-key.pem>
                  Writes update-manifest.json.sig beside it. Upload BOTH with the release.

              verify <update-manifest.json> <update-manifest.json.sig> <public-key-base64>
                  What the client does. Run it against the files you are about to upload.
            """);
        return 1;
    }

    // ── keygen ───────────────────────────────────────────────────────────────────────────────

    private static int KeyGen(string[] args)
    {
        if (args.Length < 2) return Usage();
        string path = args[1];

        if (File.Exists(path))
        {
            // Overwriting a release private key is not a thing to do by accident: every client that
            // has the matching public key compiled in is stranded the moment it is gone.
            Console.Error.WriteLine($"error: {path} already exists. Refusing to overwrite a private key.");
            return 1;
        }

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        string pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        Console.WriteLine($"Private key written to {path} (owner-read-only).");
        Console.WriteLine();
        Console.WriteLine("Paste this into src/Ui/Updates/ReleaseKeys.cs as PublicKeySpkiBase64:");
        Console.WriteLine();
        Console.WriteLine($"    public const string PublicKeySpkiBase64 = \"{pub}\";");
        Console.WriteLine();
        Console.WriteLine("From the build that carries it onward, an UNSIGNED release is not offered at all.");
        Console.WriteLine("Cut that build BEFORE you need it, and sign every release after it.");
        return 0;
    }

    // ── manifest ─────────────────────────────────────────────────────────────────────────────

    private static int Manifest(string[] args)
    {
        if (args.Length < 2) return Usage();

        string dir     = args[1];
        string outPath = Arg(args, "-o") ?? Path.Combine(dir, ManifestName);
        string? baseUrl = Arg(args, "--base-url");
        string? minFrom = Arg(args, "--min-from");
        string? feedUrl = Arg(args, "--feed-url");

        var assets = new JsonArray();

        foreach (string f in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(f);
            if (name is ManifestName or SignatureName) continue;

            var entry = new JsonObject
            {
                ["name"]   = name,
                ["url"]    = baseUrl is null ? name : baseUrl.TrimEnd('/') + "/" + name,
                ["size"]   = new FileInfo(f).Length,
                ["sha256"] = Sha256(f),
            };
            assets.Add(entry);
        }

        var manifest = new JsonObject { ["assets"] = assets };
        if (minFrom is not null) manifest["minimumUpgradableFrom"] = minFrom;
        if (feedUrl is not null) manifest["feedUrl"] = feedUrl;

        // No BOM and no trailing newline games: the signature covers these bytes exactly, so what is
        // written here is what is signed and what the client hashes.
        byte[] bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(outPath, bytes);

        Console.WriteLine($"{outPath}: {assets.Count} assets.");
        if (baseUrl is null)
            Console.WriteLine("NOTE: no --base-url, so each url is a bare file name. Give the real URLs before uploading.");
        return 0;
    }

    // ── sign / verify ────────────────────────────────────────────────────────────────────────

    private static int Sign(string[] args)
    {
        if (args.Length < 3) return Usage();

        byte[] manifest = File.ReadAllBytes(args[1]);

        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(args[2]));

        byte[] sig = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        string outPath = args[1] + ".sig";
        File.WriteAllText(outPath, Convert.ToBase64String(sig));

        Console.WriteLine($"{outPath} written. Upload it with {Path.GetFileName(args[1])}.");
        Console.WriteLine("Both must be attached to the release, under exactly those names.");
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 4) return Usage();

        byte[] manifest = File.ReadAllBytes(args[1]);
        byte[] sig      = Convert.FromBase64String(File.ReadAllText(args[2]).Trim());

        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(args[3].Trim()), out _);

        bool ok = key.VerifyData(manifest, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Console.WriteLine(ok ? "OK  the signature is valid." : "BAD the signature does not verify.");
        return ok ? 0 : 1;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Sha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
