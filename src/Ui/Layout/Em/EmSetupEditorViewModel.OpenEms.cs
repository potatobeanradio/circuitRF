// brief-em3d-9 R-em3d9-6 — the openEMS section's fields, shown when openEMS is the picked solver: brief
// 8's grid fields and this brief's two run fields.
//
// The Palace section's rules exactly (R-em-11): every control writes a .cem field through the same
// undoable CommitEdit, a blank box is the field's default — OpenEmsGridSettings.Default's and
// OpenEmsRunSettings.Default's, the ones the grid generator and the writer read — and a section with
// every field at its default is written as no section.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout.Em;

public sealed partial class EmSetupEditorViewModel
{
    [ObservableProperty] private string _openEmsCellsPerWavelengthText = "";
    [ObservableProperty] private string _openEmsGradingRatioText       = "";
    [ObservableProperty] private string _openEmsMinCellUmText          = "";
    [ObservableProperty] private string _openEmsPmlCellsText           = "";
    [ObservableProperty] private string _openEmsEndCriterionDbText     = "";
    [ObservableProperty] private string _openEmsMaxTimeStepsText       = "";
    [ObservableProperty] private bool   _openEmsThirdsRule             = true;
    [ObservableProperty] private string? _openEmsFieldError;

    /// <summary>What a blank box stands for — the defaults the generator and the writer read.</summary>
    public static string OpenEmsDefaultCellsPerWavelength => G(CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.CellsPerWavelength);
    public static string OpenEmsDefaultGradingRatio       => G(CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.GradingRatio);
    public static string OpenEmsDefaultMinCellUm          => "auto";
    public static string OpenEmsDefaultPmlCells           => CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.PmlCells.ToString(CultureInfo.InvariantCulture);
    public static string OpenEmsDefaultEndCriterionDb     => G(OpenEmsRunSettings.Default.EndCriterionDb);
    public static string OpenEmsDefaultMaxTimeSteps       => "auto";

    private void SyncOpenEmsFields()
    {
        var o = Working.OpenEms;
        OpenEmsCellsPerWavelengthText = G(o?.CellsPerWavelength);
        OpenEmsGradingRatioText       = G(o?.GradingRatio);
        OpenEmsMinCellUmText          = G(o?.MinCellUm);
        OpenEmsPmlCellsText           = o?.PmlCells?.ToString(CultureInfo.InvariantCulture) ?? "";
        OpenEmsEndCriterionDbText     = G(o?.EndCriterionDb);
        OpenEmsMaxTimeStepsText       = o?.MaxTimeSteps?.ToString(CultureInfo.InvariantCulture) ?? "";
        OpenEmsThirdsRule             = o?.ThirdsRule ?? CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.ThirdsRule;
        OpenEmsFieldError = null;
    }

    /// <summary>The thirds rule is a check box: on is the default (written as no field), off writes false.</summary>
    partial void OnOpenEmsThirdsRuleChanged(bool value)
    {
        if (_suppressCommit) return;
        CommitOpenEmsField("OpenEms.ThirdsRule");
    }

    /// <summary>
    /// Commits one openEMS field — <c>OpenEms.&lt;Field&gt;</c> is its tag. Blank writes the field as
    /// omitted (the default); an invalid value stays in the box beside the message and writes nothing.
    /// </summary>
    public void CommitOpenEmsField(string tag)
    {
        var section = Working.OpenEms?.Clone() ?? new CemOpenEms();
        string? error = null;

        void Real(string text, Func<double, bool> ok, string rule, Action<double?> set)
        {
            if (text.Trim().Length == 0) { set(null); return; }
            if (TryDouble(text, out double v) && ok(v)) { set(v); return; }
            error = rule;
        }
        void Whole(string text, long min, long max, string rule, Action<long?> set)
        {
            if (text.Trim().Length == 0) { set(null); return; }
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v >= min && v <= max)
            { set(v); return; }
            error = rule;
        }

        switch (tag)
        {
            case "OpenEms.CellsPerWavelength":
                Real(OpenEmsCellsPerWavelengthText, v => v >= 1 && double.IsFinite(v), "Enter a number of cells of at least 1, e.g. 20.",
                     v => section.CellsPerWavelength = v);
                break;
            case "OpenEms.GradingRatio":
                Real(OpenEmsGradingRatioText, v => v > 1 && v <= 4, "Enter a ratio above 1 and at most 4, e.g. 1.3.",
                     v => section.GradingRatio = v);
                break;
            case "OpenEms.MinCellUm":
                Real(OpenEmsMinCellUmText, v => v > 0 && double.IsFinite(v), "Enter a positive length in µm, or leave it blank.",
                     v => section.MinCellUm = v);
                break;
            case "OpenEms.PmlCells":
                Whole(OpenEmsPmlCellsText, 0, 64, "Enter a whole number of cells from 0 to 64.", v => section.PmlCells = (int?)v);
                break;
            case "OpenEms.EndCriterionDb":
                Real(OpenEmsEndCriterionDbText, v => v < 0 && v >= -200, "Enter a decay below 0 dB, at least −200, e.g. −50.",
                     v => section.EndCriterionDb = v);
                break;
            case "OpenEms.MaxTimeSteps":
                Whole(OpenEmsMaxTimeStepsText, 1, long.MaxValue, "Enter a whole number of time steps of at least 1, or leave it blank.",
                      v => section.MaxTimeSteps = v);
                break;
            case "OpenEms.ThirdsRule":
                section.ThirdsRule = OpenEmsThirdsRule == CircuitRF.Engine.Em3d.OpenEmsGridSettings.Default.ThirdsRule
                    ? null : OpenEmsThirdsRule;
                break;
            default:
                return;
        }

        OpenEmsFieldError = error;
        if (error is not null) return;

        var before = SnapshotJson();
        Working.OpenEms = section.IsEmpty ? null : section;
        if (SnapshotJson() == before) return;      // the same value, retyped: no undo entry
        CommitEdit(before, $"Change openEMS setting {tag["OpenEms.".Length..]}");
    }
}
