using System.Linq;
using System.Text.Json;

namespace CircuitRF.Design.Layout;

/// <summary>Where one catalog entry's bytes live.</summary>
public enum TechnologyOrigin
{
    /// <summary>Inside the assembly — one of <see cref="ShippedTechnologies"/>.</summary>
    Shipped,

    /// <summary>A <c>.ctech</c> the user put in <see cref="TechnologyCatalog.UserDirectory"/>.</summary>
    User,
}

/// <summary>
/// One technology the New Workspace picker offers. <see cref="Id"/> is the file stem for BOTH origins
/// — it is what a <c>--tech</c> argument names, what the default preference records, and what the
/// created workspace's <c>tech/</c> file is called — so the two origins share one namespace and a
/// collision between them is refused rather than resolved (see <see cref="TechnologyCatalog.Install"/>).
/// <see cref="Name"/> is the technology's own authored name, which is what the picker DISPLAYS.
/// </summary>
public sealed record TechnologyCatalogEntry(
    string           Id,
    string           Name,
    TechnologyOrigin Origin,
    string?          ResourceName,
    string?          FilePath)
{
    /// <summary>True for a technology the user installed, which is the only kind that can be removed.</summary>
    public bool IsUserInstalled => Origin == TechnologyOrigin.User;
}

/// <summary>What <see cref="TechnologyCatalog.Install"/> did, or why it did nothing.</summary>
/// <param name="Entry">The installed entry, or null when <paramref name="Refusal"/> is set.</param>
/// <param name="Refusal">One sentence naming what to do instead, or null on success.</param>
public sealed record TechnologyInstallResult(TechnologyCatalogEntry? Entry, string? Refusal);

/// <summary>
/// Every technology a new workspace can be created FROM: the ones inside the assembly
/// (<see cref="ShippedTechnologies"/>) plus the ones the user installed into
/// <see cref="UserDirectory"/>, and which of them is the default.
///
/// <para><b>Why a second type rather than widening <c>ShippedTechnologies</c>.</b> That type means
/// exactly one thing — what this binary carries — and its "ship" gate test, its round-trip tests and
/// a dozen fixtures depend on that meaning. A user file appearing in <c>ShippedTechnologies.All</c>
/// would make the gate that proves circuitRF's own four technologies parse depend on what is in
/// somebody's home directory. So the catalog is the union, and the shipped list stays the shipped
/// list.</para>
///
/// <para><b>A user technology is an ordinary <c>.ctech</c> file in an ordinary folder</b>, read
/// through <see cref="TechPersistence.Deserialize"/> exactly as a workspace's own is. There is no
/// registry, no index and no manifest: the directory listing IS the list, so a technology can be put
/// there by a fab, a script or a person copying a file, and removing it is deleting it. That is the
/// same arrangement <c>ThemeResolver.UserThemesDir</c> and <c>TemplateManager.UserTemplatesDir</c>
/// already have — technologies were the one asset kind that never got the middle rung of that chain.
/// </para>
///
/// <para><b>It lives in <c>src/Design</c>, below the firewall</b>, for the reason
/// <c>ShippedTechnologies</c>' own header gives: <c>circuitrf new workspace</c> creates a workspace
/// with no display attached, and CLAUDE.md's rule for that verb is that every default is the GUI
/// dialog's. A catalog the CLI could not see would make <c>--tech</c> reject a technology the dialog
/// offers, and would make a no-<c>--tech</c> run ignore the default the user chose — a headless
/// default that differs from the dialog's is a second product.</para>
///
/// <para><b>Nothing here is cached.</b> The directory changes while the application is running — the
/// Settings tab adds and removes files in it — and a cache would need invalidating from every one of
/// those places. Each call parses a handful of small JSON files, which is what the New Workspace
/// dialog already did on every open.</para>
/// </summary>
public static class TechnologyCatalog
{
    /// <summary>
    /// The preferences key holding the id of the technology a new workspace opens on. Shared with the
    /// Settings ▸ Technology tab's writer, exactly as <c>RevisionIdentity</c>' two keys are shared
    /// with RC-4's.
    ///
    /// <para><b>This type READS the preference and never writes it.</b> <c>AppPreferencesIo</c> holds
    /// the one in-process copy of that file and writes it whole; a second writer down here would
    /// silently drop whatever the dialog had changed but not yet flushed. So the arrangement is the
    /// one <c>RevisionIdentity</c> already has — the value is read below the firewall and written by
    /// the type that owns the dialog, and the two agree by this constant.</para>
    /// </summary>
    public const string DefaultIdPreferenceKey = "default_technology_id";

