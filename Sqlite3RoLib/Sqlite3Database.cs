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
        }

        public void Dispose()
        {
            _reader?.Dispose();
        }
    }
}
