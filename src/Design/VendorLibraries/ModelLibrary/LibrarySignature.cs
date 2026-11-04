using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary><c>library.sig</c> — who signed a library, with what key, and the signature (brief vendorlib-15 §5a).</summary>
public sealed record LibrarySignatureFile
{
    public const string FormatName = "circuitrf-library-signature";

    public string Format    { get; init; } = FormatName;
    /// <summary>Equals <c>library.clib</c>'s <c>Publisher</c>.</summary>
    public string Publisher { get; init; } = "";
    /// <summary>The P-256 public key, as SubjectPublicKeyInfo DER, base64.</summary>
    public string PublicKey { get; init; } = "";
    /// <summary>ECDSA P-256 over SHA-256 of the exact bytes of <c>library.sums</c>, as the 64-byte r‖s pair, base64.</summary>
    public string Signature { get; init; } = "";

    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>What a signature says on its own, before this user's trust is consulted.</summary>
public enum LibrarySignatureCheck
{
    /// <summary>No <c>library.sig</c>.</summary>
    Unsigned,
    /// <summary>The signature verifies over <c>library.sums</c>, and every line still matches the library.</summary>
    Intact,
    /// <summary>The signature verifies; some parts no longer hash to what was signed.</summary>
    Changed,
    /// <summary>The signature does not verify, or the files around it are not what a signature needs.</summary>
    DoesNotVerify,
}

/// <summary>The signature half of a library's state, read from its files alone (no trust store) — what an index
/// build records, so the row can be judged against whichever user's trust store the caller holds.</summary>
public sealed record LibrarySignatureInspection(
    LibrarySignatureCheck Check,
    string Publisher,
    string Fingerprint,
    IReadOnlyList<string> ChangedParts,
    string? Reason)
{
    public static readonly LibrarySignatureInspection None = new(LibrarySignatureCheck.Unsigned, "", "", [], null);
}

/// <summary>The six states of brief vendorlib-15 §5c's table.</summary>
public enum LibrarySignatureState
{
    Unsigned,
    SignedTrusted,
    SignedUntrusted,
    ChangedSinceSigning,
    DoesNotVerify,
    TrustedNameDifferentKey,
}

/// <summary>A library's signature, judged against one user's trust store: the state, the row's sentence, and what
/// <c>check</c> reports.</summary>
public sealed record LibrarySignatureVerdict(
    LibrarySignatureState State,
    LibrarySignatureInspection Inspection,
    bool Trusted)
{
    public string Publisher => Inspection.Publisher;
    public string Fingerprint => Inspection.Fingerprint;
    public IReadOnlyList<string> ChangedParts => Inspection.ChangedParts;

    /// <summary>The sentence on the library's row (Add Vendor Library, Manage Libraries); null for an unsigned one.</summary>
    public string? RowText => State switch
    {
        LibrarySignatureState.Unsigned                => null,
        LibrarySignatureState.SignedTrusted           => $"Signed by {Publisher}",
        LibrarySignatureState.SignedUntrusted         => $"Signed by {Publisher}, key {Fingerprint}. Trust this publisher?",
        LibrarySignatureState.ChangedSinceSigning     =>
            $"Signed by {Publisher}. {ChangedParts.Count} part{(ChangedParts.Count == 1 ? "" : "s")} changed since signing: {string.Join(", ", ChangedParts)}",
        LibrarySignatureState.DoesNotVerify           => "The signature does not match this library's contents.",
        LibrarySignatureState.TrustedNameDifferentKey => $"{Publisher} was trusted with a different key. This library may not come from them.",
        _ => null,
    };

    /// <summary>The row offers Trust / Not now.</summary>
    public bool AsksForTrust => State == LibrarySignatureState.SignedUntrusted
                                || State == LibrarySignatureState.ChangedSinceSigning && !Trusted;
}

/// <summary>
/// Signed model libraries (brief vendorlib-15 §5): <c>library.sums</c> lists what was signed, <c>library.sig</c>
/// carries the publisher, the public key and the signature. <b>The one implementation</b> — <c>circuitrf sign</c>,
/// <c>check</c> and the library rows all call it.
///
/// <para><b>Signing is information, never a gate</b> (D4): nothing here stops a run. What may stop a maker's code
/// from running is the external-worker policy, unchanged. <b>Trust is per user</b> (D6): the trust store is an
/// argument, the caller's per-user preferences, and is never read from or written to a workspace.</para>
///
/// <para><b>The byte rules</b>, as the format page publishes them so another tool can verify a library with no
/// circuitRF code: <c>library.sums</c> is UTF-8, LF line endings, one line per model folder
/// (<c>sha256:&lt;hex&gt;  &lt;path&gt;\n</c>, the folder's <see cref="ModelPartHash"/> and its path relative to
/// the library with <c>/</c> separators) plus one for <c>library.clib</c> (its SHA-256 after CR LF → LF), sorted
/// ordinally by path. The signature is ECDSA P-256 with SHA-256 over those exact bytes, in the IEEE P1363 form
/// (r then s, 32 bytes each), base64. The fingerprint is the SHA-256 of the SubjectPublicKeyInfo DER, its first 16
/// bytes as lower-case hex pairs joined by colons.</para>
/// </summary>
public static class LibrarySignature
{
    public const string SumsFileName = "library.sums";
    public const string SigFileName  = "library.sig";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ── library.sums ─────────────────────────────────────────────────────────

