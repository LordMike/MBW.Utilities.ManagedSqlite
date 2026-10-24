using System.Collections.Generic;
using System.Globalization;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

internal class Sqlite3MasterTable
{
    public List<Sqlite3SchemaRow> Tables { get; }

    public Sqlite3MasterTable(Sqlite3Database db, ReaderBase reader, Sqlite3Table table)
    {
        Tables = new List<Sqlite3SchemaRow>();
        SchemaCatalog catalog = new SchemaCatalog();

        IEnumerable<Sqlite3Row> rows = table.EnumerateRows();

        foreach (Sqlite3Row row in rows)
        {
            // Every value arrives as text, as in SQLite's sqlite3InitCallback, and a missing field is NULL
            string? Field(int index) => row.TryGetField(index, out object? value) ? ToText(reader, value) : null;

            string? type = Field(0);
            string? name = Field(1);
            // A table's tbl_name is its own name, so it shares the name's string when the two are stored alike
            string? tableName = row.FieldsEqual(2, 1) ? name : Field(2);
            // The rootpage is an integer, which is checked without making its text
            row.TryGetField(3, out object? rootPageValue);
            string? sql = Field(4);

            uint rootPage = CheckRow(reader, type, name, tableName, rootPageValue, sql);

            // CheckRow fails a row without a name
            Sqlite3SchemaRow schemaRow = new Sqlite3SchemaRow(db, type, name!, tableName, rootPage, sql);
            LoadRow(schemaRow, reader.Settings, catalog);
            Tables.Add(schemaRow);
        }
    }

    /// <summary>
    /// Loads a row into the schema as sqlite3InitCallback in prepare.c does, after the checks of the row by itself: a
    /// CREATE statement is parsed and checked against the rows before it, and a row without one is the index of a
    /// PRIMARY KEY or UNIQUE constraint, which a table before it must have made. SQLite fails the whole schema on the
    /// first error, and so does <see cref="SqliteValidation.Strict"/>. <see cref="SqliteValidation.Lenient"/> reports
    /// the error to <see cref="Sqlite3Settings.OnValidationIssue"/> and keeps it on the row, so only that row's table
    /// cannot be opened, and loads on.
    ///   rc = db->errCode;
    ///   ...
    ///   if( SQLITE_OK!=rc ){
    ///     ...
    ///       corruptSchema(pData, argv, sqlite3_errmsg(db));
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L150 (sqlite3InitCallback)
    /// </summary>
    private static void LoadRow(Sqlite3SchemaRow row, Sqlite3Settings settings, SchemaCatalog catalog)
    {
        try
        {
            if (StartsWithCr(row.Sql))
                SchemaParser.ParseSchemaRow(row, settings, catalog);
            else
                SchemaParser.AddIndexRow(row, catalog);
        }
        catch (SqliteCorruptException e) when (settings.Validation == SqliteValidation.Lenient)
        {
            row.Error = e;
            row.Kind = Sqlite3SchemaKind.None;
            // Every error here comes from a check (Checks.Fail, Strict or Relaxable), so it has an ID
            settings.OnValidationIssue?.Invoke(new SqliteValidationIssue(e.Check!, e.Detail, e.Page, e.Structure));
        }
    }

