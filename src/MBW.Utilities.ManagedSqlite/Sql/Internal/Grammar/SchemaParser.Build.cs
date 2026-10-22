using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The CREATE TABLE steps of build.c, as SQLite runs them while it loads the schema (db->init.busy)
/// </summary>
internal sealed partial class SchemaParser
{
    // OE_* in sqliteInt.h, for the conflict clauses
    private const int OE_None = 0, OE_Rollback = 1, OE_Abort = 2, OE_Fail = 3, OE_Ignore = 4, OE_Replace = 5,
        OE_Restrict = 7, OE_SetNull = 8, OE_SetDflt = 9, OE_Cascade = 10, OE_Default = 11;

    /// <summary>
    /// sqlite3StdType, sqlite3StdTypeLen and sqlite3StdTypeAffinity in global.c: the types that set Column.eCType
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/global.c#L385 (sqlite3StdTypeLen)
    /// </summary>
    private static readonly (string Name, char Affinity)[] StandardTypes =
    {
        ("ANY", ParsedColumn.AFF_NUMERIC),
        ("BLOB", ParsedColumn.AFF_BLOB),
        ("INT", ParsedColumn.AFF_INTEGER),
        ("INTEGER", ParsedColumn.AFF_INTEGER),
        ("REAL", ParsedColumn.AFF_REAL),
        ("TEXT", ParsedColumn.AFF_TEXT),
    };

    /// <summary>
    /// sqlite3EndTable finished the table
    /// </summary>
    private bool _tableEnded;

    /// <summary>
    /// pParse->pNewTable, once a CREATE TABLE has started one. Every step below does nothing without one, as in
    /// SQLite: a statement that is not a CREATE TABLE, such as an ALTER TABLE, or a CREATE TABLE IF NOT EXISTS of a
    /// name that exists.
    /// </summary>
    private ParsedTable Table => _newTable!;

    private ParsedColumn LastColumn => Table.Columns[Table.Columns.Count - 1];

    /// <summary>
    /// sqlite3StartTable and sqlite3TwoPartName in build.c, at init: a schema-qualified name such as "main.t" is
    /// "corrupt database"
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L976 (sqlite3TwoPartName)
    /// </summary>
    private void StartTable(Token name, Token database, bool ifNotExists)
    {
        //   if( pName2->n>0 ){
        //     if( db->init.busy ) {
        //       sqlite3ErrorMsg(pParse, "corrupt database");
        if (database.Length > 0)
            Fail("SCHEMA-28", "corrupt database");

        // The name is checked against the row (sqlite3CheckObjectName, DDL-03) and against the tables and indexes
        // loaded before it, when the schema is loaded
        string tableName = SqliteTokenizer.Dequote(Text(name));
        if (CheckNewTableName(name, "table", tableName, ifNotExists))
            _newTable = new ParsedTable(tableName);
    }

