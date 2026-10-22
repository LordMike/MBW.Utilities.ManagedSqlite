using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// The kind of a <c>sqlite_schema</c> row's CREATE statement, by its leading keywords
/// </summary>
internal static class SchemaSql
{
    /// <summary>
    /// The kind of object the statement creates, from its leading tokens as SQLite's grammar reads them (cmd in
    /// parse.y): CREATE [TEMP | TEMPORARY] TABLE, CREATE VIRTUAL TABLE, CREATE [TEMP] VIEW, CREATE [UNIQUE] INDEX and
    /// CREATE [TEMP] TRIGGER. The tokens come from the port of SQLite's tokenizer, which skips whitespace and comments as
    /// sqlite3GetToken does. Anything else is a syntax error to SQLite.
    /// </summary>
    public static SchemaObjectKind GetKind(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return SchemaObjectKind.None;

        int pos = 0;
        if (NextToken(sql!, ref pos) != TK.CREATE)
            return SchemaObjectKind.Unknown;

        int token = NextToken(sql!, ref pos);
        switch (token)
        {
            case TK.VIRTUAL:
                return NextToken(sql!, ref pos) == TK.TABLE ? SchemaObjectKind.VirtualTable : SchemaObjectKind.Unknown;
            case TK.UNIQUE:
                return NextToken(sql!, ref pos) == TK.INDEX ? SchemaObjectKind.Index : SchemaObjectKind.Unknown;
            case TK.INDEX:
                return SchemaObjectKind.Index;
            case TK.TEMP:
                // TEMP and TEMPORARY are both TK_TEMP
                token = NextToken(sql!, ref pos);
                break;
        }

        switch (token)
        {
            case TK.TABLE:
                return IsCreateTableAsSelect(sql!, pos) ? SchemaObjectKind.TableAsSelect : SchemaObjectKind.Table;
            case TK.VIEW:
                return SchemaObjectKind.View;
            case TK.TRIGGER:
                return SchemaObjectKind.Trigger;
            default:
                return SchemaObjectKind.Unknown;
        }
    }

    /// <summary>
    /// True when the CREATE TABLE at <paramref name="pos"/>, just after TABLE, is CREATE TABLE ... AS SELECT: after
    /// [IF NOT EXISTS] and the name, [schema.]name, comes AS (create_table_args ::= AS select in parse.y)
    /// </summary>
    private static bool IsCreateTableAsSelect(string sql, int pos)
    {
        // IF after TABLE always starts ifnotexists: Lemon falls back to ID only when IF cannot shift, so a table named if
        // is a syntax error
        int afterTable = pos;
        if (NextToken(sql, ref pos) == TK.IF)
        {
            if (NextToken(sql, ref pos) != TK.NOT || NextToken(sql, ref pos) != TK.EXISTS)
                return false;
        }
        else
            pos = afterTable;

        if (!IsName(sql, ref pos))
            return false;

        int afterName = pos;
        if (NextToken(sql, ref pos) == TK.DOT)
        {
            if (!IsName(sql, ref pos))
                return false;
        }
        else
            pos = afterName;

        return NextToken(sql, ref pos) == TK.AS;
    }

    /// <summary>
    /// Reads an nm (nm ::= idj | STRING, idj ::= ID | INDEXED | JOIN_KW in parse.y), where a keyword with a fallback to
    /// ID is an ID. PeekNameToken reads INDEXED and FILTER as themselves, as getToken in tokenize.c does; INDEXED is an
    /// idj, and FILTER is a keyword only after a ")" (analyzeFilterKeyword), so here both are names.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L334 (nm, idj)
    /// </summary>
    private static bool IsName(string sql, ref int pos) => SqliteTokenizer.PeekNameToken(sql, ref pos) is TK.ID or TK.INDEXED or TK.FILTER;

    /// <summary>
    /// The type of the next token that is not whitespace or a comment, moving <paramref name="pos"/> past it
    /// </summary>
    private static int NextToken(string sql, ref int pos)
    {
        int token;
        do
        {
            pos += SqliteTokenizer.GetToken(sql, pos, out token);
        } while (token is TK.SPACE or TK.COMMENT);

        return token;
    }
}
