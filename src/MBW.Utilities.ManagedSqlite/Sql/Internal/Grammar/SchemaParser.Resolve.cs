using MBW.Utilities.ManagedSqlite.Core.Helpers;
using System;
using System.Globalization;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The constant checks of expr.c and the name resolution of resolve.c, for the expressions a CREATE TABLE resolves
/// against its own table: generated columns (NC_GenCol) and the terms of PRIMARY KEY and UNIQUE (NC_IdxExpr), and
/// those of a CREATE INDEX against its table: its terms (NC_IdxExpr) and its WHERE (NC_PartIdx)
/// </summary>
internal sealed partial class SchemaParser
{
    // NC_* in sqliteInt.h, for the name contexts used here
    private const int NC_IdxExpr = 0x000020, NC_GenCol = 0x000008, NC_PartIdx = 0x000002;

    /// <summary>
    /// Ends the resolution of one expression after Lenient reported an error in it, as SQLite stops using it
    /// </summary>
    private sealed class ResolveAbort : Exception
    {
    }

    /// <summary>
    /// sqlite3ExprIsConstantOrFunction in expr.c, as it runs at init (eCode 5): a DEFAULT may not name a column, use a
    /// window function or a subquery. Any other function counts as constant, and a parameter becomes NULL.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L2866 (sqlite3ExprIsConstantOrFunction)
    /// </summary>
    private static bool IsConstantOrFunction(Expr p) => IsConstant(p, 5);

    /// <summary>
    /// sqlite3ExprIsConstant in expr.c (eCode 1): a function counts as constant only when it is a deterministic
    /// scalar function with constant arguments
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L2645 (sqlite3ExprIsConstant)
    /// </summary>
    private static bool IsConstant(Expr p) => IsConstant(p, 1);

    /// <summary>
    /// The walk of exprIsConst and exprNodeIsConstant in expr.c, for eCode 1 and 5
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L2541 (exprNodeIsConstant)
    /// </summary>
    private static bool IsConstant(Expr p, int eCode)
    {
        switch (p.Op)
        {
            case TK.FUNCTION:
                if (eCode == 5 && !p.Has(Expr.EP_WinFunc))
                    break;
                if (eCode == 5)
                    return false;
                return IsConstantFunction(p);

            case TK.ID:
                // "true" and "false" are the TRUE and FALSE literals
                if (IdToTrueFalse(p))
                    return true;
                return false;

            case TK.COLUMN:
            case TK.AGG_FUNCTION:
            case TK.AGG_COLUMN:
            case TK.IF_NULL_ROW:
            case TK.REGISTER:
            case TK.DOT:
            case TK.RAISE:
                return false;

            case TK.VARIABLE:
                // A parameter in a CREATE statement read from the schema is NULL
                if (eCode == 5)
                    p.Op = TK.NULL;
                break;
        }

        // sqlite3SelectWalkFail: a subquery is never constant
        if (p.Select != null)
            return false;

        return (p.Left == null || IsConstant(p.Left, eCode)) &&
               (p.Right == null || IsConstant(p.Right, eCode)) &&
               (p.List == null || IsConstantList(p.List, eCode));
    }

    private static bool IsConstantList(ExprList list, int eCode)
    {
        foreach (ExprListItem item in list)
        {
            if (item.Expr != null && !IsConstant(item.Expr, eCode))
                return false;
        }

        return true;
    }

    /// <summary>
    /// exprNodeIsConstantFunction in expr.c: a known, deterministic (or slowly changing), non-aggregate function that
    /// is not a window function, with constant arguments
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L2482 (exprNodeIsConstantFunction)
    /// </summary>
    private static bool IsConstantFunction(Expr p)
    {
        int n = p.List?.Count ?? 0;
        if (p.List != null && !IsConstantList(p.List, 1))
            return false;

        return SqliteFunctions.TryFind(p.Text!, n, out SqliteFunctions.Function function) &&
               (function.Flags & SqliteFunctions.FunctionFlags.Aggregate) == 0 &&
               (function.Flags & (SqliteFunctions.FunctionFlags.Constant | SqliteFunctions.FunctionFlags.SlowChange)) != 0 &&
               !p.Has(Expr.EP_WinFunc);
    }

