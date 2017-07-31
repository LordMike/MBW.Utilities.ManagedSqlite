using System;
using System.IO;
using Sqlite3RoLib.Helpers;
using Sqlite3RoLib.Objects;

namespace Sqlite3RoLib
{
    public class Sqlite3Database : IDisposable
    {
        private readonly Sqlite3Settings _settings;
        private readonly ReaderBase _reader;

        private uint _sizeInPages;

        private DatabaseHeader _header;

        public Sqlite3Database(Stream file, Sqlite3Settings settings = null)
        {
            _settings = settings ?? new Sqlite3Settings();
            _reader = new ReaderBase(file);

            Initialize();
        }

        private void Initialize()
        {
            _header = DatabaseHeader.Parse(_reader);

            // Database Size in pages adjustment
            // https://www.sqlite.org/fileformat.html#in_header_database_size

            uint expectedPages = (uint) (_reader.Length / _header.PageSize);

            // TODO: Warn on mismatch
            _sizeInPages = Math.Max(expectedPages, _header.DatabaseSizeInPages);
        }

        public void Dispose()
        {
            _reader?.Dispose();
        }
    }
}
