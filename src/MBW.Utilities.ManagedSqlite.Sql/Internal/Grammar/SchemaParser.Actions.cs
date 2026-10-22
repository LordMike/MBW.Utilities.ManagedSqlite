using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The actions of parse.y's rules, by rule. The left-hand side of a rule takes the stack entry of its first right-hand
/// symbol, so a rule without a case here passes that symbol's value on, which is what parse.y's rules without an
/// action, or with "A ::= B(A)", do. An empty rule starts from a cleared entry: no token, a null value and 0.
///
/// Only what reading a table needs is built: the CREATE TABLE, its expressions, and enough of a SELECT (in a
/// subquery) for the checks the parser makes. Statements other than CREATE TABLE are parsed and ignored.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y
/// </summary>
internal sealed partial class SchemaParser
{
    // SF_* in sqliteInt.h, for the distinct nonterminal
    private const int SF_Distinct = 0x0000001, SF_All = 0x0000002;

    /// <summary>
    /// SQLITE_MAX_VARIABLE_NUMBER, the default for SQLITE_LIMIT_VARIABLE_NUMBER
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/sqliteLimit.h#L190
    /// </summary>
    private const int MaxVariableNumber = 32766;

    /// <summary>
    /// pParse->nVar and pParse->pVList: the parameters numbered so far
    /// </summary>
    private int _variableCount;

    private readonly Dictionary<string, int> _variableNames = new Dictionary<string, int>();

