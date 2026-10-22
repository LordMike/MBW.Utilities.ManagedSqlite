using System;
using System.Globalization;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Tables;

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
    /// The kind of object the statement creates, from its leading keywords as SQLite's grammar reads them (cmd in
    /// parse.y): CREATE [TEMP | TEMPORARY] TABLE, CREATE VIRTUAL TABLE, CREATE [TEMP] VIEW, CREATE [UNIQUE] INDEX and
    /// CREATE [TEMP] TRIGGER. Whitespace and comments between the keywords are skipped, as sqlite3GetToken in tokenize.c
    /// skips them. Anything else is a syntax error to SQLite.
    /// </summary>
    public static SchemaObjectKind GetKind(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return SchemaObjectKind.None;

        int pos = 0;
        if (!IsWord(sql!, ref pos, "CREATE"))
            return SchemaObjectKind.Unknown;

        int afterCreate = pos;
        if (IsWord(sql!, ref pos, "VIRTUAL"))
            return IsWord(sql!, ref pos, "TABLE") ? SchemaObjectKind.VirtualTable : SchemaObjectKind.Unknown;

        pos = afterCreate;
        if (IsWord(sql!, ref pos, "UNIQUE"))
            return IsWord(sql!, ref pos, "INDEX") ? SchemaObjectKind.Index : SchemaObjectKind.Unknown;

        pos = afterCreate;
        if (IsWord(sql!, ref pos, "INDEX"))
            return SchemaObjectKind.Index;

        pos = afterCreate;
        if (!IsWord(sql!, ref pos, "TEMP") && !IsWord(sql!, ref pos, "TEMPORARY"))
            pos = afterCreate;

        int afterTemp = pos;
        if (IsWord(sql!, ref pos, "TABLE"))
            return IsCreateTableAsSelect(sql!, pos) ? SchemaObjectKind.TableAsSelect : SchemaObjectKind.Table;
        pos = afterTemp;
        if (IsWord(sql!, ref pos, "VIEW"))
            return SchemaObjectKind.View;
        pos = afterTemp;
        if (IsWord(sql!, ref pos, "TRIGGER"))
            return SchemaObjectKind.Trigger;

        return SchemaObjectKind.Unknown;
    }

    /// <summary>
    /// True when the CREATE TABLE at <paramref name="pos"/>, just after TABLE, is CREATE TABLE ... AS SELECT: after
    /// [IF NOT EXISTS] and the name, [schema.]name, comes AS (create_table_args ::= AS select in parse.y)
    /// </summary>
    private static bool IsCreateTableAsSelect(string sql, int pos)
    {
        int afterTable = pos;
        if (!(IsWord(sql, ref pos, "IF") && IsWord(sql, ref pos, "NOT") && IsWord(sql, ref pos, "EXISTS")))
            pos = afterTable;

        if (!SkipName(sql, ref pos))
            return false;

        int dot = SkipSpaceAndComments(sql, pos);
        if (dot < sql.Length && sql[dot] == '.')
        {
            pos = dot + 1;
            if (!SkipName(sql, ref pos))
                return false;
        }

        return IsWord(sql, ref pos, "AS");
    }

    /// <summary>
    /// Skips one name: an unquoted word, or one quoted with "", '', `` or [] (doubled quotes inside are escapes)
    /// </summary>
    private static bool SkipName(string sql, ref int pos)
    {
        int i = SkipSpaceAndComments(sql, pos);
        if (i >= sql.Length)
            return false;

        char c = sql[i];
        if (c == '"' || c == '\'' || c == '`' || c == '[')
        {
            char close = c == '[' ? ']' : c;
            for (i++; i < sql.Length; i++)
            {
                if (sql[i] != close)
                    continue;
                if (close != ']' && i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i++;
                    continue;
                }

                pos = i + 1;
                return true;
            }

            return false;
        }

        int start = i;
        while (i < sql.Length && IsIdChar(sql[i]))
            i++;
        if (i == start)
            return false;

        pos = i;
        return true;
    }

    /// <summary>
    /// Skips whitespace and comments, then reads one unquoted word and compares it with <paramref name="keyword"/>,
    /// ignoring ASCII case. Moves <paramref name="pos"/> past the word only when it matches.
    /// </summary>
    private static bool IsWord(string sql, ref int pos, string keyword)
    {
        int i = SkipSpaceAndComments(sql, pos);
        int start = i;
        while (i < sql.Length && IsIdChar(sql[i]))
            i++;

        if (i == start || (sql[start] >= '0' && sql[start] <= '9') || sql[start] == '$' ||
            !AsciiCaseInsensitiveComparer.Instance.Equals(sql.Substring(start, i - start), keyword))
            return false;

        pos = i;
        return true;
    }

    /// <summary>
    /// An identifier character (IdChar in tokenize.c): an ASCII letter or digit, '_', '$', or any character outside ASCII
    /// </summary>
    private static bool IsIdChar(char c) =>
        c > 0x7F || c == '_' || c == '$' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    /// <summary>
    /// Whitespace starts with a space, tab, line feed, form feed or carriage return and may go on with a vertical tab
    /// (CC_SPACE, then sqlite3Isspace); "--" runs to the end of the line and "/*" to "*/" or the end of the text.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/tokenize.c#L279 (sqlite3GetToken)
    /// </summary>
    private static int SkipSpaceAndComments(string sql, int i)
    {
        while (i < sql.Length)
        {
            char c = sql[i];
            // A byte order mark is whitespace too (CC_BOM, tokenize.c:575)
            if (c == ' ' || c == '\t' || c == '\n' || c == '\f' || c == '\r' || c == '\uFEFF')
            {
                i++;
                while (i < sql.Length && (sql[i] == ' ' || (sql[i] >= '\t' && sql[i] <= '\r')))
                    i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
            }
            else
                break;
        }

        return i;
    }
}
