using System;
using System.IO;
using Sqlite3RoLib.Objects;

namespace Sqlite3RoLib.Helpers
{
    internal class SqliteDataStream : ReadonlyStream
    {
        private readonly ReaderBase _reader;
        private readonly BTreeCellData _data;

        public SqliteDataStream(ReaderBase reader, BTreeCellData data)
        {
            _reader = reader;
            _data = data;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override bool CanRead { get; }
        public override bool CanSeek { get; }
        public override long Length { get; }
        public override long Position { get; set; }
    }
}