    private void Reduce(int rule, int lhs)
    {
        ref StackEntry a = ref _stack[lhs];

        Token T(int i) => _stack[lhs + i].Token;
        int I(int i) => _stack[lhs + i].Int;
        int M(int i) => _stack[lhs + i].Major;
        Expr E(int i) => (Expr)_stack[lhs + i].Value!;
        Expr? OE(int i) => (Expr?)_stack[lhs + i].Value;
        ExprList? L(int i) => (ExprList?)_stack[lhs + i].Value;
        Select S(int i) => (Select)_stack[lhs + i].Value!;
        Window W(int i) => (Window)_stack[lhs + i].Value!;

        switch (SqliteGrammar.RuleNames[rule])
        {
            // ///////////////////// The CREATE TABLE statement

            case "cmdx ::= cmd":
                // sqlite3FinishCoding: at init this ends the parse (SQLITE_DONE), so nothing after the first
                // statement is read (DDL-11)
                _done = true;
                break;

            case "create_table ::= createkw temp TABLE ifnotexists nm dbnm":
                StartTable(T(4), T(5));
                break;
            case "ifnotexists ::= IF NOT EXISTS":
                a.Int = 1;
                break;
            case "temp ::= TEMP":
                // A = pParse->db->init.busy==0: TEMP is ignored while the schema is read (SCHEMA-29)
                a.Int = 0;
                break;
            case "create_table_args ::= LP columnlist conslist_opt RP table_option_set":
                EndTable(false, I(4));
                break;
            case "create_table_args ::= AS select":
                EndTable(true, 0);
                break;
            case "table_option_set ::= table_option_set COMMA table_option":
                a.Int = I(0) | I(2);
                break;
            case "table_option ::= WITHOUT nm":
                // The raw token, so a quoted "rowid" is not the option
                if (T(1).Length == 5 && AsciiCaseInsensitiveComparer.Instance.Equals(Text(T(1)), "rowid"))
                    a.Int = ParsedTable.TF_WithoutRowid;
                else
                    Fail("DDL-29", $"unknown table option: {Text(T(1))}");
                break;
            case "table_option ::= nm":
                if (T(0).Length == 6 && AsciiCaseInsensitiveComparer.Instance.Equals(Text(T(0)), "strict"))
                    a.Int = ParsedTable.TF_Strict;
                else
                    Fail("DDL-29", $"unknown table option: {Text(T(0))}");
                break;
            case "columnname ::= nm typetoken":
                AddColumn(T(0), T(1));
                break;

            case "typetoken ::= typename LP signed RP":
                a.Token = new Token(T(0).Start, T(3).End - T(0).Start);
                break;
            case "typetoken ::= typename LP signed COMMA signed RP":
                a.Token = new Token(T(0).Start, T(5).End - T(0).Start);
                break;
            case "typename ::= typename ID|STRING":
                a.Token = new Token(T(0).Start, T(1).End - T(0).Start);
                break;
            case "scanpt ::=":
                a.Int = _lookahead.Start;
                break;
            case "scantok ::=":
                a.Token = _lookahead;
                break;

            case "ccons ::= DEFAULT scantok term":
                AddDefaultValue(E(2), T(1).Start, T(1).End);
                break;
            case "ccons ::= DEFAULT LP expr RP":
                AddDefaultValue(E(2), T(1).Start + 1, T(3).Start);
                break;
            case "ccons ::= DEFAULT PLUS scantok term":
                AddDefaultValue(E(3), T(1).Start, T(2).End);
                break;
            case "ccons ::= DEFAULT MINUS scantok term":
                AddDefaultValue(PExpr(TK.UMINUS, E(3), null), T(1).Start, T(2).End);
                break;
            case "ccons ::= DEFAULT scantok ID|INDEXED":
            {
                Expr p = TokenExpr(TK.STRING, T(2));
                IdToTrueFalse(p);
                AddDefaultValue(p, T(2).Start, T(2).End);
                break;
            }
            case "ccons ::= NOT NULL onconf":
                AddNotNull(I(2));
                break;
            case "ccons ::= PRIMARY KEY sortorder onconf autoinc":
                AddPrimaryKey(null, I(3), I(4), I(2));
                break;
            case "ccons ::= UNIQUE onconf":
                CreateIndex(null, I(1), SO_ASC, false);
                break;
            case "ccons ::= CHECK LP expr RP":
            case "tcons ::= CHECK LP expr RP onconf":
                // sqlite3AddCheckConstraint: dropped, as SQLite drops a CHECK when the database is read-only (DDL-46,
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1911), so a CHECK naming no such
                // column is accepted, as by a database opened with SQLITE_OPEN_READONLY. The tests' SQLite can not
                // show this: a database it deserializes is read-only to writes only, and so resolves the CHECK.
                break;
            case "ccons ::= REFERENCES nm eidlist_opt refargs":
                CreateForeignKey(null, T(1), L(2));
                break;
            case "ccons ::= COLLATE ID|STRING":
                AddCollateType(T(1));
                break;
            case "generated ::= LP expr RP":
                AddGenerated(E(1), null);
                break;
            case "generated ::= LP expr RP ID":
                AddGenerated(E(1), T(3));
                break;
            case "autoinc ::= AUTOINCR":
                a.Int = 1;
                break;

            case "tcons ::= PRIMARY KEY LP sortlist autoinc RP onconf":
                AddPrimaryKey(L(3), I(6), I(4), SO_ASC);
                break;
            case "tcons ::= UNIQUE LP sortlist RP onconf":
                CreateIndex(L(2), I(4), SO_ASC, false);
                break;
            case "tcons ::= FOREIGN KEY LP eidlist RP REFERENCES nm eidlist_opt refargs defer_subclause_opt":
                CreateForeignKey(L(3), T(6), L(7));
                break;

            case "onconf ::=":
            case "orconf ::=":
                a.Int = OE_Default;
                break;
            case "onconf ::= ON CONFLICT resolvetype":
                a.Int = I(2);
                break;
            case "orconf ::= OR resolvetype":
                a.Int = I(1);
                break;
            case "resolvetype ::= IGNORE":
                a.Int = OE_Ignore;
                break;
            case "resolvetype ::= REPLACE":
                a.Int = OE_Replace;
                break;
            case "raisetype ::= ROLLBACK":
                a.Int = OE_Rollback;
                break;
            case "raisetype ::= ABORT":
                a.Int = OE_Abort;
                break;
            case "raisetype ::= FAIL":
                a.Int = OE_Fail;
                break;

            // ///////////////////// The SELECT statement, for subqueries

            case "select ::= WITH wqlist selectnowith":
                DoubleLinkSelect(S(2));
                a.Value = S(2);
                break;
            case "select ::= WITH RECURSIVE wqlist selectnowith":
                DoubleLinkSelect(S(3));
                a.Value = S(3);
                break;
            case "select ::= selectnowith":
                DoubleLinkSelect(S(0));
                break;
            case "selectnowith ::= selectnowith multiselect_op oneselect":
            {
                Select left = S(0);
                Select right = S(2);
                if (right.Prior != null)
                {
                    // The right side, itself a compound of VALUES rows, becomes a subquery
                    DoubleLinkSelect(right);
                    right = new Select { Height = 1 };
                }

                right.Op = I(1);
                right.Prior = left;
                left.IsMultiValue = false;
                right.IsMultiValue = false;
                a.Value = right;
                break;
            }
            case "multiselect_op ::= UNION":
            case "multiselect_op ::= EXCEPT|INTERSECT":
                a.Int = M(0);
                break;
            case "multiselect_op ::= UNION ALL":
                a.Int = TK.ALL;
                break;
            case "oneselect ::= SELECT distinct selcollist from where_opt groupby_opt having_opt orderby_opt limit_opt":
                a.Value = NewSelect(L(2), OE(4), L(5), OE(6), L(7), OE(8));
                break;
            case "oneselect ::= SELECT distinct selcollist from where_opt groupby_opt having_opt window_clause orderby_opt limit_opt":
                a.Value = NewSelect(L(2), OE(4), L(5), OE(6), L(8), OE(9));
                break;
            case "values ::= VALUES LP nexprlist RP":
            {
                Select values = NewSelect(L(2), null, null, null, null, null);
                values.IsValues = true;
                a.Value = values;
                break;
            }
            case "mvalues ::= values COMMA LP nexprlist RP":
            case "mvalues ::= mvalues COMMA LP nexprlist RP":
                a.Value = MultiValues(S(0), L(3)!);
                break;
            case "distinct ::= DISTINCT":
                a.Int = SF_Distinct;
                break;
            case "distinct ::= ALL":
                a.Int = SF_All;
                break;

            case "selcollist ::= sclp scanpt expr scanpt as":
                a.Value = ListAppend(L(0), E(2));
                break;
            case "selcollist ::= sclp scanpt STAR":
                a.Value = ListAppend(L(0), new Expr(TK.ASTERISK));
                break;
            case "selcollist ::= sclp scanpt nm DOT STAR":
                a.Value = ListAppend(L(0), PExpr(TK.DOT, TokenExpr(TK.ID, T(2)), new Expr(TK.ASTERISK)));
                break;
            case "as ::= AS nm":
                a.Token = T(1);
                break;
            case "from ::= FROM seltablist":
                a.Value = _stack[lhs + 1].Value;
                break;
            case "seltablist ::= stl_prefix nm dbnm as on_using":
                a.Value = AppendFromTerm((SrcList?)a.Value, I(4));
                break;
            case "seltablist ::= stl_prefix nm dbnm as indexed_by on_using":
                a.Value = AppendFromTerm((SrcList?)a.Value, I(5));
                break;
            case "seltablist ::= stl_prefix nm dbnm LP exprlist RP as on_using":
                a.Value = AppendFromTerm((SrcList?)a.Value, I(7));
                break;
            case "seltablist ::= stl_prefix LP select RP as on_using":
                a.Value = AppendFromTerm((SrcList?)a.Value, I(5));
                break;
            case "seltablist ::= stl_prefix LP seltablist RP as on_using":
                if (a.Value == null && T(4).Length == 0 && I(5) == 0)
                    a.Value = _stack[lhs + 2].Value;
                else
                    a.Value = AppendFromTerm((SrcList?)a.Value, I(5));
                break;
            case "dbnm ::= DOT nm":
                a.Token = T(1);
                break;
            case "joinop ::= JOIN_KW JOIN":
                JoinType(T(0), null, null);
                break;
            case "joinop ::= JOIN_KW nm JOIN":
                JoinType(T(0), T(1), null);
                break;
            case "joinop ::= JOIN_KW nm nm JOIN":
                JoinType(T(0), T(1), T(2));
                break;
            case "on_using ::= ON expr":
                a.Int = 1;
                break;
            case "on_using ::= USING LP idlist RP":
                a.Int = 2;
                break;

            case "orderby_opt ::= ORDER BY sortlist":
            case "groupby_opt ::= GROUP BY nexprlist":
                a.Value = _stack[lhs + 2].Value;
                break;
            case "sortlist ::= sortlist COMMA expr sortorder nulls":
                a.Value = ListAppend(L(0), E(2));
                SetSortOrder(L(0)!, I(3), I(4));
                break;
            case "sortlist ::= expr sortorder nulls":
            {
                ExprList list = ListAppend(null, E(0));
                SetSortOrder(list, I(1), I(2));
                a.Value = list;
                break;
            }
            case "sortorder ::= ASC":
            case "nulls ::= NULLS FIRST":
                a.Int = SO_ASC;
                break;
            case "sortorder ::= DESC":
            case "nulls ::= NULLS LAST":
                a.Int = SO_DESC;
                break;
            case "sortorder ::=":
            case "nulls ::=":
                a.Int = SO_UNDEFINED;
                break;
            case "having_opt ::= HAVING expr":
            case "where_opt ::= WHERE expr":
                a.Value = _stack[lhs + 1].Value;
                break;
            case "limit_opt ::= LIMIT expr":
                a.Value = PExpr(TK.LIMIT, E(1), null);
                break;
            case "limit_opt ::= LIMIT expr OFFSET expr":
                a.Value = PExpr(TK.LIMIT, E(1), E(3));
                break;
            case "limit_opt ::= LIMIT expr COMMA expr":
                a.Value = PExpr(TK.LIMIT, E(3), E(1));
                break;

            // ///////////////////// Expressions

            case "expr ::= LP expr RP":
                a.Value = _stack[lhs + 1].Value;
                break;
            case "expr ::= ID|INDEXED|JOIN_KW":
                a.Value = TokenExpr(TK.ID, T(0));
                break;
            case "expr ::= nm DOT nm":
                a.Value = PExpr(TK.DOT, TokenExpr(TK.ID, T(0)), TokenExpr(TK.ID, T(2)));
                break;
            case "expr ::= nm DOT nm DOT nm":
            {
                Expr first = TokenExpr(TK.ID, T(0));
                Expr rest = PExpr(TK.DOT, TokenExpr(TK.ID, T(2)), TokenExpr(TK.ID, T(4)));
                a.Value = PExpr(TK.DOT, first, rest);
                break;
            }
            case "term ::= NULL|FLOAT|BLOB":
                a.Value = TokenExpr(M(0), T(0));
                break;
            case "term ::= STRING":
                a.Value = TokenExpr(TK.STRING, T(0));
                break;
            case "term ::= INTEGER":
                a.Value = IntegerTerm(T(0));
                break;
            case "term ::= QNUMBER":
                a.Value = NumberTerm(T(0));
                break;
            case "expr ::= VARIABLE":
                a.Value = Variable(T(0));
                break;
            case "expr ::= expr COLLATE ID|STRING":
                a.Value = AddCollate(E(0), T(2));
                break;
            case "expr ::= CAST LP expr AS typetoken RP":
            {
                // sqlite3ExprAttachSubtrees, which takes the operand's height without checking it
                Expr operand = E(2);
                a.Value = new Expr(TK.CAST)
                {
                    Text = SqliteTokenizer.Dequote(Text(T(4))),
                    Left = operand,
                    Flags = Expr.EP_Propagate & operand.Flags,
                    Height = operand.Height + 1
                };
                break;
            }
            case "expr ::= ID|INDEXED|JOIN_KW LP distinct exprlist RP":
                a.Value = Function(L(3), T(0), I(2) == SF_Distinct);
                break;
            case "expr ::= ID|INDEXED|JOIN_KW LP distinct exprlist ORDER BY sortlist RP":
            {
                Expr function = Function(L(3), T(0), I(2) == SF_Distinct);
                AddFunctionOrderBy(function, L(6)!);
                a.Value = function;
                break;
            }
            case "expr ::= ID|INDEXED|JOIN_KW LP STAR RP":
                a.Value = Function(null, T(0), false);
                break;
            case "expr ::= ID|INDEXED|JOIN_KW LP distinct exprlist RP filter_over":
            {
                Expr function = Function(L(3), T(0), I(2) == SF_Distinct);
                WindowAttach(function, W(5));
                a.Value = function;
                break;
            }
            case "expr ::= ID|INDEXED|JOIN_KW LP distinct exprlist ORDER BY sortlist RP filter_over":
            {
                Expr function = Function(L(3), T(0), I(2) == SF_Distinct);
                WindowAttach(function, W(8));
                AddFunctionOrderBy(function, L(6)!);
                a.Value = function;
                break;
            }
            case "expr ::= ID|INDEXED|JOIN_KW LP STAR RP filter_over":
            {
                Expr function = Function(null, T(0), false);
                WindowAttach(function, W(4));
                a.Value = function;
                break;
            }
            case "term ::= CTIME_KW":
                a.Value = Function(null, T(0), false);
                break;
            case "expr ::= LP nexprlist COMMA expr RP":
            {
                // A row value. Its height stays 1, as SQLite does not set it from the list.
                ExprList list = ListAppend(L(1), E(3));
                Expr vector = PExpr(TK.VECTOR, null, null);
                vector.List = list;
                foreach (ExprListItem item in list)
                    vector.Flags |= item.Expr!.Flags & Expr.EP_Propagate;
                a.Value = vector;
                break;
            }
            case "expr ::= expr AND expr":
                a.Value = ExprAnd(E(0), E(2));
                break;
            case "expr ::= expr OR expr":
            case "expr ::= expr LT|GT|GE|LE expr":
            case "expr ::= expr EQ|NE expr":
            case "expr ::= expr BITAND|BITOR|LSHIFT|RSHIFT expr":
            case "expr ::= expr PLUS|MINUS expr":
            case "expr ::= expr STAR|SLASH|REM expr":
            case "expr ::= expr CONCAT expr":
                a.Value = PExpr(M(1), E(0), E(2));
                break;
            case "likeop ::= NOT LIKE_KW|MATCH":
                a.Token = T(1);
                a.Int = 1;
                break;
            case "expr ::= expr likeop expr":
            case "expr ::= expr likeop expr ESCAPE expr":
            {
                // x LIKE y is like(y, x), and x LIKE y ESCAPE z is like(y, x, z)
                ExprList list = ListAppend(null, E(2));
                ListAppend(list, E(0));
                if (SqliteGrammar.RuleRhsCount[rule] == -5)
                    ListAppend(list, E(4));
                Expr p = Function(list, T(1), false);
                if (I(1) != 0)
                    p = PExpr(TK.NOT, p, null);
                p.Flags |= Expr.EP_InfixFunc;
                a.Value = p;
                break;
            }
            case "expr ::= expr ISNULL|NOTNULL":
                a.Value = PExprIsNull(M(1), E(0));
                break;
            case "expr ::= expr NOT NULL":
                a.Value = PExprIsNull(TK.NOTNULL, E(0));
                break;
            case "expr ::= expr IS expr":
                a.Value = PExprIs(TK.IS, E(0), E(2));
                break;
            case "expr ::= expr IS NOT expr":
                a.Value = PExprIs(TK.ISNOT, E(0), E(3));
                break;
            case "expr ::= expr IS NOT DISTINCT FROM expr":
                a.Value = PExprIs(TK.IS, E(0), E(5));
                break;
            case "expr ::= expr IS DISTINCT FROM expr":
                a.Value = PExprIs(TK.ISNOT, E(0), E(4));
                break;
            case "expr ::= NOT expr":
            case "expr ::= BITNOT expr":
                a.Value = PExpr(M(0), E(1), null);
                break;
            case "expr ::= PLUS|MINUS expr":
            {
                int op = M(0) + (TK.UPLUS - TK.PLUS);
                Expr operand = E(1);
                if (operand.Op == TK.UPLUS)
                {
                    operand.Op = op;
                    a.Value = operand;
                }
                else
                {
                    a.Value = PExpr(op, operand, null);
                }

                break;
            }
            case "expr ::= expr PTR expr":
            {
                ExprList list = ListAppend(null, E(0));
                ListAppend(list, E(2));
                a.Value = Function(list, T(1), false);
                break;
            }
            case "between_op ::= NOT BETWEEN":
            case "in_op ::= NOT IN":
                a.Int = 1;
                break;
            case "expr ::= expr between_op expr AND expr":
            {
                ExprList list = ListAppend(null, E(2));
                ListAppend(list, E(4));
                Expr p = PExpr(TK.BETWEEN, E(0), null);
                p.List = list;
                SetHeightAndFlags(p);
                if (I(1) != 0)
                    p = PExpr(TK.NOT, p, null);
                a.Value = p;
                break;
            }
            case "expr ::= expr in_op LP exprlist RP":
                a.Value = InList(E(0), I(1) != 0, L(3));
                break;
            case "expr ::= LP select RP":
            {
                Expr p = PExpr(TK.SELECT, null, null);
                AddSelect(p, S(1));
                a.Value = p;
                break;
            }
            case "expr ::= expr in_op LP select RP":
            {
                Expr p = PExpr(TK.IN, E(0), null);
                AddSelect(p, S(3));
                a.Value = I(1) != 0 ? PExpr(TK.NOT, p, null) : p;
                break;
            }
            case "expr ::= expr in_op nm dbnm paren_exprlist":
            {
                // "x IN table" or "x IN function(args)", a SELECT * FROM it
                Expr p = PExpr(TK.IN, E(0), null);
                AddSelect(p, new Select { Height = 1 });
                a.Value = I(1) != 0 ? PExpr(TK.NOT, p, null) : p;
                break;
            }
            case "expr ::= EXISTS LP select RP":
            {
                Expr p = PExpr(TK.EXISTS, null, null);
                AddSelect(p, S(2));
                a.Value = p;
                break;
            }
            case "expr ::= CASE case_operand case_exprlist case_else END":
            {
                Expr p = PExpr(TK.CASE, OE(1), null);
                p.List = OE(3) != null ? ListAppend(L(2), OE(3)) : L(2);
                SetHeightAndFlags(p);
                a.Value = p;
                break;
            }
            case "case_exprlist ::= case_exprlist WHEN expr THEN expr":
                ListAppend(L(0), E(2));
                ListAppend(L(0), E(4));
                break;
            case "case_exprlist ::= WHEN expr THEN expr":
            {
                ExprList list = ListAppend(null, E(1));
                ListAppend(list, E(3));
                a.Value = list;
                break;
            }
            case "case_else ::= ELSE expr":
                a.Value = _stack[lhs + 1].Value;
                break;
            case "nexprlist ::= nexprlist COMMA expr":
                ListAppend(L(0), E(2));
                break;
            case "nexprlist ::= expr":
                a.Value = ListAppend(null, E(0));
                break;
            case "paren_exprlist ::= LP exprlist RP":
            case "eidlist_opt ::= LP eidlist RP":
                a.Value = _stack[lhs + 1].Value;
                break;
            case "eidlist ::= eidlist COMMA nm collate sortorder":
                // parserAddExprIdListTerm: COLLATE and ASC or DESC are ignored in a legacy schema
                ListAppend(L(0), null)[L(0)!.Count - 1].Name = SqliteTokenizer.Dequote(Text(T(2)));
                break;
            case "eidlist ::= nm collate sortorder":
            {
                ExprList list = ListAppend(null, null);
                list[0].Name = SqliteTokenizer.Dequote(Text(T(0)));
                a.Value = list;
                break;
            }
            case "collate ::= COLLATE ID|STRING":
                a.Int = 1;
                break;
            case "expr ::= RAISE LP IGNORE RP":
                a.Value = PExpr(TK.RAISE, null, null);
                break;
            case "expr ::= RAISE LP raisetype COMMA expr RP":
                a.Value = PExpr(TK.RAISE, E(4), null);
                break;

            // ///////////////////// WITH and window definitions, for subqueries

            case "wqlist ::= wqitem":
                a.Value = new List<string> { SqliteTokenizer.Dequote(Text(T(0))) };
                break;
            case "wqlist ::= wqlist COMMA wqitem":
            {
                // sqlite3WithAdd in build.c
                List<string> names = (List<string>)a.Value!;
                string name = SqliteTokenizer.Dequote(Text(T(2)));
                if (names.Exists(n => AsciiCaseInsensitiveComparer.Instance.Equals(n, name)))
                    Fail("SCHEMA-22", $"duplicate WITH table name: {name}");
                names.Add(name);
                break;
            }
            case "windowdefn_list ::= windowdefn_list COMMA windowdefn":
            {
                Window window = W(2);
                WindowChain(window, W(0));
                window.Next = W(0);
                a.Value = window;
                break;
            }
            case "windowdefn ::= nm AS LP window RP":
            {
                Window window = W(3);
                window.Name = Text(T(0));
                a.Value = window;
                break;
            }
            case "window ::= PARTITION BY nexprlist orderby_opt frame_opt":
                a.Value = WindowAssemble(W(4), true, L(3) != null, null);
                break;
            case "window ::= nm PARTITION BY nexprlist orderby_opt frame_opt":
                a.Value = WindowAssemble(W(5), true, L(4) != null, T(0));
                break;
            case "window ::= ORDER BY sortlist frame_opt":
                a.Value = WindowAssemble(W(3), false, true, null);
                break;
            case "window ::= nm ORDER BY sortlist frame_opt":
                a.Value = WindowAssemble(W(4), false, true, T(0));
                break;
            case "window ::= nm frame_opt":
                a.Value = WindowAssemble(W(1), false, false, T(0));
                break;
            case "frame_opt ::=":
                a.Value = WindowAlloc(0, TK.UNBOUNDED, TK.CURRENT);
                break;
            case "frame_opt ::= range_or_rows frame_bound_s frame_exclude_opt":
                a.Value = WindowAlloc(I(0), I(1), TK.CURRENT);
                break;
            case "frame_opt ::= range_or_rows BETWEEN frame_bound_s AND frame_bound_e frame_exclude_opt":
                a.Value = WindowAlloc(I(0), I(2), I(4));
                break;
            case "range_or_rows ::= RANGE|ROWS|GROUPS":
                a.Int = M(0);
                break;
            case "frame_bound_s ::= UNBOUNDED PRECEDING":
            case "frame_bound_e ::= UNBOUNDED FOLLOWING":
                a.Int = TK.UNBOUNDED;
                break;
            case "frame_bound ::= expr PRECEDING|FOLLOWING":
                a.Int = M(1);
                break;
            case "frame_bound ::= CURRENT ROW":
                a.Int = TK.CURRENT;
                break;
            case "window_clause ::= WINDOW windowdefn_list":
            case "filter_over ::= filter_clause over_clause":
                a.Value = _stack[lhs + 1].Value;
                break;
            case "filter_over ::= filter_clause":
                a.Value = new Window { FilterOnly = true };
                break;
            case "over_clause ::= OVER LP window RP":
                a.Value = W(2);
                break;
            case "over_clause ::= OVER nm":
                a.Value = new Window { Name = Text(T(1)) };
                break;
            case "filter_clause ::= FILTER LP WHERE expr RP":
                a.Value = _stack[lhs + 3].Value;
                break;
        }
    }

    /// <summary>
    /// sqlite3SelectNew in select.c, for what the checks need: the number of result columns and the expressions'
    /// greatest height
    /// </summary>
    private static Select NewSelect(ExprList? columns, Expr? where, ExprList? groupBy, Expr? having, ExprList? orderBy, Expr? limit)
    {
        int height = System.Math.Max(where?.Height ?? 0, having?.Height ?? 0);
        height = System.Math.Max(height, limit?.Height ?? 0);
        height = System.Math.Max(height, ListHeight(columns));
        height = System.Math.Max(height, ListHeight(groupBy));
        height = System.Math.Max(height, ListHeight(orderBy));

        return new Select
        {
            ColumnCount = columns?.Count ?? 1,
            Height = height,
            HasOrderBy = orderBy != null,
            HasLimit = limit != null
        };
    }

    /// <summary>
    /// sqlite3MultiValues in insert.c, at init: a VALUES row after the first becomes a UNION ALL term
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/insert.c#L679 (sqlite3MultiValues)
    /// </summary>
    private static Select MultiValues(Select left, ExprList row)
    {
        Select select = NewSelect(row, null, null, null, null, null);
        select.IsValues = true;
        select.IsMultiValue = left.Prior == null || left.IsMultiValue;
        select.Op = TK.ALL;
        select.Prior = left;
        left.IsMultiValue = false;
        return select;
    }

    /// <summary>
    /// parserDoubleLinkSelect in parse.y: in a compound SELECT only the last term may have ORDER BY or LIMIT, and a
    /// compound has at most 500 terms, unless it is one VALUES clause
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L543 (parserDoubleLinkSelect)
    /// </summary>
    private void DoubleLinkSelect(Select p)
    {
        if (p.Prior == null)
            return;

        int count = 1;
        Select next = p;
        for (Select? loop = p.Prior; loop != null; loop = loop.Prior)
        {
            count++;
            if (loop.HasOrderBy || loop.HasLimit)
            {
                Fail("SCHEMA-22", $"{(loop.HasOrderBy ? "ORDER BY" : "LIMIT")} clause should come after {SelectOpName(next.Op)} not before");
                break;
            }

            next = loop;
        }

        if (!p.IsValues && !p.IsMultiValue && count > MaxCompoundSelect)
            Fail("SCHEMA-22", "too many terms in compound SELECT");
    }

    /// <summary>
    /// sqlite3SelectOpName in select.c
    /// </summary>
    private static string SelectOpName(int op) => op switch
    {
        TK.ALL => "UNION ALL",
        TK.INTERSECT => "INTERSECT",
        TK.EXCEPT => "EXCEPT",
        _ => "UNION"
    };

    /// <summary>
    /// sqlite3SrcListAppendFromTerm in build.c: ON and USING need a join before them
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L5068 (sqlite3SrcListAppendFromTerm)
    /// </summary>
    private SrcList AppendFromTerm(SrcList? list, int onUsing)
    {
        if (list == null && onUsing != 0)
            Fail("SCHEMA-22", $"a JOIN clause is required before {(onUsing == 1 ? "ON" : "USING")}");

        list ??= new SrcList();
        list.Count++;
        return list;
    }

    /// <summary>
    /// sqlite3JoinType in select.c: the words of a join, such as "LEFT OUTER" or "NATURAL CROSS"
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/select.c#L260 (sqlite3JoinType)
    /// </summary>
    private void JoinType(Token a, Token? b, Token? c)
    {
        // JT_* in sqliteInt.h
        const int JT_INNER = 0x01, JT_CROSS = 0x02, JT_NATURAL = 0x04, JT_LEFT = 0x08, JT_RIGHT = 0x10, JT_OUTER = 0x20, JT_ERROR = 0x80;
        (string Word, int Code)[] keywords =
        {
            ("natural", JT_NATURAL), ("left", JT_LEFT | JT_OUTER), ("outer", JT_OUTER), ("right", JT_RIGHT | JT_OUTER),
            ("full", JT_LEFT | JT_RIGHT | JT_OUTER), ("inner", JT_INNER), ("cross", JT_INNER | JT_CROSS),
        };

        int joinType = 0;
        foreach (Token? token in new[] { a, b, c })
        {
            if (token == null)
                break;

            string word = Text(token.Value);
            int code = 0;
            foreach ((string Word, int Code) keyword in keywords)
            {
                if (AsciiCaseInsensitiveComparer.Instance.Equals(word, keyword.Word))
                {
                    code = keyword.Code;
                    break;
                }
            }

            if (code == 0)
            {
                joinType |= JT_ERROR;
                break;
            }

            joinType |= code;
        }

        if ((joinType & (JT_INNER | JT_OUTER)) == (JT_INNER | JT_OUTER) || (joinType & JT_ERROR) != 0 ||
            (joinType & (JT_OUTER | JT_LEFT | JT_RIGHT)) == JT_OUTER)
        {
            string words = Text(a) + (b != null ? " " + Text(b.Value) : "") + (c != null ? " " + Text(c.Value) : "");
            Fail("SCHEMA-22", $"unknown join type: {words}");
        }
    }

    /// <summary>
    /// sqlite3WindowAssemble in window.c
    /// </summary>
    private Window WindowAssemble(Window window, bool partition, bool orderBy, Token? baseName)
    {
        window.HasPartition = partition;
        window.HasOrderBy = orderBy;
        if (baseName != null)
            window.Base = Text(baseName.Value);
        return window;
    }

    /// <summary>
    /// expr ::= VARIABLE in parse.y and sqlite3ExprAssignVarNumber in expr.c: "#N" is a register, only allowed in
    /// SQLite's own nested statements; "?N" must be from ?1 to ?32766; and there may be no more than 32766 parameters
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1317 (sqlite3ExprAssignVarNumber)
    /// </summary>
    private Expr Variable(Token t)
    {
        string z = Text(t);
        if (z[0] == '#' && z.Length > 1 && IsDigit(z[1]))
            Fail("DDL-22", $"near \"{z}\": syntax error");

        int x;
        if (z.Length == 1)
        {
            x = ++_variableCount;
        }
        else if (z[0] == '?')
        {
            if (!long.TryParse(z.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long i) ||
                i < 1 || i > MaxVariableNumber)
            {
                Fail("DDL-22", $"variable number must be between ?1 and ?{MaxVariableNumber}");
                i = 0;
            }

            x = (int)i;
            if (x > _variableCount)
                _variableCount = x;
        }
        else if (!_variableNames.TryGetValue(z, out x))
        {
            x = ++_variableCount;
            _variableNames[z] = x;
        }

        if (x > MaxVariableNumber)
            Fail("DDL-22", "too many SQL variables");

        return new Expr(TK.VARIABLE) { Text = z };
    }

    /// <summary>
    /// expr ::= expr in_op LP exprlist RP in parse.y: "x IN ()" is FALSE (NOT IN, TRUE) unless x has a function,
    /// "x IN (constant)" is "x = +constant", and "(a, b) IN ((1, 2), ...)" is a VALUES subquery
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L1491 (expr ::= expr in_op LP exprlist RP)
    /// </summary>
    private Expr InList(Expr left, bool not, ExprList? list)
    {
        if (list == null)
        {
            Expr constant = new Expr(TK.STRING) { Text = not ? "true" : "false" };
            IdToTrueFalse(constant);
            return !left.Has(Expr.EP_HasFunc) ? constant : PExpr(not ? TK.OR : TK.AND, constant, left);
        }

        Expr p;
        Expr rhs = list[0].Expr!;
        if (list.Count == 1 && IsConstant(rhs) && left.Op != TK.VECTOR)
        {
            p = PExpr(TK.EQ, left, PExpr(TK.UPLUS, rhs, null));
        }
        else if (list.Count == 1 && rhs.Op == TK.SELECT)
        {
            p = PExpr(TK.IN, left, null);
            AddSelect(p, rhs.Select!);
        }
        else
        {
            p = PExpr(TK.IN, left, null);
            if (left.Op == TK.VECTOR)
            {
                Select values = ExprListToValues(left.List!.Count, list);
                DoubleLinkSelect(values);
                AddSelect(p, values);
            }
            else
            {
                p.List = list;
                SetHeightAndFlags(p);
            }
        }

        return not ? PExpr(TK.NOT, p, null) : p;
    }

    /// <summary>
    /// sqlite3ExprListToValues in expr.c: each row of "(a, b) IN ((1, 2), (3, 4))" must have as many values as the
    /// left side
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1098 (sqlite3ExprListToValues)
    /// </summary>
    private Select ExprListToValues(int count, ExprList list)
    {
        Select? result = null;
        foreach (ExprListItem item in list)
        {
            Expr p = item.Expr!;
            int n = p.Op == TK.VECTOR ? p.List!.Count : 1;
            if (n != count)
                Fail("SCHEMA-22", $"IN(...) element has {n} term{(n > 1 ? "s" : "")} - expected {count}");

            Select row = NewSelect(p.List, null, null, null, null, null);
            row.IsValues = true;
            if (result != null)
            {
                row.Op = TK.ALL;
                row.Prior = result;
            }

            result = row;
        }

        if (result!.Prior != null)
            result.IsMultiValue = true;
        return result;
    }
}
