using System;
using System.IO;

namespace MBW.Utilities.ManagedSqlite.Core.Helpers
{
    internal abstract class ReadonlyStream : Stream
    {
        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override bool CanWrite { get; } = false;
    }
}