    /// <summary>
    /// The checks sqlite3InitCallback in prepare.c makes on each schema row before it parses its SQL. A bad row fails
    /// the database open, whatever its type, in either mode. Returns the root page.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L96 (sqlite3InitCallback)
    /// </summary>
    private static uint CheckRow(ReaderBase reader, string? type, string? name, string? tableName, object? rootPageValue, string? sql)
    {
        // SCHEMA-11: a NULL rootpage is corrupt, for every kind of row:
        //   if( argv[3]==0 ){
        //     corruptSchema(pData, argv, 0);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L113 (sqlite3InitCallback)
        Checks.Strict(rootPageValue != null, "SCHEMA-11", $"malformed database schema ({name ?? "?"}): its rootpage is NULL", 1, "sqlite_schema");

        if (!StartsWithCr(sql))
        {
            // SCHEMA-13: SQL that does not start with "cr" at its first byte is never parsed, and is corrupt unless it is
            // empty, so leading whitespace or a comment is corrupt too. A row with no SQL must have a name:
            //   }else if( argv[1]==0 || (argv[4]!=0 && argv[4][0]!=0) ){
            //     corruptSchema(pData, argv, 0);
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L164 (sqlite3InitCallback)
            Checks.Strict(name != null && string.IsNullOrEmpty(sql), "SCHEMA-13",
                $"malformed database schema ({name ?? "?"}): {(name == null ? "the row has no name" : "its SQL is not a CREATE statement")}", 1, "sqlite_schema");

            // A row without a CREATE statement is an index that a PRIMARY KEY or UNIQUE constraint created. Its SQL is
            // NULL or empty, and its rootpage must be a page of the database other than page 1 (SCHEMA-15):
            //   if( sqlite3GetUInt32(argv[3],&pIndex->tnum)==0
            //    || pIndex->tnum<2
            //    || pIndex->tnum>pData->mxPage
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L178 (sqlite3InitCallback)
            // SQLite first looks the index up among those the tables' CREATE statements made ("orphan index"), and
            // checks that no sibling index shares its root page. LoadRow checks that after this range check: an orphan
            // row with a bad rootpage fails here as SCHEMA-15, where SQLite reports it as an orphan index.
            bool rootOk = TryGetUInt32(reader, rootPageValue!, out uint indexRoot) && indexRoot >= 2 && indexRoot <= reader.PageCount;
            Checks.Strict(rootOk, "SCHEMA-15", $"malformed database schema ({name ?? "?"}): invalid rootpage {ToText(reader, rootPageValue)}", 1, "sqlite_schema");
            return indexRoot;
        }

        // SCHEMA-17: the rootpage of a CREATE row must be decimal digits, at most 2^32 (which wraps to 0), and no larger
        // than the database:
        //   if( sqlite3GetUInt32(argv[3], &db->init.newTnum)==0
        //    || (db->init.newTnum>pData->mxPage && pData->mxPage>0)
        //   ){
        //     if( sqlite3Config.bExtraSchemaChecks ){
        //       corruptSchema(pData, argv, "invalid rootpage");
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L135 (sqlite3InitCallback)
        bool ok = TryGetUInt32(reader, rootPageValue!, out uint rootPage) && (rootPage <= reader.PageCount || reader.PageCount == 0);
        Checks.Strict(ok, "SCHEMA-17", $"malformed database schema ({name ?? "?"}): invalid rootpage {ToText(reader, rootPageValue)}", 1, "sqlite_schema");

        // SCHEMA-26: a CREATE row's name must match the name in its SQL, and SQLite's comparison never matches NULL
        // (sqlite3_stricmp). sqlite3CheckObjectName in build.c:
        //   if( sqlite3_stricmp(zType, db->init.azInit[0])
        //    || sqlite3_stricmp(zName, db->init.azInit[1])
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1046 (sqlite3CheckObjectName)
        Checks.Strict(name != null, "SCHEMA-26", $"malformed database schema (?): the row has no name", 1, "sqlite_schema");

        return rootPage;
    }

    /// <summary>
    /// A schema value as the text SQLite's callback gets, or null for NULL. sqlite3_exec hands each value over through
    /// sqlite3_column_text: an integer as its decimal text, a real as "%!.*g" text, and a BLOB's bytes taken as text in
    /// the database's encoding. The callback reads a C string, so the text ends at the first NUL.
    ///   azVals[i] = (char *)sqlite3_column_text(pStmt, i);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/legacy.c#L90 (sqlite3_exec)
    /// </summary>
    private static string? ToText(ReaderBase reader, object? value)
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
    /// <see cref="TryGetUInt32(string, out uint)"/> of the text of <paramref name="value"/>, as <see cref="ToText"/>
    /// makes it. An integer, which is how SQLite stores a rootpage, is checked without making its text: its decimal
    /// digits pass when it is 0 to 2^32, and a negative one fails at its '-'.
    /// </summary>
    private static bool TryGetUInt32(ReaderBase reader, object value, out uint result)
    {
        if (value is long integer)
        {
            bool ok = integer is >= 0 and <= 4294967296L;
            result = ok ? unchecked((uint)integer) : 0;
            return ok;
        }

        return TryGetUInt32(ToText(reader, value)!, out result);
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
    private static bool TryGetUInt32(string text, out uint value)
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
    private static bool StartsWithCr(string? sql) =>
        sql != null && sql.Length >= 2 && (sql[0] | 0x20) == 'c' && (sql[1] | 0x20) == 'r';
}