    /// <summary>
    /// sqlite3AddColumn in build.c: a column with its name and declared type
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1490 (sqlite3AddColumn)
    /// </summary>
    private void AddColumn(Token name, Token type)
    {
        if (_newTable == null)
            return;

        //   if( p->nCol+1>db->aLimit[SQLITE_LIMIT_COLUMN] ){
        //     sqlite3ErrorMsg(pParse, "too many columns on %s", p->zName);
        // Lenient adds the column anyway: the limit is the reader's, not the file's
        if (Table.Columns.Count + 1 > MaxColumns)
            FailRelaxable("DDL-36", $"too many columns on {Table.Name}");

        // The GENERATED and ALWAYS keywords fall back to names, so "x INT GENERATED ALWAYS AS (1)" can leave them at
        // the end of the type; they are dropped from a type of 16 or more characters
        int typeStart = type.Start;
        int typeLength = type.Length;
        if (typeLength >= 16 && AsciiCaseInsensitiveComparer.Instance.Equals(_sql.Substring(typeStart + typeLength - 6, 6), "always"))
        {
            typeLength -= 6;
            while (typeLength > 0 && IsSpace(_sql[typeStart + typeLength - 1]))
                typeLength--;
            if (typeLength >= 9 && AsciiCaseInsensitiveComparer.Instance.Equals(_sql.Substring(typeStart + typeLength - 9, 9), "generated"))
            {
                typeLength -= 9;
                while (typeLength > 0 && IsSpace(_sql[typeStart + typeLength - 1]))
                    typeLength--;
            }
        }

        // A standard type name, quoted or not, sets eCType instead of keeping the text
        int standardType = ParsedColumn.COLTYPE_CUSTOM;
        char affinity = ParsedColumn.AFF_BLOB;
        if (typeLength >= 3)
        {
            DequoteToken(ref typeStart, ref typeLength);
            for (int i = 0; i < StandardTypes.Length; i++)
            {
                if (typeLength == StandardTypes[i].Name.Length &&
                    AsciiCaseInsensitiveComparer.Instance.Equals(_sql.Substring(typeStart, typeLength), StandardTypes[i].Name))
                {
                    typeLength = 0;
                    standardType = i + 1;
                    affinity = StandardTypes[i].Affinity;
                    break;
                }
            }
        }

        string columnName = SqliteTokenizer.Dequote(Text(name));

        //   if( p->nCol && sqlite3ColumnIndex(p, z)>=0 ){
        //     sqlite3ErrorMsg(pParse, "duplicate column name: %s", z);
        if (Table.ColumnIndex(columnName) >= 0)
            Fail("DDL-37", $"duplicate column name: {columnName}");

        ParsedColumn column = new ParsedColumn(columnName) { StandardType = standardType };
        if (typeLength == 0)
        {
            column.Affinity = affinity;
        }
        else
        {
            column.Type = SqliteTokenizer.Dequote(_sql.Substring(typeStart, typeLength));
            column.Affinity = AffinityType(column.Type);
            column.Flags |= ParsedColumn.COLFLAG_HASTYPE;
        }

        Table.Columns.Add(column);
        Table.StoredColumnCount++;
    }

    /// <summary>
    /// sqlite3Isspace in sqliteInt.h: space, \t, \n, \v, \f and \r
    /// </summary>
    private static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');

    /// <summary>
    /// sqlite3DequoteToken in util.c: drops the quotes around a token that has no other quote character in it
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L376 (sqlite3DequoteToken)
    /// </summary>
    private void DequoteToken(ref int start, ref int length)
    {
        if (length < 2 || !SqliteTokenizer.IsQuote(_sql[start]))
            return;
        for (int i = 1; i < length - 1; i++)
        {
            if (SqliteTokenizer.IsQuote(_sql[start + i]))
                return;
        }

        start++;
        length -= 2;
    }

    /// <summary>
    /// sqlite3AffinityType in build.c: the affinity of a declared type, from the first of INT, CHAR/CLOB/TEXT, BLOB,
    /// REAL/FLOA/DOUB found in it, ignoring ASCII case, or NUMERIC
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1651 (sqlite3AffinityType)
    /// </summary>
    internal static char AffinityType(string type)
    {
        uint h = 0;
        char aff = ParsedColumn.AFF_NUMERIC;
        for (int i = 0; i < type.Length; i++)
        {
            char ch = type[i];
            // SQLite hashes the UTF-8 bytes; a byte of a non-ASCII character never matches a letter, so one stand-in
            // byte per character finds the same substrings
            uint x = ch > 0x7F ? 0x80u : ch is >= 'A' and <= 'Z' ? (uint)(ch | 0x20) : ch;
            h = (h << 8) + x;
            if (h == Hash("char") || h == Hash("clob") || h == Hash("text"))
            {
                aff = ParsedColumn.AFF_TEXT;
            }
            else if (h == Hash("blob") && (aff == ParsedColumn.AFF_NUMERIC || aff == ParsedColumn.AFF_REAL))
            {
                aff = ParsedColumn.AFF_BLOB;
            }
            else if ((h == Hash("real") || h == Hash("floa") || h == Hash("doub")) && aff == ParsedColumn.AFF_NUMERIC)
            {
                aff = ParsedColumn.AFF_REAL;
            }
            else if ((h & 0x00FFFFFF) == Hash("int"))
            {
                aff = ParsedColumn.AFF_INTEGER;
                break;
            }
        }

        return aff;
    }

    private static uint Hash(string s)
    {
        uint h = 0;
        for (int i = 0; i < s.Length; i++)
            h = (h << 8) + s[i];
        return h;
    }

    /// <summary>
    /// sqlite3AddNotNull in build.c
    /// </summary>
    private void AddNotNull(int onError)
    {
        if (_newTable == null)
            return;

        LastColumn.NotNull = onError;
    }

