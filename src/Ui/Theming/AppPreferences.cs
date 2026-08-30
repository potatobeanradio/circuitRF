using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Theming;

public enum LaunchAction { Welcome, NewSchematic, NewWorkspace, OpenWorkspace, NewDataDisplay, NewSymbol, NewLayout, NewHarmonica }

/// <summary>
/// Settings ▸ On Launch ▸ <b>Window Layout</b> — the dock arrangement the shell opens with, and the
/// one View ▸ Reset Layout resets TO (there is deliberately no second place to choose a layout).
///
/// <para>The first two members were the old <c>LaunchPane</c> enum (<c>ProjectTree</c>/<c>Palette</c>)
/// and keep its ORDINALS, because the preference is serialized as a number: an existing
/// <c>preferences.json</c> holding <c>"launch_pane": 1</c> still means "the Library" after the rename.
/// <see cref="AppPreferences.LegacyLaunchPane"/> is what carries that value across.</para>
/// </summary>
public enum WindowLayout
{
    /// <summary>Project Tree and Library tabbed together on the left, Project Tree on top.</summary>
    ProjectTreeFocus,

    /// <summary>The same arrangement, with the Library's tab on top.</summary>
    LibraryFocus,

    /// <summary>Project Tree on the left, Library in its own column to the RIGHT of the documents.</summary>
    ProjectTreeAndLibrary,
}

