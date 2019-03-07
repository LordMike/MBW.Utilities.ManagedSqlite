using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;
using MBW.Utilities.ManagedSqlite.Core.Tables;

namespace MBW.Utilities.ManagedSqlite.Core
{
    public class Sqlite3Database : IDisposable
    {
        private readonly Sqlite3Settings _settings;
        private readonly ReaderBase _reader;
        private Dictionary<string, object> _propertyStore;

        private uint _sizeInPages;

        public DatabaseHeader Header { get; private set; }
        private Sqlite3MasterTable _masterTable;

        public Sqlite3Database(Stream file, Sqlite3Settings settings = null)
        {
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

            _reader.ApplySqliteDatabaseHeader(Header);
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

        public Sqlite3Table GetTable(string name)
        {
            IEnumerable<Sqlite3SchemaRow> tables = GetTables();

            foreach (Sqlite3SchemaRow table in tables)
            {
                if (table.TableName != name || table.Type != "table")
                    continue;

                // Found it
                BTreePage root = BTreePage.Parse(_reader, table.RootPage);
                Sqlite3Table tbl = new Sqlite3Table(_reader, root, table);
                return tbl;
            }

            throw new Exception("Unable to find table named " + name);
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
}