    /// <summary>
    /// sqlite3AddDefaultValue in build.c: the DEFAULT must be constant, though at init any function counts as
    /// constant, and a generated column may not have one
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1729 (sqlite3AddDefaultValue)
    /// </summary>
    private void AddDefaultValue(Expr expr, int start, int end)
    {
        if (_newTable == null)
            return;

        ParsedColumn column = LastColumn;

        //     if( !sqlite3ExprIsConstantOrFunction(pExpr, isInit) ){
        //       sqlite3ErrorMsg(pParse, "default value of column [%s] is not constant",
        // Lenient leaves the column without the default, as SQLite does when it reports this
        if (!IsConstantOrFunction(expr))
        {
            FailRelaxable("DDL-42", $"default value of column [{column.Name}] is not constant");
            return;
        }

        //     }else if( pCol->colFlags & COLFLAG_GENERATED ){
        //       sqlite3ErrorMsg(pParse, "cannot use DEFAULT on a generated column");
        if (column.Has(ParsedColumn.COLFLAG_GENERATED))
        {
            FailRelaxable("DDL-43", "cannot use DEFAULT on a generated column");
            return;
        }

        column.Default = expr;
        column.DefaultText = _sql.Substring(start, end - start);
        column.HasDefault = true;
    }

    /// <summary>
    /// sqlite3StringToId in build.c: the legacy string literal column name in a PRIMARY KEY or UNIQUE, as in
    /// PRIMARY KEY('a'), is a name
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1788 (sqlite3StringToId)
    /// </summary>
    private static void StringToId(Expr p)
    {
        if (p.Op == TK.STRING)
            p.Op = TK.ID;
        else if (p.Op == TK.COLLATE && p.Left!.Op == TK.STRING)
            p.Left.Op = TK.ID;
    }

    /// <summary>
    /// makeColumnPartOfPrimaryKey in build.c
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1799 (makeColumnPartOfPrimaryKey)
    /// </summary>
    private void MakeColumnPartOfPrimaryKey(ParsedColumn column)
    {
        column.Flags |= ParsedColumn.COLFLAG_PRIMKEY;
        if (column.Has(ParsedColumn.COLFLAG_GENERATED))
            Fail("DDL-54", "generated columns cannot be part of the PRIMARY KEY");
    }

    /// <summary>
    /// sqlite3AddPrimaryKey in build.c: one INTEGER column that is not DESC becomes the rowid (iPKey); any other key
    /// gets a unique index
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1829 (sqlite3AddPrimaryKey)
    /// </summary>
    private void AddPrimaryKey(ExprList? list, int onError, int autoIncrement, int sortOrder)
    {
        if (_newTable == null)
            return;

        //   if( pTab->tabFlags & TF_HasPrimaryKey ){
        //     sqlite3ErrorMsg(pParse,
        //       "table \"%s\" has more than one primary key", pTab->zName);
        if (Table.Has(ParsedTable.TF_HasPrimaryKey))
            Fail("DDL-48", $"table \"{Table.Name}\" has more than one primary key");
        Table.Flags |= ParsedTable.TF_HasPrimaryKey;

        ParsedColumn? column = null;
        int columnIndex = -1;
        int termCount;
        if (list == null)
        {
            columnIndex = Table.Columns.Count - 1;
            column = Table.Columns[columnIndex];
            MakeColumnPartOfPrimaryKey(column);
            termCount = 1;
        }
        else
        {
            termCount = list.Count;
            foreach (ExprListItem item in list)
            {
                Expr term = SkipCollate(item.Expr!);
                StringToId(term);
                if (term.Op == TK.ID)
                {
                    int found = Table.ColumnIndex(term.Text!);
                    if (found >= 0)
                    {
                        columnIndex = found;
                        column = Table.Columns[found];
                        MakeColumnPartOfPrimaryKey(column);
                    }
                }
            }
        }

        if (termCount == 1 && column != null && column.StandardType == ParsedColumn.COLTYPE_INTEGER && sortOrder != SO_DESC)
        {
            Table.PrimaryKeyColumn = columnIndex;
            Table.KeyConflict = onError;
            if (autoIncrement != 0)
                Table.Flags |= ParsedTable.TF_Autoincrement;
            if (list != null)
                Table.PrimaryKeySortOrder = list[0].SortOrder;
            HasExplicitNulls(list);
            return;
        }

        //   }else if( autoInc ){
        //     sqlite3ErrorMsg(pParse, "AUTOINCREMENT is only allowed on an "
        //        "INTEGER PRIMARY KEY");
        // Lenient makes the key as if AUTOINCREMENT were not there, which only changes how new rowids are chosen
        if (autoIncrement != 0)
            FailRelaxable("DDL-53", "AUTOINCREMENT is only allowed on an INTEGER PRIMARY KEY");

        CreateIndex(list, onError, sortOrder, true);
    }

