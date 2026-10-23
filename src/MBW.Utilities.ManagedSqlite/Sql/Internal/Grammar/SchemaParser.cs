using System;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// Parses the SQL of a sqlite_schema row the way SQLite does while it loads the schema (db->init.busy): SQLite's
/// tokenizer, its LALR(1) parser tables from parse.c, and the actions of parse.y and build.c. A CREATE TABLE gives a
/// <see cref="ParsedTable"/>. Any error is the error SQLite reports, which makes the schema corrupt
/// (SqliteCorruptException); in <see cref="SqliteValidation.Lenient"/> the checks that leave the table's data intact
/// are reported instead and parsing goes on.
/// </summary>
internal sealed partial class SchemaParser
{
    /// <summary>
    /// A parser stack entry, yyStackEntry in parse.c. A symbol's value is its token, an object (an Expr, ExprList,
    /// Select or Window) or an int, as its %type in parse.y says.
    /// </summary>
    private struct StackEntry
    {
        public int State;
        public int Major;
        public Token Token;
        public object? Value;
        public int Int;
    }

    /// <summary>
    /// SQLITE_MAX_PARSER_DEPTH, the default for SQLITE_LIMIT_PARSER_DEPTH: the largest the parser stack may grow
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/sqliteLimit.h#L117
    /// </summary>
    private const int MaxStackDepth = 2500;

    private readonly string _sql;
    private readonly string _objectName;
    private readonly Sqlite3Settings _settings;

    /// <summary>
    /// The parser stack, taken from <see cref="_spareStack"/> for the length of <see cref="Run"/>
    /// </summary>
    private StackEntry[] _stack = null!;

    /// <summary>
    /// The stack of the last parse on this thread, cleared, so parsing a schema's statements one after the other does
    /// not allocate a stack for each. A parse inside a parse finds none and makes its own.
    /// </summary>
    [ThreadStatic]
    private static StackEntry[]? _spareStack;
    private int _top;

    /// <summary>
    /// The statement has been parsed: sqlite3FinishCoding set SQLITE_DONE, which ends the parse at init
    /// </summary>
    private bool _done;

    /// <summary>
    /// pParse->pNewTable: the table the CREATE TABLE builds
    /// </summary>
    private ParsedTable? _newTable;

    /// <summary>
    /// pParse->sLastToken: the token being handed to the parser, which a reduce can read as the lookahead
    /// </summary>
    private Token _lookahead;

    private int _lookaheadType;

    private SchemaParser(string sql, string objectName, Sqlite3Settings settings)
    {
        // The SQL ends at a NUL character, as SQLite's C string does (DDL-12)
        int end = sql.IndexOf('\0');
        _sql = end >= 0 ? sql.Substring(0, end) : sql;
        _objectName = objectName;
        _settings = settings;
    }

    /// <summary>
    /// Parses the CREATE TABLE statement of the schema row <paramref name="objectName"/>
    /// </summary>
    /// <exception cref="SqliteCorruptException">SQLite would find the statement malformed</exception>
    public static ParsedTable ParseCreateTable(string sql, string objectName, Sqlite3Settings settings)
    {
        SchemaParser parser = new SchemaParser(sql, objectName, settings);
        parser.Run();

        // The row's SQL is a CREATE TABLE (its kind was read from it when the schema was loaded), and the parse ran
        // to the end without an error, so the table was made. An unusual statement can still end without one, such
        // as a CREATE TABLE ... AS SELECT, which sqlite3EndTable refuses at init, or a statement that is not a
        // CREATE TABLE at all.
        if (parser._newTable == null || !parser._tableEnded)
            parser.Fail("SCHEMA-22", "the SQL does not create a table");

        return parser._newTable!;
    }

    private string Text(Token token) => _sql.Substring(token.Start, token.Length);

    /// <summary>
    /// An error SQLite reports with sqlite3ErrorMsg while it loads the schema: the schema is corrupt
    /// (corruptSchema in prepare.c, "malformed database schema (name) - message")
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L22 (corruptSchema)
    /// </summary>
    private void Fail(string check, string message)
    {
        Checks.Fail(check, FormatError(message), structure: "table definition");
    }

    /// <summary>
    /// corruptSchema's message: an empty error message, as sqlite3EndTable leaves for a CREATE TABLE ... AS SELECT,
    /// adds nothing
    /// </summary>
    private string FormatError(string message) =>
        message.Length == 0 ? $"malformed database schema ({_objectName})" : $"malformed database schema ({_objectName}) - {message}";

    /// <summary>
    /// An error SQLite reports while it loads the schema, where the table's data is intact. Strict throws as
    /// <see cref="Fail"/>; Lenient reports it and returns, and the caller goes on as if the cause were not there.
    /// </summary>
    private void FailRelaxable(string check, string message)
    {
        Checks.Relaxable(false, _settings, check, $"{FormatError(message)}", structure: "table definition");
    }

