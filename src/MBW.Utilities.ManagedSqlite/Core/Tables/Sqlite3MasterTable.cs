using System.Collections.Generic;
using System.Globalization;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

internal class Sqlite3MasterTable
{
    public List<Sqlite3SchemaRow> Tables { get; }

    public Sqlite3MasterTable(Sqlite3Database db, ReaderBase reader, Sqlite3Table table)
    {
        Tables = new List<Sqlite3SchemaRow>();

        IEnumerable<Sqlite3Row> rows = table.EnumerateRows();

        foreach (Sqlite3Row row in rows)
        {
            // Every value arrives as text, as in SQLite's sqlite3InitCallback, and a missing field is NULL
            string? Field(int index) => row.TryGetField(index, out object? value) ? ToText(reader, value) : null;

            string? type = Field(0);
            string? name = Field(1);
            string? tableName = Field(2);
            string? rootPageText = Field(3);
            string? sql = Field(4);

            uint rootPage = CheckRow(reader, type, name, tableName, rootPageText, sql, out SchemaObjectKind kind);

            // CheckRow fails a row without a name
            Tables.Add(new Sqlite3SchemaRow(db, type, name!, tableName, rootPage, sql, kind));
        }
    }

    /// <summary>
    /// The checks sqlite3InitCallback in prepare.c makes on each schema row before it parses its SQL. SQLite makes them
    /// while it loads the schema, so a bad row fails the database open, whatever its type. Returns the root page.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L96 (sqlite3InitCallback)
    /// </summary>
    private static uint CheckRow(ReaderBase reader, string? type, string? name, string? tableName, string? rootPageText, string? sql, out SchemaObjectKind kind)
    {
        // SCHEMA-11: a NULL rootpage is corrupt, for every kind of row:
        //   if( argv[3]==0 ){
        //     corruptSchema(pData, argv, 0);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L113 (sqlite3InitCallback)
        Checks.Strict(rootPageText != null, "SCHEMA-11", $"malformed database schema ({name ?? "?"}): its rootpage is NULL", 1, "sqlite_schema");

        if (!StartsWithCr(sql))
        {
            // SCHEMA-13: SQL that does not start with "cr" at its first byte is never parsed, and is corrupt unless it is
            // empty, so leading whitespace or a comment is corrupt too. A row with no SQL must have a name:
            //   }else if( argv[1]==0 || (argv[4]!=0 && argv[4][0]!=0) ){
            //     corruptSchema(pData, argv, 0);
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L164 (sqlite3InitCallback)
            Checks.Strict(name != null && string.IsNullOrEmpty(sql), "SCHEMA-13",
                $"malformed database schema ({name ?? "?"}): {(name == null ? "the row has no name" : "its SQL is not a CREATE statement")}", 1, "sqlite_schema");
            kind = SchemaObjectKind.None;

            // A row without a CREATE statement is an index that a PRIMARY KEY or UNIQUE constraint created. Its SQL is
            // NULL or empty, and its rootpage must be a page of the database other than page 1 (SCHEMA-15):
            //   if( sqlite3GetUInt32(argv[3],&pIndex->tnum)==0
            //    || pIndex->tnum<2
            //    || pIndex->tnum>pData->mxPage
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L178 (sqlite3InitCallback)
            // SQLite first looks the index up among those the tables' CREATE statements made ("orphan index"), and
            // checks that no sibling index shares its root page. That needs the SQL parsed, so the whole-schema
            // validation checks it, after this range check: an orphan row with a bad rootpage fails here as SCHEMA-15,
            // where SQLite reports it as an orphan index.
            bool rootOk = TryGetUInt32(rootPageText!, out uint indexRoot) && indexRoot >= 2 && indexRoot <= reader.PageCount;
            Checks.Strict(rootOk, "SCHEMA-15", $"malformed database schema ({name ?? "?"}): invalid rootpage {rootPageText}", 1, "sqlite_schema");
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
        bool ok = TryGetUInt32(rootPageText!, out uint rootPage) && (rootPage <= reader.PageCount || reader.PageCount == 0);
        Checks.Strict(ok, "SCHEMA-17", $"malformed database schema ({name ?? "?"}): invalid rootpage {rootPageText}", 1, "sqlite_schema");

        // SCHEMA-26: a CREATE row's name must match the name in its SQL, and SQLite's comparison never matches NULL
        // (sqlite3_stricmp). sqlite3CheckObjectName in build.c:
        //   if( sqlite3_stricmp(zType, db->init.azInit[0])
        //    || sqlite3_stricmp(zName, db->init.azInit[1])
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1046 (sqlite3CheckObjectName)
        Checks.Strict(name != null, "SCHEMA-26", $"malformed database schema (?): the row has no name", 1, "sqlite_schema");

        kind = SchemaSql.GetKind(sql);

        // SCHEMA-22: SQL that starts with "cr" but is not a CREATE statement does not parse
        Checks.Strict(kind != SchemaObjectKind.Unknown, "SCHEMA-22", $"malformed database schema ({name ?? "?"}): its SQL is not a CREATE statement",
            1, "sqlite_schema");

        // SCHEMA-26, DDL-03: the type must name the statement's kind, and a table's or view's tbl_name its own name, both
        // compared ignoring ASCII case. A table's name is compared with its statement when the SQL is parsed (DDL-03);
        // the names of views, indexes and triggers, and the parent table of an index or trigger, are left to the
        // whole-schema validation.
        // sqlite3StartTable passes its own name as zTblName. sqlite3CheckObjectName in build.c:
        //   if( sqlite3_stricmp(zType, db->init.azInit[0])
        //    || sqlite3_stricmp(zName, db->init.azInit[1])
        //    || sqlite3_stricmp(zTblName, db->init.azInit[2])
        //   ){
        //     sqlite3ErrorMsg(pParse, ""); /* corruptSchema() will supply the error */
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1046 (sqlite3CheckObjectName)
        // SQLite skips this with writable_schema on, and the rows read the same either way, so Lenient reports it and
        // goes by the statement: a table is found by its kind, not its type column.
        string expectedType = kind switch
        {
            SchemaObjectKind.Table or SchemaObjectKind.TableAsSelect or SchemaObjectKind.VirtualTable => "table",
            SchemaObjectKind.View => "view",
            SchemaObjectKind.Index => "index",
            _ => "trigger"
        };
        AsciiCaseInsensitiveComparer names = AsciiCaseInsensitiveComparer.Instance;
        Checks.Relaxable(names.Equals(type, expectedType), reader.Settings, "SCHEMA-26",
            $"malformed database schema ({name}): its type is {type ?? "NULL"}, but its SQL creates a {expectedType}", 1, "sqlite_schema");
        if (kind is SchemaObjectKind.Table or SchemaObjectKind.TableAsSelect or SchemaObjectKind.VirtualTable or SchemaObjectKind.View)
            Checks.Relaxable(names.Equals(tableName, name), reader.Settings, "SCHEMA-26",
                $"malformed database schema ({name}): its tbl_name is {tableName ?? "NULL"}, not its own name", 1, "sqlite_schema");

        // SCHEMA-19: a table, view or virtual table with rootpage 1 is taken for the schema table itself, named
        // sqlite_master, so its name no longer matches the row and the schema is malformed. (A row that is itself named
        // sqlite_master would pass the name check and then find that table already exists; it is treated as corrupt
        // here too.) sqlite3StartTable in build.c
        // (sqlite3VtabBeginParse and sqlite3CreateView call it too):
        //   if( db->init.busy && db->init.newTnum==1 ){
        //     /* Special case:  Parsing the sqlite_schema or sqlite_temp_schema schema */
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1222 (sqlite3StartTable)
        Checks.Strict(rootPage != 1 || kind is SchemaObjectKind.Index or SchemaObjectKind.Trigger, "SCHEMA-19",
            $"malformed database schema ({name ?? "?"}): rootpage 1 is the schema table", 1, "sqlite_schema");

        // DDL-58: CREATE TABLE ... AS SELECT is corrupt in the schema, and a view has no b-tree, so a non-zero rootpage is
        // corrupt. A virtual table may have one, and it is not read (SCHEMA-58). sqlite3EndTable in build.c:
        //   if( pSelect || (!IsOrdinaryTable(p) && db->init.newTnum) ){
        //     sqlite3ErrorMsg(pParse, "");
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L2669 (sqlite3EndTable)
        Checks.Strict(kind != SchemaObjectKind.TableAsSelect && (kind != SchemaObjectKind.View || rootPage == 0), "DDL-58",
            $"malformed database schema ({name ?? "?"}): {(kind == SchemaObjectKind.View ? $"view with rootpage {rootPage}" : "CREATE TABLE ... AS SELECT")}", 1, "sqlite_schema");

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
