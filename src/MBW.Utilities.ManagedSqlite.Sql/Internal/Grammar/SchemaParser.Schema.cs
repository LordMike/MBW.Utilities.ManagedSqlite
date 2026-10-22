using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The statements of the other schema rows, as SQLite loads them into the schema built from the rows before: CREATE
/// INDEX, VIEW, TRIGGER and VIRTUAL TABLE, and the names every row adds (build.c, trigger.c and vtab.c at init). Views
/// and triggers are checked as far as SQLite checks them while it loads the schema: their statements must parse, and
/// their names and tables must fit the schema; their SELECT and trigger steps are not resolved, as SQLite resolves
/// them only when they are used.
/// </summary>
internal sealed partial class SchemaParser
{
    /// <summary>
    /// The schema loaded so far, when the whole schema is checked; null when one table's SQL is parsed for its
    /// definition
    /// </summary>
    private SchemaCatalog? _catalog;

    /// <summary>
    /// db->init.newTnum: the row's rootpage
    /// </summary>
    private uint _rootPage;

    /// <summary>
    /// db->init.azInit[2]: the row's tbl_name
    /// </summary>
    private string? _rowTableName;

    /// <summary>
    /// The table a CREATE INDEX indexes, which its terms are resolved against; null for the new table's own
    /// </summary>
    private ParsedTable? _resolveTable;

    /// <summary>
    /// pParse->pNewTrigger: the trigger being built, once sqlite3BeginTrigger accepted its header
    /// </summary>
    private string? _newTrigger;

    /// <summary>
    /// The virtual table being built: sqlite3VtabBeginParse accepted its name. Its module arguments are counted
    /// (u.vtab.nArg, which starts with the module, database and table names) and the current one is pending
    /// (pParse->sArg.z).
    /// </summary>
    private string? _newVirtualTable;

    private int _virtualTableArguments;
    private bool _virtualTableArgumentPending;

    /// <summary>
    /// The first database a FROM term names that is not the main one, which a view or trigger may not reference
    /// </summary>
    private string? _foreignDatabase;

    /// <summary>
    /// sqlite3FixSelect and sqlite3FixTriggerStep in attach.c: the FROM terms of a view or trigger in the main database
    /// may only name the main database
    ///         if( iDb!=sqlite3FindDbName(db, pItem->u4.zDatabase) ){
    ///           sqlite3ErrorMsg(pFix->pParse,
    ///               "%s %T cannot reference objects in database %s",
    ///               pFix->zType, pFix->pName, pItem->u4.zDatabase);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/attach.c#L497 (fixSelectCb)
    /// Variables are made NULL at init (fixExprCb), so they are no error here.
    /// </summary>
    private void CheckReferences(string check, string type, string name)
    {
        if (_foreignDatabase != null)
            Fail(check, $"{type} {name} cannot reference objects in database {_foreignDatabase}");
    }

    /// <summary>
    /// Parses the CREATE statement of a schema row and adds what it makes to <paramref name="catalog"/>, checking it
    /// against the rows before it, as sqlite3InitCallback does for each row
    /// </summary>
    /// <exception cref="SqliteCorruptException">SQLite would find the schema malformed</exception>
    public static void ParseSchemaRow(Sqlite3SchemaRow row, Sqlite3Settings settings, SchemaCatalog catalog)
    {
        SchemaParser parser = new SchemaParser(row.Sql!, row.Name, settings)
        {
            _catalog = catalog,
            _rootPage = row.RootPage,
            _rowTableName = row.TableName
        };
        parser.Run();
    }

    /// <summary>
    /// The automatic index a row without SQL is the root page of: it must be one a CREATE TABLE before it made, and
    /// the only index on its table with that root page
    ///   pIndex = sqlite3FindIndex(db, argv[1], db->aDb[iDb].zDbSName);
    ///   if( pIndex==0 ){
    ///     corruptSchema(pData, argv, "orphan index");
    ///   }else
    ///   if( sqlite3GetUInt32(argv[3],&amp;pIndex->tnum)==0
    ///    || pIndex->tnum&lt;2
    ///    || pIndex->tnum>pData->mxPage
    ///    || sqlite3IndexHasDuplicateRootPage(pIndex)
    ///   ){
    ///       corruptSchema(pData, argv, "invalid rootpage");
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L174 (sqlite3InitCallback)
    /// The root page's text and range were checked when the database was opened (SCHEMA-15).
    /// </summary>
    public static void AddIndexRow(Sqlite3SchemaRow row, SchemaCatalog catalog)
    {
        if (!catalog.Indexes.TryGetValue(row.Name, out SchemaCatalog.CatalogIndex? index))
        {
            Checks.Fail("DDL-02", $"malformed database schema ({row.Name}) - orphan index", structure: "schema");
            return;
        }

        index.RootPage = row.RootPage;
        if (SchemaCatalog.HasDuplicateRootPage(index))
            Checks.Fail("SCHEMA-15", $"malformed database schema ({row.Name}) - invalid rootpage", structure: "schema");
    }

