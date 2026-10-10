// The picture dialog's scale row — brief-img-5-dialog.md R-im5-4 (D6).
//
// The scale is never a silent guess. The row shows what the reading chose (ImageScale.Choose — the CLI's rule) and every
// other piece of evidence it found, one click from being chosen instead; with nothing usable the row asks, and Create
// waits. A choice here is a STATEMENT handed to the trace (ImageScaleStatement), never a number this file computes: the
// two-point distance is read by ImageScale.FromTwoPoints, the impedance by the trace through the technology.

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Layout.Recognition.Image;

namespace CircuitRF.Ui.Recognition;

/// <summary>What a scale-combo row does when chosen.</summary>
public enum ImageScaleChoiceKind
{
    /// <summary>A piece of evidence the reading found.</summary>
    Evidence,

    /// <summary>The scale the user stated, as the reading took it.</summary>
    Statement,

    /// <summary>Two points… — arms the measure tool.</summary>
    TwoPoints,

    /// <summary>Line impedance… — asks for a click on a trace.</summary>
    Impedance,
}

/// <summary>One row of the scale combo.</summary>
public sealed record ImageScaleChoice(string Label, ImageScaleChoiceKind Kind, ImageScaleCandidate? Candidate = null)
{
    public override string ToString() => Label;
}

/// <summary>The canvas tool the scale row has armed.</summary>
public enum ImageMeasureTool { None, TwoPoints, Impedance }

public sealed partial class ImageScaleViewModel : ObservableObject
{
    /// <summary>The canvas's one prompt, and Create's tooltip, while there is no scale (R-im5-4).</summary>
    public const string SetScalePrompt = "Set the scale: Two points…";

    private bool _updating;
    private ImageScaleChoice? _beforeTool;

    /// <summary>What the user said; Auto until they say something.</summary>
    public ImageScaleStatement Statement { get; private set; } = ImageScaleStatement.Auto;

    /// <summary>Raised when the statement changes — the dialog re-reads.</summary>
    public event Action? Changed;

    public ObservableCollection<ImageScaleChoice> Choices { get; } = [];

    [ObservableProperty] private ImageScaleChoice? _selectedChoice;

    /// <summary>The chosen metres per pixel, with its unit; a dash with none.</summary>
    [ObservableProperty] private string _valueText = "—";

    /// <summary>R-im3-3's line, beside the value.</summary>
    [ObservableProperty] private string _resolutionLine = "";

    /// <summary>The reading has copper but no scale: the row is highlighted and Create waits.</summary>
    [ObservableProperty] private bool _needsScale;

    /// <summary>Why the statement could not be used — a two-point pick on one point, a click off any trace.</summary>
    [ObservableProperty] private string _error = "";

    /// <summary>Whether the row applies at all — a layout picture.</summary>
    [ObservableProperty] private bool _isApplicable;

    // ── the measure tools ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private ImageMeasureTool _tool;
    [ObservableProperty] private PixelPoint? _pointA;
    [ObservableProperty] private PixelPoint? _pointB;
    [ObservableProperty] private bool _askingDistance;
    [ObservableProperty] private string _distanceText = "";
    [ObservableProperty] private string _distanceError = "";
    [ObservableProperty] private bool _askingZ0;
    [ObservableProperty] private string _z0Text = "50 Ω";
    [ObservableProperty] private string _z0Error = "";

    /// <summary>What the canvas says while a tool is armed.</summary>
    public string ToolPrompt => Tool switch
    {
        ImageMeasureTool.TwoPoints when PointA is null => "Click the first point.",
        ImageMeasureTool.TwoPoints when PointB is null => "Click the second point.",
        ImageMeasureTool.TwoPoints => "Type the distance between the points.",
        ImageMeasureTool.Impedance when !AskingZ0 => "Click a trace whose impedance you know.",
        ImageMeasureTool.Impedance => "Type the trace's impedance.",
        _ => "",
    };

