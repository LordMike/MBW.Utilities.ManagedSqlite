using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Core;

public class Sqlite3Database : IDisposable
{
    private readonly Sqlite3Settings _settings;
    private readonly ReaderBase _reader;
    private Dictionary<string, object> _propertyStore;

    private uint _sizeInPages;

    public DatabaseHeader Header { get; private set; }
    private Sqlite3MasterTable _masterTable;

    public SqliteEncoding TextEncoding => _reader.TextEncoding;

    public Sqlite3Database(Stream file, Sqlite3Settings settings = null)
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
        // A power of two between 512 and 32768, or the value 1 which means 65536
        uint pageSize = header.PageSize;
        bool validPageSize = pageSize == 1 || (pageSize >= 512 && pageSize <= 65536 && (pageSize & (pageSize - 1)) == 0);

        if (!validPageSize)
            throw new SqliteCorruptException($"invalid page size {pageSize}", structure: "database header");

        // https://www.sqlite.org/fileformat.html#reserved_bytes_per_page
        // The usable size of a page may not be less than 480
        uint usableSize = (pageSize == 1 ? 65536 : pageSize) - header.ReservedSpaceAtEndOfPage;
        if (usableSize < 480)
            throw new SqliteCorruptException($"usable page size {usableSize} is less than 480 (page size {pageSize}, reserved space {header.ReservedSpaceAtEndOfPage})", structure: "database header");
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

        Sqlite3Table table = new Sqlite3Table(_reader, rootBtree, schemaRow);
        _masterTable = new Sqlite3MasterTable(this, table);
    }

    public bool TryGetTable(string name, out Sqlite3Table table)
    {
        IEnumerable<Sqlite3SchemaRow> tables = GetTables();

        foreach (Sqlite3SchemaRow tbl in tables)
        {
            if (tbl.TableName != name || tbl.Type != "table")
                continue;

            // Found it
            BTreePage root = BTreePage.Parse(_reader, tbl.RootPage);

            table = new Sqlite3Table(_reader, root, tbl);
            return true;
        }

        table = null;
        return false;
    }

    public Sqlite3Table GetTable(string name)
    {
        if (!TryGetTable(name, out var tbl))
            throw new Exception("Unable to find table named " + name);

        return tbl;
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