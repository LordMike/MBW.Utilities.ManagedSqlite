using System;
using System.Globalization;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers;

/// <summary>
/// Reads <c>sqlite_schema</c> rows the way SQLite's sqlite3InitCallback in prepare.c receives them: every value as a
/// C string, and the CREATE statement by its leading keywords.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L96 (sqlite3InitCallback)
/// </summary>
internal static class SchemaSql
{
    /// <summary>
    /// A schema value as the text SQLite's callback gets, or null for NULL. sqlite3_exec hands each value over through
    /// sqlite3_column_text: an integer as its decimal text, a real as "%!.*g" text, and a BLOB's bytes taken as text in
    /// the database's encoding. The callback reads a C string, so the text ends at the first NUL.
    ///   azVals[i] = (char *)sqlite3_column_text(pStmt, i);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/legacy.c#L90 (sqlite3_exec)
    /// </summary>
    public static string? ToText(ReaderBase reader, object? value)
    {
        string? text = value switch
        {
            null => null,
            string s => s,
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => FormatReal(d),
            byte[] b => reader.DecodeString(b, 0, b.Length),
            _ => value.ToString()
        };

        int nul = text?.IndexOf('\0') ?? -1;
        return nul >= 0 ? text!.Substring(0, nul) : text;
    }

    /// <summary>
    /// A real as SQLite's "%!.*g" prints it, with ".0" after a whole number, so it is never all digits. The digits and
    /// the exponent form are approximate; only a rootpage's text is checked, and a real is never a valid one.
    ///   sqlite3_str_appendf(&amp;acc, "%!.*g", (p->db ? p->db->nFpDigit : 17), p->u.r);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vdbemem.c#L129 (vdbeMemRenderNum)
    /// </summary>
    private static string FormatReal(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && !double.IsInfinity(value))
            text += ".0";
        return text;
    }

    /// <summary>
    /// SQLite's sqlite3GetUInt32 in util.c: decimal digits only, with no sign, space or '.', and at most 2^32, which
    /// wraps to 0.
    ///   for(i=0; sqlite3Isdigit(z[i]); i++){
    ///     v = v*10 + z[i] - '0';
    ///     if( v>4294967296LL ){ *pI = 0; return 0; }
    ///   }
    ///   if( i==0 || z[i]!=0 ){ *pI = 0; return 0; }
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L1538 (sqlite3GetUInt32)
    /// </summary>
    public static bool TryGetUInt32(string text, out uint value)
    {
        ulong v = 0;
        int i;
        for (i = 0; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++)
        {
            v = v * 10 + (ulong)(text[i] - '0');
            if (v > 4294967296UL)
            {
                value = 0;
                return false;
            }
        }

        if (i == 0 || i != text.Length)
        {
            value = 0;
            return false;
        }

        value = (uint)v;
        return true;
    }

    /// <summary>
    /// SQLite parses a schema row's SQL only when it starts with "cr", in either case, at its very first byte.
    ///   }else if( argv[4]
    ///          &amp;&amp; 'c'==sqlite3UpperToLower[(unsigned char)argv[4][0]]
    ///          &amp;&amp; 'r'==sqlite3UpperToLower[(unsigned char)argv[4][1]] ){
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L115 (sqlite3InitCallback)
    /// </summary>
    public static bool StartsWithCr(string? sql) =>
        sql != null && sql.Length >= 2 && (sql[0] | 0x20) == 'c' && (sql[1] | 0x20) == 'r';

    /// <summary>
    /// The kind of object the statement creates, from its leading tokens as SQLite's grammar reads them (cmd in
    /// parse.y): CREATE [TEMP | TEMPORARY] TABLE, CREATE VIRTUAL TABLE, CREATE [TEMP] VIEW, CREATE [UNIQUE] INDEX and
    /// CREATE [TEMP] TRIGGER. The tokens come from the port of SQLite's tokenizer, which skips whitespace and comments as
    /// sqlite3GetToken does. Anything else is a syntax error to SQLite, which the parser reports when the SQL is parsed.
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
        int afterTable = pos;
        if (!(NextToken(sql, ref pos) == TK.IF && NextToken(sql, ref pos) == TK.NOT && NextToken(sql, ref pos) == TK.EXISTS))
            pos = afterTable;

        // nm ::= id | STRING | JOIN_KW, where a keyword with a fallback to ID is an id too
        if (SqliteTokenizer.PeekNameToken(sql, ref pos) != TK.ID)
            return false;

        int afterName = pos;
        if (NextToken(sql, ref pos) == TK.DOT)
        {
            if (SqliteTokenizer.PeekNameToken(sql, ref pos) != TK.ID)
                return false;
        }
        else
            pos = afterName;

        return NextToken(sql, ref pos) == TK.AS;
    }

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
