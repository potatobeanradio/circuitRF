namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One part the BOARD places that the selected rail does not carry a row for — what the add-part
/// dialog offers before free text.
/// </summary>
/// <remarks>
/// <b>A top-level type rather than a member of the view model</b>, so a compiled binding in the
/// dialog's <c>DataTemplate</c> can name it: XAML has no spelling for a nested type, and the
/// alternative was to fill the list with rendered STRINGS and read the designator back out of the
/// chosen one — a round trip through a format that embeds the land pattern in brackets, which a
/// designator is allowed to contain.
///
/// <para><b>It carries a name and a land pattern and nothing else</b>, which is the whole of what
/// the board contributes to a hand-added row. No capacitance, no ESR and no connection type is
/// derived from a land pattern here any more than it is in <c>RailPartDiscovery</c>: an 0402 land
/// is a case size.</para>
/// </remarks>
/// <param name="Refdes">Its reference designator, as the artwork states it.</param>
/// <param name="Footprint">The land pattern it stands on, for telling two rows apart. Empty where
/// the instance names no cell.</param>
public readonly record struct RailAddablePart(string Refdes, string Footprint)
{
    /// <summary>What the picker shows — the designator, and the land pattern in brackets where
    /// there is one.</summary>
    public string Label => Footprint.Length > 0 ? $"{Refdes}  ({Footprint})" : Refdes;
}
