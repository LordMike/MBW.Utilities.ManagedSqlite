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
    private readonly Sqlite3Settings _settings;
    private readonly ReaderBase _reader;
    private Dictionary<string, object> _propertyStore;

    private uint _sizeInPages;

    internal DatabaseHeader Header { get; private set; }
    private Sqlite3MasterTable _masterTable;
    private Sqlite3SchemaRow _masterSchemaRow;

    public SqliteEncoding TextEncoding => _reader.TextEncoding;

    public Sqlite3Database(Stream file, Sqlite3Settings? settings = null)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        _settings = settings ?? new Sqlite3Settings();
        _reader = new ReaderBase(file);
        _propertyStore = new Dictionary<string, object>();

        try
        {
            Initialize();
            InitializeMasterTable();
        }
        catch (Exception e) when (CorruptionHelper.IsCorruptionShaped(e))
        {
            throw new SqliteCorruptException("unable to read the database header or schema", innerException: e);
        }
    }

    [MemberNotNull(nameof(Header))]
    private void Initialize()
    {
        Header = DatabaseHeader.Parse(_reader);

        ValidateHeader(Header);

        // Database Size in pages adjustment
        // https://www.sqlite.org/fileformat.html#in_header_database_size

        uint expectedPages = (uint)(_reader.Length / Header.PageSize);

        // TODO: Warn on mismatch
        _sizeInPages = Math.Max(expectedPages, Header.DatabaseSizeInPages);

        _reader.ApplySqliteDatabaseHeader(Header, _settings);
        _reader.PageCount = _sizeInPages;
    }

    private static void ValidateHeader(DatabaseHeader header)
    {
        // https://www.sqlite.org/fileformat.html#page_size
        // A power of two between 512 and 65536. DatabaseHeader.Parse has already mapped the stored value 1 to 65536.
        uint pageSize = header.PageSize;
        bool validPageSize = pageSize >= 512 && pageSize <= 65536 && (pageSize & (pageSize - 1)) == 0;

        if (!validPageSize)
            throw new SqliteCorruptException($"invalid page size {pageSize}", structure: "database header");

        // https://www.sqlite.org/fileformat.html#reserved_bytes_per_page
        // The usable size of a page may not be less than 480
        uint usableSize = pageSize - header.ReservedSpaceAtEndOfPage;
        if (usableSize < 480)
            throw new SqliteCorruptException($"usable page size {usableSize} is less than 480 (page size {pageSize}, reserved space {header.ReservedSpaceAtEndOfPage})", structure: "database header");
    }

    [MemberNotNull(nameof(_masterTable), nameof(_masterSchemaRow))]
    private void InitializeMasterTable()
    {
        // Parse table on Page 1, the sqlite_master table
        BTreePage rootBtree = BTreePage.Parse(_reader, 1);

        // Fake the schema for the sqlite_master table
        Sqlite3SchemaRow schemaRow = new Sqlite3SchemaRow(this, "table", "sqlite_master", "sqlite_master", rootBtree.Page,
            "CREATE TABLE sqlite_master (type TEXT, name TEXT, tbl_name TEXT, rootpage INTEGER, sql TEXT);");

        _masterSchemaRow = schemaRow;

        Sqlite3Table table = new Sqlite3Table(_reader, rootBtree, schemaRow);
        _masterTable = new Sqlite3MasterTable(this, table);
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