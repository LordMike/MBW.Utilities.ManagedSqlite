using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Sqlite3RoLib.Helpers;
using Sqlite3RoLib.Objects;
using Sqlite3RoLib.Objects.Headers;

namespace Sqlite3RoLib
{
    public class Sqlite3Database : IDisposable
    {
        private readonly Sqlite3Settings _settings;
        private readonly ReaderBase _reader;

        private uint _sizeInPages;

        private DatabaseHeader _header;
        private Sqlite3MasterTable masterTable;

        public Sqlite3Database(Stream file, Sqlite3Settings settings = null)
        {
            _settings = settings ?? new Sqlite3Settings();
            _reader = new ReaderBase(file);

            Initialize();
            InitializeMasterTable();
        }

        private void Initialize()
        {
            _header = DatabaseHeader.Parse(_reader);

            // Database Size in pages adjustment
            // https://www.sqlite.org/fileformat.html#in_header_database_size

            uint expectedPages = (uint)(_reader.Length / _header.PageSize);

            // TODO: Warn on mismatch
            _sizeInPages = Math.Max(expectedPages, _header.DatabaseSizeInPages);

            _reader.ApplySqliteDatabaseHeader(_header);
        }

        private void InitializeMasterTable()
        {
            // Parse table on Page 1, the sqlite_master table
            BTreePage rootBtree = BTreePage.Parse(_reader, 1);

            Sqlite3Table table = new Sqlite3Table(_reader, rootBtree);
            masterTable = new Sqlite3MasterTable(table);
        }

        public IEnumerable<Sqlite3SchemaRow> GetTables() => masterTable.Tables;

        public void Dispose()
        {
            _reader?.Dispose();
        }
    }
}