    /// <summary>
    /// sqlite3CheckObjectName in build.c, at init: the statement's object must be the row's, by name and by the
    /// table it belongs to, ignoring ASCII case. The error message is empty; corruptSchema adds the name.
    ///   if( sqlite3_stricmp(zType, db->init.azInit[0])
    ///    || sqlite3_stricmp(zName, db->init.azInit[1])
    ///    || sqlite3_stricmp(zTblName, db->init.azInit[2])
    ///   ){
    ///     sqlite3ErrorMsg(pParse, ""); /* corruptSchema() will supply the error */
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1046 (sqlite3CheckObjectName)
    /// The type was checked against the statement when the database was opened (SCHEMA-26).
    /// </summary>
    private void CheckObjectName(string name, string tableName)
    {
        if (_catalog == null)
            return;

        if (!Core.Helpers.AsciiCaseInsensitiveComparer.Instance.Equals(name, _objectName) ||
            _rowTableName == null || !Core.Helpers.AsciiCaseInsensitiveComparer.Instance.Equals(tableName, _rowTableName))
            FailRelaxable("DDL-03", "");
    }

    /// <summary>
    /// sqlite3StartTable in build.c: a new table, view or virtual table may not have the name of a table, view or index
    /// loaded before it. With IF NOT EXISTS a name that exists is no error, and the statement makes nothing. False when
    /// the statement makes nothing.
    ///     pTable = sqlite3FindTable(db, zName, zDb);
    ///     if( pTable ){
    ///       if( !noErr ){
    ///         sqlite3ErrorMsg(pParse, "%s %T already exists",
    ///                         (IsView(pTable)? "view" : "table"), pName);
    ///     ...
    ///     if( sqlite3FindIndex(db, zName, zDb)!=0 ){
    ///       sqlite3ErrorMsg(pParse, "there is already an index named %s", zName);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1282 (sqlite3StartTable)
    /// </summary>
    private bool CheckNewTableName(Token nameToken, string name, bool ifNotExists)
    {
        CheckObjectName(name, name);
        if (_catalog == null)
            return true;

        if (_catalog.Tables.TryGetValue(name, out SchemaCatalog.CatalogTable? existing))
        {
            if (!ifNotExists)
                Fail("DDL-08", $"{(existing.IsView ? "view" : "table")} {Text(nameToken)} already exists");
            return false;
        }

        if (_catalog.Indexes.ContainsKey(name))
            Fail("DDL-08", $"there is already an index named {name}");

        return true;
    }

    /// <summary>
    /// sqlite3CreateView in build.c, at init: a view may not have parameters, its name is a new table's, and it may
    /// only reference the main database. The SELECT is parsed, but its names are resolved only when the view is used.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L3009 (sqlite3CreateView)
    /// </summary>
    private void CreateView(Token name, Token database, bool ifNotExists)
    {
        //   if( pParse->nVar>0 ){
        //     sqlite3ErrorMsg(pParse, "parameters are not allowed in views");
        if (_variableCount > 0)
            Fail("DDL-74", "parameters are not allowed in views");

        if (database.Length > 0)
            Fail("SCHEMA-28", "corrupt database");

        string viewName = SqliteTokenizer.Dequote(Text(name));
        if (!CheckNewTableName(name, viewName, ifNotExists))
            return;

        //   if( sqlite3FixSelect(&sFix, pSelect) ) goto create_view_fail;
        CheckReferences("DDL-75", "view", Text(name));
        if (_catalog != null)
            _catalog.Tables[viewName] = new SchemaCatalog.CatalogTable(viewName, null, true, false);
    }

    /// <summary>
    /// sqlite3VtabBeginParse in vtab.c: a virtual table's name is a new table's, and its module arguments start with
    /// the module's, the database's and the table's names. The module is not looked up while the schema is loaded.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vtab.c#L387 (sqlite3VtabBeginParse)
    /// </summary>
    private void BeginVirtualTable(Token name, Token database, bool ifNotExists)
    {
        if (database.Length > 0)
            Fail("SCHEMA-28", "corrupt database");

        string tableName = SqliteTokenizer.Dequote(Text(name));
        if (!CheckNewTableName(name, tableName, ifNotExists))
            return;

        _newVirtualTable = tableName;
        _virtualTableArguments = 3;
    }

    /// <summary>
    /// sqlite3VtabArgInit and sqlite3VtabArgExtend in vtab.c: a module argument is the text of its tokens, and an
    /// empty one is not an argument
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vtab.c#L533 (sqlite3VtabArgInit)
    /// </summary>
    private void VirtualTableArgument(bool extend)
    {
        if (extend)
        {
            _virtualTableArgumentPending = true;
            return;
        }

        AddVirtualTableArgument();
    }

