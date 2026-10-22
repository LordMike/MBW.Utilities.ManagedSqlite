using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Core;

public sealed class Sqlite3Database : IDisposable
{
    private readonly ReaderBase _reader;
    private Dictionary<string, object>? _propertyStore;

    private readonly Sqlite3MasterTable _masterTable;
    private readonly Sqlite3SchemaRow _masterSchemaRow;

    internal DatabaseHeader Header => _reader.Header;

    public SqliteEncoding TextEncoding => _reader.TextEncoding;

    internal Sqlite3Settings Settings => _reader.Settings;

    public Sqlite3Database(Stream file, Sqlite3Settings? settings = null)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        try
        {
            _reader = ReaderBase.Open(file, settings ?? new Sqlite3Settings());
            _masterTable = OpenMasterTable(out _masterSchemaRow);
        }
        catch (Exception e) when (CorruptionHelper.IsCorruptionShaped(e))
        {
            throw new SqliteCorruptException("unable to read the database header or schema", innerException: e);
        }
    }

    private Sqlite3MasterTable OpenMasterTable(out Sqlite3SchemaRow schemaRow)
    {
        // Parse table on Page 1, the sqlite_master table
        BTreePage rootBtree = BTreePage.Parse(_reader, 1);

        // PAGE-18: the schema table is a rowid table, read with a table cursor, so page 1 must be a table b-tree page.
        // moveToRoot in btree.c:
        //   if( pRoot->isInit==0 || (pCur->pKeyInfo==0)!=pRoot->intKey ){
        //     return SQLITE_CORRUPT_PAGE(pCur->pPage);
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/btree.c#L5609 (moveToRoot)
        Checks.Strict(rootBtree is not BTreeIndexPage, "PAGE-18", $"page 1 is an index b-tree page, but holds the schema table",
            1, "b-tree page header");

        // Fake the schema for the sqlite_master table
        schemaRow = new Sqlite3SchemaRow(this, "table", "sqlite_master", "sqlite_master", rootBtree.Page,
            "CREATE TABLE sqlite_master (type TEXT, name TEXT, tbl_name TEXT, rootpage INTEGER, sql TEXT);", SchemaObjectKind.Table);

        Sqlite3Table table = new Sqlite3Table(_reader, rootBtree, schemaRow);
        return new Sqlite3MasterTable(this, _reader, table);
    }

    /// <summary>
    /// Finds a table by name. Like SQLite, the name is matched case-insensitively against <c>sqlite_master.name</c>,
    /// and <c>sqlite_master</c> / <c>sqlite_schema</c> resolve to the schema table itself.
    /// Virtual tables cannot be read and are not returned; see <see cref="Sqlite3SchemaRow.IsVirtual"/>.
    /// </summary>
    public bool TryGetTable(string name, [NotNullWhen(true)] out Sqlite3Table? table)
    {
        Sqlite3SchemaRow? tbl = FindTable(name);

        if (tbl == null || tbl.IsVirtual)
        {
            table = null;
            return false;
        }

        table = OpenTable(tbl);
        return true;
    }

    /// <summary>
    /// Gets a table by name, see <see cref="TryGetTable"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No table with that name exists</exception>
    /// <exception cref="NotSupportedException">The table is a virtual table</exception>
    public Sqlite3Table GetTable(string name)
    {
        Sqlite3SchemaRow? tbl = FindTable(name);

        if (tbl == null)
            throw new KeyNotFoundException("Unable to find table named " + name);

        if (tbl.IsVirtual)
            throw new NotSupportedException($"Table {tbl.Name} is a virtual table ({tbl.Sql}). Virtual tables have no data in the database file and cannot be read, though their shadow tables may be.");

        return OpenTable(tbl);
    }

    private Sqlite3SchemaRow? FindTable(string name)
    {
        // Names match as in SQLite, ignoring the case of ASCII letters only (sqlite3StrICmp in util.c), so tables named
        // "é" and "É" are two tables
        AsciiCaseInsensitiveComparer names = AsciiCaseInsensitiveComparer.Instance;

        if (names.Equals(name, "sqlite_master") || names.Equals(name, "sqlite_schema"))
            return _masterSchemaRow;

        foreach (Sqlite3SchemaRow tbl in GetTables())
        {
            // A table is known by its statement, as in SQLite. The type column must agree in Strict (SCHEMA-26), in
            // any case, so "TABLE" is a table too.
            if (tbl.Kind is SchemaObjectKind.Table or SchemaObjectKind.VirtualTable && names.Equals(tbl.Name, name))
                return tbl;
        }

        return null;
    }

    private Sqlite3Table OpenTable(Sqlite3SchemaRow tbl)
    {
        // Parsed to check it and learn its kind, in the reader's page buffer: the table reads it again when its rows are read
        BTreePage root = BTreePage.Parse(_reader, tbl.RootPage, buffer: _reader.PageBuffer);
        return new Sqlite3Table(_reader, root, tbl);
    }

    public IEnumerable<Sqlite3SchemaRow> GetTables() => _masterTable.Tables;

    /// <summary>
    /// <see cref="GetProperty{T}(string, Func{T})"/> with a creator that takes the database, so a static method or
    /// lambda serves and no closure is allocated on each call
    /// </summary>
    internal T GetProperty<T>(string key, Func<Sqlite3Database, T> creator) where T : class
    {
        _propertyStore ??= new Dictionary<string, object>();
        if (!_propertyStore.TryGetValue(key, out var val))
            _propertyStore[key] = val = creator(this);

        return (T)val;
    }

    internal T GetProperty<T>(string key, Func<T> creator) where T : class
    {
        _propertyStore ??= new Dictionary<string, object>();
        if (!_propertyStore.TryGetValue(key, out var val))
            _propertyStore[key] = val = creator();

        return (T)val;
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}