    /// <summary>The text <c>library.sums</c> holds for the library as it is now.</summary>
    public static string Sums(string libraryDir)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryDir));
        var lines = new List<(string Path, string Hash)>();
        var models = ModelFolder.Under(root);
        foreach (string dir in models)
        {
            string rel = Rel(root, dir);
            lines.Add((rel, ModelPartHash.Compute(dir)));
            // part.cpart is outside its folder's PublishedHash (it holds it), and it is what a placement reads its
            // defaults, pins and builds from: signed on a line of its own.
            string cpart = Path.Combine(dir, ModelFolder.PartFileName);
            if (File.Exists(cpart)) lines.Add((rel == "." ? ModelFolder.PartFileName : rel + "/" + ModelFolder.PartFileName, FileHash(cpart)));
        }
        string clib = Path.Combine(root, ModelFolder.LibraryFileName);
        if (File.Exists(clib)) lines.Add((ModelFolder.LibraryFileName, FileHash(clib)));
        // Every other file the library holds — a SPICE part the index lists, a document — so nothing a user places from
        // a signed library is outside what was signed.
        foreach (string f in LooseFiles(root, models)) lines.Add((Rel(root, f), FileHash(f)));
        var sb = new StringBuilder();
        foreach (var (path, hash) in lines.OrderBy(l => l.Path, StringComparer.Ordinal))
            sb.Append(hash).Append("  ").Append(path).Append('\n');
        return sb.ToString();
    }

    private static string Rel(string root, string dir) => Path.GetRelativePath(root, dir).Replace('\\', '/');

    /// <summary><c>sha256:</c> and the hex SHA-256 of one file, CR LF → LF for a text file (<see cref="ModelPartHash.IsText"/>,
    /// and <c>library.clib</c> and <c>part.cpart</c>, which are JSON text).</summary>
    private static string FileHash(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        if (ModelPartHash.IsText(path) || name == ModelFolder.LibraryFileName || name == ModelFolder.PartFileName)
            bytes = ModelPartHash.NormaliseLineEndings(bytes);
        return ModelPartHash.Prefix + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>The files under the library that are in no model folder: not <c>library.clib</c>, <c>library.sums</c> or
    /// <c>library.sig</c> at the root, nothing whose name starts with <c>.</c> or under such a folder, and no
    /// operating-system litter.</summary>
    private static List<string> LooseFiles(string root, IReadOnlyList<string> models)
    {
        var inModels = new HashSet<string>(models, DocumentFileRefs.PathComparer);
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            if (inModels.Contains(dir)) continue;
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith('.') || ModelPartHash.IsLitter(name)) continue;
                if (dir == root && name is ModelFolder.LibraryFileName or SumsFileName or SigFileName) continue;
                found.Add(f);
            }
            foreach (string d in Directory.EnumerateDirectories(dir))
                if (!Path.GetFileName(d).StartsWith('.') && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(d);
        }
        return found;
    }

    /// <summary><c>library.sums</c> read back as path → hash. Null when a line is not of the stated form.</summary>
    public static Dictionary<string, string>? ParseSums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            int sep = line.IndexOf("  ", StringComparison.Ordinal);
            if (sep <= 0 || !line.StartsWith(ModelPartHash.Prefix, StringComparison.Ordinal)) return null;
            map[line[(sep + 2)..]] = line[..sep];
        }
        return map;
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    /// <summary>SHA-256 of the SubjectPublicKeyInfo, first 16 bytes, as colon-separated lower-case hex.</summary>
    public static string Fingerprint(byte[] subjectPublicKeyInfo)
        => string.Join(':', SHA256.HashData(subjectPublicKeyInfo).Take(16).Select(b => b.ToString("x2")));

    /// <summary>Why <paramref name="keyPath"/> may not be used beside <paramref name="libraryDir"/>, or null: a private
    /// key inside the library would be published with it, the one mistake that cannot be undone afterwards.</summary>
    public static string? KeyInsideLibrary(string keyPath, string libraryDir)
    {
        string key = Path.GetFullPath(keyPath);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryDir));
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return key.StartsWith(root + Path.DirectorySeparatorChar, cmp)
            ? $"The key '{key}' is inside the library '{root}'. A private key in a library is published with it; keep it outside."
            : null;
    }

    /// <summary>
    /// Writes a new P-256 private key to <paramref name="path"/> as PKCS#8 PEM and returns its public fingerprint, or
    /// null with the refusal: an existing file is never overwritten.
    /// </summary>
    public static string? NewKey(string path, out string? refusal)
    {
        refusal = null;
        string full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            refusal = $"'{full}' already exists, and a key file is never overwritten. Name a new file.";
            return null;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportPkcs8PrivateKeyPem();
        try
        {
            if (Path.GetDirectoryName(full) is { } d) Directory.CreateDirectory(d);
            using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                w.Write(pem + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refusal = $"'{full}' could not be written ({ex.Message}).";
            return null;
        }
        return Fingerprint(key.ExportSubjectPublicKeyInfo());
    }

    // ── Sign ─────────────────────────────────────────────────────────────────

    /// <summary>What a signing did.</summary>
    public sealed record SignResult(string? SumsPath, string? SigPath, string Fingerprint, int Parts, string? Refusal)
    {
        public bool Succeeded => Refusal is null;
    }

    /// <summary>
    /// Recomputes every model folder's hash, writes <c>library.sums</c> and signs it with the P-256 key at
    /// <paramref name="keyPath"/> (brief vendorlib-15 R-vl15-3a). Refuses a key inside the library, a library with no
    /// <c>library.clib</c> Publisher to sign as, and a library any model folder of which fails check at error severity
    /// — a signature over a broken library certifies the breakage.
    /// </summary>
    public static SignResult Sign(string libraryDir, string keyPath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryDir));
        SignResult Refuse(string why) => new(null, null, "", 0, why);

        if (!Directory.Exists(root)) return Refuse($"'{root}' is not a folder.");
        if (KeyInsideLibrary(keyPath, root) is { } inside) return Refuse(inside);
        if (!File.Exists(keyPath)) return Refuse($"The key '{Path.GetFullPath(keyPath)}' does not exist. Make one with --new-key.");

        var (clib, clibWhy) = ModelLibraryManifest.Read(Path.Combine(root, ModelFolder.LibraryFileName));
        if (clib is null)
            return Refuse(File.Exists(Path.Combine(root, ModelFolder.LibraryFileName))
                ? clibWhy!
                : $"'{root}' has no {ModelFolder.LibraryFileName}, so it names no publisher to sign as.");
        if (clib.Publisher.Trim().Length == 0)
            return Refuse($"{ModelFolder.LibraryFileName} has an empty \"Publisher\"; a signature says who published the library.");

        var folders = ModelFolder.Under(root);
        if (folders.Count == 0) return Refuse($"'{root}' holds no model folder to sign.");
        var errors = folders.SelectMany(ModelFolderCheck.Validate).Concat(ModelLibraryCheck.Validate(root))
                            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            return Refuse("The library does not check clean, and a signature over a broken library certifies the breakage: "
                          + string.Join(" ", errors.Take(10).Select(e => e.Render())));

        ECDsa key;
        try
        {
            key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(keyPath));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or IOException)
        { return Refuse($"'{Path.GetFullPath(keyPath)}' is not a private key this can read ({ex.Message})."); }

        using (key)
        {
            if (key.KeySize != 256) return Refuse("The key is not a P-256 key; a library is signed with ECDSA P-256.");
            string sums = Sums(root);
            byte[] bytes = Encoding.UTF8.GetBytes(sums);
            byte[] spki = key.ExportSubjectPublicKeyInfo();
            var sig = new LibrarySignatureFile
            {
                Publisher = clib.Publisher,
                PublicKey = Convert.ToBase64String(spki),
                Signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            };
            string sumsPath = Path.Combine(root, SumsFileName), sigPath = Path.Combine(root, SigFileName);
            File.WriteAllBytes(sumsPath, bytes);
            AtomicFile.WriteAllText(sigPath, JsonSerializer.Serialize(sig, Json) + "\n");
            return new SignResult(sumsPath, sigPath, Fingerprint(spki), folders.Count, null);
        }
    }

    // ── Verify ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What the library's signature files say, with no trust store: unsigned, intact, changed (which parts), or not
    /// verifying. One hash pass over every model folder when the library is signed; nothing at all when it is not.
    /// </summary>
    public static LibrarySignatureInspection Inspect(string libraryDir)
    {
        // Never throws (the library index calls this for every library): an unreadable file is a signature that cannot
        // be verified, said as one.
        try { return InspectCore(libraryDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(LibrarySignatureCheck.DoesNotVerify, "", "", [], $"The library could not be read to verify its signature ({ex.Message}).");
        }
    }

    private static LibrarySignatureInspection InspectCore(string libraryDir)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryDir));
        string sigPath = Path.Combine(root, SigFileName), sumsPath = Path.Combine(root, SumsFileName);
        if (!File.Exists(sigPath)) return LibrarySignatureInspection.None;

        LibrarySignatureInspection Fails(string why, string publisher = "", string fp = "")
            => new(LibrarySignatureCheck.DoesNotVerify, publisher, fp, [], why);

        LibrarySignatureFile? sig;
        try { sig = JsonSerializer.Deserialize<LibrarySignatureFile>(File.ReadAllText(sigPath)); }
        catch (Exception ex) when (ex is JsonException or IOException) { return Fails($"{SigFileName} cannot be read ({ex.Message})."); }
        if (sig is null || sig.Format != LibrarySignatureFile.FormatName)
            return Fails($"{SigFileName} is not a {LibrarySignatureFile.FormatName} file.");
        if (sig.PublicKey is null || sig.Signature is null || sig.Publisher is null)
            return Fails($"{SigFileName} has no {(sig.PublicKey is null ? "PublicKey" : sig.Signature is null ? "Signature" : "Publisher")}.");

        byte[] spki, signature;
        try { (spki, signature) = (Convert.FromBase64String(sig.PublicKey), Convert.FromBase64String(sig.Signature)); }
        catch (FormatException) { return Fails($"{SigFileName}'s key or signature is not base64.", sig.Publisher); }
        string fp = Fingerprint(spki);

        if (!File.Exists(sumsPath)) return Fails($"{SigFileName} is there and {SumsFileName}, what it signs, is not.", sig.Publisher, fp);
        byte[] sums = File.ReadAllBytes(sumsPath);

        bool ok;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            // The signed bytes are LF; a checkout that wrote library.sums with CR LF has not changed what was signed.
            ok = key.KeySize == 256
                 && (key.VerifyData(sums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                     || key.VerifyData(ModelPartHash.NormaliseLineEndings(sums), signature, HashAlgorithmName.SHA256,
                                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        catch (CryptographicException) { ok = false; }
        if (!ok) return Fails($"The signature in {SigFileName} does not verify over {SumsFileName}.", sig.Publisher, fp);

        var (clib, _) = ModelLibraryManifest.Read(Path.Combine(root, ModelFolder.LibraryFileName));
        if (clib is null || !string.Equals(clib.Publisher, sig.Publisher, StringComparison.Ordinal))
            return Fails($"{SigFileName} names the publisher '{sig.Publisher}' and {ModelFolder.LibraryFileName} names '{clib?.Publisher}'.", sig.Publisher, fp);

        var signedMap = ParseSums(Encoding.UTF8.GetString(ModelPartHash.NormaliseLineEndings(sums)));
        var nowMap = ParseSums(Sums(root));
        if (signedMap is null || nowMap is null) return Fails($"{SumsFileName} is not one line per signed item.", sig.Publisher, fp);
        // library.clib is the publisher's own statement: a change there is not a part changed, it is the signature
        // no longer saying what the library says.
        if (signedMap.GetValueOrDefault(ModelFolder.LibraryFileName) != nowMap.GetValueOrDefault(ModelFolder.LibraryFileName))
            return Fails($"{ModelFolder.LibraryFileName} has changed since signing.", sig.Publisher, fp);

        var changed = signedMap.Keys.Union(nowMap.Keys)
            .Where(p => p != ModelFolder.LibraryFileName && signedMap.GetValueOrDefault(p) != nowMap.GetValueOrDefault(p))
            .Select(p => p == ModelFolder.PartFileName ? "."
                       : p.EndsWith("/" + ModelFolder.PartFileName, StringComparison.Ordinal) ? p[..^(ModelFolder.PartFileName.Length + 1)] : p)
            .Select(p => p.Contains('/') ? p[(p.LastIndexOf('/') + 1)..] : p)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new LibrarySignatureInspection(changed.Count == 0 ? LibrarySignatureCheck.Intact : LibrarySignatureCheck.Changed,
                                              sig.Publisher, fp, changed, null);
    }

    /// <summary>
    /// Brief vendorlib-15 §5c: the library at <paramref name="libraryDir"/> against <paramref name="trustStore"/>
    /// (publisher name → fingerprint, the user's own). A publisher this user trusted with a different key is the
    /// strongest warning there is.
    /// </summary>
    public static LibrarySignatureVerdict Verify(string libraryDir, IReadOnlyDictionary<string, string> trustStore)
        => Judge(Inspect(libraryDir), trustStore);

    public static LibrarySignatureVerdict Judge(LibrarySignatureInspection inspection, IReadOnlyDictionary<string, string> trustStore)
    {
        if (inspection.Check == LibrarySignatureCheck.Unsigned) return new(LibrarySignatureState.Unsigned, inspection, false);
        if (inspection.Check == LibrarySignatureCheck.DoesNotVerify) return new(LibrarySignatureState.DoesNotVerify, inspection, false);

        bool known = trustStore.TryGetValue(inspection.Publisher, out string? trustedFp);
        if (known && !string.Equals(trustedFp, inspection.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return new(LibrarySignatureState.TrustedNameDifferentKey, inspection, false);
        if (inspection.Check == LibrarySignatureCheck.Changed) return new(LibrarySignatureState.ChangedSinceSigning, inspection, known);
        return new(known ? LibrarySignatureState.SignedTrusted : LibrarySignatureState.SignedUntrusted, inspection, known);
    }

    /// <summary><c>check</c>'s findings for a library's signature: info when signed and intact (trusted or not),
    /// a warning per changed part, an error when it does not verify or a trusted name arrives with another key.</summary>
    public static IReadOnlyList<Diagnostic> Findings(string libraryDir, IReadOnlyDictionary<string, string> trustStore)
    {
        var v = Verify(libraryDir, trustStore);
        string sig = Path.Combine(Path.GetFullPath(libraryDir), SigFileName);
        return v.State switch
        {
            LibrarySignatureState.Unsigned => [],
            LibrarySignatureState.SignedTrusted or LibrarySignatureState.SignedUntrusted =>
                [Diagnostic.Create("check.library.signed", DiagnosticSeverity.Info, "{path}: {text}", ("path", sig), ("text", v.RowText!),
                                   ("publisher", v.Publisher), ("fingerprint", v.Fingerprint))],
            LibrarySignatureState.ChangedSinceSigning =>
                [.. v.ChangedParts.Select(p => Diagnostic.Create("check.library.changed-since-signing", DiagnosticSeverity.Warning,
                        "{path}: {part} changed since {publisher} signed the library.", ("path", sig), ("part", p), ("publisher", v.Publisher)))],
            LibrarySignatureState.DoesNotVerify =>
                [Diagnostic.Create("check.library.signature-does-not-verify", DiagnosticSeverity.Error,
                    "{path}: The signature does not match this library's contents ({reason})", ("path", sig),
                    ("reason", v.Inspection.Reason ?? ""))],
            _ =>
                [Diagnostic.Create("check.library.publisher-key-changed", DiagnosticSeverity.Error, "{path}: {text}",
                    ("path", sig), ("text", v.RowText!), ("fingerprint", v.Fingerprint))],
        };
    }
}

/// <summary>
/// The publishers this user trusts, name → key fingerprint (brief vendorlib-15 D6, §5c) — per user, in the per-user
/// preferences beside the vendor-library list, and NEVER in a workspace: a workspace that told circuitRF which keys to
/// trust would let anyone who sends one vouch for themselves.
///
/// <para>Recorded by the application (<c>AppPreferences.TrustedPublishers</c>, which stays in <c>src/Ui</c>); read
/// here, read-only, as <c>GdsfactoryInterpreterPreferences</c> reads its table, so a headless <c>check</c> judges a
/// library against the same trust the user gave in the application. A headless run never prompts and never
/// writes.</para>
/// </summary>
public static class PublisherTrust
{
    /// <summary>The <c>preferences.json</c> key the trust decisions are recorded under.</summary>
    public const string PreferenceKey = "trusted_publishers";

    /// <summary>The table as recorded; a missing, truncated or wrong-shaped file is an empty one.</summary>
    public static IReadOnlyDictionary<string, string> FromRecordedPreferences()
        => FromPreferencesFile(UserStateDirectory.PreferencesPath);

    internal static IReadOnlyDictionary<string, string> FromPreferencesFile(string path)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return table;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(PreferenceKey, out var rows)
                && rows.ValueKind == JsonValueKind.Object)
                foreach (var row in rows.EnumerateObject())
                    if (row.Value.ValueKind == JsonValueKind.String && row.Value.GetString() is { Length: > 0 } fp)
                        table[row.Name] = fp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return table;
    }
}