    /// <summary>
    /// sqlite3CreateIndex in build.c, for the index a PRIMARY KEY or UNIQUE constraint implies: each term must be a
    /// column of the table, and an index with the same columns and collations as an earlier one is not made again
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L3941 (sqlite3CreateIndex)
    /// </summary>
    private void CreateIndex(ExprList? list, int onError, int sortOrder, bool primaryKey)
    {
        // Lenient reads on with the index: NULLS only orders NULLs
        HasExplicitNulls(list);

        if (_newTable == null)
            return;

        if (list == null)
        {
            ParsedColumn last = LastColumn;
            last.Flags |= ParsedColumn.COLFLAG_UNIQUE;
            list = ListAppend(null, new Expr(TK.ID) { Text = last.Name });
            SetSortOrder(list, sortOrder, SO_UNDEFINED);
        }
        else if (list.Count > MaxColumns)
        {
            // sqlite3ExprListCheckLength; Lenient makes the index anyway
            FailRelaxable("DDL-36", "too many columns in index");
        }

        ParsedIndex index = new ParsedIndex { OnError = onError, IsPrimaryKey = primaryKey };
        foreach (ExprListItem item in list)
        {
            StringToId(item.Expr!);
            ResolveSelfReference(item.Expr!, NC_IdxExpr);

            //     if( pCExpr->op!=TK_COLUMN ){
            //       if( pTab==pParse->pNewTable ){
            //         sqlite3ErrorMsg(pParse, "expressions prohibited in PRIMARY KEY and "
            //                                 "UNIQUE constraints");
            Expr term = SkipCollate(item.Expr!);
            if (term.Op != TK.COLUMN)
                Fail("DDL-50", "expressions prohibited in PRIMARY KEY and UNIQUE constraints");

            int column = term.Column;
            string collation = item.Expr!.Op == TK.COLLATE ? item.Expr.Text! : Table.Columns[column].Collation ?? "BINARY";
            index.Columns.Add(column);
            index.Collations.Add(collation);
            index.SortOrders.Add(item.SortOrder);
        }

        // An earlier constraint that implies the same index (same columns in the same order, and the same collations)
        // takes this one's place
        foreach (ParsedIndex existing in Table.Indexes)
        {
            if (!SameKey(existing, index))
                continue;

            if (existing.OnError != index.OnError)
            {
                //           if( !(pIdx->onError==OE_Default || pIndex->onError==OE_Default) ){
                //             sqlite3ErrorMsg(pParse,
                //                 "conflicting ON CONFLICT clauses specified", 0);
                // Lenient keeps the earlier clause; ON CONFLICT only matters for writes
                if (existing.OnError != OE_Default && index.OnError != OE_Default)
                    FailRelaxable("DDL-55", "conflicting ON CONFLICT clauses specified");
                if (existing.OnError == OE_Default)
                    existing.OnError = index.OnError;
            }

            if (primaryKey)
                existing.IsPrimaryKey = true;
            return;
        }

        Table.Indexes.Add(index);
    }