    private const string Extension = ".ctech";

    /// <summary>
    /// Where a user's own technologies live: <c>&lt;per-user state&gt;/technologies</c>. Not created
    /// here — <see cref="Install"/> creates it when there is something to put in it, so a machine
    /// where nobody has added one has no empty folder to explain.
    /// </summary>
    public static string UserDirectory => UserStateDirectory.SubDir("technologies");

    /// <summary>
    /// Shipped and user technologies together, sorted by <see cref="TechnologyCatalogEntry.Id"/> —
    /// one list, in one deterministic order, never enumeration order.
    ///
    /// <para><b>A user file that cannot be read is SKIPPED, not thrown.</b> This is on the path that
    /// opens the New Workspace dialog and the path that runs <c>new workspace</c>; a half-copied or
    /// hand-edited file in the folder must not be able to stop either. The Settings tab is where a
    /// bad file is reported, because that is where somebody is looking at it.</para>
    /// </summary>
    public static IReadOnlyList<TechnologyCatalogEntry> All
    {
        get
        {
            var list = new List<TechnologyCatalogEntry>();

            foreach (var shipped in ShippedTechnologies.All)
                list.Add(new TechnologyCatalogEntry(
                    shipped.Id, ShippedTechnologies.Load(shipped).Name,
                    TechnologyOrigin.Shipped, shipped.ResourceName, FilePath: null));

            foreach (var path in UserFiles())
            {
                if (ReadUserEntry(path) is { } entry && list.All(e => !IdEquals(e.Id, entry.Id)))
                    list.Add(entry);
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }
    }

    /// <summary>The entry with this id, or null. Ids compare case-insensitively because they are file
    /// stems, and two of the three platforms circuitRF ships on do not distinguish them.</summary>
    public static TechnologyCatalogEntry? Find(string? id)
        => id is not { Length: > 0 } ? null : All.FirstOrDefault(e => IdEquals(e.Id, id));

    /// <summary>
    /// The technology a caller that says nothing gets: the user's own choice when they have made one
    /// and it still names something, otherwise <see cref="ShippedTechnologies.DefaultId"/>.
    ///
    /// <para><b>A preference naming a technology that is no longer installed FALLS BACK rather than
    /// failing</b> — removing a file must not be able to break File ▸ New Workspace, and the picker
    /// showing a different pre-selection is a visible, self-explaining state.</para>
    /// </summary>
    public static string DefaultId
    {
        get
        {
            if (PreferredDefaultId() is { } preferred && Find(preferred) is not null) return preferred;
            return ShippedTechnologies.DefaultId;
        }
    }

    /// <summary>
    /// What the preference file says, verbatim, or null when it says nothing — WITHOUT checking that
    /// it still names an installed technology. The Settings tab uses this to tell "never chosen" from
    /// "chose one that has since been removed"; everything else wants <see cref="DefaultId"/>.
    ///
    /// <para><b>Never throws.</b> A missing, truncated or wrong-shaped preferences file is an absent
    /// one — the rule <c>RevisionIdentity.FromPreferences</c> states, for the same reason: this is
    /// called on the path that creates a workspace.</para>
    /// </summary>
    public static string? PreferredDefaultId()
    {
        try
        {
            string path = UserStateDirectory.PreferencesPath;
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(DefaultIdPreferenceKey, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) return null;

            return v.GetString()?.Trim() is { Length: > 0 } s ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The parsed technology, from wherever this entry's bytes are.</summary>
    public static Technology Load(TechnologyCatalogEntry entry)
        => entry.Origin == TechnologyOrigin.Shipped
            ? ShippedTechnologies.Load(new ShippedTechnologyEntry(entry.Id, entry.ResourceName!))
            : TechPersistence.Deserialize(File.ReadAllText(entry.FilePath!));

    /// <summary>
    /// The still-authored JSON bytes — what <see cref="Workspace.WorkspaceCreate"/> writes verbatim
    /// into the new workspace's own <c>tech/</c> folder. A user technology copies exactly as a shipped
    /// one does, so from the moment the workspace exists the two are indistinguishable, which is the
    /// property that makes a workspace portable to a machine that never had the file.
    /// </summary>
    public static string LoadRawJson(TechnologyCatalogEntry entry)
        => entry.Origin == TechnologyOrigin.Shipped
            ? ShippedTechnologies.LoadRawJson(new ShippedTechnologyEntry(entry.Id, entry.ResourceName!))
            : File.ReadAllText(entry.FilePath!);

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into <see cref="UserDirectory"/> so it is offered for
    /// every new workspace from now on.
    ///
    /// <para><b>It is PARSED before it is copied.</b> A file that does not read as a technology would
    /// otherwise sit in the list and fail at the moment somebody created a workspace with it — far
    /// from the action that put it there.</para>
    ///
    /// <para><b>An id already in the catalog is a REFUSAL, never a replacement and never a second
    /// row.</b> Shadowing a shipped id is the tempting alternative and it is worse than it looks: the
    /// id is what a <c>.cws</c>, a <c>--tech</c> argument and the default preference all record, so
    /// one id meaning different bytes on two machines makes a design's own history disagree with
    /// itself. The refusal names the collision and the remedy, which is a different file name — and a
    /// user who genuinely wants to replace their own installed one removes it first, deliberately.
    /// </para>
    /// </summary>
    public static TechnologyInstallResult Install(string sourcePath)
    {
        string id = Path.GetFileNameWithoutExtension(sourcePath);
        if (id.Length == 0)
            return new TechnologyInstallResult(null, $"'{sourcePath}' has no file name to take an id from.");

        if (!string.Equals(Path.GetExtension(sourcePath), Extension, StringComparison.OrdinalIgnoreCase))
            return new TechnologyInstallResult(null,
                $"'{Path.GetFileName(sourcePath)}' is not a {Extension} file. Technologies are installed from the "
              + $"{Extension} a workspace's tech/ folder holds, or one saved from the technology editor.");

        string json;
        Technology tech;
        try
        {
            json = File.ReadAllText(sourcePath);
            tech = TechPersistence.Deserialize(json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                     or InvalidDataException or FormatException)
        {
            return new TechnologyInstallResult(null,
                $"'{Path.GetFileName(sourcePath)}' could not be read as a technology: {e.Message}");
        }

        if (Find(id) is { } clash)
            return new TechnologyInstallResult(null, clash.IsUserInstalled
                ? $"A technology called '{id}' is already installed. Remove it first, or rename the file you are "
                + "adding — the file name is the id a workspace records, so two of them cannot share one."
                : $"'{id}' is the id of a technology circuitRF ships. Rename the file you are adding — the file "
                + "name is the id a workspace records, so a shipped one cannot be replaced in place.");

        try
        {
            Directory.CreateDirectory(UserDirectory);
            string target = Path.Combine(UserDirectory, id + Extension);

            // The SOURCE bytes, not a re-serialization: what a fab authored is what should be offered,
            // and a round trip through the writer would quietly normalise a file nobody asked us to
            // touch. ShippedTechnologies.LoadRawJson makes the same choice for the same reason.
            File.WriteAllText(target, json);

            return new TechnologyInstallResult(
                new TechnologyCatalogEntry(id, tech.Name, TechnologyOrigin.User, ResourceName: null, target),
                null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TechnologyInstallResult(null,
                $"'{Path.GetFileName(sourcePath)}' could not be copied to {UserDirectory}: {e.Message}");
        }
    }

    /// <summary>
    /// Deletes an installed technology's file. Returns null on success, or one sentence saying why not.
    ///
    /// <para><b>Workspaces already created are untouched</b>, because each holds its own copy — that is
    /// what <see cref="LoadRawJson"/> exists for. What is lost is the offer of it for the NEXT one.</para>
    /// </summary>
    public static string? Uninstall(string id)
    {
        if (Find(id) is not { } entry)
            return $"No technology called '{id}' is installed.";

        if (!entry.IsUserInstalled)
            return $"'{id}' is one of the technologies circuitRF ships and cannot be removed.";

        try
        {
            File.Delete(entry.FilePath!);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"'{entry.FilePath}' could not be deleted: {e.Message}";
        }
    }

    // ── Reading the user directory ────────────────────────────────────────────

    private static IEnumerable<string> UserFiles()
    {
        string dir = UserDirectory;
        string[] paths;
        try
        {
            if (!Directory.Exists(dir)) return [];
            paths = Directory.GetFiles(dir, "*" + Extension);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }

        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    private static TechnologyCatalogEntry? ReadUserEntry(string path)
    {
        try
        {
            var tech = TechPersistence.Deserialize(File.ReadAllText(path));
            return new TechnologyCatalogEntry(
                Path.GetFileNameWithoutExtension(path), tech.Name, TechnologyOrigin.User,
                ResourceName: null, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                     or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    private static bool IdEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
