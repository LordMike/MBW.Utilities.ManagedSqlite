using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// A token of the SQL text: where it starts and how long it is, as SQLite's Token (z, n)
/// </summary>
internal readonly struct Token
{
    public readonly int Start;
    public readonly int Length;

    public Token(int start, int length)
    {
        Start = start;
        Length = length;
    }

    public int End => Start + Length;
}

/// <summary>
/// The parts of SQLite's Expr (sqliteInt.h) that the checks made while loading a schema look at
/// </summary>
internal sealed class Expr
{
    // EP_* in sqliteInt.h, for the flags kept here
    public const int EP_Distinct = 0x000004, EP_HasFunc = 0x000008, EP_Collate = 0x000200, EP_IntValue = 0x000800,
        EP_Subquery = 0x400000, EP_WinFunc = 0x1000000, EP_Quoted = 0x4000000, EP_DblQuoted = 0x000080,
        EP_IsTrue = 0x10000000, EP_IsFalse = 0x20000000, EP_InfixFunc = 0x000100;

    /// <summary>
    /// EP_Propagate: the flags a node takes from its operands
    /// </summary>
    public const int EP_Propagate = EP_Collate | EP_Subquery | EP_HasFunc;

    public int Op;

    /// <summary>
    /// u.zToken: the token's text, dequoted for a quoted name or string
    /// </summary>
    public string? Text;

    /// <summary>
    /// u.iValue, for an EP_IntValue integer
    /// </summary>
    public int IntValue;

    public int Flags;
    public Expr? Left;
    public Expr? Right;

    /// <summary>
    /// x.pList: function arguments, the IN list, the BETWEEN bounds, a vector, or a CASE's WHEN/THEN/ELSE terms
    /// </summary>
    public ExprList? List;

    /// <summary>
    /// x.pSelect, for a subquery, EXISTS or IN (SELECT ...)
    /// </summary>
    public Select? Select;

    /// <summary>
    /// y.pWin, for a function with FILTER or OVER
    /// </summary>
    public Window? Window;

    /// <summary>
    /// nHeight, the depth of the tree below and including this node
    /// </summary>
    public int Height = 1;

    /// <summary>
    /// iColumn, once a name has been resolved to a column of the table (TK_COLUMN)
    /// </summary>
    public int Column = -1;

    public Expr(int op)
    {
        Op = op;
    }

    public bool Has(int flags) => (Flags & flags) != 0;
}

internal sealed class ExprListItem
{
    public Expr? Expr;

    /// <summary>
    /// zEName, the dequoted name of an identifier list term
    /// </summary>
    public string? Name;

    /// <summary>
    /// fg.sortFlags: SQLITE_SO_DESC (1), SQLITE_SO_UNDEFINED (-1, stored as 0xff in SQLite) or ASC (0)
    /// </summary>
    public int SortOrder;

    /// <summary>
    /// fg.bNulls: NULLS FIRST or NULLS LAST was given
    /// </summary>
    public bool Nulls;

    /// <summary>
    /// The NULLS order given, for the error message: true for NULLS LAST
    /// </summary>
    public bool NullsLast;
}

internal sealed class ExprList : List<ExprListItem>
{
}

/// <summary>
/// The parts of a SELECT that the parser's own checks look at (parserDoubleLinkSelect in parse.y), and the height and
/// result width an expression takes from it
/// </summary>
internal sealed class Select
{
    /// <summary>
    /// TK_SELECT, or for a compound term the operator that joins it to <see cref="Prior"/>: TK_UNION, TK_ALL,
    /// TK_EXCEPT or TK_INTERSECT
    /// </summary>
    public int Op = TK.SELECT;

    public Select? Prior;
    public bool HasOrderBy;
    public bool HasLimit;

    /// <summary>
    /// SF_Values: a VALUES clause
    /// </summary>
    public bool IsValues;

    /// <summary>
    /// SF_MultiValue: one VALUES clause of several rows, which is not limited in its number of terms
    /// </summary>
    public bool IsMultiValue;

    /// <summary>
    /// The number of result columns (pEList->nExpr), for sqlite3ExprVectorSize
    /// </summary>
    public int ColumnCount = 1;

    /// <summary>
    /// The greatest height of this term's expressions: WHERE, HAVING, LIMIT, the result columns, GROUP BY and ORDER BY
    /// (heightOfSelect in expr.c)
    /// </summary>
    public int Height;

    /// <summary>
    /// The height of the whole compound, as heightOfSelect walks pPrior
    /// </summary>
    public int ChainHeight
    {
        get
        {
            int height = 0;
            for (Select? p = this; p != null; p = p.Prior)
                height = System.Math.Max(height, p.Height);
            return height;
        }
    }
}

/// <summary>
/// The parts of a window definition that the parser checks
/// </summary>
internal sealed class Window
{
    /// <summary>
    /// eFrmType == TK_FILTER: only a FILTER clause, no OVER
    /// </summary>
    public bool FilterOnly;

    /// <summary>
    /// zName, for a definition in a WINDOW clause, or the name in "OVER name"
    /// </summary>
    public string? Name;

    /// <summary>
    /// zBase: the window this one is based on, as in "OVER (base ORDER BY x)"
    /// </summary>
    public string? Base;

    public bool HasPartition;
    public bool HasOrderBy;

    /// <summary>
    /// bImplicitFrame: no frame specification was given
    /// </summary>
    public bool ImplicitFrame;

    /// <summary>
    /// pNextWin: the definitions before this one in a WINDOW clause
    /// </summary>
    public Window? Next;
}

/// <summary>
/// The parts of a FROM clause the parser checks: how many terms it has so far
/// </summary>
internal sealed class SrcList
{
    public int Count;
}
