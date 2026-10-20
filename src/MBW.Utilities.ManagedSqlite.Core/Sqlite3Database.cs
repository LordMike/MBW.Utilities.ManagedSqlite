using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Core;

public sealed class Sqlite3Database : IDisposable
{
    private readonly Sqlite3Settings _settings;
    private readonly ReaderBase _reader;
    private Dictionary<string, object> _propertyStore;

    private uint _sizeInPages;

    internal DatabaseHeader Header { get; private set; }
    private Sqlite3MasterTable _masterTable;
    private Sqlite3SchemaRow _masterSchemaRow;

    public SqliteEncoding TextEncoding => _reader.TextEncoding;

    public Sqlite3Database(Stream file, Sqlite3Settings settings = null)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        _settings = settings ?? new Sqlite3Settings();
        _reader = new ReaderBase(file);
        _propertyStore = new Dictionary<string, object>();

        Initialize();
        InitializeMasterTable();
    }

    private void Initialize()
    {
        Header = DatabaseHeader.Parse(_reader);

        // Database Size in pages adjustment
        // https://www.sqlite.org/fileformat.html#in_header_database_size

        uint expectedPages = (uint)(_reader.Length / Header.PageSize);

        // TODO: Warn on mismatch
        _sizeInPages = Math.Max(expectedPages, Header.DatabaseSizeInPages);

        _reader.ApplySqliteDatabaseHeader(Header, _settings);
    }

    private void InitializeMasterTable()
    {
        // Parse table on Page 1, the sqlite_master table
        BTreePage rootBtree = BTreePage.Parse(_reader, 1);

        // Fake the schema for the sqlite_master table
        Sqlite3SchemaRow schemaRow = new Sqlite3SchemaRow
        {
            Database = this,
            Type = "table",
            Name = "sqlite_master",
            TableName = "sqlite_master",
            RootPage = rootBtree.Page,
            Sql = "CREATE TABLE sqlite_master (type TEXT, name TEXT, tbl_name TEXT, rootpage INTEGER, sql TEXT);"
        };

        _masterSchemaRow = schemaRow;

        Sqlite3Table table = new Sqlite3Table(_reader, rootBtree, schemaRow);
        _masterTable = new Sqlite3MasterTable(this, table);
    }

    /// <summary>
    /// Finds a table by name. Like SQLite, the name is matched case-insensitively against <c>sqlite_master.name</c>,
    /// and <c>sqlite_master</c> / <c>sqlite_schema</c> resolve to the schema table itself.
    /// Virtual tables cannot be read and are not returned; see <see cref="Sqlite3SchemaRow.IsVirtual"/>.
    /// </summary>
    public bool TryGetTable(string name, out Sqlite3Table table)
    {
        Sqlite3SchemaRow tbl = FindTable(name);

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
        Sqlite3SchemaRow tbl = FindTable(name);

        if (tbl == null)
            throw new KeyNotFoundException("Unable to find table named " + name);

        if (tbl.IsVirtual)
            throw new NotSupportedException($"Table {tbl.Name} is a virtual table ({tbl.Sql}). Virtual tables have no data in the database file and cannot be read, though their shadow tables may be.");

        return OpenTable(tbl);
    }

    private Sqlite3SchemaRow FindTable(string name)
    {
        if (string.Equals(name, "sqlite_master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "sqlite_schema", StringComparison.OrdinalIgnoreCase))
            return _masterSchemaRow;

        foreach (Sqlite3SchemaRow tbl in GetTables())
        {
            if (tbl.Type == "table" && string.Equals(tbl.Name, name, StringComparison.OrdinalIgnoreCase))
                return tbl;
        }

        return null;
    }

    private Sqlite3Table OpenTable(Sqlite3SchemaRow tbl)
    {
        BTreePage root = BTreePage.Parse(_reader, tbl.RootPage);
        return new Sqlite3Table(_reader, root, tbl);
    }

    public IEnumerable<Sqlite3SchemaRow> GetTables() => _masterTable.Tables;

    internal T GetProperty<T>(string key, Func<T> creator) where T : class
    {
        if (!_propertyStore.TryGetValue(key, out var val))
            _propertyStore[key] = val = creator();

        return (T)val;
    }

    public void Dispose()
    {
        _reader?.Dispose();
    }
}