    /// <summary>
    /// sqlite3ResolveSelfReference in resolve.c: resolves the names in an expression of the new table, which may only
    /// name the table's own columns
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L2308 (sqlite3ResolveSelfReference)
    /// </summary>
    private void ResolveSelfReference(Expr expr, int context)
    {
        try
        {
            CheckHeight(expr.Height);
            ResolveWalk(expr, context);
        }
        catch (ResolveAbort)
        {
            // Lenient reported the error, and the expression is not used
        }
    }

    /// <summary>
    /// An error in name resolution. In a generated column it is relaxable (DDL-62): the column's value is computed,
    /// never read, so Lenient reads on. In a PRIMARY KEY or UNIQUE term it decides which columns the key has.
    /// </summary>
    private void ResolveError(int context, string check, string message)
    {
        if (context == NC_GenCol)
        {
            FailRelaxable("DDL-62", message);
            throw new ResolveAbort();
        }

        // The terms and WHERE of a CREATE INDEX statement have their own IDs: they decide what the index holds, not
        // how a table's rows read
        if (_resolveTable != null)
            check = context == NC_PartIdx ? "DDL-69" : "DDL-68";

        Fail(check, message);
    }

    /// <summary>
    /// notValidImpl in resolve.c: "... prohibited in generated columns", "... prohibited in index expressions" or
    /// "... prohibited in partial index WHERE clauses"
    ///     const char *zIn = "partial index WHERE clauses";
    ///     if( pNC->ncFlags &amp; NC_IdxExpr )      zIn = "index expressions";
    ///     else if( pNC->ncFlags &amp; NC_IsCheck ) zIn = "CHECK constraints";
    ///     else if( pNC->ncFlags &amp; NC_GenCol ) zIn = "generated columns";
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L908 (notValidImpl)
    /// </summary>
    private void NotValid(int context, string what)
    {
        string where = context switch
        {
            NC_IdxExpr => "index expressions",
            NC_GenCol => "generated columns",
            _ => "partial index WHERE clauses"
        };
        ResolveError(context, "DDL-50", $"{what} prohibited in {where}");
    }

    /// <summary>
    /// sqlite3WalkExpr with resolveExprStep: each node before its operands, unless the step handled them
    /// </summary>
    private void ResolveWalk(Expr p, int context)
    {
        if (!ResolveStep(p, context))
            return;

        if (p.Left != null)
            ResolveWalk(p.Left, context);
        if (p.Right != null)
            ResolveWalk(p.Right, context);
        if (p.List != null)
            ResolveWalkList(p.List, context);
    }

    private void ResolveWalkList(ExprList list, int context)
    {
        foreach (ExprListItem item in list)
        {
            if (item.Expr != null)
                ResolveWalk(item.Expr, context);
        }
    }

    /// <summary>
    /// resolveExprStep in resolve.c. Returns false where SQLite returns WRC_Prune, as the operands have been handled.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L986 (resolveExprStep)
    /// </summary>
    private bool ResolveStep(Expr p, int context)
    {
        switch (p.Op)
        {
            case TK.NOTNULL:
            case TK.ISNULL:
                // The NOT NULL strength reduction applies only in a WHERE clause
                ResolveWalk(p.Left!, context);
                return false;

            case TK.DOT:
                //         sqlite3ResolveNotValid(pParse, pNC, "the \".\" operator",
                //                                NC_IdxExpr|NC_GenCol, 0, pExpr);
                // A partial index WHERE may name its table, and the main database
                if (context == NC_PartIdx)
                    LookupQualifiedName(p, context);
                else
                    NotValid(context, "the \".\" operator");
                return false;

            case TK.ID:
                LookupName(p, context);
                return false;

            case TK.FUNCTION:
                ResolveFunction(p, context);
                return false;

            case TK.EXISTS:
            case TK.SELECT:
            case TK.IN:
                //         if( pNC->ncFlags & NC_SelfRef ){
                //           notValidImpl(pParse, pNC, "subqueries", pExpr, pExpr);
                if (p.Select != null)
                {
                    NotValid(context, "subqueries");
                    return false;
                }

                return true;

            case TK.VARIABLE:
                NotValid(context, "parameters");
                return false;

            case TK.IS:
            case TK.ISNOT:
            {
                // "x IS TRUE", "x IS NOT FALSE" and the like are truth tests
                Expr right = SkipCollate(p.Right!);
                if (right.Op == TK.ID || right.Op == TK.TRUEFALSE)
                {
                    ResolveStep(right, context);
                    if (right.Op == TK.TRUEFALSE)
                    {
                        p.Op = TK.TRUTH;
                        return true;
                    }
                }

                CheckVectorSizes(p, context);
                return true;
            }

            case TK.BETWEEN:
            case TK.EQ:
            case TK.NE:
            case TK.LT:
            case TK.LE:
            case TK.GT:
            case TK.GE:
                CheckVectorSizes(p, context);
                return true;
        }

        return true;
    }