    private static bool SameKey(ParsedIndex a, ParsedIndex b)
    {
        if (a.Columns.Count != b.Columns.Count)
            return false;

        for (int k = 0; k < a.Columns.Count; k++)
        {
            if (a.Columns[k] != b.Columns[k] || !AsciiCaseInsensitiveComparer.Instance.Equals(a.Collations[k], b.Collations[k]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// sqlite3AddCollateType in build.c: the column's collation, also for the single-column indexes already made on
    /// it. At init any collation name is accepted (sqlite3LocateCollSeq makes a placeholder).
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1938 (sqlite3AddCollateType)
    /// </summary>
    private void AddCollateType(Token name)
    {
        if (_newTable == null)
            return;

        int columnIndex = Table.Columns.Count - 1;
        string collation = SqliteTokenizer.Dequote(Text(name));
        Table.Columns[columnIndex].Collation = collation;

        foreach (ParsedIndex index in Table.Indexes)
        {
            if (index.Columns[0] == columnIndex)
                index.Collations[0] = collation;
        }
    }

    /// <summary>
    /// sqlite3AddGenerated in build.c: GENERATED ALWAYS AS (expr) VIRTUAL or STORED. A VIRTUAL column is not stored.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1971 (sqlite3AddGenerated)
    /// </summary>
    private void AddGenerated(Expr expr, Token? type)
    {
        if (_newTable == null)
            return;

        ParsedColumn column = LastColumn;

        //   if( pCol->iDflt>0 ) goto generated_error;
        //   ...
        // generated_error:
        //   sqlite3ErrorMsg(pParse, "error in generated column \"%s\"",
        // Lenient leaves the column with its DEFAULT and not generated, as SQLite does when it reports this
        if (column.HasDefault)
        {
            FailRelaxable("DDL-43", $"error in generated column \"{column.Name}\"");
            return;
        }

        int kind = ParsedColumn.COLFLAG_VIRTUAL;
        if (type is { } t)
        {
            string text = Text(t);
            if (AsciiCaseInsensitiveComparer.Instance.Equals(text, "stored"))
                kind = ParsedColumn.COLFLAG_STORED;
            else if (!AsciiCaseInsensitiveComparer.Instance.Equals(text, "virtual"))
                Fail("DDL-34", $"error in generated column \"{column.Name}\"");
        }

        if (kind == ParsedColumn.COLFLAG_VIRTUAL)
            Table.StoredColumnCount--;
        column.Flags |= kind;
        Table.Flags |= kind;

        if (column.Has(ParsedColumn.COLFLAG_PRIMKEY))
            MakeColumnPartOfPrimaryKey(column);

        // A plain column reference becomes +column, a real expression
        if (expr.Op == TK.ID)
            expr = PExpr(TK.UPLUS, expr, null);

        // sqlite3ColumnSetExpr: the expression takes the default's place (iDflt), so a DEFAULT after it is refused
        column.Default = expr;
        column.HasDefault = true;
    }

    /// <summary>
    /// sqlite3CreateForeignKey in build.c: the referencing columns must be columns of this table, and as many as the
    /// referenced ones. The referenced table is not looked at.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L3617 (sqlite3CreateForeignKey)
    /// </summary>
    private void CreateForeignKey(ExprList? fromColumns, Token to, ExprList? toColumns)
    {
        if (_newTable == null)
            return;

        // Lenient reads on: a foreign key only constrains writes
        if (fromColumns == null)
        {
            if (toColumns != null && toColumns.Count != 1)
                FailRelaxable("DDL-47", $"foreign key on {LastColumn.Name} should reference only one column of table {Text(to)}");
            return;
        }

        if (toColumns != null && toColumns.Count != fromColumns.Count)
        {
            FailRelaxable("DDL-47", "number of columns in foreign key does not match the number of columns in the referenced table");
            return;
        }

        foreach (ExprListItem item in fromColumns)
        {
            if (Table.ColumnIndex(item.Name!) < 0)
            {
                FailRelaxable("DDL-47", $"unknown column \"{item.Name}\" in foreign key definition");
                return;
            }
        }
    }

    /// <summary>
    /// sqlite3EndTable in build.c, at init
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L2637 (sqlite3EndTable)
    /// </summary>
    private void EndTable(bool asSelect, int options)
    {
        if (_newTable == null)
            return;

        // DDL-58: SQLite never stores a CREATE TABLE ... AS SELECT, and finds one corrupt in the schema:
        //   if( db->init.busy ){
        //     if( pSelect || (!IsOrdinaryTable(p) && db->init.newTnum) ){
        //       sqlite3ErrorMsg(pParse, "");
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L2669 (sqlite3EndTable)
        if (asSelect)
            Fail("DDL-58", "");

        // STRICT tables: every column needs one of the standard types, and ANY has BLOB affinity. Lenient reads the
        // column with the affinity of its declared type.
        if ((options & ParsedTable.TF_Strict) != 0)
        {
            Table.Flags |= ParsedTable.TF_Strict;
            foreach (ParsedColumn column in Table.Columns)
            {
                if (column.StandardType == ParsedColumn.COLTYPE_CUSTOM)
                {
                    if (column.Has(ParsedColumn.COLFLAG_HASTYPE))
                        FailRelaxable("DDL-59", $"unknown datatype for {Table.Name}.{column.Name}: \"{column.Type}\"");
                    else
                        FailRelaxable("DDL-59", $"missing datatype for {Table.Name}.{column.Name}");
                }
                else if (column.StandardType == ParsedColumn.COLTYPE_ANY)
                {
                    column.Affinity = ParsedColumn.AFF_BLOB;
                }
            }
        }

        if ((options & ParsedTable.TF_WithoutRowid) != 0)
        {
            //     if( (p->tabFlags & TF_Autoincrement) ){
            //       sqlite3ErrorMsg(pParse,
            //           "AUTOINCREMENT not allowed on WITHOUT ROWID tables");
            // Lenient ignores AUTOINCREMENT, which only changes how new rowids are chosen
            if (Table.Has(ParsedTable.TF_Autoincrement))
                FailRelaxable("DDL-60", "AUTOINCREMENT not allowed on WITHOUT ROWID tables");

            //     if( (p->tabFlags & TF_HasPrimaryKey)==0 ){
            //       sqlite3ErrorMsg(pParse, "PRIMARY KEY missing on table %s", p->zName);
            if (!Table.Has(ParsedTable.TF_HasPrimaryKey))
                Fail("DDL-61", $"PRIMARY KEY missing on table {Table.Name}");

            Table.Flags |= ParsedTable.TF_WithoutRowid;
            ConvertToWithoutRowidTable();
        }

        // CHECK constraints were dropped as they were parsed: SQLite keeps them only for a writable database
        // (sqlite3AddCheckConstraint), so it does not resolve them when it reads one
        if (Table.Has(ParsedTable.TF_HasGenerated))
        {
            int nonGenerated = 0;
            foreach (ParsedColumn column in Table.Columns)
            {
                // Lenient reads on: a generated column's expression is not computed here
                if (column.Has(ParsedColumn.COLFLAG_GENERATED))
                    ResolveSelfReference(column.Default!, NC_GenCol);
                else
                    nonGenerated++;
            }

            if (nonGenerated == 0)
                FailRelaxable("DDL-62", "must have at least one non-generated column");
        }

        _tableEnded = true;
        _catalog?.AddTable(Table, _rootPage);
    }

    /// <summary>
    /// convertToWithoutRowidTable in build.c: an INTEGER PRIMARY KEY becomes an ordinary key, repeated key columns
    /// (same column and collation) are dropped, and the record holds the key columns, then every other stored column
    /// in declared order
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L2354 (convertToWithoutRowidTable)
    /// </summary>
    private void ConvertToWithoutRowidTable()
    {
        ParsedIndex? pk;
        if (Table.PrimaryKeyColumn >= 0)
        {
            ExprList list = ListAppend(null, new Expr(TK.ID) { Text = Table.Columns[Table.PrimaryKeyColumn].Name });
            list[0].SortOrder = Table.PrimaryKeySortOrder;
            Table.PrimaryKeyColumn = -1;
            CreateIndex(list, Table.KeyConflict, 0, true);
            pk = Table.PrimaryKeyIndex;
        }
        else
        {
            pk = Table.PrimaryKeyIndex;
            if (pk != null)
            {
                // isDupColumn: a term is a repeat when an earlier one has the same column and collation
                for (int i = pk.Columns.Count - 1; i > 0; i--)
                {
                    for (int j = 0; j < i; j++)
                    {
                        if (pk.Columns[j] == pk.Columns[i] && AsciiCaseInsensitiveComparer.Instance.Equals(pk.Collations[j], pk.Collations[i]))
                        {
                            pk.Columns.RemoveAt(i);
                            pk.Collations.RemoveAt(i);
                            pk.SortOrders.RemoveAt(i);
                            break;
                        }
                    }
                }
            }
        }

        // The PRIMARY KEY index exists: TF_HasPrimaryKey is set and its terms were columns (or this failed above)
        if (pk == null)
        {
            Fail("DDL-61", $"PRIMARY KEY missing on table {Table.Name}");
            return;
        }

        Table.RecordColumns.AddRange(pk.Columns);
        for (int i = 0; i < Table.Columns.Count; i++)
        {
            // hasColumn: the column only, so a key column is not stored again
            if (!Table.RecordColumns.Contains(i) && !Table.Columns[i].Has(ParsedColumn.COLFLAG_VIRTUAL))
                Table.RecordColumns.Add(i);
        }
    }
}
