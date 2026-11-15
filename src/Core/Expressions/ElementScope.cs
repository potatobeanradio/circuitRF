// brief-paa-4 R-paa4-2 — the element scope of a Phased Antenna Array in Feed mode (docs/design/phased-array.md §4.3, D6).
//
// FIVE NAMES, AND NOWHERE ELSE. Amp, Phase, Delay and their [n] overrides are evaluated once per element in a scope layered over the
// instance's, binding n, row, col (1-based) and x, y (metres from the array's centre, unit-bearing). The channel cell placed per element
// takes the same scope as the parent of its own, so its parameters see the same names. Anywhere else a reference to one of the five is
// an ordinary unresolved name, and Evaluator.Resolve says where the name is valid instead of only that it is missing. A user VAR of the
// same name is legal and is what the name means outside element scope; inside, the element's binding shadows it.
//
// THE TAPER FUNCTIONS READ THE LATTICE TOO, through two hidden bindings (the array's Rows and Cols) and a marker that says the scope is an
// element's. They are hidden (`__`-prefixed) because D6 adds five names, not seven: a user's Rows VAR keeps meaning what it means.
//
// Cycle detection is unchanged: the values are injected as resolved, so no binding here is ever an edge back into the instance's scope.

namespace CircuitRF.Core.Expressions;

/// <summary>R-paa4-2 — the per-element scope of a Feed-mode Phased Antenna Array.</summary>
public static class ElementScope
{
    /// <summary>The five names element scope binds (D6).</summary>
    public static readonly IReadOnlyList<string> Names = ["n", "row", "col", "x", "y"];

    /// <summary>The binding that marks a scope as an element's.</summary>
    public const string Marker = "__paa_element";

    /// <summary>The array's Rows and Cols, for the separable tapers.</summary>
    public const string RowsName = "__paa_rows", ColsName = "__paa_cols";

    /// <summary>Whether <paramref name="name"/> is one of the five.</summary>
    public static bool IsName(string name) => name is "n" or "row" or "col" or "x" or "y";

    /// <summary>The sentence after "Unresolved name" for one of the five: what it is and where it is valid.</summary>
    public static string WhereValid(string name)
    {
        string what = name switch
        {
            "n" => "n is an element's number",
            "row" => "row is an element's row",
            "col" => "col is an element's column",
            _ => $"{name} is an element's position",
        };
        return $"{what}, bound only in a Phased Antenna Array's Amp, Phase and Delay (and their [n] overrides) and in the parameters " +
               "of the channel cell it places per element";
    }

    /// <summary>The scope of element <paramref name="n"/> (1-based) over <paramref name="parent"/>, its values injected into
    /// <paramref name="evaluator"/>'s memo so nothing here is evaluated again. <paramref name="debugName"/> must be unique per element: it
    /// keys the memo.</summary>
    public static Scope Create(Scope parent, string debugName, Evaluator evaluator, int n, int row, int col, double x, double y, int rows, int cols)
    {
        var scope = new Scope(debugName, parent);
        void Put(string name, double value, string? unit = null)
        {
            scope.Bind(name, "__resolved__", unit);
            evaluator.InjectResolved(debugName, name, new Value(value));
        }
        Put("n", n);
        Put("row", row);
        Put("col", col);
        // A length, already in metres: unit-bearing, so a site unit meeting it is not applied a second time (var-unit-wins).
        Put("x", x, "metre");
        Put("y", y, "metre");
        Put(Marker, 1);
        Put(RowsName, rows);
        Put(ColsName, cols);
        return scope;
    }

    /// <summary>Whether <paramref name="scope"/> is, or is inside, an element's scope.</summary>
    public static bool IsElement(Scope scope) => scope.Lookup(Marker) is not null;
}
