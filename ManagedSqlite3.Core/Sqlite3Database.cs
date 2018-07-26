using System;
using System.Collections.Generic;
using System.IO;
using ManagedSqlite3.Core.Internal;
using ManagedSqlite3.Core.Objects;
using ManagedSqlite3.Core.Objects.Headers;
using ManagedSqlite3.Core.Tables;

namespace ManagedSqlite3.Core
{
    public class Sqlite3Database : IDisposable
    {
        private readonly Sqlite3Settings _settings;
        private readonly ReaderBase _reader;

        private uint _sizeInPages;

        public DatabaseHeader Header { get; private set; }
        private Sqlite3MasterTable _masterTable;

        public Sqlite3Database(Stream file, Sqlite3Settings settings = null)
        {
            _settings = settings ?? new Sqlite3Settings();
            _reader = new ReaderBase(file);

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
                Type = "table",
                Name = "sqlite_master",
                TableName = "sqlite_master",
                RootPage = rootBtree.Page,
                Sql = "CREATE TABLE sqlite_master (type TEXT, name TEXT, tbl_name TEXT, rootpage INTEGER, sql TEXT);"
            };

            Sqlite3Table table = new Sqlite3Table(_reader, rootBtree, schemaRow);
            _masterTable = new Sqlite3MasterTable(table);
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

        public void Dispose()
        {
            _reader?.Dispose();
        }
    }
}
