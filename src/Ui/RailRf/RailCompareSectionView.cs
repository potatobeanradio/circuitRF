using System.Collections.Generic;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// One section of the comparison, for the dialog's own item template.
/// </summary>
/// <remarks>
/// <b>A view type over <c>RailComparisonSection</c> and not a second wording.</b> Avalonia's
/// compiled bindings need a concrete <c>x:DataType</c> in this assembly, and
/// <c>RailComparisonSection</c> is in <c>src/Design</c>; the heading and the lines are carried
/// through untouched, which is R-rail16-6's rule — a panel showing the comparison reads the same
/// list the page draws.
/// </remarks>
public sealed record RailCompareSectionView(string Heading, IReadOnlyList<string> Lines);