public sealed class AppPreferences
{
    [JsonPropertyName("active_theme_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveThemeName { get; set; }

    // MRU list of workspace .cws paths, most-recent first, capped at 10.
    [JsonPropertyName("recent_workspaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RecentWorkspaces { get; set; }

    // MRU list of recently-placed component kinds (SymbolKind.ToString()), most-recent first, capped at 12.
    [JsonPropertyName("recently_placed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RecentlyPlaced { get; set; }

    // Clipboard render policy — null means use the default (FollowSystem / transparent).
    [JsonPropertyName("copy_color_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CopyColorMode? CopyColorMode { get; set; }

    [JsonPropertyName("copy_transparent_background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CopyTransparentBackground { get; set; }

    // Launch behavior — null means use the defaults (Welcome / WindowLayout.ProjectTreeAndLibrary).
    [JsonPropertyName("launch_action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaunchAction? LaunchAction { get; set; }

    // The dock arrangement the shell opens with, and what View ▸ Reset Layout resets to. Null means
    // the shipped default, WindowLayout.ProjectTreeAndLibrary — including on a machine with no
    // preferences.json at all, which is exactly the fresh-install case.
    [JsonPropertyName("window_layout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowLayout? WindowLayout { get; set; }

    // The pre-2026-08-15 name for the same setting, read once on load and never written back (see
    // AppPreferencesIo.Load). Kept so a user who chose "Palette" before the rename still gets the
    // Library focused, rather than being silently moved onto the new default.
    [JsonPropertyName("launch_pane")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WindowLayout? LegacyLaunchPane { get; set; }

    // Whether the dockers (Project Tree, Library, Properties, Analyses, Messages) are shown at app
    // launch and when a new workspace is created. Null means the default, which is ON (shown) — the
    // same arrangement circuitRF has always opened with. When false, dockers are collapsed exactly as
    // View ▸ Hide Dockers would collapse them (WorkspaceViewModel.ApplyShowDockersOnLaunchPreference).
    [JsonPropertyName("show_dockers_on_launch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowDockersOnLaunch { get; set; }

    // Which kits' PCell generator scripts this installation has agreed to run, keyed by the kit's
    // absolute directory. DELIBERATELY here and not in .cws: a decision recorded inside a shared
    // workspace could be written by whoever sent you the workspace, which would defeat the prompt
    // entirely. See PCellTrustStore for the full reasoning.
    [JsonPropertyName("pcell_trust")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, bool>? PCellTrust { get; set; }

    // L5b / R16d: "Export offers to run DRC first. Not mandatory, not silent." Null means the default,
    // which is ON — catching a spacing error before it reaches a fab is most of DRC's value, and a
    // check the user has to remember to run before every export is one they will forget before the
    // export that mattered. Per USER, not per workspace: it is a working habit, not a property of a
    // design, and a workspace arriving from someone else must not silently turn a user's check off.
    [JsonPropertyName("check_drc_on_export")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CheckDrcOnExport { get; set; }

    // The built-in wire-to-wire clearance, in MIL — the one number circuitRF's own assembly rule set
    // states, applied when a wirebond design references no `.wasm`. Null means the default (0.5 mil).
    // Per USER for CheckDrcOnExport's own reason, and stored in mil because mil is the unit it is
    // stated, shown and edited in. See WBondWireClearance for the full argument.
    [JsonPropertyName("wire_clearance_mil")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? WireClearanceMil { get; set; }

    // R-h8-4: folders holding device kits, for harmonicaRF's Set DUT. A PREFERENCE and not a
    // workspace: DeviceWorkerProviderResolver's folder-list constructor needs nothing else (src/Cli's
    // --kits already ships that form), and standing up a WorkspaceViewModel to reach it would drag in
    // the project tree, the dock layout, technologies and PCell resolvers — none of which the device
    // path reads. Per user rather than per workspace, because harmonicaRF standalone has no workspace.
    [JsonPropertyName("harmonica_kit_folders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? HarmonicaKitFolders { get; set; }

    // Message timestamp display — null means default (Time). Serialized as a number, like the others.
    [JsonPropertyName("message_timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageTimestampMode? MessageTimestamp { get; set; }

    // wbond.md §6.4: the defaults a newly drawn wire gets — 7 points on the seed arch, 1 mil
    // diameter, gold. Per USER rather than per design: they are how one shop's bonder is set up,
    // not a property of any one package, and a `.wBond` arriving from someone else must not silently
    // change what the next wire you draw looks like. Null means the shipped default.
    //
    // Gold is both the RF packaging norm and the metal of the LW1 validation set in
    // mom-wirebond-kernel.md, so the shipped default and the validated path agree.
    [JsonPropertyName("wbond_wire_points")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WBondWirePoints { get; set; }

    [JsonPropertyName("wbond_wire_diameter_nm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WBondWireDiameterNm { get; set; }

    [JsonPropertyName("wbond_wire_material")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WBondWireMaterial { get; set; }

    /// <summary>
    /// The z every new wire's FEET land at (owner, 2026-08-17) — the shipped 4 mil when unset.
    /// It governs both the wires a new wBond component is created with and the wires drawn in the
    /// layout view, which is the whole point of it being one setting: <i>"being consistent is more
    /// important than being right, and we can't guess what height the user wants the wire landings"</i>.
    ///
    /// <para><b>Zero is a real value here, unlike every other wBond default beside it.</b> A foot at
    /// z = 0 is a wire landing on the reference plane, and a NEGATIVE one is a foot in a cavity below
    /// it — both are geometry someone bonds. So this is honoured whenever it is present, where
    /// <see cref="WBondWireDiameterNm"/> and friends treat a non-positive stored value as absent.</para>
    ///
    /// <para>Per USER like the three above it: it describes how one shop's parts sit, not a property
    /// of any one design, and a <c>.wBond</c> arriving from someone else must not change what the next
    /// wire you draw looks like.</para>
    /// </summary>
    [JsonPropertyName("wbond_wire_foot_z_nm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WBondWireFootZNm { get; set; }

    // `wbond_panels_arranged` lived here and is GONE (2026-08-17). It recorded that this installation
    // had had the two wirebond panels arranged once, and the scope was wrong: a panel's home is
    // per-WORKSPACE, so the flag was already spent by the time the user's second workspace asked, and
    // that workspace — having no home for either panel — got them floating. The correct gate is the
    // per-panel "does this layout already place it", which WorkspaceViewModel.ArrangeWBondPanels
    // applies to the live layout and which needs nothing remembered between runs. An existing key in
    // someone's preferences.json is simply ignored on load and dropped on the next save.

    /// <summary>
    /// How far a PASTED wire is offset from whatever is already there (owner, 2026-08-16). It governs
    /// PLACEMENT only: the pitch WITHIN a copied group is whatever the copied wires had, and pasting
    /// never re-spaces them. Null means the shipped 5 mil.
    ///
    /// <para>It exists because paste used to apply one fixed 5 mil offset every time, so a second
    /// paste of the same clipboard landed exactly on the first — two wires on identical geometry,
    /// which makes the inductance matrix singular and was reported as an error with no third wire.
    /// See <c>WBondViewModel.PasteWiresAtFreePitch</c>.</para>
    /// </summary>
    [JsonPropertyName("wbond_paste_pitch_nm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WBondPastePitchNm { get; set; }

    // R-emp-6: the EM solver's core cap. Null means automatic (unbounded, i.e. what every run did
    // before it existed). DELIBERATELY here and not in the .cem: a core count is a property of the
    // MACHINE, not of the design, and a .cem travels with the workspace — opening a colleague's EM
    // setup must not pin your core count to theirs. Same reasoning as HarmonicaKitFolders and the
    // wirebond defaults. It is SHOWN in the EM Setup panel, because that is where the user is
    // standing when the cost lands, and it enters no provenance hash (R-emp-7) because it cannot
    // change an answer.
    [JsonPropertyName("em_max_cores")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EmMaxCores { get; set; }

    // R-h9r2-18a: what a BRAND NEW harmonicaRF document's tickle starts at. Null means the shipped
    // default (on, −50 dBm). Per USER, not per document — see HarmonicaTickleDefaults' own reasoning,
    // the same "a .charm from someone else must not change your own working habit" rule
    // WBondWirePoints etc. already follow above. HarmonicaSettings.TickleEnabled/TickleDbm are what a
    // document was actually solved with and always win once one exists; this only seeds a new one.
    [JsonPropertyName("harmonica_tickle_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HarmonicaTickleEnabled { get; set; }

    [JsonPropertyName("harmonica_tickle_dbm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HarmonicaTickleDbm { get; set; }

    /// <summary>
    /// Whether circuitRF downloads new versions in the background and installs them at the next
    /// relaunch. Null means the default, which is <b>ON</b>.
    ///
    /// <para><b>The nullable idiom is what delivers the default, and it has to.</b> A machine with no
    /// preferences.json at all — the fresh-install case — reads <c>AutomaticUpdates ?? true</c> and
    /// gets automatic updates on without a single line of first-run seeding. Never write a default
    /// value into the file: absence IS the default, and a seeded file would make the two
    /// indistinguishable.</para>
    ///
    /// <para><b>Per USER, and therefore shared by all three applications</b>, because
    /// <see cref="AppDataRoot"/> is one directory: the toggle set in circuitRF governs harmonicaRF
    /// and wBond as well. That is intended — "should this machine update itself" is a property of the
    /// user, not of which binary they happened to open — and the settings help text says so, because
    /// the scope of a checkbox should not be a surprise.</para>
    ///
    /// <para>Read it through <c>Updates.UpdatePolicy.Current</c> and nowhere else: a policy file
    /// beside the install and <c>CRF_NO_UPDATE_CHECK=1</c> both override it, and an override that is
    /// honoured in one place and forgotten in another is an override that does not exist.</para>
    /// </summary>
    [JsonPropertyName("automatic_updates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutomaticUpdates { get; set; }

    /// <summary>
    /// Whether prerelease versions are offered. Null means the default, which is <b>OFF</b> — the
    /// opposite default to the one above, and deliberately: the users on the fastest-moving channel
    /// should be the ones who opted into it.
    ///
    /// <para>A sub-item of <see cref="AutomaticUpdates"/> in the UI, and disabled while that is off.
    /// Turning it off DISCARDS a staged prerelease, and leaves a staged stable version alone — a user
    /// who unchecks the box and is then moved onto a beta at the next relaunch has been lied to by
    /// the checkbox.</para>
    /// </summary>
    [JsonPropertyName("include_beta_updates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IncludeBetaUpdates { get; set; }

    /// <summary>
    /// Whether the <b>Release Notes</b> dialog opens the first time a newly installed version is
    /// launched. Null means the default, which is <b>ON</b> — the same nullable idiom, and for the
    /// same reason, as <see cref="AutomaticUpdates"/> above: absence IS the default, so a machine with
    /// no preferences.json needs no first-run seeding and a seeded file would make "never chosen" and
    /// "chosen to be on" indistinguishable.
    ///
    /// <para><b>It governs whether the dialog is SHOWN, never whether the version is recorded as
    /// seen.</b> A user who turns it off and later turns it back on gets the notes for the next
    /// version they install, not a backlog of the ones they skipped — see
    /// <c>Updates.ReleaseNotesGate</c>, which is the only thing that reads this.</para>
    ///
    /// <para>Per USER and therefore shared by all three applications, like every key around it; only
    /// circuitRF has a window to show the dialog over, which is why the checkbox sits in the Updates
    /// section rather than growing a fourth settings surface.</para>
    /// </summary>
    [JsonPropertyName("show_release_notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowReleaseNotes { get; set; }

    /// <summary>
    /// Whether a kit may run its own EXTERNAL DEVICE WORKER — the separate process circuitRF starts
    /// to evaluate a vendor device model. Null means the default, which is <b>ON</b>.
    ///
    /// <para><b>The companion to <see cref="PCellTrust"/>, and it exists because that one had no
    /// companion.</b> A kit declares two kinds of program: generator scripts in
    /// <c>pcell-generators.json</c>, which have been gated behind an explicit prompt since B6, and a
    /// worker in <c>device-provider.json</c>, whose <c>command</c> resolves against the kit's own
    /// folder — so a kit can ship an executable and circuitRF starts it. Until this key existed, only
    /// one of the two asked (security review, 2026-08-25).</para>
    ///
    /// <para><b>Why this is a switch and not per-kit consent like <see cref="PCellTrust"/>.</b> Every
    /// kit installed before it existed evaluates its devices through a worker, so a per-kit prompt
    /// defaulting to "not yet asked" would put a dialog in front of workspaces that have always just
    /// run — and a prompt everyone meets on their existing work is a prompt they learn to dismiss.
    /// One switch, on by default, is the honest version of what this buys: somewhere to say no, and
    /// something an administrator can hold shut.</para>
    ///
    /// <para>Read it through <c>Security.ExternalWorkerPolicy.Current</c> and nowhere else: a
    /// <c>no-device-workers</c> file beside the install and <c>CRF_NO_DEVICE_WORKERS=1</c> both
    /// override it, and an override that is honoured in one place and forgotten in another is an
    /// override that does not exist.</para>
    /// </summary>
    [JsonPropertyName("external_device_workers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ExternalDeviceWorkers { get; set; }
}

public static class AppPreferencesIo
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // The one per-user state directory, so a tool can redirect it (see CircuitRF.Ui.AppDataRoot).
    private static string PrefsDir => AppDataRoot.Dir;

    private static string PrefsPath => Path.Combine(PrefsDir, "preferences.json");

    /// <summary>
    /// Whether a preferences file exists <i>at all</i> — the cheapest honest answer to "has this
    /// installation ever been used before", which is what
    /// <c>Updates.ReleaseNotesGate.CaptureAtStartup</c> has to settle before anything writes one.
    ///
    /// <para>Exposed rather than having callers rebuild the path, because the path is redirectable
    /// (<see cref="AppDataRoot"/>) and a second copy of it would be right until the day a tool moved
    /// the state directory.</para>
    /// </summary>
    public static bool FileExists => File.Exists(PrefsPath);

    public static AppPreferences Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                return Migrate(JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(PrefsPath), JsonOpts)
                               ?? new AppPreferences());
            }
        }
        catch { /* corrupt prefs — start fresh */ }
        return new AppPreferences();
    }

    /// <summary>
    /// Folds retired preference keys into their current ones, on READ rather than by rewriting the
    /// file — a user who runs an older build in between then still finds their setting where that
    /// build looks for it. Idempotent; separate from <see cref="Load"/> only so it is testable
    /// without a real preferences.json.
    /// </summary>
    public static AppPreferences Migrate(AppPreferences prefs)
    {
        // "launch_pane" (Focus Pane) → "window_layout" (Window Layout), 2026-08-15. The ordinals were
        // deliberately preserved by the rename, so this carries the value across as-is.
        prefs.WindowLayout   ??= prefs.LegacyLaunchPane;
        prefs.LegacyLaunchPane = null;
        return prefs;
    }

    public static void Save(AppPreferences prefs)
    {
        try
        {
            Directory.CreateDirectory(PrefsDir);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs, JsonOpts));
        }
        catch { /* non-critical — preference loss is recoverable */ }
    }

    /// <summary>Load → mutate → save in one step so partial writes never clobber other fields.</summary>
    public static void Update(Action<AppPreferences> mutate)
    {
        var prefs = Load();
        mutate(prefs);
        Save(prefs);
    }
}
