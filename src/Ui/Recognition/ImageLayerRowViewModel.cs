// One colour of a layout picture and what it is read as — brief-img-5-dialog.md R-im5-5 (D7).
//
// A PROJECTION of an ImageLayerRow, re-made on every reading. A change does not change the row: it hands the choice to
// the dialog, which keeps the edited map (ImageLayerMap.WithRole — the edit IM-3's map takes) and hands it to every
// later trace, which rebinds it to that trace's colours. So an edited row survives a re-run, and is never replaced by
// Auto.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Design.Layout.Recognition.Image;

namespace CircuitRF.Ui.Recognition;

public sealed partial class ImageLayerRowViewModel : ObservableObject
{
    public const string Drill = "Drill", BoardOutline = "Board outline", Silkscreen = "Silkscreen",
                        Background = "Background", Ignore = "Ignore";

    private readonly Action<ImageLayerRowViewModel, string> _changed;
    private readonly bool _refreshing;

    /// <param name="layerNames">The technology's layers, as the combo lists them.</param>
    /// <param name="edited">The user chose this row's reading; a re-run keeps it.</param>
    public ImageLayerRowViewModel(ImageLayerRow row, IReadOnlyList<string> layerNames, bool edited,
                                  Action<ImageLayerRowViewModel, string> changed)
    {
        Row = row;
        _changed = changed;
        Edited = edited;
        string current = Spell(row);
        var options = new List<string>(layerNames);
        if (row.Role == ImageLayerRole.Layer && row.Layers.Count > 1) options.Insert(0, current);
        options.AddRange([Drill, BoardOutline, Silkscreen, Background, Ignore]);
        if (!options.Contains(current)) options.Insert(0, current);
        Options = options;
        _refreshing = true;
        LayerText = current;
        _refreshing = false;
    }

    public ImageLayerRow Row { get; }
    public int Cluster => Row.Cluster;
    public int Rgb => Row.Rgb;

    public IBrush Swatch => new ImmutableSolidColorBrush(Color.FromRgb((byte)(Rgb >> 16), (byte)(Rgb >> 8), (byte)Rgb));

    /// <summary>The share of the picture, as the table shows it.</summary>
    public string ShareText => Row.Share >= 0.995 ? "100 %"
        : Row.Share >= 0.01 ? (Row.Share * 100).ToString("0", CultureInfo.InvariantCulture) + " %"
        : "<1 %";

    public string Tip => $"{Row.Hex}" + (Row.Overlap ? " — read as the overlap of two layers" : "");

    public IReadOnlyList<string> Options { get; }

    /// <summary>The user chose this reading.</summary>
    public bool Edited { get; }

    /// <summary>Mapped to a layer the technology does not have, or to nothing: drawn amber.</summary>
    public bool Unmapped => Row.Role is ImageLayerRole.Layer or ImageLayerRole.Drill or ImageLayerRole.Silkscreen or ImageLayerRole.BoardOutline
                            && Row.Layers.Count == 0;

    [ObservableProperty] private string _layerText = "";

    partial void OnLayerTextChanged(string value)
    {
        if (_refreshing || value is null || value == Spell(Row)) return;
        _changed(this, value);
    }

    /// <summary>How the combo spells a row.</summary>
    public static string Spell(ImageLayerRow row) => row.Role switch
    {
        ImageLayerRole.Layer => row.Layers.Count == 0 ? Ignore : string.Join(" + ", row.Layers),
        ImageLayerRole.Drill => Drill,
        ImageLayerRole.BoardOutline => BoardOutline,
        ImageLayerRole.Silkscreen => Silkscreen,
        ImageLayerRole.Background => Background,
        _ => Ignore,
    };

    /// <summary>What a combo spelling reads as: a role, and for a layer its name(s).</summary>
    public static (ImageLayerRole Role, string[] Layers) Parse(string text) => text switch
    {
        Drill => (ImageLayerRole.Drill, []),
        BoardOutline => (ImageLayerRole.BoardOutline, []),
        Silkscreen => (ImageLayerRole.Silkscreen, []),
        Background => (ImageLayerRole.Background, []),
        Ignore => (ImageLayerRole.Ignore, []),
        _ => (ImageLayerRole.Layer, [.. text.Split(" + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]),
    };
}
