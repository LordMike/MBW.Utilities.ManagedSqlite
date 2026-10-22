using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// Building expressions as the parser's actions do (expr.c and the %include blocks of parse.y), with the checks SQLite
/// makes while it builds them
/// </summary>
internal sealed partial class SchemaParser
{
    /// <summary>
    /// SQLITE_MAX_EXPR_DEPTH, the default for SQLITE_LIMIT_EXPR_DEPTH
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/sqliteLimit.h#L102
    /// </summary>
    private const int MaxExprDepth = 1000;

    /// <summary>
    /// SQLITE_MAX_FUNCTION_ARG, the default for SQLITE_LIMIT_FUNCTION_ARG
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/sqliteLimit.h#L148
    /// </summary>
    private const int MaxFunctionArgs = 1000;

    /// <summary>
    /// SQLITE_MAX_COLUMN, the default for SQLITE_LIMIT_COLUMN
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/sqliteLimit.h#L73
    /// </summary>
    private const int MaxColumns = 2000;

    /// <summary>
    /// SQLITE_MAX_COMPOUND_SELECT, the default for SQLITE_LIMIT_COMPOUND_SELECT
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/sqliteLimit.h#L129
    /// </summary>
    private const int MaxCompoundSelect = 500;

    // SQLITE_SO_* in sqliteInt.h
    private const int SO_ASC = 0, SO_DESC = 1, SO_UNDEFINED = -1;

    /// <summary>
    /// tokenExpr in parse.y: a node for one token, dequoted when it is quoted
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L1140 (tokenExpr)
    /// </summary>
    private Expr TokenExpr(int op, Token t) => ExprAlloc(op, Text(t), true);

    /// <summary>
    /// sqlite3ExprAlloc in expr.c, for a node with a token: a quoted token is dequoted when asked, which marks the node
    /// EP_Quoted, and EP_DblQuoted for "..." (sqlite3DequoteExpr in util.c)
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L931 (sqlite3ExprAlloc)
    /// </summary>
    private static Expr ExprAlloc(int op, string? text, bool dequote)
    {
        Expr p = new Expr(op) { Text = text };
        if (dequote && text != null && text.Length > 0 && SqliteTokenizer.IsQuote(text[0]))
        {
            p.Flags |= text[0] == '"' ? Expr.EP_Quoted | Expr.EP_DblQuoted : Expr.EP_Quoted;
            p.Text = SqliteTokenizer.Dequote(text);
        }

        return p;
    }

    /// <summary>
    /// sqlite3ExprInt32 in expr.c: an integer literal held as its value
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L981 (sqlite3ExprInt32)
    /// </summary>
    private static Expr Int32Expr(int value)
    {
        return new Expr(TK.INTEGER)
        {
            IntValue = value,
            Flags = Expr.EP_IntValue | (value != 0 ? Expr.EP_IsTrue : Expr.EP_IsFalse)
        };
    }

    /// <summary>
    /// sqlite3ExprCheckHeight in expr.c: "Expression tree is too large (maximum depth 1000)"
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L792 (sqlite3ExprCheckHeight)
    /// </summary>
    private void CheckHeight(int height)
    {
        if (height > MaxExprDepth)
            Fail("DDL-85", $"Expression tree is too large (maximum depth {MaxExprDepth})");
    }

    /// <summary>
    /// sqlite3PExpr and sqlite3ExprAttachSubtrees in expr.c: a node over up to two operands, which takes their
    /// EP_Propagate flags, one more than their height, and the height check
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1043 (sqlite3PExpr)
    /// </summary>
    private Expr PExpr(int op, Expr? left, Expr? right)
    {
        Expr p = new Expr(op);
        if (right != null)
        {
            p.Right = right;
            p.Flags |= Expr.EP_Propagate & right.Flags;
            p.Height = right.Height + 1;
        }

        if (left != null)
        {
            p.Left = left;
            p.Flags |= Expr.EP_Propagate & left.Flags;
            if (left.Height >= p.Height)
                p.Height = left.Height + 1;
        }

        CheckHeight(p.Height);
        return p;
    }

    /// <summary>
    /// sqlite3ExprSetHeightAndFlags in expr.c (exprSetHeight): the height of a node with a list or a subquery, and the
    /// EP_Propagate flags of the list
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L872 (sqlite3ExprSetHeightAndFlags)
    /// </summary>
    private void SetHeightAndFlags(Expr p)
    {
        int height = p.Left?.Height ?? 0;
        if (p.Right != null && p.Right.Height > height)
            height = p.Right.Height;

        if (p.Select != null)
        {
            height = System.Math.Max(height, p.Select.ChainHeight);
        }
        else if (p.List != null)
        {
            height = System.Math.Max(height, ListHeight(p.List));
            p.Flags |= Expr.EP_Propagate & ListFlags(p.List);
        }

        p.Height = height + 1;
        CheckHeight(p.Height);
    }