    /// <summary>
    /// The row value sizes of a comparison must match: "row value misused"
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L1477 (resolveExprStep)
    /// </summary>
    private void CheckVectorSizes(Expr p, int context)
    {
        int left = VectorSize(p.Left!);
        int right;
        if (p.Op == TK.BETWEEN)
        {
            right = VectorSize(p.List![0].Expr!);
            if (right == left)
                right = VectorSize(p.List[1].Expr!);
        }
        else
        {
            right = VectorSize(p.Right!);
        }

        if (left != right)
            ResolveError(context, "DDL-50", "row value misused");
    }

    /// <summary>
    /// lookupName in resolve.c, for a plain name: a column of the table (ASCII case ignored). Otherwise a
    /// double-quoted name is a string, and "true" or "false" the literal. The rowid cannot be named here.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L278 (lookupName)
    /// </summary>
    private void LookupName(Expr p, int context)
    {
        if (LookupColumn(p, p.Text!, context))
            return;

        // At init double-quoted strings are always enabled (areDoubleQuotedStringsEnabled)
        if (p.Has(Expr.EP_DblQuoted))
        {
            p.Op = TK.STRING;
            return;
        }

        if (IdToTrueFalse(p))
            return;

        ResolveError(context, "DDL-49", $"no such column: {p.Text}");
    }