    /// <summary>Where the inline box sits: the second point, or the trace clicked.</summary>
    public PixelPoint? PromptAt => AskingDistance ? PointB : AskingZ0 ? PointA : null;

    public bool HasTool => Tool != ImageMeasureTool.None;

    partial void OnToolChanged(ImageMeasureTool value)
    {
        OnPropertyChanged(nameof(HasTool));
        RefreshPrompt();
    }
    partial void OnPointAChanged(PixelPoint? value) => RefreshPrompt();
    partial void OnPointBChanged(PixelPoint? value) => RefreshPrompt();
    partial void OnAskingDistanceChanged(bool value) => RefreshPrompt();
    partial void OnAskingZ0Changed(bool value) => RefreshPrompt();

    private void RefreshPrompt()
    {
        OnPropertyChanged(nameof(ToolPrompt));
        OnPropertyChanged(nameof(PromptAt));
    }

    partial void OnSelectedChoiceChanged(ImageScaleChoice? oldValue, ImageScaleChoice? newValue)
    {
        if (_updating || newValue is null) return;
        switch (newValue.Kind)
        {
            case ImageScaleChoiceKind.TwoPoints:
                _beforeTool = oldValue;
                Arm(ImageMeasureTool.TwoPoints);
                break;
            case ImageScaleChoiceKind.Impedance:
                _beforeTool = oldValue;
                Arm(ImageMeasureTool.Impedance);
                break;
            case ImageScaleChoiceKind.Evidence when newValue.Candidate is { } c:
                Disarm();
                // The evidence the reading itself would choose is no statement at all; any other is stated as its number.
                State(c.Kind == ImageScaleKind.Parts && c.Support >= ImageScale.MinPartsSupport && IsBestParts(c)
                    ? ImageScaleStatement.Auto
                    : ImageScaleStatement.Stated(c.MetresPerPixel));
                break;
        }
    }

    private bool IsBestParts(ImageScaleCandidate c) =>
        Choices.Where(x => x.Candidate is { Kind: ImageScaleKind.Parts }).Select(x => x.Candidate!).MaxBy(x => x.Support) == c;

    private void Arm(ImageMeasureTool tool)
    {
        Tool = tool;
        PointA = PointB = null;
        AskingDistance = AskingZ0 = false;
        DistanceError = Z0Error = "";
    }

    private void Disarm()
    {
        Tool = ImageMeasureTool.None;
        AskingDistance = AskingZ0 = false;
        DistanceError = Z0Error = "";
    }

    /// <summary>A click on the canvas while a tool is armed. False when no tool is armed (the click is the canvas's).</summary>
    public bool Click(PixelPoint p)
    {
        switch (Tool)
        {
            case ImageMeasureTool.TwoPoints when PointA is null:
                PointA = p;
                return true;
            case ImageMeasureTool.TwoPoints when PointB is null || !AskingDistance:
                PointB = p;
                AskingDistance = true;
                return true;
            case ImageMeasureTool.Impedance:
                PointA = p;
                AskingZ0 = true;
                return true;
            case ImageMeasureTool.TwoPoints:
                return true;
            default:
                return false;
        }
    }

    /// <summary>The inline box's distance: refused in place when it has no unit (D6), else stated.</summary>
    [RelayCommand]
    private void ApplyDistance()
    {
        if (PointA is not { } a || PointB is not { } b) return;
        if (ImageScale.FromTwoPoints(a, b, DistanceText, out string? why) is null)
        {
            DistanceError = why ?? "";
            return;
        }
        Disarm();
        State(ImageScaleStatement.TwoPoints(a, b, DistanceText.Trim()));
    }