    /// <summary>
    /// sqlite3RunParser in tokenize.c: hands the tokens to the parser until the statement is done or an error stops
    /// it. Whitespace and comments are skipped; at the end of the text the parser gets a SEMI, then the end marker.
    /// At init the parse ends with the first statement (sqlite3FinishCoding sets SQLITE_DONE), so text after it is
    /// never tokenized. The text ends at a NUL character, as SQLite's C string does.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/tokenize.c#L600 (sqlite3RunParser)
    /// </summary>
    private void Run()
    {
        _stack = _spareStack ?? new StackEntry[50];
        _spareStack = null;
        try
        {
            RunParser();
        }
        finally
        {
            // Drop the values the stack refers to, so the spare keeps nothing alive
            Array.Clear(_stack, 0, _stack.Length);
            _spareStack = _stack;
        }
    }

    private void RunParser()
    {
        string z = _sql;
        int pos = 0;
        int lastTokenParsed = -1;
        while (true)
        {
            int n = SqliteTokenizer.GetToken(z, pos, out int tokenType);
            if (tokenType >= TK.WINDOW)
            {
                if (tokenType == TK.SPACE)
                {
                    pos += n;
                    continue;
                }

                if (pos >= z.Length)
                {
                    // Upon reaching the end of input, call the parser two more times with tokens TK_SEMI and 0, in
                    // that order
                    if (lastTokenParsed == TK.SEMI)
                        tokenType = 0;
                    else if (lastTokenParsed == 0)
                        break;
                    else
                        tokenType = TK.SEMI;
                    n = 0;
                }
                else if (tokenType == TK.WINDOW)
                {
                    tokenType = AnalyzeWindowKeyword(z, pos + n);
                }
                else if (tokenType == TK.OVER)
                {
                    tokenType = AnalyzeOverKeyword(z, pos + n, lastTokenParsed);
                }
                else if (tokenType == TK.FILTER)
                {
                    tokenType = AnalyzeFilterKeyword(z, pos + n, lastTokenParsed);
                }
                else if (tokenType == TK.COMMENT)
                {
                    // Comments are always ignored while the schema is read
                    pos += n;
                    continue;
                }
                else if (tokenType != TK.QNUMBER)
                {
                    Fail(UnrecognizedTokenCheck(z, pos), $"unrecognized token: \"{z.Substring(pos, n)}\"");
                }
            }

            _lookahead = new Token(pos, n);
            _lookaheadType = tokenType;
            Parse(tokenType, _lookahead);
            lastTokenParsed = tokenType;
            pos += n;
            if (_done)
                break;
        }
    }

    /// <summary>
    /// The survey ID of an unrecognized token: a malformed number (DDL-20), a malformed blob literal (DDL-21), or any
    /// other character SQLite does not know (DDL-24)
    /// </summary>
    private static string UnrecognizedTokenCheck(string z, int pos)
    {
        char c = z[pos];
        if ((c >= '0' && c <= '9') || c == '.')
            return "DDL-20";
        if ((c == 'x' || c == 'X') && pos + 1 < z.Length && z[pos + 1] == '\'')
            return "DDL-21";
        return "DDL-24";
    }

    /// <summary>
    /// WINDOW is a keyword only when a name and AS follow it
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/tokenize.c#L246 (analyzeWindowKeyword)
    /// </summary>
    private static int AnalyzeWindowKeyword(string z, int pos)
    {
        if (SqliteTokenizer.PeekNameToken(z, ref pos) != TK.ID)
            return TK.ID;
        if (SqliteTokenizer.PeekNameToken(z, ref pos) != TK.AS)
            return TK.ID;
        return TK.WINDOW;
    }

    /// <summary>
    /// OVER is a keyword only after a ")" and before a "(" or a name
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/tokenize.c#L254 (analyzeOverKeyword)
    /// </summary>
    private static int AnalyzeOverKeyword(string z, int pos, int lastToken)
    {
        if (lastToken == TK.RP)
        {
            int t = SqliteTokenizer.PeekNameToken(z, ref pos);
            if (t == TK.LP || t == TK.ID)
                return TK.OVER;
        }

        return TK.ID;
    }

    /// <summary>
    /// FILTER is a keyword only after a ")" and before a "("
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/tokenize.c#L261 (analyzeFilterKeyword)
    /// </summary>
    private static int AnalyzeFilterKeyword(string z, int pos, int lastToken)
    {
        if (lastToken == TK.RP && SqliteTokenizer.PeekNameToken(z, ref pos) == TK.LP)
            return TK.FILTER;
        return TK.ID;
    }