    /// <summary>
    /// A column of the table the expression belongs to. A partial index WHERE may also name the rowid, unless the
    /// table is WITHOUT ROWID (TF_NoVisibleRowid):
    ///   if( cnt==0
    ///    &amp;&amp; cntTab>=1
    ///    &amp;&amp; pMatch
    ///    &amp;&amp; (pNC->ncFlags &amp; (NC_IdxExpr|NC_GenCol))==0
    ///    &amp;&amp; sqlite3IsRowid(zCol)
    ///    &amp;&amp; ALWAYS(VisibleRowid(pMatch->pSTab))
    ///   ){
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L623 (lookupName)
    /// </summary>
    private bool LookupColumn(Expr p, string name, int context)
    {
        ParsedTable table = _resolveTable ?? Table;
        int column = table.ColumnIndex(name);
        if (column >= 0)
        {
            p.Op = TK.COLUMN;
            p.Column = column;
            return true;
        }

        if (context == NC_PartIdx && !table.Has(ParsedTable.TF_WithoutRowid) && IsRowid(name))
        {
            p.Op = TK.COLUMN;
            p.Column = -1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// sqlite3IsRowid in expr.c: rowid, _rowid_ or oid, ignoring ASCII case
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L3042 (sqlite3IsRowid)
    /// </summary>
    private static bool IsRowid(string name) =>
        AsciiCaseInsensitiveComparer.Instance.Equals(name, "rowid") ||
        AsciiCaseInsensitiveComparer.Instance.Equals(name, "_rowid_") ||
        AsciiCaseInsensitiveComparer.Instance.Equals(name, "oid");

    /// <summary>
    /// lookupName in resolve.c, for "table.column" or "database.table.column" in a partial index WHERE: the table must
    /// be the indexed one, ignoring ASCII case, and the database is ignored
    ///   if( zDb ){
    ///     if( (pNC->ncFlags &amp; (NC_PartIdx|NC_IsCheck))!=0 ){
    ///       /* Silently ignore database qualifiers inside CHECK constraints and
    ///       ** partial indices. ...
    ///       zDb = 0;
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L316 (lookupName)
    ///         }else if( sqlite3StrICmp(zTab, pTab->zName)!=0 ){
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L428 (lookupName)
    /// </summary>
    private void LookupQualifiedName(Expr p, int context)
    {
        string tableName = p.Left!.Text!;
        Expr columnExpr = p.Right!;
        if (columnExpr.Op == TK.DOT)
        {
            tableName = columnExpr.Left!.Text!;
            columnExpr = columnExpr.Right!;
        }

        string column = columnExpr.Text!;
        if (AsciiCaseInsensitiveComparer.Instance.Equals(tableName, (_resolveTable ?? Table).Name) && LookupColumn(p, column, context))
            return;

        ResolveError(context, "DDL-49", $"no such column: {tableName}.{column}");
    }

    /// <summary>
    /// The TK_FUNCTION case of resolveExprStep: the function must take this many arguments, be deterministic, not be
    /// an aggregate or window function here, and FILTER, OVER and ORDER BY need an aggregate. An unknown function is
    /// allowed at init.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L1142 (resolveExprStep)
    /// </summary>
    private void ResolveFunction(Expr p, int context)
    {
        int n = p.List?.Count ?? 0;
        Window? window = IsWindowFunction(p) ? p.Window : null;
        bool wrongArgumentCount = false, isAggregate = false;
        string name = p.Text!;

        bool found = SqliteFunctions.TryFind(name, n, out SqliteFunctions.Function function);
        if (!found)
        {
            wrongArgumentCount = SqliteFunctions.TryFind(name, -2, out _);
        }
        else
        {
            isAggregate = (function.Flags & SqliteFunctions.FunctionFlags.Aggregate) != 0;
            if ((function.Flags & SqliteFunctions.FunctionFlags.Unlikely) != 0 && n == 2 && !IsProbability(p.List![1].Expr!))
                ResolveError(context, "DDL-50", $"second argument to {name}() must be a constant between 0.0 and 1.0");

            if ((function.Flags & SqliteFunctions.FunctionFlags.Constant) == 0)
                NotValid(context, "non-deterministic functions");

            //           if( pNC->ncFlags & NC_FromDDL ) ExprSetProperty(pExpr, EP_FromDDL);
            //           sqlite3ExprFunctionUsable(pParse, pExpr, pDef);
            if ((function.Flags & SqliteFunctions.FunctionFlags.DirectOnly) != 0)
                ResolveError(context, "DDL-50", $"unsafe use of {name}()");
        }

        if (found && (function.Flags & SqliteFunctions.FunctionFlags.WindowValue) == 0 && window != null)
            ResolveError(context, "DDL-50", $"{name}() may not be used as a window function");
        else if (isAggregate)
            // A table's own expressions have no aggregate context (NC_AllowAgg)
            ResolveError(context, "DDL-50", $"misuse of {((function.Flags & SqliteFunctions.FunctionFlags.Window) != 0 || window != null ? "window" : "aggregate")} function {name}()");
        // An unknown function is allowed while the schema is read ("no such function" only when !db->init.busy), but
        // not with FILTER, OVER or ORDER BY
        else if (wrongArgumentCount)
            ResolveError(context, "DDL-50", $"wrong number of arguments to function {name}()");
        else if (p.Has(Expr.EP_WinFunc))
            ResolveError(context, "DDL-50", $"FILTER may not be used with non-aggregate {name}()");
        else if (p.Left != null)
            ResolveError(context, "DDL-50", $"ORDER BY may not be used with non-aggregate {name}()");

        if (p.List != null)
            ResolveWalkList(p.List, context);
    }

    /// <summary>
    /// exprProbability in resolve.c: a floating point literal no greater than 1.0
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/resolve.c#L936 (exprProbability)
    /// </summary>
    private static bool IsProbability(Expr p)
    {
        return p.Op == TK.FLOAT &&
               double.TryParse(p.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) &&
               r <= 1.0;
    }
}
