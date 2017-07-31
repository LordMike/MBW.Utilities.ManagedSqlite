using System;
using System.IO;

namespace Sqlite3RoLib.Helpers
{
    internal class ReaderBase : IDisposable
    {
        private readonly long _length;
        private readonly Stream _stream;
        internal BinaryReader Reader { get; }

        public ReaderBase(Stream stream)
        {
            _stream = stream;
            _length = _stream.Length;

            Reader = new BinaryReader(_stream);
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }

        internal bool CheckMagicBytes(byte[] comparison, bool throwException = true)
        {
            return CheckMagicBytes((uint)comparison.Length, comparison, throwException);
        }

        internal bool CheckMagicBytes(uint toRead, byte[] comparison, bool throwException = true)
        {
            Debug.Assert(toRead >= comparison.Length);
            CheckSize(toRead);

            byte[] data = Reader.ReadBytes((int)toRead);

            bool res = data.SequenceEqual(comparison);
            if (!res && throwException)
                throw new ArgumentException("The requested magic bytes did not match")
                {
                    // Note: This is the position after read
                    Data =
                    {
                        { nameof(Stream.Position), _stream.Position },
                        { nameof(toRead), toRead },
                        { nameof(comparison), comparison.ToHex() },
                        { nameof(data), data.ToHex() }
                    }
                };

            return res;
        }

        internal void CheckSize(uint sizeWanted, bool throwException = true)
        {
            if (!throwException)
                return;

            long dataLeft = _length - _stream.Position;
            if (dataLeft < sizeWanted)
            {
                throw new ArgumentException("Source stream does not have enough data")
                {
                    Data =
                    {
                        { nameof(Stream.Position), _stream.Position },
                        { nameof(sizeWanted), sizeWanted },
                        { "SourceLength", _length }
                    }
                };
            }
        }

        internal void SetPositionAndCheckSize(ulong position, uint sizeWanted, bool throwException = true)
        {
            SetPosition(position);

            CheckSize(sizeWanted, throwException);
        }

        internal void SetPosition(ulong position)
        {
            ulong newPosition = (ulong)_stream.Seek((long)position, SeekOrigin.Begin);

            if (newPosition != position)
                throw new ArgumentException("Unable to seek to position")
                {
                    Data = { { nameof(position), position } }
                };
        }

        internal void Skip(uint bytes)
        {
            Reader.BaseStream.Seek(bytes, SeekOrigin.Current);
        }
    }
}