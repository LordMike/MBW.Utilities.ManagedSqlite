using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

[StructLayout(LayoutKind.Auto)]
internal ref struct SpanReader(ReadOnlySpan<byte> span)
{
    private readonly ReadOnlySpan<byte> _data = span;
    public int Position { get; private set; } = 0;

    [MustUseReturnValue]
    public bool ReadBoolean()
    {
        const int size = sizeof(byte);
        bool data = Peek<byte>(size) != 0;
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public byte ReadByte()
    {
        const int size = sizeof(byte);
        byte data = Peek<byte>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public sbyte ReadSByte() => (sbyte)ReadByte();

    [MustUseReturnValue]
    public decimal ReadDecimal()
    {
        const int size = sizeof(decimal);
        decimal data = Peek<decimal>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public double ReadDouble()
    {
        const int size = sizeof(double);
        double data = Peek<double>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public float ReadSingle()
    {
        const int size = sizeof(float);
        float data = Peek<float>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public Half ReadHalf()
    {
        const int size = sizeof(short); // Half is 16-bit (2 bytes) but cant use constant sizeof, so we use short
        Half data = Peek<Half>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public short ReadInt16()
    {
        const int size = sizeof(short);
        short data = Peek<short>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public ushort ReadUInt16()
    {
        const int size = sizeof(ushort);
        ushort data = Peek<ushort>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public uint ReadUInt32()
    {
        const int size = sizeof(uint);
        uint data = Peek<uint>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public int ReadInt32()
    {
        const int size = sizeof(int);
        int data = Peek<int>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public long ReadInt64()
    {
        const int size = sizeof(long);
        long data = Peek<long>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public ulong ReadUInt64()
    {
        const int size = sizeof(ulong);
        ulong data = Peek<ulong>(size);
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    private T Peek<T>(int size)
    {
        // This assertion must be held for the unsafe code to work, but we dont want the overhead in release builds.
        Validator.Assert(Unsafe.SizeOf<T>() == size, "Size of T does not match the size parameter.");
        return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(PeekBytes(size)));
    }

    [MustUseReturnValue]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> PeekBytes(int size) => _data.Slice(Position, size);

    [MustUseReturnValue]
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ReadOnlySpan<byte> data = PeekBytes(count);
        Position += count;
        return data;
    }

    [MustUseReturnValue]
    public Guid ReadGuid()
    {
        int size = Unsafe.SizeOf<Guid>();
        Guid data = new Guid(_data.Slice(Position, size));
        Position += size;
        return data;
    }

    [MustUseReturnValue]
    public T ReadEnum<T>(bool failOnInvalidValue = false) where T : struct, Enum
    {
        int size = Unsafe.SizeOf<T>();
        T enumValue = Peek<T>(size);

        if (failOnInvalidValue)
            Validator.RequireValidEnum(enumValue);

        Position += size;
        return enumValue;
    }

    public readonly int BytesRead() => Position;

    public void SetPosition(int position)
    {
        Validator.Require(position >= 0);
        Validator.Require(position <= _data.Length);

        Position = position;
    }

    /// <summary>Move underlying position backward.</summary>
    /// <param name="count">Number of bytes to roll.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is less than or equal to zero.</exception>
    public void SkipBackwards(int count)
    {
        Validator.Require(count >= 0);
        Validator.Assert(count != 0, "You skip backwards with 0 bytes. This could be a bug.");

        Position -= count;
    }

    /// <summary>Move underlying position forward.</summary>
    /// <param name="count">Number of bytes to skip.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is less than or equal to zero.</exception>
    public void SkipForwards(int count)
    {
        Validator.Require(count >= 0);
        Validator.Assert(count != 0, "You skip forwards with 0 bytes. This could be a bug.");

        Position += count;
    }

    public int Length => _data.Length;

    [MustUseReturnValue]
    public string ReadFixedString(int length, Encoding encoding)
    {
        Validator.Require(length > 0);
        return encoding.GetString(ReadBytes(length));
    }

    public void Read(Span<byte> span)
    {
        PeekBytes(span.Length).CopyTo(span);
        Position += span.Length;
    }

    public T ReadStruct<T>() where T : struct
    {
        int size = Unsafe.SizeOf<T>();
        T str = Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(_data.Slice(Position, size)));
        Position += size;
        return str;
    }

    public long Align(int alignment)
    {
        Validator.Assert(alignment != 0, "You are aligning with 0");

        int position = Position;
        int remainder = position % alignment;
        if (remainder == 0)
            return 0;

        SetPosition(position + (alignment - remainder));

        return remainder;
    }
    
    public long ReadVarInt() => ReadVarInt(out byte _);

    public long ReadVarInt(out byte readBytes)
    {
        long res = 0;

        // Decode huffman encoding
        //  xyyy yyyy       x = if this is the last byte
        //                  y = data
        // Each byte provides 7 bits of the final data, and one bit to indicate followup bytes
        // The first 8 bytes are like this, the potential 9th byte is all data (8 bits data)

        for (readBytes = 1; readBytes < 9; readBytes++)
        {
            byte tmp = ReadByte();

            res <<= 7;
            res += tmp & 0x7F;

            if ((tmp & 0x80) == 0x00) // Last byte
                return res;
        }

        // Read final byte
        res <<= 8;
        res += ReadByte();

        readBytes++;

        return res;
    }

    public long ReadInteger(byte bytes)
    {
        long res = 0;

        for (int i = 0; i < bytes; i++)
        {
            byte tmp = ReadByte();

            res <<= 8;
            res += tmp;
        }

        if (((1L << (bytes * 8 - 1)) & res) > 0)
        {
            // Number was negative
            long extra = -1L; // 0xFFFF FFFF FFFF FFFF in binary
            extra <<= bytes * 8;

            res |= extra;
        }

        return res;
    }
}