    /// <summary>
    /// addArgumentToVtab and addModuleArgument in vtab.c: there may be no more module arguments than the column limit
    /// allows
    ///   if( pTable->u.vtab.nArg+3>=db->aLimit[SQLITE_LIMIT_COLUMN] ){
    ///     sqlite3ErrorMsg(pParse, "too many columns on %s", pTable->zName);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vtab.c#L361 (addModuleArgument)
    /// </summary>
    private void AddVirtualTableArgument()
    {
        if (_virtualTableArgumentPending && _newVirtualTable != null)
        {
            if (_virtualTableArguments + 3 >= MaxColumns)
                FailRelaxable("DDL-36", $"too many columns on {_newVirtualTable}");
            _virtualTableArguments++;
        }

        _virtualTableArgumentPending = false;
    }

    /// <summary>
    /// sqlite3VtabFinishParse in vtab.c, at init: the last argument, then the table goes into the schema
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/vtab.c#L449 (sqlite3VtabFinishParse)
    /// </summary>
    private void FinishVirtualTable()
    {
        AddVirtualTableArgument();
        if (_newVirtualTable != null && _catalog != null)
            _catalog.Tables[_newVirtualTable] = new SchemaCatalog.CatalogTable(_newVirtualTable, null, false, true);
    }

    /// <summary>
    /// The table a CREATE INDEX or CREATE TRIGGER names, which a row before it must have made. Its name is looked up in
    /// the main database (sqlite3FixSrcList fixes the schema), so the error names it as main.X.
    /// </summary>
    private SchemaCatalog.CatalogTable? LocateTable(string name, string check)
    {
        if (_catalog != null && _catalog.Tables.TryGetValue(name, out SchemaCatalog.CatalogTable? table))
            return table;

        Fail(check, $"no such table: main.{name}");
        return null;
    }

    /// <summary>
    /// sqlite3CreateIndex in build.c, for a CREATE INDEX statement at init: the table must be an ordinary table loaded
    /// before it, the name must be new, the terms and the WHERE are resolved against the table (expressions are
    /// allowed here), and no other index on the table may have the same root page
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L3941 (sqlite3CreateIndex)
    /// </summary>
    private void CreateIndexStatement(Token name, Token database, Token table, ExprList list, Expr? where, bool ifNotExists)
    {
        HasExplicitNulls(list);

        //   iDb = sqlite3TwoPartName(pParse, pName1, pName2, &pName);
        if (database.Length > 0)
            Fail("SCHEMA-28", "corrupt database");

        //   pTab = sqlite3LocateTableItem(pParse, 0, &pTblName->a[0]);
        string tableName = SqliteTokenizer.Dequote(Text(table));
        SchemaCatalog.CatalogTable target = LocateTable(tableName, "DDL-65")!;

        //   if( IsView(pTab) ){
        //     sqlite3ErrorMsg(pParse, "views may not be indexed");
        //   ...
        //   if( IsVirtual(pTab) ){
        //     sqlite3ErrorMsg(pParse, "virtual tables may not be indexed");
        if (target.IsView)
            Fail("DDL-66", "views may not be indexed");
        if (target.IsVirtual)
            Fail("DDL-66", "virtual tables may not be indexed");

        string indexName = SqliteTokenizer.Dequote(Text(name));
        CheckObjectName(indexName, target.Name);

        //   if( sqlite3FindIndex(db, zName, pDb->zDbSName)!=0 ){
        //     if( !ifNotExist ){
        //       sqlite3ErrorMsg(pParse, "index %s already exists", zName);
        // A name that is a table's is allowed at init (DDL-09)
        if (_catalog!.Indexes.ContainsKey(indexName))
        {
            if (!ifNotExists)
                Fail("DDL-10", $"index {indexName} already exists");
            return;
        }

        //     sqlite3ExprListCheckLength(pParse, pList, "index");
        if (list.Count > MaxColumns)
            Fail("DDL-71", "too many columns in index");

        _resolveTable = target.Table;
        if (where != null)
            ResolveSelfReference(where, NC_PartIdx);

        foreach (ExprListItem item in list)
        {
            StringToId(item.Expr!);
            ResolveSelfReference(item.Expr!, NC_IdxExpr);
        }

        _resolveTable = null;

        //       pIndex->tnum = db->init.newTnum;
        //       if( sqlite3IndexHasDuplicateRootPage(pIndex) ){
        //         sqlite3ErrorMsg(pParse, "invalid rootpage");
        SchemaCatalog.CatalogIndex index = new SchemaCatalog.CatalogIndex(indexName, target, _rootPage);
        if (SchemaCatalog.HasDuplicateRootPage(index))
            Fail("DDL-72", "invalid rootpage");

        _catalog.AddIndex(index);
    }