    private static int ListHeight(ExprList? list)
    {
        int height = 0;
        if (list != null)
        {
            foreach (ExprListItem item in list)
                height = System.Math.Max(height, item.Expr?.Height ?? 0);
        }

        return height;
    }

    /// <summary>
    /// sqlite3ExprListFlags in expr.c: the flags of all the list's expressions together
    /// </summary>
    private static int ListFlags(ExprList list)
    {
        int flags = 0;
        foreach (ExprListItem item in list)
            flags |= item.Expr?.Flags ?? 0;
        return flags;
    }

    /// <summary>
    /// sqlite3PExprAddSelect in expr.c
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1068 (sqlite3PExprAddSelect)
    /// </summary>
    private void AddSelect(Expr p, Select select)
    {
        p.Select = select;
        p.Flags |= Expr.EP_Subquery;
        SetHeightAndFlags(p);
    }

    /// <summary>
    /// sqlite3ExprAnd in expr.c: "x AND y", or the integer 0 when one side is the literal false and neither is a function
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1145 (sqlite3ExprAnd)
    /// </summary>
    private Expr? ExprAnd(Expr? left, Expr? right)
    {
        if (left == null)
            return right;
        if (right == null)
            return left;

        // EP_OuterON and EP_InnerON are only set on join constraints, after parsing
        int f = left.Flags | right.Flags;
        if ((f & (Expr.EP_IsFalse | Expr.EP_HasFunc)) == Expr.EP_IsFalse)
            return Int32Expr(0);

        return PExpr(TK.AND, left, right);
    }

    /// <summary>
    /// sqlite3ExprFunction in expr.c: a function call, named by its dequoted token
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1169 (sqlite3ExprFunction)
    /// </summary>
    private Expr Function(ExprList? list, Token name, bool distinct)
    {
        Expr p = ExprAlloc(TK.FUNCTION, Text(name), true);
        if (list != null && list.Count > MaxFunctionArgs)
            Fail("SCHEMA-22", $"too many arguments on function {Text(name)}");

        p.List = list;
        p.Flags |= Expr.EP_HasFunc;
        SetHeightAndFlags(p);
        if (distinct)
            p.Flags |= Expr.EP_Distinct;
        return p;
    }

    /// <summary>
    /// sqlite3ExprAddFunctionOrderBy in expr.c: ORDER BY inside a function's arguments. It is ignored on a function
    /// without arguments, and an error on a window function.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1219 (sqlite3ExprAddFunctionOrderBy)
    /// </summary>
    private void AddFunctionOrderBy(Expr function, ExprList orderBy)
    {
        if (function.List == null || function.List.Count == 0)
            return;

        if (IsWindowFunction(function))
            Fail("SCHEMA-22", $"ORDER BY may not be used with non-aggregate {function.Text}()");
        if (orderBy.Count > MaxColumns)
            Fail("SCHEMA-22", "too many terms in ORDER BY clause");

        // The TK_ORDER node holding the list; its height is not counted, as in SQLite
        function.Left = new Expr(TK.ORDER) { List = orderBy };
    }

    /// <summary>
    /// IsWindowFunc in sqliteInt.h: a function with OVER, not one with only FILTER
    /// </summary>
    private static bool IsWindowFunction(Expr p) => p.Has(Expr.EP_WinFunc) && p.Window is { FilterOnly: false };

    /// <summary>
    /// sqlite3WindowAttach in window.c: FILTER or OVER on a function
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/window.c#L1308 (sqlite3WindowAttach)
    /// </summary>
    private void WindowAttach(Expr function, Window window)
    {
        function.Window = window;
        function.Flags |= Expr.EP_WinFunc;
        if (function.Has(Expr.EP_Distinct) && !window.FilterOnly)
            Fail("SCHEMA-22", "DISTINCT is not supported for window functions");
    }

    /// <summary>
    /// sqlite3WindowAlloc in window.c: a frame's start may not come after its end
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/window.c#L1175 (sqlite3WindowAlloc)
    /// </summary>
    private Window WindowAlloc(int type, int start, int end)
    {
        if ((start == TK.CURRENT && end == TK.PRECEDING) ||
            (start == TK.FOLLOWING && (end == TK.PRECEDING || end == TK.CURRENT)))
            Fail("SCHEMA-22", "unsupported frame specification");

        return new Window { ImplicitFrame = type == 0 };
    }

