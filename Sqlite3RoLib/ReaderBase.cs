using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sqlite3RoLib.Helpers;

namespace Sqlite3RoLib
{
    internal class ReaderBase : IDisposable
    {
        public long Length { get; }

        private readonly Stream _stream;
        private readonly BinaryReader _binaryReader;

        public ReaderBase(Stream stream)
        {
            _stream = stream;
            Length = _stream.Length;
            _binaryReader = new BinaryReader(stream);
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

            byte[] data = StreamHelper.ReadFully(_stream, (int)toRead);

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

            long dataLeft = Length - _stream.Position;
            if (dataLeft < sizeWanted)
            {
                throw new ArgumentException("Source stream does not have enough data")
                {
                    Data =
                    {
                        { nameof(Stream.Position), _stream.Position },
                        { nameof(sizeWanted), sizeWanted },
                        { "SourceLength", Length }
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
            _stream.Seek(bytes, SeekOrigin.Current);
        }

        public byte ReadByte()
        {
            return _binaryReader.ReadByte();
        }

        public ushort ReadUInt16()
        {
            ushort res = _binaryReader.ReadByte();
            res <<= 8;
            res += _binaryReader.ReadByte();

            return res;
        }

        public uint ReadUInt32()
        {
            uint res = _binaryReader.ReadByte();
            res <<= 8;

            res += _binaryReader.ReadByte();
            res <<= 8;

            res += _binaryReader.ReadByte();
            res <<= 8;

            res += _binaryReader.ReadByte();

            return res;
        }

        public short ReadInt16()
        {
            short res = _binaryReader.ReadByte();
            res <<= 8;
            res += _binaryReader.ReadByte();

            return res;
        }

        public int ReadInt32()
        {
            int res = _binaryReader.ReadByte();
            res <<= 8;

            res += _binaryReader.ReadByte();
            res <<= 8;

            res += _binaryReader.ReadByte();
            res <<= 8;

            res += _binaryReader.ReadByte();

            return res;
        }
    }
}