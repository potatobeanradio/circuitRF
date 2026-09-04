using System;
using System.IO;
using CircuitRF.Ui;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The model's own terminal names — <c>d</c>, <c>g</c>, <c>s</c> rather than 1, 2, 3 — must still be
/// on the symbol after circuitRF is quit and the workspace reopened.
///
/// <para><b>The bug this exists for.</b> The label cache was process-lifetime only, so every restart
/// drew the component with numbers again and the only way back was to open each component's
/// parameters — a step that reads as the design having forgotten something. Nothing was wrong with
/// the design or the file; the labels simply were not written down anywhere.</para>
///
/// <para>A real <c>.osdi</c> cannot be stood up here (it needs a compiled artefact and the
/// model-hosting worker), so these drive <c>RememberLabels</c> — the same entry point
/// <c>Describe</c> calls once it has read a file — and simulate the restart with
/// <c>RefreshLabelStore</c>, which is what forgetting the loaded store amounts to.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public sealed class VerilogATerminalLabelPersistenceTests : IDisposable
{
    private readonly string _root;
    private readonly string _modelFile;

    public VerilogATerminalLabelPersistenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-labels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AppDataRoot.RedirectTo(_root);
        VerilogAModelIntrospection.ForgetCachedLabels();

        _modelFile = Path.Combine(_root, "model.osdi");
        File.WriteAllText(_modelFile, "stand-in for a compiled model");
    }

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        VerilogAModelIntrospection.ForgetCachedLabels();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly string[] Terminals = ["d", "g", "s", "b", "dt"];

    private void Remember(string typeId = "themodel", string[]? labels = null)
    {
        var info = new VerilogAModelInfo(typeId, 5, 400, labels ?? Terminals);
        VerilogAModelIntrospection.RememberLabels(
            _modelFile, _modelFile, File.GetLastWriteTimeUtc(_modelFile).Ticks, [info]);
    }

    /// <summary>Forgets everything held in memory, as quitting the application does.</summary>
    private static void Restart() => VerilogAModelIntrospection.RefreshLabelStore();

    [Fact]
    public void LabelsSurviveARestart_RatherThanRevertingToNumbers()
    {
        Remember();
        Assert.Equal(Terminals, VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, "themodel"));

        Restart();

        // The reported bug: this came back null, and the symbol drew 1..5.
        Assert.Equal(Terminals, VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, "themodel"));
    }

    [Fact]
    public void TheSingleModelCase_SurvivesUnderTheBlankModelName()
    {
        // What a component carries when the file declares exactly one type and nothing had to be
        // chosen — by far the common case, and every published model file checked is this shape.
        Remember();
        Restart();

        Assert.Equal(Terminals, VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, ""));
    }

    [Fact]
    public void AnEditedModelFile_LosesItsStoredLabels_RatherThanShowingStaleOnes()
    {
        // The mtime rule the in-memory cache always had, now enforced when the store is READ — the
        // one place it can be without putting a file stat on the glyph-rebuild path.
        Remember();
        Restart();
        Assert.NotNull(VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, "themodel"));

        File.SetLastWriteTimeUtc(_modelFile, DateTime.UtcNow.AddMinutes(5));
        Restart();

        Assert.Null(VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, "themodel"));
    }

    [Fact]
    public void AModelFileThatIsGone_LosesItsStoredLabels()
    {
        Remember();
        File.Delete(_modelFile);
        Restart();

        Assert.Null(VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, "themodel"));
    }

    [Fact]
    public void ForgettingTheLabels_DoesNotQuietlyReloadThemFromTheStore()
    {
        // ForgetCachedLabels is what a test calls to stand up a DIFFERENT model under one path.
        // Re-reading the store there would hand back the previous model's terminals, which is the
        // exact staleness the call is made to remove.
        Remember();
        VerilogAModelIntrospection.ForgetCachedLabels();

        Assert.Null(VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, "themodel"));
    }

    [Fact]
    public void AnUnreadableStore_IsACacheMiss_NotACrash()
    {
        // Half-written, hand-edited, or from a future shape of the record.
        Remember();
        Restart();

        string store = Path.Combine(AppDataRoot.SubDir("cache"), "verilog-a-terminal-labels.json");
        Assert.True(File.Exists(store), "the labels should have been written to the per-user cache");
        File.WriteAllText(store, "{ not json at all");

        Restart();
        Assert.Null(VerilogAModelIntrospection.CachedTerminalLabels(_modelFile, "themodel"));
    }

    [Fact]
    public void TheStoreIsWrittenUnderTheREDIRECTEDDirectory_NotTheRealUsers()
    {
        // Same rule the compiled-model cache follows: a redirected process (the docs factory) must
        // neither answer from the real user's cache nor add to it.
        Remember();

        Assert.True(
            File.Exists(Path.Combine(AppDataRoot.SubDir("cache"), "verilog-a-terminal-labels.json")),
            "the store belongs under the redirected root");
    }
}