    /// <summary>
    /// sqlite3WindowChain in window.c: a window in a WINDOW clause that is based on an earlier one may not override
    /// its PARTITION BY, ORDER BY or frame
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/window.c#L1274 (sqlite3WindowChain)
    /// </summary>
    private void WindowChain(Window window, Window? list)
    {
        if (window.Base == null)
            return;

        // windowFind in window.c
        Window? existing = list;
        while (existing != null && !AsciiCaseInsensitiveComparer.Instance.Equals(existing.Name, window.Base))
            existing = existing.Next;
        if (existing == null)
        {
            Fail("SCHEMA-22", $"no such window: {window.Base}");
            return;
        }

        string? error = null;
        if (window.HasPartition)
            error = "PARTITION clause";
        else if (existing.HasOrderBy && window.HasOrderBy)
            error = "ORDER BY clause";
        else if (!existing.ImplicitFrame)
            error = "frame specification";

        if (error != null)
            Fail("SCHEMA-22", $"cannot override {error} of window: {window.Base}");

        window.HasPartition = existing.HasPartition;
        window.HasOrderBy |= existing.HasOrderBy;
        window.Base = null;
    }

    /// <summary>
    /// sqlite3ExprAddCollateToken in expr.c: the COLLATE node over an expression; its text is the collation's name
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L175 (sqlite3ExprAddCollateToken)
    /// </summary>
    private Expr AddCollate(Expr p, Token name)
    {
        if (name.Length == 0)
            return p;

        Expr collate = ExprAlloc(TK.COLLATE, Text(name), true);
        collate.Left = p;
        collate.Flags |= Expr.EP_Collate;
        return collate;
    }

    /// <summary>
    /// sqlite3ExprIdToTrueFalse in expr.c: an unquoted name "true" or "false" (ASCII case ignored) becomes the
    /// TRUE/FALSE literal
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L2334 (sqlite3ExprIdToTrueFalse)
    /// </summary>
    private static bool IdToTrueFalse(Expr p)
    {
        if (p.Has(Expr.EP_Quoted | Expr.EP_IntValue))
            return false;

        int value;
        if (AsciiCaseInsensitiveComparer.Instance.Equals(p.Text, "true"))
            value = Expr.EP_IsTrue;
        else if (AsciiCaseInsensitiveComparer.Instance.Equals(p.Text, "false"))
            value = Expr.EP_IsFalse;
        else
            return false;

        p.Op = TK.TRUEFALSE;
        p.Flags |= value;
        return true;
    }

    /// <summary>
    /// sqlite3PExprIsNull in parse.y: "x IS NULL" or "x NOTNULL", which is the integer 0 or 1 when x is a literal
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L1388 (sqlite3PExprIsNull)
    /// </summary>
    private Expr PExprIsNull(int op, Expr left)
    {
        Expr p = left;
        while (p.Op == TK.UPLUS || p.Op == TK.UMINUS)
            p = p.Left!;

        if (p.Op is TK.INTEGER or TK.STRING or TK.FLOAT or TK.BLOB)
            return Int32Expr(op == TK.NOTNULL ? 1 : 0);

        return PExpr(op, left, null);
    }

    /// <summary>
    /// sqlite3PExprIs in parse.y: "x IS y" or "x IS NOT y", which is IS NULL or NOT NULL when y is NULL
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L1415 (sqlite3PExprIs)
    /// </summary>
    private Expr PExprIs(int op, Expr left, Expr right)
    {
        if (right.Op == TK.NULL)
            return PExprIsNull(op == TK.IS ? TK.ISNULL : TK.NOTNULL, left);

        return PExpr(op, left, right);
    }

    /// <summary>
    /// sqlite3ExprListAppend in expr.c
    /// </summary>
    private static ExprList ListAppend(ExprList? list, Expr? p)
    {
        list ??= new ExprList();
        list.Add(new ExprListItem { Expr = p });
        return list;
    }

    /// <summary>
    /// sqlite3ExprListSetSortOrder in expr.c: the ASC or DESC, and NULLS FIRST or LAST, of the list's last term
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L2155 (sqlite3ExprListSetSortOrder)
    /// </summary>
    private static void SetSortOrder(ExprList list, int sortOrder, int nulls)
    {
        ExprListItem item = list[list.Count - 1];
        item.SortOrder = sortOrder == SO_UNDEFINED ? SO_ASC : sortOrder;
        if (nulls != SO_UNDEFINED)
        {
            item.Nulls = true;
            item.NullsLast = nulls == SO_DESC;
        }
    }