    /// <summary>
    /// sqlite3Parser in parse.c, built without error recovery (YYNOERRORRECOVERY): takes one token, reducing as the
    /// tables say until it is shifted, and fails on a syntax error
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L45 (%syntax_error)
    /// </summary>
    private void Parse(int major, Token minor)
    {
        int act = _stack[_top].State;
        while (true)
        {
            act = FindShiftAction(major, act);
            if (act >= SqliteGrammar.YY_MIN_REDUCE)
            {
                int rule = act - SqliteGrammar.YY_MIN_REDUCE;
                // An empty rule pushes its left-hand side, so the stack must have room for one more entry
                if (SqliteGrammar.RuleRhsCount[rule] == 0 && _top >= MaxStackDepth - 1)
                    Fail("SCHEMA-22", "Recursion limit");
                // The statement's end (cmdx, which sets _done) is reduced on any lookahead, and the lookahead must still
                // be shifted: only ";" may follow, so "CREATE TABLE t(a) STRICT WITHOUT ROWID" is a syntax error
                act = ReduceRule(rule);
            }
            else if (act <= SqliteGrammar.YY_MAX_SHIFTREDUCE)
            {
                Shift(act, major, minor);
                return;
            }
            else if (act == SqliteGrammar.YY_ACCEPT_ACTION)
            {
                _top--;
                return;
            }
            else
            {
                if (minor.Length > 0 && minor.Start < _sql.Length)
                    Fail("SCHEMA-22", $"near \"{Text(minor)}\": syntax error");
                else
                    Fail("SCHEMA-22", "incomplete input");
                return;
            }
        }
    }

    /// <summary>
    /// yy_find_shift_action: the action for a terminal, falling back from a keyword to ID (%fallback) and to the
    /// wildcard ANY where the state allows
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L272 (%fallback ID)
    /// </summary>
    private static int FindShiftAction(int lookAhead, int state)
    {
        if (state > SqliteGrammar.YY_MAX_SHIFT)
            return state;

        while (true)
        {
            int i = SqliteGrammar.ShiftOffset[state] + lookAhead;
            if (SqliteGrammar.Lookahead[i] != lookAhead)
            {
                int fallback = lookAhead < SqliteGrammar.Fallback.Length ? SqliteGrammar.Fallback[lookAhead] : 0;
                if (fallback != 0)
                {
                    lookAhead = fallback;
                    continue;
                }

                int j = i - lookAhead + SqliteGrammar.YYWILDCARD;
                if (SqliteGrammar.Lookahead[j] == SqliteGrammar.YYWILDCARD && lookAhead > 0)
                    return SqliteGrammar.Action[j];

                return SqliteGrammar.Default[state];
            }

            return SqliteGrammar.Action[i];
        }
    }

    /// <summary>
    /// yy_find_reduce_action: the state to go to after reducing to a nonterminal
    /// </summary>
    private static int FindReduceAction(int state, int lookAhead)
    {
        int i = SqliteGrammar.ReduceOffset[state] + lookAhead;
        return SqliteGrammar.Action[i];
    }

    /// <summary>
    /// yy_shift: pushes a token
    /// </summary>
    private void Shift(int newState, int major, Token minor)
    {
        if (_top + 1 >= MaxStackDepth)
            Fail("SCHEMA-22", "Recursion limit");

        _top++;
        EnsureCapacity(_top);
        if (newState > SqliteGrammar.YY_MAX_SHIFT)
            newState += SqliteGrammar.YY_MIN_REDUCE - SqliteGrammar.YY_MIN_SHIFTREDUCE;

        _stack[_top] = new StackEntry { State = newState, Major = major, Token = minor };
    }

    private void EnsureCapacity(int index)
    {
        if (index < _stack.Length)
            return;

        StackEntry[] grown = new StackEntry[Math.Min(_stack.Length * 2 + 100, MaxStackDepth)];
        Array.Copy(_stack, grown, _stack.Length);
        _stack = grown;
    }

    /// <summary>
    /// yy_reduce: runs a rule's action and replaces its right-hand side on the stack with its left-hand side. The
    /// left-hand side takes the stack entry of the first symbol on the right, so a rule like "nm(A) ::= STRING(A)"
    /// passes that symbol's value on without an action.
    /// </summary>
    private int ReduceRule(int rule)
    {
        int count = -SqliteGrammar.RuleRhsCount[rule];
        int lhs = _top - count + 1;
        EnsureCapacity(lhs);
        if (count == 0)
            _stack[lhs] = default;

        Reduce(rule, lhs);

        int goTo = SqliteGrammar.RuleLhs[rule];
        int act = FindReduceAction(_stack[lhs - 1].State, goTo);
        _top = lhs;
        _stack[lhs].State = act;
        _stack[lhs].Major = goTo;
        return act;
    }
}