    /// <summary>The inline box's impedance: a positive number of ohms.</summary>
    [RelayCommand]
    private void ApplyZ0()
    {
        if (PointA is not { } p) return;
        string text = Z0Text.Trim().TrimEnd('Ω', 'Ω').Trim();
        if (text.EndsWith("ohm", StringComparison.OrdinalIgnoreCase)) text = text[..^3].Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double z0) || !(z0 > 0) || !double.IsFinite(z0))
        {
            Z0Error = $"'{Z0Text.Trim()}' is not an impedance: give a positive number of ohms (50 Ω).";
            return;
        }
        Disarm();
        State(ImageScaleStatement.Impedance(p, z0));
    }

    /// <summary>Escape on the canvas: the tool is put down and the row shows what it showed before.</summary>
    [RelayCommand]
    private void CancelTool()
    {
        if (Tool == ImageMeasureTool.None) return;
        Disarm();
        _updating = true;
        SelectedChoice = _beforeTool is { } b && Choices.Contains(b) ? b : null;
        _updating = false;
    }

    private void State(ImageScaleStatement statement)
    {
        Statement = statement;
        Error = "";
        Changed?.Invoke();
    }

    /// <summary>Forgets every statement — a new picture.</summary>
    public void Reset()
    {
        Disarm();
        Statement = ImageScaleStatement.Auto;
        Error = "";
        Update(null, false);
    }

    /// <summary>
    /// What the reading found: every candidate, the one chosen (null: no scale), and R-im3-3's line. A statement the
    /// reading refused (a click off any trace) is reported in <paramref name="statementRefusal"/>.
    /// </summary>
    public void Update(ImageTraceResult? trace, bool applicable, string? statementRefusal = null)
    {
        _updating = true;
        try
        {
            IsApplicable = applicable;
            Choices.Clear();
            ImageScaleChoice? selected = null;
            if (trace is not null)
            {
                foreach (var c in trace.ScaleCandidates)
                {
                    var choice = new ImageScaleChoice(Label(c), c.Kind is ImageScaleKind.Stated or ImageScaleKind.TwoPoints or ImageScaleKind.Impedance
                        ? ImageScaleChoiceKind.Statement : ImageScaleChoiceKind.Evidence, c);
                    Choices.Add(choice);
                    if (trace.Scale is { } chosen && ReferenceEquals(chosen, c)) selected = choice;
                }
                selected ??= trace.Scale is { } s ? Choices.FirstOrDefault(x => x.Candidate == s) : null;
            }
            Choices.Add(new ImageScaleChoice("Two points…", ImageScaleChoiceKind.TwoPoints));
            Choices.Add(new ImageScaleChoice("Line impedance…", ImageScaleChoiceKind.Impedance));
            if (Tool == ImageMeasureTool.TwoPoints) selected = Choices[^2];
            else if (Tool == ImageMeasureTool.Impedance) selected = Choices[^1];
            SelectedChoice = selected;

            ValueText = trace?.Scale?.Display ?? "—";
            ResolutionLine = trace?.ResolutionLine ?? "";
            Error = statementRefusal ?? "";
            NeedsScale = applicable && trace is { Scale: null } t && (t.Ok || statementRefusal is not null);
        }
        finally { _updating = false; }
    }

    /// <summary>How the combo spells a candidate.</summary>
    public static string Label(ImageScaleCandidate c) => c.Kind switch
    {
        ImageScaleKind.Placement => "As placed",
        ImageScaleKind.Parts => $"From parts — {Inner(c.Evidence)}",
        ImageScaleKind.Resolution => System.Text.RegularExpressions.Regex.Match(c.Evidence, @"states ([0-9.]+) dpi") is { Success: true } m
            ? $"File resolution {m.Groups[1].Value} dpi" : $"File resolution ({c.Display})",
        ImageScaleKind.TwoPoints => $"Two points — {c.Display}",
        ImageScaleKind.Impedance => $"Line impedance — {c.Display}",
        _ => $"Stated — {c.Display}",
    };

    /// <summary>The cases of a parts candidate: the evidence's parenthesised part.</summary>
    private static string Inner(string evidence)
    {
        int open = evidence.IndexOf('('), close = evidence.LastIndexOf(')');
        return open >= 0 && close > open ? evidence[(open + 1)..close] : evidence;
    }
}