    /// <summary>
    /// sqlite3HasExplicitNulls in build.c: NULLS FIRST and NULLS LAST are only allowed in ORDER BY
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L3913 (sqlite3HasExplicitNulls)
    /// </summary>
    private bool HasExplicitNulls(ExprList? list)
    {
        if (list == null)
            return false;

        foreach (ExprListItem item in list)
        {
            if (item.Nulls)
            {
                // Lenient reads on as if the clause were not there: it only orders NULLs, so the data reads the same
                FailRelaxable("DDL-51", $"unsupported use of NULLS {(item.NullsLast ? "LAST" : "FIRST")}");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// sqlite3ExprSkipCollate in expr.c: the expression under any COLLATE operators
    /// </summary>
    private static Expr SkipCollate(Expr p)
    {
        while (p.Op == TK.COLLATE)
            p = p.Left!;
        return p;
    }

    /// <summary>
    /// sqlite3ExprVectorSize in expr.c: the number of values in a row value, or 1
    /// </summary>
    private static int VectorSize(Expr p)
    {
        if (p.Op == TK.VECTOR)
            return p.List!.Count;
        if (p.Op == TK.SELECT)
            return p.Select!.ColumnCount;
        return 1;
    }

    /// <summary>
    /// sqlite3GetInt32 in util.c: the text as a 32-bit integer, decimal with an optional sign, or hexadecimal "0x..."
    /// up to 0x7fffffff; anything after the digits is ignored
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L1303 (sqlite3GetInt32)
    /// </summary>
    private static bool GetInt32(string z, out int value)
    {
        value = 0;
        int i = 0;
        bool neg = false;
        char At(int n) => n < z.Length ? z[n] : '\0';

        if (At(0) == '-')
        {
            neg = true;
            i++;
        }
        else if (At(0) == '+')
        {
            i++;
        }
        else if (At(0) == '0' && (At(1) == 'x' || At(1) == 'X') && IsXDigit(At(2)))
        {
            i = 2;
            while (At(i) == '0')
                i++;
            uint u = 0;
            int k;
            for (k = 0; k < 8 && IsXDigit(At(i + k)); k++)
                u = u * 16 + (uint)HexToInt(At(i + k));
            if ((u & 0x80000000) == 0 && !IsXDigit(At(i + k)))
            {
                value = (int)u;
                return true;
            }

            return false;
        }

        if (!IsDigit(At(i)))
            return false;
        while (At(i) == '0')
            i++;

        long v = 0;
        int count;
        for (count = 0; count < 11 && IsDigit(At(i + count)); count++)
            v = v * 10 + (At(i + count) - '0');

        if (count > 10 || v - (neg ? 1 : 0) > 2147483647)
            return false;

        value = (int)(neg ? -v : v);
        return true;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool IsXDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    internal static int HexToInt(char c) => IsDigit(c) ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>
    /// term ::= INTEGER in parse.y: an integer that fits in 32 bits is held as its value (sqlite3ExprAlloc,
    /// tag-20240227-a), a larger one as its text
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L1195 (term ::= INTEGER)
    /// </summary>
    private Expr IntegerTerm(Token t)
    {
        string text = Text(t);
        return GetInt32(text, out int value) ? Int32Expr(value) : new Expr(TK.INTEGER) { Text = text };
    }

    /// <summary>
    /// sqlite3DequoteNumber in util.c, for term ::= QNUMBER: a number with "_" digit separators. Each "_" must have a
    /// digit (a hex digit in a hex number) on both sides.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L332 (sqlite3DequoteNumber)
    /// </summary>
    private Expr NumberTerm(Token t)
    {
        string text = Text(t);
        bool hex = text.Length > 1 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X');
        int op = TK.INTEGER;
        System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '_')
            {
                sb.Append(c);
                if (c == 'e' || c == 'E' || c == '.')
                    op = TK.FLOAT;
                continue;
            }

            char before = i > 0 ? text[i - 1] : '\0';
            char after = i + 1 < text.Length ? text[i + 1] : '\0';
            if (hex ? !IsXDigit(before) || !IsXDigit(after) : !IsDigit(before) || !IsDigit(after))
                Fail("DDL-20", $"unrecognized token: \"{text}\"");
        }

        if (hex)
            op = TK.INTEGER;

        string number = sb.ToString();
        if (op == TK.INTEGER && GetInt32(number, out int value))
            return new Expr(TK.INTEGER) { IntValue = value, Flags = Expr.EP_IntValue };

        return new Expr(op) { Text = number };
    }
}
