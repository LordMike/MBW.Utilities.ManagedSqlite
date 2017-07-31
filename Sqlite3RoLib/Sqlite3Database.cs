using System;
using System.IO;
using System.Linq;
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
        private BTreePage _rootBtree;

        public Sqlite3Database(Stream file, Sqlite3Settings settings = null)
        {
            _settings = settings ?? new Sqlite3Settings();
            _reader = new ReaderBase(file);

            Initialize();

            InitializeRootTree();
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

        private void InitializeRootTree()
        {
            _rootBtree = BTreePage.Parse(_reader, 1);

            var table = new Sqlite3Table(_reader, _rootBtree);
            var rows = table.EnumerateRows().ToList();

            var names = rows.Select(s => s.ColumnData[2]).OrderBy(s => s).ToList();
        }

        public void Dispose()
        {
            _reader?.Dispose();
        }
    }
}