    /// <summary>
    /// sqlite3BeginTrigger in trigger.c, at init: the trigger's table must be loaded before it and be no virtual or
    /// sqlite_ table, INSTEAD OF is only for a view and BEFORE and AFTER only for a table, and the name must be new. A
    /// database name on the table is dropped.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/trigger.c#L104 (sqlite3BeginTrigger)
    /// </summary>
    private void BeginTrigger(Token name, Token database, int time, Token table, bool ifNotExists)
    {
        //     iDb = sqlite3TwoPartName(pParse, pName1, pName2, &pName);
        if (database.Length > 0)
            Fail("SCHEMA-28", "corrupt database");

        string tableName = SqliteTokenizer.Dequote(Text(table));
        SchemaCatalog.CatalogTable target = LocateTable(tableName, "DDL-79")!;

        //   if( IsVirtual(pTab) ){
        //     sqlite3ErrorMsg(pParse, "cannot create triggers on virtual tables");
        if (target.IsVirtual)
            Fail("DDL-80", "cannot create triggers on virtual tables");

        string triggerName = SqliteTokenizer.Dequote(Text(name));
        CheckObjectName(triggerName, target.Name);

        //     if( sqlite3HashFind(&(db->aDb[iDb].pSchema->trigHash),zName) ){
        //       if( !noErr ){
        //         sqlite3ErrorMsg(pParse, "trigger %T already exists", pName);
        if (_catalog!.Triggers.Contains(triggerName))
        {
            if (!ifNotExists)
                Fail("DDL-10", $"trigger {Text(name)} already exists");
            return;
        }

        //   if( sqlite3StrNICmp(pTab->zName, "sqlite_", 7)==0 ){
        //     sqlite3ErrorMsg(pParse, "cannot create trigger on system table");
        if (target.Name.Length >= 7 && Core.Helpers.AsciiCaseInsensitiveComparer.Instance.Equals(target.Name.Substring(0, 7), "sqlite_"))
            Fail("DDL-80", "cannot create trigger on system table");

        //   if( IsView(pTab) && tr_tm!=TK_INSTEAD ){
        //     sqlite3ErrorMsg(pParse, "cannot create %s trigger on view: %S",
        //         (tr_tm == TK_BEFORE)?"BEFORE":"AFTER", pTableName->a);
        //   ...
        //   if( !IsView(pTab) && tr_tm==TK_INSTEAD ){
        //     sqlite3ErrorMsg(pParse, "cannot create INSTEAD OF"
        //         " trigger on table: %S", pTableName->a);
        if (target.IsView && time != TK.INSTEAD)
            Fail("DDL-81", $"cannot create {(time == TK.BEFORE ? "BEFORE" : "AFTER")} trigger on view: {tableName}");
        if (!target.IsView && time == TK.INSTEAD)
            Fail("DDL-81", $"cannot create INSTEAD OF trigger on table: {tableName}");

        _newTrigger = triggerName;
    }

    /// <summary>
    /// sqlite3FinishTrigger in trigger.c, at init: the steps and WHEN may only reference the main database, and the
    /// trigger goes into the schema. Its steps are not resolved.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/trigger.c#L323 (sqlite3FinishTrigger)
    /// </summary>
    private void FinishTrigger()
    {
        if (_newTrigger == null)
            return;

        //   if( sqlite3FixTriggerStep(&sFix, pTrig->step_list)
        //    || sqlite3FixExpr(&sFix, pTrig->pWhen)
        CheckReferences("DDL-82", "trigger", _newTrigger);
        _catalog?.Triggers.Add(_newTrigger);
    }

    /// <summary>
    /// The names and values of "SET (a, b) = ..." in an UPDATE or upsert must match in number
    /// </summary>
    private void AppendVector(int columns, Expr value)
    {
        if (value.Op != TK.SELECT && columns != VectorSize(value))
            Fail("DDL-82", $"{columns} columns assigned {VectorSize(value)} values");
    }

    /// <summary>
    /// triggerStepAllocate in trigger.c: an INSERT, UPDATE or DELETE step may not name the table's database
    ///     if( pNew
    ///      &amp;&amp; pNew->pSchema!=db->aDb[1].pSchema
    ///      &amp;&amp; pTabList->a[0].u4.zDatabase
    ///     ){
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/trigger.c#L467 (triggerStepAllocate)
    /// </summary>
    private void TriggerStep(bool qualified)
    {
        if (_newTrigger != null && qualified)
            Fail("DDL-82", "qualified table names are not allowed on INSERT, UPDATE, and DELETE statements within triggers");
    }
}
