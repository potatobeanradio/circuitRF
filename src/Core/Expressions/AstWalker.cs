namespace CircuitRF.Core.Expressions;

/// <summary>Utility for collecting names referenced by an expression AST.</summary>
public static class AstWalker
{
    /// <summary>
    /// Returns all names referenced by RefExpr nodes in the AST.
    /// Does not descend into user-function bodies (the AST is self-contained).
    /// </summary>
    public static HashSet<string> CollectRefs(Expr ast)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        Walk(ast, refs);
        return refs;
    }

    private static void Walk(Expr e, HashSet<string> refs)
    {
        switch (e)
        {
            case RefExpr r:
                refs.Add(r.Name);
                break;
            case UnaryExpr u:
                Walk(u.Operand, refs);
                break;
            case BinaryExpr b:
                Walk(b.Left, refs);
                Walk(b.Right, refs);
                break;
            case CompareExpr c:
                Walk(c.Left, refs);
                Walk(c.Right, refs);
                break;
            case LogicExpr lg:
                Walk(lg.Left, refs);
                Walk(lg.Right, refs);
                break;
            case ConditionalExpr cd:
                Walk(cd.Condition, refs);
                Walk(cd.Then, refs);
                Walk(cd.Else, refs);
                break;
            case CallExpr cl:
                foreach (var a in cl.Args) Walk(a, refs);
                break;
            // NumberExpr, ConstExpr, StringLiteralExpr — no refs
        }
    }
}
